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

    def test_public_session_aliases_copied_by_another_account_do_not_add_votes(self):
        item = receipt(); self.accept(item, 1, 1)
        copied = copy.deepcopy(item); copied["evidence_id"] = str(uuid.uuid4())
        self.assertEqual("waiting", self.accept(copied, 2, 2)["status"])
        self.assertEqual(1, len(self.state["votes"]))

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
