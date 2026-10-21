"""Exercise the real test-report readers with valid and adversarial reports."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "test.ps1"
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def ps_literal(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def report_bytes(outcomes: list[str], *, overrides: dict[str, str] | None = None,
                 omit: str | None = None, duplicate_execution: bool = False) -> bytes:
    """Create VSTest's report shape, including its legacy notExecuted=0 behavior."""
    ET.register_namespace("", TRX_NAMESPACE)

    def child(parent: ET.Element, tag: str, attributes: dict[str, str]) -> ET.Element:
        return ET.SubElement(parent, "{" + TRX_NAMESPACE + "}" + tag, attributes)

    root = ET.Element("{" + TRX_NAMESPACE + "}TestRun")
    results = child(root, "Results", {})
    for index, outcome in enumerate(outcomes):
        child(results, "UnitTestResult", {
            "executionId": "same" if duplicate_execution else str(index),
            "testId": str(index), "outcome": outcome,
        })
    summary = child(root, "ResultSummary", {"outcome": "Completed"})
    counters = {
        "total": str(len(outcomes)), "executed": str(len(outcomes) - outcomes.count("NotExecuted")),
        "passed": str(outcomes.count("Passed")), "failed": str(outcomes.count("Failed")),
        "error": str(outcomes.count("Error")), "timeout": str(outcomes.count("Timeout")),
        "aborted": str(outcomes.count("Aborted")), "notExecuted": "0",
    }
    counters.update(overrides or {})
    if omit is not None:
        counters.pop(omit)
    child(summary, "Counters", counters)
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


@unittest.skipUnless(os.name == "nt", "Windows verification script")
class TestResultIntegrityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.shell = shutil.which("pwsh")
        if cls.shell is None:
            raise unittest.SkipTest("PowerShell 7 (pwsh) is required")

    def run_reader(self, function: str, argument: str | Path) -> subprocess.CompletedProcess[str]:
        code = f"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$source = [IO.File]::ReadAllText({ps_literal(SCRIPT)}, [Text.Encoding]::UTF8)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) {{ throw 'Cannot parse test.ps1' }}
$functions = @($ast.FindAll({{ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in @('Read-TrxCounters', 'Read-RequiredQtTestTotals')
}}, $true))
if ($functions.Count -ne 2) {{ throw 'Missing report reader functions' }}
foreach ($definition in $functions) {{ . ([scriptblock]::Create($definition.Extent.Text)) }}
& {function} {ps_literal(argument)} | ConvertTo-Json -Compress
"""
        with tempfile.TemporaryDirectory(prefix="mr result reader ") as directory:
            path = Path(directory) / "reader.ps1"
            path.write_text(code, encoding="utf-8-sig")
            return subprocess.run(
                [self.shell, "-NoProfile", "-NonInteractive", "-File", str(path)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
            )

    def read_trx(self, content: bytes) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory(prefix="mr trx fixture ") as directory:
            path = Path(directory) / "result.trx"
            path.write_bytes(content)
            return self.run_reader("Read-TrxCounters", path)

    def test_completed_results_and_explicit_environment_skips_are_counted(self) -> None:
        result = self.read_trx(report_bytes(["Passed", "Passed", "NotExecuted"]))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"Total": 3, "Executed": 2, "Passed": 2, "Failed": 0,
                          "Errors": 0, "Timeout": 0, "Aborted": 0, "NotRun": 0},
                         json.loads(result.stdout))

    def test_all_skipped_or_empty_reports_are_not_execution(self) -> None:
        for outcomes in ([], ["NotExecuted"], ["NotExecuted", "NotExecuted"]):
            with self.subTest(outcomes=outcomes):
                result = self.read_trx(report_bytes(outcomes))
                self.assertNotEqual(0, result.returncode, result.stdout)

    def test_each_required_counter_must_be_present(self) -> None:
        for name in ("total", "executed", "passed", "failed", "error", "timeout", "aborted", "notExecuted"):
            with self.subTest(counter=name):
                result = self.read_trx(report_bytes(["Passed"], omit=name))
                self.assertNotEqual(0, result.returncode, result.stdout)

    def test_counters_must_match_the_actual_result_rows(self) -> None:
        for override in ({"total": "2"}, {"executed": "2"}, {"passed": "0"},
                         {"failed": "1"}, {"passed": "-1"}, {"passed": "invalid"},
                         {"notExecuted": "1"}):
            with self.subTest(override=override):
                result = self.read_trx(report_bytes(["Passed"], overrides=override))
                self.assertNotEqual(0, result.returncode, result.stdout)

    def test_duplicate_execution_and_unknown_outcomes_fail_closed(self) -> None:
        for content in (report_bytes(["Passed", "Passed"], duplicate_execution=True),
                        report_bytes(["Passed", "Inconclusive"]), b"<broken"):
            with self.subTest(content=content):
                result = self.read_trx(content)
                self.assertNotEqual(0, result.returncode, result.stdout)

    def test_qt_completion_requires_actual_positive_footer(self) -> None:
        result = self.run_reader("Read-RequiredQtTestTotals", "Totals: 42 passed, 0 failed, 0 skipped, 123ms")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"Passed": 42, "Failed": 0, "Skipped": 0}, json.loads(result.stdout))

    def test_qt_absent_skipped_failed_or_ambiguous_completion_is_rejected(self) -> None:
        for output in ("", "Collector.exe exists", "Totals: 0 passed, 0 failed, 1 skipped",
                       "Totals: 1 passed, 1 failed, 0 skipped", "Totals: 2 passed, 0 failed, 1 skipped",
                       "Totals: 1 passed, 0 failed, 0 skipped\nTotals: 2 passed, 0 failed, 0 skipped"):
            with self.subTest(output=output):
                result = self.run_reader("Read-RequiredQtTestTotals", output)
                self.assertNotEqual(0, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
