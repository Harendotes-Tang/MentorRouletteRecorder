#!/usr/bin/env python3
"""The public repository's ``index.json`` and ``submissions.json``: read, check, update, revoke.

``read_index`` is a port of ``SharedCalibrationIndex.Read`` (src/Collector/Protocol/Sharing/
SharedCalibrationIndex.cs) and ``select`` / ``revoked_codes`` of its ``Select`` / ``Revoked``, so what
this module writes is exactly what the released client reads (proven by the C# regression test over
``tests/Fixtures/shared-calibration/index-sample.json``, which this module generated).

Index (downloaded by every client): ``{"schema_version": 1, "entries": [...]}``, at most
``MAX_ENTRIES`` entries and ``MAX_INDEX_BYTES`` bytes, each entry the nine fields the client requires
plus, when it applies, the optional ``conflicting`` flag of ``update_conflicts``. The index names no
account.

Ledger (``submissions.json``, never downloaded by the client): one row per account per code, so the
Action can enforce "one code per GitHub account per region and build" and count distinct submitters.
It identifies an account by its immutable numeric GitHub id (``id:<n>``), so renaming an account does
not buy a second code. The same id is already public on the issue the account opened.

Submission rules (``add_submission``):

* the account is at least ``MIN_ACCOUNT_AGE`` old;
* a revoked code is never published again, by anybody;
* one *live* code per account per (region, build): the same code again changes nothing, and a
  different code **replaces** the account's earlier one (rollback plan section 2). Replacing takes
  the account off the old code: its last submitter revokes it, another only lowers ``submitters``.
  A revoked code therefore frees the slot on its own, because its row is superseded like any other;
* no slot limit: a new code from a new account is a new entry; the same code from another account
  adds that account to ``submitters``;
* a code whose 12-digit file name is taken by another code is refused;
* nothing is written that would take the index past the client's caps.

The index format does not change: a code taken out of use is ``revoked: true``, which a client that
predates replacement already reads correctly. Only the ledger gains an optional ``replaced``, the
codes this account submitted for this build before, newest last.

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
CODE_EXTENSION = ".mrc"
MIN_ACCOUNT_AGE = _dt.timedelta(days=30)
INDEX_FILE = "index.json"
LEDGER_FILE = "submissions.json"

FIELDS = (
    "region", "game_build", "code_sha256", "match_source", "submitters", "first_published_at", "path", "commit", "revoked",
)
# Optional entry fields, written after FIELDS and only when an entry carries them: an index written
# before the field existed stays byte for byte what it was, and a client that predates it ignores it.
OPTIONAL_FIELDS = ("conflicting",)
CONFLICTING = "conflicting"
LEDGER_FIELDS = ("region", "game_build", "account", "code_sha256", "submitted_at", "issue")
# The ledger's own optional fields, written after LEDGER_FIELDS and only when a row carries them, so
# a ledger written before the field existed rewrites byte for byte. The client downloads no ledger.
LEDGER_OPTIONAL_FIELDS = ("replaced",)
REPLACED = "replaced"

PUBLISHED = "published"
ADDED = "added"
DUPLICATE = "duplicate"
REFUSED = "refused"

# Refusals that are the submitter's to fix, and refusals that only a maintainer can resolve.
CODE_INVALID = "CODE_INVALID"
PAYLOAD_MISMATCH = "PAYLOAD_MISMATCH"
BUILD_NOT_INDEXABLE = "BUILD_NOT_INDEXABLE"
ACCOUNT_TOO_NEW = "ACCOUNT_TOO_NEW"
REVOKED = "REVOKED"
PATH_COLLISION = "PATH_COLLISION"
INDEX_FULL_ENTRIES = "INDEX_FULL_ENTRIES"
INDEX_FULL_BYTES = "INDEX_FULL_BYTES"
MAINTAINER_REFUSALS = frozenset({PATH_COLLISION, INDEX_FULL_ENTRIES, INDEX_FULL_BYTES})

_REGION_DIRECTORIES = {"CN": "cn", "GLOBAL": "global"}
_BUILD = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")
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
    # Optional (plan section 18.6): an index written before the field existed reads as "no conflict".
    if CONFLICTING in item and type(item[CONFLICTING]) is not bool:
        return None, "INVALID:conflicting"
    entry = {name: item[name] for name in FIELDS}
    if CONFLICTING in item:
        entry[CONFLICTING] = item[CONFLICTING]
    return entry, None


def is_conflicting(entry: Mapping) -> bool:
    """SharedIndexEntry.Conflicting: absent or false means no conflict."""
    return bool(entry.get(CONFLICTING))


def pick_key(entry: Mapping) -> tuple:
    """The client's pick order: conflicting last, then more submitters, then published earlier, then hash."""
    return (is_conflicting(entry), -entry["submitters"], parse_stamp(entry["first_published_at"]), entry["code_sha256"])


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
    """Raises IndexFull when the client would refuse or truncate this index."""
    if len(index.entries) > MAX_ENTRIES:
        raise IndexFull(INDEX_FULL_ENTRIES)
    if len(serialize_index(index.entries)) > MAX_INDEX_BYTES:
        raise IndexFull(INDEX_FULL_BYTES)


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
    paths = [entry["path"] for entry in index.entries]
    if len(paths) != len(set(paths)):
        raise IndexCorrupt("index.json lists one code path twice")
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
        )
        if not valid:
            raise IndexCorrupt("submissions.json row %d is malformed" % position)
        read.append(_ledger_fields(row))
    return tuple(read)


def _is_replaced_chain(chain: Any, code_sha256: str) -> bool:
    """A non-empty list of distinct code hashes, none of them the row's own code."""
    return (
        isinstance(chain, list) and bool(chain) and all(is_sha256(item) for item in chain)
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
) -> SubmissionOutcome:
    """Applies one submission to the repository state.

    ``commit_sha_for_new_code`` is the commit that added the code file. Pass None to only decide:
    the outcome then carries no index for a new code (nothing to write yet) but every rule, the
    caps included, has been applied with a placeholder commit of the same length.
    """
    now = _utc(now, "now")
    created = _utc(account_created_at, "account_created_at")
    account = account_key(account_login, account_id)
    if commit_sha_for_new_code is not None and not is_commit(commit_sha_for_new_code):
        raise ValueError("commit_sha_for_new_code is not a full commit id")

    decoded = sharecode.decode(code)
    if not decoded.valid:
        return SubmissionOutcome(REFUSED, CODE_INVALID, decoded.rejection.code)
    payload, sha = decoded.payload, decoded.code_sha256
    if (payload["region"], payload["game_build"]) != (region, build):
        return SubmissionOutcome(REFUSED, PAYLOAD_MISMATCH, code_sha256=sha, payload=payload)
    if not is_build(build):
        return SubmissionOutcome(REFUSED, BUILD_NOT_INDEXABLE, code_sha256=sha, payload=payload)
    if now - created < MIN_ACCOUNT_AGE:
        return SubmissionOutcome(REFUSED, ACCOUNT_TOO_NEW, code_sha256=sha, payload=payload)

    existing = next((entry for entry in index.entries if entry["code_sha256"] == sha), None)
    if existing is not None and existing["revoked"]:
        return SubmissionOutcome(REFUSED, REVOKED, code_sha256=sha, payload=payload)
    mine = next((row for row in index.submissions
                 if (row["region"], row["game_build"], row["account"]) == (region, build, account)), None)
    if mine is not None and mine["code_sha256"] == sha:
        return SubmissionOutcome(DUPLICATE, index=index, entry=existing, code_sha256=sha, payload=payload)

    row = {"region": region, "game_build": build, "account": account, "code_sha256": sha,
           "submitted_at": format_stamp(now), "issue": issue}
    entries, submissions = index.entries, index.submissions
    replaced, replaced_revoked = (), ()
    if mine is not None:
        replaced = (mine["code_sha256"],)
        entries, replaced_revoked = _release(entries, mine["code_sha256"])
        submissions = tuple(item for item in submissions if item is not mine)
        row[REPLACED] = list(_replaced_chain(mine))

    if existing is not None:
        current = next(entry for entry in entries if entry["code_sha256"] == sha)
        entry = dict(current, submitters=current["submitters"] + 1)
        entries = tuple(entry if item["code_sha256"] == sha else item for item in entries)
        status, path = ADDED, None
    else:
        path = code_path(region, build, sha)
        if any(item["path"] == path for item in entries):
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
    current = next(entry for entry in entries if entry["code_sha256"] == code_sha256)
    if current["submitters"] > 1:
        freed, revoked = dict(current, submitters=current["submitters"] - 1), ()
    else:
        freed, revoked = dict(current, revoked=True), (code_sha256,)
    return tuple(freed if entry["code_sha256"] == code_sha256 else entry for entry in entries), revoked


def _replaced_chain(superseded: Mapping) -> tuple:
    """Every code this account submitted for this build before, newest last, each named once."""
    chain = list(superseded.get(REPLACED, ())) + [superseded["code_sha256"]]
    return tuple(dict.fromkeys(chain))


def _cap_refusal(index: Index) -> str | None:
    try:
        check_caps(index)
    except IndexFull as full:
        return str(full)
    return None


def code_descriptor(root: Path, entry: Mapping) -> tuple | None:
    """``(template_sha256, match_source)`` read from the entry's own code file, or None when it cannot be.

    "Cannot be" covers a missing or unreadable file, bytes that are not this entry's code, and a code
    that no longer decodes: such an entry takes part in no conflict group rather than stopping the run.
    """
    try:
        text = (Path(root) / entry["path"]).read_bytes().decode("utf-8", "replace")
    except OSError:
        return None
    decoded = sharecode.decode(text)
    if not decoded.valid or decoded.code_sha256 != entry["code_sha256"]:
        return None
    return decoded.payload["template_sha256"], decoded.payload["match_source"]


def update_conflicts(index: Index, root: Path, region: str, build: str, *, log: Any = None) -> Index:
    """Recomputes ``conflicting`` for one (region, build) from the code files under ``root`` (plan section 18.6).

    Two published, non-revoked codes of the same template and the same ``match_source`` that are not the
    same code disagree about the same thing, so at least one of them is wrong: every member of such a
    group is flagged, and every other entry of that (region, build) has the flag cleared. Revoked entries
    neither count nor are flagged. An entry whose code file ``code_descriptor`` cannot read is skipped and,
    when ``log`` is given, named to it. Pure apart from reading the code files, and idempotent: the flags
    depend only on what the entries and their files say, never on the flags already there.
    """
    groups: dict = {}
    for entry in index.entries:
        if (entry["region"], entry["game_build"]) != (region, build) or entry["revoked"]:
            continue
        descriptor = code_descriptor(root, entry)
        if descriptor is None:
            if log is not None:
                log("no readable code file for %s at %s" % (entry["code_sha256"][:12], entry["path"]))
            continue
        groups.setdefault(descriptor, set()).add(entry["code_sha256"])
    conflicting = {sha for codes in groups.values() if len(codes) > 1 for sha in codes}
    return replace(index, entries=tuple(
        _set_conflicting(entry, entry["code_sha256"] in conflicting)
        if (entry["region"], entry["game_build"]) == (region, build) else entry
        for entry in index.entries
    ))


def _set_conflicting(entry: Mapping, flag: bool) -> Mapping:
    """The entry with the flag set, or without the field at all when there is no conflict."""
    if flag:
        return dict(entry, conflicting=True)
    if CONFLICTING in entry:
        return {name: value for name, value in entry.items() if name != CONFLICTING}
    return entry


def revoke(index: Index, code_sha256: str) -> Index:
    """Marks every entry of a code revoked; idempotent; KeyError when the index does not list it."""
    if not any(entry["code_sha256"] == code_sha256 for entry in index.entries):
        raise KeyError(code_sha256)
    return replace(index, entries=tuple(
        dict(entry, revoked=True) if entry["code_sha256"] == code_sha256 else entry for entry in index.entries
    ))
