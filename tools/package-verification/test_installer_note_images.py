#!/usr/bin/env python3
"""Guard the installed folder's ACL, the note-image grant and user-data retention.

The installer cannot be compiled or run here, so most of these tests read
installer/MentorRecorder.iss as text. One test does run the exact icacls arguments the
installer uses against a scratch folder, which proves the command lines are valid and
produce the intended ACL. What it cannot cover - ownership needs elevation, and the order
Setup runs things in needs Setup - is listed above ProtectInstallDirectory in the .iss as
checks for a real installation.
"""

from pathlib import Path
import ctypes
import fnmatch
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
INSTALLER = REPO / "installer/MentorRecorder.iss"
IMAGE_DIRECTORY = r"{app}\note-images"

ADMINISTRATORS = "S-1-5-32-544"
SYSTEM = "S-1-5-18"
USERS = "S-1-5-32-545"
# Rights that let an account change a program file, its ACL or its owner. Only these two
# well-known administrative principals may hold them on the install folder.
WRITE_RIGHTS = {"F", "M", "W", "D", "DC", "WD", "AD", "WDAC", "WO", "GA", "GW"}


def installer_text():
    return INSTALLER.read_text(encoding="utf-8-sig")


def installer_sections():
    sections = {}
    current = ""
    for raw_line in installer_text().splitlines():
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


def pascal_constants(code):
    return {name: value.replace("''", "'")
            for name, value in re.findall(r"(?m)^\s*(\w+)\s*=\s*'((?:[^']|'')*)'\s*;", code)}


def pascal_numbers(code):
    """Integer constants (decimal or $hex) declared in [Code]."""
    return {name: int(value[1:], 16) if value.startswith("$") else int(value)
            for name, value in re.findall(r"(?m)^\s*(\w+)\s*=\s*(\$[0-9A-Fa-f]+|\d+)\s*;", code)}


def pascal_record_fields(code, name):
    """[(field, type)] of a Pascal record type, in declaration order."""
    match = re.search(rf"(?is)\b{name}\s*=\s*record\b(.*?)\bend\s*;", code)
    if not match:
        raise AssertionError(f"[Code] has no record type {name}")
    return re.findall(r"(\w+)\s*:\s*(\w+)\s*;", match.group(1))


def probe_link_count(path, numbers):
    """Open path exactly as LinkCountOf does (the .iss constants) and read BY_HANDLE_FILE_
    INFORMATION through the Windows SDK layout. Returns (links, attributes), (0, 0) on failure."""
    from ctypes import wintypes

    class ByHandleFileInformation(ctypes.Structure):  # fileapi.h
        _fields_ = [("dwFileAttributes", wintypes.DWORD),
                    ("ftCreationTime", wintypes.FILETIME),
                    ("ftLastAccessTime", wintypes.FILETIME),
                    ("ftLastWriteTime", wintypes.FILETIME),
                    ("dwVolumeSerialNumber", wintypes.DWORD),
                    ("nFileSizeHigh", wintypes.DWORD),
                    ("nFileSizeLow", wintypes.DWORD),
                    ("nNumberOfLinks", wintypes.DWORD),
                    ("nFileIndexHigh", wintypes.DWORD),
                    ("nFileIndexLow", wintypes.DWORD)]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                     wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD,
                                     wintypes.HANDLE]
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.GetFileInformationByHandle.argtypes = [wintypes.HANDLE,
                                                    ctypes.POINTER(ByHandleFileInformation)]
    kernel32.GetFileInformationByHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel32.CreateFileW(str(path), numbers["LinkProbeAccess"], numbers["LinkProbeShare"],
                                  None, numbers["LinkProbeDisposition"], numbers["LinkProbeFlags"],
                                  None)
    if handle == wintypes.HANDLE(-1).value:
        return 0, 0
    try:
        info = ByHandleFileInformation()
        if not kernel32.GetFileInformationByHandle(handle, ctypes.byref(info)):
            return 0, 0
        return info.nNumberOfLinks, info.dwFileAttributes
    finally:
        kernel32.CloseHandle(handle)


def sdk_by_handle_layout():
    """(size, {field: offset}) of BY_HANDLE_FILE_INFORMATION per the SDK, via ctypes."""
    from ctypes import wintypes
    fields = [("dwFileAttributes", wintypes.DWORD), ("ftCreationTime", wintypes.FILETIME),
              ("ftLastAccessTime", wintypes.FILETIME), ("ftLastWriteTime", wintypes.FILETIME),
              ("dwVolumeSerialNumber", wintypes.DWORD), ("nFileSizeHigh", wintypes.DWORD),
              ("nFileSizeLow", wintypes.DWORD), ("nNumberOfLinks", wintypes.DWORD),
              ("nFileIndexHigh", wintypes.DWORD), ("nFileIndexLow", wintypes.DWORD)]
    layout = type("Info", (ctypes.Structure,), {"_fields_": fields})
    return ctypes.sizeof(layout), {name: getattr(layout, name).offset for name, _ in fields}


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


def branch(code, condition):
    """The begin..end block that follows `if <condition> then`."""
    match = re.search(rf"(?i)if\s+{condition}\s+then\s*", code)
    if not match:
        raise AssertionError(f"no `if {condition} then` branch")
    return code[match.end():matching_end(code, match.end())]


def icacls_grants(arguments):
    """(sid, rights) for every /grant in an icacls argument string."""
    # icacls writes a grant as *SID:(inheritance)...RIGHTS, where RIGHTS is a simple right
    # (F, M, RX, R, W, D) or a parenthesised list of specific rights such as (WD,AD).
    grants = []
    for sid, groups, simple in re.findall(r"\*(S-[0-9-]+):((?:\([A-Z,]+\))*)([A-Z]*)", arguments):
        rights = {simple} if simple else set()
        for group in re.findall(r"\(([A-Z,]+)\)", groups):
            rights.update(group.split(","))
        grants.append((sid, rights - {"OI", "CI", "IO", "NP", "I"}))
    return grants


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

    def test_upgrade_removes_the_external_manifest_an_older_version_installed(self):
        # 1.5.0 staged MentorRecorder.Desktop.exe.manifest beside the executable; the manifest
        # is embedded in the executable now and the package no longer carries the side file, so
        # an upgrade that copies over the top would leave the old one behind (review DT4-X2).
        entries = [entry_fields(line) for line in installer_sections().get("installdelete", [])]
        manifests = [entry for entry in entries
                     if entry.get("name") == r"{app}\mentorrecorder.desktop.exe.manifest"]
        self.assertEqual(1, len(manifests), "[InstallDelete] must remove the old external manifest")
        self.assertEqual("files", manifests[0].get("type"))

    def test_uninstall_prompt_says_note_images_are_kept(self):
        # "Delete my data" removes %LOCALAPPDATA%\MentorRecorder only; the note images stay
        # in the install folder ([Dirs] uninsneveruninstall) and the prompt must say so.
        messages = {}
        for line in installer_sections().get("custommessages", []):
            name, _, value = line.partition("=")
            messages[name.strip().lower()] = value
        for language in ("chinesesimplified", "english"):
            with self.subTest(language=language):
                text = messages[f"{language}.removeuserdata"]
                self.assertIn("%1", text)
                self.assertIn("%2", text)
        self.assertIn("备注图片", messages["chinesesimplified.removeuserdata"])
        self.assertIn("保留", messages["chinesesimplified.removeuserdata"])
        uninstall = routine(pascal_code(), "CurUninstallStepChanged")
        self.assertRegex(uninstall, r"FmtMessage\(CustomMessage\('RemoveUserData'\),\s*\[DataDir,\s*\w+")
        self.assertIn("NoteImagesDirName", uninstall)


class PlantedForeignContentTests(unittest.TestCase):
    """A fresh install to the default D:\\MentorRecorder, or an upgrade over 1.5.0, must not
    let a file an ordinary user placed in {app} first be loaded or be taken over as our own."""

    def setUp(self):
        self.code = pascal_code()

    def test_holds_this_application_requires_an_executable_not_a_bare_note_images(self):
        # A bare note-images directory beside planted files must not make a foreign folder look
        # like an earlier install (an attacker can create one). A real executable is required;
        # a folder whose ONLY content is note-images (what an uninstall leaves) still passes.
        holds = routine(self.code, "HoldsThisApplication")
        self.assertIn("FileExists(AddBackslash(Dir) + '{#AppExe}')", holds)
        self.assertIn("MentorRecorder.Collector.exe", holds)
        self.assertIn("HoldsOnlyNoteImages(Dir)", holds)
        self.assertNotRegex(holds, r"DirExists\(AddBackslash\(Dir\)\s*\+\s*NoteImagesDirName\)",
                            "a bare note-images directory must no longer be sufficient")
        only = routine(self.code, "HoldsOnlyNoteImages")
        self.assertIn("FindFirst(", only)
        self.assertIn("NoteImagesDirName", only)
        self.assertIn("HasOther", only)

    def test_planted_top_level_dll_and_qt_conf_are_cleared_before_files_are_copied(self):
        # A loose DLL in {app} is loaded by the Desktop/Collector via the DLL search order; a
        # planted qt.conf would steer Qt. The package ships neither, so [InstallDelete] (which
        # runs before [Files]) clears every top-level *.dll and qt.conf; ours are re-copied.
        entries = [entry_fields(line) for line in installer_sections().get("installdelete", [])]
        files_cleared = {entry.get("name") for entry in entries if entry.get("type") == "files"}
        self.assertIn(r"{app}\*.dll", files_cleared)
        self.assertIn(r"{app}\qt.conf", files_cleared)

    def test_executables_are_not_blanket_deleted(self):
        # A blanket {app}\*.exe delete would also remove the uninstaller's own unins*.exe. Our
        # exes are overwritten by [Files]; a foreign exe is re-owned by the sweep, not deleted.
        for line in installer_sections().get("installdelete", []):
            entry = entry_fields(line)
            with self.subTest(name=entry.get("name")):
                self.assertNotEqual(r"{app}\*.exe", entry.get("name"))
                self.assertNotRegex(entry.get("name", ""), r"\{app\}\\unins")


class InstallDirectoryProtectionTests(unittest.TestCase):
    """{app} must not be writable by ordinary users; only {app}\\note-images is."""

    def setUp(self):
        self.code = pascal_code()
        self.constants = pascal_constants(self.code)

    def icacls_arguments(self):
        arguments = {name: value for name, value in self.constants.items()
                     if name.startswith("Icacls")}
        self.assertEqual({"IcaclsOwnerArgs", "IcaclsResetArgs", "IcaclsProtectArgs"},
                         set(arguments))
        return arguments

    def test_protection_runs_before_any_file_is_copied(self):
        # ssInstall comes before [InstallDelete], [Dirs] and [Files] ("Installation Order"
        # in the Inno Setup help), so every file Setup writes inherits the protected ACL,
        # and the note-images grant in [Dirs] is added on top of it.
        install = branch(routine(self.code, "CurStepChanged"), r"CurStep\s*=\s*ssInstall")
        self.assertRegex(install, r"ProtectInstallDirectory\(ExpandConstant\('\{app\}'\)\)")
        self.assertRegex(install, r"SuppressibleMsgBox\(")
        self.assertRegex(install, r"(?i)\bAbort\s*;")

    def test_icacls_is_the_system32_copy_and_its_exit_code_is_checked(self):
        runner = routine(self.code, "RunIcacls")
        self.assertIn(r"ExpandConstant('{sys}\icacls.exe')", runner)
        protect = routine(self.code, "ProtectInstallDirectory")
        for name in ("IcaclsOwnerArgs", "IcaclsResetArgs", "IcaclsProtectArgs"):
            self.assertIn(f"RunIcacls(Dir, {name})", protect)
        self.assertRegex(protect, r"Code\s*<>\s*0")
        self.assertRegex(protect, r"CustomMessage\('InstallDirProtectFailed'\)")

    def test_icacls_names_accounts_by_well_known_sid_only(self):
        # Account names are localised ("Users" is "用户" on a Chinese Windows); a SID is not.
        for name, arguments in self.icacls_arguments().items():
            with self.subTest(constant=name):
                for token in re.findall(r"(?:/grant(?::r)?|/setowner)\s+((?:[^/\s]+\s*)+)", arguments):
                    for principal in token.split():
                        self.assertRegex(principal, r"^\*S-1-[0-9-]+(:.*)?$")

    def test_the_install_folder_gets_a_protected_dacl(self):
        arguments = self.icacls_arguments()
        protect = arguments["IcaclsProtectArgs"]
        self.assertIn("/inheritance:r", protect)
        self.assertIn("/grant:r", protect)
        grants = dict((sid, rights) for sid, rights in icacls_grants(protect))
        self.assertEqual({ADMINISTRATORS: {"F"}, SYSTEM: {"F"}, USERS: {"RX"}}, grants)
        self.assertEqual(3, len(re.findall(r"\(OI\)\(CI\)", protect)))
        self.assertIn("/reset", arguments["IcaclsResetArgs"])
        self.assertIn(f"/setowner *{ADMINISTRATORS}", arguments["IcaclsOwnerArgs"])
        for name, value in arguments.items():
            with self.subTest(constant=name):
                # /L: act on a link itself, never on the folder it points to.
                self.assertIn("/L", value.split())

    def test_note_images_is_the_only_user_writable_grant(self):
        for name, arguments in self.icacls_arguments().items():
            for sid, rights in icacls_grants(arguments):
                if sid not in (ADMINISTRATORS, SYSTEM):
                    with self.subTest(constant=name, sid=sid):
                        self.assertFalse(rights & WRITE_RIGHTS, rights)

    def test_links_are_refused_before_anything_is_granted(self):
        protect = routine(self.code, "ProtectInstallDirectory")
        probe = routine(self.code, "IsReparsePoint")
        self.assertIn("FILE_ATTRIBUTE_REPARSE_POINT", probe)
        self.assertEqual("note-images", self.constants.get("NoteImagesDirName"))
        # {app} itself before icacls touches it; note-images after {app} is protected (so
        # no ordinary user can swap it any more) and before [Dirs] grants users-modify on it.
        app_check = protect.index("IsReparsePoint(Dir)")
        images_check = protect.index("IsReparsePoint(AddBackslash(Dir) + NoteImagesDirName)")
        first_icacls = protect.index("RunIcacls(")
        last_icacls = protect.rindex("RunIcacls(")
        self.assertLess(app_check, first_icacls)
        self.assertGreater(images_check, last_icacls)
        self.assertRegex(protect, r"CustomMessage\('NoteImagesIsLink'\)")

    def test_a_folder_holding_other_files_is_refused_on_the_directory_page(self):
        # Protecting a folder that already holds someone's other files would make those
        # files read-only for ordinary users as well.
        page = branch(routine(self.code, "NextButtonClick"), r"CurPageID\s*=\s*wpSelectDir")
        self.assertIn("IsReparsePoint(", page)
        self.assertIn("DirectoryHasEntries(", page)
        self.assertIn("HoldsThisApplication(", page)
        self.assertRegex(page, r"Result\s*:=\s*False")

    def test_inno_setup_6_7_and_redirection_guard_are_required(self):
        guard = re.search(r"(?m)^#if\s+Ver\s*<\s*EncodeVer\(6,\s*7,\s*0\)\s*\n\s*#error ",
                          installer_text())
        self.assertTrue(guard, "the .iss must stop an Inno Setup compiler older than 6.7")
        setup = {line.split("=", 1)[0].strip().lower(): line.split("=", 1)[1].strip().lower()
                 for line in installer_sections()["setup"] if "=" in line}
        self.assertEqual("yes", setup.get("redirectionguard"))

    def test_no_recursive_icacls_anywhere_in_code(self):
        # A recursive icacls (/T) run over {app} could follow a junction an attacker planted
        # inside it and re-ACL or take ownership of an arbitrary directory elsewhere - a worse
        # hole than the one being closed. The protection must never use /T; it handles the
        # entries directly in {app} one by one and lets [InstallDelete] remove whole folders.
        for name, value in self.constants.items():
            if name.startswith("Icacls"):
                with self.subTest(constant=name):
                    self.assertNotIn("/T", value.split())
        self.assertNotRegex(self.code, r"RunIcacls\([^)]*/T")

    def test_a_drive_root_install_is_refused_before_any_icacls(self):
        # {app} = "D:\" has no parent whose ACL Setup could trust, is world-writable, and its
        # trailing backslash would also break the icacls target quoting (review V1-D). It is
        # refused, on the directory page and again at ssInstall, before any icacls runs.
        self.assertIn("IsDriveRoot", self.code)
        protect = routine(self.code, "ProtectInstallDirectory")
        drive_root_pos = protect.index("IsDriveRoot(")
        first_icacls = protect.index("RunIcacls(")
        self.assertLess(drive_root_pos, first_icacls)
        self.assertRegex(protect, r"CustomMessage\('InstallDirIsDriveRoot'\)")
        page = branch(routine(self.code, "NextButtonClick"), r"CurPageID\s*=\s*wpSelectDir")
        self.assertIn("IsDriveRoot(", page)
        messages = _custom_messages()
        for language in ("chinesesimplified", "english"):
            with self.subTest(language=language):
                self.assertIn("%1", messages[f"{language}.installdirisdriveroot"])

    def test_protection_is_re_verified_after_the_dacl_is_applied(self):
        # Checks made before the DACL is in place cannot be trusted afterwards (the parent may
        # let an ordinary user rename {app} away). Once {app} is protected, that it is still a
        # real directory and not a link is checked again, before any file is copied into it.
        protect = routine(self.code, "ProtectInstallDirectory")
        last_app_icacls = protect.rindex("RunIcacls(Dir,")
        tail = protect[last_app_icacls:]
        self.assertIn("IsReparsePoint(Dir)", tail,
                      "{app} must be re-checked for being a link after it is protected")

    def test_every_top_level_file_is_re_owned_and_reset_after_protection(self):
        # /setowner and /reset on {app} alone leave a file another account placed here before
        # Setup ran with its old owner (an owner can always rewrite its own ACL) and explicit
        # grants. Each file directly in {app} is re-owned and reset one by one, after the DACL
        # is applied, skipping directories (so note-images and any junction are never touched).
        protect = routine(self.code, "ProtectInstallDirectory")
        self.assertRegex(protect, r"ProtectTopLevelFiles\(Dir\)")
        self.assertGreater(protect.index("ProtectTopLevelFiles(Dir)"),
                           protect.rindex("RunIcacls(Dir, IcaclsProtectArgs)"))
        sweep = routine(self.code, "ProtectTopLevelFiles")
        self.assertIn("FindFirst(", sweep)
        self.assertIn("FILE_ATTRIBUTE_DIRECTORY", sweep)
        self.assertIn("RunIcacls(Target, IcaclsOwnerArgs)", sweep)
        self.assertIn("RunIcacls(Target, IcaclsResetArgs)", sweep)
        self.assertNotIn("recursesubdirs", sweep.lower())
        self.assertNotIn("/T", sweep)
        # The sweep must not touch note-images (it is a directory and is skipped), and must not
        # grant anything - it only re-owns and resets to the inherited, protected ACL.
        self.assertNotIn("IcaclsProtectArgs", sweep)

    def test_the_sweep_skips_exactly_the_names_install_delete_removes(self):
        # ~236 of the ~250 files directly in an installed {app} are DLLs that [InstallDelete]
        # deletes right after ssInstall; re-owning them first would cost ~500 icacls launches.
        # The sweep skips *.dll and qt.conf - and those must be exactly what [InstallDelete]
        # removes, or a skipped name would be neither deleted nor re-owned.
        sweep = routine(self.code, "ProtectTopLevelFiles")
        self.assertIn("IsRemovedByInstallDelete(FindRec.Name)", sweep)
        skip = routine(self.code, "IsRemovedByInstallDelete")
        self.assertRegex(skip, r"CompareText\(ExtractFileExt\(Name\),\s*'\.dll'\)\s*=\s*0")
        self.assertRegex(skip, r"CompareText\(Name,\s*'qt\.conf'\)\s*=\s*0")
        skipped = {"{app}\\*" + ext for ext in re.findall(r"'(\.\w+)'", skip)} | \
                  {"{app}\\" + name for name in re.findall(r"'(\w+\.\w+)'", skip)}
        deleted = {entry.get("name") for entry in
                   (entry_fields(line) for line in installer_sections().get("installdelete", []))
                   if entry.get("type") == "files"}
        self.assertEqual({r"{app}\*.dll", r"{app}\qt.conf"}, skipped)
        self.assertTrue(skipped <= deleted, (skipped, deleted))

    def test_no_icacls_is_reachable_for_a_file_before_the_link_count_guard(self):
        # Hard links share one security descriptor: icacls on a name in {app} that is also a
        # name of a file elsewhere would re-own and re-ACL that other file. The link count is
        # read first; a reparse point, a second name or an unreadable count stops Setup before
        # any icacls, and the sweep deletes nothing.
        sweep = routine(self.code, "ProtectTopLevelFiles")
        guard = sweep.index("LinkCountOf(Target, Attributes)")
        self.assertLess(guard, sweep.index("RunIcacls("))
        refusal = sweep[guard:sweep.index("RunIcacls(")]
        self.assertEqual(2, refusal.count("FILE_ATTRIBUTE_REPARSE_POINT"),
                         "both the directory entry and the opened handle are checked")
        self.assertRegex(refusal, r"Links\s*<>\s*1")
        self.assertRegex(refusal, r"CustomMessage\('InstallDirFileIsLink'\)")
        self.assertRegex(refusal, r"(?i)\bBreak\s*;")
        self.assertNotRegex(sweep, r"(?i)\b(DeleteFile|DelTree|RemoveDir)\(")
        self.assertNotIn("/T", sweep)
        count = routine(self.code, "LinkCountOf")
        self.assertRegex(count, r"Result\s*:=\s*0")
        self.assertRegex(count, r"FileHandle\s*=\s*-1")
        self.assertIn("CloseHandle(FileHandle)", count)
        self.assertRegex(count, r"(?i)\bexcept\b")

    def test_the_link_count_imports_match_the_windows_declarations(self):
        # Setup is a 32-bit process: handles are 32-bit Longint, INVALID_HANDLE_VALUE is -1.
        # Pascal Script lays record fields out back to back, so the record must hold only
        # 32-bit fields for its layout to equal BY_HANDLE_FILE_INFORMATION's.
        for name in ("CreateFileW", "GetFileInformationByHandle", "CloseHandle"):
            with self.subTest(function=name):
                self.assertRegex(self.code, rf"(?s)function {name}\(.*?\)\s*:\s*Longint;\s*"
                                            rf"external '{name}@kernel32\.dll stdcall setuponly';")
        self.assertRegex(self.code, r"var lpFileInformation:\s*TByHandleFileInformation\)")
        fields = pascal_record_fields(self.code, "TByHandleFileInformation")
        self.assertEqual(13, len(fields))
        self.assertEqual({"cardinal"}, {kind.lower() for _, kind in fields})
        self.assertEqual("nNumberOfLinks", fields[10][0])
        self.assertEqual("dwFileAttributes", fields[0][0])
        numbers = pascal_numbers(self.code)
        self.assertEqual(0x80, numbers["LinkProbeAccess"])        # FILE_READ_ATTRIBUTES
        self.assertEqual(7, numbers["LinkProbeShare"])            # read | write | delete
        self.assertEqual(3, numbers["LinkProbeDisposition"])      # OPEN_EXISTING
        self.assertEqual(0x00200000, numbers["LinkProbeFlags"])   # FILE_FLAG_OPEN_REPARSE_POINT

    def test_icacls_runs_without_a_console_window(self):
        runner = routine(self.code, "RunIcacls")
        self.assertIn("SW_HIDE", runner)
        self.assertNotIn("SW_SHOWNORMAL", runner)

    def test_the_link_refusal_message_exists_in_both_languages(self):
        messages = _custom_messages()
        for language in ("chinesesimplified", "english"):
            with self.subTest(language=language):
                self.assertIn("%1", messages[f"{language}.installdirfileislink"])


def _custom_messages():
    messages = {}
    for line in installer_sections().get("custommessages", []):
        name, _, value = line.partition("=")
        messages[name.strip().lower()] = value
    return messages


def is_elevated():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, OSError):
        return False


@unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "Windows ACLs and pwsh")
class InstallDirectoryAclRunTests(unittest.TestCase):
    """Runs the installer's own icacls arguments against a scratch folder."""

    ICACLS = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "icacls.exe"

    def icacls(self, target, arguments):
        return subprocess.run([str(self.ICACLS), str(target), *arguments.split()],
                              capture_output=True, timeout=60).returncode

    def acl(self, path):
        literal = "'" + str(path).replace("'", "''") + "'"
        script = (
            f"$a = Get-Acl -LiteralPath {literal}; [ordered]@{{ protected = $a.AreAccessRulesProtected;"
            " rules = @($a.Access | ForEach-Object { [ordered]@{"
            " sid = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value;"
            " rights = [int]$_.FileSystemRights; inherited = $_.IsInherited;"
            " flags = [string]$_.InheritanceFlags; allow = [string]$_.AccessControlType } }) } |"
            " ConvertTo-Json -Depth 4 -Compress")
        completed = subprocess.run([shutil.which("pwsh"), "-NoProfile", "-NonInteractive",
                                    "-Command", script],
                                   capture_output=True, text=True, encoding="utf-8",
                                   errors="replace", timeout=60)
        self.assertEqual(0, completed.returncode, completed.stderr)
        return json.loads(completed.stdout)

    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="mr acl "))
        self.addCleanup(self.cleanup)
        # A drive root as found on the reviewer's machine: every user may write below it.
        self.parent = self.root / "drive"
        self.app = self.parent / "MentorRecorder"
        self.images = self.app / "note-images"
        self.outside = self.root / "outside"
        for directory in (self.app / "qml", self.images, self.outside):
            directory.mkdir(parents=True)
        self.assertEqual(0, self.icacls(self.parent, f"/grant *{USERS}:(OI)(CI)F"))
        # Someone also left an explicit grant of their own on the folder itself.
        self.assertEqual(0, self.icacls(self.app, "/grant *S-1-1-0:(OI)(CI)M"))
        # What an earlier installation left: the [Dirs] users-modify grant and a user's image.
        self.assertEqual(0, self.icacls(self.images, f"/grant *{USERS}:(OI)(CI)M"))
        for path in (self.app / "MentorRecorder.Desktop.exe", self.app / "qml" / "Main.qml",
                     self.images / "image.png", self.outside / "elsewhere.txt"):
            path.write_text("x", encoding="utf-8")
        link = subprocess.run(["cmd", "/c", "mklink", "/J", str(self.app / "link"), str(self.outside)],
                              capture_output=True)
        self.assertEqual(0, link.returncode, link.stderr)

    def cleanup(self):
        me = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], capture_output=True,
                            text=True).stdout.strip().split(",")[-1].strip('"')
        if me:
            self.icacls(self.root, f"/grant *{me}:(OI)(CI)F /T /C /Q")
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_installer_arguments_produce_the_intended_acl(self):
        constants = pascal_constants(pascal_code())
        owner = self.icacls(self.app, constants["IcaclsOwnerArgs"])
        # Setting the owner to Administrators needs an elevated token, which Setup has.
        self.assertIn(owner, (0,) if is_elevated() else (0, 1307))
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsResetArgs"]))
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsProtectArgs"]))

        app = self.acl(self.app)
        self.assertTrue(app["protected"])
        self.assertFalse([rule for rule in app["rules"] if rule["inherited"]])
        full_control, read_execute = 0x1F01FF, 0x1200A9
        self.assertEqual(
            sorted([(ADMINISTRATORS, full_control), (SYSTEM, full_control), (USERS, read_execute)]),
            sorted((rule["sid"], rule["rights"]) for rule in app["rules"]))
        self.assertTrue(all(rule["flags"] == "ContainerInherit, ObjectInherit" for rule in app["rules"]))

        write_mask = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000  # write/append data, delete child, delete, WRITE_DAC, WRITE_OWNER
        for path in (self.app / "MentorRecorder.Desktop.exe", self.app / "qml" / "Main.qml"):
            with self.subTest(program_file=path.name):
                rules = self.acl(path)["rules"]
                self.assertFalse([rule for rule in rules
                                  if rule["sid"] not in (ADMINISTRATORS, SYSTEM)
                                  and rule["allow"] == "Allow" and rule["rights"] & write_mask],
                                 rules)

        # The earlier note-images grant and the user's image are left as they were.
        images = self.acl(self.images)["rules"]
        self.assertTrue([rule for rule in images
                         if rule["sid"] == USERS and not rule["inherited"] and rule["rights"] & 0x2])
        image = self.acl(self.images / "image.png")["rules"]
        self.assertTrue([rule for rule in image if rule["sid"] == USERS and rule["rights"] & 0x2])

        # The junction inside {app} was not followed.
        outside = self.acl(self.outside / "elsewhere.txt")
        self.assertFalse([rule for rule in outside["rules"] if rule["sid"] == USERS])

    def test_the_top_level_file_sweep_re_owns_and_strips_a_planted_grant(self):
        # ProtectTopLevelFiles runs the owner + reset arguments on each file directly in {app}
        # after {app} is protected. Here a file carries an explicit Everyone-modify grant of its
        # own, which the {app} reset alone does not remove; the sweep must strip it and (when
        # elevated) hand ownership to Administrators, while never touching the junction's target.
        code = pascal_code()
        constants = pascal_constants(code)
        numbers = pascal_numbers(code)
        # Planted BEFORE Setup runs, as on a real machine (once {app} is protected, an ordinary
        # user can no longer create files in it - which is the whole point). planted.dll is one
        # of the names [InstallDelete] deletes next, so the sweep must leave it alone.
        planted = self.app / "planted.txt"
        skipped = self.app / "planted.dll"
        for path in (planted, skipped):
            path.write_text("x", encoding="utf-8")
            # A plain grant: (OI)(CI) flags on a FILE leave no visible explicit entry, which
            # would make the "gone after /reset" assertion below pass vacuously.
            self.assertEqual(0, self.icacls(path, "/grant *S-1-1-0:M"))
            self.assertTrue([r for r in self.acl(path)["rules"]
                             if r["sid"] == "S-1-1-0" and not r["inherited"]], path.name)

        self.icacls(self.app, constants["IcaclsOwnerArgs"])
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsResetArgs"]))
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsProtectArgs"]))

        # Mirror ProtectTopLevelFiles: every top-level FILE, directories skipped (so the
        # junction 'link' and note-images are never touched, never followed), *.dll and qt.conf
        # skipped, and the link count read before any icacls.
        for entry in os.scandir(self.app):
            if entry.is_dir() or entry.name.lower().endswith(".dll") or entry.name.lower() == "qt.conf":
                continue
            links, attributes = probe_link_count(entry.path, numbers)
            self.assertEqual(1, links, entry.name)
            self.assertFalse(attributes & 0x400, entry.name)  # FILE_ATTRIBUTE_REPARSE_POINT
            owner = self.icacls(Path(entry.path), constants["IcaclsOwnerArgs"])
            # Elevated (as Setup is): 0. Unelevated: the file now inherits Users-RX only, so
            # setowner is refused with 1307 (cannot assign that owner) or 5 (access denied).
            self.assertIn(owner, (0,) if is_elevated() else (0, 1307, 5))
            self.assertEqual(0, self.icacls(Path(entry.path), constants["IcaclsResetArgs"]))

        write_mask = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000
        rules = self.acl(planted)["rules"]
        self.assertFalse([r for r in rules if r["sid"] not in (ADMINISTRATORS, SYSTEM)
                          and r["allow"] == "Allow" and r["rights"] & write_mask], rules)
        self.assertFalse([r for r in rules if not r["inherited"]],
                         "the explicit Everyone grant must be gone after /reset")
        # The skipped DLL was never passed to icacls: its own explicit grant is still there
        # (on a real machine [InstallDelete] deletes it right afterwards).
        self.assertTrue([r for r in self.acl(skipped)["rules"]
                         if r["sid"] == "S-1-1-0" and not r["inherited"]])

        # note-images, if present, keeps its own explicit user grant - the sweep skipped it.
        images = self.acl(self.images)["rules"]
        self.assertTrue([r for r in images
                         if r["sid"] == USERS and not r["inherited"] and r["rights"] & 0x2])
        # The junction's target outside {app} was not touched.
        outside = self.acl(self.outside / "elsewhere.txt")
        self.assertFalse([r for r in outside["rules"] if r["sid"] == USERS])

    def test_the_link_count_probe_refuses_a_hard_link_to_a_file_elsewhere(self):
        # A name in {app} that is a hard link to a file outside it shares that file's security
        # descriptor. LinkCountOf reads the count with the .iss's own CreateFileW arguments and
        # record layout; here the same arguments, through the SDK's layout, must read 2 for the
        # linked name and 1 for an ordinary file, so the sweep refuses the linked name before
        # any icacls (no /setowner or /reset ever reaches the other file through it).
        code = pascal_code()
        constants = pascal_constants(code)
        numbers = pascal_numbers(code)

        # The .iss record (packed, all 32-bit) and the SDK struct agree on size and offsets.
        fields = pascal_record_fields(code, "TByHandleFileInformation")
        size, offsets = sdk_by_handle_layout()
        self.assertEqual(size, 4 * len(fields))
        for index, (name, _) in enumerate(fields):
            if name in offsets:
                with self.subTest(field=name):
                    self.assertEqual(offsets[name], 4 * index)

        victim = self.outside / "victim.txt"
        victim.write_text("x", encoding="utf-8")
        os.link(victim, self.app / "linked.txt")     # before Setup, as an ordinary user could
        ordinary = self.app / "MentorRecorder.Desktop.exe"

        self.icacls(self.app, constants["IcaclsOwnerArgs"])
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsResetArgs"]))
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsProtectArgs"]))

        self.assertEqual(1, probe_link_count(ordinary, numbers)[0])
        self.assertEqual(2, probe_link_count(self.app / "linked.txt", numbers)[0])
        # Mirror the guard: a count other than 1 stops Setup before icacls runs on the name.
        refused = [entry.name for entry in os.scandir(self.app)
                   if not entry.is_dir() and probe_link_count(entry.path, numbers)[0] != 1]
        self.assertEqual(["linked.txt"], refused)

    @unittest.expectedFailure
    def test_protecting_app_leaves_a_hard_linked_file_elsewhere_alone(self):
        # KNOWN OPEN DEFECT (pinned, owner decision pending - see the TL-5 follow-up report).
        # Step 3 of ProtectInstallDirectory runs icacls on {app} itself; icacls sets the DACL
        # through SetNamedSecurityInfo, which propagates the new inherited entries to every
        # existing child of {app}, at any depth. A child that is a hard link to a file elsewhere
        # shares that file's descriptor, so the other file is re-ACLed (Users RX, its own
        # inherited grants dropped) before the per-file guard in ProtectTopLevelFiles ever runs.
        # Setting {app}'s DACL with a non-propagating call (SetFileSecurityW) avoids it; when
        # that lands, remove @expectedFailure (an unexpected success fails the suite).
        constants = pascal_constants(pascal_code())
        victim = self.outside / "victim.txt"
        victim.write_text("x", encoding="utf-8")
        os.link(victim, self.app / "linked.txt")
        before = self.acl(victim)
        self.icacls(self.app, constants["IcaclsOwnerArgs"])
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsResetArgs"]))
        self.assertEqual(0, self.icacls(self.app, constants["IcaclsProtectArgs"]))
        self.assertEqual(before, self.acl(victim))


if __name__ == "__main__":
    unittest.main(verbosity=2)
