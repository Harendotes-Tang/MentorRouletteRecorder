#!/usr/bin/env python3
"""Tests for the Collector text catalogue and its generated tables.

    python tools/collector-texts/test_generate.py

Besides the generator's own rules they check the repository: the catalogue is valid, both
generated files are up to date, every key is used by the Collector, and every key has a
finished translation in each src/Desktop/i18n/*.ts with the same {placeholders} as the
Chinese (contracts/collector-texts.md section 5).
"""

from __future__ import annotations

import glob
import io
import os
import re
import sys
import unittest
import xml.etree.ElementTree as ElementTree

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import generate  # noqa: E402

COLLECTOR_SOURCES = os.path.join(generate.REPO, "src", "Collector")
TS_FILES = sorted(glob.glob(os.path.join(generate.REPO, "src", "Desktop", "i18n", "*.ts")))


def entry(zh, args=None, note=None):
    value = {"zh": zh}
    if args is not None:
        value["args"] = args
    if note is not None:
        value["note"] = note
    return value


def catalogue(texts):
    return {"version": 1, "texts": texts}


class ParseTests(unittest.TestCase):
    def test_a_plain_string_is_a_text_without_arguments(self):
        texts = generate.parse_catalogue(catalogue({"ipc.request.not_json": "请求不是合法的 JSON。"}))

        self.assertEqual("IpcRequestNotJson", texts[0].method)
        self.assertEqual({}, dict(texts[0].args))

    def test_texts_come_out_sorted_by_key(self):
        texts = generate.parse_catalogue(catalogue({"b.x": "乙", "a.x": "甲"}))

        self.assertEqual(["a.x", "b.x"], [text.key for text in texts])

    def test_every_placeholder_must_be_declared(self):
        with self.assertRaisesRegex(generate.CatalogueError, r"\{row\}"):
            generate.parse_catalogue(catalogue({"a.b": entry("第 {row} 行")}))

    def test_every_argument_must_appear_exactly_once(self):
        with self.assertRaisesRegex(generate.CatalogueError, "exactly once"):
            generate.parse_catalogue(catalogue({"a.b": entry("没有占位符", {"row": "number"})}))
        with self.assertRaisesRegex(generate.CatalogueError, "exactly once"):
            generate.parse_catalogue(catalogue({"a.b": entry("{row} 与 {row}", {"row": "number"})}))

    def test_an_unknown_kind_is_refused(self):
        with self.assertRaisesRegex(generate.CatalogueError, "unknown kind"):
            generate.parse_catalogue(catalogue({"a.b": entry("{x}", {"x": "colour"})}))

    def test_a_stray_brace_is_refused(self):
        with self.assertRaisesRegex(generate.CatalogueError, "brace"):
            generate.parse_catalogue(catalogue({"a.b": "半个 { 括号"}))

    def test_keys_are_dotted_snake_case(self):
        for key in ("single", "A.b", "a..b", "a.b-c", "a." + "x" * 100):
            with self.subTest(key=key):
                with self.assertRaises(generate.CatalogueError):
                    generate.parse_catalogue(catalogue({key: "文字"}))

    def test_two_keys_with_the_same_csharp_name_are_refused(self):
        with self.assertRaisesRegex(generate.CatalogueError, "same C# name"):
            generate.parse_catalogue(catalogue({"a.b_c": "一", "a_b.c": "二"}))

    def test_white_space_at_either_end_and_line_breaks_are_refused(self):
        for zh in (" 前有空格", "后有空格 ", "两\n行"):
            with self.subTest(zh=zh):
                with self.assertRaises(generate.CatalogueError):
                    generate.parse_catalogue(catalogue({"a.b": zh}))

    def test_control_and_line_separator_characters_are_refused_in_text_and_note(self):
        for bad in ("\x01", "\x1b", "\x7f", "\x85", "\u2028", "\u2029", "\t"):
            with self.subTest(character=repr(bad)):
                with self.assertRaises(generate.CatalogueError):
                    generate.parse_catalogue(catalogue({"a.b": "前" + bad + "后"}))
                with self.assertRaises(generate.CatalogueError):
                    generate.parse_catalogue(catalogue({"a.b": entry("文字", note="note" + bad + "x")}))

    def test_a_note_ending_in_a_backslash_is_refused(self):
        # It would splice the generated table row into the //: comment above it.
        with self.assertRaisesRegex(generate.CatalogueError, "backslash"):
            generate.parse_catalogue(catalogue({"a.b": entry("文字", note="path C:\\")}))

    def test_a_repeated_key_in_the_file_is_refused(self):
        with self.assertRaisesRegex(generate.CatalogueError, "twice"):
            generate._refuse_repeated_keys([("a.b", "一"), ("a.b", "二")])


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.texts = generate.parse_catalogue(catalogue({
            "import.row.missing_duty": entry("第 {row} 行缺少「{name}」。",
                                             {"row": "number", "name": "duty"}),
            "ipc.quote": 'a "b" \\c?',
        }))

    def test_csharp_factories_are_typed_and_carry_the_chinese(self):
        source = generate.render_csharp(self.texts)

        self.assertIn("public static UserText ImportRowMissingDuty(long row, string name) =>", source)
        self.assertIn('UserTextArg.Number("row", row), UserTextArg.Duty("name", name)', source)
        self.assertIn('"a \\"b\\" \\\\c?"', source)

    def test_the_desktop_table_is_marked_for_lupdate(self):
        source = generate.render_cpp(self.texts)

        self.assertIn('.source = QT_TRANSLATE_NOOP3("CollectorText", "第 {row} 行缺少「{name}」。", '
                      '"import.row.missing_duty"), .args = "row:number,name:duty"}', source)
        self.assertIn("//: Placeholders: {row} a number; {name} a duty name", source)
        # '?' is escaped so no "??x" can read as a trigraph.
        self.assertIn('"a \\"b\\" \\\\c\\?"', source)

    def test_a_csharp_keyword_argument_is_escaped(self):
        texts = generate.parse_catalogue(catalogue({"a.b": entry("{event}", {"event": "text"})}))

        self.assertIn("string @event", generate.render_csharp(texts))


class RepositoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.texts = generate.load_catalogue()

    def test_the_generated_files_are_up_to_date(self):
        stale = [os.path.relpath(path, generate.REPO) for path in generate.stale_outputs(self.texts)]

        self.assertEqual([], stale, "run python tools/collector-texts/generate.py")

    def test_every_key_is_used_by_the_collector(self):
        used = set()
        pattern = re.compile(r"\bCollectorTexts\.([A-Z][A-Za-z0-9]*)\s*\(")
        for directory, _, files in os.walk(COLLECTOR_SOURCES):
            if os.sep + "obj" in directory or os.sep + "bin" in directory:
                continue
            for name in files:
                if not name.endswith(".cs") or name.endswith(".g.cs"):
                    continue
                with io.open(os.path.join(directory, name), "r", encoding="utf-8") as handle:
                    used.update(pattern.findall(handle.read()))
        unused = [text.key for text in self.texts if text.method not in used]

        self.assertEqual([], unused, "catalogue keys no Collector code uses")

    def test_every_key_has_a_finished_translation_with_the_same_placeholders(self):
        self.assertTrue(TS_FILES, "no src/Desktop/i18n/*.ts")
        expected = {(text.zh, text.key): sorted(generate.placeholders(text.zh)) for text in self.texts}
        for path in TS_FILES:
            with self.subTest(file=os.path.basename(path)):
                found = {}
                for context in ElementTree.parse(path).getroot().iter("context"):
                    if context.findtext("name") != generate.TR_CONTEXT:
                        continue
                    for message in context.iter("message"):
                        translation = message.find("translation")
                        source = message.findtext("source")
                        comment = message.findtext("comment")
                        unfinished = translation is None or translation.get("type") in (
                            "unfinished", "vanished", "obsolete")
                        found[(source, comment)] = (
                            None if unfinished else (translation.text or ""))
                problems = ["%s: no finished translation" % key[1]
                            for key, text in sorted(found.items(), key=lambda item: item[0][1])
                            if not text and key not in expected]
                for key, wanted in sorted(expected.items(), key=lambda item: item[0][1]):
                    translated = found.get(key)
                    if not translated:
                        problems.append("%s: no finished translation" % key[1])
                    elif sorted(generate.placeholders(translated)) != wanted:
                        problems.append("%s: placeholders %s, expected %s"
                                        % (key[1], sorted(generate.placeholders(translated)), wanted))
                self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
