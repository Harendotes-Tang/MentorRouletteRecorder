#!/usr/bin/env python3
"""Rebuild a share code against a shipped template, and refuse a code that cannot describe a profile.

A port of the structural half of ``SharedProfileBuilder.Build`` and ``CalibratedShape.Messages``
(src/Collector/Protocol/Sharing/SharedProfileBuilder.cs, src/Collector/Protocol/Calibration/
CalibratedShape.cs, CalibrationTemplate.cs), so the public repository's Action refuses junk before it
is published. It is a filter, not a verifier: the client repeats all of this and much more.

Ported - the Action refuses a code for any of these, exactly as the client would:

1. ``ShareCode.Check``: everything ``sharecode.decode`` enforces.
2. ``SharedProfileBuilder.IsApplicable``: region, ``template_profile_id`` and ``template_sha256`` name
   a template this repository ships. The client calls a mismatch "not applicable" and quietly skips
   the code; the repository refuses it, because no installed software could use it.
3. ``SharedProfileBuilder.ToValues``: as many selector values as the template pop has learnable
   selectors (selector role, not ``bytes``, not ``roulette_id``).
4. ``CalibratedShape.Refusal``: length / roulette offset / selector values present exactly when the
   match source learns them; length 1..65535; the roulette offset inside the pop; each selector
   value fits its field type.
5. ``CalibratedShape.Messages``: QUEUE_REQUEST needs the territory message; territory and job opcodes
   only when the template declares that message with an ``expected_length``; no two rebuilt messages
   share a direction and opcode; every pop field fits the rebuilt pop's ``expected_length``.
6. ``CalibrationTemplate.From``: a template is VERIFIED, has a ``calibration`` section and a
   ``mentor_roulette_id``, declares CONTENT_FINDER_POP and ZONE_INITIALIZATION with an
   ``expected_length``, and its ``profile_sha256`` matches its canonical content.

Left to the client's local verification (not ported):

* ``ProfileLoader.Validate`` of the complete rebuilt document - JSON schema, profile id rules,
  evidence and provenance rules, what VERIFIED requires. ``SharedProfileBuilder.Build`` refuses a code
  whose document fails it.
* The one-time consent a QUEUE_REQUEST code needs (``SharedProfileBuildStatus.ConsentRequired``);
  here it is only reported as ``needs_consent``.
* Every traffic check of ``SharedCandidateVerifier``:
  whether the opcodes really behave that way on the player's own client. Only that can catch a code
  whose values are well formed but wrong, and the client never records from a code before it passes.

Standard library only. Pure apart from ``load_templates``, which reads a directory.
"""

from __future__ import annotations

import copy
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping

import sharecode

POP = "CONTENT_FINDER_POP"
ZONE = "ZONE_INITIALIZATION"
TERRITORY = "ZONE_TERRITORY"
JOB = "PLAYER_JOB"
ROULETTE_FIELD = "roulette_id"

FIELD_SIZES = {"u8": 1, "u16": 2, "u32": 4, "i32": 4, "u64": 8}
_FIELD_RANGES = {
    "u8": (0, 0xFF),
    "u16": (0, 0xFFFF),
    "u32": (0, 0xFFFFFFFF),
    "i32": (-(2 ** 31), 2 ** 31 - 1),
    "u64": (0, 2 ** 63 - 1),
}
# Templates are ordinary profiles, deeper than a share code: messages > fields > constraints > in.
_TEMPLATE_JSON_DEPTH = 64

BUILT = "BUILT"
NOT_APPLICABLE = "NOT_APPLICABLE"
INVALID = "INVALID"


class TemplateError(ValueError):
    """A template file that cannot serve as a calibration template."""


@dataclass(frozen=True)
class Template:
    """A shipped profile that lends its structure to calibrated profiles (CalibrationTemplate)."""

    name: str
    profile_id: str
    region: str
    sha256: str
    document: Mapping = field(repr=False)

    def message(self, name: str) -> dict | None:
        for message in self.document["messages"]:
            if message.get("name") == name:
                return copy.deepcopy(message)
        return None


@dataclass(frozen=True)
class RebuildResult:
    status: str
    reason: str | None = None
    messages: tuple = ()
    needs_consent: bool = False

    @property
    def built(self) -> bool:
        return self.status == BUILT


# --------------------------------------------------------------------------- templates


def template_hash(document: Mapping) -> str:
    """``profile_sha256`` of a profile: canonical JSON of everything except that field."""
    stripped = {key: value for key, value in document.items() if key != "profile_sha256"}
    return sharecode.sha256_hex(sharecode.canonical(stripped).encode("utf-8"))


def load_template(data: bytes, name: str = "template") -> Template:
    """Reads and checks one template file; raises TemplateError when it cannot be a template."""
    parsed, document = sharecode.strict_json(data, max_depth=_TEMPLATE_JSON_DEPTH)
    if not parsed or not isinstance(document, dict):
        raise TemplateError(name + ": not a JSON object")
    if sharecode.first_duplicate_key(document) is not None:
        raise TemplateError(name + ": repeats a key")
    if document.get("compatibility_status") != "VERIFIED":
        raise TemplateError(name + ": not VERIFIED")
    if document.get("region") not in sharecode.REGIONS:
        raise TemplateError(name + ": region is not CN or GLOBAL")
    if not isinstance(document.get("profile_id"), str):
        raise TemplateError(name + ": no profile_id")
    if type(document.get("mentor_roulette_id")) is not int:
        raise TemplateError(name + ": no mentor_roulette_id")
    try:
        actual = template_hash(document)
    except ValueError as error:
        raise TemplateError(name + ": " + str(error)) from error
    if document.get("profile_sha256") != actual:
        raise TemplateError(name + ": profile_sha256 does not match the content")
    _check_calibration(document.get("calibration"), name)
    _check_messages(document.get("messages"), name)
    return Template(name, document["profile_id"], document["region"], actual, copy.deepcopy(document))


def _check_calibration(calibration: Any, name: str) -> None:
    request = calibration.get("finder_request") if isinstance(calibration, dict) else None
    if not isinstance(request, dict):
        raise TemplateError(name + ": no calibration.finder_request")
    if not isinstance(request.get("direction"), str) or type(request.get("expected_length")) is not int:
        raise TemplateError(name + ": calibration.finder_request has no direction or expected_length")
    _check_field(request.get("roulette_field"), name + ": calibration.finder_request.roulette_field")


def _check_messages(messages: Any, name: str) -> None:
    if not isinstance(messages, list) or not all(isinstance(message, dict) for message in messages):
        raise TemplateError(name + ": messages is not a list of objects")
    names = [message.get("name") for message in messages]
    if len(names) != len(set(names)):
        raise TemplateError(name + ": a message is declared twice")
    for message in messages:
        if not isinstance(message.get("direction"), str) or not isinstance(message.get("fields"), list):
            raise TemplateError(name + ": message without direction or fields")
        for item in message["fields"]:
            _check_field(item, name + ": " + str(message.get("name")))
    for required in (POP, ZONE):
        message = next((m for m in messages if m.get("name") == required), None)
        if message is None or type(message.get("expected_length")) is not int:
            raise TemplateError(name + ": " + required + " with an expected_length is missing")


def _check_field(item: Any, where: str) -> None:
    if not isinstance(item, dict) or not isinstance(item.get("name"), str) or type(item.get("offset")) is not int:
        raise TemplateError(where + ": malformed field")
    kind = item.get("type")
    if kind == "bytes":
        if type(item.get("length")) is not int:
            raise TemplateError(where + ": bytes field without length")
    elif kind not in FIELD_SIZES:
        raise TemplateError(where + ": unknown field type")


def load_templates(directory: Path) -> tuple:
    """Every ``*.json`` in a directory as a template, by file name; raises TemplateError for a bad one."""
    templates = []
    for path in sorted(Path(directory).glob("*.json")):
        templates.append(load_template(path.read_bytes(), path.name))
    ids = [(template.region, template.profile_id, template.sha256) for template in templates]
    if len(ids) != len(set(ids)):
        raise TemplateError("two template files describe the same template")
    return tuple(templates)


def find_template(templates: Iterable[Template], payload: Mapping) -> Template | None:
    for template in templates:
        if is_applicable(payload, template):
            return template
    return None


def is_applicable(payload: Mapping, template: Template) -> bool:
    """SharedProfileBuilder.IsApplicable: same region, template profile id and template hash."""
    return (
        payload.get("region") == template.region
        and payload.get("template_profile_id") == template.profile_id
        and payload.get("template_sha256") == template.sha256
    )


def learnable_selectors(template: Template) -> tuple:
    """CalibratedShape.LearnableSelectors: selector fields that are numeric and not the roulette id."""
    pop = template.message(POP)
    return tuple(
        item for item in pop["fields"]
        if item.get("role") == "selector" and item.get("type") != "bytes" and item.get("name") != ROULETTE_FIELD
    )


# --------------------------------------------------------------------------- rebuild


def rebuild(payload: Any, template: Template) -> RebuildResult:
    """The messages a payload describes on a template, or why it describes none."""
    rejection = sharecode.check(payload)
    if rejection is not None:
        return RebuildResult(INVALID, rejection.code + " " + rejection.detail)
    if not is_applicable(payload, template):
        return RebuildResult(NOT_APPLICABLE, "the code was made against another template or region")

    learnable = learnable_selectors(template)
    pop_values = payload["pop"]
    source = payload["match_source"]
    selectors = pop_values.get("selector_values")
    if selectors is not None and len(selectors) != len(learnable):
        return RebuildResult(INVALID, "the selector values do not match the template's selectors")

    refusal = _refusal(pop_values, source, learnable)
    if refusal is not None:
        return RebuildResult(INVALID, refusal)
    territory = payload.get("territory_opcode")
    job = payload.get("job_opcode")
    if source == "QUEUE_REQUEST" and territory is None:
        return RebuildResult(INVALID, source + " needs the territory message")
    if territory is not None and _declared_length(template.message(TERRITORY)) is None:
        return RebuildResult(INVALID, "the template declares no territory message")
    if job is not None and _declared_length(template.message(JOB)) is None:
        return RebuildResult(INVALID, "the template declares no job message")

    messages = [_pop(template, pop_values, source, learnable), _with_opcode(template.message(ZONE), payload["zone_opcode"])]
    if territory is not None:
        messages.append(_with_opcode(template.message(TERRITORY), territory))
    if job is not None:
        messages.append(_with_opcode(template.message(JOB), job))

    keys = [(message["direction"], message["opcode"]) for message in messages]
    if len(set(keys)) != len(keys):
        return RebuildResult(INVALID, "two messages claim the same direction and opcode")
    built_pop = messages[0]
    length = built_pop.get("expected_length")
    if type(length) is int and any(item["offset"] + field_size(item) > length for item in built_pop["fields"]):
        return RebuildResult(INVALID, "the pop's fields do not fit its length")
    return RebuildResult(BUILT, None, tuple(messages), needs_consent=source == "QUEUE_REQUEST")


def field_size(item: Mapping) -> int:
    """ProfileField.Size: the width of a numeric type, or the declared length of a bytes field."""
    return FIELD_SIZES.get(item.get("type"), item.get("length", 0))


def _declared_length(message: Mapping | None) -> int | None:
    if message is None:
        return None
    length = message.get("expected_length")
    return length if type(length) is int else None


def _refusal(pop: Mapping, source: str, learnable: tuple) -> str | None:
    inputs = sharecode.POP_INPUTS[source]
    for key, what in (("length", "a pop length"), ("roulette_offset", "a roulette offset"), ("selector_values", "selector values")):
        if (key in pop) != (key in inputs):
            return source + (" does not use " if key in pop else " needs ") + what
    length = pop.get("length")
    if length is not None and not 1 <= length <= 0xFFFF:
        return "the pop length is out of range"
    offset = pop.get("roulette_offset")
    if offset is not None and (offset < 0 or length is None or offset + 1 > length):
        return "the roulette offset lies outside the pop"
    values = pop.get("selector_values")
    if values is not None:
        if len(values) != len(learnable):
            return "the selector values do not match the template's selectors"
        for value, item in zip(values, learnable):
            low_high = _FIELD_RANGES.get(item.get("type"))
            if low_high is None or not low_high[0] <= value <= low_high[1]:
                return "the selector values do not match the template's selectors"
    return None


def _with_opcode(message: dict, opcode: int) -> dict:
    rebuilt = dict(message)
    rebuilt["opcode"] = opcode
    rebuilt.pop("segment_type", None)
    return rebuilt


def _pop(template: Template, pop: Mapping, source: str, learnable: tuple) -> dict:
    base = _with_opcode(template.message(POP), pop["opcode"])
    if source == "REPLY_STATE":
        learned = dict(zip((item["name"] for item in learnable), pop["selector_values"]))
        base["fields"] = [
            dict(item, constraints={"in": [learned[item["name"]]]})
            if item.get("role") == "selector" and item.get("name") in learned else item
            for item in base["fields"]
        ]
        return base
    if source in ("ANNOUNCEMENT", "MARKER_OFFSET"):
        base["expected_length"] = pop["length"]
        fields = [item for item in base["fields"] if item.get("role") != "selector"]
        if source == "MARKER_OFFSET":
            base.pop("min_length", None)
            base.pop("max_length", None)
            fields = [
                dict(item, offset=pop["roulette_offset"], type="u8") if item.get("name") == ROULETTE_FIELD else item
                for item in fields
            ]
        base["fields"] = fields
        return base
    request = template.document["calibration"]["finder_request"]
    base["direction"] = request["direction"]
    base["expected_length"] = request["expected_length"]
    base.pop("min_length", None)
    base.pop("max_length", None)
    base["fields"] = [copy.deepcopy(request["roulette_field"])]
    return base
