#!/usr/bin/env python3
"""Run the package child-process helper with hostile developer search paths, offline."""

from __future__ import annotations

import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows package verification")
class PackageEnvironmentTests(unittest.TestCase):
    def test_child_search_paths_are_isolated_and_parent_environment_is_unchanged(self):
        shells = [path for name in ("powershell", "pwsh") if (path := shutil.which(name))]
        self.assertTrue(shells, "a PowerShell runtime is required to test Windows packaging")
        for shell in shells:
            with self.subTest(shell=shell), tempfile.TemporaryDirectory(prefix="mr package args ") as directory:
                child = Path(directory) / "inspect environment.py"
                child.write_text(
                    "import json, os, sys\n"
                    "print(json.dumps({'arguments': sys.argv[1:], 'environment': dict(os.environ)}))\n",
                    encoding="utf-8")
                script = rf"""
$ErrorActionPreference = 'Stop'
trap {{ [Console]::Error.WriteLine($_.Exception.Message + "`n" + $_.ScriptStackTrace); exit 1 }}
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
. {ps_literal(REPO / 'scripts/package-runtime.ps1')}
$env:PATH = 'C:\developer\toolchain;C:\developer\qt'
$env:QT_PLUGIN_PATH = 'C:\developer\plugins'
$env:QT_QPA_PLATFORM_PLUGIN_PATH = 'C:\developer\platforms'
$env:QML_IMPORT_PATH = 'C:\developer\qml'
$env:QML2_IMPORT_PATH = 'C:\developer\qml2'
$env:QT_QPA_PLATFORM = 'developer-platform'
$env:QTDIR = 'C:\developer\qt'
$env:MR_PACKAGE_SENTINEL = 'preserved'
$parentBefore = [Environment]::GetEnvironmentVariables('Process')
$results = @()
foreach ($offscreen in @($false, $true)) {{
    $info = New-IsolatedPackageStartInfo -Executable {ps_literal(sys.executable)} `
        -WorkingDirectory {ps_literal(directory)} -Offscreen:$offscreen `
        -Arguments @({ps_literal(child)},
            'two words', 'C:\trailing slash\', 'embedded"quote', 'unicode-导随')
    $process = [Diagnostics.Process]::Start($info)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) {{ $process.Kill(); throw 'child timed out' }}
    if ($process.ExitCode -ne 0) {{ throw $errTask.GetAwaiter().GetResult() }}
    $results += $outTask.GetAwaiter().GetResult() | ConvertFrom-Json
    $process.Dispose()
}}
$parentAfter = [Environment]::GetEnvironmentVariables('Process')
$same = $parentBefore.Count -eq $parentAfter.Count
foreach ($name in $parentBefore.Keys) {{
    if ($parentBefore[$name] -cne $parentAfter[$name]) {{ $same = $false }}
}}
[ordered]@{{ parentUnchanged = $same; children = $results; expectedPath = @(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::System),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) -join ';' }} |
    ConvertTo-Json -Depth 8 -Compress
"""
                encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
                run = subprocess.run(
                    [shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-EncodedCommand", encoded],
                    capture_output=True, timeout=45, check=False)
                self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
                result = json.loads(run.stdout)
                self.assertTrue(result["parentUnchanged"])
                for index, report in enumerate(result["children"]):
                    self.assertEqual(
                        ["two words", "C:\\trailing slash\\", 'embedded"quote', "unicode-导随"],
                        report["arguments"])
                    environment = {name.upper(): value for name, value in report["environment"].items()}
                    self.assertEqual(result["expectedPath"], environment["PATH"])
                    self.assertEqual("preserved", environment["MR_PACKAGE_SENTINEL"])
                    search_variables = {name for name in environment
                                        if name.startswith(("QT_", "QML_", "QML2_")) or name == "QTDIR"}
                    self.assertEqual({"QT_QPA_PLATFORM"} if index else set(), search_variables)
                    if index:
                        self.assertEqual("offscreen", environment["QT_QPA_PLATFORM"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
