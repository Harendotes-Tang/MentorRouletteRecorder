#!/usr/bin/env python3
"""issue.py: the issue form read strictly, hostile bodies refused rather than guessed, echoes defused."""

from __future__ import annotations

import re
import unittest

import testsupport
import issue as issue_form

CODE = "MRC1.abc_DEF-123"
BUILD = testsupport.BUILD


class BodyTests(unittest.TestCase):
    def test_the_body_github_renders_from_the_form_is_read(self):
        form = issue_form.parse_body(testsupport.issue_body(CODE))
        self.assertEqual((CODE, True, None), (form.code, form.confirmed, form.problem))

    def test_crlf_and_cr_line_endings_read_the_same(self):
        body = testsupport.issue_body(CODE)
        for variant in (body.replace("\n", "\r\n"), body.replace("\n", "\r")):
            form = issue_form.parse_body(variant)
            self.assertEqual((CODE, True), (form.code, form.confirmed))

    def test_only_the_exact_ticked_option_is_a_confirmation(self):
        body = testsupport.issue_body(CODE)
        self.assertTrue(issue_form.parse_body(body.replace("[X]", "[x]")).confirmed)
        for variant in (
            testsupport.issue_body(CODE, checked=False),
            body.replace("时间线\n", "时间线（其实没有）\n"),
            body.replace("核对过", "核对​过"),
            body.replace("- [X] 我在软件里逐条核对过校准时间线", "我在软件里逐条核对过校准时间线 - [X]"),
            body.replace("- [X] ", "- [X]"),
        ):
            with self.subTest(variant[-40:]):
                form = issue_form.parse_body(variant)
                self.assertTrue(form.readable)
                self.assertFalse(form.confirmed)

    def test_a_missing_code_or_confirmation_section_is_refused(self):
        cases = (
            ("### 确认\n\n- [X] 我在软件里逐条核对过校准时间线\n", issue_form.CODE_MISSING),
            (testsupport.issue_body(issue_form.NO_RESPONSE), issue_form.CODE_MISSING),
            (testsupport.issue_body(""), issue_form.CODE_MISSING),
            ("没有用表单 " + CODE, issue_form.CODE_MISSING),
            ("### 校准码\n\n" + CODE + "\n", issue_form.CONFIRM_MISSING),
        )
        for body, problem in cases:
            with self.subTest(problem):
                form = issue_form.parse_body(body)
                self.assertEqual((None, False, problem), (form.code, form.confirmed, form.problem))

    def test_a_repeated_section_is_refused_rather_than_guessed(self):
        base = testsupport.issue_body(CODE)
        cases = (
            (base + "\n### 校准码\n\nMRC1.other\n", issue_form.CODE_AMBIGUOUS),
            (base + "\n###  确认 \n\n- [X] 我在软件里逐条核对过校准时间线\n", issue_form.CONFIRM_AMBIGUOUS),
            ("### 校准码\n\n```\n" + CODE + "\n### 校准码\n```\n\n### 确认\n\n- [X] 我在软件里逐条核对过校准时间线", issue_form.CODE_AMBIGUOUS),
        )
        for body, problem in cases:
            with self.subTest(problem):
                self.assertEqual(problem, issue_form.parse_body(body).problem)

    def test_a_heading_that_only_looks_like_the_label_is_another_section(self):
        body = testsupport.issue_body(CODE) + "\n### 校准码​\n\nMRC1.other\n#### 校准码\n\nMRC1.third\n"
        self.assertEqual(CODE, issue_form.parse_body(body).code)

    def test_one_fence_around_the_whole_value_is_unwrapped(self):
        self.assertEqual(CODE, issue_form.parse_body(testsupport.issue_body("```text\n" + CODE + "\n```")).code)

    def test_script_mention_and_link_content_is_returned_as_data_for_the_decoder_to_refuse(self):
        hostile = "<script>alert(1)</script> @octocat [x](javascript:alert(1)) $(rm -rf /) `id`"
        self.assertEqual(hostile, issue_form.parse_body(testsupport.issue_body(hostile)).code)

    def test_the_body_size_limit_is_github_s(self):
        base = testsupport.issue_body(CODE)
        at_limit = base + " " * (issue_form.MAX_BODY_CHARS - len(base))
        self.assertTrue(issue_form.parse_body(at_limit).readable)
        self.assertEqual(issue_form.BODY_TOO_LARGE, issue_form.parse_body(at_limit + " ").problem)

    def test_a_body_that_is_not_text_is_missing(self):
        for body in (None, 42, ["### 校准码"], {"code": CODE}, "", "   \n"):
            with self.subTest(repr(body)):
                self.assertEqual(issue_form.BODY_MISSING, issue_form.parse_body(body).problem)


INJECTIONS = ("$(whoami)", "`id`", "@octocat", "[x](https://evil.example)", "<script>alert(1)</script>",
              "#1 https://evil.example", "CN GLOBAL")
# Payloads that forge a section heading: they make the body ambiguous, which is refused, never guessed.
FORGED_SECTIONS = ("CN\n### 现象\n\n其他", "### 游戏版本\n\n9.9")


class ReportTests(unittest.TestCase):
    """report-calibration.yml, read as strictly as the submission form: every field or nothing."""

    def test_the_body_github_renders_from_the_report_form_is_read(self):
        form = issue_form.parse_report(testsupport.report_body())
        self.assertEqual(("CN", BUILD, "弹窗时误报匹配", False, None),
                         (form.region, form.game_build, form.symptom, form.has_note, form.problem))
        self.assertTrue(form.readable)

    def test_every_dropdown_option_the_form_offers_is_accepted(self):
        for region in issue_form.REPORT_REGIONS:
            for symptom in issue_form.REPORT_SYMPTOMS:
                with self.subTest(region=region, symptom=symptom):
                    form = issue_form.parse_report(testsupport.report_body(region=region, symptom=symptom))
                    self.assertEqual((region, symptom, None), (form.region, form.symptom, form.problem))

    def test_the_optional_note_is_only_recorded_as_present_or_absent(self):
        self.assertFalse(issue_form.parse_report(testsupport.report_body(note=None)).has_note)
        self.assertFalse(issue_form.parse_report(testsupport.report_body(note="   ")).has_note)
        filled = issue_form.parse_report(testsupport.report_body(note="登录后一进本就乱报"))
        self.assertTrue(filled.has_note)
        self.assertEqual(("region", "game_build", "symptom", "has_note", "problem"), tuple(vars(filled)),
                         "the note's text is never carried out of the body")

    def test_crlf_line_endings_and_a_missing_note_section_read_the_same(self):
        for body in (testsupport.report_body().replace("\n", "\r\n"),
                     testsupport.report_body().split("### 说明")[0]):
            with self.subTest(body[-20:]):
                form = issue_form.parse_report(body)
                self.assertEqual(("CN", BUILD, None), (form.region, form.game_build, form.problem))

    def test_a_missing_or_empty_field_is_refused_rather_than_guessed(self):
        cases = (
            ("", issue_form.BODY_MISSING),
            (testsupport.report_body() + "x" * 70000, issue_form.BODY_TOO_LARGE),
            (testsupport.report_body().replace("### 区服\n\nCN", "区服 CN"), issue_form.REPORT_REGION_MISSING),
            (testsupport.report_body(build="_No response_"), issue_form.REPORT_BUILD_MISSING),
            (testsupport.report_body(build=""), issue_form.REPORT_BUILD_MISSING),
            (testsupport.report_body().replace("### 现象\n\n弹窗时误报匹配", "### 现象"), issue_form.REPORT_SYMPTOM_MISSING),
        )
        for body, problem in cases:
            with self.subTest(problem):
                form = issue_form.parse_report(body)
                self.assertEqual((problem, None, None, None), (form.problem, form.region, form.game_build, form.symptom))

    def test_a_repeated_field_is_refused_rather_than_guessed(self):
        cases = (
            (testsupport.report_body() + "\n### 区服\n\nGLOBAL\n", issue_form.REPORT_REGION_AMBIGUOUS),
            (testsupport.report_body() + "\n###  游戏版本 \n\n1.0\n", issue_form.REPORT_BUILD_AMBIGUOUS),
            (testsupport.report_body() + "\n### 现象\n\n其他\n", issue_form.REPORT_SYMPTOM_AMBIGUOUS),
        )
        for body, problem in cases:
            with self.subTest(problem):
                self.assertEqual(problem, issue_form.parse_report(body).problem)

    def test_a_value_that_is_not_what_the_form_offers_is_refused(self):
        cases = (
            (testsupport.report_body(region="cn"), issue_form.REPORT_REGION_INVALID),
            (testsupport.report_body(region="MARS"), issue_form.REPORT_REGION_INVALID),
            (testsupport.report_body(build=".."), issue_form.REPORT_BUILD_INVALID),
            (testsupport.report_body(build="9" * 129), issue_form.REPORT_BUILD_INVALID),
            (testsupport.report_body(symptom="随便什么"), issue_form.REPORT_SYMPTOM_INVALID),
        )
        for body, problem in cases:
            with self.subTest(problem):
                self.assertEqual(problem, issue_form.parse_report(body).problem)

    def test_injection_in_any_field_is_refused_and_never_becomes_a_value(self):
        for hostile in INJECTIONS + FORGED_SECTIONS:
            for field in ("region", "build", "symptom"):
                with self.subTest(field=field, hostile=hostile):
                    form = issue_form.parse_report(testsupport.report_body(**{field: hostile}))
                    self.assertIsNotNone(form.problem)
                    self.assertEqual((None, None, None), (form.region, form.game_build, form.symptom))

    def test_injection_in_the_optional_note_leaves_the_readable_fields_alone(self):
        for hostile in INJECTIONS:
            with self.subTest(hostile):
                form = issue_form.parse_report(testsupport.report_body(note=hostile))
                self.assertEqual(("CN", BUILD, "弹窗时误报匹配", True, None),
                                 (form.region, form.game_build, form.symptom, form.has_note, form.problem))

    def test_a_note_that_forges_a_section_heading_makes_the_body_ambiguous(self):
        for hostile in FORGED_SECTIONS:
            with self.subTest(hostile):
                self.assertIn(issue_form.parse_report(testsupport.report_body(note=hostile)).problem,
                              (issue_form.REPORT_BUILD_AMBIGUOUS, issue_form.REPORT_SYMPTOM_AMBIGUOUS))

    def test_a_body_that_is_not_text_is_missing(self):
        for body in (None, 42, ["### 区服"], "   \n"):
            with self.subTest(repr(body)):
                self.assertEqual(issue_form.BODY_MISSING, issue_form.parse_report(body).problem)


class TitleTests(unittest.TestCase):
    def test_the_title_the_client_writes_is_read(self):
        self.assertEqual(("CN", BUILD), issue_form.parse_title("[共享校准] CN " + BUILD))
        self.assertEqual(("GLOBAL", BUILD), issue_form.parse_title("  [共享校准] GLOBAL " + BUILD + " "))

    def test_anything_else_is_not_read(self):
        for title in (None, 7, "", "[共享校准] CN", "[共享校准] MARS " + BUILD, "[共享校准] CN 2026.09 @evil",
                      "[共享校准] CN 2026\n.09", "[共享校准]  CN " + BUILD, "[共享校准] CN ..", "[共享校准] CN " + "9" * 129,
                      "x" * 300, "[共享校准] CN " + BUILD + " " * 300):
            with self.subTest(repr(title)[:40]):
                self.assertIsNone(issue_form.parse_title(title))


class EscapeTests(unittest.TestCase):
    def test_mentions_issue_references_and_autolinks_are_defused(self):
        out = issue_form.escape_inline("@octocat #12 https://evil.example www.evil.example user@example.com", 200)
        for token in ("@", "#", "://", "www.", ".com"):
            self.assertNotIn(token, out)
        self.assertIn("octocat", out)

    def test_markdown_and_html_are_escaped(self):
        out = issue_form.escape_inline("[x](javascript:alert(1)) <img src=x onerror=alert(1)> **b** `c` ~~s~~ | t | &amp; \\", 200)
        self.assertIsNone(re.search(r"(?<!\\)[\[\]()<>*`~|&]", out), out)
        self.assertIn("\\<img", out)

    def test_the_echo_is_one_line_without_control_or_invisible_characters(self):
        self.assertEqual("a b c d e f g H", issue_form.escape_inline("a\nb\r\nc d​e\x00f‮g\tH"))

    def test_long_text_is_cut_before_it_is_escaped(self):
        self.assertEqual("a" * 10 + "…", issue_form.escape_inline("a" * 200, 10))
        self.assertEqual("\\*" * 3 + "…", issue_form.escape_inline("*" * 50, 3))

    def test_nothing_is_empty_and_plain_text_survives(self):
        self.assertEqual("", issue_form.escape_inline(None))
        self.assertEqual("中文 键名 pop", issue_form.escape_inline("中文 键名 pop"))


if __name__ == "__main__":
    unittest.main()
