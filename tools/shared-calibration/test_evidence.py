"""Evidence intake contracts and the fixed workflow exercised against local Git and gh substitutes."""
import copy
import datetime as dt
import json
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

import test_workflow_script as _workflow_tests

NOW = dt.datetime(2026, 10, 15, tzinfo=dt.timezone.utc)


def receipt(verdict="CONTRADICT", epoch=0, kind="CODE_VERIFICATION"):
    return {"format": "MentorRecorder.CalibrationEvidence", "schema_version": 1,
            "kind": kind, "evidence_id": str(uuid.uuid4()),
            "producer": {"app_version": "1.0.0", "rules_version": "calibration-evidence-v1"},
            "target": {"region": "CN", "game_build": "2026.09.01.0000.0000",
                       "template_profile_id": "cn-test", "template_sha256": "a" * 64,
                       "code_sha256": "b" * 64, "quarantine_epoch": epoch},
            "sessions": [{"session_alias": str(uuid.uuid4()), "complete": True,
                          "health": {"silent_reason": "NONE", "preexisting_connections": 0,
                                     "adapter_dropped": 0, "decoded_messages_dropped": 0,
                                     "damaged_directions": 0, "observation_overflow": False},
                          "checks": [{"field": "zone", "verdict": verdict}]} for _ in range(2)],
            "observations": []}


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        import evidence
        self.e = evidence
        self.state = evidence.empty_ledger()

    def accept(self, item=None, account=1, issue=None):
        return self.e.receive(self.state, json.dumps(item or receipt()), account_id=account,
                              account_created_at="2020-01-01T00:00:00Z", issue_number=issue or account,
                              now=NOW)

    def test_reclaiming_copied_negatives_removes_their_quarantine(self):
        originals = [receipt() for _ in range(3)]
        for offset, item in enumerate(originals):
            self.accept(item, 10 + offset, 100 + offset)
        candidate_id = self.e.digest(self.e.scope_for(originals[0]))
        self.assertTrue(self.state["candidates"][candidate_id]["quarantined"])
        for offset, item in enumerate(originals):
            self.accept(item, 1, 1 + offset)
        candidate = self.state["candidates"][candidate_id]
        self.assertEqual(1, len(self.state["votes"]))
        self.assertFalse(candidate["quarantined"])
        self.assertEqual({}, candidate["triggers"])
        self.assertGreaterEqual(candidate["epoch"], 1, "published epochs must never move backwards")
        previous_epoch = candidate["epoch"]
        for account in (20, 21):
            self.accept(receipt(epoch=1), account, 200 + account)
        self.assertEqual(previous_epoch + 1, self.state["candidates"][candidate_id]["epoch"])

    def test_reclaim_removes_a_trigger_when_negatives_no_longer_have_a_majority(self):
        for account in (30, 31, 32):
            self.accept(receipt("PASS"), account, account)
        self.accept(receipt(), 1, 2)
        for account in (11, 12):
            self.accept(receipt(), account, account)
        self.assertFalse(next(iter(self.state["candidates"].values()))["quarantined"])
        original = receipt()
        self.accept(original, 10, 100)
        self.assertTrue(next(iter(self.state["candidates"].values()))["quarantined"])
        self.accept(original, 1, 1)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertEqual(3, sum(v["verdict"] == "CONTRADICT" for v in self.state["votes"].values()))
        self.assertEqual(3, sum(v["verdict"] == "PASS" for v in self.state["votes"].values()))
        self.assertFalse(candidate["quarantined"])

    def test_reclaim_does_not_use_later_stale_pass_votes_as_recovery(self):
        original = receipt()
        self.accept(original, 10, 100)
        for account in (11, 12, 13):
            self.accept(receipt(), account, account + 100)
        for account in (30, 31, 32):
            self.accept(receipt("PASS", 0), account, account + 100)
        # The author's newer issue already has a negative position, so reclaiming the
        # earlier receipt will not add another negative and obscure the 3:3 case.
        self.accept(receipt(), 11, 200)
        self.accept(original, 11, 1)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertEqual(3, sum(v["verdict"] == "CONTRADICT" for v in self.state["votes"].values()))
        self.assertEqual(3, sum(v["verdict"] == "PASS" for v in self.state["votes"].values()))
        self.assertTrue(candidate["quarantined"])

    def test_valid_vote_changes_still_require_fresh_epoch_recovery(self):
        for account in (1, 2, 3):
            self.accept(receipt(), account, account)
        for account in (1, 2, 3):
            self.accept(receipt("PASS", 0), account, account + 10)
        self.assertTrue(next(iter(self.state["candidates"].values()))["quarantined"])

    def test_reclaim_preserves_quarantine_after_genuine_quorum_changed_positions(self):
        original = receipt()
        self.accept(original, 10, 100)
        for account in (11, 12, 13):
            self.accept(receipt(), account, account + 100)
        for account in (11, 12):
            self.accept(receipt("PASS", 0), account, account + 200)
        self.accept(original, 13, 1)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertEqual(1, sum(v["verdict"] == "CONTRADICT" for v in self.state["votes"].values()))
        self.assertTrue(candidate["quarantined"])
        # Ambiguous history does not disable the established recovery path.
        for account in (11, 12, 14):
            self.accept(receipt("PASS", candidate["epoch"]), account, account + 300)
        self.assertFalse(next(iter(self.state["candidates"].values()))["quarantined"])

    def test_reclaim_preserves_other_fields_and_independent_negative_quorum(self):
        copied = receipt()
        self.accept(copied, 10, 100)
        for account in (11, 12, 13):
            item = receipt()
            for session in item["sessions"]:
                session["checks"].append({"field": "match", "verdict": "CONTRADICT"})
            self.accept(item, account, 100 + account)
        self.accept(copied, 1, 1)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertTrue(candidate["quarantined"])
        self.assertEqual({"zone", "match"}, set(candidate["triggers"]))

    def test_reclaim_does_not_lift_an_independent_field_trigger(self):
        originals = [receipt() for _ in range(3)]
        for offset, item in enumerate(originals):
            self.accept(item, 10 + offset, 100 + offset)
        for account in (20, 21, 22):
            item = receipt()
            for session in item["sessions"]:
                session["checks"][0]["field"] = "match"
            self.accept(item, account, 200 + account)
        for offset, item in enumerate(originals):
            self.accept(item, 1, 1 + offset)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertTrue(candidate["quarantined"])
        self.assertEqual({"match"}, set(candidate["triggers"]))

    def test_strict_schema_rejects_private_and_boolean_integer(self):
        for mutate in (lambda d: d.update(payload="secret"),
                       lambda d: d.update(schema_version=True),
                       lambda d: d["sessions"][0]["health"].update(adapter_dropped=True)):
            item = receipt(); mutate(item)
            with self.assertRaises(self.e.EvidenceError): self.e.parse(json.dumps(item))
        for raw in ('{"format":1,"format":2}', '{"x":NaN}', '[' * 40 + '0' + ']' * 40):
            with self.assertRaises(self.e.EvidenceError): self.e.parse(raw)

    def test_threshold_epoch_recovery_and_consumed_negative(self):
        for account in range(1, 4): self.accept(account=account)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertTrue(candidate["quarantined"])
        self.assertEqual(1, candidate["epoch"])
        for account in range(1, 4): self.accept(receipt("PASS", 0), account, account + 10)
        self.assertTrue(candidate["quarantined"], "pre-epoch proof cannot restore")
        for account in range(1, 4): self.accept(receipt("PASS", 1), account, account + 20)
        candidate = next(iter(self.state["candidates"].values()))
        self.assertFalse(candidate["quarantined"])
        self.accept(account=4, issue=40)
        self.assertFalse(candidate["quarantined"], "old negative votes are consumed")

    def test_replay_old_issue_unhealthy_and_account_age_never_add_vote(self):
        item = receipt(); self.accept(item, 1, 10)
        self.assertEqual("duplicate", self.accept(item, 1, 10)["status"])
        altered = copy.deepcopy(item); altered["producer"]["app_version"] = "1.0.1"
        with self.assertRaises(self.e.EvidenceError): self.accept(altered, 1, 11)
        replay = copy.deepcopy(item); replay["evidence_id"] = str(uuid.uuid4())
        self.assertEqual("waiting", self.accept(replay, 1, 11)["status"])
        self.accept(receipt("PASS"), 1, 20)
        self.accept(receipt(), 1, 15)
        self.assertEqual("PASS", next(iter(self.state["votes"].values()))["verdict"])
        bad = receipt(); bad["sessions"][0]["health"]["adapter_dropped"] = 1
        self.assertEqual("waiting", self.accept(bad, 2, 30)["status"])
        with self.assertRaises(self.e.EvidenceError):
            self.e.receive(self.state, json.dumps(receipt()), account_id=5,
                           account_created_at="2026-10-14T00:00:00Z", issue_number=50, now=NOW)

    def test_wait_bundle_can_grow_but_a_voted_issue_cannot_add_a_field(self):
        one = receipt(); one["sessions"] = one["sessions"][:1]
        self.assertEqual("waiting", self.accept(one, 1, 10)["status"])
        two = copy.deepcopy(one); two["evidence_id"] = str(uuid.uuid4())
        two["sessions"].append(receipt()["sessions"][0])
        self.assertEqual("accepted", self.accept(two, 1, 10)["status"])
        edited = receipt()
        for session in edited["sessions"]: session["checks"][0]["field"] = "match"
        with self.assertRaisesRegex(self.e.EvidenceError, "NEW_ISSUE_REQUIRED"): self.accept(edited, 1, 10)
        self.assertEqual(1, len(self.state["votes"]))

    def test_consumed_session_pair_plus_one_new_cannot_toggle_again(self):
        old = {}
        for account in range(1, 4):
            old[account] = receipt(); self.accept(old[account], account, account)
        for account in range(1, 4): self.accept(receipt("PASS", 1), account, account + 10)
        self.assertFalse(next(iter(self.state["candidates"].values()))["quarantined"])
        for account in range(1, 4):
            mixed = copy.deepcopy(old[account]); mixed["evidence_id"] = str(uuid.uuid4())
            mixed["target"]["quarantine_epoch"] = 1
            mixed["sessions"].append(receipt()["sessions"][0])
            self.assertEqual("waiting", self.accept(mixed, account, account + 20)["status"])
        self.assertFalse(next(iter(self.state["candidates"].values()))["quarantined"])
        for account in range(1, 4): self.accept(receipt(epoch=1), account, account + 30)
        self.assertTrue(next(iter(self.state["candidates"].values()))["quarantined"])
        self.assertEqual(2, next(iter(self.state["candidates"].values()))["epoch"])

    def test_cumulative_bundle_ignores_consumed_old_negative_when_two_new_pass_arrive(self):
        old = {}
        for account in range(1, 4):
            old[account] = receipt()
            old[account]["sessions"] += receipt()["sessions"] + receipt()["sessions"]
            self.accept(old[account], account, account)
        for account in range(1, 4):
            growing = receipt("PASS", 1)
            growing["sessions"] = old[account]["sessions"] + growing["sessions"]
            self.assertEqual(8, len(growing["sessions"]))
            self.assertEqual("accepted", self.accept(growing, account, account + 10)["status"])
        self.assertFalse(next(iter(self.state["candidates"].values()))["quarantined"])

    def test_independent_scope_and_field_quorums(self):
        for account in range(1, 4):
            item = receipt(); item["target"]["template_sha256"] = str(account) * 64
            self.accept(item, account, account)
        self.assertFalse(any(c["quarantined"] for c in self.state["candidates"].values()))
        for account in range(1, 4):
            item = receipt()
            for session in item["sessions"]:
                session["checks"].append({"field": "match", "verdict": "CONTRADICT"})
            self.accept(item, account, account + 10)
        candidate = self.state["candidates"][self.e.digest(self.e.scope_for(receipt()))]
        self.assertEqual({"zone", "match"}, set(candidate["triggers"]))
        for account in range(1, 4): self.accept(receipt("PASS", 1), account, account + 20)
        candidate = self.state["candidates"][self.e.digest(self.e.scope_for(receipt()))]
        self.assertEqual({"match"}, set(candidate["triggers"]))
        self.assertTrue(candidate["quarantined"])

    def test_ledger_roundtrip_is_strict_and_capacity_is_atomic(self):
        self.accept()
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / self.e.LEDGER_FILE
            path.write_bytes(self.e.canonical(self.state))
            self.assertEqual(self.state, self.e.load_ledger(root))
            broken = copy.deepcopy(self.state); next(iter(broken["votes"].values()))["account"] = True
            path.write_bytes(self.e.canonical(broken))
            with self.assertRaises(self.e.EvidenceError): self.e.load_ledger(root)
        from unittest.mock import patch
        before = copy.deepcopy(self.state)
        with patch.object(self.e, "MAX_RECEIPTS", 1):
            with self.assertRaisesRegex(self.e.EvidenceError, "LEDGER_CAPACITY_WAIT"): self.accept(account=2)
        self.assertEqual(before, self.state)

    def test_account_limit_preserves_other_account_capacity(self):
        from unittest.mock import patch
        self.accept(account=1)
        before = copy.deepcopy(self.state)
        with patch.object(self.e, "MAX_ACCOUNT_RECEIPTS", 1):
            with self.assertRaisesRegex(self.e.EvidenceError, "ACCOUNT_CAPACITY_WAIT"): self.accept(account=1, issue=10)
            self.assertEqual(before, self.state)
            self.assertEqual("accepted", self.accept(account=2, issue=20)["status"])

    def test_one_alias_shared_by_two_candidates_is_skipped_not_refused(self):
        # 1.7.3 clients give every candidate observed in one capture session the same alias (N1-1).
        first = receipt()
        second = copy.deepcopy(first); second["evidence_id"] = str(uuid.uuid4())
        second["target"]["code_sha256"] = "c" * 64
        for session in second["sessions"]: session["checks"] = [{"field": "zone", "verdict": "PASS"}]
        self.assertEqual("accepted", self.accept(first, 1, 10)["status"])
        self.assertEqual("waiting", self.accept(second, 1, 11)["status"])
        self.assertEqual(1, len(self.state["votes"]))
        grown = copy.deepcopy(second); grown["evidence_id"] = str(uuid.uuid4())
        grown["sessions"] += receipt("PASS")["sessions"]
        self.assertEqual("accepted", self.accept(grown, 1, 12)["status"])
        self.assertEqual({"CONTRADICT", "PASS"}, {vote["verdict"] for vote in self.state["votes"].values()})
        tampered = copy.deepcopy(first); tampered["evidence_id"] = str(uuid.uuid4())
        tampered["sessions"][0]["checks"][0]["verdict"] = "PASS"
        with self.assertRaisesRegex(self.e.EvidenceError, "SESSION_ALIAS_REUSED"): self.accept(tampered, 1, 13)

    def test_one_alias_per_candidate_counts_for_each_candidate(self):
        first = receipt()
        second = receipt("PASS"); second["target"]["code_sha256"] = "c" * 64
        self.assertEqual("accepted", self.accept(first, 1, 10)["status"])
        self.assertEqual("accepted", self.accept(second, 1, 11)["status"])
        self.assertEqual(2, len(self.state["votes"]))

    def test_public_session_aliases_copied_by_another_account_do_not_add_votes(self):
        item = receipt(); self.accept(item, 1, 1)
        copied = copy.deepcopy(item); copied["evidence_id"] = str(uuid.uuid4())
        self.assertEqual("waiting", self.accept(copied, 2, 2)["status"])
        self.assertEqual(1, len(self.state["votes"]))

    def test_copier_processed_first_loses_the_receipt_to_the_earlier_issue(self):
        # N1-4: the copy (account 2, Issue 101) runs before the original (account 1, Issue 100).
        honest = receipt("PASS")
        self.assertEqual("accepted", self.accept(copy.deepcopy(honest), 2, 101)["status"])
        self.assertEqual("accepted", self.accept(honest, 1, 100)["status"])
        self.assertEqual((1, 100), (self.state["receipts"][honest["evidence_id"]]["account"],
                                    self.state["receipts"][honest["evidence_id"]]["issue"]))
        self.assertEqual({1}, {vote["account"] for vote in self.state["votes"].values()})
        self.assertEqual({"1"}, {key.split(":")[0] for key in self.state["sessions"]})
        with self.assertRaisesRegex(self.e.EvidenceError, "EVIDENCE_ID_REUSED"): self.accept(honest, 2, 101)
        self.assertEqual({1}, {vote["account"] for vote in self.state["votes"].values()})

    def test_aliases_copied_under_a_new_id_by_a_later_issue_return_to_the_earlier_issue(self):
        honest = receipt("PASS")
        copied = copy.deepcopy(honest); copied["evidence_id"] = str(uuid.uuid4())
        for session in copied["sessions"]: session["checks"][0]["verdict"] = "CONTRADICT"
        self.assertEqual("accepted", self.accept(copied, 2, 101)["status"])
        self.assertEqual("accepted", self.accept(honest, 1, 100)["status"])
        self.assertNotIn(copied["evidence_id"], self.state["receipts"])
        self.assertEqual([("PASS", 1)], [(v["verdict"], v["account"]) for v in self.state["votes"].values()])
        again = copy.deepcopy(copied); again["evidence_id"] = str(uuid.uuid4())
        self.assertEqual("waiting", self.accept(again, 2, 102)["status"])
        self.assertEqual(1, len(self.state["votes"]))

    def test_a_later_copy_never_takes_an_earlier_issue_s_receipt(self):
        honest = receipt("PASS")
        self.accept(honest, 1, 100)
        with self.assertRaisesRegex(self.e.EvidenceError, "EVIDENCE_ID_REUSED"): self.accept(copy.deepcopy(honest), 2, 101)
        self.assertEqual(1, self.state["receipts"][honest["evidence_id"]]["account"])

    def answer_invalid(self, account, issue):
        # What the workflow does with a body it cannot read: no receipt, but the answer is recorded.
        with self.assertRaises(self.e.EvidenceError):
            self.e.receive(self.state, "invalid", account_id=account, account_created_at="2020-01-01T00:00:00Z",
                           issue_number=issue, now=NOW)
        self.e.record_answer(self.state, issue)

    def test_an_answered_issue_edited_to_carry_a_later_summary_claims_nothing(self):
        # RV4-1: Issue 99 was answered once ("invalid"), before or after Issue 100 was accepted, then edited.
        for placeholder_first in (True, False):
            with self.subTest(placeholder_first=placeholder_first):
                self.state = self.e.empty_ledger()
                honest = receipt("PASS")
                if placeholder_first: self.answer_invalid(2, 99)
                self.assertEqual("accepted", self.accept(honest, 1, 100)["status"])
                self.e.record_answer(self.state, 100)
                if not placeholder_first: self.answer_invalid(2, 99)
                before = copy.deepcopy(self.state)
                with self.assertRaisesRegex(self.e.EvidenceError, "EVIDENCE_ID_REUSED"): self.accept(copy.deepcopy(honest), 2, 99)
                self.assertEqual(before, self.state)
                self.assertEqual((1, 100), (self.state["receipts"][honest["evidence_id"]]["account"],
                                            self.state["receipts"][honest["evidence_id"]]["issue"]))
                self.assertEqual({"1"}, {key.split(":")[0] for key in self.state["sessions"]})
                self.assertEqual([("PASS", 1)], [(v["verdict"], v["account"]) for v in self.state["votes"].values()])

    def test_an_answered_issue_edited_to_carry_a_later_issue_s_aliases_counts_none_of_them(self):
        honest = receipt("PASS")
        self.assertEqual("accepted", self.accept(honest, 1, 100)["status"])
        self.e.record_answer(self.state, 100)
        self.answer_invalid(2, 99)
        copied = copy.deepcopy(honest); copied["evidence_id"] = str(uuid.uuid4())
        for session in copied["sessions"]: session["checks"][0]["verdict"] = "CONTRADICT"
        self.assertEqual("waiting", self.accept(copied, 2, 99)["status"])
        self.assertEqual((1, 100), (self.state["receipts"][honest["evidence_id"]]["account"],
                                    self.state["receipts"][honest["evidence_id"]]["issue"]))
        self.assertEqual({"1"}, {key.split(":")[0] for key in self.state["sessions"]})
        self.assertEqual([("PASS", 1)], [(v["verdict"], v["account"]) for v in self.state["votes"].values()])

    def test_an_issue_never_answered_still_takes_back_its_summary_from_an_answered_later_copy(self):
        # The race N1-4 repairs: the copy (account 2, Issue 100) was received and answered before Issue 99 was.
        for same_id in (True, False):
            with self.subTest(same_id=same_id):
                self.state = self.e.empty_ledger()
                honest = receipt("PASS")
                copied = copy.deepcopy(honest)
                if not same_id:
                    copied["evidence_id"] = str(uuid.uuid4())
                    for session in copied["sessions"]: session["checks"][0]["verdict"] = "CONTRADICT"
                self.assertEqual("accepted", self.accept(copied, 2, 100)["status"])
                self.e.record_answer(self.state, 100)
                self.assertEqual("accepted", self.accept(honest, 1, 99)["status"])
                self.e.record_answer(self.state, 99)
                self.assertEqual((1, 99), (self.state["receipts"][honest["evidence_id"]]["account"],
                                           self.state["receipts"][honest["evidence_id"]]["issue"]))
                self.assertEqual(1, len(self.state["receipts"]))
                self.assertEqual({"1"}, {key.split(":")[0] for key in self.state["sessions"]})
                self.assertEqual([("PASS", 1)], [(v["verdict"], v["account"]) for v in self.state["votes"].values()])

    def test_answers_are_recorded_once_and_the_ledger_stays_strict(self):
        self.e.record_answer(self.state, 7)
        self.accept(account=1, issue=8); self.e.record_answer(self.state, 8)
        self.e.record_answer(self.state, 7)
        self.assertEqual((2, 0, {"7": 0, "8": 1}), (self.state["schema_version"], self.state["answered_floor"], self.state["answered"]))
        with self.assertRaises(self.e.EvidenceError): self.e.record_answer(self.state, 0)
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / self.e.LEDGER_FILE
            path.write_bytes(self.e.canonical(self.state))
            self.assertEqual(self.state, self.e.load_ledger(root))
            for mutate in (lambda s: s["answered"].update({"0": 0}), lambda s: s["answered"].update({"x": 0}),
                           lambda s: s["answered"].update({"9": True}), lambda s: s["answered"].update({"9": 2}),
                           lambda s: s["answered"].update({"9": -1}), lambda s: s.update(answered_floor=-1),
                           lambda s: s.update(answered_floor=True), lambda s: s.update(answered_floor=7),
                           lambda s: s.update(answered=[]), lambda s: s.pop("answered"),
                           lambda s: s.update(schema_version=1)):
                broken = copy.deepcopy(self.state); mutate(broken)
                path.write_bytes(self.e.canonical(broken))
                with self.subTest(broken=broken), self.assertRaisesRegex(self.e.EvidenceError, "LEDGER_CORRUPT"):
                    self.e.load_ledger(root)

    def test_a_version_1_ledger_counts_every_issue_it_knows_as_answered(self):
        # The answers of the code before version 2 were not recorded: no Issue up to the highest one it knows may claim.
        honest = receipt("PASS")
        self.accept(honest, 1, 100)
        legacy = {key: value for key, value in self.state.items() if key not in ("answered", "answered_floor")}
        legacy["schema_version"] = 1
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / self.e.LEDGER_FILE).write_bytes(self.e.canonical(legacy))
            self.state = self.e.load_ledger(root)
            self.assertEqual((2, 100, {}), (self.state["schema_version"], self.state["answered_floor"], self.state["answered"]))
            with self.assertRaisesRegex(self.e.EvidenceError, "EVIDENCE_ID_REUSED"): self.accept(copy.deepcopy(honest), 2, 99)
            legacy["answered"] = {}
            (Path(root) / self.e.LEDGER_FILE).write_bytes(self.e.canonical(legacy))
            with self.assertRaisesRegex(self.e.EvidenceError, "LEDGER_CORRUPT"): self.e.load_ledger(root)

    def test_a_full_answer_table_raises_the_floor_and_never_forgets_an_answer(self):
        from unittest.mock import patch
        for issue in (5, 7): self.e.record_answer(self.state, issue)
        with patch.object(self.e, "MAX_ANSWERED", 2):
            self.e.record_answer(self.state, 3)
        self.assertEqual((7, {}), (self.state["answered_floor"], self.state["answered"]))
        self.e.record_answer(self.state, 6)
        self.assertEqual((7, {}), (self.state["answered_floor"], self.state["answered"]), "6 counts as answered already")
        honest = receipt("PASS")
        self.accept(copy.deepcopy(honest), 2, 8)
        with self.assertRaisesRegex(self.e.EvidenceError, "EVIDENCE_ID_REUSED"): self.accept(honest, 1, 4)
        self.e.record_answer(self.state, 9)
        self.assertEqual({"9": 1}, self.state["answered"])
        with patch.object(self.e, "MAX_LEDGER_BYTES", len(self.e.canonical(self.state)) + 3):
            self.e.record_answer(self.state, 11)
            self.assertEqual((11, {}), (self.state["answered_floor"], self.state["answered"]))
            before = copy.deepcopy(self.state)
        with patch.object(self.e, "MAX_LEDGER_BYTES", len(self.e.canonical(self.state))):
            with self.assertRaisesRegex(self.e.EvidenceError, "LEDGER_CAPACITY_WAIT"): self.e.record_answer(self.state, 123456)
        self.assertEqual(before, self.state)

    def test_lift_ends_a_quarantine_and_consumes_the_negatives_that_set_it(self):
        # N1-3: a maintainer override that apply_hints does not undo, because it lives in the ledger.
        for account in range(1, 4): self.accept(account=account)
        candidate_id = next(iter(self.state["candidates"]))
        before = copy.deepcopy(self.state)
        lifted, fields = self.e.lift(self.state, candidate_id)
        self.assertEqual(before, self.state, "the caller's ledger is left alone")
        self.assertEqual(["zone"], fields)
        candidate = lifted["candidates"][candidate_id]
        self.assertEqual((False, {}, 1, {"zone": lifted["sequence"]}),
                         (candidate["quarantined"], candidate["triggers"], candidate["epoch"], candidate["consumed"]))
        entry = {"region": "CN", "game_build": "2026.09.01.0000.0000", "code_sha256": "b" * 64}
        self.assertEqual((False, 1), tuple(self.e.apply_hints((entry,), lifted)[0][k] for k in ("quarantined", "quarantine_epoch")))
        self.state = lifted
        self.accept(account=4, issue=40)
        self.assertFalse(self.state["candidates"][candidate_id]["quarantined"], "consumed negatives do not count again")
        for account in range(5, 7): self.accept(account=account, issue=account * 10)
        self.assertEqual((True, 2), (self.state["candidates"][candidate_id]["quarantined"], self.state["candidates"][candidate_id]["epoch"]))
        with self.assertRaises(KeyError): self.e.lift(self.state, "f" * 64)
        released, _ = self.e.lift(self.state, candidate_id)
        with self.assertRaises(KeyError): self.e.lift(released, candidate_id)

    def test_compact_reclaims_only_rows_no_reread_issue_can_count_again(self):
        live = receipt  # code b*64 stays published; code c*64 is retired
        for account in range(1, 4): self.accept(live(), account, account)
        for account in range(1, 4): self.accept(live("PASS", 1), account, account + 10)
        first = live("PASS", 1); first["sessions"] = first["sessions"][:1]
        self.assertEqual("waiting", self.accept(first, 4, 40)["status"])
        grown = copy.deepcopy(first); grown["evidence_id"] = str(uuid.uuid4())
        grown["sessions"] += live("PASS", 1)["sessions"][:1]
        self.assertEqual("accepted", self.accept(grown, 4, 40)["status"])
        retired = live(); retired["target"]["code_sha256"] = "c" * 64
        self.accept(retired, 5, 50)
        template = live(kind="TEMPLATE_OBSERVATION")
        template["target"].update(region="GLOBAL", template_profile_id=None, template_sha256=None, code_sha256=None)
        self.accept(template, 6, 60)
        before = copy.deepcopy(self.state)
        compacted, removed = self.e.compact(self.state, {("CN", "2026.09.01.0000.0000", "b" * 64)})
        self.assertEqual(before, self.state, "the caller's ledger is left alone")
        self.assertEqual({"candidates": 1, "votes": 1, "receipts": 2, "sessions": 6 + 2 + 2, "issues": 1}, removed)
        self.assertEqual([("b" * 64)], [c["scope"]["code_sha256"] for c in compacted["candidates"].values()])
        self.assertNotIn(first["evidence_id"], compacted["receipts"])
        self.assertEqual({1}, {row["epoch"] for row in compacted["sessions"].values()})
        self.assertNotIn("50", compacted["issues"])
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / self.e.LEDGER_FILE).write_bytes(self.e.canonical(compacted))
            self.assertEqual(compacted, self.e.load_ledger(root))
        self.state = compacted
        votes = copy.deepcopy(compacted["votes"])
        self.assertEqual("duplicate", self.accept(grown, 4, 40)["status"], "the swept current body is still a duplicate")
        self.assertEqual("duplicate", self.accept(template, 6, 60)["status"])
        with self.assertRaisesRegex(self.e.EvidenceError, "NEW_ISSUE_REQUIRED"): self.accept(first, 4, 40)
        self.assertEqual(votes, self.state["votes"])

    def test_template_artifact_stays_blocked_and_real_producer_fixture_parses(self):
        item = receipt(kind="TEMPLATE_OBSERVATION")
        item["target"].update(region="GLOBAL", template_profile_id=None, template_sha256=None, code_sha256=None)
        first = self.accept(item, 1, 1)
        repeat = copy.deepcopy(item); repeat["evidence_id"] = str(uuid.uuid4())
        self.assertEqual(first["artifact"], self.accept(repeat, 2, 2)["artifact"])
        artifact = self.e.candidate_artifact(item)
        self.assertEqual(("CANDIDATE", "BLOCKED"), (artifact["status"], artifact["readiness"]))
        self.assertTrue(artifact["blockers"])
        fixture = Path(__file__).resolve().parents[2] / "artifacts/automate-calibration-evidence/template-observation-produced.json"
        if fixture.exists(): self.e.parse(fixture.read_bytes())

    def test_producer_one_to_two_session_growth_contract(self):
        directory = Path(__file__).resolve().parents[2] / "artifacts/automate-calibration-evidence"
        one = directory / "producer-code-evidence.one-session.json"
        two = directory / "producer-code-evidence.json"
        if not one.exists() or not two.exists(): self.skipTest("producer-generated integration fixture not built")
        for number, path in enumerate((one, two), 1):
            result = self.e.receive(self.state, path.read_bytes(), account_id=12,
                                    account_created_at="2020-01-01T00:00:00Z", issue_number=number, now=NOW)
            self.assertEqual("waiting" if number == 1 else "accepted", result["status"])
        self.assertEqual(4, len(self.state["votes"]))

    def test_index_hint_roundtrip_and_type_boundaries(self):
        import index as repo_index
        fixture = Path(__file__).with_name("fixtures") / "quarantine-index.json"
        root = json.loads(fixture.read_bytes())
        parsed = repo_index.read_index(fixture.read_bytes())
        self.assertEqual((False, True, False), tuple(e["quarantined"] for e in parsed.entries))
        self.assertEqual((0, 1, 1), tuple(e["quarantine_epoch"] for e in parsed.entries))
        for name, bad in (("quarantined", 1), ("quarantine_epoch", True), ("quarantine_epoch", -1)):
            changed = copy.deepcopy(root); changed["entries"][0][name] = bad
            result = repo_index.read_index(json.dumps(changed).encode())
            self.assertEqual((0, "INVALID:" + name), result.skipped[0])
        marked = repo_index._marked(parsed.entries[1], False, None)
        self.assertEqual((True, 1), (marked["quarantined"], marked["quarantine_epoch"]))


class EvidenceWorkflowTests(unittest.TestCase):
    """Reuse only the existing sandbox harness, not its legacy script tests."""
    def setUp(self):
        _workflow_tests.WorkflowScriptTests.setUp(self)
        helper = self.root / "gh-evidence.py"
        helper.write_text('''import json, os, pathlib, sys
p = pathlib.Path(os.environ["STUB_DIR"]); a = sys.argv[1:]
with (p / "gh.log").open("a") as out: out.write(" ".join(a) + "\\n")
if a[0] == "api":
    endpoint = a[3] if a[1] == "--method" else a[1]
    failure = p / ("http-" + endpoint.replace("/", "_"))
    if failure.exists():
        print("gh: unavailable (HTTP " + failure.read_text() + ")", file=sys.stderr); sys.exit(1)
    if "/comments?" in endpoint:
        number = endpoint.split("/")[4]
        f = p / ("comments-" + number + ".json"); print(f.read_text() if f.exists() else "[]")
    elif "/issues?" in endpoint:
        page = int(endpoint.split("page=")[-1])
        if (p / "large-sweep").exists() and page <= 50:
            print(json.dumps([{"number": (page-1)*100+i, "labels": []} for i in range(1,101)]))
        elif (p / "single-page").exists() and page > 1: print("[]")
        else: print((p / "open.json").read_text())
    elif a[1] == "--method":
        for f in p.glob("comments-*.json"):
            rows = json.loads(f.read_text())
            for row in rows:
                if row["id"] == int(endpoint.split("/")[-1]): row["body"] = a[a.index("-f")+1][5:]
            f.write_text(json.dumps(rows))
    else:
        if "/issues/" in endpoint:
            number = endpoint.split("/")[-1]; counter = p / ("reads-" + number)
            count = int(counter.read_text()) + 1 if counter.exists() else 1; counter.write_text(str(count))
            edited = p / ("issue-" + number + "-read-" + str(count) + ".json")
            if edited.exists(): (p / (endpoint.replace("/", "_") + ".json")).write_text(edited.read_text())
        print((p / (endpoint.replace("/", "_") + ".json")).read_text())
elif a[:2] == ["issue", "comment"]:
    body = pathlib.Path(a[a.index("--body-file") + 1]).read_text(encoding="utf-8")
    (p / ("comments-" + a[2] + ".json")).write_text(json.dumps([{"id": int(a[2])*100+1, "body": body, "user": {"login": "github-actions[bot]", "type": "Bot"}}]))
''', encoding="utf-8")
        from test_workflow_script import _write_script
        _write_script(self.root / "bin/gh", '#!/usr/bin/env bash\npython "' + helper.as_posix() + '" "$@"\n')
    _remove_tree = _workflow_tests.WorkflowScriptTests._remove_tree
    git = _workflow_tests.WorkflowScriptTests.git
    origin_show = _workflow_tests.WorkflowScriptTests.origin_show
    gh_calls = _workflow_tests.WorkflowScriptTests.gh_calls

    def event_for(self, item, number, account=12):
        issue = {"number": number, "state": "open", "body": "### 公开证据摘要\n\n```json\n" + json.dumps(item) + "\n```",
                 "labels": [{"name": "template-observation" if item["kind"] == "TEMPLATE_OBSERVATION" else "calibration-evidence"}],
                 "user": {"id": account, "login": "Player" + str(account), "type": "User"}, "title": "evidence"}
        (self.stub / ("repos_owner_calibrations_issues_%d.json" % number)).write_text(json.dumps(issue), encoding="utf-8")
        (self.stub / ("users_Player%d.json" % account)).write_text(json.dumps(dict(issue["user"], created_at="2020-01-01T00:00:00Z")), encoding="utf-8")
        event = self.root / ("event-%d.json" % number); event.write_text(json.dumps({"issue": issue}), encoding="utf-8")
        return event, issue

    def execute(self, event=None, expected_code=0, **extra_env):
        args = ["--event", str(event)] if event else ["--sweep"]
        result = subprocess.run([sys.executable, "-c", 'import sys,os;sys.path.insert(0,"tools");import index;index.MAX_INDEX_BYTES=int(os.environ.get("EVIDENCE_TEST_INDEX_LIMIT",index.MAX_INDEX_BYTES));import evidence_workflow;sys.exit(evidence_workflow.main())', *args],
                                cwd=self.work, env=dict(self.env, **extra_env), capture_output=True, text=True, encoding="utf-8", timeout=120)
        self.assertEqual(expected_code, result.returncode, result.stderr)

    def seed_published_code(self):
        import index as repo_index
        import sharecode
        import testsupport
        payload = testsupport.payload(); sha = sharecode.code_sha256(payload)
        name = repo_index.code_path("CN", testsupport.BUILD, sha)
        path = self.work / name; path.parent.mkdir(parents=True); path.write_text(sharecode.encode(payload), encoding="utf-8")
        self.git("add", "--", name, cwd=self.work); self.git("commit", "--quiet", "-m", "published code", cwd=self.work)
        entry = {"region": "CN", "game_build": testsupport.BUILD, "code_sha256": sha, "match_source": payload["match_source"],
                 "submitters": 1, "first_published_at": "2026-09-01T00:00:00Z", "path": name,
                 "commit": self.git("rev-parse", "HEAD", cwd=self.work).stdout.strip(), "revoked": False}
        (self.work / "index.json").write_bytes(repo_index.serialize_index((entry,)))
        self.git("add", "--", "index.json", cwd=self.work); self.git("commit", "--quiet", "-m", "published index", cwd=self.work)
        self.git("push", "--quiet", "origin", "HEAD:main", cwd=self.work)
        return payload, sha

    def test_code_verification_complete_git_gh_quarantine_and_recovery(self):
        payload, sha = self.seed_published_code()
        for epoch, verdict, start in ((0, "CONTRADICT", 10), (1, "PASS", 20)):
            for account in range(1, 4):
                item = receipt(verdict, epoch)
                item["target"].update(template_profile_id=payload["template_profile_id"], template_sha256=payload["template_sha256"], code_sha256=sha)
                event, _ = self.event_for(item, start + account, account); self.execute(event)
            entry = json.loads(self.origin_show("main:index.json"))["entries"][0]
            self.assertEqual(epoch == 0, entry["quarantined"])
            self.assertEqual(1, entry["quarantine_epoch"])
            self.assertFalse(entry["revoked"])
        self.assertEqual(6, len(json.loads(self.origin_show("main:evidence-ledger.json"))["receipts"]))

    def test_settled_issues_are_closed_and_the_rest_stay_open_for_the_sweep(self):
        # N1-8: re-reading an accepted position or an archived candidate can only repeat the answer.
        payload, sha = self.seed_published_code()
        target = dict(template_profile_id=payload["template_profile_id"], template_sha256=payload["template_sha256"], code_sha256=sha)
        accepted = receipt(); accepted["target"].update(target)
        waiting = receipt(); waiting["target"].update(target); waiting["sessions"] = waiting["sessions"][:1]
        for number, (item, account) in enumerate(((accepted, 1), (waiting, 2), (receipt(kind="TEMPLATE_OBSERVATION"), 3)), 41):
            event, _ = self.event_for(item, number, account); self.execute(event)
        copied, _ = self.event_for(accepted, 44, 4)
        self.execute(copied)
        closes = [call for call in self.gh_calls() if call.startswith("issue close ")]
        self.assertEqual(["issue close 41 --reason completed", "issue close 43 --reason completed"], closes)

    def published_item(self, payload, sha, verdict="PASS"):
        item = receipt(verdict)
        item["target"].update(template_profile_id=payload["template_profile_id"], template_sha256=payload["template_sha256"], code_sha256=sha)
        return item

    def test_every_answered_issue_is_recorded_whatever_the_outcome(self):
        # RV4-1: only an Issue never answered may claim a copied summary, so every answer is recorded.
        payload, sha = self.seed_published_code()
        waiting = self.published_item(payload, sha); waiting["sessions"] = waiting["sessions"][:1]
        for number, (item, account) in enumerate(((self.published_item(payload, sha), 1), (waiting, 2),
                                                  (receipt(kind="TEMPLATE_OBSERVATION"), 3)), 41):
            event, _ = self.event_for(item, number, account); self.execute(event)
        for number, label, body in ((44, "calibration-evidence", "invalid"), (45, "calibration-report", "plain report")):
            event, issue = self.event_for(receipt(), number, number - 40)
            issue["labels"] = [{"name": label}]; issue["body"] = body
            (self.stub / ("repos_owner_calibrations_issues_%d.json" % number)).write_text(json.dumps(issue), encoding="utf-8")
            self.execute(event)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual({"41": 1, "42": 2, "43": 3, "44": 3, "45": 3}, ledger["answered"])

    def test_an_answered_placeholder_edited_to_copy_an_accepted_summary_is_refused(self):
        # RV4-1 end to end: Issue 99 was answered once ("invalid"); its edit carries Issue 100's accepted summary.
        payload, sha = self.seed_published_code()
        honest = self.published_item(payload, sha)
        event, _ = self.event_for(honest, 100, 1); self.execute(event)
        event, placeholder = self.event_for(honest, 99, 2)
        placeholder["body"] = "placeholder"
        (self.stub / "repos_owner_calibrations_issues_99.json").write_text(json.dumps(placeholder), encoding="utf-8")
        self.execute(event)
        before = json.loads(self.origin_show("main:evidence-ledger.json"))
        event, _ = self.event_for(honest, 99, 2); self.execute(event)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(before, ledger)
        self.assertEqual((1, 100), (ledger["receipts"][honest["evidence_id"]]["account"], ledger["receipts"][honest["evidence_id"]]["issue"]))
        self.assertEqual({1}, {vote["account"] for vote in ledger["votes"].values()})
        self.assertIn("EVIDENCE_ID_REUSED", (self.stub / "comments-99.json").read_text(encoding="utf-8"))

    def test_an_issue_first_processed_after_its_copy_still_takes_it_back(self):
        # N1-4 still holds: the copy (account 2, Issue 100) ran first; Issue 99 had never been answered.
        payload, sha = self.seed_published_code()
        honest = self.published_item(payload, sha)
        event, _ = self.event_for(copy.deepcopy(honest), 100, 2); self.execute(event)
        event, _ = self.event_for(honest, 99, 1); self.execute(event)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual((1, 99), (ledger["receipts"][honest["evidence_id"]]["account"], ledger["receipts"][honest["evidence_id"]]["issue"]))
        self.assertEqual({1}, {vote["account"] for vote in ledger["votes"].values()})
        self.assertEqual({"99", "100"}, set(ledger["answered"]))

    def test_a_report_is_answered_once_left_open_and_publishes_nothing(self):
        # N1-6: report-calibration.yml runs this lane (not report_issue.sh); a text report never votes.
        self.seed_published_code()
        index_before = self.origin_show("main:index.json")
        _, report = self.event_for(receipt(), 51)
        report["labels"] = [{"name": "calibration-report"}]; report["body"] = "plain report"
        (self.stub / "repos_owner_calibrations_issues_51.json").write_text(json.dumps(report), encoding="utf-8")
        event = self.root / "event-51.json"; event.write_text(json.dumps({"issue": report}), encoding="utf-8")
        for _ in range(2): self.execute(event)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(({"51": {"account": 12}}, {}, {}), (ledger["reports"], ledger["receipts"], ledger["votes"]))
        self.assertEqual(index_before, self.origin_show("main:index.json"))
        calls = self.gh_calls()
        self.assertEqual(1, len([call for call in calls if call.startswith("issue comment ")]))
        self.assertFalse(any(call.startswith("issue close ") for call in calls))
        self.assertIn("issue edit 51 --add-label evidence-waiting", calls)

    def test_changed_first_snapshot_does_not_publish_untracked_old_receipt(self):
        item = receipt(kind="TEMPLATE_OBSERVATION")
        event, issue = self.event_for(item, 31)
        issue["body"] = "invalid"
        (self.stub / "issue-31-read-2.json").write_text(json.dumps(issue), encoding="utf-8")
        self.execute(event)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual((0, {}), (ledger["sequence"], ledger["receipts"]))

    def test_push_race_recomputes_and_preserves_other_commit(self):
        from test_workflow_script import PRE_PUSH, _write_script
        rival = self.root / "rival"; self.git("clone", "--quiet", self.origin.as_posix(), str(rival))
        (rival / "other.txt").write_text("independent change")
        self.git("add", "--", "other.txt", cwd=rival); self.git("commit", "--quiet", "-m", "other change", cwd=rival)
        _write_script(self.work / ".git/hooks/pre-push", PRE_PUSH)
        event, _ = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 32)
        self.execute(event, RACE_CLONE=rival.as_posix())
        self.assertEqual(b"independent change", self.origin_show("main:other.txt"))
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(1, ledger["sequence"])

    def test_sweep_account_capacity_wait_does_not_starve_next_issue(self):
        import evidence
        state = evidence.empty_ledger()
        state["reports"] = {str(i): {"account": 12} for i in range(1, 65)}
        (self.work / evidence.LEDGER_FILE).write_bytes(evidence.canonical(state))
        self.git("add", "--", evidence.LEDGER_FILE, cwd=self.work)
        self.git("commit", "--quiet", "-m", "report quota fixture", cwd=self.work)
        self.git("push", "--quiet", "origin", "HEAD:main", cwd=self.work)
        _, report = self.event_for(receipt(), 66)
        report["labels"] = [{"name": "calibration-report"}]; report["body"] = "plain report"
        (self.stub / "repos_owner_calibrations_issues_66.json").write_text(json.dumps(report), encoding="utf-8")
        _, candidate = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 67, 2)
        (self.stub / "open.json").write_text(json.dumps([report, candidate]), encoding="utf-8")
        self.execute()
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual((64, 1), (len(ledger["reports"]), len(ledger["receipts"])))
        self.assertIn("ACCOUNT_CAPACITY_WAIT", (self.stub / "comments-66.json").read_text())

    def test_invalid_injection_then_correction_updates_one_bot_reply(self):
        item = receipt(kind="TEMPLATE_OBSERVATION")
        event, issue = self.event_for(item, 71)
        issue["body"] = '### 公开证据摘要\n\n{"payload":"$(touch injected)"}'
        (self.stub / "repos_owner_calibrations_issues_71.json").write_text(json.dumps(issue), encoding="utf-8")
        self.execute(event)
        event, _ = self.event_for(item, 71)
        self.execute(event)
        self.assertEqual(1, len([c for c in self.gh_calls() if c.startswith("issue comment ")]))
        self.assertTrue(any(c.startswith("api --method PATCH ") for c in self.gh_calls()))
        self.assertFalse((self.work / "injected").exists())

    def test_index_capacity_wait_rolls_back_vote_and_does_not_block_template(self):
        payload, sha = self.seed_published_code()
        item = receipt()
        item["target"].update(template_profile_id=payload["template_profile_id"], template_sha256=payload["template_sha256"], code_sha256=sha)
        _, code_issue = self.event_for(item, 81)
        _, template_issue = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 82, 2)
        (self.stub / "open.json").write_text(json.dumps([code_issue, template_issue]), encoding="utf-8")
        limit = str(len(self.origin_show("main:index.json")))
        self.execute(EVIDENCE_TEST_INDEX_LIMIT=limit)
        state = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual((1, {}), (len(state["receipts"]), state["votes"]))
        self.assertEqual({"81", "82"}, set(state["answered"]), "the withdrawn receipt's answer is still recorded")
        self.assertIn("PUBLIC_INDEX_CAPACITY_WAIT", (self.stub / "comments-81.json").read_text())
        self.assertNotIn("quarantined", json.loads(self.origin_show("main:index.json"))["entries"][0])

    def test_sweep_continues_past_fifty_pages_on_next_run(self):
        (self.stub / "large-sweep").touch()
        _, issue = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 5001)
        (self.stub / "open.json").write_text(json.dumps([issue]), encoding="utf-8")
        self.execute()
        cursor = json.loads(self.origin_show("main:evidence-sweep.json"))
        self.assertEqual({"after_issue": 5000, "next_page": 51}, cursor)
        self.execute()
        self.assertEqual(1, len(json.loads(self.origin_show("main:evidence-ledger.json"))["receipts"]))
        self.assertEqual({"after_issue": 0, "next_page": 1}, json.loads(self.origin_show("main:evidence-sweep.json")))

    def test_comment_limit_defers_one_issue_and_sweep_accepts_the_next(self):
        _, first = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 91)
        _, second = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 92, 2)
        (self.stub / "open.json").write_text(json.dumps([first, second]), encoding="utf-8")
        (self.stub / "comments-91.json").write_text(json.dumps([{"id": i, "user": {"type": "User", "login": "Player"}, "body": "text"} for i in range(100)]))
        self.execute()
        state = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(2, len(state["receipts"]))
        self.assertTrue((self.stub / "comments-92.json").is_file())

    def test_author_rest_failure_defers_without_consuming_following_issue(self):
        _, first = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 93)
        _, second = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 94, 2)
        (self.stub / "http-users_Player12").write_text("503")
        (self.stub / "open.json").write_text(json.dumps([first, second]), encoding="utf-8")
        self.execute()
        state = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(1, len(state["receipts"]))
        self.assertEqual(2, next(iter(state["receipts"].values()))["account"])

    def test_permission_failure_remains_a_visible_run_failure(self):
        _, issue = self.event_for(receipt(kind="TEMPLATE_OBSERVATION"), 95)
        (self.stub / "http-users_Player12").write_text("403")
        (self.stub / "open.json").write_text(json.dumps([issue]), encoding="utf-8")
        self.execute(expected_code=1)
        self.assertFalse(any(c.startswith("issue comment ") for c in self.gh_calls()))

    def test_sweep_page_accepts_one_hundred_full_issue_bodies(self):
        (self.stub / "single-page").touch()
        issues = [{"number": i, "labels": [], "body": "x" * 4096,
                   "url": "https://api.github.com/repos/owner/calibrations/issues/" + str(i)} for i in range(1, 101)]
        (self.stub / "open.json").write_text(json.dumps(issues), encoding="utf-8")
        self.assertGreater((self.stub / "open.json").stat().st_size, 256 * 1024)
        self.execute()
        self.assertEqual({"after_issue": 0, "next_page": 1}, json.loads(self.origin_show("main:evidence-sweep.json")))

    def test_real_local_git_roundtrip_and_retry_are_idempotent(self):
        item = receipt(kind="TEMPLATE_OBSERVATION")
        item["target"].update(region="GLOBAL", template_profile_id=None, template_sha256=None, code_sha256=None)
        issue = {"number": 10, "state": "open", "body": "### 公开证据摘要\n\n```json\n" + json.dumps(item) + "\n```",
                 "labels": [{"name": "template-observation"}], "user": {"id": 12, "login": "Player", "type": "User"},
                 "title": "candidate", "updated_at": "2026-10-15T00:00:00Z"}
        (self.stub / "repos_owner_calibrations_issues_10.json").write_text(json.dumps(issue), encoding="utf-8")
        (self.stub / "users_Player.json").write_text(json.dumps({"id": 12, "login": "Player", "type": "User", "created_at": "2020-01-01T00:00:00Z"}), encoding="utf-8")
        event = self.root / "event.json"; event.write_text(json.dumps({"issue": issue}), encoding="utf-8")
        for _ in range(2):
            result = subprocess.run([sys.executable, "-c", 'import sys;sys.path.insert(0,"tools");import evidence_workflow;sys.exit(evidence_workflow.main())', "--event", str(event)],
                                    cwd=self.work, env=self.env, capture_output=True, text=True, encoding="utf-8", timeout=60)
            self.assertEqual(0, result.returncode, result.stderr)
        ledger = json.loads(self.origin_show("main:evidence-ledger.json"))
        self.assertEqual(1, ledger["sequence"])
        self.assertEqual(1, len(ledger["receipts"]))
        self.assertEqual(1, len([c for c in self.gh_calls() if c.startswith("issue comment ")]))


if __name__ == "__main__": unittest.main()
