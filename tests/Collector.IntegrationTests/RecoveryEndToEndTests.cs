using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Recovery;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Crash recovery from the outside in: a real Collector process leaves a run unfinished, a
/// real Collector process starts again over the same database, and a real client asks it
/// what happened.
///
/// <see cref="CrashRecoveryTests"/> covers the rule in process. This file adds what only a
/// second process can show: the state survives a process boundary, a client connecting to the
/// restarted server is told the truth, and a further restart changes nothing.
///
/// The unfinished run is produced by driving the shipping <c>--replay-decoded</c> mode over the
/// <c>synthetic_offset_oob</c> fixture, which stops mid-duty by design: the field that would end
/// the run reads past the end of its payload, the parser refuses it, and the run stays in
/// ENTERED_DUTY with <c>result = UNKNOWN</c>. That is a real process exiting with real
/// unfinished work rather than a hand-written row.
/// </summary>
[Collection(CollectorProcessCollection.Name)]
public sealed class RecoveryEndToEndTests : IDisposable
{
    private readonly CollectorProcessFixture _collector;
    private readonly string _directory;

    public RecoveryEndToEndTests(CollectorProcessFixture collector)
    {
        _collector = collector;
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.RecoveryE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task ARunLeftUnfinishedByADeadProcessComesBackAsInterruptedAndPendingReview()
    {
        var databasePath = Path.Combine(_directory, "recovery.db");
        var runId = SeedUnfinishedRunInAnotherProcess(databasePath);

        // ---- first restart: the recovery pass runs before the pipe is even created -------
        await using var first = await _collector.ServeAsync(databasePath);
        Assert.Equal(1, first.RecoveredRuns);

        await using (var client = await first.ConnectAsync())
        {
            var rows = (await client.SendAsync("QueryRuns", new JsonObject())).Require()["items"]!
                .AsArray();
            var run = Assert.Single(rows)!.AsObject();

            Assert.Equal(runId, run["run_id"]!.GetValue<string>());
            Assert.Equal("INTERRUPTED", run["result"]!.GetValue<string>());
            Assert.True(run["pending_review"]!.GetValue<bool>());
            Assert.Equal("LOW", run["detection_confidence"]!.GetValue<string>());
            Assert.Equal(2, run["revision"]!.GetValue<int>());

            // Recovery never infers a completion.
            Assert.NotEqual("COMPLETED", run["result"]!.GetValue<string>());
        }

        await first.KillAsync();

        // ---- second restart: nothing left to recover, and nothing changes ---------------
        await using var second = await _collector.ServeAsync(databasePath);
        Assert.Equal(0, second.RecoveredRuns);

        await using (var client = await second.ConnectAsync())
        {
            var rows = (await client.SendAsync("QueryRuns", new JsonObject())).Require()["items"]!
                .AsArray();
            var run = Assert.Single(rows)!.AsObject();

            Assert.Equal("INTERRUPTED", run["result"]!.GetValue<string>());
            Assert.True(run["pending_review"]!.GetValue<bool>());

            // Still revision 2: a second restart must not append a second correction.
            Assert.Equal(2, run["revision"]!.GetValue<int>());

            var revisions = (await client.SendAsync(
                    "GetRunRevisions", new JsonObject { ["run_id"] = runId }))
                .Require();
            Assert.Equal(2, revisions["page_info"]!["total"]!.GetValue<int>());
        }

        await second.KillAsync();

        // The PROCESS_RESTART marker carries a stable event key, so two restarts leave one
        // event, not two. Read it from the file once both servers are gone.
        using var database = SqliteDatabase.Open(databasePath, SystemClock.Instance);
        var trail = new RunEventRepository(database).ListForRun(runId);
        Assert.Single(trail, item => item.EventType == CrashRecoveryService.EventType);
        Assert.Equal(
            RunState.InterruptedPendingReview,
            Assert.Single(trail, item => item.EventType == CrashRecoveryService.EventType).ToState);
    }

    /// <summary>
    /// A client that subscribes <em>after</em> the Collector has started still learns about the
    /// runs the startup recovery pass repaired.
    ///
    /// <c>CollectorHost.Open</c> runs recovery before <c>PipeServer</c> exists, so no client can
    /// be subscribed when those events are published. <c>LiveEventBus</c> retains its most recent
    /// events and replays them into each new subscription; without that the Desktop would only
    /// find a recovered run by re-querying. The subscriber here arrives late on purpose.
    /// </summary>
    [Fact]
    public async Task ARecoveredRunReachesAClientThatSubscribesAfterStartup()
    {
        var databasePath = Path.Combine(_directory, "live.db");
        var runId = SeedUnfinishedRunInAnotherProcess(databasePath);

        await using var serving = await _collector.ServeAsync(databasePath);
        Assert.Equal(1, serving.RecoveredRuns);

        // Connecting only now: the recovery pass finished before this process had a pipe at
        // all, let alone a client on it.
        await using var subscriber = await serving.ConnectAsync();
        var events = new List<JsonObject>();
        using var received = new SemaphoreSlim(0);
        subscriber.EventReceived += payload =>
        {
            lock (events)
            {
                events.Add(payload);
            }

            received.Release();
        };

        var acknowledgement =
            (await subscriber.SendAsync("SubscribeLiveEvents", new JsonObject())).Require();
        Assert.True(Guid.TryParseExact(
            acknowledgement["subscription_id"]!.GetValue<string>(), "D", out _));

        // Two events: the corrected run, and the hint that cached statistics are now stale.
        Assert.True(
            await received.WaitAsync(TimeSpan.FromSeconds(15)),
            "the recovered run never reached the subscriber");
        Assert.True(
            await received.WaitAsync(TimeSpan.FromSeconds(15)),
            "the statistics-invalidated hint never reached the subscriber");

        List<JsonObject> delivered;
        lock (events)
        {
            delivered = events.ToList();
        }

        var updated = Assert.Single(
            delivered, payload => payload["kind"]!.GetValue<string>() == "run_updated");
        Assert.Equal("RunUpdated", updated["event_type"]!.GetValue<string>());
        Assert.Equal(runId, updated["run"]!["run_id"]!.GetValue<string>());
        Assert.Equal("INTERRUPTED", updated["run"]!["result"]!.GetValue<string>());
        Assert.True(updated["run"]!["pending_review"]!.GetValue<bool>());
        Assert.Equal("LOW", updated["run"]!["detection_confidence"]!.GetValue<string>());

        var invalidated = Assert.Single(
            delivered, payload => payload["kind"]!.GetValue<string>() == "stats_invalidated");
        Assert.False(string.IsNullOrWhiteSpace(invalidated["message"]!.GetValue<string>()));

        // Replayed events are ordinary events and carry no marker: $defs/LiveEvent is
        // additionalProperties:false in v1, so there is nowhere legal to put one. They do carry
        // an unbroken sequence, which is how a client distinguishes a replay from a gap.
        var sequences = delivered
            .Select(payload => payload["sequence"]!.GetValue<long>())
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(sequences[0] + sequences.Length - 1, sequences[^1]);

        await serving.KillAsync();
    }

    /// <summary>
    /// Runs the real Collector executable in <c>--replay-decoded</c> mode over a fixture that
    /// stops mid-duty, and returns the id of the run it left behind.
    /// </summary>
    private string SeedUnfinishedRunInAnotherProcess(string databasePath)
    {
        var result = CollectorProcessFixture.RunToCompletion(
            "--replay-decoded",
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "decoded", "synthetic_offset_oob.decoded.json"),
            "--profile",
            Path.Combine(AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json"),
            "--db",
            databasePath);

        Assert.Equal(0, result.ExitCode);

        using var database = SqliteDatabase.Open(databasePath, SystemClock.Instance);
        var runs = new RunRepository(database);
        var run = Assert.Single(runs.Query(null, null, 1, 50).Items);

        // Precondition: the exited process really did leave an unfinished run.
        Assert.Equal(RunResult.Unknown, run.Result);
        Assert.False(run.PendingReview);
        Assert.NotNull(run.CaptureSessionId);
        return run.RunId;
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
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
