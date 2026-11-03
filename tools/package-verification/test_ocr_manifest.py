#!/usr/bin/env python3
"""The OCR build requirements must describe the frozen payload the manifest pins.

docs/licenses/ocr/dependency-manifest.json pins the shipped ocr/local-ai-ocr.exe,
which tools/LocalAiOcr/build_worker.py built from tools/LocalAiOcr/requirements-build.txt.
A version bump in the requirements file (by hand or by Dependabot) changes nothing in the
payload that ships, so it must come with a rebuilt payload and an updated manifest.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
REQUIREMENTS = REPO / "tools" / "LocalAiOcr" / "requirements-build.txt"
MANIFEST = REPO / "docs" / "licenses" / "ocr" / "dependency-manifest.json"
# Python distributions in the manifest; CPython, models and native notices have other scopes or sources.
DISTRIBUTION_SCOPES = {"runtime", "build-provenance-notices"}
PYPI = "https://pypi.org/project/"
REMEDY = ("tools/LocalAiOcr/requirements-build.txt no longer describes the OCR payload pinned by "
          "docs/licenses/ocr/dependency-manifest.json. Revert the requirement, or rebuild the payload "
          "(docs/build-and-package.md) and update the manifest in the same change.")
WORKER = REPO / "scripts" / "local-ai-ocr-worker.py"
BUILDER = REPO / "tools" / "LocalAiOcr" / "build_worker.py"
SOURCE_REMEDY = ("{name} differs from the source the shipped ocr/local-ai-ocr.exe was frozen from, so "
                 "test_worker.py would test code that does not ship. Revert the change, or rebuild the "
                 "payload (docs/build-and-package.md) and update the manifest in the same change.")


def canonical(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def requirement_pins(text: str) -> dict[str, str]:
    """Parse the file the way build_worker.py does: one `name==version` per non-comment line."""
    pins = {}
    for line in text.splitlines():
        if line.strip() and not line.startswith("#"):
            name, version = line.split("==")
            pins[canonical(name)] = version
    return pins


def source_digests(path: Path) -> set[str]:
    """The SHA-256 of a source file's text in both line-ending forms, LF and CRLF, from any checkout.

    build_worker.py records the raw bytes of the worker it froze, so the recorded value depends on
    the checkout it ran in: today's manifest holds the CRLF form of a Windows checkout, while a
    rebuild from an LF checkout records the LF form. Either form of the same text is accepted
    (re-review RV5-5). TODO at the next payload rebuild: make build_worker.py record the
    LF-normalised digest, as requirements_sha256 already is, and accept only that form here.
    """
    text = path.read_bytes().replace(b"\r\n", b"\n")
    return {hashlib.sha256(text).hexdigest(), hashlib.sha256(text.replace(b"\n", b"\r\n")).hexdigest()}


def manifest_pins(manifest: dict) -> dict[str, str]:
    return {canonical(component["name"]): component["version"] for component in manifest["components"]
            if component.get("scope") in DISTRIBUTION_SCOPES
            and str(component.get("source", "")).startswith(PYPI)}


class OcrManifestTests(unittest.TestCase):
    def setUp(self):
        self.requirements = REQUIREMENTS.read_bytes()
        self.manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    def test_requirements_hash_matches_the_shipped_payload(self):
        # build_worker.py records the SHA-256 of the file it built from; Git stores it with LF.
        normalized = self.requirements.replace(b"\r\n", b"\n")
        self.assertEqual(hashlib.sha256(normalized).hexdigest(),
                         self.manifest["runtime"]["requirements_sha256"], REMEDY)

    def test_every_recorded_distribution_is_pinned_at_its_shipped_version(self):
        # A pin the manifest records no component for (today flatbuffers) is held by the hash above.
        pins = requirement_pins(self.requirements.decode("utf-8"))
        recorded = manifest_pins(self.manifest)
        self.assertTrue(recorded)
        self.assertEqual({name: pins.get(name) for name in recorded}, recorded, REMEDY)

    def test_worker_and_builder_sources_are_the_ones_the_payload_was_built_from(self):
        # N3-2 (audit 2026-11-03): both hashes were written and never read, so a fixed worker passed
        # test_worker.py and every runtime check while package.ps1 shipped the old frozen exe.
        for path, field in ((WORKER, "worker_source_sha256"), (BUILDER, "builder_source_sha256")):
            with self.subTest(field=field):
                self.assertIn(self.manifest["runtime"][field], source_digests(path),
                              SOURCE_REMEDY.format(name=path.name))

    def test_either_line_ending_form_of_the_same_source_is_accepted(self):
        # RV5-5 (re-review): build_worker.py hashes the bytes of whatever checkout it runs in, so an
        # honest rebuild from an LF checkout records the LF digest; both forms of one text pass.
        crlf_bytes = b"print('a')\r\nprint('b')\r\n"
        lf_bytes = crlf_bytes.replace(b"\r\n", b"\n")
        expected = {hashlib.sha256(crlf_bytes).hexdigest(), hashlib.sha256(lf_bytes).hexdigest()}
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "worker.py"
            for checkout in (crlf_bytes, lf_bytes):
                with self.subTest(crlf=checkout == crlf_bytes):
                    source.write_bytes(checkout)
                    self.assertEqual(expected, source_digests(source))
            source.write_bytes(lf_bytes.replace(b"'b'", b"'c'"))
            self.assertTrue(expected.isdisjoint(source_digests(source)))


if __name__ == "__main__":
    unittest.main(verbosity=2)
