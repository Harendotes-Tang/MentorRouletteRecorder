#!/usr/bin/env python3
"""publish.py end to end on a synced repository: decisions, files written, replies, hostile issues."""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

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

    def publish_as(self, user_id, login, number=7, code=None, basis="calibrated") -> dict:
        event = self.issue(body=testsupport.issue_body(code or self.code, basis=basis), number=number, user_id=user_id,
                           login=login)
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

    def test_a_second_different_code_from_the_same_account_replaces_the_first(self):
        self.publish_as(4242, "Octo-Cat")
        payload = testsupport.payload("MARKER_OFFSET", 2)
        other, other_sha = sharecode.encode(payload), sharecode.code_sha256(payload)
        result = self.publish_as(4242, "Octo-Cat", number=8, code=other)
        self.assertEqual(("published", "published", "completed"), (result["status"], result["label"], result["close_reason"]))
        self.assertEqual([self.sha[:12]], result["replaced"])
        self.assertIn("替换你此前为该版本提交的那一份", result["comment"])
        self.assertIn("旧码已撤回", result["comment"])
        state = repo_index.load(self.repo)
        self.assertEqual({self.sha: True, other_sha: False}, {e["code_sha256"]: e["revoked"] for e in state.entries})
        self.assertEqual([(other_sha, [self.sha])], [(row["code_sha256"], row["replaced"]) for row in state.submissions])
        self.assertEqual((other_sha,), tuple(e["code_sha256"] for e in repo_index.select(state.entries, "CN", BUILD)))

    def test_a_replacement_keeps_a_code_another_account_also_submitted(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual("added", self.publish_as(5151, "Other-One", number=8)["status"])
        other = sharecode.encode(testsupport.payload("MARKER_OFFSET", 2))
        result = self.publish_as(4242, "Octo-Cat", number=9, code=other)
        self.assertEqual("published", result["status"])
        self.assertIn("仍有其他账号提交", result["comment"])
        self.assertNotIn("旧码已撤回", result["comment"])
        state = repo_index.load(self.repo)
        self.assertEqual((False, 1), tuple(next(e for e in state.entries if e["code_sha256"] == self.sha)[name]
                                           for name in ("revoked", "submitters")))

    def test_an_account_whose_code_a_maintainer_revoked_waits_for_review_before_it_submits_again(self):
        """ON1-1, publisher side. This used to publish the second code at once: revoking freed the slot."""
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", self.sha)[0])
        _, refused, comment, _ = self.decide("check", self.issue(number=8), self.account())
        self.assertEqual(("refused", "REVOKED"), (refused["status"], refused["reason"]))
        self.assertIn("已被维护者撤销", comment)
        payload = testsupport.payload("MARKER_OFFSET", 2)
        other, other_sha = sharecode.encode(payload), sharecode.code_sha256(payload)
        held = self.publish_as(4242, "Octo-Cat", number=9, code=other)
        self.assertEqual(("refused", "ACCOUNT_HELD", "rejected", "not planned"),
                         (held["status"], held["reason"], held["label"], held["close_reason"]))
        self.assertIn("已被维护者撤回", held["comment"])
        self.assertIn("维护者复核", held["comment"])
        self.assertIn("请在本 Issue 下留言", held["comment"])
        self.assertNotIn("修正后请重新提交", held["comment"])
        self.assertEqual([self.path], [path.relative_to(self.repo).as_posix() for path in self.repo.rglob("*.mrc")])

        exit_code, stdout = self.call("lift", "--repo", self.repo, "--account", "id:4242", "--region", "CN", "--build", BUILD)
        self.assertEqual((0, {"lifted": "id:4242", "region": "CN", "game_build": BUILD}), (exit_code, json.loads(stdout)))
        result = self.publish_as(4242, "Octo-Cat", number=10, code=other)
        self.assertEqual(("published", []), (result["status"], result["replaced"]))
        state = repo_index.load(self.repo)
        self.assertEqual({self.sha: True, other_sha: False}, {e["code_sha256"]: e["revoked"] for e in state.entries})

    def test_lift_refuses_an_account_that_is_not_held(self):
        self.publish_as(4242, "Octo-Cat")
        before = [(self.repo / name).read_bytes() for name in ("index.json", "submissions.json")]
        for account, region in (("id:4242", "CN"), ("id:5151", "CN"), ("id:4242", "GLOBAL"), ("someone", "CN")):
            with self.subTest(account=account, region=region), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual((2, ""), self.call("lift", "--repo", self.repo, "--account", account,
                                                    "--region", region, "--build", BUILD))
        self.assertEqual(before, [(self.repo / name).read_bytes() for name in ("index.json", "submissions.json")])

    def test_a_file_name_held_only_by_a_revoked_code_is_taken_over(self):
        """ON1-4: the revoked squatter's file is replaced by the new code, and both entries read back."""
        squatter = self.sha[:12] + ("0" if self.sha[12] != "0" else "1") + self.sha[13:]
        revoked = {"region": "CN", "game_build": BUILD, "code_sha256": squatter, "match_source": "ANNOUNCEMENT",
                   "submitters": 1, "first_published_at": "2026-09-01T00:00:00Z", "path": self.path,
                   "commit": "b" * 40, "revoked": True}
        repo_index.write_files(self.repo, repo_index.Index((revoked,), ()))
        (self.repo / self.path).parent.mkdir(parents=True)
        (self.repo / self.path).write_text(sharecode.encode(testsupport.payload("ANNOUNCEMENT", 5)), encoding="ascii")
        result = self.publish_as(4242, "Octo-Cat")
        self.assertEqual(("published", self.path), (result["status"], result["file"]))
        self.assertEqual(self.code.encode("ascii"), (self.repo / self.path).read_bytes())
        state = repo_index.load(self.repo)
        self.assertEqual({squatter: True, self.sha: False}, {e["code_sha256"]: e["revoked"] for e in state.entries})
        self.assertEqual((self.sha,), tuple(e["code_sha256"] for e in repo_index.select(state.entries, "CN", BUILD)))

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
        for body in (testsupport.issue_body(self.code, checked=False), testsupport.checkbox_issue_body(self.code, checked=False)):
            with self.subTest(body[-30:]):
                _, comment = self.assertRefused("NOT_CONFIRMED", event=self.issue(body=body))
                self.assertIn("没有勾选「上面的来源属实，我已在软件里核对过」", comment)

    def test_a_body_without_a_basis_is_a_calibrated_submission(self):
        """A client that predates the field sends none, and an issue of the older form has no such section."""
        for number, body in enumerate((testsupport.checkbox_issue_body(self.code), testsupport.issue_body(self.code, basis=None)), 7):
            with self.subTest(body[-30:]):
                _, result, comment, _ = self.decide("check", self.issue(body=body, number=number), self.account())
                self.assertEqual(("published", "calibrated"), (result["status"], result["basis"]))
                self.assertIn("**已发布。** 谢谢分享！", comment)

    def test_a_basis_that_was_edited_or_repeated(self):
        cases = (
            ("BASIS_INVALID", testsupport.issue_body(self.code, basis="我自己校准的 @octocat"),
             "「这份校准码的来源」一栏被改动过，请在软件里重新打开分享页面，不要改动该栏。"),
            ("BASIS_AMBIGUOUS", testsupport.issue_body(self.code) + "\n### 这份校准码的来源\n\n" +
             testsupport.BASIS_OPTIONS["verified"] + "\n", "正文里有不止一个「这份校准码的来源」栏目"),
        )
        for reason, body, text in cases:
            with self.subTest(reason):
                result, comment = self.assertRefused(reason, event=self.issue(body=body))
                self.assertIn(text, comment)
                self.assertIsNone(result["basis"])
                self.assertNotIn("octocat", comment)
                self.assertEqual(1, comment.count("不要改动"), "the advice is given once")

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

    def test_a_build_that_is_not_a_real_client_build(self):
        invented = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 1, build="synthetic-build-1"))
        _, comment = self.assertRefused("BUILD_NOT_INDEXABLE", event=self.issue(body=testsupport.issue_body(invented)))
        self.assertIn("2026.09.01.0000.0000", comment)

    def test_a_build_dated_after_the_day_after_submission(self):
        future = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 1, build="2026.09.18.0000.0000"))
        _, comment = self.assertRefused("BUILD_IN_FUTURE", event=self.issue(body=testsupport.issue_body(future)))
        self.assertIn("不是已经发布的游戏客户端版本", comment)


class LimitReplyTests(PublishTestCase):
    """ON1-2: the per-account limits are refusals the submitter can read, never a maintainer's problem."""

    def code_for(self, number, build=BUILD) -> str:
        return sharecode.encode(testsupport.payload("ANNOUNCEMENT", number, build=build))

    def test_a_fourth_code_for_one_build_is_refused_and_names_the_limit(self):
        for offset in range(3):
            self.assertEqual("published", self.publish_as(4242, "Octo-Cat", number=7 + offset, code=self.code_for(1 + offset))["status"])
        result = self.publish_as(4242, "Octo-Cat", number=11, code=self.code_for(9))
        self.assertEqual(("refused", "ACCOUNT_BUILD_LIMIT", "rejected", "not planned"),
                         (result["status"], result["reason"], result["label"], result["close_reason"]))
        self.assertIn("3 份", result["comment"])
        self.assertIn(BUILD, result["comment"])
        self.assertFalse((self.repo / repo_index.code_path("CN", BUILD, sharecode.decode(self.code_for(9)).code_sha256)).exists())

    def test_a_third_build_in_one_day_is_refused_until_the_next_day(self):
        builds = (BUILD, "2026.09.10.0000.0000", "2026.09.15.0000.0000")
        for offset, build in enumerate(builds[:2]):
            self.assertEqual("published", self.publish_as(4242, "Octo-Cat", number=7 + offset, code=self.code_for(1, build))["status"])
        result = self.publish_as(4242, "Octo-Cat", number=9, code=self.code_for(1, builds[2]))
        self.assertEqual(("refused", "ACCOUNT_DAILY_LIMIT", "rejected"), (result["status"], result["reason"], result["label"]))
        self.assertIn("2 个区服与客户端版本", result["comment"])
        self.assertIn("北京时间每天 8:00", result["comment"])

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

    def test_an_issue_that_already_carries_an_answer_label_is_skipped(self):
        """ON1-5: an issue left open for a maintainer must not be answered again on an edit or a relabel."""
        for label in (publish.LABEL_PUBLISHED, publish.LABEL_REJECTED, publish.LABEL_MAINTAINER):
            with self.subTest(label):
                _, result, comment, _ = self.decide("check", self.issue(labels=("share-calibration", label)), self.account())
                self.assertEqual(("skipped", "ALREADY_ANSWERED", "", "", ""),
                                 (result["status"], result["reason"], result["label"], result["close_reason"], comment))
        self.assertEqual([], list(self.repo.rglob("*.mrc")))

    def decide_live(self, live, event=None) -> tuple:
        path = self.root / "live.json"
        path.write_text(live if isinstance(live, str) else json.dumps(live), encoding="utf-8")
        return self.decide("check", event or self.issue(), self.account(), "--live", path)

    def test_with_a_live_view_the_issue_as_it_is_now_decides_not_the_event(self):
        """The event remembers the labels of the moment it fired; the run that answered it came later."""
        labelled = [{"name": "share-calibration"}]
        cases = (
            ({"state": "OPEN", "labels": labelled + [{"name": "needs-maintainer"}]}, "ALREADY_ANSWERED"),
            ({"state": "CLOSED", "labels": labelled}, "NOT_OPEN"),
            ({"state": "OPEN", "labels": []}, "NOT_LABELLED"),
            ("not json", "NOT_OPEN"),
            ({"state": "OPEN"}, "NOT_OPEN"),
            ({"state": 7, "labels": labelled}, "NOT_OPEN"),
        )
        for live, reason in cases:
            with self.subTest(reason=reason, live=repr(live)[:40]):
                _, result, comment, _ = self.decide_live(live)
                self.assertEqual(("skipped", reason, ""), (result["status"], result["reason"], comment))
        self.assertEqual([], list(self.repo.rglob("*.mrc")))
        current = dict(self.issue()["issue"], updated_at=NOW)
        _, result, _, _ = self.decide_live(current, event=self.issue(labels=()))
        self.assertEqual("published", result["status"], "a label added after the event fired still counts")

    def test_a_queued_event_uses_the_current_body_title_and_author(self):
        payload = testsupport.payload("MARKER_OFFSET", 2)
        current = dict(self.issue()["issue"], body=testsupport.issue_body(sharecode.encode(payload)),
                       title="[共享校准] GLOBAL " + BUILD, updated_at=NOW,
                       user={"id": 4242, "login": "Renamed-Player", "type": "User"})
        path = self.root / "live.json"
        path.write_text(json.dumps(current), encoding="utf-8")
        _, result, comment, _ = self.decide("check", self.issue(), self.account(login="Renamed-Player"), "--live", path)
        self.assertEqual(sharecode.code_sha256(payload), result["code_sha256"])
        self.assertFalse((self.repo / self.path).exists(), "the queued event's code must never be written")
        self.assertIn("标题里写的区服", comment, "the current title must also be used")

    def test_an_incomplete_live_response_cannot_fall_back_to_the_event(self):
        current = dict(self.issue()["issue"], updated_at=NOW)
        for missing in ("number", "title", "body", "user", "updated_at"):
            live = dict(current)
            del live[missing]
            with self.subTest(missing=missing):
                _, result, _, _ = self.decide_live(live)
                self.assertEqual(("skipped", "EVENT_UNREADABLE"), (result["status"], result["reason"]))
        _, result, _, _ = self.decide_live(dict(current, number=8))
        self.assertEqual(("skipped", "EVENT_UNREADABLE"), (result["status"], result["reason"]))
        self.assertEqual([], list(self.repo.rglob("*.mrc")))

    def test_mutation_guard_checks_version_contents_and_answer_state(self):
        original = dict(self.issue()["issue"], updated_at=NOW)
        snapshot, live = self.root / "snapshot.json", self.root / "live.json"
        snapshot.write_text(json.dumps(original), encoding="utf-8")

        def unchanged(current, *extra):
            live.write_text(json.dumps(current), encoding="utf-8")
            return self.call("unchanged", "--issue", snapshot, "--live", live, *extra)[0]

        self.assertEqual(0, unchanged(original))
        commented = dict(original, updated_at="2026-09-16T08:00:01Z")
        self.assertEqual(1, unchanged(commented), "an edit-and-restore still invalidates the pre-push snapshot")
        self.assertEqual(0, unchanged(commented, "--after-comment"))
        for current in (dict(commented, body="edited"), dict(commented, title="edited"),
                        dict(commented, state="closed"), dict(commented, labels=[{"name": "published"}]),
                        dict(commented, user={"id": 5151, "login": "other", "type": "User"})):
            with self.subTest(current=current):
                self.assertEqual(1, unchanged(current, "--after-comment"))

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


HOSTILE = "$(whoami) `id` @octocat [x](https://evil.example) <script>alert(1)</script> #1"


class ReportTests(PublishTestCase):
    """The "report a wrong calibration" form: labelled, answered, left open, and nothing automatic."""

    def event(self, body=None, number=12, state="open", labels=("calibration-report",),
              title="[校准有误] CN " + BUILD) -> dict:
        return {"action": "opened", "issue": {
            "number": number, "state": state, "title": title,
            "body": testsupport.report_body() if body is None else body,
            "labels": [{"name": name} for name in labels],
            "user": {"id": 4242, "login": "Octo-Cat", "type": "User"},
        }}

    @staticmethod
    def live(event, state="OPEN", labels=None) -> dict:
        """`gh issue view --json state,labels`: the issue as it is now, not as the event remembers it."""
        names = [label["name"] for label in event["issue"]["labels"]] if isinstance(event, dict) else []
        return {"state": state, "labels": [{"name": name} for name in (names if labels is None else labels)]}

    def report(self, event, live=None) -> tuple:
        path, live_path = self.root / "event.json", self.root / "live.json"
        path.write_text(event if isinstance(event, str) else json.dumps(event, ensure_ascii=False), encoding="utf-8")
        current = self.live(event) if live is None else live
        live_path.write_text(current if isinstance(current, str) else json.dumps(current, ensure_ascii=False),
                             encoding="utf-8")
        exit_code, stdout = self.call("report", "--event", path, "--live", live_path, "--out", self.out)
        result = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        return exit_code, result, (self.out / "comment.md").read_text(encoding="utf-8"), stdout

    def test_a_valid_report_is_labelled_answered_and_left_open(self):
        before = (self.repo / "index.json").read_bytes()
        exit_code, result, comment, stdout = self.report(self.event())
        self.assertEqual((0, "received", None, 12), (exit_code, result["status"], result["reason"], result["issue"]))
        self.assertEqual("calibration-report needs-maintainer", result["labels"])
        self.assertEqual(("CN", BUILD, "弹窗时误报匹配", False),
                         (result["region"], result["game_build"], result["symptom"], result["note"]))
        self.assertIn("已收到。维护者核实后会撤回有问题的校准码，或在此说明原因。", comment)
        self.assertIn("本 Issue 保持打开", comment)
        self.assertEqual("", result.get("close_reason", ""), "a report is never closed automatically")
        self.assertEqual(before, (self.repo / "index.json").read_bytes(), "a report revokes nothing")
        self.assertEqual(1, len(stdout.splitlines()))
        self.assertTrue(stdout.isascii())

    def test_a_report_that_is_not_the_form_is_still_labelled_and_left_open(self):
        cases = {
            "REPORT_BUILD_MISSING": testsupport.report_body(build="_No response_"),
            "REPORT_REGION_INVALID": testsupport.report_body(region="cn"),
            "REPORT_SYMPTOM_AMBIGUOUS": testsupport.report_body() + "\n### 现象\n\n其他\n",
            "BODY_MISSING": "",
        }
        for reason, body in cases.items():
            with self.subTest(reason):
                _, result, comment, _ = self.report(self.event(body=body))
                self.assertEqual(("refused", reason), (result["status"], result["reason"]))
                self.assertEqual("calibration-report needs-maintainer", result["labels"])
                self.assertEqual((None, None, None), (result["region"], result["game_build"], result["symptom"]))
                self.assertIn("本 Issue 保持打开", comment)
                self.assertIn("报告校准有误", comment)

    def test_nothing_from_a_hostile_report_is_echoed_or_executed(self):
        bodies = [testsupport.report_body(note=HOSTILE), testsupport.report_body(region=HOSTILE),
                  testsupport.report_body(build=HOSTILE), testsupport.report_body(symptom=HOSTILE)]
        for body in bodies + [testsupport.report_body()]:
            for title in ("[校准有误] CN " + BUILD, HOSTILE):
                with self.subTest(body=body[:24], title=title[:24]):
                    _, result, comment, stdout = self.report(self.event(body=body, title=title))
                    for token in ("whoami", "octocat", "evil", "<script", "](", "`id`", "#1"):
                        self.assertNotIn(token, comment)
                    self.assertNotIn("whoami", json.dumps(result, ensure_ascii=False))
                    self.assertTrue(stdout.isascii())
                    self.assertEqual(1, len(stdout.splitlines()))

    def test_an_issue_that_was_already_answered_or_is_not_a_report_is_skipped_silently(self):
        pull_request = self.event()
        pull_request["issue"]["pull_request"] = {"url": "https://example.invalid"}
        cases = (
            (self.event(labels=("calibration-report", "needs-maintainer")), None, "ALREADY_ANSWERED"),
            (self.event(labels=("share-calibration",)), None, "NOT_LABELLED"),
            (self.event(), self.live(self.event(), state="CLOSED"), "NOT_OPEN"),
            (pull_request, None, "NOT_AN_ISSUE"),
        )
        for event, live, reason in cases:
            with self.subTest(reason):
                _, result, comment, _ = self.report(event, live=live)
                self.assertEqual(("skipped", reason, "", ""), (result["status"], result["reason"], result["labels"], comment))

    def test_the_labels_and_the_state_are_taken_from_the_issue_now_not_from_the_event(self):
        """A redelivered event still says "unanswered"; the live labels are what decides."""
        stale = self.event()
        answered = self.live(stale, labels=("calibration-report", "needs-maintainer"))
        _, result, comment, _ = self.report(stale, live=answered)
        self.assertEqual(("skipped", "ALREADY_ANSWERED", ""), (result["status"], result["reason"], comment))
        # And the other way round: an event that never carried the label, which was added afterwards.
        added = self.event(labels=())
        _, result, _, _ = self.report(added, live=self.live(added, labels=("calibration-report",)))
        self.assertEqual("received", result["status"])

    def test_an_unreadable_view_of_the_issue_replies_to_nobody(self):
        for live in ("not json", {"state": "OPEN"}, {"state": 7, "labels": []}, {"labels": [{"name": "calibration-report"}]}):
            with self.subTest(repr(live)[:30]):
                _, result, comment, _ = self.report(self.event(), live=live)
                self.assertEqual(("skipped", "NOT_OPEN", ""), (result["status"], result["reason"], comment))

    def test_an_unreadable_event_needs_a_maintainer_rather_than_a_reply_to_nobody(self):
        _, result, comment, _ = self.report("not json")
        self.assertEqual(("error", "EVENT_UNREADABLE"), (result["status"], result["reason"]))
        self.assertIn("维护者", comment)

    def test_the_shell_only_ever_sees_the_two_labels_the_maintainer_created(self):
        self.report(self.event())
        self.assertEqual((0, "calibration-report needs-maintainer\n"), self.call("field", "--out", self.out, "--name", "labels"))
        data = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        for value in ("calibration-report; rm -rf /", "admin", "calibration-report\nneeds-maintainer"):
            with self.subTest(value):
                (self.out / "result.json").write_text(json.dumps(dict(data, labels=value)), encoding="utf-8")
                with contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual((2, ""), self.call("field", "--out", self.out, "--name", "labels"))

    def test_report_text_can_only_arrive_through_the_event_file(self):
        options = set()
        for action in publish._parser()._subparsers._group_actions[0].choices["report"]._actions:
            options.update(action.option_strings)
        self.assertEqual({"-h", "--help", "--event", "--live", "--out"}, options)


class HelperCommandTests(PublishTestCase):
    def test_field_prints_only_values_the_shell_may_use(self):
        self.decide("check", self.issue(), self.account())
        for name, expected in (("status", "published"), ("label", "published"), ("close_reason", "completed"),
                               ("file", self.path), ("conflict_issues", ""), ("conflict_label", "")):
            with self.subTest(name):
                self.assertEqual((0, expected + "\n"), self.call("field", "--out", self.out, "--name", name))
        data = json.loads((self.out / "result.json").read_text(encoding="utf-8"))
        for name, value in (("conflict_issues", "7 12 1234567890"), ("conflict_label", "conflicting")):
            with self.subTest(name + " allowed"):
                (self.out / "result.json").write_text(json.dumps(dict(data, **{name: value})), encoding="utf-8")
                self.assertEqual((0, value + "\n"), self.call("field", "--out", self.out, "--name", name))
        for name, value in (("label", "published; rm -rf /"), ("file", "../../etc/passwd"), ("status", "ok"),
                            ("code_commit_message", "x\n::add-mask::y"), ("conflict_issues", "7; rm -rf /"),
                            ("conflict_issues", "07"), ("conflict_issues", "7  12"), ("conflict_issues", "7\n12"),
                            ("conflict_issues", "-1"), ("conflict_issues", " 7"), ("conflict_issues", "12345678901"),
                            ("conflict_label", "needs-maintainer"), ("conflict_label", "conflicting\n")):
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

    def test_pending_refuses_an_issue_list_over_the_input_cap(self):
        """An open.json past MAX_INPUT_BYTES stops the sweep instead of being read into memory."""
        path = self.root / "open.json"
        # Valid JSON either way - what decides the answer is the size alone, so a padded empty
        # list that would otherwise print nothing and exit 0 is the sharpest case.
        path.write_bytes(b"[]" + b" " * (publish.MAX_INPUT_BYTES - 2))
        self.assertEqual((0, ""), self.call("pending", "--issues", path))
        path.write_bytes(b"[]" + b" " * (publish.MAX_INPUT_BYTES - 1))
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            self.assertEqual((2, ""), self.call("pending", "--issues", path))
        self.assertIn(str(publish.MAX_INPUT_BYTES), errors.getvalue())

    def test_wrap_event_turns_a_rest_issue_into_an_event(self):
        source, target = self.root / "issue.json", self.root / "wrapped.json"
        source.write_text(json.dumps(self.issue()["issue"]), encoding="utf-8")
        self.assertEqual(0, self.call("wrap-event", "--issue", source, "--out", target)[0])
        self.assertEqual({"action": "sweep", "issue": self.issue()["issue"]}, json.loads(target.read_text(encoding="utf-8")))
        source.write_text("[]", encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, self.call("wrap-event", "--issue", source, "--out", target)[0])

    def test_prune_takes_old_builds_out_with_their_rows_and_code_files(self):
        """ON1-2: the maintainer's way out of a full index; what is left loads, and so the Action keeps running."""
        old_build = "2026.08.01.0000.0000"
        old = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 2, build=old_build))
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=old)["status"])
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        old_path = self.repo / repo_index.code_path("CN", old_build, sharecode.decode(old).code_sha256)
        self.assertTrue(old_path.is_file())

        before = self.snapshot()
        plan = {"removed": [{"region": "CN", "game_build": old_build}], "entries": 1, "submissions": 1,
                "files": [repo_index.code_path("CN", old_build, sharecode.decode(old).code_sha256)]}

        # V5-1: without --apply the plan is printed and nothing is touched, so it can be read before anything goes.
        exit_code, stdout = self.call("prune", "--repo", self.repo, "--region", "CN", "--build", old_build)
        self.assertEqual((0, dict(plan, applied=False)), (exit_code, json.loads(stdout)))
        self.assertEqual(before, self.snapshot())

        exit_code, stdout = self.call("prune", "--repo", self.repo, "--region", "CN", "--build", old_build, "--apply")

        self.assertEqual((0, dict(plan, applied=True)), (exit_code, json.loads(stdout)))
        state = repo_index.load(self.repo)
        self.assertEqual([(BUILD, self.sha)], [(e["game_build"], e["code_sha256"]) for e in state.entries])
        self.assertEqual([("id:4242", BUILD)], [(row["account"], row["game_build"]) for row in state.submissions])
        self.assertFalse(old_path.exists())
        self.assertFalse(old_path.parent.exists(), "an emptied build directory goes too")
        self.assertTrue((self.repo / self.path).is_file())

    def test_prune_names_its_builds_and_refuses_one_the_index_does_not_list(self):
        """V5-1: no build is chosen by date, so an invented build dated today cannot push the real one out."""
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        before = self.snapshot()
        for argv in (("--region", "CN", "--build", "2026.08.01.0000.0000", "--apply"),
                     ("--region", "GLOBAL", "--build", BUILD, "--apply"),
                     ("--region", "CN", "--build", BUILD, "--build", "2026.08.01.0000.0000", "--apply"),
                     ("--region", "CN", "--apply"), ("--build", BUILD, "--apply"), ("--keep", "1")):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()):
                try:
                    exit_code = self.call("prune", "--repo", self.repo, *argv)[0]
                except SystemExit as stop:  # argparse refuses a missing or unknown option this way
                    exit_code = stop.code
                self.assertEqual(2, exit_code)
                self.assertEqual(before, self.snapshot())

    def snapshot(self) -> dict:
        """Every file of the repository checkout, by relative path."""
        return {path.relative_to(self.repo).as_posix(): path.read_bytes()
                for path in sorted(self.repo.rglob("*")) if path.is_file()}

    def test_revoke_marks_the_code_and_keeps_its_entry(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", self.sha)[0])
        self.assertEqual([(self.sha, True)], [(e["code_sha256"], e["revoked"]) for e in repo_index.load(self.repo).entries])
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, self.call("revoke", "--repo", self.repo, "--code-sha256", "e" * 64)[0])

    def test_two_published_codes_that_disagree_are_both_flagged_and_a_revoke_clears_the_flags(self):
        """plan section 20.3: same build and template; the same match source with another pop is a dispute."""
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        self.assertEqual({self.sha: None}, self.flags())

        other, other_sha = self.another_code("ANNOUNCEMENT", 3)
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=other)["status"])
        self.assertEqual({self.sha: True, other_sha: True}, self.flags())
        self.assertEqual((), repo_index.read_index((self.repo / "index.json").read_bytes()).skipped)

        # Another match source answers another question, so with the same zone, territory and job it is
        # no conflict with either; a zone that differs is one with both, whatever the match source.
        third, third_sha = self.another_code("MARKER_OFFSET", 4)
        self.assertEqual("published", self.publish_as(6161, "Third-One", number=9, code=third)["status"])
        self.assertEqual({self.sha: True, other_sha: True, third_sha: None}, self.flags())
        fourth, fourth_sha = self.another_code("REPLY_STATE", 5, zone_opcode=1099)
        self.assertEqual("published", self.publish_as(7171, "Fourth-One", number=10, code=fourth)["status"])
        self.assertEqual({self.sha: True, other_sha: True, third_sha: True, fourth_sha: True}, self.flags())

        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", self.sha)[0])
        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", fourth_sha)[0])
        self.assertEqual({self.sha: None, other_sha: None, third_sha: None, fourth_sha: None}, self.flags())

    def test_another_account_submitting_the_same_code_is_no_conflict(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual("added", self.publish_as(5151, "Other-One", number=8)["status"])
        self.assertEqual({self.sha: None}, self.flags())

    def flags(self) -> dict:
        return {item["code_sha256"]: item.get("conflicting") for item in repo_index.load(self.repo).entries}

    @staticmethod
    def another_code(source, number, **overrides) -> tuple:
        """A code with the zone, territory and job of ``self.payload`` unless ``overrides`` says otherwise."""
        payload = testsupport.payload(source, number, **dict(SHARED_WITH_FIRST, **overrides))
        return sharecode.encode(payload), sharecode.code_sha256(payload)

    def test_a_revoke_recomputes_the_shape_prior_of_the_later_builds(self):
        old_build = "2026.08.01.0000.0000"
        old, old_sha = self.marker(2, 24, build=old_build)
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=old)["status"])
        later, later_sha = self.marker(3, 24)
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat", number=9, code=later)["status"])
        self.assertEqual({old_sha: None, later_sha: True}, self.priors())
        self.assertEqual(0, self.call("revoke", "--repo", self.repo, "--code-sha256", old_sha)[0])
        self.assertEqual({old_sha: None, later_sha: None}, self.priors())

    def test_prune_recomputes_the_later_builds_so_no_prior_rests_on_a_removed_build(self):
        old_build = "2026.08.01.0000.0000"
        old, old_sha = self.marker(2, 24, build=old_build)
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=old)["status"])
        later, later_sha = self.marker(3, 24)
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat", number=9, code=later)["status"])
        self.assertEqual({old_sha: None, later_sha: True}, self.priors())
        exit_code, _ = self.call("prune", "--repo", self.repo, "--region", "CN", "--build", old_build, "--apply")
        self.assertEqual((0, {later_sha: None}), (exit_code, self.priors()))
        self.assertEqual((), repo_index.read_index((self.repo / "index.json").read_bytes()).skipped)

    def priors(self) -> dict:
        return {item["code_sha256"]: item.get("shape_prior") for item in repo_index.load(self.repo).entries}

    @staticmethod
    def marker(number, length, build=BUILD) -> tuple:
        payload = testsupport.payload("MARKER_OFFSET", number, build=build,
                                      pop={"opcode": 2000 + number, "length": length, "roulette_offset": 8},
                                      **SHARED_WITH_FIRST)
        return sharecode.encode(payload), sharecode.code_sha256(payload)

    def test_issue_text_can_only_arrive_through_the_event_file(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            publish.main(["check", "--repo", "r", "--event", "e", "--account", "a", "--out", "o", "--body", self.code])
        options = set()
        for action in publish._parser()._subparsers._group_actions[0].choices["check"]._actions:
            options.update(action.option_strings)
        self.assertEqual({"-h", "--help", "--repo", "--event", "--account", "--live", "--out", "--now"}, options)


# The zone, territory and job of PublishTestCase.payload (testsupport.payload("ANNOUNCEMENT", 1)), so that a
# code built with them disagrees with it only where a test says so.
SHARED_WITH_FIRST = dict(zone_opcode=1001, territory_opcode=3001, job_opcode=4001)


class BasisReplyTests(PublishTestCase):
    """Plan section 20.2: a player whose client verified another player's code submits it from the same form."""

    def test_a_verifier_of_a_published_code_is_counted_and_told_so(self):
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        added = self.publish_as(5151, "Other-One", number=8, basis="verified")
        self.assertEqual(("added", 2, "verified"), (added["status"], added["submitters"], added["basis"]))
        self.assertIn("**已计入。** 你的软件在本机核实通过了这份校准码，现在共有 2 个账号提交或核实过它，"
                      "账号越多，其他玩家的软件越优先核实它。", added["comment"])
        self.assertNotIn("之前已经有人发布过", added["comment"])
        rows = {row["account"]: row.get("basis") for row in repo_index.load(self.repo).submissions}
        self.assertEqual({"id:4242": None, "id:5151": "verified"}, rows)

    def test_a_calibrated_submission_reads_as_before(self):
        published = self.publish_as(4242, "Octo-Cat")
        self.assertEqual("calibrated", published["basis"])
        self.assertNotIn("核实通过为准", published["comment"])
        added = self.publish_as(5151, "Other-One", number=8)
        self.assertIn("完全相同的校准码之前已经有人发布过，你的提交已计入：现在共有 2 个 GitHub 账号提交了它", added["comment"])

    def test_a_verified_code_nobody_published_yet_is_published_on_the_verifiers_word(self):
        result = self.publish_as(4242, "Octo-Cat", basis="verified")
        self.assertEqual(("published", "verified"), (result["status"], result["basis"]))
        self.assertIn("**已发布。** 谢谢分享！\n\n这份校准码此前没有发布过，现在以你的核实通过为准发布。\n\n- 区服",
                      result["comment"])
        self.assertEqual(["verified"], [row.get("basis") for row in repo_index.load(self.repo).submissions])


class ConflictReplyTests(PublishTestCase):
    """Plan section 20.4: a code that disagrees with a published one says so, and the rival issues are told."""

    def rival(self, source="ANNOUNCEMENT", number=3, **overrides) -> tuple:
        payload = testsupport.payload(source, number, **dict(SHARED_WITH_FIRST, **overrides))
        return sharecode.encode(payload), sharecode.code_sha256(payload)

    def conflict_files(self) -> dict:
        return {path.name: path.read_text(encoding="utf-8") for path in sorted(self.out.glob("conflict-*.md"))}

    def test_a_code_that_disagrees_names_the_rival_and_leaves_a_notice_for_its_issue(self):
        first = self.publish_as(4242, "Octo-Cat")
        self.assertEqual(([], "", ""), (first["conflicts"], first["conflict_issues"], first["conflict_label"]))
        self.assertEqual({}, self.conflict_files())

        code, _ = self.rival()
        result = self.publish_as(5151, "Other-One", number=8, code=code)

        self.assertEqual(("published", "conflicting", "7"), (result["status"], result["conflict_label"], result["conflict_issues"]))
        self.assertEqual([{"code": self.sha[:12], "issues": [7], "fields": ["匹配报文"]}], result["conflicts"])
        self.assertIn("**提示：仓库里已有另一份同版本的校准码与这份在「匹配报文」上不一致**（Issue #7）。" + publish._CONFLICT_ADVICE,
                      result["comment"])
        self.assertLess(result["comment"].index("- 匹配方式"), result["comment"].index("**提示"), "after the summary")
        self.assertEqual({"conflict-7.md": "**提示：后来提交的校准码（Issue #8）与你这份在「匹配报文」上不一致。** " +
                                           publish._CONFLICT_RULE + "如果后来发现是自己这份有误，在软件里重新分享一次即可替换。\n\n"
                                           "<sub>MentorRecorder 共享校准 · 自动回复</sub>\n"}, self.conflict_files())
        # What the client does (plan section 20.5): the repository's prior only orders, the machine's own profile decides.
        for text in (result["comment"], self.conflict_files()["conflict-7.md"]):
            for phrase in ("它自己在上一版本核实过的档案", "只决定下载与核实的先后", "两份都要先在玩家本机完整核实"):
                self.assertIn(phrase, text)
        self.assertEqual((0, "7\n"), self.call("field", "--out", self.out, "--name", "conflict_issues"))
        self.assertEqual((0, "conflicting\n"), self.call("field", "--out", self.out, "--name", "conflict_label"))

    def test_every_field_and_every_issue_of_each_rival_is_named(self):
        self.publish_as(4242, "Octo-Cat")
        self.assertEqual("added", self.publish_as(5151, "Other-One", number=9, basis="verified")["status"])
        other, _ = self.rival("MARKER_OFFSET", 4, zone_opcode=1099)
        self.assertEqual("published", self.publish_as(6161, "Third-One", number=12, code=other)["status"])
        code, _ = self.rival("ANNOUNCEMENT", 5, zone_opcode=1099, job_opcode=4099)

        result = self.publish_as(7171, "Fourth-One", number=15, code=code)

        # The rival two accounts submitted comes first.
        self.assertEqual([{"code": self.sha[:12], "issues": [7, 9], "fields": ["换区报文", "职业报文", "匹配报文"]},
                          {"code": sharecode.decode(other).code_sha256[:12], "issues": [12], "fields": ["职业报文"]}],
                         result["conflicts"])
        self.assertEqual("7 9 12", result["conflict_issues"])
        self.assertIn("在「换区报文、职业报文、匹配报文」上不一致**（Issue #7、#9）。", result["comment"])
        self.assertIn("在「职业报文」上不一致**（Issue #12）。", result["comment"])
        files = self.conflict_files()
        self.assertEqual(["conflict-12.md", "conflict-7.md", "conflict-9.md"], sorted(files))
        self.assertIn("（Issue #15）与你这份在「换区报文、职业报文、匹配报文」上不一致", files["conflict-9.md"])
        self.assertIn("（Issue #15）与你这份在「职业报文」上不一致", files["conflict-12.md"])

    def test_a_code_that_agrees_on_every_field_it_shares_names_no_rival(self):
        self.publish_as(4242, "Octo-Cat")
        code, _ = self.rival("MARKER_OFFSET", 4)
        result = self.publish_as(5151, "Other-One", number=8, code=code)
        self.assertEqual(("published", [], "", ""),
                         (result["status"], result["conflicts"], result["conflict_issues"], result["conflict_label"]))
        self.assertNotIn("**提示", result["comment"])
        self.assertEqual({}, self.conflict_files())

    def test_at_most_eight_issues_of_a_rival_are_named_and_told(self):
        for offset in range(10):
            self.assertIn(self.publish_as(4300 + offset, "Player-%d" % offset, number=20 + offset)["status"],
                          ("published", "added"))
        code, _ = self.rival()
        published = self.publish_as(5151, "Other-One", number=40, code=code)
        self.assertEqual("published", published["status"])
        self.assertEqual([{"code": self.sha[:12], "issues": list(range(20, 28)), "fields": ["匹配报文"]}], published["conflicts"])
        self.assertEqual(" ".join(str(number) for number in range(20, 28)), published["conflict_issues"])
        self.assertIn("（Issue #20、#21、#22、#23、#24、#25、#26、#27）", published["comment"])
        self.assertEqual(["conflict-%d.md" % number for number in range(20, 28)], sorted(self.conflict_files()))

    def test_one_more_submitter_of_a_disputed_code_is_told_but_the_rival_issues_are_not_told_again(self):
        self.publish_as(4242, "Octo-Cat")
        code, _ = self.rival()
        self.assertEqual("7", self.publish_as(5151, "Other-One", number=8, code=code)["conflict_issues"])
        added = self.publish_as(6161, "Third-One", number=9, code=code, basis="verified")
        self.assertEqual(("added", "conflicting", ""), (added["status"], added["conflict_label"], added["conflict_issues"]))
        self.assertEqual([{"code": self.sha[:12], "issues": [7], "fields": ["匹配报文"]}], added["conflicts"])
        # Not "#7": GitHub would put one more cross-reference on issue 7 for every account that joins the code.
        self.assertIn("**提示：仓库里已有另一份同版本的校准码与这份在「匹配报文」上不一致**（Issue ＃7）。", added["comment"])
        self.assertNotIn("#7", added["comment"])
        self.assertEqual({}, self.conflict_files())
        self.assertEqual((0, "\n"), self.call("field", "--out", self.out, "--name", "conflict_issues"))
        self.assertEqual((0, "conflicting\n"), self.call("field", "--out", self.out, "--name", "conflict_label"))

    def test_a_publication_names_at_most_eight_rival_issues_the_most_submitted_rivals_first(self):
        rivals = (("ANNOUNCEMENT", 1, 20, 3), ("ANNOUNCEMENT", 3, 30, 5), ("ANNOUNCEMENT", 4, 40, 4))
        codes = {}
        for source, number, first_issue, submitters in rivals:
            code, sha = self.rival(source, number)
            codes[number] = sha
            for offset in range(submitters):
                result = self.publish_as(first_issue * 100 + offset, "Player-%d-%d" % (number, offset),
                                         number=first_issue + offset, code=code)
                self.assertIn(result["status"], ("published", "added"))
        code, _ = self.rival("ANNOUNCEMENT", 5)

        result = self.publish_as(9999, "Last-One", number=50, code=code)

        self.assertEqual([{"code": codes[3][:12], "issues": [30, 31, 32, 33, 34], "fields": ["匹配报文"]},
                          {"code": codes[4][:12], "issues": [40, 41, 42], "fields": ["匹配报文"]},
                          {"code": codes[1][:12], "issues": [], "fields": ["匹配报文"]}], result["conflicts"])
        self.assertEqual("30 31 32 33 34 40 41 42", result["conflict_issues"])
        self.assertEqual(sorted("conflict-%d.md" % number for number in (30, 31, 32, 33, 34, 40, 41, 42)),
                         sorted(self.conflict_files()))
        self.assertIn("**（校准码编号 `%s`）。" % codes[1][:12], result["comment"])

    def test_the_conflicting_mark_is_written_even_when_shape_prior_does_not_fit(self):
        old = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 2, build="2026.08.01.0000.0000", **SHARED_WITH_FIRST))
        code, rival_sha = self.rival()
        real = repo_index.check_caps

        def no_room_for_shape_prior(index):
            if any("shape_prior" in entry for entry in index.entries):
                raise repo_index.IndexFull(repo_index.INDEX_FULL_BYTES)
            real(index)

        errors = io.StringIO()
        with mock.patch.object(repo_index, "check_caps", no_room_for_shape_prior), contextlib.redirect_stderr(errors):
            self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=old)["status"])
            self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
            self.assertEqual("published", self.publish_as(6161, "Third-One", number=9, code=code)["status"])
        marks = {entry["code_sha256"]: (entry.get("conflicting"), entry.get("shape_prior"))
                 for entry in repo_index.load(self.repo).entries}
        self.assertEqual((True, None), marks[self.sha])
        self.assertEqual((True, None), marks[rival_sha])
        self.assertIn("writing the conflicting marks without them", errors.getvalue())

    def test_a_rival_whose_ledger_names_no_issue_is_named_by_its_code(self):
        self.publish_as(4242, "Octo-Cat")
        state = repo_index.load(self.repo)
        repo_index.write_files(self.repo, repo_index.Index(state.entries, tuple(dict(row, issue=None) for row in state.submissions)))
        code, _ = self.rival()
        result = self.publish_as(5151, "Other-One", number=8, code=code)
        self.assertEqual(([{"code": self.sha[:12], "issues": [], "fields": ["匹配报文"]}], "", "conflicting"),
                         (result["conflicts"], result["conflict_issues"], result["conflict_label"]))
        self.assertIn("**（校准码编号 `%s`）。" % self.sha[:12], result["comment"])
        self.assertEqual({}, self.conflict_files())

    def test_no_notice_outlives_the_decision_it_was_written_for(self):
        self.publish_as(4242, "Octo-Cat")
        code, _ = self.rival()
        self.publish_as(5151, "Other-One", number=8, code=code)
        self.assertEqual(["conflict-7.md"], sorted(self.conflict_files()))
        self.assertEqual(0, self.call("push-failed", "--out", self.out)[0])
        self.assertEqual({}, self.conflict_files())
        self.assertEqual((0, "\n"), self.call("field", "--out", self.out, "--name", "conflict_label"))


class RemarkTests(PublishTestCase):
    """``remark``: the maintainer's one-off backfill of both marks over builds nothing has touched since."""

    OLD_BUILD = "2026.08.01.0000.0000"

    def setUp(self):
        super().setUp()
        old = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 2, build=self.OLD_BUILD, **SHARED_WITH_FIRST))
        rival = sharecode.encode(testsupport.payload("ANNOUNCEMENT", 3, **SHARED_WITH_FIRST))
        self.assertEqual("published", self.publish_as(5151, "Other-One", number=8, code=old)["status"])
        self.assertEqual("published", self.publish_as(4242, "Octo-Cat")["status"])
        self.assertEqual("published", self.publish_as(6161, "Third-One", number=9, code=rival)["status"])
        self.rival_sha = sharecode.decode(rival).code_sha256
        self.old_sha = sharecode.decode(old).code_sha256
        self.marked = (self.repo / "index.json").read_bytes()
        state = repo_index.load(self.repo)
        # What an index written before section 20.3 looks like: no shape_prior, and the old flag where it was.
        self.unmarked = repo_index.Index(tuple(
            {name: value for name, value in entry.items() if name != "shape_prior"} for entry in state.entries), state.submissions)
        repo_index.write_files(self.repo, self.unmarked)

    def marks(self) -> dict:
        return {entry["code_sha256"]: (entry.get("conflicting"), entry.get("shape_prior"))
                for entry in repo_index.load(self.repo).entries}

    def remark(self, *argv) -> tuple:
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            exit_code, stdout = self.call("remark", "--repo", self.repo, *argv)
        return exit_code, json.loads(stdout) if stdout else None, errors.getvalue()

    def test_every_build_is_recomputed_and_written(self):
        self.assertEqual({self.old_sha: (None, None), self.sha: (True, None), self.rival_sha: (True, None)}, self.marks())
        exit_code, printed, _ = self.remark()
        self.assertEqual((0, {"recomputed": [{"region": "CN", "game_build": self.OLD_BUILD}, {"region": "CN", "game_build": BUILD}],
                              "changed": 2, "dropped": []}), (exit_code, printed))
        self.assertEqual({self.old_sha: (None, None), self.sha: (True, True), self.rival_sha: (True, True)}, self.marks())
        self.assertEqual(self.marked, (self.repo / "index.json").read_bytes(), "the same marks publication writes")
        self.assertEqual((0, 0), (self.remark()[0], self.remark()[1]["changed"]), "recomputing again changes nothing")

    def test_only_the_builds_named_are_recomputed(self):
        stale = repo_index.Index(tuple(dict(entry, conflicting=True) if entry["code_sha256"] == self.old_sha else entry
                                       for entry in self.unmarked.entries), self.unmarked.submissions)
        repo_index.write_files(self.repo, stale)
        exit_code, printed, _ = self.remark("--region", "CN", "--build", BUILD)
        self.assertEqual((0, [{"region": "CN", "game_build": BUILD}], 2), (exit_code, printed["recomputed"], printed["changed"]))
        self.assertEqual({self.old_sha: (True, None), self.sha: (True, True), self.rival_sha: (True, True)}, self.marks())
        exit_code, printed, _ = self.remark("--region", "CN", "--build", self.OLD_BUILD, "--region", "CN", "--build", self.OLD_BUILD)
        self.assertEqual((0, [{"region": "CN", "game_build": self.OLD_BUILD}], 1), (exit_code, printed["recomputed"], printed["changed"]))
        self.assertEqual((None, None), self.marks()[self.old_sha])

    def test_conflicting_survives_when_only_shape_prior_does_not_fit(self):
        bare = repo_index.Index(tuple({name: value for name, value in entry.items() if name not in repo_index.OPTIONAL_FIELDS}
                                      for entry in self.unmarked.entries), self.unmarked.submissions)
        repo_index.write_files(self.repo, bare)
        real = repo_index.check_caps

        def no_room_for_shape_prior(index):
            if any(entry["game_build"] == BUILD and "shape_prior" in entry for entry in index.entries):
                raise repo_index.IndexFull(repo_index.INDEX_FULL_BYTES)
            real(index)

        with mock.patch.object(repo_index, "check_caps", no_room_for_shape_prior):
            exit_code, printed, errors = self.remark()
        self.assertEqual((0, [{"region": "CN", "game_build": BUILD, "kept": "conflicting"}]), (exit_code, printed["dropped"]))
        self.assertIn("would not fit", errors)
        # Released clients still gate on conflicting: it is written although the file had none.
        self.assertEqual({self.old_sha: (None, None), self.sha: (True, None), self.rival_sha: (True, None)}, self.marks())

    def test_the_marks_a_build_had_are_kept_when_not_even_conflicting_fits(self):
        stale = repo_index.Index(tuple(dict(entry, shape_prior=False) if entry["game_build"] == BUILD else entry
                                       for entry in self.unmarked.entries), self.unmarked.submissions)
        repo_index.write_files(self.repo, stale)
        before = {entry["code_sha256"]: entry for entry in stale.entries if entry["game_build"] == BUILD}
        real = repo_index.check_caps

        def no_room_for_any_change_to_the_build(index):
            if any(entry["game_build"] == BUILD and entry != before[entry["code_sha256"]] for entry in index.entries):
                raise repo_index.IndexFull(repo_index.INDEX_FULL_BYTES)
            real(index)

        with mock.patch.object(repo_index, "check_caps", no_room_for_any_change_to_the_build):
            exit_code, printed, _ = self.remark()
        self.assertEqual((0, [{"region": "CN", "game_build": BUILD, "kept": "previous"}]), (exit_code, printed["dropped"]))
        self.assertEqual({self.old_sha: (None, None), self.sha: (True, False), self.rival_sha: (True, False)}, self.marks())

    def test_a_build_the_index_does_not_list_or_a_build_without_its_region_writes_nothing(self):
        before = [(self.repo / name).read_bytes() for name in ("index.json", "submissions.json")]
        for argv in (("--region", "CN", "--build", "2026.07.01.0000.0000"), ("--region", "GLOBAL", "--build", BUILD),
                     ("--region", "CN", "--build", BUILD, "--region", "CN"), ("--build", BUILD)):
            with self.subTest(argv=argv):
                exit_code, printed, _ = self.remark(*argv)
                self.assertEqual((2, None), (exit_code, printed))
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            publish.main(["remark", "--repo", str(self.repo), "--region", "MARS", "--build", BUILD])
        self.assertEqual(before, [(self.repo / name).read_bytes() for name in ("index.json", "submissions.json")])


if __name__ == "__main__":
    unittest.main()
