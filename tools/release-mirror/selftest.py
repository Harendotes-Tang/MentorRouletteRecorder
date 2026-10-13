#!/usr/bin/env python3
"""Offline transaction and network-boundary tests; no remote deployment claims."""

import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest.mock import patch

import sync_release as m

TAG = "v1.2.3"
COMMIT = "a" * 40
NAMES = m.artifact_names(TAG)
SOURCE = {"tag_name": TAG, "name": "Original title", "body": "Original\nrelease notes",
          "prerelease": False, "draft": False, "assets": []}


class FakeMirror:
    def __init__(self, contents=None, fail=None):
        self.contents = dict(contents or {})
        self.fail = fail
        self.events = []
        self.existing = bool(contents)
        self.notes = SOURCE["body"]

    def release(self):
        if not self.existing:
            return None
        return {**SOURCE, "body": self.notes, "assets": [{"name": name} for name in self.contents]}

    def create(self, source, commit):
        self.events.append(("create", commit))

    def verify(self, name, expected):
        self.events.append(("verify", name))
        if self.fail == ("verify", name) or self.contents.get(name) != expected:
            raise m.MirrorError("verification failed")

    def upload(self, name, path):
        self.events.append(("upload", name))
        if self.fail == ("upload", name):
            raise m.MirrorError("upload failed")
        data = path.read_bytes()
        self.contents[name] = (len(data), hashlib.sha256(data).hexdigest())


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.paths, self.hashes = {}, {}
        for name in NAMES:
            data = name.encode()
            path = Path(self.temp.name) / name
            path.write_bytes(data)
            self.paths[name] = path
            self.hashes[name] = (len(data), hashlib.sha256(data).hexdigest())

    def run_publish(self, mirror):
        m.publish(mirror, copy.deepcopy(SOURCE), COMMIT, self.paths, self.hashes)

    def test_metadata_last_after_both_anonymous_verifications(self):
        mirror = FakeMirror()
        self.run_publish(mirror)
        self.assertEqual(mirror.events, [("create", COMMIT), ("upload", NAMES[0]),
            ("verify", NAMES[0]), ("upload", NAMES[1]), ("verify", NAMES[1]),
            ("upload", NAMES[2]), ("verify", NAMES[2])])

    def test_every_prerequisite_failure_prevents_metadata_upload(self):
        for name in NAMES[:2]:
            for stage in ("upload", "verify"):
                with self.subTest(stage=stage, name=name):
                    mirror = FakeMirror(fail=(stage, name))
                    with self.assertRaises(m.MirrorError):
                        self.run_publish(mirror)
                    self.assertNotIn(("upload", NAMES[2]), mirror.events)

    def test_completed_identical_is_read_only(self):
        mirror = FakeMirror(self.hashes)
        self.run_publish(mirror)
        self.assertEqual(mirror.events, [("verify", name) for name in NAMES])

    def test_resume_after_interrupted_upload_reuses_verified_installer(self):
        mirror = FakeMirror({NAMES[0]: self.hashes[NAMES[0]]})
        self.run_publish(mirror)
        self.assertNotIn(("upload", NAMES[0]), mirror.events)
        self.assertEqual(mirror.events[-2:], [("upload", NAMES[2]), ("verify", NAMES[2])])

    def test_completed_or_partial_conflicting_content_never_mutates(self):
        for contents in (dict(self.hashes), {NAMES[0]: (1, "bad")}):
            contents[NAMES[0]] = (1, "bad")
            mirror = FakeMirror(contents)
            with self.assertRaises(m.MirrorError):
                self.run_publish(mirror)
            self.assertFalse(any(event[0] in {"upload", "create"} for event in mirror.events))

    def test_completion_marker_without_required_assets_is_refused(self):
        mirror = FakeMirror({NAMES[2]: self.hashes[NAMES[2]]})
        with self.assertRaises(m.MirrorError):
            self.run_publish(mirror)
        self.assertEqual(mirror.events, [])

    def test_existing_notes_conflict_is_not_overwritten(self):
        mirror = FakeMirror(self.hashes)
        mirror.notes = "different"
        with self.assertRaises(m.MirrorError):
            self.run_publish(mirror)
        self.assertEqual(mirror.events, [])

    def test_existing_notes_accept_only_line_ending_normalization(self):
        for ending in ("\r\n", "\r"):
            with self.subTest(ending=repr(ending)):
                mirror = FakeMirror(self.hashes)
                mirror.notes = SOURCE["body"].replace("\n", ending)
                self.run_publish(mirror)
                self.assertEqual(mirror.events, [("verify", name) for name in NAMES])

    def test_notes_spaces_content_and_final_newline_differences_still_conflict(self):
        for notes in (SOURCE["body"] + "\n", " " + SOURCE["body"], SOURCE["body"] + " ",
                      SOURCE["body"].replace("release", "changed"), None):
            with self.subTest(notes=notes):
                mirror = FakeMirror(self.hashes)
                mirror.notes = notes
                with self.assertRaises(m.MirrorError):
                    self.run_publish(mirror)
                self.assertEqual(mirror.events, [])

    def test_source_hash_and_commit_are_required(self):
        exe, checksum, metadata = NAMES
        self.paths[checksum].write_text(self.hashes[exe][1] + "  " + exe, encoding="utf8")
        doc = {"product": "MentorRecorder", "version": TAG[1:], "prerelease": False,
               "source_commit": COMMIT, "source_worktree_dirty": False, "public_distribution_ready": True}
        self.paths[metadata].write_text(json.dumps(doc), encoding="utf8")
        m.validate_source(TAG, COMMIT, self.paths, self.hashes)
        for key, value in (("version", "1.2.2"), ("source_commit", "b" * 40),
                           ("prerelease", True), ("source_worktree_dirty", True),
                           ("public_distribution_ready", False)):
            invalid = {**doc, key: value}
            self.paths[metadata].write_text(json.dumps(invalid), encoding="utf8")
            with self.assertRaises(m.MirrorError):
                m.validate_source(TAG, COMMIT, self.paths, self.hashes)
        self.paths[metadata].write_text(json.dumps(doc), encoding="utf8")
        self.paths[checksum].write_text("0" * 64 + "  " + exe, encoding="utf8")
        with self.assertRaises(m.MirrorError):
            m.validate_source(TAG, COMMIT, self.paths, self.hashes)


class NetworkTests(unittest.TestCase):
    def test_create_preserves_original_notes_and_marks_only_current_latest(self):
        for current, expected in ((TAG, "latest"), ("v1.2.4", "none")):
            with self.subTest(current=current):
                posts = []
                class Api:
                    def api(self, url, **kwargs):
                        if url == m.GH + "/releases/latest":
                            return {"tag_name": current, "draft": False, "prerelease": False}
                        if url.startswith(m.GC + "/tags?"):
                            return [{"name": TAG, "commit": {"sha": COMMIT}}]
                        if url == m.GC + "/releases" and kwargs.get("method") == "POST":
                            posts.append(kwargs["payload"])
                            return {}
                        raise AssertionError("Unexpected request")
                source = {**SOURCE, "body": "Original\r\nnotes\r\n"}
                m.RemoteMirror(Api(), TAG, Path("unused")).create(source, COMMIT)
                self.assertEqual(len(posts), 1)
                self.assertEqual(posts[0]["release_status"], expected)
                self.assertEqual(posts[0]["body"], source["body"])

    def test_unknown_or_invalid_github_latest_prevents_create(self):
        for latest in ({}, {"tag_name": "v1.2.4-beta", "draft": False, "prerelease": False},
                       {"tag_name": "v1.2.4", "draft": True, "prerelease": False}):
            calls = []
            class Api:
                def api(self, url, **kwargs):
                    calls.append(url)
                    return latest
            with self.subTest(latest=latest):
                with self.assertRaises(m.MirrorError):
                    m.RemoteMirror(Api(), TAG, Path("unused")).create(SOURCE, COMMIT)
                self.assertEqual(calls, [m.GH + "/releases/latest"])

    def test_incomplete_source_stops_before_any_other_request(self):
        calls = []
        class SourceOnly:
            def api(self, url, **kwargs):
                calls.append(url)
                return copy.deepcopy(SOURCE)
        with self.assertRaises(m.MirrorError):
            m.synchronize(SourceOnly(), TAG, Path("unused"))
        self.assertEqual(calls, [m.GH + "/releases/tags/" + TAG])

    def test_prerelease_never_reaches_mirror(self):
        class SourceOnly:
            def api(self, url, **kwargs):
                return {**SOURCE, "prerelease": True}
        with self.assertRaises(m.MirrorError):
            m.synchronize(SourceOnly(), TAG, Path("unused"))

    def test_mirror_tag_mismatch_or_missing_refuses_create(self):
        class Tags:
            def __init__(self, tags):
                self.tags = tags
            def api(self, url):
                return self.tags
        for tags in ([], [{"name": TAG, "commit": {"sha": "b" * 40}}]):
            with self.assertRaises(m.MirrorError):
                m.verify_mirror_tag(Tags(tags), TAG, COMMIT)
        m.verify_mirror_tag(Tags([{"name": TAG, "commit": {"sha": COMMIT}}]), TAG, COMMIT)

    def test_duplicate_asset_refused(self):
        with self.assertRaises(m.MirrorError):
            m.asset_map({"assets": [{"name": NAMES[0]}, {"name": NAMES[0]}]})

    def test_nonformal_and_path_injection_tags_refused(self):
        for tag in ("v1.2.3-beta", "v01.2.3", "../foo", "v1.2.3?x=y", "v1.2.3\n"):
            with self.assertRaises(m.MirrorError):
                m.artifact_names(tag)

    def test_upload_destination_policy(self):
        self.assertTrue(m.upload_url_allowed("https://bucket.obs.cn-north-4.myhuaweicloud.com/file?sig=x"))
        self.assertTrue(m.upload_url_allowed(f"https://{m.UPLOAD_HOST}/file"))
        for url in ("https://evil.test/file", f"https://{m.CDN_HOST}.evil.test/file"):
            self.assertFalse(m.upload_url_allowed(url))
        for url in (f"http://{m.CDN_HOST}/file", f"https://user@{m.CDN_HOST}/file",
                    f"https://{m.CDN_HOST}:8443/file", f"https://{m.CDN_HOST}/file#f"):
            with self.assertRaises(m.MirrorError):
                m.upload_url_allowed(url)

    def test_authenticated_redirect_never_forwards_token(self):
        requests = []
        class Redirect:
            def open(self, request, timeout):
                requests.append(request)
                raise urllib.error.HTTPError(request.full_url, 302, "redirect",
                    {"Location": f"https://{m.CDN_HOST}/file"}, io.BytesIO())
        http = m.Http("SECRET")
        http.opener = Redirect()
        with self.assertRaises(m.MirrorError) as raised:
            http.open(m.GC + "/releases", auth=True, redirects={m.CDN_HOST})
        self.assertEqual(len(requests), 1)
        self.assertNotIn("SECRET", str(raised.exception))
        self.assertNotIn("SECRET", requests[0].full_url)
        self.assertEqual(requests[0].get_header("Private-token"), "SECRET")

    def test_anonymous_download_has_no_api_credentials(self):
        captured = []
        class Capture:
            def open(self, request, timeout):
                captured.append(request)
                return object()
        http = m.Http("GC-SECRET", "GH-SECRET")
        http.opener = Capture()
        http.open(m.download_url("gitcode.com", TAG, NAMES[0]))
        self.assertNotIn("Private-token", captured[0].headers)
        self.assertNotIn("Authorization", captured[0].headers)

    def test_oversize_and_truncated_streams_fail(self):
        http = m.Http("")
        for body, length, limit in ((b"123", "4", 4), (b"12345", None, 4)):
            response = io.BytesIO(body)
            response.headers = {} if length is None else {"Content-Length": length}
            with self.assertRaises(m.MirrorError):
                http.read(response, limit)

    def test_slow_trickle_download_checks_deadline_after_each_network_read(self):
        clock = [0.0]
        class SlowTrickle:
            headers = {"Content-Length": "100"}
            reads = 0
            def read(self, size):
                raise AssertionError("read(size) can hide a slow trickle until size fills")
            def read1(self, size):
                self.reads += 1
                clock[0] += 1.0
                return b"x"
        response = SlowTrickle()
        http = m.Http("")
        http.deadline = 3.0
        output = io.BytesIO()
        with patch.object(m.time, "monotonic", side_effect=lambda: clock[0]):
            with self.assertRaisesRegex(m.MirrorError, "time budget exhausted"):
                http.read(response, 100, output)
        self.assertEqual(response.reads, 3)
        self.assertEqual(output.getvalue(), b"xx")

    def test_upload_file_reader_enforces_deadline_between_sent_blocks(self):
        clock = [0.0]
        source = io.BytesIO(b"x" * 100)
        body = m.DeadlineReader(source, 3.0)
        with patch.object(m.time, "monotonic", side_effect=lambda: clock[0]):
            self.assertEqual(body.read(1), b"x")
            clock[0] = 2.0  # Simulated socket sends keep succeeding slowly.
            self.assertEqual(body.read(1), b"x")
            clock[0] = 3.0
            with self.assertRaisesRegex(m.MirrorError, "time budget exhausted"):
                body.read(1)
        self.assertEqual(source.tell(), 2)

    def test_upload_file_reader_rechecks_after_slow_local_read(self):
        clock = [0.0]
        class SlowFile:
            def read(self, size):
                clock[0] = 3.0
                return b"x"
        with patch.object(m.time, "monotonic", side_effect=lambda: clock[0]):
            with self.assertRaisesRegex(m.MirrorError, "time budget exhausted"):
                m.DeadlineReader(SlowFile(), 3.0).read(1)

    def test_missing_secret_fails_without_network(self):
        with patch.dict(m.os.environ, {}, clear=True), patch.object(m.sys, "argv", ["sync_release.py"]):
            with patch.object(m, "synchronize") as sync:
                self.assertEqual(m.main(), 1)
                sync.assert_not_called()


if __name__ == "__main__":
    unittest.main(verbosity=2)
