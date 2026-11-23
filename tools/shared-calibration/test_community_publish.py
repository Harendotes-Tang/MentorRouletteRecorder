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
        receipt = {"user": {"login": "github-actions[bot]"},
                   "body": "<!-- mr-community-receipt:15:" + publisher.fingerprint(live) + " -->\nPublished"}
        edits, posted = reply(similar, [receipt])
        self.assertEqual(([], []), (edits, posted))
        live["labels"].append({"name": "community-similar"})
        edits, _ = reply({"status": "published", "ids": ["a"]}, [receipt])
        self.assertEqual([("--remove-label", "community-similar")], edits)


if __name__ == "__main__":
    unittest.main()
