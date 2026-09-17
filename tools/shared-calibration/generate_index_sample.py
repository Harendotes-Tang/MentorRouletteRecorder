#!/usr/bin/env python3
"""Generate the index sample the C# regression test reads.

    python tools/shared-calibration/generate_index_sample.py           write the files
    python tools/shared-calibration/generate_index_sample.py --check   exit 1 when they differ

The sample is a small repository state built only through ``index.add_submission`` - the function the
public repository's Action publishes with - and never written by hand:

  tests/Fixtures/shared-calibration/index-sample.json            the index, as index.dump writes it
  tests/Fixtures/shared-calibration/index-sample.expected.json   what index.select / revoked_codes pick per build
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
    ("CN", BUILD_A, 10, 1001, 24, repo_index.REFUSED),
    ("CN", BUILD_B, 0, 1000, 30, repo_index.PUBLISHED),
    ("GLOBAL", BUILD_A, 1, 1001, 31, repo_index.PUBLISHED),
)
REVOKED = (("CN", BUILD_A, 4), ("CN", BUILD_B, 0))


class SampleError(Exception):
    pass


def payload_for(template: rebuild.Template, region: str, build: str, number: int) -> dict:
    """A distinct, structurally valid payload per number, cycling through the four match sources."""
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
        if outcome.index is not None:
            state = outcome.index
        if outcome.status == repo_index.PUBLISHED:
            codes[outcome.new_code_path] = code
    for region, build, number in REVOKED:
        state = repo_index.revoke(state, sharecode.code_sha256(payload_for(template, region, build, number)))
    return state, codes


def _expected(state: repo_index.Index) -> dict:
    builds = []
    for region, build in sorted({(entry["region"], entry["game_build"]) for entry in state.entries}):
        picks = repo_index.select(state.entries, region, build)
        builds.append({
            "region": region,
            "game_build": build,
            "picks": [{name: entry[name] for name in ("code_sha256", "submitters", "commit", "path")} for entry in picks],
            "revoked": list(repo_index.revoked_codes(state.entries, region, build)),
        })
    return {
        "$comment": "Generated by tools/shared-calibration/generate_index_sample.py (index.select and "
                    "index.revoked_codes over index-sample.json). Do not edit; regenerate.",
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
