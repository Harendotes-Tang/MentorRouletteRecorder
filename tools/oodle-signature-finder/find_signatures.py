#!/usr/bin/env python3
"""Locate Machina's Oodle call-site signatures inside a FFXIV client executable.

Purpose
-------
Machina.FFXIV finds the Oodle network functions by scanning the game image for the byte
pattern of each *call site* and following the trailing ``e8 rel32``.  Its built-in table
is written against the international client, so a regional build whose compiler allocated
registers differently loses a signature and Machina then refuses to decompress anything.

CN ``2026.08.05.0000.0000`` is such a build: ten of the twelve signatures match, while
``OodleNetwork1TCP_Train`` and ``OodleNetwork1UDP_Train`` miss because the guard
``cmp r13d, 1`` (``41 83 fd 01``) is ``cmp r12d, 1`` (``41 83 fc 01``).

This tool re-derives the missing call-site patterns from the binary so a signature
profile can be recorded as evidence.  It reads a copy of the file from disk; it never
writes to the game folder and never attaches to, opens or reads a process.

Method
------
1. Map the PE file into its virtual (RVA) layout, exactly as ``LoadLibraryW`` would --
   Machina scans the loaded module, so its reported offsets are RVAs.
2. Re-run Machina's own global patterns and check them against the offsets a real run
   reported.  Reproducing those ten offsets is what validates the RVA arithmetic
   before anything new is derived.
3. Disassemble around the ``OodleNetwork1TCP_State_Size`` call site: the Oodle network
   init routine allocates the TCP and UDP states there and trains them a few
   instructions later.  Find the ``call rel32`` whose argument setup is the five-argument
   ``Train`` shape -- ``mov rcx, [reg+off]`` (state), ``mov rdx, <shared>``,
   ``xor r8d, r8d``, ``xor r9d, r9d``, ``mov dword [rsp+20h], <n>`` -- and classify the
   TCP and UDP twins by which state slot ``rcx`` is loaded from.
4. Grow a byte pattern backwards from that ``call``, one instruction at a time,
   wildcarding only the bytes a compiler is free to change, until it is at least eight
   bytes long and matches exactly once in the whole image.

Usage
-----
    python find_signatures.py --exe <path to ffxiv_dx11.exe> [--out out.json]
                              [--work <dir>]

Requires ``pefile`` and ``capstone`` (both BSD-licensed).
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import shutil
import struct
import sys
import tempfile

try:
    import pefile
    from capstone import CS_ARCH_X86, CS_MODE_64, Cs
except ImportError as exc:  # pragma: no cover - environment problem, not logic
    sys.stderr.write(
        f"missing dependency: {exc}\n"
        "install with:  python -m pip install capstone pefile\n")
    raise SystemExit(2)


# --------------------------------------------------------------------------------------
# Machina's built-in table (Machina.FFXIV/Memory/SigScan.cs, master).
# Ten of these are reused unchanged; the two Train entries are what this tool replaces.
# --------------------------------------------------------------------------------------

MACHINA_SIGNATURES = {
    "OodleNetwork1_Shared_Size": "48 83 7b ** 00 75 ** b9 11 00 00 00 e8",
    "OodleNetwork1_Shared_SetWindow":
        "4c 8b 43 ** 41 b9 00 00 10 00 ba ** 00 00 00 48 89 43 ** 48 8b c8 e8",
    "OodleNetwork1UDP_Train": "41 83 fd 01 75 ** 48 8b 0f e8 ** ** ** ** eb ** 48 8b ** ** e8",
    "OodleNetwork1UDP_Decode": "74 ** 49 8b ca e8 ** ** ** ** eb ** 48 8b 49 ** e8",
    "OodleNetwork1UDP_State_Size": "48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8",
    "OodleNetwork1UDP_Encode": "48 83 c7 02 4d 8b c4 48 89 7c ** ** e8",
    "OodleMalloc": "41 be 00 00 00 40 ba 10 00 00 00 49 8b ce ff 15",
    "OodleFree": "48 8b cb f3 ab 4d 85 c0 74 ?? 49 8b c8 ff 15",
    "OodleNetwork1TCP_State_Size":
        "48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8 ** ** ** ** 4c ** ** e8",
    "OodleNetwork1TCP_Train": "41 83 fd 01 75 ** 48 8b 0f e8",
    "OodleNetwork1TCP_Decode": "4c 8b 11 48 89 6c ** ** 4d 85 d2 74 ** 49 8b ca e8",
    "OodleNetwork1TCP_Encode":
        "48 8b ** 48 8d ** ** ** c6 44 ** ** ** 49 8b ** 48 89 44 ** ** e8",
}

TRAIN_TYPES = ("OodleNetwork1TCP_Train", "OodleNetwork1UDP_Train")

# Offsets a real Machina run reported for this exact client. Reproducing them is the
# tool's own regression test: if the RVA arithmetic is wrong, these will not match.
KNOWN_GOOD = {
    "e06704e3fa9c3bd43a0c8d238945163d7be14c5700cc4d350618b8ec1dd4e1a6": {
        "label": "CN 2026.08.05.0000.0000",
        "offsets": {
            "OodleNetwork1TCP_Encode": 0x01E43FD0,
            "OodleNetwork1TCP_Decode": 0x01E4A770,
            "OodleNetwork1TCP_State_Size": 0x01E4A7B0,
            "OodleFree": 0x028E69D0,
            "OodleMalloc": 0x028E69C8,
            "OodleNetwork1UDP_Encode": 0x01E445E0,
            "OodleNetwork1UDP_State_Size": 0x01E4AFB0,
            "OodleNetwork1UDP_Decode": 0x01E4A910,
            "OodleNetwork1_Shared_SetWindow": 0x01E4C800,
            "OodleNetwork1_Shared_Size": 0x01E4C930,
        },
    },
}

WINDOW_BYTES = 512
MIN_PATTERN_BYTES = 8
MAX_LOOKBACK_INSTRUCTIONS = 24


# --------------------------------------------------------------------------------------
# Pattern matching, byte for byte identical to Machina's SigScan semantics.
# --------------------------------------------------------------------------------------

def parse_pattern(text: str) -> list[int]:
    """Turn ``"48 8b ** e8"`` into ``[0x48, 0x8b, -1, 0xe8]``. ``??`` means ``**``."""
    packed = text.replace(" ", "").replace("??", "**")
    if len(packed) % 2:
        raise ValueError(f"odd-length signature pattern: {text!r}")
    return [-1 if packed[i:i + 2] == "**" else int(packed[i:i + 2], 16)
            for i in range(0, len(packed), 2)]


def render_pattern(pattern: list[int]) -> str:
    """Inverse of :func:`parse_pattern`, in Machina's own spelling."""
    return " ".join("**" if b < 0 else f"{b:02x}" for b in pattern)


def find_matches(pattern: list[int], image: bytes, limit: int = 0) -> list[int]:
    """Every index where ``pattern`` matches. A leading wildcard is rejected."""
    if not pattern or pattern[0] < 0:
        raise ValueError("a signature must start with a literal byte")

    lead = bytes([pattern[0]])
    tail = len(pattern)
    hits: list[int] = []
    start = 0
    while True:
        at = image.find(lead, start)
        if at < 0 or at > len(image) - tail:
            return hits
        if all(pattern[j] < 0 or pattern[j] == image[at + j] for j in range(1, tail)):
            hits.append(at)
            if limit and len(hits) >= limit:
                return hits
        start = at + 1


def resolve_call_target(image: bytes, site: int, pattern_length: int) -> int:
    """Machina's ``GetSignaturefromOffset``: read the rel32 the pattern ends on."""
    after = site + pattern_length
    rel = struct.unpack_from("<i", image, after)[0]
    return after + 4 + rel


# --------------------------------------------------------------------------------------
# PE -> virtual image
# --------------------------------------------------------------------------------------

class Image:
    """The executable laid out the way the loader maps it, so offsets are RVAs."""

    def __init__(self, path: str) -> None:
        pe = pefile.PE(path, fast_load=True)
        self.size_of_image = pe.OPTIONAL_HEADER.SizeOfImage
        self.image_base = pe.OPTIONAL_HEADER.ImageBase
        buffer = bytearray(self.size_of_image)
        header = pe.header
        buffer[0:len(header)] = header
        self.sections = []
        for section in pe.sections:
            data = section.get_data()
            rva = section.VirtualAddress
            length = max(0, min(len(data), self.size_of_image - rva))
            buffer[rva:rva + length] = data[:length]
            self.sections.append((
                section.Name.rstrip(b"\x00").decode("ascii", "replace"),
                rva, section.Misc_VirtualSize))
        pe.close()
        self.data = bytes(buffer)

    def __len__(self) -> int:
        return len(self.data)


def hash_file(path: str) -> tuple[str, int]:
    """SHA-256 (lowercase hex) and byte size of a file."""
    digest = hashlib.sha256()
    size = 0
    with open(path, "rb") as handle:
        while chunk := handle.read(1 << 20):
            digest.update(chunk)
            size += len(chunk)
    return digest.hexdigest(), size


# --------------------------------------------------------------------------------------
# Disassembly helpers
# --------------------------------------------------------------------------------------

def _disassembler() -> Cs:
    engine = Cs(CS_ARCH_X86, CS_MODE_64)
    engine.detail = True
    return engine


def disassemble(image: Image, rva: int, length: int) -> list:
    """Linear disassembly starting at a known instruction boundary."""
    engine = _disassembler()
    end = min(len(image), rva + length)
    return list(engine.disasm(image.data[rva:end], image.image_base + rva))


def instruction_rva(image: Image, instruction) -> int:
    return instruction.address - image.image_base


def branch_target(image: Image, instruction) -> int | None:
    """RVA a direct ``call``/``jmp``/``jcc`` transfers to, or None."""
    text = instruction.op_str
    if not text.startswith("0x"):
        return None
    if instruction.mnemonic not in ("call", "jmp") and not instruction.mnemonic.startswith("j"):
        return None
    return int(text, 16) - image.image_base


# --------------------------------------------------------------------------------------
# Locating the Train call sites
# --------------------------------------------------------------------------------------

def _five_argument_train_call(window: list, index: int) -> dict | None:
    """Describe the ``call`` at ``index`` when its setup is the Train argument shape.

    The Oodle ``Train`` entry points take five arguments -- state, shared, packet
    pointer array, packet size array, packet count -- and the game's init routine calls
    them with the last three set to zero.  In the Microsoft x64 calling convention that
    is ``rcx`` from a state slot, ``rdx``, ``xor r8d,r8d``, ``xor r9d,r9d`` and a
    ``mov dword [rsp+20h], <n>`` for the fifth argument on the stack.
    """
    call = window[index]
    if call.mnemonic != "call" or not call.op_str.startswith("0x"):
        return None

    facts: dict = {}
    for back in range(index - 1, max(-1, index - MAX_LOOKBACK_INSTRUCTIONS) - 1, -1):
        instruction = window[back]
        operands = instruction.op_str.replace(" ", "")
        if instruction.mnemonic == "call":
            # A call normally ends the argument set-up block, except when it is the other
            # arm of the same if/else, recognisable by the unconditional jump that
            # immediately follows it. Those two arms share one set-up block and are the
            # TCP/UDP pair this tool looks for.
            if back + 1 < len(window) and window[back + 1].mnemonic == "jmp":
                continue
            break
        if instruction.mnemonic == "mov" and operands.startswith("rcx,qwordptr["):
            facts.setdefault("state_load", instruction)
        elif instruction.mnemonic == "mov" and operands.startswith("rdx,"):
            facts.setdefault("shared_load", instruction)
        elif instruction.mnemonic == "xor" and operands == "r8d,r8d":
            facts["r8_zero"] = True
        elif instruction.mnemonic == "xor" and operands == "r9d,r9d":
            facts["r9_zero"] = True
        elif instruction.mnemonic == "mov" and operands.startswith("dwordptr[rsp+0x20],"):
            facts["stack_arg5"] = True

    required = ("state_load", "shared_load", "r8_zero", "r9_zero", "stack_arg5")
    if not all(key in facts for key in required):
        return None

    state = facts["state_load"]
    displacement = 0
    for operand in state.operands:
        if operand.type == 3:  # X86_OP_MEM
            displacement = operand.mem.disp
    return {
        "call_index": index,
        "state_displacement": displacement,
        "state_load": state,
    }


def _guard_branch_kind(window: list, call_index: int) -> str | None:
    """TCP or UDP, decided by the ``cmp <r32>, 1`` / ``jne`` guard around the call.

    The init routine branches once on the protocol: the equal side trains TCP, the
    ``jne`` side trains UDP.  This is an independent signal from the state slot the
    pointer is loaded out of, and the two are cross-checked.
    """
    for back in range(call_index - 1, max(-1, call_index - MAX_LOOKBACK_INSTRUCTIONS) - 1, -1):
        instruction = window[back]
        if instruction.mnemonic != "jne":
            continue
        previous = window[back - 1] if back else None
        if previous is None or previous.mnemonic != "cmp" or not previous.op_str.endswith(", 1"):
            continue
        target = int(instruction.op_str, 16) if instruction.op_str.startswith("0x") else None
        if target is None:
            continue
        # Equal (fall-through) side -> TCP; the jne destination -> UDP.
        return "UDP" if window[call_index].address >= target else "TCP"
    return None


def locate_train_calls(image: Image, anchor_rva: int) -> dict:
    """Find the TCP and UDP ``Train`` call sites near a state-allocation anchor."""
    start = max(0, anchor_rva - WINDOW_BYTES)
    end = min(len(image), anchor_rva + WINDOW_BYTES)
    window = []
    # ``start`` is a byte offset, not necessarily an x86 instruction boundary.
    # Move forward only as far as needed to find a stream which demonstrably
    # realigns at the known-good anchor, while retaining as much of the window
    # before the anchor as possible.
    for candidate_start in range(start, anchor_rva + 1):
        candidate = disassemble(image, candidate_start, end - candidate_start)
        if any(instruction_rva(image, item) == anchor_rva for item in candidate):
            window = candidate
            break
    if not window:
        raise RuntimeError(f"cannot disassemble the window at {anchor_rva:08X}")

    found: dict = {}
    for index, instruction in enumerate(window):
        described = _five_argument_train_call(window, index)
        if described is None:
            continue
        by_slot = "TCP" if described["state_displacement"] == 0 else "UDP"
        by_branch = _guard_branch_kind(window, index)
        described["kind"] = by_slot
        described["kind_by_state_slot"] = by_slot
        described["kind_by_guard_branch"] = by_branch
        described["kinds_agree"] = by_branch is None or by_branch == by_slot
        described["site_rva"] = instruction_rva(image, instruction)
        described["target_rva"] = branch_target(image, instruction)
        described["window"] = window
        found.setdefault(by_slot, described)
    return found


# --------------------------------------------------------------------------------------
# Pattern derivation
# --------------------------------------------------------------------------------------

def generalize(instruction) -> list[int]:
    """One instruction as a byte pattern, wildcarding only what a compiler may change.

    The policy is deliberately narrow and is the whole reason a derived pattern can be
    trusted:

    * memory displacement bytes -- stack and structure layout shift between builds;
    * branch displacement bytes (``rel8``/``rel32``) -- these move with any code motion,
      and the trailing ``rel32`` is what Machina reads to resolve the callee;
    * the ModRM byte of a ``cmp <r32>, imm`` guard -- the register the compiler picked
      for the protocol flag is incidental, and picking ``r12d`` instead of ``r13d`` is
      precisely what broke Machina's built-in Train signatures on this client.

    Everything else stays literal, including non-branch immediates, because those are
    the constants that make the site recognisable at all.
    """
    pattern = list(instruction.bytes)
    encoding = instruction.encoding

    if encoding.disp_size:
        for i in range(encoding.disp_offset, encoding.disp_offset + encoding.disp_size):
            pattern[i] = -1

    is_branch = instruction.mnemonic in ("call", "jmp") or instruction.mnemonic.startswith("j")
    if is_branch and encoding.imm_size:
        for i in range(encoding.imm_offset, encoding.imm_offset + encoding.imm_size):
            pattern[i] = -1

    if instruction.mnemonic == "cmp" and encoding.modrm_offset and encoding.imm_size:
        pattern[encoding.modrm_offset] = -1

    return pattern


def derive_pattern(image: Image, window: list, call_index: int,
                   trailing: list[int] | None = None, min_span: int = 1) -> dict:
    """Grow a pattern backwards from a call until it is long enough and unique.

    ``min_span`` forces the pattern to reach back over at least that many instructions,
    which is how the UDP twin is made to start at the same shared argument set-up as the
    TCP one instead of stopping at the first thing that happens to be unique.
    """
    suffix = trailing or []
    attempts = []
    for span in range(max(1, min_span), MAX_LOOKBACK_INSTRUCTIONS + 1):
        first = call_index - span + 1
        if first < 0:
            break
        pattern: list[int] = []
        for instruction in window[first:call_index]:
            pattern.extend(generalize(instruction))
        # The call itself contributes only its opcode: Machina reads the rel32 that
        # follows the final byte of the pattern, so the pattern must stop at the 0xe8.
        pattern.extend(list(window[call_index].bytes)[:1])
        pattern.extend(suffix)

        if len(pattern) < MIN_PATTERN_BYTES:
            continue
        hits = find_matches(pattern, image.data, limit=2)
        attempts.append({"instructions": span, "bytes": len(pattern), "matches": len(hits)})
        if len(hits) == 1:
            return {
                "pattern": render_pattern(pattern),
                "parsed": pattern,
                "site_rva": hits[0],
                "instruction_span": span,
                "attempts": attempts,
            }
    raise RuntimeError("no unique pattern could be derived for this call site")


def derive_udp_pattern(image: Image, window: list, tcp_index: int, udp_index: int,
                       min_span: int = 1) -> dict:
    """The UDP twin's pattern: the TCP prefix, the branch over it, and the UDP call.

    This mirrors how Machina's own UDP signature is written -- it reuses the shared
    prefix and reaches past the TCP call -- so the two entries stay recognisably a pair.
    """
    trailing: list[int] = [-1, -1, -1, -1]  # the TCP call's rel32
    for instruction in window[tcp_index + 1:udp_index]:
        trailing.extend(generalize(instruction))
    trailing.extend(list(window[udp_index].bytes)[:1])
    return derive_pattern(image, window, tcp_index, trailing, min_span=min_span)


# --------------------------------------------------------------------------------------
# Callee sanity checks
# --------------------------------------------------------------------------------------

def inspect_callee(image: Image, rva: int) -> dict:
    """What can be said about the function a derived pattern resolves to.

    Nothing here is proof; it is the set of observations that either support or
    contradict "this is an Oodle ``Train`` entry point", reported as found.
    """
    listing = disassemble(image, rva, 0x100)
    if not listing:
        return {"disassembled": False}

    prologue = [f"{i.mnemonic} {i.op_str}".strip() for i in listing[:8]]
    frame_setup = any(
        i.mnemonic in ("push", "sub") or
        (i.mnemonic == "mov" and "qword ptr [rsp" in i.op_str) or
        (i.mnemonic == "mov" and i.op_str.startswith("rax, rsp"))
        for i in listing[:6])

    reads_stack_arg5 = any(
        i.mnemonic in ("mov", "movsxd") and "[rsp + 0x" in i.op_str and
        any(off in i.op_str for off in ("0x60", "0x68", "0x70", "0x78", "0xa8", "0xb0", "0xb8"))
        for i in listing)
    indexed_loads = any(
        i.mnemonic in ("mov", "movsxd") and ("*8]" in i.op_str or "*4]" in i.op_str)
        for i in listing)
    copy_loop = any(i.mnemonic.startswith("movup") or i.mnemonic.startswith("movap")
                    for i in listing)
    forwards_call = [branch_target(image, i) for i in listing if i.mnemonic == "call"]

    return {
        "disassembled": True,
        "prologue": prologue,
        "has_frame_setup": frame_setup,
        "reads_fifth_stack_argument": reads_stack_arg5,
        "indexed_array_loads": indexed_loads,
        "bulk_copy_loop": copy_loop,
        "direct_calls": [f"{t:08X}" for t in forwards_call if t is not None][:6],
        "listing": [
            f"{instruction_rva(image, i):08X}  {i.bytes.hex():<24s} {i.mnemonic} {i.op_str}".rstrip()
            for i in listing[:24]
        ],
    }


# --------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------

def scan_builtin(image: Image) -> dict:
    """Run Machina's table over the image and report every hit and its callee RVA."""
    results = {}
    for name, text in MACHINA_SIGNATURES.items():
        pattern = parse_pattern(text)
        hits = find_matches(pattern, image.data)
        entry = {"pattern": text, "source": "machina-builtin", "match_count": len(hits)}
        if hits:
            entry["site_rva"] = f"{hits[0]:08X}"
            entry["resolved_rva"] = f"{resolve_call_target(image.data, hits[0], len(pattern)):08X}"
        results[name] = entry
    return results


def check_known_good(sha256: str, builtin: dict) -> dict:
    """Compare the reproduced offsets with what a real Machina run reported.

    ``reproduction`` is the single word a caller should read: ``verified`` when the
    offsets a real Machina run reported were reproduced, ``failed`` when they were not,
    and ``unchecked`` when there is no reference for this executable at all.  The last
    case is *not* a pass -- see :func:`warn_unchecked_reproduction`.
    """
    reference = KNOWN_GOOD.get(sha256)
    if reference is None:
        return {
            "checked": False,
            "reproduction": "unchecked",
            "reason": "no reference offsets for this executable",
        }

    mismatches = {}
    for name, expected in reference["offsets"].items():
        actual = builtin.get(name, {}).get("resolved_rva")
        if actual != f"{expected:08X}":
            mismatches[name] = {"expected": f"{expected:08X}", "actual": actual}
    return {
        "checked": True,
        "reproduction": "verified" if not mismatches else "failed",
        "label": reference["label"],
        "compared": len(reference["offsets"]),
        "mismatches": mismatches,
        "ok": not mismatches,
    }


UNCHECKED_REPRODUCTION_WARNING = (
    "WARNING: the known-good reproduction check did not run for this executable.\n"
    "  KNOWN_GOOD has no entry for its SHA-256, so nothing validated this tool's RVA\n"
    "  arithmetic against offsets a real Machina run reported. Twelve patterns each\n"
    "  matching exactly once does NOT prove the resolved RVAs are right: the pattern\n"
    "  matching and the section-to-RVA mapping are independent, and only the second one\n"
    "  is what this check covers.\n"
    "  A profile written from an unchecked run is a CANDIDATE in the weakest sense. The\n"
    "  collector's OodleSignatureRuntime.Install compares every RVA and will fail closed\n"
    "  on a wrong one, which looks like a broken runtime rather than a broken profile.\n"
    "  Before trusting this run, add a KNOWN_GOOD entry for this build from one real\n"
    "  Machina run and re-run the tool.\n"
)


def warn_unchecked_reproduction(report: dict, stream=None) -> bool:
    """Emit the unchecked-reproduction warning. Returns whether it was emitted.

    Deliberately written to ``stderr`` and deliberately not suppressed by ``--quiet``:
    ``--quiet`` suppresses the *report*, not a caveat about how much the report is worth.
    """
    if report.get("reproduction_check", {}).get("reproduction") != "unchecked":
        return False
    (stream or sys.stderr).write(UNCHECKED_REPRODUCTION_WARNING)
    return True


def derive_train_signatures(image: Image, builtin: dict) -> tuple[dict, dict]:
    """Derive both Train patterns, plus the evidence gathered along the way."""
    anchor_text = builtin["OodleNetwork1TCP_State_Size"].get("site_rva")
    if anchor_text is None:
        raise RuntimeError(
            "OodleNetwork1TCP_State_Size did not match; the init routine cannot be located")
    anchor = int(anchor_text, 16)

    located = locate_train_calls(image, anchor)
    missing = [kind for kind in ("TCP", "UDP") if kind not in located]
    if missing:
        raise RuntimeError(f"no five-argument Train call found for: {', '.join(missing)}")

    window = located["TCP"]["window"]
    tcp_index = located["TCP"]["call_index"]
    udp_index = located["UDP"]["call_index"]

    tcp = derive_pattern(image, window, tcp_index)
    signatures = {
        "OodleNetwork1TCP_Train": tcp,
        "OodleNetwork1UDP_Train": derive_udp_pattern(
            image, window, tcp_index, udp_index, min_span=tcp["instruction_span"]),
    }

    evidence = {}
    for kind, name in (("TCP", "OodleNetwork1TCP_Train"), ("UDP", "OodleNetwork1UDP_Train")):
        site = located[kind]
        derived = signatures[name]
        resolved = resolve_call_target(image.data, derived["site_rva"], len(derived["parsed"]))
        derived["resolved_rva"] = resolved
        evidence[name] = {
            "call_site_rva": f"{site['site_rva']:08X}",
            "target_rva": f"{site['target_rva']:08X}",
            "pattern_resolves_to": f"{resolved:08X}",
            "pattern_agrees_with_call_site": resolved == site["target_rva"],
            "kind_by_state_slot": site["kind_by_state_slot"],
            "kind_by_guard_branch": site["kind_by_guard_branch"],
            "kinds_agree": site["kinds_agree"],
            "state_slot_displacement": site["state_displacement"],
            "callee": inspect_callee(image, site["target_rva"]),
            "call_site_disassembly": [
                f"{instruction_rva(image, i):08X}  {i.bytes.hex():<22s} {i.mnemonic} {i.op_str}".rstrip()
                for i in window[max(0, tcp_index - 6):udp_index + 2]
            ],
        }
    return signatures, evidence


def build_report(exe_path: str) -> dict:
    """Everything the tool knows about one executable."""
    sha256, size = hash_file(exe_path)
    image = Image(exe_path)
    builtin = scan_builtin(image)
    reproduction = check_known_good(sha256, builtin)
    derived, evidence = derive_train_signatures(image, builtin)

    signatures = {}
    for name, entry in builtin.items():
        if name in TRAIN_TYPES:
            continue
        signatures[name] = entry
    for name, entry in derived.items():
        hits = find_matches(entry["parsed"], image.data)
        signatures[name] = {
            "pattern": entry["pattern"],
            "source": "derived",
            "match_count": len(hits),
            "site_rva": f"{entry['site_rva']:08X}",
            "resolved_rva": f"{entry['resolved_rva']:08X}",
            "instruction_span": entry["instruction_span"],
        }

    return {
        "tool": "tools/oodle-signature-finder/find_signatures.py",
        "generated_at_utc": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "executable": {
            "sha256": sha256,
            "size": size,
            "size_of_image": f"{image.size_of_image:08X}",
            "image_base": f"{image.image_base:016X}",
            "sections": [{"name": n, "rva": f"{r:08X}", "virtual_size": f"{v:08X}"}
                         for n, r, v in image.sections],
        },
        "reproduction_check": reproduction,
        "signatures": signatures,
        "evidence": evidence,
    }


def print_report(report: dict) -> None:
    """Human-readable summary: opcode bytes and mnemonics only, never data."""
    exe = report["executable"]
    print("=" * 78)
    print("Oodle signature finder")
    print("=" * 78)
    print(f"executable sha256 : {exe['sha256']}")
    print(f"executable size   : {exe['size']} bytes")
    print(f"SizeOfImage       : {exe['size_of_image']}   ImageBase {exe['image_base']}")
    print()

    check = report["reproduction_check"]
    if check.get("checked"):
        state = "OK" if check["ok"] else "FAILED"
        print(f"[1] reproduction of Machina's known offsets ({check['label']}): {state} "
              f"({check['compared']} compared)")
        for name, detail in check.get("mismatches", {}).items():
            print(f"      MISMATCH {name}: expected {detail['expected']} got {detail['actual']}")
    else:
        print(f"[1] reproduction of Machina's known offsets: UNCHECKED "
              f"({check.get('reason')})")
        print("      this run validated nothing about the RVA arithmetic; see the "
              "warning on stderr")
    print()

    print("[2] signature table")
    print(f"    {'signature':32s} {'src':8s} {'hits':>4s} {'site':>10s} {'callee':>10s}")
    for name in MACHINA_SIGNATURES:
        entry = report["signatures"][name]
        print(f"    {name:32s} {entry['source'][:8]:8s} {entry['match_count']:>4d} "
              f"{entry.get('site_rva', '-'):>10s} {entry.get('resolved_rva', '-'):>10s}")
    print()

    for name, detail in report["evidence"].items():
        print(f"[3] {name}")
        print(f"    pattern             : {report['signatures'][name]['pattern']}")
        print(f"    matches in image    : {report['signatures'][name]['match_count']} (must be 1)")
        print(f"    call site RVA       : {detail['call_site_rva']}")
        print(f"    resolves to RVA     : {detail['pattern_resolves_to']} "
              f"(agrees with call site: {detail['pattern_agrees_with_call_site']})")
        print(f"    classified by slot  : {detail['kind_by_state_slot']} "
              f"(state slot +{detail['state_slot_displacement']})")
        print(f"    classified by branch: {detail['kind_by_guard_branch']} "
              f"(agree: {detail['kinds_agree']})")
        callee = detail["callee"]
        print(f"    callee frame setup  : {callee.get('has_frame_setup')}")
        print(f"    callee reads 5th arg: {callee.get('reads_fifth_stack_argument')}")
        print(f"    callee array loads  : {callee.get('indexed_array_loads')}")
        print(f"    callee copy loop    : {callee.get('bulk_copy_loop')}")
        print("    callee disassembly (first instructions):")
        for line in callee.get("listing", [])[:14]:
            print(f"      {line}")
        print()

    first = next(iter(report["evidence"].values()))
    print("[4] call-site disassembly (shared by both Train entries)")
    for line in first["call_site_disassembly"]:
        print(f"      {line}")
    print()


# --------------------------------------------------------------------------------------
# Signature profile emission
# --------------------------------------------------------------------------------------

def canonical_json(document: dict, *, without: tuple[str, ...] = ()) -> str:
    """The one canonical byte form a profile hash is taken over.

    Deliberately the same narrow grammar as ``src/Collector/Protocol/Profiles/
    CanonicalJson.cs`` and ``tools/protocol-profile-validator/validate.py``: object keys
    sorted ordinally, no whitespace, integers only, short escapes plus ``\\u00xx`` for the
    remaining control characters. A hash that depends on a formatting choice is not a hash.
    """
    def validate(value, location: str) -> None:
        if value is None or isinstance(value, (bool, int, str)):
            return
        if isinstance(value, list):
            for index, item in enumerate(value):
                validate(item, f"{location}[{index}]")
            return
        if isinstance(value, dict):
            for key, item in value.items():
                if not isinstance(key, str):
                    raise TypeError(f"{location} has a non-string object key")
                validate(item, f"{location}.{key}")
            return
        raise TypeError(f"{location} uses unsupported JSON type {type(value).__name__}")

    trimmed = {k: v for k, v in document.items() if k not in without}
    validate(trimmed, "$")
    return json.dumps(trimmed, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                      allow_nan=False)


def profile_readiness_errors(report: dict) -> list[str]:
    """Return every failed evidence gate that forbids profile emission."""
    errors: list[str] = []
    for name in MACHINA_SIGNATURES:
        entry = report["signatures"].get(name, {})
        if entry.get("match_count") != 1:
            errors.append(f"{name} must match exactly once")
        if not entry.get("site_rva") or not entry.get("resolved_rva"):
            errors.append(f"{name} must resolve a call target")

    reproduction = report.get("reproduction_check", {})
    if reproduction.get("checked") and not reproduction.get("ok"):
        errors.append("known-good Machina offsets were not reproduced")

    for expected_kind, name in (
            ("TCP", "OodleNetwork1TCP_Train"),
            ("UDP", "OodleNetwork1UDP_Train")):
        evidence = report.get("evidence", {}).get(name, {})
        if not evidence.get("pattern_agrees_with_call_site"):
            errors.append(f"{name} pattern target disagrees with its call site")
        if not evidence.get("kinds_agree"):
            errors.append(f"{name} state-slot and guard-branch classifications disagree")
        if (evidence.get("kind_by_state_slot") != expected_kind or
                evidence.get("kind_by_guard_branch") != expected_kind):
            errors.append(f"{name} was not independently classified as {expected_kind}")

        callee = evidence.get("callee", {})
        if not callee.get("disassembled") or not callee.get("has_frame_setup"):
            errors.append(f"{name} callee has no verified function prologue")
        if not callee.get("reads_fifth_stack_argument"):
            errors.append(f"{name} callee does not use the fifth Train argument")
        if not callee.get("direct_calls"):
            errors.append(f"{name} callee has no observed direct calls")
        semantic_key = "indexed_array_loads" if expected_kind == "TCP" else "bulk_copy_loop"
        if not callee.get(semantic_key):
            errors.append(f"{name} callee lacks expected {semantic_key} evidence")

    return errors


def build_profile(report: dict, region: str, game_build: str) -> dict:
    """Turn a finder report into the signature profile the collector loads.

    The profile carries its own ``reproduction`` word -- ``checked`` when a KNOWN_GOOD
    entry existed for this executable and the offsets it lists were reproduced,
    ``unchecked`` when there was no reference to compare against at all.  It has to be in
    the profile rather than only in the report, because the profile is the file that
    outlives the run: it is the one that gets copied into protocol-profiles/, reviewed
    months later and cited as evidence.  Somebody holding only the profile could not
    previously tell a run that reproduced Machina's own offsets from one that checked
    nothing, and those two are worth very different amounts.

    ``failed`` never appears here: :func:`profile_readiness_errors` refuses to emit a
    profile whose reproduction check ran and did not match.
    """
    errors = profile_readiness_errors(report)
    if errors:
        raise ValueError("profile evidence gates failed: " + "; ".join(errors))

    signatures = {name: report["signatures"][name]["pattern"] for name in MACHINA_SIGNATURES}
    resolved = {name: report["signatures"][name].get("resolved_rva", "")
                for name in MACHINA_SIGNATURES}

    profile = {
        "schema_version": 1,
        "region": region,
        "game_build": game_build,
        "exe_sha256": report["executable"]["sha256"],
        "exe_size": report["executable"]["size"],
        "generated_at_utc": report["generated_at_utc"],
        "source": "tools/oodle-signature-finder",
        "signatures": signatures,
        "resolved_rvas": resolved,
        "status": "CANDIDATE",
        "reproduction": (
            "checked" if report.get("reproduction_check", {}).get("checked") else "unchecked"),
    }
    digest = hashlib.sha256(canonical_json(profile).encode("utf-8")).hexdigest()
    profile["profile_sha256"] = digest
    return profile


def is_within(path: str, directory: str) -> bool:
    """Whether ``path`` physically resolves inside ``directory``.

    ``abspath`` alone only collapses ``.`` and ``..``.  It does not follow a Windows
    junction or a symbolic link, so a syntactically external output could otherwise land
    back in the game directory through a linked parent.  ``realpath`` follows every
    existing link in the path and still preserves a not-yet-created filename tail.
    """
    try:
        resolved_path = os.path.normcase(os.path.realpath(os.path.abspath(path)))
        resolved_directory = os.path.normcase(os.path.realpath(os.path.abspath(directory)))
        return os.path.commonpath((resolved_path, resolved_directory)) == resolved_directory
    except (OSError, ValueError):
        # This predicate guards a forbidden destination.  A path that cannot be resolved
        # safely is therefore treated as inside rather than allowed through on uncertainty.
        return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--exe", required=True, help="path to ffxiv_dx11.exe")
    parser.add_argument("--out", help="write the JSON report to this path")
    parser.add_argument("--profile-out", help="write a collector signature profile here")
    parser.add_argument("--region", default="CN", help="region token for the profile")
    parser.add_argument("--game-build", help="client build string for the profile")
    parser.add_argument("--work", help="directory for the working copy (default: a temp dir)")
    parser.add_argument("--quiet", action="store_true", help="suppress the human report")
    args = parser.parse_args(argv)

    source = os.path.abspath(args.exe)
    if not os.path.isfile(source):
        sys.stderr.write("the requested executable is not a readable file\n")
        return 2

    source_directory = os.path.dirname(source)
    for label, requested in (("working directory", args.work),
                             ("report output", args.out),
                             ("profile output", args.profile_out)):
        if requested and is_within(requested, source_directory):
            sys.stderr.write(f"refusing {label} inside the game executable directory\n")
            return 2

    # The game folder is read-only to this tool: by default the analysis runs on a copy so
    # that nothing can write back to it.
    work = args.work or tempfile.mkdtemp(prefix="oodle-sig-")
    cleanup = None if args.work else work
    os.makedirs(work, exist_ok=True)
    target = os.path.join(work, os.path.basename(source))
    shutil.copy2(source, target)

    try:
        report = build_report(target)
    finally:
        if cleanup:
            shutil.rmtree(cleanup, ignore_errors=True)

    # Emitted before any other output: an unchecked reproduction gate changes how every
    # number below should be read.
    warn_unchecked_reproduction(report)

    if not args.quiet:
        print_report(report)
    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        print(f"wrote {os.path.basename(args.out)}")

    readiness_errors = profile_readiness_errors(report)
    if args.profile_out:
        if not args.game_build:
            sys.stderr.write("--profile-out requires --game-build\n")
            return 2
        if readiness_errors:
            for error in readiness_errors:
                sys.stderr.write(f"profile refused: {error}\n")
            return 1
        profile = build_profile(report, args.region, args.game_build)
        with open(args.profile_out, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(profile, handle, indent=2, ensure_ascii=False, sort_keys=True)
            handle.write("\n")
        print(f"wrote {os.path.basename(args.profile_out)}  "
              f"(profile_sha256 {profile['profile_sha256']}, "
              f"reproduction {profile['reproduction']})")
        # Repeated here: the profile just written is the artifact the caveat is about, and
        # the first copy is far above in the output.
        warn_unchecked_reproduction(report)

    return 0 if not readiness_errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
