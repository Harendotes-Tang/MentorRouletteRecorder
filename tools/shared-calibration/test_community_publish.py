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
