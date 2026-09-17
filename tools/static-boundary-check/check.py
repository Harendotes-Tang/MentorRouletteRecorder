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
    reason.
*   A source line may opt out by carrying the allow marker declared in
    ``rules.json``. That escape hatch exists only for code whose purpose is to
    *enforce* the boundary (for example, a build step that deletes an
    injection payload from the output directory) and every use must carry an
    explanatory comment.
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

Exit codes
----------
0   no violation found
1   at least one violation found
2   the checker could not run (bad rules file, missing directory, ...)

Usage
-----
    python tools/static-boundary-check/check.py [--root <repo-root>] [--json]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

RULES_FILENAME = "rules.json"
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

    @property
    def key(self) -> str:
        """``id`` for a rule declared once, ``id/variant`` for one of several entries."""
        return f"{self.identifier}/{self.variant}" if self.variant else self.identifier

    def applies_to(self, relative: str) -> bool:
        """True unless ``relative`` (repository-relative, forward slashes) is allowed."""
        if relative in self.allow_paths:
            return False
        return not any(relative.startswith(prefix) for prefix in self.allow_path_prefixes)


_PATH_SEGMENT = re.compile(r"^[A-Za-z0-9._-]+$")
_VARIANT = re.compile(r"^[a-z0-9][a-z0-9-]{0,39}$")


def read_allowances(identifier: str, entry: dict, key: str, directory: bool) -> tuple[str, ...]:
    """Read ``allow_paths`` (files) or ``allow_path_prefixes`` (directories) of one rule.

    Every entry must be a repository-relative path with forward slashes and no ``.`` or
    ``..`` segment. File entries must not end in ``/``; directory entries must, so that
    ``tools/shared-calibration/`` can never match ``tools/shared-calibration-old/``.
    Anything else makes the rules file unusable rather than silently allowing too much.
    """
    raw = entry.get(key, [])
    if not isinstance(raw, list):
        raise RulesError(f"rule {identifier}: '{key}' must be an array of strings")
    values: list[str] = []
    for value in raw:
        if not isinstance(value, str):
            raise RulesError(f"rule {identifier}: '{key}' must be an array of strings")
        if directory != value.endswith("/"):
            shape = "end with '/'" if directory else "name a file, not end with '/'"
            raise RulesError(f"rule {identifier}: {key} entry {value!r} must {shape}")
        body = value[:-1] if directory else value
        segments = body.split("/")
        if not body or any(
            segment in (".", "..") or not _PATH_SEGMENT.match(segment) for segment in segments
        ):
            raise RulesError(
                f"rule {identifier}: {key} entry {value!r} is not a repository-relative "
                "forward-slash path"
            )
        values.append(value)
    return tuple(values)


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
class Config:
    allow_marker: str
    scan_dirs: tuple[str, ...]
    scan_files: tuple[str, ...]
    exclude_dirs: frozenset[str]
    include_extensions: frozenset[str]
    max_file_bytes: int
    rules: tuple[Rule, ...]


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

    scan_dirs = tuple(str(d) for d in raw.get("scan_dirs", []) if str(d).strip())
    if not scan_dirs:
        raise RulesError("rules file must declare a non-empty 'scan_dirs'")

    scan_files = tuple(str(f) for f in raw.get("scan_files", []) if str(f).strip())

    extensions = {
        str(e).lower() for e in raw.get("include_extensions", []) if str(e).strip()
    }
    if not extensions:
        raise RulesError("rules file must declare a non-empty 'include_extensions'")

    return Config(
        allow_marker=allow_marker,
        scan_dirs=scan_dirs,
        scan_files=scan_files,
        exclude_dirs=frozenset(str(d) for d in raw.get("exclude_dirs", [])),
        include_extensions=frozenset(extensions),
        max_file_bytes=int(raw.get("max_file_bytes", 4 * 1024 * 1024)),
        rules=tuple(rules),
    )


def iter_candidate_files(root: Path, config: Config) -> Iterable[Path]:
    """Yield every file worth reading, from the scan directories and the root files.

    ``scan_files`` exists because the repository root holds files that are part of
    the build - the top-level ``CMakeLists.txt`` and the shared MSBuild files - and
    walking the root itself would drag in every build and artifact directory.
    """
    for scan_file in config.scan_files:
        candidate = root / scan_file
        if not candidate.is_file():
            continue
        try:
            if candidate.stat().st_size > config.max_file_bytes:
                continue
        except OSError:
            continue
        yield candidate

    for scan_dir in config.scan_dirs:
        base = root / scan_dir
        if not base.is_dir():
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = sorted(
                d for d in dirnames if d not in config.exclude_dirs and not d.startswith(".")
            )
            for filename in sorted(filenames):
                path = Path(dirpath) / filename
                if path.suffix.lower() not in config.include_extensions:
                    continue
                try:
                    if path.stat().st_size > config.max_file_bytes:
                        continue
                except OSError:
                    continue
                yield path


def relative_path(root: Path, path: Path) -> str:
    """Repository-relative path with forward slashes, as allowances are written."""
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        # Outside the root: an absolute path never equals a validated allowance.
        return path.as_posix()


def scan_file(path: Path, config: Config, relative: str) -> list[Violation]:
    """Match every rule that applies to ``relative`` against every line of ``path``.

    ``relative`` is the file's repository-relative path with forward slashes; a rule's
    ``allow_paths`` and ``allow_path_prefixes`` are compared with it and nothing else.
    """
    rules = [rule for rule in config.rules if rule.applies_to(relative)]
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        print(f"warning: cannot read {path}: {exc}", file=sys.stderr)
        return []

    violations: list[Violation] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        if config.allow_marker in line:
            continue
        for rule in rules:
            if rule.regex.search(line):
                violations.append(
                    Violation(path=path, line_number=line_number, rule=rule, line=line)
                )
    return violations


def run(root: Path, rules_path: Path) -> tuple[list[Violation], int, list[str]]:
    config = load_config(rules_path)
    violations: list[Violation] = []
    scanned = 0
    for path in iter_candidate_files(root, config):
        scanned += 1
        violations.extend(scan_file(path, config, relative_path(root, path)))

    roots = [d for d in config.scan_dirs if (root / d).is_dir()]
    roots += [f for f in config.scan_files if (root / f).is_file()]
    return violations, scanned, roots


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
        violations, scanned, scanned_roots = run(root, Path(args.rules).resolve())
    except RulesError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_ERROR

    if args.json:
        print(
            json.dumps(
                {
                    "root": root.as_posix(),
                    "scanned_roots": scanned_roots,
                    "files_scanned": scanned,
                    "violation_count": len(violations),
                    "violations": [v.to_dict(root) for v in violations],
                },
                ensure_ascii=False,
                indent=2,
            )
        )
    else:
        for violation in violations:
            print(violation.format_text(root))
        if violations:
            print(
                f"\nFAIL: {len(violations)} boundary violation(s) in "
                f"{scanned} scanned file(s)."
            )
        else:
            print(f"OK: no boundary violation. {scanned} file(s) scanned under "
                  f"{', '.join(scanned_roots) or '<nothing>'}.")

    return EXIT_VIOLATION if violations else EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
