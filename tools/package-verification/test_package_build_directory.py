#!/usr/bin/env python3
"""Exercise the real build-directory assignments without building or packaging."""

from __future__ import annotations

import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows packaging paths")
class PackageBuildDirectoryTests(unittest.TestCase):
    def test_package_selects_the_directory_built_and_tested(self):
        shells = [path for name in ("powershell", "pwsh") if (path := shutil.which(name))]
        self.assertTrue(shells, "PowerShell is required to verify packaging paths")
        for shell in shells:
            with self.subTest(shell=shell):
                script = rf"""
$ErrorActionPreference = 'Stop'
trap {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$RepoRoot = {ps_literal(REPO)}
$results = @()
foreach ($configured in @('', '  ', 'isolated build\新目录', '..\sibling build', 'C:\isolated build\新目录')) {{
    $env:MR_BUILD_DIR = $configured
    $row = @{{ configured = $configured }}
    foreach ($name in @('build', 'test', 'verify', 'package')) {{
        $tokens = $null
        $errors = $null
        $source = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts\$name.ps1"), [Text.Encoding]::UTF8)
        $ast = [Management.Automation.Language.Parser]::ParseInput(
            $source, [ref]$tokens, [ref]$errors)
        if ($errors.Count) {{ throw "Cannot parse $name.ps1" }}
        # Evaluate only the actual top-level path assignments, in script order.
        # No package commands, cleanup, or build process can execute here.
        $BuildDir = $null
        $DesktopExe = $null
        $DesktopManifest = $null
        foreach ($statement in $ast.EndBlock.Statements) {{
            if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
                $statement.Left.Extent.Text -in @('$configuredBuildDir', '$BuildDir', '$DesktopExe', '$DesktopManifest')) {{
                . ([scriptblock]::Create($statement.Extent.Text))
            }}
        }}
        $row[$name] = $BuildDir
        if ($name -eq 'package') {{
            $row['executable'] = $DesktopExe
            $row['manifest'] = $DesktopManifest
        }}
    }}
    $results += $row
}}
ConvertTo-Json -InputObject $results -Compress
"""
                encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
                run = subprocess.run(
                    [shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-EncodedCommand", encoded],
                    capture_output=True, timeout=45, check=False)
                self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
                for row in json.loads(run.stdout):
                    configured = row["configured"]
                    expected = Path(configured) if configured.strip() else REPO / "build"
                    if not expected.is_absolute():
                        expected = REPO / expected
                    expected = expected.resolve()
                    for name in ("build", "test", "verify", "package"):
                        self.assertEqual(expected, Path(row[name]), (configured, name))
                    self.assertEqual(expected / "src/Desktop/MentorRecorder.Desktop.exe",
                                     Path(row["executable"]))
                    self.assertEqual(expected / "src/Desktop/MentorRecorder.Desktop.exe.manifest",
                                     Path(row["manifest"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
