#!/usr/bin/env python3
"""The public repository's ``index.json`` and ``submissions.json``: read, check, update, revoke.

``read_index`` is a port of ``SharedCalibrationIndex.Read`` (src/Collector/Protocol/Sharing/
SharedCalibrationIndex.cs) and ``select`` / ``revoked_codes`` of its ``Select`` / ``Revoked``, so what
this module writes is exactly what the released client reads (proven by the C# regression test over
``tests/Fixtures/shared-calibration/index-sample.json``, which this module generated).

Index (downloaded by every client): ``{"schema_version": 1, "entries": [...]}``, at most
``MAX_ENTRIES`` entries and ``MAX_INDEX_BYTES`` bytes, each entry the nine fields the client requires
plus, when they apply, the optional ``conflicting`` and ``shape_prior`` marks of ``update_marks``
(plan section 20.3). The index names no account.

Ledger (``submissions.json``, never downloaded by the client): one row per account per code, so the
Action can enforce "one code per GitHub account per region and build" and count distinct submitters.
It identifies an account by its immutable numeric GitHub id (``id:<n>``), so renaming an account does
not buy a second code. The same id is already public on the issue the account opened.

Submission rules (``add_submission``):

* the build has the shape of a real client build (``is_game_build``), not merely one the client reads,
  and is dated no later than the day after the submission (``BUILD_DATE_SLACK``, for the build's own
  time zone): a build from the future is no build any client runs;
* the account is at least ``MIN_ACCOUNT_AGE`` old;
* a revoked code is never published again, by anybody;
* one *live* code per account per (region, build): the same code again changes nothing, and a
  different code **replaces** the account's earlier one (rollback plan section 2). Replacing takes
  the account off the old code: its last submitter revokes it, another only lowers ``submitters``.
  A code revoked that way never holds anybody, because its last submitter's row is superseded;
* a code a maintainer revoked (``revoke``) holds every account still on it: none of them submits
  anything more for that region and build until a maintainer has reviewed it (``lift_hold``). The
  hold is the account's ledger row still naming the revoked code, so it needs no field of its own;
* one account submits at most ``MAX_CODES_PER_ACCOUNT_BUILD`` different codes for one region and
  build, and for at most ``MAX_BUILDS_PER_ACCOUNT_DAY`` regions and builds per UTC day, so no single
  account can fill the index every client downloads;
* no slot limit across accounts: a new code from a new account is a new entry; the same code from
  another account adds that account to ``submitters``;
* a code whose 12-digit file name is taken by another code in use is refused; a name held only by
  revoked codes is free again, so a revoked squatter cannot block an honest code for ever;
* nothing is written that would take the index past the client's caps.

Entries leave the index only through ``prune``, which a maintainer runs to take out whole builds
that no client runs any more, named one by one: a build's date is the submitter's to choose, so no
order of dates can tell a real build from an invented one dated today.

The index format does not change: a code taken out of use is ``revoked: true``, which a client that
predates replacement already reads correctly. Only the ledger gains an optional ``replaced``, the
codes this account submitted for this build before, newest last, at most ``MAX_REPLACED`` of them and
never including the code the row is on now, and an optional ``basis: "verified"`` for an account that
did not calibrate the code itself but verified another player's code on its own machine (plan section
20.2; absent means calibrated). Either way the account counts once towards ``submitters``.

Whatever ``dump`` writes, ``parse`` has to read back: ``check_consistent`` therefore enforces exactly
the rules ``_read_ledger`` enforces. A row the reader would refuse must never reach the file, because
every run of the Action begins by loading it, so one such row would stop every submission until a
maintainer edited the file by hand.

Everything here is pure except ``load`` / ``write_files``.
"""

from __future__ import annotations

import datetime as _dt
import json
import re
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable, Mapping

import sharecode

SCHEMA_VERSION = 1
MAX_ENTRIES = 512
MAX_INDEX_BYTES = 64 * 1024
MAX_CANDIDATES = 8
# Caps on the ledger. They are ours, not the client's: nothing downloads submissions.json. A chain is
# an audit trail, so only the most recent codes are worth keeping; the byte cap is the backstop that
# stops accounts trading codes back and forth from growing the file without end.
MAX_REPLACED = 16
MAX_LEDGER_BYTES = 4 * 1024 * 1024
# Per-account submission limits, also ours. Entries are never deleted by a submission and the client
# refuses the whole index past its caps, so without them one account could fill MAX_INDEX_BYTES in an
# afternoon and stop every later submission. An honest player shares one code per patch and corrects it
# once or twice. The per-build limit stays below MAX_REPLACED, so a chain never has to drop a code.
MAX_CODES_PER_ACCOUNT_BUILD = 3
MAX_BUILDS_PER_ACCOUNT_DAY = 2
CODE_EXTENSION = ".mrc"
MIN_ACCOUNT_AGE = _dt.timedelta(days=30)
# How far past the submission's UTC date a build's own date may lie: a build is dated in its own time zone.
BUILD_DATE_SLACK = _dt.timedelta(days=1)
INDEX_FILE = "index.json"
LEDGER_FILE = "submissions.json"

FIELDS = (
    "region", "game_build", "code_sha256", "match_source", "submitters", "first_published_at", "path", "commit", "revoked",
)
# Optional entry fields, written after FIELDS and only when an entry carries them: an index written
# before the field existed stays byte for byte what it was, and a client that predates it ignores it.
OPTIONAL_FIELDS = ("conflicting", "shape_prior", "quarantined", "quarantine_epoch")
CONFLICTING = "conflicting"
SHAPE_PRIOR = "shape_prior"
LEDGER_FIELDS = ("region", "game_build", "account", "code_sha256", "submitted_at", "issue")
# The ledger's own optional fields, written after LEDGER_FIELDS and only when a row carries them, so
# a ledger written before the field existed rewrites byte for byte. The client downloads no ledger.
LEDGER_OPTIONAL_FIELDS = ("replaced", "basis")
REPLACED = "replaced"
BASIS = "basis"
# What the share form says the submitter did. Only "verified" is ever written: no field means calibrated.
BASIS_CALIBRATED = "calibrated"
BASIS_VERIFIED = "verified"

# The fields two codes of one (region, build, template) can disagree on (plan section 20.3), in the order
# every caller lists them. zone, territory and job compare across match sources; match only within one.
DISPUTE_FIELDS = ("zone", "territory", "job", "match")

PUBLISHED = "published"
ADDED = "added"
DUPLICATE = "duplicate"
REFUSED = "refused"

# Refusals that are the submitter's to fix, and refusals that only a maintainer can resolve.
CODE_INVALID = "CODE_INVALID"
PAYLOAD_MISMATCH = "PAYLOAD_MISMATCH"
BUILD_NOT_INDEXABLE = "BUILD_NOT_INDEXABLE"
BUILD_IN_FUTURE = "BUILD_IN_FUTURE"
ACCOUNT_TOO_NEW = "ACCOUNT_TOO_NEW"
ACCOUNT_HELD = "ACCOUNT_HELD"
ACCOUNT_BUILD_LIMIT = "ACCOUNT_BUILD_LIMIT"
ACCOUNT_DAILY_LIMIT = "ACCOUNT_DAILY_LIMIT"
REVOKED = "REVOKED"
PATH_COLLISION = "PATH_COLLISION"
INDEX_FULL_ENTRIES = "INDEX_FULL_ENTRIES"
INDEX_FULL_BYTES = "INDEX_FULL_BYTES"
LEDGER_FULL_BYTES = "LEDGER_FULL_BYTES"
MAINTAINER_REFUSALS = frozenset({PATH_COLLISION, INDEX_FULL_ENTRIES, INDEX_FULL_BYTES, LEDGER_FULL_BYTES})

_REGION_DIRECTORIES = {"CN": "cn", "GLOBAL": "global"}
_BUILD = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")
# What every real client build looks like (ffxivgame.ver, e.g. 2026.09.01.0000.0000): a date and two
# four-digit counters. The client reads any _BUILD, and so does every reader here; only add_submission
# demands this shape, so an index written before it existed still reads.
_GAME_BUILD = re.compile(r"([0-9]{4})\.([0-9]{2})\.([0-9]{2})\.[0-9]{4}\.[0-9]{4}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_COMMIT = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")
_STAMP = re.compile(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?Z")
_ACCOUNT = re.compile(r"id:[1-9][0-9]{0,19}|login:[a-z0-9-]{1,39}")
_INT32_MAX = 2 ** 31 - 1
_PLACEHOLDER_COMMIT = "0" * 40
_UTF8_BOM = b"\xef\xbb\xbf"


class IndexCorrupt(ValueError):
    """``index.json`` or ``submissions.json`` is not something this module would have written."""


class IndexFull(ValueError):
    """Writing the index would pass one of the client's caps."""


@dataclass(frozen=True)
class ReadResult:
    """What ``read_index`` produced: SharedIndexReadResult."""

    entries: tuple
    skipped: tuple
    refusal: str | None

    @property
    def readable(self) -> bool:
        return self.refusal is None


@dataclass(frozen=True)
class Index:
    """The repository's published state: index entries and ledger rows, both treated as immutable."""

    entries: tuple = ()
    submissions: tuple = ()


@dataclass(frozen=True)
class SubmissionOutcome:
    status: str
    reason: str | None = None
    detail: str | None = None
    index: Index | None = None
    entry: Mapping | None = None
    code_sha256: str | None = None
    payload: Mapping | None = None
    new_code_path: str | None = None
    # The codes this submission took the account off, and the ones that left with no submitter and
    # were therefore revoked. At most one of each, because a ledger holds one row per account and build.
    replaced: tuple = ()
    replaced_revoked: tuple = ()


# --------------------------------------------------------------------------- formats


def region_directory(region: str) -> str:
    if region not in _REGION_DIRECTORIES:
        raise ValueError("only CN and GLOBAL have shared calibrations")
    return _REGION_DIRECTORIES[region]


def is_build(text: Any) -> bool:
    return isinstance(text, str) and _BUILD.fullmatch(text) is not None


def build_date(text: Any) -> _dt.date | None:
    """The calendar date a real client build carries, or None when ``text`` does not have that shape."""
    match = _GAME_BUILD.fullmatch(text) if isinstance(text, str) else None
    if match is None:
        return None
    try:
        return _dt.date(*(int(part) for part in match.groups()))
    except ValueError:
        return None


def is_game_build(text: Any) -> bool:
    """True for the shape of a real client build on a real calendar date; every such build ``is_build``."""
    return build_date(text) is not None


def is_sha256(text: Any) -> bool:
    return isinstance(text, str) and _SHA256.fullmatch(text) is not None


def is_commit(text: Any) -> bool:
    return isinstance(text, str) and _COMMIT.fullmatch(text) is not None


def code_path(region: str, build: str, code_sha256: str) -> str:
    """SharedCalibrationIndex.CodePath: ``<cn|global>/<build>/<first 12 hex digits>.mrc``."""
    directory = region_directory(region)
    if not is_build(build):
        raise ValueError("not a client build")
    if not is_sha256(code_sha256):
        raise ValueError("not a lowercase hex SHA-256")
    return directory + "/" + build + "/" + code_sha256[:12] + CODE_EXTENSION


def parse_stamp(text: Any) -> _dt.datetime | None:
    """A UTC stamp as the client accepts it (``...Z``, up to 7 fraction digits, a real calendar time)."""
    match = _STAMP.fullmatch(text) if isinstance(text, str) else None
    if match is None:
        return None
    year, month, day, hour, minute, second, fraction = match.groups()
    micro = int((fraction or "0").ljust(7, "0")[:6])
    try:
        return _dt.datetime(int(year), int(month), int(day), int(hour), int(minute), int(second), micro,
                            tzinfo=_dt.timezone.utc)
    except ValueError:
        return None


def format_stamp(moment: _dt.datetime) -> str:
    return _utc(moment, "moment").strftime("%Y-%m-%dT%H:%M:%SZ")


def _utc(moment: Any, name: str) -> _dt.datetime:
    if not isinstance(moment, _dt.datetime) or moment.tzinfo is None:
        raise ValueError(name + " must be a timezone-aware datetime")
    return moment.astimezone(_dt.timezone.utc)


def account_key(account_login: str | None, account_id: int | None) -> str:
    """How the ledger names an account: its numeric id when known, else its lowercased login."""
    if account_id is not None:
        if type(account_id) is not int or account_id <= 0:
            raise ValueError("account_id must be a positive integer")
        return "id:%d" % account_id
    if not isinstance(account_login, str) or re.fullmatch(r"[A-Za-z0-9-]{1,39}", account_login) is None:
        raise ValueError("account_login is not a GitHub login")
    return "login:" + account_login.lower()


# --------------------------------------------------------------------------- reading (client port)


def read_index(data: bytes) -> ReadResult:
    """SharedCalibrationIndex.Read: never raises; refuses the whole index or skips single entries."""
    if data.startswith(_UTF8_BOM):
        data = data[len(_UTF8_BOM):]
    parsed, root = sharecode.strict_json(data)
    if not parsed:
        return ReadResult((), (), "NOT_JSON")
    if not isinstance(root, dict):
        return ReadResult((), (), "NOT_AN_OBJECT")
    if getattr(root, "duplicate_key", None) is not None:
        return ReadResult((), (), "DUPLICATE_KEY")
    version = root.get("schema_version")
    if type(version) is not int or version != SCHEMA_VERSION:
        return ReadResult((), (), "SCHEMA_VERSION")
    entries = root.get("entries")
    if not isinstance(entries, list):
        return ReadResult((), (), "NO_ENTRIES")
    if len(entries) > MAX_ENTRIES:
        return ReadResult((), (), "TOO_MANY_ENTRIES")
    read, skipped = [], []
    for position, item in enumerate(entries):
        entry, reason = read_entry(item)
        if reason is None:
            read.append(entry)
        else:
            skipped.append((position, reason))
    return ReadResult(tuple(read), tuple(skipped), None)


def read_entry(item: Any) -> tuple[dict | None, str | None]:
    """SharedCalibrationIndex.ReadEntry: the entry's nine fields, or why it is skipped."""
    if not isinstance(item, dict):
        return None, "NOT_AN_OBJECT"
    if getattr(item, "duplicate_key", None) is not None:
        return None, "DUPLICATE_KEY"
    for name in FIELDS:
        if name not in item:
            return None, "MISSING:" + name
    region = item["region"]
    if not isinstance(region, str) or region not in _REGION_DIRECTORIES:
        return None, "INVALID:region"
    build = item["game_build"]
    if not is_build(build):
        return None, "INVALID:game_build"
    sha = item["code_sha256"]
    if not is_sha256(sha):
        return None, "INVALID:code_sha256"
    if item["match_source"] not in sharecode.MATCH_SOURCES:
        return None, "INVALID:match_source"
    submitters = item["submitters"]
    if type(submitters) is not int or not 1 <= submitters <= _INT32_MAX:
        return None, "INVALID:submitters"
    if parse_stamp(item["first_published_at"]) is None:
        return None, "INVALID:first_published_at"
    if item["path"] != code_path(region, build, sha):
        return None, "INVALID:path"
    if not is_commit(item["commit"]):
        return None, "INVALID:commit"
    if type(item["revoked"]) is not bool:
        return None, "INVALID:revoked"
    # Old clients ignore the additional quarantine hints; updated readers require actual JSON booleans
    # and a nonnegative Int32 epoch. Missing hints retain the legacy unquarantined behavior.
    for name in OPTIONAL_FIELDS:
        if name == "quarantine_epoch":
            if name in item and (type(item[name]) is not int or not 0 <= item[name] <= _INT32_MAX):
                return None, "INVALID:" + name
        elif name in item and type(item[name]) is not bool:
            return None, "INVALID:" + name
    entry = {name: item[name] for name in FIELDS}
    entry.update((name, item[name]) for name in OPTIONAL_FIELDS if name in item)
    return entry, None


def is_conflicting(entry: Mapping) -> bool:
    """SharedIndexEntry.Conflicting: absent or false means no conflict."""
    return bool(entry.get(CONFLICTING))


def pick_key(entry: Mapping) -> tuple:
    """The client's pick order (SharedCalibrationIndex.Select, plan section 20.3).

    More submitters first; then unmarked before conflicting; then ``shape_prior == true`` before false or
    absent; then published earlier; then hash. Both marks only break a tie in submitters (audit 2026-10-03,
    ON1-1): one more account publishing a differing code must not push a code many players submitted behind
    codes from one or two accounts, and the shape of an earlier build is a prior, never evidence.
    """
    return (-entry["submitters"], is_conflicting(entry), entry.get(SHAPE_PRIOR) is not True,
            parse_stamp(entry["first_published_at"]), entry["code_sha256"])


def select(entries: Iterable[Mapping], region: str, build: str) -> tuple:
    """SharedCalibrationIndex.Select: what a client downloads for its region and build, in ``pick_key`` order."""
    for_build = [entry for entry in entries if entry["region"] == region and entry["game_build"] == build]
    revoked = {entry["code_sha256"] for entry in for_build if entry["revoked"]}
    ordered = sorted((entry for entry in for_build if entry["code_sha256"] not in revoked), key=pick_key)
    chosen, seen = [], set()
    for entry in ordered:
        if entry["code_sha256"] not in seen:
            seen.add(entry["code_sha256"])
            chosen.append(entry)
    return tuple(chosen[:MAX_CANDIDATES])


def revoked_codes(entries: Iterable[Mapping], region: str, build: str) -> tuple:
    """SharedCalibrationIndex.Revoked: revoked codes for a region and build, sorted, each once."""
    return tuple(sorted({
        entry["code_sha256"] for entry in entries
        if entry["region"] == region and entry["game_build"] == build and entry["revoked"]
    }))


# --------------------------------------------------------------------------- repository state


def empty_index() -> Index:
    return Index((), ())


def sort_entries(entries: Iterable[Mapping]) -> tuple:
    """Stable file order: region, build, first publication, hash. Revoking never moves an entry."""
    return tuple(sorted(
        entries,
        key=lambda entry: (entry["region"], entry["game_build"], entry["first_published_at"], entry["code_sha256"]),
    ))


def serialize_index(entries: Iterable[Mapping]) -> bytes:
    """The one byte form of an index: compact, one entry per line, fields in the client's order."""
    return _serialize("entries", [_entry_fields(entry) for entry in sort_entries(entries)])


def _entry_fields(entry: Mapping) -> dict:
    """The entry as one index line: the client's nine fields in order, then any optional field it carries."""
    written = {name: entry[name] for name in FIELDS}
    written.update((name, entry[name]) for name in OPTIONAL_FIELDS if name in entry)
    return written


def serialize_ledger(submissions: Iterable[Mapping]) -> bytes:
    rows = sorted(submissions, key=lambda row: (row["region"], row["game_build"], row["submitted_at"], row["account"]))
    return _serialize("submissions", [_ledger_fields(row) for row in rows])


def _ledger_fields(row: Mapping) -> dict:
    """The row as one ledger line: the six fields in order, then any optional field it carries."""
    written = {name: row[name] for name in LEDGER_FIELDS}
    written.update((name, row[name]) for name in LEDGER_OPTIONAL_FIELDS if name in row)
    return written


def _serialize(name: str, rows: list) -> bytes:
    head = '{"schema_version":%d,"%s":[' % (SCHEMA_VERSION, name)
    if not rows:
        return (head + "]}\n").encode("utf-8")
    lines = [json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in rows]
    return (head + "\n" + ",\n".join(lines) + "\n]}\n").encode("utf-8")


def check_caps(index: Index) -> None:
    """Raises IndexFull when the client would refuse or truncate this index, or the ledger is too big."""
    if len(index.entries) > MAX_ENTRIES:
        raise IndexFull(INDEX_FULL_ENTRIES)
    if len(serialize_index(index.entries)) > MAX_INDEX_BYTES:
        raise IndexFull(INDEX_FULL_BYTES)
    if len(serialize_ledger(index.submissions)) > MAX_LEDGER_BYTES:
        raise IndexFull(LEDGER_FULL_BYTES)


def dump(index: Index) -> dict:
    """``{file name: bytes}`` for both files; raises IndexFull past a cap, IndexCorrupt if inconsistent."""
    check_consistent(index)
    check_caps(index)
    return {INDEX_FILE: serialize_index(index.entries), LEDGER_FILE: serialize_ledger(index.submissions)}


def parse(index_bytes: bytes, ledger_bytes: bytes) -> Index:
    """Both files, strictly: any entry the client would skip, or any inconsistency, is IndexCorrupt."""
    result = read_index(index_bytes)
    if not result.readable:
        raise IndexCorrupt("index.json is refused whole: " + result.refusal)
    if result.skipped:
        position, reason = result.skipped[0]
        raise IndexCorrupt("index.json entry %d is skipped by the client: %s" % (position, reason))
    index = Index(result.entries, _read_ledger(ledger_bytes))
    check_consistent(index)
    return index


def check_consistent(index: Index) -> None:
    shas = [entry["code_sha256"] for entry in index.entries]
    if len(shas) != len(set(shas)):
        raise IndexCorrupt("index.json lists a code twice")
    # A revoked code keeps its entry, so a file name it held may be taken again; the client downloads every
    # code at its own commit and skips revoked ones, so only two codes in use may never share a name.
    paths = [entry["path"] for entry in index.entries if not entry["revoked"]]
    if len(paths) != len(set(paths)):
        raise IndexCorrupt("index.json lists one code path for two codes in use")
    by_sha = {entry["code_sha256"]: entry for entry in index.entries}
    seen = set()
    for row in index.submissions:
        entry = by_sha.get(row["code_sha256"])
        if entry is None or (entry["region"], entry["game_build"]) != (row["region"], row["game_build"]):
            raise IndexCorrupt("submissions.json names a code index.json does not list")
        key = (row["region"], row["game_build"], row["account"])
        if key in seen:
            raise IndexCorrupt("submissions.json has two rows for one account and build")
        seen.add(key)
        # Exactly what _read_ledger demands, so dump can never write a file parse would refuse.
        if REPLACED in row and not _is_replaced_chain(row[REPLACED], row["code_sha256"]):
            raise IndexCorrupt("submissions.json has a replaced chain the reader would refuse")
        if BASIS in row and row[BASIS] != BASIS_VERIFIED:
            raise IndexCorrupt("submissions.json has a basis the reader would refuse")
        for superseded in row.get(REPLACED, ()):
            was = by_sha.get(superseded)
            if was is None or (was["region"], was["game_build"]) != (row["region"], row["game_build"]):
                raise IndexCorrupt("submissions.json replaces a code index.json does not list")
    for entry in index.entries:
        count = sum(1 for row in index.submissions if row["code_sha256"] == entry["code_sha256"])
        if count and count != entry["submitters"]:
            raise IndexCorrupt("submitters of %s does not match submissions.json" % entry["code_sha256"][:12])


def _read_ledger(data: bytes) -> tuple:
    parsed, root = sharecode.strict_json(data)
    if not parsed or not isinstance(root, dict) or getattr(root, "duplicate_key", None) is not None:
        raise IndexCorrupt("submissions.json is not a JSON object")
    if type(root.get("schema_version")) is not int or root["schema_version"] != SCHEMA_VERSION:
        raise IndexCorrupt("submissions.json has another schema_version")
    rows = root.get("submissions")
    if not isinstance(rows, list):
        raise IndexCorrupt("submissions.json has no submissions list")
    read = []
    for position, row in enumerate(rows):
        known = set(LEDGER_FIELDS) <= set(row) <= set(LEDGER_FIELDS) | set(LEDGER_OPTIONAL_FIELDS) if isinstance(row, dict) else False
        if not isinstance(row, dict) or getattr(row, "duplicate_key", None) is not None or not known:
            raise IndexCorrupt("submissions.json row %d is malformed" % position)
        valid = (
            isinstance(row["region"], str) and row["region"] in _REGION_DIRECTORIES
            and is_build(row["game_build"]) and is_sha256(row["code_sha256"])
            and isinstance(row["account"], str) and _ACCOUNT.fullmatch(row["account"]) is not None
            and parse_stamp(row["submitted_at"]) is not None
            and (row["issue"] is None or (type(row["issue"]) is int and row["issue"] > 0))
            and (REPLACED not in row or _is_replaced_chain(row[REPLACED], row["code_sha256"]))
            and (BASIS not in row or row[BASIS] == BASIS_VERIFIED)
        )
        if not valid:
            raise IndexCorrupt("submissions.json row %d is malformed" % position)
        read.append(_ledger_fields(row))
    return tuple(read)


def _is_replaced_chain(chain: Any, code_sha256: str) -> bool:
    """A short, non-empty list of distinct code hashes, none of them the row's own code."""
    return (
        isinstance(chain, list) and 1 <= len(chain) <= MAX_REPLACED and all(is_sha256(item) for item in chain)
        and len(set(chain)) == len(chain) and code_sha256 not in chain
    )


def load(root: Path) -> Index:
    """Reads ``index.json`` and ``submissions.json`` under a repository root."""
    try:
        index_bytes = (Path(root) / INDEX_FILE).read_bytes()
        ledger_bytes = (Path(root) / LEDGER_FILE).read_bytes()
    except OSError as error:
        raise IndexCorrupt("cannot read the index files: " + type(error).__name__) from error
    return parse(index_bytes, ledger_bytes)


def write_files(root: Path, index: Index) -> None:
    for name, data in dump(index).items():
        (Path(root) / name).write_bytes(data)


# --------------------------------------------------------------------------- updates


def add_submission(
    index: Index,
    region: str,
    build: str,
    code: str,
    account_login: str | None,
    account_created_at: _dt.datetime,
    now: _dt.datetime,
    commit_sha_for_new_code: str | None,
    *,
    account_id: int | None = None,
    issue: int | None = None,
    basis: str | None = None,
) -> SubmissionOutcome:
    """Applies one submission to the repository state.

    ``commit_sha_for_new_code`` is the commit that added the code file. Pass None to only decide:
    the outcome then carries no index for a new code (nothing to write yet) but every rule, the
    caps included, has been applied with a placeholder commit of the same length.

    ``basis`` is what the share form says (plan section 20.2): None or ``BASIS_CALIBRATED`` for a code
    the account calibrated, ``BASIS_VERIFIED`` for another player's code its own client verified. The
    rules are the same either way; only a verified row carries ``basis``, a replacement row included.
    """
    now = _utc(now, "now")
    created = _utc(account_created_at, "account_created_at")
    account = account_key(account_login, account_id)
    if commit_sha_for_new_code is not None and not is_commit(commit_sha_for_new_code):
        raise ValueError("commit_sha_for_new_code is not a full commit id")
    if basis not in (None, BASIS_CALIBRATED, BASIS_VERIFIED):
        raise ValueError("basis must be None, calibrated or verified")

    decoded = sharecode.decode(code)
    if not decoded.valid:
        return SubmissionOutcome(REFUSED, CODE_INVALID, decoded.rejection.code)
    payload, sha = decoded.payload, decoded.code_sha256
    if (payload["region"], payload["game_build"]) != (region, build):
        return SubmissionOutcome(REFUSED, PAYLOAD_MISMATCH, code_sha256=sha, payload=payload)
    if not is_game_build(build):
        return SubmissionOutcome(REFUSED, BUILD_NOT_INDEXABLE, code_sha256=sha, payload=payload)
    if build_date(build) > now.date() + BUILD_DATE_SLACK:
        return SubmissionOutcome(REFUSED, BUILD_IN_FUTURE, code_sha256=sha, payload=payload)
    if now - created < MIN_ACCOUNT_AGE:
        return SubmissionOutcome(REFUSED, ACCOUNT_TOO_NEW, code_sha256=sha, payload=payload)

    existing = next((entry for entry in index.entries if entry["code_sha256"] == sha), None)
    if existing is not None and existing["revoked"]:
        return SubmissionOutcome(REFUSED, REVOKED, code_sha256=sha, payload=payload)
    mine = next((row for row in index.submissions
                 if (row["region"], row["game_build"], row["account"]) == (region, build, account)), None)
    if mine is not None and mine["code_sha256"] == sha:
        return SubmissionOutcome(DUPLICATE, index=index, entry=existing, code_sha256=sha, payload=payload)
    limit = _account_limit(index, mine, (region, build, account), sha, now)
    if limit is not None:
        return SubmissionOutcome(REFUSED, limit, code_sha256=sha, payload=payload)

    row = {"region": region, "game_build": build, "account": account, "code_sha256": sha,
           "submitted_at": format_stamp(now), "issue": issue}
    if basis == BASIS_VERIFIED:
        row[BASIS] = BASIS_VERIFIED
    entries, submissions = index.entries, index.submissions
    replaced, replaced_revoked = (), ()
    if mine is not None:
        replaced = (mine["code_sha256"],)
        entries, replaced_revoked = _release(entries, mine["code_sha256"])
        submissions = tuple(item for item in submissions if item is not mine)
        row[REPLACED] = list(_replaced_chain(mine, sha))

    if existing is not None:
        current = next(entry for entry in entries if entry["code_sha256"] == sha)
        entry = dict(current, submitters=current["submitters"] + 1)
        entries = tuple(entry if item["code_sha256"] == sha else item for item in entries)
        status, path = ADDED, None
    else:
        path = code_path(region, build, sha)
        if any(item["path"] == path and not item["revoked"] for item in entries):
            return SubmissionOutcome(REFUSED, PATH_COLLISION, code_sha256=sha, payload=payload)
        entry = {
            "region": region, "game_build": build, "code_sha256": sha, "match_source": payload["match_source"],
            "submitters": 1, "first_published_at": format_stamp(now), "path": path,
            "commit": commit_sha_for_new_code or _PLACEHOLDER_COMMIT, "revoked": False,
        }
        entries, status = sort_entries(entries + (entry,)), PUBLISHED

    updated = Index(entries, submissions + (row,))
    refusal = _cap_refusal(updated)
    if refusal is not None:
        return SubmissionOutcome(REFUSED, refusal, code_sha256=sha, payload=payload)
    # A new code with no commit yet: every rule has been applied, but there is nothing to write.
    written = None if status == PUBLISHED and commit_sha_for_new_code is None else updated
    return SubmissionOutcome(status, index=written, entry=entry, code_sha256=sha, payload=payload,
                             new_code_path=path, replaced=replaced, replaced_revoked=replaced_revoked)


def _release(entries: tuple, code_sha256: str) -> tuple:
    """``(entries, revoked)`` with one account taken off a code.

    The last submitter leaving revokes the code, so it is never published again; with other
    submitters left only the count drops. ``submitters`` never falls below one, which is what the
    client reads: a code nobody submits any more is revoked, not counted down to zero.
    """
    # check_consistent guarantees every ledger row's code is listed, and an entry is only ever deleted
    # together with every row of its build (prune), so the lookup cannot miss; a default would only
    # turn a broken invariant into a silent no-op.
    current = next(entry for entry in entries if entry["code_sha256"] == code_sha256)
    if current["submitters"] > 1:
        freed, revoked = dict(current, submitters=current["submitters"] - 1), ()
    else:
        freed, revoked = dict(current, revoked=True), (code_sha256,)
    return tuple(freed if entry["code_sha256"] == code_sha256 else entry for entry in entries), revoked


def _replaced_chain(superseded: Mapping, current: str) -> tuple:
    """Every code this account submitted for this build before, newest last, each named once.

    ``current`` is the code the new row is on, and it is never in its own chain. An account can come
    back to a code it left, because leaving a code another account still submits does not revoke it:
    X and Z both take A, X moves to B, X comes back to A. Naming A in A's own row would pass
    ``dump`` and then be refused by ``_read_ledger`` on the next run, which stops every submission
    until a maintainer edits the file. The code being left now still joins the chain, so coming back
    keeps the history instead of erasing it. Only the most recent ``MAX_REPLACED`` are kept.
    """
    chain = list(superseded.get(REPLACED, ())) + [superseded["code_sha256"]]
    kept = [code for code in dict.fromkeys(chain) if code != current]
    return tuple(kept[-MAX_REPLACED:])


def _account_limit(index: Index, mine: Mapping | None, where: tuple, code_sha256: str, now: _dt.datetime) -> str | None:
    """Why this account may not submit ``code_sha256`` for this (region, build) now, or None when it may.

    ``where`` is ``(region, build, account)`` and ``mine`` the account's row for that region and build.
    A row whose code is revoked can only be one a maintainer revoked: a code revoked by a replacement
    loses its last row in the same step (``_release``). Both counts read only the ledger, so a limit
    holds for a code that would join another account's entry just as for a new one.
    """
    region, build, account = where
    if mine is not None and any(entry["revoked"] for entry in index.entries if entry["code_sha256"] == mine["code_sha256"]):
        return ACCOUNT_HELD
    codes = {code_sha256}
    if mine is not None:
        codes.update(mine.get(REPLACED, ()), (mine["code_sha256"],))
    if len(codes) > MAX_CODES_PER_ACCOUNT_BUILD:
        return ACCOUNT_BUILD_LIMIT
    today = now.date()
    elsewhere_today = sum(
        1 for row in index.submissions
        if row["account"] == account and (row["region"], row["game_build"]) != (region, build)
        and parse_stamp(row["submitted_at"]).date() == today
    )
    if elsewhere_today >= MAX_BUILDS_PER_ACCOUNT_DAY:
        return ACCOUNT_DAILY_LIMIT
    return None


def _cap_refusal(index: Index) -> str | None:
    try:
        check_caps(index)
    except IndexFull as full:
        return str(full)
    return None


def code_payload(root: Path, entry: Mapping) -> dict | None:
    """The decoded payload of the entry's own code file, or None when it cannot be read as that code.

    "Cannot be" covers a missing or unreadable file, bytes that are not this entry's code, and a code
    that no longer decodes: such an entry takes part in no comparison rather than stopping the run.
    """
    try:
        text = (Path(root) / entry["path"]).read_bytes().decode("utf-8", "replace")
    except OSError:
        return None
    decoded = sharecode.decode(text)
    if not decoded.valid or decoded.code_sha256 != entry["code_sha256"]:
        return None
    return decoded.payload


def code_descriptor(root: Path, entry: Mapping) -> tuple | None:
    """``(template_sha256, match_source)`` of the entry's own code file, or None when ``code_payload`` is None."""
    payload = code_payload(root, entry)
    return None if payload is None else (payload["template_sha256"], payload["match_source"])


def code_fields(payload: Mapping) -> dict:
    """The fields two codes can disagree on (plan section 20.3), the model SharedCalibrationSession compares.

    ``zone``, ``territory`` and ``job`` are the opcodes, None when the code leaves one out; ``match`` is
    ``(match_source, the canonical pop object)`` and is never None. ``selector_values`` keep their order.
    """
    return {
        "zone": payload["zone_opcode"],
        "territory": payload.get("territory_opcode"),
        "job": payload.get("job_opcode"),
        "match": (payload["match_source"], sharecode.canonical(payload["pop"])),
    }


def _disagree(name: str, mine: Mapping, theirs: Mapping) -> bool:
    """True when two codes' field ``name`` disputes: both set and different, ``match`` only within one source."""
    left, right = mine[name], theirs[name]
    if left is None or right is None:
        return False
    if name == "match" and left[0] != right[0]:
        return False
    return left != right


def code_shape(payload: Mapping) -> tuple | None:
    """What a match source's pop looks like, for ``shape_prior``; None for QUEUE_REQUEST, which has no shape.

    ANNOUNCEMENT: ``(length,)``; MARKER_OFFSET: ``(length, roulette_offset)``; REPLY_STATE: the selector values.
    """
    pop, source = payload["pop"], payload["match_source"]
    if source == "ANNOUNCEMENT":
        return (pop["length"],)
    if source == "MARKER_OFFSET":
        return (pop["length"], pop["roulette_offset"])
    if source == "REPLY_STATE":
        return (tuple(pop["selector_values"]),)
    return None


def _readable(index: Index, root: Path, wanted: Any, log: Any) -> list:
    """``[(entry, payload), ...]`` for every non-revoked entry ``wanted`` accepts whose code file reads back.

    An entry whose file ``code_payload`` cannot read is left out and, when ``log`` is given, named to it.
    """
    read = []
    for entry in index.entries:
        if entry["revoked"] or not wanted(entry):
            continue
        payload = code_payload(root, entry)
        if payload is None:
            if log is not None:
                log("no readable code file for %s at %s" % (entry["code_sha256"][:12], entry["path"]))
            continue
        read.append((entry, payload))
    return read


def _in_build(region: str, build: str) -> Any:
    return lambda entry: (entry["region"], entry["game_build"]) == (region, build)


def _disputes_among(codes: list) -> dict:
    """``{code_sha256: {field: [other code_sha256, ...]}}`` among ``codes``, which share a region and a build.

    Codes compare only within one template. Fields follow DISPUTE_FIELDS, rivals are sorted, and a code
    that disputes nothing has no key at all.
    """
    found = {}
    for entry, payload in codes:
        mine, rivals = code_fields(payload), {}
        for other, other_payload in codes:
            if other["code_sha256"] == entry["code_sha256"] or other_payload["template_sha256"] != payload["template_sha256"]:
                continue
            theirs = code_fields(other_payload)
            for name in DISPUTE_FIELDS:
                if _disagree(name, mine, theirs):
                    rivals.setdefault(name, set()).add(other["code_sha256"])
        if rivals:
            found[entry["code_sha256"]] = {name: sorted(rivals[name]) for name in DISPUTE_FIELDS if name in rivals}
    return found


def disputes(index: Index, root: Path, region: str, build: str, *, log: Any = None) -> dict:
    """Which published codes of one (region, build) disagree with which, field by field (plan section 20.4).

    ``{code_sha256: {"zone" | "territory" | "job" | "match": [other code_sha256, ...]}}`` for the non-revoked
    entries whose code files read back. Two codes compare only when they share ``template_sha256``; zone,
    territory and job compare across match sources, ``match`` (match source and pop) only within one source;
    a field a code leaves out (None) never votes and never disputes. Only publish.py's replies use it: it is
    never written to the index, whose ``conflicting`` is this function's keys.
    """
    return _disputes_among(_readable(index, root, _in_build(region, build), log))


def _build_order(build: str) -> tuple | None:
    """``(date, build)`` for a build with a calendar date, else None: only dated builds have an order."""
    date = build_date(build)
    return None if date is None else (date, build)


def _accounts_by_code(index: Index) -> dict:
    """``{code_sha256: {account, ...}}``: every account whose ledger row is on the code or names it in ``replaced``."""
    accounts: dict = {}
    for row in index.submissions:
        for code in [row["code_sha256"], *row.get(REPLACED, ())]:
            accounts.setdefault(code, set()).add(row["account"])
    return accounts


def _stake(index: Index, region: str, build: str) -> set:
    """Every account with a ledger row for (region, build): whoever submitted any code of it, revoked ones included."""
    return {row["account"] for row in index.submissions if (row["region"], row["game_build"]) == (region, build)}


def _shape_priors(index: Index, root: Path, region: str, build: str, codes: list, log: Any) -> dict:
    """``{code_sha256: bool}`` for the codes of ``codes`` that have a shape and a comparable earlier build.

    For each code, the comparable codes are this region's non-revoked, readable codes of the same template
    and match source whose build is dated strictly earlier than ``build`` (both must have a date), and that
    no account with a stake in ``build`` submitted (security review 2026-10-15): a build's date is the
    submitter's to choose, so one account could otherwise publish its shape for an invented build dated
    just before this one and give its own rival here the prior, taking it from the honest code. Leaving out
    every account of this build, not only the code's own, also keeps that prior from coming back when the
    rival is revoked or replaced, and from being added by a late code for the real earlier build. Only the
    latest build that remains counts, ordered by date and then by build text; the code is True when one of
    that build's codes has its shape, False when none has, and absent when nothing comparable remains.
    """
    this = build_date(build)
    if this is None:
        return {}
    stake, accounts = _stake(index, region, build), _accounts_by_code(index)

    def earlier(entry: Mapping) -> bool:
        date = build_date(entry["game_build"])
        return (entry["region"] == region and date is not None and date < this
                and not accounts.get(entry["code_sha256"], set()) & stake)

    previous = _readable(index, root, earlier, log)
    priors = {}
    for entry, payload in codes:
        shape = code_shape(payload)
        if shape is None:
            continue
        same_kind = [(_build_order(other["game_build"]), code_shape(other_payload)) for other, other_payload in previous
                     if other_payload["template_sha256"] == payload["template_sha256"]
                     and other_payload["match_source"] == payload["match_source"]]
        if not same_kind:
            continue
        latest = max(order for order, _ in same_kind)
        priors[entry["code_sha256"]] = any(order == latest and other == shape for order, other in same_kind)
    return priors


def update_marks(index: Index, root: Path, region: str, build: str, *, log: Any = None) -> Index:
    """Recomputes ``conflicting`` and ``shape_prior`` for one (region, build) from the code files (plan section 20.3).

    ``conflicting`` is True for a code that ``disputes`` puts at odds with another published, non-revoked
    code of the same (region, build, template) on any field, and absent otherwise. ``shape_prior`` is True
    when the code's shape (``code_shape``) is one of the latest earlier build's codes of the same template and
    match source, False when that build has such codes but none of that shape, and absent when there is no
    such build or the code has no shape. Revoked entries neither count nor carry a mark, and neither does an
    entry whose code file cannot be read (named to ``log`` when given). Pure apart from reading the code
    files, and idempotent: the marks depend only on the entries and their files, never on marks already there.
    """
    codes = _readable(index, root, _in_build(region, build), log)
    conflicting = set(_disputes_among(codes))
    priors = _shape_priors(index, root, region, build, codes, log)
    in_build = _in_build(region, build)
    return replace(index, entries=tuple(
        _marked(entry, entry["code_sha256"] in conflicting, priors.get(entry["code_sha256"])) if in_build(entry) else entry
        for entry in index.entries
    ))


# Kept so callers written against plan section 18.6 still work: the marks are a superset of the old flag.
update_conflicts = update_marks


def refresh_marks(index: Index, root: Path, region: str, build: str, *, log: Any = None) -> Index:
    """``update_marks`` for (region, build) and for every later-dated build of the region.

    A later build's ``shape_prior`` reads the latest earlier build, so publishing or revoking a code in
    ``build`` can change it. What the Action writes after a publication or a revocation, and what the
    sample generator writes after each step.
    """
    marked = index
    for each in marked_builds(index, region, build):
        marked = update_marks(marked, root, region, each, log=log)
    return marked


def marked_builds(index: Index, region: str, build: str) -> tuple:
    """The builds ``refresh_marks`` recomputes: ``build`` first, then the region's later-dated builds the index lists."""
    this = build_date(build)
    if this is None:
        return (build,)

    def later(entry: Mapping) -> bool:
        date = build_date(entry["game_build"])
        return entry["region"] == region and date is not None and date > this

    return (build, *sorted({entry["game_build"] for entry in index.entries if later(entry)}))


def _marked(entry: Mapping, conflicting: bool, shape_prior: bool | None) -> Mapping:
    """The entry with exactly these marks: ``conflicting`` only when True, ``shape_prior`` only when known."""
    marked = {name: value for name, value in entry.items() if name not in ("conflicting", "shape_prior")}
    if conflicting:
        marked[CONFLICTING] = True
    if shape_prior is not None:
        marked[SHAPE_PRIOR] = shape_prior
    return entry if marked == dict(entry) else marked


def revoke(index: Index, code_sha256: str) -> Index:
    """Marks every entry of a code revoked; idempotent; KeyError when the index does not list it.

    The ledger rows on the code stay as they are, and that is what holds their accounts for this
    region and build (``add_submission``) until a maintainer lifts the hold.
    """
    if not any(entry["code_sha256"] == code_sha256 for entry in index.entries):
        raise KeyError(code_sha256)
    return replace(index, entries=tuple(
        dict(entry, revoked=True) if entry["code_sha256"] == code_sha256 else entry for entry in index.entries
    ))


def lift_hold(index: Index, region: str, build: str, account: str) -> Index:
    """Lets an account a maintainer's revocation holds submit for this region and build again, after review.

    The hold is the account's row still naming the revoked code, so lifting it removes that row and takes
    the account off the code's ``submitters``, which never falls below one (the client refuses less). The
    code itself stays revoked. KeyError when the account has no row there, or its code is not revoked.
    """
    row = next((row for row in index.submissions
                if (row["region"], row["game_build"], row["account"]) == (region, build, account)), None)
    if row is None or not any(entry["revoked"] for entry in index.entries if entry["code_sha256"] == row["code_sha256"]):
        raise KeyError(account)
    entries = tuple(
        dict(entry, submitters=entry["submitters"] - 1)
        if entry["code_sha256"] == row["code_sha256"] and entry["submitters"] > 1 else entry
        for entry in index.entries
    )
    return Index(entries, tuple(item for item in index.submissions if item is not row))


def prune(index: Index, region: str, builds: Iterable[str]) -> tuple:
    """``(index, removed)``: exactly the named ``builds`` of ``region`` taken out; ``removed`` lists them, sorted.

    The maintainer names every build; nothing is chosen by date. A build's date is whatever its submitter
    wrote, and submission accepts one dated today, so "keep the newest" would let an invented build dated
    today push the real one out. A removed build takes its entries and its ledger rows with it, and no
    chain that stays names one of its codes, so the result passes ``check_consistent`` and reads back.
    ValueError for an unknown region or no build at all; KeyError, with nothing removed, for a build
    neither file lists for that region, so a mistyped build cannot pass for one already gone.
    """
    region_directory(region)
    named = {builds} if isinstance(builds, str) else set(builds)
    if not named:
        raise ValueError("name at least one build to remove")
    listed = {item["game_build"] for item in index.entries + index.submissions if item["region"] == region}
    unknown = sorted(named - listed, key=str)
    if unknown:
        raise KeyError(unknown[0])
    removed = {(region, build) for build in named}
    gone = {item["code_sha256"] for item in index.entries if (item["region"], item["game_build"]) in removed}
    entries = tuple(item for item in index.entries if (item["region"], item["game_build"]) not in removed)
    submissions = tuple(_without_codes(row, gone) for row in index.submissions
                        if (row["region"], row["game_build"]) not in removed)
    return Index(entries, submissions), tuple(sorted(removed))


def _without_codes(row: Mapping, codes: set) -> Mapping:
    """The row with every code of ``codes`` taken out of its chain, and the chain dropped once it is empty."""
    if not set(row.get(REPLACED, ())) & codes:
        return row
    chain = [code for code in row[REPLACED] if code not in codes]
    trimmed = {name: value for name, value in row.items() if name != REPLACED}
    return dict(trimmed, replaced=chain) if chain else trimmed
