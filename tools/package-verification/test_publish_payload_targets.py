#!/usr/bin/env python3
"""Pin when Directory.Build.targets inspects the publish folder.

Directory.Build.targets promises that after every publish the published folder is scrubbed
of injection payloads (deucalion*.dll) and then checked for them, failing the build if one
is still there. scripts/package.ps1 publishes the Collector with a plain `dotnet publish`
(which builds first), so the question is whether those two targets run after
CopyFilesToPublishDirectory - and not already after Build, which MSBuild would then not
repeat.

The test publishes a throwaway project that imports the repository's own targets file and
plants a payload in the publish folder at two moments: right after the copy (the scrub must
delete it) and right after the scrub (the check must fail the publish). Nothing in the
repository is built.
"""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
TARGETS = REPO / "Directory.Build.targets"

PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <!-- The framework the installed SDK carries itself: nothing to download. -->
    <TargetFramework>net$(BundledNETCoreAppTargetFrameworkVersion)</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <!-- Named to match the targets' deucalion*.dll pattern but not the static boundary
       check's payload rule: this test plants a stand-in, never the payload. -->
  <Target Name="PlantPayload" AfterTargets="{after}">
    <WriteLinesToFile File="$(PublishDir)deucalionprobe.dll" Lines="x" Overwrite="true" />
  </Target>
</Project>
"""


@unittest.skipUnless(shutil.which("dotnet"), "the .NET SDK is required")
class PublishPayloadTargetTests(unittest.TestCase):
    def publish(self, plant_after):
        directory = Path(tempfile.mkdtemp(prefix="mr publish targets "))
        self.addCleanup(shutil.rmtree, directory, ignore_errors=True)
        shutil.copyfile(TARGETS, directory / "Directory.Build.targets")
        (directory / "Probe.csproj").write_text(PROJECT.format(after=plant_after), encoding="utf-8")
        (directory / "Probe.cs").write_text("namespace Probe { public static class P { } }\n",
                                            encoding="utf-8")
        environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                           DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
        completed = subprocess.run(
            ["dotnet", "publish", str(directory / "Probe.csproj"), "-c", "Release",
             "-o", str(directory / "out"), "-nologo", "-nodeReuse:false",
             "--disable-build-servers", "-p:UseSharedCompilation=false"],
            cwd=directory, env=environment, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=300)
        return completed, directory / "out" / "deucalionprobe.dll"

    def test_a_payload_copied_into_the_publish_folder_is_scrubbed(self):
        completed, payload = self.publish("CopyFilesToPublishDirectory")
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertFalse(payload.exists())

    def test_a_payload_left_after_the_scrub_fails_the_publish(self):
        completed, payload = self.publish("ScrubInjectionPayloadFromPublishDir")
        self.assertNotEqual(0, completed.returncode, completed.stdout)
        self.assertIn("MRBOUNDARY001", completed.stdout + completed.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
