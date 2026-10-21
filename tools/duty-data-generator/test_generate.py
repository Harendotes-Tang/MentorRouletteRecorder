#!/usr/bin/env python3
"""Offline tests for the duty data generator.

They run against the tiny saved sample in sample/ and make no network request, so they can
run in CI on a machine with no outbound access at all. The --game-names fetching tests use
the synthetic sources in sample/game-names/ through a fake transport routed by the URL roots
generate.py declares; the offline merge is covered by test_game_names.py.

    python tools/duty-data-generator/test_generate.py
"""

from __future__ import annotations

import contextlib
import io
import itertools
import json
import os
import sys
import tempfile
import unittest
import urllib.error
import urllib.parse
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "sample")
GAME_NAMES_SAMPLE = os.path.join(SAMPLE, "game-names")
sys.path.insert(0, HERE)

import game_names  # noqa: E402
import generate  # noqa: E402


def load_sample_rows():
    with io.open(os.path.join(SAMPLE, "xivapi_page.json"), "r", encoding="utf-8") as handle:
        document = json.load(handle)
    return {int(row["row_id"]): row for row in document["rows"]}


def load_sample_names():
    with io.open(os.path.join(SAMPLE, "ContentFinderCondition.cn.csv"), "r",
                 encoding="utf-8-sig") as handle:
        return generate.parse_cn_csv(handle.read())


class ParseCnCsvTests(unittest.TestCase):
    def test_reads_names_by_row_id_and_skips_blanks(self):
        names = load_sample_names()

        self.assertEqual({1: "样例迷宫一", 2: "样例讨伐二"}, names)

    def test_locates_the_name_column_by_name_not_by_index(self):
        text = "key,0,1\n#,Something,Name\nint32,str,str\n7,x,名字\n"

        self.assertEqual({7: "名字"}, generate.parse_cn_csv(text))

    def test_a_schema_without_a_name_column_fails_loudly(self):
        text = "key,0\n#,Something\nint32,str\n7,x\n"

        with self.assertRaises(ValueError):
            generate.parse_cn_csv(text)


class BuildRowsTests(unittest.TestCase):
    def setUp(self):
        self.rows = load_sample_rows()
        self.names = load_sample_names()

    def test_global_rows_use_english_names_and_skip_unnamed_rows(self):
        duties = generate.build_rows(self.rows, self.names, "en")

        self.assertEqual([1, 2, 4], [row["content_id"] for row in duties])
        self.assertEqual("Sample Dungeon One", duties[0]["localized_name"])

    def test_cn_rows_use_chinese_names_and_never_fall_back_to_english(self):
        duties = generate.build_rows(self.rows, self.names, "zh-Hans")

        self.assertEqual([1, 2], [row["content_id"] for row in duties])
        self.assertEqual("样例迷宫一", duties[0]["localized_name"])
        self.assertNotIn(
            "Sample Unmapped Four", [row["localized_name"] for row in duties])

    def test_categories_come_from_content_type_and_unmapped_stays_other(self):
        duties = {row["content_id"]: row for row in generate.build_rows(self.rows, self.names, "en")}

        self.assertEqual("四人迷宫", duties[1]["duty_category"])
        self.assertEqual("讨伐歼灭战", duties[2]["duty_category"])
        self.assertEqual(generate.UNKNOWN_CATEGORY, duties[4]["duty_category"])

    def test_territory_expansion_and_level_are_carried_through(self):
        duties = {row["content_id"]: row for row in generate.build_rows(self.rows, self.names, "en")}

        self.assertEqual(1039, duties[1]["territory_id"])
        self.assertEqual("A Realm Reborn", duties[1]["expansion"])
        self.assertEqual(24, duties[1]["level"])
        self.assertEqual("Heavensward", duties[2]["expansion"])
        self.assertTrue(duties[1]["enabled"])

    def test_party_size_is_members_per_party_times_party_count(self):
        duties = {row["content_id"]: row for row in generate.build_rows(self.rows, self.names, "en")}

        self.assertEqual(4, duties[1]["party_size"])
        self.assertEqual(24, duties[2]["party_size"])
        # A row that does not state its composition is null, not a guess.
        self.assertIsNone(duties[4]["party_size"])

    def test_party_size_rejects_malformed_composition(self):
        for fields in ({}, {"MembersPerParty": 8}, {"MembersPerParty": 0, "PartyCount": 1},
                       {"MembersPerParty": True, "PartyCount": 1},
                       {"MembersPerParty": "8", "PartyCount": 1}):
            self.assertIsNone(generate.party_size({"fields": fields}), fields)
        self.assertIsNone(generate.party_size(None))
        self.assertEqual(8, generate.party_size({"fields": {"MembersPerParty": 8, "PartyCount": 1}}))

    def test_rows_are_sorted_by_content_id_so_diffs_stay_readable(self):
        duties = generate.build_rows(self.rows, self.names, "en")

        self.assertEqual(sorted(row["content_id"] for row in duties),
                         [row["content_id"] for row in duties])


class FetchEnglishRowsTests(unittest.TestCase):
    """The paging loop, with a fake XIVAPI: it must never hand back a half-read sheet.

    The other tests build english_rows by hand and never touch this path, which is exactly how a
    silent truncation could ship: a data/duties/*.json short of entries only shows up as a duty
    the Collector cannot name.
    """

    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="duty-data-test-")
        self.addCleanup(directory.cleanup)
        self.raw_dir = directory.name
        self.addCleanup(setattr, generate, "http_get", generate.http_get)

    def serve(self, rows_on_page, version_on_page=lambda page: "sample-version"):
        """Replace the module's HTTP call with pages of rows_on_page(page_number) rows, each
        reporting the game version version_on_page(page_number)."""
        self.requests = []
        row_ids = itertools.count(1)

        def fake_get(url, timeout=60):
            self.requests.append(url)
            page = len(self.requests)
            rows = [{"row_id": next(row_ids), "fields": {}} for _ in range(rows_on_page(page))]
            return json.dumps({"version": version_on_page(page), "rows": rows}).encode("utf-8")

        generate.http_get = fake_get

    def test_a_short_last_page_ends_the_paging_and_returns_every_row(self):
        self.serve(lambda page: generate.XIVAPI_PAGE_SIZE if page == 1 else 3)

        rows, urls, digest, api_version = generate.fetch_english_rows(self.raw_dir)

        self.assertEqual(generate.XIVAPI_PAGE_SIZE + 3, len(rows))
        self.assertEqual(2, len(urls))
        self.assertEqual("sample-version", api_version)
        self.assertEqual(64, len(digest))

    def test_an_empty_page_ends_the_paging(self):
        self.serve(lambda page: generate.XIVAPI_PAGE_SIZE if page == 1 else 0)

        rows, urls, _, _ = generate.fetch_english_rows(self.raw_dir)

        self.assertEqual(generate.XIVAPI_PAGE_SIZE, len(rows))
        self.assertEqual(2, len(urls))

    def test_a_full_last_allowed_page_fails_instead_of_returning_a_truncated_sheet(self):
        self.serve(lambda page: generate.XIVAPI_PAGE_SIZE)

        with self.assertRaises(ValueError) as caught:
            generate.fetch_english_rows(self.raw_dir)

        self.assertEqual(generate.XIVAPI_MAX_PAGES, len(self.requests))
        self.assertIn("XIVAPI_MAX_PAGES", str(caught.exception))
        # ValueError is what main() and add_party_size_to_files() already catch: both print the
        # failure and return 1 without writing a data/duties file.

    def test_pages_from_two_game_versions_are_refused(self):
        # N3-4 (audit 2026-10-20): without --game-version no page is pinned, and on patch day the
        # first pages can come from the old game data and the rest from the new; the file would
        # then mix both while provenance named only the last page's version.
        self.serve(lambda page: generate.XIVAPI_PAGE_SIZE if page == 1 else 3,
                   lambda page: "7.30" if page == 1 else "7.35")

        with self.assertRaises(ValueError) as caught:
            generate.fetch_english_rows(self.raw_dir)

        self.assertIn("7.30", str(caught.exception))
        self.assertIn("7.35", str(caught.exception))

    def test_a_page_without_a_game_version_is_refused(self):
        # Without the version a page cannot be shown to match the others: fail closed, whether the
        # first page or a later one leaves it out or leaves it empty.
        for name, version_on_page in (
                ("first page missing", lambda page: None if page == 1 else "7.35"),
                ("first page empty", lambda page: "" if page == 1 else "7.35"),
                ("later page missing", lambda page: "7.35" if page == 1 else None),
                ("every page missing", lambda page: None)):
            with self.subTest(name):
                self.serve(lambda page: generate.XIVAPI_PAGE_SIZE if page == 1 else 3, version_on_page)

                with self.assertRaises(ValueError) as caught:
                    generate.fetch_english_rows(self.raw_dir)

                self.assertIn("没有给出游戏版本", str(caught.exception))

    def test_a_pinned_version_the_answer_does_not_carry_is_refused(self):
        self.serve(lambda page: 3, lambda page: "7.35")

        with self.assertRaises(ValueError) as caught:
            generate.fetch_english_rows(self.raw_dir, "7.30")

        self.assertIn("7.30", str(caught.exception))
        self.assertIn("version=7.30", self.requests[0])

    def test_a_pinned_version_answered_on_every_page_is_accepted(self):
        self.serve(lambda page: generate.XIVAPI_PAGE_SIZE if page == 1 else 3, lambda page: "7.30")

        rows, urls, _, api_version = generate.fetch_english_rows(self.raw_dir, "7.30")

        self.assertEqual("7.30", api_version)
        self.assertEqual(2, len(urls))


class BuildDocumentTests(unittest.TestCase):
    def test_document_carries_provenance_and_is_not_a_sample(self):
        rows = load_sample_rows()
        names = load_sample_names()
        provenance = generate.make_provenance(
            ["https://example.invalid/page1"],
            {"xivapi": "a" * 64, "cn": "b" * 64},
            {"xivapi_rows": len(rows), "datamining_cn_rows": len(names)},
            "sample-version")

        document = generate.build_document(
            "CN", "zh-Hans", "2026-09-04", generate.build_rows(rows, names, "zh-Hans"), provenance)

        self.assertEqual(1, document["schema_version"])
        self.assertEqual("CN", document["region"])
        self.assertEqual("2026-09-04", document["data_version"])
        self.assertFalse(document["sample"])
        self.assertIn("SQUARE ENIX", document["source"])
        self.assertEqual("a" * 64, document["provenance"]["xivapi_sha256"])
        self.assertEqual("b" * 64, document["provenance"]["datamining_cn_sha256"])
        self.assertEqual(2, len(document["duties"]))


class WithPartySizeTests(unittest.TestCase):
    def existing_document(self):
        # A file generated before party_size existed; row 9 is not in the sample fetch.
        return {
            "schema_version": 1,
            "data_version": "2026-09-04",
            "provenance": {"xivapi_game_version": "sample-version", "xivapi_sha256": "a" * 64},
            "duties": [
                {"content_id": 2, "territory_id": 1040, "localized_name": "样例讨伐二",
                 "duty_category": "讨伐歼灭战", "expansion": "Heavensward", "level": 50,
                 "enabled": True},
                {"content_id": 1, "territory_id": 1039, "localized_name": "样例迷宫一",
                 "duty_category": "四人迷宫", "expansion": "A Realm Reborn", "level": 24,
                 "enabled": True},
                {"content_id": 9, "localized_name": "无等级", "enabled": False},
            ],
        }

    def test_adds_party_size_after_level_and_keeps_everything_else(self):
        before = self.existing_document()
        source = {"xivapi_game_version": "sample-version", "xivapi_sha256": "c" * 64}

        after = generate.with_party_size(before, load_sample_rows(), source)

        self.assertEqual([2, 1, 9], [row["content_id"] for row in after["duties"]])
        self.assertEqual(
            ["content_id", "territory_id", "localized_name", "duty_category", "expansion",
             "level", "party_size", "enabled"],
            list(after["duties"][0].keys()))
        self.assertEqual(24, after["duties"][0]["party_size"])
        self.assertEqual(4, after["duties"][1]["party_size"])
        # No level: inserted before enabled. Not in the fetch: null.
        self.assertEqual(["content_id", "localized_name", "party_size", "enabled"],
                         list(after["duties"][2].keys()))
        self.assertIsNone(after["duties"][2]["party_size"])
        for original, updated in zip(before["duties"], after["duties"]):
            self.assertEqual(original, {k: v for k, v in updated.items() if k != "party_size"})
        self.assertEqual("a" * 64, after["provenance"]["xivapi_sha256"])
        self.assertEqual("c" * 64, after["provenance"]["party_size"]["xivapi_sha256"])
        self.assertEqual(1, after["provenance"]["party_size"]["rows_without_party_size"])
        # The input document is not modified.
        self.assertNotIn("party_size", before["duties"][0])
        self.assertNotIn("party_size", before["provenance"])

    def test_running_twice_replaces_rather_than_duplicates(self):
        rows = load_sample_rows()
        once = generate.with_party_size(self.existing_document(), rows, {})
        twice = generate.with_party_size(once, rows, {})

        self.assertEqual(once["duties"], twice["duties"])

    def test_cli_dry_run_reports_party_sizes_without_rewriting_existing_data(self):
        for dry_run in (True, False):
            with self.subTest(dry_run=dry_run), tempfile.TemporaryDirectory() as directory:
                path = os.path.join(directory, "existing.json")
                before = json.dumps(self.existing_document(), ensure_ascii=False).encode("utf-8")
                with open(path, "wb") as handle:
                    handle.write(before)
                args = ["--add-party-size", path, "--raw-dir", directory]
                if dry_run:
                    args.append("--dry-run")
                output = io.StringIO()
                response = (load_sample_rows(), ["https://example.invalid/page"], "c" * 64,
                            "sample-version")
                with mock.patch.object(generate, "fetch_english_rows", return_value=response) as fetch, \
                        contextlib.redirect_stdout(output):
                    code = generate.main(args)
                self.assertEqual(0, code)
                # A dry run keeps no raw download, so it hands the fetcher no directory at all.
                fetch.assert_called_once_with(None if dry_run else directory, "sample-version")
                self.assertIn("party_size", output.getvalue())
                with open(path, "rb") as handle:
                    after = handle.read()
                if dry_run:
                    self.assertEqual(before, after)
                    self.assertIn("dry run", output.getvalue())
                else:
                    self.assertEqual(24, json.loads(after)["duties"][0]["party_size"])


class DryRunTests(unittest.TestCase):
    """--dry-run writes nothing anywhere, and raw downloads never land in the repository.

    Audit 2026-10-03 (OX-7 / ON2-8): the ordinary dry run used to save both raw downloads to
    --raw-dir, or to a fresh mkdtemp directory it then left behind.
    """

    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="duty-data-dry-run-")
        self.addCleanup(directory.cleanup)
        self.scratch = directory.name
        with io.open(os.path.join(SAMPLE, "xivapi_page.json"), "rb") as handle:
            self.page = handle.read()
        with io.open(os.path.join(SAMPLE, "ContentFinderCondition.cn.csv"), "rb") as handle:
            self.csv = handle.read()
        self.requests = []

    def fake_get(self, url, timeout=60):
        self.requests.append(url)
        return self.csv if url == generate.DATAMINING_CN_CSV else self.page

    def run_main(self, args):
        output = io.StringIO()
        with mock.patch.object(generate, "http_get", side_effect=self.fake_get), \
                mock.patch.object(generate.tempfile, "mkdtemp",
                                  side_effect=AssertionError("a dry run made a temp directory")), \
                contextlib.redirect_stdout(output):
            code = generate.main(args)
        return code, output.getvalue()

    def test_an_ordinary_dry_run_fetches_both_sources_and_writes_nothing(self):
        out_dir = os.path.join(self.scratch, "out")

        code, output = self.run_main(["--dry-run", "--out-dir", out_dir])

        self.assertEqual(0, code)
        self.assertEqual(2, len(self.requests))
        self.assertIn("dry run: nothing written", output)
        self.assertIn("cn duties:          2", output)
        self.assertFalse(os.path.exists(out_dir))
        self.assertEqual([], os.listdir(self.scratch))

    def test_a_dry_run_leaves_an_explicit_raw_dir_untouched(self):
        existing = os.path.join(self.scratch, "raw")
        os.makedirs(existing)
        missing = os.path.join(self.scratch, "missing")

        for raw_dir in (existing, missing):
            with self.subTest(raw_dir=raw_dir):
                code, _ = self.run_main(["--dry-run", "--raw-dir", raw_dir,
                                         "--out-dir", os.path.join(self.scratch, "out")])

                self.assertEqual(0, code)
        self.assertEqual([], os.listdir(existing))
        self.assertFalse(os.path.exists(missing))
        self.assertEqual(["raw"], os.listdir(self.scratch))

    def test_a_raw_dir_inside_the_repository_is_refused_before_any_fetch(self):
        # Only directories that already exist, and a fetch that fails: even a generator that
        # does not refuse cannot write a single byte into the repository from this test.
        def no_fetch(url, timeout=60):
            raise AssertionError("fetched %s before refusing --raw-dir" % url)

        for raw_dir in (generate.REPO, os.path.join(generate.REPO, "tools"),
                        os.path.join(generate.REPO, "tools", "..", "data")):
            for extra in ([], ["--dry-run"], ["--add-party-size", "unused.json"]):
                with self.subTest(raw_dir=raw_dir, extra=extra), \
                        mock.patch.object(generate, "http_get", side_effect=no_fetch), \
                        contextlib.redirect_stderr(io.StringIO()) as errors:
                    with self.assertRaises(SystemExit) as caught:
                        generate.main(["--raw-dir", raw_dir,
                                       "--out-dir", os.path.join(self.scratch, "out")] + extra)

                    self.assertEqual(2, caught.exception.code)
                    self.assertIn("inside the repository", errors.getvalue())
        self.assertEqual([], os.listdir(self.scratch))

    def test_a_temporary_directory_inside_the_repository_is_refused_before_any_fetch(self):
        # Without --raw-dir the raw downloads go to a fresh directory under the temporary
        # directory, which TMP/TEMP choose; one pointing into the repository used to put the
        # downloads there unchecked (audit 2026-10-03, R2T-13). mkdtemp fails the test if called.
        def no_fetch(url, timeout=60):
            raise AssertionError("fetched %s before refusing the temporary directory" % url)

        for scratch in (generate.REPO, os.path.join(generate.REPO, "tools")):
            for extra in ([], ["--add-party-size", "unused.json"]):
                with self.subTest(scratch=scratch, extra=extra), \
                        mock.patch.object(generate.tempfile, "tempdir", scratch), \
                        mock.patch.object(generate.tempfile, "mkdtemp",
                                          side_effect=AssertionError("made a raw directory")), \
                        mock.patch.object(generate, "http_get", side_effect=no_fetch), \
                        contextlib.redirect_stderr(io.StringIO()) as errors:
                    with self.assertRaises(SystemExit) as caught:
                        generate.main(["--out-dir", os.path.join(self.scratch, "out")] + extra)

                    self.assertEqual(2, caught.exception.code)
                    self.assertIn("inside the repository", errors.getvalue())
        self.assertEqual([], os.listdir(self.scratch))

    def test_the_default_raw_directory_is_made_under_the_temporary_directory(self):
        made = []

        def fake_mkdtemp(prefix=None, dir=None):
            made.append((prefix, dir))
            path = os.path.join(self.scratch, "raw")
            os.makedirs(path)
            return path

        out_dir = os.path.join(self.scratch, "out")
        with mock.patch.object(generate.tempfile, "tempdir", self.scratch), \
                mock.patch.object(generate.tempfile, "mkdtemp", side_effect=fake_mkdtemp), \
                mock.patch.object(generate, "http_get", side_effect=self.fake_get), \
                contextlib.redirect_stdout(io.StringIO()):
            code = generate.main(["--out-dir", out_dir, "--version", "2026-10-03"])

        self.assertEqual(0, code)
        self.assertEqual([("duty-data-", self.scratch)], made)
        self.assertEqual(2, len(os.listdir(os.path.join(self.scratch, "raw"))))

    def test_only_the_repository_itself_counts_as_inside(self):
        sibling = generate.REPO.rstrip("\\/") + "-raw"

        self.assertTrue(generate.is_inside_repository(os.path.join(generate.REPO, "x")))
        self.assertFalse(generate.is_inside_repository(sibling))
        self.assertFalse(generate.is_inside_repository(self.scratch))


SAMPLE_VERSION = "sample-version"
TC_SHA = "a" * 40
CN_SHA = "b" * 40


def mark(text):
    """Stand-in for OpenCC: makes a converted name recognisable in assertions."""
    return "[tw]" + text


def game_names_sample(*parts):
    return os.path.join(GAME_NAMES_SAMPLE, *parts)


class FakeNameSources:
    """Serves the --game-names sources from sample/game-names/.

    Requests are routed by the URL roots generate.py declares, so a URL built from anything
    else fails the test instead of reaching a host.
    """

    def __init__(self, version=SAMPLE_VERSION, page_size=None):
        self.version = version
        self.page_size = page_size
        self.requests = []
        with io.open(game_names_sample("xivapi.json"), "r", encoding="utf-8") as handle:
            self.xivapi = json.load(handle)

    def __call__(self, url, timeout=60):
        self.requests.append(url)
        if url == generate.XIVAPI_ROOT + "/version":
            return self.encode({"versions": [
                {"key": SAMPLE_VERSION, "names": ["7.55x2"]},
                {"key": "sample-latest", "names": ["7.57", "latest"]}]})
        if url.startswith(generate.XIVAPI_ROOT + "/sheet/"):
            return self.sheet(url)
        if url.startswith(generate.GITHUB_API_ROOT + "/repos/"):
            sha = TC_SHA if game_names.TC_REPOSITORY in url else CN_SHA
            return self.encode([{"sha": sha, "commit": {"message": "ver 2026.09.14.0000.0000"}}])
        if url.startswith(generate.DATAMINING_RAW_ROOT + "/"):
            folder = "tc" if game_names.TC_REPOSITORY in url else "cn"
            with io.open(game_names_sample(folder, url.rsplit("/", 1)[1]), "rb") as handle:
                return handle.read()
        raise AssertionError("unexpected request %s" % url)

    def sheet(self, url):
        parsed = urllib.parse.urlparse(url)
        query = dict(urllib.parse.parse_qsl(parsed.query))
        rows = self.xivapi[parsed.path.rsplit("/", 1)[1]][query["language"]]
        rows = [row for row in rows if row["row_id"] > int(query.get("after", -1))]
        limit = self.page_size or int(query["limit"])
        return self.encode({"version": self.version, "rows": rows[:limit]})

    @staticmethod
    def encode(document):
        return json.dumps(document, ensure_ascii=False).encode("utf-8")


class GameNamesFetchTests(unittest.TestCase):
    """The fetching half of --game-names, against a fake transport."""

    def test_paging_starts_without_after_and_continues_from_the_last_row(self):
        sources = FakeNameSources(page_size=2)
        requests = []
        with mock.patch.object(generate, "http_get", side_effect=sources), \
                mock.patch.object(generate, "XIVAPI_PAGE_SIZE", 2):
            rows = generate.fetch_name_sheet("ContentFinderCondition", "Name", "en",
                                             SAMPLE_VERSION, requests, None)

        self.assertEqual([0, 1, 2, 3, 5, 6], sorted(rows))
        self.assertNotIn("after=", sources.requests[0])
        self.assertIn("after=1", sources.requests[1])
        self.assertIn("version=sample-version", sources.requests[0])
        self.assertIn("language=en", sources.requests[0])
        self.assertEqual(sources.requests, [entry["url"] for entry in requests])
        self.assertTrue(all(len(entry["sha256"]) == 64 for entry in requests))

    def test_an_answer_from_another_game_version_stops_the_run(self):
        with mock.patch.object(generate, "http_get", side_effect=FakeNameSources("other")):
            with self.assertRaises(ValueError):
                generate.fetch_name_sheet("ExVersion", "Name", "ja", SAMPLE_VERSION, [], None)

    def test_a_sheet_longer_than_the_page_limit_fails_instead_of_truncating(self):
        def endless(url, timeout=60):
            query = dict(urllib.parse.parse_qsl(urllib.parse.urlparse(url).query))
            start = int(query.get("after", 0)) + 1
            return FakeNameSources.encode({"version": SAMPLE_VERSION, "rows": [
                {"row_id": start, "fields": {}}, {"row_id": start + 1, "fields": {}}]})

        with mock.patch.object(generate, "http_get", side_effect=endless), \
                mock.patch.object(generate, "XIVAPI_PAGE_SIZE", 2):
            with self.assertRaises(ValueError) as caught:
                generate.fetch_name_sheet("ExVersion", "Name", "en", None, [], None)

        self.assertIn("XIVAPI_MAX_PAGES", str(caught.exception))

    def test_a_dropped_connection_is_retried_and_a_404_is_not(self):
        for failure in (urllib.error.URLError("reset"),
                        generate.http.client.IncompleteRead(b"partial")):
            calls = []

            def flaky(url, timeout=60, failure=failure):
                calls.append(url)
                if len(calls) == 1:
                    raise failure
                return b"ok"

            with self.subTest(failure=type(failure).__name__), \
                    mock.patch.object(generate, "http_get", side_effect=flaky), \
                    mock.patch.object(generate.time, "sleep"):
                self.assertEqual(b"ok", generate.http_get_retrying("https://example.invalid/a"))
                self.assertEqual(2, len(calls))

        missing = urllib.error.HTTPError("https://example.invalid/b", 404, "nf", {}, None)
        with mock.patch.object(generate, "http_get", side_effect=missing) as get, \
                mock.patch.object(generate.time, "sleep"):
            with self.assertRaises(urllib.error.HTTPError):
                generate.http_get_retrying("https://example.invalid/b")
        self.assertEqual(1, get.call_count)

    def test_csvs_come_from_the_resolved_commit(self):
        sources = FakeNameSources()
        with mock.patch.object(generate, "http_get", side_effect=sources):
            texts, fetched = generate.fetch_datamining_sheets(
                game_names.TC_REPOSITORY, "main", game_names.TC_COLUMNS, None)

        self.assertEqual(TC_SHA, fetched["commit"])
        self.assertEqual("ver 2026.09.14.0000.0000", fetched["commit_message"])
        self.assertIn("per_page=1", sources.requests[0])
        self.assertTrue(all(TC_SHA in url for url in sources.requests[1:]))
        self.assertEqual(sorted(game_names.TC_COLUMNS), sorted(texts))
        self.assertIn("樣例迷宮一", texts["ContentFinderCondition"])

    def test_a_listing_without_a_commit_stops_the_run(self):
        with mock.patch.object(generate, "http_get", return_value=b"[]"):
            with self.assertRaises(ValueError):
                generate.resolve_commit(game_names.TC_REPOSITORY, "main", [], None)

    def test_latest_resolves_to_the_key_xivapi_names_latest(self):
        with mock.patch.object(generate, "http_get", side_effect=FakeNameSources()):
            self.assertEqual(("sample-latest", ["7.57", "latest"]),
                             generate.resolve_xivapi_version(None, [], None))
            self.assertEqual((SAMPLE_VERSION, ["7.55x2"]),
                             generate.resolve_xivapi_version(SAMPLE_VERSION, [], None))

    def test_the_real_category_table_merges_with_the_sample(self):
        sheets = {}
        with mock.patch.object(generate, "http_get", side_effect=FakeNameSources()):
            xivapi, _ = generate.fetch_xivapi_names(SAMPLE_VERSION, None)
            for repository, columns in ((game_names.TC_REPOSITORY, game_names.TC_COLUMNS),
                                        (game_names.CN_REPOSITORY, game_names.CN_COLUMNS)):
                texts, _ = generate.fetch_datamining_sheets(repository, "main", columns, None)
                sheets[repository] = game_names.parse_sheets(texts, columns)
        base, _, _ = game_names.load_base(game_names_sample("base-duties.json"),
                                          game_names_sample("base-jobs.json"),
                                          game_names_sample("base-roulettes.json"))

        maps = game_names.build_maps(base, xivapi, sheets[game_names.TC_REPOSITORY],
                                     sheets[game_names.CN_REPOSITORY], mark,
                                     generate.CATEGORY_BY_CONTENT_TYPE)

        self.assertEqual("Dungeons", maps["duty_categories"]["四人迷宫"]["en"])
        self.assertIn(generate.UNKNOWN_CATEGORY, game_names.PROJECT_LABELS)
        self.assertEqual(["其他"], maps["project_maintained"]["duty_categories"])


class GameNamesMainTests(unittest.TestCase):
    """generate.py --game-names end to end, against the fake transport."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="game-names-test-")
        self.addCleanup(directory.cleanup)
        self.scratch = directory.name
        self.out_dir = os.path.join(self.scratch, "out")
        self.sources = FakeNameSources()
        self.opencc = mock.Mock(return_value=(mark, {"method": "OpenCC", "config": "s2twp",
                                                     "package": "fake", "package_version": "0"}))

    def args(self, *extra):
        return ["--game-names",
                "--duties", game_names_sample("base-duties.json"),
                "--jobs", game_names_sample("base-jobs.json"),
                "--roulettes", game_names_sample("base-roulettes.json"),
                "--out-dir", self.out_dir] + list(extra)

    def run_main(self, args):
        output, errors = io.StringIO(), io.StringIO()
        with mock.patch.object(generate, "http_get", side_effect=self.sources), \
                mock.patch.object(game_names, "load_opencc", self.opencc), \
                mock.patch.object(generate.tempfile, "mkdtemp",
                                  side_effect=AssertionError("made a temp directory")), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            code = generate.main(args)
        return code, output.getvalue(), errors.getvalue()

    def read_output(self):
        with io.open(os.path.join(self.out_dir, "game-names.2026-10-15.json"), "r",
                     encoding="utf-8") as handle:
            return json.load(handle)

    def test_a_run_writes_one_file_with_every_map_and_its_provenance(self):
        raw_dir = os.path.join(self.scratch, "raw")

        code, output, _ = self.run_main(self.args("--version", "2026-10-15", "--raw-dir", raw_dir))

        self.assertEqual(0, code, output)
        document = self.read_output()
        self.assertEqual(["1", "2", "3", "5"], list(document["duties"]))
        self.assertEqual({"duties": [3], "jobs": [43], "roulettes": [],
                          "duty_categories": ["讨伐歼灭战"], "expansions": []},
                         document["zh-TW_converted"])
        provenance = document["provenance"]
        self.assertEqual(SAMPLE_VERSION, provenance["xivapi"]["game_version"])
        self.assertEqual(["7.55x2"], provenance["xivapi"]["game_version_names"])
        self.assertEqual(TC_SHA, provenance["datamining_tc"]["commit"])
        self.assertEqual("fake", provenance["zh_tw_fallback"]["package"])
        recorded = [entry["url"] for key in ("xivapi", "datamining_tc", "datamining_cn")
                    for entry in provenance[key]["requests"]]
        self.assertEqual(sorted(self.sources.requests), sorted(recorded))
        roots = (generate.XIVAPI_ROOT, generate.GITHUB_API_ROOT, generate.DATAMINING_RAW_ROOT)
        self.assertTrue(all(url.startswith(roots) for url in recorded))
        self.assertEqual(len(self.sources.requests), len(os.listdir(raw_dir)))

    def test_latest_is_resolved_to_one_key_that_pins_every_sheet(self):
        self.sources = FakeNameSources(version="sample-latest")

        code, output, _ = self.run_main(self.args("--version", "2026-10-15", "--game-version",
                                                  "latest", "--raw-dir",
                                                  os.path.join(self.scratch, "raw")))

        self.assertEqual(0, code, output)
        sheet_requests = [url for url in self.sources.requests if "/sheet/" in url]
        self.assertTrue(sheet_requests)
        self.assertTrue(all("version=sample-latest" in url for url in sheet_requests))
        self.assertEqual(["7.57", "latest"],
                         self.read_output()["provenance"]["xivapi"]["game_version_names"])

    def test_a_dry_run_fetches_and_writes_nothing(self):
        code, output, _ = self.run_main(self.args("--dry-run"))

        self.assertEqual(0, code)
        self.assertIn("dry run: nothing written", output)
        self.assertFalse(os.path.exists(self.out_dir))
        self.assertEqual([], os.listdir(self.scratch))

    def assert_failed_without_writing(self, code, errors):
        self.assertEqual(1, code)
        self.assertIn("no file was written", errors)
        self.assertFalse(os.path.exists(self.out_dir))

    def test_a_source_failure_writes_nothing(self):
        self.sources = mock.Mock(side_effect=urllib.error.HTTPError("x", 404, "nf", {}, None))

        code, _, errors = self.run_main(self.args("--raw-dir", os.path.join(self.scratch, "raw")))

        self.assert_failed_without_writing(code, errors)

    def test_a_missing_opencc_stops_the_run_when_a_name_needs_converting(self):
        self.opencc = mock.Mock(side_effect=game_names.SourceError("OpenCC is not installed"))

        code, _, errors = self.run_main(self.args("--raw-dir", os.path.join(self.scratch, "raw")))

        self.assert_failed_without_writing(code, errors)
        self.assertIn("OpenCC", errors)

    def test_a_duty_file_without_a_game_version_needs_an_explicit_one(self):
        with io.open(game_names_sample("base-duties.json"), "r", encoding="utf-8") as handle:
            duties = json.load(handle)
        duties["provenance"] = {}
        path = os.path.join(self.scratch, "duties.json")
        with io.open(path, "w", encoding="utf-8") as handle:
            json.dump(duties, handle, ensure_ascii=False)

        code, _, errors = self.run_main(self.args("--duties", path, "--raw-dir",
                                                  os.path.join(self.scratch, "raw")))

        self.assert_failed_without_writing(code, errors)
        self.assertIn("--game-version", errors)
        self.assertEqual([], self.sources.requests)

    def test_a_malformed_source_ends_in_one_line_not_a_traceback(self):
        with mock.patch.object(game_names, "build_maps", side_effect=KeyError("content_id")):
            code, _, errors = self.run_main(self.args("--raw-dir",
                                                      os.path.join(self.scratch, "raw")))

        self.assert_failed_without_writing(code, errors)
        self.assertIn("KeyError", errors)

    def test_bad_arguments_are_refused_before_any_fetch(self):
        for extra in (["--version", "../x"], ["--add-party-size", "unused.json"],
                      ["--raw-dir", os.path.join(generate.REPO, "tools")]):
            with self.subTest(extra=extra):
                with self.assertRaises(SystemExit) as caught:
                    self.run_main(self.args("--dry-run", *extra))

                self.assertEqual(2, caught.exception.code)
        self.assertEqual([], self.sources.requests)


class BundledDataTests(unittest.TestCase):
    """The committed files, offline: every roulette-relevant row states its party size."""

    def test_bundled_files_carry_party_size_for_roulette_categories(self):
        duties_dir = os.path.join(generate.REPO, "data", "duties")
        for name in ("cn.2026-09-04.json", "global.2026-09-04.json"):
            with io.open(os.path.join(duties_dir, name), "r", encoding="utf-8") as handle:
                document = json.load(handle)
            self.assertIn("party_size", document["provenance"], name)
            rows = document["duties"]
            self.assertTrue(all("party_size" in row for row in rows), name)
            by_category = {}
            for row in rows:
                by_category.setdefault(row["duty_category"], set()).add(row["party_size"])
            self.assertEqual({4}, by_category["四人迷宫"], name)
            self.assertEqual({24}, by_category["团队任务"], name)
            # 大型任务 holds both 8-player raids and 24-player alliance raids.
            self.assertEqual({8, 24}, by_category["大型任务"], name)


if __name__ == "__main__":
    unittest.main(verbosity=2)
