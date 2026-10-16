#!/usr/bin/env python3
"""Fixed-command GitHub Action entrypoint; run only in its disposable default-branch checkout.

Issue content is JSON data throughout. No shell interpolation, downloaded executable, dynamic
import, eval or submitted path is used. A non-fast-forward push refreshes main and recomputes the
decision. Ledger, candidate artifacts and public hints are committed and pushed as one unit.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

import evidence
import index as repo_index
import sharecode

LABELS = {"calibration-evidence", "template-observation", "calibration-report"}
MARKER = "<!-- mentorrecorder-evidence-v1 -->"
MAX_ISSUE_BYTES = 256 * 1024
# REST pages contain up to 100 complete objects, including GitHub's own URL/user metadata and
# 65,536-character bodies. A per-object ceiling must not be reused as a whole-page ceiling.
MAX_PAGE_BYTES = 32 * 1024 * 1024


class IssueDeferred(RuntimeError):
    """A specific GitHub object is unavailable; sweep may continue and revisit it on the next wrap."""


def run(*args, check=True):
    return subprocess.run(list(args), check=check, capture_output=True, timeout=90)


def gh(*args):
    if os.name == "nt":
        # Local sandbox uses Git for Windows' bash so its extensionless gh substitute has the same
        # argv semantics as Ubuntu. This fixed string never contains issue or account content.
        git = Path(shutil.which("git") or "")
        bash = next((p / "bin/bash.exe" for p in git.resolve().parents if (p / "bin/bash.exe").is_file()), None)
        if bash is None: raise RuntimeError("git bash required for Windows sandbox")
        return run(str(bash), "-c", 'exec gh "$@"', "--", *args).stdout
    return run("gh", *args).stdout


def api(path, limit=MAX_ISSUE_BYTES):
    try: raw = gh("api", path)
    except subprocess.CalledProcessError as error:
        status = re.search(rb"HTTP ([0-9]{3})", error.stderr or b"")
        if status and status[1] in (b"404", b"410", b"422", b"500", b"502", b"503", b"504"):
            raise IssueDeferred("GITHUB_OBJECT_DEFERRED") from error
        # Authentication, permissions, git and system errors remain visible failures of the run.
        raise
    return evidence.strict_json(raw, limit)


def snapshot(issue):
    return {key: issue.get(key) for key in ("number", "body", "title", "user", "state") } | {
        "labels": sorted(label.get("name", "") for label in issue.get("labels", []) if isinstance(label, dict))}


def extract(body):
    if not isinstance(body, str) or len(body.encode("utf-8")) > 65536: raise evidence.EvidenceError("ISSUE_BODY_LIMIT")
    headings = list(re.finditer(r"^### ([^\r\n]+)\r?$", body, re.M))
    sections = []
    for i, heading in enumerate(headings):
        if heading.group(1).strip() == "公开证据摘要":
            sections.append(body[heading.end():headings[i + 1].start() if i + 1 < len(headings) else len(body)].strip())
    if len(sections) != 1: raise evidence.EvidenceError("EVIDENCE_SECTION_REQUIRED")
    value = sections[0]
    if value.startswith("```json\n") and value.endswith("```"): value = value[8:-3].strip()
    elif value.startswith("```\n") and value.endswith("```"): value = value[4:-3].strip()
    return value


def atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".evidence-", delete=False) as output:
        temporary = Path(output.name)
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def restore_controlled_file(name):
    """reset --hard does not remove a new untracked ledger from a failed first deployment attempt."""
    if name not in (evidence.LEDGER_FILE, "evidence-sweep.json"): raise RuntimeError("uncontrolled path")
    tracked = run("git", "ls-tree", "--name-only", "origin/main", "--", name).stdout.strip()
    if tracked:
        atomic(Path(name), run("git", "show", "origin/main:" + name).stdout)
    else:
        Path(name).unlink(missing_ok=True)


def write_state(state, result, item):
    paths = [evidence.LEDGER_FILE]
    atomic(Path(evidence.LEDGER_FILE), evidence.canonical(state) + b"\n")
    index_path = Path("index.json")
    read = repo_index.read_index(index_path.read_bytes())
    if not read.readable or read.skipped: raise evidence.EvidenceError("PUBLIC_INDEX_CORRUPT")
    data = repo_index.serialize_index(evidence.apply_hints(read.entries, state))
    if len(data) > repo_index.MAX_INDEX_BYTES: raise evidence.EvidenceError("PUBLIC_INDEX_CAPACITY_WAIT")
    if data != index_path.read_bytes():
        atomic(index_path, data); paths.append("index.json")
    if result.get("artifact"):
        name = "candidates/" + result["artifact"] + ".json"
        atomic(Path(name), evidence.canonical(evidence.candidate_artifact(item)) + b"\n")
        paths.append(name)
    return paths


def disposition(result):
    status = result["status"]
    text = {"accepted": "已记录该账号在此候选及判据的最新有效立场；等待其他独立账号的新证据。",
            "duplicate": "该摘要已受理，本次重试不增加票数。",
            "waiting": "证据自动等待：需要两个不同、完整且健康的会话，同一判据有一致结论。",
            "candidate": "已归档 CANDIDATE 草案和缺口报告，协议语义尚不完整，保持不支持。",
            "invalid": "摘要未受理，请在软件中重新预览并提交完整的最小摘要。",
            "report": "已自动归类为不计票的待补证据报告。请使用软件的「共享校准证据」生成摘要后新建证据 Issue；普通文字不会撤回校准码或冻结账号。"}[status]
    if result["reason"] in ("ACCOUNT_CAPACITY_WAIT", "LEDGER_CAPACITY_WAIT", "PUBLIC_INDEX_CAPACITY_WAIT"):
        text = "已达到有界台账的受理容量，暂不新增记录或票数；已有证据与其他账号的额度保持。重试不会删除防重放记录。"
    return (MARKER + "\n\n" + text + "\n\n原因：`" + result["reason"] + "`。\n\n"
            "账号与会话数量只是操作门槛，不能证明真实游戏行为或参与者独立；最终仍须本机完整核实。\n")


def reply(repo, number, live, result):
    # Exactly one bot-owned comment per Issue, updated in place. A process killed after a successful
    # POST recovers it by its marker; a user copying the marker cannot suppress the bot's response.
    if snapshot(api(f"repos/{repo}/issues/{number}")) != snapshot(live): return
    pages = []
    for page_number in range(1, 21):
        page = api(f"repos/{repo}/issues/{number}/comments?per_page=100&page={page_number}", MAX_PAGE_BYTES)
        if not isinstance(page, list): raise evidence.EvidenceError("COMMENT_RESPONSE_INVALID")
        pages.extend(page)
        if len(page) < 100: break
    else: raise evidence.EvidenceError("COMMENT_LIMIT")
    comments = [c for c in pages if c.get("user", {}).get("login") == "github-actions[bot]"
                and c.get("user", {}).get("type") == "Bot" and c.get("body", "").startswith(MARKER)
                and type(c.get("id")) is int and c["id"] > 0]
    body = disposition(result)
    if comments:
        existing = min(comments, key=lambda c: c["id"])
        if existing.get("body") != body:
            gh("api", "--method", "PATCH", f"repos/{repo}/issues/comments/{existing['id']}", "-f", "body=" + body)
    else:
        with tempfile.TemporaryDirectory(prefix="evidence-reply-") as temp:
            path = Path(temp) / "comment.md"; path.write_text(body, encoding="utf-8")
            gh("issue", "comment", str(number), "--body-file", str(path))
    if snapshot(api(f"repos/{repo}/issues/{number}")) != snapshot(live): return
    label = {"candidate": "candidate-waiting", "accepted": "evidence-accepted", "invalid": "evidence-invalid"}.get(result["status"], "evidence-waiting")
    existing_labels = {item.get("name") for item in live.get("labels", []) if isinstance(item, dict)}
    stale = sorted((existing_labels & {"candidate-waiting", "evidence-accepted", "evidence-invalid", "evidence-waiting"}) - {label})
    args = ["issue", "edit", str(number)]
    if label not in existing_labels:
        try: gh("label", "create", label)
        except subprocess.CalledProcessError: pass
        args.extend(("--add-label", label))
    if stale: args.extend(("--remove-label", ",".join(stale)))
    if len(args) > 3: gh(*args)


def process(repo, number):
    for _ in range(5):
        run("git", "fetch", "--quiet", "origin", "main")
        run("git", "reset", "--quiet", "--hard", "origin/main")
        restore_controlled_file(evidence.LEDGER_FILE)
        # No git clean: only exact controlled paths are staged, so an interrupted attempt cannot
        # delete another task's files. Every accepted output is deterministically overwritten.
        live = api(f"repos/{repo}/issues/{number}")
        if live.get("number") != number or "pull_request" in live or live.get("state") != "open": return
        labels = {label.get("name") for label in live.get("labels", []) if isinstance(label, dict)}
        if not labels & LABELS: return
        user = live.get("user", {})
        login = user.get("login")
        if not isinstance(login, str) or re.fullmatch(r"[A-Za-z0-9-]{1,39}", login) is None: return
        author = api("users/" + login)
        if author.get("type") != "User" or user.get("type") != "User" or type(author.get("id")) is not int or author.get("id") != user.get("id"):
            return
        state = evidence.load_ledger(Path("."))
        item = None
        if "calibration-report" in labels and not labels & {"calibration-evidence", "template-observation"}:
            # Persistent dedup plus bot-comment reconciliation; reports never create votes.
            result = {"status": "report", "reason": "STRUCTURED_EVIDENCE_REQUIRED"}
            if str(number) not in state["reports"] and len(state["reports"]) >= evidence.MAX_RECEIPTS:
                result = {"status": "waiting", "reason": "LEDGER_CAPACITY_WAIT"}
            elif (str(number) not in state["reports"] and
                    sum(row["account"] == author["id"] for row in state["reports"].values()) >= evidence.MAX_ACCOUNT_REPORTS):
                result = {"status": "waiting", "reason": "ACCOUNT_CAPACITY_WAIT"}
            else:
                added = str(number) not in state["reports"]
                state["reports"].setdefault(str(number), {"account": author["id"]})
                try: evidence.check_caps(state)
                except evidence.EvidenceError:
                    if added: del state["reports"][str(number)]
                    result = {"status": "waiting", "reason": "LEDGER_CAPACITY_WAIT"}
        else:
            try:
                raw = extract(live.get("body")); item = evidence.parse(raw)
                expected = "template-observation" if item["kind"] == "TEMPLATE_OBSERVATION" else "calibration-evidence"
                if expected not in labels: raise evidence.EvidenceError("FORM_KIND_MISMATCH")
                if item["kind"] == "CODE_VERIFICATION": verify_published_target(item)
                result = evidence.receive(state, raw, account_id=author["id"], account_created_at=author.get("created_at"),
                                          issue_number=number, now=dt.datetime.now(dt.timezone.utc))
                if result["status"] == "duplicate" and result.get("original"):
                    result = result["original"]
            except evidence.EvidenceError as error:
                result = {"status": "waiting" if str(error).endswith("_WAIT") else "invalid", "reason": str(error)}
        try: paths = write_state(state, result, item)
        except evidence.EvidenceError as error:
            if str(error) != "PUBLIC_INDEX_CAPACITY_WAIT": raise
            # No receipt or vote is accepted if its atomic public hint cannot be published. Leave
            # the Issue open and retryable, roll back only our fixed paths, then continue the sweep.
            restore_controlled_file(evidence.LEDGER_FILE)
            run("git", "restore", "--source=origin/main", "--staged", "--worktree", "--", "index.json")
            paths = []
            result = {"status": "waiting", "reason": "PUBLIC_INDEX_CAPACITY_WAIT"}
        if snapshot(api(f"repos/{repo}/issues/{number}")) != snapshot(live): continue
        if paths: run("git", "add", "--", *paths)
        changed = run("git", "diff", "--cached", "--quiet", check=False)
        if changed.returncode not in (0, 1): raise RuntimeError("git diff failed")
        if changed.returncode:
            run("git", "-c", "user.name=github-actions[bot]", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                "commit", "--quiet", "-m", f"Process calibration evidence #{number}")
            if run("git", "push", "--quiet", "origin", "HEAD:main", check=False).returncode:
                continue
        reply(repo, number, live, result)
        return
    raise RuntimeError("main changed repeatedly; scheduled sweep will retry")


def verify_published_target(item):
    """A supplied template hash cannot quarantine an unrelated published code with the same hash."""
    target = item["target"]
    read = repo_index.read_index(Path("index.json").read_bytes())
    entries = [entry for entry in read.entries if all(entry[name] == target[name]
               for name in ("region", "game_build", "code_sha256")) and not entry["revoked"]]
    if not read.readable or not entries: raise evidence.EvidenceError("PUBLISHED_CANDIDATE_REQUIRED")
    code = Path(repo_index.code_path(target["region"], target["game_build"], target["code_sha256"]))
    if not code.is_file(): raise evidence.EvidenceError("PUBLISHED_CODE_MISSING")
    if code.stat().st_size > 48 * 1024: raise evidence.EvidenceError("PUBLISHED_CODE_INVALID")
    decoded = sharecode.decode(code.read_text(encoding="utf-8").strip())
    if not decoded.valid or decoded.code_sha256 != target["code_sha256"]: raise evidence.EvidenceError("PUBLISHED_CODE_INVALID")
    if any(decoded.payload[name] != target[name] for name in ("region", "game_build", "template_profile_id", "template_sha256")):
        raise evidence.EvidenceError("PUBLISHED_TEMPLATE_MISMATCH")


def sweep_checkpoint(cursor, page=1):
    for _ in range(5):
        run("git", "fetch", "--quiet", "origin", "main")
        run("git", "reset", "--quiet", "--hard", "origin/main")
        restore_controlled_file("evidence-sweep.json")
        atomic(Path("evidence-sweep.json"), evidence.canonical({"after_issue": cursor, "next_page": page}) + b"\n")
        run("git", "add", "--", "evidence-sweep.json")
        if run("git", "diff", "--cached", "--quiet", check=False).returncode == 0: return
        run("git", "-c", "user.name=github-actions[bot]", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
            "commit", "--quiet", "-m", "Advance bounded evidence sweep")
        if run("git", "push", "--quiet", "origin", "HEAD:main", check=False).returncode == 0: return
    raise RuntimeError("sweep cursor push conflicted repeatedly")


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", type=Path)
    parser.add_argument("--sweep", action="store_true")
    args = parser.parse_args(argv)
    repo = os.environ.get("GH_REPO", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo): raise RuntimeError("GH_REPO invalid")
    if args.sweep:
        run("git", "fetch", "--quiet", "origin", "main")
        run("git", "reset", "--quiet", "--hard", "origin/main")
        restore_controlled_file("evidence-sweep.json")
        cursor_path = Path("evidence-sweep.json")
        cursor_state = evidence.strict_json(cursor_path.read_bytes()) if cursor_path.is_file() else {"after_issue": 0, "next_page": 1}
        if (not isinstance(cursor_state, dict) or set(cursor_state) != {"after_issue", "next_page"}
                or type(cursor_state["after_issue"]) is not int or cursor_state["after_issue"] < 0
                or type(cursor_state["next_page"]) is not int or not 1 <= cursor_state["next_page"] <= 2147483647):
            raise RuntimeError("sweep cursor invalid")
        cursor = cursor_state["after_issue"]; processed = 0; deferred = 0; succeeded = 0
        # Twenty-five issues per run and a persistent position prevent unanswered older reports or
        # burst-cancelled jobs from starving later submissions. Wrap only after reaching the end.
        last_seen = cursor
        for page in range(cursor_state["next_page"], cursor_state["next_page"] + 50):
            issues = api(f"repos/{repo}/issues?state=open&sort=created&direction=asc&per_page=100&page={page}", MAX_PAGE_BYTES)
            if not isinstance(issues, list): raise RuntimeError("issue listing invalid")
            for issue in issues:
                if type(issue.get("number")) is int: last_seen = max(last_seen, issue["number"])
                if type(issue.get("number")) is int and issue["number"] > cursor and "pull_request" not in issue:
                    labels = {label.get("name") for label in issue.get("labels", []) if isinstance(label, dict)}
                    if labels & LABELS:
                        try:
                            process(repo, issue["number"])
                            succeeded += 1
                        except (IssueDeferred, evidence.EvidenceError) as error:
                            if isinstance(error, evidence.EvidenceError) and str(error) in ("LEDGER_CORRUPT", "PUBLIC_INDEX_CORRUPT"):
                                raise
                            deferred += 1
                            print("::warning::One evidence Issue is deferred; the next sweep wrap will retry it.")
                        processed += 1
                        if processed >= 25:
                            sweep_checkpoint(issue["number"], page)
                            if deferred and not succeeded: raise RuntimeError("all attempted Issues deferred")
                            return 0
            if len(issues) < 100:
                sweep_checkpoint(0)
                if deferred and not succeeded: raise RuntimeError("all attempted Issues deferred")
                return 0
        sweep_checkpoint(last_seen, page + 1)
        if deferred and not succeeded: raise RuntimeError("all attempted Issues deferred")
        return 0
    event = evidence.strict_json(args.event.read_bytes(), MAX_ISSUE_BYTES) if args.event else {}
    number = event.get("issue", {}).get("number")
    if type(number) is not int or number <= 0: raise RuntimeError("event issue number invalid")
    process(repo, number)
    return 0


if __name__ == "__main__":
    try: sys.exit(main())
    except (evidence.EvidenceError, RuntimeError, subprocess.SubprocessError, OSError):
        # Never print arbitrary REST text, submitted content or credentials to workflow logs.
        print("Evidence intake failed; scheduled sweep will retry.", file=sys.stderr)
        sys.exit(1)
