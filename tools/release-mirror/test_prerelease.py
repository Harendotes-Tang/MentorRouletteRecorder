#!/usr/bin/env python3
"""Prerelease artifact, identity and catch-up regressions; never publish remotely."""

import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import sync_release as m
from selftest import COMMIT, SOURCE, FakeMirror

TAG = "v1.7.3-beta.3"
EXE = "MentorRecorder-1.7.3-beta.3-setup.exe"
ZIP = "MentorRecorder-1.7.3-beta.3-win-x64.zip"
NAMES = (EXE, EXE + ".sha256", ZIP, ZIP + ".sha256")
BETA = {**SOURCE, "tag_name": TAG, "prerelease": True, "published_at": "2026-10-18T08:36:45Z"}


class BetaMirror(FakeMirror):
    def release(self):
        if not self.existing:
            return None
        return {**BETA, "release_status": "pre", "body": self.notes,
                "assets": [{"name": name} for name in self.contents]}


class PrereleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.paths, self.hashes = {}, {}
        for name in NAMES:
            data = name.encode()
            if name.endswith(".sha256"):
                data = (self.hashes[name[:-7]][1] + "  " + name[:-7] + "\n").encode()
            self.paths[name] = Path(self.temp.name) / name
            self.paths[name].write_bytes(data)
            self.hashes[name] = (len(data), hashlib.sha256(data).hexdigest())

    def test_four_originals_without_fabricated_metadata(self):
        self.assertEqual(m.artifact_names(TAG), NAMES)
        m.validate_source(TAG, COMMIT, self.paths, self.hashes)
        mirror = BetaMirror()
        m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
        self.assertEqual([e[1] for e in mirror.events if e[0] == "upload"], list(NAMES))

    def test_each_binary_checksum_is_required(self):
        for binary in (EXE, ZIP):
            with self.subTest(binary=binary):
                path = self.paths[binary + ".sha256"]
                original = path.read_bytes()
                path.write_text("0" * 64 + "  " + binary)
                with self.assertRaises(m.MirrorError):
                    m.validate_source(TAG, COMMIT, self.paths, self.hashes)
                path.write_bytes(original)

    def test_partial_resume_and_completed_conflict(self):
        mirror = BetaMirror({EXE: self.hashes[EXE]})
        m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
        self.assertNotIn(("upload", EXE), mirror.events)
        mirror = BetaMirror(self.hashes)
        m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
        self.assertFalse(any(e[0] == "upload" for e in mirror.events))
        mirror.contents[ZIP] = (1, "bad")
        mirror.events.clear()
        with self.assertRaises(m.MirrorError):
            m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
        self.assertFalse(any(e[0] in {"create", "upload"} for e in mirror.events))

    def test_safe_semver_and_source_prerelease_must_agree(self):
        for tag in ("v1.7.3-beta.3", "v1.7.3-rc.1", "v1.7.3-alpha"):
            m.artifact_names(tag)
        for tag in ("v1.7.3-beta.03", "v1.7.3-", "v01.7.3-beta.3", "v1.7.3-beta/3",
                    "v1.7.3-beta.3?token=x", "v1.7.3-beta.3\n"):
            with self.subTest(tag=tag), self.assertRaises(m.MirrorError):
                m.artifact_names(tag)
        for source in ({**BETA, "prerelease": False}, {**BETA, "draft": True},
                       {**BETA, "tag_name": "v1.7.3"}, {**BETA, "prerelease": 1}):
            class Api:
                def api(self, url, **kwargs):
                    return source
            with self.subTest(source=source), self.assertRaises(m.MirrorError):
                m.synchronize(Api(), source["tag_name"], Path(self.temp.name))

    def test_missing_digest_prevents_any_download_or_mutation(self):
        source = {**BETA, "assets": [{"name": name, "size": self.hashes[name][0]}
                                      for name in NAMES]}
        class Api:
            def api(self, url, **kwargs):
                if url == m.GH + "/releases/tags/" + TAG:
                    return source
                raise AssertionError("Missing digest must fail before another request")
        with self.assertRaisesRegex(m.MirrorError, "digest"):
            m.synchronize(Api(), TAG, Path(self.temp.name))

    def test_create_marks_pre_and_never_queries_or_changes_formal_latest(self):
        posts = []
        class Api:
            def api(self, url, **kwargs):
                if url.startswith(m.GC + "/tags?"):
                    return [{"name": TAG, "commit": {"sha": COMMIT}}]
                if url == m.GC + "/releases" and kwargs.get("method") == "POST":
                    posts.append(kwargs["payload"])
                    return {}
                raise AssertionError("Prerelease must not request formal latest")
        m.RemoteMirror(Api(), TAG, Path(self.temp.name)).create(BETA, COMMIT)
        self.assertEqual(posts[0]["release_status"], "pre")
        self.assertEqual(posts[0]["body"], BETA["body"])

    def test_final_readback_requires_pre_identity_and_all_assets(self):
        release = {**BETA, "release_status": "pre", "assets": [{"name": n} for n in NAMES]}
        class Api:
            def api(self, url, **kwargs):
                return release
        mirror = m.RemoteMirror(Api(), TAG, Path(self.temp.name))
        mirror.verify_release(BETA)
        for change in ({"prerelease": False}, {"release_status": "latest"}, {"assets": []},
                       {"tag_name": "v1.7.3-beta.2"}, {"body": "modified"}):
            release = {**BETA, "release_status": "pre", "assets": [{"name": n} for n in NAMES], **change}
            with self.subTest(change=change), self.assertRaises(m.MirrorError):
                mirror.verify_release(BETA)

    def test_successful_source_and_mirror_are_verified_end_to_end(self):
        source = {**BETA, "assets": [{"name": n, "size": self.hashes[n][0],
                                       "digest": "sha256:" + self.hashes[n][1]} for n in NAMES]}
        files = {n: self.paths[n].read_bytes() for n in NAMES}
        class Api:
            def api(self, url, **kwargs):
                if url == m.GH + "/releases/tags/" + TAG:
                    return source
                if url == m.GH + "/git/ref/tags/" + TAG:
                    return {"object": {"type": "commit", "sha": COMMIT}}
                if url.startswith(m.GC + "/tags?"):
                    return [{"name": TAG, "commit": {"sha": COMMIT}}]
                raise AssertionError("Unexpected request")
            def download(inner, url, path, limit, hosts):
                name = url.rsplit("/", 1)[1]
                path.write_bytes(files[name])
                return self.hashes[name]
        with patch.object(m, "RemoteMirror") as remote:
            remote.return_value = BetaMirror()
            remote.return_value.verify_release = unittest.mock.Mock()
            self.assertEqual(m.synchronize(Api(), TAG, Path(self.temp.name)), TAG)
            remote.return_value.verify_release.assert_called_once_with(source)


class CatchupTests(unittest.TestCase):
    def test_latest_published_beta_selected_not_list_order_and_bounded(self):
        calls = []
        class Api:
            def api(self, url, **kwargs):
                calls.append(url)
                if url == m.GH + "/releases/latest":
                    return {**SOURCE, "tag_name": "v1.7.2"}
                return [{**BETA, "tag_name": "v1.7.3-beta.2", "published_at": "2026-10-17T00:00:00Z"},
                        {**BETA, "draft": True, "published_at": "2026-10-19T00:00:00Z"}, BETA]
        with patch.object(m, "synchronize", return_value="ok") as sync:
            self.assertEqual(m.catch_up(Api(), Path("unused")), ["v1.7.2", TAG])
            self.assertEqual([c.args[1] for c in sync.call_args_list], ["v1.7.2", TAG])
        self.assertEqual(calls, [m.GH + "/releases/latest", m.GH + "/releases?per_page=20&page=1"])

    def test_one_failed_candidate_does_not_starve_the_other(self):
        class Api:
            def api(self, url, **kwargs):
                return {**SOURCE, "tag_name": "v1.7.2"} if url.endswith("/latest") else [BETA]
        for fail, failure in (("v1.7.2", m.MirrorError), (TAG, m.MirrorError),
                              ("v1.7.2", ValueError), (TAG, OSError)):
            called = []
            def synchronize(http, tag, directory):
                called.append(tag)
                if tag == fail:
                    raise failure("incomplete source")
                return tag
            with patch.object(m, "synchronize", side_effect=synchronize):
                with self.assertRaises(m.MirrorError):
                    m.catch_up(Api(), Path("unused"))
            self.assertEqual(called, ["v1.7.2", TAG])

    def test_discovery_failure_is_isolated_and_raw_errors_are_redacted(self):
        for broken in ("formal", "prerelease"):
            class Api:
                def api(self, url, **kwargs):
                    channel = "formal" if url.endswith("/latest") else "prerelease"
                    if channel == broken:
                        raise ValueError("SECRET-SIGNED-URL")
                    return {**SOURCE, "tag_name": "v1.7.2"} if channel == "formal" else [BETA]
            with patch.object(m, "synchronize", return_value="ok") as sync:
                with self.assertRaises(m.MirrorError) as failure:
                    m.catch_up(Api(), Path("unused"))
                self.assertNotIn("SECRET", str(failure.exception))
                self.assertEqual([c.args[1] for c in sync.call_args_list], [TAG if broken == "formal" else "v1.7.2"])

    def test_first_candidate_cannot_consume_the_second_candidates_budget(self):
        clock = [100.0]
        budgets = []
        class Api:
            deadline = 220.0
            def api(self, url, **kwargs):
                return {**SOURCE, "tag_name": "v1.7.2"} if url.endswith("/latest") else [BETA]
        def synchronize(http, tag, directory):
            budgets.append(http.deadline - clock[0])
            clock[0] = http.deadline
            if tag == "v1.7.2":
                raise m.MirrorError("time budget exhausted")
            return tag
        api = Api()
        with patch.object(m.time, "monotonic", side_effect=lambda: clock[0]), \
                patch.object(m, "synchronize", side_effect=synchronize):
            with self.assertRaises(m.MirrorError):
                m.catch_up(api, Path("unused"))
        self.assertEqual(budgets, [60.0, 60.0])
        self.assertEqual(api.deadline, 220.0)

    def test_workflow_runs_tests_and_handles_edited_prereleases(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/mirror-release.yml").read_text(encoding="utf-8")
        self.assertIn("types: [published, edited]", workflow)
        self.assertNotIn("!github.event.release.prerelease", workflow)
        self.assertIn("python tools/release-mirror/test_prerelease.py", workflow)
        self.assertIn("ref: main", workflow)
        self.assertIn("cancel-in-progress: false", workflow)


if __name__ == "__main__":
    unittest.main(verbosity=2)
