#!/usr/bin/env python3
"""Offline tests for the Oodle signature finder.

These tests never touch a game file. They assemble a *synthetic* x86-64 image whose
bytes really do contain the shapes the tool looks for -- Machina's ten global patterns,
the five-argument ``Train`` call pair behind a ``cmp <r32>, 1`` guard, and two callees
with the prologue/argument/loop evidence the profile gates require -- and then run the
real matcher, the real Capstone disassembler and the real profile emitter over it.

``disassemble`` must not be mocked: with it patched out, an assertion about
``locate_train_calls`` describes the mock rather than the matching logic, which never
runs. The hand-encoded image below costs a few hundred bytes and keeps every assertion
about what the tool actually does.

Run:  python tools/oodle-signature-finder/test_find_signatures.py
"""

from __future__ import annotations

import contextlib
import io
import os
import struct
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import find_signatures  # noqa: E402


# --------------------------------------------------------------------------------------
# The synthetic image
# --------------------------------------------------------------------------------------

IMAGE_BASE = 0x140000000
SECTION_RVA = 0x1000
SECTION_SIZE = 0x8000
HEADER_SIZE = 0x400
IMAGE_SIZE = SECTION_RVA + SECTION_SIZE

# Layout, in RVAs. The state-allocation anchor and the Train call block have to sit
# inside one WINDOW_BYTES window of each other; everything else is deliberately far away
# so that a planted pattern cannot drift into the disassembly window.
ANCHOR_RVA = 0x1200          # the OodleNetwork1TCP_State_Size call site
CODE_RVA = 0x1240            # the init routine's TCP/UDP Train branch
TCP_CALLEE_RVA = 0x2000
UDP_CALLEE_RVA = 0x2100
PLANTED_BASE_RVA = 0x3000
SINK_RVA = 0x4000            # an inert direct-call destination

FILLER = 0x90                # nop: one byte, so any start offset realigns immediately

# Machina patterns that get planted verbatim. The two Train entries are what the tool
# derives, and the two State_Size entries share one call site (the UDP pattern is a
# strict prefix of the TCP one), so planting it twice would make both match twice.
PLANTED_SIGNATURES = [
    name for name in find_signatures.MACHINA_SIGNATURES
    if name not in find_signatures.TRAIN_TYPES and not name.endswith("State_Size")
]


def _rel32(end_of_instruction: int, target_rva: int) -> bytes:
    """The ``rel32`` operand that makes a call/jmp ending at ``end_of_instruction`` land
    on ``target_rva``."""
    return struct.pack("<i", target_rva - end_of_instruction)


def _anchor_block() -> bytes:
    """A call site the ``OodleNetwork1TCP_State_Size`` pattern matches exactly once.

    Decodes cleanly and ends exactly on its own last byte, so linear disassembly of the
    window stays aligned across it.
    """
    block = bytearray()
    block += bytes.fromhex("488b7f10")                        # mov rdi, [rdi + 0x10]
    block += bytes.fromhex("4885f6")                          # test rsi, rsi
    block += bytes.fromhex("7505")                            # jne +5
    block += bytes.fromhex("48894508")                        # mov [rbp + 8], rax
    block += b"\xe8" + _rel32(ANCHOR_RVA + 18, SINK_RVA)      # call (UDP_State_Size tail)
    block += bytes.fromhex("4c8bc0")                          # mov r8, rax
    block += b"\xe8" + _rel32(ANCHOR_RVA + 26, SINK_RVA)      # call (TCP_State_Size tail)
    assert len(block) == 26
    return bytes(block)


def _train_call_block(*, guard: bool = True, five_arguments: bool = True) -> bytes:
    """The init routine's protocol branch: TCP trained on the equal side, UDP on the
    ``jne`` side, both through one shared argument set-up.

    ``guard=False`` drops the ``cmp``/``jne`` pair, ``five_arguments=False`` drops the
    fifth stack argument -- both are shapes the tool must refuse to classify.
    """
    block = bytearray()
    if guard:
        block += bytes.fromhex("4183fd01")                    # cmp r13d, 1
        block += bytes.fromhex("751b")                        # jne (UDP arm)
    else:
        block += bytes.fromhex("90") * 6
    block += bytes.fromhex("488b0f")                          # mov rcx, [rdi]      TCP state
    block += bytes.fromhex("488bd3")                          # mov rdx, rbx        shared
    block += bytes.fromhex("4533c0")                          # xor r8d, r8d
    block += bytes.fromhex("4533c9")                          # xor r9d, r9d
    if five_arguments:
        block += bytes.fromhex("c744242000001000")            # mov [rsp+0x20], 0x100000
    else:
        block += bytes.fromhex("90") * 8
    assert len(block) == 26
    block += b"\xe8" + _rel32(CODE_RVA + 31, TCP_CALLEE_RVA)  # call TCP Train
    block += bytes.fromhex("eb09")                            # jmp past the UDP arm
    block += bytes.fromhex("488b4f08")                        # mov rcx, [rdi + 8]  UDP state
    block += b"\xe8" + _rel32(CODE_RVA + 42, UDP_CALLEE_RVA)  # call UDP Train
    assert len(block) == 42
    return bytes(block)


def _tcp_callee() -> bytes:
    """A callee with the evidence ``profile_readiness_errors`` demands of the TCP twin:
    a frame prologue, a read of the fifth stack argument, an indexed array load and at
    least one direct call."""
    block = bytearray()
    block += bytes.fromhex("55")                              # push rbp
    block += bytes.fromhex("4883ec40")                        # sub rsp, 0x40
    block += bytes.fromhex("488b442470")                      # mov rax, [rsp + 0x70]
    block += bytes.fromhex("488b0cc2")                        # mov rcx, [rdx + rax*8]
    block += b"\xe8" + _rel32(TCP_CALLEE_RVA + len(block) + 5, SINK_RVA)
    block += bytes.fromhex("4883c440")                        # add rsp, 0x40
    block += bytes.fromhex("5dc3")                            # pop rbp; ret
    return bytes(block)


def _udp_callee(*, bulk_copy: bool = True) -> bytes:
    """The UDP twin's callee. The UDP gate wants a bulk copy loop rather than an indexed
    load; ``bulk_copy=False`` removes exactly that one piece of evidence."""
    block = bytearray()
    block += bytes.fromhex("55")                              # push rbp
    block += bytes.fromhex("4883ec40")                        # sub rsp, 0x40
    block += bytes.fromhex("488b442468")                      # mov rax, [rsp + 0x68]
    if bulk_copy:
        block += bytes.fromhex("0f1002")                      # movups xmm0, [rdx]
        block += bytes.fromhex("0f1101")                      # movups [rcx], xmm0
    else:
        block += bytes.fromhex("90") * 6
    block += b"\xe8" + _rel32(UDP_CALLEE_RVA + len(block) + 5, SINK_RVA)
    block += bytes.fromhex("4883c440")
    block += bytes.fromhex("5dc3")
    return bytes(block)


def _planted(name: str, filler: int = 0x11) -> bytes:
    """One Machina pattern instantiated to concrete bytes, plus the rel32 its trailing
    call operand reads."""
    pattern = find_signatures.parse_pattern(find_signatures.MACHINA_SIGNATURES[name])
    concrete = bytes(filler if byte < 0 else byte for byte in pattern)
    return concrete + struct.pack("<i", 0x22)


def build_image_bytes(*, guard: bool = True, five_arguments: bool = True,
                      bulk_copy: bool = True, plant_globals: bool = True) -> bytearray:
    """The whole synthetic image, indexed by RVA."""
    body = bytearray(IMAGE_SIZE)
    body[SECTION_RVA:IMAGE_SIZE] = bytes([FILLER]) * SECTION_SIZE

    def put(rva: int, blob: bytes) -> None:
        body[rva:rva + len(blob)] = blob

    put(ANCHOR_RVA, _anchor_block())
    put(CODE_RVA, _train_call_block(guard=guard, five_arguments=five_arguments))
    put(TCP_CALLEE_RVA, _tcp_callee())
    put(UDP_CALLEE_RVA, _udp_callee(bulk_copy=bulk_copy))
    if plant_globals:
        for index, name in enumerate(PLANTED_SIGNATURES):
            put(PLANTED_BASE_RVA + index * 0x100, _planted(name))
    return body


class FlatImage:
    """``find_signatures.Image`` without the PE round trip: RVA equals buffer index.

    Only what the scanning code actually uses -- ``data``, ``image_base`` and a length --
    which is the entire point: these tests exercise the matcher, not pefile.
    """

    def __init__(self, body: bytes) -> None:
        self.data = bytes(body)
        self.image_base = IMAGE_BASE
        self.size_of_image = len(body)
        self.sections = [(".text", SECTION_RVA, SECTION_SIZE)]

    def __len__(self) -> int:
        return len(self.data)


def build_pe_bytes(body: bytes) -> bytes:
    """Wrap the section body in the smallest PE64 ``pefile`` will parse.

    The section is placed at ``SECTION_RVA``, so the image ``find_signatures.Image``
    reconstructs is byte-identical to ``body`` from ``SECTION_RVA`` onwards.
    """
    dos = bytearray(0x40)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 0x80)                   # e_lfanew

    coff = struct.pack("<HHIIIHH", 0x8664, 1, 0, 0, 0, 240, 0x0022)

    opt = bytearray(240)
    struct.pack_into("<H", opt, 0, 0x20B)                     # PE32+
    struct.pack_into("<I", opt, 4, SECTION_SIZE)              # SizeOfCode
    struct.pack_into("<I", opt, 16, SECTION_RVA)              # AddressOfEntryPoint
    struct.pack_into("<I", opt, 20, SECTION_RVA)              # BaseOfCode
    struct.pack_into("<Q", opt, 24, IMAGE_BASE)               # ImageBase
    struct.pack_into("<I", opt, 32, 0x1000)                   # SectionAlignment
    struct.pack_into("<I", opt, 36, 0x200)                    # FileAlignment
    struct.pack_into("<H", opt, 40, 6)                        # MajorOperatingSystemVersion
    struct.pack_into("<H", opt, 48, 6)                        # MajorSubsystemVersion
    struct.pack_into("<I", opt, 56, IMAGE_SIZE)               # SizeOfImage
    struct.pack_into("<I", opt, 60, HEADER_SIZE)              # SizeOfHeaders
    struct.pack_into("<H", opt, 68, 3)                        # Subsystem: console
    struct.pack_into("<I", opt, 108, 16)                      # NumberOfRvaAndSizes

    section = struct.pack("<8sIIIIIIHHI", b".text", SECTION_SIZE, SECTION_RVA,
                          SECTION_SIZE, HEADER_SIZE, 0, 0, 0, 0, 0x60000020)

    header = bytearray(HEADER_SIZE)
    header[0:0x40] = dos
    header[0x80:0x84] = b"PE\x00\x00"
    header[0x84:0x84 + 20] = coff
    header[0x98:0x98 + 240] = opt
    header[0x188:0x188 + 40] = section
    return bytes(header) + bytes(body[SECTION_RVA:IMAGE_SIZE])


@contextlib.contextmanager
def synthetic_executable(**kwargs):
    """A synthetic ``ffxiv_dx11.exe`` on disk, in its own directory."""
    body = build_image_bytes(**kwargs)
    with tempfile.TemporaryDirectory() as root:
        game = os.path.join(root, "game")
        os.makedirs(game)
        path = os.path.join(game, "ffxiv_dx11.exe")
        with open(path, "wb") as handle:
            handle.write(build_pe_bytes(body))
        yield path


# --------------------------------------------------------------------------------------
# Locating the Train call sites -- real bytes, real disassembler
# --------------------------------------------------------------------------------------

class TrainCallLocationTests(unittest.TestCase):
    def test_both_train_calls_are_found_and_classified_twice_over(self):
        image = FlatImage(build_image_bytes())

        located = find_signatures.locate_train_calls(image, ANCHOR_RVA)

        self.assertEqual({"TCP", "UDP"}, set(located))
        tcp, udp = located["TCP"], located["UDP"]

        # The state slot the pointer comes out of, and the guard branch, are independent
        # signals; the tool only trusts a site where both say the same thing.
        self.assertEqual("TCP", tcp["kind_by_state_slot"])
        self.assertEqual("TCP", tcp["kind_by_guard_branch"])
        self.assertTrue(tcp["kinds_agree"])
        self.assertEqual(0, tcp["state_displacement"])

        self.assertEqual("UDP", udp["kind_by_state_slot"])
        self.assertEqual("UDP", udp["kind_by_guard_branch"])
        self.assertTrue(udp["kinds_agree"])
        self.assertEqual(8, udp["state_displacement"])

        # And each resolves to the callee actually planted behind its rel32.
        self.assertEqual(CODE_RVA + 26, tcp["site_rva"])
        self.assertEqual(TCP_CALLEE_RVA, tcp["target_rva"])
        self.assertEqual(CODE_RVA + 37, udp["site_rva"])
        self.assertEqual(UDP_CALLEE_RVA, udp["target_rva"])

    def test_a_call_without_the_fifth_stack_argument_is_not_a_train_call(self):
        # Four-argument set-up: every other signal is unchanged, so this proves the
        # fifth-argument requirement is what rejects it rather than something incidental.
        image = FlatImage(build_image_bytes(five_arguments=False))

        self.assertEqual({}, find_signatures.locate_train_calls(image, ANCHOR_RVA))

    def test_without_the_protocol_guard_the_branch_classification_is_unavailable(self):
        image = FlatImage(build_image_bytes(guard=False))

        located = find_signatures.locate_train_calls(image, ANCHOR_RVA)

        # The state slots still separate the twins, but nothing independently confirms
        # them, and the profile gates below refuse a site in that state.
        self.assertEqual({"TCP", "UDP"}, set(located))
        for site in located.values():
            self.assertIsNone(site["kind_by_guard_branch"])

    def test_the_window_is_realigned_until_it_lands_on_the_anchor(self):
        # Fill the run-up to the anchor with three-byte instructions. 512 is not a
        # multiple of three, so a stream started at the window's first byte steps over
        # the anchor; only after advancing the start does one land on it.
        body = build_image_bytes()
        start = ANCHOR_RVA - find_signatures.WINDOW_BYTES
        for offset in range(start, ANCHOR_RVA, 3):
            body[offset:offset + 3] = bytes.fromhex("488bc0")  # mov rax, rax
        self.assertNotEqual(0, (ANCHOR_RVA - start) % 3)

        located = find_signatures.locate_train_calls(FlatImage(body), ANCHOR_RVA)

        self.assertEqual({"TCP", "UDP"}, set(located))
        self.assertEqual(TCP_CALLEE_RVA, located["TCP"]["target_rva"])

    def test_an_image_with_no_train_shape_at_all_yields_nothing(self):
        body = build_image_bytes()
        body[CODE_RVA:CODE_RVA + 42] = bytes([FILLER]) * 42

        self.assertEqual({}, find_signatures.locate_train_calls(FlatImage(body), ANCHOR_RVA))


# --------------------------------------------------------------------------------------
# The reproduction gate
# --------------------------------------------------------------------------------------

def _known_good_offsets(report: dict) -> dict:
    """The offsets this image really resolves for Machina's own ten patterns."""
    return {name: int(report["signatures"][name]["resolved_rva"], 16)
            for name in find_signatures.MACHINA_SIGNATURES
            if name not in find_signatures.TRAIN_TYPES}


class ReproductionGateTests(unittest.TestCase):
    def test_an_unknown_executable_is_reported_unchecked_not_passed(self):
        with synthetic_executable() as path:
            report = find_signatures.build_report(path)

        check = report["reproduction_check"]
        self.assertFalse(check["checked"])
        self.assertEqual("unchecked", check["reproduction"])

    def test_an_unchecked_run_writes_a_warning_to_stderr(self):
        with synthetic_executable() as path:
            report = find_signatures.build_report(path)

        stream = io.StringIO()
        self.assertTrue(find_signatures.warn_unchecked_reproduction(report, stream))
        written = stream.getvalue()
        self.assertIn("WARNING", written)
        self.assertIn("KNOWN_GOOD", written)

    def test_a_reproduced_executable_is_verified_and_silent(self):
        with synthetic_executable() as path:
            first = find_signatures.build_report(path)
            reference = {first["executable"]["sha256"]: {
                "label": "synthetic", "offsets": _known_good_offsets(first)}}
            with mock.patch.dict(find_signatures.KNOWN_GOOD, reference, clear=True):
                report = find_signatures.build_report(path)

        self.assertEqual("verified", report["reproduction_check"]["reproduction"])
        self.assertTrue(report["reproduction_check"]["ok"])

        stream = io.StringIO()
        self.assertFalse(find_signatures.warn_unchecked_reproduction(report, stream))
        self.assertEqual("", stream.getvalue())
        self.assertEqual([], find_signatures.profile_readiness_errors(report))

    def test_offsets_that_do_not_reproduce_are_failed_and_block_the_profile(self):
        with synthetic_executable() as path:
            first = find_signatures.build_report(path)
            offsets = _known_good_offsets(first)
            offsets["OodleMalloc"] += 0x10  # one wrong RVA is enough
            reference = {first["executable"]["sha256"]: {
                "label": "synthetic", "offsets": offsets}}
            with mock.patch.dict(find_signatures.KNOWN_GOOD, reference, clear=True):
                report = find_signatures.build_report(path)

        self.assertEqual("failed", report["reproduction_check"]["reproduction"])
        self.assertIn("OodleMalloc", report["reproduction_check"]["mismatches"])
        self.assertIn("known-good Machina offsets were not reproduced",
                      find_signatures.profile_readiness_errors(report))


# --------------------------------------------------------------------------------------
# The profile emission path
# --------------------------------------------------------------------------------------

class ProfileEmissionTests(unittest.TestCase):
    def test_a_complete_image_emits_a_profile_that_resolves_the_planted_callees(self):
        with synthetic_executable() as path:
            report = find_signatures.build_report(path)
            self.assertEqual([], find_signatures.profile_readiness_errors(report))
            profile = find_signatures.build_profile(report, "CN", "9.9.9.9999.9999")

        self.assertEqual(1, profile["schema_version"])
        self.assertEqual("CANDIDATE", profile["status"])
        self.assertEqual("tools/oodle-signature-finder", profile["source"])
        self.assertEqual(sorted(find_signatures.MACHINA_SIGNATURES),
                         sorted(profile["signatures"]))

        # The derived Train patterns must resolve to the two callees planted behind the
        # call sites -- the one thing a wrong RVA computation would get wrong silently.
        self.assertEqual(f"{TCP_CALLEE_RVA:08X}",
                         profile["resolved_rvas"]["OodleNetwork1TCP_Train"])
        self.assertEqual(f"{UDP_CALLEE_RVA:08X}",
                         profile["resolved_rvas"]["OodleNetwork1UDP_Train"])

        # Every profile signature must match the image exactly once, Train included.
        image_bytes = build_image_bytes()
        for name, text in profile["signatures"].items():
            hits = find_signatures.find_matches(
                find_signatures.parse_pattern(text), bytes(image_bytes))
            self.assertEqual(1, len(hits), f"{name} matched {len(hits)} times")

    def test_a_profile_from_an_unreferenced_executable_records_reproduction_unchecked(self):
        # The synthetic image has no KNOWN_GOOD entry, so nothing compared this run's RVA
        # arithmetic with a real Machina run. The profile has to say so on its own: the
        # warning goes to stderr and does not survive into the file anybody reads later.
        with synthetic_executable() as path:
            profile = find_signatures.build_profile(
                find_signatures.build_report(path), "CN", "9.9.9.9999.9999")

        self.assertEqual("unchecked", profile["reproduction"])

    def test_a_profile_from_a_reproduced_executable_records_reproduction_checked(self):
        with synthetic_executable() as path:
            first = find_signatures.build_report(path)
            reference = {first["executable"]["sha256"]: {
                "label": "synthetic", "offsets": _known_good_offsets(first)}}
            with mock.patch.dict(find_signatures.KNOWN_GOOD, reference, clear=True):
                report = find_signatures.build_report(path)
                profile = find_signatures.build_profile(report, "CN", "9.9.9.9999.9999")

        self.assertEqual("checked", profile["reproduction"])

    def test_the_reproduction_word_is_covered_by_the_profile_hash(self):
        # If reproduction were outside the canonical form, "unchecked" could be edited to
        # "checked" afterwards without invalidating profile_sha256.
        import hashlib

        with synthetic_executable() as path:
            profile = find_signatures.build_profile(
                find_signatures.build_report(path), "CN", "9.9.9.9999.9999")

        tampered = dict(profile, reproduction="checked")
        recomputed = hashlib.sha256(
            find_signatures.canonical_json(
                tampered, without=("profile_sha256",)).encode("utf-8")).hexdigest()
        self.assertNotEqual(profile["profile_sha256"], recomputed)

    def test_the_emitted_profile_hash_is_the_canonical_form_of_the_document(self):
        import hashlib

        with synthetic_executable() as path:
            profile = find_signatures.build_profile(
                find_signatures.build_report(path), "CN", "9.9.9.9999.9999")

        recomputed = hashlib.sha256(
            find_signatures.canonical_json(
                profile, without=("profile_sha256",)).encode("utf-8")).hexdigest()
        self.assertEqual(profile["profile_sha256"], recomputed)

    def test_a_callee_missing_its_semantic_evidence_refuses_to_emit(self):
        with synthetic_executable(bulk_copy=False) as path:
            report = find_signatures.build_report(path)

            errors = find_signatures.profile_readiness_errors(report)
            self.assertIn("OodleNetwork1UDP_Train callee lacks expected bulk_copy_loop "
                          "evidence", errors)
            with self.assertRaises(ValueError):
                find_signatures.build_profile(report, "CN", "9.9.9.9999.9999")

    def test_an_unclassifiable_guard_refuses_to_emit(self):
        with synthetic_executable(guard=False) as path:
            errors = find_signatures.profile_readiness_errors(
                find_signatures.build_report(path))

        self.assertIn("OodleNetwork1TCP_Train was not independently classified as TCP",
                      errors)
        self.assertIn("OodleNetwork1UDP_Train was not independently classified as UDP",
                      errors)

    def test_the_command_line_writes_a_profile_and_still_warns_about_the_gate(self):
        with synthetic_executable() as path:
            with tempfile.TemporaryDirectory() as outside:
                out = os.path.join(outside, "profile.json")
                stdout, stderr = io.StringIO(), io.StringIO()
                with contextlib.redirect_stdout(stdout), \
                        contextlib.redirect_stderr(stderr):
                    code = find_signatures.main(
                        ["--exe", path, "--profile-out", out,
                         "--game-build", "9.9.9.9999.9999", "--quiet"])

                self.assertEqual(0, code)
                self.assertTrue(os.path.isfile(out))
                # --quiet suppresses the report, never the caveat about what it is worth.
                self.assertIn("WARNING", stderr.getvalue())

                import json
                with open(out, encoding="utf-8") as handle:
                    written = json.load(handle)
                self.assertEqual("CANDIDATE", written["status"])
                self.assertEqual(f"{TCP_CALLEE_RVA:08X}",
                                 written["resolved_rvas"]["OodleNetwork1TCP_Train"])

    def test_the_command_line_refuses_to_write_into_the_game_directory(self):
        with synthetic_executable() as path:
            out = os.path.join(os.path.dirname(path), "profile.json")
            stderr = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), \
                    contextlib.redirect_stderr(stderr):
                code = find_signatures.main(
                    ["--exe", path, "--profile-out", out,
                     "--game-build", "9.9.9.9999.9999", "--quiet"])

            self.assertEqual(2, code)
            self.assertFalse(os.path.exists(out))
            self.assertIn("refusing", stderr.getvalue())


# --------------------------------------------------------------------------------------
# Output boundary
# --------------------------------------------------------------------------------------

class OutputBoundaryTests(unittest.TestCase):
    def test_a_path_below_a_link_into_the_game_directory_counts_as_inside(self):
        # `is_within` answers "is this physically inside the game folder"; main() turns a
        # True into a refusal. The link is the interesting part: abspath alone would call
        # this path external.
        with tempfile.TemporaryDirectory() as root:
            game = os.path.join(root, "game")
            outside = os.path.join(root, "outside")
            link = os.path.join(outside, "linked-game")
            os.makedirs(game)
            os.makedirs(outside)
            try:
                os.symlink(game, link, target_is_directory=True)
            except (NotImplementedError, OSError) as error:
                self.skipTest(f"directory links are unavailable: {error}")

            requested = os.path.join(link, "new", "finder-report.json")

            self.assertTrue(find_signatures.is_within(requested, game))

    def test_allows_an_unrelated_output_directory(self):
        with tempfile.TemporaryDirectory() as root:
            game = os.path.join(root, "game")
            outside = os.path.join(root, "outside")
            os.makedirs(game)

            self.assertFalse(
                find_signatures.is_within(os.path.join(outside, "report.json"), game))

    def test_resolution_failure_is_fail_closed(self):
        original = os.path.realpath

        def fail(_path):
            raise OSError("unresolvable")

        try:
            os.path.realpath = fail
            self.assertTrue(find_signatures.is_within("report.json", "game"))
        finally:
            os.path.realpath = original


if __name__ == "__main__":
    unittest.main(verbosity=2)
