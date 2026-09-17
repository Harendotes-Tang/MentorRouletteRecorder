using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

internal sealed class TestClock(DateTimeOffset utcNow, TimeSpan? elapsed = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public TimeSpan Elapsed { get; set; } = elapsed ?? TimeSpan.Zero;
}

internal sealed class TestDatabase : IDisposable
{
    private readonly string _directory;

    public TestDatabase()
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MentorRecorder.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "test.db");
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

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can briefly retain a WAL handle after a failed test. The temp folder is
            // process-scoped test debris and is safe to leave for the OS temp cleaner.
        }
    }
}
