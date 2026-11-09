#!/usr/bin/env python3
"""Exercise private-Qt SDK provenance with local bytes and configure-only fixtures."""
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
MODULES = ("Core", "Gui", "Qml", "Quick", "QuickTemplates2")


def ps(value: Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def cmake_literal(value: Path) -> str:
    return "[==[" + value.as_posix() + "]==]"


@unittest.skipUnless(os.name == "nt", "Windows Qt DLL deployment")
class QtPrivateRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required")
        self.cmake = shutil.which("cmake")
        self.ninja = shutil.which("ninja")
        self.temp = tempfile.TemporaryDirectory(prefix="mr private ' sdk ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.build = self.root / "build 中文"
        self.target = self.root / "deployed 中文"
        self.build.mkdir()
        self.target.mkdir()
        self.payloads = {}
        self.manifest = {}
        self.reset_runtime()
        self.cmake_runs = 0

    def reset_runtime(self, suffixes=("",)):
        for file in self.target.glob("*.dll"):
            file.unlink()
        self.payloads = {
            f"Qt6{module}{suffix}.dll": f"Qt 6.11.2 SDK fixture: {module}:{suffix}".encode()
            for module in MODULES for suffix in suffixes
        }
        for name, payload in self.payloads.items():
            (self.target / name).write_bytes(payload)
        self.manifest = {
            "schema_version": 1,
            "qt_version": "6.11.2",
            "dll_sha256": {name: hashlib.sha256(data).hexdigest()
                           for name, data in self.payloads.items()},
        }
        self.write_manifest()

    def write_manifest(self):
        (self.build / "qt-private-runtime.json").write_text(
            json.dumps(self.manifest), encoding="utf-8",
        )

    def run_ps(self, body, *, success=True):
        script = self.root / "probe.ps1"
        script.write_text(
            "$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n"
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n"
            "trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n"
            f". {ps(REPO / 'scripts/qt-private-runtime.ps1')}\n"
            f"$build={ps(self.build)}\n$target={ps(self.target)}\n" + body,
            encoding="utf-8-sig",
        )
        run = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
            capture_output=True, timeout=30,
        )
        output = (run.stdout + run.stderr).decode("utf-8", errors="replace")
        if success:
            self.assertEqual(0, run.returncode, output)
        else:
            self.assertNotEqual(0, run.returncode, output)
        return output

    def check_runtime(self, *, success=True):
        return self.run_ps(
            "$manifest=Read-QtPrivateRuntime -BuildDirectory $build\n"
            "Assert-QtPrivateRuntime -Manifest $manifest -Directory $target\n",
            success=success,
        )

    def test_matching_release_sdk_passes_without_writing_files(self):
        sentinel = self.target / "unrelated.txt"
        sentinel.write_bytes(b"existing application data")
        before = {file.name: file.read_bytes() for file in self.target.iterdir()}
        manifest_bytes = (self.build / "qt-private-runtime.json").read_bytes()
        self.check_runtime()
        self.assertEqual(before, {file.name: file.read_bytes() for file in self.target.iterdir()})
        self.assertEqual(manifest_bytes, (self.build / "qt-private-runtime.json").read_bytes())

    def test_debug_only_sdk_is_supported(self):
        self.reset_runtime(("d",))
        self.check_runtime()

    def test_both_debug_and_release_are_checked_when_present(self):
        self.reset_runtime(("", "d"))
        self.check_runtime()
        (self.target / "Qt6Quickd.dll").write_bytes(b"other SDK, also version 6.11.2")
        output = self.check_runtime(success=False)
        self.assertIn("Qt6Quickd.dll", output)
        self.assertIn("拒绝混用私有 ABI", output)

    def test_same_version_different_sdk_or_tampered_dll_is_rejected(self):
        for module in MODULES:
            with self.subTest(module=module):
                path = self.target / f"Qt6{module}.dll"
                path.write_bytes(b"different build of SDK version 6.11.2")
                output = self.check_runtime(success=False)
                self.assertIn(path.name, output)
                self.assertIn("拒绝混用私有 ABI", output)
                self.assertEqual(b"different build of SDK version 6.11.2", path.read_bytes())
                path.write_bytes(self.payloads[path.name])

    def test_extra_unrecorded_debug_variant_is_rejected(self):
        (self.target / "Qt6Cored.dll").write_bytes(b"unrecorded debug runtime")
        output = self.check_runtime(success=False)
        self.assertIn("Qt6Cored.dll", output)
        self.assertIn("拒绝混用私有 ABI", output)

    def test_missing_deployed_module_is_rejected(self):
        for module in MODULES:
            with self.subTest(module=module):
                path = self.target / f"Qt6{module}.dll"
                path.unlink()
                output = self.check_runtime(success=False)
                self.assertIn(f"部署的 Qt 私有接口运行时缺少 {module}", output)
                path.write_bytes(self.payloads[path.name])

    def test_missing_manifest_is_rejected(self):
        (self.build / "qt-private-runtime.json").unlink()
        self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)

    def test_malformed_json_or_nonobject_manifest_is_rejected(self):
        for text in ("{", "null", "[]", '"manifest"'):
            with self.subTest(manifest=text):
                (self.build / "qt-private-runtime.json").write_text(text, encoding="utf-8")
                self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)

    def test_invalid_schema_or_qt_version_is_rejected(self):
        for field, value in (("schema_version", 2), ("qt_version", "7.0.0"),
                             ("qt_version", "6.11"), ("qt_version", "6.11.2-custom")):
            with self.subTest(field=field, value=value):
                original = self.manifest[field]
                self.manifest[field] = value
                self.write_manifest()
                output = self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)
                self.assertIn("清单格式无效", output)
                self.manifest[field] = original

    def test_invalid_filename_or_path_in_manifest_is_rejected(self):
        digest = self.manifest["dll_sha256"]["Qt6Core.dll"]
        for name in ("../Qt6Core.dll", "sub/Qt6Core.dll", "C:/Qt6Core.dll", "Qt6Network.dll"):
            with self.subTest(name=name):
                self.manifest["dll_sha256"][name] = digest
                self.write_manifest()
                output = self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)
                self.assertIn("无效文件或哈希", output)
                del self.manifest["dll_sha256"][name]

    def test_malformed_hash_is_rejected(self):
        for digest in ("a" * 63, "g" * 64, "a" * 65, 1, None):
            with self.subTest(hash=digest):
                self.manifest["dll_sha256"]["Qt6Core.dll"] = digest
                self.write_manifest()
                output = self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)
                self.assertIn("无效文件或哈希", output)

    def test_missing_manifest_module_is_rejected(self):
        for module in MODULES:
            with self.subTest(module=module):
                name = f"Qt6{module}.dll"
                digest = self.manifest["dll_sha256"].pop(name)
                self.write_manifest()
                output = self.run_ps("Read-QtPrivateRuntime -BuildDirectory $build | Out-Null", success=False)
                self.assertIn(f"清单缺少 {module}", output)
                self.manifest["dll_sha256"][name] = digest

    def fake_sdk(self, sdk, private_versions=None):
        # This is a package-search/receipt fixture, not a Qt installation or ABI test.
        package_root = sdk / "lib/cmake"
        for module in ("", *MODULES):
            (package_root / f"Qt6{module}").mkdir(parents=True, exist_ok=True)
        (sdk / "bin").mkdir(parents=True, exist_ok=True)
        for name, data in self.payloads.items():
            (sdk / "bin" / name).write_bytes(data)
        for module in ("Quick", "QuickTemplates2"):
            package = f"Qt6{module}Private"
            directory = package_root / package
            directory.mkdir(parents=True, exist_ok=True)
            version = (private_versions or {}).get(module, "6.11.2")
            (directory / f"{package}Config.cmake").write_text(
                'if(NOT QT_FIND_PRIVATE_MODULES)\nmessage(FATAL_ERROR "private lookup not enabled")\nendif()\n'
                f'set({package}_FOUND TRUE)\nset({package}_VERSION "{version}")\n', encoding="utf-8",
            )
            (directory / f"{package}ConfigVersion.cmake").write_text(
                f'set(PACKAGE_VERSION "{version}")\n'
                'if(PACKAGE_FIND_VERSION STREQUAL PACKAGE_VERSION)\n'
                'set(PACKAGE_VERSION_COMPATIBLE TRUE)\nset(PACKAGE_VERSION_EXACT TRUE)\n'
                'else()\nset(PACKAGE_VERSION_COMPATIBLE FALSE)\nendif()\n', encoding="utf-8",
            )

    def configure(self, sdk, extra="", *, success=True):
        if not self.cmake or not self.ninja:
            self.skipTest("CMake and Ninja are required for configure-only guard probes")
        self.cmake_runs += 1
        source = self.root / f"cmake source {self.cmake_runs}"
        build = self.root / f"cmake build {self.cmake_runs}"
        source.mkdir()
        root = sdk / "lib/cmake"
        script = (
            'cmake_minimum_required(VERSION 3.19)\nproject(QtPrivateGuard LANGUAGES NONE)\n'
            f'set(Qt6_DIR {cmake_literal(root / "Qt6")})\nset(Qt6_VERSION "6.11.2")\n'
            'set(QT_FIND_PRIVATE_MODULES FALSE)\n'
        )
        for module in MODULES:
            script += f'set(Qt6{module}_DIR {cmake_literal(root / f"Qt6{module}")})\n'
            script += f'set(Qt6{module}_VERSION "6.11.2")\n'
        script += extra + "\n"
        script += f'include({cmake_literal(REPO / "src/Desktop/cmake/QtPrivateModules.cmake")})\n'
        script += (
            'mr_find_qt_private_modules()\n'
            'if(QT_FIND_PRIVATE_MODULES)\nmessage(FATAL_ERROR "private lookup leaked")\nendif()\n'
        )
        (source / "CMakeLists.txt").write_text(script, encoding="utf-8")
        run = subprocess.run(
            [self.cmake, "-S", str(source), "-B", str(build), "-G", "Ninja",
             f"-DCMAKE_MAKE_PROGRAM={self.ninja}"],
            capture_output=True, timeout=30,
        )
        output = (run.stdout + run.stderr).decode("utf-8", errors="replace")
        if success:
            self.assertEqual(0, run.returncode, output)
        else:
            self.assertNotEqual(0, run.returncode, output)
            self.assertFalse((build / "qt-private-runtime.json").exists())
        return build, output

    def test_cmake_matching_sdk_produces_manifest_accepted_by_real_reader(self):
        sdk = self.root / "SDK matching"
        self.fake_sdk(sdk)
        build, _ = self.configure(sdk)
        receipt = json.loads((build / "qt-private-runtime.json").read_text("utf-8"))
        self.assertEqual(self.manifest, receipt)
        self.build = build
        self.target = sdk / "bin"
        self.check_runtime()

    def test_cmake_rejects_public_module_from_other_sdk_or_version(self):
        sdk = self.root / "SDK selected"
        other = self.root / "SDK other"
        self.fake_sdk(sdk)
        self.fake_sdk(other)
        for extra in (f'set(Qt6Quick_DIR {cmake_literal(other / "lib/cmake/Qt6Quick")})',
                      'set(Qt6Quick_VERSION "6.11.3")'):
            with self.subTest(override=extra):
                _, output = self.configure(sdk, extra, success=False)
                self.assertIn("Qt Quick must match the selected Qt SDK and version", output)

    def test_cmake_rejects_private_module_from_other_sdk(self):
        sdk = self.root / "SDK selected"
        other = self.root / "SDK other"
        self.fake_sdk(sdk)
        self.fake_sdk(other)
        _, output = self.configure(
            sdk, f'set(Qt6QuickPrivate_DIR {cmake_literal(other / "lib/cmake/Qt6QuickPrivate")})', success=False,
        )
        self.assertIn("Qt QuickPrivate must come from the public module's SDK", output)

    def test_cmake_rejects_private_version_different_from_public_module(self):
        for module in ("Quick", "QuickTemplates2"):
            with self.subTest(module=module):
                sdk = self.root / f"SDK mismatched {module}"
                self.fake_sdk(sdk, {module: "6.11.3"})
                _, output = self.configure(sdk, success=False)
                self.assertIn(f"Qt6{module}Private", output)
                self.assertIn("6.11.2", output)
                self.assertIn("6.11.3", output)


if __name__ == "__main__":
    unittest.main(verbosity=2)
