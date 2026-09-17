#!/usr/bin/env python3
"""rebuild.py: the structural rules of SharedProfileBuilder / CalibratedShape the Action applies."""

from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path

import testsupport
import rebuild


def _without_message(document: dict, name: str) -> dict:
    edited = copy.deepcopy(document)
    edited["messages"] = [message for message in edited["messages"] if message["name"] != name]
    return edited


class TemplateTests(unittest.TestCase):
    def setUp(self):
        self.document = testsupport.shipped_template_document()

    def test_the_shipped_template_loads(self):
        template = testsupport.shipped_template()
        self.assertEqual("cn.2026.08.05", template.profile_id)
        self.assertEqual("CN", template.region)
        self.assertEqual(self.document["profile_sha256"], template.sha256)
        self.assertEqual(["finder_state"], [field["name"] for field in rebuild.learnable_selectors(template)])

    def test_a_template_whose_content_does_not_match_its_hash_is_refused(self):
        edited = copy.deepcopy(self.document)
        edited["match_window_seconds"] = 121
        with self.assertRaisesRegex(rebuild.TemplateError, "profile_sha256"):
            rebuild.load_template(json.dumps(edited).encode("utf-8"))

    def test_a_profile_that_cannot_lend_its_structure_is_not_a_template(self):
        no_calibration = copy.deepcopy(self.document)
        del no_calibration["calibration"]
        not_verified = dict(copy.deepcopy(self.document), compatibility_status="CANDIDATE")
        no_pop_length = copy.deepcopy(self.document)
        del no_pop_length["messages"][0]["expected_length"]
        no_roulette = copy.deepcopy(self.document)
        del no_roulette["mentor_roulette_id"]
        for why, document in (("calibration", no_calibration), ("VERIFIED", not_verified),
                              ("CONTENT_FINDER_POP", no_pop_length), ("mentor_roulette_id", no_roulette)):
            with self.subTest(why), self.assertRaisesRegex(rebuild.TemplateError, why):
                testsupport.template_from(document)

    def test_json_that_is_not_a_profile_object_is_refused(self):
        for data in (b"", b"[]", b'{"a":1,"a":2}', b"\xff"):
            with self.subTest(data), self.assertRaises(rebuild.TemplateError):
                rebuild.load_template(data)

    def test_a_directory_of_templates_loads_by_name_and_refuses_twins(self):
        with tempfile.TemporaryDirectory(prefix="mr-templates-") as name:
            directory = Path(name)
            (directory / "a.json").write_bytes(testsupport.TEMPLATE_PATH.read_bytes())
            self.assertEqual(["a.json"], [template.name for template in rebuild.load_templates(directory)])
            (directory / "b.json").write_bytes(testsupport.TEMPLATE_PATH.read_bytes())
            with self.assertRaises(rebuild.TemplateError):
                rebuild.load_templates(directory)


class RebuildTests(unittest.TestCase):
    def setUp(self):
        self.template = testsupport.shipped_template()

    def build(self, payload, template=None):
        return rebuild.rebuild(payload, template or self.template)

    def test_every_match_source_rebuilds_the_messages_the_draft_would_write(self):
        reply = self.build(testsupport.payload("REPLY_STATE", 1))
        self.assertTrue(reply.built, reply.reason)
        pop = reply.messages[0]
        self.assertEqual((2001, "SERVER_TO_CLIENT", 40), (pop["opcode"], pop["direction"], pop["expected_length"]))
        self.assertEqual({"in": [3]}, pop["fields"][0]["constraints"])
        self.assertEqual({"min": 1, "max": 255}, pop["fields"][1]["constraints"])

        announcement = self.build(testsupport.payload("ANNOUNCEMENT", 1)).messages[0]
        self.assertEqual(40, announcement["expected_length"])
        self.assertEqual(["roulette_id"], [field["name"] for field in announcement["fields"]])

        marker = self.build(testsupport.payload("MARKER_OFFSET", 1)).messages[0]
        self.assertEqual(24, marker["expected_length"])
        self.assertEqual([("roulette_id", 8, "u8")], [(f["name"], f["offset"], f["type"]) for f in marker["fields"]])

        queue = self.build(testsupport.payload("QUEUE_REQUEST", 1))
        self.assertTrue(queue.built and queue.needs_consent)
        self.assertEqual(("CLIENT_TO_SERVER", 24), (queue.messages[0]["direction"], queue.messages[0]["expected_length"]))
        self.assertEqual([self.template.document["calibration"]["finder_request"]["roulette_field"]], queue.messages[0]["fields"])

        self.assertEqual(
            ["CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB"],
            [message["name"] for message in reply.messages])
        self.assertEqual([2001, 1001, 3001, 4001], [message["opcode"] for message in reply.messages])
        minimal = self.build(testsupport.payload("ANNOUNCEMENT", 1, territory_opcode=None, job_opcode=None))
        self.assertEqual(["CONTENT_FINDER_POP", "ZONE_INITIALIZATION"], [message["name"] for message in minimal.messages])

    def test_a_code_for_another_template_or_region_is_not_applicable(self):
        for why, payload in (
            ("template hash", testsupport.payload(template_sha256="0" * 64)),
            ("template id", testsupport.payload(template_profile_id="cn.2026.01.01")),
            ("region", testsupport.payload(region="GLOBAL")),
        ):
            with self.subTest(why):
                self.assertEqual(rebuild.NOT_APPLICABLE, self.build(payload).status)
                self.assertIsNone(rebuild.find_template([self.template], payload))
        self.assertIs(self.template, rebuild.find_template([self.template], testsupport.payload()))

    def test_the_selector_values_must_match_the_template_selectors(self):
        for values, built in (([], False), ([3, 4], False), ([256], False), ([-1], False), ([255], True), ([0], True)):
            with self.subTest(values):
                result = self.build(testsupport.payload("REPLY_STATE", pop={"opcode": 7, "selector_values": values}))
                self.assertEqual(built, result.built, result.reason)

    def test_the_pop_fields_must_fit_the_rebuilt_length(self):
        # The roulette id sits at byte 16 of the template pop: an announcement of 16 bytes cannot hold it.
        self.assertEqual("the pop's fields do not fit its length",
                         self.build(testsupport.payload("ANNOUNCEMENT", pop={"opcode": 7, "length": 16})).reason)
        self.assertTrue(self.build(testsupport.payload("ANNOUNCEMENT", pop={"opcode": 7, "length": 17})).built)
        self.assertTrue(self.build(testsupport.payload("MARKER_OFFSET", pop={"opcode": 7, "length": 24, "roulette_offset": 23})).built)

    def test_two_messages_may_not_share_a_direction_and_opcode(self):
        same_direction = testsupport.payload("ANNOUNCEMENT", 1, zone_opcode=2001)
        self.assertEqual("two messages claim the same direction and opcode", self.build(same_direction).reason)
        territory_is_job = testsupport.payload("ANNOUNCEMENT", 1, job_opcode=3001)
        self.assertFalse(self.build(territory_is_job).built)
        # The inferred pop is the client's own request, so it may share a number with a server message.
        other_direction = testsupport.payload("QUEUE_REQUEST", 1, zone_opcode=2001)
        self.assertTrue(self.build(other_direction).built)

    def test_territory_and_job_need_the_template_to_declare_them(self):
        document = testsupport.shipped_template_document()
        no_territory = testsupport.template_from(_without_message(document, "ZONE_TERRITORY"))
        no_job = testsupport.template_from(_without_message(document, "PLAYER_JOB"))

        def on(template, **changes):
            return testsupport.payload("ANNOUNCEMENT", 1, template=template, **changes)

        self.assertEqual("the template declares no territory message", self.build(on(no_territory), no_territory).reason)
        self.assertTrue(self.build(on(no_territory, territory_opcode=None), no_territory).built)
        self.assertEqual("the template declares no job message", self.build(on(no_job), no_job).reason)
        self.assertTrue(self.build(on(no_job, job_opcode=None), no_job).built)

    def test_a_payload_the_decoder_refuses_is_invalid_before_anything_else(self):
        for payload in (testsupport.payload(note="x"), testsupport.payload("QUEUE_REQUEST", territory_opcode=None), {}):
            with self.subTest(payload.get("match_source")):
                result = self.build(payload)
                self.assertEqual(rebuild.INVALID, result.status)
                self.assertTrue(result.reason.startswith("E_SHARE_CODE_"))


if __name__ == "__main__":
    unittest.main()
