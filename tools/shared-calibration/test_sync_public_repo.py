#!/usr/bin/env python3
"""sync_public_repo.py: the public repository's file set, and that its tools run on their own."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import testsupport
import index as repo_index
import rebuild
import sharecode
import sync_public_repo

EXPECTED = {
    ".gitattributes", ".gitignore", ".github/ISSUE_TEMPLATE/config.yml", ".github/ISSUE_TEMPLATE/share-calibration.yml",
    ".github/ISSUE_TEMPLATE/report-calibration.yml", ".github/workflows/publish-calibration.yml",
    ".github/workflows/report-calibration.yml", "LICENSE.md", "LICENSES/GPL-3.0-or-later.txt", "README.md",
    "index.json", "submissions.json", "templates/cn.2026.08.05.json", "tools/index.py", "tools/issue.py",
    "tools/publish.py", "tools/publish_issue.sh", "tools/rebuild.py", "tools/report_issue.sh", "tools/sharecode.py",
    "tools/sweep_issues.sh",
}
TEXT_SUFFIXES = {".py", ".sh", ".yml", ".md", ".txt", ""}


class SyncTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mr-sync-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.out = self.root / "public"
        self.names = sync_public_repo.sync(self.out)

    def test_the_file_set_is_exactly_the_public_layout(self):
        self.assertEqual(EXPECTED, set(self.names))
        on_disk = {path.relative_to(self.out).as_posix() for path in self.out.rglob("*") if path.is_file()}
        self.assertEqual(EXPECTED, on_disk)

    def test_a_new_repository_starts_with_an_empty_index_the_client_reads(self):
        data = (self.out / "index.json").read_bytes()
        self.assertEqual(b'{"schema_version":1,"entries":[]}\n', data)
        read = repo_index.read_index(data)
        self.assertEqual((True, (), ()), (read.readable, read.entries, read.skipped))
        self.assertEqual(repo_index.empty_index(), repo_index.load(self.out))

    def test_templates_are_byte_for_byte_copies_of_the_shipped_ones(self):
        self.assertEqual(testsupport.TEMPLATE_PATH.read_bytes(), (self.out / "templates/cn.2026.08.05.json").read_bytes())
        self.assertEqual(testsupport.shipped_template().sha256, rebuild.load_templates(self.out / "templates")[0].sha256)

    def test_tools_are_the_runtime_files_only_with_lf_line_endings(self):
        for name in sync_public_repo.RUNTIME_FILES:
            with self.subTest(name):
                source = (testsupport.HERE / name).read_bytes().replace(b"\r\n", b"\n")
                self.assertEqual(source, (self.out / "tools" / name).read_bytes())
        for path in self.out.rglob("*"):
            if path.is_file() and path.suffix in TEXT_SUFFIXES and path.parent.name != "templates":
                with self.subTest(path.name):
                    self.assertNotIn(b"\r\n", path.read_bytes())

    def test_the_licence_text_is_the_projects(self):
        licence = (testsupport.REPO / "LICENSE").read_bytes().replace(b"\r\n", b"\n")
        self.assertEqual(licence, (self.out / "LICENSES/GPL-3.0-or-later.txt").read_bytes())
        text = (self.out / "LICENSE.md").read_text(encoding="utf-8")
        self.assertIn("GPL-3.0-or-later", text)
        # The data licence was decided on 2026-09-16: CC0-1.0, with submission through the issue
        # form counting as agreement. Both halves have to be stated, and the form has to say it
        # too -- a player agrees where they submit, not only where the licence file says so.
        self.assertIn("CC0-1.0", text)
        self.assertIn("提交即同意", text)
        form = (self.out / ".github/ISSUE_TEMPLATE/share-calibration.yml").read_text(encoding="utf-8")
        self.assertIn("CC0-1.0", form)

    def test_a_directory_that_is_not_empty_is_refused(self):
        with self.assertRaises(sync_public_repo.SyncError):
            sync_public_repo.sync(self.out)
        stray = self.root / "stray"
        stray.mkdir()
        (stray / "keep.txt").write_text("mine", encoding="utf-8")
        with self.assertRaises(sync_public_repo.SyncError):
            sync_public_repo.sync(stray)
        self.assertEqual("mine", (stray / "keep.txt").read_text(encoding="utf-8"))

    def test_the_synced_tools_run_on_their_own_outside_this_repository(self):
        payload = testsupport.payload("REPLY_STATE", 4)
        issue = {"number": 3, "state": "open", "title": "[共享校准] CN " + testsupport.BUILD,
                 "body": testsupport.issue_body(sharecode.encode(payload)), "labels": [{"name": "share-calibration"}],
                 "user": {"id": 77, "login": "someone", "type": "User"}}
        event, account, out = self.root / "event.json", self.root / "account.json", self.root / "result"
        event.write_text(json.dumps({"issue": issue}, ensure_ascii=False), encoding="utf-8")
        account.write_text(json.dumps({"id": 77, "type": "User", "created_at": "2019-05-05T00:00:00Z"}), encoding="utf-8")
        environment = dict(os.environ)
        environment.pop("PYTHONPATH", None)
        command = [sys.executable, "-B", str(self.out / "tools" / "publish.py"), "check", "--repo", str(self.out),
                   "--event", str(event), "--account", str(account), "--out", str(out), "--now", "2026-09-16T08:00:00Z"]
        completed = subprocess.run(command, cwd=self.root, env=environment, capture_output=True, text=True, timeout=120)
        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads((out / "result.json").read_text(encoding="utf-8"))
        self.assertEqual(("published", repo_index.code_path("CN", testsupport.BUILD, sharecode.code_sha256(payload))),
                         (result["status"], result["file"]))
        self.assertFalse(list(self.out.rglob("__pycache__")))

    def test_the_command_line_prints_the_tree_and_refuses_a_used_directory(self):
        import contextlib
        import io

        fresh = self.root / "fresh"
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(0, sync_public_repo.main(["--out", str(fresh)]))
        self.assertEqual(sorted(EXPECTED), buffer.getvalue().splitlines())
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, sync_public_repo.main(["--out", str(fresh)]))


if __name__ == "__main__":
    unittest.main()
