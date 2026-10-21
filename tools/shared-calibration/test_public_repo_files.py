#!/usr/bin/env python3
"""The public repository's workflow and issue form: the security rules, and the contracts with the client.

Structural checks use PyYAML when it is installed; the text checks below run either way, so a machine
without PyYAML (the CI runner) still enforces every security rule.
"""

from __future__ import annotations

import importlib.util
import json
import re
import sys
import unittest

import testsupport
import index as repo_index
import issue as issue_form
import publish
import sharecode
import sync_public_repo

try:
    import yaml
except ImportError:  # pragma: no cover - depends on the machine
    yaml = None

PUBLIC = testsupport.HERE / "public-repo"
WORKFLOW = PUBLIC / ".github" / "workflows" / "publish-calibration.yml"
REPORT_WORKFLOW = PUBLIC / ".github" / "workflows" / "report-calibration.yml"
MIRROR_WORKFLOW = PUBLIC / ".github" / "workflows" / "mirror-repository.yml"
EVIDENCE_WORKFLOW = PUBLIC / ".github" / "workflows" / "evidence-calibration.yml"
FORM = PUBLIC / ".github" / "ISSUE_TEMPLATE" / "share-calibration.yml"
REPORT_FORM = PUBLIC / ".github" / "ISSUE_TEMPLATE" / "report-calibration.yml"
CONFIG = PUBLIC / ".github" / "ISSUE_TEMPLATE" / "config.yml"
CI = testsupport.REPO / ".github" / "workflows" / "ci.yml"
SHARING = testsupport.REPO / "src" / "Collector" / "Protocol" / "Sharing"
SCRIPTS = ("publish_issue.sh", "sweep_issues.sh", "report_issue.sh")


def _load_collector_texts():
    """tools/collector-texts/generate.py, which reads contracts/collector-texts.json and names each key's factory."""
    path = testsupport.REPO / "tools" / "collector-texts" / "generate.py"
    spec = importlib.util.spec_from_file_location("collector_texts_generate", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


collector_texts = _load_collector_texts()


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


USES = re.compile(r"uses:\s*(?P<action>[^\s@]+)@(?P<reference>\S+)(?P<rest>.*)$")


def action_pins(text: str) -> list:
    """Every ``uses:`` in a workflow as (action, reference, trailing comment)."""
    matches = (USES.search(line) for line in text.splitlines())
    return [(m.group("action"), m.group("reference"), m.group("rest").strip()) for m in matches if m]


def assert_every_action_is_pinned(case, text: str, label: str) -> set:
    """Every action is referenced by commit id, with the release in a trailing comment.

    A tag can be moved to another commit - and jurplel/install-qt-action@v4, which the main CI
    used to reference, was not a tag at all but a branch, which moves on every push. The publish
    job here holds contents: write and everything it writes is downloaded by every installed
    client, so only a commit id states what will actually run. The version stays in the comment
    so an upgrade remains readable in a diff; Dependabot proposes it as a commit bump.
    """
    pins = action_pins(text)
    case.assertTrue(pins, label)
    for action, reference, comment in pins:
        with case.subTest(label + " " + action):
            case.assertRegex(reference, r"^[0-9a-f]{40}$")
            case.assertRegex(comment, r"^#\s*v\d")
    return {"%s@%s" % (action, reference) for action, reference, _ in pins}


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

    def test_actions_are_pinned_to_a_commit_id_and_taken_from_the_main_ci_set(self):
        used = assert_every_action_is_pinned(self, self.text, "publish-calibration.yml")
        # The main CI is the other half of the same rule: it builds the releases, and it runs the
        # one third-party action in either repository.
        available = assert_every_action_is_pinned(self, CI.read_text(encoding="utf-8"), "ci.yml")
        # Same commit ids, not merely the same action names: a public repository whose workflow
        # ran a different build of an action than the one the maintainer verified is the point.
        self.assertLessEqual(used, available)

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
        for label in (publish.SUBMISSION_LABEL, publish.REPORT_LABEL, publish.LABEL_PUBLISHED, publish.LABEL_REJECTED,
                      publish.LABEL_MAINTAINER, publish.LABEL_CONFLICTING):
            self.assertIn("`%s`" % label, readme)
        for label in (publish.REPORT_LABEL, publish.LABEL_MAINTAINER, publish.LABEL_CONFLICTING):
            with self.subTest(label):
                self.assertIn("gh label create %s" % label, readme, "a new label needs a manual step")
        self.assertIn("`%s`" % publish.LABEL_CONFLICTING, (PUBLIC / "README.md").read_text(encoding="utf-8"))

    def test_every_label_a_form_applies_or_the_evidence_lane_sets_has_a_manual_create_step(self):
        """N1-2: GitHub applies a form's label only if it exists, and the evidence lane reads nothing else."""
        readme = (testsupport.HERE / "README.md").read_text(encoding="utf-8")
        forms = set()
        for path in sorted((PUBLIC / ".github" / "ISSUE_TEMPLATE").glob("*.yml")):
            match = re.search(r"^labels:\s*(?:\[(?P<inline>[^\]]*)\]|\n(?P<block>(?:\s+-\s*\S+\n)+))", path.read_text(encoding="utf-8"), re.M)
            if match is None:
                continue
            names = match.group("inline").split(",") if match.group("inline") is not None else re.findall(r"-\s*(\S+)", match.group("block"))
            forms.update(name.strip() for name in names)
        self.assertEqual({"share-calibration", "calibration-report", "calibration-evidence", "template-observation"}, forms)
        lane = (testsupport.HERE / "evidence_workflow.py").read_text(encoding="utf-8")
        results = {"candidate-waiting", "evidence-accepted", "evidence-invalid", "evidence-waiting"}
        for label in sorted(forms | results):
            with self.subTest(label):
                if label in results:
                    self.assertIn('"%s"' % label, lane)
                self.assertTrue("gh label create %s " % label in readme, "no manual create step for " + label)

    def test_the_manual_setup_names_the_mirror_secret_and_the_evidence_files(self):
        readme = (testsupport.HERE / "README.md").read_text(encoding="utf-8")
        steps = readme[readme.index("## 需要用户确认后才能执行的手工步骤"):]
        self.assertTrue("GITCODE_MIRROR_TOKEN" in steps, "the mirror secret is a manual step")
        self.assertFalse("不需要任何 secret" in steps, "the mirror workflow needs a secret")
        for name in ("tools/evidence.py", "tools/evidence_workflow.py", "tools/sync_refs.py",
                     "schemas/calibration-evidence-v1.schema.json", ".github/ISSUE_TEMPLATE/evidence-calibration.yml",
                     ".github/ISSUE_TEMPLATE/template-observation.yml", ".github/workflows/evidence-calibration.yml",
                     ".github/workflows/mirror-repository.yml"):
            with self.subTest(name):
                self.assertTrue(name in steps, name + " is missing from the manual steps")

    def test_the_conflict_notices_are_best_effort_and_only_use_validated_fields(self):
        text = (testsupport.HERE / "publish_issue.sh").read_text(encoding="utf-8")
        for fragment in ("--name conflict_label", "--name conflict_issues", '--body-file "$out/conflict-$rival.md"',
                         '--add-label "$conflict_label"', "::warning::"):
            with self.subTest(fragment):
                self.assertTrue(fragment in text, fragment)
        # Every value the shell takes from a result is one `field` validates; event-field gives number and login.
        read = set(re.findall(r"--name ([a-z_]+)", text))
        self.assertLessEqual(read, set(publish._FIELD_CHECKS) | {"number", "login"})
        self.assertNotIn("conflicts", read, "the shell reads the two validated fields, never the list itself")


class MirrorWorkflowSecurityTests(unittest.TestCase):
    """Publishing with GITHUB_TOKEN needs workflow_run; only trusted main may consume the mirror secret."""

    @classmethod
    def setUpClass(cls):
        cls.text = MIRROR_WORKFLOW.read_text(encoding="utf-8")

    def test_mirror_job_never_executes_the_event_head_or_untrusted_shell_expressions(self):
        for script in run_scripts(self.text):
            self.assertNotIn("${{", script)
        self.assertNotIn("head_sha", self.text)
        self.assertNotIn("pull_request_target", self.text)
        self.assertIn("ref: main", self.text)
        self.assertIn("persist-credentials: false", self.text)
        self.assertIn("contents: read", self.text)
        self.assertNotIn("contents: write", self.text)
        # N1-7: a run that pushed and then failed (or skipped its reply) is mirrored too; main is trusted either way.
        self.assertNotIn("github.event.workflow_run.conclusion", self.text)
        self.assertIn("github.event.workflow_run.head_repository.full_name == github.repository", self.text)
        self.assertIn("github.repository == 'Harendotes-Tang/MentorRecorder-Calibrations'", self.text)
        expressions = set(re.findall(r"\$\{\{\s*(.*?)\s*\}\}", self.text))
        self.assertEqual({"github.repository", "secrets.GITCODE_MIRROR_TOKEN"}, expressions)

    def test_triggers_include_every_workflow_that_pushes_main_and_periodic_reconciliation(self):
        self.assertIn("workflows: [publish-calibration, evidence-calibration, report-calibration]", self.text)
        self.assertIn("types: [completed]", self.text)
        self.assertIn("cancel-in-progress: false", self.text)
        self.assertIn("python tools/sync_refs.py --repo MentorRecorder-Calibrations", self.text)
        if yaml is None:
            for name in ("push:", "schedule:", "workflow_dispatch:", "workflow_run:"):
                self.assertIn(name, self.text)
            return
        data = yaml.safe_load(self.text)
        triggers = data.get("on", data.get(True))
        self.assertEqual({"push", "schedule", "workflow_dispatch", "workflow_run"}, set(triggers))
        self.assertEqual({"workflows": ["publish-calibration", "evidence-calibration", "report-calibration"],
                          "types": ["completed"], "branches": ["main"]},
                         triggers["workflow_run"])
        self.assertEqual({"contents": "read"}, data["permissions"])
        job = data["jobs"]["mirror"]
        self.assertIn("timeout-minutes", job)
        checkouts = [step for step in job["steps"] if step.get("uses", "").startswith("actions/checkout@")]
        self.assertEqual(1, len(checkouts))
        self.assertEqual("main", checkouts[0]["with"]["ref"])
        self.assertIs(False, checkouts[0]["with"]["persist-credentials"])

    def test_mirror_actions_use_the_already_verified_pins(self):
        used = assert_every_action_is_pinned(self, self.text, "mirror-repository.yml")
        available = assert_every_action_is_pinned(self, CI.read_text(encoding="utf-8"), "ci.yml")
        self.assertLessEqual(used, available)


class ReportWorkflowSecurityTests(unittest.TestCase):
    """report-calibration.yml: the same hardening, less power, and no way to take a calibration down."""

    @classmethod
    def setUpClass(cls):
        cls.text = REPORT_WORKFLOW.read_text(encoding="utf-8")

    def test_no_run_script_contains_an_expression_and_no_event_text_is_referenced(self):
        scripts = [script for script in run_scripts(self.text) if script.strip()]
        self.assertGreaterEqual(len(scripts), 1)
        for script in scripts:
            with self.subTest(script[:40]):
                self.assertNotIn("${{", script)
        for forbidden in ("github.event.issue.title", "github.event.issue.body", "github.event.issue.user",
                          "github.event.comment", "github.head_ref", "pull_request_target", "secrets."):
            with self.subTest(forbidden):
                self.assertNotIn(forbidden, self.text)

    def test_expressions_outside_if_conditions_are_only_trusted_values(self):
        expressions = set(re.findall(r"\$\{\{\s*(.*?)\s*\}\}", self.text))
        self.assertLessEqual(expressions, {"github.token", "github.repository", "github.event.issue.number"})

    def test_reports_share_the_atomic_evidence_ledger_and_publication_lock(self):
        self.assertIn("contents: write", self.text)
        self.assertIn("group: publish-calibration", self.text)
        self.assertIn('python -B tools/evidence_workflow.py --event "$GITHUB_EVENT_PATH"', self.text)
        if yaml is None:
            self.assertIn("permissions:\n  contents: write\n  issues: write\n", self.text)
            return
        data = yaml.safe_load(self.text)
        self.assertEqual({"contents": "write", "issues": "write"}, data["permissions"])
        self.assertEqual({"issues"}, set(data.get("on", data.get(True))))
        for job in data["jobs"].values():
            self.assertEqual(False, job["concurrency"]["cancel-in-progress"])
            self.assertIn("timeout-minutes", job)
            self.assertNotIn("permissions", job)
        condition = next(iter(data["jobs"].values()))["if"]
        for fragment in ("github.event.issue.state == 'open'",
                         "contains(github.event.issue.labels.*.name, '%s')" % publish.REPORT_LABEL):
            self.assertIn(fragment, condition)

    def test_actions_are_pinned_to_a_commit_id_and_taken_from_the_main_ci_set(self):
        used = assert_every_action_is_pinned(self, self.text, "report-calibration.yml")
        available = assert_every_action_is_pinned(self, CI.read_text(encoding="utf-8"), "ci.yml")
        self.assertLessEqual(used, available)

    def test_it_runs_the_evidence_lane_which_revokes_nothing(self):
        # N1-6: report_issue.sh is kept only for the old interface. What the workflow runs is evidence_workflow.py,
        # whose report path test_evidence.py drives end to end: one reply, left open, nothing published.
        self.assertNotIn("report_issue.sh", self.text)
        lane = (testsupport.HERE / "evidence_workflow.py").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r"\brevoke\b", lane), "the evidence lane never revokes a code")

    def test_the_maintainer_readme_describes_the_report_lane_the_workflow_runs(self):
        readme = (testsupport.HERE / "README.md").read_text(encoding="utf-8")
        for stale in ("`report-calibration.yml` 不写仓库", "**另一个**并发组", "报告没有定时补处理"):
            with self.subTest(stale):
                self.assertFalse(stale in readme, "the README still describes report_issue.sh")
        self.assertTrue("evidence_workflow.py --event" in readme, "the README names what the workflow runs")


class EvidenceWorkflowSecurityTests(unittest.TestCase):
    """N1-6: evidence-calibration.yml writes main with the token too, so it gets the same hardening checks."""

    @classmethod
    def setUpClass(cls):
        cls.text = EVIDENCE_WORKFLOW.read_text(encoding="utf-8")

    def test_no_run_script_contains_an_expression_and_no_event_text_is_referenced(self):
        scripts = [script for script in run_scripts(self.text) if script.strip()]
        self.assertGreaterEqual(len(scripts), 2)
        for script in scripts:
            with self.subTest(script[:40]):
                self.assertNotIn("${{", script)
        for forbidden in ("github.event.issue.title", "github.event.issue.body", "github.event.issue.user",
                          "github.event.comment", "github.head_ref", "pull_request_target", "secrets."):
            with self.subTest(forbidden):
                self.assertNotIn(forbidden, self.text)

    def test_expressions_outside_if_conditions_are_only_trusted_values(self):
        expressions = set(re.findall(r"\$\{\{\s*(.*?)\s*\}\}", self.text))
        self.assertEqual({"github.token", "github.repository"}, expressions)

    def test_actions_are_pinned_to_a_commit_id_and_taken_from_the_main_ci_set(self):
        used = assert_every_action_is_pinned(self, self.text, "evidence-calibration.yml")
        available = assert_every_action_is_pinned(self, CI.read_text(encoding="utf-8"), "ci.yml")
        self.assertLessEqual(used, available)

    def test_permissions_triggers_and_the_shared_concurrency_group(self):
        self.assertIn("group: publish-calibration", self.text)
        self.assertNotIn("cancel-in-progress: true", self.text)
        if yaml is None:
            self.assertIn("permissions:\n  contents: write\n  issues: write\n", self.text)
            return
        data = yaml.safe_load(self.text)
        triggers = data.get("on", data.get(True))
        self.assertEqual({"issues", "schedule", "workflow_dispatch"}, set(triggers))
        self.assertEqual(["opened", "edited", "labeled", "reopened"], triggers["issues"]["types"])
        self.assertEqual({"contents": "write", "issues": "write"}, data["permissions"])
        self.assertNotIn("concurrency", data, "a workflow-level group would let skipped events cancel pending jobs")
        for job in data["jobs"].values():
            self.assertEqual({"group": "publish-calibration", "cancel-in-progress": False}, job["concurrency"])
            self.assertIn("timeout-minutes", job)
            self.assertNotIn("permissions", job)
            for fragment in ("github.event.issue.state == 'open'",
                             "contains(github.event.issue.labels.*.name, 'calibration-evidence')",
                             "contains(github.event.issue.labels.*.name, 'template-observation')"):
                self.assertIn(fragment, job["if"])


class ReportFormTests(unittest.TestCase):
    def test_the_form_is_the_one_the_parser_reads(self):
        text = REPORT_FORM.read_text(encoding="utf-8")
        self.assertEqual(publish.REPORT_FORM, REPORT_FORM.name)
        for fragment in ("- " + publish.REPORT_LABEL, 'title: "%s"' % issue_form.REPORT_TITLE_PREFIX,
                         "label: " + issue_form.REPORT_REGION_LABEL, "label: " + issue_form.REPORT_BUILD_LABEL,
                         "label: " + issue_form.REPORT_SYMPTOM_LABEL, "label: " + issue_form.REPORT_NOTE_LABEL):
            with self.subTest(fragment):
                self.assertIn(fragment, text)
        if yaml is None:
            return
        data = yaml.safe_load(text)
        self.assertEqual(issue_form.REPORT_TITLE_PREFIX, data["title"])
        self.assertEqual([publish.REPORT_LABEL], data["labels"])
        fields = [field for field in data["body"] if field["type"] != "markdown"]
        self.assertEqual(["dropdown", "input", "dropdown", "textarea"], [field["type"] for field in fields])
        region, build, symptom, note = fields
        self.assertEqual(list(issue_form.REPORT_REGIONS), region["attributes"]["options"])
        self.assertEqual(list(issue_form.REPORT_SYMPTOMS), symptom["attributes"]["options"])
        for field in (region, build, symptom):
            with self.subTest(field["attributes"]["label"]):
                self.assertTrue(field["validations"]["required"])
        self.assertEqual(issue_form.REPORT_NOTE_LABEL, note["attributes"]["label"])
        self.assertNotIn("validations", note, "说明 is optional, so an empty one renders as _No response_")

    def test_a_body_the_form_renders_reads_back_as_the_values_it_offered(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        data = yaml.safe_load(REPORT_FORM.read_text(encoding="utf-8"))
        fields = [field for field in data["body"] if field["type"] != "markdown"]
        for region in fields[0]["attributes"]["options"]:
            for symptom in fields[2]["attributes"]["options"]:
                with self.subTest(region=region, symptom=symptom):
                    form = issue_form.parse_report(testsupport.report_body(region=region, symptom=symptom))
                    self.assertEqual((region, symptom, None), (form.region, form.symptom, form.problem))


class IssueFormTests(unittest.TestCase):
    def test_the_form_is_the_one_the_client_links_to_and_the_one_the_parser_reads(self):
        link = (SHARING / "SharedCalibrationIssueLink.cs").read_text(encoding="utf-8")
        self.assertEqual(csharp_constant(SHARING / "SharedCalibrationIssueLink.cs", "FormTemplate"), FORM.name)
        self.assertEqual(publish.ISSUE_FORM, FORM.name)
        self.assertIn('"&code="', link)
        self.assertIn('"%s"' % issue_form.TITLE_PREFIX, link)
        # The client prefills the basis input with one of these texts; any other text is refused as BASIS_INVALID,
        # so a drift between the two sides must fail here rather than refuse every share.
        self.assertIn('"&basis="', link)
        for option in (issue_form.BASIS_OPTION_CALIBRATED, issue_form.BASIS_OPTION_VERIFIED):
            with self.subTest(option):
                self.assertIn('"%s"' % option, link)
        text = FORM.read_text(encoding="utf-8")
        for fragment in ("id: %s" % publish.CODE_FIELD_ID, "label: " + issue_form.CODE_LABEL,
                         "id: %s" % publish.BASIS_FIELD_ID, "label: " + issue_form.BASIS_LABEL,
                         "placeholder: " + issue_form.BASIS_OPTION_CALIBRATED,
                         "label: " + issue_form.CONFIRM_LABEL, "- label: " + issue_form.CONFIRM_OPTION,
                         'title: "%s"' % issue_form.TITLE_PREFIX, "- " + publish.SUBMISSION_LABEL,
                         "提交同一份校准码会计入提交人数"):
            self.assertIn(fragment, text)
        self.assertEqual(2, text.count("required: true"), "the code and the confirmation; the basis stays optional")
        if yaml is None:
            return
        data = yaml.safe_load(text)
        self.assertEqual(issue_form.TITLE_PREFIX, data["title"])
        self.assertEqual([publish.SUBMISSION_LABEL], data["labels"])
        fields = [field for field in data["body"] if field["type"] != "markdown"]
        # GitHub prefills only inputs and textareas from the query string, so the basis is an input (plan section 20.2).
        self.assertEqual(["textarea", "input", "checkboxes"], [field["type"] for field in fields])
        code, basis, confirm = fields
        self.assertEqual((publish.CODE_FIELD_ID, issue_form.CODE_LABEL, True),
                         (code["id"], code["attributes"]["label"], code["validations"]["required"]))
        self.assertEqual((publish.BASIS_FIELD_ID, issue_form.BASIS_LABEL, "软件会自动填好，请不要改动。代人提交或留空时按本机校准处理。",
                          issue_form.BASIS_OPTION_CALIBRATED),
                         (basis["id"], basis["attributes"]["label"], basis["attributes"]["description"],
                          basis["attributes"]["placeholder"]))
        self.assertFalse(basis.get("validations", {}).get("required", False),
                         "a client that predates the field sends no basis, and its submissions must still be read")
        self.assertEqual(("confirm", issue_form.CONFIRM_LABEL), (confirm["id"], confirm["attributes"]["label"]))
        self.assertEqual([{"label": issue_form.CONFIRM_OPTION, "required": True}], confirm["attributes"]["options"])

    def test_a_body_the_share_form_renders_reads_back_as_the_basis_the_client_sent(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        data = yaml.safe_load(FORM.read_text(encoding="utf-8"))
        confirm = next(field for field in data["body"] if field.get("id") == "confirm")
        self.assertEqual(issue_form.CONFIRM_OPTION, confirm["attributes"]["options"][0]["label"])
        for stated, basis in ((issue_form.BASIS_OPTION_CALIBRATED, issue_form.BASIS_CALIBRATED),
                              (issue_form.BASIS_OPTION_VERIFIED, issue_form.BASIS_VERIFIED),
                              (None, issue_form.BASIS_CALIBRATED)):
            with self.subTest(stated):
                form = issue_form.parse_body(testsupport.issue_body("MRC1.x", basis=stated))
                self.assertEqual((basis, True, None), (form.basis, form.confirmed, form.problem))

    def test_blank_issues_are_disabled(self):
        text = CONFIG.read_text(encoding="utf-8")
        self.assertIn("\nblank_issues_enabled: false\n", "\n" + text)
        if yaml is not None:
            self.assertEqual({"blank_issues_enabled": False}, yaml.safe_load(text))


# Claims the client does not make good. docs/privacy-boundary.md section 8.2 is the authority: a published
# code is put to use once the login-time zone message verifies by structure, the queue and duty-entry
# messages keep being checked while it records, and what a code later contradicted recorded is marked for
# review - so a wrong code can record wrongly, or miss runs, before it is caught. Since 1.4.0 a shared
# profile in use re-reads the index; the update check (section 8.4) is a second request that is on by
# default; a maintainer's revocation now holds the accounts on the code; and the scripts that run in the
# public repository's Action do push and call the GitHub API.
FALSE_CLAIMS = (
    "不会写出错误的记录",
    "核实通过才会用来记录",
    "核实通过后才生成档案",
    "唯一出站请求",
    "不会因索引撤销而联网检查",
    "名额即被释放",
    "撤销即释放名额",
    "没有任何脚本会创建仓库、推送或调用 GitHub API",
    "never records from a code before it passes",
)


def published_texts() -> dict:
    """Everything a player or maintainer reads about what a published code does: files and replies."""
    texts = {path.relative_to(testsupport.HERE).as_posix(): path.read_text(encoding="utf-8")
             for path in sorted(PUBLIC.rglob("*")) if path.is_file()}
    for name in sync_public_repo.RUNTIME_FILES + ("README.md",):
        texts[name] = (testsupport.HERE / name).read_text(encoding="utf-8")
    described = dict(issue=1, region="CN", game_build=testsupport.BUILD, code_sha256="a" * 64,
                     match_source="ANNOUNCEMENT", submitters=2, file="cn/%s/aaaaaaaaaaaa.mrc" % testsupport.BUILD)
    for status in (publish.PUBLISHED, publish.ADDED, publish.DUPLICATE):
        texts["reply:" + status] = publish.compose_comment(publish.Result(status, **described))
        texts["reply:verified:" + status] = publish.compose_comment(publish.Result(status, basis="verified", **described))
    conflicts = ({"code": "b" * 12, "issues": [3, 4], "fields": ["换区报文", "匹配报文"]},)
    disputed = publish.Result(publish.PUBLISHED, conflicts=conflicts, **described)
    texts["reply:conflict"] = publish.compose_comment(disputed)
    texts["notice:conflict"] = publish.compose_conflict_notice(disputed, 3)
    for reason in publish._REFUSALS:
        texts["reply:refused:" + reason] = publish.compose_comment(publish.Result(publish.REFUSED, reason, **described))
    return texts


class PublicClaimTests(unittest.TestCase):
    """ON1-3: the public texts claim what the client does, and no more."""

    def test_no_text_makes_a_claim_the_client_does_not_keep(self):
        for name, text in published_texts().items():
            for claim in FALSE_CLAIMS:
                with self.subTest(text=name, claim=claim):
                    self.assertNotIn(claim, text)

    def test_the_texts_a_player_reads_say_that_records_can_be_marked_for_review(self):
        texts = published_texts()
        for name in ("public-repo/README.md", "public-repo/.github/ISSUE_TEMPLATE/share-calibration.yml",
                     "reply:" + publish.PUBLISHED):
            with self.subTest(name):
                self.assertIn("换区报文", texts[name], "the one check a published code must pass before it records")
                self.assertIn("待复核", texts[name], "what happens to records once a code is contradicted")

    def test_the_maintainer_readme_names_the_action_releases_the_workflows_are_pinned_to(self):
        readme = (testsupport.HERE / "README.md").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r"actions/[a-z-]+@v\d", readme), "the workflows reference no tag")
        for action, _, comment in action_pins(WORKFLOW.read_text(encoding="utf-8")):
            with self.subTest(action):
                self.assertIn("`%s`（%s，按提交号固定）" % (action, comment.lstrip("# ").strip()), readme)


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
        # The client words a rejection by the catalogue (contracts/collector-texts.md): each arm of
        # ShareCodeRejection.TextFor maps tokens to a CollectorTexts factory, and contracts/collector-texts.json
        # holds that factory's Chinese. The player reads the same sentence from either side, token by token.
        text = (SHARING / "ShareCodeRejection.cs").read_text(encoding="utf-8")
        constants = dict(re.findall(r'\bconst\s+string\s+(\w+)\s*=\s*"(E_SHARE_CODE_[A-Z0-9_]+)";', text))
        switch = re.search(r"\bUserText\s+TextFor\(string code\)\s*=>\s*code\s+switch\s*\{(.*?)\};", text, re.S)
        self.assertIsNotNone(switch, "ShareCodeRejection.TextFor is the token -> catalogue text switch")
        arms = re.findall(r"([\w\s]+?)\s*=>\s*CollectorTexts\.([A-Z]\w*)\(\)", switch.group(1))
        self.assertTrue(arms)
        chinese = {entry.method: entry.zh for entry in collector_texts.load_catalogue()}
        by_token, fallback = {}, None
        for names, method in arms:
            self.assertIn(method, chinese, "CollectorTexts.%s is not a catalogue key" % method)
            names = re.split(r"\s+or\s+", names.strip())
            if names == ["_"]:
                fallback = chinese[method]
                continue
            for name in names:
                self.assertIn(name, constants, "the arm names a token constant")
                self.assertNotIn(constants[name], by_token, "one arm per token")
                by_token[constants[name]] = chinese[method]
        tokens = {token for name, token in vars(sharecode).items() if name.startswith("E_")}
        self.assertEqual(tokens, set(constants.values()))
        self.assertEqual(tokens, set(by_token), "every token has its own wording")
        for token in sorted(tokens):
            with self.subTest(token):
                self.assertEqual(sharecode.MESSAGES[token], by_token[token])
        self.assertEqual(sharecode.Rejection("E_SHARE_CODE_UNKNOWN", "").message, fallback)


if __name__ == "__main__":
    unittest.main()
