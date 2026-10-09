using System.Text.Json.Nodes;
using System.Threading.Channels;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

public sealed class IpcImportAdmissionTests
{
    [Fact]
    public async Task ImportAdmissionIsSharedAcrossConnectionsAndCancellationReleasesDatabaseWait()
    {
        using var fixture = new TestDatabase();
        var clock = new PreviewClock(fixture.Clock.UtcNow);
        using var host = OpenHost(fixture, clock);
        var dispatcher = new MessageDispatcher(host);
        var previews = Enumerable.Range(0, RunImportService.MaxPreviews)
            .Select(index => host.Imports.PreviewSource("ROWS", rows: Rows("retained-" + index))).ToArray();
        var commit = CommitRequest(previews[0]);
        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Observe();

        using var firstStream = new RequestStream(PreviewRequest("cancelled-preview"));
        using var firstLifetime = new CancellationTokenSource();
        var firstConnection = new PipeConnection(firstStream, dispatcher).ServeAsync(firstLifetime.Token);
        using var secondStream = new RequestStream(
            Enumerable.Range(0, 30).Select(index => PreviewRequest("busy-" + index))
                .Append(commit).Append(Request("GetVersion", new JsonObject())).ToArray());
        using var secondLifetime = new CancellationTokenSource();
        Task secondConnection = Task.CompletedTask;
        try
        {
            await clock.FirstObservedRead.WaitAsync(TimeSpan.FromSeconds(5));
            secondConnection = new PipeConnection(secondStream, dispatcher).ServeAsync(secondLifetime.Token);
            var replies = new List<JsonObject>();
            for (var index = 0; index < 32; index++)
                replies.Add(await secondStream.NextWrittenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            var refusals = replies.Where(reply => reply["ok"]!.GetValue<bool>() == false).ToArray();
            Assert.Equal(31, refusals.Length);
            Assert.All(refusals, reply =>
            {
                Assert.Equal(ErrorCodes.DbBusy, reply["error"]!["code"]!.GetValue<string>());
                Assert.True(reply["error"]!["retryable"]!.GetValue<bool>());
            });
            Assert.Equal("GetVersion", Assert.Single(replies, reply => reply["ok"]!.GetValue<bool>())["message_type"]!.GetValue<string>());
            Assert.Equal(1, clock.ObservedReads);

            await firstLifetime.CancelAsync();
            // The database holder is still running: leaving the connection must release its
            // one admitted worker without waiting for somebody else's database operation.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstConnection.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(blocker.Finished);
        }
        finally
        {
            await firstLifetime.CancelAsync();
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
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));
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
        using var stream = new RequestStream(request) { BeforeWrite = cancellation.Cancel };
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
        capture: new CaptureServices
        {
            Npcap = new NpcapDetector(new FakeNpcapEnvironment()),
            Game = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader()),
            Adapters = new AdapterEnumerator(new FakeAdapterProvider(), new FakeProcessTcpTable()),
            EnableFollowTimer = false,
        });

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
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    private sealed class PreviewClock(DateTimeOffset utcNow) : IClock
    {
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _observe;
        private int _reads;
        public Task FirstObservedRead => _firstRead.Task;
        public int ObservedReads => Volatile.Read(ref _reads);
        public void Observe() => Volatile.Write(ref _observe, true);
        public DateTimeOffset UtcNow
        {
            get
            {
                if (Volatile.Read(ref _observe)) { Interlocked.Increment(ref _reads); _firstRead.TrySetResult(); }
                return utcNow;
            }
        }
        public TimeSpan Elapsed => TimeSpan.Zero;
    }

    private sealed class DatabaseBlocker : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _holding;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DatabaseBlocker(SqliteDatabase database) => _holding = Task.Factory.StartNew(() => database.Read(_ =>
        {
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Test database holder was not released.");
            return 0;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        public Task Entered => _entered.Task;
        public bool Finished => _holding.IsCompleted;
        public async ValueTask DisposeAsync()
        {
            _release.Set();
            await _holding;
            _release.Dispose();
        }
    }

    private sealed class RequestStream(params IpcRequest[] requests) : Stream
    {
        private readonly byte[] _input = requests.SelectMany(request => FrameCodec.Encode(IpcEnvelope.ToBytes(new JsonObject
        {
            ["protocol_version"] = IpcEnvelope.ProtocolVersion, ["request_id"] = request.RequestId,
            ["message_type"] = request.MessageType, ["payload"] = request.Payload.DeepClone(),
        }))).ToArray();
        private readonly Channel<JsonObject> _writes = Channel.CreateUnbounded<JsonObject>();
        private int _offset;
        private int _writtenResponses;
        public Action? BeforeWrite { get; init; }
        public int WrittenResponses => Volatile.Read(ref _writtenResponses);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public ValueTask<JsonObject> NextWrittenAsync() => _writes.Reader.ReadAsync();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _input.Length)
            {
                var count = Math.Min(buffer.Length, _input.Length - _offset);
                _input.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Writer.TryWrite(JsonNode.Parse(buffer.Span[FrameCodec.PrefixBytes..])!.AsObject());
            Interlocked.Increment(ref _writtenResponses);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
