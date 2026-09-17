#!/usr/bin/env python3
"""tools/publish_issue.sh and tools/sweep_issues.sh, run for real against local git repositories.

A bare repository stands in for GitHub, a stub ``gh`` records every call, and a pre-push hook in the
runner's clone lets a "maintainer" push first, so the start-again-from-the-new-main loop runs the way
a lost race happens on GitHub. Skipped when bash or git is missing; on Windows, bash is taken from Git
for Windows, never from WSL.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import testsupport
import index as repo_index
import sharecode
import sync_public_repo

GH_STUB = """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$STUB_DIR/gh.log"
if [ "$1" = issue ] && [ "$2" = view ]; then
  if [ -e "$STUB_DIR/state-$3" ]; then cat "$STUB_DIR/state-$3"; else echo OPEN; fi
elif [ "$1" = issue ] && [ "$2" = comment ]; then
  cat "$5" >> "$STUB_DIR/comment-$3.md"
elif [ "$1" = issue ] && [ "$2" = close ]; then
  echo CLOSED > "$STUB_DIR/state-$3"
elif [ "$1" = api ] && [ "$2" = --paginate ]; then
  cat "$STUB_DIR/open.json"
elif [ "$1" = api ]; then
  name="$(printf '%s' "$2" | tr '/' '_')"
  if [ -e "$STUB_DIR/$name.json" ]; then cat "$STUB_DIR/$name.json"; else echo '{"message":"Not Found"}'; exit 1; fi
fi
"""

PRE_PUSH = """#!/usr/bin/env bash
cat > /dev/null
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_QUARANTINE_PATH
if [ -n "${RACE_CLONE:-}" ] && [ ! -e "$RACE_CLONE.raced" ]; then
  : > "$RACE_CLONE.raced"
  git -C "$RACE_CLONE" push --quiet origin HEAD:main
fi
exit 0
"""


def _find_bash() -> str | None:
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git is None:
        return None
    for parent in list(Path(git).resolve().parents)[:3]:
        candidate = parent / "bin" / "bash.exe"
        if candidate.is_file():
            return str(candidate)
    return None


BASH = _find_bash()
GIT = shutil.which("git")


def _write_script(path: Path, text: str) -> None:
    path.write_bytes(text.encode("utf-8"))
    path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


@unittest.skipIf(BASH is None or GIT is None, "bash and git are needed to run the workflow scripts")
class WorkflowScriptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mr-workflow-", ignore_cleanup_errors=True)
        self.addCleanup(self._remove_tree)
        self.root = Path(self.temp.name)
        self.stub = self.root / "stub"
        self.stub.mkdir()
        bin_directory = self.root / "bin"
        bin_directory.mkdir()
        _write_script(bin_directory / "gh", GH_STUB)
        self.runner = self.root / "runner"
        self.runner.mkdir()
        config = self.root / "gitconfig"
        config.write_text("[user]\n\tname = Test\n\temail = test@example.invalid\n[init]\n\tdefaultBranch = main\n", encoding="utf-8")
        self.env = dict(os.environ)
        self.env.pop("PYTHONPATH", None)
        self.env.update(
            PATH=os.pathsep.join([str(bin_directory), str(Path(sys.executable).parent), os.environ.get("PATH", "")]),
            GIT_CONFIG_GLOBAL=str(config), GIT_CONFIG_NOSYSTEM="1", GIT_TERMINAL_PROMPT="0",
            RUNNER_TEMP=self.runner.as_posix(), STUB_DIR=self.stub.as_posix(),
            GH_REPO="owner/calibrations", GH_TOKEN="not-a-token", PYTHONUTF8="1",
        )
        self.origin = self.root / "origin.git"
        self.git("init", "--quiet", "--bare", str(self.origin))
        seed = self.root / "seed"
        sync_public_repo.sync(seed)
        self.git("init", "--quiet", cwd=seed)
        self.git("add", "-A", cwd=seed)
        self.git("commit", "--quiet", "-m", "seed", cwd=seed)
        self.git("push", "--quiet", self.origin.as_posix(), "HEAD:refs/heads/main", cwd=seed)
        self.work = self.root / "work"
        self.git("clone", "--quiet", self.origin.as_posix(), str(self.work))

    def _remove_tree(self):
        def make_writable(function, path, _):
            os.chmod(path, stat.S_IWRITE)
            function(path)

        handler = {"onexc": make_writable} if sys.version_info >= (3, 12) else {"onerror": make_writable}
        shutil.rmtree(self.temp.name, **handler)
        self.temp.cleanup()

    def git(self, *args, cwd=None) -> subprocess.CompletedProcess:
        return subprocess.run([GIT, *args], cwd=cwd, env=self.env, check=True, capture_output=True, text=True, encoding="utf-8")

    def origin_show(self, spec: str) -> bytes:
        return subprocess.run([GIT, "--git-dir", str(self.origin), "show", spec], env=self.env, check=True, capture_output=True).stdout

    def origin_log(self) -> list:
        return [line.split(" ", 1) for line in self.git("--git-dir", str(self.origin), "log", "--format=%H %s", "main").stdout.splitlines()]

    def submission(self, number, user_id, login, code, created="2020-01-01T00:00:00Z", with_account=True) -> tuple:
        issue = {"number": number, "state": "open", "title": "[共享校准] CN " + testsupport.BUILD,
                 "body": testsupport.issue_body(code), "labels": [{"name": "share-calibration"}],
                 "user": {"id": user_id, "login": login, "type": "User"}}
        if with_account:
            account = {"id": user_id, "login": login, "type": "User", "created_at": created}
            (self.stub / ("users_%s.json" % login)).write_text(json.dumps(account), encoding="utf-8")
        (self.stub / ("repos_owner_calibrations_issues_%d.json" % number)).write_text(json.dumps(issue, ensure_ascii=False), encoding="utf-8")
        event = self.root / ("event-%d.json" % number)
        event.write_text(json.dumps({"action": "opened", "issue": issue}, ensure_ascii=False), encoding="utf-8")
        return issue, event

    def run_bash(self, script: str, *args, **env) -> subprocess.CompletedProcess:
        return subprocess.run([BASH, script, *args], cwd=self.work, env=dict(self.env, **env), capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=900)

    def gh_calls(self) -> list:
        log = self.stub / "gh.log"
        return log.read_text(encoding="utf-8").splitlines() if log.exists() else []

    def comment(self, number: int) -> str:
        return (self.stub / ("comment-%d.md" % number)).read_text(encoding="utf-8")

    def test_a_new_code_is_published_even_when_a_maintainer_pushes_first(self):
        other = self.root / "other"
        self.git("clone", "--quiet", self.origin.as_posix(), str(other))
        (other / "README.md").write_bytes((other / "README.md").read_bytes() + b"\nmaintainer edit\n")
        self.git("commit", "--quiet", "-am", "maintainer edit", cwd=other)
        _write_script(self.work / ".git" / "hooks" / "pre-push", PRE_PUSH)
        code = sharecode.encode(testsupport.payload("MARKER_OFFSET", 3))
        _, event = self.submission(7, 4242, "Octo-Cat", code)

        completed = self.run_bash("tools/publish_issue.sh", event.as_posix(), RACE_CLONE=other.as_posix())

        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("attempt 1 of 5", completed.stdout)
        log = self.origin_log()
        self.assertEqual(["index: published", "calibration: add", "maintainer edit", "seed"],
                         [subject.rsplit(" CN ", 1)[0] for _, subject in log])
        entry = repo_index.read_index(self.origin_show("main:index.json")).entries[0]
        self.assertEqual(log[1][0], entry["commit"])
        self.assertEqual(code.encode("ascii"), self.origin_show("%s:%s" % (entry["commit"], entry["path"])))
        calls = self.gh_calls()
        for expected in ("issue view 7 --json state --jq .state", "api users/Octo-Cat", "issue edit 7 --add-label published",
                         "issue close 7 --reason completed"):
            self.assertIn(expected, calls)
        self.assertTrue(any(call.startswith("issue comment 7 --body-file ") for call in calls))
        self.assertIn("已发布", self.comment(7))
        self.assertTrue((self.runner / "publish-7" / "replied").exists())

    def test_a_sweep_answers_every_unanswered_submission_one_after_another(self):
        code = sharecode.encode(testsupport.payload("REPLY_STATE", 5))
        first, _ = self.submission(8, 1001, "first-player", code)
        second, _ = self.submission(9, 1002, "second-player", code)
        answered = dict(first, number=10, labels=[{"name": "share-calibration"}, {"name": "published"}])
        (self.stub / "open.json").write_text(json.dumps([first, answered]) + json.dumps([second]), encoding="utf-8")

        completed = self.run_bash("tools/sweep_issues.sh")

        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([2], [entry["submitters"] for entry in repo_index.read_index(self.origin_show("main:index.json")).entries])
        calls = self.gh_calls()
        self.assertIn("issue close 8 --reason completed", calls)
        self.assertIn("issue close 9 --reason completed", calls)
        self.assertFalse(any(" 10 " in call for call in calls))
        self.assertIn("已发布", self.comment(8))
        self.assertIn("已计入", self.comment(9))

    def test_a_refused_submission_is_answered_and_closed_without_touching_main(self):
        young = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=5)).strftime("%Y-%m-%dT%H:%M:%SZ")
        _, event = self.submission(11, 3003, "new-player", sharecode.encode(testsupport.payload()), created=young)

        completed = self.run_bash("tools/publish_issue.sh", event.as_posix())

        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["seed"], [subject for _, subject in self.origin_log()])
        calls = self.gh_calls()
        self.assertIn("issue edit 11 --add-label rejected", calls)
        self.assertIn("issue close 11 --reason not planned", calls)
        self.assertIn("没有受理", self.comment(11))

    def test_a_closed_issue_is_left_alone_and_a_failed_account_lookup_needs_a_maintainer(self):
        _, closed = self.submission(12, 4004, "closed-player", sharecode.encode(testsupport.payload()))
        (self.stub / "state-12").write_text("CLOSED\n", encoding="utf-8")
        completed = self.run_bash("tools/publish_issue.sh", closed.as_posix())
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["issue view 12 --json state --jq .state"], self.gh_calls())

        _, unknown = self.submission(13, 5005, "ghost-player", sharecode.encode(testsupport.payload()), with_account=False)
        completed = self.run_bash("tools/publish_issue.sh", unknown.as_posix())
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        calls = self.gh_calls()
        self.assertIn("issue edit 13 --add-label needs-maintainer", calls)
        self.assertFalse(any(call.startswith("issue close 13") for call in calls))
        self.assertIn("维护者会处理", self.comment(13))
        self.assertTrue((self.runner / "publish-13" / "replied").exists())
        self.assertEqual(["seed"], [subject for _, subject in self.origin_log()])


if __name__ == "__main__":
    unittest.main()
