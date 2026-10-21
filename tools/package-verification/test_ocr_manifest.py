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


if __name__ == "__main__":
    unittest.main(verbosity=2)
