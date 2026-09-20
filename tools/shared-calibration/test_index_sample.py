#!/usr/bin/env python3
"""The index sample under tests/Fixtures/shared-calibration is generated, never hand-written.

SharedCalibrationPublicRepoSampleTests.cs feeds it to the released client; this test makes sure the
files it reads are exactly what generate_index_sample.py produces through index.add_submission.
"""

from __future__ import annotations

import json
import unittest

import testsupport  # noqa: F401 - puts this directory on sys.path when run from elsewhere
import generate_index_sample as sample
import index as repo_index


class IndexSampleTests(unittest.TestCase):
    def test_the_committed_sample_is_exactly_what_the_generator_writes(self):
        self.assertEqual(sample.build_sample(), sample.on_disk(),
                         "regenerate with: python tools/shared-calibration/generate_index_sample.py")

    def test_the_sample_exercises_the_rules_the_client_relies_on(self):
        files = sample.on_disk()
        expected = json.loads(files[sample.EXPECTED_NAME].decode("utf-8"))
        read = repo_index.read_index(files[sample.INDEX_NAME])
        self.assertEqual((True, ()), (read.readable, read.skipped))
        self.assertEqual(13, expected["entries"])
        groups = {(group["region"], group["game_build"]): group for group in expected["builds"]}
        crowded = groups[("CN", sample.BUILD_A)]
        self.assertEqual(repo_index.MAX_CANDIDATES, len(crowded["picks"]))
        self.assertEqual([3, 2, 1, 1, 1, 1, 1, 1], [pick["submitters"] for pick in crowded["picks"]])
        # One code a maintainer revoked, and one a replacement took out of use: the client skips both.
        self.assertEqual(2, len(crowded["revoked"]))
        self.assertEqual([], [pick for pick in crowded["picks"] if pick["code_sha256"] in crowded["revoked"]])
        self.assertEqual(([], 1), (groups[("CN", sample.BUILD_B)]["picks"], len(groups[("CN", sample.BUILD_B)]["revoked"])))
        self.assertEqual(1, len(groups[("GLOBAL", sample.BUILD_A)]["picks"]))
        codes = [name for name in files if name.startswith(sample.CODES_DIRECTORY + "/")]
        self.assertEqual(sorted(sample.CODES_DIRECTORY + "/" + entry["path"] for entry in read.entries), sorted(codes))


if __name__ == "__main__":
    unittest.main()
