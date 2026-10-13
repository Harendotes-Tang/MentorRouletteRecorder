#!/usr/bin/env python3
"""Preview approved GitCode branch cleanup; only --execute permits deletion.

Run from the trusted main checkout in Actions. The adjacent approval plan is the
only authority; no CLI option can supply another plan, branch, SHA or destination.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import sys
import tempfile

from sync_refs import Git, REPOSITORIES, SHA, RefSyncError, parse_refs, repository_urls

PLAN_PATH = Path(__file__).with_name("cleanup-plan.json")
MAX_PLAN_BYTES = 16 * 1024
MAX_ENTRIES = 16
MAIN = "refs/heads/main"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RefSyncError(message)


def unique_object(pairs: list[tuple]) -> dict:
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate approval plan field")
        result[key] = value
    return result


def validate_plan(value: object) -> list[dict[str, str]]:
    require(isinstance(value, dict) and set(value) == {"schema_version", "branches"},
            "Invalid approval plan schema")
    require(type(value["schema_version"]) is int and value["schema_version"] == 1,
            "Unsupported approval plan version")
    entries = value["branches"]
    require(isinstance(entries, list) and 0 < len(entries) <= MAX_ENTRIES,
            "Invalid approval plan entry count")
    seen = set()
    for entry in entries:
        require(isinstance(entry, dict)
                and set(entry) == {"repo", "ref", "expected_sha", "merge_anchor"}
                and all(isinstance(v, str) for v in entry.values()), "Invalid approval entry")
        repository_urls(entry["repo"])
        ref = entry["ref"]
        require(ref.startswith("refs/heads/") and len(ref) <= 255
                and re.fullmatch(r"refs/heads/[A-Za-z0-9_./-]+", ref) is not None,
                "Only approved public branch names are accepted")
        parts = ref.removeprefix("refs/heads/").split("/")
        require(all(part and not part.startswith(".") and not part.endswith((".", ".lock"))
                    for part in parts) and ".." not in ref,
                "Invalid approved branch name")
        require(ref.lower() not in {"refs/heads/main", "refs/heads/dev"},
                "Protected branch cannot be approved for deletion")
        for field in ("expected_sha", "merge_anchor"):
            require(SHA.fullmatch(entry[field]) is not None and entry[field] != "0" * 40,
                    "Approval requires complete nonzero commit SHAs")
        identity = (entry["repo"], ref)
        require(identity not in seen, "Duplicate approved branch")
        seen.add(identity)
    return entries


def load_plan() -> list[dict[str, str]]:
    try:
        with PLAN_PATH.open("rb") as stream:
            data = stream.read(MAX_PLAN_BYTES + 1)
        require(len(data) <= MAX_PLAN_BYTES, "Approval plan exceeds size limit")
        return validate_plan(json.loads(data.decode("utf-8"), object_pairs_hook=unique_object))
    except (OSError, UnicodeError, ValueError):
        raise RefSyncError("Approval plan could not be read or parsed") from None


def remote_refs(git: Git, url: str) -> dict[str, str]:
    # Reads, including post-deletion proof, are deliberately anonymous.
    return parse_refs(git.run(["ls-remote", "--refs", url, "refs/heads/*", "refs/tags/*"]))


def classify(entries: list[dict[str, str]], source: dict[str, str],
             target: dict[str, str]) -> dict[str, str]:
    result = {}
    for entry in entries:
        ref, expected = entry["ref"], entry["expected_sha"]
        require(ref not in target or target[ref] == expected,
                "Approved target SHA changed; a fresh audit is required")
        if ref in source:
            result[ref] = "retained-source-present"
        elif ref not in target:
            result[ref] = "already-absent"
        else:
            result[ref] = "eligible"
    return result


def deletion_arguments(entries: list[dict[str, str]], decisions: dict[str, str],
                       destination: str) -> list[str]:
    require(destination in {repository_urls(repo)[1] for repo in REPOSITORIES},
            "Invalid cleanup destination")
    # Entries have already passed the trusted plan validator; never enumerate
    # deletions from the target's inventory or reuse ordinary mirror prune flags.
    selected = sorted((entry for entry in entries if decisions[entry["ref"]] == "eligible"),
                      key=lambda entry: entry["ref"])
    if not selected:
        return []
    return ["push", "--atomic", "--porcelain",
            *(f"--force-with-lease={e['ref']}:{e['expected_sha']}" for e in selected),
            destination, *(":" + e["ref"] for e in selected)]


def restore_reappeared(git: Git, repo: str, revived: dict[str, str]) -> None:
    """Restore only just-deleted approved refs, never overwrite a target ref.

    The caller constructs revived by intersecting its approved deletions with
    the fresh anonymous source inventory. A successful restoration is still an
    incomplete cleanup, not deletion success. Cross-host reads are not a lock.
    """
    source_url, target_url = repository_urls(repo)
    for sha in sorted(set(revived.values())):
        git.run(["fetch", "--no-tags", source_url, sha])
        require(git.run(["rev-parse", "--verify", sha + "^{commit}"]).strip() == sha,
                "Source restoration object is not the observed commit")
    before = remote_refs(git, target_url)
    source = remote_refs(git, source_url)
    require(all(source.get(ref) == sha for ref, sha in revived.items()),
            "Source changed during restoration; cleanup remains incomplete")
    require(all(ref not in before or before[ref] == sha for ref, sha in revived.items()),
            "Restoration target changed; refusing overwrite; cleanup remains incomplete")
    missing = sorted(ref for ref in revived if ref not in before)
    if missing:
        git.run(["push", "--atomic", "--porcelain",
                 *("--force-with-lease=" + ref + ":" for ref in missing), target_url,
                 *(revived[ref] + ":" + ref for ref in missing)], destination=target_url)
    after = remote_refs(git, target_url)
    source = remote_refs(git, source_url)
    require(after == {**before, **revived}
            and all(source.get(ref) == sha for ref, sha in revived.items()),
            "Restoration verification failed; cleanup remains incomplete")


def cleanup(git: Git, repo: str, plan: list[dict[str, str]], *, execute: bool = False) -> dict[str, str]:
    source_url, target_url = repository_urls(repo)
    # Validate even in library use: callers cannot bypass protected-ref checks.
    plan = validate_plan({"schema_version": 1, "branches": plan})
    entries = [entry for entry in plan if entry["repo"] == repo]
    require(bool(entries), "Repository has no approved cleanup entries")
    git.run(["init", "--bare", "--template="])
    git.run(["fetch", "--no-tags", source_url, "+refs/heads/main:refs/heads/main"])
    main_sha = git.run(["rev-parse", "--verify", MAIN]).strip()
    require(SHA.fullmatch(main_sha) is not None, "Invalid fetched main commit")
    for anchor in sorted({entry["merge_anchor"] for entry in entries}):
        require(git.run(["cat-file", "-t", anchor]).strip() == "commit",
                "Approved merge anchor must be a commit")
        try:
            git.run(["merge-base", "--is-ancestor", anchor, main_sha])
        except RefSyncError:
            raise RefSyncError("Approved merge anchor is not verified in GitHub main") from None
    source, target = remote_refs(git, source_url), remote_refs(git, target_url)
    require(source.get(MAIN) == main_sha, "GitHub main changed during preflight; retry")
    decisions = classify(entries, source, target)
    if not execute:
        return decisions
    # Refetching is unnecessary: unchanged main SHA preserves the verified
    # ancestry. These fresh reads precede the exact-lease atomic deletion.
    target = remote_refs(git, target_url)
    source = remote_refs(git, source_url)
    require(source.get(MAIN) == main_sha, "GitHub main changed before cleanup; retry")
    decisions = classify(entries, source, target)
    arguments = deletion_arguments(entries, decisions, target_url)
    removed = {ref for ref, decision in decisions.items() if decision == "eligible"}
    if arguments:
        git.run(arguments, destination=target_url)
    after = remote_refs(git, target_url)
    source_after = remote_refs(git, source_url)
    revived = {ref: source_after[ref] for ref in removed if ref in source_after}
    if revived:
        restore_reappeared(git, repo, revived)
        raise RefSyncError("Source branch reappeared; approved refs restored and verified; cleanup remains incomplete")
    expected_after = {ref: sha for ref, sha in target.items() if ref not in removed}
    require(after == expected_after,
            "Anonymous cleanup verification failed; approved or unrelated refs changed")
    return {ref: "deleted-and-verified" if ref in removed else decision
            for ref, decision in decisions.items()}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, choices=REPOSITORIES)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args(argv)
    try:
        plan = load_plan()
        token = os.environ.get("GITCODE_MIRROR_TOKEN", "") if args.execute else ""
        require(not args.execute or bool(token), "Configure GITCODE_MIRROR_TOKEN before execution")
        with tempfile.TemporaryDirectory(prefix="mentor-approved-cleanup-") as directory:
            decisions = cleanup(Git(Path(directory), token), args.repo, plan, execute=args.execute)
        print("Approved mirror cleanup " + ("execution" if args.execute else "preview") + ": " + args.repo)
        for ref, decision in decisions.items():
            print(ref + ": " + decision)
        return 0
    except RefSyncError as error:
        print("Approved mirror cleanup failed: " + str(error), file=sys.stderr)
    except Exception:
        print("Approved mirror cleanup failed: unexpected response or local I/O error", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
