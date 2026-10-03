#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Static hard-boundary checker for the MentorRecorder repository.

This script enforces the non-negotiable behavioural boundary described in
``docs/privacy-boundary.md``: the software passively observes local network
traffic and does nothing else to the game or to the network.

Design notes
------------
*   The forbidden patterns live in ``rules.json`` next to this file, never in
    this source file, so that the checker itself contains no literal match.
*   The directory holding this script is excluded from the scan for the same
    reason, by its exact path (``exclude_paths``). Only directories that can never
    hold a source file of ours (``.git``, ``__pycache__``, ...) are skipped by name
    wherever they appear (``exclude_dir_names``); a ``build/`` or ``packages/``
    inside ``src/`` is scanned like any other directory. Build output
    (``exclude_output_dir_names``: ``bin``, ``obj``) is skipped only directly beside
    a ``.csproj``, because that is the only place the .NET SDK leaves it out of the
    build: a ``.cs`` in a ``bin/`` or ``obj/`` anywhere deeper is compiled.
*   A source line may opt out of ONE rule by carrying the allow marker declared
    in ``rules.json`` in the form ``<marker>(<RULE-ID>): <reason>``, for example
    ``BOUNDARY-ALLOW(INJ-008): named only to say why it is not used``. The marker
    lifts one hit of that rule on that line and nothing else; a bare marker, one
    without a reason (another marker is not a reason), or one naming another rule
    lifts nothing, and a marker naming a rule id ``rules.json`` does not declare
    makes the run fail (exit 2). Every marker must also be pinned in
    ``allow_markers`` by file, rule and the exact text of the hit it lifts: a
    marker lifts that hit only when it is the rule's one hit on the line, so a
    second hit added to a marked line, or a different one, is reported. An
    unpinned marker lifts nothing, and an unpinned marker, one whose line does not
    hold exactly its pinned hit, a pinned one the scan does not find and one that
    lifts no hit on its line each fail the run. Every honoured marker is listed,
    with the text it lifted, in both the text and the JSON output. The escape
    hatch exists for code whose purpose is to *enforce* the boundary, and for a
    file's single hit that is not the forbidden thing at all (a doc comment naming
    a format, a record's ``ExitCode``).
*   A rule entry with ``only_extensions`` applies to those file types only, so a
    pattern written for one language's syntax (a shell command at command
    position) is not matched against another language's prose.
*   A rule may also name the files it does not apply to: ``allow_paths`` lists
    repository-relative, forward-slash file paths matched exactly, and
    ``allow_path_prefixes`` lists directory prefixes that must end in ``/``.
    Neither matches by file or directory name alone, each lifts only its own
    rule, and a malformed entry makes the rules file unusable (exit 2) rather
    than allowing more than it says. Every allowance is explained in
    ``docs/privacy-boundary.md``.
*   One rule id may be declared more than once when each entry names a
    different ``variant``: the entries share the id violations are reported
    under, but each has its own pattern and its own allowances, so a file
    allowed one host fragment is still refused the other (NET-007).
*   A scan that could not look at everything it was asked to fails rather than
    passes: a candidate file that cannot be stat-ed or read, one over
    ``max_file_bytes``, one holding NUL bytes (UTF-16 or binary, which the
    UTF-8 reader would see as noise), a directory that cannot be listed, a
    configured scan root, scan file or exclusion that does not exist as written,
    a symbolic link or junction to a directory inside a scan root, and a run that
    scanned no file at all each end the run with exit 2.

Exit codes
----------
0   no violation found
1   at least one violation found
2   the checker could not run, or could not read every candidate file (bad
    rules file, unreadable or oversize file, nothing scanned, ...)

Usage
-----
    python tools/static-boundary-check/check.py [--root <repo-root>] [--json]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import stat
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

RULES_FILENAME = "rules.json"
# A directory holding a file with this suffix is a .NET project; its exclude_output_dir_names
# children are the build output the SDK leaves out of its default item globs.
PROJECT_FILE_SUFFIX = ".csproj"
EXIT_OK = 0
EXIT_VIOLATION = 1
EXIT_ERROR = 2


class RulesError(Exception):
    """Raised when the rules file is missing or malformed."""


@dataclass(frozen=True)
class Rule:
    identifier: str
    category: str
    message: str
    regex: "re.Pattern[str]"
    # Repository-relative, forward-slash file paths this rule does not apply to. Exact
    # match only: a file of the same name anywhere else is still checked.
    allow_paths: frozenset[str] = frozenset()
    # Repository-relative directory prefixes, each ending in "/", this rule does not apply
    # under. Separate from allow_paths so a whole-directory allowance always reads as one.
    allow_path_prefixes: tuple[str, ...] = ()
    # Distinguishes several entries of one rule id, each with its own pattern and its own
    # allowances. Empty for a rule declared once.
    variant: str = ""
    # When not empty, the only file extensions (as path.suffix.lower() gives them) the rule
    # applies to: a pattern written for one language's syntax, such as a shell command at
    # command position, is not matched against prose in another.
    only_extensions: frozenset[str] = frozenset()

    @property
    def key(self) -> str:
        """``id`` for a rule declared once, ``id/variant`` for one of several entries."""
        return f"{self.identifier}/{self.variant}" if self.variant else self.identifier

    def applies_to(self, relative: str) -> bool:
        """True unless ``relative`` (repository-relative, forward slashes) is allowed."""
        if self.only_extensions and Path(relative).suffix.lower() not in self.only_extensions:
            return False
        if relative in self.allow_paths:
            return False
        return not any(relative.startswith(prefix) for prefix in self.allow_path_prefixes)


_PATH_SEGMENT = re.compile(r"^[A-Za-z0-9._-]+$")
_VARIANT = re.compile(r"^[a-z0-9][a-z0-9-]{0,39}$")
# A scanned extension as path.suffix.lower() produces it: one leading dot, lower case.
_EXTENSION = re.compile(r"^\.[a-z0-9]+$")


def marker_regex(allow_marker: str) -> "re.Pattern[str]":
    """``<marker>(<RULE-ID>): <reason>``; the reason runs to the next marker or the line end.

    A bare marker, or one with nothing after the colon, does not match and so lifts nothing.
    The reason may not begin with another marker: ``<marker>(A):<marker>(B): text`` gives A no
    reason at all (so A lifts nothing), and B its own.
    """
    escaped = re.escape(allow_marker)
    return re.compile(
        escaped + r"\((?P<rule>[^()\s]+)\):[ \t]*(?P<reason>(?!" + escaped + r"\()[^ \t].*?)[ \t]*"
        r"(?=" + escaped + r"\(|$)"
    )


def check_relative_path(owner: str, value: object, directory: bool) -> str:
    """``value`` as a repository-relative, forward-slash path, or RulesError.

    No ``.`` or ``..`` segment, no backslash, no leading ``/``. A directory must end in ``/``,
    so that ``tools/shared-calibration/`` can never match ``tools/shared-calibration-old/``;
    a file must not.
    """
    if not isinstance(value, str):
        raise RulesError(f"{owner}: {value!r} is not a string")
    if directory != value.endswith("/"):
        shape = "end with '/'" if directory else "name a file, not end with '/'"
        raise RulesError(f"{owner}: {value!r} must {shape}")
    body = value[:-1] if directory else value
    segments = body.split("/")
    if not body or any(
        segment in (".", "..") or not _PATH_SEGMENT.match(segment) for segment in segments
    ):
        raise RulesError(f"{owner}: {value!r} is not a repository-relative forward-slash path")
    return value


def read_allowances(identifier: str, entry: dict, key: str, directory: bool) -> tuple[str, ...]:
    """Read ``allow_paths`` (files) or ``allow_path_prefixes`` (directories) of one rule.

    Every entry must be a repository-relative path (see ``check_relative_path``). Anything
    else makes the rules file unusable rather than silently allowing too much.
    """
    raw = entry.get(key, [])
    if not isinstance(raw, list):
        raise RulesError(f"rule {identifier}: '{key}' must be an array of strings")
    return tuple(
        check_relative_path(f"rule {identifier}: {key} entry", value, directory) for value in raw
    )


@dataclass(frozen=True)
class Violation:
    path: Path
    line_number: int
    rule: Rule
    line: str

    def format_text(self, root: Path) -> str:
        try:
            shown = self.path.relative_to(root).as_posix()
        except ValueError:
            shown = self.path.as_posix()
        return (
            f"{shown}:{self.line_number}: [{self.rule.identifier}] "
            f"{self.rule.message}\n"
            f"    | {self.line.strip()[:200]}"
        )

    def to_dict(self, root: Path) -> dict:
        try:
            shown = self.path.relative_to(root).as_posix()
        except ValueError:
            shown = self.path.as_posix()
        return {
            "file": shown,
            "line": self.line_number,
            "rule_id": self.rule.identifier,
            "rule_key": self.rule.key,
            "category": self.rule.category,
            "message": self.rule.message,
            "text": self.line.strip()[:200],
        }


@dataclass(frozen=True)
class AllowMarker:
    """One honoured ``<marker>(<RULE-ID>): <reason>`` and the one hit, ``match``, it lifted."""

    path: Path
    line_number: int
    rule_id: str
    reason: str
    match: str
    lifted: int = 1

    def to_dict(self, root: Path) -> dict:
        return {
            "file": relative_path(root, self.path),
            "line": self.line_number,
            "rule_id": self.rule_id,
            "reason": self.reason[:200],
            "match": self.match,
            "lifted": self.lifted,
        }

    def format_text(self, root: Path) -> str:
        return (
            f"  {relative_path(root, self.path)}:{self.line_number}: [{self.rule_id}] "
            f"{self.reason[:200]} (lifted {self.lifted} hit: {self.match!r})"
        )


@dataclass(frozen=True)
class Config:
    allow_marker: str
    marker: "re.Pattern[str]"
    scan_dirs: tuple[str, ...]
    scan_files: tuple[str, ...]
    # Directory names skipped wherever the walk meets them: only names that can never hold a
    # source file of ours (.git, __pycache__, ...). Everything else is excluded by exact path.
    exclude_dir_names: frozenset[str]
    # Build output directory names (bin, obj), skipped only directly beside a .csproj.
    exclude_output_dir_names: frozenset[str]
    # Repository-relative directories, each ending in "/", skipped by exact path. Each must
    # exist as written and lie inside a scan root without swallowing one.
    exclude_paths: tuple[str, ...]
    include_extensions: frozenset[str]
    max_file_bytes: int
    rules: tuple[Rule, ...]
    # (file, rule id, matched text) of every allow marker the repository is expected to carry, one
    # entry per marker. A marker not listed here lifts nothing and fails the run.
    pinned_markers: tuple[tuple[str, str, str], ...] = ()


def load_config(rules_path: Path) -> Config:
    """Read and validate ``rules.json``."""
    if not rules_path.is_file():
        raise RulesError(f"rules file not found: {rules_path}")
    try:
        raw = json.loads(rules_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise RulesError(f"cannot parse {rules_path}: {exc}") from exc

    if not isinstance(raw, dict):
        raise RulesError("rules file must contain a JSON object")

    raw_rules = raw.get("rules")
    if not isinstance(raw_rules, list) or not raw_rules:
        raise RulesError("rules file must declare a non-empty 'rules' array")
    include_extensions = read_include_extensions(raw)

    rules: list[Rule] = []
    seen: set[str] = set()
    for index, entry in enumerate(raw_rules):
        if not isinstance(entry, dict):
            raise RulesError(f"rules[{index}] must be an object")
        identifier = str(entry.get("id", "")).strip()
        pattern = entry.get("pattern")
        if not identifier:
            raise RulesError(f"rules[{index}] is missing 'id'")
        variant = entry.get("variant", "")
        if not isinstance(variant, str) or (variant and not _VARIANT.match(variant)):
            raise RulesError(
                f"rule {identifier}: 'variant' must be a short lower-case token, got {variant!r}"
            )
        key = f"{identifier}/{variant}" if variant else identifier
        if key in seen:
            raise RulesError(f"duplicate rule id: {key}")
        seen.add(key)
        if not isinstance(pattern, str) or not pattern:
            raise RulesError(f"rule {identifier} is missing 'pattern'")
        try:
            regex = re.compile(pattern)
        except re.error as exc:
            raise RulesError(f"rule {identifier} has an invalid regex: {exc}") from exc
        rules.append(
            Rule(
                identifier=identifier,
                category=str(entry.get("category", "uncategorized")),
                message=str(entry.get("message", "forbidden pattern")),
                regex=regex,
                allow_paths=frozenset(
                    read_allowances(identifier, entry, "allow_paths", directory=False)
                ),
                allow_path_prefixes=read_allowances(
                    identifier, entry, "allow_path_prefixes", directory=True
                ),
                variant=variant,
                only_extensions=read_only_extensions(key, entry, include_extensions),
            )
        )

    # An id declared once and again with a variant would make "the NET-007 rule" ambiguous:
    # every entry of a repeated id must name its variant.
    for rule in rules:
        if not rule.variant and any(
            other.identifier == rule.identifier and other.variant for other in rules
        ):
            raise RulesError(
                f"rule {rule.identifier} is declared more than once; every entry needs a 'variant'"
            )

    allow_marker = str(raw.get("allow_marker", "")).strip()
    if not allow_marker:
        raise RulesError("rules file must declare a non-empty 'allow_marker'")

    scan_dirs = read_path_list(raw, "scan_dirs", directory=True)
    if not scan_dirs:
        raise RulesError("rules file must declare a non-empty 'scan_dirs'")
    scan_files = read_path_list(raw, "scan_files", directory=False)

    max_file_bytes = raw.get("max_file_bytes", 4 * 1024 * 1024)
    if isinstance(max_file_bytes, bool) or not isinstance(max_file_bytes, int) or max_file_bytes < 1:
        raise RulesError(f"'max_file_bytes' must be a positive integer, got {max_file_bytes!r}")

    exclude_dir_names = read_exclude_dir_names(raw, "exclude_dir_names")
    exclude_output_dir_names = read_exclude_dir_names(raw, "exclude_output_dir_names")
    both = sorted(exclude_dir_names & exclude_output_dir_names)
    if both:
        # Skipped everywhere or only beside a project file: a name cannot be both.
        raise RulesError(f"{both[0]!r} is in both exclude_dir_names and exclude_output_dir_names")

    return Config(
        allow_marker=allow_marker,
        marker=marker_regex(allow_marker),
        scan_dirs=scan_dirs,
        scan_files=scan_files,
        exclude_dir_names=exclude_dir_names,
        exclude_output_dir_names=exclude_output_dir_names,
        exclude_paths=read_exclude_paths(raw, scan_dirs, scan_files),
        include_extensions=include_extensions,
        max_file_bytes=max_file_bytes,
        rules=tuple(rules),
        pinned_markers=read_pinned_markers(raw, {rule.identifier for rule in rules}),
    )


def read_include_extensions(raw: dict) -> frozenset[str]:
    """``include_extensions``, each a lower-case suffix with its leading dot.

    Matched against path.suffix.lower(), so an entry without its dot or in upper case would
    never match anything and silently drop its files from the scan: refuse it instead.
    """
    raw_extensions = raw.get("include_extensions", [])
    if not isinstance(raw_extensions, list) or not raw_extensions:
        raise RulesError("rules file must declare a non-empty 'include_extensions'")
    for extension in raw_extensions:
        if not isinstance(extension, str) or not _EXTENSION.match(extension):
            raise RulesError(
                f"include_extensions entry {extension!r} must be a lower-case suffix with "
                "its leading dot, such as '.cs'"
            )
    return frozenset(raw_extensions)


def read_only_extensions(key: str, entry: dict, scanned: frozenset[str]) -> frozenset[str]:
    """A rule's ``only_extensions``: absent, or a non-empty list of scanned extensions."""
    if "only_extensions" not in entry:
        return frozenset()
    values = entry["only_extensions"]
    if not isinstance(values, list) or not values:
        raise RulesError(f"rule {key}: 'only_extensions' must be a non-empty array")
    for extension in values:
        if not isinstance(extension, str) or not _EXTENSION.match(extension):
            raise RulesError(
                f"rule {key}: only_extensions entry {extension!r} must be a lower-case suffix "
                "with its leading dot"
            )
        if extension not in scanned:
            # The rule would never see such a file: an entry that cannot apply is a mistake.
            raise RulesError(
                f"rule {key}: only_extensions entry {extension!r} is not in include_extensions"
            )
    return frozenset(values)


def read_path_list(raw: dict, key: str, directory: bool) -> tuple[str, ...]:
    """``scan_dirs`` (written without a trailing '/') or ``scan_files``, as checked paths."""
    values = raw.get(key, [])
    if not isinstance(values, list):
        raise RulesError(f"'{key}' must be an array of strings")
    checked = []
    for value in values:
        written = value + "/" if directory and isinstance(value, str) else value
        checked.append(check_relative_path(f"{key} entry", written, directory))
    return tuple(value[:-1] if directory else value for value in checked)


def read_exclude_dir_names(raw: dict, key: str) -> frozenset[str]:
    """``exclude_dir_names`` or ``exclude_output_dir_names``: plain directory names, each one
    path segment."""
    if "exclude_dirs" in raw:
        # The old single list excluded every name at any depth; refuse it rather than guess
        # which of its entries were meant as names and which as paths.
        raise RulesError("'exclude_dirs' is replaced by 'exclude_dir_names' and 'exclude_paths'")
    values = raw.get(key, [])
    if not isinstance(values, list):
        raise RulesError(f"'{key}' must be an array of strings")
    for value in values:
        if not isinstance(value, str) or value in (".", "..") or not _PATH_SEGMENT.match(value):
            raise RulesError(f"{key} entry {value!r} must be one directory name")
    return frozenset(values)


def read_exclude_paths(raw: dict, scan_dirs: tuple[str, ...],
                       scan_files: tuple[str, ...]) -> tuple[str, ...]:
    """``exclude_paths``: directories inside a scan root, each ending in '/'.

    An entry outside every scan root excludes nothing, and one that equals or encloses a scan
    root (or a scan file) would switch the boundary off for all of it: both are refused.
    """
    values = raw.get("exclude_paths", [])
    if not isinstance(values, list):
        raise RulesError("'exclude_paths' must be an array of strings")
    checked = tuple(check_relative_path("exclude_paths entry", value, True) for value in values)
    for value in checked:
        swallowed = [d for d in scan_dirs if (d + "/").startswith(value)]
        swallowed += [f for f in scan_files if f.startswith(value)]
        if swallowed:
            raise RulesError(f"exclude_paths entry {value!r} would exclude {swallowed[0]!r}, "
                             "which the scan is configured to read")
        if not any(value.startswith(d + "/") for d in scan_dirs):
            raise RulesError(f"exclude_paths entry {value!r} is not inside any scan_dirs entry, "
                             "so it excludes nothing")
    return checked


def read_pinned_markers(raw: dict, declared: set[str]) -> tuple[tuple[str, str, str], ...]:
    """``allow_markers``: one {"file", "rule", "match"} object per marker the repository carries.

    ``match`` is the exact text of the one hit the marker lifts, as the rule's pattern matches it,
    so the pin says what is allowed and not merely where.
    """
    values = raw.get("allow_markers", [])
    if not isinstance(values, list):
        raise RulesError("'allow_markers' must be an array of {\"file\", \"rule\", \"match\"} objects")
    pins = []
    for value in values:
        if not isinstance(value, dict) or set(value) != {"file", "rule", "match"}:
            raise RulesError(
                f"allow_markers entry {value!r} must have exactly 'file', 'rule' and 'match'")
        relative = check_relative_path("allow_markers file", value["file"], directory=False)
        if value["rule"] not in declared:
            raise RulesError(f"allow_markers entry {value!r} names a rule that is not declared")
        match = value["match"]
        if not isinstance(match, str) or not match or "\n" in match or "\r" in match:
            raise RulesError(f"allow_markers entry {value!r}: 'match' must be the text of one hit")
        pins.append((relative, value["rule"], match))
    return tuple(pins)


def missing_as_written(root: Path, relative: str, directory: bool) -> str | None:
    """None when ``relative`` exists under ``root``, spelled exactly so, as a directory or a
    regular file; otherwise why not.

    Every segment is looked up in its parent's listing, so a case-only difference counts as
    missing even on a case-insensitive file system. The final check is ``os.stat``, which
    raises on every Python version where ``Path.is_dir()`` / ``is_file()`` would answer False
    for an entry that merely cannot be stat-ed (3.14 swallows every OSError there).
    """
    current = root
    for segment in relative.rstrip("/").split("/"):
        try:
            names = os.listdir(current)
        except OSError as exc:
            return f"cannot list {relative_path(root, current) or '.'}: {exc}"
        if segment not in names:
            return "does not exist as written"
        current = current / segment
    try:
        mode = os.stat(current).st_mode
    except OSError as exc:
        return f"cannot be stat-ed: {exc}"
    if directory and not stat.S_ISDIR(mode):
        return "is not a directory"
    if not directory and not stat.S_ISREG(mode):
        return "is not a regular file"
    return None


def is_directory_link(path: str) -> bool:
    """True for a symbolic link, or a Windows junction, that stands in for a directory."""
    status = os.lstat(path)
    if stat.S_ISLNK(status.st_mode):
        return True
    junction = getattr(stat, "IO_REPARSE_TAG_MOUNT_POINT", None)
    return junction is not None and getattr(status, "st_reparse_tag", 0) == junction


def iter_candidate_files(root: Path, config: Config, errors: list[str],
                         roots: list[str] | None = None) -> Iterable[Path]:
    """Yield every file worth reading, from the scan directories and the root files.

    ``scan_files`` exists because the repository root holds files that are part of
    the build - the top-level ``CMakeLists.txt`` and the shared MSBuild files - and
    walking the root itself would drag in every build and artifact directory.

    A candidate that cannot be stat-ed, or is larger than ``max_file_bytes``, a directory
    that cannot be listed, a configured scan root, scan file or exclusion that does not
    exist as written, and a symbolic link or junction to a directory met inside a scan root
    are not scanned: each is appended to ``errors`` instead, which fails the run. Skipping
    them silently would let a forbidden token pass just by living in such a place. Every
    scan root and scan file actually read is appended to ``roots``.
    """
    roots = [] if roots is None else roots

    def unlistable(exc: OSError) -> None:
        errors.append(f"cannot list {relative_path(root, Path(exc.filename or ''))}: {exc}")

    def readable_size(path: Path) -> bool:
        try:
            size = path.stat().st_size
        except OSError as exc:
            errors.append(f"cannot stat {relative_path(root, path)}: {exc}")
            return False
        if size > config.max_file_bytes:
            errors.append(
                f"{relative_path(root, path)} is {size} bytes, over max_file_bytes "
                f"({config.max_file_bytes}); it was not scanned"
            )
            return False
        return True

    for excluded in config.exclude_paths:
        problem = missing_as_written(root, excluded, directory=True)
        if problem:
            errors.append(f"exclude_paths entry {excluded} {problem}; remove or correct it")

    for scan_file in config.scan_files:
        problem = missing_as_written(root, scan_file, directory=False)
        if problem:
            errors.append(f"configured scan file {scan_file} {problem}")
            continue
        roots.append(scan_file)
        candidate = root / scan_file
        if readable_size(candidate):
            yield candidate

    for scan_dir in config.scan_dirs:
        problem = missing_as_written(root, scan_dir, directory=True)
        if problem:
            errors.append(f"configured scan root {scan_dir} {problem}")
            continue
        roots.append(scan_dir)
        for dirpath, dirnames, filenames in os.walk(root / scan_dir, onerror=unlistable):
            here = relative_path(root, Path(dirpath))
            # The SDK leaves out <project>/bin and <project>/obj only; one deeper is compiled.
            project = any(name.lower().endswith(PROJECT_FILE_SUFFIX) for name in filenames)
            kept = []
            for name in sorted(dirnames):
                if name in config.exclude_dir_names or f"{here}/{name}/" in config.exclude_paths:
                    continue
                if project and name in config.exclude_output_dir_names:
                    continue
                try:
                    linked = is_directory_link(os.path.join(dirpath, name))
                except OSError as exc:
                    errors.append(f"cannot stat {here}/{name}: {exc}")
                    continue
                if linked:
                    # os.walk does not follow a symbolic link (and follows a junction): either
                    # way the boundary would depend on where the link points. Report it.
                    errors.append(
                        f"{here}/{name} is a symbolic link or junction to a directory and was not "
                        "scanned; replace it with a real directory or exclude it by path"
                    )
                    continue
                kept.append(name)
            dirnames[:] = kept
            for filename in sorted(filenames):
                path = Path(dirpath) / filename
                if path.suffix.lower() not in config.include_extensions:
                    continue
                if readable_size(path):
                    yield path


def relative_path(root: Path, path: Path) -> str:
    """Repository-relative path with forward slashes, as allowances are written."""
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        # Outside the root: an absolute path never equals a validated allowance.
        return path.as_posix()


@dataclass(frozen=True)
class ScanResult:
    violations: list[Violation]
    markers: list[AllowMarker]
    errors: list[str]
    scanned: int
    roots: list[str]


def line_markers(line: str, config: Config, relative: str, line_number: int,
                 errors: list[str]) -> dict[str, str]:
    """Rule id -> reason for every well-formed, pinned allow marker on ``line``.

    A marker naming a rule id that ``rules.json`` does not declare is an error: it would
    lift nothing, and is most likely a renamed rule or a typo hiding behind a review. A
    marker whose file and rule ``allow_markers`` does not list is an error too, and lifts
    nothing: a new exemption has to be added to the configuration, where review sees it.
    """
    found: dict[str, str] = {}
    if config.allow_marker not in line:
        return found
    declared = {rule.identifier for rule in config.rules}
    pinned = {(file, rule) for file, rule, _ in config.pinned_markers}
    for match in config.marker.finditer(line):
        rule_id, reason = match.group("rule"), match.group("reason")
        if not re.search(r"\w", reason):
            continue
        if rule_id not in declared:
            errors.append(
                f"{relative}:{line_number}: {config.allow_marker}({rule_id}) names a rule "
                "that rules.json does not declare"
            )
            continue
        if (relative, rule_id) not in pinned:
            errors.append(
                f"{relative}:{line_number}: {config.allow_marker}({rule_id}) is not pinned in "
                "rules.json 'allow_markers', so it lifts nothing"
            )
            continue
        found.setdefault(rule_id, reason)
    return found


def scan_file(path: Path, config: Config, relative: str,
              errors: list[str]) -> tuple[list[Violation], list[AllowMarker]]:
    """Match every rule that applies to ``relative`` against every line of ``path``.

    ``relative`` is the file's repository-relative path with forward slashes; a rule's
    ``allow_paths`` and ``allow_path_prefixes`` are compared with it and nothing else.
    A file that cannot be read, or that holds NUL bytes, is reported in ``errors``.
    """
    rules = [rule for rule in config.rules if rule.applies_to(relative)]
    try:
        data = path.read_bytes()
    except OSError as exc:
        errors.append(f"cannot read {relative}: {exc}")
        return [], []
    if b"\x00" in data:
        errors.append(
            f"{relative} contains NUL bytes (UTF-16 or binary?); the checker reads UTF-8 "
            "text only, so it could not scan this file"
        )
        return [], []
    text = data.decode("utf-8", errors="replace")

    violations: list[Violation] = []
    markers: list[AllowMarker] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        allowed = line_markers(line, config, relative, line_number, errors)
        # Every hit of a marked rule id on this line, each with the rule entry that made it.
        marked: dict[str, list[tuple[Rule, str]]] = {rule_id: [] for rule_id in allowed}
        for rule in rules:
            if rule.identifier in allowed:
                marked[rule.identifier].extend((rule, hit.group(0)) for hit in rule.regex.finditer(line))
            elif rule.regex.search(line):
                violations.append(
                    Violation(path=path, line_number=line_number, rule=rule, line=line)
                )
        for rule_id, reason in allowed.items():
            hit = lifted_hit(config, relative, line_number, rule_id,
                             [matched for _, matched in marked[rule_id]], errors)
            if hit is not None:
                markers.append(AllowMarker(path, line_number, rule_id, reason, hit))
                continue
            for rule in dict.fromkeys(rule for rule, _ in marked[rule_id]):
                violations.append(
                    Violation(path=path, line_number=line_number, rule=rule, line=line)
                )
    return violations, markers


def lifted_hit(config: Config, relative: str, line_number: int, rule_id: str, hits: list[str],
               errors: list[str]) -> str | None:
    """The one hit a pinned marker lifts on its line, or None with the reason in ``errors``.

    The marker lifts a hit only when it is the rule's one hit on the line and its text is the text
    a pin of this file and rule names: a second hit added to a marked line, or another one in its
    place, is reported rather than lifted along with it.
    """
    marker = f"{relative}:{line_number}: {config.allow_marker}({rule_id})"
    if not hits:
        # A marker with nothing to lift is either stale or waiting to hide a future hit.
        errors.append(f"{marker} lifts no hit on its line; remove it and its 'allow_markers' entry")
        return None
    pinned = sorted({match for file, rule, match in config.pinned_markers if (file, rule) == (relative, rule_id)})
    if len(hits) == 1 and hits[0] in pinned:
        return hits[0]
    errors.append(f"{marker} lifts nothing: its line holds the hit(s) {hits!r}, and rules.json pins it "
                  f"to lift exactly one of {pinned!r}")
    return None


def run(root: Path, rules_path: Path) -> ScanResult:
    config = load_config(rules_path)
    violations: list[Violation] = []
    markers: list[AllowMarker] = []
    errors: list[str] = []
    read: list[str] = []
    scanned = 0
    for path in iter_candidate_files(root, config, errors, read):
        scanned += 1
        found, honoured = scan_file(path, config, relative_path(root, path), errors)
        violations.extend(found)
        markers.extend(honoured)

    if scanned == 0:
        errors.append(
            "no file was scanned: scan_dirs, scan_files and include_extensions matched "
            "nothing under the root, so a pass would prove nothing"
        )

    # Every pinned marker must be present, as often as it is pinned: a pin left behind by a
    # removed marker would quietly admit the next marker written in that file for that rule.
    expected = Counter(config.pinned_markers)
    present = Counter((relative_path(root, marker.path), marker.rule_id, marker.match) for marker in markers)
    for (relative, rule_id, match), count in sorted(expected.items()):
        if present[(relative, rule_id, match)] != count:
            errors.append(
                f"rules.json pins {count} {config.allow_marker}({rule_id}) marker(s) lifting {match!r} "
                f"in {relative}, the scan found {present[(relative, rule_id, match)]}"
            )
    roots = [d for d in config.scan_dirs if d in read] + [f for f in config.scan_files if f in read]
    return ScanResult(violations, markers, errors, scanned, roots)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Static hard-boundary checker for MentorRecorder."
    )
    parser.add_argument(
        "--root",
        default=str(Path(__file__).resolve().parents[2]),
        help="repository root (default: two levels above this script)",
    )
    parser.add_argument(
        "--rules",
        default=str(Path(__file__).resolve().parent / RULES_FILENAME),
        help=f"path to {RULES_FILENAME}",
    )
    parser.add_argument(
        "--json", action="store_true", help="emit machine-readable JSON output"
    )
    args = parser.parse_args(argv)

    # Force UTF-8 so a legacy Windows console code page does not mangle the
    # bilingual messages. Failure to reconfigure the stream is not fatal.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, OSError, ValueError):
            pass

    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"error: root directory not found: {root}", file=sys.stderr)
        return EXIT_ERROR

    try:
        result = run(root, Path(args.rules).resolve())
    except RulesError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_ERROR
    violations, scanned = result.violations, result.scanned

    if args.json:
        print(
            json.dumps(
                {
                    "root": root.as_posix(),
                    "scanned_roots": result.roots,
                    "files_scanned": scanned,
                    "violation_count": len(violations),
                    "violations": [v.to_dict(root) for v in violations],
                    "allow_marker_count": len(result.markers),
                    "allow_markers": [m.to_dict(root) for m in result.markers],
                    "error_count": len(result.errors),
                    "errors": result.errors,
                },
                ensure_ascii=False,
                indent=2,
            )
        )
    else:
        for violation in violations:
            print(violation.format_text(root))
        # Listed on every run, so a new exemption is visible in review rather than silent.
        print(f"\n{len(result.markers)} allow marker(s) honoured:")
        for marker in result.markers:
            print(marker.format_text(root))
        for error in result.errors:
            print(f"error: {error}", file=sys.stderr)
        if result.errors:
            print(
                f"\nERROR: the scan is incomplete ({len(result.errors)} problem(s) above); "
                f"{len(violations)} violation(s) in {scanned} scanned file(s)."
            )
        elif violations:
            print(
                f"\nFAIL: {len(violations)} boundary violation(s) in "
                f"{scanned} scanned file(s)."
            )
        else:
            print(f"OK: no boundary violation. {scanned} file(s) scanned under "
                  f"{', '.join(result.roots) or '<nothing>'}.")

    if result.errors:
        return EXIT_ERROR
    return EXIT_VIOLATION if violations else EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
