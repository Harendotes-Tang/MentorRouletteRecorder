#!/usr/bin/env python3
"""Run scripts/run-python-tool-tests.ps1 against a throwaway tools/ tree (audit 2026-10-20, N2-6).

The runner is the gate for every Python self-test under tools/. Two things it used to hide:
a missing package from tools/requirements-ci.txt surfaced only as one test script's "exit code
1" deep in the log, and a test class skipped for want of a tool or package still ended in
"all passed". The runner is copied into a temporary repository layout (it finds tools/ next to
its own scripts/ directory) with a requirements file and a sample test of our choosing.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest


REPO = Path(__file__).resolve().parents[2]
RUNNER = REPO / "scripts" / "run-python-tool-tests.ps1"

PASSING_SAMPLE = """
import unittest

class Sample(unittest.TestCase):
    def test_one(self):
        print('SAMPLE RAN')

    def test_two(self):
        pass

if __name__ == '__main__':
    unittest.main()
"""

SKIPPING_SAMPLE = """
import unittest

class Sample(unittest.TestCase):
    def test_runs(self):
        pass

    @unittest.skip('needs a tool this machine does not have')
    def test_skipped_one(self):
        pass

    @unittest.skip('needs a tool this machine does not have')
    def test_skipped_two(self):
        pass

if __name__ == '__main__':
    unittest.main()
"""

FAILING_SAMPLE = """
import unittest

class Sample(unittest.TestCase):
    def test_fails(self):
        self.fail('deliberate')

if __name__ == '__main__':
    unittest.main()
"""


@unittest.skipUnless(os.name == "nt", "Windows verification script")
class PythonToolRunnerTests(unittest.TestCase):
    def setUp(self):
        shell = shutil.which("pwsh")
        if shell is None:
            self.fail("PowerShell 7 (pwsh) is required to test the tool-test runner")
        self.shell = shell
        self.temporary = tempfile.TemporaryDirectory(prefix="mr tool runner ")
        self.root = Path(self.temporary.name)
        (self.root / "scripts").mkdir()
        shutil.copy2(RUNNER, self.root / "scripts" / RUNNER.name)
        self.site = self.root / "site"
        self.site.mkdir()
        (self.site / "fake_ok.py").write_text("VALUE = 1\n", encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def run_runner(self, requirements: str, sample: str, *, structured=False):
        tools = self.root / "tools"
        (tools / "sample").mkdir(parents=True, exist_ok=True)
        (tools / "requirements-ci.txt").write_text(textwrap.dedent(requirements), encoding="utf-8")
        (tools / "sample" / "test_sample.py").write_text(sample, encoding="utf-8")
        environment = {**os.environ, "MR_PYTHON_EXE": sys.executable, "PYTHONPATH": str(self.site)}
        script = self.root / "scripts" / RUNNER.name
        if structured:
            script = self.root / "invoke.ps1"
            script.write_text(
                "$result = 'stale result'\n"
                "& (Join-Path $PSScriptRoot 'scripts/run-python-tool-tests.ps1') -Result ([ref]$result)\n"
                "$runnerExit = $LASTEXITCODE\n"
                "$result | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'result.json') -Encoding utf8\n"
                "exit $runnerExit\n", encoding="utf-8-sig")
        completed = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-File", str(script)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=environment, timeout=180)
        return completed, completed.stdout + completed.stderr

    def test_a_clean_run_passes_and_reports_no_skip(self):
        # Guards the harness: the assertions below could otherwise pass on a runner that
        # never got as far as the sample.
        completed, output = self.run_runner("# comment\nfake-ok==1.0  # trailing comment\n\n", PASSING_SAMPLE)
        self.assertEqual(0, completed.returncode, output)
        self.assertIn("SAMPLE RAN", output)
        self.assertIn("工具自测全部通过（1 个脚本）", output)
        self.assertNotIn("跳过", output)

    def test_a_missing_requirement_fails_before_any_test_runs(self):
        completed, output = self.run_runner("fake-ok==1.0\nmr-surely-missing-package==1.0\n", PASSING_SAMPLE)
        self.assertEqual(1, completed.returncode, output)
        self.assertNotIn("SAMPLE RAN", output)
        self.assertIn("缺少 mr-surely-missing-package（import mr_surely_missing_package", output)
        self.assertNotIn("缺少 fake-ok", output)
        self.assertIn("pip install -r tools/requirements-ci.txt", output)
        self.assertIn("MR_PYTHON_EXE", output)

    def test_opencv_is_imported_by_its_module_name(self):
        (self.site / "cv2.py").write_text("VALUE = 1\n", encoding="utf-8")
        completed, output = self.run_runner("opencv-python-headless==5.0.0.93\n", PASSING_SAMPLE)
        self.assertEqual(0, completed.returncode, output)
        self.assertIn("SAMPLE RAN", output)

    def test_skipped_tests_are_listed_in_the_summary(self):
        completed, output = self.run_runner("fake-ok==1.0\n", SKIPPING_SAMPLE)
        self.assertEqual(0, completed.returncode, output)
        self.assertNotIn("全部通过", output)
        summary = output[output.rindex("以下脚本有测试被跳过"):]
        self.assertIn(os.path.join("tools", "sample", "test_sample.py") + "：跳过 2 项", summary)
        self.assertIn("2 项测试被跳过", summary)

    def test_skip_result_records_exact_count_and_preserves_success_exit(self):
        completed, output = self.run_runner("fake-ok==1.0\n", SKIPPING_SAMPLE, structured=True)
        self.assertEqual(0, completed.returncode, output)
        result = json.loads((self.root / "result.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(dict(Succeeded=True, Complete=False, ScriptCount=1,
                              FailedScriptCount=0, SkippedTestCount=2), result)

    def test_preflight_failure_clears_any_previous_complete_result(self):
        completed, output = self.run_runner("mr-surely-missing-package==1.0\n", PASSING_SAMPLE, structured=True)
        self.assertEqual(1, completed.returncode, output)
        result = json.loads((self.root / "result.json").read_text(encoding="utf-8-sig"))
        self.assertIs(False, result["Succeeded"])
        self.assertIs(False, result["Complete"])
        self.assertEqual(0, result["ScriptCount"])

    def test_a_failing_script_still_fails_the_run(self):
        completed, output = self.run_runner("fake-ok==1.0\n", FAILING_SAMPLE)
        self.assertEqual(1, completed.returncode, output)
        self.assertIn("工具自测失败 1/1", output)


if __name__ == "__main__":
    unittest.main()
