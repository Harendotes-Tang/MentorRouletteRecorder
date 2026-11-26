"""Replay GLOBAL candidate evidence locally. Results are entry hypotheses, never live records."""

from __future__ import annotations

import argparse
from collections import defaultdict
from itertools import groupby
import json
from pathlib import Path

from derive_fields import EvidenceError, load_evidence

ROOT = Path(__file__).resolve().parents[2]
PROFILE_ID = "global.2026.11.03.candidate"
ENTRY_WINDOW_MS = 120_000  # StateMachineOptions.AnnouncedWindow, measured from the notice.
QUEUE = "QUEUE_REGISTRATION"
NOTICE = "QUEUE_SIDE_03BF"
ZONE = "TERRITORY_CANDIDATE_00AB"


def candidate_shapes():
    """The three hypotheses' shapes in the candidate profile as it is in this checkout."""
    path = ROOT / "protocol-profiles" / "global" / (PROFILE_ID + ".json")
    hypotheses = json.loads(path.read_text(encoding="utf-8"))["hypotheses"]
    directions = {"SERVER_TO_CLIENT": "S2C", "CLIENT_TO_SERVER": "C2S"}
    return {h["name"]: (directions[h["direction"]], h["opcode"], h["expected_length"])
            for h in hypotheses if h["name"] in (QUEUE, NOTICE, ZONE)}


def export_shapes(observations):
    """The three hypotheses' shapes as the export itself records them.

    The candidate profile is edited in place under the same profile id between evidence rounds, so
    an older export is read by its own rows, never by the profile in the checkout (audit 2026-11-26,
    N3-1). One hypothesis seen with two shapes in one export is refused rather than half used.
    """
    shapes = {}
    for row in observations:
        if row.hypothesis_name in (QUEUE, NOTICE, ZONE):
            shape = (row.direction, row.opcode, row.length)
            if shapes.setdefault(row.hypothesis_name, shape) != shape:
                raise EvidenceError(row.hypothesis_name + " has more than one packet shape in this export")
    return shapes


def shape_notes(observations):
    """One line per hypothesis whose exported shape differs from the profile in the checkout."""
    current = candidate_shapes()
    return [f"{name}: the export has {shape}, the current candidate profile has {current.get(name)}"
            for name, shape in sorted(export_shapes(observations).items()) if current.get(name) != shape]


def replay_stream(rows, territories):
    """A known duty confirms one notice in this connection. A queue alone never expires into a run."""
    queued = None
    notice_ms = None
    entries = []
    for timestamp, simultaneous in groupby(sorted(rows, key=lambda row: row.t_ms), key=lambda row: row.t_ms):
        evidence = {(row.hypothesis_name, row.payload) for row in simultaneous}
        if len(evidence) != 1:
            # An export has no wire order for contradictory observations at the same millisecond.
            queued = notice_ms = None
            continue
        kind, payload = evidence.pop()
        if kind == QUEUE:
            queued = payload[0] if payload else None
            notice_ms = None
        elif kind == NOTICE:
            # The two mentor captures support testing this value; its width is still a hypothesis.
            if queued == 9 and payload and int.from_bytes(payload[2:4], "little") == 9:
                notice_ms = timestamp
            else:
                queued = notice_ms = None
        elif notice_ms is not None:
            territory = int.from_bytes(payload[2:4], "little") if payload else None
            if territory in territories and 0 < timestamp - notice_ms <= ENTRY_WINDOW_MS:
                entries.append({"matched_ms": notice_ms, "entered_ms": timestamp, "territory_id": territory})
            # The first zone after a notice resolves it, even if the destination is unknown.
            queued = notice_ms = None
    return entries


def replay(evidence, territories):
    export_shapes(evidence.observations)
    streams = defaultdict(list)
    sessions = {}
    for row in evidence.observations:
        if row.profile_id != PROFILE_ID:
            raise EvidenceError("expected observations from " + PROFILE_ID)
        sessions.setdefault(row.session, [])
        if row.hypothesis_name in (QUEUE, NOTICE, ZONE):
            streams[row.session, row.connection].append(row)
    for (session, _), rows in streams.items():
        sessions[session].extend(replay_stream(rows, territories))
    for entries in sessions.values():
        entries.sort(key=lambda entry: entry["entered_ms"])
    return sessions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path, help="candidate-evidence JSON export")
    parser.add_argument("duty_catalog", type=Path, help="GLOBAL duty catalogue JSON")
    args = parser.parse_args()
    try:
        catalog = json.loads(args.duty_catalog.read_text(encoding="utf-8"))
        if catalog["region"] != "GLOBAL":
            raise EvidenceError("expected the GLOBAL duty catalogue")
        territories = {d["territory_id"] for d in catalog["duties"] if d["enabled"] and d["territory_id"]}
        evidence = load_evidence(args.export)
        sessions = replay(evidence, territories)
        notes = shape_notes(evidence.observations)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, f"Cannot replay evidence: {error}\n")
    print(json.dumps({"status": "ANALYSIS_ONLY", "source_sha256": evidence.source_sha256,
                      "sessions": sessions, "shape_notes": notes}, indent=2))


if __name__ == "__main__":
    main()
