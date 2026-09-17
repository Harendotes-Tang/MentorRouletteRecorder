#!/usr/bin/env python3
"""Share codes (``MRC1.``): decode, encode, canonical form, identity and payload rules.

A port of ``src/Collector/Protocol/Sharing/ShareCode.cs`` and ``ShareCodeRejection.cs`` for the
public calibration repository's GitHub Action. The client decodes every downloaded code again, so
this module is a gate in front of it, never a replacement: it must not accept a code the client
refuses, and it names the same rejection token for every vector in
``tests/Fixtures/shared-calibration/vectors.json`` (test_sharecode.py).

Format: ``MRC1.`` + base64url (RFC 4648 section 5, no padding) of a raw DEFLATE stream (RFC 1951)
holding the payload as canonical JSON. ``code_sha256`` is the SHA-256 of that canonical text, not of
the code: two compressors write two different codes for one payload and both are the same code.

Where the C# decoder's behaviour comes from the .NET runtime rather than from ShareCode.cs, it is
reproduced on purpose (probed against the .NET runtime on 2026-09-16):

* the whitespace trimmed around a code is exactly what ``string.Trim()`` removes
  (``char.IsWhiteSpace``), which is narrower than Python's ``str.strip()``;
* the length limit counts UTF-16 code units;
* ``DeflateStream`` does not fail on a truncated stream or on bytes after the final block: it
  yields what it inflated, and the JSON step then refuses whatever that is;
* ``JsonDocument`` refuses a byte order mark, ``NaN``/``Infinity``, leading zeros and nesting
  deeper than 8;
* ``TryGetInt64`` accepts only integer literals in the signed 64-bit range (``1.0`` and ``1e2``
  are not integers, ``-0`` is 0).

Text that is not valid UTF-8, or a key or string anywhere (a repeated key's dropped value included)
that holds a lone surrogate escape, is text ``JsonProperty.Name`` and ``JsonElement.GetString`` throw
on. The client refuses it as not JSON before reading anything (``WellFormedJson.TryParse``), and so
does ``strict_json``: ``E_SHARE_CODE_JSON`` here, ``NOT_JSON`` for ``index.read_index``.

Standard library only. Pure: no IO, no clock, no network.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import json
import re
import zlib
from dataclasses import dataclass
from typing import Any, Mapping

PREFIX = "MRC1."
PAYLOAD_VERSION = 1
MAX_CODE_LENGTH = 4096
MAX_INFLATED_BYTES = 16 * 1024
MAX_SELECTOR_VALUES = 32
MAX_JSON_DEPTH = 8

REGIONS = ("CN", "GLOBAL")
MATCH_SOURCES = ("REPLY_STATE", "ANNOUNCEMENT", "MARKER_OFFSET", "QUEUE_REQUEST")

# The pop keys each match source learns (CalibratedShape.InputsFor). All of them are required and
# every other pop key is forbidden, so one profile has exactly one code identity.
POP_INPUTS: Mapping[str, frozenset] = {
    "REPLY_STATE": frozenset({"opcode", "selector_values"}),
    "ANNOUNCEMENT": frozenset({"opcode", "length"}),
    "MARKER_OFFSET": frozenset({"opcode", "length", "roulette_offset"}),
    "QUEUE_REQUEST": frozenset({"opcode"}),
}

_POP_KEY_ORDER = ("opcode", "length", "roulette_offset", "selector_values")
_POP_KEYS = frozenset(_POP_KEY_ORDER)
_TOP_KEYS = frozenset({
    "v", "region", "game_build", "template_profile_id", "template_sha256", "match_source", "pop",
    "zone_opcode", "territory_opcode", "job_opcode",
})
_REQUIRED_KEYS = (
    "v", "region", "game_build", "template_profile_id", "template_sha256", "match_source", "pop", "zone_opcode",
)

E_EMPTY = "E_SHARE_CODE_EMPTY"
E_TOO_LONG = "E_SHARE_CODE_TOO_LONG"
E_NOT_A_CODE = "E_SHARE_CODE_NOT_A_CODE"
E_VERSION = "E_SHARE_CODE_VERSION"
E_CHARACTERS = "E_SHARE_CODE_CHARACTERS"
E_BASE64 = "E_SHARE_CODE_BASE64"
E_COMPRESSION = "E_SHARE_CODE_COMPRESSION"
E_INFLATED_TOO_LONG = "E_SHARE_CODE_INFLATED_TOO_LONG"
E_JSON = "E_SHARE_CODE_JSON"
E_DUPLICATE_KEY = "E_SHARE_CODE_DUPLICATE_KEY"
E_PAYLOAD_VERSION = "E_SHARE_CODE_PAYLOAD_VERSION"
E_UNKNOWN_KEY = "E_SHARE_CODE_UNKNOWN_KEY"
E_MISSING_KEY = "E_SHARE_CODE_MISSING_KEY"
E_VALUE = "E_SHARE_CODE_VALUE"
E_POP_KEY_FORBIDDEN = "E_SHARE_CODE_POP_KEY_FORBIDDEN"
E_POP_KEY_MISSING = "E_SHARE_CODE_POP_KEY_MISSING"
E_TERRITORY_REQUIRED = "E_SHARE_CODE_TERRITORY_REQUIRED"
E_NOT_CANONICAL = "E_SHARE_CODE_NOT_CANONICAL"

_CONTENT_WRONG = "校准码的内容有误，不是本软件生成的有效校准码。"

# ShareCodeRejection.MessageFor, word for word: what a player reads (Chinese, never an opcode).
MESSAGES: Mapping[str, str] = {
    E_EMPTY: "没有粘贴任何内容。",
    E_TOO_LONG: "内容太长，不是本软件生成的校准码。",
    E_NOT_A_CODE: "这不是本软件的校准码：校准码以 MRC1. 开头。",
    E_VERSION: "这份校准码来自更新版本的软件，当前版本读不了；请先更新本软件。",
    E_CHARACTERS: "校准码里有不该出现的字符，可能复制时多了或少了内容；请重新完整复制一次。",
    E_BASE64: "校准码不完整，可能复制时少了一截；请重新完整复制一次。",
    E_COMPRESSION: "校准码已经损坏，解不开；请重新完整复制一次。",
    E_INFLATED_TOO_LONG: "校准码解开后内容过大，不是有效的校准码。",
    E_JSON: _CONTENT_WRONG,
    E_DUPLICATE_KEY: _CONTENT_WRONG,
    E_UNKNOWN_KEY: _CONTENT_WRONG,
    E_MISSING_KEY: _CONTENT_WRONG,
    E_VALUE: _CONTENT_WRONG,
    E_POP_KEY_FORBIDDEN: _CONTENT_WRONG,
    E_POP_KEY_MISSING: _CONTENT_WRONG,
    E_NOT_CANONICAL: _CONTENT_WRONG,
    E_PAYLOAD_VERSION: "这份校准码的内容格式来自更新版本的软件，当前版本读不了；请先更新本软件。",
    E_TERRITORY_REQUIRED: "这份校准码缺少「这次进的是哪个副本」那条报文，按它记录只会记成未知副本，所以不能使用。",
}
_FALLBACK_MESSAGE = "校准码无法使用。"

_OTHER_VERSION = re.compile(r"MRC[0-9]+\.")
_BASE64URL = re.compile(r"[A-Za-z0-9_-]*")
_BUILD = re.compile(r"[A-Za-z0-9._-]{1,128}")
_PROFILE_ID = re.compile(r"[a-z0-9][a-z0-9.-]{0,63}")
_SHA256 = re.compile(r"[0-9a-f]{64}")

_INT64_MIN = -(2 ** 63)
_INT64_MAX = 2 ** 63 - 1
_OPCODE_MAX = 0xFFFF

# Every character System.Char.IsWhiteSpace is true for: Zs, Zl, Zp, U+0009..U+000D and U+0085.
# U+001C..U+001F (which str.strip() removes), U+180E, U+200B and U+FEFF are deliberately absent.
_DOTNET_WHITESPACE = frozenset(
    "\t\n\x0b\x0c\r \x85\xa0 "
    + "".join(chr(point) for point in range(0x2000, 0x200B))
    + "    　"
)


@dataclass(frozen=True)
class Rejection:
    """Why a code was refused: a stable token, and a short detail naming a key or a step."""

    code: str
    detail: str

    @property
    def message(self) -> str:
        """The player's explanation (Chinese)."""
        return MESSAGES.get(self.code, _FALLBACK_MESSAGE)


@dataclass(frozen=True)
class DecodeResult:
    """What decoding produced. ``payload`` is a fresh dict owned by the caller."""

    payload: dict | None
    code_sha256: str | None
    rejection: Rejection | None

    @property
    def valid(self) -> bool:
        return self.payload is not None


class JsonObject(dict):
    """A parsed JSON object that remembers whether its source repeated a key."""

    duplicate_key: str | None = None


# --------------------------------------------------------------------------- canonical JSON


def canonical(node: Any) -> str:
    """Canonical JSON: sorted keys, no whitespace, integer numbers only.

    The same grammar as ``tools/protocol-profile-validator/validate.py`` ``canonical()`` and
    ``CanonicalJson.cs``. It is copied rather than imported so that the public repository's tools
    stand alone; test_sharecode.py fails the moment the two copies disagree.
    """
    if node is None:
        return "null"
    if node is True:
        return "true"
    if node is False:
        return "false"
    if isinstance(node, int):
        return str(node)
    if isinstance(node, float):
        raise ValueError("a protocol profile may not contain a non-integer number")
    if isinstance(node, str):
        return canonical_string(node)
    if isinstance(node, (list, tuple)):
        return "[" + ",".join(canonical(item) for item in node) + "]"
    if isinstance(node, Mapping):
        return "{" + ",".join(
            canonical_string(key) + ":" + canonical(node[key]) for key in sorted(node.keys())
        ) + "}"
    raise ValueError("unsupported JSON node: " + repr(type(node)))


_SHORT_ESCAPES = {
    "\b": "\\b",
    "\f": "\\f",
    "\n": "\\n",
    "\r": "\\r",
    "\t": "\\t",
    '"': '\\"',
    "\\": "\\\\",
}


def canonical_string(text: str) -> str:
    out = ['"']
    for ch in text:
        if ch in _SHORT_ESCAPES:
            out.append(_SHORT_ESCAPES[ch])
        elif ord(ch) < 0x20:
            out.append("\\u%04x" % ord(ch))
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------- strict JSON


def strict_json(data: bytes, max_depth: int = MAX_JSON_DEPTH) -> tuple[bool, Any]:
    """Parses UTF-8 JSON the way ``WellFormedJson.TryParse`` does with this project's options.

    Returns ``(True, value)``, or ``(False, None)`` for anything the client refuses as not JSON:
    invalid UTF-8, a byte order mark, ``NaN``/``Infinity``, trailing content, nesting deeper than
    ``max_depth``, or a key or string anywhere (a repeated key's dropped value included) holding a lone
    surrogate escape. Objects are ``JsonObject`` instances carrying their first repeated key.
    """
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        return False, None

    ill_formed = False

    def check(value: Any) -> None:
        # Strings and lists of strings; an object was checked by its own ``pairs`` call.
        nonlocal ill_formed
        pending = [value]
        while pending and not ill_formed:
            item = pending.pop()
            if isinstance(item, str):
                ill_formed = not _is_well_formed(item)
            elif isinstance(item, list):
                pending.extend(item)

    def pairs(items: list) -> JsonObject:
        obj = JsonObject()
        for key, value in items:
            check(key)
            check(value)
            if key in obj and obj.duplicate_key is None:
                obj.duplicate_key = key
            elif key not in obj:
                obj[key] = value
        return obj

    def refuse_constant(name: str) -> Any:
        raise ValueError("non-standard JSON constant " + name)

    try:
        value = json.loads(text, object_pairs_hook=pairs, parse_constant=refuse_constant)
    except (ValueError, RecursionError):
        return False, None
    check(value)
    if ill_formed or _deeper_than(value, max_depth):
        return False, None
    return True, value


def _is_well_formed(text: str) -> bool:
    """False for a string holding a lone surrogate, which only a ``\\uXXXX`` escape can put there.

    .NET's ``JsonProperty.Name`` and ``JsonElement.GetString`` throw on such a key or string, so
    ``WellFormedJson.TryParse`` refuses the whole text, and so does ``strict_json``.
    """
    try:
        text.encode("utf-8")
    except UnicodeEncodeError:
        return False
    return True


def _deeper_than(value: Any, limit: int) -> bool:
    if isinstance(value, dict):
        children = value.values()
    elif isinstance(value, list):
        children = value
    else:
        return False
    if limit <= 0:
        return True
    return any(_deeper_than(child, limit - 1) for child in children)


def first_duplicate_key(value: Any) -> str | None:
    """The first repeated key anywhere in a parsed value, depth first (ShareCode.DuplicateKey)."""
    if isinstance(value, dict):
        duplicate = getattr(value, "duplicate_key", None)
        if duplicate is not None:
            return duplicate
        for key, child in value.items():
            nested = first_duplicate_key(child)
            if nested is not None:
                return key + "." + nested
        return None
    if isinstance(value, list):
        for child in value:
            nested = first_duplicate_key(child)
            if nested is not None:
                return nested
    return None


def is_int64(value: Any) -> bool:
    """True for what ``JsonElement.TryGetInt64`` accepts: an integer literal (never a bool) in range."""
    return type(value) is int and _INT64_MIN <= value <= _INT64_MAX


# --------------------------------------------------------------------------- .NET string rules


def dotnet_trim(text: str) -> str:
    """``string.Trim()``: removes exactly the characters ``char.IsWhiteSpace`` is true for."""
    start, end = 0, len(text)
    while start < end and text[start] in _DOTNET_WHITESPACE:
        start += 1
    while end > start and text[end - 1] in _DOTNET_WHITESPACE:
        end -= 1
    return text[start:end]


def utf16_length(text: str) -> int:
    """``string.Length``: UTF-16 code units, so a character outside the BMP counts twice."""
    return sum(2 if ord(ch) > 0xFFFF else 1 for ch in text)


# --------------------------------------------------------------------------- decode


def decode(text: str | None) -> DecodeResult:
    """Decodes a code, never raising for anything a player could paste (ShareCode.Decode)."""
    trimmed = dotnet_trim(text) if isinstance(text, str) else ""
    if not trimmed:
        return _refuse(E_EMPTY, "no text")
    if utf16_length(trimmed) > MAX_CODE_LENGTH:
        return _refuse(E_TOO_LONG, "code text over %d characters" % MAX_CODE_LENGTH)
    if not trimmed.startswith(PREFIX):
        if _OTHER_VERSION.match(trimmed):
            return _refuse(E_VERSION, "prefix is not " + PREFIX)
        return _refuse(E_NOT_A_CODE, "missing prefix")

    body = trimmed[len(PREFIX):]
    if not _BASE64URL.fullmatch(body):
        return _refuse(E_CHARACTERS, "body is not base64url")
    raw = _from_base64url(body)
    if raw is None:
        return _refuse(E_BASE64, "body has an impossible base64 length")

    inflated, inflate_rejection = _inflate(raw)
    if inflate_rejection is not None:
        return DecodeResult(None, None, inflate_rejection)

    parsed, root = strict_json(inflated)
    if not parsed:
        return _refuse(E_JSON, "not JSON")
    if not isinstance(root, dict):
        return _refuse(E_JSON, "root is not an object")
    duplicate = first_duplicate_key(root)
    if duplicate is not None:
        return _refuse(E_DUPLICATE_KEY, duplicate)

    payload, rejection = _read(root)
    if rejection is not None:
        return DecodeResult(None, None, rejection)
    canonical_bytes = canonical(payload).encode("utf-8")
    if inflated != canonical_bytes:
        return _refuse(E_NOT_CANONICAL, "payload text is not canonical JSON")
    return DecodeResult(payload, sha256_hex(canonical_bytes), None)


def _refuse(code: str, detail: str) -> DecodeResult:
    return DecodeResult(None, None, Rejection(code, detail))


def _from_base64url(body: str) -> bytes | None:
    if len(body) == 0 or len(body) % 4 == 1:
        return None
    padded = body.replace("-", "+").replace("_", "/") + "=" * ((4 - len(body) % 4) % 4)
    try:
        return base64.b64decode(padded, validate=True)
    except (binascii.Error, ValueError):
        return None


def _inflate(raw: bytes) -> tuple[bytes, Rejection | None]:
    inflater = zlib.decompressobj(-15)
    try:
        inflated = inflater.decompress(raw, MAX_INFLATED_BYTES + 1)
    except zlib.error:
        return b"", Rejection(E_COMPRESSION, "not a DEFLATE stream")
    if len(inflated) > MAX_INFLATED_BYTES:
        return b"", Rejection(E_INFLATED_TOO_LONG, "inflates past %d bytes" % MAX_INFLATED_BYTES)
    return inflated, None


def _text(parent: Mapping, key: str) -> str | None:
    value = parent.get(key)
    return value if isinstance(value, str) else None


def _read(root: Mapping) -> tuple[dict | None, Rejection | None]:
    """ShareCode.Read: validates a payload object, the version first, and returns it normalised."""
    if "v" not in root:
        return None, Rejection(E_MISSING_KEY, "v")
    if not is_int64(root["v"]):
        return None, Rejection(E_VALUE, "v")
    if root["v"] != PAYLOAD_VERSION:
        return None, Rejection(E_PAYLOAD_VERSION, "v")
    for key in root:
        if key not in _TOP_KEYS:
            return None, Rejection(E_UNKNOWN_KEY, key)
    for key in _REQUIRED_KEYS:
        if key not in root:
            return None, Rejection(E_MISSING_KEY, key)

    region = _text(root, "region")
    if region not in REGIONS:
        return None, Rejection(E_VALUE, "region")
    build = _text(root, "game_build")
    template_id = _text(root, "template_profile_id")
    template_sha = _text(root, "template_sha256")
    if build is None or not _BUILD.fullmatch(build):
        return None, Rejection(E_VALUE, "game_build")
    if template_id is None or not _PROFILE_ID.fullmatch(template_id):
        return None, Rejection(E_VALUE, "template_profile_id")
    if template_sha is None or not _SHA256.fullmatch(template_sha):
        return None, Rejection(E_VALUE, "template_sha256")
    source = _text(root, "match_source")
    if source not in MATCH_SOURCES:
        return None, Rejection(E_VALUE, "match_source")

    zone, rejection = _opcode(root, "zone_opcode", required=True)
    if rejection is not None:
        return None, rejection
    territory, rejection = _opcode(root, "territory_opcode", required=False)
    if rejection is not None:
        return None, rejection
    job, rejection = _opcode(root, "job_opcode", required=False)
    if rejection is not None:
        return None, rejection
    pop, rejection = _read_pop(root["pop"], source)
    if rejection is not None:
        return None, rejection
    if source == "QUEUE_REQUEST" and territory is None:
        return None, Rejection(E_TERRITORY_REQUIRED, "territory_opcode")

    payload = {
        "v": PAYLOAD_VERSION,
        "region": region,
        "game_build": build,
        "template_profile_id": template_id,
        "template_sha256": template_sha,
        "match_source": source,
        "pop": pop,
        "zone_opcode": zone,
    }
    if territory is not None:
        payload["territory_opcode"] = territory
    if job is not None:
        payload["job_opcode"] = job
    return payload, None


def _read_pop(element: Any, source: str) -> tuple[dict | None, Rejection | None]:
    if not isinstance(element, dict):
        return None, Rejection(E_VALUE, "pop")
    for key in element:
        if key not in _POP_KEYS:
            return None, Rejection(E_UNKNOWN_KEY, "pop." + key)
    inputs = POP_INPUTS[source]
    for key in _POP_KEY_ORDER:
        present = key in element
        if present and key not in inputs:
            return None, Rejection(E_POP_KEY_FORBIDDEN, "pop." + key)
        if not present and key in inputs:
            return None, Rejection(E_POP_KEY_MISSING, "pop." + key)

    opcode, rejection = _opcode(element, "opcode", required=True)
    if rejection is not None:
        return None, rejection
    pop: dict = {"opcode": opcode}

    length = None
    if "length" in element:
        value = element["length"]
        if not is_int64(value) or not 1 <= value <= 0xFFFF:
            return None, Rejection(E_VALUE, "pop.length")
        length = value
        pop["length"] = value

    if "roulette_offset" in element:
        value = element["roulette_offset"]
        if not is_int64(value) or value < 0 or length is None or value >= length:
            return None, Rejection(E_VALUE, "pop.roulette_offset")
        pop["roulette_offset"] = value

    if "selector_values" in element:
        values = element["selector_values"]
        if not isinstance(values, list) or len(values) > MAX_SELECTOR_VALUES:
            return None, Rejection(E_VALUE, "pop.selector_values")
        if not all(is_int64(value) for value in values):
            return None, Rejection(E_VALUE, "pop.selector_values")
        pop["selector_values"] = list(values)
    return pop, None


def _opcode(parent: Mapping, key: str, required: bool) -> tuple[int | None, Rejection | None]:
    if key not in parent:
        return None, (Rejection(E_MISSING_KEY, key) if required else None)
    value = parent[key]
    if not is_int64(value) or not 0 <= value <= _OPCODE_MAX:
        return None, Rejection(E_VALUE, key)
    return value, None


# --------------------------------------------------------------------------- encode


def check(payload: Any) -> Rejection | None:
    """Why this payload cannot be a code, or None: exactly the checks a decoder applies (ShareCode.Check)."""
    normalized, rejection = _normalize(payload)
    return rejection if normalized is None else None


def _normalize(payload: Any) -> tuple[dict | None, Rejection | None]:
    if not isinstance(payload, Mapping):
        return None, Rejection(E_JSON, "root is not an object")
    try:
        text = canonical(payload)
    except (ValueError, TypeError):
        return None, Rejection(E_VALUE, "payload")
    # A lone surrogate cannot be written as UTF-8. ShareCode.Check refuses such a payload for the value it
    # sits in instead of throwing, so it is substituted here and refused the same way.
    parsed, root = strict_json(text.encode("utf-8", "replace"))
    if not parsed or not isinstance(root, dict):
        return None, Rejection(E_JSON, "not JSON")
    return _read(root)


def code_sha256(payload: Any) -> str:
    """``code_sha256`` of a valid payload; raises ValueError for one a decoder would refuse."""
    normalized, rejection = _normalize(payload)
    if normalized is None:
        raise ValueError("the payload cannot be a share code (%s %s)" % (rejection.code, rejection.detail))
    return sha256_hex(canonical(normalized).encode("utf-8"))


def encode(payload: Any) -> str:
    """Encodes a payload; raises ValueError when a decoder would refuse it (ShareCode.Encode)."""
    normalized, rejection = _normalize(payload)
    if normalized is None:
        raise ValueError("the payload cannot be a share code (%s %s)" % (rejection.code, rejection.detail))
    data = canonical(normalized).encode("utf-8")
    compressor = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
    raw = compressor.compress(data) + compressor.flush()
    code = PREFIX + base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")
    if len(code) > MAX_CODE_LENGTH:
        raise ValueError("the payload does not fit the share code length limit")
    return code
