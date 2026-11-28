using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class HolminsterDutyStageTests
{
    private const uint FinishObjective = 0x80000016;
    private const uint NextObjective = 0x80000015;
    private const string Session = "5e3c568b-3816-437b-a31f-fd7374be70a6";

    [Fact]
    public void TheBlindTraceDirectorSequenceProjectsEveryStage()
    {
        var stage = new HolminsterDutyStage();
        stage.Reset("zone");
        var rows = new (uint Command, uint First, uint Second, long Epoch, string Digest, string? Phase)[]
        {
            (DutyStartSignal.CommencedCommand, 5400, 0, 1_795_834_660_209, "e5b81ebb2d17", "trash1"),
            (FinishObjective, 0, 1, 1_795_834_747_146, "8e6c1dce1d6b", null),
            (NextObjective, 1, 0, 1_795_834_747_146, "bfff704e570d", "boss1"),
            (FinishObjective, 1, 1, 1_795_834_777_321, "cfae1eac98dc", null),
            (NextObjective, 2, 0, 1_795_834_777_321, "b8c7990f244e", "trash2"),
            (FinishObjective, 2, 1, 1_795_834_857_240, "eb964ac9c4e0", null),
            (NextObjective, 3, 0, 1_795_834_857_240, "6862cf2c59b6", "boss2"),
            (FinishObjective, 3, 1, 1_795_834_888_606, "fca97be0ee72", null),
            (NextObjective, 4, 0, 1_795_834_888_606, "04e39e88cc87", "trash3"),
            (FinishObjective, 4, 1, 1_795_835_006_285, "b1718740268b", null),
            (NextObjective, 5, 0, 1_795_835_006_285, "16fcb5e5d8f5", "boss3"),
            (DutyClearSignal.Command, 0, 0, 1_795_835_042_476, "92b0317b74cd", null),
        };

        foreach (var row in rows)
        {
            var message = Director(row.Command, row.First, row.Second, row.Epoch);
            Assert.Equal(row.Digest, CaptureTraceSink.HashPrefix(message.Payload.Span));
            Assert.True(HolminsterDutyStage.IsCarrier(message));
            Assert.Equal(row.Phase is not null, stage.Observe(message));
            if (row.Phase is not null)
                Assert.Equal(row.Phase, stage.Phase);
        }
    }

    [Fact]
    public void APartialObjectiveOrForeignConnectionCannotAdvanceAndADuplicateDoesNotRewind()
    {
        var stage = new HolminsterDutyStage();
        stage.Reset("zone");
        var start = Director(DutyStartSignal.CommencedCommand, 5400, 0, 100);
        Assert.True(stage.Observe(start));
        Assert.False(HolminsterDutyStage.IsCarrier(start with { Opcode = 0x1234 }));
        Assert.False(HolminsterDutyStage.IsCarrier(start with { Direction = MessageDirection.Outbound }));
        var wrongDirector = start.Payload.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongDirector.AsSpan(4), 7);
        Assert.False(HolminsterDutyStage.IsCarrier(start with { Payload = wrongDirector }));
        Assert.False(stage.Observe(Director(NextObjective, 1, 0, 200)));
        Assert.False(stage.Observe(Director(FinishObjective, 0, 1, 200)));
        Assert.False(stage.Observe(Director(NextObjective, 1, 0, 200) with { ConnectionKey = "other" }));
        Assert.False(stage.Observe(Director(NextObjective, 1, 0, 201)));
        Assert.Equal("trash1", stage.Phase);

        Assert.False(stage.Observe(Director(FinishObjective, 0, 1, 202)));
        Assert.True(stage.Observe(Director(NextObjective, 1, 0, 202)));
        Assert.False(stage.Observe(start));
        Assert.False(stage.Observe(Director(FinishObjective, 0, 1, 203)));
        Assert.False(stage.Observe(Director(NextObjective, 1, 0, 203)));
        Assert.Equal("boss1", stage.Phase);
    }

    [Fact]
    public void ANewOrPartiallyObservedRunWaitsForAnAnchoredFullUpdate()
    {
        var stage = new HolminsterDutyStage();
        stage.Reset("zone");
        Assert.True(stage.Observe(Director(DutyStartSignal.CommencedCommand, 5400, 0, 100)));
        stage.Reset();
        Assert.Equal("unknown", stage.Phase);
        Assert.False(stage.Observe(Director(FinishObjective, 2, 1, 200)));
        Assert.False(stage.Observe(Director(NextObjective, 3, 0, 200)));
        stage.Reset("zone");
        Assert.False(stage.Observe(Director(DutyStartSignal.TimeRemainingCommand, 5300, 0, 201)));
        Assert.False(stage.Observe(Director(DutyStartSignal.CommencedCommand, 5400, 0, 202)));
        Assert.False(stage.Observe(Director(FinishObjective, 4, 1, 198)));
        Assert.False(stage.Observe(Director(NextObjective, 5, 0, 198)));
        Assert.Equal("unknown", stage.Phase);
        Assert.False(stage.Observe(Director(FinishObjective, 2, 1, 300)));
        Assert.True(stage.Observe(Director(NextObjective, 3, 0, 300)));
        Assert.Equal("boss2", stage.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LivePipelinePublishesPhaseAndClearsItWhenTheRunEnds(bool cleared)
    {
        using var fixture = new TestDatabase();
        var source = SyntheticProfile.Select().Profile!;
        var profile = source with
        {
            Region = Region.Cn,
            GameBuild = HolminsterDutyStage.ClientBuild,
            Status = ProfileCompatibilityStatus.Verified,
            Messages = source.Messages.Where(message => message.Name != "DUTY_RESULT").ToArray(),
        };
        var selection = new ProfileSelection(profile.Status, profile.ToBinding(), profile, Region.Cn,
            profile.GameBuild, "in-memory stage test profile");
        var bus = new LiveEventBus(fixture.Clock);
        using var events = bus.Subscribe(Guid.NewGuid().ToString("D"));
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus, _ => selection);
        var sessions = new CaptureSessionRepository(fixture.Database);
        fixture.Database.RunInTransaction(transaction => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = Session,
            StartedAtUtc = fixture.Clock.UtcNow,
            CollectorVersion = Program.Version,
            Region = Region.Cn,
            ProtocolProfileId = profile.ProfileId,
            ProfileStatus = ProfileStatus.Verified,
        }, transaction));
        pipeline.OnCaptureStarted(Session);

        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, (ushort)profile.MentorRouletteId!.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 676);
        pipeline.Accept(Message(61441, pop, 10_000) with { SegmentType = 61440 });
        Assert.Null(pipeline.GetCurrentRun().DutyPhase);
        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, HolminsterDutyStage.Territory);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 676);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 1);
        zone[12] = 1;
        pipeline.Accept(Message(61442, zone, 20_000) with { SegmentType = 61440 });
        Assert.Equal("unknown", pipeline.GetCurrentRun().DutyPhase);

        pipeline.Accept(Director(DutyStartSignal.TimeRemainingCommand, 5000, 0, 24_000) with { ConnectionKey = "foreign" });
        pipeline.Accept(Director(DutyStartSignal.CommencedCommand, 5400, 0, 25_000));
        pipeline.Accept(Director(FinishObjective, 0, 1, 26_000));
        pipeline.Accept(Director(NextObjective, 1, 0, 26_000));
        Assert.Equal("boss1", pipeline.GetCurrentRun().DutyPhase);

        var eventsBeforeLoss = await events.DrainAsync();
        Assert.Equal(2, eventsBeforeLoss.Count(e => e["kind"]!.GetValue<string>() == "run_phase_changed"));
        Assert.True(profile.ObservesDutyClear);
        if (cleared)
            pipeline.Accept(Director(DutyClearSignal.Command, 0, 0, 29_000));
        else
            pipeline.OnConnectionLost(Session);
        Assert.Null(pipeline.GetCurrentRun().DutyPhase);
    }

    private static DecodedMessage Director(uint command, uint first, uint second, long epoch)
    {
        var payload = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, DutyClearSignal.Category);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 72);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), DutyClearSignal.DirectorType);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), first);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), second);
        return Message(0x00a9, payload, epoch);
    }

    private static DecodedMessage Message(ushort opcode, byte[] payload, long epoch) =>
        new(Session, MessageDirection.Inbound, DateTimeOffset.UnixEpoch.AddMilliseconds(epoch),
            TimeSpan.FromMilliseconds(epoch), epoch, 3, opcode, payload, "zone");
}
