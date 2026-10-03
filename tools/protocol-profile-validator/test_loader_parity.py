#!/usr/bin/env python3
"""Profiles the Collector refuses must not pass validate.py (audit 2026-10-03, ON2-7).

Each case below was accepted by the Python validator and refused by
src/Collector/Protocol/Profiles/ProfileLoader.cs, JsonSchemaValidator.cs and CanonicalJson.cs:

  * the calibration roulette field skipped the checks ReadCalibration gets from ReadFields
    (a length on a fixed-width field, constraint max below min);
  * an integer outside the signed 64-bit range is the wrong type for JsonSchemaValidator
    ("integer" means TryGetInt64);
  * a duplicated key was folded into one by json.load, so a hash stamped over the folded
    document matched in Python while CanonicalJson writes both entries and never matches;
  * maxLength counted code points, where C# counts UTF-16 code units.
"""

from __future__ import annotations

import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest

import validate


class LoaderParityTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="mr-profile-parity-")
        self.addCleanup(self.directory.cleanup)
        # Not named "cn": the directory rules are irrelevant to the cases under test.
        source = Path(validate.REPO) / "protocol-profiles/cn/cn.2026.08.05.json"
        self.document = json.loads(source.read_text(encoding="utf-8"))
        self.path = Path(self.directory.name) / "cn.2026.08.05.json"

    def write(self, document):
        document["profile_sha256"] = validate.profile_hash(
            {k: v for k, v in document.items() if k != "profile_sha256"})
        self.path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")

    def assert_refused(self):
        code, errors = validate.validate(str(self.path), stamp=False)
        self.assertEqual(1, code, errors)
        return errors

    def test_the_unmodified_profile_is_accepted(self):
        self.write(copy.deepcopy(self.document))

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(0, code, errors)

    # --- (a) the calibration roulette field gets the ordinary field checks ----------------

    def test_calibration_roulette_field_with_a_length_on_a_fixed_width_type_is_refused(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_request"]["roulette_field"] = {
            "name": "roulette_id", "offset": 0, "type": "u16", "length": 2,
        }
        self.write(document)

        errors = self.assert_refused()

        self.assertIn(
            "calibration.finder_request.roulette_field: length applies to bytes fields only",
            errors)

    def test_calibration_roulette_field_with_max_below_min_is_refused(self):
        document = copy.deepcopy(self.document)
        document["calibration"]["finder_request"]["roulette_field"]["constraints"] = {
            "min": 9, "max": 3,
        }
        self.write(document)

        errors = self.assert_refused()

        self.assertIn(
            "calibration.finder_request.roulette_field: constraint max is below min", errors)

    # --- (b) integers are signed 64-bit -----------------------------------------------------

    def test_a_constraint_beyond_int64_is_refused(self):
        for key, value in (("max", 2 ** 63), ("min", -(2 ** 63) - 1), ("in", [2 ** 64])):
            with self.subTest(key=key):
                document = copy.deepcopy(self.document)
                document["messages"][0]["fields"][1]["constraints"] = {key: value}
                self.write(document)

                errors = self.assert_refused()

                self.assertTrue(any("expected type integer" in error for error in errors), errors)

    def test_the_int64_limits_themselves_are_integers(self):
        document = copy.deepcopy(self.document)
        document["messages"][0]["fields"][1]["constraints"] = {
            "min": -(2 ** 63), "max": 2 ** 63 - 1,
        }
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(0, code, errors)

    # --- (c) a duplicated key -----------------------------------------------------------

    def test_a_duplicated_key_is_refused_even_when_the_folded_hash_matches(self):
        document = copy.deepcopy(self.document)
        document["profile_sha256"] = validate.profile_hash(
            {k: v for k, v in document.items() if k != "profile_sha256"})
        text = json.dumps(document, ensure_ascii=False, indent=2)
        # json.load keeps the last of two equal keys, so this folds back into `document`, whose
        # hash is the one stamped above. CanonicalJson writes both entries and refuses it.
        duplicated = text.replace(
            '"region": "CN",', '"region": "GLOBAL",\n  "region": "CN",', 1)
        self.assertNotEqual(text, duplicated)
        self.path.write_text(duplicated, encoding="utf-8")

        errors = self.assert_refused()

        self.assertTrue(any("duplicate key 'region'" in error for error in errors), errors)

    def test_stamp_refuses_a_duplicated_key_and_leaves_the_file_alone(self):
        text = json.dumps(self.document, ensure_ascii=False, indent=2).replace(
            '"region": "CN",', '"region": "CN",\n  "region": "CN",', 1)
        self.path.write_text(text, encoding="utf-8")

        code, errors = validate.validate(str(self.path), stamp=True)

        self.assertEqual(1, code, errors)
        self.assertEqual(text, self.path.read_text(encoding="utf-8"))

    # --- (d) string lengths are UTF-16 code units ---------------------------------------

    def test_max_length_counts_utf16_code_units(self):
        # 2049 characters outside the BMP: 2049 code points, 4098 UTF-16 code units, over the
        # summary's maxLength of 4096 that C#'s string.Length enforces.
        document = copy.deepcopy(self.document)
        document["provenance"]["summary"] = "\U0001F600" * 2049
        self.write(document)

        errors = self.assert_refused()

        self.assertTrue(any("longer than 4096" in error for error in errors), errors)

    def test_max_length_accepts_exactly_the_limit_in_utf16_code_units(self):
        document = copy.deepcopy(self.document)
        document["provenance"]["summary"] = "\U0001F600" * 2048
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(0, code, errors)

    # --- (e) a profile that cannot be hashed is refused, not a traceback (R2T-12) -------

    def write_with_lone_surrogate(self):
        # A lone surrogate escape parses to a string UTF-8 cannot encode, so the canonical hash
        # cannot be computed; the Collector cannot read the string either.
        document = copy.deepcopy(self.document)
        document["provenance"]["summary"] = "\ud800"
        text = json.dumps(document, ensure_ascii=True, indent=2)
        self.assertIn("\\ud800", text)
        self.path.write_text(text, encoding="utf-8")
        return text

    def test_a_lone_surrogate_escape_is_refused_without_a_traceback(self):
        self.write_with_lone_surrogate()

        errors = self.assert_refused()

        self.assertTrue(any("canonical" in error for error in errors), errors)

    def test_stamp_refuses_a_lone_surrogate_escape_and_leaves_the_file_alone(self):
        text = self.write_with_lone_surrogate()

        code, errors = validate.validate(str(self.path), stamp=True)

        self.assertEqual(1, code, errors)
        self.assertEqual(text, self.path.read_text(encoding="utf-8"))

    def test_the_command_line_reports_an_unhashable_profile_and_checks_the_rest(self):
        self.write_with_lone_surrogate()
        good = Path(self.directory.name) / "good" / "cn.2026.08.05.json"
        good.parent.mkdir()
        good.write_text(json.dumps(self.document, ensure_ascii=False), encoding="utf-8")
        output = io.StringIO()

        with contextlib.redirect_stdout(output):
            code = validate.main([str(self.path), str(good)])

        self.assertEqual(1, code, output.getvalue())
        self.assertIn("%s: FAILED" % self.path, output.getvalue())
        self.assertIn("%s: OK" % good, output.getvalue())

    def test_invalid_utf8_is_refused(self):
        # The Collector's File.ReadAllText turns the byte into U+FFFD and carries on; this tool
        # refuses the file (one of the documented differences, where Python is the stricter side).
        self.path.write_bytes(json.dumps(self.document).encode("utf-8").replace(
            b'"region": "CN"', b'"region": "CN\xff"', 1))

        errors = self.assert_refused()

        self.assertTrue(any("cannot read the profile" in error for error in errors), errors)


if __name__ == "__main__":
    unittest.main(verbosity=2)
