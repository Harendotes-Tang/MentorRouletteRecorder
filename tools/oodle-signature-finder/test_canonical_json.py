#!/usr/bin/env python3
"""The canonical-JSON contract between this tool and the collector.

``find_signatures.canonical_json`` stamps ``profile_sha256`` onto every emitted profile;
``src/Collector/Protocol/Profiles/CanonicalJson.cs`` recomputes it when the collector
loads one. The two must agree byte for byte: a divergence on an escape, a number or a key
ordering makes the collector reject every profile this tool has written, and only at the
moment capture is supposed to start.

These are the Python half; the C# twin lives in ``tests/Collector.UnitTests``.
The anchor both halves share is the shipped profile
``protocol-profiles/oodle-signatures/cn.2026.08.05.json``: if either implementation
drifts, its stored ``profile_sha256`` stops reproducing.

Run:  python tools/oodle-signature-finder/test_canonical_json.py
"""

from __future__ import annotations

import hashlib
import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
PROFILE_DIRECTORY = os.path.join(REPO_ROOT, "protocol-profiles", "oodle-signatures")
sys.path.insert(0, HERE)

import find_signatures  # noqa: E402


def _profile_paths() -> list[str]:
    if not os.path.isdir(PROFILE_DIRECTORY):
        return []
    return [os.path.join(PROFILE_DIRECTORY, name)
            for name in sorted(os.listdir(PROFILE_DIRECTORY))
            if name.endswith(".json")]


def _digest(document: dict) -> str:
    canonical = find_signatures.canonical_json(document, without=("profile_sha256",))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


class ShippedProfileTests(unittest.TestCase):
    """Every profile that ships in the repository must re-hash to its stored value."""

    def test_the_signature_profile_directory_is_not_empty(self):
        # A test that silently covers nothing when the directory is empty would be worth
        # nothing; this is the guard that keeps the ones below honest.
        self.assertTrue(_profile_paths(),
                        f"no signature profiles found under {PROFILE_DIRECTORY}")

    def test_every_shipped_profile_reproduces_its_own_profile_sha256(self):
        for path in _profile_paths():
            with self.subTest(profile=os.path.basename(path)):
                with open(path, encoding="utf-8") as handle:
                    document = json.load(handle)
                self.assertIn("profile_sha256", document)
                self.assertEqual(document["profile_sha256"], _digest(document))

    def test_the_cn_profile_is_present_and_self_consistent(self):
        # Named explicitly so that deleting or renaming the profile the CN client depends
        # on fails here rather than quietly reducing the loop above to zero iterations.
        path = os.path.join(PROFILE_DIRECTORY, "cn.2026.08.05.json")
        self.assertTrue(os.path.isfile(path), path)

        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)

        self.assertEqual(1, document["schema_version"])
        self.assertEqual("CN", document["region"])
        self.assertEqual("tools/oodle-signature-finder", document["source"])
        self.assertIn(document["status"], ("CANDIDATE", "VERIFIED"))
        self.assertEqual(sorted(find_signatures.MACHINA_SIGNATURES),
                         sorted(document["signatures"]))
        self.assertEqual(sorted(find_signatures.MACHINA_SIGNATURES),
                         sorted(document["resolved_rvas"]))
        self.assertEqual(document["profile_sha256"], _digest(document))


class CanonicalFormTests(unittest.TestCase):
    """The grammar itself, so a drift is caught here before it reaches a profile."""

    def test_keys_are_sorted_ordinally_and_nothing_is_padded(self):
        self.assertEqual('{"B":2,"a":1,"å":3}',
                         find_signatures.canonical_json({"a": 1, "å": 3, "B": 2}))

    def test_the_excluded_field_is_removed_not_emptied(self):
        document = {"a": 1, "profile_sha256": "deadbeef"}
        self.assertEqual('{"a":1}',
                         find_signatures.canonical_json(document, without=("profile_sha256",)))
        # ... and the caller's document is left alone.
        self.assertEqual("deadbeef", document["profile_sha256"])

    def test_non_ascii_stays_unescaped_so_both_sides_hash_the_same_utf8(self):
        self.assertEqual('{"k":"导随"}',
                         find_signatures.canonical_json({"k": "导随"}))

    def test_control_characters_use_the_short_escapes_json_defines(self):
        self.assertEqual('{"k":"a\\nb\\tc\\"d\\\\e"}',
                         find_signatures.canonical_json({"k": 'a\nb\tc"d\\e'}))

    def test_floats_are_refused_because_their_spelling_is_not_portable(self):
        # "1.0" or "1"? "1e-07" or "1E-07"? .NET and Python do not have to agree, so the
        # grammar excludes floating point outright rather than hoping that they do.
        for value in (1.5, float("nan")):
            with self.subTest(value=value), self.assertRaises(TypeError):
                find_signatures.canonical_json({"k": value})

    def test_types_outside_the_grammar_are_refused_rather_than_coerced(self):
        for value in ({1, 2}, (1, 2), b"bytes"):
            with self.subTest(value=value), self.assertRaises(TypeError):
                find_signatures.canonical_json({"k": value})

    def test_nested_objects_and_arrays_are_canonicalised_too(self):
        self.assertEqual('{"a":[{"y":1,"z":2},[]],"b":null}',
                         find_signatures.canonical_json(
                             {"b": None, "a": [{"z": 2, "y": 1}, []]}))


if __name__ == "__main__":
    unittest.main(verbosity=2)
