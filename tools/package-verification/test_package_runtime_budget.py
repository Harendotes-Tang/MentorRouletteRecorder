#!/usr/bin/env python3
"""Exercise package probe output, exit status and timeout cleanup with real children."""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value: object) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def assert_process_exited(testcase: unittest.TestCase, pid: int):
    # Query only the exact PID emitted by our fixture, retaining the returned
    # handle while waiting. Production cleanup uses job handles, not PID scans.
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]  # ctypes signature for the exact owned fixture PID wait below.
    kernel.OpenProcess.restype = wintypes.HANDLE  # ctypes return type for the exact owned fixture PID wait below.
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.OpenProcess(0x100000, False, pid)  # SYNCHRONIZE-only wait on a PID emitted by this test's own fixture, never game discovery.
    if not handle:
        testcase.assertEqual(87, ctypes.get_last_error(), "Cannot confirm owned fixture exit")
        return
    try:
        testcase.assertEqual(0, kernel.WaitForSingleObject(handle, 0),
                             f"Owned fixture process {pid} is still running")
    finally:
        kernel.CloseHandle(handle)


@unittest.skipUnless(os.name == "nt", "Windows package verification")
class PackageRuntimeBudgetTests(unittest.TestCase):
    def assert_process_exited(self, pid: int):
        assert_process_exited(self, pid)

    def probe(self, source: str, timeout: int = 10) -> dict:
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell, "PowerShell 7 is required for package verification")
        with tempfile.TemporaryDirectory(prefix="mr package timeout ") as directory:
            child = Path(directory) / "child.py"
            child.write_text(source, encoding="utf-8")
            script = Path(directory) / "probe.ps1"
            script.write_text(
                "$ErrorActionPreference = 'Stop'\n"
                f". {ps_literal(REPO / 'scripts/package-runtime.ps1')}\n"
                f"$info = New-IsolatedPackageStartInfo -Executable {ps_literal(sys.executable)} "
                f"-WorkingDirectory {ps_literal(directory)} -Arguments @({ps_literal(child)})\n"
                f"$result = Invoke-PackageVerificationProcess -StartInfo $info -TimeoutSeconds {timeout}\n"
                "$result | ConvertTo-Json -Compress\n",
                encoding="utf-8-sig",
            )
            run = subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                capture_output=True, timeout=timeout + 20, check=False,
            )
            self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
            return json.loads(run.stdout)

    def test_preserves_nonzero_exit_and_both_diagnostic_streams(self):
        result = self.probe(
            "import sys\nprint('out')\nprint('err', file=sys.stderr)\nsys.exit(7)\n"
        )
        self.assertFalse(result["TimedOut"])
        self.assertEqual(7, result["ExitCode"])
        self.assertEqual("out", result["Stdout"].strip())
        self.assertEqual("err", result["Stderr"].strip())

    def test_large_output_cannot_deadlock_the_probe(self):
        result = self.probe(
            "import sys\nsys.stdout.write('o' * 250000)\n"
            "sys.stderr.write('e' * 250000)\n"
        )
        self.assertFalse(result["TimedOut"])
        self.assertEqual(0, result["ExitCode"])
        self.assertEqual("o" * 250000, result["Stdout"])
        self.assertEqual("e" * 250000, result["Stderr"])

    def test_hung_probe_is_terminated_and_not_reported_as_success(self):
        started = time.monotonic()
        result = self.probe(
            "import os, time\nprint(os.getpid(), flush=True)\ntime.sleep(60)\n",
            timeout=1,
        )
        self.assertTrue(result["TimedOut"])
        self.assertNotEqual(0, result["ExitCode"])
        self.assertLess(time.monotonic() - started, 15)
        # Verify the exact child, never terminate arbitrary recorder processes.
        pid = int(result["Stdout"].strip())
        self.assert_process_exited(pid)

    def test_exited_parent_cannot_leave_inherited_pipe_descendant_alive(self):
        started = time.monotonic()
        result = self.probe(
            "import json, os, subprocess, sys\n"
            "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])\n"
            "print(json.dumps({'parent': os.getpid(), 'descendant': child.pid}), flush=True)\n",
            timeout=1,
        )
        # Parent exited 0, but the complete probe exceeded its lifecycle budget.
        self.assertTrue(result["TimedOut"])
        self.assertEqual(0, result["ExitCode"])
        self.assertLess(time.monotonic() - started, 15)
        for pid in json.loads(result["Stdout"]).values():
            self.assert_process_exited(pid)

    def test_pipe_free_descendant_is_owned_without_killing_unrelated_process(self):
        unrelated = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        try:
            result = self.probe(
                "import json, os, subprocess, sys\n"
                "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'], "
                "stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n"
                "print(json.dumps({'parent': os.getpid(), 'descendant': child.pid}), flush=True)\n",
                timeout=1,
            )
            self.assertTrue(result["TimedOut"])
            self.assertEqual(0, result["ExitCode"])
            for pid in json.loads(result["Stdout"]).values():
                self.assert_process_exited(pid)
            self.assertIsNone(unrelated.poll(), "Probe cleanup killed an unrelated process")
        finally:
            unrelated.kill()
            unrelated.wait(timeout=10)

    def test_finite_descendant_can_complete_after_parent_within_shared_budget(self):
        result = self.probe(
            "import subprocess, sys\n"
            "subprocess.Popen([sys.executable, '-c', "
            "'import time; time.sleep(0.2); print(\"child finished\", flush=True)'])\n"
        )
        self.assertFalse(result["TimedOut"])
        self.assertEqual(0, result["ExitCode"])
        self.assertEqual("child finished", result["Stdout"].strip())

    def test_json_dispatcher_round_trips_arguments_and_isolates_diagnostic_streams(self):
        shell = shutil.which("pwsh")
        self.assertIsNotNone(shell)
        expected = ["中文 spaces", 'quote"end', "trailing\\", "", "a'$(literal)"]
        request = {
            "executable": sys.executable,
            "arguments": [
                "-c", "import json,sys;sys.stdout.reconfigure(encoding='utf-8');"
                "sys.stderr.reconfigure(encoding='utf-8');"
                "print(json.dumps(sys.argv[1:],ensure_ascii=False));"
                "print('err✓',file=sys.stderr);sys.exit(7)", *expected,
            ],
            "working_directory": str(REPO), "timeout_seconds": 10,
        }
        run = subprocess.run(
            [shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
             str(REPO / "scripts/run-bounded-process.ps1")],
            input=json.dumps(request, ensure_ascii=False).encode("utf-8"),
            capture_output=True, timeout=25, check=False,
        )
        self.assertEqual(0, run.returncode, run.stderr.decode(errors="replace"))
        report = json.loads(run.stdout)
        self.assertEqual(7, report["ExitCode"])
        self.assertFalse(report["TimedOut"])
        self.assertEqual(expected, json.loads(report["Stdout"]))
        self.assertEqual("err✓", report["Stderr"].strip())


if __name__ == "__main__":
    unittest.main(verbosity=2)
