#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Negative self-test for the static hard-boundary checker.

``check.py`` passing proves nothing on its own: a checker that never matches
anything also passes. This script proves the opposite direction. It plants each
forbidden token in a throwaway repository tree and asserts that ``check.py``
*fails* on it, that it names the right rule, that it looks in every place the
boundary has to be enforced, and that the ``BOUNDARY-ALLOW`` escape hatch still
works.

This file lives inside ``tools/static-boundary-check/``, which ``rules.json``
excludes from the scan, so the samples below never trip the real run.

Exit codes
----------
0   every expectation held
1   at least one expectation failed

Usage
-----
    python tools/static-boundary-check/selftest.py [-v]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Iterable

HERE = Path(__file__).resolve().parent
CHECK_PY = HERE / "check.py"
RULES_PATH = HERE / "rules.json"

EXIT_OK = 0
EXIT_VIOLATION = 1

# One line per rule that must be rejected. The key is the rule id declared in
# rules.json, or "id/variant" for a rule id declared more than once; the value is
# a snippet a careless change could plausibly produce. Every rule entry in
# rules.json must appear here, so adding a rule without a negative test fails
# this script.
SAMPLES: dict[str, str] = {
    "INJ-001": "        var ok = ReadProcessMemory(handle, address, buffer, size, out var read);",
    "INJ-002": "        var ok = WriteProcessMemory(handle, address, buffer, size, out var written);",
    "INJ-003": "        var remote = VirtualAllocEx(handle, IntPtr.Zero, 4096, Commit, ReadWrite);",
    "INJ-004": "        var thread = CreateRemoteThread(handle, IntPtr.Zero, 0, start, arg, 0, out _);",
    "INJ-005": "        var hook = SetWindowsHookExW(WhKeyboardLl, callback, module, 0);",
    "INJ-006": "        NtWriteVirtualMemory(handle, address, buffer, size, out _);",
    "INJ-007": "        using var game = OpenProcess(ProcessVmRead, false, processId);",
    "DEU-001": "        monitor.UseDeucalion = true;",
    "DEU-002": "        var client = new DeucalionClient(processId);",
    "DEU-003": '        var payload = "deucalion-1.2.3.dll";',
    "DEU-004": "        InjectLibrary(processId, payloadPath);",
    "CAP-001": "        pcap_sendpacket(handle, frame, frame.Length);",
    "CAP-002": "        pcap_inject(handle, frame, frame.Length);",
    "CAP-003": "        monitor.MonitorType = NetworkMonitorType.RawSocket;",
    "CAP-004": "        var socket = new RawCaptureSocket(adapterAddress);",
    "CAP-005": "        pcap_dump_open(handle, capturePath);",
    "CAP-006": '        [DllImport("wpcap.dll")] static extern int pcap_open_live();',
    "NET-001": "        var listener = new HttpListener();",
    "NET-002": "        var listener = new TcpListener(IPAddress.Loopback, 8080);",
    "NET-003": "        var socket = new ClientWebSocket();",
    "NET-004": "        using Microsoft.AspNetCore.Builder;",
    "NET-005": "        auto *server = new QTcpServer(this);",
    "NET-006": "        using var client = new HttpClient();",
    "NET-007/shared-calibration-hosts": '        const string Mirror = "https://cdn.jsdelivr.net/gh/owner/repo@main/index.json";',
    "NET-007/github-content-hosts": '        const string Raw = "https://raw.githubusercontent.com/owner/repo/main/index.json";',
    "NET-007/speech-host": '        const string Endpoint = "https://eastasia.tts.speech.microsoft.com/cognitiveservices/v1";',
    "AUT-001": "        SendInput(1, ref input, Marshal.SizeOf(input));",
}

# Tokens the specification names one by one. Each must be rejected wherever it
# appears, regardless of which rule happens to catch it.
SPEC_TOKENS: tuple[str, ...] = (
    "ReadProcessMemory",
    "WriteProcessMemory",
    "VirtualAllocEx",
    "CreateRemoteThread",
    "SetWindowsHookEx",
    "pcap_sendpacket",
    "pcap_inject",
    "NetworkMonitorType.RawSocket",
    "UseDeucalion = true",
    "HttpListener",
    "TcpListener",
    "QTcpServer",
    "QHttpServer",
    "QNetworkAccessManager",
    "WebSocket",
)

# Every location the checker has to look at, and a file in it that the include
# list accepts. A token planted in any one of these must be found.
PLANT_SITES: tuple[tuple[str, str], ...] = (
    ("src/Collector/Capture/Sample.cs", "cs"),
    ("src/Desktop/cpp/Sample.cpp", "cpp"),
    ("src/Desktop/qml/Sample.qml", "qml"),
    ("src/Desktop/CMakeLists.txt", "cmake"),
    ("tests/Collector.UnitTests/Sample.cs", "cs"),
    ("tools/some-tool/sample.py", "py"),
    ("scripts/sample.ps1", "ps1"),
    ("CMakeLists.txt", "cmake"),
    ("Directory.Build.targets", "xml"),
)

COMMENT: dict[str, str] = {
    "cs": "// ",
    "cpp": "// ",
    "qml": "// ",
    "py": "# ",
    "ps1": "# ",
    "cmake": "# ",
    "xml": "<!-- ",
}


class Failure(Exception):
    """A self-test expectation did not hold."""


def run_check(root: Path) -> tuple[int, dict]:
    """Run check.py against ``root`` and return its exit code and JSON report."""
    completed = subprocess.run(
        [
            sys.executable,
            str(CHECK_PY),
            "--root",
            str(root),
            "--rules",
            str(RULES_PATH),
            "--json",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    try:
        report = json.loads(completed.stdout or "{}")
    except ValueError as exc:  # pragma: no cover - only on a broken checker
        raise Failure(
            f"check.py did not emit JSON (exit {completed.returncode}): "
            f"{completed.stdout[:400]}{completed.stderr[:400]}"
        ) from exc
    return completed.returncode, report


def plant(root: Path, relative: str, line: str) -> None:
    """Write a file under ``root`` containing ``line``."""
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "// generated by selftest\n" + line + "\n", encoding="utf-8"
    )


def clean_tree(root: Path) -> None:
    """Create the directory shape the checker expects, with nothing forbidden in it."""
    for relative, _ in PLANT_SITES:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("// nothing to see here\n", encoding="utf-8")


def expect_violation(
    root: Path, why: str, rule_id: str | None = None, rule_key: str | None = None
) -> None:
    code, report = run_check(root)
    if code != EXIT_VIOLATION:
        raise Failure(f"{why}: expected exit {EXIT_VIOLATION}, got {code}")
    if report.get("violation_count", 0) < 1:
        raise Failure(f"{why}: exit was 1 but no violation was reported")
    if rule_id is not None:
        found = {v["rule_id"] for v in report.get("violations", [])}
        if rule_id not in found:
            raise Failure(
                f"{why}: expected rule {rule_id}, checker reported {sorted(found)}"
            )
    if rule_key is not None:
        keys = {v.get("rule_key") for v in report.get("violations", [])}
        if rule_key not in keys:
            raise Failure(
                f"{why}: expected rule entry {rule_key}, checker reported {sorted(map(str, keys))}"
            )


def expect_clean(root: Path, why: str) -> None:
    code, report = run_check(root)
    if code != EXIT_OK:
        raise Failure(
            f"{why}: expected exit {EXIT_OK}, got {code}; "
            f"violations={report.get('violations')}"
        )


def rule_key(rule: dict) -> str:
    """The key a rules.json entry is sampled under: ``id``, or ``id/variant``."""
    variant = rule.get("variant", "")
    return f"{rule['id']}/{variant}" if variant else rule["id"]


def run_check_with_rules(root: Path, raw: dict) -> int:
    """Run check.py against ``root`` with a modified copy of the rules; return its exit code."""
    rules_copy = root / "rules.json"
    rules_copy.write_text(json.dumps(raw), encoding="utf-8")
    completed = subprocess.run(
        [sys.executable, str(CHECK_PY), "--root", str(root), "--rules", str(rules_copy), "--json"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    return completed.returncode


def cases() -> Iterable[tuple[str, callable]]:
    """Yield (name, thunk) for every self-test case."""
    rules = json.loads(RULES_PATH.read_text(encoding="utf-8"))
    allow_marker = rules["allow_marker"]
    declared = [rule_key(rule) for rule in rules["rules"]]

    missing = [rule_id for rule_id in declared if rule_id not in SAMPLES]
    stale = [rule_id for rule_id in SAMPLES if rule_id not in declared]

    def coverage() -> None:
        if missing:
            raise Failure(
                "rules.json declares rules with no negative sample: " + ", ".join(missing)
            )
        if stale:
            raise Failure(
                "selftest has samples for rules that no longer exist: " + ", ".join(stale)
            )

    yield "every rule has a negative sample", coverage

    # The baseline: a tree with nothing forbidden in it must pass, otherwise
    # every case below would "pass" for the wrong reason.
    def baseline() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            expect_clean(root, "a tree with no forbidden token")

    yield "a clean tree passes", baseline

    for key in declared:
        if key not in SAMPLES:
            continue
        sample = SAMPLES[key]

        def one_rule(key: str = key, sample: str = sample) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, "src/Collector/Capture/Sample.cs", sample)
                expect_violation(root, f"rule {key}", key.split("/", 1)[0], key)

        yield f"{key} is rejected", one_rule

    for token in SPEC_TOKENS:

        def one_token(token: str = token) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, "src/Collector/Capture/Sample.cs", "        x = " + token + ";")
                expect_violation(root, f"token {token!r}")

        yield f"token {token!r} is rejected", one_token

    for relative, kind in PLANT_SITES:

        def one_site(relative: str = relative) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, relative, "        x = CreateRemoteThread(h);")
                expect_violation(root, f"planted in {relative}", "INJ-004")

        yield f"{relative} is scanned", one_site

    # --- path allowances -------------------------------------------------------------
    # The one outbound client is allowed by its exact repository-relative path, and the
    # public repository's script mirror by an explicit directory prefix. Both must be
    # exact: a file of the same name elsewhere, a sibling, or a look-alike directory is
    # still a violation, and an allowance lifts one rule, never the others.
    client = "src/Collector/Protocol/Sharing/SharedCalibrationClient.cs"
    http_line = "        using var client = new HttpClient();"
    host_line = '        var index = "https://raw.githubusercontent.com/owner/repo/main/index.json";'
    cdn_line = '        var mirror = "https://cdn.jsdelivr.net/gh/owner/repo@main/index.json";'

    def exact_path_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, client, http_line + "\n" + host_line)
            expect_clean(root, f"NET-006 and NET-007 in {client}")

    yield "NET-006/NET-007 allow the fetch client by exact path", exact_path_is_allowed

    for elsewhere in (
        "tests/Collector.UnitTests/SharedCalibrationClient.cs",
        "src/Collector/Capture/SharedCalibrationClient.cs",
        "tools/mirror/src/Collector/Protocol/Sharing/SharedCalibrationClient.cs",
        "src/Collector/Protocol/Sharing/SharedCalibrationStore.cs",
    ):

        def same_name_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, http_line)
                expect_violation(root, f"HttpClient in {elsewhere}", "NET-006")

        yield f"NET-006 still rejects {elsewhere}", same_name_elsewhere

    def allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, client, "        x = CreateRemoteThread(h);")
            expect_violation(root, f"CreateRemoteThread in {client}", "INJ-004")

    yield "an allowed path is still checked by every other rule", allowance_lifts_one_rule_only

    for inside in ("tools/shared-calibration/publish.py", "tools/shared-calibration/lib/index.py"):

        def prefix_is_allowed(inside: str = inside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, inside, "        MIRROR = 'https://cdn.jsdelivr.net/gh/owner/repo@main/'")
                expect_clean(root, f"a host literal in {inside}")

        yield f"NET-007 allows {inside}", prefix_is_allowed

    for outside in (
        "tools/shared-calibration-old/publish.py",
        "tools/other/shared-calibration/publish.py",
        "scripts/fetch.ps1",
        "tests/Collector.UnitTests/SharedCalibrationClientTests.cs",
    ):

        def prefix_ends_at_the_boundary(outside: str = outside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, outside, "        x = 'HTTPS://FASTLY.JSDELIVR.NET/gh/owner/repo@main/'")
                expect_violation(root, f"a host literal in {outside}", "NET-007")

        yield f"NET-007 still rejects {outside}", prefix_ends_at_the_boundary

    def prefix_does_not_open_net006() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "tools/shared-calibration/fetch.py", http_line)
            expect_violation(root, "HttpClient under tools/shared-calibration/", "NET-006")

    yield "the NET-007 directory allowance does not lift NET-006", prefix_does_not_open_net006

    # --- the online-speech client (docs/privacy-boundary.md section 8.3) ---------------
    # The second outbound client is allowed HttpClient and the speech host, by exact path,
    # and nothing else: the CDN hosts stay refused there, the speech host stays refused in
    # the shared-calibration client, and siblings or look-alikes stay refused everywhere.
    speech = "src/Collector/Speech/OnlineSpeechClient.cs"
    speech_host_line = '        const string HostSuffix = ".TTS.Speech.Microsoft.com";'

    def speech_client_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, http_line + "\n" + speech_host_line)
            expect_clean(root, f"NET-006 and the speech host in {speech}")

    yield "NET-006/NET-007 allow the online-speech client by exact path", speech_client_is_allowed

    def speech_client_may_not_name_the_cdn() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, cdn_line)
            expect_violation(
                root, f"a CDN host in {speech}", "NET-007", "NET-007/shared-calibration-hosts"
            )

    yield "NET-007 still rejects the CDN hosts in the online-speech client", speech_client_may_not_name_the_cdn

    def speech_client_may_not_name_the_content_host() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, host_line)
            expect_violation(
                root, f"the content host in {speech}", "NET-007", "NET-007/github-content-hosts"
            )

    yield (
        "NET-007 still rejects the content host in the online-speech client",
        speech_client_may_not_name_the_content_host,
    )

    # --- the update-check client (docs/privacy-boundary.md section 8.4) -----------------
    # The third outbound client is allowed HttpClient and the content host, by exact path, and
    # nothing else: the CDN hosts and the speech host stay refused there, the content host stays
    # refused in every other file of the feature, and a sibling is refused everywhere.
    updates = "src/Collector/Update/UpdateCheckClient.cs"

    def update_client_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, updates, http_line + "\n" + host_line)
            expect_clean(root, f"NET-006 and the content host in {updates}")

    yield "NET-006/NET-007 allow the update-check client by exact path", update_client_is_allowed

    for line, variant, what in (
        (cdn_line, "NET-007/shared-calibration-hosts", "a CDN host"),
        (speech_host_line, "NET-007/speech-host", "the speech host"),
    ):

        def update_client_may_not_name_it(
            line: str = line, variant: str = variant, what: str = what
        ) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, updates, line)
                expect_violation(root, f"{what} in {updates}", "NET-007", variant)

        yield f"NET-007 still rejects {what} in the update-check client", update_client_may_not_name_it

    for elsewhere in (
        "src/Collector/Update/UpdateCheckService.cs",
        "src/Collector/Update/UpdateCheckClient.cs.bak.cs",
        "src/Collector/Capture/UpdateCheckClient.cs",
        "tests/Collector.UnitTests/UpdateCheckClientTests.cs",
        "src/Collector/Speech/OnlineSpeechClient.cs",
    ):

        def content_host_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, host_line)
                expect_violation(
                    root, f"the content host in {elsewhere}", "NET-007", "NET-007/github-content-hosts"
                )

        yield f"NET-007 rejects the content host in {elsewhere}", content_host_elsewhere

    def http_beside_the_update_client() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "src/Collector/Update/UpdateCheckService.cs", http_line)
            expect_violation(root, "HttpClient in the update-check service", "NET-006")

    yield "NET-006 still rejects HttpClient beside the update-check client", http_beside_the_update_client

    def update_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, updates, "        var socket = new ClientWebSocket();")
            expect_violation(root, f"a WebSocket in {updates}", "NET-003")

    yield "the update-check client is still checked by every other rule", update_allowance_lifts_one_rule_only

    for elsewhere in (
        client,
        "src/Collector/Speech/OnlineSpeechCache.cs",
        "src/Collector/Speech/OnlineSpeechClient.cs.bak.cs",
        "src/Collector/Capture/OnlineSpeechClient.cs",
        "tests/Collector.UnitTests/OnlineSpeechClientTests.cs",
        "src/Desktop/cpp/TtsService.cpp",
        "src/Desktop/qml/pages/SettingsPage.qml",
        "tools/shared-calibration/speech.py",
        "tools/duty-data-generator/generate.py",
        "scripts/verify.ps1",
    ):

        def speech_host_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, speech_host_line)
                expect_violation(
                    root, f"the speech host in {elsewhere}", "NET-007", "NET-007/speech-host"
                )

        yield f"NET-007 rejects the speech host in {elsewhere}", speech_host_elsewhere

    for elsewhere in (
        "src/Collector/Speech/OnlineSpeechCache.cs",
        "src/Collector/Speech/OnlineSpeechService.cs",
        "tests/Collector.UnitTests/OnlineSpeechClient.cs",
        "src/Desktop/cpp/TtsService.cpp",
    ):

        def http_beside_the_speech_client(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, http_line)
                expect_violation(root, f"HttpClient in {elsewhere}", "NET-006")

        yield f"NET-006 still rejects {elsewhere}", http_beside_the_speech_client

    def speech_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, "        var socket = new ClientWebSocket();")
            expect_violation(root, f"a WebSocket in {speech}", "NET-003")

    yield "the online-speech client is still checked by every other rule", speech_allowance_lifts_one_rule_only

    # --- variants: one id, several entries, each with its own pattern and allowances ----
    def variant_rules(mutate) -> dict:
        raw = json.loads(RULES_PATH.read_text(encoding="utf-8"))
        mutate(raw["rules"])
        return raw

    def drop_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                del entry["variant"]

    def duplicate_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = "shared-calibration-hosts"

    def bad_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = "Speech Host"

    def non_string_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = 7

    for name, mutate in (
        ("an entry of a repeated id without a variant", drop_variant),
        ("two entries with the same id and variant", duplicate_variant),
        ("a variant that is not a lower-case token", bad_variant),
        ("a variant that is not a string", non_string_variant),
    ):

        def malformed_variant(mutate=mutate, name: str = name) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code = run_check_with_rules(root, variant_rules(mutate))
                if code != 2:
                    raise Failure(f"{name}: expected exit 2 (unusable rules), got {code}")

        yield f"{name} is refused as a rules error", malformed_variant

    for key, bad in (
        ("allow_paths", "src\\Collector\\Protocol\\Sharing\\SharedCalibrationClient.cs"),
        ("allow_paths", "/src/Collector/Protocol/Sharing/SharedCalibrationClient.cs"),
        ("allow_paths", "src/Collector/../Collector/SharedCalibrationClient.cs"),
        ("allow_paths", "src/Collector/Protocol/Sharing/"),
        ("allow_path_prefixes", "tools/shared-calibration"),
        ("allow_path_prefixes", "./tools/shared-calibration/"),
        ("allow_paths", ""),
    ):

        def malformed_allowance(key: str = key, bad: str = bad) -> None:
            raw = json.loads(RULES_PATH.read_text(encoding="utf-8"))
            for rule in raw["rules"]:
                if rule["id"] == "NET-006":
                    rule[key] = [bad]
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                rules_copy = root / "rules.json"
                rules_copy.write_text(json.dumps(raw), encoding="utf-8")
                completed = subprocess.run(
                    [sys.executable, str(CHECK_PY), "--root", str(root), "--rules", str(rules_copy), "--json"],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    check=False,
                )
                if completed.returncode != 2:
                    raise Failure(
                        f"{key}={bad!r}: expected exit 2 (unusable rules), got {completed.returncode}"
                    )

        yield f"{key} {bad!r} is refused as a rules error", malformed_allowance

    def allow_marker_works() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(
                root,
                "src/Collector/Capture/Sample.cs",
                "        x = CreateRemoteThread(h); // " + allow_marker + ": enforcement only",
            )
            expect_clean(root, f"a line carrying {allow_marker}")

    yield f"{allow_marker} still suppresses a hit", allow_marker_works

    def excluded_dirs_are_skipped() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "src/Collector/bin/Release/Sample.cs", "        CreateRemoteThread(h);")
            plant(root, "src/Collector/obj/Debug/Sample.cs", "        CreateRemoteThread(h);")
            expect_clean(root, "build output directories")

    yield "bin/ and obj/ are not scanned", excluded_dirs_are_skipped

    def unscanned_extension() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            # Documentation names the forbidden APIs on purpose; it is not code.
            plant(root, "src/Collector/notes.md", "we never call CreateRemoteThread")
            expect_clean(root, "a markdown file")

    yield "documentation is not scanned", unscanned_extension


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Negative self-test for the static boundary checker."
    )
    parser.add_argument("-v", "--verbose", action="store_true", help="print every case")
    args = parser.parse_args(argv)

    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, OSError, ValueError):
            pass

    if not CHECK_PY.is_file():
        print(f"error: checker not found: {CHECK_PY}", file=sys.stderr)
        return EXIT_VIOLATION

    passed = 0
    failures: list[str] = []
    for name, thunk in cases():
        try:
            thunk()
        except Failure as exc:
            failures.append(f"{name}: {exc}")
            print(f"FAIL {name}", file=sys.stderr)
            continue
        passed += 1
        if args.verbose:
            print(f"ok   {name}")

    print()
    if failures:
        for failure in failures:
            print("  " + failure, file=sys.stderr)
        print(f"FAIL: {len(failures)} of {passed + len(failures)} self-test case(s) failed.")
        return EXIT_VIOLATION

    print(f"OK: {passed} self-test case(s) passed; the checker rejects what it must.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
