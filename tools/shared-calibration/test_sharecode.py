#!/usr/bin/env python3
"""sharecode.py against the shared vectors, the profile validator's canonical form, and the .NET
runtime behaviour the C# decoder inherits (probed 2026-09-16)."""

from __future__ import annotations

import re
import unittest

import testsupport
import sharecode


class VectorAgreementTests(unittest.TestCase):
    """Every vector in tests/Fixtures/shared-calibration/vectors.json, which ShareCodeTests.cs also runs."""

    @classmethod
    def setUpClass(cls):
        cls.vectors = testsupport.load_vectors()

    def test_the_vectors_are_there(self):
        self.assertGreaterEqual(len(self.vectors["valid"]), 5)
        self.assertGreaterEqual(len(self.vectors["invalid"]), 20)

    def test_every_valid_vector_decodes_to_its_payload_canonical_form_and_hash(self):
        for vector in self.vectors["valid"]:
            with self.subTest(vector["name"]):
                self.assertEqual(vector["canonical"], sharecode.canonical(vector["payload"]))
                decoded = sharecode.decode(vector["code"])
                self.assertIsNone(decoded.rejection)
                self.assertEqual(vector["payload"], decoded.payload)
                self.assertEqual(vector["code_sha256"], decoded.code_sha256)
                self.assertEqual(vector["code_sha256"], sharecode.code_sha256(decoded.payload))

    def test_encoding_a_valid_payload_round_trips_to_the_same_identity(self):
        for vector in self.vectors["valid"]:
            with self.subTest(vector["name"]):
                code = sharecode.encode(vector["payload"])
                self.assertRegex(code, r"\AMRC1\.[A-Za-z0-9_-]+\Z")
                self.assertLessEqual(len(code), sharecode.MAX_CODE_LENGTH)
                self.assertEqual(vector["code_sha256"], sharecode.decode(code).code_sha256)

    def test_every_invalid_vector_is_refused_for_its_own_reason(self):
        for vector in self.vectors["invalid"]:
            with self.subTest(vector["name"]):
                decoded = sharecode.decode(vector["code"])
                self.assertIsNone(decoded.payload)
                self.assertIsNone(decoded.code_sha256)
                self.assertEqual(vector["reason"], decoded.rejection.code)
                self.assertRegex(decoded.rejection.message, "[一-鿿]")
                self.assertNotIn("0x", decoded.rejection.message.lower())

    def test_nothing_or_something_that_is_not_text_is_empty(self):
        self.assertEqual(sharecode.E_EMPTY, sharecode.decode(None).rejection.code)
        self.assertEqual(sharecode.E_EMPTY, sharecode.decode(42).rejection.code)


class CanonicalAgreementTests(unittest.TestCase):
    def test_the_copy_agrees_with_the_profile_validator(self):
        validate = testsupport.load_validator()
        samples = [vector["payload"] for vector in testsupport.load_vectors()["valid"]] + [
            testsupport.shipped_template_document(),
            {"b": [1, -2, True, False, None], "a": {"z": "\"\\\b\f\n\r\t\x01\x1f\x7f", "é": "中文 \U0001F38C"}},
            [], {}, "", 0, -(2 ** 63), 2 ** 63 - 1,
        ]
        for sample in samples:
            with self.subTest(repr(sample)[:40]):
                self.assertEqual(validate.canonical(sample), sharecode.canonical(sample))
        for bad in (1.5, {"a": 0.0}, [float("nan")]):
            with self.assertRaises(ValueError):
                validate.canonical(bad)
            with self.assertRaises(ValueError):
                sharecode.canonical(bad)

    def test_the_template_hash_is_the_validator_hash_and_the_one_stamped_in_the_file(self):
        validate = testsupport.load_validator()
        document = testsupport.shipped_template_document()
        self.assertEqual(document["profile_sha256"], validate.profile_hash(document))
        self.assertEqual(document["profile_sha256"], testsupport.shipped_template().sha256)


def _canonical_bytes(payload: dict) -> bytes:
    return sharecode.canonical(payload).encode("utf-8")


class DotnetRuntimeBehaviourTests(unittest.TestCase):
    """What ShareCode.Decode does because of the .NET runtime, not because of its own code."""

    def setUp(self):
        self.payload = testsupport.payload("ANNOUNCEMENT", 1)
        self.code = sharecode.encode(self.payload)
        self.sha = sharecode.code_sha256(self.payload)

    def decode_text(self, text: str) -> str:
        decoded = sharecode.decode(testsupport.raw_code(text.encode("utf-8")))
        return decoded.rejection.code if decoded.rejection else "VALID"

    def test_trimming_removes_exactly_what_string_trim_removes(self):
        self.assertEqual(self.sha, sharecode.decode("　 \t " + self.code + "  ").code_sha256)
        self.assertEqual(sharecode.E_NOT_A_CODE, sharecode.decode("\x1c" + self.code).rejection.code)
        self.assertEqual(sharecode.E_NOT_A_CODE, sharecode.decode("​" + self.code).rejection.code)
        self.assertEqual(sharecode.E_NOT_A_CODE, sharecode.decode("﻿" + self.code).rejection.code)
        self.assertEqual(sharecode.E_CHARACTERS, sharecode.decode(self.code + "\x1f").rejection.code)

    def test_the_length_limit_counts_utf16_code_units(self):
        text = sharecode.PREFIX + "\U0001F600" * 2046
        self.assertLess(len(text), sharecode.MAX_CODE_LENGTH)
        self.assertEqual(sharecode.E_TOO_LONG, sharecode.decode(text).rejection.code)

    def test_a_truncated_deflate_stream_is_refused_by_the_json_step(self):
        raw = testsupport.deflate(_canonical_bytes(self.payload))
        decoded = sharecode.decode(testsupport.code_from_raw(raw[: len(raw) // 2]))
        self.assertEqual(sharecode.E_JSON, decoded.rejection.code)

    def test_bytes_after_the_final_deflate_block_are_ignored(self):
        raw = testsupport.deflate(_canonical_bytes(self.payload)) + b"\x00\xff\x13"
        self.assertEqual(self.sha, sharecode.decode(testsupport.code_from_raw(raw)).code_sha256)

    def test_a_byte_order_mark_is_refused(self):
        code = testsupport.raw_code(b"\xef\xbb\xbf" + _canonical_bytes(self.payload))
        self.assertEqual(sharecode.E_JSON, sharecode.decode(code).rejection.code)

    def test_nesting_deeper_than_eight_is_refused_as_json(self):
        self.assertEqual(sharecode.E_JSON, self.decode_text('{"v":' + "[" * 8 + "1" + "]" * 8 + "}"))
        self.assertEqual(sharecode.E_VALUE, self.decode_text('{"v":' + "[" * 7 + "1" + "]" * 7 + "}"))

    def test_numbers_follow_try_get_int64(self):
        zero = sharecode.canonical(testsupport.payload("ANNOUNCEMENT", 1, zone_opcode=0))
        text = sharecode.canonical(testsupport.payload("ANNOUNCEMENT", 1, zone_opcode=4321))
        cases = {
            text.replace('"zone_opcode":4321', '"zone_opcode":4321.0'): sharecode.E_VALUE,
            text.replace('"zone_opcode":4321', '"zone_opcode":4.321e3'): sharecode.E_VALUE,
            text.replace('"zone_opcode":4321', '"zone_opcode":true'): sharecode.E_VALUE,
            text.replace('"zone_opcode":4321', '"zone_opcode":NaN'): sharecode.E_JSON,
            text.replace('"zone_opcode":4321', '"zone_opcode":04321'): sharecode.E_JSON,
            zero.replace('"zone_opcode":0', '"zone_opcode":-0'): sharecode.E_NOT_CANONICAL,
        }
        for inflated, expected in cases.items():
            with self.subTest(inflated[-60:]):
                self.assertEqual(expected, self.decode_text(inflated))
        reply = sharecode.canonical(testsupport.payload("REPLY_STATE", 1))
        self.assertEqual(sharecode.E_VALUE, self.decode_text(reply.replace("[3]", "[9223372036854775808]")))
        self.assertEqual("VALID", self.decode_text(reply.replace("[3]", "[9223372036854775807]")))

    def test_text_that_is_not_utf8_is_refused_as_json(self):
        code = testsupport.raw_code(b'{"v":1,"\xff":1}')
        self.assertEqual(sharecode.E_JSON, sharecode.decode(code).rejection.code)

    def test_a_lone_surrogate_anywhere_is_refused_as_json(self):
        # ShareCodeTests.cs applies the same damage. .NET cannot read a key or string holding a lone
        # surrogate, so both decoders refuse the whole text before looking at what it says.
        text = sharecode.canonical(testsupport.payload("REPLY_STATE", 1))
        cases = {
            "a key of the payload": ('{"game_build"', '{"\\ud800":1,"game_build"'),
            "a key inside the pop": ('"pop":{"opcode"', '"pop":{"\\ud800":1,"opcode"'),
            "a string value": ('"game_build":"2026.09.01.0000.0000"', '"game_build":"\\ud800"'),
            "a lone low surrogate": ('"match_source":"REPLY_STATE"', '"match_source":"\\udc00"'),
            "a pair in the wrong order": ('"region":"CN"', '"region":"\\udc00\\ud800"'),
            "a high surrogate before a letter": ('"region":"CN"', '"region":"\\ud800N"'),
            "an array element": ('"selector_values":[3]', '"selector_values":["\\ud800"]'),
            "the value of a repeated key": ('{"game_build"', '{"game_build":"\\ud800","game_build"'),
        }
        for why, (anchor, replacement) in cases.items():
            with self.subTest(why):
                self.assertIn(anchor, text)
                self.assertEqual(sharecode.E_JSON, self.decode_text(text.replace(anchor, replacement)))

    def test_bytes_that_are_not_utf8_are_refused_as_json_wherever_they_are(self):
        data = _canonical_bytes(testsupport.payload("REPLY_STATE", 1))
        cases = {
            "inside a key": (b'{"game_build"', b'{"\xff":1,"game_build"'),
            "inside a string value": (b'"region":"CN"', b'"region":"C\xff"'),
            "a surrogate written as UTF-8": (b'"region":"CN"', b'"region":"\xed\xa0\x80"'),
            "a truncated sequence": (b'"region":"CN"', b'"region":"\xe4\xb8"'),
        }
        for why, (anchor, replacement) in cases.items():
            with self.subTest(why):
                self.assertIn(anchor, data)
                decoded = sharecode.decode(testsupport.raw_code(data.replace(anchor, replacement)))
                self.assertEqual(sharecode.E_JSON, decoded.rejection.code)

    def test_a_well_formed_surrogate_pair_is_ordinary_text(self):
        text = sharecode.canonical(testsupport.payload("REPLY_STATE", 1))
        paired = text.replace('{"game_build"', '{"\\ud83d\\ude00":1,"game_build"')
        self.assertEqual(sharecode.E_UNKNOWN_KEY, self.decode_text(paired))

    def test_a_repeated_key_inside_the_pop_is_a_duplicate(self):
        text = sharecode.canonical(self.payload)
        repeated = text.replace('"pop":{"length":40,', '"pop":{"length":40,"length":40,')
        self.assertNotEqual(text, repeated)
        self.assertEqual(sharecode.E_DUPLICATE_KEY, self.decode_text(repeated))

    def test_unused_base64_bits_are_ignored(self):
        alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
        for number in range(64):
            payload = testsupport.payload("ANNOUNCEMENT", number)
            code = sharecode.encode(payload)
            if len(code[len(sharecode.PREFIX):]) % 4 in (2, 3):
                break
        else:
            self.fail("no payload produced a body with unused bits")
        flipped = code[:-1] + alphabet[alphabet.index(code[-1]) ^ 1]
        self.assertNotEqual(code, flipped)
        self.assertEqual(sharecode.code_sha256(payload), sharecode.decode(flipped).code_sha256)


class EncodeTests(unittest.TestCase):
    def test_a_payload_the_decoder_would_refuse_cannot_be_encoded(self):
        cases = {
            "queue request without territory": (testsupport.payload("QUEUE_REQUEST", territory_opcode=None), sharecode.E_TERRITORY_REQUIRED),
            "reply state with a length": (testsupport.payload("REPLY_STATE", pop={"opcode": 1, "length": 40, "selector_values": [3]}), sharecode.E_POP_KEY_FORBIDDEN),
            "announcement without a length": (testsupport.payload("ANNOUNCEMENT", pop={"opcode": 1}), sharecode.E_POP_KEY_MISSING),
            "marker offset outside the pop": (testsupport.payload("MARKER_OFFSET", pop={"opcode": 1, "length": 24, "roulette_offset": 30}), sharecode.E_VALUE),
            "unknown region": (testsupport.payload(region="UNKNOWN"), sharecode.E_VALUE),
            "uppercase template hash": (testsupport.payload(template_sha256="A" * 64), sharecode.E_VALUE),
            "build with a space": (testsupport.payload(game_build="2026 09"), sharecode.E_VALUE),
            "too many selector values": (testsupport.payload("REPLY_STATE", pop={"opcode": 1, "selector_values": list(range(33))}), sharecode.E_VALUE),
            "fractional opcode": (testsupport.payload(zone_opcode=1.5), sharecode.E_VALUE),
            "an extra key": (testsupport.payload(note="hello"), sharecode.E_UNKNOWN_KEY),
            # ShareCodeTests.cs: ShareCode.Check refuses a build holding a lone surrogate as a bad value too.
            "build with a lone surrogate": (testsupport.payload(game_build="2026\ud800"), sharecode.E_VALUE),
            "not an object": ([], sharecode.E_JSON),
        }
        for why, (payload, token) in cases.items():
            with self.subTest(why):
                self.assertEqual(token, sharecode.check(payload).code)
                with self.assertRaises(ValueError):
                    sharecode.encode(payload)
                with self.assertRaises(ValueError):
                    sharecode.code_sha256(payload)

    def test_the_largest_payload_the_format_allows_still_fits_the_code_limit(self):
        payload = testsupport.payload(
            "REPLY_STATE", pop={"opcode": 0xFFFF, "selector_values": [-(2 ** 63)] * sharecode.MAX_SELECTOR_VALUES},
            game_build="9" * 128, template_profile_id="a" + "9" * 63, zone_opcode=0xFFFF,
            territory_opcode=0xFFFF, job_opcode=0xFFFF)
        code = sharecode.encode(payload)
        self.assertLessEqual(len(code), sharecode.MAX_CODE_LENGTH)
        self.assertTrue(sharecode.decode(code).valid)

    def test_the_identity_is_the_hash_of_the_canonical_payload_whatever_the_key_order(self):
        payload = testsupport.payload("MARKER_OFFSET", 3)
        reordered = dict(reversed(list(payload.items())))
        self.assertEqual(sharecode.sha256_hex(_canonical_bytes(payload)), sharecode.code_sha256(reordered))
        self.assertEqual(sharecode.code_sha256(payload), sharecode.decode(sharecode.encode(reordered)).code_sha256)

    def test_every_rejection_token_has_a_chinese_message_without_hex(self):
        tokens = [value for name, value in vars(sharecode).items() if name.startswith("E_")]
        self.assertEqual(18, len(tokens))
        for token in tokens:
            with self.subTest(token):
                message = sharecode.Rejection(token, "").message
                self.assertIn(token, sharecode.MESSAGES)
                self.assertRegex(message, "[一-鿿]")
                self.assertIsNone(re.search(r"0x|[0-9a-f]{12}", message))


if __name__ == "__main__":
    unittest.main()
