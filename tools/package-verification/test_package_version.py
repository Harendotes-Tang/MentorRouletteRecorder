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

    # ------------------------------------------------- released sections

    RELEASED = (
        "# 变更记录 / Changelog\n\n"
        "## [1.5.0] - 2026-10-03\n\n### 修复\n- 新条目\n\n"
        "## [1.4.0] - 2026-09-21\n\n### 新增\n- 备注图片\n- 自动更新检查\n\n"
        "## [1.3.1] - 2026-09-19\n\n### 变更\n- older\n"
    )

    def released_change(self, tagged, working, version="1.4.0"):
        # The texts travel through files so CR and LF reach PowerShell byte for byte: the
        # tagged blob arrives LF-only from git, the working copy is CRLF.
        with tempfile.TemporaryDirectory(prefix="mr changelog ") as directory:
            tagged_path = Path(directory) / "tagged.md"
            working_path = Path(directory) / "working.md"
            tagged_path.write_bytes(tagged.encode("utf-8"))
            working_path.write_bytes(working.encode("utf-8"))
            return self.run_helpers(
                f"$tagged = [IO.File]::ReadAllText({ps_literal(tagged_path)}, [Text.Encoding]::UTF8)\n"
                f"$working = [IO.File]::ReadAllText({ps_literal(working_path)}, [Text.Encoding]::UTF8)\n"
                f"ConvertTo-Json -Compress -InputObject (Get-ReleasedChangelogSectionChange "
                f"-Tagged $tagged -Working $working -Version {ps_literal(version)})")

    def test_an_unchanged_released_section_passes_across_line_endings(self):
        working = self.RELEASED.replace("\n", "\r\n")
        self.assertIsNone(self.released_change(self.RELEASED, working))
        # PowerShell splits git's output on a lone CR as well; a lone CR in the working copy
        # must therefore read the same as a line break.
        self.assertIsNone(self.released_change(
            self.RELEASED, working.replace("- 备注图片\r\n", "- 备注图片\r")))

    def test_a_changed_released_body_is_refused(self):
        working = self.RELEASED.replace("- 自动更新检查", "- 自动更新检查（补写）")
        self.assertIsNotNone(self.released_change(self.RELEASED, working))

    def test_a_changed_released_heading_is_refused(self):
        working = self.RELEASED.replace("## [1.4.0] - 2026-09-21", "## [1.4.0] - 2026-09-22")
        self.assertIsNotNone(self.released_change(self.RELEASED, working))

    def test_a_deleted_or_renamed_released_section_is_refused(self):
        start = self.RELEASED.index("## [1.4.0]")
        end = self.RELEASED.index("## [1.3.1]")
        for working in (self.RELEASED[:start] + self.RELEASED[end:],
                        self.RELEASED.replace("## [1.4.0]", "## [1.4.1]")):
            with self.subTest(working=working[start:start + 30]):
                self.assertIsNotNone(self.released_change(self.RELEASED, working))

    def test_a_tag_without_that_section_has_nothing_to_protect(self):
        start = self.RELEASED.index("## [1.4.0]")
        end = self.RELEASED.index("## [1.3.1]")
        tagged = self.RELEASED[:start] + self.RELEASED[end:]
        self.assertIsNone(self.released_change(tagged, self.RELEASED))

    # ------------------------------------------------------ source tree

    def source_state(self, repository, path_prefix=None, path_only=None):
        if path_only is not None:
            path = f"$env:PATH = {ps_literal(path_only)}\n"
        elif path_prefix is not None:
            path = f"$env:PATH = {ps_literal(path_prefix)} + ';' + $env:PATH\n"
        else:
            path = ""
        return self.run_helpers(
            path + f"Get-SourceTreeState -RepoRoot {ps_literal(repository)} | ConvertTo-Json -Compress")

    def make_repository(self, directory):
        repository = Path(directory) / "repo"
        repository.mkdir()
        for arguments in (["init", "-q"], ["commit", "-q", "--allow-empty", "-m", "initial"]):
            subprocess.run(
                ["git", "-c", "user.name=package test", "-c",
                 "user.email=package-test@example.invalid", "-c", "commit.gpgsign=false",
                 *arguments], cwd=repository, check=True, capture_output=True)
        return repository

    @unittest.skipUnless(shutil.which("git"), "git is required")
    def test_a_committed_tree_reports_its_commit_and_cleanliness(self):
        with tempfile.TemporaryDirectory(prefix="mr source ") as directory:
            repository = self.make_repository(directory)
            clean = self.source_state(repository)
            self.assertRegex(clean["Commit"], r"^[0-9a-f]{40}$")
            self.assertIs(False, clean["Dirty"])
            self.assertIsNone(clean["Problem"])
            (repository / "untracked.txt").write_text("x", encoding="utf-8")
            self.assertIs(True, self.source_state(repository)["Dirty"])

    @unittest.skipUnless(shutil.which("git"), "git is required")
    def test_a_failing_or_missing_git_is_a_problem_not_a_clean_tree(self):
        with tempfile.TemporaryDirectory(prefix="mr source ") as directory:
            repository = self.make_repository(directory)
            shim = Path(directory) / "failing-git"
            shim.mkdir()
            # "dubious ownership" and "not a git repository" both look like this.
            (shim / "git.cmd").write_bytes(
                b"@echo off\r\necho fatal: detected dubious ownership 1>&2\r\nexit /b 128\r\n")
            empty = Path(directory) / "no-git"
            empty.mkdir()
            for label, state in (("exit 128", self.source_state(repository, path_prefix=shim)),
                                 ("missing", self.source_state(repository, path_only=empty))):
                with self.subTest(git=label):
                    self.assertIsNone(state["Commit"])
                    self.assertIsNone(state["Dirty"])
                    self.assertTrue(state["Problem"])


if __name__ == "__main__":
    unittest.main()
