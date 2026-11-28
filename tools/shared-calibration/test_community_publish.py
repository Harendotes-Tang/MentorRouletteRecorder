from contextlib import chdir
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import community_classification as classification
import community_publish as publisher
import publish
import sync_public_repo
import testsupport


class CommunityPublishTests(unittest.TestCase):
    identifier = "241ff32e-2e0f-4730-9f70-558a9308297b"

    def issue(self, title="First", *, number=45, user=201, base=None, labels=None):
        item = dict(schema_version=1, id=self.identifier, type="macro", content_id=4, region="CN",
                    language="zh-Hans", title=title, body="/p 全角　<t>  \r\n/p <me> <wait.1>",
                    boss="", phase="", role="", tags=[], game_version="7.3", links=[], source="")
        envelope = dict(schema_version=1, license="CC-BY-4.0", consent=True, items=[item],
                        base_revisions={} if base is None else {self.identifier: base})
        return dict(number=number, state="open", title="Contribution", updated_at="2026-11-18T11:00:00Z",
                    user={"id": user, "type": "User", "login": "player-name"}, labels=labels or [],
                    body=classification.MARKER + "\n\n```json\n" + json.dumps(envelope) + "\n```")

    def test_accepts_unlabelled_player_and_updates_idempotently(self):
        issue = self.issue()
        first, entries = publisher.evaluate(issue, {})
        self.assertEqual("published", first["status"])
        self.assertEqual(201, entries[self.identifier]["author"]["id"])
        self.assertEqual("community", entries[self.identifier]["status"])
        self.assertEqual(entries, publisher.evaluate(issue, entries)[1])
        changed, revised = publisher.evaluate(self.issue("Edited"), entries)
        self.assertEqual("published", changed["status"])
        self.assertEqual(2, revised[self.identifier]["revision"])
        self.assertEqual("Edited", revised[self.identifier]["title"])
        self.assertEqual("failed", publisher.evaluate(self.issue("Stale", number=52, base=1), revised)[0]["status"])
        self.assertEqual("published", publisher.evaluate(self.issue("New", number=53, base=2), revised)[0]["status"])
        self.assertEqual("failed", publisher.evaluate(self.issue("Wrong author", user=999), entries)[0]["status"])

    def test_community_classification_prevents_calibration_rejection(self):
        issue = self.issue(labels=[{"name": "share-calibration"}])
        self.assertEqual("conflict", classification.classify(issue))
        self.assertEqual("conflict", publisher.evaluate(issue, {})[0]["status"])
        self.assertIsNone(publish._read_issue({"issue": issue}, issue)[0])
        issue["labels"] = []
        issue["body"] = "### Community submission\n\n" + issue["body"]
        self.assertEqual("published", publisher.evaluate(issue, {})[0]["status"])
        issue["state"] = "closed"
        self.assertIsNone(publisher.evaluate(issue, {})[0])

    def test_rejects_injected_fields_excess_and_invalid_links(self):
        for key, value in [("status", "verified"), ("body", "/p x\n" * 16),
                           ("links", ["821ff32e-2e0f-4730-9f70-558a9308297b"])]:
            issue = self.issue()
            data = publisher.parse_submission(issue)
            data["items"][0][key] = value
            issue["body"] = classification.MARKER + "\n```json\n" + json.dumps(data) + "\n```"
            with self.subTest(key):
                self.assertEqual("failed", publisher.evaluate(issue, {})[0]["status"])

    def test_sweep_retries_against_calibration_commit_and_publishes_latest_issue_once(self):
        with tempfile.TemporaryDirectory(prefix="mr-community-push-") as directory:
            repo = Path(directory) / "public"
            sync_public_repo.sync(repo)
            with chdir(repo):
                publisher.git("init", "-b", "main")
                publisher.git("config", "user.email", "test@example.invalid")
                publisher.git("config", "user.name", "Test")
                publisher.git("add", ".")
                publisher.git("commit", "-m", "seed")
                current_main = publisher.git("rev-parse", "HEAD")
                real_run = subprocess.run
                pushes = 0
                replies = []

                def api(_repo, route):
                    if route.startswith("issues?"):
                        return [self.issue("Stale list snapshot")] if route.endswith("page=1") else []
                    return self.issue("Latest edited body")

                def run(args, **kwargs):
                    nonlocal current_main, pushes
                    if tuple(args) != ("git", "push", "origin", "HEAD:main"):
                        return real_run(args, **kwargs)
                    pushes += 1
                    if pushes == 1:
                        publisher.git("reset", "--hard", current_main)
                        Path("index.json").write_bytes(b'{"schema_version":1,"entries":[]}\n\n')
                        publisher.git("add", "--", "index.json")
                        publisher.git("commit", "-m", "concurrent calibration update")
                        current_main = publisher.git("rev-parse", "HEAD")
                        return subprocess.CompletedProcess(args, 1)
                    current_main = publisher.git("rev-parse", "HEAD")
                    return subprocess.CompletedProcess(args, 0)

                with patch.object(publisher, "api", api), patch.object(publisher.subprocess, "run", run), \
                        patch.object(publisher, "latest_checkout", lambda: publisher.git("reset", "--hard", current_main)), \
                        patch.object(publisher, "reply", lambda *parts: replies.append(parts[-1])), \
                        patch.object(publisher.time, "sleep"):
                    publisher.sweep("fixture")
                    head = publisher.git("rev-parse", "HEAD")
                    publisher.process("fixture", 45)
                pointer, receipt, entries = publisher.load()
                self.assertEqual(2, pushes)
                self.assertEqual(head, publisher.git("rev-parse", "HEAD"))
                self.assertEqual(1, pointer["generation"])
                self.assertEqual("Latest edited body", entries[self.identifier]["title"])
                self.assertEqual("published", receipt["issues"]["45"]["result"]["status"])
                self.assertEqual(replies[0], replies[1])
                self.assertIn("concurrent calibration update", publisher.git("log", "--format=%s"))
                self.assertTrue(Path("index.json").read_bytes().endswith(b"\n\n"))

    def test_snapshot_commit_stays_reachable_and_contains_the_hashed_bytes(self):
        with tempfile.TemporaryDirectory(prefix="mr-community-") as folder:
            root = Path(folder)
            repo = root / "public"
            sync_public_repo.sync(repo)
            def git(*args):
                return subprocess.check_output(("git", *args), cwd=repo).strip()
            git("init", "-b", "main")
            git("config", "user.name", "test")
            git("config", "user.email", "test@example.invalid")
            git("add", ".")
            git("commit", "-m", "seed")
            _, entries = publisher.evaluate(self.issue(), {})
            with patch.object(publisher, "git", lambda *args: git(*args).decode()), \
                    patch.object(publisher, "ROOT", repo / "community/v1"), \
                    patch.object(publisher, "SNAPSHOT", repo / "community/v1/snapshot.json"):
                index = publisher.files_for({}, entries, 1)
            data = subprocess.check_output(("git", "show", index["snapshot_commit"] + ":community/v1/snapshot.json"), cwd=repo)
            self.assertEqual(index["snapshot_sha256"], hashlib.sha256(data).hexdigest())
            self.assertEqual(entries[self.identifier], json.loads(data)["entries"][0])
            self.assertEqual(1, json.loads(data)["generation"])

    def test_sweep_recovers_the_receipt_of_an_edit_with_an_unchanged_result_label(self):
        original = self.issue("original")
        current = self.issue("edited", labels=[{"name": "community-published"}])
        digest = publisher.fingerprint(current)
        result = {"status": "published", "ids": [self.identifier]}
        ledger = {"issues": {"45": publisher.ledger_record(current, digest, result)}}
        bot = {"login": "github-actions[bot]", "type": "Bot"}
        issue_url = "https://api.github.com/repos/fixture/issues/45"
        comments = [{"user": bot, "issue_url": issue_url,
                     "body": publisher.receipt_marker(45, publisher.fingerprint(original))}]

        def api(_repo, route, *args):
            if route.startswith("issues?"):
                return [current] if route.endswith("page=1") else []
            if route.startswith("issues/comments?"):
                return comments
            if route.startswith("issues/45/comments?"):
                return [comments]
            return current

        def checked(*args):
            self.assertEqual(("gh", "issue", "comment"), args[:3])
            comments.append({"user": bot, "issue_url": issue_url, "body": args[-1]})

        with tempfile.TemporaryDirectory() as work:
            ledger_path = Path(work) / "ledger.json"
            ledger_path.write_bytes(publisher.encode(ledger))
            with patch.object(publisher, "api", api), patch.object(publisher, "checked", checked), \
                    patch.object(publisher, "LEDGER", ledger_path), patch.object(publisher, "latest_checkout"), \
                    patch.object(publisher, "load", return_value=({}, ledger, {})):
                publisher.sweep("fixture")
                publisher.sweep("fixture")
        self.assertEqual(2, len(comments))
        self.assertTrue(comments[-1]["body"].startswith(publisher.receipt_marker(45, digest)))

    def test_sweep_confirms_unchanged_issues_across_repository_comment_pages(self):
        repo = "owner/public"
        bot = {"login": "github-actions[bot]", "type": "Bot"}
        issues = [self.issue(number=n, labels=[{"name": "community-published"}]) for n in range(1, 102)]
        rows, comments = {}, []
        for issue in issues:
            number, digest = issue["number"], publisher.fingerprint(issue)
            rows[str(number)] = {"fingerprint": digest, "result": {"status": "published"}}
            comments.append({"user": bot, "issue_url": f"https://api.github.com/repos/{repo}/issues/{number}",
                             "body": publisher.receipt_marker(number, digest) + "\nreceipt"})
        # Markers on a different Issue, from a non-bot, or belonging to a previous edit cannot settle #101.
        last = comments.pop()
        comments.extend((dict(last, issue_url=comments[0]["issue_url"]),
                         dict(last, user=dict(bot, type="User")),
                         dict(last, body=publisher.receipt_marker(101, "0" * 64))))
        calls = []

        def api(_repo, route):
            calls.append(route)
            page = int(route.rsplit("=", 1)[1])
            records = comments if route.startswith("issues/comments?") else issues
            return records[100 * (page - 1):100 * page]

        with tempfile.TemporaryDirectory() as work:
            ledger_path = Path(work) / "ledger.json"
            ledger_path.write_bytes(publisher.encode({"issues": rows}))
            with patch.object(publisher, "api", api), patch.object(publisher, "LEDGER", ledger_path), \
                    patch.object(publisher, "process") as process:
                publisher.sweep(repo)
                process.assert_called_once_with(repo, 101)
                self.assertEqual(4, len(calls))  # two pages of Issues and two of repository comments
                comments.append(last)
                calls.clear()
                process.reset_mock()
                publisher.sweep(repo)
                self.assertEqual(4, len(calls))
                process.assert_not_called()

    def test_old_receipt_among_unrelated_comments_uses_targeted_pagination(self):
        repo = "owner/public"
        marker = publisher.receipt_marker(45, "a" * 64)
        ordinary = {"issue_url": "https://api.github.com/repos/owner/public/issues/12",
                    "user": {"login": "tester", "type": "User"}, "body": "calibration"}
        receipt = {"user": {"login": "github-actions[bot]", "type": "Bot"},
                   "body": marker + "\nPublished"}
        calls = []

        def api(_repo, route, *args):
            calls.append((route, args))
            if route.startswith("issues/comments?"):
                return [ordinary] * 100
            return [[ordinary] * 100, [receipt]]

        with patch.object(publisher, "api", api):
            self.assertEqual([], publisher.missing_receipts(repo, {45: marker}))
        self.assertEqual([("issues/comments?sort=created&direction=desc&per_page=100&page=1", ()),
                          ("issues/45/comments?per_page=100", ("--paginate", "--slurp"))], calls)



class CommunityRepeatTests(unittest.TestCase):
    """The same text twice is published once; text that reads alike is published and reported."""

    def item(self, number, body, *, kind="macro", links=()):
        return dict(schema_version=1, id=f"00000000-0000-4000-8000-{number:012d}", type=kind, content_id=4,
                    region="CN", language="zh-Hans", title=f"Entry {number}", body=body, boss="", phase="",
                    role="", tags=[], game_version="", links=list(links), source="")

    def issue(self, items, *, number, user, login="player-name", bases=None):
        envelope = dict(schema_version=1, license="CC-BY-4.0", consent=True, items=items, base_revisions=bases or {})
        return dict(number=number, state="open", title="Contribution", updated_at="2026-11-23T11:00:00Z",
                    user={"id": user, "type": "User", "login": login}, labels=[],
                    body=classification.MARKER + "\n\n```json\n" + json.dumps(envelope) + "\n```")

    def library(self):
        first = self.item(1, "/p 辛苦了嗷♡ <se.2>\n/p 下次见")
        result, entries = publisher.evaluate(self.issue([first], number=10, user=300, login="first-author"), {})
        self.assertEqual("published", result["status"])
        return first["id"], entries

    def test_texts_compare_as_the_game_and_a_reader_see_them(self):
        # The app runs the same vectors through DutyCommunityMatch.js (DutyCommunityTests::
        # communityTextRulesMatchThePublisher), so the two sides decide alike.
        vectors = json.loads((testsupport.REPO / "tests/Fixtures/shared-calibration/community-text-vectors.json")
                             .read_text(encoding="utf-8"))
        for text, expected in vectors["exact_text"]:
            with self.subTest(exact=text):
                self.assertEqual(expected, publisher.exact_text(text))
        for text, expected in vectors["loose_text"]:
            with self.subTest(loose=text):
                self.assertEqual(expected, publisher.loose_text(text))
        for first, second, expected in vectors["similarity"]:
            with self.subTest(pair=(first, second)):
                self.assertAlmostEqual(expected, publisher.similarity(publisher.loose_text(first),
                                                                      publisher.loose_text(second)), places=6)
        self.assertNotEqual(publisher.exact_text("/p a <se.1>"), publisher.exact_text("/p a <se.2>"))
        for case in vectors["repeats"]:
            with self.subTest(repeats=case["name"]):
                library = {item["id"]: item for item in case["library"]}
                found = publisher.repeats(case["drafts"], library, {"id": 1, "login": "vector"})
                self.assertEqual(case["expected"], {identifier: twin["id"] for identifier, twin in found.items()})
        # The Collector runs the same field vectors (CommunityWorkspaceTests.TextFieldsFollowTheSharedVectors).
        for value, multiline, valid in vectors["field_text"]:
            with self.subTest(field=value, multiline=multiline):
                self.assertEqual(valid, publisher.text(value, 1600, multiline))

    def test_an_own_edit_is_weighed_against_the_library_it_leaves(self):
        first, entries = self.library()
        # A twin left from before this check (or restored by a maintainer) does not stop a title edit.
        twin = dict(entries[first], id=self.item(9, "x")["id"], issue=9)
        entries = {**entries, twin["id"]: twin}
        mine = self.item(1, "/p 辛苦了嗷♡ <se.2>\n/p 下次见")
        mine["title"] = "Renamed"
        result, revised = publisher.evaluate(self.issue([mine], number=20, user=300, login="first-author",
                                                        bases={first: 1}), entries)
        self.assertEqual("published", result["status"])
        self.assertEqual("Renamed", revised[first]["title"])
        # The order of the items does not decide: the new item takes the text the edit gives up.
        _, entries = self.library()
        moved = self.item(1, "/p 换了新的说法")
        fresh = self.item(2, "/p 辛苦了嗷♡ <se.2>\n/p 下次见")
        for items in ([fresh, moved], [moved, fresh]):
            with self.subTest(order=[item["id"][-1] for item in items]):
                result, revised = publisher.evaluate(self.issue(items, number=21, user=300, login="first-author",
                                                            bases={first: 1}), entries)
                self.assertEqual("published", result["status"])
                self.assertNotIn("skipped", result)
                self.assertEqual("/p 换了新的说法", revised[first]["body"])
                self.assertIn(fresh["id"], revised)

    def test_an_edit_into_someone_elses_text_keeps_the_published_version_and_its_links(self):
        first, entries = self.library()
        mine = self.item(2, "/p 我自己写的")
        _, entries = publisher.evaluate(self.issue([mine], number=22, user=201), entries)
        copied = dict(mine, body="/p 辛苦了嗷♡ <se.2>\n/p 下次见")
        note = self.item(3, "跟着提醒走", kind="reminder", links=[mine["id"]])
        result, revised = publisher.evaluate(self.issue([copied, note], number=23, user=201, bases={mine["id"]: 1}),
                                             entries)
        self.assertEqual([note["id"]], result["ids"])
        self.assertEqual([{"id": mine["id"], "same_as": first, "author": "first-author", "own": False, "revision": True}],
                         result["skipped"])
        self.assertEqual("/p 我自己写的", revised[mine["id"]]["body"])
        self.assertEqual([mine["id"]], revised[note["id"]]["links"])
        text = publisher.receipt(result)
        self.assertIn("Edits not applied", text)
        self.assertNotIn("import that entry instead", text)

    def test_edits_that_hand_text_along_are_settled_whatever_their_order(self):
        first, entries = self.library()
        mine, other = self.item(2, "/p 第一段"), self.item(3, "/p 第二段")
        _, entries = publisher.evaluate(self.issue([mine, other], number=24, user=201), entries)
        bases = {mine["id"]: 1, other["id"]: 1}
        # mine wants someone else's text and is refused, so it keeps 第一段; other wanted 第一段.
        refused = dict(mine, body="/p 辛苦了嗷♡ <se.2>\n/p 下次见")
        taker = dict(other, body="/p 第一段")
        for items in ([refused, taker], [taker, refused]):
            with self.subTest(order=[item["id"][-1] for item in items]):
                result, revised = publisher.evaluate(self.issue(items, number=25, user=201, bases=bases), entries)
                self.assertEqual("duplicate", result["status"])
                self.assertEqual({mine["id"], other["id"]}, {entry["id"] for entry in result["skipped"]})
                self.assertIs(entries, revised)
        # Two edits to one new text: the same one is kept back in either order.
        same = [dict(mine, body="/p 第三段"), dict(other, body="/p 第三段")]
        outcomes = []
        for items in (same, same[::-1]):
            result, _ = publisher.evaluate(self.issue(items, number=26, user=201, bases=bases), entries)
            outcomes.append(([entry["id"] for entry in result["skipped"]], result["ids"]))
        self.assertEqual(outcomes[0][0], outcomes[1][0])
        self.assertEqual(1, len(outcomes[0][0]))

    def test_same_text_from_another_author_is_left_out_and_links_point_at_the_first(self):
        first, entries = self.library()
        repeat = self.item(2, "\n/p 辛苦了嗷♡ <se.2>  \r\n/p 下次见\n")
        fresh = self.item(3, "/p 第一次来的话跟着我走就行")
        note = self.item(4, "BOSS 2 记得分散", kind="reminder", links=[repeat["id"]])
        result, revised = publisher.evaluate(self.issue([repeat, fresh, note], number=11, user=201), entries)
        self.assertEqual("published", result["status"])
        self.assertEqual([fresh["id"], note["id"]], result["ids"])
        self.assertEqual([{"id": repeat["id"], "same_as": first, "author": "first-author", "own": False,
                           "revision": False}], result["skipped"])
        self.assertNotIn(repeat["id"], revised)
        self.assertEqual([first], revised[note["id"]]["links"])
        again, unchanged = publisher.evaluate(self.issue([repeat], number=11, user=201), revised)
        self.assertEqual("duplicate", again["status"])
        self.assertIs(revised, unchanged)

    def test_a_submission_of_repeats_only_publishes_nothing(self):
        first, entries = self.library()
        result, revised = publisher.evaluate(self.issue([self.item(2, "/p 辛苦了嗷♡ <se.2>\n/p 下次见")],
                                                        number=12, user=300, login="first-author"), entries)
        self.assertEqual("duplicate", result["status"])
        self.assertTrue(result["skipped"][0]["own"])
        self.assertIs(entries, revised)
        self.assertIn("your `" + first + "`", publisher.receipt(result))

    def test_repeats_inside_one_submission_and_withdrawn_entries(self):
        first, entries = self.library()
        once = self.item(2, "/p 开场先拉一波")
        again = self.item(3, "/p 开场先拉一波 ")
        result, revised = publisher.evaluate(self.issue([once, again], number=13, user=201), entries)
        self.assertEqual([once["id"]], result["ids"])
        self.assertEqual(once["id"], result["skipped"][0]["same_as"])
        withdrawn = dict(entries)
        withdrawn[first] = dict(entries[first], status="withdrawn")
        result, _ = publisher.evaluate(self.issue([self.item(5, "/p 辛苦了嗷♡ <se.2>\n/p 下次见")],
                                                  number=14, user=201), withdrawn)
        self.assertEqual("published", result["status"])
        self.assertNotIn("skipped", result)

    def test_similar_text_is_published_and_named_without_submitted_text(self):
        first, entries = self.library()
        alike = self.item(2, "/p 辛苦了嗷♡ <se.4>\n/p 下次见！")
        result, revised = publisher.evaluate(self.issue([alike], number=15, user=201), entries)
        self.assertEqual("published", result["status"])
        self.assertIn(alike["id"], revised)
        self.assertEqual({alike["id"]: [{"id": first, "author": "first-author"}]}, result["similar"])
        text = publisher.receipt(result)
        self.assertIn(f"`{alike['id']}`: `{first}` by `first-author`", text)
        self.assertNotIn("辛苦", text)
        self.assertNotIn("@", text)
        unrelated, _ = publisher.evaluate(self.issue([self.item(3, "/p 我是中途加入的")], number=16, user=201), entries)
        self.assertNotIn("similar", unrelated)

    def test_reply_sets_the_result_and_similar_labels(self):
        live = {"number": 15, "state": "open", "labels": [{"name": "community-published"}],
                "body": "x", "user": {"id": 201, "login": "player-name"}}
        similar = {"status": "published", "ids": ["a"], "similar": {"a": [{"id": "b", "author": "first-author"}]}}

        def reply(result, comments):
            calls = []
            with patch.object(publisher, "api", lambda _repo, route, *rest: live if route == "issues/15" else [comments]), \
                    patch.object(publisher, "checked", lambda *args, **kwargs: calls.append(args)):
                publisher.reply("fixture", 15, live, result)
            return [args[-2:] for args in calls if args[:3] == ("gh", "issue", "edit")], \
                [args for args in calls if args[:3] == ("gh", "issue", "comment")]

        edits, posted = reply({"status": "duplicate", "skipped": [
            {"id": "a", "same_as": "b", "author": "first-author", "own": False, "revision": False}]}, [])
        self.assertEqual([("--remove-label", "community-published"), ("--add-label", "community-duplicate")], edits)
        self.assertEqual(1, len(posted))
        edits, posted = reply(similar, [])
        self.assertEqual([("--add-label", "community-similar")], edits)
        # Once the receipt is there, a maintainer who removed community-similar keeps it removed.
        receipt = {"user": {"login": "github-actions[bot]", "type": "Bot"},
                   "body": "<!-- mr-community-receipt:15:" + publisher.fingerprint(live) + " -->\nPublished"}
        edits, posted = reply(similar, [receipt])
        self.assertEqual(([], []), (edits, posted))
        live["labels"].append({"name": "community-similar"})
        edits, _ = reply({"status": "published", "ids": ["a"]}, [receipt])
        self.assertEqual([("--remove-label", "community-similar")], edits)


class CommunityCapacityTests(unittest.TestCase):
    """No account can fill the library, withdrawn entries leave room, and a maintainer can remove one."""

    item = CommunityRepeatTests.item
    issue = CommunityRepeatTests.issue

    def published(self, number, author, *, status="community", kind="macro", links=()):
        return dict(self.item(number, f"/p 条目 {number}", kind=kind, links=links),
                    author={"id": author, "login": f"user-{author}"}, license="CC-BY-4.0", revision=1,
                    updated_at="2026-11-20T10:00:00Z", status=status, issue=number)

    def library(self, count, author=None):
        entries = [self.published(number, author or 1000 + number) for number in range(1, count + 1)]
        return {entry["id"]: entry for entry in entries}

    def submit(self, entries, *, user=201, number=900, history=None):
        fresh = self.item(number, f"/p 新的条目 {number}")
        return publisher.evaluate(self.issue([fresh], number=number, user=user), entries, history)

    def test_one_account_keeps_a_bounded_number_of_live_entries(self):
        entries = self.library(publisher.MAX_ACCOUNT_ENTRIES, author=201)
        result, _ = self.submit(entries)
        self.assertEqual("failed", result["status"])
        self.assertIn(str(publisher.MAX_ACCOUNT_ENTRIES), result["detail"])
        self.assertEqual("published", self.submit(entries, user=202)[0]["status"])
        first = next(iter(entries))
        entries[first] = dict(entries[first], status="withdrawn")
        self.assertEqual("published", self.submit(entries)[0]["status"])

    def test_withdrawn_and_removed_entries_leave_room_for_others(self):
        entries = self.library(publisher.MAX_LIVE_ENTRIES)
        result, unchanged = self.submit(entries)
        self.assertEqual(("failed", "The current community release has reached its capacity."),
                         (result["status"], result["detail"]))
        self.assertIs(entries, unchanged)
        first = next(iter(entries))
        withdrawn = dict(entries, **{first: dict(entries[first], status="withdrawn")})
        self.assertEqual("published", self.submit(withdrawn)[0]["status"])
        removed = publisher.without(entries, first, "2026-11-26T08:00:00Z")
        self.assertNotIn(first, removed)
        self.assertEqual("published", self.submit(removed)[0]["status"])

    def test_maintainer_restoration_obeys_account_and_global_limits(self):
        for entries in (self.library(publisher.MAX_ACCOUNT_ENTRIES, author=201),
                        self.library(publisher.MAX_LIVE_ENTRIES)):
            withdrawn = self.published(900, 201, status="withdrawn")
            entries[withdrawn["id"]] = withdrawn
            with patch.object(publisher, "latest_checkout"), \
                    patch.object(publisher, "load", return_value=({"generation": 1}, {}, entries)), \
                    patch.object(publisher, "files_for") as snapshot:
                with self.assertRaises(publisher.Invalid):
                    publisher.moderate(withdrawn["id"], "verified", 1)
                snapshot.assert_not_called()
                del entries[next(iter(entries))]
                with patch.object(publisher, "write"), patch.object(publisher, "git"), \
                        patch.object(publisher.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)):
                    publisher.moderate(withdrawn["id"], "verified", 1)
                restored = snapshot.call_args.args[1][withdrawn["id"]]
                self.assertEqual(("verified", 2), (restored["status"], restored["revision"]))

    def test_removing_an_entry_drops_the_links_to_it_and_revises_the_linking_entries(self):
        macro = self.published(1, 300)
        note = self.published(2, 301, kind="reminder", links=[macro["id"]])
        other = self.published(3, 302, kind="reminder")
        entries = {item["id"]: item for item in (macro, note, other)}
        revised = publisher.without(entries, macro["id"], "2026-11-26T08:00:00Z")
        self.assertEqual({note["id"], other["id"]}, set(revised))
        self.assertEqual(([], 2, "2026-11-26T08:00:00Z"),
                         (revised[note["id"]]["links"], revised[note["id"]]["revision"], revised[note["id"]]["updated_at"]))
        self.assertIs(other, revised[other["id"]])
        self.assertEqual(macro, entries[macro["id"]], "the library passed in is not changed")

    def test_one_account_publishes_a_bounded_number_of_submissions_a_day(self):
        def tally(day, count, author=201):
            return {"schema_version": 1, "issues": {}, "daily": {str(author): {day: count}}}

        result, _ = self.submit({}, history=tally("2026-11-23", publisher.MAX_ACCOUNT_DAILY))
        self.assertEqual("failed", result["status"])
        self.assertIn("tomorrow", result["detail"])
        for name, ledger in (("another day", tally("2026-11-22", 5)),
                             ("another account", tally("2026-11-23", 5, author=202)),
                             ("below the limit", tally("2026-11-23", 4)), ("empty ledger", None)):
            with self.subTest(name):
                self.assertEqual("published", self.submit({}, history=ledger)[0]["status"])
        issue = self.issue([self.item(1, "/p x")], number=7, user=201)
        self.assertEqual({"fingerprint": "d", "result": {"status": "published"}, "author": 201, "day": "2026-11-23"},
                         publisher.ledger_record(issue, "d", {"status": "published"}))

    def ledger_after(self, issues):
        """Runs Issues through evaluate as process does, returning each result and the ledger."""
        ledger, entries, results = {"schema_version": 1, "issues": {}}, {}, []
        for issue in issues:
            result, revised = publisher.evaluate(issue, entries, ledger)
            record = publisher.ledger_record(issue, "digest", result)
            if revised != entries:
                publisher.count_publication(ledger, record)
            ledger["issues"][str(issue["number"])] = record
            entries = revised
            results.append(result["status"])
        return results, ledger

    def test_edits_count_toward_the_daily_limit_and_a_broken_edit_keeps_its_count(self):
        # Six publishing edits of one Issue: the sixth is refused.
        edits = [self.issue([self.item(1, f"/p 第 {n} 版")], number=31, user=201) for n in range(6)]
        results, ledger = self.ledger_after(edits)
        self.assertEqual(["published"] * 5 + ["failed"], results)
        self.assertEqual({"2026-11-23": 5}, ledger["daily"]["201"])
        # Publish, then edit into a failing body, five times over: the sixth Issue is still refused.
        broken = self.issue([dict(self.item(99, "/p x"), title="")], number=0, user=201)
        issues = []
        for number in range(40, 45):
            issues += [self.issue([self.item(number, f"/p 条目 {number}")], number=number, user=201),
                       dict(broken, number=number)]
        issues.append(self.issue([self.item(50, "/p 第六个")], number=50, user=201))
        results, _ = self.ledger_after(issues)
        self.assertEqual(["published", "failed"] * 5 + ["failed"], results)

    def test_a_removed_id_is_never_published_again(self):
        macro = self.published(1, 201)
        ledger = {"schema_version": 1, "issues": {}, "removed": [macro["id"]]}
        again = self.issue([self.item(1, "/p 条目 1")], number=60, user=201)
        result, entries = publisher.evaluate(again, {}, ledger)
        self.assertEqual("failed", result["status"])
        self.assertIn("removed", result["detail"])
        self.assertEqual({}, entries)
        self.assertEqual("published", publisher.evaluate(again, {})[0]["status"])

    def test_entries_published_before_the_character_rule_stay_valid_and_editable(self):
        # Published under the earlier rule: a ZWJ emoji in the title and a bidi control in a tag.
        old = dict(self.published(1, 201), title="宏 👩\u200d🍳", tags=["标\u202e签"])
        note = self.published(2, 300, kind="reminder", links=[old["id"]])
        entries = {old["id"]: old, note["id"]: note}
        publisher.validate(old, published=True, legacy=True)
        with self.assertRaises(publisher.Invalid):
            publisher.validate(old, published=True)
        # The maintainer can still remove it, and the entry linking to it is revised.
        revised = publisher.without(entries, old["id"], "2026-11-26T08:00:00Z")
        self.assertEqual([], revised[note["id"]]["links"])
        # An Issue that leaves it as published and adds another item publishes; changing it with
        # the character still in it is refused.
        unchanged = {key: old[key] for key in publisher.FIELDS}
        fresh = self.item(3, "/p 新的条目")
        result, _ = publisher.evaluate(self.issue([unchanged, fresh], number=1, user=201), entries)
        self.assertEqual(("published", [old["id"], fresh["id"]]), (result["status"], result["ids"]))
        edited = dict(unchanged, body="/p 改过的条目")
        result, _ = publisher.evaluate(self.issue([edited], number=1, user=201, bases={old["id"]: 1}), entries)
        self.assertEqual("failed", result["status"])

    def test_load_accepts_a_snapshot_holding_an_entry_published_under_the_earlier_rule(self):
        old = dict(self.published(1, 201), title="宏 👩\u200d🍳")
        raw = publisher.encode({"schema_version": 1, "generation": 1, "entries": [old]})
        index = {"schema_version": 1, "generation": 1, "snapshot_commit": "a" * 40,
                 "snapshot_sha256": publisher.hashlib.sha256(raw).hexdigest()}
        ledger = {"schema_version": 1, "issues": {}, "daily": {"201": {"2026-11-23": 1}}, "removed": []}
        with tempfile.TemporaryDirectory(prefix="mr-community-load-") as folder, chdir(folder):
            publisher.write(publisher.INDEX, index)
            publisher.write(publisher.LEDGER, ledger)
            publisher.SNAPSHOT.write_bytes(raw)
            with patch.object(publisher.subprocess, "run", lambda args, **_: subprocess.CompletedProcess(args, 0)), \
                    patch.object(publisher.subprocess, "check_output", lambda args, **_: raw):
                _, loaded, items = publisher.load()
        self.assertEqual({old["id"]: old}, items)
        self.assertEqual(ledger, loaded)

    def test_a_title_with_a_reordering_or_invisible_character_is_refused(self):
        for title in ("宏\u202e反向", "宏\u200b", "宏\u2028第二行"):
            draft = dict(self.item(1, "/p x"), title=title)
            with self.subTest(title=title):
                result, _ = publisher.evaluate(self.issue([draft], number=8, user=201), {})
                self.assertEqual("failed", result["status"])
        joined = self.item(2, "/p 👩\u200d🍳 开场")
        self.assertEqual("published", publisher.evaluate(self.issue([joined], number=9, user=201), {})[0]["status"])


class CommunityWithdrawalTests(unittest.TestCase):
    """An author withdraws their own published entries with an Issue, as they submitted them."""

    item = CommunityRepeatTests.item
    issue = CommunityRepeatTests.issue
    published = CommunityCapacityTests.published
    ledger_after = CommunityCapacityTests.ledger_after

    def withdrawal(self, ids, *, number=70, user=201, block=None):
        data = block if block is not None else {"schema_version": 1, "withdraw": list(ids)}
        return dict(number=number, state="open", title="Withdrawal", updated_at="2026-11-26T09:30:00Z",
                    user={"id": user, "type": "User", "login": "player-name"}, labels=[],
                    body=classification.MARKER + "\n\n```json\n" + json.dumps(data) + "\n```")

    def test_the_author_withdraws_own_entries_and_a_second_run_changes_nothing(self):
        macro = self.published(1, 201)
        note = self.published(2, 201, kind="reminder", links=[macro["id"]])
        other = self.published(3, 300)
        entries = {item["id"]: item for item in (macro, note, other)}
        result, revised = publisher.evaluate(self.withdrawal([macro["id"]]), entries)
        self.assertEqual({"status": "withdrawn", "ids": [macro["id"]]}, result)
        self.assertEqual(dict(macro, status="withdrawn", revision=2, updated_at="2026-11-26T09:30:00Z"),
                         revised[macro["id"]], "text, links, author and the publishing Issue stay")
        self.assertIs(note, revised[note["id"]], "an entry linking to it keeps the link")
        self.assertEqual("community", entries[macro["id"]]["status"], "the library passed in is not changed")
        again, unchanged = publisher.evaluate(self.withdrawal([macro["id"]]), revised)
        self.assertEqual(("withdrawn", revised), (again["status"], unchanged))
        text = publisher.receipt(result)
        self.assertIn(macro["id"], text)
        self.assertNotIn(macro["body"], text)
        self.assertEqual("community-withdrawn", publisher.RESULT_LABELS[result["status"]])

    def test_only_the_author_withdraws_and_only_published_ids(self):
        macro = self.published(1, 201)
        entries = {macro["id"]: macro}
        cases = [("another account", self.withdrawal([macro["id"]], user=999), "original author"),
                 ("unknown ID", self.withdrawal([self.item(9, "/p x")["id"]]), "Only published IDs"),
                 ("no ID", self.withdrawal([]), "between 1 and 20"),
                 ("too many", self.withdrawal([self.item(n, "/p x")["id"] for n in range(21)]), "between 1 and 20"),
                 ("twice", self.withdrawal([macro["id"], macro["id"]]), "between 1 and 20"),
                 ("not an ID", self.withdrawal(["macro"]), "between 1 and 20"),
                 ("extra field", self.withdrawal((), block={"schema_version": 1, "withdraw": [macro["id"]],
                                                            "reason": "x"}), "complete withdrawal block")]
        for name, issue, detail in cases:
            with self.subTest(name):
                result, unchanged = publisher.evaluate(issue, entries)
                self.assertEqual("failed", result["status"])
                self.assertIn(detail, result["detail"])
                self.assertIs(entries, unchanged)

    def test_a_withdrawal_is_open_at_the_daily_limit_and_is_not_counted(self):
        # Five publications use up the day; the withdrawal after them goes through and is not counted.
        issues = [self.issue([self.item(n, f"/p 条目 {n}")], number=n, user=201) for n in range(1, 6)]
        issues.append(self.withdrawal([self.item(1, "")["id"]], number=6))
        results, ledger = self.ledger_after(issues)
        self.assertEqual(["published"] * 5 + ["withdrawn"], results)
        self.assertEqual({"2026-11-23": 5}, ledger["daily"]["201"])
        busy = {"schema_version": 1, "issues": {}, "daily": {"201": {"2026-11-26": 5}}}
        macro = self.published(1, 201)
        result, _ = publisher.evaluate(self.withdrawal([macro["id"]]), {macro["id"]: macro}, busy)
        self.assertEqual("withdrawn", result["status"])

    def test_a_withdrawn_entry_is_never_reported_as_published_again(self):
        first, second = self.item(1, "/p 第一条"), self.item(2, "/p 第二条")
        _, entries = publisher.evaluate(self.issue([first, second], number=10, user=201), {})
        _, entries = publisher.evaluate(self.withdrawal([first["id"]], number=11), entries)
        # The Issue that published both is edited: the other entry is published, the withdrawn one named.
        edited = self.issue([first, dict(second, title="改过")], number=10, user=201)
        result, revised = publisher.evaluate(edited, entries)
        self.assertEqual(("published", [second["id"]], [first["id"]]),
                         (result["status"], result["ids"], result["withdrawn"]))
        self.assertEqual("withdrawn", revised[first["id"]]["status"])
        self.assertIn("Withdrawn earlier", publisher.receipt(result))
        # With nothing else in it, the Issue reads as withdrawn earlier and changes nothing.
        result, unchanged = publisher.evaluate(self.issue([first], number=10, user=201), entries)
        self.assertEqual(({"status": "withdrawn", "ids": [first["id"]], "earlier": True}, entries), (result, unchanged))
        self.assertIn("withdrawn earlier", publisher.receipt(result))
        # The same ID with other text is refused; the same text under a new ID publishes.
        result, _ = publisher.evaluate(self.issue([dict(first, body="/p 换了")], number=12, user=201), entries)
        self.assertEqual("failed", result["status"])
        self.assertIn("new ID", result["detail"])
        fresh = dict(first, id=self.item(3, "")["id"])
        self.assertEqual("published", publisher.evaluate(self.issue([fresh], number=13, user=201), entries)[0]["status"])

    def test_withdrawn_entries_count_toward_what_one_account_holds(self):
        # Publishing and withdrawing in turn cannot fill the snapshot: an account holds at most
        # MAX_ACCOUNT_STORED entries of any status, though fewer than 40 of them are live.
        held = publisher.MAX_ACCOUNT_STORED
        entries = {}
        for number in range(1, held + 1):
            entry = self.published(number, 201, status="withdrawn" if number <= held - 30 else "community")
            entries[entry["id"]] = entry
        fresh = self.issue([self.item(900, "/p 新的条目")], number=900, user=201)
        result, unchanged = publisher.evaluate(fresh, entries)
        self.assertEqual("failed", result["status"])
        self.assertIn(str(held), result["detail"])
        self.assertIs(entries, unchanged)
        self.assertEqual("published", publisher.evaluate(dict(fresh, user={**fresh["user"], "id": 202}), entries)[0]["status"])
        live = next(identifier for identifier, entry in entries.items() if entry["status"] == "community")
        self.assertEqual("withdrawn", publisher.evaluate(self.withdrawal([live]), entries)[0]["status"])
        removed = publisher.without(entries, next(iter(entries)), "2026-11-26T08:00:00Z")
        self.assertEqual("published", publisher.evaluate(fresh, removed)[0]["status"])

    def test_a_withdrawn_entry_leaves_the_library_thirty_days_after_its_withdrawal(self):
        now = publisher.dt.datetime(2026, 12, 27, 9, 30, tzinfo=publisher.dt.timezone.utc)
        due = dict(self.published(1, 201, status="withdrawn"), updated_at="2026-11-27T09:30:00Z")
        recent = dict(self.published(2, 201, status="withdrawn", kind="reminder", links=[due["id"]]),
                      updated_at="2026-11-28T09:30:00Z")
        live = dict(self.published(3, 201), updated_at="2026-01-01T00:00:00Z")
        note = self.published(4, 300, kind="reminder", links=[due["id"]])
        entries = {item["id"]: item for item in (due, recent, live, note)}
        removed, revised = publisher.purge(entries, now)
        self.assertEqual([due["id"]], removed, "exactly 30 days goes; 29 days and live entries stay")
        self.assertEqual({recent["id"], live["id"], note["id"]}, set(revised))
        self.assertEqual(([], 2, "2026-12-27T09:30:00Z"),
                         (revised[note["id"]]["links"], revised[note["id"]]["revision"], revised[note["id"]]["updated_at"]))
        self.assertEqual(([], 2, recent["updated_at"]),
                         (revised[recent["id"]]["links"], revised[recent["id"]]["revision"], revised[recent["id"]]["updated_at"]))
        self.assertEqual([recent["id"]], publisher.purge(revised, now + publisher.dt.timedelta(days=1))[0])
        self.assertEqual(([], revised), publisher.purge(revised, now), "a second sweep changes nothing")
        self.assertIn(due["id"], entries, "the library passed in is not changed")
        # The sweep commits the removal and records the IDs, as a maintainer's removal does.
        calls = []
        with patch.object(publisher, "latest_checkout"), \
                patch.object(publisher, "load", lambda: ({"generation": 3}, {"schema_version": 1, "issues": {}}, entries)), \
                patch.object(publisher, "record_removal", lambda *args: calls.append(args) or True):
            self.assertEqual([due["id"]], publisher.purge_withdrawn(now))
        self.assertEqual(([due["id"]], "Remove community entries withdrawn 30 days ago"), calls[0][4:])
        order = []

        def failed_sweep(repo):
            order.append("sweep")
            raise RuntimeError("Community issues to retry: [7]")

        with patch.object(publisher, "purge_withdrawn", lambda: order.append("purge")), \
                patch.object(publisher, "sweep", lambda repo: order.append("sweep")), \
                patch.object(publisher, "refresh_likes", lambda repo: order.append("likes")):
            publisher.scheduled("fixture")
        self.assertEqual(["purge", "sweep", "likes"], order, "the scheduled run purges, sweeps, then reads likes")
        # An Issue left to retry is still reported, after the likes were read.
        order.clear()
        with patch.object(publisher, "purge_withdrawn", lambda: order.append("purge")), \
                patch.object(publisher, "sweep", failed_sweep), \
                patch.object(publisher, "refresh_likes", lambda repo: order.append("likes")), \
                self.assertRaises(RuntimeError):
            publisher.scheduled("fixture")
        self.assertEqual(["purge", "sweep", "likes"], order)

    def test_the_checkout_is_reset_only_in_the_public_repositorys_workflow(self):
        # latest_checkout hard-resets to origin/main. A test that forgot to stub it once reset a
        # developer's clone; outside the publisher's Actions job it now refuses instead.
        calls = []
        with patch.object(publisher, "git", lambda *args: calls.append(args)):
            for environment in ({}, {"CI": "true", "GITHUB_ACTIONS": "true"},
                                {"CI": "true", "GITHUB_ACTIONS": "true", "GH_REPO": "someone/else"}):
                with self.subTest(environment), patch.dict(publisher.os.environ, environment, clear=True), \
                        self.assertRaises(RuntimeError):
                    publisher.latest_checkout()
            with patch.dict(publisher.os.environ, {"CI": "true", "GITHUB_ACTIONS": "true",
                                                    "GH_REPO": "Harendotes-Tang/MentorRecorder-Calibrations"}, clear=True):
                publisher.latest_checkout()
        self.assertEqual([("fetch", "origin", "main"), ("reset", "--hard", "origin/main")], calls)


class CommunityLikeTests(unittest.TestCase):
    """Each live entry gets one like comment; the 👍 on it are its likes, read into likes.json."""

    def entry(self, number, *, issue, status="community"):
        item = CommunityRepeatTests.item(None, number, f"/p {number}")
        return dict(item, author={"id": 300, "login": "first-author"}, license="CC-BY-4.0", revision=1,
                    updated_at="2026-11-27T10:00:00Z", status=status, issue=issue)

    def bot(self, identifier, comment, node):
        return {"id": comment, "node_id": node, "user": {"login": "github-actions[bot]", "type": "Bot"},
                "body": publisher.like_marker(identifier) + "\nLike"}

    def run_likes(self, entries, ledger, comments, reactions, *, likes_file=None):
        """refresh_likes against stubs: `comments` per Issue, `reactions` {node: count or None}."""
        posted, commits = [], []

        def api(_repo, route, *rest):
            if rest[:1] == ("-f",):
                number = int(route.split("/")[1])
                posted.append((number, rest[1]))
                return {"id": 2_400_000_000 + len(posted), "node_id": f"IC_new{len(posted)}"}
            found = comments.get(int(route.split("/")[1]), [])
            if isinstance(found, Exception):
                raise found
            return [found]

        def run(args, **kwargs):
            if args[:3] == ("gh", "api", "graphql"):
                ids = json.loads(kwargs["input"])["variables"]["ids"]
                nodes = [None if reactions.get(node) is None else
                         {"id": node, "reactions": {"totalCount": reactions[node]}} for node in ids]
                errors = [{"type": "NOT_FOUND", "path": ["nodes", index]} for index, node in enumerate(nodes) if node is None]
                return subprocess.CompletedProcess(args, 1 if errors else 0,
                                                   json.dumps({"data": {"nodes": nodes}, "errors": errors}).encode())
            return subprocess.CompletedProcess(args, 0)

        with tempfile.TemporaryDirectory() as folder:
            likes_path, ledger_path = Path(folder) / "likes.json", Path(folder) / "submissions.json"
            if likes_file is not None:
                likes_path.write_bytes(publisher.encode(likes_file))
            with patch.object(publisher, "api", api), patch.object(publisher, "latest_checkout"), \
                    patch.object(publisher, "load", lambda: ({"generation": 1}, json.loads(json.dumps(ledger)), entries)), \
                    patch.object(publisher, "LIKES", likes_path), patch.object(publisher, "LEDGER", ledger_path), \
                    patch.object(publisher, "git", lambda *args: commits.append(args)), \
                    patch.object(publisher.subprocess, "run", run), patch.object(publisher.time, "sleep"):
                self.error = None
                try:
                    publisher.refresh_likes("fixture")
                except RuntimeError as error:
                    self.error = error
            written = (json.loads(likes_path.read_bytes()) if likes_path.exists() else None,
                       json.loads(ledger_path.read_bytes()) if ledger_path.exists() else None)
        return posted, [args for args in commits if args[0] == "commit"], written

    def test_each_live_entry_gets_one_like_comment_and_its_count(self):
        live, recovered, withdrawn = self.entry(1, issue=5), self.entry(2, issue=5), self.entry(3, issue=6, status="withdrawn")
        entries = {item["id"]: item for item in (live, recovered, withdrawn)}
        # Issue 5 holds a like comment an earlier run posted but could not record, and a player's
        # comment that copies the marker of another entry.
        forged = dict(self.bot(live["id"], 99, "IC_forged"), user={"login": "player", "type": "User"})
        comments = {5: [forged, self.bot(recovered["id"], 2_400_000_777, "IC_old")]}
        posted, commits, (likes, ledger) = self.run_likes(
            entries, {"schema_version": 1, "issues": {}}, comments, {"IC_new1": 4, "IC_old": 12})
        # One new comment, in the entry's Issue, naming its ID and never its title.
        self.assertEqual([5], [number for number, _ in posted])
        body = posted[0][1]
        self.assertTrue(body.startswith("body=" + publisher.like_marker(live["id"]) + "\n"), body)
        self.assertIn(live["id"], body)
        self.assertNotIn(live["title"], body)
        fixture = testsupport.REPO / "tests/Fixtures/shared-calibration/community-likes.json"
        self.assertEqual(json.loads(fixture.read_text(encoding="utf-8")), likes)
        self.assertEqual({live["id"]: {"issue": 5, "comment": 2_400_000_001, "node": "IC_new1"},
                          recovered["id"]: {"issue": 5, "comment": 2_400_000_777, "node": "IC_old"}}, ledger["likes"])
        self.assertTrue(publisher.valid_like_links(ledger["likes"]))
        self.assertEqual(1, len(commits))

        # The next run posts nothing and, with the same counts, commits nothing; a new 👍 is recorded.
        posted, commits, _ = self.run_likes(entries, ledger, comments, {"IC_new1": 4, "IC_old": 12}, likes_file=likes)
        self.assertEqual(([], []), (posted, commits))
        posted, commits, (likes, _) = self.run_likes(entries, ledger, comments, {"IC_new1": 5, "IC_old": 12}, likes_file=likes)
        self.assertEqual(([], 1), (posted, len(commits)))
        self.assertEqual(5, likes["likes"][live["id"]]["count"])

    def test_deleted_comments_are_forgotten_and_withdrawn_or_removed_entries_leave_the_counts(self):
        kept, deleted, withdrawn = self.entry(1, issue=5), self.entry(2, issue=5), self.entry(3, issue=6, status="withdrawn")
        entries = {item["id"]: item for item in (kept, deleted, withdrawn)}
        removed = "00000000-0000-4000-8000-000000000009"
        ledger = {"schema_version": 1, "issues": {}, "likes": {
            kept["id"]: {"issue": 5, "comment": 11, "node": "IC_kept"},
            deleted["id"]: {"issue": 5, "comment": 12, "node": "IC_deleted"},
            withdrawn["id"]: {"issue": 6, "comment": 13, "node": "IC_withdrawn"},
            removed: {"issue": 7, "comment": 14, "node": "IC_removed"}}}
        posted, _, (likes, written) = self.run_likes(entries, ledger, {}, {"IC_kept": 2, "IC_withdrawn": 8})
        self.assertEqual([], posted)
        self.assertEqual({kept["id"]: {"count": 2, "issue": 5, "comment": 11}}, likes["likes"])
        self.assertEqual({kept["id"], withdrawn["id"]}, set(written["likes"]))
        # The deleted one is no longer known, so the next run gives the entry a new comment.
        posted, _, _ = self.run_likes(entries, written, {}, {"IC_kept": 2, "IC_withdrawn": 8, "IC_new1": 0},
                                      likes_file=likes)
        self.assertEqual([5], [number for number, _ in posted])

    def test_an_issue_that_cannot_be_reached_holds_no_other_entry_back(self):
        # A deleted or hidden Issue answers 404: its entries are reported, the others still get
        # their comment and the counts are committed first.
        lost, live, also_lost = self.entry(1, issue=6), self.entry(2, issue=5), self.entry(3, issue=6)
        entries = {item["id"]: item for item in (lost, live, also_lost)}
        gone = subprocess.CalledProcessError(1, ["gh", "api"])
        posted, commits, (likes, ledger) = self.run_likes(
            entries, {"schema_version": 1, "issues": {}}, {6: gone}, {"IC_new1": 2})
        self.assertEqual([5], [number for number, _ in posted])
        self.assertEqual({live["id"]: {"count": 2, "issue": 5, "comment": 2_400_000_001}}, likes["likes"])
        self.assertEqual([live["id"]], list(ledger["likes"]))
        self.assertEqual(1, len(commits))
        self.assertIsInstance(self.error, RuntimeError)
        self.assertIn(lost["id"], str(self.error))
        self.assertIn(also_lost["id"], str(self.error))

    def test_counts_are_read_a_hundred_at_a_time_and_any_other_gap_stops_the_run(self):
        asked = []

        def run(args, **kwargs):
            ids = json.loads(kwargs["input"])["variables"]["ids"]
            asked.append(len(ids))
            nodes = [{"id": node, "reactions": {"totalCount": 1}} for node in ids]
            return subprocess.CompletedProcess(args, 0, json.dumps({"data": {"nodes": nodes}}).encode())

        with patch.object(publisher.subprocess, "run", run):
            counts, gone = publisher.thumbs_up([f"IC_{index}" for index in range(150)])
        self.assertEqual(([100, 50], 150, set()), (asked, len(counts), gone))
        for reply in ({"data": {"nodes": [None]}, "errors": [{"type": "FORBIDDEN", "path": ["nodes", 0]}]},
                      {"data": {"nodes": [{"id": "IC_other", "reactions": {"totalCount": 1}}]}},
                      {"data": {"nodes": [{"id": "IC_kept", "reactions": {"totalCount": -1}}]}}):
            with patch.object(publisher.subprocess, "run",
                              lambda args, reply=reply, **_: subprocess.CompletedProcess(args, 1, json.dumps(reply).encode())), \
                    self.assertRaises(RuntimeError, msg=reply):
                publisher.thumbs_up(["IC_kept"])

    def test_load_refuses_a_ledger_whose_like_links_are_malformed(self):
        index = {"schema_version": 1, "generation": 0, "snapshot_commit": None, "snapshot_sha256": None}
        link = {"issue": 5, "comment": 11, "node": "IC_kept"}
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "index.json").write_bytes(publisher.encode(index))
            with patch.object(publisher, "INDEX", root / "index.json"), patch.object(publisher, "LEDGER", root / "ledger.json"), \
                    patch.object(publisher, "SNAPSHOT", root / "snapshot.json"):
                for likes, valid in (({"00000000-0000-4000-8000-000000000001": link}, True),
                                     ({"00000000-0000-4000-8000-000000000001": dict(link, comment=2 ** 53)}, False),
                                     ({"00000000-0000-4000-8000-000000000001": dict(link, node="a b")}, False)):
                    (root / "ledger.json").write_bytes(publisher.encode({"schema_version": 1, "issues": {}, "likes": likes}))
                    if valid:
                        self.assertEqual(likes, publisher.load()[1]["likes"])
                    else:
                        with self.assertRaises(publisher.Invalid):
                            publisher.load()

    def test_a_count_github_does_not_report_stops_the_run_and_the_ledger_shape_is_checked(self):
        entries = {"00000000-0000-4000-8000-000000000001": self.entry(1, issue=5)}
        ledger = {"schema_version": 1, "issues": {}, "likes": {
            "00000000-0000-4000-8000-000000000001": {"issue": 5, "comment": 11, "node": "IC_kept"}}}
        with patch.object(publisher.subprocess, "run",
                          lambda args, **_: subprocess.CompletedProcess(args, 1, b"")), \
                self.assertRaises(RuntimeError):
            publisher.thumbs_up(["IC_kept"])
        self.assertTrue(publisher.valid_like_links(ledger["likes"]))
        for broken in ({"x": {"issue": 5, "comment": 11, "node": "IC"}},
                       {"00000000-0000-4000-8000-000000000001": {"issue": 0, "comment": 11, "node": "IC"}},
                       {"00000000-0000-4000-8000-000000000001": {"issue": 5, "comment": 11, "node": "<b>"}},
                       {"00000000-0000-4000-8000-000000000001": {"issue": 5, "comment": 11}}, []):
            self.assertFalse(publisher.valid_like_links(broken), broken)


if __name__ == "__main__":
    unittest.main()
