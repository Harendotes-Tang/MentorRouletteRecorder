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

    def test_licence_file_is_beside_the_icons_and_names_the_feather_subset(self):
        with io.open(os.path.join(generate.ICON_DIR, "LICENSE"), "r", encoding="utf-8") as handle:
            self.assertIn("ISC License", handle.read())
        feather = generate.feather_icons()
        # Sanity: the list Lucide publishes today; a shrink to nothing means the parser broke.
        self.assertIn("chevron-left", feather)
        self.assertIn("x", feather)
        self.assertNotIn("sun", feather)


if __name__ == "__main__":
    unittest.main(verbosity=2)
