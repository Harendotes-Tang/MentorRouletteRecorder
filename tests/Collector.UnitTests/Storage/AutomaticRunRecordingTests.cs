using System.Buffers.Binary;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// How capture finishes and stores an automatically recorded run. An explicit exit from the duty ends the run
/// with the result the profile can vouch for: left, or unknown and pending review when the profile cannot
/// detect results. Automatic changes that all fall in one millisecond still each publish the updated row and
/// the statistics, an unchanged duplicate publishes nothing, and the stored row stays at revision 1.
/// </summary>
public sealed class AutomaticRunRecordingTests
{
    [Theory]
    [InlineData("zone", false)]
    [InlineData("instance", false)]
    [InlineData("nonduty", false)]
    [InlineData("zone", true)]
    [InlineData("instance", true)]
    [InlineData("nonduty", true)]
    public void ExplicitExitsRespectTheProfilesResultCapability(string exitKind, bool canDetectResult)
    {
        var start = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("review", Region.Cn, ProfileStatus.Verified, 42, canDetectResult));
        machine.Handle(new ContentFinderPop
        {
            Key = Key("pop", 0), ObservedAtUtc = start, Mono = TimeSpan.Zero, RouletteId = 42,
        });
        machine.Handle(new ZoneInitialization
        {
            Key = Key("entry", 1), ObservedAtUtc = start.AddSeconds(1),
            Mono = TimeSpan.FromSeconds(1), IsDutyInstance = true,
        });
        var key = Key(exitKind, 2);
        var time = start.AddSeconds(2);
        var mono = TimeSpan.FromSeconds(2);
        SemanticEvent exit = exitKind switch
        {
            "zone" => new ZoneLeft { Key = key, ObservedAtUtc = time, Mono = mono },
            "instance" => new InstanceLeft { Key = key, ObservedAtUtc = time, Mono = mono },
            _ => new ZoneInitialization { Key = key, ObservedAtUtc = time, Mono = mono, IsDutyInstance = false },
        };

        var finished = Assert.Single(machine.Handle(exit).Commands.OfType<FinishRunCommand>());

        Assert.Equal(canDetectResult ? RunResult.LeftOrAbandoned : RunResult.Unknown, finished.Result);
        Assert.Equal(!canDetectResult, finished.PendingReview);
        if (!canDetectResult) Assert.Equal(DetectionConfidence.Low, finished.Confidence);
    }

    [Fact]
    public async Task AutomaticChangesInOneMillisecondStillPublishUpdatedRowsAndStatistics()
    {
        using var db = new TestDatabase();
        _ = db.Settings();
        var sessionId = db.OpenCaptureSession(Region.Cn, null, ProfileStatus.Unverified);
        var selection = SyntheticProfile.Select();
        var bus = new LiveEventBus(db.Clock);
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, bus, _ => selection);
        pipeline.OnCaptureStarted(sessionId);
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 900_001);
        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 800_001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 900_001);
        zone[12] = 1;
        DecodedMessage Message(ushort opcode, long epoch, byte[] payload) => new(
            sessionId, MessageDirection.Inbound, db.Clock.UtcNow, TimeSpan.FromMilliseconds(epoch),
            epoch, 61440, opcode, payload, "review-connection");
        pipeline.Accept(Message(61441, 0, pop));
        pipeline.Accept(Message(61442, 1, zone));
        var job = Message(61444, 2, new byte[] { 19, 0, 0, 0 });
        pipeline.Accept(job);
        pipeline.Accept(job); // An unchanged duplicate must not emit another row update.
        pipeline.Accept(Message(61443, 3, new byte[] { 1, 0, 0, 0 }));

        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));
        var kinds = new List<string?>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None) is { } row)
            kinds.Add(row["kind"]?.GetValue<string>());

        Assert.Equal(3, kinds.Count(kind => kind == "run_updated"));
        Assert.Equal(4, kinds.Count(kind => kind == "stats_invalidated"));
        Assert.Equal(1, kinds.Count(kind => kind == "run_finished"));
        var stored = Assert.Single(new RunRepository(db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(db.Clock.UtcNow, stored.UpdatedAtUtc);
        Assert.Equal(RunResult.Completed, stored.Result);
    }

    private static EventKey Key(string kind, long epoch) =>
        new("review", PacketDirection.ServerToClient, kind, epoch, null, kind + epoch);
}
