#!/usr/bin/env python3
"""Offline tests for the parse / merge / write half of the game-name generator (game_names.py).

game_names.py never touches the network, and neither do these tests: they feed it the
synthetic rows and CSVs in sample/game-names/. The fetching half lives in generate.py and is
tested in test_generate.py with a fake transport. OpenCC is not required: the zh-TW fallback
is replaced by a marker function, and the one test that exercises the real package skips
when it is not installed.

    python tools/duty-data-generator/test_game_names.py
"""

from __future__ import annotations

import copy
import glob
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "sample", "game-names")
sys.path.insert(0, HERE)

import game_names  # noqa: E402

# A slice of generate.CATEGORY_BY_CONTENT_TYPE; test_generate.py runs the real table.
CATEGORIES = {"Dungeons": "四人迷宫", "Trials": "讨伐歼灭战", "Ultimate Raids": "绝境战"}


def mark(text):
    """Stand-in for OpenCC: makes a converted name recognisable in assertions."""
    return "[tw]" + text


def sample_path(*parts):
    return os.path.join(SAMPLE, *parts)


def load_json(*parts):
    with io.open(sample_path(*parts), "r", encoding="utf-8") as handle:
        return json.load(handle)


def csv_text(directory, sheet):
    with io.open(sample_path(directory, sheet + ".csv"), "rb") as handle:
        return handle.read().decode("utf-8-sig")


def xivapi_sheets():
    document = load_json("xivapi.json")
    return {sheet: {language: {row["row_id"]: row["fields"] for row in document[sheet][language]}
                    for language in game_names.XIVAPI_LANGUAGES}
            for sheet in game_names.XIVAPI_FIELDS}


def csv_sheets(directory, columns):
    return game_names.parse_sheets({sheet: csv_text(directory, sheet) for sheet in columns},
                                   columns)


def base():
    rows, _, _ = game_names.load_base(sample_path("base-duties.json"),
                                      sample_path("base-jobs.json"),
                                      sample_path("base-roulettes.json"))
    return rows


class ParseSheetCsvTests(unittest.TestCase):
    def test_reads_requested_columns_by_name(self):
        rows = game_names.parse_sheet_csv(csv_text("tc", "ClassJob"), ("Name", "Abbreviation"),
                                          "ClassJob")

        self.assertEqual({"Name": "樣例騎士", "Abbreviation": "PLD"}, rows[19])
        self.assertEqual({"Name": "", "Abbreviation": ""}, rows[0])

    def test_a_missing_column_fails_loudly(self):
        text = "key,0\n#,Something\nint32,str\n7,x\n"

        with self.assertRaises(game_names.SourceError) as caught:
            game_names.parse_sheet_csv(text, ("Name",), "Sample")

        self.assertIn("Name", str(caught.exception))

    def test_a_file_without_a_header_fails_and_junk_rows_are_skipped(self):
        with self.assertRaises(game_names.SourceError):
            game_names.parse_sheet_csv("key,0\n", ("Name",))

        rows = game_names.parse_sheet_csv("key,0\n#,Name\nint32,str\nx,bad\n\n4,ok\n", ("Name",))

        self.assertEqual({4: {"Name": "ok"}}, rows)


class TextRuleTests(unittest.TestCase):
    def test_only_the_first_character_is_capitalised(self):
        self.assertEqual("The Praetorium", game_names.capitalize_first("the Praetorium"))
        self.assertEqual("Sastasha", game_names.capitalize_first("Sastasha"))
        self.assertEqual("The Tam–Tara Deepcroft",
                         game_names.capitalize_first("the Tam–Tara Deepcroft"))
        self.assertEqual("(Savage) x", game_names.capitalize_first("(Savage) x"))
        self.assertEqual("", game_names.capitalize_first(""))

    def test_official_text_wins_and_the_fallback_is_marked(self):
        self.assertEqual(("官方", False), game_names.pick_zh_tw("官方", "官方简", mark))
        self.assertEqual(("[tw]简体", True), game_names.pick_zh_tw("", "简体", mark))
        self.assertEqual((None, False), game_names.pick_zh_tw(None, None, mark))

    def test_client_version_is_read_from_the_commit_message(self):
        self.assertEqual("2026.09.14.0000.0000",
                         game_names.client_version("ver 2026.09.14.0000.0000"))
        self.assertIsNone(game_names.client_version("init"))


class BuildDutiesTests(unittest.TestCase):
    def build(self, duties=None, tc=None):
        tc_rows = tc if tc is not None else csv_sheets("tc", game_names.TC_COLUMNS)[
            "ContentFinderCondition"]
        return game_names.build_duties(duties or base()["duties"],
                                       xivapi_sheets()["ContentFinderCondition"], tc_rows, mark)

    def test_every_base_duty_gets_all_three_languages(self):
        names, _, _ = self.build()

        self.assertEqual(["1", "2", "3", "5"], list(names))
        self.assertEqual({"zh-TW": "樣例迷宮一", "en": "Sample Dungeon One", "ja": "サンプル迷宮 一"},
                         names["1"])
        # A row XIVAPI has but the base file lacks is not added.
        self.assertNotIn("6", names)

    def test_english_names_get_a_capital_first_letter(self):
        names, _, _ = self.build()

        self.assertEqual("The Sample Trial Two", names["2"]["en"])
        self.assertEqual("The Sample Remaster Five", names["5"]["en"])

    def test_a_duty_the_tc_client_lacks_is_converted_and_listed(self):
        names, converted, _ = self.build()

        self.assertEqual("[tw]样例迷宫三", names["3"]["zh-TW"])
        self.assertEqual([3], converted)

    def test_a_remastered_territory_is_recorded_not_rejected(self):
        names, _, audit = self.build()

        self.assertEqual("樣例重製迷宮五", names["5"]["zh-TW"])
        self.assertEqual({"zh-TW": [5], "en": [], "ja": []}, audit.mismatches)
        # Duty 3 has no TC row, so the TC side compared three territories.
        self.assertEqual({"zh-TW": 3, "en": 4, "ja": 4}, audit.checked)

    def test_an_unreadable_territory_column_stops_the_run(self):
        tc = {content_id: dict(row, TerritoryType="n/a") for content_id, row in
              csv_sheets("tc", game_names.TC_COLUMNS)["ContentFinderCondition"].items()}

        with self.assertRaises(game_names.SourceError) as caught:
            self.build(tc=tc)

        self.assertIn("territory column changed", str(caught.exception))

    def test_territories_that_disagree_everywhere_stop_the_run(self):
        # Eight duties whose TC rows all sit one territory further on: shifted row ids.
        duties = [{"content_id": 100 + index, "territory_id": 1000 + index,
                   "localized_name": "样例%d" % index} for index in range(8)]
        rows = {row["content_id"]: {"Name": "x", "TerritoryType@as(raw)": row["territory_id"]}
                for row in duties}
        tc = {row["content_id"]: {"Name": "甲", "TerritoryType": str(row["territory_id"] + 1)}
              for row in duties}

        with self.assertRaises(game_names.SourceError) as caught:
            game_names.build_duties(duties, {"en": rows, "ja": rows}, tc, mark)

        self.assertIn("no longer line up", str(caught.exception))

    def test_a_duty_without_an_english_or_japanese_name_stops_the_run(self):
        duties = base()["duties"] + [{"content_id": 77, "territory_id": None,
                                      "localized_name": "样例七十七"}]

        with self.assertRaises(game_names.SourceError) as caught:
            self.build(duties=duties)

        self.assertIn("content_id 77", str(caught.exception))


class BuildJobsAndRoulettesTests(unittest.TestCase):
    def setUp(self):
        self.tc = csv_sheets("tc", game_names.TC_COLUMNS)
        self.xivapi = xivapi_sheets()

    def test_jobs_use_the_title_case_english_column(self):
        names, converted = game_names.build_jobs(base()["jobs"], self.xivapi["ClassJob"],
                                                 self.tc["ClassJob"], mark)

        self.assertEqual({"zh-TW": "樣例騎士", "en": "Sample Paladin", "ja": "サンプルナイト"},
                         names["19"])
        self.assertEqual("[tw]样例驯兽师", names["43"]["zh-TW"])
        self.assertEqual([43], converted)
        self.assertNotIn("abbr", names["1"])

    def test_a_differing_abbreviation_is_kept_per_language(self):
        xivapi = copy.deepcopy(self.xivapi["ClassJob"])
        xivapi["ja"][1]["Abbreviation"] = "剣"

        names, _ = game_names.build_jobs(base()["jobs"], xivapi, self.tc["ClassJob"], mark)

        self.assertEqual({"ja": "剣"}, names["1"]["abbr"])

    def test_roulettes_use_the_short_category_text(self):
        names, converted = game_names.build_roulettes(
            base()["roulettes"], self.xivapi["ContentRoulette"], self.tc["ContentRoulette"], mark)

        self.assertEqual({"zh-TW": "樣例指導者", "en": "Sample Mentor", "ja": "サンプルメンター"},
                         names["9"])
        self.assertEqual([], converted)


class BuildLabelTests(unittest.TestCase):
    def setUp(self):
        self.tc = csv_sheets("tc", game_names.TC_COLUMNS)
        self.cn = csv_sheets("cn", game_names.CN_COLUMNS)
        self.xivapi = xivapi_sheets()

    def test_categories_come_from_the_content_type_each_label_is_derived_from(self):
        labels, converted, project = game_names.build_duty_categories(
            {"四人迷宫", "讨伐歼灭战", "其他"}, self.xivapi["ContentType"],
            self.tc["ContentType"], mark, CATEGORIES)

        self.assertEqual({"zh-TW": "樣例迷宮挑戰", "en": "Dungeons", "ja": "サンプルダンジョン"},
                         labels["四人迷宫"])
        # The TC sample has no Trials row: the zh-Hans label is converted.
        self.assertEqual("[tw]讨伐歼灭战", labels["讨伐歼灭战"]["zh-TW"])
        self.assertEqual(["讨伐歼灭战"], converted)
        self.assertEqual({"zh-TW": "其他", "en": "Other", "ja": "その他"}, labels["其他"])
        self.assertEqual(["其他"], project)
        # Ultimate Raids is not in the sample's ContentType sheet and not used: skipped.
        self.assertNotIn("绝境战", labels)

    def test_a_used_category_without_its_content_type_stops_the_run(self):
        with self.assertRaises(game_names.SourceError):
            game_names.build_duty_categories({"绝境战"}, self.xivapi["ContentType"],
                                             self.tc["ContentType"], mark, CATEGORIES)

    def test_expansions_carry_zh_cn_as_well(self):
        labels, converted = game_names.build_expansions(
            {"A Realm Reborn", "Heavensward"}, self.xivapi["ExVersion"], self.tc["ExVersion"],
            self.cn["ExVersion"], mark)

        self.assertEqual({"zh-CN": "样例苍穹", "zh-TW": "樣例蒼天", "en": "Heavensward",
                          "ja": "サンプル蒼天"}, labels["Heavensward"])
        self.assertEqual([], converted)

    def test_a_used_expansion_without_names_stops_the_run(self):
        with self.assertRaises(game_names.SourceError):
            game_names.build_expansions({"Dawntrail"}, self.xivapi["ExVersion"],
                                        self.tc["ExVersion"], self.cn["ExVersion"], mark)


class DocumentTests(unittest.TestCase):
    def fetched(self, commit):
        return {"repository": "sample/repository", "ref": "main", "commit": commit,
                "commit_message": "ver 2026.09.14.0000.0000", "requests": []}

    def test_the_document_carries_every_map_counts_and_provenance(self):
        maps = game_names.build_maps(base(), xivapi_sheets(),
                                     csv_sheets("tc", game_names.TC_COLUMNS),
                                     csv_sheets("cn", game_names.CN_COLUMNS), mark, CATEGORIES)
        provenance = game_names.make_provenance(
            "2026-10-15T00:00:00.000Z", {"game_version": "sample-version"},
            self.fetched("a" * 40), self.fetched("b" * 40), {"method": "OpenCC"})

        document = game_names.build_document("2026-10-15", {"duties": "x"}, maps, provenance)

        self.assertEqual({"duties": [3], "jobs": [43], "roulettes": [],
                          "duty_categories": ["讨伐歼灭战"], "expansions": []},
                         document["zh-TW_converted"])
        self.assertEqual("2026.09.14.0000.0000",
                         document["provenance"]["datamining_tc"]["client_version"])
        self.assertEqual({"total": 4, "zh_tw_official": 3, "zh_tw_converted": 1},
                         document["provenance"]["row_counts"]["duties"])
        self.assertEqual({"zh-TW": [5], "en": [], "ja": []},
                         document["provenance"]["territory_mismatches"])
        self.assertIn("SQUARE ENIX", document["source"])

    def test_an_empty_base_list_stops_the_run(self):
        with tempfile.TemporaryDirectory() as directory:
            jobs = os.path.join(directory, "jobs.json")
            with io.open(jobs, "w", encoding="utf-8") as handle:
                json.dump({"jobs": []}, handle)

            with self.assertRaises(game_names.SourceError) as caught:
                game_names.load_base(sample_path("base-duties.json"), jobs,
                                     sample_path("base-roulettes.json"))

        self.assertIn("jobs", str(caught.exception))

    def test_load_base_reports_the_pinned_game_version(self):
        _, data_version, pinned = game_names.load_base(sample_path("base-duties.json"),
                                                       sample_path("base-jobs.json"),
                                                       sample_path("base-roulettes.json"))

        self.assertEqual(("2026-09-04", "sample-version"), (data_version, pinned))

    def test_repository_paths_are_relative_with_forward_slashes(self):
        inside = os.path.join(game_names.REPO, "data", "jobs", "jobs.json")

        self.assertEqual("data/jobs/jobs.json", game_names.repository_path(inside))
        sibling = game_names.REPO.rstrip("\\/") + "-other"
        self.assertFalse(game_names.repository_path(sibling).startswith(".."))


class RenderTests(unittest.TestCase):
    def test_render_round_trips_and_puts_one_id_per_line(self):
        document = {"schema_version": 1, "provenance": {"a": [1, 2]},
                    "duties": {"1": {"zh-TW": "甲", "en": "A", "ja": "あ"},
                               "2": {"zh-TW": "乙", "en": "B", "ja": "い"}},
                    "zh-TW_converted": {"duties": [2], "jobs": []},
                    "project_maintained": {}}

        text = game_names.render(document)

        self.assertEqual(document, json.loads(text))
        self.assertIn('    "1": {"zh-TW": "甲", "en": "A", "ja": "あ"},\n', text)
        self.assertIn('    "duties": [2],\n', text)
        self.assertTrue(text.endswith("}\n"))

    def test_a_document_that_does_not_round_trip_leaves_the_existing_file_alone(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "game-names.2026-10-15.json")
            with io.open(path, "w", encoding="utf-8") as handle:
                handle.write("previous")

            with mock.patch.object(game_names, "render", return_value='{"other": 1}\n'):
                with self.assertRaises(ValueError):
                    game_names.write_document(path, {"duties": {}})

            with io.open(path, "r", encoding="utf-8") as handle:
                self.assertEqual("previous", handle.read())
            self.assertEqual(["game-names.2026-10-15.json"], os.listdir(directory))

    def test_a_written_file_parses_back_to_the_document(self):
        document = {"schema_version": 1, "duties": {"1": {"zh-TW": "甲", "en": "A", "ja": "あ"}}}
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "out.json")

            game_names.write_document(path, document)

            with io.open(path, "rb") as handle:
                body = handle.read()
        self.assertEqual(document, json.loads(body.decode("utf-8")))
        self.assertNotIn(b"\r\n", body)


class OpenCcTests(unittest.TestCase):
    def test_the_real_converter_produces_taiwan_traditional_text(self):
        if importlib.util.find_spec("opencc") is None:
            self.skipTest("OpenCC is not installed; the fallback is tested with a stand-in")
        converter = game_names.Converter()

        self.assertEqual("樣例迷宮", converter("样例迷宫"))
        self.assertEqual("s2twp", converter.info["config"])

    def test_a_missing_opencc_names_the_package_to_install(self):
        with mock.patch.dict(sys.modules, {"opencc": None}):
            with self.assertRaises(game_names.SourceError) as caught:
                game_names.Converter()("样例")

        self.assertIn("pip install opencc", str(caught.exception))


class BundledDataTests(unittest.TestCase):
    """The committed data/i18n file, offline: complete for the base files it names."""

    def latest(self):
        paths = sorted(glob.glob(os.path.join(game_names.REPO, "data", "i18n",
                                              "game-names.*.json")))
        if not paths:
            self.skipTest("no data/i18n/game-names.*.json bundled yet")
        with io.open(paths[-1], "r", encoding="utf-8") as handle:
            return json.load(handle)

    def base_file(self, document, key):
        return game_names.load_json(os.path.join(game_names.REPO,
                                                 document["provenance"]["base_files"][key]))

    def assert_complete(self, entries, ids, what):
        self.assertEqual(sorted(str(item) for item in ids), sorted(entries), what)
        for key, entry in entries.items():
            for language in game_names.LANGUAGES:
                self.assertTrue((entry.get(language) or "").strip(), (what, key, language))

    def test_every_id_of_the_base_files_has_all_three_languages(self):
        document = self.latest()
        duties = self.base_file(document, "duties")["duties"]
        self.assert_complete(document["duties"], [row["content_id"] for row in duties], "duties")
        jobs = self.base_file(document, "jobs")["jobs"]
        self.assert_complete(document["jobs"], [row["job_id"] for row in jobs], "jobs")
        roulettes = self.base_file(document, "roulettes")["roulettes"]
        self.assert_complete(document["roulettes"], [row["roulette_id"] for row in roulettes],
                             "roulettes")
        for label in {row["duty_category"] for row in duties}:
            self.assertIn(label, document["duty_categories"])
        for label in {row["expansion"] for row in duties}:
            self.assertIn(label, document["expansions"])

    def test_the_newest_file_covers_the_newest_duty_file(self):
        # A new data/duties/cn.<date>.json without regenerated names fails here, not in the UI.
        document = self.latest()

        self.assertEqual(game_names.repository_path(game_names.latest_duty_file()),
                         document["provenance"]["base_files"]["duties"])

    def test_converted_and_project_lists_only_name_existing_entries(self):
        document = self.latest()
        for key, listed in document["zh-TW_converted"].items():
            self.assertTrue({str(item) for item in listed} <= set(document[key]), key)
        for key, listed in document["project_maintained"].items():
            self.assertTrue(set(listed) <= set(document[key]), key)


if __name__ == "__main__":
    unittest.main(verbosity=2)
