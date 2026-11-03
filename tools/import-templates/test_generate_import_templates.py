#!/usr/bin/env python3
"""Offline tests for scripts/generate-import-templates.py --check (re-review 2026-11-03 RV2-1).

The text templates are git text=auto, so a Windows checkout with core.autocrlf (this machine,
and the windows-latest CI runner) has them with CRLF; the workbook's deflate stream depends on
the zlib the interpreter links. --check must call both current when they say what the generator
says, and still catch a template that says something else.

    python -I tools/import-templates/test_generate_import_templates.py
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import tempfile
import unittest
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "generate-import-templates.py"
_SPEC = importlib.util.spec_from_file_location("generate_import_templates", SCRIPT)
generator = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(generator)

TEXT_TEMPLATES = ("instructions.txt", "personal-records.json")
WORKBOOK = "personal-records.xlsx"


class CheckTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="import-templates-")
        self.addCleanup(directory.cleanup)
        self.folder = Path(directory.name)
        for name, body in generator.render().items():
            (self.folder / name).write_bytes(body)

    def check(self) -> int:
        with contextlib.redirect_stderr(io.StringIO()):
            return generator.main(["--check", "--out-dir", str(self.folder)])

    def rewrite_workbook(self, change=None) -> None:
        """Re-zips the workbook stored, not deflated, optionally changing one member."""
        path = self.folder / WORKBOOK
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
            for name, body in generator._members(path.read_bytes()):
                archive.writestr(name, change(name, body) if change else body)
        path.write_bytes(buffer.getvalue())

    def test_freshly_generated_templates_are_current(self) -> None:
        self.assertEqual(0, self.check())

    def test_a_crlf_checkout_of_the_text_templates_is_current(self) -> None:
        for name in TEXT_TEMPLATES:
            path = self.folder / name
            path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n"))
            self.assertIn(b"\r\n", path.read_bytes())

        self.assertEqual(0, self.check())

    def test_a_workbook_compressed_differently_is_current(self) -> None:
        self.rewrite_workbook()

        self.assertNotEqual(generator.render()[WORKBOOK], (self.folder / WORKBOOK).read_bytes())
        self.assertEqual(0, self.check())

    def test_a_changed_line_is_stale(self) -> None:
        path = self.folder / "instructions.txt"
        path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n") + b"\r\nextra line\r\n")

        self.assertEqual(1, self.check())

    def test_a_changed_workbook_member_is_stale(self) -> None:
        self.rewrite_workbook(lambda name, body: body.replace(b"<sheetData>", b"<sheetData><row r=\"9\"/>")
                              if name == "xl/worksheets/sheet1.xml" else body)

        self.assertEqual(1, self.check())

    def test_a_missing_or_unreadable_template_is_stale(self) -> None:
        (self.folder / WORKBOOK).write_bytes(b"not a zip")
        self.assertEqual(1, self.check())
        (self.folder / WORKBOOK).unlink()
        self.assertEqual(1, self.check())


if __name__ == "__main__":
    unittest.main(verbosity=2)
