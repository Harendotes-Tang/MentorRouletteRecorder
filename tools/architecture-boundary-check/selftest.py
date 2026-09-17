#!/usr/bin/env python3
"""Positive and negative fixture tests for architecture-boundary-check."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


CHECKER = Path(__file__).with_name("check.py")


class Fixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.write("src/Collector/Collector.csproj", "<Project><ItemGroup /></Project>\n")
        self.write(
            "src/Collector/Domain/Good.cs",
            "using System;\nusing MentorRecorder.Collector.Domain.Time;\n"
            "namespace MentorRecorder.Collector.Domain;\n"
            "public sealed class Good { public string Text => \"Ipc.ErrorCodes\"; }\n",
        )
        self.write(
            "src/Collector/Ipc/ErrorCodes.cs",
            "namespace MentorRecorder.Collector.Ipc; public static class ErrorCodes {}\n",
        )
        self.write("src/Desktop/qml/pages/HistoryPage.qml", "import QtQuick\nItem {}\n")
        self.write("src/Desktop/qml/pages/DashboardPage.qml", "import QtQuick\nItem {}\n")
        self.write(
            "src/Desktop/cpp/WorkflowHelper.h",
            "#pragma once\n// class AppController;\nconst char *description = \"TtsService HistoryPage\";\n",
        )
        for controller in ("HistoryController", "StatisticsController"):
            self.write(
                f"src/Desktop/cpp/{controller}.h",
                f"#pragma once\n#include \"WorkflowHelper.h\"\nnamespace mr {{ class {controller} {{}}; }}\n",
            )
            self.write(
                f"src/Desktop/cpp/{controller}.cpp",
                f"#include \"{controller}.h\"\n// AppController must remain absent.\n",
            )

    def write(self, relative: str, content: str | bytes) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, bytes):
            path.write_bytes(content)
        else:
            path.write_text(content, encoding="utf-8")
        return path

    def run(self) -> tuple[int, dict[str, object], str]:
        result = subprocess.run(
            [sys.executable, "-B", str(CHECKER), "--root", str(self.root), "--json"],
            text=True,
            encoding="utf-8",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env={**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"},
            check=False,
        )
        try:
            report = json.loads(result.stdout)
        except json.JSONDecodeError as exc:
            raise AssertionError(f"checker did not emit JSON: {result.stdout!r} {result.stderr!r}") from exc
        return result.returncode, report, result.stderr


class BoundaryCheckerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="mr-architecture-boundary-")
        self.fixture = Fixture(Path(self.temporary.name))

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def assert_violation(self, rule: str, dependency: str, *, line: int | None = None,
                         column: int | None = None) -> dict[str, object]:
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        matches = [item for item in report["violations"]
                   if item["rule"] == rule and dependency in item["dependency"]]
        self.assertTrue(matches, report)
        item = matches[0]
        if line is not None:
            self.assertEqual(line, item["line"], item)
        if column is not None:
            self.assertEqual(column, item["column"], item)
        return item

    def test_clean_fixture_ignores_comments_strings_and_qml_macro(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/WorkflowHelper.h",
            "#pragma once\n#define QML_ELEMENT\n// CollectorProcess DashboardPage\n"
            "const char *text = \"AppController and HistoryPage\";\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))
        self.assertTrue(report["ok"])
        self.assertGreaterEqual(report["scanned_counts"]["workflow_files"], 5)

    def test_domain_ordinary_static_and_alias_imports(self) -> None:
        cases = (
            "using MentorRecorder.Collector.Ipc;",
            "using static MentorRecorder.Collector.Ipc.ErrorCodes;",
            "using Errors = MentorRecorder.Collector.Ipc.ErrorCodes;",
        )
        for statement in cases:
            with self.subTest(statement=statement):
                self.fixture.write(
                    "src/Collector/Domain/Bad.cs",
                    statement + "\nnamespace MentorRecorder.Collector.Domain;\nclass Bad {}\n",
                )
                item = self.assert_violation("domain-using", "MentorRecorder.Collector.Ipc", line=1)
                self.assertEqual(statement.index("MentorRecorder") + 1, item["column"])

    def test_domain_imports_inside_block_namespace_and_on_same_line(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "using System; using MentorRecorder.Collector.Ipc;\n"
            "namespace MentorRecorder.Collector.Domain {\n"
            "  using Alias = MentorRecorder.Collector.Ipc.ErrorCodes;\nclass Bad {}\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        imports = [item for item in report["violations"] if item["rule"] == "domain-using"]
        self.assertEqual([1, 3], sorted(item["line"] for item in imports), report)

    def test_external_global_using_injects_dependency(self) -> None:
        self.fixture.write(
            "src/Collector/GlobalUsings.cs",
            "global using Alias = MentorRecorder.Collector.Ipc.ErrorCodes;\n",
        )
        self.assert_violation("domain-global-using", "MentorRecorder.Collector.Ipc", line=1)

    def test_full_relative_and_interpolated_qualified_references(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Bad {\n"
            "  object A = MentorRecorder.Collector.Ipc.ErrorCodes.X;\n"
            "  object B = Ipc.ErrorCodes.X;\n"
            "  string C = $\"value={MentorRecorder.Collector.Ipc.ErrorCodes.X}\";\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        matches = [item for item in report["violations"]
                   if item["rule"] == "domain-qualified-reference"]
        self.assertEqual([3, 4, 5], sorted({item["line"] for item in matches}), report)

    def test_qualified_references_allow_whitespace_and_comments(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Bad {\n"
            "  object A = MentorRecorder.Collector.Ipc /* gap */ .ErrorCodes.X;\n"
            "  object B = Ipc /* gap */ . ErrorCodes.X;\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        lines = {item["line"] for item in report["violations"]
                 if item["rule"] == "domain-qualified-reference"}
        self.assertEqual({3, 4}, lines, report)

    def test_interpolation_format_text_is_not_code(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Format.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class Format { string Value = $\"{1:Ipc.ErrorCodes}\"; }\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

    def test_interpolation_alignment_expression_remains_code(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Alignment.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class Alignment { string Value = "
            "$\"{1,MentorRecorder.Collector.Ipc.ErrorCodes.Width}\"; }\n",
        )
        self.assert_violation(
            "domain-qualified-reference", "MentorRecorder.Collector.Ipc", line=2
        )

    def test_known_external_package_qualified_reference_is_forbidden(self) -> None:
        self.fixture.write(
            "src/Collector/Collector.csproj",
            '<Project><ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" '
            'Version="8.0.11" /></ItemGroup></Project>\n',
        )
        self.fixture.write(
            "src/Collector/Domain/External.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class External { Microsoft.Data.Sqlite.SqliteConnection Value = new(); }\n",
        )
        self.assert_violation(
            "domain-external-qualified-reference", "Microsoft.Data.Sqlite.SqliteConnection",
            line=2,
        )

    def test_existing_collector_import_identifies_external_namespace(self) -> None:
        self.fixture.write(
            "src/Collector/Storage/ExternalUse.cs",
            "using Microsoft.Data.Sqlite;\n"
            "namespace MentorRecorder.Collector.Storage; class ExternalUse {}\n",
        )
        self.fixture.write(
            "src/Collector/Domain/External.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class External { Microsoft.Data.Sqlite.SqliteConnection Value = new(); }\n",
        )
        self.assert_violation(
            "domain-external-qualified-reference", "Microsoft.Data.Sqlite.SqliteConnection",
            line=2,
        )

    def test_global_qualified_runtime_and_project_imports_do_not_pollute_external_prefixes(self) -> None:
        self.fixture.write(
            "src/Collector/Storage/GlobalQualifiedUsings.cs",
            "using global::System;\n"
            "using global::MentorRecorder.Collector.Domain;\n"
            "namespace MentorRecorder.Collector.Storage; class GlobalQualifiedUsings {}\n",
        )
        self.fixture.write(
            "src/Collector/Domain/RuntimeUse.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class RuntimeUse { System.DateTime Value { get; set; } }\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

    def test_msbuild_using_is_checked_at_its_position(self) -> None:
        self.fixture.write(
            "src/Collector/Collector.csproj",
            "<Project>\n  <ItemGroup>\n    <Using Include=\"MentorRecorder.Collector.Ipc\" />\n"
            "  </ItemGroup>\n</Project>\n",
        )
        self.assert_violation("domain-msbuild-using", "MentorRecorder.Collector.Ipc", line=3,
                              column=21)

    def test_msbuild_comment_is_not_a_dependency(self) -> None:
        self.fixture.write(
            "src/Collector/Collector.csproj",
            "<Project>\n  <!-- <Using Include=\"MentorRecorder.Collector.Ipc\" /> -->\n"
            "  <ItemGroup />\n</Project>\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

    def test_static_msbuild_import_is_followed(self) -> None:
        self.fixture.write(
            "src/Collector/Collector.csproj",
            '<Project><Import Project="../../Shared.props" /></Project>\n',
        )
        self.fixture.write(
            "Shared.props",
            '<Project><ItemGroup><Using Include="MentorRecorder.Collector.Ipc" />'
            '</ItemGroup></Project>\n',
        )
        self.assert_violation("domain-msbuild-using", "MentorRecorder.Collector.Ipc", line=1)

    def test_dynamic_or_missing_msbuild_import_fails_closed(self) -> None:
        for imported in ("$(SharedProps)", "../../Missing.props"):
            with self.subTest(imported=imported):
                self.fixture.write(
                    "src/Collector/Collector.csproj",
                    f'<Project><Import Project="{imported}" /></Project>\n',
                )
                code, report, _ = self.fixture.run()
                self.assertEqual(2, code, report)
                self.assertTrue(any("MSBuild Import" in error for error in report["errors"]))

    def test_direct_and_transitive_forbidden_includes(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/HistoryController.cpp",
            '#include "HistoryController.h"\n#include "AppController.h"\n',
        )
        self.assert_violation("workflow-forbidden-include", "AppController.h", line=2,
                              column=11)

        self.fixture.write("src/Desktop/cpp/HistoryController.cpp", '#include "HistoryController.h"\n')
        self.fixture.write(
            "src/Desktop/cpp/WorkflowHelper.h",
            '#pragma once\n#include "TtsService.h"\n',
        )
        self.assert_violation("workflow-forbidden-include", "TtsService.h", line=2, column=11)

    def test_local_angle_include_is_followed(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/HistoryController.cpp",
            "#include <PrivateHelper.h>\n#include \"HistoryController.h\"\n",
        )
        self.fixture.write("src/Desktop/cpp/PrivateHelper.h", "class AppController;\n")
        self.assert_violation("workflow-forbidden-reference", "AppController", line=1)

    def test_missing_quoted_include_fails_closed(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/HistoryController.cpp",
            '#include "MissingHelper.h"\n#include "HistoryController.h"\n',
        )
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("quoted local include is missing" in error
                            for error in report["errors"]))

    def test_cpp_raw_string_include_text_is_ignored(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/WorkflowHelper.h",
            'const char *text = R"tag(\n#include "AppController.h"\n)tag";\n',
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

    def test_forward_declaration_qualified_type_and_qml_page(self) -> None:
        self.fixture.write(
            "src/Desktop/cpp/WorkflowHelper.h",
            "namespace mr { class CollectorProcess; AppController *owner; }\n"
            "mr::TtsService *tts;\nHistoryPage *page;\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        dependencies = {item["dependency"] for item in report["violations"]}
        self.assertTrue({"CollectorProcess", "AppController", "TtsService", "HistoryPage"}
                        <= dependencies, report)

    def test_inline_waiver_comment_does_not_bypass_rule(self) -> None:
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "// BOUNDARY-ALLOW\nusing MentorRecorder.Collector.Ipc;\n"
            "namespace MentorRecorder.Collector.Domain;\n",
        )
        self.assert_violation("domain-using", "MentorRecorder.Collector.Ipc", line=2)

    def test_missing_and_empty_required_scopes_exit_two(self) -> None:
        self.fixture.root.joinpath("src/Collector/Domain/Good.cs").unlink()
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("scope is empty" in error for error in report["errors"]))

        self.fixture.write("src/Collector/Domain/Good.cs", "namespace MentorRecorder.Collector.Domain;\n")
        self.fixture.root.joinpath("src/Desktop/cpp/StatisticsController.h").unlink()
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("required controller source is missing" in error
                            for error in report["errors"]))

    def test_unreadable_or_unparseable_input_exits_two(self) -> None:
        self.fixture.write("src/Collector/Domain/Binary.cs", b"\xff\xfe\x00")
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("cannot read" in error for error in report["errors"]))

        self.fixture.root.joinpath("src/Collector/Domain/Binary.cs").unlink()
        self.fixture.write("src/Collector/Domain/Broken.cs", "/* unterminated")
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("cannot tokenize" in error for error in report["errors"]))

    def test_dynamic_msbuild_using_fails_closed(self) -> None:
        self.fixture.write(
            "src/Collector/Collector.csproj",
            '<Project><ItemGroup><Using Include="$(InjectedNamespace)" /></ItemGroup></Project>\n',
        )
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("cannot resolve dynamic MSBuild Using" in error
                            for error in report["errors"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
