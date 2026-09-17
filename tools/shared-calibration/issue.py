#!/usr/bin/env python3
"""Reads a share-calibration issue, and makes anything echoed back from it harmless.

GitHub renders an issue form into the issue body as markdown: every field becomes a ``### <label>``
heading followed by its value, an empty optional field becomes ``_No response_``, and a checkbox
becomes ``- [X] <option>`` or ``- [ ] <option>``. The labels this module looks for are the ones in
``.github/ISSUE_TEMPLATE/share-calibration.yml`` (test_public_repo_files.py keeps the two in step).

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

_HEADING = re.compile(r"### (.*)")
_CHECKED = re.compile(r"[ \t]*[-*] \[[xX]\] (.*?)[ \t]*")
_TITLE = re.compile(r"\[共享校准\] (CN|GLOBAL) ([A-Za-z0-9][A-Za-z0-9._-]{0,127})")

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
