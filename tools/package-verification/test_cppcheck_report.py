#!/usr/bin/env python3
"""Reject incomplete analyzer evidence using the real PowerShell report reader."""

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
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows C++ analyzer gate")
class CppcheckReportTests(unittest.TestCase):
    def test_real_reader_accepts_complete_reports_and_rejects_missing_or_ambiguous_evidence(self):
        cases = [
            ('<results version="2"><cppcheck version="2.21.0"/><errors/></results>', True, 0),
            ('<results version="2"><cppcheck version="2.21.0"/><errors>'
             '<error id="nullPointer" severity="error" msg="Null dereference">'
             '<location file="src/Desktop/cpp/Example.cpp" line="12"/></error></errors></results>', True, 1),
            ('<results version="2"><cppcheck version="2.21.0"/><errors>'
             '<error id="internalError" severity="error" msg="Analysis failed internally"/></errors></results>', False, None),
            ('<results version="2"><cppcheck version="2.21.0"/><errors>'
             '<error id="nullPointer" severity="error" msg="Unlocated failure"/></errors></results>', False, None),
            ('<not-a-report/>', False, None),
            ('<results version="2"><errors/></results>', False, None),
            ('<results version="2"><cppcheck version="2.21.0"/></results>', False, None),
            ('<results version="1"><cppcheck version="2.21.0"/><errors/></results>', False, None),
            ('<results version="2"><cppcheck version="2.21.0"/><errors/><errors/></results>', False, None),
            ('<results version="2"><cppcheck version="2.21.0"/><errors><unknown/></errors></results>', False, None),
            ('<results version="2"><cppcheck version="2.21.0"/><errors>'
             '<error id="bad" severity="unknown" msg="ambiguous"/></errors></results>', False, None),
            ('<!DOCTYPE results [<!ENTITY x "unexpected">]><results version="2">'
             '<cppcheck version="2.21.0"/><errors>&x;</errors></results>', False, None),
        ]
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell)
        with tempfile.TemporaryDirectory(prefix="mr analyzer report ") as directory:
            paths = []
            for index, (text, _, _) in enumerate(cases):
                path = Path(directory) / f"report-{index}.xml"
                path.write_text(text, encoding="utf-8")
                paths.append(path)
            script = Path(directory) / "reader.ps1"
            script.write_text(
                "$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version Latest\n"
                "$tokens=$null; $errors=$null\n"
                "$ast=[Management.Automation.Language.Parser]::ParseFile("
                f"{ps_literal(REPO / 'scripts/static-analysis.ps1')},[ref]$tokens,[ref]$errors)\n"
                "if ($errors.Count) { throw 'Invalid analyzer script' }\n"
                "$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] "
                "-and $n.Name -eq 'Read-CppcheckReport'},$true)\n"
                ". ([scriptblock]::Create($node.Extent.Text))\n"
                "$results=@()\n"
                f"foreach ($path in @({','.join(ps_literal(path) for path in paths)})) {{\n"
                "  try { $report=Read-CppcheckReport $path; "
                "$results+=@{accepted=$true;findings=$report.SelectNodes('/results/errors/error').Count} }\n"
                "  catch { $results+=@{accepted=$false;findings=$null} }\n}\n"
                "$results | ConvertTo-Json -Compress\n",
                encoding="utf-8-sig",
            )
            run = subprocess.run([shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                                 capture_output=True, timeout=30, check=False)
            self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
            reports = json.loads(run.stdout)
            self.assertEqual(len(cases), len(reports))
            for case, report in zip(cases, reports):
                with self.subTest(xml=case[0]):
                    self.assertEqual(case[1], report["accepted"])
                    self.assertEqual(case[2], report["findings"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
