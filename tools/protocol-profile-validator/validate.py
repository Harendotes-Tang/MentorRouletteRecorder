#!/usr/bin/env python3
"""Validate (and optionally stamp) a MentorRecorder protocol profile.

No third-party dependencies. The checks mirror, one for one, the checks the Collector
performs in src/Collector/Protocol/Profiles/ProfileLoader.cs, so a profile that this tool
accepts is a profile the Collector accepts and vice versa.

Exit codes: 0 valid, 1 invalid, 2 could not run.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
SCHEMA_PATH = os.path.join(REPO, "protocol-profiles", "profile.schema.json")

REQUIRED_MESSAGE_FIELDS = {
    "CONTENT_FINDER_POP": ("roulette_id",),
    "ZONE_INITIALIZATION": (),
    "ZONE_TERRITORY": ("territory_id",),
    "DUTY_RESULT": ("outcome",),
    "PLAYER_JOB": ("job_id",),
    "ZONE_LEFT": (),
    "INSTANCE_LEFT": (),
    "MATCH_CANCELLED": (),
    # Nothing to require: on a build like CN 2026.09.15 the message announcing a match carries
    # no roulette id at any offset, and the message arriving is the whole observation.
    "MATCH_ANNOUNCED": (),
}

FIELD_SIZES = {"u8": 1, "u16": 2, "u32": 4, "i32": 4, "u64": 8}


# --------------------------------------------------------------------------- canonical JSON


def canonical(node) -> str:
    """Canonical JSON: sorted keys, no whitespace, integer numbers only.

    Deliberately narrow so that the Python writer and the C# writer cannot drift apart.
    """
    if node is None:
        return "null"
    if node is True:
        return "true"
    if node is False:
        return "false"
    if isinstance(node, int):
        return str(node)
    if isinstance(node, float):
        raise ValueError("a protocol profile may not contain a non-integer number")
    if isinstance(node, str):
        return canonical_string(node)
    if isinstance(node, list):
        return "[" + ",".join(canonical(item) for item in node) + "]"
    if isinstance(node, dict):
        parts = []
        for key in sorted(node.keys()):
            parts.append(canonical_string(key) + ":" + canonical(node[key]))
        return "{" + ",".join(parts) + "}"
    raise ValueError("unsupported JSON node: " + repr(type(node)))


_SHORT_ESCAPES = {
    chr(0x08): "\\b",
    chr(0x0C): "\\f",
    chr(0x0A): "\\n",
    chr(0x0D): "\\r",
    chr(0x09): "\\t",
    chr(0x22): "\\\"",
    chr(0x5C): "\\\\",
}


def canonical_string(text: str) -> str:
    out = ['"']
    for ch in text:
        if ch in _SHORT_ESCAPES:
            out.append(_SHORT_ESCAPES[ch])
        elif ord(ch) < 0x20:
            out.append("\\u%04x" % ord(ch))
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def profile_hash(document: dict) -> str:
    stripped = {k: v for k, v in document.items() if k != "profile_sha256"}
    return hashlib.sha256(canonical(stripped).encode("utf-8")).hexdigest()


# --------------------------------------------------------------------------- mini schema


def schema_errors(node, schema: dict, root: dict, path: str) -> list:
    errors = []
    if "$ref" in schema:
        ref = schema["$ref"]
        if not ref.startswith("#/"):
            return ["%s: unsupported $ref %s" % (path, ref)]
        target = root
        for segment in ref[2:].split("/"):
            target = target[segment]
        return schema_errors(node, target, root, path)

    if "enum" in schema and node not in schema["enum"]:
        errors.append("%s: value is not one of %s" % (path, schema["enum"]))
    if "const" in schema and node != schema["const"]:
        errors.append("%s: value must be %r" % (path, schema["const"]))

    types = schema.get("type")
    if types is not None:
        if isinstance(types, str):
            types = [types]
        if not any(_is_type(node, t) for t in types):
            errors.append("%s: expected type %s" % (path, "|".join(types)))
            return errors

    if isinstance(node, bool):
        return errors
    if isinstance(node, int):
        if "minimum" in schema and node < schema["minimum"]:
            errors.append("%s: below minimum %s" % (path, schema["minimum"]))
        if "maximum" in schema and node > schema["maximum"]:
            errors.append("%s: above maximum %s" % (path, schema["maximum"]))
    elif isinstance(node, str):
        if "minLength" in schema and len(node) < schema["minLength"]:
            errors.append("%s: shorter than %s" % (path, schema["minLength"]))
        if "maxLength" in schema and len(node) > schema["maxLength"]:
            errors.append("%s: longer than %s" % (path, schema["maxLength"]))
        if "pattern" in schema and re.search(schema["pattern"], node) is None:
            errors.append("%s: does not match %s" % (path, schema["pattern"]))
    elif isinstance(node, list):
        if "minItems" in schema and len(node) < schema["minItems"]:
            errors.append("%s: fewer than %s items" % (path, schema["minItems"]))
        if "maxItems" in schema and len(node) > schema["maxItems"]:
            errors.append("%s: more than %s items" % (path, schema["maxItems"]))
        if "items" in schema:
            for index, item in enumerate(node):
                errors += schema_errors(item, schema["items"], root, "%s[%d]" % (path, index))
    elif isinstance(node, dict):
        for key in schema.get("required", []):
            if key not in node:
                errors.append("%s: missing required property '%s'" % (path, key))
        properties = schema.get("properties", {})
        for key, value in node.items():
            if key in properties:
                errors += schema_errors(value, properties[key], root, "%s.%s" % (path, key))
            elif schema.get("additionalProperties", True) is False:
                errors.append("%s: unexpected property '%s'" % (path, key))
    return errors


def _is_type(node, name: str) -> bool:
    if name == "null":
        return node is None
    if name == "boolean":
        return isinstance(node, bool)
    if name == "integer":
        return isinstance(node, int) and not isinstance(node, bool)
    if name == "number":
        return isinstance(node, (int, float)) and not isinstance(node, bool)
    if name == "string":
        return isinstance(node, str)
    if name == "array":
        return isinstance(node, list)
    if name == "object":
        return isinstance(node, dict)
    return False


# --------------------------------------------------------------------------- semantics


def semantic_errors(document: dict, path: str) -> list:
    errors = []
    # The schema checks the spelling, not whether the day exists in the calendar.
    # Keep the accepted date range aligned with UtcTimestamp.TryParse in the Collector.
    try:
        datetime.datetime.strptime(document["generated_at"], "%Y-%m-%dT%H:%M:%S.%fZ")
    except ValueError:
        errors.append("generated_at is not a valid UTC timestamp")

    stem = os.path.splitext(os.path.basename(path))[0]
    if document.get("profile_id") != stem:
        errors.append("profile_id must equal the file name stem '%s'" % stem)

    directory = os.path.basename(os.path.dirname(os.path.abspath(path))).lower()
    region = document.get("region")
    expected_region = {"cn": "CN", "global": "GLOBAL", "synthetic": "UNKNOWN"}.get(directory)
    if expected_region is not None and region != expected_region:
        errors.append("region %r does not match directory '%s'" % (region, directory))

    status = document.get("compatibility_status")
    messages = document.get("messages") or []
    hypotheses = document.get("hypotheses") or []
    if "hypotheses" in document and status != "CANDIDATE":
        errors.append("only a CANDIDATE profile may declare hypotheses")
    hypothesis_names = [item["name"] for item in hypotheses]
    if len(hypothesis_names) != len(set(hypothesis_names)):
        errors.append("duplicate hypothesis name")
    identities = [(item["direction"], item["opcode"]) for item in hypotheses]
    if len(identities) != len(set(identities)):
        errors.append("duplicate hypothesis (direction, opcode) pair")
    for hypothesis in hypotheses:
        errors += length_errors(hypothesis)
    names = [message.get("name") for message in messages]
    if len(names) != len(set(names)):
        errors.append("duplicate message name")
    opcodes = [(message.get("direction"), message.get("opcode")) for message in messages]
    if len(opcodes) != len(set(opcodes)):
        errors.append("duplicate (direction, opcode) pair")

    if status == "UNSUPPORTED":
        if messages:
            errors.append("an UNSUPPORTED profile must not declare any message")
        if document.get("mentor_roulette_id") is not None:
            errors.append("an UNSUPPORTED profile must declare mentor_roulette_id: null")
    elif status == "CANDIDATE" and not messages and hypotheses:
        if document.get("mentor_roulette_id") is not None:
            errors.append("an opcode-only CANDIDATE profile must declare mentor_roulette_id: null")
    else:
        if document.get("mentor_roulette_id") is None:
            errors.append("a %s profile must declare a mentor_roulette_id" % status)
        # DUTY_RESULT is optional (docs/state-machine.md section 3.10): without it a duty
        # exit closes as UNKNOWN pending review instead of ever being inferred COMPLETED.
        for required in ("CONTENT_FINDER_POP", "ZONE_INITIALIZATION"):
            if required not in names:
                errors.append("missing required message %s" % required)

    if status == "SYNTHETIC" and directory != "synthetic":
        errors.append("a SYNTHETIC profile may only live in protocol-profiles/synthetic/")
    if status != "SYNTHETIC" and directory == "synthetic":
        errors.append("protocol-profiles/synthetic/ may only hold SYNTHETIC profiles")

    if status == "VERIFIED":
        covered = set()
        for item in document.get("provenance", {}).get("evidence", []):
            if item.get("method") != "SYNTHETIC":
                covered.add(item.get("field"))
        for message in messages:
            key = "messages.%s.opcode" % message["name"]
            if key not in covered:
                errors.append("VERIFIED profile lacks evidence for %s" % key)
        if "calibration" in document and "calibration.finder_request.roulette_field" not in covered:
            errors.append(
                "VERIFIED profile lacks evidence for calibration.finder_request.roulette_field")
    if "calibration" in document and status != "VERIFIED":
        errors.append("only a VERIFIED profile may carry a calibration template")
    if "calibration" in document:
        errors += calibration_errors(document["calibration"])

    errors += announcement_errors(messages)

    for message in messages:
        if message["opcode"] in document.get("obfuscated_opcodes", []):
            errors.append("opcode %s is declared obfuscated and also declared as message %s" %
                          (message["opcode"], message["name"]))
        errors += message_errors(message)
    return errors


def announcement_errors(messages: list) -> list:
    """Where MATCH_ANNOUNCED may appear. Mirrors ProfileLoader.CheckAnnouncementContext.

    It exists for a build whose announcement carries no roulette id anywhere, where the profile
    already stands the player's own queue request in for the match and the announcement only adds
    the moment. Beside a CONTENT_FINDER_POP the server sends there is nothing for it to add, and a
    match is something the server tells the client.
    """
    announced = next((m for m in messages if m.get("name") == "MATCH_ANNOUNCED"), None)
    if announced is None:
        return []

    errors = []
    if announced.get("direction") != "SERVER_TO_CLIENT":
        errors.append("MATCH_ANNOUNCED must be SERVER_TO_CLIENT")
    pop = next((m for m in messages if m.get("name") == "CONTENT_FINDER_POP"), None)
    if pop is None or pop.get("direction") != "CLIENT_TO_SERVER":
        errors.append("MATCH_ANNOUNCED is only valid where CONTENT_FINDER_POP is CLIENT_TO_SERVER")
    return errors


def length_errors(message: dict) -> list:
    errors = []
    name = message.get("name")
    has_expected = "expected_length" in message
    has_range = "min_length" in message or "max_length" in message
    if has_expected == has_range:
        errors.append("%s: declare either expected_length or min_length/max_length" % name)
    if "min_length" in message and "max_length" in message:
        if message["max_length"] < message["min_length"]:
            errors.append("%s: max_length is below min_length" % name)
    return errors


def message_errors(message: dict) -> list:
    errors = length_errors(message)
    name = message.get("name")

    upper = message.get("expected_length", message.get("max_length"))
    field_names = [field["name"] for field in message.get("fields", [])]
    if len(field_names) != len(set(field_names)):
        errors.append("%s: duplicate field name" % name)
    for field in message.get("fields", []):
        size = FIELD_SIZES.get(field["type"])
        if size is None:
            if "length" not in field:
                errors.append("%s.%s: a bytes field requires a length" % (name, field["name"]))
                continue
            size = field["length"]
        elif "length" in field:
            errors.append("%s.%s: length applies to bytes fields only" % (name, field["name"]))
        if upper is not None and field["offset"] + size > upper:
            errors.append("%s.%s: reads past the declared message length" % (name, field["name"]))
        constraints = field.get("constraints", {})
        if "min" in constraints and "max" in constraints:
            if constraints["max"] < constraints["min"]:
                errors.append("%s.%s: constraint max is below min" % (name, field["name"]))

    for required in REQUIRED_MESSAGE_FIELDS.get(name, ()):
        if required not in field_names:
            errors.append("%s: missing required field '%s'" % (name, required))
    if name == "DUTY_RESULT" and not message.get("victory_values"):
        errors.append("DUTY_RESULT: victory_values must be a non-empty array")
    return errors


def calibration_errors(calibration: dict) -> list:
    """Mirrors ProfileLoader.ReadCalibration: the roulette field must be a fixed-width
    integer (a bytes run has no numeric value to compare against an echoed roulette id) and
    must not read past the declared request length -- the same bounds check every ordinary
    field gets, applied to this one field.
    """
    errors = []
    request = calibration.get("finder_request", {})
    field = request.get("roulette_field", {})
    expected_length = request.get("expected_length")
    field_type = field.get("type")
    if field_type not in ("u8", "u16", "u32"):
        errors.append(
            "calibration.finder_request.roulette_field: must be u8, u16 or u32")
        return errors
    size = FIELD_SIZES[field_type]
    if expected_length is not None and field.get("offset", 0) + size > expected_length:
        errors.append(
            "calibration.finder_request.roulette_field: reads past the declared request length")
    return errors


def fixture_errors(document: dict, path: str) -> list:
    errors = []
    base = os.path.dirname(os.path.abspath(path))
    for entry in document.get("fixtures", []):
        target = os.path.normpath(os.path.join(base, entry["path"]))
        if not os.path.isfile(target):
            continue
        with open(target, "rb") as handle:
            actual = hashlib.sha256(handle.read()).hexdigest()
        if actual != entry["sha256"]:
            errors.append("fixture %s: SHA-256 mismatch" % entry["path"])
    return errors


# --------------------------------------------------------------------------- entry point


def validate(path: str, stamp: bool):
    try:
        with open(SCHEMA_PATH, "r", encoding="utf-8") as handle:
            schema = json.load(handle)
    except OSError as error:
        return 2, ["cannot read the schema: %s" % error]

    try:
        with open(path, "r", encoding="utf-8") as handle:
            document = json.load(handle)
    except (OSError, ValueError) as error:
        return 1, ["cannot read the profile: %s" % error]

    errors = schema_errors(document, schema, schema, "$")
    if errors:
        return 1, errors

    if stamp:
        document["profile_sha256"] = profile_hash(document)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(document, handle, ensure_ascii=False, indent=2)
            handle.write("\n")

    expected = profile_hash(document)
    if document["profile_sha256"] != expected:
        errors.append("profile_sha256 mismatch: expected %s" % expected)
    errors += semantic_errors(document, path)
    errors += fixture_errors(document, path)
    return (1 if errors else 0), errors


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", help="profile JSON files to check")
    parser.add_argument("--stamp", action="store_true",
                        help="recompute and write profile_sha256 before checking")
    args = parser.parse_args(argv)

    worst = 0
    for path in args.paths:
        code, errors = validate(path, args.stamp)
        worst = max(worst, code)
        label = "OK" if code == 0 else "FAILED"
        print("%s: %s" % (path, label))
        for error in errors:
            print("  - %s" % error)
    return worst


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
