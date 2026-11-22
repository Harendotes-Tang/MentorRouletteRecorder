#!/usr/bin/env python3
"""The optional ``calibration`` template section, mirrored against
ProfileLoader.ReadCalibration / CheckStatusRules in src/Collector/Protocol/Profiles/ProfileLoader.cs
so the Python validator and the Collector agree (docs/protocol-profile-format.md section 11.1).
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


class FinderReplyTests(unittest.TestCase):
    """calibration.finder_reply, mirrored against ProfileLoader.ReadFinderReply: the two-message
    template of GLOBAL 2026.11.03, where the reply and the match announcement are different messages.
    The document is the CN template reshaped that way - only the rules are under test here."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="mr-profile-finder-reply-")
        self.addCleanup(self.directory.cleanup)
        source = Path(validate.REPO) / "protocol-profiles/cn/cn.2026.08.05.json"
        document = json.loads(source.read_text(encoding="utf-8"))
        by_name = {message["name"]: message for message in document["messages"]}
        by_name["CONTENT_FINDER_POP"]["fields"] = [{
            "name": "roulette_id", "offset": 2, "type": "u16", "endian": "little",
            "constraints": {"min": 0, "max": 255}}]
        by_name["PLAYER_JOB"]["expected_length"] = 40
        by_name["PLAYER_JOB"]["fields"] = [
            {"name": "finder_state", "offset": 0, "type": "u8", "role": "selector",
             "constraints": {"in": [1]}},
            {"name": "job_id", "offset": 1, "type": "u8", "constraints": {"min": 1, "max": 43}},
        ]
        document["calibration"]["finder_reply"] = {
            "direction": "SERVER_TO_CLIENT",
            "expected_length": 40,
            "roulette_field": {"name": "roulette_id", "offset": 16, "type": "u8",
                               "constraints": {"min": 1, "max": 255}},
            "player_job": True,
        }
        document["provenance"]["evidence"].append({
            "field": "calibration.finder_reply.roulette_field",
            "method": "OBSERVED_LOCAL_TRAFFIC",
            "recorded_at_utc": "2026-11-22T11:00:00.000Z",
            "sample_count": 2,
            "url": None,
            "note": "test",
        })
        self.document = document
        self.path = Path(self.directory.name) / "cn.2026.08.05.json"

    def validate(self, document):
        document["profile_sha256"] = validate.profile_hash(
            {k: v for k, v in document.items() if k != "profile_sha256"})
        self.path.write_text(json.dumps(document), encoding="utf-8")
        return validate.validate(str(self.path), stamp=False)

    def test_two_message_template_is_valid(self):
        code, errors = self.validate(copy.deepcopy(self.document))

        self.assertEqual(0, code, errors)

    def test_reply_needs_evidence_of_its_own(self):
        document = copy.deepcopy(self.document)
        document["provenance"]["evidence"] = [
            item for item in document["provenance"]["evidence"]
            if item["field"] != "calibration.finder_reply.roulette_field"]

        code, errors = self.validate(document)

        self.assertEqual(1, code)
        self.assertIn(
            "VERIFIED profile lacks evidence for calibration.finder_reply.roulette_field", errors)

    def test_reply_that_is_the_pop_itself_is_refused(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_reply"]["roulette_field"] = {
            "name": "roulette_id", "offset": 2, "type": "u16", "endian": "little"}

        code, errors = self.validate(document)

        self.assertEqual(1, code)
        self.assertTrue(any("describes CONTENT_FINDER_POP itself" in error for error in errors), errors)

    def test_reply_with_a_pop_that_declares_a_selector_is_refused(self):
        document = copy.deepcopy(self.document)
        pop = next(m for m in document["messages"] if m["name"] == "CONTENT_FINDER_POP")
        pop["fields"].append({"name": "finder_state", "offset": 9, "type": "u8", "role": "selector",
                              "constraints": {"in": [3]}})

        code, errors = self.validate(document)

        self.assertEqual(1, code)
        self.assertTrue(any("must declare no selector" in error for error in errors), errors)

    def test_player_job_needs_a_job_message_of_the_replys_shape(self):
        document = copy.deepcopy(self.document)
        job = next(m for m in document["messages"] if m["name"] == "PLAYER_JOB")
        job["expected_length"] = 16
        job["fields"] = [{"name": "job_id", "offset": 0, "type": "u8"}]

        code, errors = self.validate(document)

        self.assertEqual(1, code)
        self.assertIn("calibration.finder_reply.player_job: needs a PLAYER_JOB message of the "
                      "reply's direction and exact length", errors)

    def test_reply_roulette_field_gets_the_requests_checks(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_reply"]["roulette_field"]["offset"] = 40

        code, errors = self.validate(document)

        self.assertEqual(1, code)
        self.assertIn(
            "calibration.finder_reply.roulette_field: reads past the declared reply length", errors)


if __name__ == "__main__":
    unittest.main(verbosity=2)
