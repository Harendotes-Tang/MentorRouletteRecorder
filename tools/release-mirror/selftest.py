#!/usr/bin/env python3
"""Offline transaction and network-boundary tests; no remote deployment claims."""

import copy
from contextlib import redirect_stdout
import hashlib
import io
import json
import subprocess
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
        self.title = SOURCE["name"]
        self.notes = SOURCE["body"]
        self.identity = {}

    def release(self):
        if not self.existing:
            return None
        return {**SOURCE, "name": self.title, "body": self.notes, **self.identity,
                "assets": [{"name": name} for name in self.contents]}

    def create(self, source, commit):
        self.events.append(("create", commit))

    def update(self, source, commit):
        self.events.append(("update", commit))
        if self.fail == ("update", None):
            raise m.MirrorError("update failed")
        self.title, self.notes = source["name"], source["body"]

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
            for notes in (SOURCE["body"], "Notes before a GitHub edit"):
                with self.subTest(contents=sorted(contents), notes=notes):
                    mirror = FakeMirror(contents)
                    mirror.notes = notes
                    with self.assertRaises(m.MirrorError):
                        self.run_publish(mirror)
                    self.assertFalse(any(event[0] in {"upload", "create", "update"} for event in mirror.events))

    def test_completion_marker_without_required_assets_is_refused(self):
        mirror = FakeMirror({NAMES[2]: self.hashes[NAMES[2]]})
        with self.assertRaises(m.MirrorError):
            self.run_publish(mirror)
        self.assertEqual(mirror.events, [])

    def test_edited_notes_after_cut_short_formal_mirror_update_text_then_finish(self):
        # The installer upload timed out after create, then the GitHub notes were edited.
        mirror = FakeMirror()
        mirror.existing = True
        mirror.notes = "Notes before a GitHub edit"
        self.run_publish(mirror)
        self.assertEqual(mirror.events, [("update", COMMIT), ("upload", NAMES[0]), ("verify", NAMES[0]),
            ("upload", NAMES[1]), ("verify", NAMES[1]), ("upload", NAMES[2]), ("verify", NAMES[2])])
        self.assertEqual((mirror.title, mirror.notes), (SOURCE["name"], SOURCE["body"]))
        self.assertEqual(mirror.contents, self.hashes)

    def test_edited_title_with_partial_mirror_verifies_registered_attachments_first(self):
        mirror = FakeMirror({NAMES[0]: self.hashes[NAMES[0]]})
        mirror.title = "Title before a GitHub edit"
        self.run_publish(mirror)
        self.assertEqual(mirror.events[:2], [("verify", NAMES[0]), ("update", COMMIT)])
        self.assertNotIn(("upload", NAMES[0]), mirror.events)
        self.assertEqual(mirror.contents, self.hashes)

    def test_failed_text_update_adds_no_attachment(self):
        mirror = FakeMirror({NAMES[0]: self.hashes[NAMES[0]]}, fail=("update", None))
        mirror.notes = "Notes before a GitHub edit"
        with self.assertRaises(m.MirrorError):
            self.run_publish(mirror)
        self.assertEqual(mirror.events, [("verify", NAMES[0]), ("update", COMMIT)])

    def test_identity_or_completion_conflict_never_updates_edited_notes(self):
        for contents, identity in ((self.hashes, {"prerelease": True}), (self.hashes, {"tag_name": "v1.2.2"}),
                                   ({NAMES[2]: self.hashes[NAMES[2]]}, {})):
            with self.subTest(contents=sorted(contents), identity=identity):
                mirror = FakeMirror(contents)
                mirror.identity = identity
                mirror.notes = "Notes before a GitHub edit"
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

    def test_every_title_or_notes_difference_is_copied_after_attachments_verify(self):
        for title, notes in ((SOURCE["name"], SOURCE["body"] + "\n"), (SOURCE["name"], " " + SOURCE["body"]),
                             (SOURCE["name"], SOURCE["body"] + " "), (SOURCE["name"], None),
                             (SOURCE["name"], SOURCE["body"].replace("release", "changed")),
                             ("Edited title", SOURCE["body"])):
            with self.subTest(title=title, notes=notes):
                mirror = FakeMirror(self.hashes)
                mirror.title, mirror.notes = title, notes
                self.run_publish(mirror)
                self.assertEqual(mirror.events, [("verify", name) for name in NAMES] + [("update", COMMIT)])
                self.assertEqual((mirror.title, mirror.notes), (SOURCE["name"], SOURCE["body"]))

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

    def test_update_rechecks_mirror_tag_and_patches_only_title_and_notes(self):
        calls = []
        class Api:
            def api(self, url, **kwargs):
                calls.append((url, kwargs))
                if url.startswith(m.GC + "/tags?"):
                    return [{"name": TAG, "commit": {"sha": COMMIT}}]
                if url == m.GC + "/releases/" + TAG and kwargs.get("method") == "PATCH":
                    return {}
                raise AssertionError("Unexpected request")
        source = {**SOURCE, "name": "Edited title", "body": "Edited\r\nnotes\r\n"}
        m.RemoteMirror(Api(), TAG, Path("unused")).update(source, COMMIT)
        self.assertEqual([url for url, _ in calls], [m.GC + "/tags?per_page=100&page=1", m.GC + "/releases/" + TAG])
        self.assertEqual(calls[1][1], {"auth": True, "method": "PATCH",
                                       "payload": {"name": "Edited title", "body": source["body"]}})

    def test_update_never_patches_when_mirror_tag_moved(self):
        calls = []
        class Api:
            def api(self, url, **kwargs):
                calls.append(url)
                if url.startswith(m.GC + "/tags?"):
                    return [{"name": TAG, "commit": {"sha": "b" * 40}}]
                raise AssertionError("PATCH must not follow a moved mirror tag")
        with self.assertRaises(m.MirrorError):
            m.RemoteMirror(Api(), TAG, Path("unused")).update(SOURCE, COMMIT)
        self.assertEqual(calls, [m.GC + "/tags?per_page=100&page=1"])

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

    def test_formal_tag_with_prerelease_identity_never_reaches_mirror(self):
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

    def test_invalid_version_and_path_injection_tags_refused(self):
        for tag in ("v1.2.3-beta..1", "v01.2.3", "../foo", "v1.2.3?x=y", "v1.2.3\n"):
            with self.assertRaises(m.MirrorError):
                m.artifact_names(tag)

    def test_upload_destination_policy(self):
        self.assertTrue(m.upload_url_allowed("https://bucket.obs.cn-north-4.myhuaweicloud.com/file?sig=x"))
        self.assertTrue(m.upload_url_allowed(f"https://{m.UPLOAD_HOST}/file"))
        self.assertTrue(m.upload_url_allowed(f"https://{m.UPLOAD_FILE_HOST}/file?signature=secret"))
        for url in ("https://evil.test/file", f"https://{m.CDN_HOST}.evil.test/file"):
            self.assertFalse(m.upload_url_allowed(url))
        for url in (f"http://{m.CDN_HOST}/file", f"https://user@{m.CDN_HOST}/file",
                    f"https://{m.CDN_HOST}:8443/file", f"https://{m.CDN_HOST}/file#f"):
            with self.assertRaises(m.MirrorError):
                m.upload_url_allowed(url)

    def test_file_upload_host_rejects_spoofing_credentials_and_nondefault_port(self):
        self.assertFalse(m.upload_url_allowed(f"https://{m.UPLOAD_FILE_HOST}.evil.test/file"))
        for url in (f"http://{m.UPLOAD_FILE_HOST}/file", f"https://user:secret@{m.UPLOAD_FILE_HOST}/file",
                    f"https://{m.UPLOAD_FILE_HOST}:8443/file", f"https://{m.UPLOAD_FILE_HOST}/file#secret"):
            with self.subTest(url=url), self.assertRaises(m.MirrorError):
                m.upload_url_allowed(url)

    def test_rejected_upload_url_has_fixed_error_and_no_host_log(self):
        class Api:
            def api(self, url, **kwargs):
                return {"url": "https://secret-host.evil.test/private?signature=SIGNED-SECRET"}
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaises(m.MirrorError) as failure:
            m.RemoteMirror(Api(), TAG, Path("unused")).upload(NAMES[0], Path("unused"))
        self.assertEqual(str(failure.exception), "Refused upload storage host")
        self.assertEqual(output.getvalue(), "")

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

    def test_release_budget_covers_slow_large_artifact_transfers(self):
        with patch.object(m.time, "monotonic", return_value=123.0):
            self.assertEqual(m.Http("").deadline, 123.0 + 5 * 60 * 60)

    def test_missing_secret_fails_without_network(self):
        with patch.dict(m.os.environ, {}, clear=True), patch.object(m.sys, "argv", ["sync_release.py"]):
            with patch.object(m, "synchronize") as sync:
                self.assertEqual(m.main(), 1)
                sync.assert_not_called()


class CurlTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "asset"
        self.path.write_bytes(b"original")
        self.url = f"https://{m.UPLOAD_FILE_HOST}/private-path?signature=SIGNED-SECRET"
        self.headers = {key: "CALLBACK-SECRET" for key in m.UPLOAD_HEADERS}

    def invoke(self, result=None, error=None):
        output = io.StringIO()
        with redirect_stdout(output), patch.object(m.subprocess, "run", return_value=result, side_effect=error) as run:
            try:
                m.upload_with_curl(self.url, self.headers, self.path, m.time.monotonic() + 120)
            except m.MirrorError as failure:
                return run, output.getvalue(), str(failure)
        return run, output.getvalue(), None

    def test_signed_values_only_on_stdin_and_only_numeric_metrics_logged(self):
        result = subprocess.CompletedProcess([], 0, "200|8|1234.500|0.250")
        with patch.dict(m.os.environ, {"GITCODE_MIRROR_TOKEN": "PAT-SECRET", "GITHUB_TOKEN": "GH-SECRET",
                                      "SSLKEYLOGFILE": "forbidden", "CURL_HOME": "forbidden"}):
            run, output, failure = self.invoke(result)
        self.assertIsNone(failure)
        self.assertEqual(run.call_args.args[0], ["curl", "-q", "--config", "-"])
        options = run.call_args.kwargs
        self.assertIn(self.url, options["input"])
        self.assertIn("CALLBACK-SECRET", options["input"])
        for secret in ("SIGNED-SECRET", "CALLBACK-SECRET", "PAT-SECRET", "GH-SECRET", "private-path"):
            self.assertNotIn(secret, output)
        self.assertNotIn("PAT-SECRET", options["input"])
        for key in ("GITCODE_MIRROR_TOKEN", "GITHUB_TOKEN", "SSLKEYLOGFILE", "CURL_HOME"):
            self.assertNotIn(key, options["env"])
        self.assertEqual(options["stderr"], subprocess.DEVNULL)
        self.assertIn("Native upload metrics: HTTP 200; bytes=8/8; average_bytes_per_second=1234.500; seconds=0.250", output)

    def test_transfer_is_https_nonredirecting_and_deadline_bounded(self):
        result = subprocess.CompletedProcess([], 0, "201|8|8|1.000")
        # Spend five seconds from an exact, fixed clock so deadline subtraction
        # cannot intermittently round a live 120-second budget above 120.
        with patch.object(m.time, "monotonic", side_effect=[100.0, 105.0, 106.0]):
            run, _, failure = self.invoke(result)
        self.assertIsNone(failure)
        config = run.call_args.kwargs["input"]
        for expected in ('proto = "=https"', 'max-redirs = "0"', 'no-location\n', 'no-netrc\n',
                         'globoff\n', 'http1.1\n', 'speed-limit = "4096"', 'speed-time = "120"',
                         'connect-timeout = "60"', 'max-filesize = "2097152"'):
            self.assertIn(expected, config)
        self.assertLessEqual(run.call_args.kwargs["timeout"], 120)
        self.assertEqual(run.call_args.kwargs["timeout"], 115)
        native_limit = float(config.split('max-time = "', 1)[1].split('"', 1)[0])
        self.assertLess(native_limit, run.call_args.kwargs["timeout"])
        self.assertEqual(run.call_count, 1)

    def test_early_http_error_redirect_partial_success_and_transport_error_fail(self):
        for exit_code, metrics in ((22, "403|0|0|0.100"), (0, "307|0|0|0.100"),
                                   (0, "200|7|7|1.000"), (28, "000|4|1|60.000")):
            with self.subTest(exit=exit_code, metrics=metrics):
                run, output, failure = self.invoke(subprocess.CompletedProcess([], exit_code, metrics))
                self.assertIsNotNone(failure)
                self.assertIn("Native upload", failure)
                if exit_code == 28:
                    self.assertIn("timed out", failure)
                self.assertIn("Native upload metrics:", output)
                self.assertEqual(run.call_count, 1)

    def test_large_upload_keeps_a_fixed_stall_guard_when_retry_budget_shrinks(self):
        size = 145032587
        result = subprocess.CompletedProcess([], 0, f"200|{size}|2000000|75.000")
        from types import SimpleNamespace
        with patch.object(Path, "stat", return_value=SimpleNamespace(st_size=size)), \
                patch.object(m.time, "monotonic", side_effect=[100.0, 105.0, 106.0]):
            run, _, failure = self.invoke(result)
        self.assertIsNone(failure)
        config = run.call_args.kwargs["input"]
        limit = int(config.split('speed-limit = "', 1)[1].split('"', 1)[0])
        duration = float(config.split('max-time = "', 1)[1].split('"', 1)[0])
        self.assertEqual(limit, m.UPLOAD_MINIMUM_SPEED)
        self.assertEqual(duration, 113.0)

    def test_untrusted_diagnostics_and_exception_output_are_not_printed(self):
        for result, error in ((subprocess.CompletedProcess([], 1, self.url), None),
                              (None, subprocess.TimeoutExpired(["curl"], 1, output=self.url, stderr="CALLBACK-SECRET")),
                              (None, OSError(self.url))):
            with self.subTest(error=type(error).__name__):
                _, output, failure = self.invoke(result, error)
                self.assertIsNotNone(failure)
                self.assertNotIn(self.url, output + failure)
                self.assertNotIn("CALLBACK-SECRET", output + failure)

    def test_control_character_injection_cannot_create_config_options(self):
        for value in ('x\nlocation', 'x\rheader: secret', 'x\0y', 'x\ty'):
            with self.assertRaises(m.MirrorError):
                m.curl_quote(value)
        self.assertEqual(m.curl_quote('a"b\\c'), '"a\\"b\\\\c"')

    def test_expired_budget_never_launches_transport(self):
        with patch.object(m.subprocess, "run") as run:
            with self.assertRaisesRegex(m.MirrorError, "time budget exhausted"):
                m.upload_with_curl(self.url, self.headers, self.path, m.time.monotonic() - 1)
            run.assert_not_called()

    def test_remote_upload_uses_same_response_headers_without_api_token(self):
        class Api:
            deadline = 123.0
            def api(inner, url, **kwargs):
                return {"url": self.url, "headers": self.headers}
        with patch.object(m, "upload_with_curl") as upload:
            m.RemoteMirror(Api(), TAG, self.path.parent).upload(NAMES[0], self.path)
        upload.assert_called_once_with(self.url, self.headers, self.path, 123.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
