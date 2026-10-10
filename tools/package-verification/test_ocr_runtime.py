#!/usr/bin/env python3
"""Exercise OCR deployment refusal boundaries with a tiny pinned local payload."""
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


def ps(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows OCR deployment")
class OcrRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required")
        self.temp = tempfile.TemporaryDirectory(prefix="mr ocr staging ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repository"
        self.runtime = self.root / "source"
        self.manifest_path = self.repo / "docs/licenses/ocr/dependency-manifest.json"
        self.manifest_path.parent.mkdir(parents=True)
        files = []
        for name in ("local-ai-ocr.exe", "models/PP-OCRv6_det_small.onnx",
                     "models/PP-OCRv6_rec_small.onnx", "models/ch_ppocr_mobile_v2.0_cls_mobile.onnx",
                     "licenses/fixture.txt"):
            data = ("pinned " + name).encode()
            path = self.runtime / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            files.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                              component="fixture", source="fixture-local-input"))
        self.manifest = dict(schema_version=2, cache_id="fixture-1", files=files,
                             runtime=dict(version="fixture", entry_point="local-ai-ocr.exe",
                                          model_directory="models"),
                             components=[dict(id="fixture", name="Fixture", licence="MIT",
                                              source="https://example.test/fixture", texts=["fixture.txt"],
                                              embedded_in=[])])
        self.write_manifest()

    def write_manifest(self):
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")

    def run_ps(self, body, expect_success=True):
        script = self.root / "probe.ps1"
        script.write_text(
            "$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n"
            "trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n"
            f". {ps(REPO / 'scripts/ocr-runtime.ps1')}\n"
            f"$repo={ps(self.repo)}\n$source={ps(self.runtime)}\n" + body,
            encoding="utf-8-sig")
        run = subprocess.run([self.shell, "-NoProfile", "-NonInteractive", "-File", str(script)],
                             capture_output=True, timeout=30)
        output = (run.stdout + run.stderr).decode("utf-8", errors="replace")
        if expect_success:
            self.assertEqual(0, run.returncode, output)
        else:
            self.assertNotEqual(0, run.returncode, output)
        return output

    def test_install_copies_only_pinned_files_and_manifest(self):
        target = self.root / "desktop"
        self.run_ps(f"$env:MR_OCR_DIR=$source\nInstall-OcrRuntime -DestinationDirectory {ps(target)} -RepoRoot $repo")
        expected = {file["path"] for file in self.manifest["files"]} | {"OCR-DEPENDENCIES.json"}
        self.assertEqual(expected, {path.relative_to(target / "ocr").as_posix()
                                    for path in (target / "ocr").rglob("*") if path.is_file()})
        for file in self.manifest["files"]:
            self.assertEqual(file["sha256"], hashlib.sha256((target / "ocr" / file["path"]).read_bytes()).hexdigest())

    def test_registered_legacy_runtime_is_replaced_without_bundling_old_engine(self):
        target = self.root / "desktop"
        self.run_ps(f"$env:MR_OCR_DIR=$source\nInstall-OcrRuntime -DestinationDirectory {ps(target)} -RepoRoot $repo")
        old = json.loads(json.dumps(self.manifest))
        old["schema_version"] = 1
        old["runtime"]["version"] = "legacy-tesseract"
        old["files"].append(dict(path="tesseract.exe"))
        (target / "ocr/tesseract.exe").write_bytes(b"old engine")
        (target / "ocr/OCR-DEPENDENCIES.json").write_text(json.dumps(old), encoding="utf-8")
        self.run_ps(f"$env:MR_OCR_DIR=$source\nInstall-OcrRuntime -DestinationDirectory {ps(target)} -RepoRoot $repo")
        self.assertFalse((target / "ocr/tesseract.exe").exists())
        self.assertTrue((target / "ocr/local-ai-ocr.exe").is_file())

    def test_tampered_missing_and_extra_files_are_rejected_before_destination_changes(self):
        destination = self.root / "destination"
        destination.mkdir()
        sentinel = destination / "preserved.txt"
        sentinel.write_text("preserve me")
        victim = self.runtime / "local-ai-ocr.exe"
        original = victim.read_bytes()
        victim.write_bytes(bytes(byte ^ 1 for byte in original))
        body = f"Copy-PinnedOcrRuntime -SourceDirectory $source -TargetDirectory {ps(destination)} -RepoRoot $repo"
        self.run_ps(body, False)
        self.assertEqual("preserve me", sentinel.read_text())
        victim.unlink()
        self.run_ps(body, False)
        self.assertEqual("preserve me", sentinel.read_text())
        victim.write_bytes(original)
        (self.runtime / "undeclared.dll").write_bytes(b"extra")
        self.run_ps(body, False)
        self.assertEqual("preserve me", sentinel.read_text())

    def test_bad_manifest_paths_and_uncovered_components_are_rejected(self):
        original = json.loads(json.dumps(self.manifest))
        for path in ("../escape.dll", "models/../escape.dll", "C:/escape.dll", "models/./x", "models\\x"):
            with self.subTest(path=path):
                self.manifest = json.loads(json.dumps(original))
                self.manifest["files"][0]["path"] = path
                self.write_manifest()
                self.run_ps("Read-OcrDependencyManifest -RepoRoot $repo | Out-Null", False)
        self.manifest = json.loads(json.dumps(original))
        self.manifest["files"][0]["component"] = "unlicensed"
        self.write_manifest()
        self.run_ps("Read-OcrDependencyManifest -RepoRoot $repo | Out-Null", False)
        self.manifest = json.loads(json.dumps(original))
        self.manifest["components"][0]["texts"] = ["missing.txt"]
        self.write_manifest()
        self.run_ps("Read-OcrDependencyManifest -RepoRoot $repo | Out-Null", False)

    def test_source_target_overlap_is_rejected_without_removal(self):
        for target in (self.runtime, self.runtime / "child", self.root):
            with self.subTest(target=target):
                self.run_ps(f"Copy-PinnedOcrRuntime -SourceDirectory $source -TargetDirectory {ps(target)} -RepoRoot $repo", False)
                self.assertTrue((self.runtime / "local-ai-ocr.exe").is_file())

    def test_links_in_source_or_target_are_rejected_without_touching_referent(self):
        outside = self.root / "outside"
        outside.mkdir()
        sentinel = outside / "preserved.txt"
        sentinel.write_text("outside data")
        target = self.root / "target"
        output = self.run_ps(
            f"New-Item -ItemType Junction -Path {ps(target)} -Target {ps(outside)} | Out-Null\n"
            f"Copy-PinnedOcrRuntime -SourceDirectory $source -TargetDirectory {ps(target)} -RepoRoot $repo", False)
        self.assertIn("OCR", output)
        self.assertEqual("outside data", sentinel.read_text())
        self.run_ps(
            f"New-Item -ItemType Junction -Path (Join-Path $source 'linked') -Target {ps(outside)} | Out-Null\n"
            "Assert-OcrRuntime -RuntimeDirectory $source -RepoRoot $repo | Out-Null", False)
        self.assertEqual("outside data", sentinel.read_text())

    def test_deployed_manifest_tamper_is_rejected(self):
        deployed = self.runtime / "OCR-DEPENDENCIES.json"
        deployed.write_text("{}", encoding="utf-8")
        self.run_ps("Assert-OcrRuntime -RuntimeDirectory $source -RepoRoot $repo | Out-Null", False)

    def test_valid_source_does_not_erase_unregistered_target_files(self):
        target = self.root / "configured-existing-folder"
        target.mkdir()
        sentinel = target / "user-file.txt"
        sentinel.write_text("preserve")
        self.run_ps(f"Copy-PinnedOcrRuntime -SourceDirectory $source -TargetDirectory {ps(target)} -RepoRoot $repo", False)
        self.assertEqual("preserve", sentinel.read_text())
        # An otherwise valid deployment marker cannot claim an extra local file.
        target = self.root / "desktop"
        self.run_ps(f"$env:MR_OCR_DIR=$source\nInstall-OcrRuntime -DestinationDirectory {ps(target)} -RepoRoot $repo")
        sentinel = target / "ocr/user-file.txt"
        sentinel.write_text("preserve")
        self.run_ps(f"$env:MR_OCR_DIR=$source\nInstall-OcrRuntime -DestinationDirectory {ps(target)} -RepoRoot $repo", False)
        self.assertEqual("preserve", sentinel.read_text())

    def test_cache_id_cannot_escape_and_relative_environment_override_is_resolved(self):
        self.run_ps(
            "$env:MR_OCR_DIR='relative/runtime'\n"
            "if ((Get-OcrRuntimeDirectory -RepoRoot $repo) -ne (Join-Path $repo 'relative/runtime')) { throw 'relative override failed' }")
        self.manifest["cache_id"] = "../escape"
        self.write_manifest()
        self.run_ps("Read-OcrDependencyManifest -RepoRoot $repo | Out-Null", False)


if __name__ == "__main__":
    unittest.main(verbosity=2)
