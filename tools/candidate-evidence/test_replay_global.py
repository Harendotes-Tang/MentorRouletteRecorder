from dataclasses import replace
import unittest

from derive_fields import Evidence, Observation
from replay_global import NOTICE, PROFILE_ID, QUEUE, ZONE, candidate_shapes, replay


class GlobalReplayTests(unittest.TestCase):
    def event(self, kind, t, value, *, connection="a"):
        direction, opcode, length = candidate_shapes()[kind]
        payload = bytearray(length)
        if kind == QUEUE:
            payload[0] = value
        else:
            payload[2:4] = value.to_bytes(2, "little")
        return Observation(str(t), PROFILE_ID, kind, opcode, direction, length, "session",
                           connection, t, None, bytes(payload))

    def entries(self, *events):
        return replay(Evidence(events, 0, ""), {430})["session"]

    def test_exported_packet_shapes_and_times_are_independent_of_profile_edits(self):
        def observed(kind, direction, opcode, t, payload):
            return Observation(str(t), PROFILE_ID, kind, opcode, direction, len(payload), "session",
                               "connection", t, None, payload)

        queue = observed(QUEUE, "C2S", 0x0215, 84_761, bytes([9]) + bytes(23))
        notice = observed(NOTICE, "S2C", 0x03bf, 169_800, bytes([3, 0, 9, 0]) + bytes(36))
        zone = observed(ZONE, "S2C", 0x00ab, 195_454, bytes([0, 0, 0xae, 1]) + bytes(132))
        self.assertEqual([{"matched_ms": 169_800, "entered_ms": 195_454, "territory_id": 430}],
                         self.entries(queue, notice, zone))

    def test_long_queue_then_match_and_entry_records_once_at_the_two_observed_times(self):
        queue = self.event(QUEUE, 1, 9)
        notice = self.event(NOTICE, 3_660_000, 9)
        duty = self.event(ZONE, 3_680_000, 430)
        town = self.event(ZONE, 5_000, 341)
        self.assertEqual([{"matched_ms": notice.t_ms, "entered_ms": duty.t_ms, "territory_id": 430}],
                         self.entries(duty, notice, town, queue, duty, self.event(ZONE, 3_700_000, 430)))

    def test_no_entry_without_a_matching_mentor_notice_and_following_known_duty(self):
        q = self.event(QUEUE, 0, 9)
        pop = self.event(NOTICE, 5_000, 9)
        zone = self.event(ZONE, 10_000, 430)
        for events in [(q, zone), (q, pop), (pop, zone), (q, self.event(ZONE, 2_000, 341)),
                       (self.event(QUEUE, 0, 2), pop, zone), (q, self.event(NOTICE, 5_000, 2), zone),
                       (q, pop, self.event(QUEUE, 8_000, 2), zone),
                       (q, pop, self.event(ZONE, 7_000, 341), zone),
                       (q, pop, replace(zone, t_ms=125_001)), (q, pop, replace(zone, session="elsewhere")),
                       (q, replace(pop, connection="elsewhere"), zone),
                       (q, pop, replace(zone, t_ms=pop.t_ms)), (q, replace(pop, payload=None), zone)]:
            with self.subTest(events=events):
                result = replay(Evidence(events, 0, ""), {430})
                self.assertFalse(any(result.values()))


if __name__ == "__main__":
    unittest.main()
