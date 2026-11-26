using System.Diagnostics;
using System.IO.Pipes;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The Collector process's lifecycle, from the second adversarial review: the single-instance
/// pipe gate, the pid file, the stop event and subsystem disposal.
/// </summary>
public sealed class ProgramLifecycleTests
{
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

        var failure = CollectorHost.RunShutdownSteps(
            new (string, Action)[]
            {
                ("validation_dispose", () => throw boom),
                ("capture_dispose", () => captureDisposed = true),
            },
            (record, error) => records.Add(record));

        Assert.Same(boom, failure);
        Assert.True(captureDisposed, "capture must be released even when validation refuses to be");
        Assert.Contains("validation_dispose_failed", records);
    }

    /// <summary>The first failure is the one reported when several steps refuse; every later step still runs.</summary>
    [Fact]
    public void BothSubsystemsFailingReportsTheFirstAndStillReleasesTheSecond()
    {
        var first = new InvalidOperationException("validation");
        var second = new TimeoutException("capture");
        var records = new List<string>();
        var updatesDisposed = false;

        var failure = CollectorHost.RunShutdownSteps(
            new (string, Action)[]
            {
                ("validation_dispose", () => throw first),
                ("capture_dispose", () => throw second),
                ("update_download_dispose", () => updatesDisposed = true),
            },
            (record, error) => records.Add(record));

        Assert.Same(first, failure);
        Assert.Equal(new[] { "validation_dispose_failed", "capture_dispose_timed_out" }, records);
        Assert.True(updatesDisposed, "a refusing capture must not keep the update download from being released");
    }

    /// <summary>
    /// Capture is torn down right after the evidence round and before the update download and check, which can
    /// each wait up to five seconds: once the round has released the capture lease, the automatic-recording timer
    /// would otherwise start a live capture while the host is shutting down (re-review 2026-11-20, R1-1).
    /// </summary>
    [Fact]
    public void ShutdownStopsTheRoundFirstAndDisposesCaptureBeforeTheUpdateWaits()
    {
        using var temp = new TempDirectory("Shutdown");
        using var logger = new RotatingFileLogger(temp.Path, enabled: false);
        using var host = CollectorHost.Open(temp.Combine("shutdown.db"), logger: logger);
        var order = host.ShutdownSteps().Select(step => step.Record).ToList();

        Assert.Equal("validation_stop", order[0]);
        Assert.Equal(order.IndexOf("validation_dispose") + 1, order.IndexOf("capture_dispose"));
        Assert.True(order.IndexOf("capture_dispose") < order.IndexOf("update_download_dispose"));
        Assert.True(order.IndexOf("capture_dispose") < order.IndexOf("update_check_dispose"));
        Assert.True(order.IndexOf("shared_calibration_stop") < order.IndexOf("capture_dispose"));
    }

    /// <summary>
    /// Audit 2026-11-26, Gb-4: emptying the expired part of the recycle bin at startup is maintenance. When it fails
    /// the host still opens - with the record still in the recycle bin for the hourly check to try again - instead of
    /// refusing to start with a sentence that says the database cannot be opened.
    /// </summary>
    [Fact]
    public void AFailingStartupPurgeOfTheRecycleBinStillOpensTheHost()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "startup-purge.db");
        string runId;
        using (var host = CollectorHost.Open(path, fixture.Clock, capture: InertCapture.Services()))
        {
            var run = host.Mutations.CreateManualRun(new CreateManualRunCommand
            {
                RequestId = Guid.NewGuid().ToString("D"), Reason = "补录一次导随", Result = RunResult.Completed, JobId = 19,
                EnteredAtUtc = fixture.Clock.UtcNow.AddHours(-1), EndedAtUtc = fixture.Clock.UtcNow.AddMinutes(-30),
            }).Run!;
            host.Mutations.SoftDeleteRun(new RunReasonCommand(Guid.NewGuid().ToString("D"), run.RunId, run.Revision, "删除"));
            runId = run.RunId;
            using var trigger = host.Database.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_startup_purge BEFORE DELETE ON mentor_runs WHEN OLD.run_id='" + runId +
                "' BEGIN SELECT RAISE(ABORT,'synthetic purge failure'); END;";
            trigger.ExecuteNonQuery();
        }

        // Past the 30-day recycle bin: the next start tries to purge the record and fails.
        fixture.Clock.UtcNow += TimeSpan.FromDays(31);
        using var reopened = CollectorHost.Open(path, fixture.Clock, capture: InertCapture.Services());
        Assert.True(reopened.Runs.Get(runId)!.SoftDeleted);
    }

    private static string TestPipeName() =>
        "MentorRecorder.test-" + Guid.NewGuid().ToString("N") + ".v1";
}
