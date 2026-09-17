"""Synthetic evidence only; payload samples exist solely in individual temporary directories."""

from contextlib import redirect_stderr, redirect_stdout
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid

import derive_fields as derive


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


class DeriveFieldsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="candidate-evidence-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.path = self.directory / "synthetic.json"
        self.session = str(uuid.uuid4())
        self.sequence = 0

    def row(self, payload=b"\x34\x12\xaa", **changes):
        self.sequence += 1
        row = {"observation_id": str(uuid.uuid4()), "capture_session_id": self.session,
               "profile_id": "candidate-test", "hypothesis_name": "FINDER_STATE_NOTIFICATION",
               "direction": "S2C", "opcode": 0x0323, "payload_length": len(payload) if payload is not None else 3,
               "payload_hash12": hashlib.sha256(payload or b"").hexdigest()[:12],
               "payload_hex": payload.hex() if payload is not None else None,
               "connection_tag": "0123456789ab", "t_ms": self.sequence * 100,
               "review_verdict": "CORRECT"}
        row.update(changes)
        return row

    def document(self, rows):
        opcodes = sorted({f"0x{row['opcode']:04x}" for row in rows if row.get("payload_hex") is not None})
        return {"format": "MentorRecorder.CandidateEvidence", "schema_version": 1,
                "profile_status": "CANDIDATE", "contains_raw_payload": bool(opcodes),
                "research_payload_opcodes": opcodes, "observation_count": len(rows), "review_count": 0,
                "observations": rows, "reviews": []}

    def load(self, rows):
        return self.load_document(self.document(rows))

    def load_document(self, document):
        self.path.write_text(json.dumps(document), encoding="utf-8")
        return derive.load_evidence(self.path)

    def analyze(self, rows, **changes):
        options = {"roulette_id": 0x1234, "popup_hypothesis": "FINDER_STATE_NOTIFICATION"}
        options.update(changes)
        return derive.analyze(self.load(rows), **options)

    def test_constants_increments_and_variation_use_sorted_monotonic_time(self):
        rows = [self.row(bytes(value)) for value in ((7, 10, 5), (7, 11, 9), (7, 12, 3))]
        result = derive.analyze(self.load(list(reversed(rows))))
        offsets = result["groups"][0]["offsets"]
        self.assertEqual(["constant", "incrementing", "random"], [item["classification"] for item in offsets])
        self.assertEqual([2, 2, 2], [item["temporal_comparisons"] for item in offsets])
        self.assertIn("不能证明随机性", derive.render_markdown(result))
        self.assertEqual("ANALYSIS_ONLY", result["status"])
        self.assertEqual([], result["messages"])

    def test_temporal_increments_never_join_sessions_or_connections(self):
        for changed in ({"capture_session_id": str(uuid.uuid4())}, {"connection_tag": "abcdef012345"}):
            with self.subTest(changed=changed):
                result = derive.analyze(self.load([self.row(b"\x01"), self.row(b"\x02", **changed)]))
                offset = result["groups"][0]["offsets"][0]
                self.assertEqual("random", offset["classification"])
                self.assertEqual(0, offset["temporal_comparisons"])

    def test_separate_streams_can_independently_increment_without_joining_the_reset(self):
        other = str(uuid.uuid4())
        rows = [self.row(b"\x01"), self.row(b"\x02"), self.row(b"\x01", capture_session_id=other),
                self.row(b"\x02", capture_session_id=other)]
        offset = derive.analyze(self.load(rows))["groups"][0]["offsets"][0]
        self.assertEqual("incrementing", offset["classification"])
        self.assertEqual(2, offset["temporal_comparisons"])

    def test_equal_timestamps_and_byte_wraparound_are_not_inferred_as_increments(self):
        for rows in ([self.row(b"\x01", t_ms=1), self.row(b"\x02", t_ms=1)],
                     [self.row(b"\xff"), self.row(b"\x00")]):
            with self.subTest(rows=rows):
                offset = derive.analyze(self.load(rows))["groups"][0]["offsets"][0]
                self.assertEqual("random", offset["classification"])

    def test_single_sample_and_duplicate_ids_cannot_propose_fields(self):
        row = self.row()
        result = self.analyze([row, copy.deepcopy(row)])
        self.assertEqual("INSUFFICIENT_EVIDENCE", result["status"])
        self.assertEqual(1, result["duplicate_observation_count"])
        self.assertEqual(1, result["observation_count"])
        self.assertEqual([], result["message_candidates"])
        self.assertTrue(all(item["classification"] == "insufficient" for item in result["groups"][0]["offsets"]))

    def test_conflicting_duplicate_id_is_rejected_instead_of_picking_a_review(self):
        row = self.row()
        with self.assertRaisesRegex(derive.EvidenceError, "inconsistent"):
            self.load([row, {**row, "review_verdict": "WRONG"}])

    def test_groups_separate_profile_hypothesis_opcode_direction_and_length(self):
        rows = [self.row(), self.row(profile_id="candidate-other"), self.row(hypothesis_name="OTHER"),
                self.row(opcode=0x0104), self.row(direction="C2S"), self.row(b"\x34\x12")]
        result = self.analyze(rows)
        self.assertEqual(6, len(result["groups"]))
        self.assertTrue(all(group["payload_sample_count"] == 1 for group in result["groups"]))
        self.assertEqual([], result["message_candidates"])

    def test_unique_little_u16_match_generates_only_candidate_message(self):
        result = self.analyze([self.row(), self.row()])
        self.assertEqual("DRAFT_READY", result["status"])
        self.assertEqual("CANDIDATE", result["compatibility_status"])
        self.assertIsNone(result["mentor_roulette_id"])
        self.assertEqual("EXPLICIT_USER_INPUT", result["roulette_id_source"])
        message = result["messages"][0]
        self.assertEqual("CONTENT_FINDER_POP", message["name"])
        self.assertEqual([{"name": "roulette_id", "offset": 0, "type": "u16", "endian": "little"}], message["fields"])
        self.assertEqual(2, result["field_candidates"][0]["support_count"])

    def test_big_endian_order_does_not_match_little_endian_value(self):
        result = self.analyze([self.row(b"\x12\x34\xaa"), self.row(b"\x12\x34\xaa")])
        self.assertEqual("NO_MATCH", result["status"])
        self.assertEqual([], result["messages"])

    def test_u8_u16_u32_width_ambiguity_is_retained_until_explicit_selection(self):
        rows = [self.row(b"\x25\x00\x00\x00"), self.row(b"\x25\x00\x00\x00")]
        result = self.analyze(rows, roulette_id=37)
        self.assertEqual("AMBIGUOUS", result["status"])
        self.assertEqual([], result["messages"])
        self.assertEqual({(0, "u8"), (0, "u16"), (0, "u32")},
                         {(item["offset"], item["type"]) for item in result["field_candidates"]})
        selected = self.analyze(rows, roulette_id=37, field=(0, "u32"))
        self.assertEqual("DRAFT_READY", selected["status"])
        self.assertEqual("u32", selected["messages"][0]["fields"][0]["type"])
        self.assertEqual(3, len(selected["message_candidates"]))

    def test_offset_ambiguity_is_not_silently_resolved(self):
        rows = [self.row(b"\x34\x12\xaa\x34\x12"), self.row(b"\x34\x12\xaa\x34\x12")]
        result = self.analyze(rows)
        self.assertEqual("AMBIGUOUS", result["status"])
        self.assertEqual([0, 3], [item["offset"] for item in result["field_candidates"]])
        self.assertEqual(3, self.analyze(rows, field=(3, "u16"))["messages"][0]["fields"][0]["offset"])

    def test_wrong_unsure_unreviewed_and_other_hypotheses_do_not_supply_popup_support(self):
        rows = [self.row(), self.row(review_verdict="WRONG"), self.row(review_verdict="UNSURE"),
                self.row(review_verdict=None), self.row(hypothesis_name="QUEUE_REQUEST"),
                self.row(hypothesis_name="QUEUE_REQUEST")]
        result = self.analyze(rows)
        self.assertEqual("INSUFFICIENT_EVIDENCE", result["status"])
        self.assertEqual([], result["message_candidates"])
        self.assertEqual("ANALYSIS_ONLY", derive.analyze(self.load(rows))["status"])

    def test_partial_support_is_reported_but_requires_explicit_field(self):
        rows = [self.row(), self.row(), self.row(b"\x35\x12\xaa")]
        result = self.analyze(rows)
        self.assertEqual("PARTIAL_SUPPORT", result["status"])
        self.assertEqual([], result["messages"])
        self.assertEqual(2, result["field_candidates"][0]["support_count"])
        self.assertEqual(3, result["field_candidates"][0]["correct_sample_count"])
        self.assertEqual("DRAFT_READY", self.analyze(rows, field=(0, "u16"))["status"])

    def test_field_selection_does_not_resolve_cross_profile_or_opcode_ambiguity(self):
        rows = [self.row(), self.row(), self.row(profile_id="candidate-other", opcode=0x0104),
                self.row(profile_id="candidate-other", opcode=0x0104)]
        self.assertEqual("AMBIGUOUS", self.analyze(rows, field=(0, "u16"))["status"])
        result = self.analyze(rows, field=(0, "u16"), profile_id="candidate-other", opcode=0x0104,
                              direction="S2C", payload_length=3)
        self.assertEqual("DRAFT_READY", result["status"])
        self.assertEqual(0x0104, result["messages"][0]["opcode"])

    def test_explicit_selection_cannot_create_an_unsupported_offset(self):
        with self.assertRaisesRegex(derive.EvidenceError, "lacks support"):
            self.analyze([self.row(), self.row()], field=(1, "u8"))

    def test_missing_payload_and_zone_anchors_are_skipped_and_reported(self):
        anchor = self.row(None, hypothesis_name="ZONE_LOAD", direction="NONE", opcode=None,
                          payload_length=None, payload_hash12=None)
        result = self.analyze([self.row(None), anchor, self.row(), self.row()])
        self.assertEqual(2, result["skipped_without_payload"])
        self.assertEqual("DRAFT_READY", result["status"])
        self.assertEqual(1, len(result["groups"]))
        self.assertIn("无负载跳过 2", derive.render_markdown(result))

    def test_maximum_payload_length_and_zero_length_are_supported(self):
        rows = [self.row(bytes(512)), self.row(bytes(512)), self.row(b"")]
        result = derive.analyze(self.load(rows))
        self.assertEqual([0, 512], [len(group["offsets"]) for group in result["groups"]])

    def test_invalid_hex_length_hash_and_payload_types_are_rejected(self):
        mutations = [{"payload_hex": "3412AA"}, {"payload_hex": "3412gg"}, {"payload_hex": "a"},
                     {"payload_length": 2}, {"payload_hash12": "0" * 12}, {"payload_hex": [52, 18, 170]},
                     {"payload_hex": "000000\0hidden"}, {"payload_length": True}, {"opcode": True},
                     {"t_ms": -1}, {"direction": "UNKNOWN"}, {"review_verdict": "YES"},
                     {"connection_tag": "127.0.0.1"}, {"capture_session_id": "invalid"}]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                document = self.document([self.row()])
                document["observations"][0].update(mutation)
                with self.assertRaises(derive.EvidenceError):
                    self.load_document(document)
        with self.assertRaisesRegex(derive.EvidenceError, "512"):
            self.load([self.row(bytes(513))])
        self.load([self.row(bytes(257))])

    def test_header_must_describe_actual_payload_even_after_settings_changed(self):
        mutations = [{"contains_raw_payload": False}, {"contains_raw_payload": 1},
                     {"research_payload_opcodes": []}, {"research_payload_opcodes": ["0x0104"]},
                     {"research_payload_opcodes": ["0x0323", "0x0323"]}, {"observation_count": 2},
                     {"review_count": 1}, {"schema_version": True}, {"schema_version": 2},
                     {"profile_status": "VERIFIED"}, {"format": "OTHER"}, {"reviews": [False], "review_count": 1}]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                document = self.document([self.row()])
                document.update(mutation)
                with self.assertRaises(derive.EvidenceError):
                    self.load_document(document)
        document = self.document([self.row(None)])
        document["contains_raw_payload"] = True
        with self.assertRaisesRegex(derive.EvidenceError, "header"):
            self.load_document(document)

    def test_duplicate_json_keys_and_nonfinite_numbers_are_rejected(self):
        for content in ('{"format":"first","format":"second"}', '{"bad":NaN}', "not-json"):
            with self.subTest(content=content):
                self.path.write_text(content, encoding="utf-8")
                with self.assertRaises(derive.EvidenceError):
                    derive.load_evidence(self.path)

    def test_roulette_id_and_popup_name_must_be_explicit_and_valid(self):
        evidence = self.load([self.row(), self.row()])
        for options in ({"roulette_id": 37}, {"popup_hypothesis": "FINDER_STATE_NOTIFICATION"},
                        {"field": (0, "u8")}, {"roulette_id": 0, "popup_hypothesis": "POP"},
                        {"roulette_id": 65536, "popup_hypothesis": "POP"},
                        {"roulette_id": True, "popup_hypothesis": "POP"}):
            with self.subTest(options=options), self.assertRaises(derive.EvidenceError):
                derive.analyze(evidence, **options)

    def test_every_copyable_message_and_field_matches_the_actual_repository_schema(self):
        spec = importlib.util.spec_from_file_location("candidate_test_profile_validator",
                                                   REPO / "tools/protocol-profile-validator/validate.py")
        validator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(validator)
        schema = json.loads((REPO / "protocol-profiles/profile.schema.json").read_text(encoding="utf-8"))
        rows = [self.row(b"\x25\x00\x00\x00"), self.row(b"\x25\x00\x00\x00")]
        result = self.analyze(rows, roulette_id=37, field=(0, "u32"))
        for index, message in enumerate(result["messages"] + result["message_candidates"]):
            self.assertEqual([], validator.schema_errors(message, schema["$defs"]["message"], schema,
                                                          f"$.messages[{index}]"))

    def test_cli_writes_candidate_outputs_and_preserves_them_on_second_run(self):
        self.load([self.row(), self.row()])
        command = [sys.executable, str(HERE / "derive_fields.py"), str(self.path),
                   "--roulette-id", "0x1234", "--popup-hypothesis", "FINDER_STATE_NOTIFICATION"]
        process = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", check=False)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertIn("STATUS: DRAFT_READY; CANDIDATE", process.stdout)
        output = self.directory / "synthetic.derived"
        draft_path = output / "candidate-messages.json"
        before = draft_path.read_bytes()
        draft = json.loads(before)
        self.assertEqual("CANDIDATE", draft["compatibility_status"])
        self.assertIsNone(draft["mentor_roulette_id"])
        self.assertTrue((output / "field-candidates.md").exists())
        self.assertNotIn("3412aa", process.stdout + process.stderr)
        process = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", check=False)
        self.assertEqual(2, process.returncode)
        self.assertIn("output already exists", process.stderr)
        self.assertEqual(before, draft_path.read_bytes())

    def test_cli_ambiguity_and_invalid_input_report_distinct_statuses(self):
        self.load([self.row(b"\x25\x00"), self.row(b"\x25\x00")])
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = derive.main([str(self.path), "--roulette-id", "37", "--popup-hypothesis", "FINDER_STATE_NOTIFICATION"])
        self.assertEqual(0, code)
        self.assertIn("STATUS: AMBIGUOUS", stdout.getvalue())
        self.assertIn("messages=0", stdout.getvalue())
        invalid_dir = self.directory / "invalid-output"
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = derive.main([str(self.path), "--roulette-id", "37", "--output-dir", str(invalid_dir)])
        self.assertEqual(2, code)
        self.assertFalse(invalid_dir.exists())
        self.assertIn("must be supplied together", stderr.getvalue())

    def test_existing_second_output_is_not_overwritten_and_no_first_output_is_created(self):
        result = self.analyze([self.row(), self.row()])
        output = self.directory / "already-there"
        output.mkdir()
        draft = output / "candidate-messages.json"
        draft.write_text("existing user data", encoding="utf-8")
        with self.assertRaisesRegex(derive.EvidenceError, "already exists"):
            derive.write_outputs(result, output)
        self.assertEqual("existing user data", draft.read_text(encoding="utf-8"))
        self.assertFalse((output / "field-candidates.md").exists())

    def test_network_paths_and_shipped_profile_output_are_rejected_before_access(self):
        for path in ("//server/share/evidence.json", "\\\\server\\share\\evidence.json", "https://example.invalid/evidence"):
            with self.subTest(path=path), self.assertRaisesRegex(derive.EvidenceError, "local filesystem"):
                derive.load_evidence(path)
        result = self.analyze([self.row(), self.row()])
        with self.assertRaisesRegex(derive.EvidenceError, "shipped"):
            derive.write_outputs(result, REPO / "protocol-profiles/cn")


if __name__ == "__main__":
    unittest.main()
