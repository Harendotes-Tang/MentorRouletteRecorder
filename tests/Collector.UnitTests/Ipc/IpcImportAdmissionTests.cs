using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

public sealed class IpcImportAdmissionTests
{
    [Fact]
    public async Task ImportAdmissionIsSharedAcrossConnectionsAndCancellationPreservesCachedPreviews()
    {
        using var fixture = new TestDatabase();
        using var clock = new PreviewClock(fixture.Clock.UtcNow);
        using var host = OpenHost(fixture, clock);
        var dispatcher = new MessageDispatcher(host);
        var previews = Enumerable.Range(0, RunImportService.MaxPreviews)
            .Select(index => host.Imports.PreviewSource("ROWS", rows: Rows("retained-" + index))).ToArray();
        var commit = CommitRequest(previews[0]);
        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(90));
        // A WAL preview no longer waits behind the writer. Pause the admitted preview at
        // its normalization timestamp so the other connection always sees an occupied slot.
        clock.PauseNextRead();

        using var firstStream = new ScriptedPipePeer(PreviewRequest("cancelled-preview"));
        using var firstLifetime = new CancellationTokenSource();
        var firstConnection = new PipeConnection(firstStream, dispatcher).ServeAsync(firstLifetime.Token);
        using var secondStream = new ScriptedPipePeer(
            Enumerable.Range(0, 30).Select(index => PreviewRequest("busy-" + index))
                .Append(commit).Append(Request("GetVersion", new JsonObject())).ToArray());
        using var secondLifetime = new CancellationTokenSource();
        Task secondConnection = Task.CompletedTask;
        try
        {
            await clock.Paused.WaitAsync(TimeSpan.FromSeconds(90));
            secondConnection = new PipeConnection(secondStream, dispatcher).ServeAsync(secondLifetime.Token);
            var replies = new List<JsonObject>();
            for (var index = 0; index < 32; index++)
                // Bounded, not tight: on a loaded machine (the full gate builds C++ in parallel)
                // a refusal can take several seconds to be written; a hang is still caught.
                replies.Add(await secondStream.NextWrittenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90)));

            var refusals = replies.Where(reply => reply["ok"]!.GetValue<bool>() == false).ToArray();
            Assert.Equal(31, refusals.Length);
            Assert.All(refusals, reply =>
            {
                Assert.Equal(ErrorCodes.DbBusy, reply["error"]!["code"]!.GetValue<string>());
                Assert.True(reply["error"]!["retryable"]!.GetValue<bool>());
            });
            Assert.Equal("GetVersion", Assert.Single(replies, reply => reply["ok"]!.GetValue<bool>())["message_type"]!.GetValue<string>());

            await firstLifetime.CancelAsync();
            clock.Resume();
            // The connection token is cancelled before normalization can proceed to cache admission.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstConnection.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, firstStream.WrittenResponses);
            Assert.False(blocker.Finished);
        }
        finally
        {
            await firstLifetime.CancelAsync();
            clock.Resume();
            await secondLifetime.CancelAsync();
            await blocker.DisposeAsync();
            await Drain(firstConnection);
            await Drain(secondConnection);
        }

        // A cancelled preview must not evict a usable preview from the bounded cache.
        var response = await dispatcher.DispatchAsync(commit, CancellationToken.None);
        Assert.Equal(1, response["imported_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACancelledCommitWaitingForTheDatabaseLeavesItsPreviewAndIdentityUsable()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var dispatcher = new MessageDispatcher(host);
        var preview = host.Imports.PreviewSource("ROWS", rows: Rows("cancelled-commit"));
        var request = CommitRequest(preview);
        using var cancellation = new CancellationTokenSource();
        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(90));
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(dispatcher.DispatchAsync(request, cancellation.Token).GetAwaiter().GetResult()); }
            catch (Exception failure) { completion.TrySetException(failure); }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((worker.ThreadState & ThreadState.WaitSleepJoin) == 0 && !completion.Task.IsCompleted)
                await Task.Delay(10, deadline.Token);
            Assert.False(completion.Task.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => completion.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(blocker.Finished);
        }
        finally
        {
            await cancellation.CancelAsync();
            await blocker.DisposeAsync();
            await Drain(completion.Task);
        }
        AssertImportTables(host.Database, rows: 0, receipts: 0);
        var committed = RunImportHandlers.Commit(host, request.RequestId, request.Payload);
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        Assert.True(RunImportHandlers.Commit(host, request.RequestId, request.Payload)["replayed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("mentor_runs")]
    [InlineData("ipc_idempotency")]
    public async Task CancellationBeforeTransactionCommitRollsBackEveryImportWrite(string cancellationTable)
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var dispatcher = new MessageDispatcher(host);
        var preview = host.Imports.PreviewSource("ROWS", rows: new JsonArray(Row("atomic-first"), Row("atomic-second")));
        var request = CommitRequest(preview, 1, 2);
        using var cancellation = new CancellationTokenSource();
        using var subscription = host.LiveEvents.Subscribe(Guid.NewGuid().ToString("D"));
        host.Database.Read(connection =>
        {
            connection.CreateFunction("cancel_import", () => { cancellation.Cancel(); return 0; });
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER cancel_import_write AFTER INSERT ON " + cancellationTable +
                " BEGIN SELECT cancel_import(); END;";
            command.ExecuteNonQuery();
            return 0;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(request, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        AssertImportTables(host.Database, rows: 0, receipts: 0);
        Assert.Null(await subscription.ReadAsync(TimeSpan.Zero, CancellationToken.None));
        host.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER cancel_import_write;";
            command.ExecuteNonQuery();
            return 0;
        });
        var committed = await dispatcher.DispatchAsync(request, CancellationToken.None);
        Assert.Equal(2, committed["imported_count"]!.GetValue<int>());
        AssertImportTables(host.Database, rows: 2, receipts: 1);
        Assert.True((await dispatcher.DispatchAsync(request, CancellationToken.None))["replayed"]!.GetValue<bool>());
        AssertImportTables(host.Database, rows: 2, receipts: 1);
    }

    [Fact]
    public async Task CancellationAfterSuccessfulCommitKeepsTheReceiptWhenThePeerNeverReceivesTheResponse()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var request = CommitRequest(host.Imports.PreviewSource("ROWS", rows: Rows("committed")));
        var dispatcher = new MessageDispatcher(host);
        using var cancellation = new CancellationTokenSource();
        using var stream = new ScriptedPipePeer(request) { BeforeWrite = cancellation.Cancel };
        var serving = new PipeConnection(stream, dispatcher).ServeAsync(cancellation.Token);
        // The response is attempted only after the SQLite commit has succeeded. Disconnect
        // at that point, before the peer gets any bytes of the successful response.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => serving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, stream.WrittenResponses);
        var replay = await dispatcher.DispatchAsync(request, CancellationToken.None);
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(1, replay["imported_count"]!.GetValue<int>());
        AssertImportTables(host.Database, rows: 1, receipts: 1);
    }

    /// <summary>
    /// Audit 2026-11-26, Gb-2: the request id is looked up before the import slot refuses. A retry of a
    /// commit that already finished replays its answer while another import holds the slot; a retry of the
    /// commit still running under its id is refused as busy and retryable, and that commit goes on.
    /// </summary>
    [Fact]
    public async Task ARetryOfAFinishedCommitReplaysWhileAnotherImportHoldsTheSlot()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var dispatcher = new MessageDispatcher(host);
        var finished = CommitRequest(host.Imports.PreviewSource("ROWS", rows: Rows("finished-commit")));
        Assert.Equal(1, (await dispatcher.DispatchAsync(finished, CancellationToken.None))["imported_count"]!.GetValue<int>());
        var running = CommitRequest(host.Imports.PreviewSource("ROWS", rows: Rows("running-commit")));
        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(90));
        Task<JsonObject>? first = null;
        Task<JsonObject>? replay = null;
        try
        {
            // Takes the one import slot, then waits for the database.
            first = dispatcher.DispatchAsync(running, CancellationToken.None);
            var busy = await Assert.ThrowsAsync<CollectorException>(
                () => dispatcher.DispatchAsync(running, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)));
            Assert.Equal(ErrorCodes.DbBusy, busy.Code);
            Assert.True(busy.Retryable);
            replay = dispatcher.DispatchAsync(finished, CancellationToken.None);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        var replayed = await replay!.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(replayed["replayed"]!.GetValue<bool>());
        Assert.Equal(1, replayed["imported_count"]!.GetValue<int>());
        var committed = await first!.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.False(committed["replayed"]!.GetValue<bool>());
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        AssertImportTables(host.Database, rows: 2, receipts: 2);
    }

    /// <summary>
    /// Audit 2026-11-26, RB-3: the lookup also finds a finished commit by its permanent import receipt. A retry
    /// arriving after the 24-hour response body was pruned, while another import holds the slot, still replays.
    /// </summary>
    [Fact]
    public async Task ARetryOfACommitWhoseResponseWasPrunedReplaysFromItsImportReceipt()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var dispatcher = new MessageDispatcher(host);
        var finished = CommitRequest(host.Imports.PreviewSource("ROWS", rows: Rows("pruned-commit")));
        Assert.Equal(1, (await dispatcher.DispatchAsync(finished, CancellationToken.None))["imported_count"]!.GetValue<int>());
        // What IdempotencyRepository.Prune does to a CommitRunImport response once it is a day old.
        host.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ipc_idempotency WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", finished.RequestId);
            Assert.Equal(1, command.ExecuteNonQuery());
            return 0;
        });
        var running = CommitRequest(host.Imports.PreviewSource("ROWS", rows: Rows("running-commit")));
        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(90));
        Task<JsonObject>? first = null;
        Task<JsonObject>? replay = null;
        try
        {
            first = dispatcher.DispatchAsync(running, CancellationToken.None);
            replay = dispatcher.DispatchAsync(finished, CancellationToken.None);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        var replayed = await replay!.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(replayed["replayed"]!.GetValue<bool>());
        Assert.Equal(1, replayed["imported_count"]!.GetValue<int>());
        Assert.False((await first!.WaitAsync(TimeSpan.FromSeconds(90)))["replayed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PreCancellationAndMalformedRequestsDoNotKeepTheImportSlot()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture, fixture.Clock);
        var dispatcher = new MessageDispatcher(host);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(PreviewRequest("pre-cancelled"), cancellation.Token));
        await Assert.ThrowsAsync<CollectorException>(
            () => dispatcher.DispatchAsync(Request("PreviewRunImport", new JsonObject()), CancellationToken.None));
        Assert.NotNull((await dispatcher.DispatchAsync(PreviewRequest("after-failure"), CancellationToken.None))["preview_id"]);
    }

    private static CollectorHost OpenHost(TestDatabase fixture, IClock clock) => CollectorHost.Open(
        Path.Combine(Path.GetDirectoryName(fixture.Path)!, "admission.db"), clock,
        capture: InertCapture.Services());

    private static JsonObject Row(string name) => new()
    {
        ["duty_name"] = name, ["result"] = "UNKNOWN", ["reflection_text"] = "合成导入并发回归",
    };
    private static JsonArray Rows(string name) => new(Row(name));
    private static IpcRequest Request(string messageType, JsonObject payload) =>
        new(Guid.NewGuid().ToString("D"), messageType, payload);
    private static IpcRequest PreviewRequest(string name) =>
        Request("PreviewRunImport", new JsonObject { ["source_kind"] = "ROWS", ["rows"] = Rows(name) });
    private static IpcRequest CommitRequest(JsonObject preview, params int[] rowNumbers) => Request("CommitRunImport", new JsonObject
    {
        ["preview_id"] = preview["preview_id"]!.DeepClone(), ["confirm_own_records"] = true,
        ["row_numbers"] = new JsonArray((rowNumbers.Length == 0 ? new[] { 1 } : rowNumbers).Select(number => (JsonNode)JsonValue.Create(number)!).ToArray()),
    });

    private static void AssertImportTables(SqliteDatabase database, long rows, long receipts)
    {
        foreach (var table in new[] { "mentor_runs", "run_import_metadata", "run_reflections", "run_revisions", "run_import_batches", "ipc_idempotency" })
        {
            var count = database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM " + table;
                return (long)command.ExecuteScalar()!;
            });
            Assert.Equal(table is "run_import_batches" or "ipc_idempotency" ? receipts : rows, count);
        }
    }

    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (OperationCanceledException) { }
    }

    private sealed class PreviewClock(DateTimeOffset utcNow) : IClock, IDisposable
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _resume = new();
        private int _pauseNextRead;
        public Task Paused => _paused.Task;
        public void PauseNextRead() => Interlocked.Exchange(ref _pauseNextRead, 1);
        public void Resume() => _resume.Set();
        public void Dispose() => _resume.Dispose();
        public DateTimeOffset UtcNow
        {
            get
            {
                if (Interlocked.Exchange(ref _pauseNextRead, 0) == 1)
                {
                    _paused.SetResult();
                    if (!_resume.Wait(TimeSpan.FromSeconds(90)))
                        throw new TimeoutException("Test did not release the admitted preview.");
                }
                return utcNow;
            }
        }
        public TimeSpan Elapsed => TimeSpan.Zero;
    }
}
