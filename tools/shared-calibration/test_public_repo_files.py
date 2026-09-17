#!/usr/bin/env python3
"""The public repository's workflow and issue form: the security rules, and the contracts with the client.

Structural checks use PyYAML when it is installed; the text checks below run either way, so a machine
without PyYAML (the CI runner) still enforces every security rule.
"""

from __future__ import annotations

import json
import re
import unittest

import testsupport
import index as repo_index
import issue as issue_form
import publish
import sharecode

try:
    import yaml
except ImportError:  # pragma: no cover - depends on the machine
    yaml = None

PUBLIC = testsupport.HERE / "public-repo"
WORKFLOW = PUBLIC / ".github" / "workflows" / "publish-calibration.yml"
FORM = PUBLIC / ".github" / "ISSUE_TEMPLATE" / "share-calibration.yml"
CONFIG = PUBLIC / ".github" / "ISSUE_TEMPLATE" / "config.yml"
CI = testsupport.REPO / ".github" / "workflows" / "ci.yml"
SHARING = testsupport.REPO / "src" / "Collector" / "Protocol" / "Sharing"
SCRIPTS = ("publish_issue.sh", "sweep_issues.sh")


def run_scripts(text: str) -> list:
    """The body of every ``run:`` key, block scalars included, found by indentation."""
    lines = text.splitlines()
    scripts, position = [], 0
    while position < len(lines):
        match = re.match(r"^(\s*)(?:-\s+)?run:\s*(.*)$", lines[position])
        position += 1
        if match is None:
            continue
        indent, rest = len(match.group(1)), match.group(2).strip()
        if rest in ("|", ">", "|-", ">-", "|+", ">+"):
            block = []
            while position < len(lines) and (not lines[position].strip() or len(lines[position]) - len(lines[position].lstrip()) > indent):
                block.append(lines[position])
                position += 1
            scripts.append("\n".join(block))
        else:
            scripts.append(rest)
    return scripts


def csharp_constant(path, name: str):
    match = re.search(r"\bconst\s+(?:int|string)\s+%s\s*=\s*(.+?);" % re.escape(name), path.read_text(encoding="utf-8"))
    if match is None:
        raise AssertionError("%s declares no constant %s" % (path.name, name))
    value = match.group(1).strip()
    if value.startswith('"'):
        return json.loads(value)
    product = 1
    for factor in value.split("*"):
        product *= int(factor.strip())
    return product


class WorkflowSecurityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = WORKFLOW.read_text(encoding="utf-8")

    def test_no_run_script_contains_an_expression(self):
        scripts = [script for script in run_scripts(self.text) if script.strip()]
        self.assertGreaterEqual(len(scripts), 3)
        for script in scripts:
            with self.subTest(script[:40]):
                self.assertNotIn("${{", script)

    def test_untrusted_event_text_is_never_referenced(self):
        for forbidden in ("github.event.issue.title", "github.event.issue.body", "github.event.issue.user",
                          "github.event.comment", "github.head_ref", "pull_request_target", "secrets."):
            with self.subTest(forbidden):
                self.assertNotIn(forbidden, self.text)

    def test_expressions_outside_if_conditions_are_only_trusted_values(self):
        expressions = set(re.findall(r"\$\{\{\s*(.*?)\s*\}\}", self.text))
        self.assertEqual({"github.token", "github.repository", "github.event.issue.number"}, expressions)

    def test_triggers_permissions_and_the_shared_concurrency_group(self):
        self.assertIn("types: [opened, edited, labeled]", self.text)
        self.assertEqual(2, self.text.count("group: publish-calibration"))
        self.assertEqual(2, self.text.count("cancel-in-progress: false"))
        self.assertNotIn("cancel-in-progress: true", self.text)
        if yaml is None:
            self.assertIn("permissions:\n  contents: write\n  issues: write\n", self.text)
            return
        data = yaml.safe_load(self.text)
        triggers = data.get("on", data.get(True))
        self.assertEqual({"issues", "schedule", "workflow_dispatch"}, set(triggers))
        self.assertEqual(["opened", "edited", "labeled"], triggers["issues"]["types"])
        self.assertEqual({"contents": "write", "issues": "write"}, data["permissions"])
        self.assertNotIn("concurrency", data, "a workflow-level group would let skipped events cancel pending jobs")
        self.assertEqual({"publish", "sweep"}, set(data["jobs"]))
        for job in data["jobs"].values():
            self.assertEqual({"group": "publish-calibration", "cancel-in-progress": False}, job["concurrency"])
            self.assertIn("timeout-minutes", job)
            self.assertNotIn("permissions", job)
        condition = data["jobs"]["publish"]["if"]
        for fragment in ("github.event_name == 'issues'", "github.event.issue.state == 'open'",
                         "contains(github.event.issue.labels.*.name, 'share-calibration')",
                         "github.event.label.name == 'share-calibration'"):
            self.assertIn(fragment, condition)

    def test_actions_are_pinned_like_the_main_ci(self):
        used = set(re.findall(r"uses:\s*(\S+)", self.text))
        self.assertTrue(used)
        self.assertLessEqual(used, set(re.findall(r"uses:\s*(\S+)", CI.read_text(encoding="utf-8"))))

    def test_the_scripts_are_strict_and_safe_against_being_replaced_while_they_run(self):
        for name in SCRIPTS:
            with self.subTest(name):
                text = (testsupport.HERE / name).read_text(encoding="utf-8")
                self.assertTrue(text.startswith("#!/usr/bin/env bash\n"))
                self.assertIn("\nset -euo pipefail\n", text)
                self.assertTrue(text.endswith('\nmain "$@"; exit "$?"\n'))
                self.assertNotIn("\r", text)
                for token in ("${{", ".title", ".body", "eval ", "GITHUB_ENV", "GITHUB_OUTPUT"):
                    self.assertNotIn(token, text)
                self.assertTrue(all(line.startswith(("#", "set ", "py()", "main ")) or not line or line[0] in " }"
                                    or re.match(r"^[a-z_]+\(\) \{$", line) for line in text.splitlines()),
                                "every statement must sit inside a function")

    def test_every_label_the_scripts_use_is_documented_for_the_manual_setup(self):
        readme = (testsupport.HERE / "README.md").read_text(encoding="utf-8")
        for label in (publish.SUBMISSION_LABEL, publish.LABEL_PUBLISHED, publish.LABEL_REJECTED, publish.LABEL_MAINTAINER):
            self.assertIn("`%s`" % label, readme)


class IssueFormTests(unittest.TestCase):
    def test_the_form_is_the_one_the_client_links_to_and_the_one_the_parser_reads(self):
        link = (SHARING / "SharedCalibrationIssueLink.cs").read_text(encoding="utf-8")
        self.assertEqual(csharp_constant(SHARING / "SharedCalibrationIssueLink.cs", "FormTemplate"), FORM.name)
        self.assertEqual(publish.ISSUE_FORM, FORM.name)
        self.assertIn('"&code="', link)
        self.assertIn('"%s"' % issue_form.TITLE_PREFIX, link)
        text = FORM.read_text(encoding="utf-8")
        for fragment in ("id: %s" % publish.CODE_FIELD_ID, "label: " + issue_form.CODE_LABEL,
                         "label: " + issue_form.CONFIRM_LABEL, "- label: " + issue_form.CONFIRM_OPTION,
                         'title: "%s"' % issue_form.TITLE_PREFIX, "- " + publish.SUBMISSION_LABEL):
            self.assertIn(fragment, text)
        self.assertEqual(2, text.count("required: true"))
        if yaml is None:
            return
        data = yaml.safe_load(text)
        self.assertEqual(issue_form.TITLE_PREFIX, data["title"])
        self.assertEqual([publish.SUBMISSION_LABEL], data["labels"])
        fields = [field for field in data["body"] if field["type"] != "markdown"]
        self.assertEqual(["textarea", "checkboxes"], [field["type"] for field in fields])
        code, confirm = fields
        self.assertEqual((publish.CODE_FIELD_ID, issue_form.CODE_LABEL, True),
                         (code["id"], code["attributes"]["label"], code["validations"]["required"]))
        self.assertEqual(issue_form.CONFIRM_LABEL, confirm["attributes"]["label"])
        self.assertEqual([{"label": issue_form.CONFIRM_OPTION, "required": True}], confirm["attributes"]["options"])

    def test_blank_issues_are_disabled(self):
        text = CONFIG.read_text(encoding="utf-8")
        self.assertIn("\nblank_issues_enabled: false\n", "\n" + text)
        if yaml is not None:
            self.assertEqual({"blank_issues_enabled": False}, yaml.safe_load(text))


class ClientContractTests(unittest.TestCase):
    def test_names_and_limits_agree_with_the_released_client(self):
        client, index_file, code_file = (SHARING / "SharedCalibrationClient.cs", SHARING / "SharedCalibrationIndex.cs",
                                         SHARING / "ShareCode.cs")
        pairs = (
            (client, "Owner", publish.OWNER), (client, "Repository", publish.REPOSITORY),
            (client, "MaxIndexBytes", repo_index.MAX_INDEX_BYTES), (client, "MaxCodeBytes", sharecode.MAX_CODE_LENGTH),
            (client, "IndexFileName", repo_index.INDEX_FILE),
            (index_file, "SchemaVersion", repo_index.SCHEMA_VERSION), (index_file, "MaxEntries", repo_index.MAX_ENTRIES),
            (index_file, "MaxCandidates", repo_index.MAX_CANDIDATES), (index_file, "CodeExtension", repo_index.CODE_EXTENSION),
            (code_file, "Prefix", sharecode.PREFIX), (code_file, "PayloadVersion", sharecode.PAYLOAD_VERSION),
            (code_file, "MaxCodeLength", sharecode.MAX_CODE_LENGTH), (code_file, "MaxInflatedBytes", sharecode.MAX_INFLATED_BYTES),
            (code_file, "MaxSelectorValues", sharecode.MAX_SELECTOR_VALUES),
        )
        for path, name, value in pairs:
            with self.subTest(name):
                self.assertEqual(csharp_constant(path, name), value)

    def test_rejection_tokens_and_messages_are_the_clients_word_for_word(self):
        text = (SHARING / "ShareCodeRejection.cs").read_text(encoding="utf-8")
        for name, token in vars(sharecode).items():
            if name.startswith("E_"):
                with self.subTest(token):
                    self.assertIn('"%s"' % token, text)
                    self.assertIn('"%s"' % sharecode.MESSAGES[token], text)
        self.assertIn('"校准码无法使用。"', text)


if __name__ == "__main__":
    unittest.main()
