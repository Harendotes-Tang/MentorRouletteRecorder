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
    def test_removed_profile_disappears_from_collector_and_incremental_desktop_output(self):
        cmake = os.environ.get("MR_CMAKE_EXE") or shutil.which("cmake")
        if not cmake or not shutil.which("dotnet"):
            self.skipTest("CMake and the .NET SDK are required")
        project = REPO / "src/Collector/MentorRecorder.Collector.csproj"
        stage = REPO / "src/Desktop/cmake/StageCollector.cmake"
        with tempfile.TemporaryDirectory(prefix="mr staged profiles ") as temp:
            collector, desktop = (Path(temp) / name for name in ("collector", "desktop"))
            fn = Path("protocol-profiles/cn/cn.2026.08.05.json")
            for root in (collector, desktop):
                (root / fn).parent.mkdir(parents=True)
                (root / fn).write_bytes(b"current")
                (root / fn.parent / "retired.json").write_bytes(b"obsolete")
            (collector / "MentorRecorder.Collector.dll").write_bytes(b"collector")
            preserved = desktop / "unrelated.txt"
            preserved.write_bytes(b"keep")
            subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-t:PruneObsoleteBuiltInProfiles",
                            f"-p:OutDir={collector}{os.sep}"], check=True, capture_output=True, timeout=90)
            subprocess.run([cmake, f"-DSOURCE_DIRECTORY={collector}", f"-DDEST={desktop}", "-P", str(stage)],
                           check=True, capture_output=True, timeout=30)
            for root in (collector, desktop):
                self.assertFalse((root / fn.parent / "retired.json").exists())
                self.assertEqual(b"current", (root / fn).read_bytes())
            self.assertEqual(b"keep", preserved.read_bytes())

    def test_locked_obsolete_profile_fails_collector_prune_and_desktop_staging(self):
        cmake = os.environ.get("MR_CMAKE_EXE") or shutil.which("cmake")
        pwsh = shutil.which("pwsh")
        if not (cmake and pwsh and shutil.which("dotnet")):
            self.skipTest("PowerShell, CMake and the .NET SDK are required")
        with tempfile.TemporaryDirectory(prefix="mr locked profiles ") as temp:
            collector, desktop = (Path(temp) / name for name in ("collector", "desktop"))
            for root in (collector, desktop):
                path = root / "protocol-profiles/cn/retired.json"
                path.parent.mkdir(parents=True)
                path.write_bytes(b"obsolete")
            (collector / "MentorRecorder.Collector.dll").write_bytes(b"collector")
            project = REPO / "src/Collector/MentorRecorder.Collector.csproj"
            stage = REPO / "src/Desktop/cmake/StageCollector.cmake"
            for root, invocation in (
                    (collector, f"dotnet msbuild {ps_literal(project)} -nologo -t:PruneObsoleteBuiltInProfiles "
                                f"{ps_literal('-p:OutDir=' + str(collector) + os.sep)}"),
                    (desktop, f"& {ps_literal(cmake)} {ps_literal('-DSOURCE_DIRECTORY=' + str(collector))} "
                              f"{ps_literal('-DDEST=' + str(desktop))} -P {ps_literal(stage)}")):
                obsolete = root / "protocol-profiles/cn/retired.json"
                run = run_powershell(pwsh, f"""
$lock = [IO.File]::Open({ps_literal(obsolete)}, 'Open', 'Read', 'Read')
try {{ {invocation}; $code = $LASTEXITCODE }} finally {{ $lock.Dispose() }}
exit $code
""")
                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertIn("retired.json", (run.stdout + run.stderr).decode(errors="replace"))
                self.assertTrue(obsolete.exists())
                obsolete.unlink()

    @unittest.skipUnless(os.name == "nt", "Windows short-path aliases")
    def test_short_paths_cannot_select_the_development_directory_for_distribution(self):
        import ctypes
        from ctypes import wintypes

        short_path = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
        short_path.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
        short_path.restype = wintypes.DWORD
        with tempfile.TemporaryDirectory(prefix="mr build short names ") as temporary:
            repository = Path(temporary).resolve()
            (repository / "build").mkdir()
            buffer = ctypes.create_unicode_buffer(32768)
            length = short_path(str(repository), buffer, len(buffer))
            self.assertGreater(length, 0, ctypes.get_last_error())
            self.assertLess(length, len(buffer))
            alias = Path(buffer.value)
            if str(alias).casefold() == str(repository).casefold():
                self.skipTest("The temporary volume does not provide a short-path alias")
            shell = shutil.which("pwsh")
            self.assertTrue(shell, "PowerShell 7 is required")
            script = rf"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$SourceRoot = {ps_literal(REPO)}
$results = @()
foreach ($root in @({ps_literal(repository)}, {ps_literal(alias)})) {{
    $RepoRoot = $root
    foreach ($configured in @('build', {ps_literal(repository / 'build')}, {ps_literal(alias / 'build')})) {{
        $env:MR_BUILD_DIR = $configured
        foreach ($name in @('build', 'test', 'verify', 'package')) {{
            $Distribution = [switch]$true
            $tokens = $null
            $errors = $null
            $source = [IO.File]::ReadAllText((Join-Path $SourceRoot "scripts\$name.ps1"))
            $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
            if ($errors.Count) {{ throw "Cannot parse $name.ps1" }}
            $statements = @($ast.EndBlock.Statements)
            $refusal = $null
            for ($i = 0; $i -lt $statements.Count; $i++) {{
                $statement = $statements[$i]
                if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $statement.Left.Extent.Text -in @('$configuredBuildDir', '$BuildDir')) {{
                    . ([scriptblock]::Create($statement.Extent.Text))
                    if ($statement.Left.Extent.Text -eq '$BuildDir') {{
                        if ($statements[$i + 1] -isnot [Management.Automation.Language.IfStatementAst]) {{
                            throw 'Missing distribution-directory guard'
                        }}
                        try {{ . ([scriptblock]::Create($statements[$i + 1].Extent.Text)) }}
                        catch {{ $refusal = $_.Exception.Message }}
                    }}
                }}
            }}
            $results += @{{ root=$root; configured=$configured; script=$name; refusal=$refusal }}
        }}
    }}
}}
ConvertTo-Json -InputObject $results -Compress
"""
            result = run_powershell(shell, script)
            self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
            for row in json.loads(result.stdout):
                self.assertTrue(row["refusal"], row)
                self.assertIn("MR_BUILD_DIR", row["refusal"], row)
                self.assertIn("build-distribution", row["refusal"], row)

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
