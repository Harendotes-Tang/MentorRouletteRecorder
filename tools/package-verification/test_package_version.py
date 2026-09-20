#!/usr/bin/env python3
"""Self-test for the pure version helpers scripts/package.ps1 packages a release with.

`scripts/package-version.ps1` is dot-sourced by `scripts/package.ps1`; it holds the
functions that decide what a version *is* (three numbers plus an optional prerelease
label), what the artifacts are called, what `BUILD-METADATA.json` records, and which
CHANGELOG heading has to sit on top. Those rules decide whether a test build can be told
apart from a release, and neither `dotnet test` nor `ctest` reaches PowerShell, so they
are exercised here - the same way tools/package-verification/test_package_environment.py
exercises scripts/package-runtime.ps1.

Discovered and run by scripts/run-python-tool-tests.ps1, which verify.ps1 calls.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
HELPERS = REPO / "scripts" / "package-version.ps1"


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def find_shell():
    for name in ("pwsh", "powershell"):
        found = shutil.which(name)
        if found:
            return found
    return None


@unittest.skipUnless(os.name == "nt", "Windows packaging helpers")
class PackageVersionHelperTests(unittest.TestCase):
    """Drives the helpers through one PowerShell process and reads back JSON."""

    @classmethod
    def setUpClass(cls):
        cls.shell = find_shell()
        if not cls.shell:
            raise unittest.SkipTest("a PowerShell runtime is required")

    def run_helpers(self, body):
        script = f"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {{ [Console]::Error.WriteLine($_.Exception.Message + "`n" + $_.ScriptStackTrace); exit 1 }}
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
. {ps_literal(HELPERS)}
{body}
"""
        # errors="replace": a refusal is written by the shell itself, in the console code
        # page (GBK on a Chinese Windows), and a decoding failure would hide the message
        # this assertion is supposed to show.
        completed = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    # ------------------------------------------------------------------ props

    def write_props(self, directory, prefix, suffix):
        element = f"    <VersionSuffix>{suffix}</VersionSuffix>\n" if suffix is not None else ""
        path = Path(directory) / "Directory.Build.props"
        path.write_text(
            "<Project>\n  <PropertyGroup>\n"
            f"    <VersionPrefix>{prefix}</VersionPrefix>\n{element}"
            "  </PropertyGroup>\n</Project>\n",
            encoding="utf-8")
        return path

    def read_props(self, prefix, suffix):
        with tempfile.TemporaryDirectory(prefix="mr props ") as directory:
            path = self.write_props(directory, prefix, suffix)
            return self.run_helpers(
                f"Read-ProductVersion -PropsPath {ps_literal(path)} | ConvertTo-Json -Compress")

    def test_an_empty_suffix_is_a_release(self):
        for suffix in (None, "", "   "):
            with self.subTest(suffix=suffix):
                version = self.read_props("1.4.0", suffix)
                self.assertEqual("1.4.0", version["Full"])
                self.assertEqual("1.4.0", version["Prefix"])
                self.assertEqual("", version["Suffix"])
                self.assertFalse(version["Prerelease"])

    def test_a_suffix_makes_a_prerelease(self):
        version = self.read_props("1.4.0", "beta.1")
        self.assertEqual("1.4.0-beta.1", version["Full"])
        self.assertEqual("1.4.0", version["Prefix"])
        self.assertEqual("beta.1", version["Suffix"])
        self.assertTrue(version["Prerelease"])

    def test_a_malformed_version_is_refused(self):
        with tempfile.TemporaryDirectory(prefix="mr props ") as directory:
            for prefix, suffix in (("1.4", None), ("1.4.0.1", None), ("v1.4.0", None),
                                   ("1.4.0", "beta 1"), ("1.4.0", "-beta.1")):
                with self.subTest(prefix=prefix, suffix=suffix):
                    path = self.write_props(directory, prefix, suffix)
                    completed = subprocess.run(
                        [self.shell, "-NoProfile", "-NonInteractive", "-Command",
                         f". {ps_literal(HELPERS)}; "
                         f"Read-ProductVersion -PropsPath {ps_literal(path)}"],
                        capture_output=True, text=True, encoding="utf-8",
                        errors="replace", timeout=120)
                    self.assertNotEqual(0, completed.returncode, completed.stdout)

    # ------------------------------------------------------ version shapes

    def test_the_numeric_part_is_what_a_version_resource_can_hold(self):
        observed = self.run_helpers(
            "@('1.4.0', '1.4.0-beta.1', '1.4.0.0', ' 1.4.0.0 ', '1.4.0-beta.1+2f3a', 'x', '') |"
            " ForEach-Object { Get-NormalizedVersion $_ } | ConvertTo-Json -Compress")
        self.assertEqual(["1.4.0", "1.4.0", "1.4.0", "1.4.0", "1.4.0", None, None], observed)

    def test_the_full_version_keeps_its_prerelease_label_only(self):
        observed = self.run_helpers(
            "@('1.4.0', '1.4.0-beta.1', '1.4.0-beta.1+2f3a4b5', ' 1.4.0-beta.10 ',"
            " '1.4.0.0', '1.4', 'beta.1', '') |"
            " ForEach-Object { Get-FullVersion $_ } | ConvertTo-Json -Compress")
        self.assertEqual(
            ["1.4.0", "1.4.0-beta.1", "1.4.0-beta.1", "1.4.0-beta.10", None, None, None, None],
            observed)

    # ----------------------------------------------------------- changelog

    def changelog(self, directory, heading):
        path = Path(directory) / "CHANGELOG.md"
        path.write_text(
            "# 变更记录 / Changelog\n\n"
            f"## [{heading}]\n\n### 修复\n- something\n\n"
            "## [1.3.1] - 2026-09-19\n\n### 变更\n- older\n",
            encoding="utf-8")
        return path

    def test_the_top_heading_is_read_whatever_it_says(self):
        with tempfile.TemporaryDirectory(prefix="mr changelog ") as directory:
            for heading in ("Unreleased", "1.4.0"):
                path = self.changelog(directory, heading)
                observed = self.run_helpers(
                    f"Get-TopChangelogHeading {ps_literal(path)} | ConvertTo-Json -Compress")
                self.assertEqual(heading, observed)

    def test_a_prerelease_requires_unreleased_on_top(self):
        # A beta is cut from work that is not released yet, so the entries it carries must
        # still be under [Unreleased]; a numbered heading would claim the release happened.
        observed = self.run_helpers(
            "@(@{h='Unreleased';v='1.4.0-beta.1'}, @{h='1.4.0';v='1.4.0-beta.1'},"
            "  @{h='1.4.0';v='1.4.0'}, @{h='Unreleased';v='1.4.0'},"
            "  @{h='1.3.1';v='1.4.0'}, @{h=$null;v='1.4.0'}) |"
            " ForEach-Object { [bool](Test-ChangelogTopIsCorrect -Heading $_.h -Version $_.v) } |"
            " ConvertTo-Json -Compress")
        self.assertEqual([True, False, True, False, False, False], observed)

    def test_the_refusal_names_what_was_expected(self):
        observed = self.run_helpers(
            "@((Get-ChangelogTopRefusal -Heading '1.4.0' -Version '1.4.0-beta.1'),"
            "  (Get-ChangelogTopRefusal -Heading 'Unreleased' -Version '1.4.0')) |"
            " ConvertTo-Json -Compress")
        self.assertIn("Unreleased", observed[0])
        self.assertIn("1.4.0", observed[1])


if __name__ == "__main__":
    unittest.main()
