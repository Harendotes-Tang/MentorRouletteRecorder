#!/usr/bin/env python3
"""Guard what the uninstaller removes outside the install folder: the login entry.

Audit 2026-11-03, finding M-4 (= I5-6). With 开机启动 on, the Desktop writes the value
MentorRecorder under HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run: the program's full
path in double quotes, then its start-up argument, if any. Nothing removed that value on uninstall.
Windows kept trying a missing program at every sign-in. And once the leftover folder (kept for
note-images) was deleted, any local account that can create a folder at the data-drive root could
recreate the path with a program of its own and have it start in this user's session.

The uninstaller now deletes the value at usUninstall when, and only when, its command starts the
Desktop of the folder being uninstalled: never a value that names another copy or a development
build. It reaches the registry of the account it runs as. That is the installing user in the usual
case, an administrator confirming the UAC prompt; a value in another account's registry stays.
The 开机启动 setting itself is kept with the rest of the data.

Setup cannot be run here, so these tests read installer/MentorRecorder.iss as text. They also check
that the Desktop still writes the value under the name and in the form the uninstaller looks for.
"""

from pathlib import Path
import re
import unittest


REPO = Path(__file__).resolve().parents[2]
INSTALLER = REPO / "installer/MentorRecorder.iss"
APP_SETTINGS = REPO / "src/Desktop/cpp/AppSettings.cpp"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MentorRecorder"


def installer_text():
    return INSTALLER.read_text(encoding="utf-8-sig")


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


def app_exe():
    match = re.search(r'(?m)^#define\s+AppExe\s+"([^"]+)"', installer_text())
    if not match:
        raise AssertionError("the .iss defines no AppExe")
    return match.group(1)


def cpp_string(literals):
    """The value of adjacent C++ narrow string literals ("a" "b"), escapes resolved."""
    joined = "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', literals))
    return re.sub(r"\\(.)", r"\1", joined)


def ascii_fold(text):
    """What CompareText compares: ASCII letters fold to one case, nothing else does."""
    return "".join(chr(ord(ch) + 32) if "A" <= ch <= "Z" else ch for ch in text)


def starts_this_installation(command, app):
    """RunCommandStartsThisInstallation, as written in the .iss (pinned by a test below)."""
    expected = '"' + (app if app.endswith("\\") else app + "\\") + app_exe() + '"'
    trimmed = command.lstrip("".join(chr(code) for code in range(33)))  # TrimLeft: <= ' '
    return ascii_fold(trimmed[:len(expected)]) == ascii_fold(expected)


class UninstallLoginEntryTests(unittest.TestCase):
    def setUp(self):
        self.code = pascal_code()

    def test_the_value_looked_for_is_the_one_the_desktop_writes(self):
        self.assertEqual(RUN_KEY, string_constant(self.code, "RunKey"))
        self.assertEqual(RUN_VALUE, string_constant(self.code, "RunValueName"))
        source = APP_SETTINGS.read_text(encoding="utf-8")
        key = re.search(r"constexpr\s+char\s+kRunKey\[\]\s*=\s*((?:\"(?:[^\"\\]|\\.)*\"\s*)+);", source)
        value = re.search(r"constexpr\s+char\s+kRunValue\[\]\s*=\s*((?:\"(?:[^\"\\]|\\.)*\"\s*)+);", source)
        self.assertTrue(key and value, "AppSettings.cpp no longer names the Run key and value")
        self.assertEqual("HKEY_CURRENT_USER\\" + RUN_KEY, cpp_string(key.group(1)))
        self.assertEqual(RUN_VALUE, cpp_string(value.group(1)))
        # The command starts with the program's own path, in Windows separators, in double quotes.
        self.assertIn("QDir::toNativeSeparators(QCoreApplication::applicationFilePath())", source)
        formats = [cpp_string(literal) for literal in
                   re.findall(r'QStringLiteral\(("(?:[^"\\]|\\.)*")\)', source)]
        commands = [text for text in formats if text.startswith('"%1"')]
        self.assertTrue(commands, "the Desktop no longer writes the login command as \"<exe>\" ...")
        app = r"D:\MentorRecorder"
        for text in commands:
            with self.subTest(format=text):
                command = text.replace("%1", app + "\\" + app_exe()).replace("%2", "--autostart")
                self.assertTrue(starts_this_installation(command, app), command)

    def test_the_value_is_removed_at_uninstall_and_only_when_it_starts_this_folder(self):
        uninstall = routine(self.code, "CurUninstallStepChanged")
        self.assertRegex(uninstall, r"if\s+CurUninstallStep\s*=\s*usUninstall\s+then\s+"
                                    r"RemoveLoginEntry\(ExpandConstant\('\{app\}'\)\);")
        remove = routine(self.code, "RemoveLoginEntry")
        self.assertRegex(remove,
                         r"if\s+RegQueryStringValue\(HKEY_CURRENT_USER,\s*RunKey,\s*RunValueName,\s*"
                         r"Command\)\s+then\s+if\s+RunCommandStartsThisInstallation\(Command,\s*Dir\)\s+"
                         r"then\s+begin\s+if\s+RegDeleteValue\(HKEY_CURRENT_USER,\s*RunKey,\s*"
                         r"RunValueName\)\s+then")
        # The value is the only thing deleted outside the install folder, and nothing else in
        # [Code] writes, deletes or reads that key; the data prompt below is untouched.
        self.assertEqual(1, len(re.findall(r"\bRegDeleteValue\(", self.code)))
        self.assertEqual(2, len(re.findall(r"\bRunKey\b", remove)))
        self.assertEqual(3, len(re.findall(r"\bRunKey\b", self.code)),
                         "RunKey is declared once and used by RemoveLoginEntry only")
        self.assertEqual([], re.findall(r"Reg\w*\((?:HKEY_CURRENT_USER|HKCU),(?!\s*RunKey\b)[^;]*",
                                        self.code))
        self.assertEqual([], re.findall(r"RegWrite\w*\([^;]*RunKey[^;]*", self.code))
        for call in ("DelTree", "DeleteFile", "RemoveDir", "RegDeleteKey", "MsgBox", "Exec"):
            with self.subTest(call=call):
                self.assertNotRegex(remove, rf"\b{call}\w*\(")
        self.assertNotIn("RemoveLoginEntry", routine(self.code, "CurStepChanged"))
        sections = re.findall(r"(?im)^\[(\w+)\]\s*$", installer_text())
        self.assertNotIn("registry", [name.lower() for name in sections],
                         "the value is the Desktop's; Setup never writes it")

    def test_only_a_command_that_starts_this_folders_desktop_matches(self):
        match = routine(self.code, "RunCommandStartsThisInstallation")
        self.assertRegex(match, r"Expected\s*:=\s*'\"'\s*\+\s*AddBackslash\(Dir\)\s*\+\s*"
                                r"'\{#AppExe\}'\s*\+\s*'\"';")
        self.assertRegex(match, r"Result\s*:=\s*CompareText\(Copy\(TrimLeft\(Command\),\s*1,\s*"
                                r"Length\(Expected\)\),\s*Expected\)\s*=\s*0;")
        exe = app_exe()
        app = r"D:\MentorRecorder"
        matching = [
            f'"{app}\\{exe}"',                                   # 0.9.1 to 1.8.0
            f'"{app}\\{exe}" --autostart',                       # the tray start
            f'"{app}\\{exe}" autostart',
            f' "{app.lower()}\\{exe.upper()}" --autostart',      # Windows ignores case
        ]
        other = [
            f'"{app}2\\{exe}" --autostart',                      # another folder
            f'"{app}\\sub\\{exe}"',
            f'"{app}\\{exe}.old"',
            f'"D:\\PRJ\\导随记录\\build\\src\\Desktop\\{exe}" --autostart',  # a development build
            f'{app}\\{exe} --autostart',                         # unquoted: not what we write
            "",
        ]
        for command in matching:
            with self.subTest(command=command):
                self.assertTrue(starts_this_installation(command, app))
        for command in other:
            with self.subTest(command=command):
                self.assertFalse(starts_this_installation(command, app))
        for folder in (r"C:\Program Files\MentorRecorder", r"E:\游戏\导随记录器"):
            with self.subTest(folder=folder):
                self.assertTrue(starts_this_installation(f'"{folder}\\{exe}" --autostart', folder))
                self.assertFalse(starts_this_installation(f'"{app}\\{exe}" --autostart', folder))


if __name__ == "__main__":
    unittest.main(verbosity=2)
