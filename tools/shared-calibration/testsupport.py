#!/usr/bin/env python3
"""Helpers shared by the tools/shared-calibration tests. Not copied to the public repository."""

from __future__ import annotations

import base64
import copy
import datetime as dt
import functools
import json
import sys
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import rebuild  # noqa: E402
import sharecode  # noqa: E402

VECTORS = REPO / "tests" / "Fixtures" / "shared-calibration" / "vectors.json"
TEMPLATE_PATH = REPO / "protocol-profiles" / "cn" / "cn.2026.08.05.json"
VALIDATOR_DIRECTORY = REPO / "tools" / "protocol-profile-validator"
BUILD = "2026.09.01.0000.0000"
UTC = dt.timezone.utc
NOW = dt.datetime(2026, 9, 16, 8, 0, 0, tzinfo=UTC)
OLD_ACCOUNT = dt.datetime(2020, 1, 1, tzinfo=UTC)
COMMIT = "c" * 40


def load_vectors() -> dict:
    return json.loads(VECTORS.read_text(encoding="utf-8"))


def load_validator():
    """tools/protocol-profile-validator/validate.py, the canonical() the share code format is defined by."""
    if str(VALIDATOR_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(VALIDATOR_DIRECTORY))
    import validate  # noqa: PLC0415

    return validate


def shipped_template_document() -> dict:
    return json.loads(TEMPLATE_PATH.read_text(encoding="utf-8"))


@functools.lru_cache(maxsize=1)
def shipped_template() -> rebuild.Template:
    return rebuild.load_template(TEMPLATE_PATH.read_bytes(), TEMPLATE_PATH.name)


def template_from(document: dict, name: str = "variant.json") -> rebuild.Template:
    """A template from an edited copy of a profile, re-stamped so only the edit differs."""
    stamped = copy.deepcopy(document)
    stamped["profile_sha256"] = rebuild.template_hash(stamped)
    return rebuild.load_template(json.dumps(stamped, ensure_ascii=False).encode("utf-8"), name)


def raw_code(inflated: bytes) -> str:
    """A code around arbitrary bytes: raw DEFLATE and base64url, with no check of what the bytes are."""
    compressor = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
    raw = compressor.compress(inflated) + compressor.flush()
    return sharecode.PREFIX + base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def deflate(data: bytes) -> bytes:
    compressor = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
    return compressor.compress(data) + compressor.flush()


def code_from_raw(raw: bytes) -> str:
    return sharecode.PREFIX + base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def payload(source: str = "ANNOUNCEMENT", number: int = 0, region: str = "CN", build: str = BUILD,
            template: rebuild.Template | None = None, **overrides) -> dict:
    """A valid payload for the shipped template; ``overrides`` replace top-level keys, None removes one."""
    template = template or shipped_template()
    pop = {"opcode": 2000 + number}
    if source == "REPLY_STATE":
        pop["selector_values"] = [3]
    elif source == "ANNOUNCEMENT":
        pop["length"] = 40
    elif source == "MARKER_OFFSET":
        pop.update(length=24, roulette_offset=8)
    result = {
        "v": 1, "region": region, "game_build": build, "template_profile_id": template.profile_id,
        "template_sha256": template.sha256, "match_source": source, "pop": pop,
        "zone_opcode": 1000 + number, "territory_opcode": 3000 + number, "job_opcode": 4000 + number,
    }
    for key, value in overrides.items():
        if value is None:
            result.pop(key, None)
        else:
            result[key] = value
    return result


# The texts the client puts into the share form's 这份校准码的来源 input, by the value issue.py records for them.
BASIS_OPTIONS = {
    "calibrated": "本机校准得到，并逐条核对过校准时间线",
    "verified": "其他玩家分享，已在我的软件里核实通过并正常记录",
}


def issue_body(code: str, basis: str | None = "calibrated", checked: bool = True) -> str:
    """An issue body exactly as GitHub renders the share-calibration form (plan section 20.2).

    ``basis`` is "calibrated" or "verified" for the text the client prefills, any other text for a field
    edited by hand, or None for the field left empty (``_No response_``, as from a client that sends none).
    """
    stated = "_No response_" if basis is None else BASIS_OPTIONS.get(basis, basis)
    return "### 校准码\n\n%s\n\n### 这份校准码的来源\n\n%s\n\n### 确认\n\n- [%s] 上面的来源属实，我已在软件里核对过\n" % (
        code, stated, "X" if checked else " ")


def checkbox_issue_body(code: str, checked: bool = True) -> str:
    """A body of the share form before plan section 20.2: no 这份校准码的来源 and the older 确认 option."""
    return "### 校准码\n\n%s\n\n### 确认\n\n- [%s] 我在软件里逐条核对过校准时间线\n" % (code, "X" if checked else " ")


def report_body(region: str = "CN", build: str = BUILD, symptom: str = "弹窗时误报匹配", note: str | None = None) -> str:
    """An issue body exactly as GitHub renders the report-calibration form; None leaves 说明 empty."""
    return "### 区服\n\n%s\n\n### 游戏版本\n\n%s\n\n### 现象\n\n%s\n\n### 说明\n\n%s\n" % (
        region, build, symptom, note if note is not None else "_No response_")
