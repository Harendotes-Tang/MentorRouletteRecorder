using System.Buffers.Binary;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// How capture stores an automatically recorded run: automatic changes that all fall in one millisecond still
/// each publish the updated row and the statistics, an unchanged duplicate publishes nothing, and the stored
/// row stays at revision 1. Its subject is LiveProtocolPipeline; it stays here until ProtocolPipelineTests is
/// split under the test-file size limit.
/// </summary>
public sealed class AutomaticRunRecordingTests
{
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
}
