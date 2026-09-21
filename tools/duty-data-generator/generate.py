#!/usr/bin/env python3
"""Build data/duties/<region>.<version>.json from public sources.

Two sources, both public and both cited in the output:

  * XIVAPI v2 (https://v2.xivapi.com) for ContentFinderCondition row ids, English names,
    territory ids, content types, level requirements and party composition (party_size);
  * the thewakingsands/ffxiv-datamining-cn CSV export for the Simplified Chinese names.

The two are joined by ContentFinderCondition row id, which is the same key in both.

Constraints: no game data file is copied into the repository, no row is written that did
not come from one of the two sources, and a Chinese name is never invented. A row without
a Chinese name is written to the global file only; the Collector then shows it as the
unknown duty rather than as a guess.

Raw downloads are written to a scratch directory outside the repository and only their
SHA-256 digests are recorded, so the repository never carries a redistributed copy of the
upstream data.

Usage:
    python tools/duty-data-generator/generate.py [--version 2026-09-04] [--out-dir data/duties]
    python tools/duty-data-generator/generate.py --dry-run     # fetch and report, write nothing
    python tools/duty-data-generator/generate.py --add-party-size data/duties/cn.2026-09-04.json
        # add party_size to an existing file, pinned to the game version it was built from
"""

from __future__ import annotations

import argparse
import csv
import datetime
import hashlib
import io
import json
import os
import ssl
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

XIVAPI_SHEET = "https://v2.xivapi.com/api/sheet/ContentFinderCondition"
# ContentMemberType is the game's own party composition row: MembersPerParty x PartyCount is
# the number of players the duty finder fills (4, 8, 24, ...). ContentType cannot tell an
# 8-player raid from a 24-player alliance raid; this can, without guessing from names.
XIVAPI_FIELDS = (
    "Name,ClassJobLevelRequired,ContentType.Name,TerritoryType.ExVersion.Name,"
    "ContentMemberType.MembersPerParty,ContentMemberType.PartyCount"
)
XIVAPI_PAGE_SIZE = 500
XIVAPI_MAX_PAGES = 10

DATAMINING_CN_CSV = (
    "https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/"
    "ContentFinderCondition.csv"
)

USER_AGENT = "MentorRecorder-duty-data-generator/1.0 (+local, no telemetry)"

# ContentType.Name (English, from the game's own ContentType sheet) to the category the UI
# groups by. Anything not listed becomes 其他 rather than being forced into a bucket.
#
# Alliance raids are deliberately not a separate category here: ContentFinderCondition's
# ContentType does not distinguish them from other raids. The distinction is carried by the
# separate party_size field (from ContentMemberType) instead of being folded into the category.
CATEGORY_BY_CONTENT_TYPE = {
    "Dungeons": "四人迷宫",
    "Guildhests": "行会令",
    "Trials": "讨伐歼灭战",
    "Raids": "大型任务",
    "PvP": "PVP",
    "Quest Battles": "任务战斗",
    "Treasure Hunt": "寻宝",
    "Deep Dungeons": "深层迷宫",
    "Adventuring Forays": "探索性任务",
    "Ultimate Raids": "绝境战",
    "V&C Dungeon Finder": "多变迷宫",
    "Chaotic Alliance Raid": "团队任务",
    "Duty Roulette": "随机任务",
    "Gold Saucer": "金碟游乐场",
    "The Masked Carnivale": "假面狂欢",
    "Disciples of the Land": "采集活动",
    "Eureka": "禁地探索",
    "Save the Queen": "南方战线",
    "Occult Crescent": "新月岛",
}

UNKNOWN_CATEGORY = "其他"


# --------------------------------------------------------------------------- fetching


def http_get(url: str, timeout: int = 60) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    # TLS 1.2 or better, certificate verification on. Both are the defaults of a modern
    # Python; they are stated explicitly so that a downgrade would be a visible edit.
    context = ssl.create_default_context()
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
        return response.read()


def fetch_english_rows(raw_dir: str, game_version: str | None = None):
    """Page through ContentFinderCondition. Returns (rows, urls, digest, api_version).

    game_version pins every page to one XIVAPI game version (the key recorded as
    provenance.xivapi_game_version), so an existing data version can be rebuilt from the
    same game data it was first built from.
    """
    rows = {}
    urls = []
    pages = []
    after = 0
    api_version = None
    for _ in range(XIVAPI_MAX_PAGES):
        params = {
            "fields": XIVAPI_FIELDS,
            "limit": XIVAPI_PAGE_SIZE,
            "after": after,
        }
        if game_version:
            params["version"] = game_version
        query = urllib.parse.urlencode(params)
        url = XIVAPI_SHEET + "?" + query
        urls.append(url)
        body = http_get(url)
        pages.append(body)
        document = json.loads(body.decode("utf-8"))
        api_version = document.get("version") or api_version
        page_rows = document.get("rows") or []
        if not page_rows:
            break
        for row in page_rows:
            rows[int(row["row_id"])] = row
        after = int(page_rows[-1]["row_id"])
        if len(page_rows) < XIVAPI_PAGE_SIZE:
            break
    else:
        # Reached only when the last allowed page still came back full, i.e. the sheet is
        # provably longer than XIVAPI_MAX_PAGES x XIVAPI_PAGE_SIZE rows. The loop used to fall
        # out of range() here without a word, and everything downstream treated the truncated
        # result as the whole sheet: a data/duties/*.json missing entries would ship with the
        # release, and the only symptom is a player seeing a duty named as the unknown one.
        raise ValueError(
            "ContentFinderCondition 分页已达到上限"
            "（%d 页 x %d 条），第 %d 页仍是满页，"
            "说明数据还没取完；请调大 XIVAPI_MAX_PAGES "
            "后重新生成。"
            % (XIVAPI_MAX_PAGES, XIVAPI_PAGE_SIZE, XIVAPI_MAX_PAGES))

    blob = b"".join(pages)
    path = os.path.join(raw_dir, "xivapi_contentfindercondition.json")
    with open(path, "wb") as handle:
        handle.write(blob)
    return rows, urls, hashlib.sha256(blob).hexdigest(), api_version


def fetch_chinese_names(raw_dir: str):
    """Returns (names_by_row_id, url, digest)."""
    body = http_get(DATAMINING_CN_CSV)
    path = os.path.join(raw_dir, "ContentFinderCondition.cn.csv")
    with open(path, "wb") as handle:
        handle.write(body)
    return parse_cn_csv(body.decode("utf-8-sig")), DATAMINING_CN_CSV, hashlib.sha256(body).hexdigest()


def parse_cn_csv(text: str):
    """Extract row_id -> Chinese name from a SaintCoinach-style export.

    Line 1 is the column key row, line 2 the column names, line 3 the column types, and the
    data follows. The Name column is located by name rather than by index so a schema change
    upstream fails loudly instead of silently shifting every value by one column.
    """
    reader = csv.reader(io.StringIO(text))
    header = None
    names = {}
    name_index = None
    for index, row in enumerate(reader):
        if index == 0:
            header = row
            continue
        if index == 1:
            if "Name" not in row:
                raise ValueError("the CN CSV has no Name column; upstream schema changed")
            name_index = row.index("Name")
            continue
        if index == 2:
            continue
        if not row or name_index is None or len(row) <= name_index:
            continue
        try:
            row_id = int(row[0])
        except ValueError:
            continue
        value = row[name_index].strip()
        if value:
            names[row_id] = value
    if header is None:
        raise ValueError("the CN CSV was empty")
    return names


# --------------------------------------------------------------------------- building


def linked(field):
    """Value of a linked field: the numeric target, or None."""
    if not isinstance(field, dict):
        return None
    return field.get("value")


def nested_name(field):
    if not isinstance(field, dict):
        return None
    fields = field.get("fields")
    if not isinstance(fields, dict):
        return None
    value = fields.get("Name")
    return value if isinstance(value, str) and value else None


def deep_name(field, key):
    """Name of a link reached through one more link, for example TerritoryType.ExVersion."""
    if not isinstance(field, dict):
        return None
    fields = field.get("fields")
    if not isinstance(fields, dict):
        return None
    return nested_name(fields.get(key))


def party_size(field):
    """Players the duty finder fills: MembersPerParty x PartyCount, or None.

    4 for a light party, 8 for a full party, 24 for an alliance of three parties. A row that
    does not state its composition is None rather than a guess.
    """
    if not isinstance(field, dict):
        return None
    fields = field.get("fields")
    if not isinstance(fields, dict):
        return None
    members = fields.get("MembersPerParty")
    parties = fields.get("PartyCount")
    if isinstance(members, bool) or isinstance(parties, bool):
        return None
    if not isinstance(members, int) or not isinstance(parties, int):
        return None
    if members <= 0 or parties <= 0:
        return None
    return members * parties


def build_rows(english_rows, chinese_names, language):
    """Builds the duty rows of one localisation. Sorted by content_id for a stable diff."""
    duties = []
    for row_id in sorted(english_rows):
        row = english_rows[row_id]
        fields = row.get("fields") or {}
        english = (fields.get("Name") or "").strip()
        chinese = chinese_names.get(row_id, "").strip()
        name = chinese if language == "zh-Hans" else english
        if not name:
            continue
        territory = linked(fields.get("TerritoryType"))
        duties.append({
            "content_id": row_id,
            "territory_id": territory if territory else None,
            "localized_name": name,
            "duty_category": CATEGORY_BY_CONTENT_TYPE.get(
                nested_name(fields.get("ContentType")) or "", UNKNOWN_CATEGORY),
            "expansion": deep_name(fields.get("TerritoryType"), "ExVersion") or "UNKNOWN",
            "level": int(fields.get("ClassJobLevelRequired") or 0),
            "party_size": party_size(fields.get("ContentMemberType")),
            "enabled": True,
        })
    return duties


def with_party_size(document, english_rows, source):
    """A copy of an existing duty document with party_size filled in from english_rows.

    Every other field, the key order inside each row and the row order stay exactly as they
    were: this is how a data version that was generated before party_size existed gains the
    field without also picking up whatever changed upstream since (new rows, renamed duties).
    The fetch that supplied the value is recorded under provenance.party_size.
    """
    duties = []
    missing = 0
    for row in document.get("duties") or []:
        source_row = english_rows.get(row.get("content_id"))
        value = party_size((source_row or {}).get("fields", {}).get("ContentMemberType"))
        if value is None:
            missing += 1
        updated = {}
        for key, item in row.items():
            if key == "party_size":
                continue
            if key == "enabled" and "party_size" not in updated:
                updated["party_size"] = value
            updated[key] = item
            if key == "level":
                updated["party_size"] = value
        if "party_size" not in updated:
            updated["party_size"] = value
        duties.append(updated)

    provenance = dict(document.get("provenance") or {})
    provenance["party_size"] = dict(source, rows_without_party_size=missing)
    result = dict(document)
    result["provenance"] = provenance
    result["duties"] = duties
    return result


def add_party_size_to_files(paths, raw_dir, game_version=None):
    """--add-party-size: rewrite existing files in place. Returns a process exit code."""
    documents = []
    for path in paths:
        with io.open(path, "r", encoding="utf-8") as handle:
            documents.append((path, json.load(handle)))

    fetched = {}
    for path, document in documents:
        pinned = game_version or (document.get("provenance") or {}).get("xivapi_game_version")
        if not pinned:
            print("FAILED: %s records no xivapi_game_version; pass --game-version" % path,
                  file=sys.stderr)
            return 1
        if pinned not in fetched:
            try:
                rows, urls, digest, api_version = fetch_english_rows(raw_dir, pinned)
            except (urllib.error.URLError, ValueError, OSError) as error:
                print("FAILED to fetch XIVAPI ContentFinderCondition: %s" % error,
                      file=sys.stderr)
                print("no file was written", file=sys.stderr)
                return 1
            if api_version != pinned:
                print("FAILED: asked XIVAPI for game version %s, got %s" % (pinned, api_version),
                      file=sys.stderr)
                return 1
            fetched[pinned] = (rows, {
                "fields": "ContentMemberType.MembersPerParty * ContentMemberType.PartyCount",
                "fetched_at_utc": datetime.datetime.now(datetime.timezone.utc)
                .strftime("%Y-%m-%dT%H:%M:%S.000Z"),
                "xivapi_game_version": api_version,
                "xivapi_urls": urls,
                "xivapi_sha256": digest,
            })

    updated = []
    for path, document in documents:
        pinned = game_version or document["provenance"]["xivapi_game_version"]
        rows, source = fetched[pinned]
        result = with_party_size(document, rows, source)
        updated.append((path, result))
        print("%s: %d rows" % (path, len(result["duties"])))
        for size, count in sorted(party_size_counts(result["duties"]).items(),
                                  key=lambda item: str(item[0])):
            print("  party_size %-5s %d" % (size, count))

    # Written only after every file was built, so a failure leaves all of them unchanged.
    for path, result in updated:
        write_document(path, result)
        print("wrote %s" % path)
    return 0


def party_size_counts(duties):
    """party_size -> number of rows, for the run summary."""
    counts = {}
    for row in duties:
        key = row.get("party_size")
        counts[key] = counts.get(key, 0) + 1
    return counts


def build_document(region, language, version, duties, provenance):
    return {
        "schema_version": 1,
        "region": region,
        "language": language,
        "data_version": version,
        "sample": False,
        "updated_at_utc": provenance["fetched_at_utc"],
        "source": (
            "Generated by tools/duty-data-generator/generate.py from XIVAPI v2 and the "
            "thewakingsands/ffxiv-datamining-cn CSV export (community export of the Chinese "
            "client text; no licence file upstream, credited here as the source). Display "
            "data only: it never identifies a roulette and never makes a protocol profile "
            "usable. The duty names are FINAL FANTASY XIV game text, © SQUARE ENIX CO., LTD., "
            "used for non-commercial display under the FINAL FANTASY XIV Materials Usage "
            "License with attribution."
        ),
        "provenance": provenance,
        "duties": duties,
    }


def make_provenance(urls, digests, counts, api_version):
    return {
        "fetched_at_utc": datetime.datetime.now(datetime.timezone.utc)
        .strftime("%Y-%m-%dT%H:%M:%S.000Z"),
        "xivapi_urls": urls,
        "xivapi_sha256": digests["xivapi"],
        "xivapi_game_version": api_version,
        "datamining_cn_url": DATAMINING_CN_CSV,
        "datamining_cn_sha256": digests["cn"],
        "row_counts": counts,
        "note": (
            "Raw downloads are kept outside the repository; only their SHA-256 digests are "
            "recorded here so the result stays reproducible without redistributing upstream "
            "data."
        ),
    }


def write_document(path, document):
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


# --------------------------------------------------------------------------- entry point


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=None,
                        help="data version label used in the file name; defaults to today (UTC)")
    parser.add_argument("--out-dir", default=os.path.join(REPO, "data", "duties"))
    parser.add_argument("--raw-dir", default=None,
                        help="where to keep the raw downloads; a temp directory by default")
    parser.add_argument("--game-version", default=None,
                        help="pin every XIVAPI request to this game version key (see "
                             "provenance.xivapi_game_version); XIVAPI's latest by default")
    parser.add_argument("--dry-run", action="store_true", help="fetch and report, write nothing")
    parser.add_argument("--add-party-size", nargs="+", metavar="FILE", default=None,
                        help="add party_size to existing data files in place, fetched at the "
                             "game version each file records; nothing else in them changes")
    args = parser.parse_args(argv)

    version = args.version or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    raw_dir = args.raw_dir or tempfile.mkdtemp(prefix="duty-data-")
    os.makedirs(raw_dir, exist_ok=True)

    if args.add_party_size:
        return add_party_size_to_files(args.add_party_size, raw_dir, args.game_version)

    try:
        english_rows, urls, xivapi_digest, api_version = fetch_english_rows(
            raw_dir, args.game_version)
    except (urllib.error.URLError, ValueError, OSError) as error:
        print("FAILED to fetch XIVAPI ContentFinderCondition: %s" % error, file=sys.stderr)
        print("no file was written; the existing data/duties/*.json are unchanged",
              file=sys.stderr)
        return 1

    try:
        chinese_names, cn_url, cn_digest = fetch_chinese_names(raw_dir)
    except (urllib.error.URLError, ValueError, OSError) as error:
        print("FAILED to fetch the ffxiv-datamining-cn CSV: %s" % error, file=sys.stderr)
        print("no file was written; the existing data/duties/*.json are unchanged",
              file=sys.stderr)
        return 1

    global_duties = build_rows(english_rows, chinese_names, "en")
    cn_duties = build_rows(english_rows, chinese_names, "zh-Hans")
    counts = {
        "xivapi_rows": len(english_rows),
        "datamining_cn_rows": len(chinese_names),
        "global_duties": len(global_duties),
        "cn_duties": len(cn_duties),
    }
    provenance = make_provenance(
        urls, {"xivapi": xivapi_digest, "cn": cn_digest}, counts, api_version)

    print("raw downloads:      %s" % raw_dir)
    print("xivapi rows:        %d (%d request(s))" % (len(english_rows), len(urls)))
    print("datamining-cn rows: %d (1 request)" % len(chinese_names))
    print("global duties:      %d" % len(global_duties))
    print("cn duties:          %d" % len(cn_duties))
    for size, count in sorted(party_size_counts(cn_duties).items(), key=lambda item: str(item[0])):
        print("cn party_size %-5s %d" % (size, count))
    if args.dry_run:
        print("dry run: nothing written")
        return 0

    os.makedirs(args.out_dir, exist_ok=True)
    global_path = os.path.join(args.out_dir, "global.%s.json" % version)
    cn_path = os.path.join(args.out_dir, "cn.%s.json" % version)
    write_document(global_path, build_document("GLOBAL", "en", version, global_duties, provenance))
    write_document(cn_path, build_document("CN", "zh-Hans", version, cn_duties, provenance))
    print("wrote %s" % global_path)
    print("wrote %s" % cn_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
