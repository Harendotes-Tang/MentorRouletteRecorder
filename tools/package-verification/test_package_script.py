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
import re
import shutil
import struct
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
AST_FIXTURE = Path(__file__).with_name("package-script-ast.ps1").resolve()
DESKTOP_MANIFEST = REPO / "src" / "Desktop" / "resources" / "app" / "MentorRecorder.Desktop.manifest"
# The flavour stamp src/Desktop/cpp/transport/BuildFlavour.cpp compiles in (docs/build-and-package.md
# section 3.7): a NUL-terminated narrow string, one of exactly two values.
DISTRIBUTION_STAMP = b"MR_BUILD_FLAVOUR=distribution\0"
DEVELOPMENT_STAMP = b"MR_BUILD_FLAVOUR=development\0"


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
        self.directory = Path(tempfile.mkdtemp(prefix="mr package script ")).resolve()
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
        script = f". {ps_literal(AST_FIXTURE)}\n{path_line}\n{body}"
        path = self.directory / "statements.ps1"
        path.write_text(script, encoding="utf-8-sig")
        completed = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-File", str(path)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    def test_optional_opencv_ffmpeg_video_dll_is_forbidden_alongside_qt_backend(self):
        result = self.run_package_statements("""
Invoke-Expression (Get-Assignments @('$ForbiddenMediaPatterns'))
Invoke-Expression (Get-FunctionDefinitions @('Test-ForbiddenMediaFile'))
@{
    opencv = Test-ForbiddenMediaFile ([IO.FileInfo]::new('opencv_videoio_ffmpeg500_64.dll'))
    qt = Test-ForbiddenMediaFile ([IO.FileInfo]::new('ffmpegmediaplugin.dll'))
    alias = Test-ForbiddenMediaFile ([IO.FileInfo]::new('avcodec-61.dll'))
    image = Test-ForbiddenMediaFile ([IO.FileInfo]::new('cv2.pyd'))
} | ConvertTo-Json -Compress
""")
        self.assertEqual(result, dict(opencv=True, qt=True, alias=True, image=False))

    def test_readmes_and_changelog_are_required_in_the_stage_and_archive(self):
        stage = self.directory / "stage"
        stage.mkdir()
        unpacked = self.directory / "unpacked"
        result = self.run_package_statements(rf"""
$RepoRoot = {ps_literal(REPO)}
$StageDir = {ps_literal(stage)}
$ZipPath = {ps_literal(self.directory / "test.zip")}
$verifyRoot = {ps_literal(unpacked)}
Invoke-Expression (Get-Assignments @('$PackagedRootDocs'))
Invoke-Expression (Get-StatementRange '^foreach \(\$document in \$PackagedRootDocs\)' '^Copy-Item .*THIRD_PARTY_NOTICES')
Invoke-Expression (Get-FunctionDefinitions @('Assert-RequiredContent'))
$compress = $ast.Find({{ param($n) $n -is [Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -eq 'Compress-Archive' }}, $true)
Invoke-Expression $compress.Extent.Text
Expand-Archive -LiteralPath $ZipPath -DestinationPath $verifyRoot
$unpacked = Join-Path $verifyRoot (Split-Path -Leaf $StageDir)
Remove-Item -LiteralPath (Join-Path $StageDir 'README.ja.md')
Remove-Item -LiteralPath (Join-Path $unpacked 'CHANGELOG.md')
$missing = @{{}}
foreach ($directory in @($StageDir, $unpacked)) {{
    try {{ Assert-RequiredContent $directory }}
    catch {{ $missing[$directory] = $_.Exception.Message -split '\r?\n' }}
}}
$missing | ConvertTo-Json -Compress
""")
        expected = {"README.md", "README.zh-TW.md", "README.en.md", "README.ja.md", "CHANGELOG.md"}
        for folder, omitted in ((stage, "README.ja.md"), (unpacked / stage.name, "CHANGELOG.md")):
            with self.subTest(folder=folder):
                self.assertEqual({omitted}, expected.intersection(result[str(folder)]))
                self.assertEqual(expected - {omitted}, {file.name for file in folder.iterdir()})
                for name in expected - {omitted}:
                    self.assertEqual((REPO / name).read_bytes(), (folder / name).read_bytes())

    def test_stage_copies_only_tracked_distributable_documentation(self):
        published = ["docs/指南 [简中]/安装 说明.md", "docs/licenses/sub/LICENSE.txt"]
        developer = [f"docs/{name}/internal.md" for name in ("diagrams", "plans", "reviews")]
        private = ["docs/私有 笔记.md", "docs/ignored/working.md"]
        (self.repository / ".gitignore").write_text("/docs/ignored/\n", encoding="utf-8")
        for name in published + developer + private:
            source = self.repository / name
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_text(name, encoding="utf-8")
        git("--literal-pathspecs", "add", "--", *published, *developer, cwd=self.repository)
        # A local build still includes working edits to an already tracked user document.
        (self.repository / published[0]).write_text("已修改", encoding="utf-8")
        stage = self.directory / "stage"
        self.run_package_statements(rf"""
$RepoRoot = {ps_literal(self.repository)}
$StageDir = {ps_literal(stage)}
$AllowReusedVersion = $true
Invoke-Expression (Get-FunctionDefinitions @('Copy-TrackedDocumentation'))
Invoke-Expression (Get-StatementRange '^Copy-TrackedDocumentation ' '^Install-OcrRuntime ')
$true | ConvertTo-Json
""")
        self.assertEqual(set(published), {p.relative_to(stage).as_posix() for p in stage.rglob("*") if p.is_file()})
        for name in published:
            self.assertEqual((self.repository / name).read_bytes(), (stage / name).read_bytes())

    def copy_docs_from_source_snapshot(self, paths, *, allow=True, missing_git=False):
        manifest = self.repository / "scripts/package-docs.txt"
        manifest.parent.mkdir(exist_ok=True)
        manifest.write_text("\n".join(paths) + "\n", encoding="utf-8")
        stage = self.directory / "stage"
        outcome = self.run_package_statements(rf"""
$RepoRoot = {ps_literal(self.repository)}
$StageDir = {ps_literal(stage)}
$AllowReusedVersion = ${str(allow).lower()}
{"$env:PATH = ''" if missing_git else ""}
Invoke-Expression (Get-FunctionDefinitions @('Copy-TrackedDocumentation'))
$refusal = $null
try {{ Invoke-Expression (Get-StatementRange '^Copy-TrackedDocumentation ' '^Install-OcrRuntime ') 3>$null }}
catch {{ $refusal = $_.Exception.Message }}
@{{ message = [string]$refusal }} | ConvertTo-Json -Compress
""", failing_git=not missing_git)
        return outcome["message"], {p.relative_to(stage).as_posix() for p in stage.rglob("*") if p.is_file()}

    def test_explicit_source_snapshot_packaging_uses_only_manifest_documents(self):
        approved = "docs/指南 [简中]/安装 说明.md"
        for name in (approved, "docs/untracked.md", "docs/reviews/private.md"):
            file = self.repository / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(name, encoding="utf-8")
        for missing_git in (False, True):
            with self.subTest(missing_git=missing_git):
                message, copied = self.copy_docs_from_source_snapshot([approved], missing_git=missing_git)
                self.assertEqual("", message)
                self.assertEqual({approved}, copied)
                shutil.rmtree(self.directory / "stage")

    def test_source_snapshot_manifest_requires_the_override_and_enforces_document_paths(self):
        message, copied = self.copy_docs_from_source_snapshot(["docs/permitted.md"], allow=False)
        self.assertIn("-AllowReusedVersion", message)
        self.assertEqual(set(), copied)
        for path in ("docs/../src/private.txt", "docs/REVIEWS/private.txt", "secrets.txt"):
            with self.subTest(path=path):
                message, copied = self.copy_docs_from_source_snapshot([path])
                self.assertIn("禁止打包", message)
                self.assertEqual(set(), copied)

    # ----------------------------------------------- public_distribution_ready

    def distribution_claim(self, *, failing_git, verify=True, verification_complete=True):
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$stagedDoctor = [pscustomobject]@{{ public_distribution_ready = $true }}
$liveCaptureStatus = 'VERIFIED_POP_TO_EXIT'
$verifiedProfileIds = @('global.test.verified')
$SkipVerify = [switch]$false
$verificationResult = [pscustomobject]@{{ Complete = ${'true' if verification_complete else 'false'}; Succeeded = $true }}
$Verify = [switch]${'true' if verify else 'false'}
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

    def test_an_artifact_that_was_not_unpacked_and_run_is_not_ready(self):
        # M-2 (audit 2026-10-20): release-checklist §9.4 requires the unpack-and-run check; a
        # -Force run without -Verify used to claim public_distribution_ready all the same.
        claim = self.distribution_claim(failing_git=False, verify=False)
        self.assertFalse(claim["ready"])
        self.assertTrue(any("-Verify" in blocker for blocker in claim["blockers"]), claim["blockers"])

    def test_partial_preflight_blocks_the_distribution_claim(self):
        claim = self.distribution_claim(failing_git=False, verification_complete=False)
        self.assertFalse(claim["ready"])
        self.assertTrue(any("部分验证" in blocker for blocker in claim["blockers"]), claim["blockers"])


    def test_preflight_call_preserves_partial_and_missing_results(self):
        verifier = self.directory / "verify.ps1"
        for returns_result in (True, False):
            with self.subTest(returns_result=returns_result):
                verifier.write_text(
                    "param([string]$Configuration, [switch]$Distribution, [ref]$Result)\n"
                    + ("$Result.Value = [pscustomobject]@{ Succeeded=$true; Complete=$false }\n"
                       if returns_result else "") + "exit 0\n", encoding="utf-8-sig")
                result = self.run_package_statements(f"""
function Write-Head([string]$Text) {{ }}
$SkipVerify = $false
$Configuration = 'Release'
$VerifyScript = {ps_literal(verifier)}
$verificationResult = $null
$preflight = @($ast.FindAll({{
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '-not $SkipVerify'
}}, $true))
if ($preflight.Count -ne 1) {{ throw 'The preflight verification call was not found' }}
. ([scriptblock]::Create($preflight[0].Extent.Text))
@{{ missing = $null -eq $verificationResult; result = $verificationResult }} | ConvertTo-Json -Compress
""")
                self.assertEqual(not returns_result, result["missing"])
                if returns_result:
                    self.assertIs(False, result["result"]["Complete"])
                    self.assertIs(True, result["result"]["Succeeded"])

    # ------------------------------- the distribution build directory (J-3 = M-2, audit 2026-11-03)

    def packaging_directory(self, configured):
        value = "$null" if configured is None else ps_literal(configured)
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$env:MR_BUILD_DIR = {value}
$BuildDir = $null
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^\\$configuredBuildDir\\s*=' '^\\$DesktopExe\\s*=')))
}}
catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{ directory = [string]$BuildDir; refused = [bool]$refusal; message = [string]$refusal }} |
    ConvertTo-Json -Compress
""")

    def test_packaging_builds_in_its_own_directory_by_default(self):
        # MR_DEV_BUILD is a cached CMake option: configured OFF in the developers' build\, it stays
        # OFF for every later plain configure and build there, and that exe opens the installed
        # copy's data and migrates it. The distribution flavour gets a directory of its own.
        for configured in (None, "", "  "):
            with self.subTest(configured=configured):
                outcome = self.packaging_directory(configured)
                self.assertFalse(outcome["refused"], outcome["message"])
                self.assertEqual(self.repository / "build-distribution", Path(outcome["directory"]))

    def test_an_explicit_packaging_directory_is_honoured(self):
        for configured, expected in (
            ("isolated package", self.repository / "isolated package"),
            (str(self.directory / "elsewhere"), self.directory / "elsewhere"),
        ):
            with self.subTest(configured=configured):
                outcome = self.packaging_directory(configured)
                self.assertFalse(outcome["refused"], outcome["message"])
                self.assertEqual(expected, Path(outcome["directory"]))

    def test_packaging_refuses_the_developers_build_directory(self):
        for configured in ("build", "build\\", ".\\BUILD", str(self.repository / "build")):
            with self.subTest(configured=configured):
                outcome = self.packaging_directory(configured)
                self.assertTrue(outcome["refused"], outcome)
                self.assertIn("MR_BUILD_DIR", outcome["message"])
                self.assertIn("build-distribution", outcome["message"])

    def test_verify_and_build_run_in_the_packaging_directory_and_the_caller_keeps_its_own(self):
        record = self.directory / "children.json"
        child = (
            "param([string]$Configuration, [switch]$Distribution, [switch]$NoRestore, [ref]$Result)\n"
            "$entry = [ordered]@{ script = (Split-Path -Leaf $PSCommandPath); "
            "directory = $env:MR_BUILD_DIR; distribution = [bool]$Distribution }\n"
            f"Add-Content -LiteralPath {ps_literal(record)} -Value (ConvertTo-Json $entry -Compress) "
            "-Encoding utf8\n"
            "exit 0\n")
        for name in ("verify.ps1", "build.ps1"):
            (self.directory / name).write_text(child, encoding="utf-8-sig")
        for caller in (None, "caller build"):
            with self.subTest(caller=caller):
                record.unlink(missing_ok=True)
                value = "$null" if caller is None else ps_literal(caller)
                outcome = self.run_package_statements(f"""
function Write-Head([string]$Text) {{ }}
$SkipVerify = $false
$Configuration = 'Release'
$VerifyScript = {ps_literal(self.directory / "verify.ps1")}
$BuildScript = {ps_literal(self.directory / "build.ps1")}
$BuildDir = {ps_literal(self.directory / "package build")}
$verificationResult = $null
$env:MR_BUILD_DIR = {value}
$steps = @($statements | Where-Object {{
    $_ -is [Management.Automation.Language.TryStatementAst] -and
    $_.Body.Extent.Text.Contains('$VerifyScript') -and $_.Body.Extent.Text.Contains('$BuildScript')
}})
if ($steps.Count -ne 1) {{ throw 'The verify and build steps were not found in one try block' }}
. ([scriptblock]::Create((Get-StatementRange '^\\$callerBuildDir\\s*=' '^Assert-File \\$DesktopExe\\b')))
[ordered]@{{ after = [Environment]::GetEnvironmentVariable('MR_BUILD_DIR') }} | ConvertTo-Json -Compress
""")
                children = [json.loads(line) for line in
                            record.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
                self.assertEqual(["verify.ps1", "build.ps1"], [c["script"] for c in children])
                for entry in children:
                    self.assertEqual(self.directory / "package build", Path(entry["directory"]))
                    self.assertTrue(entry["distribution"])
                self.assertEqual(caller, outcome["after"])

    def version_preflight(self, heading, version):
        changelog = self.directory / "CHANGELOG.md"
        changelog.write_text(f"# Changelog\n\n## [{heading}]\n\n- entry\n", encoding="utf-8")
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$ChangelogPath = {ps_literal(changelog)}
$Version = {ps_literal(version)}
. ([scriptblock]::Create((Get-FunctionDefinitions @('Write-Head', 'Assert-ReleasedChangelogSectionsUnchanged'))))
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^\\$changelogRefusal\\s*=' '^Assert-ReleasedChangelogSectionsUnchanged\\b' -Inclusive))) 6>$null
}}
catch {{ $refusal = $_.Exception.Message }}
$order = @{{}}
for ($i = 0; $i -lt $statements.Count; $i++) {{
    $text = $statements[$i].Extent.Text
    if (-not $order.Contains('preflight') -and $text -match '^\\$changelogRefusal\\s*=') {{ $order['preflight'] = $i }}
    if ($statements[$i] -is [Management.Automation.Language.TryStatementAst] -and
        $statements[$i].Body.Extent.Text.Contains('$VerifyScript')) {{ $order['verify'] = $i }}
}}
[ordered]@{{ refused = [bool]$refusal; message = [string]$refusal;
             preflight = $order['preflight']; verify = $order['verify'] }} | ConvertTo-Json -Compress
""")

    def test_the_changelog_rule_is_checked_before_verify_and_build(self):
        # M-2: at a formal version with [Unreleased] on top, the run used to verify and build the
        # distribution flavour for twenty minutes and only then refuse in Assert-StagedVersion.
        outcome = self.version_preflight("Unreleased", "9.9.9")
        self.assertTrue(outcome["refused"])
        self.assertIn("CHANGELOG.md 最上方应为 ## [9.9.9]", outcome["message"])
        self.assertIsNotNone(outcome["preflight"])
        self.assertIsNotNone(outcome["verify"])
        self.assertLess(outcome["preflight"], outcome["verify"])

    def test_a_consistent_changelog_passes_the_preflight(self):
        for heading, version in (("9.9.9", "9.9.9"), ("Unreleased", "9.9.9-beta.1")):
            with self.subTest(version=version):
                outcome = self.version_preflight(heading, version)
                self.assertFalse(outcome["refused"], outcome["message"])

    # ------------------------- a release number is spent only by its release commit (M-2, audit 2026-11-20)

    def unsuffixed_guard(self, version, *, skip_verify=False, allow=False, subject=None, failing_git=False):
        if subject is not None:
            git("commit", "-q", "--allow-empty", "-m", subject, cwd=self.repository)
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$Version = {ps_literal(version)}
$SkipVerify = [switch]${'true' if skip_verify else 'false'}
$AllowUnsuffixedVersion = [switch]${'true' if allow else 'false'}
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^\\$unsuffixedRefusal\\s*=' '^if \\(\\$unsuffixedRefusal\\)' -Inclusive))) 6>$null
}}
catch {{ $refusal = $_.Exception.Message }}
$order = @{{}}
for ($i = 0; $i -lt $statements.Count; $i++) {{
    if ($statements[$i].Extent.Text -match '^\\$unsuffixedRefusal\\s*=') {{ $order['guard'] = $i }}
    if ($statements[$i] -is [Management.Automation.Language.TryStatementAst] -and
        $statements[$i].Body.Extent.Text.Contains('$VerifyScript')) {{ $order['verify'] = $i }}
}}
[ordered]@{{ refused = [bool]$refusal; message = [string]$refusal;
             guard = $order['guard']; verify = $order['verify'] }} | ConvertTo-Json -Compress
""", failing_git=failing_git)

    def test_a_formal_version_off_its_release_commit_is_refused_before_verify(self):
        outcome = self.unsuffixed_guard("9.9.9", subject="feat(desktop): include feedback in the 9.9.9 test build")
        self.assertTrue(outcome["refused"])
        self.assertIn("chore(release): 9.9.9", outcome["message"])
        self.assertIn("-AllowUnsuffixedVersion", outcome["message"])
        self.assertIsNotNone(outcome["guard"])
        self.assertIsNotNone(outcome["verify"])
        self.assertLess(outcome["guard"], outcome["verify"])

    def test_a_formal_version_with_skip_verify_is_refused_even_on_its_release_commit(self):
        outcome = self.unsuffixed_guard("9.9.9", skip_verify=True, subject="chore(release): 9.9.9")
        self.assertTrue(outcome["refused"])
        self.assertIn("-SkipVerify", outcome["message"])

    def test_a_formal_version_whose_head_git_cannot_read_is_refused(self):
        self.assertTrue(self.unsuffixed_guard("9.9.9", failing_git=True)["refused"])

    def test_the_release_commit_a_prerelease_or_the_override_passes(self):
        self.assertFalse(self.unsuffixed_guard("9.9.9", subject="chore(release): 9.9.9")["refused"])
        self.assertFalse(self.unsuffixed_guard("9.9.9-beta.1", skip_verify=True, subject="wip")["refused"])
        self.assertFalse(self.unsuffixed_guard("9.9.9", skip_verify=True, allow=True)["refused"])

    # ------------------------- a published number is never reissued (M-2, audit 2026-11-26)

    def reused_version_guard(self, version, *, tag=None, commit_after_tag=False, allow=False, failing_git=False):
        if tag is not None:
            git("tag", tag, cwd=self.repository)
        if commit_after_tag:
            git("commit", "-q", "--allow-empty", "-m", "rebuilt under the same version", cwd=self.repository)
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
$Version = {ps_literal(version)}
$AllowReusedVersion = [switch]${'true' if allow else 'false'}
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^\\$versionTag\\s*=' '^if \\(\\$reusedVersionRefusal\\)' -Inclusive))) 6>$null
}}
catch {{ $refusal = $_.Exception.Message }}
$order = @{{}}
for ($i = 0; $i -lt $statements.Count; $i++) {{
    if ($statements[$i].Extent.Text -match '^\\$versionTag\\s*=') {{ $order['guard'] = $i }}
    if ($statements[$i] -is [Management.Automation.Language.TryStatementAst] -and
        $statements[$i].Body.Extent.Text.Contains('$VerifyScript')) {{ $order['verify'] = $i }}
}}
[ordered]@{{ refused = [bool]$refusal; message = [string]$refusal;
             guard = $order['guard']; verify = $order['verify'] }} | ConvertTo-Json -Compress
""", failing_git=failing_git)

    def test_a_version_tagged_on_another_commit_is_refused_before_verify(self):
        outcome = self.reused_version_guard("9.9.9-beta.2", tag="v9.9.9-beta.2", commit_after_tag=True)
        self.assertTrue(outcome["refused"])
        self.assertIn("v9.9.9-beta.2", outcome["message"])
        self.assertIn("-AllowReusedVersion", outcome["message"])
        self.assertIsNotNone(outcome["guard"])
        self.assertIsNotNone(outcome["verify"])
        self.assertLess(outcome["guard"], outcome["verify"])

    def test_a_version_whose_tag_git_cannot_read_is_refused(self):
        self.assertTrue(self.reused_version_guard("9.9.9-beta.2", failing_git=True)["refused"])

    def test_a_tagged_dirty_tree_requires_the_explicit_reuse_override(self):
        (self.repository / "uncommitted.cs").write_text("// pending source", encoding="utf-8")
        result = self.reused_version_guard("9.9.9-beta.2", tag="v9.9.9-beta.2")
        self.assertTrue(result["refused"])
        self.assertIn("-AllowReusedVersion", result["message"])
        self.assertFalse(self.reused_version_guard("9.9.9-beta.2", allow=True)["refused"])

    def test_debug_publish_embeds_symbols_in_a_separate_build_directory(self):
        observed = self.run_package_statements(f"""
$RepoRoot = {ps_literal(REPO)}
$BuildDir = {ps_literal(self.repository)}
$Configuration = 'Debug'
. ([scriptblock]::Create((Get-StatementRange '^\\$publishSymbolOptions\\s*=' '^& dotnet publish')))
$properties = & dotnet msbuild (Join-Path $RepoRoot 'src/Collector/MentorRecorder.Collector.csproj') -nologo `
    -p:Configuration=Debug @($publishSymbolOptions | Where-Object {{ $_ -like '-p:*' }}) `
    -getProperty:DebugType,PathMap
if ($LASTEXITCODE -ne 0) {{ throw 'MSBuild property evaluation failed' }}
@{{ symbols = ($properties -join [Environment]::NewLine | ConvertFrom-Json).Properties;
    options = $publishSymbolOptions }} | ConvertTo-Json -Compress
""")
        self.assertEqual("embedded", observed["symbols"]["DebugType"])
        self.assertIn(str(REPO), observed["symbols"]["PathMap"])
        self.assertEqual(str(self.repository / "collector-publish-artifacts"), observed["options"][1])

    def test_an_untagged_version_its_own_tag_or_the_override_passes(self):
        self.assertFalse(self.reused_version_guard("9.9.9-beta.2")["refused"])
        self.assertFalse(self.reused_version_guard("9.9.9-beta.2", tag="v9.9.9-beta.2")["refused"])
        self.assertFalse(self.reused_version_guard("9.9.9-beta.2", commit_after_tag=True, allow=True)["refused"])

    # ------------------------- every process the scripts start carries every kill switch (M-6, audit 2026-11-26)

    KILL_SWITCHES = {"MR_DISABLE_SHARED_FETCH", "MR_DISABLE_ONLINE_SPEECH",
                     "MR_DISABLE_UPDATE_CHECK", "MR_DISABLE_DUTY_COMMUNITY"}

    def test_verify_test_and_the_package_probes_set_every_network_kill_switch(self):
        scripts = REPO / "scripts"
        verify = (scripts / "verify.ps1").read_text(encoding="utf-8-sig")
        self.assertEqual(self.KILL_SWITCHES,
                         set(re.findall(r"^\$env:(MR_DISABLE_\w+)\s*=\s*'1'", verify, re.M)))

        test = (scripts / "test.ps1").read_text(encoding="utf-8-sig")
        self.assertEqual(self.KILL_SWITCHES,
                         set(re.findall(r"^\$env:(MR_DISABLE_\w+)\s*=\s*'1'", test, re.M)))
        # test.ps1 gives the caller's value back before it exits.
        self.assertEqual(self.KILL_SWITCHES,
                         set(re.findall(r"Remove-Item Env:\\(MR_DISABLE_\w+)", test)))

        probes = self.run_package_statements("""
$found = [ordered]@{}
foreach ($node in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                                 $n.Name -in @('Invoke-StagedCollectorJson', 'Invoke-Unpacked') }, $true)) {
    $found[$node.Name] = $node.Extent.Text
}
$found | ConvertTo-Json -Compress
""")
        self.assertEqual({"Invoke-StagedCollectorJson", "Invoke-Unpacked"}, set(probes))
        for name, text in probes.items():
            with self.subTest(probe=name):
                self.assertEqual(self.KILL_SWITCHES,
                                 set(re.findall(r"\['(MR_DISABLE_\w+)'\]\s*=\s*'1'", text)))

    # ------------------------- the Qt runtime deployment order (N2-1, audits 2026-11-10 to 2026-11-20)

    def qt_runtime_calls(self):
        return self.run_package_statements(r"""
$names = @('Assert-QtPrivateRuntime', 'Install-QtGraphsRuntime', 'Assert-InstalledQtGraphsRuntime')
$calls = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        ($node.GetCommandName() -in $names -or $node.CommandElements[0].Extent.Text -eq '$Windeployqt')
}, $true) | Sort-Object { $_.Extent.StartOffset })
$verifyBlock = @($statements | Where-Object {
    $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Test-UnpackedPackage'
})
if ($verifyBlock.Count -ne 1) { throw 'The unpack verification function was not found' }
$block = $verifyBlock[0].Extent
@($calls | ForEach-Object {
    $name = if ($_.CommandElements[0].Extent.Text -eq '$Windeployqt') { 'windeployqt' } else { $_.GetCommandName() }
    [ordered]@{
        name = $name
        verify = ($_.Extent.StartOffset -ge $block.StartOffset -and $_.Extent.EndOffset -le $block.EndOffset)
        text = $_.Extent.Text
    }
}) | ConvertTo-Json -Compress -AsArray
""")

    def test_the_qt_runtime_is_deployed_checked_patched_and_rechecked_in_order(self):
        # windeployqt copies the SDK's Qt6Graphs.dll, so the reviewed patch is restored only after it; the
        # private-ABI check reads what windeployqt deployed; -Verify checks both again in the unpacked tree.
        calls = self.qt_runtime_calls()
        self.assertEqual(
            [("windeployqt", False), ("Assert-QtPrivateRuntime", False), ("Install-QtGraphsRuntime", False),
             ("Assert-QtPrivateRuntime", True), ("Assert-InstalledQtGraphsRuntime", True)],
            [(call["name"], call["verify"]) for call in calls])
        for call, target in zip(calls, ("$StageDir", "$StageDir", "$StageDir", "$unpacked", "$unpacked")):
            with self.subTest(call=call["name"], target=target):
                self.assertIn(target, call["text"])

    def build_metadata(self, *, verify):
        metadata = self.directory / "BUILD-METADATA.json"
        self.run_package_statements(f"""
$Version = '9.9.9'
$IsPrerelease = $false
$Configuration = 'Release'
$sourceState = [ordered]@{{ Commit = ('a' * 40); Dirty = $false; Problem = $null }}
$liveCaptureStatus = 'VERIFIED_POP_TO_EXIT'
$sourceProfileStatus = 'VERIFIED'
$packagedProfileStatus = 'VERIFIED'
$publicDistributionReady = $false
$distributionBlockers = @()
$qtGraphsContext = $null
$qtPrivateRuntime = $null
$Verify = [switch]${'true' if verify else 'false'}
$BuildMetadataPath = {ps_literal(metadata)}
$write = $ast.Find({{
    param($node)
    $node -is [Management.Automation.Language.PipelineAst] -and
    $node.PipelineElements[-1] -is [Management.Automation.Language.CommandAst] -and
    $node.PipelineElements[-1].Extent.Text -match '^Set-Content -LiteralPath \\$BuildMetadataPath'
}}, $true)
if (-not $write) {{ throw 'The BUILD-METADATA.json writer was not found' }}
. ([scriptblock]::Create($write.Extent.Text))
@{{}} | ConvertTo-Json -Compress
""")
        return json.loads(metadata.read_text(encoding="utf-8-sig"))

    def test_build_metadata_says_whether_the_archive_was_unpacked_and_run(self):
        self.assertIs(True, self.build_metadata(verify=True)["unpack_verified"])
        self.assertIs(False, self.build_metadata(verify=False)["unpack_verified"])

    def failed_archive_step(self, step):
        stage = self.directory / step / "MentorRecorder-9.9.9-win-x64"
        stage.mkdir(parents=True)
        (stage / "payload.txt").write_text("retained for diagnosis", encoding="ascii")
        deploy = self.directory / "deploy.ps1"
        deploy.write_text("exit " + ("17" if step == "deploy" else "0"), encoding="ascii")
        return self.run_package_statements(rf"""
$StageDir = {ps_literal(stage)}
$ZipPath = $StageDir + '.zip'
$ZipHashPath = $ZipPath + '.sha256'
$BuildMetadataPath = Join-Path $StageDir 'BUILD-METADATA.json'
$HashPath = Join-Path $StageDir 'SHA256SUMS.txt'
$Version = '9.9.9'
$Configuration = 'Release'
$IsPrerelease = $false
$sourceState = [ordered]@{{ Commit = ('a' * 40); Dirty = $false }}
$liveCaptureStatus = 'VERIFIED_POP_TO_EXIT'
$sourceProfileStatus = $packagedProfileStatus = 'VERIFIED'
$Verify = [switch]$true
$publicDistributionReady = $true
$distributionBlockers = @()
$QmlSourceDir = $StageDir
$qtGraphsContext = $qtPrivateRuntime = $null
$Windeployqt = {ps_literal(deploy)}
$step = {ps_literal(step)}
function Write-Head {{}}
function Assert-QtPrivateRuntime {{}}
function Add-MinGwRuntime {{}}
function Add-OffscreenPlatformPlugin {{}}
function Assert-NoForbiddenPayload {{}}
function Assert-NoLocalPathLeak {{}}
function Assert-MultimediaLayout {{}}
function Assert-RequiredContent {{ if ($step -eq 'layout') {{ throw 'simulated layout failure' }} }}
function Assert-StagedVersion {{}}
function Assert-ReleasedChangelogSectionsUnchanged {{}}
function Assert-InstallDeleteCoverage {{}}
function Compress-Archive([string]$LiteralPath, [string]$DestinationPath, [string]$CompressionLevel) {{
    Set-Content -LiteralPath $DestinationPath -Value 'partial zip'
    if ($step -eq 'compress') {{ throw 'simulated compress failure' }}
}}
function Expand-Archive {{ throw 'simulated unpack failure' }}

# Invoke the production sequence as a child script so a native deploy failure can exit with its own code.
$steps = Get-StatementRange '^Write-Host .*packaged_verified_profile_status' '^if \(-not \$NoInstaller\)'
$scriptFile = {ps_literal(self.directory / "archive-steps.ps1")}
Set-Content -LiteralPath $scriptFile -Value $steps -Encoding utf8
$refusal = $null
try {{ & $scriptFile 6>$null }} catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{
    exit_code = $LASTEXITCODE
    message = [string]$refusal
    zip_left = Test-Path -LiteralPath $ZipPath
    zip_hash_left = Test-Path -LiteralPath $ZipHashPath
    metadata_left = Test-Path -LiteralPath $BuildMetadataPath
    manifest_left = Test-Path -LiteralPath $HashPath
    payload_left = Test-Path -LiteralPath (Join-Path $StageDir 'payload.txt')
}} | ConvertTo-Json -Compress
""")

    def test_failed_deploy_layout_compression_or_unpack_leave_no_claim_or_checksum(self):
        for step in ("deploy", "layout", "compress", "unpack"):
            with self.subTest(step=step):
                outcome = self.failed_archive_step(step)
                if step == "deploy":
                    self.assertEqual(17, outcome["exit_code"])
                else:
                    self.assertIn(f"simulated {step} failure", outcome["message"])
                self.assertFalse(outcome["zip_left"])
                self.assertFalse(outcome["zip_hash_left"])
                self.assertFalse(outcome["metadata_left"])
                self.assertFalse(outcome["manifest_left"])
                self.assertTrue(outcome["payload_left"])

    # ------------------------------------------- the source identity, before and after

    def source_identity_check(self, change):
        return self.run_package_statements(f"""
$RepoRoot = {ps_literal(self.repository)}
. ([scriptblock]::Create((Get-Assignments @('$sourceStateBeforeVerify'))))
{change}
$refusal = $null
try {{
    . ([scriptblock]::Create((Get-StatementRange '^\\$sourceState\\s*=' '^Write-Head\\s')))
}}
catch {{ $refusal = $_.Exception.Message }}
[ordered]@{{ refused = [bool]$refusal; message = [string]$refusal }} | ConvertTo-Json -Compress
""")

    def test_an_unchanged_source_tree_passes_the_identity_check(self):
        # Guards the harness: the check runs and accepts the same tree read twice.
        outcome = self.source_identity_check("")
        self.assertFalse(outcome["refused"], outcome["message"])

    def test_a_commit_made_while_packaging_is_refused(self):
        # M-2: another session's checkout, stash or commit between verify.ps1 and the metadata
        # step would leave a clean source_commit beside binaries built from another tree.
        outcome = self.source_identity_check(
            "& git -C $RepoRoot -c user.name=t -c user.email=t@example.invalid -c commit.gpgsign=false "
            "commit -q --allow-empty -m moved")
        self.assertTrue(outcome["refused"])
        self.assertIn("源码在打包过程中发生了变化", outcome["message"])

    def test_a_tree_cleaned_while_packaging_is_refused(self):
        (self.repository / "local-change.txt").write_text("not committed\n", encoding="utf-8")
        outcome = self.source_identity_check("Remove-Item -LiteralPath (Join-Path $RepoRoot 'local-change.txt')")
        self.assertTrue(outcome["refused"])
        self.assertIn("有未提交改动", outcome["message"])

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

    def test_competing_output_owner_cannot_replace_finished_or_in_progress_files(self):
        output = self.directory / "outputs"
        output.mkdir()
        archive = output / "previous.zip"
        archive.write_bytes(b"previous completed archive")
        outcome = self.run_package_statements(rf"""
$root = {ps_literal(output)}
$child = {ps_literal(self.directory / "output-lock.ps1")}
$pwsh = {ps_literal(self.shell)}
$definition = Get-FunctionDefinitions @('Invoke-WithPackageOutputLock')
. ([scriptblock]::Create($definition))
# Use the lock invocation of the actual entry point with a small output-producing callback.
$entry = $ast.EndBlock.Statements | Where-Object {{ $_.Extent.Text -match '^Invoke-WithPackageOutputLock ' }}
$body = $entry.PipelineElements[0].CommandElements[-1].ScriptBlock
if ($body.Extent.Text -notmatch 'Assert-ReplaceAllowed @' -or
    $body.Extent.Text -notmatch 'if \(\$Verify\) {{ Test-UnpackedPackage }}' -or
    $body.Extent.Text -notmatch 'Set-Content -LiteralPath \$InstallerHashPath') {{ throw 'Unprotected output production' }}
$call = $entry.Extent.Text.Substring(0, $body.Extent.StartOffset - $entry.Extent.StartOffset)
$action = @'
{{
    if ($mode -eq 'replace') {{ Set-Content -LiteralPath (Join-Path $OutputRoot 'previous.zip') -Value 'overwritten' }}
    if ($mode -eq 'throw') {{ throw 'simulated owner failure' }}
    if ($mode -eq 'exit') {{ exit 17 }}
}}
'@
$scriptText = 'param($OutputRoot, $mode)' + [Environment]::NewLine + $definition + [Environment]::NewLine + $call + $action
Set-Content -LiteralPath $child -Value $scriptText -Encoding utf8
# The second process names the same directory through a different path.
$result = Invoke-WithPackageOutputLock -Directory $root -Build {{
    $failure = & $pwsh -NoProfile -NonInteractive -File $child (Join-Path $root '.') 'replace' 2>&1
    [ordered]@{{ code = $LASTEXITCODE; message = ($failure | Out-String) }}
}}
try {{ . $child $root 'throw' }} catch {{ $ownerError = $_.Exception.Message }}
& $child $root 'exit'
$exitCode = $LASTEXITCODE
& $child $root 'probe'
[ordered]@{{ contention = $result; ownerError = $ownerError; exitCode = $exitCode
    remaining = @(Get-ChildItem -LiteralPath $root -Force -Name) }} | ConvertTo-Json -Compress
""")
        self.assertNotEqual(0, outcome["contention"]["code"])
        self.assertIn("打包输出目录已被占用", outcome["contention"]["message"])
        self.assertEqual("simulated owner failure", outcome["ownerError"])
        self.assertEqual(17, outcome["exitCode"])
        self.assertEqual(["previous.zip"], outcome["remaining"])
        self.assertEqual(b"previous completed archive", archive.read_bytes())

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

    def distribution_executable(self, extra: bytes = b""):
        """What scripts/package.ps1 is given: the project manifest and the distribution stamp."""
        return portable_executable(self.project_manifest(), DISTRIBUTION_STAMP + extra)

    def test_the_project_manifest_embedded_in_the_executable_is_accepted(self):
        # Review OJ-5: asInvoker, long paths and Windows 10/11 travel inside the executable.
        outcome = self.desktop_check(self.distribution_executable())
        self.assertFalse(outcome["refused"], outcome["message"])
        self.assertIn('level="asInvoker"', outcome["manifest"])

    def test_a_development_build_is_refused(self):
        # MR_DEV_BUILD=ON: data under MentorRecorder-dev, a .dev pipe, a dev marker on screen.
        # Such a binary must never ship, whatever else is right about it.
        outcome = self.desktop_check(portable_executable(self.project_manifest(), DEVELOPMENT_STAMP))
        self.assertTrue(outcome["refused"])
        self.assertIn("MR_DEV_BUILD", outcome["message"])
        self.assertIn("-Distribution", outcome["message"])

    def test_an_executable_without_a_flavour_stamp_is_refused(self):
        # Neither stamp: not a build this script knows how to vouch for.
        outcome = self.desktop_check(portable_executable(self.project_manifest()))
        self.assertTrue(outcome["refused"])
        self.assertIn("MR_BUILD_FLAVOUR", outcome["message"])
        # A stamp without its terminator is not the compiled-in string either.
        outcome = self.desktop_check(portable_executable(self.project_manifest(), DISTRIBUTION_STAMP[:-1] + b"\x07"))
        self.assertTrue(outcome["refused"])
        self.assertIn("MR_BUILD_FLAVOUR", outcome["message"])

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
        outcome = self.desktop_check(self.distribution_executable(b"MR_COLLECTOR_PATH\0"))
        self.assertTrue(outcome["refused"])
        self.assertIn("MR_DEV_COLLECTOR_DISCOVERY", outcome["message"])

    def test_embedded_changelog_and_translation_mentions_are_not_developer_code(self):
        # Release notes and QM source messages carry the names as data, not as the C strings
        # the code compiles in: no NUL follows them.
        for resource in ((REPO / "CHANGELOG.md").read_bytes(), b"MR_COLLECTOR_PATH\x07",
                         DEVELOPMENT_STAMP[:-1] + b"\x07"):
            with self.subTest(resource=resource[:40]):
                outcome = self.desktop_check(self.distribution_executable(resource))
                self.assertFalse(outcome["refused"], outcome["message"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
