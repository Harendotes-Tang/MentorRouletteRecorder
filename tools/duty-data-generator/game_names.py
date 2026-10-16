#!/usr/bin/env python3
"""Offline half of the game-name generator: data/i18n/game-names.<version>.json.

The Desktop interface can switch between zh-CN, zh-TW, en and ja. The zh-Hans names already
ship in data/duties, data/jobs and data/roulettes; this file adds the other three languages,
keyed by the same ids, plus the duty category and expansion labels those files use.

This module never touches the network. generate.py, the one Python script the static
boundary check lets fetch (docs/privacy-boundary.md), downloads the sources and hands the
already-fetched rows and CSV text to the functions here, which parse, merge and write:

  * XIVAPI v2 rows (language=en and language=ja, pinned to the base duty file's game version)
    of ContentFinderCondition, ClassJob, ContentRoulette, ContentType and ExVersion;
  * the CSVs of thewakingsands/ffxiv-datamining-tc, the SaintCoinach export of the FINAL
    FANTASY XIV Traditional Chinese client, for the official zh-TW text;
  * ExVersion.csv of thewakingsands/ffxiv-datamining-cn for the zh-CN expansion names.

The Traditional Chinese client trails the Chinese and global ones. An id it does not have yet
gets a mechanical OpenCC s2twp conversion of its zh-Hans name and is listed under
"zh-TW_converted", so the interface can tell an official name from a converted one. English
and Japanese are never converted or guessed: an id either source lacks stops the run.

Usage (the entry point is generate.py):
    python tools/duty-data-generator/generate.py --game-names [--version 2026-10-15]
    python tools/duty-data-generator/generate.py --game-names --dry-run
"""

from __future__ import annotations

import csv
import glob
import io
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
LANGUAGES = ("zh-TW", "en", "ja")
XIVAPI_LANGUAGES = ("en", "ja")
# TerritoryType@as(raw) returns the bare row id instead of resolving the linked row.
XIVAPI_FIELDS = {
    "ContentFinderCondition": "Name,TerritoryType@as(raw)",
    "ClassJob": "Name,NameEnglish,Abbreviation",
    "ContentRoulette": "Name,Category",
    "ContentType": "Name",
    "ExVersion": "Name",
}
TC_REPOSITORY = "thewakingsands/ffxiv-datamining-tc"
CN_REPOSITORY = "thewakingsands/ffxiv-datamining-cn"
TC_COLUMNS = {
    "ContentFinderCondition": ("Name", "TerritoryType"),
    "ClassJob": ("Name", "Abbreviation"),
    "ContentRoulette": ("Name", "Category"),
    "ContentType": ("Name",),
    "ExVersion": ("Name",),
}
CN_COLUMNS = {"ExVersion": ("Name",)}
OPENCC_CONFIG = "s2twp"
# A remastered dungeon moves to a new territory, so a client on an older patch legitimately
# disagrees on a handful of rows. Disagreement on more than this share of rows means the row
# ids no longer line up, and the run stops instead of pairing names with the wrong duties.
MAX_TERRITORY_MISMATCH_RATIO = 0.05
# A territory column that parses on fewer than this share of the rows it should cover has
# changed shape upstream; the mismatch guard would otherwise pass on zero compared rows.
MIN_TERRITORY_CHECKED_RATIO = 0.9
# The fallback category (generate.UNKNOWN_CATEGORY) is the project's own bucket, not game text.
PROJECT_LABELS = {
    "其他": {"zh-TW": "其他", "en": "Other", "ja": "その他"},
}
RAW_NOTE = ("Raw downloads are kept outside the repository; only their SHA-256 digests are "
            "recorded here so the result stays reproducible without redistributing upstream "
            "data.")


class SourceError(ValueError):
    """A source is missing data the file must carry; nothing is written."""


# --------------------------------------------------------------------------- parsing


def parse_sheet_csv(text, columns, sheet="sheet"):
    """{row_id: {column: value}} from a SaintCoinach CSV, locating columns by name.

    Line 1 holds column keys, line 2 column names, line 3 types; data follows. A column that
    is not where the name says fails loudly instead of shifting every value by one column.
    """
    rows = list(csv.reader(io.StringIO(text)))
    if len(rows) < 3:
        raise SourceError("%s.csv is too short to carry a header" % sheet)
    names = rows[1]
    missing = [column for column in columns if column not in names]
    if missing:
        raise SourceError("%s.csv has no %s column; upstream schema changed"
                          % (sheet, ", ".join(missing)))
    indexes = {column: names.index(column) for column in columns}
    result = {}
    for row in rows[3:]:
        try:
            row_id = int(row[0])
        except (IndexError, ValueError):
            continue
        result[row_id] = {column: (row[index].strip() if index < len(row) else "")
                          for column, index in indexes.items()}
    return result


def parse_sheets(texts, columns):
    """{sheet: rows} for every sheet in columns, from {sheet: CSV text}."""
    return {sheet: parse_sheet_csv(texts[sheet], wanted, sheet)
            for sheet, wanted in columns.items()}


def client_version(message):
    """The client build the datamining commit message names ("ver 2026.09.14.0000.0000")."""
    match = re.search(r"\b(\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4})\b", message or "")
    return match.group(1) if match else None


# --------------------------------------------------------------------------- zh-TW fallback


class Converter:
    """OpenCC s2twp, imported on first use: a run the TC client fully covers never needs it."""

    def __init__(self, config=OPENCC_CONFIG):
        self.config = config
        self._convert = None
        self.info = None

    def __call__(self, text):
        if self._convert is None:
            self._convert, self.info = load_opencc(self.config)
        return self._convert(text)


def load_opencc(config):
    """(convert function, provenance info). Accepts the official or the pure-Python package."""
    try:
        import opencc  # type: ignore
    except ImportError as error:
        raise SourceError(
            "the Traditional Chinese client lacks some names and OpenCC is not installed; "
            "run `python -m pip install opencc` (or opencc-python-reimplemented) and retry"
        ) from error
    converter = None
    for name in (config, config + ".json"):
        try:
            converter = opencc.OpenCC(name)
            break
        except Exception:  # noqa: BLE001 - the two packages raise different types
            continue
    if converter is None:
        raise SourceError("OpenCC could not load the %s configuration" % config)
    package, version = opencc_package()
    return converter.convert, {"method": "OpenCC", "config": config,
                               "package": package, "package_version": version}


def opencc_package():
    from importlib import metadata
    for package in ("opencc", "OpenCC", "opencc-python-reimplemented"):
        try:
            return package, metadata.version(package)
        except metadata.PackageNotFoundError:
            continue
    return "opencc", None


def pick_zh_tw(official, zh_hans, convert):
    """(name, converted?): the TC client's own text, else the converted zh-Hans name."""
    if official:
        return official, False
    if not zh_hans:
        return None, False
    return convert(zh_hans), True


# --------------------------------------------------------------------------- building


def capitalize_first(text):
    """Upper-case only the first character: "the Praetorium" -> "The Praetorium"."""
    return text[:1].upper() + text[1:] if text else text


def require(value, what):
    if not value:
        raise SourceError("no %s" % what)
    return value


class TerritoryAudit:
    """Compares each source's territory with the duty file's, per language.

    Rows disagreeing on more than MAX_TERRITORY_MISMATCH_RATIO (and more than five) mean the
    row ids are shifted; a territory column readable on fewer than MIN_TERRITORY_CHECKED_RATIO
    of the named rows means the column itself moved. Either stops the run.
    """

    def __init__(self):
        self.eligible = {language: 0 for language in LANGUAGES}
        self.checked = {language: 0 for language in LANGUAGES}
        self.mismatches = {language: [] for language in LANGUAGES}

    def note(self, language, content_id, value, expected):
        if expected is None:
            return
        self.eligible[language] += 1
        if territory_of(value) is None:
            return
        self.checked[language] += 1
        if territory_of(value) != expected:
            self.mismatches[language].append(content_id)

    def verify(self):
        for language in LANGUAGES:
            eligible, checked = self.eligible[language], self.checked[language]
            mismatched = len(self.mismatches[language])
            if checked < MIN_TERRITORY_CHECKED_RATIO * eligible:
                raise SourceError("the %s source gives a readable territory for only %d of %d "
                                  "duties; its territory column changed" % (language, checked,
                                                                           eligible))
            if mismatched > max(5, MAX_TERRITORY_MISMATCH_RATIO * checked):
                raise SourceError("the %s source disagrees with the duty file on the territory "
                                  "of %d of %d duties; the row ids no longer line up"
                                  % (language, mismatched, checked))


def territory_of(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def build_duties(base_duties, xivapi, tc, convert):
    """(names, converted ids, TerritoryAudit) for every base content_id."""
    names, converted = {}, []
    audit = TerritoryAudit()
    for duty in sorted(base_duties, key=lambda row: row["content_id"]):
        content_id = duty["content_id"]
        territory = duty.get("territory_id")
        entry = {}
        for language in XIVAPI_LANGUAGES:
            row = xivapi[language].get(content_id) or {}
            name = require((row.get("Name") or "").strip(),
                           "%s name for content_id %d" % (language, content_id))
            entry[language] = capitalize_first(name) if language == "en" else name
            audit.note(language, content_id, row.get("TerritoryType@as(raw)"), territory)
        tc_row = tc.get(content_id) or {}
        zh_tw, was_converted = pick_zh_tw(tc_row.get("Name"), duty.get("localized_name"), convert)
        if tc_row.get("Name"):
            audit.note("zh-TW", content_id, tc_row.get("TerritoryType"), territory)
        names[str(content_id)] = {"zh-TW": require(zh_tw, "zh-TW name for content_id %d"
                                                   % content_id), **entry}
        if was_converted:
            converted.append(content_id)
    audit.verify()
    return names, converted, audit


def build_jobs(base_jobs, xivapi, tc, convert):
    """(names, converted ids). "abbr" appears only for a language whose abbreviation differs."""
    names, converted = {}, []
    for job in sorted(base_jobs, key=lambda row: row["job_id"]):
        job_id = job["job_id"]
        en = xivapi["en"].get(job_id) or {}
        ja = xivapi["ja"].get(job_id) or {}
        tc_row = tc.get(job_id) or {}
        zh_tw, was_converted = pick_zh_tw(tc_row.get("Name"), job.get("name_zh"), convert)
        entry = {
            "zh-TW": require(zh_tw, "zh-TW name for job %d" % job_id),
            "en": require((en.get("NameEnglish") or "").strip(), "en name for job %d" % job_id),
            "ja": require((ja.get("Name") or "").strip(), "ja name for job %d" % job_id),
        }
        abbreviations = {"zh-TW": tc_row.get("Abbreviation"), "en": en.get("Abbreviation"),
                         "ja": ja.get("Abbreviation")}
        differing = {language: value for language, value in abbreviations.items()
                     if value and value != job.get("abbreviation")}
        if differing:
            entry["abbr"] = differing
        names[str(job_id)] = entry
        if was_converted:
            converted.append(job_id)
    return names, converted


def build_roulettes(base_roulettes, xivapi, tc, convert):
    """(names, converted ids) from ContentRoulette.Category, the short form cn.json uses."""
    names, converted = {}, []
    for roulette in sorted(base_roulettes, key=lambda row: row["roulette_id"]):
        roulette_id = roulette["roulette_id"]
        zh_tw, was_converted = pick_zh_tw((tc.get(roulette_id) or {}).get("Category"),
                                          roulette.get("localized_name"), convert)
        entry = {"zh-TW": require(zh_tw, "zh-TW name for roulette %d" % roulette_id)}
        for language in XIVAPI_LANGUAGES:
            row = xivapi[language].get(roulette_id) or {}
            entry[language] = require((row.get("Category") or "").strip(),
                                      "%s name for roulette %d" % (language, roulette_id))
        names[str(roulette_id)] = entry
        if was_converted:
            converted.append(roulette_id)
    return names, converted


def build_duty_categories(used, xivapi, tc, convert, category_by_content_type):
    """(labels, converted, project) for every category label, each from its own ContentType.

    category_by_content_type is generate.CATEGORY_BY_CONTENT_TYPE: a label translates to the
    official name of the ContentType it is derived from, so the wording can differ from the
    zh-Hans label (四人迷宫 <- Dungeons <- 迷宮挑戰).
    """
    rows_by_english = {(fields.get("Name") or "").strip(): row_id
                       for row_id, fields in xivapi["en"].items()}
    labels, converted = {}, []
    for content_type, label in category_by_content_type.items():
        row_id = rows_by_english.get(content_type)
        if row_id is None:
            if label in used:
                raise SourceError("ContentType %r (category %s) is gone from the sheet"
                                  % (content_type, label))
            continue
        zh_tw, was_converted = pick_zh_tw((tc.get(row_id) or {}).get("Name"), label, convert)
        labels[label] = {"zh-TW": zh_tw, "en": content_type,
                         "ja": require((xivapi["ja"].get(row_id) or {}).get("Name"),
                                       "ja ContentType %d" % row_id)}
        if was_converted:
            converted.append(label)
    project = []
    for label, names in PROJECT_LABELS.items():
        labels[label] = dict(names)
        project.append(label)
    missing = sorted(set(used) - set(labels))
    if missing:
        raise SourceError("no translation for duty categories %s" % ", ".join(missing))
    return labels, converted, project


def build_expansions(used, xivapi, tc, cn, convert):
    """(labels, converted) keyed by the English ExVersion name the duty file stores."""
    labels, converted = {}, []
    for row_id in sorted(xivapi["en"]):
        english = (xivapi["en"][row_id].get("Name") or "").strip()
        if not english:
            continue
        zh_cn = (cn.get(row_id) or {}).get("Name")
        zh_tw, was_converted = pick_zh_tw((tc.get(row_id) or {}).get("Name"), zh_cn, convert)
        ja = (xivapi["ja"].get(row_id) or {}).get("Name")
        if not (zh_cn and zh_tw and ja):
            if english in used:
                raise SourceError("expansion %s lacks a zh-CN, zh-TW or ja name" % english)
            continue
        labels[english] = {"zh-CN": zh_cn, "zh-TW": zh_tw, "en": english, "ja": ja}
        if was_converted:
            converted.append(english)
    missing = sorted(set(used) - set(labels) - {"UNKNOWN"})
    if missing:
        raise SourceError("no translation for expansions %s" % ", ".join(missing))
    return labels, converted


def build_maps(base, xivapi, tc, cn, convert, category_by_content_type):
    """Every name map plus the converted / project-maintained lists and the audit results."""
    duties, duties_converted, audit = build_duties(
        base["duties"], xivapi["ContentFinderCondition"], tc["ContentFinderCondition"], convert)
    jobs, jobs_converted = build_jobs(base["jobs"], xivapi["ClassJob"], tc["ClassJob"], convert)
    roulettes, roulettes_converted = build_roulettes(
        base["roulettes"], xivapi["ContentRoulette"], tc["ContentRoulette"], convert)
    used_categories = {duty.get("duty_category") for duty in base["duties"]}
    categories, categories_converted, project = build_duty_categories(
        used_categories, xivapi["ContentType"], tc["ContentType"], convert,
        category_by_content_type)
    used_expansions = {duty.get("expansion") for duty in base["duties"]}
    expansions, expansions_converted = build_expansions(
        used_expansions, xivapi["ExVersion"], tc["ExVersion"], cn["ExVersion"], convert)
    return {
        "duties": duties,
        "jobs": jobs,
        "roulettes": roulettes,
        "duty_categories": categories,
        "expansions": expansions,
        "zh-TW_converted": {
            "duties": duties_converted,
            "jobs": jobs_converted,
            "roulettes": roulettes_converted,
            "duty_categories": categories_converted,
            "expansions": expansions_converted,
        },
        "project_maintained": {"duty_categories": project},
        "territory_checked": audit.checked,
        "territory_mismatches": audit.mismatches,
    }


def row_counts(maps):
    counts = {}
    for key in ("duties", "jobs", "roulettes", "duty_categories", "expansions"):
        converted = len(maps["zh-TW_converted"][key])
        counts[key] = {"total": len(maps[key]), "zh_tw_official": len(maps[key]) - converted,
                       "zh_tw_converted": converted}
    counts["duty_categories"]["project_maintained"] = len(
        maps["project_maintained"]["duty_categories"])
    counts["duty_categories"]["zh_tw_official"] -= counts["duty_categories"]["project_maintained"]
    return counts


# --------------------------------------------------------------------------- document

SOURCE_TEXT = (
    "Generated by tools/duty-data-generator/game_names.py from XIVAPI v2 (English and Japanese "
    "client text), thewakingsands/ffxiv-datamining-tc (community SaintCoinach export of the "
    "Traditional Chinese client text) and thewakingsands/ffxiv-datamining-cn (Chinese client "
    "text, expansion names only); neither datamining repository carries a licence file and both "
    "are credited here as the source. Names the Traditional Chinese client does not have yet are "
    "an OpenCC s2twp conversion of the zh-Hans name and are listed under zh-TW_converted. "
    "Display data only: it never identifies a roulette and never makes a protocol profile "
    "usable. The names are FINAL FANTASY XIV game text, © SQUARE ENIX CO., LTD., used for "
    "non-commercial display under the FINAL FANTASY XIV Materials Usage License with "
    "attribution."
)


def datamining_provenance(fetched):
    """The provenance block of one datamining repository, from what generate.py fetched."""
    return {
        "repository": fetched["repository"],
        "ref": fetched["ref"],
        "commit": fetched["commit"],
        "commit_message": fetched["commit_message"],
        "client_version": client_version(fetched["commit_message"]),
        "requests": fetched["requests"],
    }


def make_provenance(fetched_at_utc, xivapi, tc_fetched, cn_fetched, fallback):
    return {
        "fetched_at_utc": fetched_at_utc,
        "xivapi": xivapi,
        "datamining_tc": datamining_provenance(tc_fetched),
        "datamining_cn": datamining_provenance(cn_fetched),
        "zh_tw_fallback": fallback,
        "note": RAW_NOTE,
    }


def build_document(version, base_paths, maps, provenance):
    document = {
        "schema_version": 1,
        "data_version": version,
        "source_language": "zh-Hans",
        "languages": list(LANGUAGES),
        "updated_at_utc": provenance["fetched_at_utc"],
        "source": SOURCE_TEXT,
        "provenance": dict(provenance, base_files=base_paths, row_counts=row_counts(maps),
                           territory_checked=maps["territory_checked"],
                           territory_mismatches=maps["territory_mismatches"]),
    }
    for key in ("duties", "jobs", "roulettes", "duty_categories", "expansions",
                "zh-TW_converted", "project_maintained"):
        document[key] = maps[key]
    return document


def report(maps, provenance, raw_dir):
    """The run summary generate.py prints before writing (or instead of it, on a dry run)."""
    print("raw downloads:   %s" % (raw_dir or "not kept (dry run)"))
    print("xivapi version:  %s %s" % (provenance["xivapi"]["game_version"],
                                      provenance["xivapi"]["game_version_names"]))
    print("tc client:       %s (%s)" % (provenance["datamining_tc"]["client_version"],
                                        provenance["datamining_tc"]["commit"]))
    for key, counts in row_counts(maps).items():
        print("%-16s %s" % (key + ":", json.dumps(counts)))
    for language, ids in maps["territory_mismatches"].items():
        if ids:
            print("territory differs (%s): %s" % (language, ids))


# --------------------------------------------------------------------------- output

INLINE_KEYS = ("duties", "jobs", "roulettes", "duty_categories", "expansions",
               "zh-TW_converted", "project_maintained")


def render(document):
    """JSON text with one line per id in the name maps, so a diff reads one name per line."""
    lines = ["{"]
    items = list(document.items())
    for index, (key, value) in enumerate(items):
        comma = "," if index < len(items) - 1 else ""
        label = json.dumps(str(key), ensure_ascii=False)
        if key in INLINE_KEYS and isinstance(value, dict) and value:
            lines.append("  %s: {" % label)
            entries = list(value.items())
            for inner, (name, entry) in enumerate(entries):
                lines.append("    %s: %s%s" % (
                    json.dumps(str(name), ensure_ascii=False),
                    json.dumps(entry, ensure_ascii=False, separators=(", ", ": ")),
                    "," if inner < len(entries) - 1 else ""))
            lines.append("  }" + comma)
        else:
            text = json.dumps(value, ensure_ascii=False, indent=2).replace("\n", "\n  ")
            lines.append("  %s: %s%s" % (label, text, comma))
    lines.append("}")
    return "\n".join(lines) + "\n"


def write_document(path, document):
    """Render, prove the text parses back to the document, then replace the file in one step.

    The text goes to a sibling temporary file first, so a failed write never leaves a
    truncated data file behind or clobbers an existing one.
    """
    text = render(document)
    if json.loads(text) != document:
        raise ValueError("rendered JSON does not round-trip; nothing written")
    temporary = path + ".tmp"
    try:
        with io.open(temporary, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)


# --------------------------------------------------------------------------- base files


def latest_duty_file(repo=REPO):
    """The newest real data/duties/cn.<date>.json (the synthetic sample never counts)."""
    paths = [path for path in glob.glob(os.path.join(repo, "data", "duties", "cn.*.json"))
             if re.fullmatch(r"cn\.\d{4}-\d{2}-\d{2}\.json", os.path.basename(path))]
    return max(paths) if paths else None


def repository_path(path, repo=REPO):
    """Repository-relative path with forward slashes, for provenance; absolute outside it."""
    absolute = os.path.abspath(path)
    try:
        relative = os.path.relpath(absolute, repo)
    except ValueError:
        relative = None  # another drive
    if relative is not None and relative != ".." and not relative.startswith(".." + os.sep):
        return relative.replace(os.sep, "/")
    return absolute.replace(os.sep, "/")


def load_json(path):
    with io.open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def load_base(duties_path, jobs_path, roulettes_path):
    """(base rows, duty data_version, the duty file's pinned XIVAPI version)."""
    duties_doc = load_json(duties_path)
    base = {
        "duties": duties_doc.get("duties") or [],
        "jobs": load_json(jobs_path).get("jobs") or [],
        "roulettes": load_json(roulettes_path).get("roulettes") or [],
    }
    empty = [key for key, rows in base.items() if not rows]
    if empty:
        raise SourceError("the base %s list is empty; check --duties / --jobs / --roulettes"
                          % ", ".join(empty))
    pinned = (duties_doc.get("provenance") or {}).get("xivapi_game_version")
    return base, duties_doc.get("data_version"), pinned
