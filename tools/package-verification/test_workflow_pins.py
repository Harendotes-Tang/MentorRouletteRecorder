#!/usr/bin/env python3
"""Every package a workflow runs through npx names one exact version (audit 2026-10-20, M-4).

``npx --yes markdownlint-cli`` installed whatever release was newest on npm at every run, so a
compromised or breaking release ran in CI with the checkout's token and could flip the required
"Markdown lint" check. A version written as ``name@X.Y.Z`` changes only in a reviewed commit.
"""

from __future__ import annotations

from pathlib import Path
import re
import unittest


REPO = Path(__file__).resolve().parents[2]
WORKFLOWS = REPO / ".github" / "workflows"
NPX = re.compile(r"(?<![\w-])npx(?![\w-])(?P<rest>[^\r\n#]*)")
EXACT = re.compile(r"^(?:@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*@\d+\.\d+\.\d+$")


def npx_packages(text: str) -> list[str]:
    """The package argument of every npx call outside a comment line: its first non-option word."""
    packages = []
    for line in text.splitlines():
        if line.lstrip().startswith("#"):
            continue
        for match in NPX.finditer(line):
            words = [word.strip("'\"") for word in match.group("rest").split()]
            packages.append(next((word for word in words if not word.startswith("-")), ""))
    return packages


class WorkflowPinTests(unittest.TestCase):
    def test_the_parser_reads_the_package_argument(self):
        # Guards the check below: an unpinned call must be seen as one.
        self.assertEqual(["markdownlint-cli"], npx_packages("# npx would install anything\n"
                                                            "run: npx --yes markdownlint-cli 'README.md'"))
        self.assertFalse(EXACT.match("markdownlint-cli"))
        self.assertFalse(EXACT.match("markdownlint-cli@latest"))
        self.assertFalse(EXACT.match("markdownlint-cli@^0.49.1"))
        self.assertTrue(EXACT.match("markdownlint-cli@0.49.1"))

    def test_every_npx_package_names_an_exact_version(self):
        workflows = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
        self.assertTrue(workflows, f"no workflow found under {WORKFLOWS}")
        found = 0
        for workflow in workflows:
            for package in npx_packages(workflow.read_text(encoding="utf-8")):
                found += 1
                with self.subTest(workflow=workflow.name, package=package):
                    self.assertRegex(package, EXACT)
        self.assertGreater(found, 0, "the markdownlint step no longer runs through npx; update this test")


if __name__ == "__main__":
    unittest.main()
