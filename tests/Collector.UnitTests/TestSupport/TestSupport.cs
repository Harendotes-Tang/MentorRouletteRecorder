using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

internal sealed class TestClock(DateTimeOffset utcNow, TimeSpan? elapsed = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public TimeSpan Elapsed { get; set; } = elapsed ?? TimeSpan.Zero;
}

internal sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _directory = new();

    public TestDatabase()
    {
        Path = _directory.Combine("test.db");
        Clock = new TestClock(new DateTimeOffset(2026, 9, 4, 1, 2, 3, 456, TimeSpan.Zero));
        Database = SqliteDatabase.Open(Path, Clock);
    }

    public string Path { get; }

    public TestClock Clock { get; }

    public SqliteDatabase Database { get; private set; }

    public void Reopen()
    {
        Database.Dispose();
        Database = SqliteDatabase.Open(Path, Clock);
    }

    /// <summary>
    /// The current character profile of a region, created when the region has none. Every production
    /// writer files a new run under it; a row a test builds by hand names it the same way when the
    /// row is meant to count towards the achievement. Not for use inside an open transaction.
    /// </summary>
    public long CurrentProfile(Region region = Region.Cn) => Database.RunInTransaction(tx =>
        new CharacterProfileRepository(Database, Clock).EnsureCurrent(region, tx).ProfileId);

    /// <summary>Settings with their defaults written, built on demand so they follow <see cref="Reopen"/>.</summary>
    public SettingsRepository Settings()
    {
        var settings = new SettingsRepository(Database, Clock);
        settings.EnsureDefaults();
        return settings;
    }

    /// <summary>The mutation service over <see cref="Settings"/>, built on demand.</summary>
    public RunMutationService Mutations() => new(Database, Settings(), Clock);

    /// <summary>Stores hand-built runs in one transaction, without revisions.</summary>
    /// <param name="runs">Rows to insert, usually <see cref="Run"/> with changes.</param>
    public void InsertRuns(params MentorRun[] runs)
    {
        var repository = new RunRepository(Database);
        Database.RunInTransaction(tx =>
        {
            foreach (var run in runs)
            {
                repository.Insert(run, tx);
            }
        });
    }

    /// <summary>
    /// Stores runs as their writers leave them, in one transaction: each row with its creation revision 1,
    /// stamped at the row's creation. A manual run is created by the user with a reason; any other run by the
    /// system without one. The run trail's trigger requires that revision for every later change.
    /// </summary>
    /// <param name="runs">Rows to insert.</param>
    public void InsertWithFirstRevision(params MentorRun[] runs)
    {
        var repository = new RunRepository(Database);
        var revisions = new RunRevisionRepository(Database);
        Database.RunInTransaction(tx =>
        {
            foreach (var run in runs)
            {
                var manual = run.Source == RunSource.Manual;
                repository.Insert(run, tx);
                revisions.Append(new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"),
                    RunId = run.RunId,
                    Revision = 1,
                    ChangedAtUtc = run.CreatedAtUtc,
                    ChangeKind = manual ? ChangeKind.CreateManual : ChangeKind.CreateAuto,
                    Actor = manual ? RevisionActor.User : RevisionActor.System,
                    Reason = manual ? "测试初始记录" : null,
                    Changes = Array.Empty<RunFieldChange>(),
                }, tx);
            }
        });
    }

    /// <summary>Stores imported runs in one transaction, each with the import metadata it carries.</summary>
    /// <param name="runs">Rows whose <see cref="MentorRun.ImportMetadata"/> is set.</param>
    public void InsertImported(params MentorRun[] runs)
    {
        var repository = new RunRepository(Database);
        var imports = new RunImportRepository(Database);
        Database.RunInTransaction(tx =>
        {
            foreach (var run in runs)
            {
                repository.Insert(run, tx);
                imports.Insert(run.RunId, run.ImportMetadata!, tx);
            }
        });
    }

    /// <summary>
    /// Opens a capture-session row now, the way a capture start does, and returns its id. No profile is
    /// named and no packet counted yet.
    /// </summary>
    /// <param name="region">Region of the session.</param>
    /// <param name="gameBuild">Build of the client, or null.</param>
    /// <param name="status">Profile status the session starts with.</param>
    public string OpenCaptureSession(
        Region region, string? gameBuild, ProfileStatus status = ProfileStatus.UnsupportedBuild)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessions = new CaptureSessionRepository(Database);
        Database.RunInTransaction(tx => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = sessionId,
            StartedAtUtc = Clock.UtcNow,
            CollectorVersion = "test",
            Region = region,
            GameBuild = gameBuild,
            ProtocolProfileId = null,
            ProfileStatus = status,
            PacketsObserved = 0,
        }, tx));
        return sessionId;
    }

    /// <summary>Rows in one table.</summary>
    /// <param name="table">A table name the test spells out.</param>
    public long Count(string table)
    {
        using var command = Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Runs <paramref name="work"/> while another thread holds the database gate the way a live-capture write
    /// does. The work must finish within 10 s with the gate held, so it may never ask for it.
    /// </summary>
    /// <param name="work">The read that must not wait for the writer.</param>
    public async Task<T> WhileTheWriterGateIsHeldAsync<T>(Func<T> work)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => Database.Read(_ =>
        {
            held.Set();
            release.Wait();
            return 0;
        }));
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            return await Task.Run(work).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Writes 256 bytes of 0xFF over the start of page 2 of a checkpointed database file: a corruption
    /// <c>PRAGMA integrity_check</c> reports. The file must already have at least two pages.
    /// </summary>
    /// <param name="path">The database file.</param>
    public static void OverwriteSecondPage(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.True(file.Length > 4096 * 2, "expected at least two pages after migration");
        file.Position = 4096;
        var junk = new byte[256];
        Array.Fill(junk, (byte)0xFF);
        file.Write(junk);
    }

    public static MentorRun Run(
        string? runId = null,
        RunResult result = RunResult.Completed,
        RunSource source = RunSource.Manual,
        DateTimeOffset? enteredAt = null,
        long? durationMs = 120_000,
        bool contributesToGoal = true,
        bool softDeleted = false,
        int? contentId = 900001,
        int? jobId = 19)
    {
        var entered = enteredAt ?? new DateTimeOffset(2026, 9, 4, 1, 0, 0, 0, TimeSpan.Zero);
        return new MentorRun
        {
            RunId = runId ?? Guid.NewGuid().ToString("D"),
            Revision = 1,
            Region = Region.Cn,
            MentorRouletteId = source == RunSource.Manual ? null : 42,
            ContentId = contentId,
            TerritoryId = contentId is null ? null : 800001,
            DutyName = contentId is null ? null : "样例迷宫挑战 A",
            DutyCategory = contentId is null ? null : "迷宫挑战",
            JobId = jobId,
            JobName = jobId is null ? "未知" : "骑士",
            Role = jobId is null ? Role.Unknown : Role.Tank,
            MatchedAtUtc = entered.AddSeconds(-10),
            EnteredAtUtc = result == RunResult.CancelledBeforeEntry ? null : entered,
            EndedAtUtc = result == RunResult.CancelledBeforeEntry ? null : entered.AddMilliseconds(durationMs ?? 1),
            DurationMs = result == RunResult.CancelledBeforeEntry ? null : durationMs,
            Result = result,
            DetectionConfidence = source == RunSource.Manual ? DetectionConfidence.None : DetectionConfidence.High,
            Source = source,
            ContributesToGoal = contributesToGoal,
            ManuallyCreated = source == RunSource.Manual,
            SoftDeleted = softDeleted,
            CreatedAtUtc = entered.AddMinutes(-1),
            UpdatedAtUtc = entered.AddMinutes(-1),
        };
    }

    /// <summary>
    /// A valid "补录一次导随" command: CN, duty 900001, job 19, counted, matched 31 and entered 30 minutes
    /// before it ended. A run cancelled before entry has neither an entry nor an end.
    /// </summary>
    /// <param name="result">Result of the run.</param>
    /// <param name="endedAt">End of the run; 2026-09-03 10:31 UTC when null.</param>
    public static CreateManualRunCommand ManualRun(RunResult result = RunResult.Completed, DateTimeOffset? endedAt = null)
    {
        var ended = endedAt ?? new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero);
        var entered = result != RunResult.CancelledBeforeEntry;
        return new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Reason = "补录一次导随",
            Region = Region.Cn,
            Result = result,
            ContentId = 900001,
            JobId = 19,
            ContributesToGoal = true,
            MatchedAtUtc = ended.AddMinutes(-31),
            EnteredAtUtc = entered ? ended.AddMinutes(-30) : null,
            EndedAtUtc = entered ? ended : null,
        };
    }

    public void Dispose()
    {
        Database.Dispose();
        _directory.Dispose();
    }
}
