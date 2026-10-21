#!/usr/bin/env python3
"""Executable local/remote gates; GitHub data is projected before any persistence.

Only standard-library modules and the project's existing dotnet/git/gh executables
are used. A report is an observation, not a remote policy change or publication.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
from typing import Any, Callable
from urllib.parse import quote, urlsplit, urlunsplit
import uuid


class GateError(Exception):
    """Safe fixed diagnostic; never include external response text in this error."""


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def run(command: list[str], root: Path, timeout: int = 300) -> subprocess.CompletedProcess:
    try:
        environment = os.environ.copy()
        if Path(command[0]).name.lower() in ("dotnet", "dotnet.exe"):
            # This operation owns only its children. Do not attach to a pre-existing
            # MSBuild server, or leave reusable nodes keeping its job alive afterward.
            environment.update(MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1")
        if os.name == "nt":
            dispatcher = Path(__file__).resolve().parents[2] / "scripts/run-bounded-process.ps1"
            executable = shutil.which(command[0])
            if not dispatcher.is_file() or executable is None:
                raise GateError("Bounded process dispatcher or executable unavailable")
            request = dict(executable=executable, arguments=command[1:],
                           working_directory=str(root), timeout_seconds=timeout)
            response = subprocess.run(["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(dispatcher)],
                                      input=json.dumps(request), cwd=root, capture_output=True, text=True,
                                      encoding="utf-8", errors="replace", timeout=timeout + 30, check=False,
                                      env=environment)
            if response.returncode:
                raise GateError("Bounded process dispatcher failed")
            payload = json.loads(response.stdout)
            if not isinstance(payload, dict) or type(payload.get("ExitCode")) is not int or type(payload.get("TimedOut")) is not bool or not isinstance(payload.get("Stdout"), str) or not isinstance(payload.get("Stderr"), str):
                raise GateError("Bounded process dispatcher returned an invalid result")
            if payload["TimedOut"]:
                raise GateError("Command process tree timed out")
            return subprocess.CompletedProcess(command, payload["ExitCode"], payload["Stdout"], payload["Stderr"])
        # A process group closes the same inherited-pipe hole on POSIX: killing only the
        # initial process leaves a descendant holding communicate()'s pipes open.
        child = subprocess.Popen(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 text=True, encoding="utf-8", errors="replace", start_new_session=True,
                                 env=environment)
        try:
            stdout, stderr = child.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid, signal.SIGKILL)
            child.communicate(timeout=5)
            raise GateError("Command process group timed out") from None
        return subprocess.CompletedProcess(command, child.returncode, stdout, stderr)
    except (OSError, subprocess.TimeoutExpired, ValueError):
        raise GateError("Command unavailable or timed out") from None


def gate(name: str, status: str, required: bool, detail: str,
         configuration: str = "CONFIGURED", **evidence: Any) -> dict:
    return dict(name=name, status=status, required=required, configuration=configuration,
                detail=detail, evidence=evidence)


def safe_url(value: Any) -> str | None:
    """Drop credentials/query/fragment; do not persist arbitrary response strings."""
    if not isinstance(value, str):
        return None
    try:
        parts = urlsplit(value)
    except ValueError:
        return None
    if parts.scheme != "https" or not parts.hostname:
        return None
    return urlunsplit(("https", parts.hostname, parts.path, "", ""))


def validate_policy(policy: Any) -> dict:
    """Reject damaged policy rather than interpreting empty/mistyped rules as green."""
    keys = {"schema_version", "solution", "required_checks", "required_remote_scans", "require_configured_scans"}
    if not isinstance(policy, dict) or set(policy) != keys or type(policy["schema_version"]) is not int or policy["schema_version"] != 1:
        raise GateError("Unsupported or incomplete security policy schema")
    solution = policy["solution"]
    if not isinstance(solution, str) or not solution.endswith(".sln") or "\0" in solution or solution.startswith(("/", "\\")) or re.match(r"^[A-Za-z]:", solution):
        raise GateError("Security policy solution must be a safe relative .sln path")
    path = Path(solution.replace("\\", "/"))
    if path.is_absolute() or any(part in (".", "..") for part in path.parts) or not path.parts:
        raise GateError("Security policy solution must stay inside this checkout")
    checks = policy["required_checks"]
    if not isinstance(checks, list) or not checks or any(not isinstance(check, str) or not check.strip() or check != check.strip() for check in checks) or len(set(checks)) != len(checks):
        raise GateError("Security policy required checks must be nonempty unique names")
    scans = policy["required_remote_scans"]
    if not isinstance(scans, list) or any(scan not in ("dependabot", "code-scanning", "secret-scanning") for scan in scans) or len(set(scans)) != len(scans) or "dependabot" not in scans:
        raise GateError("Security policy scan list must be valid and include Dependabot")
    if type(policy["require_configured_scans"]) is not bool:
        raise GateError("Security policy configured-scan requirement must be a boolean")
    return policy


def solution_projects(root: Path, solution: str) -> list[Path]:
    path = root / solution
    entries = re.findall(r'^Project\([^\n]+?=\s*"[^"\n]+",\s*"([^"\n]+\.csproj)"',
                         path.read_text(encoding="utf-8-sig"), re.MULTILINE)
    projects = [(root / entry.replace("\\", "/")).resolve() for entry in entries]
    if not projects or len(set(projects)) != len(projects):
        raise GateError("Solution project discovery is empty or duplicated")
    if any(not project.is_relative_to(root.resolve()) or not project.is_file()
           for project in projects):
        raise GateError("Solution project is missing or outside this checkout")
    return projects


def audit_payload(payload: dict, expected_projects: list[Path], root: Path) -> dict:
    """Validate actual dotnet JSON coverage, not merely the process exit code."""
    if not isinstance(payload, dict) or payload.get("version") != 1:
        raise GateError("Unexpected dependency audit schema")
    if not isinstance(payload.get("parameters"), str):
        raise GateError("Dependency audit parameters are malformed")
    parameters = payload["parameters"].split()
    if "--vulnerable" not in parameters or "--include-transitive" not in parameters:
        raise GateError("Dependency audit did not include transitive vulnerability results")
    projects, sources = payload.get("projects"), payload.get("sources")
    if not isinstance(projects, list) or not isinstance(sources, list) or not sources:
        raise GateError("Dependency audit has no project coverage or package sources")
    found: set[Path] = set()
    findings = []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str):
            raise GateError("Dependency audit project is malformed")
        path = Path(project["path"].replace("\\", "/"))
        path = path.resolve() if path.is_absolute() else (root / path).resolve()
        if path in found:
            raise GateError("Dependency audit duplicated a project")
        found.add(path)
        # .NET 8 omits the frameworks member entirely for a project with no vulnerable
        # packages. Coverage/framework existence is separately proved by resolved_graph.
        frameworks = project.get("frameworks", [])
        if not isinstance(frameworks, list) or "frameworks" in project and not frameworks:
            raise GateError("Dependency audit target framework list is malformed")
        for framework in frameworks:
            if not isinstance(framework, dict) or not framework.get("framework"):
                raise GateError("Dependency audit target framework is malformed")
            for kind in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(kind, [])
                if not isinstance(packages, list):
                    raise GateError("Dependency audit package list is malformed")
                for package in packages:
                    vulnerabilities = package.get("vulnerabilities") if isinstance(package, dict) else None
                    if not isinstance(vulnerabilities, list) or not vulnerabilities:
                        raise GateError("Vulnerable-package report contains an unclassified package")
                    findings.append(dict(project=path.relative_to(root.resolve()).as_posix(),
                                         framework=framework["framework"], kind=kind,
                                         package=package.get("id"), version=package.get("resolvedVersion"),
                                         vulnerabilities=[dict(severity=item.get("severity"),
                                             advisory_url=safe_url(item.get("advisoryurl")))
                                             for item in vulnerabilities if isinstance(item, dict)]))
    if found != set(expected_projects):
        raise GateError("Dependency audit does not cover exactly the solution's projects")
    return dict(project_count=len(found), vulnerable_framework_count=sum(len(p.get("frameworks", [])) for p in projects),
                sources=[safe_url(source) for source in sources], findings=findings)


def resolved_graph(payload: dict, expected_projects: list[Path], root: Path) -> dict:
    """Prove nonempty actual framework/package coverage independently of the filter."""
    if not isinstance(payload, dict) or payload.get("version") != 1:
        raise GateError("Unexpected resolved dependency graph schema")
    if not isinstance(payload.get("parameters"), str):
        raise GateError("Resolved dependency graph parameters are malformed")
    parameters = payload["parameters"].split()
    if "--include-transitive" not in parameters or "--vulnerable" in parameters:
        raise GateError("Full resolved dependency graph was not queried")
    projects = payload.get("projects")
    if not isinstance(projects, list):
        raise GateError("Resolved dependency graph has no projects")
    found, records = set(), []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str):
            raise GateError("Resolved dependency graph project is malformed")
        path = Path(project["path"].replace("\\", "/"))
        path = path.resolve() if path.is_absolute() else (root / path).resolve()
        if path not in expected_projects or path in found:
            raise GateError("Resolved dependency graph project identity is incomplete or duplicated")
        found.add(path)
        frameworks = project.get("frameworks")
        if not isinstance(frameworks, list) or not frameworks:
            raise GateError("Resolved dependency graph has no target framework")
        for framework in frameworks:
            if not isinstance(framework, dict) or not framework.get("framework"):
                raise GateError("Resolved dependency graph target framework is malformed")
            packages = []
            for kind in ("topLevelPackages", "transitivePackages"):
                entries = framework.get(kind, [])
                if not isinstance(entries, list):
                    raise GateError("Resolved dependency package list is malformed")
                for package in entries:
                    if not isinstance(package, dict) or not package.get("id") or not package.get("resolvedVersion"):
                        raise GateError("Resolved dependency package has no identity/version")
                    packages.append(dict(kind=kind, package=package["id"], version=package["resolvedVersion"]))
            if not packages:
                raise GateError("Resolved dependency graph framework is unexpectedly empty")
            records.append(dict(project=path.relative_to(root.resolve()).as_posix(),
                                framework=framework["framework"], packages=sorted(packages, key=lambda x: (x["kind"], x["package"]))))
    if found != set(expected_projects):
        raise GateError("Resolved dependency graph does not cover every solution project")
    records.sort(key=lambda x: (x["project"], x["framework"]))
    return dict(framework_count=len(records), package_entries=sum(len(r["packages"]) for r in records),
                resolved_graph_sha256=hashlib.sha256(json.dumps(records, sort_keys=True).encode("utf-8")).hexdigest(),
                resolved_graph=records)


def local_audit(root: Path, policy: dict, runner: Callable = run) -> dict:
    try:
        projects = solution_projects(root, policy["solution"])
        inputs = [root / policy["solution"], *projects,
                  *[path for path in (root / "Directory.Build.props", root / "Directory.Build.targets",
                                     root / "NuGet.Config") if path.is_file()]]
        hashes = {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in inputs}
        restore = runner(["dotnet", "restore", policy["solution"], "--force-evaluate", "--disable-build-servers",
                          "-p:NuGetAudit=true", "-p:NuGetAuditMode=all", "-p:NuGetAuditLevel=low",
                          "-p:TreatWarningsAsErrors=true"], root)
        if restore.returncode:
            return gate("nuget-audit", "FAIL", True, "Audited restore failed; no clean graph proved",
                        exit_code=restore.returncode,
                        diagnostic_codes=sorted(set(re.findall(r"\bNU\d{4}\b", restore.stdout + restore.stderr))),
                        inputs_sha256=hashes)
        graph_result = runner(["dotnet", "list", policy["solution"], "package", "--include-transitive",
                               "--format", "json", "--output-version", "1"], root)
        if graph_result.returncode:
            return gate("nuget-audit", "FAIL", True, "Full resolved package graph query failed",
                        exit_code=graph_result.returncode, inputs_sha256=hashes)
        graph = resolved_graph(json.loads(graph_result.stdout), projects, root)
        listed = runner(["dotnet", "list", policy["solution"], "package", "--vulnerable",
                         "--include-transitive", "--format", "json", "--output-version", "1"], root)
        if listed.returncode:
            return gate("nuget-audit", "FAIL", True, "Vulnerability query failed; missing results are not PASS",
                        exit_code=listed.returncode, inputs_sha256=hashes)
        summary = audit_payload(json.loads(listed.stdout), projects, root)
        if any(hashlib.sha256(path.read_bytes()).hexdigest() != hashes[path.relative_to(root).as_posix()]
               for path in inputs):
            raise GateError("Dependency inputs changed during audit")
        return gate("nuget-audit", "FAIL" if summary["findings"] else "PASS", True,
                    "Current solution restore and direct/transitive NuGet vulnerability query",
                    inputs_sha256=hashes, **graph, **summary)
    except GateError as error:
        return gate("nuget-audit", "BLOCKED", True, str(error))
    except (ValueError, KeyError, OSError):
        return gate("nuget-audit", "BLOCKED", True,
                    "Dependency audit unavailable, malformed, incomplete or inputs changed")


class GhApi:
    """Use gh's existing authentication without reading or printing its credentials."""
    def __init__(self, root: Path):
        self.root = root
        self.next_endpoint = None
        self.failure_kind = None
        self.repository_aliases = {}

    def get(self, endpoint: str) -> tuple[int, Any]:
        self.next_endpoint = None
        self.failure_kind = None
        result = run(["gh", "api", "--hostname", "github.com", "--include", "--method", "GET",
                      "-H", "Accept: application/vnd.github+json",
                      "-H", "X-GitHub-Api-Version: 2022-11-28", endpoint], self.root, timeout=45)
        # Never forward gh stderr, response bodies, or exception messages to reports.
        parts = re.split(r"\r?\n\r?\n", result.stdout, maxsplit=1)
        match = re.match(r"HTTP/\S+\s+(\d{3})", parts[0])
        if not match or len(parts) != 2:
            raise GateError("GitHub API response unavailable")
        status = int(match.group(1))
        if result.returncode and status == 200:
            # gh can print an empty body and still reject an insufficient token scope.
            # A successful-looking body cannot override the actual CLI failure.
            raise GateError("GitHub CLI rejected the request")
        if status != 200:
            if status == 404 and "/code-scanning/" in endpoint:
                try:
                    failure = json.loads(parts[1])
                    if isinstance(failure, dict) and str(failure.get("message", "")).lower() == "no analysis found":
                        self.failure_kind = "NO_CODE_ANALYSIS"
                except ValueError:
                    pass
            return status, None
        links = re.findall(r"(?im)^link:\s*(.+)$", parts[0])
        next_links = re.findall(r'<([^>]+)>;\s*rel="next"', ",".join(links))
        if len(next_links) > 1:
            raise GateError("GitHub pagination has multiple next links")
        if next_links:
            parsed = urlsplit(next_links[0])
            expected_path = endpoint.split("?", 1)[0]
            allowed_paths = {expected_path}
            for named, numeric in self.repository_aliases.items():
                if expected_path.startswith(named + "/"):
                    allowed_paths.add(numeric + expected_path[len(named):])
                if expected_path.startswith(numeric + "/"):
                    allowed_paths.add(named + expected_path[len(numeric):])
            if parsed.scheme != "https" or parsed.netloc != "api.github.com" or parsed.path.lstrip("/") not in allowed_paths:
                raise GateError("GitHub pagination left the requested API resource")
            self.next_endpoint = parsed.path.lstrip("/") + "?" + parsed.query
        try:
            payload = json.loads(parts[1])
            # GitHub sometimes emits canonical /repositories/<id>/... links even
            # when the original request used /repos/<owner>/<name>/.... Accept the
            # alias only after this exact repository identity was read successfully.
            match_repo = re.fullmatch(r"repos/([\w.-]+/[\w.-]+)", endpoint)
            if match_repo and isinstance(payload, dict) and isinstance(payload.get("full_name"), str) and payload["full_name"].lower() == match_repo.group(1).lower() and type(payload.get("id")) is int and payload["id"] > 0:
                self.repository_aliases[endpoint] = f"repositories/{payload['id']}"
            return status, payload
        except ValueError:
            raise GateError("GitHub API returned invalid JSON") from None


def pages(api: Any, endpoint: str, member: str | None = None) -> tuple[int, list]:
    result, identifiers = [], set()
    total = None
    separator = "&" if "?" in endpoint else "?"
    current = f"{endpoint}{separator}per_page=100"
    visited = set()
    for page in range(1, 101):
        if current in visited:
            raise GateError("GitHub pagination repeated a cursor")
        visited.add(current)
        status, payload = api.get(current)
        next_endpoint = getattr(api, "next_endpoint", None)
        if status != 200:
            return status, []
        if member:
            if not isinstance(payload, dict) or not isinstance(payload.get(member), list):
                raise GateError("GitHub list response has an invalid schema")
            entries = payload[member]
            if "total_count" in payload:
                if total is None:
                    total = payload["total_count"]
                elif total != payload["total_count"]:
                    raise GateError("GitHub list changed during pagination")
        else:
            entries = payload
        if not isinstance(entries, list) or len(entries) > 100:
            raise GateError("GitHub list response is malformed")
        for entry in entries:
            if not isinstance(entry, dict):
                raise GateError("GitHub list entry is malformed")
            identity = entry.get("id", entry.get("number"))
            if identity is not None:
                if identity in identifiers:
                    raise GateError("GitHub pagination repeated an entry")
                identifiers.add(identity)
        result.extend(entries)
        if next_endpoint is None:
            if total is not None and total != len(result):
                raise GateError("GitHub list count does not match its pages")
            return 200, result
        if not entries:
            raise GateError("GitHub empty page unexpectedly points to another page")
        current = next_endpoint
    raise GateError("GitHub pagination exceeded its safety bound")


def ci_gate(api: Any, repository: str, sha: str, policy: dict) -> dict:
    try:
        validate_policy(policy)
        status, checks = pages(api, f"repos/{repository}/commits/{sha}/check-runs?filter=latest", "check_runs")
        if status != 200:
            return gate("github-ci", "BLOCKED", True, "Cannot read commit checks", http_status=status)
        # Repository CI is configured in this policy. Status checks from other apps cannot
        # substitute for missing expected jobs or make a no-analysis commit look green.
        required = set(policy["required_checks"])
        observed = {}
        for check in checks:
            name = check.get("name")
            if not isinstance(name, str):
                raise GateError("Check name is malformed")
            if name not in required:
                continue
            app = check.get("app") or {}
            if not isinstance(app, dict):
                raise GateError("Check app identity is malformed")
            if app.get("slug") != "github-actions":
                continue
            if check.get("head_sha") != sha:
                raise GateError("Check belongs to a different commit")
            if not isinstance(check.get("id"), int):
                raise GateError("Check identity is missing")
            # A push and a pull-request workflow can both run for one SHA. gh's latest
            # filter is per check suite; choose the newest Actions check with this name,
            # and never let a mirror/third-party app substitute for the required CI.
            if name in observed and check["id"] <= observed[name]["id"]:
                continue
            observed[name] = dict(id=check["id"], name=name, status=check.get("status"),
                                  conclusion=check.get("conclusion"),
                                  html_url=safe_url(check.get("html_url")))
        missing = sorted(required - observed.keys())
        pending = any(item["status"] != "completed" for item in observed.values())
        failed = any(item["status"] == "completed" and item["conclusion"] != "success"
                     for item in observed.values())
        outcome = "FAIL" if failed else "NOT_RUN" if missing or pending else "PASS"
        return gate("github-ci", outcome, True, "Required CI jobs for this exact commit",
                    commit=sha, checks=list(observed.values()), missing_checks=missing)
    except GateError:
        return gate("github-ci", "BLOCKED", True, "Commit checks incomplete, ambiguous or malformed")


def alert_projection(entry: dict, scan: str) -> dict:
    # Never persist secret, descriptions/messages, snippets, raw locations or author data.
    projected = {key: entry.get(key) for key in ("number", "state")}
    projected["html_url"] = safe_url(entry.get("html_url"))
    if scan == "dependabot":
        vulnerability = entry.get("security_vulnerability") or {}
        projected["severity"] = vulnerability.get("severity") if isinstance(vulnerability, dict) else None
    # Do not copy even secret_type/display_name: external strings are unnecessary for a
    # zero-open-alert decision, and a compromised API response could use them as an echo.
    return projected


def scan_gate(api: Any, repository: str, scan: str, required: bool,
              config: str | None, sha: str) -> dict:
    if config == "disabled":
        return gate(scan, "NOT_RUN", required, "Repository explicitly reports this scan disabled",
                    "NOT_CONFIGURED")
    try:
        endpoint = f"repos/{repository}/{scan}/alerts?state=open"
        status, alerts = pages(api, endpoint)
        if status != 200:
            if scan == "code-scanning" and status == 404:
                setup_status, setup = api.get(f"repos/{repository}/code-scanning/default-setup")
                if setup_status == 200 and isinstance(setup, dict) and setup.get("state") == "not-configured":
                    analyses_status, analyses = pages(api, f"repos/{repository}/code-scanning/analyses")
                    if (analyses_status == 200 and not analyses) or (
                            analyses_status == 404 and getattr(api, "failure_kind", None) == "NO_CODE_ANALYSIS"):
                        return gate(scan, "NOT_RUN", required,
                                    "Default setup is unconfigured and no custom analysis exists",
                                    "NOT_CONFIGURED")
            return gate(scan, "BLOCKED", required,
                        "Security API inaccessible; 404 does not establish missing configuration",
                        "UNKNOWN", http_status=status)
        # Successful access proves alerts can be read, but code scanning additionally
        # needs a completed analysis for the requested commit.
        if any(alert.get("state") != "open" or not isinstance(alert.get("number"), int) for alert in alerts):
            raise GateError("Open alerts response has invalid entries")
        evidence = dict(open_count=len(alerts), alerts=[alert_projection(a, scan) for a in alerts],
                        scope="repository open alerts; not a commit-specific zero-findings claim")
        if alerts:
            return gate(scan, "FAIL", required, "Repository has unresolved open security alerts", **evidence)
        if scan == "code-scanning":
            status, analyses = pages(api, f"repos/{repository}/code-scanning/analyses")
            if status != 200:
                return gate(scan, "BLOCKED", required, "Cannot prove current-commit code analysis",
                            http_status=status, **evidence)
            matching = [a for a in analyses if a.get("commit_sha") == sha]
            if not matching:
                return gate(scan, "NOT_RUN", required, "No code analysis for the requested commit",
                            "UNKNOWN" if config is None and not analyses else "CONFIGURED", **evidence)
            if any(a.get("error") or a.get("warning") for a in matching):
                return gate(scan, "FAIL", required, "Current-commit analysis reports an error or warning", **evidence)
            if any(not isinstance(a.get("ref"), str) for a in matching):
                raise GateError("Current-commit analysis reference is malformed")
            refs = sorted({a["ref"] for a in matching})
            if not refs or any(not ref.startswith("refs/") for ref in refs):
                raise GateError("Current-commit analysis has no bound reference")
            ref_counts = []
            for ref in refs:
                ref_status, current = api.get(f"repos/{repository}/commits/{quote(ref, safe='')}")
                if ref_status != 200 or not isinstance(current, dict) or current.get("sha") != sha:
                    return gate(scan, "BLOCKED", required,
                                "Analysis reference is inaccessible or moved; its alert state cannot prove this commit",
                                **evidence)
                ref_status, ref_alerts = pages(api, f"repos/{repository}/code-scanning/alerts?state=open&ref={quote(ref, safe='')}")
                if ref_status != 200:
                    return gate(scan, "BLOCKED", required, "Cannot read alerts on the analyzed reference",
                                http_status=ref_status, **evidence)
                if any(alert.get("state") != "open" or not isinstance(alert.get("number"), int)
                       for alert in ref_alerts):
                    raise GateError("Reference alerts response has invalid entries")
                if any((alert.get("most_recent_instance") or {}).get("ref") != ref or
                       (alert.get("most_recent_instance") or {}).get("state") != "open" for alert in ref_alerts):
                    raise GateError("Code alert instance is not bound to the requested reference")
                ref_counts.append(dict(ref=ref, commit=sha, open_count=len(ref_alerts)))
                if ref_alerts:
                    evidence.update(open_count=len(ref_alerts), alerts=[alert_projection(a, scan) for a in ref_alerts],
                                    analyzed_refs=ref_counts)
                    return gate(scan, "FAIL", required, "Analyzed commit reference has unresolved open code alerts", **evidence)
                # Close the read window: ref-specific alerts cannot be attributed to SHA
                # if the branch/tag moved while the result pages were being read.
                after_status, after = api.get(f"repos/{repository}/commits/{quote(ref, safe='')}")
                if after_status != 200 or not isinstance(after, dict) or after.get("sha") != sha:
                    return gate(scan, "BLOCKED", required, "Analysis reference changed during alert readback", **evidence)
            evidence["analysis_count"] = len(matching)
            evidence["analyzed_refs"] = ref_counts
            evidence["scope"] = "repository default alert view plus analyzed refs bound to the requested commit"
        return gate(scan, "PASS", required, "Accessible scan reports zero open repository alerts", **evidence)
    except GateError:
        return gate(scan, "BLOCKED", required, "Security API incomplete, malformed or timed out", "UNKNOWN")


def remote_gates(api: Any, repository: str, sha: str, phase: str,
                 policy: dict, extra_required: list[str]) -> list[dict]:
    blocking = phase in ("merge", "release")
    mandatory = set(policy["required_remote_scans"]) | set(extra_required)
    try:
        status, repo = api.get(f"repos/{repository}")
        if status != 200 or not isinstance(repo, dict) or not isinstance(repo.get("full_name"), str) or repo["full_name"].lower() != repository.lower():
            raise GateError("Repository identity inaccessible")
        settings = repo.get("security_and_analysis") or {}
        if not isinstance(settings, dict):
            raise GateError("Repository security configuration is malformed")
    except GateError:
        return [gate("github-repository", "BLOCKED", phase != "pre-push",
                     "Repository identity/configuration is inaccessible", "UNKNOWN")]
    results = []
    unknown_configuration = []
    if phase != "pre-push":
        results.append(ci_gate(api, repository, sha, policy))
    for scan in ("dependabot", "code-scanning", "secret-scanning"):
        setting_name = {"dependabot": "dependabot_security_updates",
                        "code-scanning": "code_security", "secret-scanning": "secret_scanning"}[scan]
        setting = settings.get(setting_name) or {}
        config = setting.get("status") if isinstance(setting, dict) else None
        # Dependabot security-updates disabled does not disable vulnerability alerts.
        # Likewise code_security is a product setting, not proof custom analyses are off.
        if scan != "secret-scanning":
            config = "enabled" if config == "enabled" else None
        required = blocking and (scan in mandatory or
                    policy["require_configured_scans"] and config == "enabled")
        result = scan_gate(api, repository, scan, required, config, sha)
        if blocking and policy["require_configured_scans"] and result["configuration"] == "CONFIGURED":
            result["required"] = True
        if result["configuration"] == "UNKNOWN" and config != "enabled":
            unknown_configuration.append(scan)
        results.append(result)
    if blocking and unknown_configuration:
        results.append(gate("github-security-configuration", "BLOCKED", True,
                            "Cannot prove whether optional security capabilities are configured; do not treat inaccessible APIs as disabled",
                            "UNKNOWN", unknown_scans=unknown_configuration))
    return results


def source_identity(root: Path) -> dict:
    head = run(["git", "rev-parse", "HEAD"], root, 30)
    state = run(["git", "status", "--porcelain=v1", "--untracked-files=normal"], root, 30)
    if head.returncode or state.returncode or not re.fullmatch(r"[0-9a-f]{40}", head.stdout.strip()):
        raise GateError("Cannot identify current checkout")
    files = run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], root, 30)
    if files.returncode:
        raise GateError("Cannot identify current source inputs")
    digest = hashlib.sha256()
    names = sorted(set(filter(None, files.stdout.split("\0"))))
    for name in names:
        path = root / name
        if not path.exists():
            digest.update(name.encode("utf-8") + b"\0DELETED\0")
            continue
        if not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
            raise GateError("Source input lies outside this checkout")
        file_digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                file_digest.update(chunk)
        digest.update(name.encode("utf-8") + b"\0" + file_digest.digest())
    # Persist only hashes and counts, never arbitrary diffs, filenames or user data.
    return dict(git_head=head.stdout.strip(), dirty=bool(state.stdout.strip()),
                source_inputs_sha256=digest.hexdigest(), source_file_count=len(names))


def discover_repository(root: Path) -> str:
    result = run(["git", "config", "--get", "remote.origin.url"], root, 30)
    match = re.fullmatch(r"(?:https://github\.com/|git@github\.com:)([\w.-]+/[\w.-]+?)(?:\.git)?\s*",
                         result.stdout)
    if result.returncode or not match:
        raise GateError("GitHub origin is unavailable; pass --repository owner/name")
    return match.group(1)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--phase", choices=("local", "pre-push", "post-push", "merge", "release"), default="local")
    parser.add_argument("--commit")
    parser.add_argument("--repository")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--skip-local", action="store_true")
    parser.add_argument("--required-scan", action="append", choices=("dependabot", "code-scanning", "secret-scanning"), default=[])
    args = parser.parse_args(argv)
    root = args.root.resolve()
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:12]
    output = (args.output_dir or root / "artifacts/security-quality" / run_id).resolve()
    # An explicit existing directory must not overwrite another session's evidence.
    output.mkdir(parents=True, exist_ok=False)
    report = dict(schema_version=1, run_id=run_id, started_at=utc_now(), phase=args.phase,
                  scope="NuGet solution audit and optional read-only GitHub observations",
                  output_directory=str(output), gates=[])
    try:
        policy_path = Path(__file__).parent / "policy.json"
        policy = validate_policy(json.loads(policy_path.read_text(encoding="utf-8")))
        report["policy_sha256"] = hashlib.sha256(policy_path.read_bytes()).hexdigest()
        identity = source_identity(root)
        report["source"] = identity
        sha = args.commit or identity["git_head"]
        if not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
            raise GateError("Commit must be a full 40-character SHA")
        sha = sha.lower()
        report["decision_scope"] = ("remote commit observations only; local audit explicitly not run"
                                    if args.skip_local else "current checkout source inputs and the stated remote commit")
        if args.phase in ("merge", "release") and not args.skip_local and (sha != identity["git_head"] or identity["dirty"]):
            report["gates"].append(gate("local-remote-identity", "BLOCKED", True,
                                       "Merge/release combined verification requires a clean checkout at the observed commit; use explicit remote-only readback for another SHA"))
        if args.skip_local:
            report["gates"].append(gate("nuget-audit", "NOT_RUN", args.phase == "local",
                                       "Explicit --skip-local; remote readback is not local verification"))
        else:
            report["gates"].append(local_audit(root, policy))
        if args.phase != "local":
            repository = args.repository or discover_repository(root)
            if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
                raise GateError("Invalid GitHub repository identity")
            report["repository"] = repository
            report["observed_commit"] = sha
            report["gates"].extend(remote_gates(GhApi(root), repository, sha, args.phase, policy, args.required_scan))
        if source_identity(root) != identity:
            report["gates"].append(gate("source-stability", "BLOCKED", True,
                                       "Checkout identity changed during the run"))
    except (GateError, ValueError, KeyError, OSError):
        report["gates"].append(gate("gate-execution", "BLOCKED", True,
                                   "Gate configuration, source identity or execution unavailable"))
    denied = any(item["required"] and item["status"] != "PASS" for item in report["gates"])
    remote_only = args.skip_local and args.phase in ("merge", "release")
    report.update(completed_at=utc_now(), decision="DENY" if denied else "REMOTE_ONLY" if remote_only else "ALLOW",
                  limitations=["Local audit covers NuGet only; C++ toolchain/native and Python graphs need their own evidence",
                               "Repository alert readback does not prove a new secret analysis completed for this commit",
                               "No remote protection, service, hook, commit, push or release was changed"])
    path = output / "report.json"
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Security/quality {args.phase}: {report['decision']}; report: {path}")
    for item in report["gates"]:
        print(f"  {item['name']}: {item['status']} (required={item['required']}, {item['configuration']})")
    return 1 if denied or remote_only else 0


if __name__ == "__main__":
    raise SystemExit(main())
