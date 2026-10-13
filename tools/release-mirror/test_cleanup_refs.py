#!/usr/bin/env python3
"""Offline approval-policy tests and disposable local Git deletion proofs."""

import contextlib
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
# Draft staging only: after promotion both modules reside in the same directory.
if not (HERE / "sync_refs.py").exists():
    sys.path.insert(0, str(HERE.parents[1] / "tools" / "release-mirror"))
import sync_refs as shared
import cleanup_refs as m

REPO, OTHER = m.REPOSITORIES
SOURCE, TARGET = m.repository_urls(REPO)
A, B, C = "a" * 40, "b" * 40, "c" * 40
REF = "refs/heads/codex/approved-old-branch"
SECOND = "refs/heads/dependabot/approved-old-branch"
UNLISTED = "refs/heads/preserve-target-only"
TAG = "refs/tags/v1.7.2"


def entry(ref=REF, expected=B, anchor=A, repo=REPO):
    return {"repo": repo, "ref": ref, "expected_sha": expected, "merge_anchor": anchor}


def plan(entries=None):
    return {"schema_version": 1, "branches": entries if entries is not None else [entry()]}


class FakeGit:
    def __init__(self):
        self.source = {m.MAIN: A, TAG: A}
        self.target = {m.MAIN: A, REF: B, SECOND: B, UNLISTED: C, TAG: A}
        self.anchors = {A}
        self.calls = []
        self.source_reads = 0
        self.target_reads = 0
        self.before_read = lambda kind, count: None
        self.before_push = lambda: None
        self.after_push = lambda: None
        self.before_restore = lambda: None

    def run(self, args, *, destination=None):
        self.calls.append((args, destination))
        if args[0] == "rev-parse":
            return (args[-1].removesuffix("^{commit}") if args[-1].endswith("^{commit}") else A) + "\n"
        if args[0] == "cat-file":
            if args[-1] not in self.anchors:
                raise m.RefSyncError("Missing object")
            return "commit\n"
        if args[0] == "merge-base":
            if args[-2] not in self.anchors:
                raise m.RefSyncError("No ancestry")
        if args[0] == "ls-remote":
            kind = "source" if args[2] == SOURCE else "target"
            attr = kind + "_reads"
            setattr(self, attr, getattr(self, attr) + 1)
            self.before_read(kind, getattr(self, attr))
            return "".join(sha + "\t" + ref + "\n" for ref, sha in getattr(self, kind).items())
        if args[0] == "push":
            leases = dict(arg.removeprefix("--force-with-lease=").split(":", 1)
                          for arg in args if arg.startswith("--force-with-lease="))
            specs = dict(arg.split(":", 1)[::-1] for arg in args[args.index(TARGET) + 1:])
            deleting = all(not sha for sha in specs.values())
            (self.before_push if deleting else self.before_restore)()
            if any(self.target.get(ref, "") != leases.get(ref) for ref in specs):
                raise m.RefSyncError("Atomic lease refused")
            for ref, sha in specs.items():
                if sha:
                    self.target[ref] = sha
                else:
                    self.target.pop(ref)
            if deleting:
                self.after_push()
        return ""

    def pushes(self):
        return [args for args, _ in self.calls if args[0] == "push"]


class PlanTests(unittest.TestCase):
    def test_real_plan_is_only_the_three_approved_branches(self):
        actual = m.load_plan()
        self.assertEqual([(e["repo"], e["ref"]) for e in actual], [
            (REPO, "refs/heads/dependabot/pip/tools/LocalAiOcr/setuptools-83.0.0"),
            (REPO, "refs/heads/codex/gitcode-mirror-fallback"),
            (OTHER, "refs/heads/codex/gitcode-actions-mirror")])
        self.assertEqual(actual[1]["expected_sha"], "b47043a0dbd871cf5af8ced63ba1d36b41e9e089")
        self.assertEqual(actual[1]["merge_anchor"], actual[1]["expected_sha"])

    def test_protected_tags_and_malformed_refs_rejected(self):
        for ref in ("refs/tags/v1.7.2", "refs/pull/1/head", "main", "refs/heads/main",
                    "refs/heads/dev", "refs/heads/MAIN", "refs/heads/", "refs/heads/a..b",
                    "refs/heads/.hidden/x", "refs/heads/a.lock/x", "refs/heads/a//b",
                    "refs/heads/a:b", "refs/heads/a b", "refs/heads/a\nb", "refs/heads/a/."):
            with self.subTest(ref=ref), self.assertRaises(m.RefSyncError):
                m.validate_plan(plan([entry(ref=ref)]))

    def test_unknown_repo_bad_sha_duplicates_and_schema_fail_closed(self):
        invalid = [None, [], {}, plan([]), plan([entry(), entry()]),
                   plan([entry(repo="elsewhere")]), plan([entry(expected="abc")]),
                   plan([entry(anchor="0" * 40)]), plan([entry(expected=1)]),
                   {"schema_version": True, "branches": [entry()]},
                   {**plan(), "extra": "ignored?"}, plan([{**entry(), "delete_all": True}])]
        for value in invalid:
            with self.subTest(value=value), self.assertRaises(m.RefSyncError):
                m.validate_plan(value)

    def test_plan_reader_rejects_duplicate_keys_and_oversized_input(self):
        for data in (b'{"schema_version":1,"schema_version":1,"branches":[]}',
                     b"x" * (m.MAX_PLAN_BYTES + 1), b"not-json", b"\xff"):
            with tempfile.TemporaryDirectory(prefix="mentor-cleanup-plan-test-") as directory:
                path = Path(directory) / "plan.json"
                path.write_bytes(data)
                with patch.object(m, "PLAN_PATH", path), self.assertRaises(m.RefSyncError):
                    m.load_plan()


class CleanupTests(unittest.TestCase):
    def test_default_preview_never_pushes_or_supplies_credentials(self):
        git = FakeGit()
        self.assertEqual(m.cleanup(git, REPO, [entry()]), {REF: "eligible"})
        self.assertFalse(git.pushes())
        self.assertTrue(all(destination is None for _, destination in git.calls))

    def test_default_cli_is_preview_and_has_no_arbitrary_plan_argument(self):
        git = FakeGit()
        output = io.StringIO()
        with patch.object(m, "load_plan", return_value=[entry()]), patch.object(m, "Git", return_value=git) as constructor, \
                patch.object(m.tempfile, "TemporaryDirectory") as temp, contextlib.redirect_stdout(output):
            temp.return_value.__enter__.return_value = str(HERE)
            self.assertEqual(m.main(["--repo", REPO]), 0)
        self.assertEqual(constructor.call_args.args[1], "")
        self.assertFalse(git.pushes())
        self.assertIn("preview", output.getvalue())
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            m.main(["--repo", REPO, "--plan", "other.json"])

    def test_existing_source_is_retained_without_push(self):
        git = FakeGit()
        git.source[REF] = C
        result = m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(result[REF], "retained-source-present")
        self.assertFalse(git.pushes())
        self.assertEqual(git.target[REF], B)

    def test_changed_target_refuses_before_any_push(self):
        git = FakeGit()
        git.target[REF] = C
        with self.assertRaisesRegex(m.RefSyncError, "SHA changed"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertFalse(git.pushes())

    def test_target_changed_at_recheck_is_refused(self):
        git = FakeGit()
        def mutate(kind, count):
            if kind == "target" and count == 2:
                git.target[REF] = C
        git.before_read = mutate
        with self.assertRaisesRegex(m.RefSyncError, "SHA changed"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertFalse(git.pushes())

    def test_source_reappears_before_write_is_retained(self):
        git = FakeGit()
        def mutate(kind, count):
            if kind == "source" and count == 2:
                git.source[REF] = B
        git.before_read = mutate
        self.assertEqual(m.cleanup(git, REPO, [entry()], execute=True)[REF], "retained-source-present")
        self.assertFalse(git.pushes())

    def test_main_changed_before_write_is_refused(self):
        git = FakeGit()
        def mutate(kind, count):
            if kind == "source" and count == 2:
                git.source[m.MAIN] = C
        git.before_read = mutate
        with self.assertRaisesRegex(m.RefSyncError, "main changed"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertFalse(git.pushes())

    def test_missing_anchor_fails_before_target_contact_or_push(self):
        git = FakeGit()
        with self.assertRaises(m.RefSyncError):
            m.cleanup(git, REPO, [entry(anchor=C)], execute=True)
        self.assertEqual(git.target_reads, 0)
        self.assertFalse(git.pushes())

    def test_exact_atomic_leases_and_anonymous_after_verification(self):
        git = FakeGit()
        before = copy.copy(git.target)
        result = m.cleanup(git, REPO, [entry(), entry(ref=SECOND)], execute=True)
        self.assertEqual(set(result.values()), {"deleted-and-verified"})
        self.assertEqual(git.pushes(), [["push", "--atomic", "--porcelain",
            "--force-with-lease=" + REF + ":" + B, "--force-with-lease=" + SECOND + ":" + B,
            TARGET, ":" + REF, ":" + SECOND]])
        self.assertEqual(git.target, {ref: sha for ref, sha in before.items() if ref not in {REF, SECOND}})
        self.assertEqual(git.calls[-2][0][:3], ["ls-remote", "--refs", TARGET])
        self.assertEqual(git.calls[-1][0][:3], ["ls-remote", "--refs", SOURCE])
        self.assertIsNone(git.calls[-1][1])
        self.assertTrue(all(dest is None for args, dest in git.calls if args[0] != "push"))

    def test_lease_race_refuses_entire_batch(self):
        git = FakeGit()
        git.before_push = lambda: git.target.update({REF: C})
        with self.assertRaisesRegex(m.RefSyncError, "lease"):
            m.cleanup(git, REPO, [entry(), entry(ref=SECOND)], execute=True)
        self.assertEqual(git.target[REF], C)
        self.assertEqual(git.target[SECOND], B)

    def test_absent_target_is_idempotent_without_push(self):
        git = FakeGit()
        git.target.pop(REF)
        before = copy.copy(git.target)
        self.assertEqual(m.cleanup(git, REPO, [entry()], execute=True)[REF], "already-absent")
        self.assertFalse(git.pushes())
        self.assertEqual(git.target, before)

    def test_changed_unlisted_ref_after_push_is_not_claimed_as_success(self):
        git = FakeGit()
        git.after_push = lambda: git.target.update({UNLISTED: B})
        with self.assertRaisesRegex(m.RefSyncError, "verification failed"):
            m.cleanup(git, REPO, [entry()], execute=True)

    def test_unverified_deletion_is_not_claimed_as_success(self):
        git = FakeGit()
        git.after_push = lambda: git.target.update({REF: B})
        with self.assertRaisesRegex(m.RefSyncError, "verification failed"):
            m.cleanup(git, REPO, [entry()], execute=True)

    def test_source_recreated_at_push_is_restored_not_reported_deleted(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C, UNLISTED: A})
        before = copy.copy(git.target)
        with self.assertRaisesRegex(m.RefSyncError, "restored and verified.*incomplete"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(git.target, {**before, REF: C})
        self.assertEqual(len(git.pushes()), 2)
        self.assertEqual(git.pushes()[1], ["push", "--atomic", "--porcelain",
                         "--force-with-lease=" + REF + ":", TARGET, C + ":" + REF])
        self.assertIn((["fetch", "--no-tags", SOURCE, C], None), git.calls)
        self.assertEqual(git.calls[-1][0][:3], ["ls-remote", "--refs", SOURCE])
        self.assertTrue(all(dest is None for args, dest in git.calls if args[0] != "push"))

    def test_source_change_during_restore_refuses_creation(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C})
        def mutate(kind, count):
            if kind == "source" and count == 4:
                git.source[REF] = B
        git.before_read = mutate
        with self.assertRaisesRegex(m.RefSyncError, "Source changed during restoration"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(len(git.pushes()), 1)
        self.assertNotIn(REF, git.target)

    def test_different_target_during_restore_is_never_overwritten(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C})
        def mutate(kind, count):
            if kind == "target" and count == 4:
                git.target[REF] = B
        git.before_read = mutate
        with self.assertRaisesRegex(m.RefSyncError, "Restoration target changed"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(len(git.pushes()), 1)
        self.assertEqual(git.target[REF], B)

    def test_matching_restoration_target_requires_no_second_push(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C})
        git.after_push = lambda: git.target.update({REF: C})
        with self.assertRaisesRegex(m.RefSyncError, "restored and verified.*incomplete"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(len(git.pushes()), 1)
        self.assertEqual(git.target[REF], C)

    def test_restoration_lease_race_keeps_other_recreated_target(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C})
        git.before_restore = lambda: git.target.update({REF: B})
        with self.assertRaisesRegex(m.RefSyncError, "lease"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(git.target[REF], B)

    def test_source_change_after_restore_is_not_reported_verified(self):
        git = FakeGit()
        git.before_push = lambda: git.source.update({REF: C})
        def mutate(kind, count):
            if kind == "source" and count == 5:
                git.source[REF] = B
        git.before_read = mutate
        with self.assertRaisesRegex(m.RefSyncError, "Restoration verification failed"):
            m.cleanup(git, REPO, [entry()], execute=True)
        self.assertEqual(git.target[REF], C)


class LocalGitTests(unittest.TestCase):
    """Real ancestry/atomic deletion semantics, using local disposable repos only."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mentor-cleanup-git-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.target = self.root / "target.git"
        self.source.mkdir()
        self.run_git(self.source, "init", "--initial-branch=main", "--template=")
        self.run_git(self.root, "init", "--bare", "--template=", str(self.target))
        self.run_git(self.source, "commit", "--allow-empty", "-m", "approved anchor")
        self.one = self.run_git(self.source, "rev-parse", "HEAD").strip()
        self.run_git(self.source, "commit", "--allow-empty", "-m", "current main")
        self.two = self.run_git(self.source, "rev-parse", "HEAD").strip()
        self.run_git(self.source, "push", str(self.target), self.two + ":" + m.MAIN,
                     self.one + ":" + REF, self.one + ":" + SECOND,
                     self.one + ":" + UNLISTED, self.one + ":" + TAG)

    def run_git(self, cwd, *args, check=True):
        result = subprocess.run(["git", "-c", "user.name=Cleanup Test",
            "-c", "user.email=cleanup@example.invalid", "-c", "credential.helper=",
            "-c", "protocol.file.allow=always", "-c", "core.hooksPath=" + str(self.root / "no-hooks"),
            *args], cwd=cwd, env=shared.git_environment(), capture_output=True, text=True, timeout=30)
        if check:
            self.assertEqual(result.returncode, 0, result.stderr)
            return result.stdout
        return result

    def push_cleanup(self, entries):
        decisions = {item["ref"]: "eligible" for item in entries}
        args = m.deletion_arguments(entries, decisions, TARGET)
        args[args.index(TARGET)] = str(self.target)
        return self.run_git(self.source, *args, check=False)

    def test_actual_atomic_delete_leaves_other_refs_and_tag_untouched(self):
        before = shared.parse_refs(self.run_git(self.source, "ls-remote", "--refs", str(self.target)))
        result = self.push_cleanup([entry(expected=self.one), entry(ref=SECOND, expected=self.one)])
        self.assertEqual(result.returncode, 0, result.stderr)
        after = shared.parse_refs(self.run_git(self.source, "ls-remote", "--refs", str(self.target)))
        self.assertEqual(after, {ref: sha for ref, sha in before.items() if ref not in {REF, SECOND}})

    def test_actual_stale_lease_blocks_all_deletions(self):
        self.run_git(self.source, "push", str(self.target), self.two + ":" + REF)
        result = self.push_cleanup([entry(expected=self.one), entry(ref=SECOND, expected=self.one)])
        self.assertNotEqual(result.returncode, 0)
        after = shared.parse_refs(self.run_git(self.source, "ls-remote", "--refs", str(self.target)))
        self.assertEqual(after[REF], self.two)
        self.assertEqual(after[SECOND], self.one)

    def test_actual_ancestry_accepts_anchor_and_rejects_unmerged_branch(self):
        self.run_git(self.source, "merge-base", "--is-ancestor", self.one, self.two)
        self.run_git(self.source, "checkout", "--orphan", "unmerged")
        self.run_git(self.source, "commit", "--allow-empty", "-m", "unmerged")
        orphan = self.run_git(self.source, "rev-parse", "HEAD").strip()
        result = self.run_git(self.source, "merge-base", "--is-ancestor", orphan, self.two, check=False)
        self.assertEqual(result.returncode, 1)

    def test_actual_cleanup_restores_reappeared_source_with_empty_target_lease(self):
        case = self
        clone = self.root / "cleanup.git"
        clone.mkdir()
        calls = []
        class LocalTransport:
            def run(self, args, *, destination=None):
                calls.append((args, destination))
                if args[0] == "push" and ":" + REF in args:
                    # Simulate the source branch being recreated after the last
                    # pre-deletion read, with a commit newer than the approval.
                    case.run_git(case.source, "update-ref", REF, case.two)
                local_args = [str(case.source) if arg == SOURCE else
                              str(case.target) if arg == TARGET else arg for arg in args]
                return case.run_git(clone, *local_args)
        before = shared.parse_refs(self.run_git(self.source, "ls-remote", "--refs", str(self.target)))
        with self.assertRaisesRegex(m.RefSyncError, "restored and verified.*incomplete"):
            m.cleanup(LocalTransport(), REPO, [entry(expected=self.one, anchor=self.one)], execute=True)
        after = shared.parse_refs(self.run_git(self.source, "ls-remote", "--refs", str(self.target)))
        self.assertEqual(after, {**before, REF: self.two})
        pushes = [args for args, _ in calls if args[0] == "push"]
        self.assertEqual(len(pushes), 2)
        self.assertIn("--force-with-lease=" + REF + ":", pushes[1])
        self.assertEqual(pushes[1][-1], self.two + ":" + REF)


if __name__ == "__main__":
    unittest.main(verbosity=2)
