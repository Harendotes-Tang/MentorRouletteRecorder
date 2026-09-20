#!/usr/bin/env python3
"""Reads a share-calibration or report-calibration issue, and makes anything echoed back harmless.

GitHub renders an issue form into the issue body as markdown: every field becomes a ``### <label>``
heading followed by its value, an empty optional field becomes ``_No response_``, and a checkbox
becomes ``- [X] <option>`` or ``- [ ] <option>``. The labels this module looks for are the ones in
``.github/ISSUE_TEMPLATE/share-calibration.yml`` and ``report-calibration.yml``
(test_public_repo_files.py keeps them in step).

The title and the body are untrusted: anyone can open an issue from the form and then edit the body
into anything. So nothing is guessed. A missing or repeated section is a refusal, not a best effort;
the title is parsed only to notice that it disagrees with the code, and never decides anything; and
text taken from the issue is echoed back only through ``escape_inline``.

Standard library only; pure.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Any

CODE_LABEL = "校准码"
CONFIRM_LABEL = "确认"
CONFIRM_OPTION = "我在软件里逐条核对过校准时间线"
TITLE_PREFIX = "[共享校准] "
NO_RESPONSE = "_No response_"
MAX_BODY_CHARS = 65536
MAX_TITLE_CHARS = 256

BODY_MISSING = "BODY_MISSING"
BODY_TOO_LARGE = "BODY_TOO_LARGE"
CODE_MISSING = "CODE_MISSING"
CODE_AMBIGUOUS = "CODE_AMBIGUOUS"
CONFIRM_MISSING = "CONFIRM_MISSING"
CONFIRM_AMBIGUOUS = "CONFIRM_AMBIGUOUS"

# The "report a wrong calibration" form. It carries no code and starts nothing automatic: the fields
# exist so a maintainer knows where to look, so every one of them is a fixed option or a build number.
REPORT_REGION_LABEL = "区服"
REPORT_BUILD_LABEL = "游戏版本"
REPORT_SYMPTOM_LABEL = "现象"
REPORT_NOTE_LABEL = "说明"
REPORT_TITLE_PREFIX = "[校准有误] "
REPORT_REGIONS = ("CN", "GLOBAL")
REPORT_SYMPTOMS = ("弹窗时误报匹配", "记录不到", "职业或副本不对", "其他")

REPORT_REGION_MISSING = "REPORT_REGION_MISSING"
REPORT_REGION_AMBIGUOUS = "REPORT_REGION_AMBIGUOUS"
REPORT_REGION_INVALID = "REPORT_REGION_INVALID"
REPORT_BUILD_MISSING = "REPORT_BUILD_MISSING"
REPORT_BUILD_AMBIGUOUS = "REPORT_BUILD_AMBIGUOUS"
REPORT_BUILD_INVALID = "REPORT_BUILD_INVALID"
REPORT_SYMPTOM_MISSING = "REPORT_SYMPTOM_MISSING"
REPORT_SYMPTOM_AMBIGUOUS = "REPORT_SYMPTOM_AMBIGUOUS"
REPORT_SYMPTOM_INVALID = "REPORT_SYMPTOM_INVALID"

_HEADING = re.compile(r"### (.*)")
_CHECKED = re.compile(r"[ \t]*[-*] \[[xX]\] (.*?)[ \t]*")
_TITLE = re.compile(r"\[共享校准\] (CN|GLOBAL) ([A-Za-z0-9][A-Za-z0-9._-]{0,127})")
# The same client build a code carries (index.is_build); anything else is not a build the maintainer can look up.
_REPORT_BUILD = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")

# Rendered literally when preceded by a backslash in GitHub markdown.
_BACKSLASH_ESCAPED = frozenset("\\`*_{}[]()+-!|<>~=&\"'$^%")
# Replaced by look-alikes, because a backslash does not stop GitHub from linking or mentioning:
# @user (mention), #123 (issue link), scheme://host and www.host (autolinks).
_LOOK_ALIKES = {"@": "＠", "#": "＃", ":": "：", "/": "／", ".": "．"}


@dataclass(frozen=True)
class IssueForm:
    """The two form values, or the reason the body could not be read as the form."""

    code: str | None
    confirmed: bool
    problem: str | None

    @property
    def readable(self) -> bool:
        return self.problem is None


def _sections(body: str) -> list:
    """``[(label, [line, ...]), ...]`` for every ``### `` heading, in order; text before the first is dropped."""
    sections: list = []
    for line in body.split("\n"):
        heading = _HEADING.fullmatch(line)
        if heading is not None:
            sections.append((heading.group(1).strip(), []))
        elif sections:
            sections[-1][1].append(line)
    return sections


def parse_body(body: Any) -> IssueForm:
    """The code and whether the confirmation box is ticked, from an issue-form body."""
    if not isinstance(body, str) or not body.strip():
        return IssueForm(None, False, BODY_MISSING)
    if len(body) > MAX_BODY_CHARS:
        return IssueForm(None, False, BODY_TOO_LARGE)
    sections = _sections(body.replace("\r\n", "\n").replace("\r", "\n"))
    code_sections = [lines for label, lines in sections if label == CODE_LABEL]
    confirm_sections = [lines for label, lines in sections if label == CONFIRM_LABEL]
    if len(code_sections) > 1:
        return IssueForm(None, False, CODE_AMBIGUOUS)
    if len(confirm_sections) > 1:
        return IssueForm(None, False, CONFIRM_AMBIGUOUS)
    if not code_sections:
        return IssueForm(None, False, CODE_MISSING)
    if not confirm_sections:
        return IssueForm(None, False, CONFIRM_MISSING)

    code = _unfence(code_sections[0])
    if not code or code == NO_RESPONSE:
        return IssueForm(None, False, CODE_MISSING)
    confirmed = any(
        match is not None and match.group(1) == CONFIRM_OPTION
        for match in (_CHECKED.fullmatch(line) for line in confirm_sections[0])
    )
    return IssueForm(code, confirmed, None)


def _unfence(lines: list) -> str:
    """The section's text; one fenced block around the whole value (```...```) is unwrapped."""
    kept = [line for line in lines]
    while kept and not kept[0].strip():
        kept.pop(0)
    while kept and not kept[-1].strip():
        kept.pop()
    if len(kept) >= 2 and kept[0].strip().startswith("```") and kept[-1].strip() == "```":
        kept = kept[1:-1]
    return "\n".join(kept).strip()


@dataclass(frozen=True)
class ReportForm:
    """The three report values, or the reason the body could not be read as the form.

    The optional 说明 is recorded as present or absent only: its text is free-form and nothing here
    or in the reply ever needs it, so it never leaves the issue body.
    """

    region: str | None
    game_build: str | None
    symptom: str | None
    has_note: bool
    problem: str | None

    @property
    def readable(self) -> bool:
        return self.problem is None


# (attribute, section label, missing, ambiguous, invalid, what the form offers)
_REPORT_FIELDS = (
    ("region", REPORT_REGION_LABEL, REPORT_REGION_MISSING, REPORT_REGION_AMBIGUOUS, REPORT_REGION_INVALID,
     lambda text: text in REPORT_REGIONS),
    ("game_build", REPORT_BUILD_LABEL, REPORT_BUILD_MISSING, REPORT_BUILD_AMBIGUOUS, REPORT_BUILD_INVALID,
     lambda text: _REPORT_BUILD.fullmatch(text) is not None),
    ("symptom", REPORT_SYMPTOM_LABEL, REPORT_SYMPTOM_MISSING, REPORT_SYMPTOM_AMBIGUOUS, REPORT_SYMPTOM_INVALID,
     lambda text: text in REPORT_SYMPTOMS),
)


def parse_report(body: Any) -> ReportForm:
    """The three report values, from a report-calibration issue-form body; nothing is guessed."""
    if not isinstance(body, str) or not body.strip():
        return ReportForm(None, None, None, False, BODY_MISSING)
    if len(body) > MAX_BODY_CHARS:
        return ReportForm(None, None, None, False, BODY_TOO_LARGE)
    sections = _sections(body.replace("\r\n", "\n").replace("\r", "\n"))
    values = {}
    for name, label, missing, ambiguous, invalid, offered in _REPORT_FIELDS:
        found = [lines for heading, lines in sections if heading == label]
        if len(found) > 1:
            return ReportForm(None, None, None, False, ambiguous)
        text = _unfence(found[0]) if found else ""
        if not text or text == NO_RESPONSE:
            return ReportForm(None, None, None, False, missing)
        if not offered(text):
            return ReportForm(None, None, None, False, invalid)
        values[name] = text
    has_note = any(
        _unfence(lines) not in ("", NO_RESPONSE) for heading, lines in sections if heading == REPORT_NOTE_LABEL
    )
    return ReportForm(values["region"], values["game_build"], values["symptom"], has_note, None)


def parse_title(title: Any) -> tuple | None:
    """``(region, build)`` from ``[共享校准] CN 2026.09.01.0000.0000``, or None. For display only."""
    if not isinstance(title, str) or len(title) > MAX_TITLE_CHARS:
        return None
    match = _TITLE.fullmatch(title.strip())
    return (match.group(1), match.group(2)) if match is not None else None


def escape_inline(text: Any, max_chars: int = 80) -> str:
    """Untrusted text made safe to put inside a comment: one line, no markup, no mention, no link.

    Control, format (zero-width) and separator characters become spaces and runs of spaces collapse;
    the result is cut to ``max_chars`` characters before escaping; markdown punctuation is
    backslash-escaped and ``@ # : / .`` are replaced by full-width look-alikes.
    """
    raw = "" if text is None else str(text)
    visible = "".join(
        ch if ch == " " or (ch.isprintable() and unicodedata.category(ch)[0] not in "CZ") else " " for ch in raw
    )
    collapsed = " ".join(visible.split())
    if len(collapsed) > max_chars:
        collapsed = collapsed[:max_chars] + "…"
    out = []
    for ch in collapsed:
        if ch in _LOOK_ALIKES:
            out.append(_LOOK_ALIKES[ch])
        elif ch in _BACKSLASH_ESCAPED:
            out.append("\\" + ch)
        else:
            out.append(ch)
    return "".join(out)
