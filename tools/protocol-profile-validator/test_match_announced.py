#!/usr/bin/env python3
"""The ``MATCH_ANNOUNCED`` message, mirrored against ProfileLoader.CheckAnnouncementContext in
src/Collector/Protocol/Profiles/ProfileLoader.cs so the Python validator and the Collector agree
(docs/protocol-profile-format.md section 11).

The message exists for a build whose announcement carries no roulette id anywhere, where the
profile already stands the player's own queue request in for the match and the announcement only
adds the moment the popup appeared. That is the only place it may appear, and it may only ever
travel from the server.
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

import validate


ANNOUNCED = {
    "name": "MATCH_ANNOUNCED",
    "opcode": 1234,
    "direction": "SERVER_TO_CLIENT",
    "expected_length": 12,
    "fields": [],
}


class MatchAnnouncedTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="mr-profile-announced-")
        self.addCleanup(self.directory.cleanup)
        source = Path(validate.REPO) / "protocol-profiles/cn/cn.2026.08.05.json"
        self.document = json.loads(source.read_text(encoding="utf-8"))
        self.path = Path(self.directory.name) / "cn.2026.08.05.json"

    def write(self, document):
        document["profile_sha256"] = validate.profile_hash(
            {k: v for k, v in document.items() if k != "profile_sha256"})
        self.path.write_text(json.dumps(document), encoding="utf-8")

    def queue_inferred(self):
        """The shipped template turned into the kind of profile calibration writes when no
        announcement can be found by value: the pop is the player's own request."""
        document = copy.deepcopy(self.document)
        document.pop("calibration", None)
        for message in document["messages"]:
            if message["name"] == "CONTENT_FINDER_POP":
                message["direction"] = "CLIENT_TO_SERVER"
        document["provenance"]["evidence"].append({
            "field": "messages.MATCH_ANNOUNCED.opcode",
            "method": "OBSERVED_LOCAL_TRAFFIC",
            "recorded_at_utc": "2026-09-19T10:38:00.000Z",
            "url": None,
            "sample_count": 2,
            "note": "本机校准：按出现时机认出的匹配通知。",
        })
        return document

    def test_a_queue_inferred_profile_may_declare_it(self):
        document = self.queue_inferred()
        document["messages"].append(copy.deepcopy(ANNOUNCED))
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(0, code, errors)

    def test_it_is_refused_beside_a_server_sent_pop(self):
        document = copy.deepcopy(self.document)
        document["messages"].append(copy.deepcopy(ANNOUNCED))
        document["provenance"]["evidence"].append({
            "field": "messages.MATCH_ANNOUNCED.opcode",
            "method": "OBSERVED_LOCAL_TRAFFIC",
            "recorded_at_utc": "2026-09-19T10:38:00.000Z",
            "url": None,
            "sample_count": 2,
            "note": "本机校准：按出现时机认出的匹配通知。",
        })
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn(
            "MATCH_ANNOUNCED is only valid where CONTENT_FINDER_POP is CLIENT_TO_SERVER", errors)

    def test_it_must_travel_from_the_server(self):
        document = self.queue_inferred()
        announced = copy.deepcopy(ANNOUNCED)
        announced["direction"] = "CLIENT_TO_SERVER"
        document["messages"].append(announced)
        self.write(document)

        code, errors = validate.validate(str(self.path), stamp=False)

        self.assertEqual(1, code)
        self.assertIn("MATCH_ANNOUNCED must be SERVER_TO_CLIENT", errors)


if __name__ == "__main__":
    unittest.main()
