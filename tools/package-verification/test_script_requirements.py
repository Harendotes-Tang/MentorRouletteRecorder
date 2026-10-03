#!/usr/bin/env python3
"""Every script under scripts/ must refuse Windows PowerShell 5.1 by name.

The scripts are UTF-8 without a byte-order mark and full of Chinese text. Windows
PowerShell 5.1 reads such a file in the ANSI code page - GBK on a Chinese Windows - and a
GBK lead byte swallows the next ASCII character: a quote or a brace disappears and the
script either fails to parse or, worse, parses into something else and runs. PowerShell 7
reads them as UTF-8. The scripts therefore declare ``#Requires -Version 7.0``, which 5.1
checks before it runs a single statement, so the refusal names the real requirement
instead of a garbled parse error.
"""

from __future__ import annotations

import os
from pathlib import Path
import re
import shutil
import subprocess
import unittest


REPO = Path(__file__).resolve().parents[2]
SCRIPTS = sorted((REPO / "scripts").glob("*.ps1"))
REQUIREMENT = re.compile(r"#Requires\s+-Version\s+(\S+)", re.IGNORECASE)


class ScriptRequirementTests(unittest.TestCase):
    def test_every_script_requires_powershell_7(self):
        self.assertTrue(SCRIPTS, "scripts/*.ps1 not found")
        for script in SCRIPTS:
            with self.subTest(script=script.name):
                first_line = script.read_text(encoding="utf-8-sig").splitlines()[0]
                match = REQUIREMENT.fullmatch(first_line.strip())
                self.assertIsNotNone(match, f"{script.name} must start with #Requires -Version 7.0")
                self.assertEqual("7.0", match.group(1))

    @unittest.skipUnless(os.name == "nt" and shutil.which("powershell"),
                         "Windows PowerShell 5.1 is not available")
    def test_windows_powershell_refuses_before_running_anything(self):
        # Run the way a person would (-File). package-version.ps1 only defines functions, so
        # running it is harmless even if the refusal did not happen.
        helpers = REPO / "scripts" / "package-version.ps1"
        completed = subprocess.run(
            [shutil.which("powershell"), "-NoProfile", "-NonInteractive", "-ExecutionPolicy",
             "Bypass", "-File", str(helpers)],
            capture_output=True, timeout=120)
        output = (completed.stdout + completed.stderr).decode("utf-8", errors="replace")
        self.assertNotEqual(0, completed.returncode, output)
        self.assertIn("ScriptRequiresUnmatchedPSVersion", output)


if __name__ == "__main__":
    unittest.main(verbosity=2)
