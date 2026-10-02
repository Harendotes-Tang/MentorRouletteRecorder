using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class LivePipelineProcessMemoryTests
{
    [Theory]
    [InlineData(111, 0, 0, true)]
    [InlineData(222, 0, 0, false)]
    [InlineData(111, 0, 1, false)]
    [InlineData(111, null, null, false)]
    [InlineData(111, null, 0, false)]
    [InlineData(111, 0, null, false)]
    public void ARecordedRunInheritsTheJobOnlyWhenRetryingTheSameClient(
        int nextPid, int? firstStartOffset, int? nextStartOffset, bool preservesJob)
    {
        using var db = new TestDatabase();
        var selection = ProfileSelector.SelectExplicit(Path.Combine(
            AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json"), allowSynthetic: true);
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), _ => selection);
        var first = OpenSession(db);
        pipeline.Refresh(Game(db, 111, firstStartOffset));
        pipeline.OnCaptureStarted(first);
        pipeline.Accept(Message(db, first, 61444, 1, new byte[] { 19, 0, 0, 0 }));
        pipeline.OnCaptureStopped(first, CaptureEndReason.UserStop);

        // Job recognition is optional. Without that rule, an inherited job would otherwise
        // silently become the new client's job for every record it produces.
        selection = selection with
        {
            Profile = selection.Profile! with
            {
                Messages = selection.Profile!.Messages.Where(message => message.Name != "PLAYER_JOB").ToArray(),
            },
        };
        var second = OpenSession(db);
        pipeline.Refresh(Game(db, nextPid, nextStartOffset));
        pipeline.OnCaptureStarted(second);
        pipeline.Accept(Message(db, second, 61444, 1, new byte[] { 24, 0, 0, 0 }));
        CompleteRun(db, pipeline, second);
        pipeline.OnCaptureStopped(second, CaptureEndReason.UserStop);

        var run = Assert.Single(new RunRepository(db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(second, run.CaptureSessionId);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(preservesJob ? 19 : (int?)null, run.JobId);
        Assert.Equal(preservesJob ? Role.Tank : Role.Unknown, run.Role);
    }

    private static GameProcessDetection Game(TestDatabase db, int pid, int? startOffset) => new(
        true, pid, "ffxiv_dx11", startOffset is { } offset ? db.Clock.UtcNow.AddSeconds(offset) : null,
        Region.Cn, "synthetic", 2, null, Array.Empty<string>());

    private static string OpenSession(TestDatabase db)
    {
        var id = Guid.NewGuid().ToString("D");
        db.Database.RunInTransaction(tx => new CaptureSessionRepository(db.Database).Insert(new CaptureSession
        {
            CaptureSessionId = id,
            StartedAtUtc = db.Clock.UtcNow,
            CollectorVersion = "test",
            Region = Region.Cn,
            ProfileStatus = ProfileStatus.Unverified,
        }, tx));
        return id;
    }

    private static DecodedMessage Message(TestDatabase db, string session, ushort opcode, long milliseconds, byte[] payload) =>
        new(session, MessageDirection.Inbound, db.Clock.UtcNow.AddMilliseconds(milliseconds),
            TimeSpan.FromMilliseconds(milliseconds), milliseconds, 61440, opcode, payload, "synthetic");

    private static void CompleteRun(TestDatabase db, LiveProtocolPipeline pipeline, string session)
    {
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 900_001);
        pipeline.Accept(Message(db, session, 61441, 1_000, pop));
        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 800_001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 900_001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 1);
        zone[12] = 1;
        pipeline.Accept(Message(db, session, 61442, 2_000, zone));
        pipeline.Accept(Message(db, session, 61443, 3_000, new byte[] { 1, 0, 0, 0 }));
    }
}
