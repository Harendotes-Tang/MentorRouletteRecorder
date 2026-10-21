#!/usr/bin/env python3
"""Behavioral positive/negative tests with synthetic inputs and no network."""
import copy
from contextlib import redirect_stdout
import io
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("security_quality", Path(__file__).with_name("check.py"))
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)
SHA = "a" * 40
REPO = "owner/project"
POLICY = {"schema_version": 1, "solution": "app.sln", "required_checks": ["Build", "Test"],
          "required_remote_scans": ["dependabot"], "require_configured_scans": True}


class Api:
    def __init__(self, responses):
        self.responses = responses
        self.calls = []
        self.next_endpoint = None
        self.failure_kind = None

    def get(self, endpoint):
        self.next_endpoint = None
        self.calls.append(endpoint)
        if endpoint not in self.responses:
            raise CHECK.GateError("Synthetic endpoint unavailable")
        response = self.responses[endpoint]
        if isinstance(response, Exception):
            raise response
        if len(response) == 3:
            self.next_endpoint = response[2]
            response = response[:2]
        return copy.deepcopy(response)


def url(path, page=1):
    base = f"repos/{REPO}/{path}{'&' if '?' in path else '?'}per_page=100"
    return base if page == 1 else f"{base}&after=cursor{page}"


def check(name, **kwargs):
    return dict(id=kwargs.pop("id", 1 if name == "Build" else 2), name=name,
                app=kwargs.pop("app", {"slug": "github-actions"}),
                head_sha=kwargs.pop("head_sha", SHA), status=kwargs.pop("status", "completed"),
                conclusion=kwargs.pop("conclusion", "success"), **kwargs)


class AuditTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        (self.root / "app.csproj").write_text("<Project/>", encoding="utf-8")
        (self.root / "app.sln").write_text(
            'Project("{type}") = "app", "app.csproj", "{id}"\nEndProject\n', encoding="utf-8")
        self.projects = [self.root / "app.csproj"]
        self.payload = {"version": 1, "parameters": "--vulnerable --include-transitive",
                        "sources": ["https://api.nuget.org/v3/index.json"],
                        "projects": [{"path": str(self.projects[0])}]}
        self.graph = {"version": 1, "parameters": "--include-transitive", "projects": [
            {"path": str(self.projects[0]), "frameworks": [{"framework": "net8.0", "topLevelPackages": [
                {"id": "Safe.Package", "resolvedVersion": "2.0"}]}]}]}

    def runner(self, command, root):
        payload = self.payload if "--vulnerable" in command else self.graph
        return subprocess.CompletedProcess(command, 0, json.dumps(payload) if "list" in command else "restore ok", "")

    def test_actual_clean_report_covers_all_projects(self):
        result = CHECK.local_audit(self.root, POLICY, self.runner)
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(result["evidence"]["project_count"], 1)
        self.assertTrue(result["evidence"]["inputs_sha256"]["app.csproj"])

    def test_transitive_vulnerability_fails(self):
        self.payload["projects"][0]["frameworks"] = [{"framework": "net8.0", "transitivePackages": [
            {"id": "Unsafe.Package", "resolvedVersion": "1.0", "vulnerabilities": [
                {"severity": "High", "advisoryurl": "https://github.com/advisories/EXAMPLE"}]}]}]
        result = CHECK.local_audit(self.root, POLICY, self.runner)
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["evidence"]["findings"][0]["kind"], "transitivePackages")

    def test_zero_projects_is_not_pass(self):
        self.payload["projects"] = []
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_missing_target_framework_is_not_pass(self):
        self.graph["projects"][0]["frameworks"] = []
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_empty_resolved_packages_is_not_pass(self):
        self.graph["projects"][0]["frameworks"][0]["topLevelPackages"] = []
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_unfiltered_report_cannot_substitute_for_vulnerability_query(self):
        self.payload["parameters"] = "--include-transitive"
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_malformed_parameter_type_is_machine_readable_block(self):
        self.payload["parameters"] = ["--vulnerable", "--include-transitive"]
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_wrong_project_is_not_pass(self):
        self.payload["projects"][0]["path"] = "other.csproj"
        self.assertEqual(CHECK.local_audit(self.root, POLICY, self.runner)["status"], "BLOCKED")

    def test_restore_error_does_not_query_or_persist_output(self):
        calls = []
        def runner(command, root):
            calls.append(command)
            return subprocess.CompletedProcess(command, 1, "NU1900 token=DO-NOT-LEAK", "credential=DO-NOT-LEAK")
        result = CHECK.local_audit(self.root, POLICY, runner)
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(len(calls), 1)
        self.assertNotIn("DO-NOT-LEAK", json.dumps(result))
        self.assertEqual(result["evidence"]["diagnostic_codes"], ["NU1900"])

    def test_query_error_not_empty_success(self):
        def runner(command, root):
            return subprocess.CompletedProcess(command, 1 if "list" in command else 0, "[]", "")
        self.assertEqual(CHECK.local_audit(self.root, POLICY, runner)["status"], "FAIL")

    def test_input_changed_while_query_ran(self):
        def runner(command, root):
            if "list" in command:
                (root / "app.csproj").write_text("<Project changed='true'/>")
            return self.runner(command, root)
        self.assertEqual(CHECK.local_audit(self.root, POLICY, runner)["status"], "BLOCKED")

    def test_timeout_and_malformed_json_block(self):
        def timeout(command, root):
            raise CHECK.GateError("timeout")
        def malformed(command, root):
            return subprocess.CompletedProcess(command, 0, "broken JSON", "")
        self.assertEqual(CHECK.local_audit(self.root, POLICY, timeout)["status"], "BLOCKED")
        self.assertEqual(CHECK.local_audit(self.root, POLICY, malformed)["status"], "BLOCKED")


class PolicyTests(unittest.TestCase):
    def test_strict_policy_positive(self):
        self.assertEqual(CHECK.validate_policy(copy.deepcopy(POLICY)), POLICY)

    def test_empty_duplicate_unknown_and_mistyped_rules_are_rejected(self):
        changes = [dict(schema_version=2), dict(schema_version=True), dict(required_checks=[]),
                   dict(required_checks=["Build", "Build"]), dict(required_checks=[""]),
                   dict(required_remote_scans=[]), dict(required_remote_scans=["dependabot", "invalid"]),
                   dict(required_remote_scans=["dependabot", "dependabot"]),
                   dict(require_configured_scans="false"), dict(solution="../outside.sln"),
                   dict(solution="C:\\outside.sln"), dict(solution="/outside.sln")]
        for change in changes:
            with self.subTest(change=change), self.assertRaises(CHECK.GateError):
                CHECK.validate_policy(dict(POLICY, **change))

    def test_empty_required_check_policy_cannot_make_empty_ci_pass(self):
        result = CHECK.ci_gate(Api({}), REPO, SHA, dict(POLICY, required_checks=[]))
        self.assertEqual(result["status"], "BLOCKED")


class ProcessTests(unittest.TestCase):
    def test_bounded_runner_preserves_arguments_output_and_exit_code(self):
        with tempfile.TemporaryDirectory() as folder:
            result = CHECK.run([sys.executable, "-X", "utf8", "-c",
                                "import sys; print(sys.argv[1]); print('diagnostic', file=sys.stderr); sys.exit(7)",
                                '含空格 path "quoted"'], Path(folder), 15)
        self.assertEqual(result.returncode, 7)
        self.assertIn('含空格 path "quoted"', result.stdout)
        self.assertIn("diagnostic", result.stderr)

    def test_inherited_descendant_pipes_cannot_extend_timeout(self):
        # The descendants sleep 60 s and the bound is 30 s (N2-9, audit 2026-10-20): a timeout
        # extended by an inherited pipe ends after 60 s, while an honoured one ends after 1 s plus
        # the Windows dispatcher's own start-up (pwsh, Add-Type of the job helper, job cleanup),
        # which took about 4 s on an idle machine and is no business of this assertion.
        script = ("import subprocess,sys,time; "
                  "subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
                  "time.sleep(60)")
        started = time.monotonic()
        with tempfile.TemporaryDirectory() as folder, self.assertRaises(CHECK.GateError):
            CHECK.run([sys.executable, "-c", script], Path(folder), 1)
        self.assertLess(time.monotonic() - started, 30.0)


class RemoteTests(unittest.TestCase):
    def ci_api(self, checks):
        return Api({url(f"commits/{SHA}/check-runs?filter=latest"):
                    (200, {"total_count": len(checks), "check_runs": checks})})

    def test_exact_commit_required_checks_pass(self):
        self.assertEqual(CHECK.ci_gate(self.ci_api([check("Build"), check("Test")]), REPO, SHA, POLICY)["status"], "PASS")

    def test_empty_checks_missing_job_skipped_and_pending_do_not_pass(self):
        for checks in ([], [check("Build")], [check("Build"), check("Test", status="in_progress")],
                       [check("Build"), check("Test", conclusion="skipped")]):
            with self.subTest(checks=checks):
                self.assertNotEqual(CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)["status"], "PASS")

    def test_wrong_commit_blocks(self):
        checks = [check("Build", head_sha="b" * 40), check("Test")]
        self.assertEqual(CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)["status"], "BLOCKED")

    def test_malformed_ci_context_is_blocked(self):
        checks = [check("Build"), check("Test")]
        checks[0]["name"] = {"unexpected": "object"}
        self.assertEqual(CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)["status"], "BLOCKED")

    def test_duplicate_workflows_select_latest_actions_run_not_mirror(self):
        checks = [check("Build", conclusion="failure"), check("Build", id=3), check("Test"),
                  check("Build", id=4, app={"slug": "mirror"}, conclusion="failure")]
        result = CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(next(item for item in result["evidence"]["checks"] if item["name"] == "Build")["id"], 3)
        checks[1]["conclusion"] = "failure"
        self.assertEqual(CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)["status"], "FAIL")

    def test_mirror_check_cannot_fill_missing_github_actions_job(self):
        checks = [check("Build"), check("Test", app={"slug": "mirror"})]
        result = CHECK.ci_gate(self.ci_api(checks), REPO, SHA, POLICY)
        self.assertEqual(result["status"], "NOT_RUN")
        self.assertEqual(result["evidence"]["missing_checks"], ["Test"])

    def test_page_two_alert_is_seen(self):
        first = [{"number": n, "state": "open"} for n in range(1, 101)]
        api = Api({url("dependabot/alerts?state=open"): (200, first, url("dependabot/alerts?state=open", 2)),
                   url("dependabot/alerts?state=open", 2): (200, [{"number": 101, "state": "open"}])})
        result = CHECK.scan_gate(api, REPO, "dependabot", True, None, SHA)
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["evidence"]["open_count"], 101)
        self.assertEqual(len(api.calls), 2)

    def test_failure_after_first_page_does_not_return_partial_success(self):
        api = Api({url("dependabot/alerts?state=open"): (200, [{"number": n, "state": "open"} for n in range(100)], url("dependabot/alerts?state=open", 2)),
                   url("dependabot/alerts?state=open", 2): (403, None)})
        self.assertEqual(CHECK.scan_gate(api, REPO, "dependabot", True, None, SHA)["status"], "BLOCKED")

    def test_repeated_pages_or_malformed_list_block(self):
        page = [{"number": n, "state": "open"} for n in range(100)]
        for second in (page, {"unexpected": []}):
            api = Api({url("dependabot/alerts?state=open"): (200, page, url("dependabot/alerts?state=open", 2)),
                       url("dependabot/alerts?state=open", 2): (200, second)})
            self.assertEqual(CHECK.scan_gate(api, REPO, "dependabot", True, None, SHA)["status"], "BLOCKED")

    def test_unknown_404_and_permission_403_are_blocked(self):
        for status in (403, 404, 429, 500):
            api = Api({url("secret-scanning/alerts?state=open"): (status, None)})
            result = CHECK.scan_gate(api, REPO, "secret-scanning", True, None, SHA)
            self.assertEqual(result["status"], "BLOCKED")
            self.assertEqual(result["configuration"], "UNKNOWN")

    def test_explicitly_disabled_secret_scan_is_not_configured(self):
        result = CHECK.scan_gate(Api({}), REPO, "secret-scanning", False, "disabled", SHA)
        self.assertEqual((result["status"], result["configuration"]), ("NOT_RUN", "NOT_CONFIGURED"))

    def test_code_scan_empty_alerts_without_exact_analysis_not_pass(self):
        for analyses in ([], [{"id": 1, "commit_sha": "b" * 40, "error": ""}]):
            api = Api({url("code-scanning/alerts?state=open"): (200, []),
                       url("code-scanning/analyses"): (200, analyses)})
            self.assertEqual(CHECK.scan_gate(api, REPO, "code-scanning", True, None, SHA)["status"], "NOT_RUN")

    def test_code_scan_exact_analysis_and_clean_alerts_pass(self):
        api = Api({url("code-scanning/alerts?state=open"): (200, []),
                   url("code-scanning/analyses"): (200, [{"id": 1, "commit_sha": SHA, "ref": "refs/heads/dev", "error": "", "warning": ""}]),
                   f"repos/{REPO}/commits/refs%2Fheads%2Fdev": (200, {"sha": SHA}),
                   url("code-scanning/alerts?state=open&ref=refs%2Fheads%2Fdev"): (200, [])})
        self.assertEqual(CHECK.scan_gate(api, REPO, "code-scanning", True, "enabled", SHA)["status"], "PASS")

    def test_nondefault_ref_alert_cannot_hide_behind_clean_default_branch(self):
        api = Api({url("code-scanning/alerts?state=open"): (200, []),
                   url("code-scanning/analyses"): (200, [{"id": 1, "commit_sha": SHA, "ref": "refs/heads/dev", "results_count": 1}]),
                   f"repos/{REPO}/commits/refs%2Fheads%2Fdev": (200, {"sha": SHA}),
                   url("code-scanning/alerts?state=open&ref=refs%2Fheads%2Fdev"):
                   (200, [{"number": 5, "state": "open", "most_recent_instance": {"ref": "refs/heads/dev", "state": "open"}}])})
        result = CHECK.scan_gate(api, REPO, "code-scanning", True, "enabled", SHA)
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["evidence"]["open_count"], 1)

    def test_code_analysis_results_count_is_not_open_alert_count(self):
        api = Api({url("code-scanning/alerts?state=open"): (200, []),
                   url("code-scanning/analyses"): (200, [{"id": 1, "commit_sha": SHA, "ref": "refs/heads/dev", "results_count": 99}]),
                   f"repos/{REPO}/commits/refs%2Fheads%2Fdev": (200, {"sha": SHA}),
                   url("code-scanning/alerts?state=open&ref=refs%2Fheads%2Fdev"): (200, [])})
        self.assertEqual(CHECK.scan_gate(api, REPO, "code-scanning", True, "enabled", SHA)["status"], "PASS")

    def test_moved_analysis_ref_cannot_prove_requested_commit(self):
        api = Api({url("code-scanning/alerts?state=open"): (200, []),
                   url("code-scanning/analyses"): (200, [{"id": 1, "commit_sha": SHA, "ref": "refs/heads/dev"}]),
                   f"repos/{REPO}/commits/refs%2Fheads%2Fdev": (200, {"sha": "b" * 40})})
        self.assertEqual(CHECK.scan_gate(api, REPO, "code-scanning", True, "enabled", SHA)["status"], "BLOCKED")

    def test_no_default_setup_and_no_custom_analysis_is_not_configured(self):
        api = Api({url("code-scanning/alerts?state=open"): (404, None),
                   f"repos/{REPO}/code-scanning/default-setup": (200, {"state": "not-configured"}),
                   url("code-scanning/analyses"): (200, [])})
        result = CHECK.scan_gate(api, REPO, "code-scanning", False, None, SHA)
        self.assertEqual((result["status"], result["configuration"]), ("NOT_RUN", "NOT_CONFIGURED"))

    def test_secret_and_error_messages_never_persist(self):
        secret = "DO-NOT-PERSIST-SECRET-OR-TOKEN"
        api = Api({url("secret-scanning/alerts?state=open"): (200, [
            {"number": 7, "state": "open", "secret": secret, "secret_type": secret,
             "message": secret, "html_url": "https://github.com/owner/project/security/secret-scanning/7"}])})
        result = CHECK.scan_gate(api, REPO, "secret-scanning", True, "enabled", SHA)
        self.assertEqual(result["status"], "FAIL")
        self.assertNotIn(secret, json.dumps(result))

    def test_gh_nonzero_cannot_be_overridden_by_http200_empty_body(self):
        response = subprocess.CompletedProcess([], 1, "HTTP/2.0 200 OK\n\n[]", "token scope required")
        with patch.object(CHECK, "run", return_value=response):
            with self.assertRaises(CHECK.GateError):
                CHECK.GhApi(Path.cwd()).get("repos/owner/project/secret-scanning/alerts")

    def test_gh_invalid_json_and_timeout_fail_closed(self):
        with patch.object(CHECK, "run", return_value=subprocess.CompletedProcess([], 0, "HTTP/2.0 200 OK\n\nbad", "")):
            with self.assertRaises(CHECK.GateError):
                CHECK.GhApi(Path.cwd()).get("repos/owner/project")

    def test_gh_cursor_link_is_followed_without_synthesizing_page_number(self):
        endpoint = "repos/owner/project/dependabot/alerts?state=open&per_page=100"
        target = "repos/owner/project/dependabot/alerts?state=open&per_page=100&after=opaque-cursor"
        first = "HTTP/2.0 200 OK\nLink: <https://api.github.com/" + target + '>; rel="next"\n\n' + json.dumps([{"number": 1}])
        second = "HTTP/2.0 200 OK\n\n[]"
        with patch.object(CHECK, "run", side_effect=[subprocess.CompletedProcess([], 0, first, ""),
                                                    subprocess.CompletedProcess([], 0, second, "")]) as runner:
            status, entries = CHECK.pages(CHECK.GhApi(Path.cwd()), "repos/owner/project/dependabot/alerts?state=open")
        self.assertEqual((status, len(entries)), (200, 1))
        self.assertEqual(runner.call_args_list[1].args[0][-1], target)
        self.assertNotIn("page=", target.replace("per_page=", ""))

    def test_pagination_cannot_send_authenticated_request_to_other_host(self):
        response = 'HTTP/2.0 200 OK\nLink: <https://attacker.invalid/steal>; rel="next"\n\n[]'
        with patch.object(CHECK, "run", return_value=subprocess.CompletedProcess([], 0, response, "")):
            with self.assertRaises(CHECK.GateError):
                CHECK.GhApi(Path.cwd()).get("repos/owner/project/secret-scanning/alerts")

    def test_canonical_repository_id_link_requires_verified_same_repository(self):
        repo_response = 'HTTP/2.0 200 OK\n\n{"id":7,"full_name":"owner/project"}'
        for repository_id, allowed in ((7, True), (8, False)):
            with self.subTest(repository_id=repository_id):
                linked = ('HTTP/2.0 200 OK\nLink: <https://api.github.com/repositories/' + str(repository_id) +
                          '/dependabot/alerts?after=cursor>; rel="next"\n\n[{"number":1}]')
                with patch.object(CHECK, "run", side_effect=[subprocess.CompletedProcess([], 0, repo_response, ""),
                                                            subprocess.CompletedProcess([], 0, linked, "")]):
                    api = CHECK.GhApi(Path.cwd())
                    api.get("repos/owner/project")
                    if allowed:
                        api.get("repos/owner/project/dependabot/alerts?per_page=100")
                        self.assertEqual(api.next_endpoint, "repositories/7/dependabot/alerts?after=cursor")
                    else:
                        with self.assertRaises(CHECK.GateError):
                            api.get("repos/owner/project/dependabot/alerts?per_page=100")

    def test_404_message_only_classifies_no_analysis_for_code_scanning(self):
        response = 'HTTP/2.0 404 Not Found\n\n{"message":"no analysis found"}'
        with patch.object(CHECK, "run", return_value=subprocess.CompletedProcess([], 1, response, "")):
            api = CHECK.GhApi(Path.cwd())
            self.assertEqual(api.get("repos/owner/project/code-scanning/analyses")[0], 404)
            self.assertEqual(api.failure_kind, "NO_CODE_ANALYSIS")
            api.get("repos/owner/project/secret-scanning/alerts")
            self.assertIsNone(api.failure_kind)

    def test_pre_push_allows_fixing_push_but_release_requires_existing_scans(self):
        responses = {f"repos/{REPO}": (200, {"full_name": REPO, "security_and_analysis": {
            "secret_scanning": {"status": "enabled"}}}),
            url("dependabot/alerts?state=open"): (200, [{"number": 1, "state": "open"}]),
            url("code-scanning/alerts?state=open"): (404, None),
            url("secret-scanning/alerts?state=open"): (403, None),
            url(f"commits/{SHA}/check-runs?filter=latest"): (200, {"total_count": 2,
            "check_runs": [check("Build"), check("Test")]})}
        before = CHECK.remote_gates(Api(responses), REPO, SHA, "pre-push", POLICY, [])
        self.assertFalse(any(item["required"] for item in before))
        after = CHECK.remote_gates(Api(responses), REPO, SHA, "release", POLICY, [])
        by_name = {item["name"]: item for item in after}
        self.assertTrue(by_name["dependabot"]["required"])
        self.assertTrue(by_name["secret-scanning"]["required"])
        self.assertFalse(by_name["code-scanning"]["required"])
        self.assertTrue(any(item["required"] and item["status"] != "PASS" for item in after))

    def test_explicit_extra_scan_requirement_does_not_weaken_default_policy(self):
        responses = {f"repos/{REPO}": (200, {"full_name": REPO}),
                     url("dependabot/alerts?state=open"): (200, []),
                     url("code-scanning/alerts?state=open"): (404, None),
                     url("secret-scanning/alerts?state=open"): (404, None),
                     url(f"commits/{SHA}/check-runs?filter=latest"): (200, {"total_count": 2,
                     "check_runs": [check("Build"), check("Test")]})}
        after = CHECK.remote_gates(Api(responses), REPO, SHA, "release", POLICY, ["code-scanning"])
        self.assertTrue(next(item for item in after if item["name"] == "code-scanning")["required"])
        self.assertTrue(next(item for item in after if item["name"] == "dependabot")["required"])

    def test_hidden_security_configuration_and_403_cannot_allow_release(self):
        responses = {f"repos/{REPO}": (200, {"full_name": REPO}),
                     url("dependabot/alerts?state=open"): (200, []),
                     url("code-scanning/alerts?state=open"): (403, None),
                     url("secret-scanning/alerts?state=open"): (403, None),
                     url(f"commits/{SHA}/check-runs?filter=latest"): (200, {"total_count": 2,
                     "check_runs": [check("Build"), check("Test")]})}
        results = CHECK.remote_gates(Api(responses), REPO, SHA, "release", POLICY, [])
        discovery = next(item for item in results if item["name"] == "github-security-configuration")
        self.assertEqual((discovery["status"], discovery["required"]), ("BLOCKED", True))
        self.assertEqual(discovery["evidence"]["unknown_scans"], ["code-scanning", "secret-scanning"])
        before = CHECK.remote_gates(Api(responses), REPO, SHA, "pre-push", POLICY, [])
        self.assertFalse(any(item["required"] for item in before))

    def test_actual_report_file_preserves_denial_and_redacts_secret(self):
        secret = "NEVER-WRITE-THIS-SECRET"
        real_policy = json.loads(Path(__file__).with_name("policy.json").read_text(encoding="utf-8"))
        checks = [check(name, id=index + 1) for index, name in enumerate(real_policy["required_checks"])]
        responses = {f"repos/{REPO}": (200, {"full_name": REPO, "security_and_analysis": {
            "secret_scanning": {"status": "enabled"}}}),
            url(f"commits/{SHA}/check-runs?filter=latest"): (200, {"total_count": len(checks), "check_runs": checks}),
            url("dependabot/alerts?state=open"): (200, []),
            url("code-scanning/alerts?state=open"): (404, None),
            url("secret-scanning/alerts?state=open"): (200, [{"number": 1, "state": "open", "secret": secret}])}
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "unique-run"
            identity = {"git_head": SHA, "dirty": False, "source_inputs_sha256": "f" * 64, "source_file_count": 1}
            stdout = io.StringIO()
            with redirect_stdout(stdout), patch.object(CHECK, "source_identity", return_value=identity), \
                    patch.object(CHECK, "GhApi", return_value=Api(responses)):
                result = CHECK.main(["--phase", "release", "--skip-local", "--repository", REPO,
                                     "--output-dir", str(output)])
            self.assertEqual(result, 1)
            text = (output / "report.json").read_text(encoding="utf-8")
            self.assertNotIn(secret, text)
            self.assertNotIn(secret, stdout.getvalue())
            report = json.loads(text)
            self.assertEqual(report["decision"], "DENY")
            self.assertEqual(next(item for item in report["gates"] if item["name"] == "secret-scanning")["status"], "FAIL")

    def test_existing_evidence_directory_cannot_be_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            marker = Path(folder) / "report.json"
            marker.write_text("existing evidence", encoding="utf-8")
            with self.assertRaises(FileExistsError):
                CHECK.main(["--phase", "local", "--skip-local", "--output-dir", folder])
            self.assertEqual(marker.read_text(encoding="utf-8"), "existing evidence")

    def test_remote_only_release_never_returns_full_allow(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "remote-run"
            identity = {"git_head": SHA, "dirty": False, "source_inputs_sha256": "f" * 64, "source_file_count": 1}
            observations = [CHECK.gate("github-ci", "PASS", True, "Synthetic current commit"),
                            CHECK.gate("dependabot", "PASS", True, "Synthetic zero alerts")]
            with redirect_stdout(io.StringIO()), patch.object(CHECK, "source_identity", return_value=identity), \
                    patch.object(CHECK, "remote_gates", return_value=observations):
                result = CHECK.main(["--phase", "release", "--skip-local", "--repository", REPO,
                                     "--output-dir", str(output)])
            report = json.loads((output / "report.json").read_text(encoding="utf-8"))
            self.assertEqual((result, report["decision"]), (1, "REMOTE_ONLY"))
            self.assertIn("remote commit observations only", report["decision_scope"])

    def test_combined_release_rejects_dirty_or_different_local_commit(self):
        for dirty, observed_commit in ((True, SHA), (False, "b" * 40)):
            with self.subTest(dirty=dirty, commit=observed_commit), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "combined-run"
                identity = {"git_head": SHA, "dirty": dirty, "source_inputs_sha256": "f" * 64, "source_file_count": 1}
                with redirect_stdout(io.StringIO()), patch.object(CHECK, "source_identity", return_value=identity), \
                        patch.object(CHECK, "local_audit", return_value=CHECK.gate("nuget-audit", "PASS", True, "Synthetic local graph")), \
                        patch.object(CHECK, "remote_gates", return_value=[CHECK.gate("github-ci", "PASS", True, "Synthetic remote")]):
                    result = CHECK.main(["--phase", "release", "--repository", REPO,
                                         "--commit", observed_commit, "--output-dir", str(output)])
                report = json.loads((output / "report.json").read_text(encoding="utf-8"))
                self.assertEqual((result, report["decision"]), (1, "DENY"))
                self.assertTrue(next(item for item in report["gates"] if item["name"] == "local-remote-identity")["required"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
