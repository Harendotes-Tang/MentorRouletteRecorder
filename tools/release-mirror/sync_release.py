#!/usr/bin/env python3
"""Copy an official GitHub release to GitCode, publishing metadata last.

Git refs are synchronized by the preceding Actions ref-sync step. This tool never creates or changes
tags, deletes attachments or releases, or replaces a conflicting attachment. An edited GitHub title or
notes is copied only after the tag, the channel and every registered attachment verify.
API contracts: https://docs.gitcode.com/docs/apis/ (Release and Tag sections).
"""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

OWNER = "Harendotes-Tang"
REPO = "MentorRouletteRecorder"
GH = f"https://api.github.com/repos/{OWNER}/{REPO}"
GC = f"https://api.gitcode.com/api/v5/repos/{OWNER}/{REPO}"
TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
PRERELEASE_TAG = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-"
    r"([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")
MAX_INSTALLER = 2 * 1024**3
MAX_JSON = 2 * 1024**2
CDN_HOST = "file-cdn.gitcode.com"
UPLOAD_HOST = "file-upload.gitcode.com"
UPLOAD_FILE_HOST = "file.gitcode.com"
UPLOAD_HEADERS = {"x-obs-meta-project-id", "x-obs-acl", "x-obs-callback", "content-type"}


class MirrorError(Exception):
    """A sanitized diagnostic safe to print in public Actions logs."""


class Missing(MirrorError):
    """The requested public resource returned HTTP 404."""


class UploadInterrupted(MirrorError):
    """A native upload timed out; the remote file may already be registered."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise MirrorError(message)


def secure_url(url: str) -> urllib.parse.SplitResult:
    p = urllib.parse.urlsplit(url)
    require(p.scheme == "https" and p.hostname is not None and p.port in (None, 443)
            and not p.username and not p.password and not p.fragment,
            "Refused unsafe remote URL")
    return p


def upload_url_allowed(url: str) -> bool:
    p = secure_url(url)
    # The API returns a signed OBS upload URL; never send the API token there.
    return p.hostname in {UPLOAD_HOST, UPLOAD_FILE_HOST, CDN_HOST} or bool(
        re.fullmatch(r"[a-z0-9-]+\.obs\.[a-z0-9-]+\.myhuaweicloud\.com", p.hostname or ""))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def curl_quote(value: str) -> str:
    """Quote one curl config value without permitting additional config lines."""
    require(all(ord(c) >= 32 and ord(c) != 127 for c in value), "Unsafe upload configuration value")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def upload_with_curl(url: str, headers: dict, path: Path, deadline: float) -> None:
    """PUT via native curl; credentials stay on stdin and only numeric metrics escape."""
    require(upload_url_allowed(url), "Refused upload storage host")
    require(set(headers) == UPLOAD_HEADERS and all(isinstance(v, str) and "\r" not in v
            and "\n" not in v for v in headers.values()), "Unexpected upload headers")
    total_bytes = path.stat().st_size
    require(0 < total_bytes <= MAX_INSTALLER, "Invalid upload size")
    remaining = deadline - time.monotonic()
    require(remaining > 0, "Release mirror time budget exhausted")
    # Let curl emit its numeric timeout metrics before the parent hard deadline.
    transfer_seconds = max(remaining - 2.0, remaining / 2.0)
    # Abort a route that cannot finish within the remaining budget instead of
    # accepting a trickle until the hard deadline. Small control files keep 1 B/s.
    minimum_speed = max(1, int(total_bytes / transfer_seconds) + 1)
    # -q is first on argv, so a user curlrc cannot enable traces, netrc or redirects.
    # Signed URL/header values are never argv, persisted files, stdout or stderr.
    options = [
        ("url", url), ("upload-file", str(path.resolve())), ("request", "PUT"),
        ("proto", "=https"), ("max-redirs", "0"), ("max-time", f"{transfer_seconds:.6f}"),
        ("connect-timeout", str(min(60, remaining))), ("speed-limit", str(minimum_speed)), ("speed-time", "60"),
        ("expect100-timeout", "1"), ("output", os.devnull), ("max-filesize", str(MAX_JSON)),
        ("user-agent", "MentorRecorder-Release-Mirror"),
        ("write-out", "%{http_code}|%{size_upload}|%{speed_upload}|%{time_total}"),
    ]
    config = "silent\nfail\nno-location\nno-netrc\ngloboff\nhttp1.1\n"
    config += "\n".join(key + " = " + curl_quote(value) for key, value in options) + "\n"
    config += "\n".join("header = " + curl_quote(key + ": " + value)
                        for key, value in headers.items()) + "\n"
    env = {key: value for key, value in os.environ.items()
           if "TOKEN" not in key.upper() and not key.upper().startswith(("GIT_", "CURL_"))
           and key.upper() != "SSLKEYLOGFILE"}
    print("Uploading to verified storage host: " + secure_url(url).hostname, flush=True)
    try:
        result = subprocess.run(["curl", "-q", "--config", "-"], input=config,
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                text=True, encoding="utf-8", errors="replace", env=env,
                                timeout=remaining, check=False)
    except subprocess.TimeoutExpired:
        raise MirrorError("Native upload exceeded release time budget") from None
    except OSError:
        raise MirrorError("Native upload could not start; curl is required") from None
    # Never relay curl errors: they can contain the signed URL or callback headers.
    metrics = re.fullmatch(r"([0-9]{3})\|([0-9]+)\|([0-9]+(?:\.[0-9]+)?)\|([0-9]+(?:\.[0-9]+)?)",
                           result.stdout) if len(result.stdout) <= 256 else None
    require(metrics is not None, "Native upload returned invalid diagnostics")
    code, count = int(metrics[1]), int(metrics[2])
    print(f"Native upload metrics: HTTP {code}; bytes={count}/{total_bytes}; "
          f"average_bytes_per_second={metrics[3]}; seconds={metrics[4]}", flush=True)
    if result.returncode == 28:
        raise UploadInterrupted(f"Native upload timed out (HTTP {code})")
    require(result.returncode == 0 and 200 <= code < 300 and count == total_bytes,
            f"Native upload failed (exit {result.returncode}, HTTP {code})")
    require(time.monotonic() < deadline, "Release mirror time budget exhausted")


class Http:
    """Bounded HTTP with explicit redirect policy and no cookie jar."""

    def __init__(self, gitcode_token: str, github_token: str = "") -> None:
        self.gc_token = gitcode_token
        self.gh_token = github_token
        self.opener = urllib.request.build_opener(NoRedirect())
        self.deadline = time.monotonic() + 60 * 60

    def open(self, url, *, method="GET", data=None, headers=None, auth=False,
             redirects=()):
        initial = secure_url(url)
        request_headers = {"User-Agent": "MentorRecorder-Release-Mirror", **(headers or {})}
        if auth:
            if url.startswith(GC + "/"):
                require(bool(self.gc_token), "GITCODE_MIRROR_TOKEN is required")
                request_headers["PRIVATE-TOKEN"] = self.gc_token
            elif url.startswith(GH + "/") and self.gh_token:
                request_headers["Authorization"] = "Bearer " + self.gh_token
            else:
                require(url.startswith(GH + "/"), "Refused credential destination")
        for attempt in range(4):
            remaining = self.deadline - time.monotonic()
            require(remaining > 0, "Release mirror time budget exhausted")
            request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
            try:
                return self.opener.open(request, timeout=min(60, remaining))
            except urllib.error.HTTPError as error:
                status = error.code
                location = error.headers.get("Location")
                error.close()
                if status in (301, 302, 303, 307, 308) and location and method == "GET" and not auth:
                    target = urllib.parse.urljoin(url, location)
                    p = secure_url(target)
                    require(attempt < 3 and p.hostname in redirects and p.hostname != initial.hostname,
                            "Refused download redirect")
                    url = target
                    continue
                if status == 404:
                    raise Missing("Remote resource not found (HTTP 404)") from None
                raise MirrorError(f"Remote request failed (HTTP {status})") from None
            except (urllib.error.URLError, TimeoutError, OSError):
                raise MirrorError("Remote request failed; check connectivity or retry later") from None
        raise MirrorError("Too many redirects")

    def read(self, response, limit: int, output=None) -> tuple[int, str, bytes]:
        count, digest, chunks = 0, hashlib.sha256(), []
        length = response.headers.get("Content-Length")
        if length is not None:
            require(length.isdigit() and int(length) <= limit, "Remote content exceeds size limit")
        while True:
            require(time.monotonic() < self.deadline, "Release mirror time budget exhausted")
            # HTTPResponse.read(size) waits to fill size under a slow trickle. read1
            # returns after one underlying read so the deadline is checked per chunk.
            block = response.read1(min(1024**2, limit - count + 1))
            require(time.monotonic() < self.deadline, "Release mirror time budget exhausted")
            if not block:
                break
            count += len(block)
            require(count <= limit, "Remote content exceeds size limit")
            digest.update(block)
            if output:
                output.write(block)
            else:
                chunks.append(block)
        require(length is None or count == int(length), "Truncated remote content")
        return count, digest.hexdigest(), b"".join(chunks)

    def api(self, url, *, auth=False, method="GET", payload=None):
        require(url.startswith(GH + "/") or url.startswith(GC + "/"), "Refused API destination")
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        with self.open(url, auth=auth, method=method, data=data,
                       headers={"Accept": "application/json", "Content-Type": "application/json"}) as response:
            _, _, body = self.read(response, MAX_JSON)
        return json.loads(body)

    def download(self, url, path, limit, hosts):
        with self.open(url, redirects=hosts) as response, path.open("wb") as output:
            size, digest, _ = self.read(response, limit, output)
        return size, digest


def is_prerelease(tag: str) -> bool:
    require(isinstance(tag, str), "Invalid release tag")
    if TAG.fullmatch(tag):
        return False
    match = PRERELEASE_TAG.fullmatch(tag)
    require(match is not None and all(not part.isdigit() or part == "0" or not part.startswith("0")
                                    for part in match[4].split(".")), "Invalid release tag")
    return True


def artifact_names(tag: str) -> tuple[str, ...]:
    prerelease = is_prerelease(tag)
    exe = f"MentorRecorder-{tag[1:]}-setup.exe"
    if prerelease:
        portable = f"MentorRecorder-{tag[1:]}-win-x64.zip"
        return exe, exe + ".sha256", portable, portable + ".sha256"
    return exe, exe + ".sha256", "BUILD-METADATA.json"


def asset_map(release: dict) -> dict:
    result = {}
    require(isinstance(release.get("assets"), list), "Release assets are missing")
    for asset in release["assets"]:
        require(isinstance(asset, dict) and isinstance(asset.get("name"), str), "Invalid release asset")
        # GitCode also lists generated source archives; they are not release attachments.
        if asset.get("type") == "source":
            continue
        name = asset["name"]
        require(name not in result, "Duplicate release asset name")
        result[name] = asset
    return result


def tag_commit(http: Http, tag: str) -> str:
    obj = http.api(GH + "/git/ref/tags/" + tag, auth=True)["object"]
    for _ in range(5):
        require(bool(SHA.fullmatch(obj.get("sha", ""))), "Invalid source tag SHA")
        if obj.get("type") == "commit":
            return obj["sha"]
        require(obj.get("type") == "tag", "Source tag does not identify a commit")
        obj = http.api(GH + "/git/tags/" + obj["sha"], auth=True)["object"]
    raise MirrorError("Source tag nesting exceeds limit")


def verify_mirror_tag(http: Http, tag: str, commit: str) -> None:
    for page in range(1, 11):
        tags = http.api(GC + f"/tags?per_page=100&page={page}")
        require(isinstance(tags, list), "Invalid mirror tag listing")
        for item in tags:
            if item.get("name") == tag:
                require(item.get("commit", {}).get("sha") == commit,
                        "Mirror tag differs from GitHub; rerun repository synchronization")
                return
        if len(tags) < 100:
            break
    raise MirrorError("Mirror tag not found; rerun repository synchronization")


def download_url(host: str, tag: str, name: str) -> str:
    return f"https://{host}/{OWNER}/{REPO}/releases/download/{tag}/{name}"


def validate_source(tag: str, commit: str, paths: dict[str, Path], hashes: dict) -> None:
    names = artifact_names(tag)
    for binary, checksum in zip(names[::2], names[1::2]):
        line = paths[checksum].read_text(encoding="utf-8-sig").strip()
        match = re.fullmatch(r"([0-9a-fA-F]{64})[ \t]+\*?" + re.escape(binary), line)
        require(match is not None and match[1].lower() == hashes[binary][1], "Source binary checksum mismatch")
    if is_prerelease(tag):
        return
    metadata = names[2]
    doc = json.loads(paths[metadata].read_text(encoding="utf-8-sig"))
    require(doc.get("product") == "MentorRecorder" and doc.get("version") == tag[1:]
            and doc.get("prerelease") is False and doc.get("source_commit") == commit
            and doc.get("source_worktree_dirty") is False
            and doc.get("public_distribution_ready") is True,
            "Source build metadata is not a clean distributable build of the release tag")


def notes_equal(source: str, mirror) -> bool:
    """GitCode normalizes newlines; preserve all text, whitespace and final newlines."""
    if not isinstance(mirror, str):
        return False
    return source.replace("\r\n", "\n").replace("\r", "\n") == mirror.replace("\r\n", "\n").replace("\r", "\n")


class RemoteMirror:
    def __init__(self, http: Http, tag: str, directory: Path):
        self.http, self.tag, self.directory = http, tag, directory

    def release(self):
        try:
            return self.http.api(GC + "/releases/tags/" + self.tag)
        except Missing:
            return None

    def create(self, source, commit):
        if source["prerelease"]:
            status = "pre"
        else:
            # Historical formal releases and prereleases must not replace latest.
            latest = self.http.api(GH + "/releases/latest", auth=True)
            require(isinstance(latest, dict) and latest.get("draft") is False
                    and latest.get("prerelease") is False and isinstance(latest.get("tag_name"), str)
                    and not is_prerelease(latest["tag_name"]),
                    "Cannot establish the current formal GitHub release")
            status = "latest" if self.tag == latest["tag_name"] else "none"
        # Recheck immediately before POST: no tool-driven creation of missing tags.
        verify_mirror_tag(self.http, self.tag, commit)
        self.http.api(GC + "/releases", auth=True, method="POST", payload={
            "tag_name": self.tag, "name": source["name"], "body": source["body"],
            "target_commitish": commit, "release_status": status})

    def update(self, source, commit):
        """Copy an edited GitHub title and notes; nothing else about the release changes."""
        verify_mirror_tag(self.http, self.tag, commit)
        self.http.api(GC + "/releases/" + self.tag, auth=True, method="PATCH",
                      payload={"name": source["name"], "body": source["body"]})

    def verify(self, name, expected):
        path = self.directory / ("mirror-" + name)
        actual = self.http.download(download_url("gitcode.com", self.tag, name), path,
                                    expected[0], {CDN_HOST})
        require(actual == expected, "Mirror asset content conflicts with GitHub: " + name)

    def verify_release(self, source):
        current = self.release()
        validate_mirror_identity(current, source)
        require(all(name in asset_map(current) for name in artifact_names(self.tag)),
                "Mirror release is missing required attachments")

    def upload(self, name, path):
        response = self.http.api(GC + "/releases/" + self.tag + "/upload_url?" +
                                 urllib.parse.urlencode({"file_name": name}), auth=True)
        require(upload_url_allowed(response["url"]), "Refused upload storage host")
        headers = {key.lower(): value for key, value in response["headers"].items()}
        upload_with_curl(response["url"], headers, path, self.http.deadline)


def validate_mirror_release(existing, source):
    """Tag and channel must match; the title and notes may still predate a GitHub edit."""
    require(isinstance(existing, dict) and existing.get("tag_name") == source["tag_name"]
            and existing.get("prerelease") is source["prerelease"]
            and (not source["prerelease"] or existing.get("release_status") == "pre"),
            "Existing mirror release identity conflicts with GitHub")


def text_current(existing, source) -> bool:
    return existing.get("name") == source["name"] and notes_equal(source["body"], existing.get("body"))


def validate_mirror_identity(existing, source):
    validate_mirror_release(existing, source)
    require(text_current(existing, source), "Existing mirror release identity or notes conflict with GitHub")


def publish(mirror, source: dict, commit: str, paths: dict, hashes: dict) -> None:
    """Validate every preexisting asset before mutating; commit metadata last."""
    names = artifact_names(source["tag_name"])
    existing = mirror.release()
    assets = {}
    if existing is not None:
        validate_mirror_release(existing, source)
        assets = asset_map(existing)
        require(source["prerelease"] or names[2] not in assets or all(n in assets for n in names),
                "Existing completion metadata has missing attachments; refusing repair in place")
        for name in names:
            if name in assets:
                mirror.verify(name, hashes[name])
        if not text_current(existing, source):
            # The GitHub title or notes were edited after mirroring. Every registered
            # attachment matched, so only the text follows; missing attachments come next.
            mirror.update(source, commit)
    else:
        mirror.create(source, commit)
    for name in names:  # Formal metadata is last; beta has both binary/checksum pairs.
        if name not in assets:
            try:
                mirror.upload(name, paths[name])
            except UploadInterrupted:
                if not source["prerelease"]:
                    raise
                # A lost response can follow a completed PUT. Reconcile every
                # registered original before requesting one fresh upload URL.
                current = mirror.release()
                validate_mirror_identity(current, source)
                assets = asset_map(current)
                for registered in names:
                    if registered in assets:
                        mirror.verify(registered, hashes[registered])
                if name in assets:
                    continue
                print("Retrying interrupted prerelease upload with a fresh upload address", flush=True)
                mirror.upload(name, paths[name])
            mirror.verify(name, hashes[name])


def synchronize(http: Http, tag: str | None, directory: Path) -> str:
    if tag is not None:
        artifact_names(tag)
    source = http.api(GH + ("/releases/tags/" + tag if tag else "/releases/latest"), auth=True)
    require(isinstance(source, dict) and source.get("draft") is False
            and type(source.get("prerelease")) is bool,
            "Only published releases can be mirrored")
    require(tag is None or source.get("tag_name") == tag, "Source release tag differs from request")
    tag = source["tag_name"]
    names = artifact_names(tag)
    require(source["prerelease"] == is_prerelease(tag), "Release identity disagrees with tag")
    require(isinstance(source.get("name"), str) and isinstance(source.get("body"), str), "Missing release notes")
    assets = asset_map(source)
    require(all(n in assets for n in names), "Source release assets incomplete; retry after publishing finishes")
    if source["prerelease"]:
        require(all(isinstance(assets[n].get("digest"), str)
                    and re.fullmatch(r"sha256:[0-9a-f]{64}", assets[n]["digest"]) for n in names),
                "Source prerelease asset digest missing or invalid")
    commit = tag_commit(http, tag)
    verify_mirror_tag(http, tag, commit)
    paths, hashes = {}, {}
    limits = (MAX_INSTALLER, 1024, MAX_INSTALLER, 1024) if source["prerelease"] else (MAX_INSTALLER, 1024, 16 * 1024)
    for name, limit in zip(names, limits):
        asset = assets[name]
        require(type(asset.get("size")) is int and 0 < asset["size"] <= limit,
                "Invalid source asset size")
        path = directory / name
        paths[name] = path
        hashes[name] = http.download(download_url("github.com", tag, name), path, limit,
                                     {"release-assets.githubusercontent.com", "objects.githubusercontent.com"})
        require(hashes[name][0] == asset["size"], "Source asset size changed during download")
        if asset.get("digest"):
            require(asset["digest"] == "sha256:" + hashes[name][1], "GitHub asset digest mismatch")
    validate_source(tag, commit, paths, hashes)
    mirror = RemoteMirror(http, tag, directory)
    publish(mirror, source, commit, paths, hashes)
    mirror.verify_release(source)
    return tag


def withdrawn_formal_releases(http: Http) -> list[str]:
    """Formal GitCode releases whose GitHub release is gone; reported, never deleted."""
    # The same listing the Collector's GitCode update fallback reads.
    releases = http.api(GC + "/releases?page=1&per_page=20")
    require(isinstance(releases, list) and len(releases) <= 20
            and all(isinstance(release, dict) for release in releases), "Invalid mirror release listing")
    withdrawn = []
    for release in releases:
        tag = release.get("tag_name")
        # Like the Collector, an absent draft/prerelease flag counts as formal. Only
        # formal version tags are offered as updates, and only they enter a URL.
        if (not isinstance(tag, str) or not TAG.fullmatch(tag)
                or any(release.get(key, False) is not False for key in ("draft", "prerelease"))):
            continue
        try:
            http.api(GH + "/releases/tags/" + tag, auth=True)
        except Missing:
            withdrawn.append(tag)
    return withdrawn


def catch_up(http: Http, directory: Path) -> list[str]:
    """Bounded catch-up for the two channels and the withdrawal report; none starves another."""
    total_deadline = getattr(http, "deadline", None)
    tags = []
    failures = []
    for channel in ("formal", "prerelease", "withdrawal"):
        if total_deadline is not None:
            # Discovery is a small JSON request, not an artifact transfer. A
            # slow stream must leave time for the other channel and uploads.
            http.deadline = min(total_deadline, time.monotonic() + 60)
        try:
            if channel == "formal":
                latest = http.api(GH + "/releases/latest", auth=True)
                require(isinstance(latest, dict) and latest.get("draft") is False
                        and latest.get("prerelease") is False and not is_prerelease(latest.get("tag_name")),
                        "Cannot establish the latest formal release")
                tags.append(latest["tag_name"])
            elif channel == "withdrawal":
                # A release deleted on GitHub stays on GitCode, where the Collector would
                # still offer it. Removing it there is the maintainer's decision.
                for tag in withdrawn_formal_releases(http):
                    failures.append(tag + ": deleted on GitHub but still published on GitCode;"
                                    " delete the GitCode release by hand")
            else:
                releases = http.api(GH + "/releases?per_page=20&page=1", auth=True)
                require(isinstance(releases, list) and len(releases) <= 20, "Invalid bounded source release listing")
                candidates = []
                for release in releases:
                    require(isinstance(release, dict), "Invalid source release")
                    if release.get("draft") is False and release.get("prerelease") is True:
                        require(is_prerelease(release.get("tag_name")), "Prerelease tag identity mismatch")
                        published = datetime.fromisoformat(release["published_at"])
                        require(published.tzinfo is not None, "Source publication time has no timezone")
                        candidates.append((published, release["tag_name"]))
                if candidates:
                    tags.append(max(candidates)[1])
        except MirrorError as error:
            failures.append(channel + ": " + str(error))
        except Exception:
            # Do not expose raw parsing/transport errors or skip the other channel.
            failures.append(channel + ": invalid response, interrupted transfer, or local I/O error")
        finally:
            if total_deadline is not None:
                http.deadline = total_deadline
    for index, tag in enumerate(tags):
        if total_deadline is not None:
            now = time.monotonic()
            http.deadline = now + max(0, total_deadline - now) / (len(tags) - index)
        try:
            synchronize(http, tag, directory)
            print("Verified complete GitCode mirror: " + tag, flush=True)
        except MirrorError as error:
            failures.append(tag + ": " + str(error))
        except Exception:
            failures.append(tag + ": invalid response, interrupted transfer, or local I/O error")
        finally:
            if total_deadline is not None:
                http.deadline = total_deadline
    require(not failures, "; ".join(failures))
    return tags


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", default=None, help="Published tag; omitted catches up latest formal and prerelease")
    args = parser.parse_args()
    try:
        token = os.environ.get("GITCODE_MIRROR_TOKEN", "")
        require(bool(token), "Configure the repository Actions secret GITCODE_MIRROR_TOKEN first")
        with tempfile.TemporaryDirectory(prefix="mentor-release-mirror-") as directory:
            http = Http(token, os.environ.get("GITHUB_TOKEN", ""))
            if args.tag:
                tag = synchronize(http, args.tag, Path(directory))
                print(f"Verified complete GitCode mirror: {tag}")
            else:
                catch_up(http, Path(directory))
        return 0
    except MirrorError as error:
        print("Release mirror failed: " + str(error), file=sys.stderr)
    except Exception:
        # HTTP/JSON/OS exceptions may contain signed URLs or response bodies. Never dump them.
        print("Release mirror failed: invalid response, interrupted transfer, or local I/O error", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
