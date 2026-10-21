#!/usr/bin/env python3
"""Run the actual analyzer process helpers against completion and stalled children."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_package_runtime_budget import assert_process_exited

REPO = Path(__file__).resolve().parents[2]


def ps_literal(value: object) -> str:
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows analyzer process helpers")
class StaticAnalysisBudgetTests(unittest.TestCase):
    def probe(self, source: str, *, expire_before_wait: bool = False) -> dict:
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell)
        with tempfile.TemporaryDirectory(prefix="mr analyzer budget ") as directory:
            child = Path(directory) / "analyzer.py"
            child.write_text(source, encoding="utf-8")
            log = Path(directory) / "analyzer.log"
            script = Path(directory) / "probe.ps1"
            script.write_text(
                "$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version Latest\n"
                f". {ps_literal(REPO / 'scripts/package-runtime.ps1')}\n"
                "$tokens = $null; $errors = $null\n"
                "$ast = [Management.Automation.Language.Parser]::ParseFile("
                f"{ps_literal(REPO / 'scripts/static-analysis.ps1')}, [ref]$tokens, [ref]$errors)\n"
                "if ($errors.Count) { throw 'Cannot parse static-analysis.ps1' }\n"
                "foreach ($name in @('Start-Tool', 'Wait-Tool')) {\n"
                "  $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] "
                "-and $n.Name -eq $name }, $true)\n"
                "  if (-not $node) { throw 'Analyzer helper not found' }\n"
                "  . ([scriptblock]::Create($node.Extent.Text))\n}\n"
                f"$RepoRoot = {ps_literal(directory)}\n"
                "$Utf8NoBom = [Text.UTF8Encoding]::new($false)\n"
                "$ToolTimeoutSeconds = 1\n"
                f"$handle = Start-Tool {ps_literal(sys.executable)} @({ps_literal(child)}) {ps_literal(log)}\n"
                + ("Start-Sleep -Milliseconds 1200\n" if expire_before_wait else "")
                + "$code = Wait-Tool $handle\n"
                + f"[ordered]@{{ code=$code; out=[IO.File]::ReadAllText({ps_literal(log)}); "
                + f"err=[IO.File]::ReadAllText({ps_literal(log.with_suffix('.err'))}) }} | ConvertTo-Json -Compress\n",
                encoding="utf-8-sig",
            )
            run = subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                capture_output=True, timeout=20, check=False,
            )
            self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
            return json.loads(run.stdout)

    def test_actual_nonzero_analyzer_exit_is_preserved_with_diagnostics(self):
        report = self.probe("import sys\nprint('finding')\nprint('error', file=sys.stderr)\nsys.exit(9)\n")
        self.assertEqual(9, report["code"])
        self.assertEqual("finding", report["out"].strip())
        self.assertEqual("error", report["err"].strip())

    def test_timeout_budget_starts_when_the_analyzer_starts(self):
        report = self.probe("import time\nprint('started', flush=True)\ntime.sleep(60)\n", expire_before_wait=True)
        self.assertEqual(124, report["code"])
        self.assertEqual("started", report["out"].strip())

    def test_exited_analyzer_cannot_leave_inherited_pipe_descendant(self):
        report = self.probe(
            "import json, os, subprocess, sys\n"
            "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])\n"
            "print(json.dumps({'parent': os.getpid(), 'descendant': child.pid}), flush=True)\n"
        )
        self.assertEqual(124, report["code"])
        for pid in json.loads(report["out"]).values():
            assert_process_exited(self, pid)

    def test_disposing_one_concurrent_job_keeps_the_other_job_intact(self):
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell)
        with tempfile.TemporaryDirectory(prefix="mr analyzer concurrent ") as directory:
            root = Path(directory)
            pid_file = root / "owned.json"
            child = root / "owned.py"
            child.write_text(
                "import json, os, pathlib, subprocess, sys, time\n"
                "descendant = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])\n"
                "pathlib.Path(sys.argv[1]).write_text(json.dumps([os.getpid(), descendant.pid]))\n"
                "time.sleep(30)\n", encoding="utf-8",
            )
            script = root / "probe.ps1"
            script.write_text(
                "$ErrorActionPreference='Stop'\n"
                f". {ps_literal(REPO / 'scripts/package-runtime.ps1')}\n"
                "Add-Type @'\n"
                "using System; using System.Runtime.InteropServices;\n"
                "public static class OwnedFixtureWait {\n"
                " [DllImport(\"kernel32.dll\",SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);\n"  # Test-only signature for the exact spawned fixture wait.
                " [DllImport(\"kernel32.dll\")] public static extern uint WaitForSingleObject(IntPtr process,uint timeout);\n"
                " [DllImport(\"kernel32.dll\")] public static extern bool CloseHandle(IntPtr handle);\n"
                "}\n'@\n"
                f"$aInfo=New-IsolatedPackageStartInfo -Executable {ps_literal(sys.executable)} "
                f"-WorkingDirectory {ps_literal(root)} -Arguments @({ps_literal(child)},{ps_literal(pid_file)})\n"
                "$a=Start-PackageVerificationProcess -StartInfo $aInfo -TimeoutSeconds 30\n"
                "$b=$null\n$ownedHandles=@()\ntry {\n"
                "  $ready=[Diagnostics.Stopwatch]::StartNew()\n  $ownedIds=$null\n"
                "  while ($null -eq $ownedIds) {\n"
                "    if ($a.IsCompleted) { $early=$a.Wait(); throw ('Fixture exited before marker: '+$early.Stderr) }\n"
                f"    if ([IO.File]::Exists({ps_literal(pid_file)})) {{\n"
                f"      try {{ $ownedIds=[IO.File]::ReadAllText({ps_literal(pid_file)}) | ConvertFrom-Json }} catch {{ }}\n    }}\n"
                "    if ($ready.Elapsed.TotalSeconds -gt 5) { throw 'Fixture did not produce a complete marker' }\n"
                "    Start-Sleep -Milliseconds 25\n  }\n"
                "  foreach ($ownedId in $ownedIds) {\n"
                "    $owned=[OwnedFixtureWait]::OpenProcess(0x100000,$false,$ownedId)\n"  # SYNCHRONIZE-only handle for this fixture's emitted PID, retained to avoid PID reuse.
                "    if ($owned -eq [IntPtr]::Zero) { throw 'Cannot retain exact owned fixture handle' }\n"
                "    $ownedHandles += $owned\n  }\n"
                f"  $bInfo=New-IsolatedPackageStartInfo -Executable {ps_literal(sys.executable)} "
                f"-WorkingDirectory {ps_literal(root)} -Arguments @('-c','print(\"second job survived\")')\n"
                "  $b=Start-PackageVerificationProcess -StartInfo $bInfo -TimeoutSeconds 5\n"
                "  $a.Dispose()\n"
                "  foreach ($owned in $ownedHandles) {\n"
                "    if ([OwnedFixtureWait]::WaitForSingleObject($owned,0) -ne 0) {\n"
                "      throw 'Dispose returned before its owned process terminated'\n    }\n  }\n"
                "  $b.Wait() | ConvertTo-Json -Compress\n"
                "} finally {\n"
                "  foreach ($owned in $ownedHandles) { [void][OwnedFixtureWait]::CloseHandle($owned) }\n"
                "  $a.Dispose(); if ($null -ne $b) { $b.Dispose() }\n}\n",
                encoding="utf-8-sig",
            )
            run = subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                capture_output=True, timeout=20, check=False,
            )
            self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
            result = json.loads(run.stdout)
            self.assertFalse(result["TimedOut"])
            self.assertEqual(0, result["ExitCode"])
            self.assertEqual("second job survived", result["Stdout"].strip())
            for pid in json.loads(pid_file.read_text(encoding="utf-8")):
                assert_process_exited(self, pid)


if __name__ == "__main__":
    unittest.main(verbosity=2)
