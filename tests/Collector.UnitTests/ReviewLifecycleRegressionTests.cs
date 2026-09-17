using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Lifecycle regressions from the second adversarial review: database busy budgets, the
/// single-instance pipe gate, the pid file, the stop event, the parent watchdog and subsystem
/// disposal.
/// </summary>
public sealed class ReviewLifecycleRegressionTests
{
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

    /// <summary>
    /// <c>WaitNamedPipe</c> answers three different questions and the gate must keep them
    /// apart. "Busy" is a healthy Collector serving somebody else, and probing it can only ever
    /// fail (review finding R-3).
    /// </summary>
    [Fact]
    public async Task ThePipeGateTellsAbsentFromServedFromBusy()
    {
        var name = TestPipeName();
        Assert.Equal(Program.PipePresence.Absent, Program.ProbePipe(name));

        using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.Equal(Program.PipePresence.Present, Program.ProbePipe(name));

        var accepted = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await client.ConnectAsync(5_000);
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));

        // The only instance is occupied. That is still somebody serving the name.
        Assert.Equal(Program.PipePresence.Busy, Program.ProbePipe(name));
    }

    /// <summary>
    /// A lease held with no pipe is a Collector that is still opening its database or already
    /// closing it, and a busy pipe is one serving another client. Reporting either as "hung"
    /// makes the Desktop kill the process, recreating the orphan the graceful stop exists to
    /// prevent (review finding R-3).
    /// </summary>
    [Theory]
    [InlineData("lease")]
    [InlineData("pipe")]
    public void AHolderThatCannotBeProbedIsReuseRatherThanHung(string gate)
    {
        var name = TestPipeName();

        foreach (var presence in new[] { Program.PipePresence.Absent, Program.PipePresence.Busy })
        {
            var refusal = Program.AlreadyRunningOrHung(
                RotatingFileLogger.Disabled, gate, name, presence);

            var collector = Assert.IsType<CollectorException>(refusal);
            Assert.Equal(ErrorCodes.AlreadyRunning, collector.Code);
            Assert.Contains("复用", collector.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Only a pipe that exists and will accept a connection can be called hung, and only after
    /// the holder has been given several seconds to answer: one second is shorter than an
    /// integrity check on a large database (review finding R-3).
    /// </summary>
    [Fact]
    public void AServedButSilentPipeIsCalledHungOnlyAfterSecondsOfRetrying()
    {
        var name = TestPipeName();
        using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var elapsed = Stopwatch.StartNew();
        var refusal = Program.AlreadyRunningOrHung(
            RotatingFileLogger.Disabled, "pipe", name, Program.PipePresence.Present);
        elapsed.Stop();

        Assert.IsNotType<CollectorException>(refusal);
        Assert.Contains("没有响应", refusal.Message, StringComparison.Ordinal);
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromSeconds(2),
            "the holder must be given seconds, not one attempt; took " + elapsed.Elapsed);
    }

    /// <summary>
    /// A development Collector on its own pipe must not overwrite or delete the pid file of the
    /// Collector the user is actually running. The default per-user pipe keeps the bare name
    /// the Desktop reads (review finding R-10).
    /// </summary>
    [Fact]
    public void ThePidFileIsKeyedByPipeNameExceptForTheDefaultPerUserPipe()
    {
        Assert.Equal(
            Program.ServePidFileName,
            Program.ServePidFileNameFor(PipeNaming.CurrentUserPipeName()));

        Assert.Equal(
            "serve.MentorRecorder.harness-1.v1.pid",
            Program.ServePidFileNameFor("MentorRecorder.harness-1.v1"));

        // Anything that is not a plain name component becomes an underscore; the file name is
        // never allowed to become a path.
        Assert.Equal("serve.a_b_c.pid", Program.ServePidFileNameFor("a:b|c"));
    }

    /// <summary>
    /// The stop-event callback runs on a pool thread, and an unhandled exception there ends the
    /// process with 0xE0434352 instead of exiting cleanly. Disposal must also wait for a
    /// callback that is already running (review finding R-11).
    /// </summary>
    [Fact]
    public void SettingTheStopEventAfterTheSourceIsGoneNeitherThrowsNorCrashes()
    {
        var pipeName = TestPipeName();
        var stopping = new CancellationTokenSource();
        var subscription = Program.OpenStopRequest(pipeName, stopping, RotatingFileLogger.Disabled);
        try
        {
            stopping.Dispose();

            using (var handle = EventWaitHandle.OpenExisting(Program.StopEventName(pipeName)))
            {
                handle.Set();
            }

            // Give the pool callback time to run against the disposed source. An unhandled
            // ObjectDisposedException here would take the test host with it.
            Thread.Sleep(300);
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// Inside a daylight-saving fall-back one local reading stands for two instants an hour
    /// apart, and every conversion back to UTC picks standard time. A parent started during the
    /// daylight half of that hour then reads an hour late, the tolerance rejects it, and the
    /// watchdog stops watching, leaving the orphaned Collector it exists to prevent
    /// (review findings L-5 and R-12).
    /// </summary>
    [Fact]
    public void AParentStartedInsideARepeatedHourIsStillRecognised()
    {
        var zone = FallBackZone();
        var wall = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        Assert.True(zone.IsAmbiguousTime(wall));

        var candidates = ParentProcessWatchdog.CandidateInstants(wall, zone);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(TimeSpan.FromHours(1), (candidates[0] - candidates[1]).Duration());

        foreach (var candidate in candidates)
        {
            Assert.True(
                ParentProcessWatchdog.StartTimeMatches(wall, candidate, zone),
                "both readings of the repeated hour are the same process");
        }

        // A genuinely different instant is still a mismatch, so recycling detection stands.
        Assert.False(ParentProcessWatchdog.StartTimeMatches(wall, candidates[0].AddHours(3), zone));
    }

    /// <summary>An unambiguous reading resolves to exactly one instant.</summary>
    [Fact]
    public void AnOrdinaryStartTimeHasOneCandidateInstant()
    {
        var zone = FallBackZone();
        var wall = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var candidate = Assert.Single(ParentProcessWatchdog.CandidateInstants(wall, zone));

        Assert.Equal(new DateTimeOffset(wall, TimeSpan.FromHours(-4)).ToUniversalTime(), candidate);
        Assert.False(ParentProcessWatchdog.StartTimeMatches(wall, candidate.AddMinutes(5), zone));
    }

    /// <summary>
    /// Validation and capture must not share one try: a validation controller that throws on
    /// the way out would take the capture lease and the capture source with it, the same leak
    /// as review finding L-3 one layer down (review finding R-15).
    /// </summary>
    [Fact]
    public void AThrowingValidationShutdownStillDisposesCapture()
    {
        var boom = new InvalidOperationException("validation refused to stop");
        var captureDisposed = false;
        var records = new List<string>();

        var failure = CollectorHost.DisposeSubsystems(
            new ThrowingDisposable(boom),
            new ActionDisposable(() => captureDisposed = true),
            (record, error) => records.Add(record));

        Assert.Same(boom, failure);
        Assert.True(captureDisposed, "capture must be released even when validation refuses to be");
        Assert.Contains("validation_dispose_failed", records);
    }

    /// <summary>The first failure is the one reported when both subsystems refuse.</summary>
    [Fact]
    public void BothSubsystemsFailingReportsTheFirstAndStillReleasesTheSecond()
    {
        var first = new InvalidOperationException("validation");
        var second = new TimeoutException("capture");
        var records = new List<string>();

        var failure = CollectorHost.DisposeSubsystems(
            new ThrowingDisposable(first), new ThrowingDisposable(second),
            (record, error) => records.Add(record));

        Assert.Same(first, failure);
        Assert.Equal(new[] { "validation_dispose_failed", "capture_dispose_timed_out" }, records);
    }

    private static void WriteSetting(TestDatabase fixture)
    {
        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "INSERT INTO settings(key, value_json, updated_at_utc) VALUES('review.r1', '1', '2026-09-09T00:00:00.000Z') " +
            "ON CONFLICT(key) DO UPDATE SET value_json = '1';";
        command.ExecuteNonQuery();
    }

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

    private static string TestPipeName() =>
        "MentorRecorder.test-" + Guid.NewGuid().ToString("N") + ".v1";

    /// <summary>A zone with an ordinary northern-hemisphere fall-back, declared rather than read.</summary>
    private static TimeZoneInfo FallBackZone() => TimeZoneInfo.CreateCustomTimeZone(
        "MentorRecorder.Test",
        TimeSpan.FromHours(-5),
        "MentorRecorder Test",
        "MR Standard",
        "MR Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    private sealed class ThrowingDisposable(Exception error) : IDisposable
    {
        public void Dispose() => throw error;
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
