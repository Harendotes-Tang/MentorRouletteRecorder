#!/usr/bin/env python3
"""Exercise QtGraphs deployment with pinned local bytes, never a Qt build."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
MATERIALS = ("dependency-manifest.json", "qtgraphs-d3d11.patch", "GPL-3.0-only.txt", "README.md")


def ps(value):
    return "'" + str(value).replace("'", "''") + "'"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


@unittest.skipUnless(os.name == "nt", "Windows QtGraphs deployment")
class QtGraphsRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required")
        self.temp = tempfile.TemporaryDirectory(prefix="mr qtgraphs ' staging ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repository 中文"
        self.materials = self.repo / "docs/licenses/qtgraphs"
        self.materials.mkdir(parents=True)
        self.cache = self.root / "isolated-cache"
        self.entry = self.cache / "qg-fixture"
        self.runtime = self.entry / "runtime"
        self.runtime.mkdir(parents=True)
        self.archive = self.entry / "qtgraphs-source.tar.gz"
        self.archive_bytes = b"pinned archive fixture, intentionally not an extractable Qt source tree"
        self.archive.write_bytes(self.archive_bytes)
        # PE architecture and export validation belongs to the real builder's
        # integration checks. These bytes exercise deployment integrity alone.
        self.dll_bytes = b"pinned QtGraphs fixture, intentionally not a loadable DLL"
        self.dll = self.runtime / "Qt6Graphs.dll"
        self.dll.write_bytes(self.dll_bytes)
        patch = (REPO / "docs/licenses/qtgraphs/qtgraphs-d3d11.patch").read_bytes()
        (self.materials / "qtgraphs-d3d11.patch").write_bytes(patch)
        self.recipe = dict(
            schema_version=1, cache_id="qtgraphs-fixture", qt_version="6.11.2",
            source_commit="f" * 40,
            source_url="https://codeload.github.com/qt/qtgraphs/tar.gz/" + "f" * 40,
            source_archive="qtgraphs-source.tar.gz", source_sha256=sha256(self.archive_bytes),
            patch_file="qtgraphs-d3d11.patch",
            patch_sha256=sha256(patch), patched_file="src/common/utils/commonutils.cpp",
            original_file_sha256="a" * 64, patched_file_sha256="b" * 64,
            cmake_options=["-DCMAKE_BUILD_TYPE=Release", "-DQT_BUILD_TESTS=OFF"],
        )
        self.write_json(self.materials / "dependency-manifest.json", self.recipe)
        (self.materials / "GPL-3.0-only.txt").write_bytes(b"fixture license material")
        (self.materials / "README.md").write_bytes(b"fixture corresponding-source instructions")
        self.features = [f"QT_FEATURE_graphs_fixture{i}={1 if i % 2 else -1}" for i in range(12)]
        self.qt_prefix = self.root / "Qt SDK"
        (self.qt_prefix / "bin").mkdir(parents=True)
        self.sdk_dll = self.qt_prefix / "bin/Qt6Graphs.dll"
        self.sdk_dll.write_bytes(b"upstream SDK must remain unchanged")
        self.context = dict(
            Recipe=self.recipe, RepoRoot=str(self.repo), QtPrefix=str(self.qt_prefix),
            Compiler=str(self.root / "unused-tools/g++.exe"),
            CMake="Invoke-UnexpectedQtGraphsTool", Ninja="Invoke-UnexpectedQtGraphsTool",
            OriginalDll=str(self.sdk_dll), Features=self.features,
            SdkHash="c" * 64, Key="d" * 64, CacheRoot=str(self.cache),
            Entry=str(self.entry), RuntimeDirectory=str(self.runtime), Archive=str(self.archive),
        )
        self.context_path = self.root / "context.json"
        self.write_json(self.context_path, self.context)
        self.receipt = dict(
            schema_version=1, qt_version="6.11.2", recipe_key=self.context["Key"],
            source_commit=self.recipe["source_commit"], source_sha256=self.recipe["source_sha256"],
            patch_sha256=self.recipe["patch_sha256"], sdk_sha256=self.context["SdkHash"],
            dll_sha256=sha256(self.dll_bytes), architecture="AMD64", features=self.features,
            original_exports=150, patched_exports=150, missing_exports=0,
        )
        self.receipt_path = self.runtime / "runtime.json"
        self.write_json(self.receipt_path, self.receipt)
        self.target = self.root / "desktop 中文"

    @staticmethod
    def write_json(path, value):
        path.write_text(json.dumps(value), encoding="utf-8")

    def run_ps(self, body, expect_success=True):
        script = self.root / "probe.ps1"
        script.write_text(
            "$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n"
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n"
            "trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n"
            f". {ps(REPO / 'scripts/qtgraphs-runtime.ps1')}\n"
            f"$context=Get-Content -LiteralPath {ps(self.context_path)} -Raw -Encoding UTF8 | ConvertFrom-Json\n"
            f"$target={ps(self.target)}\n" + body,
            encoding="utf-8-sig",
        )
        run = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
            capture_output=True, timeout=30,
        )
        output = (run.stdout + run.stderr).decode("utf-8", errors="replace")
        if expect_success:
            self.assertEqual(0, run.returncode, output)
        else:
            self.assertNotEqual(0, run.returncode, output)
        return output

    def install(self):
        return self.run_ps("Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null")

    def assert_installed_refused(self):
        return self.run_ps(
            "Assert-InstalledQtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null",
            expect_success=False,
        )

    def assert_install_refused(self):
        output = self.run_ps(
            "Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertFalse(self.target.exists(), "rejected source must not create the destination")
        return output

    def test_install_deploys_verified_dll_source_and_license_materials(self):
        self.target.mkdir()
        sentinel = self.target / "unrelated.txt"
        sentinel.write_bytes(b"existing application data")
        self.install()
        self.run_ps("Assert-InstalledQtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null")
        self.assertEqual(self.dll_bytes, (self.target / "Qt6Graphs.dll").read_bytes())
        deployed_materials = self.target / "docs/licenses/qtgraphs"
        self.assertEqual(
            set(MATERIALS) | {"runtime.json", "qtgraphs-source.tar.gz"},
            {path.name for path in deployed_materials.iterdir()},
        )
        for name in MATERIALS:
            self.assertEqual((self.materials / name).read_bytes(), (deployed_materials / name).read_bytes())
        self.assertEqual(self.receipt, json.loads((deployed_materials / "runtime.json").read_text("utf-8-sig")))
        self.assertEqual(self.archive_bytes, (deployed_materials / "qtgraphs-source.tar.gz").read_bytes())
        self.assertEqual(b"existing application data", sentinel.read_bytes())
        self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())

    def test_deployment_arguments_skip_only_dxc_when_sdk_disables_d3d12(self):
        gui = self.qt_prefix / "bin/Qt6Gui.dll"
        gui.write_bytes(b"\x00\xfffixture\x00Qt was built without Direct3D 12 support.\x00")
        result = self.root / "deployment-arguments.json"
        self.run_ps(
            "$deploymentArguments=@(Get-QtGraphsDeploymentArguments -Context $context)\n"
            f"ConvertTo-Json -InputObject $deploymentArguments -Compress | Set-Content -LiteralPath {ps(result)} -Encoding utf8\n"
        )
        arguments = json.loads(result.read_text("utf-8-sig"))
        # The separate D3D11 system compiler remains part of normal deployment.
        self.assertEqual(["--no-system-dxc-compiler"], arguments)
        self.assertNotIn("--no-system-d3d-compiler", arguments)

    def test_deployment_arguments_preserve_defaults_without_disabled_d3d12_marker(self):
        gui = self.qt_prefix / "bin/Qt6Gui.dll"
        gui.write_bytes(b"\x00\xfffixture QtGui with no disabled Direct3D 12 diagnostic\x00")
        result = self.root / "deployment-arguments.json"
        self.run_ps(
            "$deploymentArguments=@(Get-QtGraphsDeploymentArguments -Context $context)\n"
            f"ConvertTo-Json -InputObject $deploymentArguments -Compress | Set-Content -LiteralPath {ps(result)} -Encoding utf8\n"
        )
        self.assertEqual([], json.loads(result.read_text("utf-8-sig")))

    def test_cached_dll_hash_mismatch_is_rejected_before_deployment(self):
        self.dll.write_bytes(bytes(byte ^ 1 for byte in self.dll_bytes))
        self.assertIn("QtGraphs 缓存与配方、SDK 或文件哈希不一致，拒绝部署。", self.assert_install_refused())

    def test_cached_source_archive_hash_mismatch_is_rejected_before_deployment(self):
        self.archive.write_bytes(b"replaced source archive")
        self.assertIn("QtGraphs 缓存与配方、SDK 或文件哈希不一致，拒绝部署。", self.assert_install_refused())

    def test_cached_receipt_drift_is_rejected_before_deployment(self):
        changes = dict(
            recipe_key="e" * 64, source_commit="e" * 40, source_sha256="e" * 64,
            patch_sha256="e" * 64, sdk_sha256="e" * 64, architecture="I386",
            missing_exports=1, features=self.features[:-1],
        )
        for field, value in changes.items():
            with self.subTest(field=field):
                changed = dict(self.receipt, **{field: value})
                self.write_json(self.receipt_path, changed)
                self.assertIn("QtGraphs 缓存与配方、SDK 或文件哈希不一致，拒绝部署。", self.assert_install_refused())
        self.write_json(self.receipt_path, self.receipt)
        self.install()

    def test_missing_cached_dll_or_receipt_is_rejected_before_deployment(self):
        for path in (self.dll, self.receipt_path, self.archive):
            with self.subTest(missing=path.name):
                original = path.read_bytes()
                path.unlink()
                self.assertIn(path.name, self.assert_install_refused())
                path.write_bytes(original)
        self.install()

    def test_sdk_and_cache_destinations_are_rejected(self):
        for target in (self.qt_prefix, self.qt_prefix / "bin", self.qt_prefix / "new/child",
                       self.cache, self.cache / "new/child"):
            with self.subTest(target=target):
                output = self.run_ps(
                    f"Install-QtGraphsRuntime -DestinationDirectory {ps(target)} -Context $context | Out-Null",
                    expect_success=False,
                )
                self.assertIn("QtGraphs", output)
                self.assertIn("目标不能是", output)
                self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())
                self.assertFalse((target / "docs/licenses/qtgraphs").exists())

    def test_sdk_junction_alias_is_rejected_without_modifying_sdk(self):
        self.run_ps(
            f"New-Item -ItemType Junction -Path $target -Target {ps(self.qt_prefix / 'bin')} | Out-Null"
        )
        output = self.run_ps(
            "Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertIn("QtGraphs", output)
        self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())
        self.assertFalse((self.qt_prefix / "bin/docs/licenses/qtgraphs").exists())

    def test_materials_junction_is_rejected_without_writing_into_sdk(self):
        self.target.mkdir()
        self.run_ps(
            f"New-Item -ItemType Junction -Path (Join-Path $target 'docs') -Target {ps(self.qt_prefix)} | Out-Null"
        )
        output = self.run_ps(
            "Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertIn("QtGraphs", output)
        self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())
        self.assertFalse((self.qt_prefix / "licenses").exists())

    def test_install_rejects_sdk_dll_hard_link_before_destination_writes(self):
        self.target.mkdir()
        deployed_dll = self.target / "Qt6Graphs.dll"
        os.link(self.sdk_dll, deployed_dll)
        output = self.run_ps(
            "Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertIn("QtGraphs 写入目标包含硬链接", output)
        self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())
        self.assertEqual(self.sdk_dll.read_bytes(), deployed_dll.read_bytes())
        self.assertFalse((self.target / "docs").exists())

    def test_install_rejects_material_hard_links_before_destination_writes(self):
        for index, name in enumerate((*MATERIALS, "runtime.json", "qtgraphs-source.tar.gz")):
            with self.subTest(material=name):
                target = self.root / f"desktop shared material {index}"
                materials = target / "docs/licenses/qtgraphs"
                materials.mkdir(parents=True)
                preserved = self.qt_prefix / "bin" / f"shared-material-{index}.bin"
                sentinel = f"temporary SDK material {name} must remain unchanged".encode("ascii")
                preserved.write_bytes(sentinel)
                os.link(preserved, materials / name)
                output = self.run_ps(
                    f"Install-QtGraphsRuntime -DestinationDirectory {ps(target)} -Context $context | Out-Null",
                    expect_success=False,
                )
                self.assertIn("QtGraphs 写入目标包含硬链接", output)
                self.assertEqual(sentinel, preserved.read_bytes())
                self.assertEqual({name}, {path.name for path in materials.iterdir()})
                self.assertFalse((target / "Qt6Graphs.dll").exists())

    def test_writable_directory_guard_rejects_sdk_and_drive_root_for_cache(self):
        # Cache selection calls the guard without CacheRoot: the cache may be
        # newly created, but must never be placed within the read-only SDK.
        for target in (Path(self.root.anchor), self.qt_prefix, self.qt_prefix / "new/cache"):
            with self.subTest(target=target):
                output = self.run_ps(
                    f"Assert-QtGraphsWritableDirectory -Directory {ps(target)} -QtPrefix $context.QtPrefix",
                    expect_success=False,
                )
                self.assertIn("QtGraphs 写入目标不能是根目录或 Qt SDK。", output)
        valid = self.root / "new-independent-cache"
        self.run_ps(f"Assert-QtGraphsWritableDirectory -Directory {ps(valid)} -QtPrefix $context.QtPrefix")
        self.assertFalse(valid.exists(), "the path guard must not create a cache directory")

    def test_writable_directory_guard_rejects_junction_ancestor(self):
        self.run_ps(
            f"New-Item -ItemType Junction -Path $target -Target {ps(self.qt_prefix)} | Out-Null"
        )
        output = self.run_ps(
            "Assert-QtGraphsWritableDirectory -Directory (Join-Path $target 'new/cache') -QtPrefix $context.QtPrefix",
            expect_success=False,
        )
        self.assertIn("QtGraphs 写入目标或祖先包含链接目录", output)
        self.assertFalse((self.qt_prefix / "new").exists())

    def test_install_restores_dll_replaced_by_upstream_deployment(self):
        self.install()
        deployed_dll = self.target / "Qt6Graphs.dll"
        deployed_dll.write_bytes(self.sdk_dll.read_bytes())
        self.assertIn("部署的 QtGraphs 不是当前核验过的专用库。", self.assert_installed_refused())
        self.install()
        self.assertEqual(self.dll_bytes, deployed_dll.read_bytes())
        self.run_ps("Assert-InstalledQtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null")

    def test_install_keeps_matching_dll_while_application_holds_read_lock(self):
        self.install()
        self.run_ps(
            "$dll=Join-Path $target 'Qt6Graphs.dll'\n"
            "[IO.File]::SetLastWriteTimeUtc($dll, [DateTime]::new(2020,1,2,3,4,5,[DateTimeKind]::Utc))\n"
            "$before=[IO.File]::GetLastWriteTimeUtc($dll)\n"
            "$lock=[IO.File]::Open($dll,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)\n"
            "try {\n"
            "  Install-QtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null\n"
            "  if ([IO.File]::GetLastWriteTimeUtc($dll) -ne $before) { throw 'matching DLL was rewritten' }\n"
            "  Assert-InstalledQtGraphsRuntime -DestinationDirectory $target -Context $context | Out-Null\n"
            "} finally { $lock.Dispose() }\n"
        )
        self.assertEqual(self.dll_bytes, (self.target / "Qt6Graphs.dll").read_bytes())

    def test_installed_dll_tamper_is_rejected(self):
        self.install()
        (self.target / "Qt6Graphs.dll").write_bytes(b"modified deployed DLL")
        self.assertIn("部署的 QtGraphs 不是当前核验过的专用库。", self.assert_installed_refused())

    def test_installed_source_archive_tamper_is_rejected(self):
        self.install()
        (self.target / "docs/licenses/qtgraphs/qtgraphs-source.tar.gz").write_bytes(b"modified source archive")
        self.assertIn("部署的 QtGraphs 不是当前核验过的专用库。", self.assert_installed_refused())

    def test_installed_recipe_patch_and_license_tamper_is_rejected(self):
        self.install()
        for name in MATERIALS:
            with self.subTest(material=name):
                path = self.target / "docs/licenses/qtgraphs" / name
                original = path.read_bytes()
                path.write_bytes(original + b"\nmodified deployed material")
                self.assertIn(f"QtGraphs 对应源码/许可证材料不一致：{name}", self.assert_installed_refused())
                path.write_bytes(original)

    def test_installed_receipt_provenance_tamper_is_rejected(self):
        self.install()
        path = self.target / "docs/licenses/qtgraphs/runtime.json"
        changes = dict(
            recipe_key="e" * 64, dll_sha256="e" * 64, source_commit="e" * 40,
            source_sha256="e" * 64, patch_sha256="e" * 64, sdk_sha256="e" * 64,
            architecture="I386", missing_exports=1, features=self.features[:-1],
        )
        for field, value in changes.items():
            with self.subTest(field=field):
                self.write_json(path, dict(self.receipt, **{field: value}))
                self.assertIn("部署的 QtGraphs 不是当前核验过的专用库。", self.assert_installed_refused())
        self.write_json(path, self.receipt)

    def test_initialize_reuses_valid_receipt_without_external_tools(self):
        result = self.root / "initialized-receipt.json"
        self.run_ps(
            "function Invoke-UnexpectedQtGraphsTool { throw 'unexpected QtGraphs build tool' }\n"
            "function tar.exe { throw 'unexpected QtGraphs extraction' }\n"
            "function git { throw 'unexpected QtGraphs patch application' }\n"
            "function Get-QtGraphsPeExports { throw 'unexpected QtGraphs binary inspection' }\n"
            "$receipt=Initialize-QtGraphsRuntime -Context $context\n"
            f"$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath {ps(result)} -Encoding utf8\n"
        )
        self.assertEqual(self.receipt, json.loads(result.read_text("utf-8-sig")))
        self.assertEqual({"runtime", "qtgraphs-source.tar.gz", "prepare.lock"},
                         {path.name for path in self.entry.iterdir()})

    def test_initialize_rejects_stale_receipt_without_rebuilding(self):
        self.dll.write_bytes(b"tampered cached DLL")
        output = self.run_ps(
            "function Invoke-UnexpectedQtGraphsTool { throw 'unexpected QtGraphs build tool' }\n"
            "function tar.exe { throw 'unexpected QtGraphs extraction' }\n"
            "function git { throw 'unexpected QtGraphs patch application' }\n"
            "Initialize-QtGraphsRuntime -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertIn("QtGraphs 缓存与配方、SDK 或文件哈希不一致，拒绝部署。", output)
        self.assertNotIn("unexpected QtGraphs", output)
        self.assertEqual({"runtime", "qtgraphs-source.tar.gz", "prepare.lock"},
                         {path.name for path in self.entry.iterdir()})

    def test_initialize_rejects_cache_junction_before_creating_lock(self):
        alias = self.root / "cache alias"
        self.run_ps(
            f"New-Item -ItemType Junction -Path {ps(alias)} -Target {ps(self.qt_prefix)} | Out-Null"
        )
        output = self.run_ps(
            f"$context.Entry={ps(alias)}\n"
            "$context.RuntimeDirectory=Join-Path $context.Entry 'runtime'\n"
            "$context.Archive=Join-Path $context.Entry 'qtgraphs-source.tar.gz'\n"
            "function Invoke-UnexpectedQtGraphsTool { throw 'unexpected QtGraphs build tool' }\n"
            "function tar.exe { throw 'unexpected QtGraphs extraction' }\n"
            "Initialize-QtGraphsRuntime -Context $context | Out-Null",
            expect_success=False,
        )
        self.assertIn("QtGraphs 写入目标或祖先包含链接目录", output)
        self.assertNotIn("unexpected QtGraphs", output)
        self.assertEqual({"bin"}, {path.name for path in self.qt_prefix.iterdir()})
        self.assertEqual(b"upstream SDK must remain unchanged", self.sdk_dll.read_bytes())

    def test_initialize_rejects_cache_lock_hard_link_before_writes(self):
        preserved = self.qt_prefix / "bin/shared-cache-lock.bin"
        sentinel = b"temporary SDK lock material must remain unchanged"
        preserved.write_bytes(sentinel)
        os.link(preserved, self.entry / "prepare.lock")
        before = {path.name for path in self.entry.iterdir()}
        output = self.run_ps(
            "Initialize-QtGraphsRuntime -Context $context | Out-Null", expect_success=False,
        )
        self.assertIn("QtGraphs 写入目标包含硬链接", output)
        self.assertEqual(sentinel, preserved.read_bytes())
        self.assertEqual(before, {path.name for path in self.entry.iterdir()})

    def test_initialize_rejects_cached_dll_hard_link_before_creating_lock(self):
        preserved = self.qt_prefix / "bin/shared-cached-Qt6Graphs.dll"
        # Keep receipt hashes valid: only the shared-file boundary should refuse
        # this cache, rather than the existing SHA256 mismatch check.
        preserved.write_bytes(self.dll_bytes)
        self.dll.unlink()
        os.link(preserved, self.dll)
        output = self.run_ps(
            "Initialize-QtGraphsRuntime -Context $context | Out-Null", expect_success=False,
        )
        self.assertIn("QtGraphs 写入目标包含硬链接", output)
        self.assertEqual(self.dll_bytes, preserved.read_bytes())
        self.assertFalse((self.entry / "prepare.lock").exists())

    def test_read_recipe_rejects_local_patch_drift(self):
        self.run_ps(f"Read-QtGraphsRecipe -RepoRoot {ps(self.repo)} | Out-Null")
        path = self.materials / "qtgraphs-d3d11.patch"
        path.write_bytes(path.read_bytes() + b"\nchanged local patch")
        output = self.run_ps(
            f"Read-QtGraphsRecipe -RepoRoot {ps(self.repo)} | Out-Null", expect_success=False,
        )
        self.assertIn("QtGraphs 补丁与固定清单不一致。", output)

    def test_repository_recipe_pins_only_d3d11_initialization_change(self):
        # Supplement the deployment probes with a check of the actual recipe,
        # so a fixture recipe cannot hide a checked-in patch/hash mismatch.
        self.run_ps(f"Read-QtGraphsRecipe -RepoRoot {ps(REPO)} | Out-Null")
        patch = (REPO / "docs/licenses/qtgraphs/qtgraphs-d3d11.patch").read_text("utf-8")
        changed = [line for line in patch.splitlines() if line.startswith(("+", "-"))
                   and not line.startswith(("+++", "---"))]
        self.assertEqual([
            "-        QRhiD3D12InitParams params;",
            "-        rhi.reset(QRhi::create(QRhi::D3D12, &params));",
            "+        QRhiD3D11InitParams params;",
            "+        rhi.reset(QRhi::create(QRhi::D3D11, &params));",
        ], changed)

    def test_repository_patch_checkout_preserves_pinned_bytes_with_autocrlf(self):
        git = shutil.which("git")
        self.assertIsNotNone(git, "Git is required to verify checkout filters")
        checkout = self.root / "isolated git checkout"
        checkout.mkdir()
        (checkout / ".gitattributes").write_bytes((REPO / ".gitattributes").read_bytes())
        patch_path = "docs/licenses/qtgraphs/qtgraphs-d3d11.patch"
        patch = REPO / patch_path

        def run_git(*arguments):
            run = subprocess.run(
                [git, "-C", str(checkout), "-c", "core.autocrlf=true", *arguments],
                capture_output=True, timeout=30,
            )
            self.assertEqual(0, run.returncode, run.stderr.decode("utf-8", errors="replace"))
            return run.stdout

        run_git("init", "--quiet")
        # Store the untouched bytes only in this temporary repository, then
        # run the actual checkout filter with the project's attribute rules.
        blob = run_git("hash-object", "-w", "--no-filters", str(patch)).decode("ascii").strip()
        filtered = run_git("cat-file", "--filters", f"--path={patch_path}", blob)
        actual_recipe = json.loads((REPO / "docs/licenses/qtgraphs/dependency-manifest.json").read_text("utf-8"))
        self.assertEqual(actual_recipe["patch_sha256"], sha256(filtered))
        self.assertEqual(patch.read_bytes(), filtered)


if __name__ == "__main__":
    unittest.main(verbosity=2)
