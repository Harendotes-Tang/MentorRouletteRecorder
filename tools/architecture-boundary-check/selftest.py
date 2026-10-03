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

    def test_root_namespace_types_are_non_domain_dependencies(self) -> None:
        # MentorRecorder.Collector encloses Domain's own namespace, so its types need neither a
        # using nor a qualifier in Domain code (audit 2026-10-03, ON2-9).
        self.fixture.write(
            "src/Collector/CommandLineOptions.cs",
            "namespace MentorRecorder.Collector;\n"
            "public enum CollectorMode { Serve }\n"
            "public sealed record CommandLineOptions(string Db);\n",
        )
        self.fixture.write(
            "src/Collector/Program.cs",
            "namespace MentorRecorder.Collector\n{\n"
            "    public static class Program { internal enum PipePresence { Absent } }\n}\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Bad {\n"
            "  CommandLineOptions? Options;\n"
            "  object Mode = CollectorMode.Serve;\n"
            "  MentorRecorder.Collector.CommandLineOptions? Fully;\n"
            "  object Relative = Collector.CollectorMode.Serve;\n"
            "  string Name = nameof(Program);\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        found = {(item["line"], item["dependency"]) for item in report["violations"]
                 if item["rule"] == "domain-root-namespace-reference"}
        self.assertEqual(
            {(3, "CommandLineOptions"), (4, "CollectorMode"),
             (5, "MentorRecorder.Collector.CommandLineOptions"),
             (6, "Collector.CollectorMode"), (7, "Program")},
            found,
            report,
        )

    def test_global_and_parent_namespace_types_are_non_domain_dependencies(self) -> None:
        for relative, source in (
            ("src/Collector/Legacy.cs", "public static class Shared { }\n"),
            ("src/Collector/Parent.cs", "namespace MentorRecorder { public static class Shared { } }\n"),
        ):
            with self.subTest(relative=relative):
                self.fixture.write(relative, source)
                self.fixture.write(
                    "src/Collector/Domain/Bad.cs",
                    "namespace MentorRecorder.Collector.Domain;\nclass Bad { object A = Shared.X; }\n",
                )
                self.assert_violation("domain-root-namespace-reference", "Shared", line=2)
                self.fixture.root.joinpath(relative).unlink()

    def test_nested_and_shadowed_root_names_are_not_dependencies(self) -> None:
        # A type nested in a root type is reachable only through it, and a Domain type of the same
        # name as a root type shadows it inside Domain.
        self.fixture.write(
            "src/Collector/Program.cs",
            "namespace MentorRecorder.Collector;\n"
            "public static class Program { private sealed class Helper { } }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Program.cs",
            "namespace MentorRecorder.Collector.Domain;\ninternal static class Program { }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Uses.cs",
            "namespace MentorRecorder.Collector.Domain;\n"
            "class Uses { string Name = nameof(Program); object Helper = null; }\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

    def test_a_nested_domain_type_does_not_shadow_a_root_type(self) -> None:
        # Only a namespace-level Domain type shadows an enclosing one; a type nested in a Domain
        # class is reachable only through that class (audit 2026-10-03, R2T-8).
        self.fixture.write(
            "src/Collector/Options.cs",
            "namespace MentorRecorder.Collector;\npublic sealed class Options { }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Holder.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Holder { class Options { } }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Uses.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Uses { Options? Value; }\n",
        )
        self.assert_violation("domain-root-namespace-reference", "Options", line=2)

    def test_a_domain_type_shadows_only_in_its_own_namespace_and_below(self) -> None:
        self.fixture.write(
            "src/Collector/Options.cs",
            "namespace MentorRecorder.Collector;\npublic sealed class Options { }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Time/Options.cs",
            "namespace MentorRecorder.Collector.Domain.Time;\npublic sealed class Options { }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Time/Below/Uses.cs",
            "namespace MentorRecorder.Collector.Domain.Time.Below;\nclass Below { Options? Value; }\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

        self.fixture.write(
            "src/Collector/Domain/Events/Uses.cs",
            "namespace MentorRecorder.Collector.Domain.Events;\nclass Sibling { Options? Value; }\n",
        )
        item = self.assert_violation("domain-root-namespace-reference", "Options", line=2)
        self.assertEqual("src/Collector/Domain/Events/Uses.cs", item["file"])

    def test_root_namespace_delegates_are_non_domain_dependencies(self) -> None:
        # A namespace-level delegate is a type like any other (R2T-9).
        self.fixture.write(
            "src/Collector/Transports.cs",
            "namespace MentorRecorder.Collector;\n"
            "public delegate System.Threading.Tasks.Task<int> FetchTransport(System.Uri uri);\n"
            "public delegate void Generic<T>(T value);\n"
            "public delegate (int A, int B) Pair();\n"
            "public static class Holder { public static System.Func<int, int> F = delegate (int x) "
            "{ return x; }; }\n",
        )
        self.fixture.write(
            "src/Collector/Domain/Bad.cs",
            "namespace MentorRecorder.Collector.Domain;\nclass Bad {\n"
            "  FetchTransport? A;\n  Generic<int>? B;\n  Pair? C;\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(1, code, (report, stderr))
        found = {(item["line"], item["dependency"]) for item in report["violations"]
                 if item["rule"] == "domain-root-namespace-reference"}
        self.assertEqual({(3, "FetchTransport"), (4, "Generic"), (5, "Pair")}, found, report)

    def test_a_type_declared_in_a_domain_namespace_outside_domain_is_reported(self) -> None:
        # Domain code names such a type with neither a using nor a qualifier (R2T-10).
        for source in (
            "namespace MentorRecorder.Collector.Domain;\npublic static class Sneaky { }\n",
            "namespace MentorRecorder.Collector.Domain.Time\n{\n    public static class Sneaky { }\n}\n",
            "namespace MentorRecorder.Collector\n{\n    namespace Domain { public static class Sneaky { } }\n}\n",
        ):
            with self.subTest(source=source):
                self.fixture.write("src/Collector/Capture/Sneaky.cs", source)
                item = self.assert_violation("domain-namespace-outside-domain", "Sneaky")
                self.assertEqual("src/Collector/Capture/Sneaky.cs", item["file"])

    def test_a_root_attribute_named_by_its_short_name_is_a_dependency(self) -> None:
        # [RootMarker] names RootMarkerAttribute (R2T-11).
        self.fixture.write(
            "src/Collector/RootMarkerAttribute.cs",
            "namespace MentorRecorder.Collector;\n"
            "public sealed class RootMarkerAttribute : System.Attribute { }\n",
        )
        for usage in ("[RootMarker] class Bad { }", "[System.Serializable, RootMarker()] class Bad { }",
                      "class Bad { [return: RootMarker] int M() => 0; }"):
            with self.subTest(usage=usage):
                self.fixture.write(
                    "src/Collector/Domain/Bad.cs",
                    "namespace MentorRecorder.Collector.Domain;\n" + usage + "\n",
                )
                self.assert_violation("domain-root-namespace-reference", "RootMarker", line=2)

    def test_braces_split_by_conditional_compilation_fail_closed(self) -> None:
        # A namespace or type opened differently in two #if branches cannot be followed (R2T-11).
        self.fixture.write(
            "src/Collector/Split.cs",
            "#if FEATURE\nnamespace MentorRecorder.Collector.Capture {\n#else\n"
            "namespace MentorRecorder.Collector {\n#endif\n    public static class Shared { }\n}\n",
        )
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("Split.cs" in error and "brace" in error for error in report["errors"]),
                        report)

    def test_unbalanced_braces_fail_closed(self) -> None:
        self.fixture.write(
            "src/Collector/Unbalanced.cs",
            "namespace MentorRecorder.Collector\n{\n    public static class Shared { }\n",
        )
        code, report, _ = self.fixture.run()
        self.assertEqual(2, code, report)
        self.assertTrue(any("Unbalanced.cs" in error for error in report["errors"]), report)

    def test_balanced_conditional_compilation_is_followed(self) -> None:
        self.fixture.write(
            "src/Collector/Conditional.cs",
            "namespace MentorRecorder.Collector;\npublic static class Conditional\n{\n"
            "#if DEBUG\n    public static void Trace() { }\n#else\n    public static void Trace() { }\n"
            "#endif\n}\n",
        )
        code, report, stderr = self.fixture.run()
        self.assertEqual(0, code, (report, stderr))

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
