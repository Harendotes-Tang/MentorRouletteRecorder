#!/usr/bin/env python3
"""Exercise the real build-directory assignments without building or packaging."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def run_powershell(shell, script):
    # A script file, not -EncodedCommand: endpoint protection commonly stalls or kills an
    # encoded command, which fails this test for a reason that has nothing to do with the
    # scripts under test. The BOM lets Windows PowerShell 5.1 read the text as UTF-8.
    with tempfile.TemporaryDirectory(prefix="mr package script ") as directory:
        path = Path(directory) / "probe.ps1"
        path.write_text(script, encoding="utf-8-sig")
        return subprocess.run(
            [shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-File", str(path)],
            capture_output=True, timeout=45, check=False)


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
                run = run_powershell(shell, script)
                self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
                for row in json.loads(run.stdout):
                    configured = row["configured"]
                    expected = Path(configured) if configured.strip() else REPO / "build"
                    if not expected.is_absolute():
                        expected = REPO / expected
                    expected = expected.resolve()
                    if not configured.strip():
                        # J-3 = M-2 (audit 2026-11-03): package.ps1 builds the distribution flavour
                        # in a directory of its own and hands it to verify.ps1 and build.ps1, so the
                        # developers' build\ never holds a cached MR_DEV_BUILD=OFF.
                        expected_package = (REPO / "build-distribution").resolve()
                    else:
                        expected_package = expected
                    for name in ("build", "test", "verify"):
                        self.assertEqual(expected, Path(row[name]), (configured, name))
                    self.assertEqual(expected_package, Path(row["package"]), (configured, "package"))
                    expected = expected_package
                    self.assertEqual(expected / "src/Desktop/MentorRecorder.Desktop.exe",
                                     Path(row["executable"]))
                    self.assertEqual(expected / "src/Desktop/MentorRecorder.Desktop.exe.manifest",
                                     Path(row["manifest"]))

    def test_the_distribution_flavour_never_builds_in_the_developers_directory(self):
        # RV5-2 (re-review of J-3 = M-2): build.ps1 -Distribution, and verify.ps1 / test.ps1 that
        # forward it, used to configure the developers' build\ OFF just as package.ps1 did. With
        # -Distribution an unset MR_BUILD_DIR means build-distribution\ in every script, and a
        # directory that resolves to <repo>\build is refused; package.ps1 refuses it in any case.
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
foreach ($flavour in @($false, $true)) {{
    foreach ($configured in @('', 'isolated build', 'build', '.\BUILD\')) {{
        $env:MR_BUILD_DIR = $configured
        $row = @{{ configured = $configured; distribution = $flavour }}
        foreach ($name in @('build', 'test', 'verify', 'package')) {{
            $tokens = $null
            $errors = $null
            $source = [IO.File]::ReadAllText((Join-Path $RepoRoot "scripts\$name.ps1"), [Text.Encoding]::UTF8)
            $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
            if ($errors.Count) {{ throw "Cannot parse $name.ps1" }}
            # Only the directory assignments and the refusal right after them run; nothing builds.
            $Distribution = [switch]$flavour
            $BuildDir = $null
            $outcome = $null
            $statements = @($ast.EndBlock.Statements)
            for ($i = 0; $i -lt $statements.Count; $i++) {{
                $statement = $statements[$i]
                if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $statement.Left.Extent.Text -in @('$configuredBuildDir', '$BuildDir')) {{
                    . ([scriptblock]::Create($statement.Extent.Text))
                    if ($statement.Left.Extent.Text -eq '$BuildDir' -and
                        $statements[$i + 1] -is [Management.Automation.Language.IfStatementAst]) {{
                        try {{ . ([scriptblock]::Create($statements[$i + 1].Extent.Text)) }}
                        catch {{ $outcome = 'refused: ' + $_.Exception.Message }}
                    }}
                }}
            }}
            $row[$name] = if ($outcome) {{ $outcome }} else {{ [string]$BuildDir }}
        }}
        $results += $row
    }}
}}
ConvertTo-Json -InputObject $results -Compress
"""
                run = run_powershell(shell, script)
                self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
                for row in json.loads(run.stdout):
                    configured, distribution = row["configured"], row["distribution"]
                    developers = configured.strip().strip(".\\").lower() == "build"
                    for name in ("build", "test", "verify", "package"):
                        outcome = row[name]
                        context = (configured, distribution, name)
                        if developers and (distribution or name == "package"):
                            self.assertTrue(outcome.startswith("refused: "), context)
                            self.assertIn("MR_BUILD_DIR", outcome, context)
                            self.assertIn("build-distribution", outcome, context)
                            continue
                        if configured:
                            expected = REPO / configured
                        elif distribution or name == "package":
                            expected = REPO / "build-distribution"
                        else:
                            expected = REPO / "build"
                        self.assertEqual(expected.resolve(), Path(outcome).resolve(), context)


if __name__ == "__main__":
    unittest.main(verbosity=2)
