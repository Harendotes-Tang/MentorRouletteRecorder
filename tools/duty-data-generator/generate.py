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

Raw downloads are written to a scratch directory outside the repository (a --raw-dir inside
it is refused, and so is a default temporary directory - TMP/TEMP - inside it) and only their
SHA-256 digests are recorded, so the repository never carries a
redistributed copy of the upstream data. A dry run keeps no raw download at all.

--game-names builds data/i18n/game-names.<version>.json instead: zh-TW / en / ja names for
the ids of the bundled zh-Hans duty, job and roulette files. This script fetches the sources
(XIVAPI v2 with language=en / ja, and the CSVs of thewakingsands/ffxiv-datamining-tc and
-cn at one resolved commit each); game_names.py, which never touches the network, parses,
merges and writes them. This is the only script that fetches game reference data; the static
boundary check lets one other Python script fetch: the CI-only release mirror,
tools/release-mirror/sync_release.py.

Usage:
    python tools/duty-data-generator/generate.py [--version 2026-09-04] [--out-dir data/duties]
    python tools/duty-data-generator/generate.py --dry-run     # fetch and report, write nothing anywhere
    python tools/duty-data-generator/generate.py --add-party-size data/duties/cn.2026-09-04.json
        # add party_size to an existing file, pinned to the game version it was built from
    python tools/duty-data-generator/generate.py --game-names [--version 2026-10-15] [--dry-run]
        # zh-TW / en / ja names into data/i18n, pinned to the duty file's game version
"""

from __future__ import annotations

import argparse
import csv
import datetime
import hashlib
import http.client
import io
import json
import os
import re
import ssl
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import game_names  # noqa: E402  (offline parse / merge / write half of --game-names)

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

# --game-names: the three hosts it reads, all read-only. Every URL is built from these.
XIVAPI_SITE = "https://v2.xivapi.com"
XIVAPI_ROOT = XIVAPI_SITE + "/api"
GITHUB_API_ROOT = "https://api.github.com"
DATAMINING_RAW_ROOT = "https://raw.githubusercontent.com"
FETCH_ATTEMPTS = 3

USER_AGENT ="MentorRecorder-duty-data-generator/1.0 (+local, no telemetry)"

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


def keep_raw(raw_dir: str | None, name: str, body: bytes) -> None:
    """Save one raw download under raw_dir; with no raw_dir (a dry run) keep nothing."""
    if raw_dir is None:
        return
    with open(os.path.join(raw_dir, name), "wb") as handle:
        handle.write(body)


def is_inside_repository(path: str) -> bool:
    """True when path is the repository or anything below it, after resolving links and case."""
    root = os.path.normcase(os.path.realpath(REPO))
    target = os.path.normcase(os.path.realpath(path))
    try:
        return os.path.commonpath([root, target]) == root
    except ValueError:
        # Different drives: never inside.
        return False


def fetch_english_rows(raw_dir: str | None, game_version: str | None = None):
    """Page through ContentFinderCondition. Returns (rows, urls, digest, api_version).

    game_version pins every page to one XIVAPI game version (the key recorded as
    provenance.xivapi_game_version), so an existing data version can be rebuilt from the
    same game data it was first built from. raw_dir None keeps no raw download.
    """
    rows = {}
    urls = []
    pages = []
    after = 0
    api_version = None
    first_version = None
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
        page_version = document.get("version")
        # Every page must come from one game version (N3-4): unpinned, the pages of a run that
        # straddles a patch would mix two versions while provenance named only the last one. A
        # page that names no version cannot be shown to match the others, so it fails too.
        if not isinstance(page_version, str) or not page_version:
            raise ValueError(
                "XIVAPI ContentFinderCondition 第 %d 页没有给出游戏版本，无法确认各页来自同一版本。"
                % len(pages))
        if game_version and page_version != game_version:
            raise ValueError(
                "请求的 XIVAPI 游戏版本是 %s，ContentFinderCondition 第 %d 页却来自 %s。"
                % (game_version, len(pages), page_version))
        if first_version is None:
            first_version = page_version
        elif page_version != first_version:
            raise ValueError(
                "XIVAPI ContentFinderCondition 的各页来自不同的游戏版本（第 1 页 %s，第 %d 页 %s），"
                "多半是生成时恰逢数据更新；请稍后重新生成，或用 --game-version 固定版本。"
                % (first_version, len(pages), page_version))
        api_version = page_version
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
    keep_raw(raw_dir, "xivapi_contentfindercondition.json", blob)
    return rows, urls, hashlib.sha256(blob).hexdigest(), api_version


def fetch_chinese_names(raw_dir: str | None):
    """Returns (names_by_row_id, url, digest). raw_dir None keeps no raw download."""
    body = http_get(DATAMINING_CN_CSV)
    keep_raw(raw_dir, "ContentFinderCondition.cn.csv", body)
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


def add_party_size_to_files(paths, raw_dir, game_version=None, *, dry_run=False):
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

    if dry_run:
        print("dry run: nothing written")
        return 0

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


# --------------------------------------------------------------------------- game names: fetching


def utc_timestamp():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")


def http_get_retrying(url):
    """http_get with a short retry for dropped connections; a 4xx answer is not retried."""
    for attempt in range(1, FETCH_ATTEMPTS + 1):
        try:
            return http_get(url)
        except urllib.error.HTTPError as error:
            if error.code < 500 or attempt == FETCH_ATTEMPTS:
                raise
        except (urllib.error.URLError, OSError, http.client.HTTPException):
            if attempt == FETCH_ATTEMPTS:
                raise
        time.sleep(2 * attempt)
    raise AssertionError("unreachable")


def fetch_recorded(url, requests, raw_dir, raw_name):
    """Fetch url, record {url, sha256}, keep the raw body under raw_dir (None keeps nothing)."""
    body = http_get_retrying(url)
    requests.append({"url": url, "sha256": hashlib.sha256(body).hexdigest()})
    keep_raw(raw_dir, raw_name, body)
    return body


def fetch_name_sheet(sheet, fields, language, game_version, requests, raw_dir):
    """Every row of one sheet in one language, {row_id: fields}, all pages at one version."""
    rows = {}
    after = None
    for page in range(1, XIVAPI_MAX_PAGES + 1):
        params = {"fields": fields, "language": language, "limit": XIVAPI_PAGE_SIZE}
        if after is not None:
            params["after"] = after
        if game_version:
            params["version"] = game_version
        url = "%s/sheet/%s?%s" % (XIVAPI_ROOT, sheet, urllib.parse.urlencode(params))
        body = fetch_recorded(url, requests, raw_dir,
                              "xivapi.%s.%s.%d.json" % (sheet, language, page))
        document = json.loads(body.decode("utf-8"))
        if game_version and document.get("version") != game_version:
            raise ValueError("asked XIVAPI for game version %s, %s/%s answered %s"
                             % (game_version, sheet, language, document.get("version")))
        page_rows = document.get("rows") or []
        for row in page_rows:
            rows[int(row["row_id"])] = row.get("fields") or {}
        if len(page_rows) < XIVAPI_PAGE_SIZE:
            return rows
        after = int(page_rows[-1]["row_id"])
    raise ValueError("%s/%s still returned a full page after %d pages; raise XIVAPI_MAX_PAGES"
                     % (sheet, language, XIVAPI_MAX_PAGES))


def resolve_xivapi_version(game_version, requests, raw_dir):
    """(version key, patch names such as ["7.55x2"]) from XIVAPI's version list.

    None asks for the key XIVAPI currently names "latest", so every later request is pinned
    to one version even when the caller did not choose one.
    """
    body = fetch_recorded(XIVAPI_ROOT + "/version", requests, raw_dir, "xivapi.version.json")
    for entry in json.loads(body.decode("utf-8")).get("versions") or []:
        names = list(entry.get("names") or [])
        if entry.get("key") == game_version or (game_version is None and "latest" in names):
            return entry.get("key"), names
    if game_version is None:
        raise ValueError("XIVAPI's version list names no latest version")
    return game_version, []


def fetch_xivapi_names(game_version, raw_dir):
    """({sheet: {language: rows}} for game_names.XIVAPI_FIELDS, provenance block)."""
    requests = []
    game_version, names = resolve_xivapi_version(game_version, requests, raw_dir)
    sheets = {sheet: {language: fetch_name_sheet(sheet, fields, language, game_version,
                                                 requests, raw_dir)
                      for language in game_names.XIVAPI_LANGUAGES}
              for sheet, fields in game_names.XIVAPI_FIELDS.items()}
    provenance = {
        "url": XIVAPI_SITE,
        "languages": list(game_names.XIVAPI_LANGUAGES),
        "game_version": game_version,
        "game_version_names": names,
        "fields": dict(game_names.XIVAPI_FIELDS),
        "requests": requests,
    }
    return sheets, provenance


def resolve_commit(repository, ref, requests, raw_dir):
    """(commit sha, commit message) of ref, so every CSV comes from one recorded commit.

    The list endpoint is used because the single-commit one also returns every changed file's
    patch, which for a datamining commit runs to megabytes.
    """
    url = "%s/repos/%s/commits?%s" % (GITHUB_API_ROOT, repository,
                                      urllib.parse.urlencode({"sha": ref, "per_page": 1}))
    body = fetch_recorded(url, requests, raw_dir, "%s.commit.json" % repository.split("/")[1])
    listing = json.loads(body.decode("utf-8"))
    document = listing[0] if isinstance(listing, list) and listing else {}
    sha = document.get("sha") or ""
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("GitHub returned no commit for %s@%s" % (repository, ref))
    return sha, ((document.get("commit") or {}).get("message") or "").strip()


def fetch_datamining_sheets(repository, ref, sheets, raw_dir):
    """({sheet: CSV text}, fetch record) for each sheet, all from one resolved commit."""
    requests = []
    sha, message = resolve_commit(repository, ref, requests, raw_dir)
    short = repository.split("/")[1]
    texts = {}
    for sheet in sheets:
        url = "%s/%s/%s/%s.csv" % (DATAMINING_RAW_ROOT, repository, sha, sheet)
        body = fetch_recorded(url, requests, raw_dir, "%s.%s.csv" % (short, sheet))
        texts[sheet] = body.decode("utf-8-sig")
    return texts, {"repository": "https://github.com/" + repository, "ref": ref,
                   "commit": sha, "commit_message": message, "requests": requests}


# --------------------------------------------------------------------------- game names: run


def choose_game_version(requested, pinned):
    """--game-version, else the duty file's version; 'latest' resolves later (None)."""
    if requested == "latest":
        return None
    if requested:
        return requested
    if not pinned:
        raise game_names.SourceError("the duty file records no xivapi_game_version; pass "
                                     "--game-version <key> or --game-version latest")
    return pinned


def gather_game_names(args, raw_dir, converter):
    """(maps, provenance, base paths): every fetch, then the offline merge. Raises on failure."""
    base, data_version, pinned = game_names.load_base(args.duties, args.jobs, args.roulettes)
    game_version = choose_game_version(args.game_version, pinned)
    xivapi, xivapi_provenance = fetch_xivapi_names(game_version, raw_dir)
    tc_texts, tc_fetched = fetch_datamining_sheets(
        game_names.TC_REPOSITORY, args.tc_ref, game_names.TC_COLUMNS, raw_dir)
    cn_texts, cn_fetched = fetch_datamining_sheets(
        game_names.CN_REPOSITORY, args.cn_ref, game_names.CN_COLUMNS, raw_dir)
    maps = game_names.build_maps(
        base, xivapi, game_names.parse_sheets(tc_texts, game_names.TC_COLUMNS),
        game_names.parse_sheets(cn_texts, game_names.CN_COLUMNS), converter,
        CATEGORY_BY_CONTENT_TYPE)
    provenance = game_names.make_provenance(utc_timestamp(), xivapi_provenance, tc_fetched,
                                            cn_fetched, converter.info)
    base_paths = {
        "duties": game_names.repository_path(args.duties),
        "duties_data_version": data_version,
        "jobs": game_names.repository_path(args.jobs),
        "roulettes": game_names.repository_path(args.roulettes),
    }
    return maps, provenance, base_paths


def run_game_names(args, raw_dir, version):
    """--game-names: returns a process exit code; writes nothing unless every step succeeded."""
    converter = game_names.Converter()
    try:
        maps, provenance, base_paths = gather_game_names(args, raw_dir, converter)
    except (urllib.error.URLError, ValueError, OSError, http.client.HTTPException, KeyError,
            TypeError, AttributeError, csv.Error) as error:
        # Wrong JSON shapes (KeyError / TypeError / AttributeError) end here too: one line
        # instead of a traceback, and nothing has been written yet.
        print("FAILED: %s: %s" % (type(error).__name__, error), file=sys.stderr)
        print("no file was written; the existing data/i18n files are unchanged", file=sys.stderr)
        return 1
    game_names.report(maps, provenance, raw_dir)
    if args.dry_run:
        print("dry run: nothing written")
        return 0
    out_dir = args.out_dir or os.path.join(REPO, "data", "i18n")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "game-names.%s.json" % version)
    try:
        game_names.write_document(path, game_names.build_document(version, base_paths, maps,
                                                                  provenance))
    except (OSError, ValueError) as error:
        print("FAILED to write %s: %s" % (path, error), file=sys.stderr)
        return 1
    print("wrote %s" % path)
    return 0


# --------------------------------------------------------------------------- entry point


def add_game_names_arguments(parser):
    parser.add_argument("--game-names", action="store_true",
                        help="write data/i18n/game-names.<version>.json (zh-TW / en / ja names) "
                             "instead of duty files; --game-version defaults to the one the "
                             "duty file records, 'latest' asks for XIVAPI's newest")
    parser.add_argument("--duties", default=game_names.latest_duty_file(),
                        help="--game-names: zh-Hans duty file whose ids to cover; newest by default")
    parser.add_argument("--jobs", default=os.path.join(REPO, "data", "jobs", "jobs.json"),
                        help="--game-names: zh-Hans job file")
    parser.add_argument("--roulettes", default=os.path.join(REPO, "data", "roulettes", "cn.json"),
                        help="--game-names: zh-Hans roulette file")
    parser.add_argument("--tc-ref", default="main",
                        help="--game-names: ffxiv-datamining-tc branch or commit")
    parser.add_argument("--cn-ref", default="master",
                        help="--game-names: ffxiv-datamining-cn branch or commit")


def check_game_names_arguments(parser, args):
    if args.add_party_size:
        parser.error("--game-names and --add-party-size are separate runs")
    if args.version is not None and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.version):
        parser.error("--version must be a UTC date such as 2026-10-15")
    if not args.duties:
        parser.error("no data/duties/cn.<date>.json found; pass --duties")


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=None,
                        help="data version label used in the file name; defaults to today (UTC)")
    parser.add_argument("--out-dir", default=None,
                        help="output directory; data/duties by default, data/i18n with "
                             "--game-names")
    parser.add_argument("--raw-dir", default=None,
                        help="where to keep the raw downloads, outside the repository; a temp "
                             "directory by default; a dry run keeps none")
    parser.add_argument("--game-version", default=None,
                        help="pin every XIVAPI request to this game version key (see "
                             "provenance.xivapi_game_version); XIVAPI's latest by default")
    parser.add_argument("--dry-run", action="store_true", help="fetch and report, write nothing")
    parser.add_argument("--add-party-size", nargs="+", metavar="FILE", default=None,
                        help="add party_size to existing data files in place, fetched at the "
                             "game version each file records; nothing else in them changes")
    add_game_names_arguments(parser)
    args = parser.parse_args(argv)
    if args.game_names:
        check_game_names_arguments(parser, args)
    if args.raw_dir and is_inside_repository(args.raw_dir):
        parser.error("--raw-dir %s is inside the repository; raw downloads are upstream data "
                     "and must stay outside it" % args.raw_dir)

    version = args.version or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    if args.dry_run:
        # Write nothing anywhere: the downloads are hashed in memory and dropped, and no
        # scratch directory is created that would outlive the run.
        raw_dir = None
    elif args.raw_dir:
        raw_dir = args.raw_dir
        os.makedirs(raw_dir, exist_ok=True)
    else:
        # The default raw directory is made under the temporary directory, which TMP/TEMP
        # choose: refuse it there too, before anything is created or fetched.
        scratch = tempfile.gettempdir()
        if is_inside_repository(scratch):
            parser.error("the temporary directory %s is inside the repository; raw downloads "
                         "are upstream data and must stay outside it (pass --raw-dir)" % scratch)
        raw_dir = tempfile.mkdtemp(prefix="duty-data-", dir=scratch)

    if args.add_party_size:
        return add_party_size_to_files(args.add_party_size, raw_dir, args.game_version,
                                       dry_run=args.dry_run)

    if args.game_names:
        return run_game_names(args, raw_dir, version)

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

    print("raw downloads:      %s" % (raw_dir or "not kept (dry run)"))
    print("xivapi rows:        %d (%d request(s))" % (len(english_rows), len(urls)))
    print("datamining-cn rows: %d (1 request)" % len(chinese_names))
    print("global duties:      %d" % len(global_duties))
    print("cn duties:          %d" % len(cn_duties))
    for size, count in sorted(party_size_counts(cn_duties).items(), key=lambda item: str(item[0])):
        print("cn party_size %-5s %d" % (size, count))
    if args.dry_run:
        print("dry run: nothing written")
        return 0

    out_dir = args.out_dir or os.path.join(REPO, "data", "duties")
    os.makedirs(out_dir, exist_ok=True)
    global_path = os.path.join(out_dir, "global.%s.json" % version)
    cn_path = os.path.join(out_dir, "cn.%s.json" % version)
    write_document(global_path, build_document("GLOBAL", "en", version, global_duties, provenance))
    write_document(cn_path, build_document("CN", "zh-Hans", version, cn_duties, provenance))
    print("wrote %s" % global_path)
    print("wrote %s" % cn_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
