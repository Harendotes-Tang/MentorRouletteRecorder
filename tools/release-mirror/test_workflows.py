#!/usr/bin/env python3
"""Configuration pins for the three GitCode mirror workflows.

These read workflow text; the mirror behaviour itself is tested in selftest.py and
test_prerelease.py. No network, no GitHub Actions runtime.
"""

from pathlib import Path
import unittest

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"
REPOSITORY_GUARD = "github.repository == 'Harendotes-Tang/MentorRouletteRecorder' && "
# Dependabot-triggered pushes and deletions run without Actions secrets. A scheduled
# run is never skipped, whoever GitHub names as its actor.
NOT_DEPENDABOT = " && (github.event_name == 'schedule' || github.actor != 'dependabot[bot]')"
SHARED_LOCK = "gitcode-mirror-${{ github.repository }}"
# RV4-3 (audit 2026-10-20): one lock for every mirror-release.yml run, whatever its trigger. A
# per-tag lock for release events let a catch-up run upload to the same tag at the same time.
RELEASE_LOCK = "gitcode-release-${{ github.repository }}"


def read(name: str) -> str:
    return (WORKFLOWS / name).read_text(encoding="utf-8").replace("\r\n", "\n")


def block_value(lines: list[str], header: str, key: str) -> str:
    """The value of `key` among the lines indented under `header`."""
    indent = len(header) - len(header.lstrip()) + 2
    prefix = " " * indent + key + ": "
    for line in lines[lines.index(header) + 1:]:
        if line.startswith(prefix):
            return line[len(prefix):]
        if line.strip() and len(line) - len(line.lstrip()) < indent:
            break
    raise AssertionError(f"{header.strip()} has no {key}")


def job_condition(name: str, job: str) -> str:
    lines = read(name).split("\n")
    return block_value(lines[lines.index("jobs:"):], "  " + job + ":", "if")


def concurrency_group(name: str) -> str:
    return block_value(read(name).split("\n"), "concurrency:", "group")


class DependabotTests(unittest.TestCase):
    def test_secret_jobs_skip_dependabot_pushes_and_deletions(self):
        for name, job in (("mirror-repository.yml", "mirror"), ("cleanup-mirror-branches.yml", "cleanup")):
            with self.subTest(workflow=name):
                condition = job_condition(name, job)
                self.assertTrue(condition.startswith(REPOSITORY_GUARD), condition)
                self.assertTrue(condition.endswith(NOT_DEPENDABOT), condition)

    def test_cleanup_still_ignores_tag_deletions(self):
        self.assertIn("(github.event_name != 'delete' || github.event.ref_type == 'branch')",
                      job_condition("cleanup-mirror-branches.yml", "cleanup"))


class ConcurrencyTests(unittest.TestCase):
    def test_every_release_mirror_run_holds_one_lock_apart_from_the_repository_mirror(self):
        self.assertEqual(concurrency_group("mirror-release.yml"), RELEASE_LOCK)
        self.assertNotEqual(RELEASE_LOCK, SHARED_LOCK)
        for name in ("mirror-repository.yml", "cleanup-mirror-branches.yml"):
            with self.subTest(workflow=name):
                self.assertEqual(concurrency_group(name), SHARED_LOCK)

    def test_no_mirror_run_is_cancelled_while_in_progress(self):
        for name in ("mirror-release.yml", "mirror-repository.yml", "cleanup-mirror-branches.yml"):
            with self.subTest(workflow=name):
                self.assertEqual(block_value(read(name).split("\n"), "concurrency:", "cancel-in-progress"), "false")

    def test_release_ref_sync_retries_once_after_losing_a_lease_race(self):
        # No run of this workflow is serialized with the repository mirror.
        command = "python tools/release-mirror/sync_refs.py --repo MentorRouletteRecorder"
        self.assertIn(f"run: {command} || {{ sleep 30; {command}; }}", read("mirror-release.yml"))


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_workflow_subscribes_to_edits_and_runs_its_tests_from_main(self):
        text = read("mirror-release.yml")
        self.assertIn("\n  release:\n    types: [published, edited]\n", text)
        self.assertNotIn("!github.event.release.prerelease", text)
        for test in ("selftest.py", "test_prerelease.py"):
            self.assertIn("python tools/release-mirror/" + test, text)
        self.assertIn("ref: main", text)
        self.assertIn("timeout-minutes: 360", text)
        self.assertIn("timeout-minutes: 330", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
