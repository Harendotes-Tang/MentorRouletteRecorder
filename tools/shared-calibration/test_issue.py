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
