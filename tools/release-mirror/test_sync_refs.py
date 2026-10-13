#!/usr/bin/env python3
"""Offline policy tests plus local-only Git atomic/lease integration checks."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import sync_refs as m

REPO = m.REPOSITORIES[0]
DESTINATION = m.repository_urls(REPO)[1]
A, B = "a" * 40, "b" * 40


class PolicyTests(unittest.TestCase):
    def test_only_two_fixed_repositories(self):
        for repo in m.REPOSITORIES:
            self.assertIn(m.OWNER, m.repository_urls(repo)[1])
        for repo in ("other", "../MentorRouletteRecorder", "Other/" + REPO):
            with self.assertRaises(m.RefSyncError):
                m.repository_urls(repo)

    def test_no_pruning_internal_refs_or_unconditional_force(self):
        args = m.push_arguments({"refs/heads/main": A, "refs/tags/v1": B},
                                {"refs/heads/main": B, "refs/heads/mirror-only": A}, DESTINATION)
        self.assertIn("--atomic", args)
        self.assertIn("--force-with-lease=refs/heads/main:" + B, args)
        self.assertIn("--force-with-lease=refs/tags/v1:", args)
        self.assertIn(A + ":refs/heads/main", args)
        self.assertFalse(any("mirror-only" in arg or arg in {"--mirror", "--force", "--prune"} for arg in args))

    def test_matching_snapshot_is_noop(self):
        self.assertEqual(m.push_arguments({"refs/heads/main": A}, {"refs/heads/main": A}, DESTINATION), [])

    def test_internal_malformed_duplicate_refs_are_refused(self):
        for ref in ("refs/pull/1/head", "HEAD", "refs/heads/../evil", "refs/heads/x:y"):
            with self.assertRaises(m.RefSyncError):
                m.parse_refs(A + "\t" + ref)
        with self.assertRaises(m.RefSyncError):
            m.parse_refs((A + "\trefs/heads/main\n") * 2)
        with self.assertRaises(m.RefSyncError):
            m.parse_refs("not-a-sha\trefs/heads/main")

    def test_credential_is_scoped_ephemeral_and_traces_removed(self):
        inherited = {"GIT_TRACE": "1", "GIT_TRACE_CURL": "1", "GIT_CONFIG_PARAMETERS": "evil",
                     "GIT_ASKPASS": "evil", "GITCODE_MIRROR_TOKEN": "secret", "GITHUB_TOKEN": "gh"}
        with patch.dict(os.environ, inherited, clear=True):
            public = m.git_environment()
            private = m.git_environment(DESTINATION, "secret")
        for key in inherited:
            self.assertNotIn(key, public)
            self.assertNotIn(key, private)
        self.assertNotIn("GIT_CONFIG_VALUE_0", public)
        self.assertEqual(private["GIT_CONFIG_KEY_0"], f"http.{DESTINATION}.extraHeader")
        self.assertTrue(private["GIT_CONFIG_VALUE_0"].startswith("Authorization: Basic "))
        self.assertEqual(private["GIT_CONFIG_GLOBAL"], os.devnull)
        with self.assertRaises(m.RefSyncError):
            m.git_environment("https://evil.test/repo.git", "secret")

    def test_raw_git_errors_are_never_relayed(self):
        outcome = subprocess.CompletedProcess([], 1, "secret leaked stdout", "secret leaked stderr")
        with patch.object(m.subprocess, "run", return_value=outcome) as run:
            with self.assertRaises(m.RefSyncError) as failure:
                m.Git(Path("unused"), "secret").run(["ls-remote", DESTINATION], destination=DESTINATION)
        self.assertNotIn("secret", str(failure.exception))
        self.assertNotIn("secret", " ".join(run.call_args.args[0]))
        self.assertIn("http.followRedirects=false", run.call_args.args[0])

    def test_changed_source_snapshot_aborts_before_target_contact(self):
        class Changed:
            def __init__(self):
                self.calls = []
            def run(self, args, **kwargs):
                self.calls.append(args)
                if args[0] == "for-each-ref":
                    return A + "\trefs/heads/main\n"
                if args[0] == "ls-remote":
                    return B + "\trefs/heads/main\n"
                return ""
        fake = Changed()
        with self.assertRaisesRegex(m.RefSyncError, "changed during fetch"):
            m.synchronize(fake, REPO)
        self.assertFalse(any(DESTINATION in args for args in fake.calls))

    def test_push_is_followed_by_full_ref_verification(self):
        class Inconsistent:
            def __init__(self):
                self.target_reads = 0
                self.pushed = False
            def run(self, args, **kwargs):
                if args[0] == "for-each-ref" or (args[0] == "ls-remote" and DESTINATION not in args):
                    return A + "\trefs/heads/main\n"
                if args[0] == "ls-remote":
                    self.target_reads += 1
                    return B + "\trefs/heads/main\n"
                if args[0] == "push":
                    self.pushed = True
                return ""
        fake = Inconsistent()
        with self.assertRaisesRegex(m.RefSyncError, "after push"):
            m.synchronize(fake, REPO)
        self.assertTrue(fake.pushed)
        self.assertEqual(fake.target_reads, 2)


class LocalGitTests(unittest.TestCase):
    """Exercise real Git on disposable local repositories; no network or PAT."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.target = self.root / "target.git"
        self.source.mkdir()
        self.git(self.source, "init", "--initial-branch=main", "--template=")
        self.git(self.root, "init", "--bare", "--template=", str(self.target))
        self.git(self.source, "commit", "--allow-empty", "-m", "one")
        self.one = self.git(self.source, "rev-parse", "HEAD").strip()
        self.git(self.source, "commit", "--allow-empty", "-m", "two")
        self.two = self.git(self.source, "rev-parse", "HEAD").strip()
        self.git(self.source, "push", str(self.target), self.one + ":refs/heads/main",
                 self.one + ":refs/heads/target-only")

    def git(self, cwd, *args, check=True):
        result = subprocess.run(["git", "-c", "user.name=Mirror Test", "-c", "user.email=mirror@example.invalid",
                                 "-c", "core.hooksPath=" + str(self.root / "no-hooks"),
                                 "-c", "protocol.file.allow=always", *args], cwd=cwd,
                                env=m.git_environment(), text=True, capture_output=True, timeout=30)
        if check:
            self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout if check else result

    def local_push(self, source, target):
        args = m.push_arguments(source, target, DESTINATION)
        args[args.index(DESTINATION)] = str(self.target)  # Tests only: transport is a disposable local path.
        return self.git(self.source, *args, check=False)

    def test_atomic_update_preserves_target_only_ref_and_tags(self):
        source = {"refs/heads/main": self.two, "refs/tags/v1": self.two}
        result = self.local_push(source, {"refs/heads/main": self.one, "refs/heads/target-only": self.one})
        self.assertEqual(result.returncode, 0, result.stderr)
        after = m.parse_refs(self.git(self.source, "ls-remote", "--refs", str(self.target)))
        self.assertEqual(after["refs/heads/main"], self.two)
        self.assertEqual(after["refs/tags/v1"], self.two)
        self.assertEqual(after["refs/heads/target-only"], self.one)

    def test_stale_lease_atomically_prevents_other_new_tag(self):
        self.git(self.source, "push", str(self.target), self.two + ":refs/heads/main")
        self.git(self.source, "commit", "--allow-empty", "-m", "three")
        three = self.git(self.source, "rev-parse", "HEAD").strip()
        result = self.local_push({"refs/heads/main": three, "refs/tags/v2": three},
                                 {"refs/heads/main": self.one})
        self.assertNotEqual(result.returncode, 0)
        after = m.parse_refs(self.git(self.source, "ls-remote", "--refs", str(self.target)))
        self.assertEqual(after["refs/heads/main"], self.two)
        self.assertNotIn("refs/tags/v2", after)


if __name__ == "__main__":
    unittest.main(verbosity=2)
