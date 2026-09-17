#!/usr/bin/env python3
"""Generates src/Desktop/qml/components/Lucide.js from the Lucide SVGs kept beside it.

The desktop draws button icons from Lucide (https://lucide.dev, ISC). Qt's SVG renderer
does not resolve `stroke="currentColor"`, and the desktop has no C++ image provider that
every QML engine (application, screenshot harness, tests) would have to register, so the
icons are carried as the drawing elements of each SVG in a `.pragma library` JavaScript
file: AppButton builds a data: URL with the button's own text colour baked into the
stroke. This script is the only writer of that file; edit the SVGs, not the JavaScript.

Usage:
    python tools/lucide-icons/generate.py            # rewrite Lucide.js
    python tools/lucide-icons/generate.py --check    # exit 1 if Lucide.js is stale
"""

from __future__ import annotations

import argparse
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
ICON_DIR = os.path.join(REPO, "src", "Desktop", "resources", "icons", "lucide")
OUTPUT = os.path.join(REPO, "src", "Desktop", "qml", "components", "Lucide.js")

# The root attributes every Lucide icon shares; anything else in a file is refused so a
# stray icon from another set (or a hand-edited one) cannot slip in unnoticed.
EXPECTED_ROOT = {
    "xmlns": "http://www.w3.org/2000/svg",
    "width": "24",
    "height": "24",
    "viewBox": "0 0 24 24",
    "fill": "none",
    "stroke": "currentColor",
    "stroke-width": "2",
    "stroke-linecap": "round",
    "stroke-linejoin": "round",
}
NAME = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
VERSION_COMMENT = re.compile(r"<!--\s*@license lucide-static v([0-9][0-9.]*) - ISC\s*-->")
ROOT_TAG = re.compile(r"<svg\b([^>]*)>(.*)</svg>\s*$", re.S)
ATTRIBUTE = re.compile(r'([A-Za-z][A-Za-z0-9:-]*)="([^"]*)"')
ELEMENT = re.compile(r"<(path|circle|rect|line|polyline|polygon|ellipse)\b([^>]*?)/>")
# Plain geometry only: a colour, a style or a class on an element would either fight the
# stroke AppButton bakes in or mean the file is not an untouched Lucide icon.
ALLOWED_ELEMENT_ATTRIBUTES = {
    "d", "cx", "cy", "r", "rx", "ry", "x", "y", "x1", "y1", "x2", "y2", "width", "height", "points",
}


class IconError(ValueError):
    pass


def parse_icon(name: str, text: str) -> tuple[str, str]:
    """Returns (version, drawing elements) of one Lucide SVG, or raises IconError."""
    if not NAME.match(name):
        raise IconError("%s: icon names are lower-case words joined by '-'" % name)
    version_match = VERSION_COMMENT.search(text)
    if not version_match:
        raise IconError("%s: missing the upstream '@license lucide-static' comment" % name)
    root = ROOT_TAG.search(text)
    if not root:
        raise IconError("%s: not a single <svg> element" % name)
    attributes = dict(ATTRIBUTE.findall(root.group(1)))
    attributes.pop("class", None)
    if attributes != EXPECTED_ROOT:
        raise IconError("%s: root attributes differ from Lucide's: %r" % (name, attributes))
    body = root.group(2)
    elements = ELEMENT.findall(body)
    leftover = ELEMENT.sub("", body).strip()
    if not elements or leftover:
        raise IconError("%s: unexpected content inside <svg>: %r" % (name, leftover[:80]))
    rebuilt = []
    for tag, attrs in elements:
        parsed = ATTRIBUTE.findall(attrs)
        if ATTRIBUTE.sub("", attrs).strip():
            raise IconError("%s: cannot parse element attributes: %r" % (name, attrs))
        unexpected = sorted(key for key, _value in parsed if key not in ALLOWED_ELEMENT_ATTRIBUTES)
        if unexpected:
            raise IconError("%s: element attributes must be plain geometry, not %s" % (name, unexpected))
        rebuilt.append("<%s%s/>" % (tag, "".join(' %s="%s"' % pair for pair in parsed)))
    return version_match.group(1), "".join(rebuilt)


def load_icons(icon_dir: str = ICON_DIR) -> tuple[str, dict[str, str]]:
    """Returns (lucide version, {name: drawing elements}) for every SVG in icon_dir."""
    icons: dict[str, str] = {}
    versions: set[str] = set()
    for file in sorted(os.listdir(icon_dir)):
        if not file.endswith(".svg"):
            continue
        name = file[:-4]
        with io.open(os.path.join(icon_dir, file), "r", encoding="utf-8") as handle:
            version, elements = parse_icon(name, handle.read())
        versions.add(version)
        icons[name] = elements
    if not icons:
        raise IconError("no .svg files under %s" % icon_dir)
    if len(versions) != 1:
        raise IconError("icons come from different Lucide releases: %s" % sorted(versions))
    return versions.pop(), icons


FEATHER_LIST = re.compile(
    r"derived from the Feather project:\s*\n\s*\n(?P<names>[^\n]+)\n", re.S
)


def feather_icons(icon_dir: str = ICON_DIR) -> set[str]:
    """The icon names Lucide's LICENSE says are derived from Feather (MIT, Cole Bemis).

    Those icons carry a second licence whose notice must also appear in every copy, so
    the generated library names the ones it actually contains. Read from the bundled
    LICENSE rather than hard-coded, so a Lucide update that moves the line moves it here.
    """
    with io.open(os.path.join(icon_dir, "LICENSE"), "r", encoding="utf-8") as handle:
        text = handle.read()
    match = FEATHER_LIST.search(text)
    if not match:
        raise IconError("LICENSE does not list the Feather-derived icons; Lucide changed its licence file")
    return {name.strip() for name in match.group("names").split(",") if name.strip()}


def js_string(text: str) -> str:
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def render(version: str, icons: dict[str, str], feather: set[str] | None = None) -> str:
    """The JavaScript library text (LF line endings; git normalises on checkout).

    `feather` is the Feather-derived subset of Lucide (see feather_icons); the MIT notice
    is emitted only when one of those icons is actually bundled.
    """
    from_feather = sorted(name for name in icons if name in (feather or set()))
    lines = [
        ".pragma library",
        "",
        "// Generated by tools/lucide-icons/generate.py from",
        "// src/Desktop/resources/icons/lucide/*.svg. Do not edit: edit the SVGs and rerun it.",
        "//",
        "// Lucide Icons v%s - https://lucide.dev - ISC License" % version,
        "// Copyright (c) 2026 Lucide Icons and Contributors (see resources/icons/lucide/LICENSE)",
        "//",
        "// Permission to use, copy, modify, and/or distribute this software for any purpose",
        "// with or without fee is hereby granted, provided that the above copyright notice",
        "// and this permission notice appear in all copies.",
    ]
    if from_feather:
        lines += [
            "//",
            "// The following icons are derived from the Feather project and are also under",
            "// The MIT License (MIT), Copyright (c) 2013-present Cole Bemis:",
            "//   " + ", ".join(from_feather),
            "//",
            "// Permission is hereby granted, free of charge, to any person obtaining a copy of",
            "// this software and associated documentation files (the \"Software\"), to deal in",
            "// the Software without restriction, including without limitation the rights to",
            "// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of",
            "// the Software, and to permit persons to whom the Software is furnished to do so,",
            "// subject to the following conditions: The above copyright notice and this",
            "// permission notice shall be included in all copies or substantial portions of",
            "// the Software. THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND.",
        ]
    lines += [
        "",
        'var version = "%s"' % version,
        "",
        "// Icons Lucide derives from Feather (MIT, see the header); the rest are ISC only.",
        "var fromFeather = [%s]" % ", ".join(js_string(name) for name in from_feather),
        "",
        "// name -> the drawing elements of the icon's 24x24 viewBox.",
        "var icons = {",
    ]
    names = sorted(icons)
    for index, name in enumerate(names):
        comma = "," if index + 1 < len(names) else ""
        lines.append("    %s: %s%s" % (js_string(name), js_string(icons[name]), comma))
    lines += [
        "}",
        "",
        "function has(name) {",
        "    return Object.prototype.hasOwnProperty.call(icons, name)",
        "}",
        "",
        "// The whole SVG with `color` (a QML color or a #rrggbb / #aarrggbb string) as the stroke.",
        "function svg(name, color) {",
        "    if (!has(name))",
        '        return ""',
        "    var text = String(color)",
        '    var opacity = ""',
        "    if (text.length === 9) {",
        "        // QML writes a translucent colour as #aarrggbb; SVG wants rgb + stroke-opacity.",
        "        opacity = ' stroke-opacity=\"' + (parseInt(text.substr(1, 2), 16) / 255).toFixed(3) + '\"'",
        '        text = "#" + text.substr(3)',
        "    }",
        "    return '<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\"'",
        "           + ' fill=\"none\" stroke=\"' + text + '\"' + opacity",
        "           + ' stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">'",
        "           + icons[name] + '</svg>'",
        "}",
        "",
        "// An Image source for `name` drawn in `color`; empty when the icon is not bundled.",
        "function source(name, color) {",
        "    var text = svg(name, color)",
        '    return text ? "data:image/svg+xml;utf8," + encodeURIComponent(text) : ""',
        "}",
        "",
    ]
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="do not write; exit 1 when Lucide.js differs from the SVGs")
    args = parser.parse_args(argv)
    try:
        version, icons = load_icons()
        feather = feather_icons()
    except (IconError, OSError) as error:
        print("FAILED: %s" % error, file=sys.stderr)
        return 2
    text = render(version, icons, feather)
    current = ""
    if os.path.exists(OUTPUT):
        with io.open(OUTPUT, "r", encoding="utf-8", newline="") as handle:
            current = handle.read().replace("\r\n", "\n")
    if args.check:
        if current != text:
            print("STALE: %s does not match the SVGs; run tools/lucide-icons/generate.py" % OUTPUT,
                  file=sys.stderr)
            return 1
        print("Lucide.js is up to date (%d icons, Lucide v%s)" % (len(icons), version))
        return 0
    if current == text:
        print("unchanged: %s" % OUTPUT)
        return 0
    with io.open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print("wrote %s (%d icons, Lucide v%s)" % (OUTPUT, len(icons), version))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
