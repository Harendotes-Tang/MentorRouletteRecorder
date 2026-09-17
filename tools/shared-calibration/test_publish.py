#!/usr/bin/env python3
"""publish.py end to end on a synced repository: decisions, files written, replies, hostile issues."""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import testsupport
from testsupport import BUILD
import index as repo_index
import publish
import sharecode
import sync_public_repo

NOW = "2026-09-16T08:00:00Z"
COMMIT_A = "a" * 40


class PublishTestCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mr-publish-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        sync_public_repo.sync(self.repo)
        self.out = self.root / "out"
        self.payload = testsupport.payload("ANNOUNCEMENT", 1)
        self.code = sharecode.encode(self.payload)
        self.sha = sharecode.code_sha256(self.payload)
        self.path = repo_index.code_path("CN", BUILD, self.sha)

    def issue(self, body=None, number=7, user_id=4242, login="Octo-Cat", state="open", labels=("share-calibration",),
              title="[共享校准] CN " + BUILD, user_type="User") -> dict:
        return {"action": "opened", "issue": {
            "number": number, "state": state, "title": title,
            "body": testsupport.issue_body(self.code) if body is None else body,
            "labels": [{"name": name} for name in labels],
            "user": {"id": user_id, "login": login, "type": user_type},
        }}

    @staticmethod
    def account(user_id=4242, created="2020-01-01T00:00:00Z", kind="User", login="Octo-Cat") -> dict:
        return {"id": user_id, "login": login, "type": kind, "created_at": created}

    def call(self, *argv) -> tuple:
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            exit_code = publish.main([str(arg) for arg in argv])
        return exit_code, buffer.getvalue()

    def decide(self, command, event, account, *extra, now=NOW) -> tuple:
        event_path, account_path = self.root / "event.json", self.root / "account.json"
        for path, value in ((event_path, event), (account_path, account)):
            path.write_text(value if isinstance(value, str) else json.dumps(value, ensure_ascii=False), encoding="utf-8")
        exit_code, stdout = self.call(command, "--repo", self.repo, "--event", event_path, "--account", account_path,
                                      "--out", self.out, "--now", now, *extra)
        result = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        comment = (self.out / "comment.md").read_text(encoding="utf-8")
        return exit_code, result, comment, stdout

    def publish_as(self, user_id, login, number=7, code=None) -> dict:
        event = self.issue(body=testsupport.issue_body(code or self.code), number=number, user_id=user_id, login=login)
        account = self.account(user_id, login=login)
        _, checked, _, _ = self.decide("check", event, account)
        status = checked["status"]
        if status not in ("published", "added"):
            return checked
        extra = ("--expect", status) + (("--commit", COMMIT_A) if status == "published" else ())
        exit_code, updated, _, _ = self.decide("update-index", event, account, *extra)
        self.assertEqual(0, exit_code)
        return updated


class FlowTests(PublishTestCase):
    def test_a_new_code_is_checked_and_written_then_indexed_at_the_commit_that_added_it(self):
        exit_code, result, comment, stdout = self.decide("check", self.issue(), self.account())
        self.assertEqual(0, exit_code)
        self.assertEqual(("published", self.path, 1), (result["status"], result["file"], result["submitters"]))
        self.assertEqual(("published", "completed"), (result["label"], result["close_reason"]))
        self.assertEqual(self.code.encode("ascii"), (self.repo / self.path).read_bytes())
        self.assertIn("已发布", comment)
        self.assertIn(self.sha[:12], comment)
        self.assertEqual(1, len(stdout.splitlines()))
        self.assertTrue(stdout.isascii())
        self.assertEqual("published", json.loads(stdout)["status"])
        self.assertEqual((), repo_index.load(self.repo).entries)

        exit_code, result, _, _ = self.decide("update-index", self.issue(), self.account(),
                                              "--expect", "published", "--commit", COMMIT_A)
        self.assertEqual(0, exit_code)
        state = repo_index.load(self.repo)
        self.assertEqual([(self.sha, COMMIT_A, 1, NOW, self.path)],
                         [(e["code_sha256"], e["commit"], e["submitters"], e["first_published_at"], e["path"]) for e in state.entries])
        self.assertEqual([("id:4242", 7)], [(row["account"], row["issue"]) for row in state.submissions])
        self.assertEqual((), repo_index.read_index((self.repo / "index.json").read_bytes()).skipped)
        self.assertEqual("calibration: add CN %s %s" % (BUILD, self.sha[:12]), result["code_commit_message"])
        self.assertEqual("index: published CN %s %s (issue #7)" % (BUILD, self.sha[:12]), result["index_commit_message"])

    def test_another_account_is_added_and_the_same_account_again_is_a_duplicate(self):
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        added = self.publish_as(5151, "Other-One", number=8)
        self.assertEqual(("added", 2, "published", "completed"),
                         (added["status"], added["submitters"], added["label"], added["close_reason"]))
        self.assertIn("已计入", added["comment"])
        self.assertEqual([(2, COMMIT_A)], [(e["submitters"], e["commit"]) for e in repo_index.load(self.repo).entries])
        before = (self.repo / "index.json").read_bytes()
        duplicate = self.publish_as(4242, "Octo-Cat", number=9)
        self.assertEqual(("duplicate", "published", "completed"), (duplicate["status"], duplicate["label"], duplicate["close_reason"]))
        self.assertEqual(before, (self.repo / "index.json").read_bytes())

    def test_a_second_different_code_from_the_same_account_is_refused_and_writes_nothing(self):
        self.publish_as(4242, "Octo-Cat")
        payload = testsupport.payload("MARKER_OFFSET", 2)
        other = sharecode.encode(payload)
        _, result, comment, _ = self.decide("check", self.issue(body=testsupport.issue_body(other), number=8), self.account())
        self.assertEqual(("refused", "ACCOUNT_HAS_OTHER_CODE", "rejected", "not planned"),
                         (result["status"], result["reason"], result["label"], result["close_reason"]))
        self.assertIn("另一份校准码", comment)
        self.assertFalse((self.repo / repo_index.code_path("CN", BUILD, sharecode.code_sha256(payload))).exists())

    def test_update_index_needs_a_commit_for_a_new_code(self):
        self.decide("check", self.issue(), self.account())
        event, account = self.root / "event.json", self.root / "account.json"
        with contextlib.redirect_stderr(io.StringIO()):
            exit_code, _ = self.call("update-index", "--repo", self.repo, "--event", event, "--account", account,
                                     "--out", self.out, "--now", NOW, "--expect", "published")
        self.assertEqual(2, exit_code)

    def test_update_index_that_disagrees_with_check_is_an_invariant_break(self):
        self.decide("check", self.issue(), self.account())
        exit_code, result, _, _ = self.decide("update-index", self.issue(), self.account(), "--expect", "added")
        self.assertEqual((3, "error", "UNEXPECTED_STATUS"), (exit_code, result["status"], result["reason"]))
        self.assertEqual((), repo_index.load(self.repo).entries)

    def test_update_index_needs_the_new_code_file(self):
        exit_code, result, _, _ = self.decide("update-index", self.issue(), self.account(),
                                              "--expect", "published", "--commit", COMMIT_A)
        self.assertEqual((3, "CODE_FILE_MISSING"), (exit_code, result["reason"]))
        self.assertEqual((), repo_index.load(self.repo).entries)

    def test_a_file_name_already_holding_another_code_needs_a_maintainer(self):
        target = self.repo / self.path
        target.parent.mkdir(parents=True)
        target.write_text(sharecode.encode(testsupport.payload("ANNOUNCEMENT", 5)), encoding="ascii")
        _, result, _, _ = self.decide("check", self.issue(), self.account())
        self.assertEqual(("error", "PATH_COLLISION", "needs-maintainer", ""),
                         (result["status"], result["reason"], result["label"], result["close_reason"]))


class RefusalTests(PublishTestCase):
    def assertRefused(self, reason, event=None, account=None) -> tuple:
        _, result, comment, stdout = self.decide("check", event or self.issue(), account or self.account())
        self.assertEqual(("refused", reason), (result["status"], result["reason"]))
        self.assertEqual(("rejected", "not planned"), (result["label"], result["close_reason"]))
        self.assertIn("没有受理", comment)
        self.assertEqual([], list(self.repo.rglob("*.mrc")))
        self.assertTrue(stdout.isascii())
        return result, comment

    def test_an_account_younger_than_thirty_days(self):
        self.assertRefused("ACCOUNT_TOO_NEW", account=self.account(created="2026-08-17T08:00:01Z"))
        _, result, _, _ = self.decide("check", self.issue(), self.account(created="2026-08-17T08:00:00Z"))
        self.assertEqual("published", result["status"])

    def test_bots_organisations_and_accounts_that_are_not_the_author(self):
        self.assertRefused("ACCOUNT_NOT_PERSONAL", event=self.issue(user_type="Bot", login="helper[bot]"))
        self.assertRefused("ACCOUNT_NOT_PERSONAL", account=self.account(kind="Organization"))
        self.assertRefused("ACCOUNT_MISMATCH", account=self.account(user_id=999))

    def test_an_unticked_confirmation(self):
        self.assertRefused("NOT_CONFIRMED", event=self.issue(body=testsupport.issue_body(self.code, checked=False)))

    def test_bodies_that_are_not_the_form(self):
        other = sharecode.encode(testsupport.payload("MARKER_OFFSET", 2))
        cases = {
            "CODE_MISSING": "我不用表单 " + self.code,
            "CODE_AMBIGUOUS": testsupport.issue_body(self.code) + "\n### 校准码\n\n" + other + "\n",
            "CONFIRM_MISSING": "### 校准码\n\n" + self.code + "\n",
            "BODY_TOO_LARGE": testsupport.issue_body(self.code) + "x" * 70000,
            "BODY_MISSING": "",
        }
        for reason, body in cases.items():
            with self.subTest(reason):
                _, comment = self.assertRefused(reason, event=self.issue(body=body))
                self.assertIn("分享给其他玩家", comment)

    def test_a_code_that_does_not_decode_gets_the_players_message_and_no_markup_back(self):
        result, comment = self.assertRefused("CODE_INVALID", event=self.issue(body=testsupport.issue_body("<script>alert(1)</script>")))
        self.assertEqual(sharecode.E_NOT_A_CODE, result["detail"])
        self.assertIn(sharecode.MESSAGES[sharecode.E_NOT_A_CODE], comment)
        self.assertNotIn("<script", comment)

    def test_a_hostile_key_name_inside_a_code_is_echoed_defused(self):
        text = sharecode.canonical(self.payload).replace('"pop":{', '"pop":{"@octocat [x](https://evil.example) #1 <b>":1,')
        hostile = testsupport.raw_code(text.encode("utf-8"))
        result, comment = self.assertRefused("CODE_INVALID", event=self.issue(body=testsupport.issue_body(hostile)))
        self.assertEqual(sharecode.E_UNKNOWN_KEY, result["detail"])
        for token in ("@octocat", "](", "https://", "#1", "<b>"):
            self.assertNotIn(token, comment)
        self.assertIn("octocat", comment)

    def test_a_code_made_against_a_template_this_repository_does_not_ship(self):
        vector = testsupport.load_vectors()["valid"][0]
        result, comment = self.assertRefused("TEMPLATE_UNKNOWN", event=self.issue(body=testsupport.issue_body(vector["code"])))
        self.assertEqual(vector["code_sha256"], result["code_sha256"])
        self.assertIn("更新到最新版本", comment)

    def test_a_code_that_does_not_fit_the_template_structure(self):
        bad = sharecode.encode(testsupport.payload("ANNOUNCEMENT", pop={"opcode": 7, "length": 16}))
        self.assertRefused("STRUCTURE_INVALID", event=self.issue(body=testsupport.issue_body(bad)))

    def test_the_title_only_raises_a_note_and_is_never_echoed(self):
        _, _, comment, _ = self.decide("check", self.issue(title="[共享校准] GLOBAL " + BUILD), self.account())
        self.assertIn("标题里写的区服或客户端版本与校准码不一致", comment)
        self.assertNotIn("GLOBAL", comment)
        _, _, comment, _ = self.decide("check", self.issue(title="@octocat $(rm -rf /) `id` [x](https://evil)"), self.account())
        self.assertNotIn("标题", comment)
        for token in ("octocat", "rm -rf", "evil"):
            self.assertNotIn(token, comment)


class SkipAndErrorTests(PublishTestCase):
    def test_closed_unlabelled_and_pull_request_events_are_skipped_silently(self):
        pull_request = self.issue()
        pull_request["issue"]["pull_request"] = {"url": "https://example.invalid"}
        for event, reason in ((self.issue(state="closed"), "NOT_OPEN"), (self.issue(labels=("question",)), "NOT_LABELLED"),
                              (pull_request, "NOT_AN_ISSUE")):
            with self.subTest(reason):
                _, result, comment, _ = self.decide("check", event, self.account())
                self.assertEqual(("skipped", reason, "", "", ""),
                                 (result["status"], result["reason"], result["label"], result["close_reason"], comment))

    def test_repository_side_problems_keep_the_issue_open_for_a_maintainer(self):
        cases = []
        _, result, comment, _ = self.decide("check", self.issue(), "{}")
        cases.append(("ACCOUNT_LOOKUP_FAILED", result, comment))
        _, result, comment, _ = self.decide("check", "not json", self.account())
        cases.append(("EVENT_UNREADABLE", result, comment))
        (self.repo / "index.json").write_text('{"schema_version":1,"entries":[5]}', encoding="utf-8")
        _, result, comment, _ = self.decide("check", self.issue(), self.account())
        cases.append(("INDEX_CORRUPT", result, comment))
        for template in (self.repo / "templates").glob("*.json"):
            template.unlink()
        _, result, comment, _ = self.decide("check", self.issue(), self.account())
        cases.append(("TEMPLATES_BROKEN", result, comment))
        for reason, result, comment in cases:
            with self.subTest(reason):
                self.assertEqual(("error", reason, "needs-maintainer", ""),
                                 (result["status"], result["reason"], result["label"], result["close_reason"]))
                self.assertIn("维护者会处理", comment)

    def test_push_failed_turns_the_last_result_into_a_maintainer_error(self):
        self.decide("check", self.issue(), self.account())
        exit_code, _ = self.call("push-failed", "--out", self.out)
        result = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        self.assertEqual((0, "error", "PUSH_FAILED", 7, "needs-maintainer", ""),
                         (exit_code, result["status"], result["reason"], result["issue"], result["label"], result["close_reason"]))


class HelperCommandTests(PublishTestCase):
    def test_field_prints_only_values_the_shell_may_use(self):
        self.decide("check", self.issue(), self.account())
        for name, expected in (("status", "published"), ("label", "published"), ("close_reason", "completed"), ("file", self.path)):
            with self.subTest(name):
                self.assertEqual((0, expected + "\n"), self.call("field", "--out", self.out, "--name", name))
        data = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        for name, value in (("label", "published; rm -rf /"), ("file", "../../etc/passwd"), ("status", "ok"),
                            ("code_commit_message", "x\n::add-mask::y")):
            with self.subTest(name + " tampered"):
                (self.out / "result.json").write_text(json.dumps(dict(data, **{name: value})), encoding="utf-8")
                with contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual((2, ""), self.call("field", "--out", self.out, "--name", name))

    def test_event_field_validates_the_number_and_the_login(self):
        path = self.root / "event.json"
        path.write_text(json.dumps(self.issue()), encoding="utf-8")
        self.assertEqual((0, "7\n"), self.call("event-field", "--event", path, "--name", "number"))
        self.assertEqual((0, "Octo-Cat\n"), self.call("event-field", "--event", path, "--name", "login"))
        for login in ("a b", "$(id)", "x" * 40, "../users", ""):
            with self.subTest(login):
                path.write_text(json.dumps(self.issue(login=login)), encoding="utf-8")
                self.assertEqual((3, ""), self.call("event-field", "--event", path, "--name", "login"))
        path.write_text(json.dumps({"issue": {"number": "7"}}), encoding="utf-8")
        self.assertEqual((3, ""), self.call("event-field", "--event", path, "--name", "number"))

    def test_pending_lists_only_open_unanswered_submissions(self):
        def rest(number, labels=("share-calibration",), state="open", **extra):
            return dict({"number": number, "state": state, "labels": [{"name": name} for name in labels]}, **extra)

        pages = [
            [rest(1), rest(2, labels=("share-calibration", "published")), rest(3, state="closed")],
            [rest(4, pull_request={}), rest(5, labels=("question",)), rest(6, labels=("share-calibration", "needs-maintainer")), rest(11)],
        ]
        path = self.root / "open.json"
        path.write_text(json.dumps(pages[0], indent=2) + json.dumps(pages[1]), encoding="utf-8")
        self.assertEqual((0, "1\n11\n"), self.call("pending", "--issues", path))
        path.write_text("[{", encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, self.call("pending", "--issues", path)[0])

    def test_wrap_event_turns_a_rest_issue_into_an_event(self):
        source, target = self.root / "issue.json", self.root / "wrapped.json"
        source.write_text(json.dumps(self.issue()["issue"]), encoding="utf-8")
        self.assertEqual(0, self.call("wrap-event", "--issue", source, "--out", target)[0])
        self.assertEqual({"action": "sweep", "issue": self.issue()["issue"]}, json.loads(target.read_text(encoding="utf-8")))
        source.write_text("[]", encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, self.call("wrap-event", "--issue", source, "--out", target)[0])

    def test_revoke_marks_the_code_and_keeps_its_entry(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", self.sha)[0])
        self.assertEqual([(self.sha, True)], [(e["code_sha256"], e["revoked"]) for e in repo_index.load(self.repo).entries])
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, self.call("revoke", "--repo", self.repo, "--code-sha256", "e" * 64)[0])

    def test_two_published_codes_that_disagree_are_both_flagged_and_a_revoke_clears_the_flags(self):
        """plan section 18.6: same build, same template, same match source, different opcodes."""
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        self.assertEqual({self.sha: None}, self.flags())

        other, other_sha = self.another_code("ANNOUNCEMENT", 3)
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=other)["status"])
        self.assertEqual({self.sha: True, other_sha: True}, self.flags())
        self.assertEqual((), repo_index.read_index((self.repo / "index.json").read_bytes()).skipped)

        # Another match source answers another question, so it is no conflict with either.
        third, third_sha = self.another_code("MARKER_OFFSET", 4)
        self.assertEqual("published", self.publish_as(6161, "Third-One", number=9, code=third)["status"])
        self.assertEqual({self.sha: True, other_sha: True, third_sha: None}, self.flags())

        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", self.sha)[0])
        self.assertEqual({self.sha: None, other_sha: None, third_sha: None}, self.flags())

    def test_another_account_submitting_the_same_code_is_no_conflict(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual("added", self.publish_as(5151, "Other-One", number=8)["status"])
        self.assertEqual({self.sha: None}, self.flags())

    def flags(self) -> dict:
        return {item["code_sha256"]: item.get("conflicting") for item in repo_index.load(self.repo).entries}

    @staticmethod
    def another_code(source, number) -> tuple:
        payload = testsupport.payload(source, number)
        return sharecode.encode(payload), sharecode.code_sha256(payload)

    def test_issue_text_can_only_arrive_through_the_event_file(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            publish.main(["check", "--repo", "r", "--event", "e", "--account", "a", "--out", "o", "--body", self.code])
        options = set()
        for action in publish._parser()._subparsers._group_actions[0].choices["check"]._actions:
            options.update(action.option_strings)
        self.assertEqual({"-h", "--help", "--repo", "--event", "--account", "--out", "--now"}, options)


if __name__ == "__main__":
    unittest.main()
