#!/usr/bin/env python3
"""Push only GitHub heads/tags to the two fixed GitCode mirrors, without pruning.

Run from trusted GitHub Actions default-branch code. GITCODE_MIRROR_TOKEN needs
repository push access. Authentication exists only in the Git subprocess environment.
"""

from __future__ import annotations

import argparse
import base64
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

OWNER = "Harendotes-Tang"
REPOSITORIES = ("MentorRouletteRecorder", "MentorRecorder-Calibrations")
MAX_REFS = 512
SHA = re.compile(r"[0-9a-f]{40}\Z")


class RefSyncError(Exception):
    """Safe diagnostic: never contains Git output or credential material."""


def repository_urls(repo: str) -> tuple[str, str]:
    if repo not in REPOSITORIES:
        raise RefSyncError("Repository is outside the mirror allowlist")
    return (f"https://github.com/{OWNER}/{repo}.git", f"https://gitcode.com/{OWNER}/{repo}.git")


def git_environment(destination: str | None = None, token: str = "") -> dict[str, str]:
    # Inherited trace/config/credential helpers must not leak or redirect the PAT.
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("GIT_")
           and key.upper() not in {"GITCODE_MIRROR_TOKEN", "GITHUB_TOKEN", "GH_TOKEN"}}
    env.update({"GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
                "GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never"})
    if destination is not None:
        if destination not in {repository_urls(repo)[1] for repo in REPOSITORIES} or not token:
            raise RefSyncError("Missing mirror token or invalid credential destination")
        value = base64.b64encode((OWNER + ":" + token).encode("utf-8")).decode("ascii")
        env.update({"GIT_CONFIG_COUNT": "1",
                    "GIT_CONFIG_KEY_0": f"http.{destination}.extraHeader",
                    "GIT_CONFIG_VALUE_0": "Authorization: Basic " + value})
    return env


def parse_refs(output: str) -> dict[str, str]:
    result = {}
    for line in output.splitlines():
        fields = line.split("\t")
        if len(fields) != 2 or not SHA.fullmatch(fields[0]):
            raise RefSyncError("Invalid Git reference response")
        sha, ref = fields
        if not ref.startswith(("refs/heads/", "refs/tags/")):
            raise RefSyncError("Non-public Git reference refused")
        if (ref in result or any(ord(c) < 33 or c in ":~^?*[\\" for c in ref)
                or ".." in ref or "@{" in ref or "//" in ref or ref.endswith(("/", ".", ".lock"))):
            raise RefSyncError("Invalid or duplicate Git reference")
        result[ref] = sha
    if len(result) > MAX_REFS:
        raise RefSyncError("Git reference count exceeds bounded synchronization limit")
    return result


def push_arguments(source: dict[str, str], target: dict[str, str], destination: str) -> list[str]:
    """Create exact leases for changed source refs; target-only refs are untouched."""
    if destination not in {repository_urls(repo)[1] for repo in REPOSITORIES}:
        raise RefSyncError("Invalid push destination")
    changed = sorted(ref for ref, sha in source.items() if target.get(ref) != sha)
    if not changed:
        return []
    leases = [f"--force-with-lease={ref}:{target.get(ref, '')}" for ref in changed]
    specs = [f"{source[ref]}:{ref}" for ref in changed]
    # If the server lacks atomic support or any lease changed, the whole push fails.
    return ["push", "--atomic", "--porcelain", *leases, destination, *specs]


class Git:
    def __init__(self, directory: Path, token: str):
        self.directory = directory
        self.token = token
        self.deadline = time.monotonic() + 15 * 60

    def run(self, arguments: list[str], *, destination: str | None = None) -> str:
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise RefSyncError("Repository mirror time budget exhausted")
        env = git_environment(destination, self.token)
        command = ["git", "-c", "credential.helper=", "-c", "http.followRedirects=false",
                   "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                   "-c", "core.hooksPath=" + str(self.directory / "no-hooks"), *arguments]
        try:
            result = subprocess.run(command, cwd=self.directory, env=env, capture_output=True,
                                    text=True, encoding="utf-8", errors="strict", timeout=remaining,
                                    check=False)
        except (OSError, subprocess.TimeoutExpired, UnicodeError):
            raise RefSyncError("Git invocation failed or repository mirror timed out") from None
        if result.returncode:
            # Git can echo remote headers/URLs. Never relay stdout/stderr, even on failure.
            raise RefSyncError("Git operation failed; check token, mirror protection, connectivity or lease/atomic support")
        return result.stdout


def synchronize(git: Git, repo: str) -> int:
    source_url, target_url = repository_urls(repo)
    git.run(["init", "--bare", "--template="])
    # No remote is persisted, and neither GitHub PR refs nor arbitrary refs are fetched.
    git.run(["fetch", "--no-tags", source_url,
             "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"])
    source = parse_refs(git.run(["for-each-ref", "--format=%(objectname)%09%(refname)",
                                "refs/heads/", "refs/tags/"]))
    if not source:
        raise RefSyncError("Source repository has no heads or tags")
    # Avoid replaying an obsolete snapshot if GitHub changed while fetch was running.
    current = parse_refs(git.run(["ls-remote", "--refs", source_url, "refs/heads/*", "refs/tags/*"]))
    if current != source:
        raise RefSyncError("Source refs changed during fetch; retry from a fresh snapshot")
    before = parse_refs(git.run(["ls-remote", "--refs", target_url, "refs/heads/*", "refs/tags/*"],
                                destination=target_url))
    arguments = push_arguments(source, before, target_url)
    if arguments:
        git.run(arguments, destination=target_url)
    after = parse_refs(git.run(["ls-remote", "--refs", target_url, "refs/heads/*", "refs/tags/*"],
                               destination=target_url))
    if any(after.get(ref) != sha for ref, sha in source.items()):
        raise RefSyncError("Mirror refs do not match the fetched source snapshot after push")
    return sum(before.get(ref) != sha for ref, sha in source.items())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, choices=REPOSITORIES)
    args = parser.parse_args()
    try:
        token = os.environ.get("GITCODE_MIRROR_TOKEN", "")
        if not token:
            raise RefSyncError("Configure the repository Actions secret GITCODE_MIRROR_TOKEN first")
        with tempfile.TemporaryDirectory(prefix="mentor-refs-mirror-") as directory:
            changed = synchronize(Git(Path(directory), token), args.repo)
        print(f"Verified GitCode heads/tags for {args.repo}: {changed} updated; target-only refs preserved")
        return 0
    except RefSyncError as error:
        print("Repository mirror failed: " + str(error), file=sys.stderr)
    except Exception:
        print("Repository mirror failed: unexpected response or local I/O error", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
