#!/usr/bin/env python3
"""Guard the folders Setup lets stay in the install folder, and how it knows an earlier install.

Audit 2026-10-20, finding M-1. Qt looks for plugins and QML modules in folders directly inside
the application's own folder, so a folder an ordinary user planted in {app} before Setup ran
(for example D:\\MentorRecorder\\QtQuick) could be loaded into every user's program. Setup
neither deletes nor re-protects a folder it does not know, so ProtectInstallDirectory stops on
any top-level folder that is neither note-images nor one [InstallDelete] removes whole. And a
planted, empty MentorRecorder.Desktop.exe must no longer make a foreign folder pass as an
earlier installation: that is now recognised by the uninstall entry Setup writes for its AppId
under HKLM, which only an administrator can create.

A folder under one of the cleared names is no way round this (RV4-4): [InstallDelete] ignores a
failed delete and Setup takes no ownership first, so a folder an ordinary user created as, say,
{app}\\qml with an ACL that grants Administrators nothing would stay with its creator's owner and
ACL, and [Files] would write Qt's QML modules into it. ProtectInstallDirectory therefore deletes
those folders itself and stops Setup when one is left.

Setup cannot be run here, so these tests read installer/MentorRecorder.iss as text. What only a
real installation can show is in the install matrix of the 2026-10-03 audit (section 5 item 3).
"""

from pathlib import Path
import re
import unittest

try:
    import winreg
except ImportError:  # not Windows
    winreg = None


REPO = Path(__file__).resolve().parents[2]
INSTALLER = REPO / "installer/MentorRecorder.iss"

APP_ID = "{{7D5B1C2E-6A3F-4C0B-9E1D-2F8A4B6C9D01}"
UNINSTALL_KEY = (r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
                 r"\{7D5B1C2E-6A3F-4C0B-9E1D-2F8A4B6C9D01}_is1")
APP_PATH_VALUE = "Inno Setup: App Path"
NOTE_IMAGES = "note-images"
# Every folder a released package (1.3.1 through 1.7.3) put directly in {app}. An upgrade over
# any of them finds these folders in place; if one ever stopped being a known name, that upgrade
# would be refused. Taken from the packages under artifacts/ on 2026-10-20.
FOLDERS_EARLIER_PACKAGES_SHIPPED = {
    "docs", "generic", "iconengines", "imageformats", "multimedia", "networkinformation", "ocr",
    "platforminputcontexts", "platforms", "protocol-profiles", "qml", "qmltooling", "styles",
    "texttospeech", "tls", "vectorimageformats",
}


def installer_text():
    return INSTALLER.read_text(encoding="utf-8-sig")


def installer_sections():
    sections, current = {}, ""
    for raw_line in installer_text().splitlines():
        line = raw_line.strip()
        if not line or line.startswith(";"):
            continue
        if re.fullmatch(r"\[\w+\]", line):
            current = line[1:-1].lower()
        else:
            sections.setdefault(current, []).append(line)
    return sections


def install_delete_folders():
    """Lower-case names of the folders [InstallDelete] removes whole (filesandordirs)."""
    folders = []
    for line in installer_sections().get("installdelete", []):
        fields = {key.lower(): value.strip().strip('"')
                  for key, value in re.findall(r"(\w+):\s*(\"[^\"]*\"|[^;]+)", line)}
        if fields.get("type", "").lower() == "filesandordirs":
            name = fields["name"]
            if not name.lower().startswith("{app}\\"):
                raise AssertionError(f"[InstallDelete] entry outside {{app}}: {name}")
            folders.append(name[len("{app}\\"):].lower())
    return folders


def custom_messages():
    messages = {}
    for line in installer_sections().get("custommessages", []):
        name, _, value = line.partition("=")
        messages[name.strip().lower()] = value
    return messages


def pascal_code():
    """The [Code] section with Pascal comments removed and string literals kept."""
    text = installer_text()
    code = text[text.index("\n[Code]") + len("\n[Code]"):]
    out, i = [], 0
    while i < len(code):
        if code[i] == "'":
            end = i + 1
            while end < len(code):
                if code[end] == "'" and code[end + 1:end + 2] == "'":
                    end += 2
                elif code[end] == "'":
                    break
                else:
                    end += 1
            out.append(code[i:end + 1])
            i = end + 1
        elif code.startswith("//", i):
            i = code.find("\n", i)
            i = len(code) if i < 0 else i
        elif code[i] == "{":
            i = code.index("}", i) + 1
        elif code.startswith("(*", i):
            i = code.index("*)", i) + 2
        else:
            out.append(code[i])
            i += 1
    return "".join(out)


def matching_end(code, start):
    """Index just past the `end` that closes the first begin/try/case at or after start."""
    depth = 0
    for match in re.finditer(r"(?i)\b(begin|try|case|end)\b|'(?:[^']|'')*'", code[start:]):
        word = (match.group(1) or "").lower()
        if word in ("begin", "try", "case"):
            depth += 1
        elif word == "end":
            depth -= 1
            if depth == 0:
                return start + match.end()
    raise AssertionError("unbalanced Pascal block")


def routine(code, name):
    header = re.search(rf"(?im)^(procedure|function)\s+{name}\b", code)
    if not header:
        raise AssertionError(f"[Code] has no routine {name}")
    return code[header.start():matching_end(code, header.end())]


def string_constant(code, name):
    match = re.search(rf"(?m)^\s*{name}\s*=\s*'((?:[^']|'')*)'\s*;", code)
    if not match:
        raise AssertionError(f"[Code] has no string constant {name}")
    return match.group(1).replace("''", "'")


def guard_folders(code):
    """The folder names in ClearedDirectoryNames, as the Pascal Pos() lookup reads them."""
    value = string_constant(code, "ClearedDirectoryNames")
    if not (value.startswith("|") and value.endswith("|")):
        raise AssertionError("ClearedDirectoryNames must start and end with '|'")
    return value[1:-1].split("|")


def known_like_setup(code, name):
    """IsKnownTopLevelDirectory, with the list Setup uses: note-images or a cleared folder."""
    cleared = string_constant(code, "ClearedDirectoryNames")
    return name.lower() == NOTE_IMAGES or ("|" + name.lower() + "|") in cleared


def evaluate_boolean(tokens, values):
    """A Pascal boolean expression over named operands; not binds tighter than and, and than or."""
    position = 0

    def peek():
        return tokens[position] if position < len(tokens) else None

    def take(expected=None):
        nonlocal position
        token = peek()
        if token is None or (expected is not None and token != expected):
            raise AssertionError(f"cannot read {' '.join(tokens)!r} at {token!r}")
        position += 1
        return token

    def factor():
        if peek() == "not":
            take("not")
            return not factor()
        if peek() == "(":
            take("(")
            result = disjunction()
            take(")")
            return result
        return values[take()]

    def conjunction():
        result = factor()
        while peek() == "and":
            take("and")
            result = factor() and result
        return result

    def disjunction():
        result = conjunction()
        while peek() == "or":
            take("or")
            result = conjunction() or result
        return result

    result = disjunction()
    if position != len(tokens):
        raise AssertionError(f"unread tokens in {' '.join(tokens)!r}")
    return result


def install_step_branch(code):
    """The body of `if CurStep = ssInstall then begin ... end` in CurStepChanged."""
    steps = routine(code, "CurStepChanged")
    start = re.search(r"CurStep\s*=\s*ssInstall\s+then", steps).end()
    return steps[start:matching_end(steps, start)]


def expand_constant(value):
    """Inno Setup's ExpandConstant for a string whose only brace is an escaped '{{'."""
    out, i = [], 0
    while i < len(value):
        if value.startswith("{{", i):
            out.append("{")
            i += 2
        elif value[i] == "{":
            raise AssertionError(f"unexpected constant in {value!r}")
        else:
            out.append(value[i])
            i += 1
    return "".join(out)


class UnknownTopLevelFolderTests(unittest.TestCase):
    def setUp(self):
        self.code = pascal_code()

    def test_the_guard_list_is_exactly_the_folders_install_delete_removes(self):
        # One list is written twice: [InstallDelete] removes these folders whole and [Files]
        # recreates them, so they are the only folders besides note-images that Setup may leave
        # in place. A name only in [InstallDelete] would stop every upgrade; a name only in the
        # guard would let a folder Setup never clears stay with its creator's ACL.
        folders = guard_folders(self.code)
        self.assertEqual(len(folders), len(set(folders)), "duplicate name in ClearedDirectoryNames")
        for folder in folders:
            with self.subTest(folder=folder):
                self.assertTrue(folder, "empty name in ClearedDirectoryNames")
                self.assertEqual(folder, folder.lower(), "the lookup lower-cases the name it tests")
        self.assertEqual(sorted(install_delete_folders()), sorted(folders))
        self.assertNotIn(NOTE_IMAGES, folders, "note-images is user data and is never cleared")

    def test_every_folder_a_package_shipped_is_known_so_upgrades_are_not_refused(self):
        for folder in sorted(FOLDERS_EARLIER_PACKAGES_SHIPPED):
            with self.subTest(folder=folder):
                self.assertTrue(known_like_setup(self.code, folder))
        packages = sorted(path for path in (REPO / "artifacts").glob("MentorRecorder-*-win-x64")
                          if path.is_dir())
        for package in packages:
            for folder in (path for path in package.iterdir() if path.is_dir()):
                with self.subTest(package=package.name, folder=folder.name):
                    self.assertTrue(known_like_setup(self.code, folder.name))

    def test_folders_qt_would_load_from_are_refused_and_known_ones_pass(self):
        # The two vectors of M-1: {app}\QtQuick\qmldir (QML import path) and a Qt plugin folder
        # the package does not ship (Qt's library paths include the application folder).
        for name in ("QtQuick", "platformthemes", "Qt", "translations", "note-images-old", "x"):
            with self.subTest(name=name):
                self.assertFalse(known_like_setup(self.code, name))
        for name in ("note-images", "Note-Images", "qml", "QML", "Protocol-Profiles", "ocr"):
            with self.subTest(name=name):
                self.assertTrue(known_like_setup(self.code, name))
        known = routine(self.code, "IsKnownTopLevelDirectory")
        self.assertRegex(known, r"CompareText\(Name,\s*NoteImagesDirName\)\s*=\s*0")
        self.assertRegex(known, r"Pos\('\|'\s*\+\s*Lowercase\(Name\)\s*\+\s*'\|',\s*"
                                r"ClearedDirectoryNames\)\s*>\s*0")

    def test_the_guard_stops_on_any_other_folder_and_deletes_nothing(self):
        guard = routine(self.code, "RefuseUnknownTopLevelDirectories")
        self.assertIn("FindFirst(AddBackslash(Dir) + '*', FindRec)", guard)
        self.assertIn("FILE_ATTRIBUTE_DIRECTORY", guard)
        self.assertIn("(FindRec.Name <> '.')", guard)
        self.assertIn("(FindRec.Name <> '..')", guard)
        self.assertIn("IsKnownTopLevelDirectory(FindRec.Name)", guard)
        self.assertIn("CustomMessage('InstallDirUnknownFolder')", guard)
        self.assertIn("Break", guard)
        # A junction or symbolic link to a folder is a directory entry too and must be refused
        # like any other unknown folder, never skipped; and the guard only stops Setup - it does
        # not guess what is safe to delete.
        self.assertNotIn("FILE_ATTRIBUTE_REPARSE_POINT", guard)
        for call in ("DelTree", "RemoveDir", "DeleteFile", "RunIcacls", "ApplyDacl"):
            with self.subTest(call=call):
                self.assertNotIn(call, guard)

    def test_the_guard_runs_once_app_is_protected_and_before_anything_else_changes(self):
        # After the protected DACL is on {app} (and {app} is re-checked to be a real folder), no
        # ordinary user can add another entry to it; before note-images is granted and the
        # top-level files are re-owned, so a stop changes as little as possible.
        protect = routine(self.code, "ProtectInstallDirectory")
        dacl = protect.index("ApplyDacl(Dir, AppDirSddl)")
        recheck = protect.index("IsReparsePoint(Dir)", dacl)
        guard = protect.index("RefuseUnknownTopLevelDirectories(Dir)")
        self.assertLess(recheck, guard)
        self.assertLess(guard, protect.index("NoteImagesDirName"))
        self.assertLess(guard, protect.index("ApplyDacl(Images, NoteImagesSddl)"))
        self.assertLess(guard, protect.index("ProtectTopLevelFiles(Dir)"))
        self.assertRegex(protect, r"Result\s*:=\s*RefuseUnknownTopLevelDirectories\(Dir\);\s*"
                                  r"if\s+Result\s*<>\s*''\s+then\s+Exit;")

    def test_the_stop_message_names_the_folder_in_both_languages(self):
        messages = custom_messages()
        for language in ("chinesesimplified", "english"):
            with self.subTest(language=language):
                self.assertIn("%1", messages[f"{language}.installdirunknownfolder"])
        chinese = messages["chinesesimplified.installdirunknownfolder"]
        self.assertIn("安装已停止", chinese)
        self.assertIn("重新运行安装程序", chinese)
        # Plain words only: no file-system or Qt jargon in the Chinese sentence.
        self.assertIsNone(re.search(r"[A-Za-z]{2,}", chinese.replace("%n", " ")), chinese)

    def test_the_refuted_loads_nothing_claim_is_gone(self):
        # The 2026-10-03 comment said the programs load nothing from such a folder; Qt does.
        self.assertNotRegex(installer_text(), r"(?i)loads?\s+nothing")


class ClearedFolderRemovalTests(unittest.TestCase):
    """RV4-4: a folder under a cleared name that Setup cannot delete stops Setup."""

    def setUp(self):
        self.code = pascal_code()
        self.protect = routine(self.code, "ProtectInstallDirectory")
        self.removal = routine(self.code, "RemoveClearedDirectories")

    def test_the_folders_are_deleted_after_the_guard_and_before_install_delete_and_files(self):
        # After the guard, so only folders it accepted are left to delete; after note-images and
        # the top-level files, so every stop that deletes nothing comes first. It runs inside
        # ProtectInstallDirectory at ssInstall, which comes before [InstallDelete] and [Files]
        # ("Installation Order" in the Inno Setup help), and nothing after it resets the verdict.
        guard = self.protect.index("RefuseUnknownTopLevelDirectories(Dir)")
        removal = self.protect.index("RemoveClearedDirectories(Dir)")
        self.assertLess(guard, removal)
        self.assertLess(self.protect.index("ApplyDacl(Images, NoteImagesSddl)"), removal)
        self.assertLess(self.protect.index("ProtectTopLevelFiles(Dir)"), removal)
        self.assertRegex(self.protect, r"Result\s*:=\s*ProtectTopLevelFiles\(Dir\);\s*"
                                       r"if\s+Result\s*=\s*''\s+then\s+"
                                       r"Result\s*:=\s*RemoveClearedDirectories\(Dir\);")
        self.assertNotRegex(self.protect[removal + len("RemoveClearedDirectories(Dir)"):],
                            r"Result\s*:=")
        self.assertIn("ProtectInstallDirectory(ExpandConstant('{app}'))",
                      install_step_branch(self.code))
        self.assertEqual(1, len(re.findall(r"\bRemoveClearedDirectories\(Dir\)", self.code)),
                         "called once, from ProtectInstallDirectory only")

    def test_every_folder_install_delete_clears_is_deleted_and_note_images_never(self):
        # The folders are read from {app}'s own listing, so a name in any letter case is found,
        # and each one whose name is on the cleared list is deleted - the list the guard reads,
        # which the first test above keeps equal to the filesandordirs names of [InstallDelete].
        self.assertIn("FindFirst(AddBackslash(Dir) + '*', FindRec)", self.removal)
        self.assertIn("FILE_ATTRIBUTE_DIRECTORY", self.removal)
        self.assertIn("IsClearedDirectoryName(FindRec.Name)", self.removal)
        self.assertRegex(self.removal, r"Target\s*:=\s*AddBackslash\(Dir\)\s*\+\s*FindRec\.Name;")
        self.assertRegex(routine(self.code, "IsClearedDirectoryName"),
                         r"Result\s*:=\s*Pos\('\|'\s*\+\s*Lowercase\(Name\)\s*\+\s*'\|',\s*"
                         r"ClearedDirectoryNames\)\s*>\s*0;")
        cleared = string_constant(self.code, "ClearedDirectoryNames")

        def deleted(name):
            return ("|" + name.lower() + "|") in cleared

        folders = install_delete_folders()
        self.assertTrue(folders)
        for folder in folders:
            for spelling in (folder, folder.upper(), folder.title()):
                with self.subTest(folder=spelling):
                    self.assertTrue(deleted(spelling))
        for name in (NOTE_IMAGES, "Note-Images", "QtQuick", "platformthemes"):
            with self.subTest(name=name):
                self.assertFalse(deleted(name))
        # note-images is user data: never named, never deleted here.
        self.assertNotIn("NoteImagesDirName", self.removal)
        self.assertNotIn(NOTE_IMAGES, self.removal.lower())

    def test_a_link_is_removed_itself_and_never_followed(self):
        # DelTree with IsDir set removes a folder that is a reparse point (a junction or symbolic
        # link) without deleting anything inside what it points to (Inno Setup help, Pascal
        # Scripting: DelTree, Remarks). Nothing else here deletes, re-owns or builds a path below
        # the folder.
        self.assertEqual(["Target, True, True, True"],
                         re.findall(r"\bDelTree\(([^)]*)\)", self.removal))
        for call in ("DeleteFile", "RemoveDir", "RunIcacls", "ApplyDacl", "Exec", "Rename"):
            with self.subTest(call=call):
                self.assertNotRegex(self.removal, rf"\b{call}\w*\(")
        self.assertNotRegex(self.removal, r"Target\s*\+")

    def test_a_folder_left_after_the_delete_stops_setup_and_names_it(self):
        # The verdict is what {app}'s listing still shows after the delete, not DelTree's answer:
        # the listing also shows a folder whose own ACL lets Administrators read nothing, and a
        # link whose target is gone (DirExists would follow the link).
        delete = self.removal.index("DelTree(")
        check = self.removal.index("FirstClearedDirectory(Dir)")
        self.assertLess(delete, check)
        self.assertEqual(1, self.removal.count("CustomMessage('InstallDirFolderNotRemoved')"))
        self.assertRegex(self.removal[check:],
                         r"FirstClearedDirectory\(Dir\);\s*"
                         r"if\s+Left\s*<>\s*''\s+then\s+Result\s*:=\s*"
                         r"FmtMessage\(CustomMessage\('InstallDirFolderNotRemoved'\),\s*"
                         r"\[AddBackslash\(Dir\)\s*\+\s*Left\]\);")
        self.assertRegex(self.removal, r"Left\s*:=\s*FirstClearedDirectory\(Dir\);")
        first = routine(self.code, "FirstClearedDirectory")
        self.assertIn("FindFirst(AddBackslash(Dir) + '*', FindRec)", first)
        self.assertIn("FILE_ATTRIBUTE_DIRECTORY", first)
        self.assertIn("IsClearedDirectoryName(FindRec.Name)", first)
        self.assertIn("FindClose(FindRec)", first)
        self.assertNotIn("DirExists", first)

    def test_a_silent_install_stops_the_same_way(self):
        # The message box is suppressible (/SUPPRESSMSGBOXES answers it), the Abort is not: a
        # silent install ends at the same point, with the reason in the Setup log.
        self.assertRegex(install_step_branch(self.code),
                         r"Failure\s*:=\s*ProtectInstallDirectory\(ExpandConstant\('\{app\}'\)\);\s*"
                         r"if\s+Failure\s*<>\s*''\s+then\s+begin\s+Log\([^;]*Failure\);\s*"
                         r"SuppressibleMsgBox\(Failure,[^;]*\);\s*Abort;\s*end")
        for body in (self.protect, self.removal):
            self.assertNotIn("WizardSilent", body)

    def test_the_stop_message_names_the_folder_in_plain_words(self):
        messages = custom_messages()
        for language in ("chinesesimplified", "english"):
            with self.subTest(language=language):
                self.assertIn("%1", messages[f"{language}.installdirfoldernotremoved"])
        chinese = messages["chinesesimplified.installdirfoldernotremoved"]
        self.assertIn("安装已停止", chinese)
        self.assertIn("删除", chinese)
        self.assertIn("重新运行安装程序", chinese)
        self.assertIsNone(re.search(r"[A-Za-z]{2,}", chinese.replace("%n", " ")), chinese)


class EarlierInstallationTests(unittest.TestCase):
    def setUp(self):
        self.code = pascal_code()

    def test_the_app_id_the_uninstall_entry_is_named_after_is_unchanged(self):
        setup = dict(line.split("=", 1) for line in installer_sections()["setup"] if "=" in line)
        self.assertEqual(APP_ID, setup["AppId"].strip())

    def test_the_uninstall_entry_of_this_app_id_is_what_is_read(self):
        registered = routine(self.code, "IsRegisteredInstallDir")
        self.assertRegex(registered, r"RegQueryStringValue\(HKEY_LOCAL_MACHINE,")
        self.assertIn(f"'{APP_PATH_VALUE}'", registered)
        key = re.search(r"ExpandConstant\('([^']*)'\)", registered)
        self.assertTrue(key, "the key must be built from the AppId with ExpandConstant")
        self.assertIn('{#SetupSetting("AppId")}', key.group(1))
        preprocessed = key.group(1).replace('{#SetupSetting("AppId")}', APP_ID)
        self.assertEqual(UNINSTALL_KEY, expand_constant(preprocessed))
        # The folder is compared, not merely the existence of an entry: an entry for another
        # folder says nothing about this one.
        self.assertRegex(registered, r"CompareText\(RemoveBackslashUnlessRoot\(\w+\),\s*"
                                     r"RemoveBackslashUnlessRoot\(Dir\)\)\s*=\s*0")

    def test_a_file_name_alone_no_longer_makes_a_folder_an_earlier_installation(self):
        holds = routine(self.code, "HoldsThisApplication")
        assignment = re.search(r"Result\s*:=\s*(.*?);", holds, re.S)
        self.assertTrue(assignment)
        expression = assignment.group(1)
        expression = re.sub(r"IsRegisteredInstallDir\(Dir\)", " R ", expression)
        expression = re.sub(r"FileExists\(AddBackslash\(Dir\)\s*\+\s*'[^']*'\)", " E ", expression)
        expression = re.sub(r"HoldsOnlyNoteImages\(Dir\)", " N ", expression)
        tokens = re.findall(r"\(|\)|\w+", expression)
        self.assertTrue(set(tokens) <= {"(", ")", "R", "E", "N", "and", "or", "not"}, expression)

        def holds_for(registered, exe, only_images):
            values = {"R": registered, "E": exe, "N": only_images}
            return evaluate_boolean(tokens, values)

        self.assertFalse(holds_for(False, True, False), "a planted exe alone must not pass")
        self.assertFalse(holds_for(False, False, False))
        self.assertTrue(holds_for(True, True, False), "a registered earlier install must pass")
        self.assertTrue(holds_for(False, False, True), "what an uninstall leaves must pass")

    def test_the_value_read_is_where_setup_records_an_installation_here(self):
        # Read-only look at this machine's registry: when this application is installed here,
        # the entry and value the installer reads must exist. Skipped where it is not.
        if winreg is None:
            self.skipTest("not Windows")
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                                winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
                value, kind = winreg.QueryValueEx(key, APP_PATH_VALUE)
        except OSError:
            self.skipTest("this application is not installed on this machine")
        self.assertEqual(winreg.REG_SZ, kind)
        self.assertTrue(value)


if __name__ == "__main__":
    unittest.main(verbosity=2)
