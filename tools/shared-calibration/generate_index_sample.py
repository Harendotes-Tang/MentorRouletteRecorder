#!/usr/bin/env python3
"""Generate the index sample the C# regression test reads.

    python tools/shared-calibration/generate_index_sample.py           write the files
    python tools/shared-calibration/generate_index_sample.py --check   exit 1 when they differ

The sample is a small repository state built only through ``index.add_submission`` and ``index.revoke``,
with the marks (``conflicting``, ``shape_prior``) ``index.refresh_marks`` recomputes after each of them -
the functions the public repository's Action publishes with - and never written by hand:

  tests/Fixtures/shared-calibration/index-sample.json            the index, as index.dump writes it
  tests/Fixtures/shared-calibration/index-sample.expected.json   what index.select / revoked_codes pick per build,
                                                                 with each pick's two marks
  tests/Fixtures/shared-calibration/index-sample-codes/<path>    every published code file

tests/Collector.UnitTests/SharedCalibrationPublicRepoSampleTests.cs feeds these to the released
SharedCalibrationIndex and SharedCalibrationClient and requires the same picks; test_index_sample.py
requires the committed files to be exactly what this generator writes.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import sys
import tempfile
from pathlib import Path

import index as repo_index
import rebuild
import sharecode

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
FIXTURES = REPO / "tests" / "Fixtures" / "shared-calibration"
TEMPLATE = REPO / "protocol-profiles" / "cn" / "cn.2026.08.05.json"
INDEX_NAME = "index-sample.json"
EXPECTED_NAME = "index-sample.expected.json"
CODES_DIRECTORY = "index-sample-codes"

BUILD_A = "2026.09.01.0000.0000"
BUILD_B = "2026.09.10.0000.0000"
START = dt.datetime(2026, 9, 1, 8, 0, 0, tzinfo=dt.timezone.utc)
ACCOUNT_CREATED = dt.datetime(2020, 1, 1, tzinfo=dt.timezone.utc)

# (region, build, code number, GitHub account id, hours after START, the outcome the rules must give)
SCENARIO = (
    *(("CN", BUILD_A, number, 1000 + number, number, repo_index.PUBLISHED) for number in range(10)),
    ("CN", BUILD_A, 2, 2001, 20, repo_index.ADDED),
    ("CN", BUILD_A, 2, 2002, 21, repo_index.ADDED),
    ("CN", BUILD_A, 6, 2003, 22, repo_index.ADDED),
    ("CN", BUILD_A, 8, 1008, 23, repo_index.DUPLICATE),
    # A second code from an account that already has one replaces it (rollback plan section 2). Account
    # 1001 was code 1's only submitter, so code 1 is revoked here and the client must skip it.
    ("CN", BUILD_A, 10, 1001, 24, repo_index.PUBLISHED),
    # Shared on the day build B came out: a build dated after the day after its submission is refused.
    # The steps run in this order, not by time; the ones after this keep their earlier hours.
    ("CN", BUILD_B, 0, 1000, 222, repo_index.PUBLISHED),
    ("GLOBAL", BUILD_A, 1, 1001, 31, repo_index.PUBLISHED),
    # Codes 5 and 1 share a match source and template and disagree on every field. Code 2 is the only
    # MARKER_OFFSET code, so the section-18.6 rule left it unmarked, but its zone differs from both, so
    # the field-level rule marks it as well. Code 5 then gains a second submitter: the client picks it
    # first, then code 1 and code 2 in the order they were published.
    ("GLOBAL", BUILD_A, 5, 3001, 32, repo_index.PUBLISHED),
    ("GLOBAL", BUILD_A, 5, 3002, 33, repo_index.ADDED),
    ("GLOBAL", BUILD_A, 2, 3003, 34, repo_index.PUBLISHED),
    # Listed last so that every earlier code keeps the commit it had before plan section 20.3 added these.
    # Build B's other codes (VARIANTS) agree on zone, territory and job, and build A is the build before it
    # (plan section 20.3). 21 and 22 are both MARKER_OFFSET with another pop, so both are conflicting; 22
    # has build A's MARKER_OFFSET shape (shape_prior true), 21 does not (false). 23 has build A's
    # ANNOUNCEMENT shape and no rival (true, unmarked); 20 is QUEUE_REQUEST, which has no shape (absent).
    # Code 0, revoked below, disputed every one of them on the zone until then. The client picks 23, then
    # 20 (unmarked beats the prior), then 22 before 21 (the prior beats the earlier publication).
    ("CN", BUILD_B, 20, 5020, 223, repo_index.PUBLISHED),
    ("CN", BUILD_B, 21, 5021, 224, repo_index.PUBLISHED),
    ("CN", BUILD_B, 22, 5022, 225, repo_index.PUBLISHED),
    ("CN", BUILD_B, 23, 5023, 226, repo_index.PUBLISHED),
)
REVOKED = (("CN", BUILD_A, 4), ("CN", BUILD_B, 0))

# Build B's codes after code 0: (match source, pop), all with VARIANT_FIELDS, so they differ only in the match.
VARIANT_FIELDS = {"zone_opcode": 1100, "territory_opcode": 3100, "job_opcode": 4100}
VARIANTS = {
    20: ("QUEUE_REQUEST", {"opcode": 2020}),
    21: ("MARKER_OFFSET", {"opcode": 2021, "length": 40, "roulette_offset": 8}),
    22: ("MARKER_OFFSET", {"opcode": 2022, "length": 24, "roulette_offset": 8}),
    23: ("ANNOUNCEMENT", {"opcode": 2023, "length": 40}),
}


class SampleError(Exception):
    pass


def payload_for(template: rebuild.Template, region: str, build: str, number: int) -> dict:
    """A distinct, structurally valid payload per number: VARIANTS as listed, the rest cycling the match sources.

    Outside VARIANTS every number has its own zone, territory and job, so two of them always disagree.
    """
    if number in VARIANTS:
        source, pop = VARIANTS[number]
        return {
            "v": 1, "region": region, "game_build": build, "template_profile_id": template.profile_id,
            "template_sha256": template.sha256, "match_source": source, "pop": dict(pop), **VARIANT_FIELDS,
        }
    source = sharecode.MATCH_SOURCES[number % len(sharecode.MATCH_SOURCES)]
    pop = {"opcode": 2000 + number}
    if source == "REPLY_STATE":
        pop["selector_values"] = [3]
    elif source == "ANNOUNCEMENT":
        pop["length"] = 40
    elif source == "MARKER_OFFSET":
        pop.update(length=24, roulette_offset=8)
    payload = {
        "v": 1, "region": region, "game_build": build, "template_profile_id": template.profile_id,
        "template_sha256": template.sha256, "match_source": source, "pop": pop,
        "zone_opcode": 1000 + number, "territory_opcode": 3000 + number,
    }
    if number % 3 != 2:
        payload["job_opcode"] = 4000 + number
    return payload


def _commit(number: int) -> str:
    return hashlib.sha1(("index-sample code commit %d" % number).encode("ascii")).hexdigest()


def build_state() -> tuple:
    template = rebuild.load_template(TEMPLATE.read_bytes(), TEMPLATE.name)
    state = repo_index.empty_index()
    codes = {}
    # The checkout refresh_marks reads the code files from, as publish.py's update-index and revoke do.
    with tempfile.TemporaryDirectory() as checkout:
        root = Path(checkout)
        for region, build, number, account, hours, expected in SCENARIO:
            payload = payload_for(template, region, build, number)
            if region == template.region and not rebuild.rebuild(payload, template).built:
                raise SampleError("sample code %d does not rebuild on %s" % (number, template.name))
            code = sharecode.encode(payload)
            outcome = repo_index.add_submission(
                state, region, build, code, None, ACCOUNT_CREATED, START + dt.timedelta(hours=hours),
                _commit(len(codes)), account_id=account, issue=100 + hours)
            if outcome.status != expected:
                raise SampleError("step %s/%s/%d by %d ended %s (%s)" % (region, build, number, account, outcome.status, outcome.reason))
            if outcome.status == repo_index.PUBLISHED:
                codes[outcome.new_code_path] = code
                target = root / outcome.new_code_path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(code.encode("ascii"))
            if outcome.index is not None:
                state = repo_index.refresh_marks(outcome.index, root, region, build, log=_missing_code_file)
        for region, build, number in REVOKED:
            revoked = repo_index.revoke(state, sharecode.code_sha256(payload_for(template, region, build, number)))
            state = repo_index.refresh_marks(revoked, root, region, build, log=_missing_code_file)
    return state, codes


def _missing_code_file(message: str) -> None:
    """refresh_marks skips a code it cannot read; in the sample every code file is written, so that is a bug."""
    raise SampleError(message)


def _pick(entry: dict) -> dict:
    """One pick as the expected file lists it: what the client reads, then the two marks (null when absent)."""
    pick = {name: entry[name] for name in ("code_sha256", "submitters", "commit", "path")}
    pick.update((name, entry.get(name)) for name in repo_index.OPTIONAL_FIELDS)
    return pick


def _expected(state: repo_index.Index) -> dict:
    builds = []
    for region, build in sorted({(entry["region"], entry["game_build"]) for entry in state.entries}):
        picks = repo_index.select(state.entries, region, build)
        builds.append({
            "region": region,
            "game_build": build,
            "picks": [_pick(entry) for entry in picks],
            "revoked": list(repo_index.revoked_codes(state.entries, region, build)),
        })
    return {
        "$comment": "Generated by tools/shared-calibration/generate_index_sample.py (index.select and "
                    "index.revoked_codes over index-sample.json; conflicting and shape_prior are null when "
                    "the entry does not carry them). Do not edit; regenerate.",
        "entries": len(state.entries),
        "builds": builds,
    }


def build_sample() -> dict:
    """``{path relative to tests/Fixtures/shared-calibration: bytes}``."""
    state, codes = build_state()
    files = {
        INDEX_NAME: repo_index.dump(state)[repo_index.INDEX_FILE],
        EXPECTED_NAME: (json.dumps(_expected(state), ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
    }
    for path, code in sorted(codes.items()):
        files[CODES_DIRECTORY + "/" + path] = code.encode("ascii")
    return files


def on_disk(directory: Path = FIXTURES) -> dict:
    files = {}
    for name in (INDEX_NAME, EXPECTED_NAME):
        if (directory / name).is_file():
            files[name] = (directory / name).read_bytes()
    codes = directory / CODES_DIRECTORY
    if codes.is_dir():
        for path in sorted(codes.rglob("*")):
            if path.is_file():
                files[path.relative_to(directory).as_posix()] = path.read_bytes()
    return files


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate the shared-calibration index sample fixtures.")
    parser.add_argument("--check", action="store_true", help="only compare with the committed files")
    args = parser.parse_args(argv)
    sample = build_sample()
    existing = on_disk()
    if args.check:
        if existing != sample:
            print("the index sample fixtures differ from what the generator writes", file=sys.stderr)
            return 1
        print("index sample fixtures are up to date (%d files)" % len(sample))
        return 0
    stale = sorted(set(existing) - set(sample))
    if stale:
        print("remove these stale files first: " + ", ".join(stale), file=sys.stderr)
        return 1
    for name, data in sample.items():
        target = FIXTURES / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    print("wrote %d files under %s" % (len(sample), FIXTURES))
    return 0


if __name__ == "__main__":
    sys.exit(main())
