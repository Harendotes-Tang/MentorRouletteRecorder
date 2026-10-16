#!/usr/bin/env python3
"""Bounded, deterministic public evidence intake. Account counts are not proof of gameplay.

The sole wire schema is contracts/calibration-evidence-v1.schema.json (copied to schemas/ when
deployed). This standard-library interpreter supports precisely the keywords that contract uses;
unknown validation keywords fail closed rather than silently relaxing a later contract.
"""
from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import re
from pathlib import Path

MAX_BYTES = 48 * 1024
MAX_LEDGER_BYTES = 8 * 1024 * 1024
MAX_RECEIPTS = 4096
MAX_SESSIONS = 32768
MAX_VOTES = 16384
MAX_ACCOUNT_RECEIPTS = 128
MAX_ACCOUNT_SESSIONS = 1024
MAX_ACCOUNT_REPORTS = 64
LEDGER_FILE = "evidence-ledger.json"
FIELDS = ("zone", "territory", "job", "match")


class EvidenceError(ValueError):
    """Safe reason code only: untrusted input is never interpolated in a public reply."""


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def _pairs(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise EvidenceError("DUPLICATE_KEY")
        result[name] = value
    return result


def strict_json(data, limit=MAX_BYTES):
    if isinstance(data, str):
        data = data.encode("utf-8")
    if not isinstance(data, bytes) or len(data) > limit:
        raise EvidenceError("SIZE_LIMIT")
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(EvidenceError("NONFINITE")))
        def depth(item, level=0):
            if level > 20:
                raise EvidenceError("DEPTH_LIMIT")
            if isinstance(item, dict):
                for child in item.values(): depth(child, level + 1)
            elif isinstance(item, list):
                for child in item: depth(child, level + 1)
        depth(value)
        return value
    except (UnicodeError, ValueError, RecursionError) as error:
        if isinstance(error, EvidenceError): raise
        raise EvidenceError("INVALID_JSON") from error


def schema_path():
    here = Path(__file__).resolve().parent
    deployed = here.parent / "schemas/calibration-evidence-v1.schema.json"
    return deployed if deployed.is_file() else here.parent.parent / "contracts/calibration-evidence-v1.schema.json"


def _validate(value, spec, root):
    known = {"$schema", "$id", "title", "$defs", "$ref", "type", "const", "enum", "properties",
             "required", "additionalProperties", "items", "minItems", "maxItems", "maxLength",
             "pattern", "minimum", "maximum", "allOf", "if", "then"}
    if set(spec) - known:
        raise EvidenceError("UNSUPPORTED_SCHEMA")
    if "$ref" in spec:
        ref = spec["$ref"]
        if not ref.startswith("#/$defs/"): raise EvidenceError("UNSUPPORTED_SCHEMA")
        _validate(value, root["$defs"][ref[len("#/$defs/"):]], root)
    if "const" in spec and (type(value) is not type(spec["const"]) or value != spec["const"]):
        raise EvidenceError("SCHEMA_CONST")
    if "enum" in spec and not any(type(value) is type(v) and value == v for v in spec["enum"]):
        raise EvidenceError("SCHEMA_ENUM")
    types = {"object": dict, "array": list, "integer": int, "string": str, "boolean": bool, "null": type(None)}
    if "type" in spec:
        names = spec["type"] if isinstance(spec["type"], list) else [spec["type"]]
        if not any(type(value) is types[name] for name in names): raise EvidenceError("SCHEMA_TYPE")
    if isinstance(value, dict):
        if set(spec.get("required", [])) - set(value): raise EvidenceError("SCHEMA_REQUIRED")
        props = spec.get("properties", {})
        if spec.get("additionalProperties") is False and set(value) - set(props):
            raise EvidenceError("SCHEMA_PRIVATE_OR_UNKNOWN_FIELD")
        for name in set(value) & set(props): _validate(value[name], props[name], root)
    if isinstance(value, list):
        if len(value) < spec.get("minItems", 0) or len(value) > spec.get("maxItems", MAX_BYTES):
            raise EvidenceError("SCHEMA_ARRAY_SIZE")
        if "items" in spec:
            for item in value: _validate(item, spec["items"], root)
    if isinstance(value, str):
        if len(value) > spec.get("maxLength", MAX_BYTES): raise EvidenceError("SCHEMA_STRING_SIZE")
        if "pattern" in spec and re.fullmatch(spec["pattern"], value) is None: raise EvidenceError("SCHEMA_PATTERN")
    if type(value) is int and not spec.get("minimum", value) <= value <= spec.get("maximum", value):
        raise EvidenceError("SCHEMA_RANGE")
    for child in spec.get("allOf", []): _validate(value, child, root)
    if "if" in spec:
        try: _validate(value, spec["if"], root)
        except EvidenceError: pass
        else: _validate(value, spec.get("then", {}), root)


def parse(data):
    value = strict_json(data)
    schema = strict_json(schema_path().read_bytes())
    _validate(value, schema, schema)
    # Build counters are opaque counters, not a time of day.
    try: dt.date(*map(int, value["target"]["game_build"].split(".")[:3]))
    except ValueError as error: raise EvidenceError("INVALID_BUILD_DATE") from error
    aliases = [session["session_alias"] for session in value["sessions"]]
    if len(aliases) != len(set(aliases)): raise EvidenceError("REPEATED_SESSION")
    for session in value["sessions"]:
        checks = [check["field"] for check in session["checks"]]
        if len(checks) != len(set(checks)): raise EvidenceError("REPEATED_CHECK")
    return value


def empty_ledger():
    return {"schema_version": 1, "sequence": 0, "receipts": {}, "sessions": {}, "votes": {},
            "candidates": {}, "issues": {}, "reports": {}}


def load_ledger(root):
    try: return _load_ledger(root)
    except EvidenceError as error: raise EvidenceError("LEDGER_CORRUPT") from error


def _load_ledger(root):
    path = Path(root) / LEDGER_FILE
    if not path.exists(): return empty_ledger()
    state = strict_json(path.read_bytes(), MAX_LEDGER_BYTES)
    if not isinstance(state, dict) or set(state) != set(empty_ledger()) or type(state["schema_version"]) is not int or state["schema_version"] != 1:
        raise EvidenceError("LEDGER_CORRUPT")
    if type(state["sequence"]) is not int or state["sequence"] < 0:
        raise EvidenceError("LEDGER_CORRUPT")
    for name in ("receipts", "sessions", "votes", "candidates", "issues", "reports"):
        if not isinstance(state[name], dict): raise EvidenceError("LEDGER_CORRUPT")
    _check_ledger_records(state)
    check_caps(state)
    return state


def _record(row, fields):
    if not isinstance(row, dict) or set(row) != set(fields.split()): raise EvidenceError("LEDGER_CORRUPT")


def _natural(value, positive=False):
    if type(value) is not int or value < (1 if positive else 0): raise EvidenceError("LEDGER_CORRUPT")


def _hash(value):
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None: raise EvidenceError("LEDGER_CORRUPT")


def _check_ledger_records(state):
    """Internal format is strict too: a damaged repository never silently resets replay protection."""
    for key, row in state["receipts"].items():
        if re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", key) is None: raise EvidenceError("LEDGER_CORRUPT")
        _record(row, "hash account issue sequence result")
        _hash(row["hash"])
        for name in ("account", "issue", "sequence"): _natural(row[name], True)
        result = row["result"]
        if not isinstance(result, dict) or not {"status", "reason", "hash"} <= set(result) <= {"status", "reason", "hash", "candidate", "artifact"}:
            raise EvidenceError("LEDGER_CORRUPT")
        if result["status"] not in ("accepted", "waiting", "candidate") or not re.fullmatch(r"[A-Z_]+", result["reason"]): raise EvidenceError("LEDGER_CORRUPT")
        for name in ("hash", "candidate", "artifact"):
            if name in result: _hash(result[name])
    for key, row in state["sessions"].items():
        if re.fullmatch(r"[1-9][0-9]*:[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", key) is None: raise EvidenceError("LEDGER_CORRUPT")
        _record(row, "hash sequence candidate epoch"); _hash(row["hash"]); _natural(row["sequence"], True)
        _hash(row["candidate"]); _natural(row["epoch"])
    for key, row in state["issues"].items():
        if re.fullmatch(r"[1-9][0-9]*", key) is None: raise EvidenceError("LEDGER_CORRUPT")
        _record(row, "account sequence voted"); _natural(row["account"], True); _natural(row["sequence"], True)
        if type(row["voted"]) is not bool: raise EvidenceError("LEDGER_CORRUPT")
    for key, row in state["reports"].items():
        if re.fullmatch(r"[1-9][0-9]*", key) is None: raise EvidenceError("LEDGER_CORRUPT")
        _record(row, "account"); _natural(row["account"], True)
    schema = strict_json(schema_path().read_bytes())
    for key, row in state["candidates"].items():
        _hash(key); _record(row, "scope epoch quarantined triggers consumed")
        _record(row["scope"], "region game_build template_profile_id template_sha256 code_sha256 rules_version")
        for name, value in row["scope"].items():
            spec = schema["properties"]["producer"]["properties"][name] if name == "rules_version" else schema["properties"]["target"]["properties"][name]
            _validate(value, spec, schema)
        if key != digest(row["scope"]) or any(row["scope"][name] is None for name in ("template_profile_id", "template_sha256", "code_sha256")): raise EvidenceError("LEDGER_CORRUPT")
        _natural(row["epoch"])
        if row["epoch"] > 2147483647 or type(row["quarantined"]) is not bool: raise EvidenceError("LEDGER_CORRUPT")
        for name in ("triggers", "consumed"):
            if not isinstance(row[name], dict) or set(row[name]) - set(FIELDS): raise EvidenceError("LEDGER_CORRUPT")
            for sequence in row[name].values(): _natural(sequence, True)
        if row["quarantined"] != bool(row["triggers"]): raise EvidenceError("LEDGER_CORRUPT")
    for key, row in state["votes"].items():
        _record(row, "candidate field account verdict issue sequence epoch")
        if row["candidate"] not in state["candidates"] or row["field"] not in FIELDS or row["verdict"] not in ("PASS", "CONTRADICT"): raise EvidenceError("LEDGER_CORRUPT")
        for name in ("account", "issue", "sequence"): _natural(row[name], True)
        _natural(row["epoch"])
        if key != row["candidate"] + ":" + row["field"] + ":" + str(row["account"]): raise EvidenceError("LEDGER_CORRUPT")
    for rows in (state["receipts"], state["sessions"], state["issues"], state["votes"]):
        if any(row["sequence"] > state["sequence"] for row in rows.values()): raise EvidenceError("LEDGER_CORRUPT")


def check_caps(state):
    if (len(state["receipts"]) > MAX_RECEIPTS or len(state["sessions"]) > MAX_SESSIONS or
            len(state["votes"]) > MAX_VOTES or len(state["issues"]) > MAX_RECEIPTS or
            len(state["reports"]) > MAX_RECEIPTS or len(canonical(state)) > MAX_LEDGER_BYTES):
        # Never prune replay tombstones and accidentally accept old evidence as new.
        raise EvidenceError("LEDGER_CAPACITY_WAIT")


def _healthy(session):
    health = session["health"]
    return (session["complete"] and health["silent_reason"] == "NONE" and health["preexisting_connections"] == 0
            and not any(health[name] for name in ("adapter_dropped", "decoded_messages_dropped", "damaged_directions", "observation_overflow")))


def scope_for(item):
    target = item["target"]
    return {key: target[key] for key in ("region", "game_build", "template_profile_id", "template_sha256", "code_sha256")} | {
        "rules_version": item["producer"]["rules_version"]}


def _recompute(state, candidate_id):
    candidate = state["candidates"][candidate_id]
    seq = state["sequence"]
    for field in FIELDS:
        votes = [v for v in state["votes"].values() if v["candidate"] == candidate_id and v["field"] == field]
        floor = candidate["consumed"].get(field, 0)
        negative = [v for v in votes if v["verdict"] == "CONTRADICT" and v["sequence"] > floor]
        positive = [v for v in votes if v["verdict"] == "PASS"]
        if field in candidate["triggers"]:
            fresh = [v for v in positive if v["epoch"] == candidate["epoch"] and v["sequence"] > candidate["triggers"][field]]
            if len(fresh) >= 3 and len(fresh) > len(negative):
                del candidate["triggers"][field]
                candidate["consumed"][field] = seq
        elif len(negative) >= 3 and len(negative) > len(positive):
            if not candidate["quarantined"]:
                if candidate["epoch"] >= 2147483647: raise EvidenceError("EPOCH_CAPACITY_WAIT")
                candidate["epoch"] += 1
                candidate["quarantined"] = True
            candidate["triggers"][field] = seq
    candidate["quarantined"] = bool(candidate["triggers"])


def receive(state, raw, *, account_id, account_created_at, issue_number, now):
    """Atomically mutate caller's ledger only after every check; returns fixed disposition and artifact.

    An accepted issue is immutable for voting. A newer issue number can replace a position; an old
    issue edit cannot supersede it. Session aliases are single-use across all scopes for an account.
    """
    item = parse(raw)
    if type(account_id) is not int or account_id <= 0 or type(issue_number) is not int or issue_number <= 0:
        raise EvidenceError("IDENTITY_INVALID")
    try: created = dt.datetime.fromisoformat(account_created_at.replace("Z", "+00:00"))
    except (ValueError, AttributeError) as error: raise EvidenceError("ACCOUNT_METADATA_INVALID") from error
    if created.tzinfo is None or now.tzinfo is None or now - created < dt.timedelta(days=30):
        raise EvidenceError("ACCOUNT_TOO_NEW")
    sha = digest(item)
    evidence_id = item["evidence_id"]
    previous = state["receipts"].get(evidence_id)
    if previous:
        if previous["hash"] != sha or previous["account"] != account_id: raise EvidenceError("EVIDENCE_ID_REUSED")
        return {"status": "duplicate", "reason": "ALREADY_RECEIVED", "hash": sha, "original": previous.get("result")}
    issue_previous = state["issues"].get(str(issue_number))
    if issue_previous and issue_previous["voted"]: raise EvidenceError("NEW_ISSUE_REQUIRED")
    if sum(row["account"] == account_id for row in state["receipts"].values()) >= MAX_ACCOUNT_RECEIPTS:
        raise EvidenceError("ACCOUNT_CAPACITY_WAIT")
    updated = copy.deepcopy(state)
    updated["sequence"] += 1
    seq = updated["sequence"]
    issue_key = str(issue_number)
    updated["receipts"][evidence_id] = {"hash": sha, "account": account_id, "issue": issue_number, "sequence": seq}
    updated["issues"].setdefault(issue_key, {"account": account_id, "sequence": seq, "voted": False})
    scope = scope_for(item)
    candidate_id = digest(scope)
    accepted = {}
    existing_aliases = {key.split(":", 1)[1]: key for key in updated["sessions"]}
    for session in item["sessions"]:
        key = str(account_id) + ":" + session["session_alias"]
        if existing_aliases.get(session["session_alias"], key) != key:
            # Copying somebody else's public JSON under another account and evidence UUID is not a
            # distinct observed session. Random aliases carry no persistent device identifier.
            continue
        fresh = key not in updated["sessions"]
        if key in updated["sessions"]:
            if updated["sessions"][key]["hash"] != digest(session): raise EvidenceError("SESSION_ALIAS_REUSED")
            if updated["sessions"][key]["candidate"] != candidate_id: continue
        else:
            updated["sessions"][key] = {"hash": digest(session), "sequence": seq, "candidate": candidate_id,
                                         "epoch": item["target"]["quarantine_epoch"]}
        session_record = updated["sessions"][key]
        if _healthy(session):
            for check in session["checks"]:
                if check["verdict"] != "WAIT":
                    accepted.setdefault((check["field"], check["verdict"]), []).append((session_record, fresh))
    if sum(key.startswith(str(account_id) + ":") for key in updated["sessions"]) > MAX_ACCOUNT_SESSIONS:
        raise EvidenceError("ACCOUNT_CAPACITY_WAIT")
    result = {"status": "waiting", "reason": "TWO_NEW_HEALTHY_COMPLETE_SESSIONS_REQUIRED", "hash": sha}
    if item["kind"] == "CODE_VERIFICATION":
        updated["candidates"].setdefault(candidate_id, {"scope": scope, "epoch": 0, "quarantined": False, "triggers": {}, "consumed": {}})
        votes = 0
        if issue_previous is None or issue_previous["account"] == account_id:
            for field in FIELDS:
                key = candidate_id + ":" + field + ":" + str(account_id)
                previous_vote = updated["votes"].get(key)
                if previous_vote and previous_vote["issue"] >= issue_number: continue
                candidate = updated["candidates"][candidate_id]
                consumed = max(previous_vote["sequence"] if previous_vote else 0, candidate["consumed"].get(field, 0))
                eligible = {v: [(record, fresh) for record, fresh in records if record["sequence"] > consumed]
                            for (f, v), records in accepted.items() if f == field}
                eligible = {v: records for v, records in eligible.items() if records}
                options = [v for v, records in eligible.items() if len(records) >= 2 and any(fresh for _, fresh in records)]
                # Consumed historical sessions remain in the producer's bounded bundle. Only new,
                # unconsumed conclusions can conflict; a mixed fresh bundle cannot select a majority.
                if len(options) != 1 or len(eligible) != 1: continue
                records = [record for record, _ in eligible[options[0]]]
                # A recovery position must be based on two genuinely newly received sessions from
                # the same epoch. Relabelling a pre-quarantine alias cannot refresh its provenance.
                qualifying = [r for r in records if r["epoch"] == candidate["epoch"]
                              and r["sequence"] > candidate["triggers"].get(field, 0)]
                vote_epoch = candidate["epoch"] if len(qualifying) >= 2 else 0
                updated["votes"][key] = {"candidate": candidate_id, "field": field, "account": account_id,
                                         "verdict": options[0], "issue": issue_number, "sequence": seq,
                                         "epoch": vote_epoch}
                votes += 1
                updated["issues"][issue_key]["voted"] = True
        _recompute(updated, candidate_id)
        if votes: result.update(status="accepted", reason="LATEST_ACCOUNT_POSITION_RECORDED")
        result["candidate"] = candidate_id
    else:
        # The artifact key deliberately ignores evidence_id and session aliases: metadata resubmitted
        # under another random id creates no extra regional template or inferred contributor count.
        observation_id = digest({"scope": scope, "observations": item["observations"]})
        result.update(status="candidate", reason="INCOMPLETE_PROTOCOL_SEMANTICS", artifact=observation_id)
    updated["receipts"][evidence_id]["result"] = dict(result)
    check_caps(updated)
    state.clear(); state.update(updated)
    return result


def candidate_artifact(item):
    return {"status": "CANDIDATE", "region": item["target"]["region"], "game_build": item["target"]["game_build"],
            "observations": item["observations"], "readiness": "BLOCKED",
            "blockers": ["FIELD_OFFSETS_UNKNOWN", "ROULETTE_IDENTITY_UNPROVEN", "ZONE_SEMANTICS_UNPROVEN",
                         "COMPLETION_SEMANTICS_UNPROVEN", "REGIONAL_GAMEPLAY_NOT_VERIFIED"],
            "delivery": "FORMAL_TEMPLATE_REQUIRES_SOFTWARE_RELEASE"}


def apply_hints(entries, state):
    """Preserve all existing index fields; never revoke codes or modify submitter accounts."""
    output = []
    for source in entries:
        entry = dict(source)
        states = [c for c in state["candidates"].values() if all(c["scope"][key] == entry[key]
                  for key in ("region", "game_build", "code_sha256"))]
        if states:
            entry["quarantined"] = any(c["quarantined"] for c in states)
            entry["quarantine_epoch"] = max(c["epoch"] for c in states)
        output.append(entry)
    return tuple(output)
