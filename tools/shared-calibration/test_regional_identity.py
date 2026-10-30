"""Independent TC identity across local sharing intake; all values here are offline test data."""

import copy
import datetime as dt
import json
import tempfile
import unittest
import uuid
from pathlib import Path

import evidence
import index as repo_index
import issue
import rebuild
import sharecode
import sync_public_repo
import testsupport


class RegionalIdentityTests(unittest.TestCase):
    def test_tc_code_roundtrip_has_a_different_identity_at_the_same_build(self):
        cn = testsupport.payload()
        tc = dict(cn, region="TC", template_profile_id="tc.test-only")
        decoded = sharecode.decode(sharecode.encode(tc))
        self.assertIsNone(decoded.rejection)
        self.assertEqual(tc, decoded.payload)
        self.assertNotEqual(sharecode.code_sha256(cn), decoded.code_sha256)
        self.assertEqual("tc/" + testsupport.BUILD + "/" + decoded.code_sha256[:12] + ".mrc",
                         repo_index.code_path("TC", testsupport.BUILD, decoded.code_sha256))

    def test_tc_index_does_not_borrow_cn_entries_or_accept_a_cn_path(self):
        entries = []
        for region in ("CN", "TC"):
            entries.append({"region": region, "game_build": testsupport.BUILD,
                            "code_sha256": "a" * 64, "match_source": "ANNOUNCEMENT", "submitters": 1,
                            "first_published_at": "2026-09-15T08:00:00Z", "commit": "b" * 40,
                            "path": repo_index.code_path(region, testsupport.BUILD, "a" * 64), "revoked": False})
        read = repo_index.read_index(json.dumps({"schema_version": 1, "entries": entries}).encode())
        self.assertEqual((), read.skipped)
        self.assertEqual((entries[1],), repo_index.select(read.entries, "TC", testsupport.BUILD))
        entries[1]["path"] = entries[0]["path"]
        wrong = repo_index.read_index(json.dumps({"schema_version": 1, "entries": entries[1:]}).encode())
        self.assertEqual((), wrong.entries)
        self.assertEqual(((0, "INVALID:path"),), wrong.skipped)

    def test_tc_forms_and_title_keep_exact_region_tokens(self):
        self.assertEqual(("TC", testsupport.BUILD), issue.parse_title("[共享校准] TC " + testsupport.BUILD))
        report = issue.parse_report(testsupport.report_body(region="TC"))
        self.assertIsNone(report.problem)
        self.assertEqual("TC", report.region)
        self.assertIsNotNone(issue.parse_report(testsupport.report_body(region="tc")).problem)

    def test_tc_template_is_separate_and_the_shipped_placeholder_never_becomes_a_template(self):
        # An edited test-only shape exercises identity plumbing; it is never a shipped TC protocol.
        document = dict(testsupport.shipped_template_document(), region="TC", profile_id="tc.test-only")
        template = testsupport.template_from(document)
        payload = testsupport.payload(region="TC", template=template)
        self.assertTrue(rebuild.rebuild(payload, template).built)
        self.assertFalse(rebuild.rebuild(payload, testsupport.shipped_template()).built)
        with self.assertRaises(rebuild.TemplateError):
            rebuild.load_template((testsupport.REPO / "protocol-profiles/tc/tc-unsupported.json").read_bytes())
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name) / "protocol-profiles/tc"
            directory.mkdir(parents=True)
            raw = json.dumps(dict(document, profile_sha256=template.sha256)).encode()
            (directory / "tc.test-only.json").write_bytes(raw)
            templates = sync_public_repo.shipped_templates(Path(name))
            self.assertEqual({"templates/tc.test-only.json": raw}, templates)

    def test_tc_observation_intake_remains_blocked_candidate_with_private_fields_rejected(self):
        item = {"format": "MentorRecorder.CalibrationEvidence", "schema_version": 1,
                "kind": "TEMPLATE_OBSERVATION", "evidence_id": str(uuid.uuid4()),
                "producer": {"app_version": "1.8.0-beta.3", "rules_version": "calibration-evidence-v1"},
                "target": {"region": "TC", "game_build": testsupport.BUILD, "template_profile_id": None,
                           "template_sha256": None, "code_sha256": None, "quarantine_epoch": 0},
                "sessions": [{"session_alias": str(uuid.uuid4()), "complete": True,
                              "health": {"silent_reason": "HEALTH_UNAVAILABLE", "preexisting_connections": None,
                                         "adapter_dropped": 0, "decoded_messages_dropped": 0,
                                         "damaged_directions": 0, "observation_overflow": False}, "checks": []}],
                "observations": [{"event": "MATCH_SHOWN", "direction": "S2C", "segment_type": 3,
                                  "opcode": 49154, "length": 40, "inside_count": 2,
                                  "outside_count": 1, "event_count": 2}]}
        parsed = evidence.parse(json.dumps(item))
        artifact = evidence.candidate_artifact(parsed)
        self.assertEqual(("TC", "CANDIDATE", "BLOCKED"),
                         (artifact["region"], artifact["status"], artifact["readiness"]))
        state = evidence.empty_ledger()
        result = evidence.receive(state, json.dumps(item), account_id=1,
                                  account_created_at="2020-01-01T00:00:00Z", issue_number=1,
                                  now=dt.datetime(2026, 10, 24, tzinfo=dt.timezone.utc))
        self.assertEqual("candidate", result["status"])
        private = copy.deepcopy(item)
        private["payload"] = "private"
        with self.assertRaises(evidence.EvidenceError):
            evidence.parse(json.dumps(private))


if __name__ == "__main__":
    unittest.main()
