#!/usr/bin/env python3
"""The optional ``calibration`` template section, mirrored against
ProfileLoader.ReadCalibration / CheckStatusRules in src/Collector/Protocol/Profiles/ProfileLoader.cs
so the Python validator and the Collector agree (docs/protocol-profile-format.md section 3.6).
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

import validate


class CalibrationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="mr-profile-calibration-")
        self.addCleanup(self.directory.cleanup)
        # Not named "cn" on purpose: the profile_id/region/directory rules are exercised
        # elsewhere, and are irrelevant to the calibration rules under test here.
        source = Path(validate.REPO) / "protocol-profiles/cn/cn.2026.08.05.json"
        self.document = json.loads(source.read_text(encoding="utf-8"))
        self.path = Path(self.directory.name) / "cn.2026.08.05.json"

    def write(self, document=None):
        document = self.document if document is None else document
        document["profile_sha256"] = validate.profile_hash(
            {k: v for k, v in document.items() if k != "profile_sha256"})
        self.path.write_text(json.dumps(document), encoding="utf-8")

    def test_shipped_calibration_template_is_valid(self):
        self.write()

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(0, code, errors)

    def test_calibration_is_refused_on_a_candidate_profile(self):
        document = copy.deepcopy(self.document)
        document["compatibility_status"] = "CANDIDATE"
        document["messages"] = []
        document["mentor_roulette_id"] = None
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn("only a VERIFIED profile may carry a calibration template", errors)

    def test_calibration_is_refused_on_a_synthetic_profile(self):
        document = copy.deepcopy(self.document)
        document["compatibility_status"] = "SYNTHETIC"
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn("only a VERIFIED profile may carry a calibration template", errors)

    def test_bytes_roulette_field_is_refused(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_request"]["roulette_field"] = {
            "name": "roulette_id",
            "offset": 0,
            "type": "bytes",
            "length": 1,
        }
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn(
            "calibration.finder_request.roulette_field: must be u8, u16 or u32", errors)

    def test_out_of_bounds_roulette_field_is_refused(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_request"]["roulette_field"]["offset"] = 24
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn(
            "calibration.finder_request.roulette_field: reads past the declared request length",
            errors)

    def test_verified_calibration_without_evidence_is_refused(self):
        document = copy.deepcopy(self.document)
        document["provenance"]["evidence"] = [
            item for item in document["provenance"]["evidence"]
            if item["field"] != "calibration.finder_request.roulette_field"
        ]
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn(
            "VERIFIED profile lacks evidence for calibration.finder_request.roulette_field",
            errors)

    def test_synthetic_evidence_does_not_count_towards_calibration_coverage(self):
        document = copy.deepcopy(self.document)
        for item in document["provenance"]["evidence"]:
            if item["field"] == "calibration.finder_request.roulette_field":
                item["method"] = "SYNTHETIC"
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn(
            "VERIFIED profile lacks evidence for calibration.finder_request.roulette_field",
            errors)


if __name__ == "__main__":
    unittest.main(verbosity=2)
