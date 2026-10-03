#!/usr/bin/env python3
"""Run the real summary block of scripts/verify.ps1 without running any of its gates.

The summary decides whether a run may print ``PUBLIC_DISTRIBUTION_READY = true``. That
line is a claim about a *complete* run: every gate, the unfiltered test suite, and a fresh
build. The test parses verify.ps1, takes the top-level statements from the summary heading
to the end of the script, and evaluates only those against chosen inputs - no build, no
test, no probe process runs. The same technique as test_package_build_directory.py.
"""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
VERIFY = REPO / "scripts" / "verify.ps1"
READY_LINE = "PUBLIC_DISTRIBUTION_READY = true"


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows verification script")
class VerifySummaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.shell = shutil.which("pwsh")
        if not cls.shell:
            raise unittest.SkipTest("PowerShell 7 (pwsh) is required")

    def run_summary(self, *, no_build=False, test_filter="", skipped=(), failures=()):
        script = f"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$source = [IO.File]::ReadAllText({ps_literal(VERIFY)}, [Text.Encoding]::UTF8)
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) {{ throw 'Cannot parse verify.ps1' }}
$statements = @($ast.EndBlock.Statements)
$start = -1
for ($i = 0; $i -lt $statements.Count; $i++) {{
    if ($statements[$i].Extent.Text -match '^Write-Head\\s+''结论') {{ $start = $i; break }}
}}
if ($start -lt 0) {{ throw 'The summary block of verify.ps1 was not found' }}
function Write-Head([string]$Text) {{ Write-Host "== $Text" }}
$failures = New-Object System.Collections.Generic.List[string]
foreach ($name in @({", ".join(ps_literal(f) for f in failures)})) {{ $failures.Add($name) }}
$skipped = New-Object System.Collections.Generic.List[string]
foreach ($name in @({", ".join(ps_literal(s) for s in skipped)})) {{ $skipped.Add($name) }}
$TestFilter = {ps_literal(test_filter)}
$NoBuild = [switch]${"true" if no_build else "false"}
$summary = ($statements[$start..($statements.Count - 1)] | ForEach-Object {{ $_.Extent.Text }}) -join "`n"
. ([scriptblock]::Create($summary))
"""
        with tempfile.TemporaryDirectory(prefix="mr verify summary ") as directory:
            path = Path(directory) / "summary.ps1"
            path.write_text(script, encoding="utf-8-sig")
            completed = subprocess.run(
                [self.shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                 "-File", str(path)],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        return completed

    def test_a_complete_run_states_the_distribution_claim(self):
        # Guards the harness itself: without it the assertions below could pass because
        # nothing was printed at all.
        completed = self.run_summary()
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn(READY_LINE, completed.stdout)

    def test_a_run_without_a_fresh_build_is_partial(self):
        # -NoBuild tests whatever binaries are already on disk, which may predate the
        # source being verified; it did not make the observation the claim rests on.
        completed = self.run_summary(no_build=True)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertNotIn(READY_LINE, completed.stdout)
        self.assertIn("-NoBuild", completed.stdout)

    def test_skipped_gates_and_filters_stay_partial(self):
        for arguments in ({"skipped": ("listener-check",)}, {"test_filter": "Category!=Soak"}):
            with self.subTest(**arguments):
                completed = self.run_summary(**arguments)
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertNotIn(READY_LINE, completed.stdout)

    def test_a_failed_run_exits_non_zero(self):
        completed = self.run_summary(failures=("build-and-test",))
        self.assertEqual(1, completed.returncode, completed.stdout)
        self.assertNotIn(READY_LINE, completed.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
