using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Xunit.Abstractions;

namespace MentorRecorder.Collector.UnitTests;

public sealed class KeeperOfLakeDutyStageTests(ITestOutputHelper output)
{
    private const string Session = "2adc66dc-03cd-45f7-9271-7f9d997849cb";
    private const string SampleConnection = "269d915c";
    private const uint Player = 0x10000001;
    private const string TraceHash = "e4d00bea74caf3282481c57ebdebec9da6e4d84c9aeb9af9fd6276756d2bae37";
    private const ushort Countdown = 2012;
    private const ushort Sealed = 2013;
    private const ushort Released = 2014;

    [Fact]
    public async Task TheLevelSyncedTraceReachesTheLiveSnapshotAndIgnoresAdds()
    {
        using var database = new TestDatabase();
        var (pipeline, bus) = OpenPipeline(database, BoundProfile());
        using var events = bus.Subscribe(Guid.NewGuid().ToString("D"));

        var path = Environment.GetEnvironmentVariable("MR_KEEPER_PHASE_TRACE");
        var rows = string.IsNullOrEmpty(path) ? SampleBoundaries() : ReadEvidence(path);
        var checkpoints = new Dictionary<int, string?>
        {
            [2095] = "trash1",
            [9067] = "trash1",
            [9198] = "boss1",
            [14276] = "boss1",
            [14404] = "trash2",
            [24259] = "trash2",
            [24336] = "boss2",
            [30744] = "boss2",
            [30816] = "trash3",
            [37339] = "boss3",
            [42853] = "boss3", // Mirage Dragon 81
            [43431] = "boss3", // Mirage Dragon 80
            [46276] = "boss3",
            [46305] = null, // Verified duty clear precedes the last death notification.
            [46370] = null,
        };
        var checkedRows = 0;
        var observedRows = 0;
        foreach (var row in rows)
        {
            observedRows++;
            if (row.Sequence == 1961)
            {
                EnterKeeper(pipeline);
                Assert.Equal("unknown", pipeline.GetCurrentRun().DutyPhase);
            }

            pipeline.Accept(row.Message);
            if (checkpoints.Remove(row.Sequence, out var phase))
            {
                Assert.Equal(phase, pipeline.GetCurrentRun().DutyPhase);
                output.WriteLine($"seq={row.Sequence} phase={phase ?? "(cleared)"}");
                checkedRows++;
            }
        }

        Assert.Empty(checkpoints);
        if (!string.IsNullOrEmpty(path))
            Assert.Equal(47_303, observedRows);
        output.WriteLine($"Replayed {observedRows} rows and checked {checkedRows} stage snapshots.");
        Assert.Null(pipeline.GetCurrentRun().DutyPhase);
        Assert.Equal(RunResult.Completed,
            Assert.Single(new RunRepository(database.Database).Query(null, null, 1, 50).Items).Result);
        Assert.Equal(6, (await events.DrainAsync()).Count(e => e["kind"]!.GetValue<string>() == "run_phase_changed"));
        Assert.Equal(0, events.DroppedCount);
    }

    [Fact]
    public void OneArenaReleaseAndAnExactBossInstanceAreRequired()
    {
        var stage = Started();
        var boss = Npc(24);
        Assert.False(stage.Observe(Hit(2_900, boss))); // BOSS 2 can also take damage before its countdown.
        Assert.False(stage.Observe(Arena(3_000, 1503, Countdown)));
        Assert.True(stage.Observe(Packet(0, [], 4_501)));
        Assert.Equal("boss1", stage.Phase);

        Assert.False(stage.Observe(Death(8_000, boss))); // No release.
        Assert.False(stage.Observe(Arena(9_000, 1503, Released))); // Seal was never observed.
        Assert.False(stage.Observe(Death(9_001, boss)));
        Assert.False(stage.Observe(Arena(18_000, 1503, Sealed)));
        Assert.False(stage.Observe(Arena(30_000, 1505, Released))); // Another arena.
        Assert.False(stage.Observe(Death(30_001, boss)));
        Assert.False(stage.Observe(Arena(30_002, 1503, Released)));
        // The add may target the boss in the header; only the death subject can be matched.
        Assert.False(stage.Observe(Death(30_003, Npc(25)) with { TargetActor = boss }));
        Assert.False(stage.Observe(Death(30_004, boss) with { ConnectionKey = "other" }));
        Assert.False(stage.Observe(Death(30_005, boss) with { Epoch = 1 }));
        Assert.Equal("boss1", stage.Phase);

        Assert.True(stage.Observe(Death(32_002, boss)));
        Assert.Equal("trash2", stage.Phase);
        Assert.False(stage.Observe(Death(32_003, boss)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACountdownNeedsOneUnambiguousDamageTarget(bool ambiguous)
    {
        var stage = Started();
        Assert.False(stage.Observe(Arena(5_000, 1503, Countdown)));
        if (ambiguous)
        {
            Assert.False(stage.Observe(Hit(5_100, Npc(24))));
            Assert.False(stage.Observe(Hit(5_200, Npc(25))));
        }
        else
        {
            Assert.False(stage.Observe(Hit(5_100, Npc(24)) with { SourceActor = 0 }));
            Assert.False(stage.Observe(Hit(5_200, Npc(24)) with { SourceActor = Npc(25) }));
        }

        Assert.True(stage.Observe(Packet(0, [], 6_501)));
        Assert.Equal("unknown", stage.Phase);
        Assert.False(stage.Observe(Arena(20_000, 1503, Sealed)));
        Assert.False(stage.Observe(Arena(30_000, 1503, Released)));
        Assert.False(stage.Observe(Death(32_000, Npc(24))));
        Assert.Equal("unknown", stage.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedAttemptCannotAdvanceAndTheNextPullRebindsTheSameArena(bool openedAfterSeal)
    {
        var stage = Started();
        Assert.False(stage.Observe(Arena(3_000, 1503, Countdown)));
        Assert.False(stage.Observe(Hit(3_100, Npc(24))));
        Assert.True(stage.Observe(Packet(0, [], 4_501)));
        Assert.False(stage.Observe(Arena(4_701, 1503, Countdown))); // Duplicate during the same pull.
        if (openedAfterSeal)
        {
            Assert.False(stage.Observe(Arena(18_000, 1503, Sealed)));
            Assert.False(stage.Observe(Arena(30_000, 1503, Released)));
        }
        Assert.False(stage.Observe(Death(35_001, Npc(24)))); // No release, or too late to pair with it.

        Assert.False(stage.Observe(Arena(37_000, 1505, Countdown))); // Cannot skip to the next boss.
        Assert.False(stage.Observe(Arena(40_000, 1503, Countdown)));
        Assert.False(stage.Observe(Hit(40_300, Npc(124))));
        Assert.False(stage.Observe(Packet(0, [], 41_501))); // Still boss1, with a different instance.
        Assert.False(stage.Observe(Arena(55_000, 1503, Sealed)));
        Assert.False(stage.Observe(Arena(60_000, 1503, Released)));
        Assert.False(stage.Observe(Death(61_000, Npc(24))));
        Assert.Equal("boss1", stage.Phase);

        Assert.True(stage.Observe(Death(62_000, Npc(124))));
        Assert.Equal("trash2", stage.Phase);
    }

    [Fact]
    public void AStagedStartAllowsRecoveryAtAConfirmedArenaWithoutAssumingTrashOne()
    {
        var start = Director(DutyStartSignal.CommencedCommand, 5400, 5_000);
        var staging = new SharedCandidateStage(Session, BoundProfile());
        staging.Accept(start);
        var entry = Assert.Single(staging.Drain());
        Assert.IsType<MentorRecorder.Collector.Domain.Events.DutyCommenced>(entry.Event);
        var anchor = Assert.IsType<DutyStartAnchor>(entry.StartAnchor);

        var stage = new KeeperOfLakeDutyStage(SampleConnection);
        Assert.False(stage.ObserveStartAnchor(anchor with { ConnectionKey = "other" }, replayed: true));
        Assert.False(stage.ObserveStartAnchor(anchor with { Director = 72 }, replayed: true));
        Assert.False(stage.ObserveStartAnchor(anchor, replayed: true));
        Assert.Equal("unknown", stage.Phase);
        Assert.False(stage.Observe(Arena(8_000, 1505, Countdown)));
        Assert.False(stage.Observe(Hit(8_100, Npc(48))));
        Assert.True(stage.Observe(Packet(0, [], 9_501)));
        Assert.Equal("boss2", stage.Phase);
    }

    [Fact]
    public void LateEntryWaitsForTheFirstConfirmedArena()
    {
        var stage = new KeeperOfLakeDutyStage(SampleConnection);
        Assert.False(stage.Observe(Arena(1_000, 1505, Countdown)));
        Assert.False(stage.Observe(Director(DutyStartSignal.TimeRemainingCommand, 5000, 2_000)));
        Assert.Equal("unknown", stage.Phase);

        Assert.False(stage.Observe(Arena(3_000, 1505, Countdown)));
        Assert.False(stage.Observe(Hit(3_200, Npc(48))));
        Assert.True(stage.Observe(Packet(0, [], 4_501)));
        Assert.Equal("boss2", stage.Phase);
    }

    [Theory]
    [InlineData("2026.11.03.0000.0000", 32, 1063, 0, true)]
    [InlineData("2026.11.03.0000.0000", 32, 1063, 0x01de, false)]
    [InlineData("2026.11.03.0000.0000", 32, 1063, 0x02df, false)]
    [InlineData("2026.11.03.0000.0000", 32, 1063, 0x0190, false)]
    [InlineData("2026.11.03.0000.0000", 32, 1062, 0, false)]
    [InlineData("2026.10.03.0000.0000", 32, 1063, 0, false)]
    public void ATrackerRequiresTheVerifiedEntryAndIsDiscardedOnDisconnect(
        string build, int content, int territory, int obfuscated, bool supported)
    {
        using var database = new TestDatabase();
        var (pipeline, _) = OpenPipeline(database, BoundProfile(build, obfuscated));
        EnterKeeper(pipeline, content, territory);
        Assert.Equal(RunState.EnteredDuty, pipeline.GetCurrentRun().State);

        pipeline.Accept(Director(DutyStartSignal.CommencedCommand, 5400, 132_569) with { ConnectionKey = "other" });
        Assert.Equal(supported ? "unknown" : null, pipeline.GetCurrentRun().DutyPhase);
        pipeline.Accept(Director(DutyStartSignal.CommencedCommand, 5400, 132_569));
        Assert.Equal(supported ? "trash1" : null, pipeline.GetCurrentRun().DutyPhase);

        pipeline.OnConnectionLost(Session);
        Assert.Null(pipeline.GetCurrentRun().DutyPhase);
    }

    private static KeeperOfLakeDutyStage Started()
    {
        var stage = new KeeperOfLakeDutyStage(SampleConnection);
        Assert.True(stage.Observe(Director(DutyStartSignal.CommencedCommand, 5400, 1_000)));
        return stage;
    }

    private static IEnumerable<ReplayRow> SampleBoundaries()
    {
        yield return new(1961, Packet(0x031f, new byte[456], 126_246));
        yield return new(2095, Director(DutyStartSignal.CommencedCommand, 5400, 132_569));
        yield return new(9067, Arena(245_785, 1503, Countdown));
        yield return new(9120, Hit(246_280, Npc(24)));
        yield return new(9198, Packet(0x00ae, new byte[16], 247_301));
        yield return new(9966, Arena(260_848, 1503, Sealed));
        yield return new(14276, Arena(331_050, 1503, Released));
        yield return new(14404, Death(333_039, Npc(24)));
        yield return new(24218, Hit(504_896, Npc(48)));
        yield return new(24259, Arena(505_634, 1505, Countdown));
        yield return new(24336, Packet(0x00ae, new byte[16], 507_167));
        yield return new(24897, Arena(520_642, 1505, Sealed));
        yield return new(30744, Arena(576_369, 1505, Released));
        yield return new(30816, Death(578_353, Npc(48)));
        yield return new(37253, Hit(684_059, Npc(73)));
        yield return new(37266, Arena(684_182, 1507, Countdown));
        yield return new(37339, Packet(0x00ae, new byte[16], 685_732));
        yield return new(38121, Arena(699_224, 1507, Sealed));
        yield return new(42853, Death(794_124, Npc(81)));
        yield return new(43431, Death(802_980, Npc(80)));
        yield return new(46276, Arena(867_488, 1507, Released));
        yield return new(46305, Director(DutyClearSignal.Command, 0, 867_551));
        yield return new(46370, Death(869_482, Npc(73)));
    }

    // The original JSONL contains digests and anonymous aliases, not packet bodies.
    // Rebuild and hash-check intact director and arena bodies. Recreate only observed
    // action/death fields with test actor ids. Retain all message order, times and connections.
    private static IEnumerable<ReplayRow> ReadEvidence(string path)
    {
        using (var stream = File.OpenRead(path))
            Assert.Equal(TraceHash, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());

        var signatures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var row in SampleBoundaries().Where(row => row.Message.Opcode is 0x00a9 or 0x01de))
            signatures.Add(CaptureTraceSink.HashPrefix(row.Message.Payload.Span), row.Message.Payload);

        var seq = 0;
        var restored = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            if (!row.TryGetProperty("seq", out var sequence))
                continue;

            Assert.Equal(++seq, sequence.GetInt32());
            var opcode = ushort.Parse(row.GetProperty("op").GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var length = row.GetProperty("len").GetInt32();
            var digest = row.GetProperty("h12").GetString()!;
            var mono = row.GetProperty("t_ms").GetInt64();
            var epoch = row.GetProperty("epoch").GetInt64();
            var connection = row.GetProperty("conn").GetString()!;
            ReadOnlyMemory<byte> body = new byte[length];
            uint source = 0;
            if (opcode is 0x00a9 or 0x01de && signatures.TryGetValue(digest, out var verified))
            {
                Assert.Equal(length, verified.Length);
                body = verified;
                restored++;
            }
            else if (row.TryGetProperty("duty_phase", out var clue))
            {
                var kind = clue.GetProperty("kind").GetString();
                if (kind == "action")
                {
                    source = ReadActor(clue.GetProperty("source_npc"), Player);
                    var targets = clue.GetProperty("targets").EnumerateArray()
                        .Select(target => (target.GetProperty("index").GetInt32(),
                            ReadActor(target.GetProperty("npc"), 0), target.GetProperty("damage_candidate").GetBoolean()));
                    body = EncodeAction(length, targets);
                }
                else if (kind == "defeat")
                {
                    source = ReadActor(clue.GetProperty("source_npc"), 0);
                    body = Death(mono, source).Payload;
                }
            }

            yield return new(seq,
                new DecodedMessage(Session,
                    row.GetProperty("dir").GetString() == "S2C" ? MessageDirection.Inbound : MessageDirection.Outbound,
                    row.GetProperty("at_utc").GetDateTimeOffset(), TimeSpan.FromMilliseconds(mono), epoch,
                    row.GetProperty("seg").GetUInt16(), opcode, body, connection)
                {
                    SourceActor = source,
                });
        }

        Assert.Equal(11, restored); // Three countdown/seal/release triplets, commence and clear.
    }

    private static uint ReadActor(JsonElement alias, uint fallback) =>
        alias.ValueKind == JsonValueKind.Null ? fallback : Npc(alias.GetUInt32());

    private static uint Npc(uint alias) => 0x40000000 | alias;

    private static byte[] EncodeAction(int length, IEnumerable<(int Index, uint Actor, bool Damage)> targets)
    {
        var body = new byte[length];
        var targetList = length switch { 124 => 112, 636 => 560, _ => throw new InvalidDataException("Unexpected action length.") };
        foreach (var (index, actor, damage) in targets)
        {
            body[33] = Math.Max(body[33], checked((byte)(index + 1)));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(targetList + 8 * index), actor);
            if (damage)
                body[42 + 64 * index] = 3;
        }

        return body;
    }

    private static DecodedMessage Hit(long mono, uint actor) =>
        Packet(0x0190, EncodeAction(124, [(0, actor, true)]), mono) with { SourceActor = Player };

    private static DecodedMessage Death(long mono, uint actor)
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 6);
        return Packet(0x02df, body, mono) with { SourceActor = actor };
    }

    private static DecodedMessage Director(uint command, uint seconds, long mono)
    {
        var body = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x6d);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0x80030020);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), seconds);
        return Packet(0x00a9, body, mono);
    }

    private static DecodedMessage Arena(long mono, uint place, ushort code)
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x80030020);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), code);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), place);
        if (code == Countdown)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 15);
        return Packet(0x01de, body, mono);
    }

    private static DecodedMessage Packet(ushort opcode, byte[] body, long mono) =>
        new(Session, MessageDirection.Inbound, DateTimeOffset.UnixEpoch.AddMilliseconds(mono),
            TimeSpan.FromMilliseconds(mono), 1_795_836_000_000 + mono, 3, opcode, body, SampleConnection);

    private static ProtocolProfile BoundProfile(string build = "2026.11.03.0000.0000", int obfuscated = 0)
    {
        var source = SyntheticProfile.Select().Profile!;
        return new ProtocolProfile(source.ProfileId, Region.Cn, build, source.GeneratedAtUtc,
            source.MentorRouletteId, ProfileCompatibilityStatus.Verified, source.MatchWindow,
            source.Messages.Where(message => message.Name != "DUTY_RESULT").ToArray(),
            source.Fixtures, source.ProvenanceSummary, source.ProfileSha256, source.SourcePath,
            source.FixturesVerified, ObfuscatedOpcodes: obfuscated == 0 ? [] : [obfuscated]);
    }

    private static (LiveProtocolPipeline Pipeline, LiveEventBus Bus) OpenPipeline(TestDatabase database, ProtocolProfile profile)
    {
        var selection = new ProfileSelection(profile.Status, profile.ToBinding(), profile, Region.Cn,
            profile.GameBuild, "in-memory keeper stage test profile");
        var bus = new LiveEventBus(database.Clock);
        var pipeline = new LiveProtocolPipeline(database.Database, database.Clock, bus, _ => selection);
        var sessions = new CaptureSessionRepository(database.Database);
        database.Database.RunInTransaction(transaction => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = Session,
            StartedAtUtc = database.Clock.UtcNow,
            CollectorVersion = Program.Version,
            Region = Region.Cn,
            ProtocolProfileId = profile.ProfileId,
            ProfileStatus = ProfileStatus.Verified,
        }, transaction));
        pipeline.OnCaptureStarted(Session);
        return (pipeline, bus);
    }

    private static void EnterKeeper(LiveProtocolPipeline pipeline, int content = 32, int territory = 1063)
    {
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, (ushort)SyntheticProfile.Select().Profile!.MentorRouletteId!.Value);
        BinaryPrimitives.WriteInt32LittleEndian(pop.AsSpan(4), content);
        pipeline.Accept(Packet(61441, pop, 110_000) with { SegmentType = 61440 });

        var zone = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(zone, territory);
        BinaryPrimitives.WriteInt32LittleEndian(zone.AsSpan(4), content);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 1);
        zone[12] = 1;
        pipeline.Accept(Packet(61442, zone, 126_246) with { SegmentType = 61440 });
    }

    private sealed record ReplayRow(int Sequence, DecodedMessage Message);
}
