#!/usr/bin/env python3
"""Offline TC source validation, independent identity and entry-point tests."""

from __future__ import annotations

import contextlib
import csv
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import generate
import tc_catalog

COMMIT = "1cf1f9fb735bad0748848dbd3f4de05721fe619a"
STAMP = "2026-10-24T00:00:00.000Z"


def csv_text(sheet, rows, extra=()):
    fields = list(tc_catalog.SHEETS[sheet]) + list(extra)
    types = [tc_catalog.SHEETS[sheet].get(field, "byte") for field in fields]
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(["key", *range(len(fields))])
    writer.writerow(["#", *fields])
    writer.writerow(["int32", *types])
    for row_id, values in rows:
        writer.writerow([row_id, *values])
    return buffer.getvalue()


def synthetic_sources():
    return {
        "ContentFinderCondition": csv_text("ContentFinderCondition", [
            (5, ["樣例五", 172, 2, 50, 2]),
            (13, ["樣例十三", 171, 2, 50, 2]),
            (36, ["樣例三十六", 434, 2, 50, 2]),
            (235, ["樣例二三五", 616, 2, 50, 2]),
            (1029, ["TC原文名稱", 1272, 4, 100, 2]),
            (1100, ["", 0, 0, 0, 0]),
        ]),
        "TerritoryType": csv_text("TerritoryType", [
            (172, [0]), (171, [0]), (434, [1]), (616, [2]), (1272, [5]),
        ]),
        "ContentType": csv_text("ContentType", [(2, ["迷宮挑戰"]), (4, ["討伐殲滅戰"])]),
        "ExVersion": csv_text("ExVersion", [
            (0, ["新生艾奧傑亞"]), (1, ["蒼天伊修加爾德"]),
            (2, ["紅蓮解放者"]), (5, ["黃金遺產"]),
        ]),
        "ContentMemberType": csv_text("ContentMemberType", [(0, []), (2, [])]),
        "ContentRoulette": csv_text("ContentRoulette", [
            (1, ["隨機任務：練級", "True"]), (9, ["隨機任務：指導者", "True"]),
            (26, ["陸行鳥競賽：隨機賽道", "False"]), (0, ["", "False"]),
        ]),
    }


def provenance():
    return {
        "repository": "https://github.com/" + tc_catalog.REPOSITORY,
        "ref": COMMIT, "commit": COMMIT, "commit_message": "ver 2026.09.14.0000.0000",
        "requests": [{"url": "%s/%s/%s/%s.csv" %
                      (generate.DATAMINING_RAW_ROOT, tc_catalog.REPOSITORY, COMMIT, sheet),
                      "sha256": "a" * 64}
                     for sheet in tc_catalog.SHEETS],
    }


class TcCatalogTests(unittest.TestCase):
    def build(self, texts=None, fetched=None):
        return tc_catalog.build_catalogues(texts or synthetic_sources(), fetched or provenance(),
                                           "2026-10-24", STAMP)

    def test_only_tc_named_rows_and_native_territories_are_emitted(self):
        duties, roulettes = self.build()
        rows = {row["content_id"]: row for row in duties["duties"]}
        self.assertEqual({5, 13, 36, 235, 1029}, rows.keys())
        self.assertEqual([172, 171, 434, 616], [rows[i]["territory_id"] for i in (5, 13, 36, 235)])
        self.assertEqual("TC原文名稱", rows[1029]["localized_name"])
        self.assertEqual("四人迷宫", rows[5]["duty_category"])
        self.assertEqual("Dawntrail", rows[1029]["expansion"])
        self.assertEqual("TC", duties["region"])
        self.assertFalse(roulettes["roulettes"][-1]["enabled"])
        self.assertEqual("隨機任務：指導者", roulettes["roulettes"][1]["localized_name"])
        self.assertTrue(all(row["party_size"] is None for row in rows.values()))
        self.assertFalse(any("preferred_for_name_matching" in row for row in rows.values()))

    def test_named_tc_party_columns_are_used_when_the_source_supplies_them(self):
        texts = synthetic_sources()
        texts["ContentMemberType"] = csv_text("ContentMemberType", [(0, [0, 0]), (2, [8, 3])],
                                             ["MembersPerParty", "PartyCount"])
        duties, _ = self.build(texts)
        self.assertTrue(all(row["party_size"] == 24 for row in duties["duties"]))
        self.assertEqual(0, duties["provenance"]["party_size_missing_named_columns"])

    def test_missing_source_link_header_or_width_fails_before_output(self):
        variants = []
        missing = synthetic_sources()
        del missing["TerritoryType"]
        variants.append(missing)
        missing_link = synthetic_sources()
        missing_link["TerritoryType"] = csv_text("TerritoryType", [(171, [0])])
        variants.append(missing_link)
        for old, new in (("Name", "Renamed"), ("TerritoryType", "Name"),
                         ("172", "misaligned"), ("5,樣例五,172", "5,樣例五,,172")):
            changed = synthetic_sources()
            changed["ContentFinderCondition"] = changed["ContentFinderCondition"].replace(old, new, 1)
            variants.append(changed)
        for texts in variants:
            with self.subTest(texts=texts), self.assertRaises(ValueError):
                self.build(texts)

    def test_duplicate_source_row_and_type_change_are_rejected(self):
        texts = synthetic_sources()
        texts["ContentFinderCondition"] += texts["ContentFinderCondition"].splitlines()[3] + "\n"
        with self.assertRaisesRegex(ValueError, "repeats row"):
            self.build(texts)
        texts = synthetic_sources()
        texts["ContentFinderCondition"] = texts["ContentFinderCondition"].replace(
            "str,TerritoryType", "str,str")
        with self.assertRaisesRegex(ValueError, "type changed"):
            self.build(texts)

    def test_no_named_duty_or_roulette_is_not_an_empty_success(self):
        for sheet in ("ContentFinderCondition", "ContentRoulette"):
            texts = synthetic_sources()
            parsed = list(csv.reader(io.StringIO(texts[sheet])))
            index = parsed[1].index("Name")
            for row in parsed[3:]:
                row[index] = ""
            buffer = io.StringIO()
            csv.writer(buffer).writerows(parsed)
            texts[sheet] = buffer.getvalue()
            with self.subTest(sheet=sheet), self.assertRaises(ValueError):
                self.build(texts)

    def test_source_commit_build_and_each_pinned_hash_are_required(self):
        for field, value in (("commit", "main"), ("commit_message", "unknown"),
                             ("repository", "https://github.com/another/source"),
                             ("requests", [])):
            fetched = provenance()
            fetched[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.build(fetched=fetched)

    def test_column_and_source_row_reordering_are_deterministic(self):
        expected = self.build()
        texts = synthetic_sources()
        for sheet, text in texts.items():
            parsed = list(csv.reader(io.StringIO(text)))
            order = [0, *range(len(parsed[1]) - 1, 0, -1)]
            parsed = [[row[index] for index in order] for row in parsed]
            buffer = io.StringIO()
            csv.writer(buffer).writerows([*parsed[:3], *reversed(parsed[3:])])
            texts[sheet] = buffer.getvalue()
        self.assertEqual(expected, self.build(texts))

    def test_cli_uses_tc_only_transport_and_rebuilds_identical_files(self):
        with tempfile.TemporaryDirectory() as root:
            args = ["--tc-catalogues", "--version", "2026-10-24", "--tc-ref", COMMIT,
                    "--tc-fetched-at", STAMP, "--out-dir", root,
                    "--roulettes-out-dir", root, "--raw-dir", os.path.join(root, "raw")]
            with mock.patch.object(generate, "fetch_datamining_sheets",
                                   return_value=(synthetic_sources(), provenance())) as fetched, \
                    mock.patch.object(generate, "fetch_english_rows", side_effect=AssertionError), \
                    mock.patch.object(generate, "fetch_chinese_names", side_effect=AssertionError), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, generate.main(args))
                before = {name: (Path(root) / name).read_bytes()
                          for name in ("tc.2026-10-24.json", "tc.json")}
                self.assertEqual(0, generate.main(args))
            for name, body in before.items():
                self.assertEqual(body, (Path(root) / name).read_bytes())
            self.assertEqual(COMMIT, fetched.call_args.args[1])
            self.assertFalse((Path(root) / "cn.2026-10-24.json").exists())

    def test_cli_validation_failure_keeps_both_existing_files(self):
        texts = synthetic_sources()
        del texts["ContentRoulette"]
        with tempfile.TemporaryDirectory() as root:
            for name in ("tc.2026-10-24.json", "tc.json"):
                (Path(root) / name).write_text("existing", encoding="utf-8")
            with mock.patch.object(generate, "fetch_datamining_sheets",
                                   return_value=(texts, provenance())), \
                    contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(1, generate.main([
                    "--tc-catalogues", "--version", "2026-10-24", "--out-dir", root,
                    "--roulettes-out-dir", root, "--raw-dir", os.path.join(root, "raw")]))
            for name in ("tc.2026-10-24.json", "tc.json"):
                self.assertEqual("existing", (Path(root) / name).read_text(encoding="utf-8"))

    def test_cli_dry_run_keeps_no_raw_download_or_output(self):
        with mock.patch.object(generate, "fetch_datamining_sheets",
                               return_value=(synthetic_sources(), provenance())) as fetched, \
                mock.patch.object(generate, "write_document", side_effect=AssertionError), \
                mock.patch.object(generate.tempfile, "mkdtemp", side_effect=AssertionError), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, generate.main(["--tc-catalogues", "--dry-run"]))
        self.assertIsNone(fetched.call_args.args[3])

    def test_bundled_tc_rows_exclude_converted_missing_ids_and_keep_native_divergences(self):
        root = Path(__file__).resolve().parents[2]
        duty_doc = json.loads((root / "data/duties/tc.2026-10-24.json").read_text(encoding="utf-8"))
        names = json.loads((root / "data/i18n/game-names.2026-10-15.json").read_text(encoding="utf-8"))
        cn = json.loads((root / "data/duties/cn.2026-09-04.json").read_text(encoding="utf-8"))
        tc = {row["content_id"]: row for row in duty_doc["duties"]}
        converted = set(names["zh-TW_converted"]["duties"])
        self.assertEqual(76, len(converted))
        self.assertTrue(converted.isdisjoint(tc))
        self.assertEqual(converted, {row["content_id"] for row in cn["duties"]} - tc.keys())
        self.assertEqual(782, len(tc))
        self.assertEqual({1029}, tc.keys() - {row["content_id"] for row in cn["duties"]})
        for row_id, territory in {5: 172, 13: 171, 36: 434, 235: 616}.items():
            self.assertEqual(territory, tc[row_id]["territory_id"])
        self.assertNotEqual(tc[69]["territory_id"], tc[944]["territory_id"])
        self.assertNotIn("preferred_for_name_matching", tc[944])
        self.assertEqual(COMMIT, duty_doc["provenance"]["datamining_tc"]["commit"])
        self.assertEqual("2026.09.14.0000.0000", duty_doc["provenance"]["client_build"])


if __name__ == "__main__":
    unittest.main()
