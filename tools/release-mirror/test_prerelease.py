#!/usr/bin/env python3
"""Prerelease artifact, identity and catch-up regressions; never publish remotely."""

import copy
import hashlib
import io
import json
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
        return {**BETA, "release_status": "pre", "name": self.title, "body": self.notes,
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

    def test_timeout_retry_reconciles_remote_and_is_bounded(self):
        for registered in ("missing", "matching", "conflicting", "other_conflicting", "notes_conflicting", "always_timeout"):
            with self.subTest(registered=registered):
                mirror = BetaMirror({EXE: self.hashes[EXE], EXE + ".sha256": self.hashes[EXE + ".sha256"]})
                original_upload = mirror.upload
                attempts = []
                def upload(name, path):
                    attempts.append(name)
                    if name == ZIP and (attempts.count(ZIP) == 1 or registered == "always_timeout"):
                        if registered in ("matching", "conflicting"):
                            mirror.contents[ZIP] = self.hashes[ZIP] if registered == "matching" else (1, "bad")
                        if registered == "other_conflicting":
                            mirror.contents[EXE] = (1, "bad")
                        if registered == "notes_conflicting":
                            mirror.notes = "changed"
                        raise m.UploadInterrupted("timeout")
                    original_upload(name, path)
                mirror.upload = upload
                if registered in ("missing", "matching"):
                    m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
                    self.assertEqual(mirror.contents, self.hashes)
                else:
                    with self.assertRaises(m.MirrorError):
                        m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
                    self.assertNotIn(ZIP + ".sha256", attempts)
                self.assertEqual(attempts.count(ZIP), 2 if registered in ("missing", "always_timeout") else 1)

    def test_validation_errors_do_not_retry_upload(self):
        mirror = BetaMirror({EXE: self.hashes[EXE]})
        with patch.object(mirror, "upload", side_effect=m.MirrorError("invalid headers")) as upload:
            with self.assertRaises(m.MirrorError):
                m.publish(mirror, BETA, COMMIT, self.paths, self.hashes)
            self.assertEqual(upload.call_count, 1)

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

    def test_edited_release_notes_reach_the_existing_mirror(self):
        """The `release: edited` run after a complete beta mirror: copy the text, upload nothing."""
        source = {**BETA, "name": "Edited title", "body": "Edited\r\nrelease notes",
                  "assets": [{"name": n, "size": self.hashes[n][0],
                              "digest": "sha256:" + self.hashes[n][1]} for n in NAMES]}
        mirror = {**BETA, "release_status": "pre", "assets": [{"name": n, "type": "attach"} for n in NAMES]}
        files = {n: self.paths[n].read_bytes() for n in NAMES}
        writes = []
        class Api:
            def api(inner, url, *, auth=False, method="GET", payload=None):
                if method != "GET":
                    writes.append((method, url, auth, payload))
                if url == m.GH + "/releases/tags/" + TAG:
                    return copy.deepcopy(source)
                if url == m.GH + "/git/ref/tags/" + TAG:
                    return {"object": {"type": "commit", "sha": COMMIT}}
                if url.startswith(m.GC + "/tags?"):
                    return [{"name": TAG, "commit": {"sha": COMMIT}}]
                if url == m.GC + "/releases/tags/" + TAG and method == "GET":
                    return copy.deepcopy(mirror)
                if url == m.GC + "/releases/" + TAG and method == "PATCH":
                    # GitCode stores notes with normalized newlines.
                    mirror.update(payload, body=payload["body"].replace("\r\n", "\n"))
                    return copy.deepcopy(mirror)
                raise AssertionError("Unexpected request")
            def download(inner, url, path, limit, hosts):
                name = url.rsplit("/", 1)[1]
                path.write_bytes(files[name])
                return self.hashes[name]
        self.assertEqual(m.synchronize(Api(), TAG, Path(self.temp.name)), TAG)
        self.assertEqual(writes, [("PATCH", m.GC + "/releases/" + TAG, True,
                                   {"name": "Edited title", "body": "Edited\r\nrelease notes"})])
        self.assertEqual((mirror["name"], mirror["body"], mirror["release_status"]),
                         ("Edited title", "Edited\nrelease notes", "pre"))


class CatchupTests(unittest.TestCase):
    def test_slow_discovery_does_not_exhaust_the_other_channels_budget(self):
        for broken in ("formal", "prerelease"):
            with self.subTest(broken=broken):
                clock = [100.0]
                opened = []
                completed = []

                class Response(io.BytesIO):
                    headers = {}

                    def __init__(self, body, slow):
                        super().__init__(json.dumps(body).encode("utf-8"))
                        self.slow = slow

                    def read1(self, size):
                        if self.slow:
                            # Keep each read below the socket timeout while the
                            # complete response exceeds the discovery budget.
                            clock[0] += 20.0
                            return self.read(1)
                        return self.read(size)

                class Opener:
                    def open(self, request, timeout):
                        if request.full_url.startswith(m.GC + "/"):
                            opened.append("withdrawal")
                            return Response([], False)
                        channel = "formal" if request.full_url.endswith("/latest") else "prerelease"
                        opened.append(channel)
                        body = {**SOURCE, "tag_name": "v1.7.2"} if channel == "formal" else [BETA]
                        return Response(body, channel == broken)

                def synchronize(http, tag, directory):
                    m.require(http.deadline > clock[0], "Release mirror time budget exhausted")
                    completed.append(tag)
                    return tag

                with patch.object(m.time, "monotonic", side_effect=lambda: clock[0]), \
                        patch.object(m, "synchronize", side_effect=synchronize):
                    http = m.Http("unused")
                    http.deadline = 220.0
                    http.opener = Opener()
                    with self.assertRaises(m.MirrorError) as failure:
                        m.catch_up(http, Path("unused"))

                self.assertEqual(opened, ["formal", "prerelease", "withdrawal"])
                self.assertEqual(completed, [TAG if broken == "formal" else "v1.7.2"])
                self.assertIn(broken + ":", str(failure.exception))
                self.assertNotIn(("prerelease" if broken == "formal" else "formal") + ":", str(failure.exception))
                self.assertEqual(http.deadline, 220.0)

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
        self.assertEqual(calls, [m.GH + "/releases/latest", m.GH + "/releases?per_page=20&page=1",
                                 m.GC + "/releases?page=1&per_page=20"])

    def test_formal_mirror_deleted_on_github_is_reported_and_never_deleted(self):
        requests = []
        class Api:
            def api(self, url, *, auth=False, method="GET", payload=None):
                requests.append((method, url))
                if url == m.GH + "/releases/latest":
                    return {**SOURCE, "tag_name": "v1.7.2"}
                if url == m.GH + "/releases?per_page=20&page=1":
                    return [BETA]
                if url == m.GC + "/releases?page=1&per_page=20":
                    # The same listing the Collector's GitCode fallback reads.
                    return [{"tag_name": "v1.7.2", "prerelease": False, "draft": False},
                            {"tag_name": "v1.7.1", "prerelease": False},
                            {"tag_name": "v1.7.0"},  # no flags: the Collector treats it as formal
                            {"tag_name": "v1.6.0", "prerelease": False, "draft": True},
                            {"tag_name": "v1.7.3-beta.1", "prerelease": True},
                            {"tag_name": "v1.5.0/../x", "prerelease": False},
                            {"tag_name": None}]
                if url == m.GH + "/releases/tags/v1.7.2":
                    return {**SOURCE, "tag_name": "v1.7.2"}
                if url in (m.GH + "/releases/tags/v1.7.1", m.GH + "/releases/tags/v1.7.0"):
                    raise m.Missing("Remote resource not found (HTTP 404)")
                raise AssertionError("Unexpected request")
        with patch.object(m, "synchronize", side_effect=lambda http, tag, directory: tag) as sync:
            with self.assertRaises(m.MirrorError) as failure:
                m.catch_up(Api(), Path("unused"))
        self.assertEqual([c.args[1] for c in sync.call_args_list], ["v1.7.2", TAG])
        message = str(failure.exception)
        self.assertIn("v1.7.1: deleted on GitHub but still published on GitCode", message)
        self.assertIn("v1.7.0: deleted on GitHub but still published on GitCode", message)
        for other in ("v1.7.2:", "v1.6.0", "beta.1", "v1.5.0"):
            self.assertNotIn(other, message)
        self.assertEqual({method for method, _ in requests}, {"GET"})
        self.assertEqual([url for _, url in requests if url.startswith(m.GH + "/releases/tags/")],
                         [m.GH + "/releases/tags/" + tag for tag in ("v1.7.2", "v1.7.1", "v1.7.0")])

    def test_invalid_mirror_listing_fails_the_check_without_starving_synchronization(self):
        for listing in ({"tag_name": "v1.7.1"}, [{"tag_name": "v1.7.%d" % n} for n in range(21)], ["v1.7.1"]):
            class Api:
                def api(self, url, **kwargs):
                    if url == m.GC + "/releases?page=1&per_page=20":
                        return listing
                    return {**SOURCE, "tag_name": "v1.7.2"} if url.endswith("/latest") else [BETA]
            with self.subTest(listing=str(listing)[:40]), \
                    patch.object(m, "synchronize", side_effect=lambda http, tag, directory: tag) as sync:
                with self.assertRaisesRegex(m.MirrorError, "withdrawal: Invalid mirror release"):
                    m.catch_up(Api(), Path("unused"))
                self.assertEqual([c.args[1] for c in sync.call_args_list], ["v1.7.2", TAG])

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


if __name__ == "__main__":
    unittest.main(verbosity=2)
