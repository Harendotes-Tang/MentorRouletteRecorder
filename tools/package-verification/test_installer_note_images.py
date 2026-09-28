#!/usr/bin/env python3
"""Guard the installed note-image ACL and user-data retention boundaries."""

from pathlib import Path
import fnmatch
import re
import unittest


REPO = Path(__file__).resolve().parents[2]
IMAGE_DIRECTORY = r"{app}\note-images"


def installer_sections():
    sections = {}
    current = ""
    for raw_line in (REPO / "installer/MentorRecorder.iss").read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line or line.startswith(";"):
            continue
        if re.fullmatch(r"\[\w+\]", line):
            current = line[1:-1].lower()
        else:
            sections.setdefault(current, []).append(line)
    return sections


def entry_fields(line):
    return {key.lower(): value.strip().strip('"').lower()
            for key, value in re.findall(r"(\w+):\s*(\"[^\"]*\"|[^;]+)", line)}


class InstallerNoteImageTests(unittest.TestCase):
    def test_only_image_directory_receives_ordinary_user_modify_permission(self):
        sections = installer_sections()
        permission_entries = [(section, entry_fields(line))
                              for section, lines in sections.items() if section != "code"
                              for line in lines if "permissions:" in line.lower()]
        self.assertEqual(1, len(permission_entries),
                         "Only note-images may receive an explicit writable ACL")
        section, entry = permission_entries[0]
        self.assertEqual("dirs", section)
        self.assertEqual(IMAGE_DIRECTORY, entry.get("name"))
        self.assertEqual("users-modify", entry.get("permissions"))

    def test_image_directory_is_retained_on_uninstall(self):
        entries = [entry_fields(line) for line in installer_sections().get("dirs", [])]
        images = [entry for entry in entries if entry.get("name") == IMAGE_DIRECTORY]
        self.assertEqual(1, len(images), "Installer must explicitly create the image directory")
        self.assertIn("uninsneveruninstall", images[0].get("flags", "").split())

    def test_upgrade_and_uninstall_cleanup_do_not_target_images(self):
        sections = installer_sections()
        protected_paths = [IMAGE_DIRECTORY, IMAGE_DIRECTORY + r"\run-id\image.png"]
        for section in ("installdelete", "uninstalldelete"):
            for line in sections.get(section, []):
                entry = entry_fields(line)
                pattern = entry.get("name", "").rstrip("\\")
                for protected in protected_paths:
                    with self.subTest(section=section, pattern=pattern):
                        self.assertFalse(fnmatch.fnmatchcase(protected, pattern))
                        self.assertFalse(protected.startswith(pattern + "\\"))
        code = "\n".join(sections.get("code", []))
        self.assertNotRegex(code, r"(?i)(?:DelTree|DeleteFile|RemoveDir)\([^\n]*note-images")


if __name__ == "__main__":
    unittest.main(verbosity=2)
