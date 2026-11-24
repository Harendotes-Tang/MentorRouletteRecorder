using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The repositories and the transactions they write in: what survives a round trip, what the
/// revision chain refuses, how long a write waits behind a locked database, and what a failed or
/// busy transaction leaves behind.
/// </summary>
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

    /// <summary>
    /// The live-capture path must lower both <c>PRAGMA busy_timeout</c> and
    /// <c>CommandTimeout</c>. Lowering only the pragma leaves Microsoft.Data.Sqlite's own retry
    /// loop bounded by the connection's five-second default, and three attempts upstream then
    /// stall the pipeline for fifteen seconds while a zone-change burst fills the bounded queue
    /// (review findings L-11 and R-1).
    /// </summary>
    [Fact]
    public void ALockedDatabaseIsGivenUpOnQuicklyOnTheLivePathAndPatientlyOnTheManualOne()
    {
        using var fixture = new TestDatabase();
        using var blocker = OpenSecondConnection(fixture.Path);
        using (var begin = blocker.CreateCommand())
        {
            // A write transaction on another connection: every writer behind it now waits.
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var live = Stopwatch.StartNew();
        var liveFailure = Assert.Throws<CollectorException>(
            () => fixture.Database.RunLiveCaptureTransaction(_ => WriteSetting(fixture)));
        live.Stop();

        Assert.Equal(ErrorCodes.DbBusy, liveFailure.Code);
        Assert.True(
            live.Elapsed < TimeSpan.FromSeconds(2),
            "the live path must give up inside its own budget, not the connection default; took " + live.Elapsed);

        // Both budgets are handed back, so a manual mutation is still allowed to be patient.
        Assert.Equal(SqliteDatabase.DefaultBusyTimeoutMs, fixture.Database.BusyTimeoutMs);
        Assert.Equal(5, fixture.Database.CommandTimeoutSeconds);

        var manual = Stopwatch.StartNew();
        var manualFailure = Assert.Throws<CollectorException>(
            () => fixture.Database.RunInTransaction(_ => WriteSetting(fixture)));
        manual.Stop();

        Assert.Equal(ErrorCodes.DbBusy, manualFailure.Code);
        Assert.True(
            manual.Elapsed > TimeSpan.FromSeconds(3),
            "a person waiting for their own correction would rather wait than retype it; took " + manual.Elapsed);
    }

    [Fact]
    public void BusyAtTransactionStartUsesTheRetryableContractError()
    {
        using var fixture = new TestDatabase();
        fixture.Database.Read(connection => { connection.DefaultTimeout = 1; return 0; });
        using var other = OpenSecondConnection(fixture.Path);
        var workCalled = false;
        using (var transaction = other.BeginTransaction())
        {
            var error = Assert.Throws<CollectorException>(() =>
                fixture.Database.RunInTransaction(_ => { workCalled = true; }));
            Assert.Equal(ErrorCodes.DbBusy, error.Code);
            Assert.True(error.Retryable);
            Assert.False(workCalled);
            Assert.IsType<SqliteException>(error.InnerException);
        }
        fixture.Database.RunInTransaction(_ => { workCalled = true; });
        Assert.True(workCalled);
    }

    [Fact]
    public void NonSqliteFailureStillRollsBackTheWholeTransaction()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        Assert.Throws<InvalidOperationException>(() => fixture.Database.RunInTransaction(transaction =>
        {
            runs.Insert(TestDatabase.Run(), transaction);
            throw new InvalidOperationException("Injected failure after the write.");
        }));

        Assert.Equal(0, runs.Query(null, null, 1, 50).Total);
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

        fixture.InsertRuns(observed, inferred, unknown);

        Assert.Equal(DutySource.ContentId, runs.Get(observed.RunId)!.DutySource);
        Assert.Equal(DutySource.Territory, runs.Get(inferred.RunId)!.DutySource);
        Assert.Null(runs.Get(unknown.RunId)!.DutySource);

        var stored = runs.Get(unknown.RunId)!;
        fixture.Database.RunInTransaction(tx => runs.Update(
            stored with { Revision = 2, DutySource = DutySource.Manual }, 1, tx));
        Assert.Equal(DutySource.Manual, runs.Get(unknown.RunId)!.DutySource);
    }

    /// <summary>Three states, all of which have to survive insert, update and read: joined, not joined, not seen.</summary>
    [Fact]
    public void Run_RoundTripsWhetherItWasJoinedInProgressIncludingNotSeen()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var joined = TestDatabase.Run() with { JoinedInProgress = true };
        var fresh = TestDatabase.Run() with { JoinedInProgress = false };
        var unseen = TestDatabase.Run();

        fixture.InsertRuns(joined, fresh, unseen);

        Assert.True(runs.Get(joined.RunId)!.JoinedInProgress);
        Assert.False(runs.Get(fresh.RunId)!.JoinedInProgress);
        Assert.Null(runs.Get(unseen.RunId)!.JoinedInProgress);

        var stored = runs.Get(unseen.RunId)!;
        fixture.Database.RunInTransaction(tx => runs.Update(
            stored with { Revision = 2, JoinedInProgress = true }, 1, tx));
        var decided = runs.Get(unseen.RunId)!;
        Assert.True(decided.JoinedInProgress);
        fixture.Database.RunInTransaction(tx => runs.Update(
            decided with { Revision = 3, JoinedInProgress = null }, 2, tx));
        Assert.Null(runs.Get(unseen.RunId)!.JoinedInProgress);
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
    public void HighestAcceptedPageIsEmptyForBothRunAndRevisionQueries()
    {
        using var fixture = new TestDatabase();
        var created = fixture.Mutations().CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Reason = "分页回归检查",
            Result = RunResult.CancelledBeforeEntry,
        });
        var runs = new RunRepository(fixture.Database).Query(null, null, int.MaxValue, 200);
        var revisions = new RunRevisionRepository(fixture.Database)
            .ListForRun(created.RunId, int.MaxValue, 200);

        Assert.Equal(1, runs.Total);
        Assert.Empty(runs.Items);
        Assert.Equal(1, revisions.Total);
        Assert.Empty(revisions.Items);
    }

    /// <summary>Persisted integer audit values must remain exact and usable by undo.</summary>
    [Theory]
    [InlineData(2_147_483_647L)]
    [InlineData(9_007_199_254_740_993L)]
    [InlineData(long.MaxValue)]
    public void IntegerAuditValuesRoundTripWithoutFloatingPointPromotion(long value)
    {
        var json = RunRevisionRepository.SerializeChanges(new[]
        {
            new RunFieldChange(RunFields.DurationMs, value, 1L),
        });
        var change = Assert.Single(RunRevisionRepository.DeserializeChanges(json));

        Assert.Equal(value, Assert.IsType<long>(change.OldValue));
        Assert.Equal(value, RunFieldWriter.Apply(TestDatabase.Run(), change.Field, change.OldValue).DurationMs);
    }

    [Fact]
    public void FractionalAuditValuesRemainInvalidForIntegerFields()
    {
        var change = Assert.Single(RunRevisionRepository.DeserializeChanges(
            "[{\"field\":\"job_id\",\"old_value\":19.5,\"new_value\":24}]"));

        Assert.Equal(19.5, Assert.IsType<double>(change.OldValue));
        Assert.Throws<CollectorException>(() =>
            RunFieldWriter.Apply(TestDatabase.Run(), change.Field, change.OldValue));
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

    private static void WriteSetting(TestDatabase fixture)
    {
        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "INSERT INTO settings(key, value_json, updated_at_utc) VALUES('review.r1', '1', '2026-09-09T00:00:00.000Z') " +
            "ON CONFLICT(key) DO UPDATE SET value_json = '1';";
        command.ExecuteNonQuery();
    }

    /// <summary>A second, unpooled connection to the same file that gives up on a lock after one second.</summary>
    private static SqliteConnection OpenSecondConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ConnectionString);
        connection.Open();
        return connection;
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
