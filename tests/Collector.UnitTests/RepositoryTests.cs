using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RepositoryTests
{
    [Fact]
    public void Repositories_RoundTripSessionRunEventAndRevision()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var runs = new RunRepository(fixture.Database);
        var events = new RunEventRepository(fixture.Database);
        var revisions = new RunRevisionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        var run = TestDatabase.Run(source: RunSource.AutoNetwork) with
        {
            CaptureSessionId = sessionId,
            ProtocolProfileId = "synthetic/v1",
            GameBuild = "fixture",
        };

        fixture.Database.RunInTransaction(tx =>
        {
            sessions.Insert(new CaptureSession
            {
                CaptureSessionId = sessionId,
                StartedAtUtc = run.CreatedAtUtc,
                CollectorVersion = "test",
                Region = Region.Cn,
                ProtocolProfileId = "synthetic/v1",
                ProfileStatus = ProfileStatus.Verified,
                PacketsObserved = 3,
            }, tx);
            runs.Insert(run, tx);
            revisions.Append(CreateRevision(run), tx);
            Assert.True(events.Append(CreateEvent(run.RunId, "event-key-1", sequence: 0), tx));
        });

        Assert.Equal(run, runs.Get(run.RunId));
        Assert.Equal(sessionId, sessions.Get(sessionId)?.CaptureSessionId);
        Assert.Equal("event-key-1", Assert.Single(events.ListForRun(run.RunId)).EventKey);
        Assert.Equal(1, Assert.Single(revisions.ListForRun(run.RunId, 1, 50).Items).Revision);
    }

    /// <summary>
    /// Review finding L-11. Live capture holds the pipeline lock while it commits and every
    /// IPC read queues behind it, so its busy wait must be one second across three attempts
    /// rather than the five-second default a manual correction would otherwise block for.
    /// </summary>
    [Fact]
    public void LiveCaptureCommitsUseTheShortBusyTimeoutAndRestoreTheDefault()
    {
        using var fixture = new TestDatabase();

        Assert.Equal(5000, SqliteDatabase.DefaultBusyTimeoutMs);
        Assert.Equal(1000, SqliteDatabase.LiveCaptureBusyTimeoutMs);
        Assert.Equal(3, SemanticEventProcessor.StorageAttempts);
        Assert.Equal(SqliteDatabase.DefaultBusyTimeoutMs, fixture.Database.BusyTimeoutMs);

        var inside = 0;
        fixture.Database.RunLiveCaptureTransaction(_ => inside = ReadBusyTimeout(fixture.Database));

        Assert.Equal(SqliteDatabase.LiveCaptureBusyTimeoutMs, inside);
        Assert.Equal(SqliteDatabase.DefaultBusyTimeoutMs, fixture.Database.BusyTimeoutMs);

        // A failure inside the unit of work still restores the manual-mutation timeout.
        Assert.Throws<InvalidOperationException>(() => fixture.Database.RunLiveCaptureTransaction(
            _ => throw new InvalidOperationException("boom")));
        Assert.Equal(SqliteDatabase.DefaultBusyTimeoutMs, fixture.Database.BusyTimeoutMs);
    }

    private static int ReadBusyTimeout(SqliteDatabase database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Review finding M-5. <c>duty_source</c> is provenance the maintainer tools read back, so
    /// it has to survive insert, update and read like any other column -- and stay null for a
    /// run whose duty was never established.
    /// </summary>
    [Fact]
    public void Run_RoundTripsItsDutySourceIncludingTheAbsentOne()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var observed = TestDatabase.Run() with { DutySource = DutySource.ContentId };
        var inferred = TestDatabase.Run(contentId: null) with
        {
            TerritoryId = 1036, DutySource = DutySource.Territory,
        };
        var unknown = TestDatabase.Run(contentId: null) with { DutySource = null };

        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(observed, tx);
            runs.Insert(inferred, tx);
            runs.Insert(unknown, tx);
        });

        Assert.Equal(DutySource.ContentId, runs.Get(observed.RunId)!.DutySource);
        Assert.Equal(DutySource.Territory, runs.Get(inferred.RunId)!.DutySource);
        Assert.Null(runs.Get(unknown.RunId)!.DutySource);

        var stored = runs.Get(unknown.RunId)!;
        fixture.Database.RunInTransaction(tx => runs.Update(
            stored with { Revision = 2, DutySource = DutySource.Manual }, 1, tx));
        Assert.Equal(DutySource.Manual, runs.Get(unknown.RunId)!.DutySource);
    }

    [Fact]
    public void RunEvent_DeduplicatesOnlyEventKeyAndDoesNotHideSequenceCollision()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var events = new RunEventRepository(fixture.Database);
        var run = TestDatabase.Run();
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            Assert.True(events.Append(CreateEvent(run.RunId, "same", 0), tx));
            Assert.False(events.Append(CreateEvent(run.RunId, "same", 1), tx));
            Assert.Throws<SqliteException>(() => events.Append(CreateEvent(run.RunId, "different", 0), tx));
        });
    }

    [Fact]
    public void RevisionChain_RequiresContiguousVersionsAndCannotBeRewritten()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var revisions = new RunRevisionRepository(fixture.Database);
        var run = TestDatabase.Run();

        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            Assert.Throws<SqliteException>(() => revisions.Append(CreateRevision(run) with
            {
                Revision = 2,
                ChangeKind = ChangeKind.Correct,
            }, tx));
            revisions.Append(CreateRevision(run), tx);
        });

        using var update = fixture.Database.CreateCommand();
        update.CommandText = "UPDATE run_revisions SET reason = 'rewrite' WHERE run_id = $run;";
        update.Parameters.AddWithValue("$run", run.RunId);
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());

        using var delete = fixture.Database.CreateCommand();
        delete.CommandText = "DELETE FROM run_revisions WHERE run_id = $run;";
        delete.Parameters.AddWithValue("$run", run.RunId);
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
    }

    [Fact]
    public void IdempotencyResponse_SurvivesDatabaseReopen()
    {
        using var fixture = new TestDatabase();
        var requestId = Guid.NewGuid().ToString("D");
        var repository = new IdempotencyRepository(fixture.Database, fixture.Clock);
        fixture.Database.RunInTransaction(tx =>
            repository.Store(requestId, "CorrectRun", "{\"revision\":2}", tx));

        fixture.Reopen();

        repository = new IdempotencyRepository(fixture.Database, fixture.Clock);
        Assert.Equal("{\"revision\":2}", repository.TryGetResponse(requestId));
    }

    private static RunRevision CreateRevision(MentorRun run) => new()
    {
        RevisionId = Guid.NewGuid().ToString("D"),
        RunId = run.RunId,
        Revision = 1,
        ChangedAtUtc = run.CreatedAtUtc,
        ChangeKind = ChangeKind.CreateManual,
        Actor = RevisionActor.User,
        Reason = "单元测试补录",
        RequestId = Guid.NewGuid().ToString("D"),
        Changes = new[] { new RunFieldChange("run_id", null, run.RunId) },
    };

    private static RunEvent CreateEvent(string runId, string key, int sequence) => new()
    {
        EventId = Guid.NewGuid().ToString("D"),
        RunId = runId,
        Sequence = sequence,
        OccurredAtUtc = new DateTimeOffset(2026, 9, 4, 1, 0, sequence, 0, TimeSpan.Zero),
        MonotonicOffsetMs = sequence * 1000,
        EventType = "TEST",
        FromState = RunState.Idle,
        ToState = RunState.MentorMatched,
        Confidence = DetectionConfidence.High,
        EventKey = key,
    };
}
