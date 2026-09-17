#!/usr/bin/env python3
"""Calendar boundaries shared with ProtocolProfileTimestampTests, without network access."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

import validate


class TimestampTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="mr-profile-dates-")
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "cn-unsupported.json"
        source = Path(validate.REPO) / "protocol-profiles/cn/cn-unsupported.json"
        self.document = json.loads(source.read_text(encoding="utf-8"))

    def write_date(self, value):
        self.document["generated_at"] = value
        self.document["profile_sha256"] = validate.profile_hash(self.document)
        self.path.write_text(json.dumps(self.document), encoding="utf-8")

    def test_invalid_calendar_values_are_refused_even_with_a_valid_stamp(self):
        for value in (
            "2026-99-99T99:99:99.000Z", "2026-02-29T00:00:00.000Z",
            "1900-02-29T00:00:00.000Z", "2026-04-31T00:00:00.000Z",
            "2026-09-08T24:00:00.000Z", "2026-09-08T00:00:60.000Z",
            "0000-01-01T00:00:00.000Z",
        ):
            with self.subTest(timestamp=value):
                self.write_date(value)
                code, errors = validate.validate(str(self.path), stamp=True)
                self.assertEqual(1, code)
                self.assertIn("generated_at is not a valid UTC timestamp", errors)

    def test_valid_calendar_boundaries_still_load(self):
        for value in (
            "0001-01-01T00:00:00.000Z", "2000-02-29T23:59:59.999Z",
            "2028-02-29T00:00:00.000Z", "9999-12-31T23:59:59.999Z",
        ):
            with self.subTest(timestamp=value):
                self.write_date(value)
                code, errors = validate.validate(str(self.path), stamp=False)
                self.assertEqual(0, code, errors)


if __name__ == "__main__":
    unittest.main(verbosity=2)
