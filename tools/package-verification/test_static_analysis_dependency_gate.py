#!/usr/bin/env python3
"""Exercise the real clang diagnostic parser and exit policy for dependency paths."""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value: object) -> str:
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows analyzer gate")
class StaticAnalysisDependencyGateTests(unittest.TestCase):
    def run_gate(self, diagnostic: str, *, strict: bool = False) -> subprocess.CompletedProcess:
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell)
        with tempfile.TemporaryDirectory(prefix="mr dependency gate ") as directory:
            root = Path(directory)
            logs = root / "logs"
            logs.mkdir()
            (logs / "unit.log").write_text(diagnostic, encoding="utf-8")
            script = root / "gate.ps1"
            script.write_text(
                "$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n"
                "$tokens=$null; $errors=$null\n"
                "$ast=[Management.Automation.Language.Parser]::ParseFile("
                f"{ps_literal(REPO / 'scripts/static-analysis.ps1')},[ref]$tokens,[ref]$errors)\n"
                "if ($errors.Count) { throw 'Invalid analyzer script' }\n"
                "foreach ($name in @('Convert-ToRepoRelative','Add-Finding','Format-PathLabel','Write-Head')) {\n"
                "  $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] "
                "-and $n.Name -eq $name},$true)\n"
                "  if (-not $node) { throw ('Missing helper: '+$name) }\n"
                "  . ([scriptblock]::Create($node.Extent.Text))\n}\n"
                f"$RepoRoot={ps_literal(REPO)}\n$OutputRoot={ps_literal(root)}\n"
                f"$tidyDir={ps_literal(logs)}\n$outputLabel='fixture'\n"
                "$Utf8NoBom=[Text.UTF8Encoding]::new($false)\n"
                "$findings=[Collections.Generic.List[object]]::new()\n$toolFailures=@()\n"
                f"$Strict=${str(strict).lower()}\n"
                "$patternNode=$ast.Find({param($n) "
                "$n -is [Management.Automation.Language.AssignmentStatementAst] "
                "-and $n.Left.Extent.Text -eq '$pattern'},$true)\n"
                "$parser=$ast.Find({param($n) "
                "$n -is [Management.Automation.Language.ForEachStatementAst] "
                "-and $n.Variable.Extent.Text -eq '$log'},$true)\n"
                "if (-not $patternNode -or -not $parser) { throw 'Diagnostic parser not found' }\n"
                ". ([scriptblock]::Create($patternNode.Extent.Text))\n"
                ". ([scriptblock]::Create($parser.Extent.Text))\n"
                "$statements=@($ast.EndBlock.Statements)\n$start=-1\n"
                "for ($i=0; $i -lt $statements.Count; $i++) {\n"
                "  if ($statements[$i].Extent.Text.StartsWith(\"Write-Head '汇总\")) { $start=$i; break }\n}\n"
                "if ($start -lt 0) { throw 'Exit policy not found' }\n"
                "$summary=($statements[$start..($statements.Count-1)] | "
                "ForEach-Object {$_.Extent.Text}) -join \"`n\"\n"
                ". ([scriptblock]::Create($summary))\n",
                encoding="utf-8-sig",
            )
            return subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
                check=False,
            )

    def test_dependency_analyzer_warning_blocks_without_strict(self):
        result = self.run_gate(
            "D:/Qt/include/QtCore/helper.h:141:50: warning: Use of memory after it is freed "
            "[clang-analyzer-cplusplus.NewDelete]\n"
        )
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("clang-analyzer-cplusplus.NewDelete", result.stdout)
        self.assertIn("D:/Qt/include/QtCore/helper.h:141", result.stdout)

    def test_dependency_compiler_error_blocks_without_strict(self):
        result = self.run_gate(
            "D:/Qt/include/QtCore/helper.h:20:2: error: Invalid declaration [clang-diagnostic-error]\n"
        )
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_dependency_style_warning_blocks_in_strict_mode(self):
        result = self.run_gate(
            "D:/Qt/include/QtCore/helper.h:10:3: warning: Unnecessary copy [performance-unnecessary-copy-initialization]\n",
            strict=True,
        )
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_non_strict_style_policy_still_reports_without_blocking(self):
        result = self.run_gate(
            "D:/Qt/include/QtCore/helper.h:10:3: warning: Unnecessary copy [performance-unnecessary-copy-initialization]\n"
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("performance-unnecessary-copy-initialization", result.stdout)

    def test_repository_analyzer_warning_still_blocks(self):
        source = (REPO / "src/Desktop/cpp/Example.cpp").as_posix()
        result = self.run_gate(
            f"{source}:12:3: warning: Use of memory after it is freed [clang-analyzer-cplusplus.NewDelete]\n"
        )
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_empty_completed_diagnostics_pass_in_strict_mode(self):
        result = self.run_gate("", strict=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("static analysis passed", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
