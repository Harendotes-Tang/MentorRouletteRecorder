#!/usr/bin/env python3
"""Regression tests for release paths and the verifier's native argv boundary.

Execute the relevant expressions from the live PowerShell AST, without starting
builds, packaging, or Collector. Discovered by run-python-tool-tests.ps1.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]


def ps_literal(value: object) -> str:
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows release and verifier paths")
class ReleasePathTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.shell = shutil.which("pwsh")
        if not cls.shell:
            raise unittest.SkipTest("PowerShell 7 is required")

    def run_script(self, source: Path, body: str) -> object:
        script = f"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {{ [Console]::Error.WriteLine($_.Exception.Message + "`n" + $_.ScriptStackTrace); exit 1 }}
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile({ps_literal(source)}, [ref]$tokens, [ref]$errors)
if ($errors.Count) {{ throw $errors[0] }}
function Get-Assignment([string]$Name) {{
    $nodes = @($ast.FindAll({{ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq ('$' + $Name)
    }}, $true))
    if ($nodes.Count -ne 1) {{ throw "Expected one assignment for $Name, found $($nodes.Count)" }}
    $nodes[0].Extent.Text
}}
{body}
"""
        completed = subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    def test_checksum_manifest_resolves_normalized_relative_and_absolute_outputs(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mr release 路径 ") as directory:
            root = Path(directory) / "仓库 with spaces"
            root.mkdir()
            cases = (
                r".\artifacts\发布 输出",
                r".\unused\..\artifacts\发布 输出",
                "artifacts\\发布 输出\\",
                str(root / "artifacts" / "." / "absolute 输出") + r"\..\absolute 输出",
            )
            expected = {
                "example.txt": b"release checksum fixture",
                "子目录 with spaces/中文.txt": "UTF-8 content 中文".encode("utf-8"),
            }
            for index, output in enumerate(cases):
                with self.subTest(output=output):
                    observed = self.run_script(REPO / "scripts/package.ps1", f"""
$RepoRoot = {ps_literal(root)}
$OutputDir = {ps_literal(output)}
$ArtifactName = 'stage-{index}'
Invoke-Expression (Get-Assignment 'OutputRoot')
Invoke-Expression (Get-Assignment 'StageDir')
$null = New-Item -ItemType Directory -Path (Join-Path $StageDir '子目录 with spaces') -Force
[IO.File]::WriteAllText((Join-Path $StageDir 'example.txt'), 'release checksum fixture', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $StageDir '子目录 with spaces/中文.txt'), 'UTF-8 content 中文', [Text.UTF8Encoding]::new($false))
Invoke-Expression (Get-Assignment 'hashLines')
@{{ stage = $StageDir; lines = @($hashLines) }} | ConvertTo-Json -Depth 4 -Compress
""")
                    entries = dict(line.split("  ", 1)[::-1] for line in observed["lines"])
                    self.assertEqual(set(expected), set(entries), observed)
                    for relative, content in expected.items():
                        self.assertEqual(hashlib.sha256(content).hexdigest(), entries[relative])
                        self.assertEqual(content, (Path(observed["stage"]) / relative).read_bytes())

    def test_listener_arguments_reach_child_without_splitting_temp_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mr-native-argv-") as directory:
            root = Path(directory)
            dumper = root / "argv dump.ps1"
            dumper.write_text(
                "$args | ConvertTo-Json -AsArray -Compress -EscapeHandling EscapeNonAscii\n",
                encoding="utf-8",
            )
            cases = ("simple", "temp with spaces 中文", "apostrophe's & (目录)")
            for folder in cases:
                with self.subTest(folder=folder):
                    database = root / folder / "mr-verify-probe.db"
                    observed = self.run_script(REPO / "scripts/verify.ps1", f"""
$probeDb = {ps_literal(database)}
$probeLogs = "$probeDb.logs"
$commands = @($ast.FindAll({{ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Start-Process' -and
    $node.Extent.Text.Contains('$probeDb')
}}, $true))
if ($commands.Count -ne 1) {{ throw 'Expected exactly one listener process launch' }}
$arguments = @($commands[0].CommandElements | Where-Object {{ $_ -is [Management.Automation.Language.ArrayExpressionAst] }})
if ($arguments.Count -ne 1) {{ throw 'Expected the listener argument array' }}
$nativeArguments = @(& ([scriptblock]::Create($arguments[0].Extent.Text)))
$stdout = {ps_literal(root / 'stdout.json')}
$stderr = {ps_literal(root / 'stderr.txt')}
Start-Process -FilePath {ps_literal(self.shell)} -WindowStyle Hidden -Wait `
    -ArgumentList (@('-NoProfile', '-File', ('"{{0}}"' -f {ps_literal(dumper)})) + $nativeArguments) `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if ((Get-Item -LiteralPath $stderr).Length -ne 0) {{ throw "argv child failed: $(Get-Content -LiteralPath $stderr -Raw)" }}
Get-Content -LiteralPath $stdout -Raw
""")
                    self.assertEqual(
                        ["--serve", "--db", str(database), "--log-dir", str(database) + ".logs", "--json"],
                        observed,
                    )


if __name__ == "__main__":
    unittest.main(verbosity=2)
