#!/usr/bin/env python3
"""Enforce MentorRecorder's source-level module dependency rules.

This is deliberately a small lexical checker rather than a compiler front end.  It
removes comments and string literal text while preserving locations, keeps C#
interpolation expressions as code, validates the required inspection scope, and fails
closed when it cannot read or tokenize a source file.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable


EXIT_OK = 0
EXIT_VIOLATION = 1
EXIT_INSPECTION_ERROR = 2

IGNORED_DIRS = {"bin", "obj", ".git", "build", "artifacts", "__pycache__"}
DOMAIN_NAMESPACE = "MentorRecorder.Collector.Domain"
PROJECT_NAMESPACE = "MentorRecorder.Collector"
BASE_RUNTIME_NAMESPACE_PREFIXES = ("System", "Microsoft.Win32")
FORBIDDEN_WORKFLOW_TYPES = {"AppController", "CollectorProcess", "TtsService"}


class LexError(ValueError):
    """A source construct could not be inspected without risking a false pass."""


@dataclass(frozen=True)
class Finding:
    file: str
    line: int
    column: int
    rule: str
    dependency: str
    message: str


def _relative(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


def _position(text: str, offset: int) -> tuple[int, int]:
    line = text.count("\n", 0, offset) + 1
    previous = text.rfind("\n", 0, offset)
    return line, offset - previous


def _blank(chars: list[str], start: int, end: int) -> None:
    for index in range(start, end):
        if chars[index] not in "\r\n":
            chars[index] = " "


def _quoted_end(text: str, start: int, quote: str, verbatim: bool = False) -> int:
    index = start + 1
    while index < len(text):
        char = text[index]
        if verbatim and char == '"' and index + 1 < len(text) and text[index + 1] == '"':
            index += 2
            continue
        if char == quote:
            return index + 1
        if not verbatim and char == "\\":
            index += 2
            continue
        if char in "\r\n" and not verbatim:
            raise LexError("newline in quoted literal")
        index += 1
    raise LexError("unterminated quoted literal")


def _csharp_interpolation_parts(text: str, start: int) -> tuple[int, int]:
    """Return (closing brace, end of executable expression) for interpolation."""
    depth = 1
    parentheses = 0
    brackets = 0
    expression_end: int | None = None
    index = start
    while index < len(text):
        if text.startswith("//", index):
            newline = text.find("\n", index + 2)
            index = len(text) if newline < 0 else newline + 1
            continue
        if text.startswith("/*", index):
            end = text.find("*/", index + 2)
            if end < 0:
                raise LexError("unterminated block comment in interpolation")
            index = end + 2
            continue
        if text.startswith('@"', index):
            index = _quoted_end(text, index + 1, '"', verbatim=True)
            continue
        if text[index] in "'\"":
            index = _quoted_end(text, index, text[index])
            continue
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return index, expression_end if expression_end is not None else index
        elif depth == 1 and expression_end is None:
            if text[index] == "(":
                parentheses += 1
            elif text[index] == ")":
                parentheses = max(0, parentheses - 1)
            elif text[index] == "[":
                brackets += 1
            elif text[index] == "]":
                brackets = max(0, brackets - 1)
            elif text[index] == ":" and parentheses == 0 and brackets == 0:
                expression_end = index
        index += 1
    raise LexError("unterminated interpolated expression")


def _mask_interpolated_string(text: str, chars: list[str], start: int, prefix_length: int,
                              verbatim: bool) -> int:
    index = start + prefix_length
    _blank(chars, start, index)
    while index < len(text):
        char = text[index]
        if verbatim and char == '"' and index + 1 < len(text) and text[index + 1] == '"':
            _blank(chars, index, index + 2)
            index += 2
            continue
        if char == '"':
            _blank(chars, index, index + 1)
            return index + 1
        if not verbatim and char == "\\":
            _blank(chars, index, min(index + 2, len(text)))
            index += 2
            continue
        if char == "{" and index + 1 < len(text) and text[index + 1] == "{":
            _blank(chars, index, index + 2)
            index += 2
            continue
        if char == "{":
            close, expression_end = _csharp_interpolation_parts(text, index + 1)
            _blank(chars, index, index + 1)
            expression = mask_csharp(text[index + 1:expression_end])
            chars[index + 1:expression_end] = expression
            _blank(chars, expression_end, close)
            _blank(chars, close, close + 1)
            index = close + 1
            continue
        if char == "}" and index + 1 < len(text) and text[index + 1] == "}":
            _blank(chars, index, index + 2)
            index += 2
            continue
        if char in "\r\n" and not verbatim:
            raise LexError("newline in interpolated string")
        _blank(chars, index, index + 1)
        index += 1
    raise LexError("unterminated interpolated string")


def mask_csharp(text: str) -> list[str]:
    """Return location-preserving C# code with comments/string text blanked."""
    chars = list(text)
    index = 0
    while index < len(text):
        if text.startswith("//", index):
            end = text.find("\n", index + 2)
            end = len(text) if end < 0 else end
            _blank(chars, index, end)
            index = end
            continue
        if text.startswith("/*", index):
            end = text.find("*/", index + 2)
            if end < 0:
                raise LexError("unterminated block comment")
            _blank(chars, index, end + 2)
            index = end + 2
            continue

        raw = re.match(r'(\$*)("{3,})', text[index:])
        if raw:
            if raw.group(1):
                raise LexError("interpolated raw strings are not supported")
            delimiter = raw.group(2)
            end = text.find(delimiter, index + len(delimiter))
            if end < 0:
                raise LexError("unterminated raw string")
            _blank(chars, index, end + len(delimiter))
            index = end + len(delimiter)
            continue

        if text.startswith('$@"', index) or text.startswith('@$"', index):
            index = _mask_interpolated_string(text, chars, index, 3, True)
            continue
        if text.startswith('$"', index):
            index = _mask_interpolated_string(text, chars, index, 2, False)
            continue
        if text.startswith('@"', index):
            end = _quoted_end(text, index + 1, '"', verbatim=True)
            _blank(chars, index, end)
            index = end
            continue
        if text[index] in "'\"":
            end = _quoted_end(text, index, text[index])
            _blank(chars, index, end)
            index = end
            continue
        index += 1
    return chars


def _mask_cpp(text: str, *, preserve_include_strings: bool = False) -> str:
    chars = list(text)
    index = 0
    while index < len(text):
        if text.startswith("//", index):
            end = text.find("\n", index + 2)
            end = len(text) if end < 0 else end
            _blank(chars, index, end)
            index = end
            continue
        if text.startswith("/*", index):
            end = text.find("*/", index + 2)
            if end < 0:
                raise LexError("unterminated block comment")
            _blank(chars, index, end + 2)
            index = end + 2
            continue
        raw = re.match(r'(?:u8|u|U|L)?R"([^ ()\\\t\r\n]{0,16})\(', text[index:])
        if raw:
            terminator = ")" + raw.group(1) + '"'
            end = text.find(terminator, index + raw.end())
            if end < 0:
                raise LexError("unterminated C++ raw string")
            _blank(chars, index, end + len(terminator))
            index = end + len(terminator)
            continue
        quoted = re.match(r'(?:u8|u|U|L)?(["\'])', text[index:])
        if quoted:
            quote_at = index + quoted.end() - 1
            end = _quoted_end(text, quote_at, quoted.group(1))
            line_start = text.rfind("\n", 0, index) + 1
            prefix = "".join(chars[line_start:index])
            is_quoted_include = (
                preserve_include_strings
                and quoted.group(1) == '"'
                and re.fullmatch(r"\s*#\s*include\s*", prefix) is not None
            )
            if not is_quoted_include:
                _blank(chars, index, end)
            index = end
            continue
        index += 1
    return "".join(chars)


def _source_files(base: Path, suffixes: set[str]) -> list[Path]:
    if not base.is_dir():
        return []
    return sorted(path for path in base.rglob("*")
                  if path.is_file() and path.suffix.lower() in suffixes
                  and not any(part.lower() in IGNORED_DIRS for part in path.parts))


def _mask_xml_non_elements(text: str) -> str:
    """Blank XML comments and CDATA while preserving element locations."""
    chars = list(text)
    index = 0
    for opening, closing in (("<!--", "-->"), ("<![CDATA[", "]]>") ):
        index = 0
        while True:
            start = text.find(opening, index)
            if start < 0:
                break
            end = text.find(closing, start + len(opening))
            if end < 0:
                raise LexError(f"unterminated XML {opening}")
            end += len(closing)
            _blank(chars, start, end)
            index = end
    return "".join(chars)


class Checker:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()
        self.findings: list[Finding] = []
        self.errors: list[str] = []
        self.counts = {
            "domain_files": 0,
            "collector_csharp_files": 0,
            "msbuild_files": 0,
            "workflow_files": 0,
        }
        self._scanned_files: set[Path] = set()
        self._seen_findings: set[tuple[str, int, int, str, str]] = set()

    def error(self, message: str) -> None:
        self.errors.append(message)

    def finding(self, path: Path, text: str, offset: int, rule: str,
                dependency: str, message: str) -> None:
        line, column = _position(text, offset)
        item = Finding(_relative(path, self.root), line, column, rule, dependency, message)
        key = (item.file, item.line, item.column, item.rule, item.dependency)
        if key not in self._seen_findings:
            self._seen_findings.add(key)
            self.findings.append(item)

    def read(self, path: Path) -> str | None:
        try:
            return path.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as exc:
            self.error(f"cannot read {_relative(path, self.root)} as UTF-8: {exc}")
            return None

    @staticmethod
    def forbidden_csharp_dependency(name: str) -> bool:
        name = re.sub(r"\s+", "", name).removeprefix("global::")
        if any(name == prefix or name.startswith(prefix + ".")
               for prefix in BASE_RUNTIME_NAMESPACE_PREFIXES):
            return False
        if name == DOMAIN_NAMESPACE or name.startswith(DOMAIN_NAMESPACE + "."):
            return False
        return True

    def check_csharp(self) -> None:
        collector = self.root / "src" / "Collector"
        domain = collector / "Domain"
        if not collector.is_dir():
            self.error("required scope is missing: src/Collector")
            return
        domain_files = _source_files(domain, {".cs"})
        if not domain.is_dir():
            self.error("required scope is missing: src/Collector/Domain")
        elif not domain_files:
            self.error("required scope is empty: src/Collector/Domain contains no C# files")

        all_csharp = _source_files(collector, {".cs"})
        if not all_csharp:
            self.error("required scope is empty: src/Collector contains no C# files")
            return

        namespace_pattern = re.compile(
            r"\bnamespace\s+(MentorRecorder\s*\.\s*Collector"
            r"(?:\s*\.\s*[A-Za-z_]\w*)+)"
        )
        implementation_roots: set[str] = set()
        external_prefixes: set[str] = set()
        masked_by_path: dict[Path, tuple[str, str]] = {}
        for path in all_csharp:
            text = self.read(path)
            if text is None:
                continue
            try:
                masked = "".join(mask_csharp(text))
            except LexError as exc:
                self.error(f"cannot tokenize {_relative(path, self.root)}: {exc}")
                continue
            masked_by_path[path] = (text, masked)
            self._scanned_files.add(path.resolve())
            self.counts["collector_csharp_files"] += 1
            if path in set(domain_files):
                self.counts["domain_files"] += 1
            for match in namespace_pattern.finditer(masked):
                name = re.sub(r"\s+", "", match.group(1))
                suffix = name[len(PROJECT_NAMESPACE) + 1:].split(".", 1)[0]
                if suffix != "Domain":
                    implementation_roots.add(suffix)

        using_pattern = re.compile(
            r"(?<![A-Za-z0-9_])(?P<global>global\s+)?using\s+(?P<static>static\s+)?"
            r"(?:(?P<alias>[A-Za-z_]\w*)\s*=\s*)?"
            r"(?P<name>(?:global::)?[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*;"
        )
        domain_set = set(domain_files)
        for path, (text, masked) in masked_by_path.items():
            for match in using_pattern.finditer(masked):
                is_global = bool(match.group("global"))
                name = re.sub(r"\s+", "", match.group("name"))
                canonical_name = name.removeprefix("global::")
                if path not in domain_set and not (
                    any(canonical_name == prefix or canonical_name.startswith(prefix + ".")
                        for prefix in BASE_RUNTIME_NAMESPACE_PREFIXES)
                    or canonical_name == PROJECT_NAMESPACE
                    or canonical_name.startswith(PROJECT_NAMESPACE + ".")
                ):
                    parts = canonical_name.split(".")
                    prefix_size = min(3, len(parts)) if parts[0] == "Microsoft" else 1
                    external_prefixes.add(".".join(parts[:prefix_size]))
                if path not in domain_set and not is_global:
                    continue
                if self.forbidden_csharp_dependency(name):
                    rule = "domain-global-using" if is_global else "domain-using"
                    self.finding(path, text, match.start("name"), rule, name,
                                 "Domain receives a dependency outside Domain and the base runtime")

        package_reference = re.compile(
            r"<PackageReference\b(?P<attrs>[^>]*)/?>", re.IGNORECASE
        )
        package_include = re.compile(
            r"\bInclude\s*=\s*([\"'])(?P<name>.*?)\1", re.IGNORECASE
        )
        for project in _source_files(collector, {".csproj"}):
            project_text = self.read(project)
            if project_text is None:
                continue
            try:
                inspectable_project = _mask_xml_non_elements(project_text)
            except LexError:
                continue
            for element in package_reference.finditer(inspectable_project):
                included = package_include.search(element.group("attrs"))
                if not included:
                    continue
                package_name = included.group("name").strip()
                if not package_name or "$" in package_name:
                    continue
                parts = package_name.split(".")
                prefix_size = min(3, len(parts)) if parts[0] == "Microsoft" else 1
                external_prefixes.add(".".join(parts[:prefix_size]))

        for path in domain_files:
            pair = masked_by_path.get(path)
            if pair is None:
                continue
            text, masked = pair
            qualified_source = list(masked)
            for match in using_pattern.finditer(masked):
                _blank(qualified_source, *match.span())
            qualified = "".join(qualified_source)
            for prefix in sorted(external_prefixes):
                components = prefix.split(".")
                prefix_pattern = r"\s*\.\s*".join(re.escape(part) for part in components)
                external_pattern = re.compile(
                    rf"(?<![\w.])(?:global\s*::\s*)?{prefix_pattern}"
                    rf"(?P<tail>(?:\s*\.\s*[A-Za-z_]\w*)+)"
                )
                for match in external_pattern.finditer(qualified):
                    dependency = re.sub(r"\s+", "", match.group(0))
                    self.finding(
                        path,
                        text,
                        match.start(),
                        "domain-external-qualified-reference",
                        dependency,
                        "Domain references a known external package namespace",
                    )

        for path in domain_files:
            pair = masked_by_path.get(path)
            if pair is None:
                continue
            text, masked = pair
            imports = [match.span() for match in using_pattern.finditer(masked)]
            qualified_source = list(masked)
            for start, end in imports:
                _blank(qualified_source, start, end)
            qualified = "".join(qualified_source)
            full_pattern = re.compile(
                r"(?<![\w.])(?:global\s*::\s*)?MentorRecorder\s*\.\s*"
                r"Collector\s*\.\s*(?!Domain\b)"
                r"(?P<dep>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)+)"
            )
            for match in full_pattern.finditer(qualified):
                dependency = re.sub(r"\s+", "", match.group(0))
                self.finding(path, text, match.start(), "domain-qualified-reference", dependency,
                             "Domain references a non-Domain Collector namespace")
            if implementation_roots:
                roots = "|".join(re.escape(item) for item in sorted(implementation_roots))
                relative_pattern = re.compile(
                    rf"(?<![\w.])(?:(?:Collector)\s*\.\s*)?"
                    rf"(?P<dep>(?:{roots})\s*\.\s*[A-Za-z_]\w*)"
                )
                for match in relative_pattern.finditer(qualified):
                    dependency = re.sub(r"\s+", "", match.group("dep"))
                    self.finding(path, text, match.start("dep"), "domain-qualified-reference",
                                 dependency, "Domain references a non-Domain Collector namespace")

        project_files = _source_files(collector, {".csproj"})
        for ancestor in (collector, collector.parent, self.root):
            for name in ("Directory.Build.props", "Directory.Build.targets"):
                candidate = ancestor / name
                if candidate.is_file() and candidate not in project_files:
                    project_files.append(candidate)
        csproj_files = [path for path in project_files if path.suffix.lower() == ".csproj"]
        if not csproj_files:
            self.error("required scope is missing: no Collector .csproj file was found")
        using_element = re.compile(r"<Using\b(?P<attrs>[^>]*)/?>", re.IGNORECASE)
        import_element = re.compile(r"<Import\b(?P<attrs>[^>]*)/?>", re.IGNORECASE)
        include_attr = re.compile(r"\bInclude\s*=\s*([\"'])(?P<name>.*?)\1", re.IGNORECASE)
        project_attr = re.compile(r"\bProject\s*=\s*([\"'])(?P<name>.*?)\1", re.IGNORECASE)
        pending_project_files = sorted(set(path.resolve() for path in project_files))
        inspected_project_files: set[Path] = set()
        while pending_project_files:
            path = pending_project_files.pop(0)
            if path in inspected_project_files:
                continue
            inspected_project_files.add(path)
            text = self.read(path)
            if text is None:
                continue
            self._scanned_files.add(path.resolve())
            self.counts["msbuild_files"] += 1
            try:
                ET.fromstring(text)
            except ET.ParseError as exc:
                self.error(f"cannot parse {_relative(path, self.root)} as MSBuild XML: {exc}")
                continue
            try:
                inspectable_xml = _mask_xml_non_elements(text)
            except LexError as exc:
                self.error(f"cannot inspect {_relative(path, self.root)} as MSBuild XML: {exc}")
                continue
            for element in using_element.finditer(inspectable_xml):
                include = include_attr.search(element.group("attrs"))
                if not include:
                    continue
                name = include.group("name").strip()
                if "$" in name:
                    self.error(
                        f"cannot resolve dynamic MSBuild Using in {_relative(path, self.root)} "
                        f"at line {_position(text, element.start())[0]}: {name}"
                    )
                elif self.forbidden_csharp_dependency(name):
                    self.finding(path, text, element.start("attrs") + include.start("name"),
                                 "domain-msbuild-using", name,
                                 "MSBuild injects a dependency outside Domain and the base runtime")
            for element in import_element.finditer(inspectable_xml):
                imported = project_attr.search(element.group("attrs"))
                if not imported:
                    self.error(
                        f"cannot resolve MSBuild Import without Project in "
                        f"{_relative(path, self.root)} at line {_position(text, element.start())[0]}"
                    )
                    continue
                name = imported.group("name").strip()
                if "$" in name or "*" in name or "?" in name:
                    self.error(
                        f"cannot resolve dynamic MSBuild Import in {_relative(path, self.root)} "
                        f"at line {_position(text, element.start())[0]}: {name}"
                    )
                    continue
                candidate = (path.parent / name.replace("\\", "/")).resolve()
                try:
                    candidate.relative_to(self.root)
                except ValueError:
                    self.error(
                        f"MSBuild Import leaves inspection root in {_relative(path, self.root)} "
                        f"at line {_position(text, element.start())[0]}: {name}"
                    )
                    continue
                if not candidate.is_file():
                    self.error(
                        f"MSBuild Import target is missing in {_relative(path, self.root)} "
                        f"at line {_position(text, element.start())[0]}: {name}"
                    )
                    continue
                pending_project_files.append(candidate)

    def _resolve_local_include(self, source: Path, include: str, cpp_root: Path) -> Path | None:
        candidates = [source.parent / include, cpp_root / include]
        for candidate in candidates:
            try:
                resolved = candidate.resolve()
                resolved.relative_to(self.root)
            except (OSError, ValueError):
                continue
            if resolved.is_file():
                return resolved
        return None

    def check_workflows(self) -> None:
        cpp_root = self.root / "src" / "Desktop" / "cpp"
        qml_pages = self.root / "src" / "Desktop" / "qml" / "pages"
        if not cpp_root.is_dir():
            self.error("required scope is missing: src/Desktop/cpp")
            return
        if not qml_pages.is_dir():
            self.error("required scope is missing: src/Desktop/qml/pages")
            page_types: set[str] = set()
        else:
            page_types = {path.stem for path in qml_pages.glob("*.qml") if path.is_file()}
            if not page_types:
                self.error("required scope is empty: src/Desktop/qml/pages contains no QML pages")

        queue: list[Path] = []
        for controller in ("HistoryController", "StatisticsController"):
            for suffix in (".h", ".cpp"):
                path = cpp_root / f"{controller}{suffix}"
                if not path.is_file():
                    self.error(f"required controller source is missing: {_relative(path, self.root)}")
                else:
                    queue.append(path.resolve())
        visited: set[Path] = set()
        forbidden_identifiers = FORBIDDEN_WORKFLOW_TYPES | page_types
        include_pattern = re.compile(
            r'(?m)^\s*#\s*include\s*(?P<open>["<])(?P<path>[^">\r\n]+)(?P<close>[">])'
        )

        while queue:
            path = queue.pop(0)
            if path in visited:
                continue
            visited.add(path)
            text = self.read(path)
            if text is None:
                continue
            self._scanned_files.add(path.resolve())
            self.counts["workflow_files"] += 1
            try:
                include_text = _mask_cpp(text, preserve_include_strings=True)
                code = _mask_cpp(text)
            except LexError as exc:
                self.error(f"cannot tokenize {_relative(path, self.root)}: {exc}")
                continue
            for match in include_pattern.finditer(include_text):
                include = match.group("path")
                if (match.group("open"), match.group("close")) not in {("\"", "\""), ("<", ">") }:
                    line, _ = _position(text, match.start("path"))
                    self.error(
                        f"malformed include delimiter in {_relative(path, self.root)} "
                        f"at line {line}: {include}"
                    )
                    continue
                stem = Path(include.replace("\\", "/")).stem
                if stem in forbidden_identifiers or include.lower().endswith(".qml"):
                    self.finding(path, text, match.start("path"), "workflow-forbidden-include",
                                 include, "workflow includes a forbidden controller, service or QML page")
                    continue
                resolved = self._resolve_local_include(path, include, cpp_root)
                if match.group("open") == '"' and resolved is None:
                    line, _ = _position(text, match.start("path"))
                    self.error(
                        f"quoted local include is missing in {_relative(path, self.root)} "
                        f"at line {line}: {include}"
                    )
                    continue
                if resolved and resolved.suffix.lower() in {".h", ".hh", ".hpp", ".hxx"}:
                    queue.append(resolved)
            if forbidden_identifiers:
                identifiers = "|".join(re.escape(item) for item in sorted(forbidden_identifiers))
                for match in re.finditer(rf"\b(?P<dep>{identifiers})\b", code):
                    self.finding(path, text, match.start("dep"), "workflow-forbidden-reference",
                                 match.group("dep"),
                                 "workflow references a forbidden controller, service or QML page")
        if queue or visited:
            pass
        elif not self.errors:
            self.error("required workflow scope scanned zero files")

    def run(self) -> int:
        if not self.root.is_dir():
            self.error(f"inspection root does not exist or is not a directory: {self.root}")
        else:
            self.check_csharp()
            self.check_workflows()
        self.findings.sort(key=lambda item: (item.file, item.line, item.column, item.rule))
        if self.errors:
            return EXIT_INSPECTION_ERROR
        if self.findings:
            return EXIT_VIOLATION
        return EXIT_OK

    def report(self, exit_code: int) -> dict[str, object]:
        return {
            "ok": exit_code == EXIT_OK,
            "exit_code": exit_code,
            "root": str(self.root),
            "scanned_files": len(self._scanned_files),
            "scanned_counts": self.counts,
            "violations": [asdict(item) for item in self.findings],
            "errors": self.errors,
        }


def _print_human(report: dict[str, object]) -> None:
    for item in report["violations"]:
        assert isinstance(item, dict)
        print(
            f"{item['file']}:{item['line']}:{item['column']}: "
            f"{item['rule']}: {item['dependency']} - {item['message']}"
        )
    for message in report["errors"]:
        print(f"ERROR: {message}")
    counts = report["scanned_counts"]
    print(
        f"Scanned {report['scanned_files']} source inputs "
        f"(Domain {counts['domain_files']}, Collector C# {counts['collector_csharp_files']}, "
        f"MSBuild {counts['msbuild_files']}, workflow {counts['workflow_files']})."
    )
    if report["exit_code"] == EXIT_OK:
        print("OK: architecture dependency boundaries satisfied.")
    elif report["exit_code"] == EXIT_VIOLATION:
        print(f"FAIL: {len(report['violations'])} architecture dependency violation(s).")
    else:
        print(f"FAIL: inspection was incomplete ({len(report['errors'])} error(s)).")


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check MentorRecorder source dependency boundaries.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2],
                        help="repository root (defaults to the checker repository)")
    parser.add_argument("--json", action="store_true", help="emit one JSON report")
    args = parser.parse_args(argv)
    checker = Checker(args.root)
    exit_code = checker.run()
    report = checker.report(exit_code)
    if args.json:
        json.dump(report, sys.stdout, ensure_ascii=False, indent=2)
        print()
    else:
        _print_human(report)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
