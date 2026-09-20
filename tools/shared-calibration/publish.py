#!/usr/bin/env python3
"""The public calibration repository's GitHub Action, as a command line.

Run by tools/publish_issue.sh and tools/sweep_issues.sh, which .github/workflows/publish-calibration.yml calls:

  check          decide one submission against the repository; for a new code, write its code file
  update-index   decide the same submission again and write index.json and submissions.json, pinning a
                 new code to the commit that added its file (--commit), and recomputing the conflict
                 flags of that region and build (plan section 18.6)
  push-failed    turn the last result into a maintainer error once the workflow gave up pushing
  field          print one validated field of the last result, for the shell
  event-field    print the issue number or the submitter's login from an event file, validated
  pending        list open submissions nobody has answered yet, from `gh api --paginate` output
  wrap-event     wrap a REST issue object as an event file
  revoke         maintainer: mark a code revoked in index.json, recomputing the conflict flags
  report         validate one "report a wrong calibration" issue, for labelling and one reply

``report`` publishes nothing and revokes nothing: a report is a reason for a maintainer to look, and
a count of reports must never be able to take a working calibration down. It only decides whether the
form was filled in, which labels the issue gets, and what the single reply says.

Untrusted input - the issue title, the body, the submitter's login - is read only from the event file
named on the command line, never from arguments or the environment. Every decision writes result.json
and comment.md into --out and prints the result as one line of ASCII JSON, so no issue text can start
a line of the Actions log (and be read as a workflow command).

Exit codes: 0 a result was written, whatever it decided; 2 bad invocation or unreadable helper input;
3 an invariant broke (update-index disagreeing with check, the new code's file missing).
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sys
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterator

import index as repo_index
import issue as issue_form
import rebuild
import sharecode

OWNER = "Harendotes-Tang"
REPOSITORY = "MentorRecorder-Calibrations"
ISSUE_FORM = "share-calibration.yml"
REPORT_FORM = "report-calibration.yml"
CODE_FIELD_ID = "code"

SUBMISSION_LABEL = "share-calibration"
REPORT_LABEL = "calibration-report"
LABEL_PUBLISHED = "published"
LABEL_REJECTED = "rejected"
LABEL_MAINTAINER = "needs-maintainer"
ANSWER_LABELS = frozenset({LABEL_PUBLISHED, LABEL_REJECTED, LABEL_MAINTAINER})
REPORT_LABELS = "%s %s" % (REPORT_LABEL, LABEL_MAINTAINER)
TEMPLATES_DIRECTORY = "templates"
MAX_PENDING = 50
MAX_INPUT_BYTES = 16 * 1024 * 1024

SKIPPED = "skipped"
PUBLISHED = repo_index.PUBLISHED
ADDED = repo_index.ADDED
DUPLICATE = repo_index.DUPLICATE
REFUSED = repo_index.REFUSED
RECEIVED = "received"
ERROR = "error"
STATUSES = (SKIPPED, PUBLISHED, ADDED, DUPLICATE, REFUSED, RECEIVED, ERROR)
CLOSE_COMPLETED = "completed"
CLOSE_NOT_PLANNED = "not planned"

NOT_OPEN = "NOT_OPEN"
NOT_LABELLED = "NOT_LABELLED"
NOT_AN_ISSUE = "NOT_AN_ISSUE"
EVENT_UNREADABLE = "EVENT_UNREADABLE"
ACCOUNT_LOOKUP_FAILED = "ACCOUNT_LOOKUP_FAILED"
ACCOUNT_MISMATCH = "ACCOUNT_MISMATCH"
ACCOUNT_NOT_PERSONAL = "ACCOUNT_NOT_PERSONAL"
NOT_CONFIRMED = "NOT_CONFIRMED"
ALREADY_ANSWERED = "ALREADY_ANSWERED"
TEMPLATE_UNKNOWN = "TEMPLATE_UNKNOWN"
STRUCTURE_INVALID = "STRUCTURE_INVALID"
TEMPLATES_BROKEN = "TEMPLATES_BROKEN"
INDEX_CORRUPT = "INDEX_CORRUPT"
PUSH_FAILED = "PUSH_FAILED"
UNEXPECTED_STATUS = "UNEXPECTED_STATUS"
CODE_FILE_MISSING = "CODE_FILE_MISSING"

_LOGIN = re.compile(r"[A-Za-z0-9-]{1,39}")
_CODE_PATH = re.compile(r"(?:cn|global)/[A-Za-z0-9][A-Za-z0-9._-]{0,127}/[0-9a-f]{12}\.mrc")
_COMMIT_MESSAGE = re.compile(r"[A-Za-z0-9 ._:#()/-]{1,200}")
_ECHOED_DETAILS = frozenset({sharecode.E_UNKNOWN_KEY, sharecode.E_DUPLICATE_KEY})

MATCH_SOURCE_NAMES = {
    "REPLY_STATE": "排本回执里的「已匹配」状态",
    "ANNOUNCEMENT": "独立的「匹配成功」报文",
    "MARKER_OFFSET": "独立的「匹配成功」报文（轮盘编号位置由分享者观察得出）",
    "QUEUE_REQUEST": "按排本申请推断（其他玩家的软件使用前会先征求同意）",
}

_FORM_PROBLEMS = {
    issue_form.BODY_MISSING: "Issue 正文是空的，没有读到校准码。",
    issue_form.BODY_TOO_LARGE: "Issue 正文太长，不是软件预填的表单。",
    issue_form.CODE_MISSING: "没有在「校准码」一栏读到校准码。",
    issue_form.CODE_AMBIGUOUS: "正文里有不止一个「校准码」栏目，无法确定要提交的是哪一个。",
    issue_form.CONFIRM_MISSING: "正文里没有「确认」栏目。",
    issue_form.CONFIRM_AMBIGUOUS: "正文里有不止一个「确认」栏目。",
}
_FORM_ADVICE = "请在软件里点「分享给其他玩家」重新打开预填好的页面提交，不要改动表单的栏目。"

_REFUSALS = {
    repo_index.ACCOUNT_TOO_NEW: "提交校准码的 GitHub 账号需要注册满 30 天，这个账号还不满 30 天。满 30 天后可以重新提交，"
                                "也可以把校准码交给注册满 30 天的玩家代为提交。",
    repo_index.REVOKED: "完全相同的校准码之前发布过，但已被维护者撤销，不再发布。",
    repo_index.PAYLOAD_MISMATCH: "校准码里的客户端版本号无法作为仓库目录名（需要以字母或数字开头），因此无法发布。",
    repo_index.BUILD_NOT_INDEXABLE: "校准码里的客户端版本号无法作为仓库目录名（需要以字母或数字开头），因此无法发布。",
    ACCOUNT_MISMATCH: "无法确认提交这个 Issue 的 GitHub 账号（账号可能刚改过名或已被删除），因此没有受理。",
    ACCOUNT_NOT_PERSONAL: "只受理个人 GitHub 账号的提交，机器人与组织账号不受理。",
    NOT_CONFIRMED: "没有勾选「我在软件里逐条核对过校准时间线」。请先在软件里核对校准时间线，确认无误后再提交。",
    TEMPLATE_UNKNOWN: "这份校准码依据的随包模板本仓库还不认识：生成它的软件版本比仓库支持的新，或者已经过旧。"
                      "请先把软件更新到最新版本再分享；如果已经是最新版本，请等待维护者更新本仓库。",
    STRUCTURE_INVALID: "校准码与它依据的随包模板对不上（例如字段超出了报文长度，或两条报文用了同一个编号），"
                       "不是软件正常生成的校准码。",
}


@dataclass(frozen=True)
class Result:
    """One decision about one issue. Every field is either a constant of this module or validated data."""

    status: str
    reason: str | None = None
    detail: str | None = None
    issue: int | None = None
    region: str | None = None
    game_build: str | None = None
    code_sha256: str | None = None
    match_source: str | None = None
    file: str | None = None
    submitters: int | None = None
    title_mismatch: bool = False
    echo: str | None = None
    replaced: tuple = ()
    replaced_revoked: tuple = ()

    @property
    def label(self) -> str:
        if self.status in (PUBLISHED, ADDED, DUPLICATE):
            return LABEL_PUBLISHED
        return {REFUSED: LABEL_REJECTED, ERROR: LABEL_MAINTAINER}.get(self.status, "")

    @property
    def close_reason(self) -> str:
        if self.status in (PUBLISHED, ADDED, DUPLICATE):
            return CLOSE_COMPLETED
        return CLOSE_NOT_PLANNED if self.status == REFUSED else ""

    def commit_messages(self) -> tuple:
        if self.code_sha256 is None or self.status not in (PUBLISHED, ADDED):
            return "", ""
        what = "%s %s %s" % (self.region, self.game_build, self.code_sha256[:12])
        code_message = "calibration: add " + what if self.status == PUBLISHED else ""
        return code_message, "index: %s %s (issue #%s)" % (self.status, what, self.issue)

    def to_json(self) -> dict:
        code_message, index_message = self.commit_messages()
        return {
            "status": self.status,
            "reason": self.reason,
            "detail": self.detail,
            "issue": self.issue,
            "region": self.region,
            "game_build": self.game_build,
            "code_sha256": self.code_sha256,
            "code_sha12": self.code_sha256[:12] if self.code_sha256 else None,
            "match_source": self.match_source,
            "file": self.file,
            "submitters": self.submitters,
            "replaced": [code[:12] for code in self.replaced],
            "label": self.label,
            "close_reason": self.close_reason,
            "code_commit_message": code_message,
            "index_commit_message": index_message,
            "comment": compose_comment(self),
        }


# --------------------------------------------------------------------------- the reply


def _where(result: Result) -> str:
    return "`%s` `%s`" % (result.region, result.game_build)


def _summary(result: Result) -> list:
    return [
        "- 区服与客户端版本：" + _where(result),
        "- 校准码编号：`%s`" % result.code_sha256[:12],
        "- 匹配方式：" + MATCH_SOURCE_NAMES.get(result.match_source, "未知"),
    ]


_REPLACED_REVOKED = "已用这份校准码替换你此前为该版本提交的那一份（旧码已撤回）。"
_REPLACED_KEPT = "已用这份校准码替换你此前为该版本提交的那一份（那一份仍有其他账号提交，因此继续保留）。"


def _replacement(result: Result) -> str:
    """How this submission replaced the account's earlier code, or an empty string when it did not."""
    if not result.replaced:
        return ""
    return _REPLACED_REVOKED if result.replaced_revoked else _REPLACED_KEPT


def _refusal_text(result: Result) -> str:
    if result.reason in _FORM_PROBLEMS:
        return _FORM_PROBLEMS[result.reason] + _FORM_ADVICE
    if result.reason == repo_index.CODE_INVALID:
        token = result.detail or ""
        tail = "：" + result.echo if result.echo else ""
        return "校准码无法使用：%s（`%s`%s）" % (sharecode.MESSAGES.get(token, "校准码无法使用。"), token, tail)
    text = _REFUSALS.get(result.reason, "校准码没有通过校验（`%s`）。" % result.reason)
    return text.format(where=_where(result))


def compose_comment(result: Result) -> str:
    """The reply posted on the issue (Chinese). Empty for a skipped issue."""
    if result.status == SKIPPED:
        return ""
    if result.status == PUBLISHED:
        opening = ("**已发布，并%s**" % _replacement(result)) if result.replaced else "**已发布。** 谢谢分享！"
        lines = [opening, "", *_summary(result), "",
                 "其他玩家的软件在游戏更新后会自动下载它，并先在自己的本机流量里逐条核实，核实通过才会用来记录。",
                 "下载源有缓存：raw.githubusercontent.com 几分钟内可见，jsDelivr 最多约 12 小时。", "", "本 Issue 自动关闭。"]
    elif result.status == ADDED:
        lines = ["**已计入。** 完全相同的校准码之前已经有人发布过，你的提交已计入：现在共有 %d 个 GitHub 账号提交了它，"
                 "提交的账号越多，其他玩家的软件越优先核实它。" % result.submitters, "", *_summary(result), ""]
        if result.replaced:
            lines += [_replacement(result), ""]
        lines += ["本 Issue 自动关闭。"]
    elif result.status == DUPLICATE:
        lines = ["**没有重复计数。** 这个 GitHub 账号之前已经提交过这份校准码。", "", *_summary(result), "", "本 Issue 自动关闭。"]
    elif result.status == REFUSED:
        lines = ["**没有受理。** " + _refusal_text(result), "",
                 "本 Issue 自动关闭。修正后请重新提交一个新的 Issue；编辑已经关闭的 Issue 不会被重新处理。"]
    else:
        lines = ["**自动发布遇到了仓库这边的问题**（`%s`），这不是你的校准码的问题。" % result.reason, "",
                 "本 Issue 保持打开，维护者会处理；请不要重复提交。"]
    if result.title_mismatch:
        lines += ["", "提示：标题里写的区服或客户端版本与校准码不一致，已按校准码本身处理。"]
    lines += ["", "<sub>MentorRecorder 共享校准 · 自动回复</sub>"]
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- a report of a wrong calibration

_REPORT_RECEIVED = "**已收到。维护者核实后会撤回有问题的校准码，或在此说明原因。**"
_REPORT_OPEN = "本 Issue 保持打开。请不要重复提交；如果还有别的线索，直接在本 Issue 下补充即可。"
_REPORT_ADVICE = "请用「报告校准有误」表单重新提交，不要改动栏目标题；也可以直接在本 Issue 下补齐。本 Issue 保持打开。"
_REPORT_PROBLEMS = {
    issue_form.BODY_MISSING: "Issue 正文是空的。",
    issue_form.BODY_TOO_LARGE: "Issue 正文太长，不是表单填出来的。",
    issue_form.REPORT_REGION_MISSING: "没有在「区服」栏目读到内容。",
    issue_form.REPORT_REGION_AMBIGUOUS: "正文里有不止一个「区服」栏目。",
    issue_form.REPORT_REGION_INVALID: "「区服」栏目的内容不是表单里的选项（CN 或 GLOBAL）。",
    issue_form.REPORT_BUILD_MISSING: "没有在「游戏版本」栏目读到内容。",
    issue_form.REPORT_BUILD_AMBIGUOUS: "正文里有不止一个「游戏版本」栏目。",
    issue_form.REPORT_BUILD_INVALID: "「游戏版本」栏目里的不是一个客户端版本号（形如 2026.09.01.0000.0000）。",
    issue_form.REPORT_SYMPTOM_MISSING: "没有在「现象」栏目读到内容。",
    issue_form.REPORT_SYMPTOM_AMBIGUOUS: "正文里有不止一个「现象」栏目。",
    issue_form.REPORT_SYMPTOM_INVALID: "「现象」栏目的内容不是表单里的选项。",
}


@dataclass(frozen=True)
class ReportResult:
    """One decision about one report. Every field is a constant of this module or a validated option.

    Nothing the reporter wrote is carried here: the region, the build and the symptom are values the
    form offers, and the free-form 说明 is recorded only as present or absent. The reply is therefore
    built from constants alone and can echo nothing.
    """

    status: str
    reason: str | None = None
    issue: int | None = None
    region: str | None = None
    game_build: str | None = None
    symptom: str | None = None
    note: bool = False

    @property
    def labels(self) -> str:
        """The labels the shell adds, space separated. A skipped issue is not touched at all."""
        return "" if self.status == SKIPPED else REPORT_LABELS

    def to_json(self) -> dict:
        return {
            "status": self.status, "reason": self.reason, "issue": self.issue, "region": self.region,
            "game_build": self.game_build, "symptom": self.symptom, "note": self.note,
            "labels": self.labels, "comment": compose_report_comment(self),
        }


def compose_report_comment(result: ReportResult) -> str:
    """The one reply posted on a report (Chinese). Empty for a skipped issue."""
    if result.status == SKIPPED:
        return ""
    if result.status == RECEIVED:
        lines = [_REPORT_RECEIVED, "", _REPORT_OPEN]
    elif result.status == REFUSED:
        problem = _REPORT_PROBLEMS.get(result.reason, "没有读到表单里的栏目。")
        lines = ["**没有读全报告的内容。** " + problem, "", _REPORT_ADVICE]
    else:
        lines = ["**处理这个报告时遇到了仓库这边的问题**（`%s`）。" % result.reason, "",
                 "本 Issue 保持打开，维护者会查看。"]
    return "\n".join(lines + ["", "<sub>MentorRecorder 共享校准 · 自动回复</sub>"]) + "\n"


def evaluate_report(event: Any) -> ReportResult:
    """One report issue decided. Reads only the event file; publishes, revokes and closes nothing."""
    issue = event.get("issue") if isinstance(event, dict) else None
    if not isinstance(issue, dict) or not _positive_int(issue.get("number")):
        return ReportResult(ERROR, EVENT_UNREADABLE)
    number = issue["number"]
    if "pull_request" in issue:
        return ReportResult(SKIPPED, NOT_AN_ISSUE, issue=number)
    if issue.get("state") != "open":
        return ReportResult(SKIPPED, NOT_OPEN, issue=number)
    labels = _label_names(issue)
    if REPORT_LABEL not in labels:
        return ReportResult(SKIPPED, NOT_LABELLED, issue=number)
    # The report stays open, so a later edit or relabel would otherwise answer it a second time.
    if labels & ANSWER_LABELS:
        return ReportResult(SKIPPED, ALREADY_ANSWERED, issue=number)
    form = issue_form.parse_report(issue.get("body"))
    if not form.readable:
        return ReportResult(REFUSED, form.problem, issue=number)
    return ReportResult(RECEIVED, issue=number, region=form.region, game_build=form.game_build,
                        symptom=form.symptom, note=form.has_note)


# --------------------------------------------------------------------------- deciding


@dataclass(frozen=True)
class _Issue:
    number: int
    user_id: int
    login: str
    user_type: Any
    title: Any
    body: Any


def _positive_int(value: Any) -> bool:
    return type(value) is int and value > 0


def _label_names(issue: dict) -> set:
    labels = issue.get("labels")
    if not isinstance(labels, list):
        return set()
    return {label.get("name") for label in labels if isinstance(label, dict) and isinstance(label.get("name"), str)}


def _read_issue(event: Any) -> tuple:
    issue = event.get("issue") if isinstance(event, dict) else None
    if not isinstance(issue, dict) or not _positive_int(issue.get("number")):
        return None, Result(ERROR, EVENT_UNREADABLE)
    number = issue["number"]
    if "pull_request" in issue:
        return None, Result(SKIPPED, NOT_AN_ISSUE, issue=number)
    if issue.get("state") != "open":
        return None, Result(SKIPPED, NOT_OPEN, issue=number)
    if SUBMISSION_LABEL not in _label_names(issue):
        return None, Result(SKIPPED, NOT_LABELLED, issue=number)
    user = issue.get("user")
    if not isinstance(user, dict) or not _positive_int(user.get("id")) or not isinstance(user.get("login"), str):
        return None, Result(ERROR, EVENT_UNREADABLE, issue=number)
    return _Issue(number, user["id"], user["login"], user.get("type"), issue.get("title"), issue.get("body")), None


def _check_account(submitted: _Issue, account: Any) -> tuple:
    number = submitted.number
    if submitted.user_type not in (None, "User") or not _LOGIN.fullmatch(submitted.login):
        return None, Result(REFUSED, ACCOUNT_NOT_PERSONAL, issue=number)
    if not isinstance(account, dict) or not _positive_int(account.get("id")):
        return None, Result(ERROR, ACCOUNT_LOOKUP_FAILED, issue=number)
    if account["id"] != submitted.user_id:
        return None, Result(REFUSED, ACCOUNT_MISMATCH, issue=number)
    if account.get("type") != "User":
        return None, Result(REFUSED, ACCOUNT_NOT_PERSONAL, issue=number)
    created = repo_index.parse_stamp(account.get("created_at"))
    if created is None:
        return None, Result(ERROR, ACCOUNT_LOOKUP_FAILED, issue=number)
    return created, None


def _describe(result: Result, payload: Any, code_sha256: str | None) -> Result:
    if not isinstance(payload, dict):
        return result
    return replace(result, region=payload["region"], game_build=payload["game_build"],
                   code_sha256=code_sha256, match_source=payload["match_source"])


def _check_code(repo: Path, submitted: _Issue) -> tuple:
    """``(payload, code text, None)`` for a code that may be applied, else ``(None, None, refusal)``."""
    number = submitted.number
    form = issue_form.parse_body(submitted.body)
    if not form.readable:
        return None, None, Result(REFUSED, form.problem, issue=number)
    if not form.confirmed:
        return None, None, Result(REFUSED, NOT_CONFIRMED, issue=number)
    decoded = sharecode.decode(form.code)
    if not decoded.valid:
        rejection = decoded.rejection
        echo = issue_form.escape_inline(rejection.detail, 60) if rejection.code in _ECHOED_DETAILS else None
        return None, None, Result(REFUSED, repo_index.CODE_INVALID, rejection.code, issue=number, echo=echo)
    payload = decoded.payload
    described = _describe(Result(REFUSED, issue=number), payload, decoded.code_sha256)
    try:
        templates = rebuild.load_templates(repo / TEMPLATES_DIRECTORY)
    except (rebuild.TemplateError, OSError):
        templates = ()
    if not templates:
        return None, None, replace(described, status=ERROR, reason=TEMPLATES_BROKEN)
    template = rebuild.find_template(templates, payload)
    if template is None:
        return None, None, replace(described, reason=TEMPLATE_UNKNOWN)
    if not rebuild.rebuild(payload, template).built:
        return None, None, replace(described, reason=STRUCTURE_INVALID)
    return payload, sharecode.dotnet_trim(form.code), None


def _with_title(result: Result, submitted: _Issue) -> Result:
    title = issue_form.parse_title(submitted.title)
    mismatch = result.region is not None and title is not None and title != (result.region, result.game_build)
    return replace(result, title_mismatch=mismatch)


def _from_outcome(number: int, outcome: repo_index.SubmissionOutcome) -> Result:
    base = _describe(Result(outcome.status, outcome.reason, outcome.detail, issue=number), outcome.payload, outcome.code_sha256)
    base = replace(base, replaced=outcome.replaced, replaced_revoked=outcome.replaced_revoked)
    if outcome.status == REFUSED and outcome.reason in repo_index.MAINTAINER_REFUSALS:
        return replace(base, status=ERROR)
    if outcome.status == PUBLISHED:
        return replace(base, file=outcome.new_code_path, submitters=1)
    if outcome.entry is not None:
        return replace(base, file=outcome.entry["path"], submitters=outcome.entry["submitters"])
    return base


def evaluate(repo: Path, event: Any, account: Any, now: dt.datetime, commit: str | None) -> tuple:
    """``(Result, SubmissionOutcome or None, code text or None)`` for one issue event."""
    submitted, stop = _read_issue(event)
    if stop is not None:
        return stop, None, None
    created, stop = _check_account(submitted, account)
    if stop is not None:
        return stop, None, None
    payload, code, stop = _check_code(Path(repo), submitted)
    if stop is not None:
        return _with_title(stop, submitted), None, None
    try:
        state = repo_index.load(Path(repo))
    except repo_index.IndexCorrupt:
        return Result(ERROR, INDEX_CORRUPT, issue=submitted.number), None, None
    outcome = repo_index.add_submission(
        state, payload["region"], payload["game_build"], code, submitted.login, created, now, commit,
        account_id=submitted.user_id, issue=submitted.number)
    return _with_title(_from_outcome(submitted.number, outcome), submitted), outcome, code


# --------------------------------------------------------------------------- commands


class UsageError(Exception):
    pass


def _load_json(path: str) -> tuple:
    try:
        data = Path(path).read_bytes()
        if len(data) > MAX_INPUT_BYTES:
            return False, None
        return True, json.loads(data.decode("utf-8"))
    except (OSError, UnicodeDecodeError, ValueError, RecursionError):
        return False, None


def _now(text: str | None) -> dt.datetime:
    if text is None:
        return dt.datetime.now(dt.timezone.utc)
    moment = repo_index.parse_stamp(text)
    if moment is None:
        raise UsageError("--now must look like 2026-09-16T08:00:00Z")
    return moment


def _note(text: str) -> None:
    """A maintainer-facing note on stderr. Never stdout: that carries one line of ASCII JSON and nothing else."""
    print("publish.py: " + text, file=sys.stderr)


def _with_conflicts(repo: Path, state: repo_index.Index, region: str, build: str) -> repo_index.Index:
    """``state`` with the section-18.6 conflict flags recomputed from the code files in the checkout.

    The flags cost bytes the submission was not measured against, so an index that would pass the
    client's byte cap with them is written without them: the flags only reorder candidates, while a
    refused index would take every published code of every build down with it.
    """
    marked = repo_index.update_conflicts(state, repo, region, build, log=_note)
    try:
        repo_index.check_caps(marked)
    except repo_index.IndexFull as full:
        _note("the conflict flags would not fit the index (%s); writing it without them" % full)
        return state
    return marked


def _emit(out: str, result: Any) -> None:
    directory = Path(out)
    directory.mkdir(parents=True, exist_ok=True)
    data = result.to_json()
    (directory / "result.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (directory / "comment.md").write_text(data["comment"], encoding="utf-8", newline="\n")
    print(json.dumps(data, ensure_ascii=True, separators=(",", ":")))


def _decide(args: argparse.Namespace, commit: str | None) -> tuple:
    _, event = _load_json(args.event)
    _, account = _load_json(args.account)
    return evaluate(Path(args.repo), event, account, _now(args.now), commit)


def _code_file_holds(repo: Path, relative: str, code_sha256: str) -> bool | None:
    """True when the file holds this code, False when it holds another, None when there is no file."""
    target = repo / relative
    if not target.is_file():
        return None
    return sharecode.decode(target.read_bytes().decode("utf-8", "replace")).code_sha256 == code_sha256


def command_check(args: argparse.Namespace) -> int:
    result, outcome, code = _decide(args, None)
    if result.status == PUBLISHED:
        repo = Path(args.repo)
        held = _code_file_holds(repo, outcome.new_code_path, outcome.code_sha256)
        if held is False:
            result = replace(result, status=ERROR, reason=repo_index.PATH_COLLISION)
        elif held is None:
            target = repo / outcome.new_code_path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(code.encode("ascii"))
    _emit(args.out, result)
    return 0


def command_update_index(args: argparse.Namespace) -> int:
    if args.expect == PUBLISHED and not repo_index.is_commit(args.commit):
        raise UsageError("--expect published needs --commit <full commit id>")
    result, outcome, _ = _decide(args, args.commit if args.expect == PUBLISHED else None)
    if result.status != args.expect:
        _emit(args.out, replace(result, status=ERROR, reason=UNEXPECTED_STATUS))
        return 3
    repo = Path(args.repo)
    if result.status == PUBLISHED and _code_file_holds(repo, outcome.new_code_path, outcome.code_sha256) is not True:
        _emit(args.out, replace(result, status=ERROR, reason=CODE_FILE_MISSING))
        return 3
    repo_index.write_files(repo, _with_conflicts(repo, outcome.index, result.region, result.game_build))
    _emit(args.out, result)
    return 0


def command_push_failed(args: argparse.Namespace) -> int:
    ok, previous = _load_json(str(Path(args.out) / "result.json"))
    number = previous.get("issue") if ok and isinstance(previous, dict) and _positive_int(previous.get("issue")) else None
    _emit(args.out, Result(ERROR, PUSH_FAILED, issue=number))
    return 0


_FIELD_CHECKS = {
    "status": lambda value: value in STATUSES,
    "label": lambda value: value in ("", LABEL_PUBLISHED, LABEL_REJECTED, LABEL_MAINTAINER),
    "labels": lambda value: value in ("", REPORT_LABELS),
    "close_reason": lambda value: value in ("", CLOSE_COMPLETED, CLOSE_NOT_PLANNED),
    "file": lambda value: value == "" or _CODE_PATH.fullmatch(value) is not None,
    "code_commit_message": lambda value: value == "" or _COMMIT_MESSAGE.fullmatch(value) is not None,
    "index_commit_message": lambda value: value == "" or _COMMIT_MESSAGE.fullmatch(value) is not None,
}


def _out(text: str) -> None:
    """One line for the shell to capture, always ending in a bare LF (Windows would otherwise add CR)."""
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is None:
        sys.stdout.write(text + "\n")
        return
    sys.stdout.flush()
    buffer.write((text + "\n").encode("utf-8"))
    buffer.flush()


def command_field(args: argparse.Namespace) -> int:
    ok, data = _load_json(str(Path(args.out) / "result.json"))
    if not ok or not isinstance(data, dict):
        raise UsageError("no readable result.json in --out")
    value = data.get(args.name)
    value = "" if value is None else value
    if not isinstance(value, str) or not _FIELD_CHECKS[args.name](value):
        raise UsageError("result.json field %s is not an allowed value" % args.name)
    _out(value)
    return 0


def command_event_field(args: argparse.Namespace) -> int:
    ok, event = _load_json(args.event)
    issue = event.get("issue") if ok and isinstance(event, dict) else None
    if not isinstance(issue, dict):
        return 3
    if args.name == "number":
        if not _positive_int(issue.get("number")):
            return 3
        _out(str(issue["number"]))
        return 0
    user = issue.get("user")
    login = user.get("login") if isinstance(user, dict) else None
    if not isinstance(login, str) or _LOGIN.fullmatch(login) is None:
        return 3
    _out(login)
    return 0


def _json_values(text: str) -> Iterator:
    """Every JSON value in a text: `gh api --paginate` prints one array per page, back to back."""
    decoder = json.JSONDecoder()
    position = 0
    while True:
        while position < len(text) and text[position] in " \t\r\n":
            position += 1
        if position >= len(text):
            return
        value, position = decoder.raw_decode(text, position)
        yield value


def _is_pending(item: Any) -> bool:
    return (
        isinstance(item, dict) and _positive_int(item.get("number")) and item.get("state") == "open"
        and "pull_request" not in item and SUBMISSION_LABEL in _label_names(item)
        and not (_label_names(item) & ANSWER_LABELS)
    )


def command_pending(args: argparse.Namespace) -> int:
    try:
        values = list(_json_values(Path(args.issues).read_text(encoding="utf-8")))
    except (OSError, ValueError) as error:
        raise UsageError("cannot read the issue list: " + type(error).__name__) from error
    numbers = set()
    for value in values:
        for item in value if isinstance(value, list) else [value]:
            if _is_pending(item):
                numbers.add(item["number"])
    for number in sorted(numbers)[:MAX_PENDING]:
        _out(str(number))
    return 0


def command_wrap_event(args: argparse.Namespace) -> int:
    ok, issue = _load_json(args.issue)
    if not ok or not isinstance(issue, dict) or not _positive_int(issue.get("number")):
        raise UsageError("not a REST issue object")
    Path(args.out).write_text(json.dumps({"action": "sweep", "issue": issue}, ensure_ascii=False), encoding="utf-8")
    return 0


def command_report(args: argparse.Namespace) -> int:
    _, event = _load_json(args.event)
    _emit(args.out, evaluate_report(event))
    return 0


def command_revoke(args: argparse.Namespace) -> int:
    repo = Path(args.repo)
    state = repo_index.load(repo)
    try:
        updated = repo_index.revoke(state, args.code_sha256)
    except KeyError as error:
        raise UsageError("index.json does not list that code") from error
    revoked = next(entry for entry in updated.entries if entry["code_sha256"] == args.code_sha256)
    repo_index.write_files(repo, _with_conflicts(repo, updated, revoked["region"], revoked["game_build"]))
    print(json.dumps({"revoked": args.code_sha256}))
    return 0


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="publish.py", description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    def decision(name: str) -> argparse.ArgumentParser:
        sub = commands.add_parser(name)
        sub.add_argument("--repo", required=True, help="root of the calibration repository checkout")
        sub.add_argument("--event", required=True, help="path of the GitHub issues event JSON")
        sub.add_argument("--account", required=True, help="path of `gh api users/<login>` output")
        sub.add_argument("--out", required=True, help="directory for result.json and comment.md")
        sub.add_argument("--now", help="UTC time to decide at (tests); default: the clock")
        return sub

    decision("check").set_defaults(run=command_check)
    update = decision("update-index")
    update.add_argument("--expect", required=True, choices=(PUBLISHED, ADDED))
    update.add_argument("--commit", help="commit that added the new code file")
    update.set_defaults(run=command_update_index)

    failed = commands.add_parser("push-failed")
    failed.add_argument("--out", required=True)
    failed.set_defaults(run=command_push_failed)

    field = commands.add_parser("field")
    field.add_argument("--out", required=True)
    field.add_argument("--name", required=True, choices=sorted(_FIELD_CHECKS))
    field.set_defaults(run=command_field)

    event_field = commands.add_parser("event-field")
    event_field.add_argument("--event", required=True)
    event_field.add_argument("--name", required=True, choices=("number", "login"))
    event_field.set_defaults(run=command_event_field)

    pending = commands.add_parser("pending")
    pending.add_argument("--issues", required=True)
    pending.set_defaults(run=command_pending)

    report = commands.add_parser("report")
    report.add_argument("--event", required=True, help="path of the GitHub issues event JSON")
    report.add_argument("--out", required=True, help="directory for result.json and comment.md")
    report.set_defaults(run=command_report)

    wrap = commands.add_parser("wrap-event")
    wrap.add_argument("--issue", required=True)
    wrap.add_argument("--out", required=True)
    wrap.set_defaults(run=command_wrap_event)

    revoke = commands.add_parser("revoke")
    revoke.add_argument("--repo", required=True)
    revoke.add_argument("--code-sha256", required=True)
    revoke.set_defaults(run=command_revoke)
    return parser


def main(argv: list | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        return args.run(args)
    except UsageError as error:
        print("publish.py: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
