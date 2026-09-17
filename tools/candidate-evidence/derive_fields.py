#!/usr/bin/env python3
"""Offline analysis of candidate evidence. Standard library only; never promotes a profile."""

from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import sys
import uuid

# Mirrors src/Collector/Capture/ResearchPayloadPolicy.MaxPayloadBytes and migrations/0007.
MAX_PAYLOAD_BYTES = 512


class EvidenceError(ValueError):
    """Invalid evidence or an unsupported explicit field selection."""


@dataclass(frozen=True)
class Observation:
    observation_id: str
    profile_id: str
    hypothesis_name: str
    opcode: int | None
    direction: str
    length: int | None
    session: str
    connection: str
    t_ms: int
    verdict: str | None
    payload: bytes | None


@dataclass(frozen=True)
class Evidence:
    observations: tuple[Observation, ...]
    duplicate_count: int
    source_sha256: str


def _integer(value, low, high, label):
    if type(value) is not int or not low <= value <= high:
        raise EvidenceError(f"{label} must be an integer in {low}..{high}")
    return value


def _string(value, pattern, label):
    if not isinstance(value, str) or re.fullmatch(pattern, value) is None:
        raise EvidenceError(f"invalid {label}")
    return value


def _uuid(value, label):
    if not isinstance(value, str):
        raise EvidenceError(f"invalid {label}")
    try:
        if str(uuid.UUID(value)) != value.lower():
            raise ValueError()
    except ValueError as error:
        raise EvidenceError(f"invalid {label}") from error
    return value.lower()


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError("duplicate JSON object key")
        result[key] = value
    return result


def _invalid_constant(_value):
    raise EvidenceError("non-finite JSON numbers are not allowed")


def _local_path(value):
    text = str(value)
    if text.startswith(("\\\\", "//")) or "://" in text:
        raise EvidenceError("only local filesystem paths are accepted")
    return Path(value)


def load_evidence(path) -> Evidence:
    """Validate a schema-1 export before analysis; no raw bytes appear in errors."""
    raw = _local_path(path).read_bytes()
    try:
        document = json.loads(raw, object_pairs_hook=_object, parse_constant=_invalid_constant)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise EvidenceError("input is not valid UTF JSON") from error
    if not isinstance(document, dict) or document.get("format") != "MentorRecorder.CandidateEvidence":
        raise EvidenceError("expected MentorRecorder.CandidateEvidence")
    _integer(document.get("schema_version"), 1, 1, "schema_version")
    if document.get("profile_status") != "CANDIDATE":
        raise EvidenceError("profile_status must remain CANDIDATE")
    if type(document.get("contains_raw_payload")) is not bool:
        raise EvidenceError("contains_raw_payload must be a boolean")
    declared = document.get("research_payload_opcodes")
    if not isinstance(declared, list):
        raise EvidenceError("research_payload_opcodes must be an array")
    declared = [_string(item, r"0x[0-9a-f]{4}", "research_payload_opcodes entry") for item in declared]
    if len(set(declared)) != len(declared):
        raise EvidenceError("research_payload_opcodes contains duplicates")
    rows, reviews = document.get("observations"), document.get("reviews")
    if not isinstance(rows, list) or not isinstance(reviews, list):
        raise EvidenceError("observations and reviews must be arrays")
    _integer(document.get("observation_count"), len(rows), len(rows), "observation_count")
    _integer(document.get("review_count"), len(reviews), len(reviews), "review_count")
    if any(not isinstance(review, dict) for review in reviews):
        raise EvidenceError("each review must be an object")
    unique, actual_opcodes, duplicates = {}, set(), 0
    for index, row in enumerate(rows):
        label = f"observations[{index}]"
        if not isinstance(row, dict):
            raise EvidenceError(f"{label} must be an object")
        observation_id = _uuid(row.get("observation_id"), label + ".observation_id")
        profile = _string(row.get("profile_id"), r"[a-z0-9][a-z0-9.-]{0,63}", label + ".profile_id")
        name = _string(row.get("hypothesis_name"), r"[A-Z][A-Z0-9_]{0,63}", label + ".hypothesis_name")
        session = _uuid(row.get("capture_session_id"), label + ".capture_session_id")
        connection = _string(row.get("connection_tag"), r"[0-9a-f]{12}", label + ".connection_tag")
        t_ms = _integer(row.get("t_ms"), 0, 2**63 - 1, label + ".t_ms")
        verdict = row.get("review_verdict")
        if verdict is not None and verdict not in ("CORRECT", "WRONG", "UNSURE"):
            raise EvidenceError(f"invalid {label}.review_verdict")
        direction, opcode, length = row.get("direction"), row.get("opcode"), row.get("payload_length")
        hex_payload, payload_hash = row.get("payload_hex"), row.get("payload_hash12")
        payload = None
        if direction == "NONE":
            if name != "ZONE_LOAD" or any(value is not None for value in (opcode, length, hex_payload, payload_hash)):
                raise EvidenceError(f"{label}: inferred zone anchors cannot carry payload")
        else:
            if direction not in ("S2C", "C2S"):
                raise EvidenceError(f"invalid {label}.direction")
            opcode = _integer(opcode, 0, 65535, label + ".opcode")
            length = _integer(length, 0, 65535, label + ".payload_length")
            _string(payload_hash, r"[0-9a-f]{12}", label + ".payload_hash12")
            if hex_payload is not None:
                _string(hex_payload, r"[0-9a-f]*", label + ".payload_hex")
                if length > MAX_PAYLOAD_BYTES or len(hex_payload) != length * 2:
                    raise EvidenceError(f"{label}: payload length mismatch or above {MAX_PAYLOAD_BYTES} bytes")
                payload = bytes.fromhex(hex_payload)
                if hashlib.sha256(payload).hexdigest()[:12] != payload_hash:
                    raise EvidenceError(f"{label}: payload_hash12 mismatch")
                actual_opcodes.add(f"0x{opcode:04x}")
        observation = Observation(observation_id, profile, name, opcode, direction, length,
                                  session, connection, t_ms, verdict, payload)
        if observation_id in unique:
            # Even conflicting review summaries cannot silently change which evidence is CORRECT.
            if unique[observation_id][0] != row:
                raise EvidenceError(f"{label}: duplicate observation_id has inconsistent content")
            duplicates += 1
        else:
            unique[observation_id] = (row, observation)
    if document["contains_raw_payload"] != bool(actual_opcodes) or set(declared) != actual_opcodes:
        raise EvidenceError("payload header does not match the payloads actually present")
    return Evidence(tuple(item[1] for item in unique.values()), duplicates, hashlib.sha256(raw).hexdigest())


def _stability(samples, offset):
    values = {sample.payload[offset] for sample in samples}
    streams = defaultdict(list)
    for sample in samples:
        streams[(sample.session, sample.connection)].append(sample)
    comparisons, increasing = 0, True
    for stream in streams.values():
        stream.sort(key=lambda item: (item.t_ms, item.observation_id))
        for earlier, later in zip(stream, stream[1:]):
            comparisons += 1
            if later.t_ms <= earlier.t_ms or later.payload[offset] <= earlier.payload[offset]:
                increasing = False
    if len(samples) < 2:
        classification = "insufficient"
    elif len(values) == 1:
        classification = "constant"
    elif comparisons and increasing:
        classification = "incrementing"
    else:
        classification = "random"
    return {"offset": offset, "classification": classification, "distinct_values": len(values),
            "sample_count": len(samples), "temporal_comparisons": comparisons}


def _message(group, offset, field_type):
    return {"name": "CONTENT_FINDER_POP", "opcode": group["opcode"],
            "direction": {"S2C": "SERVER_TO_CLIENT", "C2S": "CLIENT_TO_SERVER"}[group["direction"]],
            "expected_length": group["payload_length"],
            "fields": [{"name": "roulette_id", "offset": offset, "type": field_type, "endian": "little"}]}


def analyze(evidence: Evidence, *, roulette_id=None, popup_hypothesis=None, field=None,
            profile_id=None, opcode=None, direction=None, payload_length=None):
    """Classify bytes and propose evidence-supported fields without assigning a mentor id."""
    if (roulette_id is None) != (popup_hypothesis is None):
        raise EvidenceError("--roulette-id and --popup-hypothesis must be supplied together")
    if roulette_id is not None:
        _integer(roulette_id, 1, 65535, "roulette_id candidate")
        _string(popup_hypothesis, r"[A-Z][A-Z0-9_]{0,63}", "popup hypothesis")
    elif any(value is not None for value in (field, profile_id, opcode, direction, payload_length)):
        raise EvidenceError("field/group selection requires explicit popup and roulette candidates")
    if profile_id is not None:
        _string(profile_id, r"[a-z0-9][a-z0-9.-]{0,63}", "profile selection")
    if opcode is not None:
        _integer(opcode, 0, 65535, "opcode selection")
    if payload_length is not None:
        _integer(payload_length, 0, MAX_PAYLOAD_BYTES, "payload length selection")
    if direction is not None and direction not in ("S2C", "C2S"):
        raise EvidenceError("direction selection must be S2C or C2S")
    if field is not None and (not isinstance(field, tuple) or len(field) != 2 or
                              type(field[0]) is not int or not 0 <= field[0] <= 255 or
                              field[1] not in ("u8", "u16", "u32")):
        raise EvidenceError("field selection must be offset:u8, offset:u16 or offset:u32")
    grouped = defaultdict(list)
    for observation in evidence.observations:
        if observation.direction != "NONE":
            grouped[(observation.profile_id, observation.hypothesis_name, observation.opcode,
                     observation.direction, observation.length)].append(observation)
    groups, candidates, max_correct = [], [], 0
    for key, rows in sorted(grouped.items()):
        group = dict(zip(("profile_id", "hypothesis_name", "opcode", "direction", "payload_length"), key))
        samples = [row for row in rows if row.payload is not None]
        group.update(observation_count=len(rows), payload_sample_count=len(samples),
                     skipped_without_payload=len(rows) - len(samples),
                     offsets=[_stability(samples, offset) for offset in range(key[4])] if samples else [])
        groups.append(group)
        if popup_hypothesis != key[1] or any(selected is not None and selected != actual for selected, actual in
                ((profile_id, key[0]), (opcode, key[2]), (direction, key[3]), (payload_length, key[4]))):
            continue
        correct = [row for row in samples if row.verdict == "CORRECT"]
        max_correct = max(max_correct, len(correct))
        if len(correct) < 2:
            continue
        for field_type, width in (("u8", 1), ("u16", 2), ("u32", 4)):
            if roulette_id >= 1 << (width * 8):
                continue
            for offset in range(key[4] - width + 1):
                support = [row for row in correct
                           if int.from_bytes(row.payload[offset:offset + width], "little") == roulette_id]
                if len(support) >= 2:
                    candidates.append({**{name: group[name] for name in key_names()},
                        "offset": offset, "type": field_type, "support_count": len(support),
                        "correct_sample_count": len(correct), "support_session_count": len({row.session for row in support}),
                        "support_connection_count": len({(row.session, row.connection) for row in support}),
                        "message": _message(group, offset, field_type)})
    selected = candidates if field is None else [item for item in candidates
                                               if (item["offset"], item["type"]) == field]
    if field is not None and not selected:
        raise EvidenceError("selected field lacks support from two distinct CORRECT popup observations")
    messages = []
    if roulette_id is None:
        status = "ANALYSIS_ONLY"
    elif not candidates:
        status = "INSUFFICIENT_EVIDENCE" if max_correct < 2 else "NO_MATCH"
    elif len(selected) != 1:
        status = "AMBIGUOUS"
    elif field is None and selected[0]["support_count"] != selected[0]["correct_sample_count"]:
        status = "PARTIAL_SUPPORT"
    else:
        status = "DRAFT_READY"
        messages = [selected[0]["message"]]
    return {"format": "MentorRecorder.CandidateFieldDraft", "schema_version": 1,
            "compatibility_status": "CANDIDATE", "mentor_roulette_id": None, "status": status,
            "source_sha256": evidence.source_sha256, "roulette_id_candidate": roulette_id,
            "roulette_id_source": "EXPLICIT_USER_INPUT" if roulette_id is not None else None,
            "popup_hypothesis": popup_hypothesis, "selected_field": list(field) if field is not None else None,
            "observation_count": len(evidence.observations), "duplicate_observation_count": evidence.duplicate_count,
            "skipped_without_payload": sum(row.payload is None for row in evidence.observations),
            "messages": messages, "message_candidates": [item["message"] for item in candidates],
            "field_candidates": [{name: value for name, value in item.items() if name != "message"}
                                 for item in candidates], "groups": groups}


def key_names():
    return ("profile_id", "hypothesis_name", "opcode", "direction", "payload_length")


def render_markdown(result):
    """Render analysis only: raw payloads are never copied into the report."""
    lines = ["# 字段偏移候选表", "", f"状态：`{result['status']}`；档案信任：`CANDIDATE`；`mentor_roulette_id = null`。",
             "", "草稿仅供离线复核，不代表协议已验证，不进入正式解析、记录或统计。",
             "`random` 只表示样本发生变化，不能证明随机性。`incrementing` 要求同会话、同连接内按严格递增的 t_ms 比较；",
             "不同连接/会话不拼接计数，时间相同、计数回绕或下降不会推断成递增。", "",
             f"输入 SHA-256：`{result['source_sha256']}`。",
             f"去重观测 {result['observation_count']}；重复 id {result['duplicate_observation_count']}；"
             f"无负载跳过 {result['skipped_without_payload']}。", "",
             f"roulette 候选：`{result['roulette_id_candidate']}`（仅明确用户输入）；"
             f"弹窗假设：`{result['popup_hypothesis']}`（由维护者明确指定）。", "",
             "## roulette 字段候选", "",
             "每个备选至少有两个不同 observation_id 的 CORRECT 弹窗样本。支持数分母仅包含该分组有负载的 CORRECT 样本。",
             "不同随机任务可能产生不同值；部分匹配只作为备选，不能自动填入 messages。",
             "偏移、宽度或分组有歧义时 messages 留空；使用 --field 和必要的分组筛选明确选择。", "",
             "| 备选序号 | 档案 / hypothesis | opcode / 方向 / 长度 | 偏移 | little 类型 | 支持 / CORRECT | 会话 / 连接 |",
             "|---|---|---|---:|---|---:|---:|"]
    for index, item in enumerate(result["field_candidates"]):
        lines.append(f"| {index} | {item['profile_id']} / {item['hypothesis_name']} | 0x{item['opcode']:04x} / "
                     f"{item['direction']} / {item['payload_length']} | {item['offset']} | {item['type']} | "
                     f"{item['support_count']} / {item['correct_sample_count']} | "
                     f"{item['support_session_count']} / {item['support_connection_count']} |")
    if not result["field_candidates"]:
        lines.append("| — | 没有满足条件的候选 | — | — | — | — | — |")
    lines += ["", "备选序号对应 JSON 的 message_candidates 与 field_candidates；每个消息片段均可独立复制。",
              "messages 只放唯一全支持或维护者明确选择的单个候选；JSON 是草稿容器，不是可加载的完整协议档案。", ""]
    for group in result["groups"]:
        lines += [f"## {group['profile_id']} / {group['hypothesis_name']} / 0x{group['opcode']:04x} / "
                  f"{group['direction']} / {group['payload_length']} 字节", "",
                  f"观测 {group['observation_count']}，有负载 {group['payload_sample_count']}，"
                  f"缺负载 {group['skipped_without_payload']}。", "",
                  "| byte offset | 分类 | 样本数 | 不同值数 | 同会话同连接时序比较数 |",
                  "|---:|---|---:|---:|---:|"]
        for offset in group["offsets"]:
            lines.append(f"| {offset['offset']} | {offset['classification']} | {offset['sample_count']} | "
                         f"{offset['distinct_values']} | {offset['temporal_comparisons']} |")
        lines.append("")
    return "\n".join(lines)


def write_outputs(result, output_dir):
    """Create two new local files; never overwrite outputs or write into shipped profiles."""
    directory = _local_path(output_dir).resolve()
    profiles = Path(__file__).resolve().parents[2] / "protocol-profiles"
    if directory == profiles or profiles in directory.parents:
        raise EvidenceError("output directory must be outside shipped protocol-profiles")
    paths = [directory / "field-candidates.md", directory / "candidate-messages.json"]
    if any(path.exists() for path in paths):
        raise EvidenceError("output already exists; choose a new output directory")
    directory.mkdir(parents=True, exist_ok=True)
    contents = [render_markdown(result), json.dumps(result, ensure_ascii=False, indent=2) + "\n"]
    created = []
    try:
        for path, content in zip(paths, contents):
            with path.open("x", encoding="utf-8", newline="\n") as output:
                created.append(path)
                output.write(content)
    except OSError:
        for path in created:
            path.unlink(missing_ok=True)
        raise
    return tuple(paths)


def _field_argument(value):
    match = re.fullmatch(r"([0-9]+):(u8|u16|u32)", value)
    if match is None or int(match[1]) > 255:
        raise argparse.ArgumentTypeError("expected offset:u8|u16|u32 with offset 0..255")
    return int(match[1]), match[2]


def _number_argument(value):
    try:
        return int(value, 16 if value.lower().startswith("0x") else 10)
    except ValueError as error:
        raise argparse.ArgumentTypeError("expected a decimal or 0x-prefixed integer") from error


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", help="local schema-1 CandidateEvidence JSON")
    parser.add_argument("--output-dir", help="new local directory; defaults to <evidence stem>.derived")
    parser.add_argument("--roulette-id", type=_number_argument, help="explicit candidate value, 1..65535")
    parser.add_argument("--popup-hypothesis", help="explicit hypothesis to treat as popup observations")
    parser.add_argument("--field", type=_field_argument, help="explicit offset:u8|u16|u32 selection")
    parser.add_argument("--profile-id", help="restrict field search to one profile")
    parser.add_argument("--opcode", type=_number_argument, help="restrict field search to one opcode")
    parser.add_argument("--direction", choices=("S2C", "C2S"), help="restrict field search direction")
    parser.add_argument("--payload-length", type=int, help="restrict field search to one exact length")
    args = parser.parse_args(argv)
    try:
        evidence_path = _local_path(args.evidence)
        result = analyze(load_evidence(evidence_path), roulette_id=args.roulette_id,
                         popup_hypothesis=args.popup_hypothesis, field=args.field, profile_id=args.profile_id,
                         opcode=args.opcode, direction=args.direction, payload_length=args.payload_length)
        output_dir = args.output_dir or evidence_path.with_name(evidence_path.stem + ".derived")
        write_outputs(result, output_dir)
    except (OSError, EvidenceError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(f"STATUS: {result['status']}; CANDIDATE; messages={len(result['messages'])}; "
          f"alternatives={len(result['message_candidates'])}; skipped_without_payload={result['skipped_without_payload']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
