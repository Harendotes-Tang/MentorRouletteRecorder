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
import sharecode


class IndexSampleTests(unittest.TestCase):
    def test_the_committed_sample_is_exactly_what_the_generator_writes(self):
        self.assertEqual(sample.build_sample(), sample.on_disk(),
                         "regenerate with: python tools/shared-calibration/generate_index_sample.py")

    def test_the_sample_exercises_the_rules_the_client_relies_on(self):
        files = sample.on_disk()
        expected = json.loads(files[sample.EXPECTED_NAME].decode("utf-8"))
        read = repo_index.read_index(files[sample.INDEX_NAME])
        self.assertEqual((True, ()), (read.readable, read.skipped))
        self.assertEqual(19, expected["entries"])
        groups = {(group["region"], group["game_build"]): group for group in expected["builds"]}
        crowded = groups[("CN", sample.BUILD_A)]
        self.assertEqual(repo_index.MAX_CANDIDATES, len(crowded["picks"]))
        self.assertEqual([3, 2, 1, 1, 1, 1, 1, 1], [pick["submitters"] for pick in crowded["picks"]])
        # One code a maintainer revoked, and one a replacement took out of use: the client skips both.
        self.assertEqual(2, len(crowded["revoked"]))
        self.assertEqual([], [pick for pick in crowded["picks"] if pick["code_sha256"] in crowded["revoked"]])
        # The expected file carries what the index says about every pick's marks.
        by_sha = {entry["code_sha256"]: entry for entry in read.entries}
        for group in expected["builds"]:
            for pick in group["picks"]:
                entry = by_sha[pick["code_sha256"]]
                self.assertEqual((entry.get("conflicting"), entry.get("shape_prior")), (pick["conflicting"], pick["shape_prior"]))

    def test_build_b_exercises_every_shape_prior_and_every_step_of_the_pick_order(self):
        """Plan section 20.3: unmarked before conflicting, then shape_prior == true, then the earlier publication."""
        expected = json.loads(sample.on_disk()[sample.EXPECTED_NAME].decode("utf-8"))
        group = next(group for group in expected["builds"] if (group["region"], group["game_build"]) == ("CN", sample.BUILD_B))
        self.assertEqual(1, len(group["revoked"]))
        picked = [(pick["submitters"], pick["conflicting"], pick["shape_prior"]) for pick in group["picks"]]
        self.assertEqual([(1, None, True), (1, None, None), (1, True, True), (1, True, False)], picked)
        stamps = {entry["code_sha256"]: entry["first_published_at"]
                  for entry in repo_index.read_index(sample.on_disk()[sample.INDEX_NAME]).entries}
        first, second, third, fourth = (stamps[pick["code_sha256"]] for pick in group["picks"])
        # Each step of the order is shown against an earlier publication: none of it is publication order.
        self.assertGreater(first, second)
        self.assertGreater(third, fourth)

    def test_a_dispute_the_section_18_6_rule_never_flagged_is_marked(self):
        """GLOBAL build A: the MARKER_OFFSET code is alone in its match source, but its zone differs."""
        files = sample.on_disk()
        expected = json.loads(files[sample.EXPECTED_NAME].decode("utf-8"))
        group = next(group for group in expected["builds"] if group["region"] == "GLOBAL")
        self.assertEqual([(2, True), (1, True), (1, True)], [(pick["submitters"], pick["conflicting"]) for pick in group["picks"]])
        read = repo_index.read_index(files[sample.INDEX_NAME])
        state = repo_index.Index(read.entries, ())
        root = sample.FIXTURES / sample.CODES_DIRECTORY
        found = repo_index.disputes(state, root, "GLOBAL", sample.BUILD_A)
        marker = next(entry["code_sha256"] for entry in read.entries
                      if entry["region"] == "GLOBAL" and entry["match_source"] == "MARKER_OFFSET")
        self.assertIn("zone", found[marker])
        self.assertNotIn("match", found[marker])
        self.assertEqual({sha for sha in found}, {entry["code_sha256"] for entry in read.entries
                                                   if entry["region"] == "GLOBAL" and entry.get("conflicting")})
        codes = [name for name in files if name.startswith(sample.CODES_DIRECTORY + "/")]
        self.assertEqual(sorted(sample.CODES_DIRECTORY + "/" + entry["path"] for entry in read.entries), sorted(codes))
        for entry in read.entries:
            self.assertEqual(entry["code_sha256"], sharecode.decode(files[sample.CODES_DIRECTORY + "/" + entry["path"]].decode("ascii")).code_sha256)


if __name__ == "__main__":
    unittest.main()
