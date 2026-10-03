#!/usr/bin/env python3
"""Evaluate selected top-level statements of scripts/package.ps1 against chosen inputs.

package.ps1 builds, publishes and runs executables, so it cannot run in a self-test. The
decisions checked here are plain statements in it, though: which outputs a run replaces,
which preconditions block ``public_distribution_ready``, and which Desktop executables are
refused. The test parses the script and evaluates only those statements - the same technique
as test_package_build_directory.py - with git replaced by a shim on PATH where a broken git
is the point.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
PACKAGE = REPO / "scripts" / "package.ps1"
HELPERS = REPO / "scripts" / "package-version.ps1"
DESKTOP_MANIFEST = REPO / "src" / "Desktop" / "resources" / "app" / "MentorRecorder.Desktop.manifest"


def portable_executable(manifest: str | None, extra: bytes = b"") -> bytes:
    """A minimal PE32+ image: the headers, one .rsrc section and - when given - an application
    manifest as resource type 24, name 1, language 1033. Enough for the resource walk in
    package.ps1; nothing in it could ever run."""
    section = bytearray()
    if manifest is not None:
        data = manifest.encode("utf-8")

        def directory(entry_id: int, offset: int) -> bytes:
            # IMAGE_RESOURCE_DIRECTORY with one numbered entry, then that entry.
            return struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1) + struct.pack("<II", entry_id, offset)

        section += directory(24, 0x80000000 | 0x18)
        section += directory(1, 0x80000000 | 0x30)
        section += directory(1033, 0x48)
        section += struct.pack("<IIII", 0x1000 + 0x58, len(data), 0, 0)
        section += data
    section += extra
    raw = bytes(section).ljust(max(0x200, (len(section) + 0x1FF) & ~0x1FF), b"\0")

    dos = bytearray(0x40)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 0x40)
    coff = struct.pack("<HHIIIHH", 0x8664, 1, 0, 0, 0, 240, 0x22)
    optional = bytearray(240)
    struct.pack_into("<H", optional, 0, 0x20B)
    if manifest is not None:
        struct.pack_into("<II", optional, 112 + 2 * 8, 0x1000, len(section))
    section_header = struct.pack("<8sIIIIIIHHI", b".rsrc", len(raw), 0x1000, len(raw), 0x200,
                                 0, 0, 0, 0, 0x40000040)
    headers = bytes(dos) + b"PE\0\0" + coff + bytes(optional) + section_header
    return headers.ljust(0x200, b"\0") + raw

# The two ways git fails in practice: a checkout owned by another account ("dubious
# ownership") and a directory that is not a repository. Both exit 128 with nothing on stdout.
FAILING_GIT = (
    "@echo off\r\n"
    "echo fatal: detected dubious ownership in repository 1>&2\r\n"
    "exit /b 128\r\n"
)

PRELUDE = r"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
. {helpers}
$source = [IO.File]::ReadAllText({package}, [Text.Encoding]::UTF8)
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) {{ throw 'Cannot parse package.ps1' }}
$statements = @($ast.EndBlock.Statements)

function Get-StatementRange([string]$From, [string]$Until, [switch]$Inclusive) {{
    # Top-level statements from the first whose text matches $From up to the first later one
    # matching $Until (excluded unless -Inclusive).
    $start = -1
    for ($i = 0; $i -lt $statements.Count; $i++) {{
        if ($statements[$i].Extent.Text -match $From) {{ $start = $i; break }}
    }}
    if ($start -lt 0) {{ throw "No statement matches $From" }}
    for ($j = $start + 1; $j -lt $statements.Count; $j++) {{
        if ($statements[$j].Extent.Text -match $Until) {{
            $end = if ($Inclusive) {{ $j }} else {{ $j - 1 }}
            return ($statements[$start..$end] | ForEach-Object {{ $_.Extent.Text }}) -join "`n"
        }}
    }}
    throw "No statement after $From matches $Until"
}}

function Get-Assignments([string[]]$Names) {{
    # Top-level assignments to the named variables, in script order.
    return (@($statements | Where-Object {{
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left.Extent.Text -in $Names
    }}) | ForEach-Object {{ $_.Extent.Text }}) -join "`n"
}}

function Get-FunctionDefinitions([string[]]$Names) {{
    return (@($statements | Where-Object {{
        $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -in $Names
    }}) | ForEach-Object {{ $_.Extent.Text }}) -join "`n"
}}
"""


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def git(*arguments, cwd):
    subprocess.run(
        ["git", "-c", "user.name=package test", "-c", "user.email=package-test@example.invalid",
         "-c", "commit.gpgsign=false", *arguments],
        cwd=cwd, check=True, capture_output=True)


@unittest.skipUnless(os.name == "nt", "Windows packaging script")
class PackageScriptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.shell = shutil.which("pwsh")
        if not cls.shell:
            raise unittest.SkipTest("PowerShell 7 (pwsh) is required")
        if not shutil.which("git"):
            raise unittest.SkipTest("git is required to build the test repository")

    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix="mr package script "))
        self.addCleanup(shutil.rmtree, self.directory, ignore_errors=True)
        self.repository = self.directory / "repo"
        self.repository.mkdir()
        git("init", "-q", cwd=self.repository)
        git("commit", "-q", "--allow-empty", "-m", "initial", cwd=self.repository)
        self.shim = self.directory / "failing-git"
        self.shim.mkdir()
        (self.shim / "git.cmd").write_bytes(FAILING_GIT.encode("ascii"))

    def run_package_statements(self, body, *, failing_git=False):
        path_line = (f"$env:PATH = {ps_literal(self.shim)} + ';' + $env:PATH"
                     if failing_git else "")
        script = (PRELUDE.format(helpers=ps_literal(HELPERS), package=ps_literal(PACKAGE))
                  + path_line + "\n" + body)
        path = self.directory / "statements.ps1"
        path.write_text(script, encoding="utf-8-sig")
        completed = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-File", str(path)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    # ----------------------------------------------- public_distribution_ready

    def distribution_claim(self, *, failing_git):
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$stagedDoctor = [pscustomobject]@{{ public_distribution_ready = $true }}
$liveCaptureStatus = 'VERIFIED_POP_TO_EXIT'
$verifiedProfileIds = @('global.test.verified')
$SkipVerify = [switch]$false
$IsPrerelease = $false
$Version = '9.9.9'
. ([scriptblock]::Create((Get-Assignments @(
    '$sourceState', '$sourceCommit', '$sourceCommitAvailable', '$sourceDirty'))))
. ([scriptblock]::Create((Get-StatementRange '^\\$distributionBlockers\\s*=' '^\\$publicDistributionReady\\s*=' -Inclusive)))
[ordered]@{{ ready = $publicDistributionReady; blockers = @($distributionBlockers) }} |
    ConvertTo-Json -Compress
""", failing_git=failing_git)

    def test_a_clean_committed_tree_is_ready(self):
        # Guards the harness: everything else being satisfied, the claim is true.
        claim = self.distribution_claim(failing_git=False)
        self.assertEqual([], claim["blockers"])
        self.assertTrue(claim["ready"])

    def test_a_failing_git_blocks_the_distribution_claim(self):
        # git exits 128, prints nothing on stdout: an empty `git status` is not a clean tree,
        # and an artifact with no source_commit cannot point at its corresponding source.
        claim = self.distribution_claim(failing_git=True)
        self.assertFalse(claim["ready"])
        self.assertTrue(any("git" in blocker for blocker in claim["blockers"]), claim["blockers"])

    # ------------------------------------------------------- -Verify recheck

    def verify_recheck(self, *, failing_git, source_commit):
        commit = "$null" if source_commit is None else ps_literal(source_commit)
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$metadata = [pscustomobject]@{{
    public_distribution_ready = $true
    source_worktree_dirty = $false
    source_commit = {commit}
}}
$unpackedVerifiedIds = @('global.test.verified')
$report = [pscustomobject]@{{ live_capture_status = 'VERIFIED_POP_TO_EXIT' }}
$check = $ast.Find({{
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$metadata.public_distribution_ready'
}}, $true)
if (-not $check) {{ throw 'The -Verify recheck of public_distribution_ready was not found' }}
$refusal = $null
try {{ . ([scriptblock]::Create($check.Extent.Text)) }} catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{ refused = [bool]$refusal; message = [string]$refusal }} | ConvertTo-Json -Compress
""", failing_git=failing_git)

    def head(self):
        return subprocess.run(["git", "rev-parse", "HEAD"], cwd=self.repository, check=True,
                              capture_output=True, text=True).stdout.strip()

    def test_the_verify_recheck_accepts_a_consistent_claim(self):
        outcome = self.verify_recheck(failing_git=False, source_commit=self.head())
        self.assertFalse(outcome["refused"], outcome["message"])

    def test_the_verify_recheck_refuses_when_git_fails(self):
        outcome = self.verify_recheck(failing_git=True, source_commit=self.head())
        self.assertTrue(outcome["refused"])
        self.assertIn("git", outcome["message"])

    def test_the_verify_recheck_refuses_a_claim_without_a_source_commit(self):
        outcome = self.verify_recheck(failing_git=False, source_commit=None)
        self.assertTrue(outcome["refused"])
        self.assertIn("source_commit", outcome["message"])

    # ---------------------------------------------------- replaced outputs

    def replace_outputs(self, *, force):
        output = self.directory / "artifacts"
        output.mkdir(exist_ok=True)
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$OutputDir = {ps_literal(output)}
$Configuration = 'Release'
$Version = '9.9.9'
$Force = [switch]${'true' if force else 'false'}
. ([scriptblock]::Create((Get-Assignments @(
    '$OutputRoot', '$ArtifactName', '$StageDir', '$CollectorStage', '$ZipPath', '$ZipHashPath',
    '$InstallerPath', '$InstallerHashPath'))))
. ([scriptblock]::Create((Get-FunctionDefinitions @('Remove-IfExists', 'Assert-ReplaceAllowed'))))
$installer = Join-Path $OutputRoot 'MentorRecorder-9.9.9-setup.exe'
Set-Content -LiteralPath $installer -Value 'stale installer'
Set-Content -LiteralPath ($installer + '.sha256') -Value 'stale checksum'
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^Assert-ReplaceAllowed\\b' '^New-Item -ItemType Directory -Path \\$StageDir')))
}}
catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{
    refused = [bool]$refusal
    message = [string]$refusal
    installer_left = Test-Path -LiteralPath $installer
    checksum_left = Test-Path -LiteralPath ($installer + '.sha256')
}} | ConvertTo-Json -Compress
""")

    def test_a_stale_same_version_installer_needs_force(self):
        # Without -Force nothing of an earlier run may be replaced - nor silently kept beside
        # the new zip, where it would pass for this run's installer.
        outcome = self.replace_outputs(force=False)
        self.assertTrue(outcome["refused"])
        self.assertIn("MentorRecorder-9.9.9-setup.exe", outcome["message"])

    def test_force_removes_a_stale_installer_even_when_none_is_built(self):
        # -NoInstaller, or no ISCC on this machine: the installer step never runs, so the
        # stale setup.exe and its checksum must be gone before staging starts.
        outcome = self.replace_outputs(force=True)
        self.assertFalse(outcome["refused"], outcome["message"])
        self.assertFalse(outcome["installer_left"])
        self.assertFalse(outcome["checksum_left"])

    # ------------------------------------------------- the Desktop executable

    def desktop_check(self, image):
        path = self.directory / "MentorRecorder.Desktop.exe"
        path.write_bytes(image)
        return self.run_package_statements(f"""
. ([scriptblock]::Create((Get-FunctionDefinitions @(
    'Assert-File', 'Get-EmbeddedManifest', 'Assert-DesktopExecutable'))))
$path = {ps_literal(path)}
$manifest = Get-EmbeddedManifest $path
$refusal = $null
try {{ Assert-DesktopExecutable $path 6>$null }} catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{
    manifest = [string]$manifest
    refused = [bool]$refusal
    message = [string]$refusal
}} | ConvertTo-Json -Compress
""")

    def project_manifest(self):
        return DESKTOP_MANIFEST.read_text(encoding="utf-8")

    def test_the_project_manifest_embedded_in_the_executable_is_accepted(self):
        # Review OJ-5: asInvoker, long paths and Windows 10/11 travel inside the executable.
        outcome = self.desktop_check(portable_executable(self.project_manifest()))
        self.assertFalse(outcome["refused"], outcome["message"])
        self.assertIn('level="asInvoker"', outcome["manifest"])

    def test_an_executable_without_an_embedded_manifest_is_a_packaging_error(self):
        # What the 1.5.0 build looked like: the manifest was a side file, copied only if present.
        outcome = self.desktop_check(portable_executable(None))
        self.assertTrue(outcome["refused"])
        self.assertIn("RT_MANIFEST", outcome["message"])
        self.assertEqual("", outcome["manifest"])

    def test_a_manifest_asking_for_other_rights_or_short_paths_is_refused(self):
        elevated = self.project_manifest().replace('level="asInvoker"', 'level="requireAdministrator"')
        outcome = self.desktop_check(portable_executable(elevated))
        self.assertTrue(outcome["refused"])
        self.assertIn("asInvoker", outcome["message"])

        short = self.project_manifest().replace(">true</ws2:longPathAware>", ">false</ws2:longPathAware>")
        outcome = self.desktop_check(portable_executable(short))
        self.assertTrue(outcome["refused"])
        self.assertIn("longPathAware", outcome["message"])

    def test_a_desktop_built_with_developer_collector_discovery_is_refused(self):
        # Review DT2-X3: MR_DEV_COLLECTOR_DISCOVERY compiles the MR_COLLECTOR_PATH override in.
        outcome = self.desktop_check(portable_executable(self.project_manifest(), b"MR_COLLECTOR_PATH\0"))
        self.assertTrue(outcome["refused"])
        self.assertIn("MR_DEV_COLLECTOR_DISCOVERY", outcome["message"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
