using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Replay;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// What happens when the Collector dies mid-run and starts again.
///
/// Rule under test: an unfinished run becomes INTERRUPTED and pending review, never COMPLETED.
/// Recovery never infers an outcome it did not observe.
/// </summary>
public sealed class CrashRecoveryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public CrashRecoveryTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.RecoveryIt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "recovery.db");
    }

    /// <summary>
    /// Writes a run that is still in flight, exactly as a process that died between the duty
    /// finder pop and the duty result would have left it.
    /// </summary>
    /// <param name="sessionEnded">
    /// Whether the capture session row was closed before the process went away. A session can
    /// be closed while a run under it stays open, so recovery keys off the run, not the
    /// session (docs/state-machine.md section 3.9).
    /// </param>
    private string SeedUnfinishedRun(bool sessionEnded = false)
    {
        using var database = SqliteDatabase.Open(_databasePath, SystemClock.Instance);
        var settings = new SettingsRepository(database, SystemClock.Instance);
        settings.EnsureDefaults();

        var runs = new RunRepository(database);
        var revisions = new RunRevisionRepository(database);
        var sessions = new CaptureSessionRepository(database);
        var runId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("D");
        var matched = new DateTimeOffset(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

        database.RunInTransaction(tx =>
        {
            sessions.Insert(
                new CaptureSession
                {
                    CaptureSessionId = sessionId,
                    StartedAtUtc = matched.AddMinutes(-5),
                    EndedAtUtc = sessionEnded ? matched.AddMinutes(2) : null,
                    EndReason = sessionEnded ? CaptureEndReason.Unknown : null,
                    CollectorVersion = Program.Version,
                    ProfileStatus = ProfileStatus.Unverified,
                },
                tx);

            runs.Insert(
                new MentorRun
                {
                    RunId = runId,
                    Revision = 1,
                    CaptureSessionId = sessionId,
                    MentorRouletteId = 42,
                    ContentId = 900001,
                    JobId = 19,
                    JobName = "骑士",
                    Role = Role.Tank,
                    MatchedAtUtc = matched,
                    EnteredAtUtc = matched.AddMinutes(1),
                    Result = RunResult.Unknown,
                    DetectionConfidence = DetectionConfidence.Medium,
                    Source = RunSource.AutoNetwork,
                    CreatedAtUtc = matched,
                    UpdatedAtUtc = matched,
                },
                tx);

            revisions.Append(
                new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"),
                    RunId = runId,
                    Revision = 1,
                    ChangedAtUtc = matched,
                    ChangeKind = ChangeKind.CreateAuto,
                    Actor = RevisionActor.System,
                    Changes = new[] { new RunFieldChange("result", null, "UNKNOWN") },
                },
                tx);
        });

        return runId;
    }

    /// <summary>
    /// Writes a run that a CN-style profile finished by design: the duty ended, the profile
    /// could observe the ending but not its outcome, so the row is UNKNOWN with
    /// pending_review = 1 and a real ended_at_utc (docs/state-machine.md section 3.10).
    /// </summary>
    private string SeedFinishedUnknownRun()
    {
        using var database = SqliteDatabase.Open(_databasePath, SystemClock.Instance);
        var settings = new SettingsRepository(database, SystemClock.Instance);
        settings.EnsureDefaults();

        var runs = new RunRepository(database);
        var sessions = new CaptureSessionRepository(database);
        var runId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("D");
        var matched = new DateTimeOffset(2026, 9, 3, 21, 0, 0, TimeSpan.Zero);

        database.RunInTransaction(tx =>
        {
            sessions.Insert(
                new CaptureSession
                {
                    CaptureSessionId = sessionId,
                    StartedAtUtc = matched.AddMinutes(-5),
                    CollectorVersion = Program.Version,
                    ProfileStatus = ProfileStatus.Verified,
                },
                tx);

            runs.Insert(
                new MentorRun
                {
                    RunId = runId,
                    Revision = 1,
                    CaptureSessionId = sessionId,
                    MentorRouletteId = 42,
                    ContentId = 900001,
                    MatchedAtUtc = matched,
                    EnteredAtUtc = matched.AddMinutes(1),
                    EndedAtUtc = matched.AddMinutes(26),
                    DurationMs = 1_500_000,
                    Result = RunResult.Unknown,
                    DetectionConfidence = DetectionConfidence.Low,
                    PendingReview = true,
                    Source = RunSource.AutoNetwork,
                    CreatedAtUtc = matched,
                    UpdatedAtUtc = matched.AddMinutes(26),
                },
                tx);
        });

        return runId;
    }

    /// <summary>
    /// The whole shipping CN profile ends its runs as UNKNOWN + pending review. Recovery must
    /// look at whether a run is still open, never at its result alone, or every finished
    /// mentor roulette is rewritten to INTERRUPTED on the next start.
    /// </summary>
    [Fact]
    public void Restart_LeavesAFinishedUnknownRunAlone_AndStillRecoversTheOpenOne()
    {
        var finished = SeedFinishedUnknownRun();
        var open = SeedUnfinishedRun();

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        Assert.Equal(open, Assert.Single(host.Recovery.RecoveredRunIds));

        var untouched = host.Runs.Get(finished)!;
        Assert.Equal(RunResult.Unknown, untouched.Result);
        Assert.Equal(DetectionConfidence.Low, untouched.DetectionConfidence);
        Assert.True(untouched.PendingReview);
        Assert.Equal(1, untouched.Revision);
        Assert.DoesNotContain(host.Events.ListForRun(finished), item => item.EventType == "PROCESS_RESTART");

        var recovered = host.Runs.Get(open)!;
        Assert.Equal(RunResult.Interrupted, recovered.Result);
        Assert.True(recovered.PendingReview);
    }

    [Fact]
    public void Restart_MarksAnUnfinishedRunInterruptedAndPendingReview()
    {
        var runId = SeedUnfinishedRun();

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        Assert.Equal(1, host.Recovery.RecoveredCount);
        Assert.Equal(runId, Assert.Single(host.Recovery.RecoveredRunIds));

        var run = host.Runs.Get(runId)!;
        Assert.Equal(RunResult.Interrupted, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.True(run.PendingReview);
        Assert.NotNull(run.EndedAtUtc);
        Assert.Equal(2, run.Revision);

        Assert.Equal(1, host.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    /// <summary>
    /// The offline replay tool closes its capture session while leaving the run it was driving
    /// open, as does a process that closes its session row and then dies. Such a run is
    /// unfinished work whatever its session row says.
    /// </summary>
    [Fact]
    public void Restart_RecoversAnOpenRun_EvenWhenItsSessionWasAlreadyClosed()
    {
        var runId = SeedUnfinishedRun(sessionEnded: true);

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        Assert.Equal(runId, Assert.Single(host.Recovery.RecoveredRunIds));
        Assert.Equal(RunResult.Interrupted, host.Runs.Get(runId)!.Result);
    }

    [Fact]
    public void Restart_NeverInfersACompletion()
    {
        var runId = SeedUnfinishedRun();

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        Assert.NotEqual(RunResult.Completed, host.Runs.Get(runId)!.Result);
        Assert.Equal(0, host.Statistics.GetDashboard().CompletedCount);
    }

    [Fact]
    public void Restart_AppendsAProcessRestartEventAndAnAuditRevision()
    {
        var runId = SeedUnfinishedRun();

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        var events = host.Events.ListForRun(runId);
        var marker = Assert.Single(events, item => item.EventType == "PROCESS_RESTART");
        Assert.Equal(RunState.InterruptedPendingReview, marker.ToState);
        Assert.Equal(DetectionConfidence.Low, marker.Confidence);

        var chain = host.Revisions.ListForRun(runId, 1, 50);
        Assert.Equal(2, chain.Total);
        Assert.Equal(RevisionActor.System, chain.Items[1].Actor);
        Assert.False(string.IsNullOrWhiteSpace(chain.Items[1].Reason));
    }

    [Fact]
    public void RestartingTwice_DoesNotReInsertTheMarkerOrRecoverAgain()
    {
        var runId = SeedUnfinishedRun();

        using (var first = CollectorHost.Open(_databasePath, SystemClock.Instance))
        {
            Assert.Equal(1, first.Recovery.RecoveredCount);
        }

        using var second = CollectorHost.Open(_databasePath, SystemClock.Instance);

        // The run is no longer UNKNOWN, so there is nothing left to recover, and the
        // PROCESS_RESTART marker is deduplicated by its event key regardless.
        Assert.Equal(0, second.Recovery.RecoveredCount);
        Assert.Single(second.Events.ListForRun(runId), item => item.EventType == "PROCESS_RESTART");
        Assert.Equal(2, second.Runs.Get(runId)!.Revision);
    }

    [Fact]
    public void Restart_ClosesTheOpenCaptureSession()
    {
        SeedUnfinishedRun();

        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        Assert.Equal(1, host.Recovery.ClosedSessionCount);
        Assert.DoesNotContain(
            host.Database.Read(_ => host.Sessions.ListOpen(null)),
            session => session.CaptureSessionId != host.CaptureSessionId);
    }

    [Fact]
    public void Restart_LeavesACompletedRunAlone()
    {
        var databasePath = Path.Combine(_directory, "completed.db");
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "synthetic-completed-v1.fixture.json");
        FixtureReplayRunner.Run(fixturePath, databasePath);

        using var host = CollectorHost.Open(databasePath, SystemClock.Instance);

        Assert.Equal(0, host.Recovery.RecoveredCount);
        var run = Assert.Single(host.Runs.Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.False(run.PendingReview);
    }

    [Fact]
    public void RecoveredRun_CanOnlyBecomeCompletedThroughAHumanCorrection()
    {
        var runId = SeedUnfinishedRun();
        using var host = CollectorHost.Open(_databasePath, SystemClock.Instance);

        var outcome = host.Mutations.CorrectRun(new Domain.Mutations.CorrectRunCommand(
            Guid.NewGuid().ToString("D"),
            runId,
            2,
            "当时其实打完了，只是程序崩了",
            new Domain.Mutations.RunChangeSet
            {
                Specified = new HashSet<string>(StringComparer.Ordinal)
                {
                    Domain.Mutations.RunFields.Result,
                    Domain.Mutations.RunFields.EndedAtUtc,
                },
                Result = RunResult.Completed,
                EndedAtUtc = new DateTimeOffset(2026, 9, 3, 20, 31, 0, TimeSpan.Zero),
            }));

        var run = host.Runs.Get(runId)!;
        Assert.Equal(3, outcome.Revision);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.True(run.ManuallyCorrected);
        Assert.False(run.PendingReview);
        Assert.Equal(0, host.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    [Fact]
    public void BoundedDedup_StaysBoundedAcrossOneHundredThousandEvents()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Synthetic("synthetic/v1", 42),
            new StateMachineOptions { DedupCapacity = 4096 });

        var session = Guid.NewGuid().ToString("D");
        var start = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 100_000; index++)
        {
            machine.Handle(new TimeoutTick
            {
                Key = new EventKey(session, PacketDirection.None, "TIMEOUT_TICK", index, null, "tick-" + index),
                ObservedAtUtc = start.AddMilliseconds(index),
                Mono = TimeSpan.FromMilliseconds(index),
            });
        }

        Assert.Equal(4096, machine.DedupSetCount);
        Assert.Equal(0, machine.DuplicateCount);

        // Replaying the tail is still recognised as duplicated.
        var duplicate = machine.Handle(new TimeoutTick
        {
            Key = new EventKey(session, PacketDirection.None, "TIMEOUT_TICK", 99_999, null, "tick-99999"),
            ObservedAtUtc = start.AddMilliseconds(99_999),
            Mono = TimeSpan.FromMilliseconds(99_999),
        });

        Assert.True(duplicate.Duplicate);
        Assert.Equal(4096, machine.DedupSetCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a WAL handle briefly; the temp cleaner will get it.
        }
    }
}
