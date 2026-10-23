#!/usr/bin/env python3
"""Exercise the analyzer's actual compile-unit selection without starting analyzers."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value: object) -> str:
    """Quote one literal for the generated PowerShell fixture."""
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows analyzer source selection")
class StaticAnalysisSelectionTests(unittest.TestCase):
    def run_selection(self, entries: list[dict[str, object]],
                      fragments: tuple[str, ...] = ()) -> list[dict[str, object]]:
        """Execute the production helper definitions and foreach selector from its AST."""
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell, "PowerShell 7 is required to verify analyzer selection")
        with tempfile.TemporaryDirectory(prefix="mr analyzer selection ") as directory:
            root = Path(directory)
            database = root / "compile_commands.json"
            database.write_text(json.dumps(entries), encoding="utf-8")
            script = root / "selection.ps1"
            script.write_text(
                "$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n"
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n"
                "$tokens=$null; $errors=$null\n"
                "$ast=[Management.Automation.Language.Parser]::ParseFile("
                f"{ps_literal(REPO / 'scripts/static-analysis.ps1')},[ref]$tokens,[ref]$errors)\n"
                "if ($errors.Count) { throw 'Invalid analyzer script' }\n"
                "foreach ($name in @('Convert-ToRepoRelative','Split-CommandLine')) {\n"
                "  $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] "
                "-and $n.Name -eq $name},$true)\n"
                "  if (-not $node) { throw ('Missing helper: '+$name) }\n"
                "  . ([scriptblock]::Create($node.Extent.Text))\n}\n"
                "$selectors=@($ast.FindAll({param($n) "
                "$n -is [Management.Automation.Language.ForEachStatementAst] "
                "-and $n.Variable.Extent.Text -eq '$entry' "
                "-and $n.Condition.Extent.Text -eq '$entries'},$true))\n"
                "if ($selectors.Count -ne 1) { throw 'Compile-unit selector not found uniquely' }\n"
                f"$RepoRoot={ps_literal(REPO)}\n"
                f"$entries=Get-Content -LiteralPath {ps_literal(database)} -Raw -Encoding UTF8 | ConvertFrom-Json\n"
                f"$Path=@({','.join(ps_literal(fragment) for fragment in fragments)})\n"
                "$units=@()\n. ([scriptblock]::Create($selectors[0].Extent.Text))\n"
                "ConvertTo-Json -InputObject @($units) -Depth 5 -Compress\n",
                encoding="utf-8-sig",
            )
            result = subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                capture_output=True, text=True, encoding="utf-8", errors="replace",
                timeout=30, check=False,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            selected = json.loads(result.stdout)
            self.assertIsInstance(selected, list)
            return selected

    def test_recursive_production_sources_keep_the_test_and_generated_boundaries(self) -> None:
        included = (
            "src/Desktop/cpp/AppController.cpp",
            "src/Desktop/cpp/transport/IpcFraming.cpp",
            "src/Desktop/cpp/workflows/RunCompletionCoordinator.cpp",
            "src/Desktop/cpp/future/nested/Helper.cpp",
            "tests/Desktop.Tests/workflows/RunCompletionCoordinatorTests.cpp",
            "tests/Desktop.Tests/support/QmlRuntimeTestRegistration.cpp",
        )
        excluded = (
            "tests/Desktop.Tests/workflows/RunCompletionCoordinatorTests.h",
            "tests/Desktop.TestsExtra/Test.cpp",
            "src/Desktop/cppExtra/Helper.cpp",
            "src/Desktop/cpp/transport/IpcFraming.h",
            "src/Desktop/qml/Generated.cpp",
            "build/src/Desktop/cpp/Generated.cpp",
            "src/Desktop/.rcc/qrc_resources.cpp",
        )
        entries = [{"file": str(REPO / path), "arguments": ["c++", "-c", path],
                    "directory": str(REPO)} for path in included + excluded]
        entries.append({"file": str(REPO.parent / (REPO.name + "-other") /
                                     "src/Desktop/cpp/Outside.cpp"),
                        "arguments": ["c++"], "directory": str(REPO.parent)})

        selected = self.run_selection(entries)

        self.assertEqual(list(included), [unit["Relative"] for unit in selected])
        self.assertEqual([entry["arguments"] for entry in entries[:len(included)]],
                         [unit["Arguments"] for unit in selected])
        self.assertEqual([str(REPO)] * len(included),
                         [unit["Directory"] for unit in selected])

    def test_path_fragments_remain_case_insensitive_and_select_either_fragment(self) -> None:
        paths = (
            "src/Desktop/cpp/AppController.cpp",
            "src/Desktop/cpp/transport/IpcFraming.cpp",
            "src/Desktop/cpp/workflows/RunCompletionCoordinator.cpp",
            "tests/Desktop.Tests/IpcFramingTests.cpp",
            "tests/Desktop.Tests/transport/IpcRequestTests.cpp",
        )
        entries = [{"file": path, "arguments": ["c++"], "directory": str(REPO)}
                   for path in paths]

        selected = self.run_selection(entries, ("TRANSPORT/", "runcompletion"))

        self.assertEqual(list(paths[1:3]) + [paths[4]], [unit["Relative"] for unit in selected])
        self.assertEqual([], self.run_selection(entries, ("no matching source",)))

    def test_nested_source_preserves_command_line_arguments(self) -> None:
        selected = self.run_selection([{
            "file": "src\\Desktop\\cpp\\transport\\IpcClient.cpp",
            "command": '"C:/tool chain/c++.exe" -I"include dir" -c transport/IpcClient.cpp',
            "directory": str(REPO / "build"),
        }])

        self.assertEqual([{
            "Relative": "src/Desktop/cpp/transport/IpcClient.cpp",
            "Arguments": ["C:/tool chain/c++.exe", "-Iinclude dir", "-c", "transport/IpcClient.cpp"],
            "Directory": str(REPO / "build"),
        }], selected)


if __name__ == "__main__":
    unittest.main(verbosity=2)
