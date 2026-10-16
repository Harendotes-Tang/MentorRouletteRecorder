#!/usr/bin/env python3
"""Generate the Collector's and the Desktop's tables of player-facing Collector texts.

The Collector (C#) does not send finished sentences only. Every sentence a player can read
has a key in contracts/collector-texts.json; the Collector sends the key and its arguments
next to the Simplified Chinese sentence, and the Desktop renders the key in the interface
language with tr() (contracts/collector-texts.md). The catalogue is the single source of the
Simplified Chinese wording; this script turns it into

    src/Collector/Contracts/Texts/CollectorTexts.g.cs   one typed factory per key
    src/Desktop/cpp/CollectorTextCatalog.g.cpp          the Desktop's table, with
                                                        QT_TRANSLATE_NOOP3 so lupdate sees
                                                        every source text

Both outputs are checked in. Run after editing the catalogue:

    python tools/collector-texts/generate.py           # write both files
    python tools/collector-texts/generate.py --check   # exit 1 when either is stale

Only the Python standard library is used. Exit codes: 0 done (or up to date), 1 stale
output or invalid catalogue, 2 could not run.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
from collections import OrderedDict
from typing import Dict, List, Mapping, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
CATALOGUE_PATH = os.path.join(REPO, "contracts", "collector-texts.json")
CSHARP_PATH = os.path.join(REPO, "src", "Collector", "Contracts", "Texts", "CollectorTexts.g.cs")
CPP_PATH = os.path.join(REPO, "src", "Desktop", "cpp", "CollectorTextCatalog.g.cpp")

# The tr() context of every Collector text on the Desktop (src/Desktop/i18n/*.ts).
TR_CONTEXT = "CollectorText"

KEY_PATTERN = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")
KEY_MAX_LENGTH = 96
ARG_PATTERN = re.compile(r"^[a-z][a-z0-9_]*$")
PLACEHOLDER_PATTERN = re.compile(r"\{([^{}]*)\}")
MAX_ARGS = 8
# Characters no catalogue text may hold: C0 and C1 controls, DEL, and the line and paragraph
# separators. Each would end a C# string or a C++ comment line in the generated files, or turn
# into an escape the next character could extend.
FORBIDDEN_CHARACTERS = re.compile("[\x00-\x1f\x7f-\x9f\u2028\u2029]")

# Argument kinds: what the value is, so the Desktop can show it in the interface language.
#   number    an integer, written as plain digits
#   text      shown as it is: a file name, a path, a field path, a version, a token, the
#             player's own words
#   duty      a duty name as the Chinese client spells it; the Desktop shows the official
#             name in the interface language (GameNames)
#   job       a job name, likewise
#   roulette  a roulette name, likewise
#   clock     an instant, shown as local HH:mm
#   text_ref  another catalogue text, rendered in the same language
ARG_KINDS: Mapping[str, Tuple[str, str]] = OrderedDict(
    [
        # kind: (C# parameter type, UserTextArg factory)
        ("number", ("long", "Number")),
        ("text", ("string", "Text")),
        ("duty", ("string", "Duty")),
        ("job", ("string", "Job")),
        ("roulette", ("string", "Roulette")),
        ("clock", ("DateTimeOffset", "Clock")),
        ("text_ref", ("UserText", "Ref")),
    ]
)

CSHARP_KEYWORDS = frozenset(
    "abstract as base bool break byte case catch char checked class const continue decimal "
    "default delegate do double else enum event explicit extern false finally fixed float for "
    "foreach goto if implicit in int interface internal is lock long namespace new null object "
    "operator out override params private protected public readonly ref return sbyte sealed "
    "short sizeof stackalloc static string struct switch this throw true try typeof uint ulong "
    "unchecked unsafe ushort using virtual void volatile while".split()
)


class CatalogueError(ValueError):
    """The catalogue breaks a rule; the message names the key and the rule."""


class Text:
    """One catalogue entry."""

    def __init__(self, key: str, zh: str, args: "OrderedDict[str, str]", note: str) -> None:
        self.key = key
        self.zh = zh
        self.args = args
        self.note = note

    @property
    def method(self) -> str:
        return pascal(self.key)


def pascal(key: str) -> str:
    parts = re.split(r"[._]", key)
    return "".join(part[:1].upper() + part[1:] for part in parts if part)


def camel(name: str) -> str:
    value = pascal(name)
    value = value[:1].lower() + value[1:]
    return "@" + value if value in CSHARP_KEYWORDS else value


def placeholders(template: str) -> List[str]:
    return PLACEHOLDER_PATTERN.findall(template)


def load_catalogue(path: str = CATALOGUE_PATH) -> List[Text]:
    with io.open(path, "r", encoding="utf-8") as handle:
        raw = handle.read()
    try:
        document = json.loads(raw, object_pairs_hook=_refuse_repeated_keys)
    except json.JSONDecodeError as error:
        raise CatalogueError("%s is not valid JSON: %s" % (path, error)) from error
    return parse_catalogue(document)


def _refuse_repeated_keys(pairs: Sequence[Tuple[str, object]]) -> "OrderedDict[str, object]":
    result: "OrderedDict[str, object]" = OrderedDict()
    for name, value in pairs:
        if name in result:
            raise CatalogueError("key %r appears twice" % name)
        result[name] = value
    return result


def parse_catalogue(document: object) -> List[Text]:
    if not isinstance(document, dict):
        raise CatalogueError("the catalogue must be a JSON object")
    unknown = set(document) - {"$comment", "version", "texts"}
    if unknown:
        raise CatalogueError("unknown top-level member(s): %s" % ", ".join(sorted(unknown)))
    if document.get("version") != 1:
        raise CatalogueError("version must be 1")
    texts = document.get("texts")
    if not isinstance(texts, dict) or not texts:
        raise CatalogueError("texts must be a non-empty object")

    result: List[Text] = []
    methods: Dict[str, str] = {}
    for key, entry in texts.items():
        text = parse_entry(key, entry)
        previous = methods.get(text.method)
        if previous is not None:
            raise CatalogueError("%s and %s give the same C# name %s" % (previous, key, text.method))
        methods[text.method] = key
        result.append(text)
    return sorted(result, key=lambda text: text.key)


def parse_entry(key: str, entry: object) -> Text:
    if not KEY_PATTERN.match(key) or len(key) > KEY_MAX_LENGTH:
        raise CatalogueError("%s: a key is dot-separated lower_snake_case words, at most %d "
                             "characters" % (key, KEY_MAX_LENGTH))
    if isinstance(entry, str):
        entry = {"zh": entry}
    if not isinstance(entry, dict):
        raise CatalogueError("%s: an entry is a string or an object" % key)
    unknown = set(entry) - {"zh", "args", "note"}
    if unknown:
        raise CatalogueError("%s: unknown member(s) %s" % (key, ", ".join(sorted(unknown))))

    zh = entry.get("zh")
    if not isinstance(zh, str) or not zh.strip():
        raise CatalogueError("%s: zh must be a non-empty string" % key)
    if zh != zh.strip():
        raise CatalogueError("%s: zh must not start or end with white space" % key)
    if FORBIDDEN_CHARACTERS.search(zh):
        raise CatalogueError("%s: zh is one line without control characters" % key)

    note = entry.get("note", "")
    if not isinstance(note, str):
        raise CatalogueError("%s: note must be a string" % key)
    if FORBIDDEN_CHARACTERS.search(note) or note.endswith("\\"):
        # The note becomes a //: comment line: a line break would put text into the code, and a
        # trailing backslash would splice the next line, the table row itself, into the comment.
        raise CatalogueError("%s: note is one line without control characters or a trailing backslash"
                             % key)

    declared = entry.get("args", {})
    if not isinstance(declared, dict):
        raise CatalogueError("%s: args must be an object" % key)
    if len(declared) > MAX_ARGS:
        raise CatalogueError("%s: at most %d arguments" % (key, MAX_ARGS))
    args: "OrderedDict[str, str]" = OrderedDict()
    for name, kind in declared.items():
        if not ARG_PATTERN.match(name):
            raise CatalogueError("%s: argument %r is not lower_snake_case" % (key, name))
        if kind not in ARG_KINDS:
            raise CatalogueError("%s: argument %s has unknown kind %r (one of %s)"
                                 % (key, name, kind, ", ".join(ARG_KINDS)))
        args[name] = kind

    used = placeholders(zh)
    for name in used:
        if name not in args:
            raise CatalogueError("%s: {%s} in zh is not a declared argument" % (key, name))
    for name in args:
        if used.count(name) != 1:
            raise CatalogueError("%s: argument %s must appear exactly once in zh" % (key, name))
    if "{" in PLACEHOLDER_PATTERN.sub("", zh) or "}" in PLACEHOLDER_PATTERN.sub("", zh):
        raise CatalogueError("%s: a brace that is not part of a {placeholder}" % key)
    return Text(key, zh, args, note)


# --------------------------------------------------------------------------- C#


def csharp_string(value: str) -> str:
    # parse_entry() has refused every control and line-separator character already.
    out = []
    for char in value:
        if char == "\\":
            out.append("\\\\")
        elif char == '"':
            out.append('\\"')
        else:
            out.append(char)
    return '"' + "".join(out) + '"'


def xml_text(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def render_csharp(texts: Sequence[Text]) -> str:
    lines = [
        "// <auto-generated>",
        "//     Generated by tools/collector-texts/generate.py from contracts/collector-texts.json.",
        "//     Do not edit: change the catalogue and run the generator again.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace MentorRecorder.Collector.Contracts.Texts;",
        "",
        "/// <summary>",
        "/// Every sentence the Collector shows the player, one factory per key of",
        "/// contracts/collector-texts.json (contracts/collector-texts.md).",
        "/// </summary>",
        "public static partial class CollectorTexts",
        "{",
    ]
    for index, text in enumerate(texts):
        if index:
            lines.append("")
        lines.append("    /// <summary><c>%s</c>: %s</summary>" % (text.key, xml_text(text.zh)))
        parameters = ", ".join(
            "%s %s" % (ARG_KINDS[kind][0], camel(name)) for name, kind in text.args.items())
        head = "    public static UserText %s(%s) =>" % (text.method, parameters)
        call = ["%s" % csharp_string(text.key), csharp_string(text.zh)]
        call += ["UserTextArg.%s(%s, %s)" % (ARG_KINDS[kind][1], csharp_string(name), camel(name))
                 for name, kind in text.args.items()]
        lines.append(head)
        lines.append("        new(%s);" % ", ".join(call))
    lines.append("}")
    return "\r\n".join(lines) + "\r\n"


# --------------------------------------------------------------------------- C++


def cpp_string(value: str) -> str:
    out = []
    for char in value:
        if char == "\\":
            out.append("\\\\")
        elif char == '"':
            out.append('\\"')
        elif char == "?":
            # Keeps "??x" from ever reading as a trigraph.
            out.append("\\?")
        else:
            out.append(char)
    return '"' + "".join(out) + '"'


KIND_NOTES = {
    "number": "a number",
    "text": "shown as it is",
    "duty": "a duty name, already in the interface language",
    "job": "a job name, already in the interface language",
    "roulette": "a roulette name, already in the interface language",
    "clock": "a local time such as 21:07",
    "text_ref": "another sentence, already translated",
}


def translator_note(text: Text) -> str:
    parts = []
    if text.args:
        parts.append("Placeholders: " + "; ".join(
            "{%s} %s" % (name, KIND_NOTES[kind]) for name, kind in text.args.items()) + ".")
        parts.append("Keep every {placeholder} exactly as written.")
    if text.note:
        parts.append(text.note)
    return " ".join(parts)


def render_cpp(texts: Sequence[Text]) -> str:
    lines = [
        "// Generated by tools/collector-texts/generate.py from contracts/collector-texts.json.",
        "// Do not edit: change the catalogue and run the generator again.",
        "//",
        "// Every Collector text the Desktop can render in the interface language",
        "// (contracts/collector-texts.md). QT_TRANSLATE_NOOP3 puts each Simplified",
        "// Chinese source into the %s context of i18n/*.ts, disambiguated by its key."
        % TR_CONTEXT,
        "",
        '#include "CollectorTextCatalog.h"',
        "",
        "#include <QtGlobal>",
        "",
        "namespace mr::collector_text {",
        "",
        "namespace {",
        "",
        "const CatalogueEntry kEntries[] = {",
    ]
    for text in texts:
        note = translator_note(text)
        if note:
            lines.append("    //: %s" % note)
        args = ",".join("%s:%s" % (name, kind) for name, kind in text.args.items())
        lines.append("    {QT_TRANSLATE_NOOP3(%s, %s, %s), %s},"
                     % (cpp_string(TR_CONTEXT), cpp_string(text.zh), cpp_string(text.key),
                        cpp_string(args)))
    lines += [
        "};",
        "",
        "} // namespace",
        "",
        "const CatalogueEntry *catalogueBegin()",
        "{",
        "    return kEntries;",
        "}",
        "",
        "const CatalogueEntry *catalogueEnd()",
        "{",
        "    return kEntries + sizeof(kEntries) / sizeof(kEntries[0]);",
        "}",
        "",
        "} // namespace mr::collector_text",
    ]
    return "\r\n".join(lines) + "\r\n"


# --------------------------------------------------------------------------- driver


def outputs(texts: Sequence[Text]) -> List[Tuple[str, str]]:
    return [(CSHARP_PATH, render_csharp(texts)), (CPP_PATH, render_cpp(texts))]


def normalised(text: str) -> str:
    return text.replace("\r\n", "\n")


def stale_outputs(texts: Sequence[Text]) -> List[str]:
    stale = []
    for path, content in outputs(texts):
        try:
            with io.open(path, "r", encoding="utf-8", newline="") as handle:
                current = handle.read()
        except FileNotFoundError:
            stale.append(path)
            continue
        if normalised(current) != normalised(content):
            stale.append(path)
    return stale


def write_outputs(texts: Sequence[Text]) -> List[str]:
    written = []
    for path, content in outputs(texts):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        try:
            with io.open(path, "r", encoding="utf-8", newline="") as handle:
                if normalised(handle.read()) == normalised(content):
                    continue
        except FileNotFoundError:
            pass
        with io.open(path, "w", encoding="utf-8", newline="") as handle:
            handle.write(content)
        written.append(path)
    return written


def main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true",
                        help="write nothing; exit 1 when a generated file is stale")
    options = parser.parse_args(argv)
    try:
        texts = load_catalogue()
    except (OSError, CatalogueError) as error:
        print("collector-texts: %s" % error, file=sys.stderr)
        return 1 if isinstance(error, CatalogueError) else 2

    if options.check:
        stale = stale_outputs(texts)
        for path in stale:
            print("collector-texts: %s is stale; run python tools/collector-texts/generate.py"
                  % os.path.relpath(path, REPO), file=sys.stderr)
        return 1 if stale else 0

    for path in write_outputs(texts):
        print("wrote %s" % os.path.relpath(path, REPO))
    print("%d texts" % len(texts))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
