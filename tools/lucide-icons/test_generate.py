#!/usr/bin/env python3
"""Self-test for generate.py: the parser refuses what is not a Lucide icon, the generated
library is deterministic, and the committed Lucide.js matches the committed SVGs."""

from __future__ import annotations

import io
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import generate  # noqa: E402

PLUS = """<!-- @license lucide-static v1.46.0 - ISC -->
<svg
  class="lucide lucide-plus"
  xmlns="http://www.w3.org/2000/svg"
  width="24"
  height="24"
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="2"
  stroke-linecap="round"
  stroke-linejoin="round"
>
  <path d="M5 12h14" />
  <path d="M12 5v14" />
</svg>
"""


class ParseIconTests(unittest.TestCase):
    def test_a_lucide_icon_yields_its_version_and_elements(self):
        version, elements = generate.parse_icon("plus", PLUS)
        self.assertEqual("1.46.0", version)
        self.assertEqual('<path d="M5 12h14"/><path d="M12 5v14"/>', elements)

    def test_line_and_circle_geometry_is_kept(self):
        # gamepad-2 draws with <line x1 y1 x2 y2>, sun with <circle cx cy r>.
        text = PLUS.replace('<path d="M5 12h14" />', '<line x1="6" x2="10" y1="11" y2="11" />') \
                   .replace('<path d="M12 5v14" />', '<circle cx="12" cy="12" r="4" />')
        _version, elements = generate.parse_icon("gamepad-2", text)
        self.assertEqual('<line x1="6" x2="10" y1="11" y2="11"/><circle cx="12" cy="12" r="4"/>', elements)

    def test_refuses_what_is_not_a_lucide_icon(self):
        cases = {
            "no licence comment": PLUS.replace("<!-- @license lucide-static v1.46.0 - ISC -->", ""),
            "other stroke width": PLUS.replace('stroke-width="2"', 'stroke-width="1.5"'),
            "colour in an element": PLUS.replace('<path d="M5 12h14" />',
                                                 '<path d="M5 12h14" stroke="red" />'),
            "nested group": PLUS.replace('<path d="M5 12h14" />', '<g><path d="M5 12h14" /></g>'),
            "script": PLUS.replace('<path d="M5 12h14" />', "<script>alert(1)</script>"),
        }
        for why, text in cases.items():
            with self.assertRaises(generate.IconError, msg=why):
                generate.parse_icon("plus", text)
        with self.assertRaises(generate.IconError):
            generate.parse_icon("Plus", PLUS)
        with self.assertRaises(generate.IconError):
            generate.parse_icon("../plus", PLUS)


class RenderTests(unittest.TestCase):
    def test_output_is_sorted_and_carries_the_isc_notice(self):
        text = generate.render("1.46.0", {"x": '<path d="M18 6 6 18"/>', "a": '<path d="M1 1"/>'})
        self.assertLess(text.index('"a":'), text.index('"x":'))
        self.assertIn("ISC License", text)
        self.assertIn("Copyright (c) 2026 Lucide Icons and Contributors", text)
        self.assertIn('var version = "1.46.0"', text)
        self.assertTrue(text.startswith(".pragma library\n"))
        again = generate.render("1.46.0", {"a": '<path d="M1 1"/>', "x": '<path d="M18 6 6 18"/>'})
        self.assertEqual(text, again)

    def test_the_mit_notice_appears_exactly_when_a_feather_icon_is_bundled(self):
        icons = {"x": '<path d="M18 6 6 18"/>', "sun": '<circle cx="12" cy="12" r="4"/>'}
        with_feather = generate.render("1.46.0", icons, {"x", "plus"})
        self.assertIn("Copyright (c) 2013-present Cole Bemis", with_feather)
        self.assertIn('var fromFeather = ["x"]', with_feather)
        without = generate.render("1.46.0", {"sun": icons["sun"]}, {"x", "plus"})
        self.assertNotIn("Cole Bemis", without)
        self.assertIn("var fromFeather = []", without)


class CommittedFilesTests(unittest.TestCase):
    """Offline: the SVGs in the repository parse, and Lucide.js was generated from them."""

    def test_committed_library_matches_committed_svgs(self):
        version, icons = generate.load_icons()
        self.assertGreaterEqual(len(icons), 1)
        with io.open(generate.OUTPUT, "r", encoding="utf-8", newline="") as handle:
            current = handle.read().replace("\r\n", "\n")
        self.assertEqual(generate.render(version, icons, generate.feather_icons()), current)

    def test_every_svg_has_its_sha256_row_in_the_readme(self):
        # N3-5 (audit 2026-11-03): cloud-off.svg and hard-drive.svg were bundled without the
        # provenance row the README asks for, and nothing compared the table with the directory.
        import hashlib
        import re

        with io.open(os.path.join(generate.ICON_DIR, "README.md"), "r", encoding="utf-8") as handle:
            rows = dict(re.findall(r"^\| `([a-z0-9-]+\.svg)` \| `([0-9a-f]{64})` \|$", handle.read(), re.M))
        on_disk = {}
        for name in sorted(os.listdir(generate.ICON_DIR)):
            if name.endswith(".svg"):
                with open(os.path.join(generate.ICON_DIR, name), "rb") as handle:
                    on_disk[name] = hashlib.sha256(handle.read()).hexdigest()
        self.assertTrue(on_disk)
        self.assertEqual(on_disk, rows)

    def test_licence_file_is_beside_the_icons_and_names_the_feather_subset(self):
        with io.open(os.path.join(generate.ICON_DIR, "LICENSE"), "r", encoding="utf-8") as handle:
            self.assertIn("ISC License", handle.read())
        feather = generate.feather_icons()
        # Sanity: the list Lucide publishes today; a shrink to nothing means the parser broke.
        self.assertIn("chevron-left", feather)
        self.assertIn("x", feather)
        self.assertNotIn("sun", feather)

    def test_every_rename_names_a_listed_feather_icon_under_its_current_name(self):
        with io.open(os.path.join(generate.ICON_DIR, "LICENSE"), "r", encoding="utf-8") as handle:
            listed = {name.strip() for name in
                      generate.FEATHER_LIST.search(handle.read()).group("names").split(",")}
        for current, listed_as in generate.FEATHER_RENAMES.items():
            with self.subTest(current=current):
                self.assertIn(listed_as, listed)
                self.assertNotIn(current, listed)
                self.assertIn(current, generate.feather_icons())

    def test_the_notices_name_exactly_the_feather_icons_that_ship(self):
        # J-4 (audit 2026-11-20): THIRD_PARTY_NOTICES.md named 11 and the icon README 14 (globe among them)
        # while Lucide.js carried the MIT notice for 26; circle-alert and circle-help, renamed by Lucide,
        # were in none of them.
        import re

        _version, icons = generate.load_icons()
        shipped = sorted(name for name in icons if name in generate.feather_icons())
        self.assertIn("circle-alert", shipped)
        self.assertNotIn("globe", shipped)
        # Where each notice lists them: the icon README's sentence and the shipped notice's paragraph.
        lists = {
            os.path.join(generate.ICON_DIR, "README.md"): r"属于该名单：(.*?)。",
            os.path.join(generate.REPO, "THIRD_PARTY_NOTICES.md"): r"个图标中，(.*?)由 Lucide 从",
        }
        for path, pattern in lists.items():
            with self.subTest(path=os.path.basename(path)):
                with io.open(path, "r", encoding="utf-8") as handle:
                    text = handle.read()
                match = re.search(pattern, text, re.S)
                self.assertIsNotNone(match, "the Feather list was not found")
                self.assertEqual(shipped, sorted(re.findall(r"`([a-z0-9-]+)`", match.group(1))))
                self.assertIn("%d 个" % len(icons), text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
