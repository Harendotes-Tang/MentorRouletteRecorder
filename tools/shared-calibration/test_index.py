#!/usr/bin/env python3
"""index.py: the client's index reader ported exactly, and the repository rules add_submission enforces."""

from __future__ import annotations

import datetime as dt
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import testsupport
from testsupport import BUILD, COMMIT, NOW, OLD_ACCOUNT
import index as repo_index
import sharecode


def sha(value) -> str:
    return value * 64 if isinstance(value, str) else "%064x" % value


def entry(code_sha, submitters=1, published="2026-09-15T08:00:00Z", region="CN", build=BUILD, revoked=False,
          match_source="ANNOUNCEMENT", commit=None, conflicting=None) -> dict:
    item = {
        "region": region, "game_build": build, "code_sha256": code_sha, "match_source": match_source,
        "submitters": submitters, "first_published_at": published,
        "path": region.lower() + "/" + build + "/" + code_sha[:12] + ".mrc", "commit": commit or "c" * 40,
        "revoked": revoked,
    }
    if conflicting is not None:
        item["conflicting"] = conflicting
    return item


def index_bytes(*entries) -> bytes:
    return json.dumps({"schema_version": 1, "entries": list(entries)}).encode("utf-8")


class ReaderTests(unittest.TestCase):
    """Port of SharedCalibrationIndexTests.cs: the same inputs give the same readings."""

    def test_a_well_formed_index_reads_every_entry(self):
        first = entry(sha("a"), submitters=3)
        second = entry(sha("b"), region="GLOBAL", revoked=True, match_source="REPLY_STATE")
        read = repo_index.read_index(index_bytes(first, second))
        self.assertTrue(read.readable)
        self.assertEqual((), read.skipped)
        self.assertEqual((first, second), read.entries)

    def test_a_code_lives_under_its_region_and_build_named_by_twelve_hex_digits(self):
        self.assertEqual("cn/" + BUILD + "/aaaaaaaaaaaa.mrc", repo_index.code_path("CN", BUILD, sha("a")))
        self.assertEqual("global/" + BUILD + "/bbbbbbbbbbbb.mrc", repo_index.code_path("GLOBAL", BUILD, sha("b")))
        for args in (("UNKNOWN", BUILD, sha("a")), ("CN", "..", sha("a")), ("CN", BUILD, "abc"), ("CN", "a/b", sha("a"))):
            with self.subTest(args), self.assertRaises(ValueError):
                repo_index.code_path(*args)

    def test_unknown_fields_are_ignored_even_when_they_repeat_keys_inside(self):
        text = '{"schema_version":1,"generated_at":"whenever","x":{"a":1,"a":2},"entries":[%s]}' % (
            json.dumps(entry(sha("a")))[:-1] + ',"note":{"k":1,"k":2},"template_sha256":"%s"}' % sha("f"))
        read = repo_index.read_index(text.encode("utf-8"))
        self.assertEqual(1, len(read.entries))
        self.assertEqual((), read.skipped)

    MALFORMED = (
        ("region", "MISSING:region", None),
        ("region", "INVALID:region", '"MARS"'),
        ("region", "INVALID:region", '"UNKNOWN"'),
        ("region", "INVALID:region", '["CN"]'),
        ("game_build", "INVALID:game_build", '".."'),
        ("game_build", "INVALID:game_build", '"2026/09"'),
        ("game_build", "INVALID:game_build", "20260901"),
        ("code_sha256", "INVALID:code_sha256", json.dumps("A" * 64)),
        ("code_sha256", "INVALID:code_sha256", '"abc"'),
        ("match_source", "INVALID:match_source", '"GUESSED"'),
        ("match_source", "INVALID:match_source", '["REPLY_STATE"]'),
        ("submitters", "INVALID:submitters", "0"),
        ("submitters", "INVALID:submitters", '"3"'),
        ("submitters", "INVALID:submitters", "1.5"),
        ("submitters", "INVALID:submitters", "true"),
        ("submitters", "INVALID:submitters", "2147483648"),
        ("first_published_at", "INVALID:first_published_at", '"yesterday"'),
        ("first_published_at", "INVALID:first_published_at", '"2026-09-15T08:00:00"'),
        ("first_published_at", "INVALID:first_published_at", '"2026-13-15T08:00:00Z"'),
        ("first_published_at", "INVALID:first_published_at", '"2026-02-30T08:00:00Z"'),
        ("path", "INVALID:path", '"../index.json"'),
        ("path", "INVALID:path", json.dumps("cn/" + BUILD + "/bbbbbbbbbbbb.mrc")),
        ("path", "INVALID:path", json.dumps("CN/" + BUILD + "/aaaaaaaaaaaa.mrc")),
        ("commit", "MISSING:commit", None),
        ("commit", "INVALID:commit", '"main"'),
        ("revoked", "MISSING:revoked", None),
        ("revoked", "INVALID:revoked", '"false"'),
        ("conflicting", "INVALID:conflicting", '"true"'),
        ("conflicting", "INVALID:conflicting", "1"),
        ("conflicting", "INVALID:conflicting", "null"),
    )

    def test_a_malformed_entry_is_skipped_with_a_reason_and_the_rest_is_still_read(self):
        for field, reason, replacement in self.MALFORMED:
            with self.subTest(field=field, replacement=replacement):
                bad = entry(sha("a"))
                if replacement is None:
                    del bad[field]
                else:
                    bad[field] = json.loads(replacement)
                read = repo_index.read_index(index_bytes(bad, entry(sha("b"))))
                self.assertTrue(read.readable)
                self.assertEqual(((0, reason),), read.skipped)
                self.assertEqual([sha("b")], [item["code_sha256"] for item in read.entries])

    def test_an_entry_that_is_not_an_object_or_repeats_a_key_is_skipped(self):
        duplicated = json.dumps(entry(sha("a")))[:-1] + ',"submitters":900}'
        text = '{"schema_version":1,"entries":[5,' + duplicated + "," + json.dumps(entry(sha("b"))) + "]}"
        read = repo_index.read_index(text.encode("utf-8"))
        self.assertEqual(((0, "NOT_AN_OBJECT"), (1, "DUPLICATE_KEY")), read.skipped)
        self.assertEqual([sha("b")], [item["code_sha256"] for item in read.entries])

    def test_an_index_that_cannot_be_read_is_refused_whole(self):
        cases = (
            ("", "NOT_JSON"), ("not json at all", "NOT_JSON"), ("[]", "NOT_AN_OBJECT"),
            ('{"entries":[]}', "SCHEMA_VERSION"), ('{"schema_version":2,"entries":[]}', "SCHEMA_VERSION"),
            ('{"schema_version":"1","entries":[]}', "SCHEMA_VERSION"), ('{"schema_version":1.0,"entries":[]}', "SCHEMA_VERSION"),
            ('{"schema_version":true,"entries":[]}', "SCHEMA_VERSION"), ('{"schema_version":1}', "NO_ENTRIES"),
            ('{"schema_version":1,"entries":{}}', "NO_ENTRIES"),
            ('{"schema_version":1,"schema_version":1,"entries":[]}', "DUPLICATE_KEY"),
            ('{"schema_version":1,"entries":[],"x":NaN}', "NOT_JSON"),
        )
        for text, reason in cases:
            with self.subTest(text):
                read = repo_index.read_index(text.encode("utf-8"))
                self.assertFalse(read.readable)
                self.assertEqual(reason, read.refusal)
                self.assertEqual((), read.entries)
        self.assertEqual("NOT_JSON", repo_index.read_index(b"\x7b\xff\xfe\x7d").refusal)

    def test_text_that_is_not_well_formed_unicode_refuses_the_whole_index(self):
        # SharedCalibrationIndexTests.cs applies the same damage: .NET cannot read such a key or string.
        text = json.dumps({"schema_version": 1, "entries": [entry(sha("a"))]}, separators=(",", ":"))
        cases = {
            "a key inside an entry": ('{"region"', '{"\\ud800":1,"region"'),
            "a value the entry needs": ('"game_build":"%s"' % BUILD, '"game_build":"\\ud800"'),
            "a field nobody reads": ('{"schema_version"', '{"generated_at":"\\udfff","schema_version"'),
            "the value of a repeated key": ('{"schema_version":1', '{"schema_version":"\\ud800","schema_version":1'),
            "a string in an unknown entry field": ('"revoked":false', '"revoked":false,"note":["\\ud800"]'),
        }
        for why, (anchor, replacement) in cases.items():
            with self.subTest(why):
                self.assertIn(anchor, text)
                read = repo_index.read_index(text.replace(anchor, replacement).encode("utf-8"))
                self.assertEqual(("NOT_JSON", (), ()), (read.refusal, read.entries, read.skipped))
        not_utf8 = text.encode("utf-8").replace(b'"ANNOUNCEMENT"', b'"ANNOUNCEMENT\xff"')
        self.assertEqual("NOT_JSON", repo_index.read_index(not_utf8).refusal)

    def test_a_byte_order_mark_is_tolerated(self):
        self.assertEqual(1, len(repo_index.read_index(b"\xef\xbb\xbf" + index_bytes(entry(sha("a")))).entries))

    def test_an_index_with_more_entries_than_the_limit_is_refused_whole(self):
        at_limit = repo_index.read_index(index_bytes(*(entry(sha(i)) for i in range(repo_index.MAX_ENTRIES))))
        over = repo_index.read_index(index_bytes(*(entry(sha(i)) for i in range(repo_index.MAX_ENTRIES + 1))))
        self.assertEqual(repo_index.MAX_ENTRIES, len(at_limit.entries))
        self.assertEqual("TOO_MANY_ENTRIES", over.refusal)

    def test_selection_takes_this_region_and_build_skips_revoked_orders_and_stops_at_eight(self):
        read = repo_index.read_index(index_bytes(
            entry(sha("0"), submitters=99, revoked=True),
            entry(sha("1"), submitters=50, build="2026.08.05.0000.0000"),
            entry(sha("2"), submitters=50, region="GLOBAL"),
            entry(sha(16), submitters=1, published="2026-09-01T00:00:00Z"),
            entry(sha(17), submitters=3, published="2026-09-05T00:00:00Z"),
            entry(sha(18), submitters=2, published="2026-09-02T00:00:00Z"),
            entry(sha(19), submitters=3, published="2026-09-03T00:00:00Z"),
            entry(sha(20), submitters=1, published="2026-09-02T00:00:00Z"),
            entry(sha(21), submitters=2, published="2026-09-01T00:00:00Z"),
            entry(sha(22), submitters=1, published="2026-09-03T00:00:00Z"),
            entry(sha(23), submitters=5, published="2026-09-09T00:00:00Z"),
            entry(sha(24), submitters=1, published="2026-09-04T00:00:00Z"),
            entry(sha(25), submitters=2, published="2026-09-02T00:00:00Z")))
        chosen = repo_index.select(read.entries, "CN", BUILD)
        self.assertEqual([sha(23), sha(19), sha(17), sha(21), sha(18), sha(25), sha(16), sha(20)],
                         [item["code_sha256"] for item in chosen])
        self.assertEqual((sha("0"),), repo_index.revoked_codes(read.entries, "CN", BUILD))
        self.assertEqual((), repo_index.revoked_codes(read.entries, "GLOBAL", BUILD))

    def test_a_revocation_wins_over_another_entry_for_the_same_code_and_a_duplicate_is_chosen_once(self):
        read = repo_index.read_index(index_bytes(
            entry(sha("a"), submitters=5), entry(sha("a"), revoked=True),
            entry(sha("b"), submitters=4), entry(sha("b"), submitters=2)))
        chosen = repo_index.select(read.entries, "CN", BUILD)
        self.assertEqual([(sha("b"), 4)], [(item["code_sha256"], item["submitters"]) for item in chosen])

    def test_the_conflicting_flag_is_optional_and_read_as_written(self):
        read = repo_index.read_index(index_bytes(
            entry(sha("a")), entry(sha("b"), conflicting=True), entry(sha("c"), conflicting=False)))
        self.assertEqual((), read.skipped)
        self.assertEqual([None, True, False], [item.get("conflicting") for item in read.entries])
        self.assertEqual([False, True, False], [repo_index.is_conflicting(item) for item in read.entries])

    def test_a_conflicting_entry_is_picked_after_every_entry_that_is_not(self):
        read = repo_index.read_index(index_bytes(
            entry(sha("a"), submitters=9, conflicting=True),
            entry(sha("b"), submitters=8, published="2026-09-02T00:00:00Z", conflicting=True),
            entry(sha("c"), submitters=1, published="2026-09-03T00:00:00Z"),
            entry(sha("d"), submitters=1, published="2026-09-02T00:00:00Z", conflicting=False)))
        self.assertEqual([sha("d"), sha("c"), sha("a"), sha("b")],
                         [item["code_sha256"] for item in repo_index.select(read.entries, "CN", BUILD)])

    def test_stamps_are_what_the_client_parses(self):
        self.assertIsNotNone(repo_index.parse_stamp("2026-09-15T08:00:00.1234567Z"))
        self.assertIsNotNone(repo_index.parse_stamp("0001-01-01T00:00:00Z"))
        for text in ("2026-02-30T00:00:00Z", "2026-09-15T24:00:00Z", "2026-09-15T23:59:60Z", "2026-09-15 08:00:00Z", None, 5):
            with self.subTest(text):
                self.assertIsNone(repo_index.parse_stamp(text))


def _code(number=0, source="ANNOUNCEMENT", region="CN", build=BUILD) -> str:
    return sharecode.encode(testsupport.payload(source, number, region=region, build=build))


def _submit(state, number=0, account=4242, when=NOW, commit=COMMIT, created=OLD_ACCOUNT, region="CN", build=BUILD,
            login=None, source="ANNOUNCEMENT"):
    return repo_index.add_submission(state, region, build, _code(number, source, region, build), login, created, when,
                                     commit, account_id=account, issue=7)


class SubmissionTests(unittest.TestCase):
    def test_a_new_code_is_published_at_its_commit_under_its_path(self):
        outcome = _submit(repo_index.empty_index(), number=3)
        code_sha = sharecode.decode(_code(3)).code_sha256
        self.assertEqual(repo_index.PUBLISHED, outcome.status)
        self.assertEqual(repo_index.code_path("CN", BUILD, code_sha), outcome.new_code_path)
        self.assertEqual({
            "region": "CN", "game_build": BUILD, "code_sha256": code_sha, "match_source": "ANNOUNCEMENT", "submitters": 1,
            "first_published_at": "2026-09-16T08:00:00Z", "path": outcome.new_code_path, "commit": COMMIT, "revoked": False,
        }, dict(outcome.index.entries[0]))
        self.assertEqual(({"region": "CN", "game_build": BUILD, "account": "id:4242", "code_sha256": code_sha,
                           "submitted_at": "2026-09-16T08:00:00Z", "issue": 7},), outcome.index.submissions)

    def test_deciding_without_a_commit_applies_every_rule_but_gives_nothing_to_write(self):
        outcome = _submit(repo_index.empty_index(), commit=None)
        self.assertEqual(repo_index.PUBLISHED, outcome.status)
        self.assertIsNone(outcome.index)
        self.assertIsNotNone(outcome.new_code_path)
        with self.assertRaises(ValueError):
            _submit(repo_index.empty_index(), commit="main")

    def test_what_is_written_reads_back_through_the_client_reader(self):
        state = repo_index.empty_index()
        for number in range(6):
            state = _submit(state, number=number, account=100 + number, when=NOW + dt.timedelta(minutes=number),
                            source=sharecode.MATCH_SOURCES[number % 4]).index
        files = repo_index.dump(state)
        read = repo_index.read_index(files[repo_index.INDEX_FILE])
        self.assertTrue(read.readable)
        self.assertEqual((), read.skipped)
        self.assertEqual(state.entries, read.entries)
        self.assertEqual(state, repo_index.parse(files[repo_index.INDEX_FILE], files[repo_index.LEDGER_FILE]))

    def test_the_file_order_is_stable_and_revoking_never_moves_a_line(self):
        forward, backward = repo_index.empty_index(), repo_index.empty_index()
        for number in range(4):
            forward = _submit(forward, number=number, account=100 + number).index
        for number in reversed(range(4)):
            backward = _submit(backward, number=number, account=100 + number).index
        self.assertEqual(repo_index.dump(forward), repo_index.dump(backward))
        before = repo_index.serialize_index(forward.entries).splitlines()
        after = repo_index.serialize_index(repo_index.revoke(forward, forward.entries[1]["code_sha256"]).entries).splitlines()
        self.assertEqual(len(before), len(after))
        self.assertEqual([i for i, (a, b) in enumerate(zip(before, after)) if a != b], [2])

    def test_the_account_must_be_at_least_thirty_days_old(self):
        exactly = _submit(repo_index.empty_index(), created=NOW - dt.timedelta(days=30))
        just_short = _submit(repo_index.empty_index(), created=NOW - dt.timedelta(days=30) + dt.timedelta(seconds=1))
        from_the_future = _submit(repo_index.empty_index(), created=NOW + dt.timedelta(days=400))
        self.assertEqual(repo_index.PUBLISHED, exactly.status)
        self.assertEqual((repo_index.REFUSED, repo_index.ACCOUNT_TOO_NEW), (just_short.status, just_short.reason))
        self.assertEqual(repo_index.ACCOUNT_TOO_NEW, from_the_future.reason)
        with self.assertRaises(ValueError):
            _submit(repo_index.empty_index(), created=dt.datetime(2020, 1, 1))

    def test_one_code_per_account_per_region_and_build(self):
        state = _submit(repo_index.empty_index(), number=1).index
        other_code = _submit(state, number=2)
        same_code = _submit(state, number=1, when=NOW + dt.timedelta(days=1))
        other_build = _submit(state, number=2, build="2026.09.10.0000.0000")
        other_region = _submit(state, number=2, region="GLOBAL")
        self.assertEqual((repo_index.REFUSED, repo_index.ACCOUNT_HAS_OTHER_CODE), (other_code.status, other_code.reason))
        self.assertEqual(repo_index.DUPLICATE, same_code.status)
        self.assertEqual(repo_index.dump(state), repo_index.dump(same_code.index))
        self.assertEqual(repo_index.PUBLISHED, other_build.status)
        self.assertEqual(repo_index.PUBLISHED, other_region.status)

    def test_an_account_is_its_numeric_id_so_renaming_it_buys_nothing(self):
        state = _submit(repo_index.empty_index(), number=1, login="old-name").index
        renamed = _submit(state, number=2, login="new-name")
        self.assertEqual(repo_index.ACCOUNT_HAS_OTHER_CODE, renamed.reason)
        by_login = _submit(repo_index.empty_index(), number=1, account=None, login="Octo-Cat").index
        self.assertEqual("login:octo-cat", by_login.submissions[0]["account"])
        self.assertEqual(repo_index.ACCOUNT_HAS_OTHER_CODE, _submit(by_login, number=2, account=None, login="OCTO-cat").reason)

    def test_the_same_code_from_other_accounts_counts_distinct_submitters(self):
        state = _submit(repo_index.empty_index(), number=1, account=1).index
        published = dict(state.entries[0])
        for account in (2, 3):
            outcome = _submit(state, number=1, account=account, when=NOW + dt.timedelta(hours=account), commit="d" * 40)
            self.assertEqual(repo_index.ADDED, outcome.status)
            state = outcome.index
        self.assertEqual(dict(published, submitters=3), dict(state.entries[0]))
        self.assertEqual(["id:1", "id:2", "id:3"], [row["account"] for row in state.submissions])
        self.assertEqual(repo_index.DUPLICATE, _submit(state, number=1, account=2).status)

    def test_a_revoked_code_is_not_published_again(self):
        state = _submit(repo_index.empty_index(), number=1, account=1).index
        state = repo_index.revoke(state, state.entries[0]["code_sha256"])
        outcome = _submit(state, number=1, account=2)
        self.assertEqual((repo_index.REFUSED, repo_index.REVOKED), (outcome.status, outcome.reason))

    def test_a_file_name_taken_by_another_code_is_refused(self):
        real = sharecode.decode(_code(1)).code_sha256
        impostor = real[:12] + ("0" if real[12] != "0" else "1") + real[13:]
        state = repo_index.Index((entry(impostor, published="2026-09-01T00:00:00Z"),), ())
        outcome = _submit(state, number=1)
        self.assertEqual((repo_index.REFUSED, repo_index.PATH_COLLISION), (outcome.status, outcome.reason))
        self.assertIn(repo_index.PATH_COLLISION, repo_index.MAINTAINER_REFUSALS)

    def test_a_code_that_does_not_decode_or_does_not_match_its_region_and_build_is_refused(self):
        bad = repo_index.add_submission(repo_index.empty_index(), "CN", BUILD, "MRC1.@@", None, OLD_ACCOUNT, NOW, COMMIT, account_id=1)
        self.assertEqual((repo_index.CODE_INVALID, sharecode.E_CHARACTERS), (bad.reason, bad.detail))
        mismatch = repo_index.add_submission(repo_index.empty_index(), "GLOBAL", BUILD, _code(1), None, OLD_ACCOUNT, NOW, COMMIT, account_id=1)
        self.assertEqual(repo_index.PAYLOAD_MISMATCH, mismatch.reason)
        dotted = sharecode.encode(testsupport.payload(build=".hidden"))
        not_indexable = repo_index.add_submission(repo_index.empty_index(), "CN", ".hidden", dotted, None, OLD_ACCOUNT, NOW, COMMIT, account_id=1)
        self.assertEqual(repo_index.BUILD_NOT_INDEXABLE, not_indexable.reason)

    def test_no_new_code_once_the_index_holds_as_many_entries_as_the_client_reads(self):
        full = repo_index.Index(tuple(entry(sha(i)) for i in range(repo_index.MAX_ENTRIES)), ())
        outcome = _submit(full, number=1)
        self.assertEqual((repo_index.REFUSED, repo_index.INDEX_FULL_ENTRIES), (outcome.status, outcome.reason))

    def test_no_new_code_once_the_index_would_pass_the_clients_byte_limit(self):
        state = repo_index.empty_index()
        for number in range(repo_index.MAX_ENTRIES):
            outcome = _submit(state, number=number, account=10000 + number, when=NOW + dt.timedelta(seconds=number))
            if outcome.status != repo_index.PUBLISHED:
                break
            state = outcome.index
        self.assertEqual((repo_index.REFUSED, repo_index.INDEX_FULL_BYTES), (outcome.status, outcome.reason))
        self.assertLessEqual(len(repo_index.dump(state)[repo_index.INDEX_FILE]), repo_index.MAX_INDEX_BYTES)
        self.assertLess(len(state.entries), repo_index.MAX_ENTRIES)
        self.assertGreater(len(state.entries), 150)

    def test_a_new_submitter_that_would_pass_the_byte_limit_is_refused_too(self):
        state = repo_index.empty_index()
        for account in range(1, 10):
            state = (_submit(state, number=1, account=account)).index
        self.assertEqual(9, state.entries[0]["submitters"])
        size = len(repo_index.serialize_index(state.entries))
        with mock.patch.object(repo_index, "MAX_INDEX_BYTES", size):
            outcome = _submit(state, number=1, account=10)
        self.assertEqual((repo_index.REFUSED, repo_index.INDEX_FULL_BYTES), (outcome.status, outcome.reason))
        self.assertEqual(repo_index.ADDED, _submit(state, number=1, account=10).status)

    def test_revoke_marks_every_entry_of_the_code_and_is_idempotent(self):
        state = _submit(repo_index.empty_index(), number=1).index
        code_sha = state.entries[0]["code_sha256"]
        once = repo_index.revoke(state, code_sha)
        self.assertTrue(once.entries[0]["revoked"])
        self.assertFalse(state.entries[0]["revoked"])
        self.assertEqual(once, repo_index.revoke(once, code_sha))
        self.assertEqual((), repo_index.select(once.entries, "CN", BUILD))
        self.assertEqual((code_sha,), repo_index.revoked_codes(once.entries, "CN", BUILD))
        with self.assertRaises(KeyError):
            repo_index.revoke(state, sha("e"))


class ConflictTests(unittest.TestCase):
    """update_conflicts (plan section 18.6): two codes that disagree about the same thing are both flagged."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mr-conflicts-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.state = repo_index.empty_index()

    def publish(self, number, source="ANNOUNCEMENT", region="CN", build=BUILD) -> str:
        """Publishes code ``number`` and writes its file where the index says it lives."""
        outcome = _submit(self.state, number=number, account=1000 + number, source=source, region=region, build=build,
                          when=NOW + dt.timedelta(minutes=number))
        self.assertEqual(repo_index.PUBLISHED, outcome.status)
        self.state = outcome.index
        self.write(outcome.new_code_path, _code(number, source, region, build))
        return outcome.code_sha256

    def write(self, relative, text) -> None:
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="ascii")

    def flags(self, region="CN", build=BUILD, log=None) -> dict:
        self.state = repo_index.update_conflicts(self.state, self.root, region, build, log=log)
        return {item["code_sha256"]: item.get("conflicting") for item in self.state.entries}

    def test_two_different_codes_of_one_template_and_match_source_flag_each_other(self):
        first, second = self.publish(1), self.publish(2)
        self.assertEqual({first: True, second: True}, self.flags())
        files = repo_index.dump(self.state)
        self.assertIn(b'"revoked":false,"conflicting":true', files[repo_index.INDEX_FILE])
        self.assertEqual(self.state, repo_index.parse(files[repo_index.INDEX_FILE], files[repo_index.LEDGER_FILE]))
        self.assertEqual((), repo_index.read_index(files[repo_index.INDEX_FILE]).skipped)

    def test_a_code_alone_and_a_code_of_another_match_source_are_not_flagged(self):
        alone = self.publish(1)
        self.assertEqual({alone: None}, self.flags())
        other = self.publish(2, source="MARKER_OFFSET")
        self.assertEqual({alone: None, other: None}, self.flags())
        third = self.publish(3)
        self.assertEqual({alone: True, other: None, third: True}, self.flags())

    def test_another_submitter_of_the_same_code_is_no_conflict(self):
        only = self.publish(1)
        added = _submit(self.state, number=1, account=77, when=NOW + dt.timedelta(hours=1))
        self.assertEqual(repo_index.ADDED, added.status)
        self.state = added.index
        self.assertEqual({only: None}, self.flags())

    def test_a_revoked_code_neither_counts_nor_keeps_a_flag(self):
        first, second = self.publish(1), self.publish(2)
        self.assertEqual({first: True, second: True}, self.flags())
        self.state = repo_index.revoke(self.state, first)
        self.assertEqual({first: None, second: None}, self.flags())
        self.assertTrue(self.state.entries[0]["revoked"])

    def test_recomputing_twice_changes_nothing(self):
        self.publish(1), self.publish(2), self.publish(3, source="REPLY_STATE")
        once, written = self.flags(), repo_index.dump(self.state)
        self.assertEqual(once, self.flags())
        self.assertEqual(written, repo_index.dump(self.state))
        self.assertEqual({True, None}, set(once.values()))

    def test_a_code_file_that_cannot_be_read_is_skipped_and_named_while_the_rest_still_counts(self):
        first, second, third = self.publish(1), self.publish(2), self.publish(3)
        (self.root / repo_index.code_path("CN", BUILD, third)).unlink()
        notes = []
        self.assertEqual({first: True, second: True, third: None}, self.flags(log=notes.append))
        self.assertEqual(1, len(notes))
        self.assertIn(third[:12], notes[0])
        self.write(repo_index.code_path("CN", BUILD, first), "not a code at all")
        self.write(repo_index.code_path("CN", BUILD, second), _code(9))
        self.assertEqual({first: None, second: None, third: None}, self.flags())

    def test_only_the_named_region_and_build_are_recomputed(self):
        first, second = self.publish(1), self.publish(2)
        third, fourth = self.publish(3, region="GLOBAL"), self.publish(4, region="GLOBAL")
        self.assertEqual({first: True, second: True, third: None, fourth: None}, self.flags())
        self.assertEqual({first: True, second: True, third: True, fourth: True}, self.flags(region="GLOBAL"))
        self.assertEqual([third, fourth], [item["code_sha256"] for item in repo_index.select(self.state.entries, "GLOBAL", BUILD)])


class RepositoryFileTests(unittest.TestCase):
    def setUp(self):
        self.state = _submit(_submit(repo_index.empty_index(), number=1, account=1).index, number=1, account=2).index
        self.files = repo_index.dump(self.state)

    def test_a_new_repository_starts_with_files_the_client_and_the_action_read(self):
        empty = repo_index.dump(repo_index.empty_index())
        self.assertEqual(b'{"schema_version":1,"entries":[]}\n', empty[repo_index.INDEX_FILE])
        self.assertEqual(b'{"schema_version":1,"submissions":[]}\n', empty[repo_index.LEDGER_FILE])
        self.assertTrue(repo_index.read_index(empty[repo_index.INDEX_FILE]).readable)

    def test_files_it_would_not_have_written_are_corrupt(self):
        index_text = self.files[repo_index.INDEX_FILE].decode("utf-8")
        ledger_text = self.files[repo_index.LEDGER_FILE].decode("utf-8")
        cases = {
            "an entry the client skips": (index_text.replace('"submitters":2', '"submitters":0'), ledger_text),
            "a refused index": ("[]", ledger_text),
            "a code listed twice": (index_text.replace("\n]}", ",\n" + index_text.splitlines()[1] + "\n]}"), ledger_text),
            "a ledger row for an unlisted code": (index_text, ledger_text.replace(self.state.entries[0]["code_sha256"], sha("e"))),
            "submitters that disagree with the ledger": (index_text.replace('"submitters":2', '"submitters":3'), ledger_text),
            "an account that is not an id or a login": (index_text, ledger_text.replace('"id:1"', '"someone"')),
            "a ledger that is not the ledger": (index_text, "{}"),
        }
        for why, (index_data, ledger_data) in cases.items():
            with self.subTest(why), self.assertRaises(repo_index.IndexCorrupt):
                repo_index.parse(index_data.encode("utf-8"), ledger_data.encode("utf-8"))

    def test_load_and_write_files_round_trip_on_disk(self):
        with tempfile.TemporaryDirectory(prefix="mr-index-") as name:
            root = Path(name)
            with self.assertRaises(repo_index.IndexCorrupt):
                repo_index.load(root)
            repo_index.write_files(root, self.state)
            self.assertEqual(self.state, repo_index.load(root))
            self.assertEqual(self.files[repo_index.INDEX_FILE], (root / repo_index.INDEX_FILE).read_bytes())


if __name__ == "__main__":
    unittest.main()
