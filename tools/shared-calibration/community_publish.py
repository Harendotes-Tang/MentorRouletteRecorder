#!/usr/bin/env python3
"""Validate and publish public reminder and macro submissions from the trusted main checkout."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import unicodedata

if Path(__file__).parent.name == "community":
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from community_classification import MARKER, classify, labels

ROOT = Path("community/v1")
INDEX = ROOT / "index.json"
SNAPSHOT = ROOT / "snapshot.json"
LEDGER = ROOT / "submissions.json"
RESULT_LABELS = {"published": "community-published", "failed": "community-failed",
                 "conflict": "community-classification"}
FIELDS = {"schema_version", "id", "type", "content_id", "region", "language", "title", "body",
          "boss", "phase", "role", "tags", "game_version", "links", "source"}
PUBLIC_FIELDS = FIELDS | {"author", "license", "revision", "updated_at", "status", "issue"}
ID = re.compile(r"(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
LOGIN = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9-]{0,38}")
HEX = re.compile(r"[a-f0-9]{40}")
LICENSES = {"CC-BY-4.0", "CC-BY-SA-4.0"}
MAX_BYTES = 1_250_000
MAX_ISSUE = 30_000
MAX_ENTRIES = 400
MAX_BATCH = 20


class Invalid(ValueError):
    """An explanation safe to publish, with no submitted text."""


def encode(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def decode(raw):
    def unique(pairs):
        result = dict(pairs)
        if len(result) != len(pairs):
            raise Invalid("Duplicate JSON field.")
        return result

    def depth(value, remaining):
        if remaining < 0:
            raise Invalid("JSON nesting limit exceeded.")
        children = value.values() if isinstance(value, dict) else value if isinstance(value, list) else ()
        for child in children:
            depth(child, remaining - 1)

    try:
        value = json.loads(raw, object_pairs_hook=unique, parse_constant=lambda _: None)
        depth(value, 12)
        return value
    except (UnicodeError, ValueError, RecursionError) as error:
        raise Invalid("Invalid JSON data.") from error


def text(value, maximum, multiline=False):
    if not isinstance(value, str):
        return False
    try:
        if len(value.encode("utf-16-le")) // 2 > maximum:
            return False
    except UnicodeEncodeError:
        return False
    normalized = value.replace("\r\n", "\n") if multiline else value
    return all(unicodedata.category(char) != "Cc" or (multiline and char in "\n\t") for char in normalized)


def integer(value, low, high=2_147_483_647):
    return type(value) is int and low <= value <= high


def values(value, maximum, predicate):
    return isinstance(value, list) and len(value) <= maximum and all(predicate(x) for x in value) and len(set(value)) == len(value)


def validate(item, published=False):
    fields = PUBLIC_FIELDS if published else FIELDS
    if not isinstance(item, dict) or set(item) != fields or not integer(item["schema_version"], 1, 1):
        raise Invalid("Unexpected content fields or schema version.")
    if not isinstance(item["id"], str) or ID.fullmatch(item["id"]) is None:
        raise Invalid("Invalid stable content ID.")
    if item["type"] not in ("reminder", "macro") or item["region"] not in ("ALL", "CN", "GLOBAL", "TC") or \
            item["language"] not in ("zh-Hans", "zh-Hant", "en", "ja") or not integer(item["content_id"], 0, 65535):
        raise Invalid("Invalid type, region, language, or duty ID.")
    if not text(item["title"], 120) or not item["title"].strip() or not text(item["body"], 1600, True) or not item["body"].strip():
        raise Invalid("Title or body is empty, invalid, or too long.")
    if not text(item["source"], 240) or any(not text(item[key], 80) for key in ("role", "boss", "phase", "game_version")):
        raise Invalid("Invalid metadata or source.")
    if not values(item["tags"], 10, lambda tag: text(tag, 40) and bool(tag)) or not values(
            item["links"], 10, lambda link: text(link, 36) and ID.fullmatch(link) and link != item["id"]):
        raise Invalid("Invalid tags or links.")
    if item["type"] == "macro":
        lines = item["body"].replace("\r\n", "\n").split("\n")
        if len(lines) > 15 or any(len(line.encode("utf-8")) > 180 for line in lines):
            raise Invalid("A macro supports 15 lines with up to 180 UTF-8 bytes per line.")
    if published:
        author = item["author"]
        if not isinstance(author, dict) or set(author) != {"id", "login"} or \
                not integer(author["id"], 1, 2**63-1) or not isinstance(author["login"], str) or \
                not LOGIN.fullmatch(author["login"]) or item["license"] not in ("CC-BY-4.0", "CC-BY-SA-4.0") or \
                not integer(item["revision"], 1) or not integer(item["issue"], 1) or \
                item["status"] not in ("community", "verified", "outdated", "withdrawn"):
            raise Invalid("Invalid published metadata.")
        try:
            dt.datetime.strptime(item["updated_at"], "%Y-%m-%dT%H:%M:%SZ")
        except (ValueError, TypeError) as error:
            raise Invalid("Invalid publication date.") from error


def validate_links(entries):
    for item in entries.values():
        for link in item["links"]:
            target = entries.get(link)
            if target is None or target["type"] == item["type"]:
                raise Invalid("Links must refer to published items of the other content type.")


def parse_submission(issue):
    body = issue.get("body") or ""
    if len(body.encode("utf-8")) > MAX_ISSUE + 200:
        raise Invalid("Submission exceeds the size limit.")
    match = re.fullmatch(r"(?:### Community submission\s+)?"
                         + re.escape(MARKER) + r"\s+```json\r?\n(.*)\r?\n```\s*", body, re.DOTALL)
    if not match:
        raise Invalid("Paste the complete structured submission block from the app.")
    data = decode(match[1])
    if not isinstance(data, dict) or set(data) != {"schema_version", "license", "consent", "items", "base_revisions"} or \
            not integer(data["schema_version"], 1, 1) or data["consent"] is not True or data["license"] not in ("CC-BY-4.0", "CC-BY-SA-4.0"):
        raise Invalid("Choose a supported license and confirm permission to publish the content.")
    items = data["items"]
    if not isinstance(items, list) or not 1 <= len(items) <= MAX_BATCH:
        raise Invalid("A submission must contain between 1 and 20 items.")
    for item in items:
        validate(item)
    ids = {item["id"] for item in items}
    if len(ids) != len(items):
        raise Invalid("A submission contains a repeated ID.")
    bases = data["base_revisions"]
    if not isinstance(bases, dict) or not set(bases) <= ids or not all(integer(x, 1) for x in bases.values()):
        raise Invalid("Invalid source revision.")
    return data


def evaluate(issue, entries):
    category = classify(issue)
    if issue.get("state") != "open" or "pull_request" in issue or category == "other":
        return None, entries
    if category == "conflict":
        return {"status": "conflict", "detail": "Remove the calibration category labels from this community submission."}, entries
    try:
        data = parse_submission(issue)
        user = issue.get("user") or {}
        if user.get("type") != "User" or not integer(user.get("id"), 1, 2**63-1) or \
                not isinstance(user.get("login"), str) or not LOGIN.fullmatch(user["login"]):
            raise Invalid("A GitHub user account is required.")
        timestamp = dt.datetime.strptime(issue["updated_at"], "%Y-%m-%dT%H:%M:%SZ").strftime("%Y-%m-%dT%H:%M:%SZ")
        author = {"id": user["id"], "login": user["login"]}
        revised = dict(entries)
        for draft in data["items"]:
            current = revised.get(draft["id"])
            base = data["base_revisions"].get(draft["id"])
            if current:
                if current["type"] != draft["type"]:
                    raise Invalid("A published ID cannot change content type.")
                if current["author"]["id"] != author["id"]:
                    raise Invalid("Only the original author can revise a published ID. Create a new ID for your adaptation.")
                if all(current[key] == draft[key] for key in FIELDS) and current["license"] == data["license"]:
                    continue
                if current["status"] == "withdrawn":
                    raise Invalid("A withdrawn entry requires maintainer review.")
                if current["issue"] != issue["number"] and current["revision"] != base:
                    raise Invalid("Update conflict. Refresh the community library and review the latest source revision.")
            elif base is not None:
                raise Invalid("The base revision does not exist.")
            published = dict(draft, author=author, license=data["license"],
                             revision=current["revision"] + 1 if current else 1, updated_at=timestamp,
                             status="community", issue=issue["number"])
            validate(published, published=True)
            revised[draft["id"]] = published
        validate_links(revised)
        if len(revised) > MAX_ENTRIES or len(encode(list(revised.values()))) + 100 > MAX_BYTES:
            raise Invalid("The current community release has reached its capacity.")
        return {"status": "published", "ids": [item["id"] for item in data["items"]]}, revised
    except Invalid as error:
        return {"status": "failed", "detail": str(error)}, entries


def checked(*args, check=True):
    return subprocess.run(args, check=check, stdout=subprocess.PIPE).stdout.strip()


def git(*args):
    return checked("git", *args).decode("utf-8")


def api(repo, route, *args):
    return json.loads(checked("gh", "api", f"repos/{repo}/{route}", *args))


def fingerprint(issue):
    user = issue.get("user") or {}
    return hashlib.sha256(encode([issue.get("body"), issue.get("state"), classify(issue),
                                  user.get("id"), user.get("login")])).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    data = encode(value)
    path.write_bytes(data)
    return data


def load():
    index = decode(INDEX.read_bytes())
    ledger = decode(LEDGER.read_bytes())
    if set(ledger) != {"schema_version", "issues"} or ledger["schema_version"] != 1:
        raise Invalid("Existing submission ledger is invalid.")
    if not SNAPSHOT.exists() and index["generation"] == 0:
        return index, ledger, {}
    commit = index["snapshot_commit"]
    if not isinstance(commit, str) or not HEX.fullmatch(commit):
        raise Invalid("Existing snapshot commit is invalid.")
    if subprocess.run(("git", "merge-base", "--is-ancestor", commit, "HEAD"), check=False).returncode:
        raise Invalid("Snapshot commit is not reachable from main.")
    raw = SNAPSHOT.read_bytes()
    if raw != subprocess.check_output(("git", "show", f"{commit}:{SNAPSHOT.as_posix()}")) or \
            hashlib.sha256(raw).hexdigest() != index["snapshot_sha256"] or len(raw) > MAX_BYTES:
        raise Invalid("Existing snapshot does not match the index.")
    snapshot = decode(raw)
    if snapshot["schema_version"] != 1 or snapshot["generation"] != index["generation"]:
        raise Invalid("Existing snapshot generation is invalid.")
    items = {}
    for item in snapshot["entries"]:
        validate(item, published=True)
        if item["id"] in items:
            raise Invalid("Existing snapshot contains duplicate IDs.")
        items[item["id"]] = item
    validate_links(items)
    return index, ledger, items


def files_for(entries, revised, generation):
    paths = [SNAPSHOT.as_posix()]
    for identifier, item in revised.items():
        if entries.get(identifier) == item:
            continue
        folder = "reminders" if item["type"] == "reminder" else "macros"
        target = ROOT / folder / (identifier + ".json")
        write(target, item)
        paths.append(target.as_posix())
    digest = hashlib.sha256(write(SNAPSHOT, {"schema_version": 1, "generation": generation,
                                               "entries": list(revised.values())})).hexdigest()
    git("add", "--", *paths)
    git("commit", "-m", f"Publish community snapshot {generation}")
    return {"schema_version": 1, "generation": generation, "snapshot_commit": git("rev-parse", "HEAD"),
            "snapshot_sha256": digest}


def reply(repo, number, original, result):
    live = api(repo, f"issues/{number}")
    if fingerprint(live) != fingerprint(original):
        return
    status = result["status"]
    previous = labels(live) & set(RESULT_LABELS.values())
    label = RESULT_LABELS[status]
    marker = "<!-- mr-community-receipt:" + str(number) + ":" + fingerprint(live) + " -->"
    comments = api(repo, f"issues/{number}/comments?per_page=100", "--paginate", "--slurp")
    if not any(comment.get("user", {}).get("login") == "github-actions[bot]"
               and (comment.get("body") or "").startswith(marker)
               for page in comments for comment in page):
        message = result.get("detail", "Published as community content. Format validation does not verify game mechanics.")
        if status == "published":
            message += "\n\nIDs: " + ", ".join(result["ids"]) + "\n\n[Public index](../blob/main/community/v1/index.json)"
        checked("gh", "issue", "comment", str(number), "--repo", repo, "--body", marker + "\n" + message)
    for old in previous - {label}:
        checked("gh", "issue", "edit", str(number), "--repo", repo, "--remove-label", old)
    if label not in previous:
        checked("gh", "issue", "edit", str(number), "--repo", repo, "--add-label", label)


def latest_checkout():
    git("fetch", "origin", "main")
    git("reset", "--hard", "origin/main")


def process(repo, number):
    for attempt in range(3):
        latest_checkout()
        issue = api(repo, f"issues/{number}")
        if issue.get("number") != number or issue.get("state") != "open" or "pull_request" in issue or classify(issue) == "other":
            return
        index, ledger, entries = load()
        digest = fingerprint(issue)
        previous = ledger["issues"].get(str(number))
        if previous and previous["fingerprint"] == digest:
            reply(repo, number, issue, previous["result"])
            return
        result, revised = evaluate(issue, entries)
        if result is None:
            return
        if revised != entries:
            index = files_for(entries, revised, index["generation"] + 1)
        ledger["issues"][str(number)] = {"fingerprint": digest, "result": result}
        write(INDEX, index)
        write(LEDGER, ledger)
        git("add", "--", INDEX.as_posix(), LEDGER.as_posix())
        git("commit", "-m", f"Record community issue #{number}")
        if fingerprint(api(repo, f"issues/{number}")) != digest:
            continue
        push = subprocess.run(("git", "push", "origin", "HEAD:main"), check=False)
        if push.returncode == 0:
            reply(repo, number, issue, result)
            return
        time.sleep(attempt + 1)
    raise RuntimeError(f"Community issue #{number} changed or main advanced during all attempts.")


def sweep(repo):
    failed = []
    page = 1
    while True:
        issues = api(repo, f"issues?state=open&sort=created&direction=asc&per_page=100&page={page}")
        if not issues:
            break
        ledger = decode(LEDGER.read_bytes())
        for issue in issues:
            if "pull_request" in issue or classify(issue) == "other":
                continue
            number = issue["number"]
            previous = ledger["issues"].get(str(number))
            if previous and previous["fingerprint"] == fingerprint(issue) and \
                    RESULT_LABELS[previous["result"]["status"]] in labels(issue):
                continue
            try:
                process(repo, number)
            except (Invalid, RuntimeError, subprocess.CalledProcessError):
                failed.append(number)
        page += 1
    if failed:
        raise RuntimeError(f"Community issues to retry: {failed}")


def moderate(identifier, status, expected_revision):
    if not ID.fullmatch(identifier) or expected_revision is None or status is None:
        raise Invalid("A valid ID, status and expected revision are required.")
    for attempt in range(3):
        latest_checkout()
        index, _, entries = load()
        current = entries.get(identifier)
        if current is None or current["revision"] != expected_revision:
            raise Invalid("The item or its expected revision is no longer current.")
        if current["status"] == status:
            return
        now = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        updated = dict(current, status=status, revision=expected_revision + 1, updated_at=now)
        revised = dict(entries)
        revised[identifier] = updated
        index = files_for(entries, revised, index["generation"] + 1)
        write(INDEX, index)
        git("add", "--", INDEX.as_posix())
        git("commit", "-m", "Record maintainer review of community content")
        if subprocess.run(("git", "push", "origin", "HEAD:main"), check=False).returncode == 0:
            return
        time.sleep(attempt + 1)
    raise RuntimeError("main advanced during all review attempts.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", type=Path)
    parser.add_argument("--moderate")
    parser.add_argument("--status", choices=["community", "verified", "outdated", "withdrawn"])
    parser.add_argument("--revision", type=int)
    args = parser.parse_args()
    repo = os.environ["GH_REPO"]
    if os.environ.get("CI") != "true" or os.environ.get("GITHUB_ACTIONS") != "true" or \
            repo != "Harendotes-Tang/MentorRecorder-Calibrations":
        parser.error("Use the isolated GitHub Actions checkout for publication.")
    git("config", "user.name", "github-actions[bot]")
    git("config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
    if args.event:
        issue = json.loads(args.event.read_bytes())["issue"]
        if classify(issue) != "other":
            process(repo, issue["number"])
    elif args.moderate:
        moderate(args.moderate, args.status, args.revision)
    else:
        latest_checkout()
        sweep(repo)


if __name__ == "__main__":
    main()
