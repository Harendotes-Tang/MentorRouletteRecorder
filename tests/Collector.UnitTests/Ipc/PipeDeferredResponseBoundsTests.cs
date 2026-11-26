using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>不读取响应的客户端不能靠持续发送请求无限保留后台任务与已编码响应。</summary>
public sealed class PipeDeferredResponseBoundsTests
{
    [Fact]
    public async Task NonReadingPeerIsDisconnectedAndOutstandingResponsesAreCancelled()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture);
        using var stopping = new CancellationTokenSource();
        using var stream = new DeferredResponseStream(100, blockWrites: true);
        var messages = new ConcurrentQueue<string>();
        var serving = new PipeConnection(stream, new MessageDispatcher(host),
            (message, _) => messages.Enqueue(message)).ServeAsync(stopping.Token);
        try
        {
            await serving.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PipeConnection.MaxDeferredResponses + 1, stream.FramesRead);
            // A cancelled semaphore waiter may acquire the gate as the first write unwinds;
            // every such attempt still receives the cancelled connection token.
            Assert.InRange(stream.WriteAttempts, 1, PipeConnection.MaxDeferredResponses);
            Assert.Equal(stream.WriteAttempts, stream.CancelledWrites);
            Assert.Equal(0, stream.CompletedWrites);
            Assert.Contains(messages, message => message.Contains("deferred response limit", StringComparison.Ordinal));
        }
        finally
        {
            await stopping.CancelAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task CompletedResponsesReleaseCapacityAndAReadablePeerReceivesEveryRefusal()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture);
        using var stream = new DeferredResponseStream(100, blockWrites: false);
        await new PipeConnection(stream, new MessageDispatcher(host)).ServeAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(100, stream.FramesRead);
        Assert.Equal(100, stream.CompletedWrites);
        Assert.Equal(0, stream.CancelledWrites);
        Assert.Equal(100, stream.Responses.Select(reply => reply["request_id"]!.GetValue<string>()).Distinct().Count());
        Assert.All(stream.Responses, reply =>
        {
            Assert.False(reply["ok"]!.GetValue<bool>());
            Assert.Equal(ErrorCodes.BadRequest, reply["error"]!["code"]!.GetValue<string>());
        });
    }

    /// <summary>
    /// Audit 2026-10-20, G-1: the most a well-behaved Desktop has outstanding on one connection - four sentences, the
    /// integrity check, an import and 立即检查 - stays under the limit even while none of the answers can be written yet,
    /// and 立即检查 pressed again while that check runs joins it instead of taking slots of its own. Every request is
    /// answered once the writes go through, and the repeated presses send no second request.
    /// Audit 2026-11-26, RB-2: the daily backup waiting behind two exports for the one whole-file slot takes one slot
    /// between them, and a batch change over a staged selection is admitted on top of all that.
    /// </summary>
    [Fact]
    public async Task ALegitimatePeerIsNeverCutAndRepeatedUpdateChecksJoinTheOneInFlight()
    {
        using var fixture = new TestDatabase();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRequests = 0;
        var updates = new UpdateCheckClient(async (uri, token) =>
        {
            if (uri == UpdateCheckClient.MetadataUri())
            {
                Interlocked.Increment(ref metadataRequests);
            }

            await release.Task.WaitAsync(token);
            return new UpdateTransportResponse(503, uri, null, 0, new MemoryStream());
        }, readEnvironment: _ => null);
        using var host = OpenHost(fixture, updates);
        var requests = Enumerable.Repeat(("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" }), 4)
            .Append(("CheckDatabaseIntegrity", new JsonObject()))
            .Append(("PreviewRunImport", new JsonObject { ["unexpected"] = true }))
            .Concat(Enumerable.Repeat(("CheckUpdateNow", new JsonObject()), 4))
            .Append(("ExportCsv", new JsonObject { ["unexpected"] = true }))
            .Append(("ExportJson", new JsonObject { ["unexpected"] = true }))
            .Append(("BackupDatabase", new JsonObject { ["unexpected"] = true }))
            .Append(("BatchMutateRuns", new JsonObject { ["selection_id"] = Guid.NewGuid().ToString("D"), ["unexpected"] = true }))
            .Select(item => (MessageType: item.Item1, Payload: item.Item2))
            .ToArray();
        using var stream = new ScriptedStream(requests, release.Task);
        var messages = new ConcurrentQueue<string>();
        var serving = new PipeConnection(stream, new MessageDispatcher(host),
            (message, _) => messages.Enqueue(message)).ServeAsync(CancellationToken.None);

        await Task.WhenAny(stream.AllRead, serving).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(messages, message => message.Contains("deferred response limit", StringComparison.Ordinal));
        Assert.False(serving.IsCompleted);
        Assert.True(stream.AllRead.IsCompleted);

        release.SetResult();
        await serving.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            stream.RequestIds.Order(StringComparer.Ordinal),
            stream.Responses.Select(reply => reply["request_id"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(stream.Responses.Where(reply => reply["message_type"]!.GetValue<string>() == "CheckUpdateNow"),
            reply => Assert.True(reply["ok"]!.GetValue<bool>()));
        Assert.Equal(1, Volatile.Read(ref metadataRequests));
    }

    /// <summary>
    /// Audit 2026-11-26, G-1: a batch change over a staged selection that waits for the database does not hold the
    /// connection. The release of its selection and a version request sent after it on the same connection are
    /// answered first, and the batch still applies to the set it was admitted with once the database is free.
    /// </summary>
    [Fact]
    public async Task AStagedBatchWaitingForTheDatabaseDoesNotHoldTheReadLoop()
    {
        using var fixture = new TestDatabase();
        using var host = OpenHost(fixture);
        var run = host.Mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"), Reason = "补录一次导随", Result = RunResult.Completed, JobId = 19,
            EnteredAtUtc = fixture.Clock.UtcNow.AddHours(-1), EndedAtUtc = fixture.Clock.UtcNow.AddMinutes(-30),
        }).Run!;
        var targets = new[] { new BatchRunTarget(run.RunId, run.Revision) };
        var selection = HistorySelectionStore.Digest(targets);
        host.HistorySelections.Stage(selection, 0, targets, last: true);
        var batch = new IpcRequest(Guid.NewGuid().ToString("D"), "BatchMutateRuns", new JsonObject
        {
            ["action"] = "soft_delete", ["selection_id"] = selection, ["reason"] = "批量删除",
        });
        var release = new IpcRequest(Guid.NewGuid().ToString("D"), "QueryHistorySelection",
            new JsonObject { ["selection_id"] = selection, ["release"] = true });
        var version = new IpcRequest(Guid.NewGuid().ToString("D"), "GetVersion", new JsonObject());

        var blocker = new DatabaseBlocker(host.Database);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(90));
        using var stream = new ScriptedPipePeer(batch, release, version);
        using var lifetime = new CancellationTokenSource();
        var serving = new PipeConnection(stream, new MessageDispatcher(host)).ServeAsync(lifetime.Token);
        try
        {
            var first = await stream.NextWrittenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90));
            var second = await stream.NextWrittenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(release.RequestId, first["request_id"]!.GetValue<string>());
            Assert.Equal(version.RequestId, second["request_id"]!.GetValue<string>());
            Assert.False(blocker.Finished);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        try
        {
            var applied = await stream.NextWrittenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(batch.RequestId, applied["request_id"]!.GetValue<string>());
            Assert.True(applied["ok"]!.GetValue<bool>());
            Assert.Equal(1, applied["payload"]!["changed_count"]!.GetValue<int>());
            Assert.True(host.Runs.Get(run.RunId)!.SoftDeleted);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { }
        }
    }

    private static CollectorHost OpenHost(TestDatabase fixture, UpdateCheckClient? updates = null) => CollectorHost.Open(
        Path.Combine(Path.GetDirectoryName(fixture.Path)!, "deferred-bounds.db"), fixture.Clock,
        capture: InertCapture.Services(),
        speechClient: new OnlineSpeechClient((_, _) => throw new InvalidOperationException("these tests never reach a speech service")),
        updateCheckClient: updates ?? new UpdateCheckClient(
            (_, _) => throw new InvalidOperationException("these tests never reach a release host"), readEnvironment: _ => null));

    /// <summary>
    /// Hands the connection the scripted requests one frame per read, holds every answer until <c>writes</c> completes,
    /// and ends the stream once every request has its answer.
    /// </summary>
    private sealed class ScriptedStream((string MessageType, JsonObject Payload)[] requests, Task writes) : AsyncOnlyStream
    {
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<JsonObject> _responses = new();
        private byte[]? _next;
        private int _nextOffset;
        private int _read;

        public Task AllRead => _allRead.Task;
        public List<string> RequestIds { get; } = new();

        public IReadOnlyList<JsonObject> Responses
        {
            get
            {
                lock (_responses)
                {
                    return _responses.ToArray();
                }
            }
        }

        public override bool CanWrite => true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next is null)
            {
                if (_read == requests.Length)
                {
                    _allRead.TrySetResult();
                    await _answered.Task.WaitAsync(cancellationToken);
                    return 0;
                }

                var requestId = Guid.NewGuid().ToString("D");
                RequestIds.Add(requestId);
                _next = IpcTestFrames.Request(requestId, requests[_read].MessageType, requests[_read].Payload);
                _read++;
            }

            var count = Math.Min(buffer.Length, _next.Length - _nextOffset);
            _next.AsMemory(_nextOffset, count).CopyTo(buffer);
            _nextOffset += count;
            if (_nextOffset == _next.Length) { _next = null; _nextOffset = 0; }
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writes.WaitAsync(cancellationToken);
            lock (_responses)
            {
                _responses.Add(IpcTestFrames.Reply(buffer.Span));
                if (_responses.Count == requests.Length)
                {
                    _answered.TrySetResult();
                }
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// 每次读取恰好提供一帧。异步消息故意使用非法字段，在任何网络调用前形成契约错误；
    /// 生产传输的写入背压由可取消的无限等待模拟，测试不连接命名管道或远端服务。
    /// </summary>
    private sealed class DeferredResponseStream(int requests, bool blockWrites) : AsyncOnlyStream
    {
        private byte[]? _next;
        private int _nextOffset;
        public int FramesRead { get; private set; }
        public int WriteAttempts { get; private set; }
        public int CompletedWrites { get; private set; }
        public int CancelledWrites { get; private set; }
        public List<JsonObject> Responses { get; } = new();
        public override bool CanWrite => true;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_next is null)
            {
                if (FramesRead == requests) return ValueTask.FromResult(0);
                _next = IpcTestFrames.Request(
                    Guid.NewGuid().ToString("D"), "CheckUpdateNow", new JsonObject { ["unexpected"] = true });
                FramesRead++;
            }
            var count = Math.Min(buffer.Length, _next.Length - _nextOffset);
            _next.AsMemory(_nextOffset, count).CopyTo(buffer);
            _nextOffset += count;
            if (_nextOffset == _next.Length) { _next = null; _nextOffset = 0; }
            return ValueTask.FromResult(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            if (blockWrites)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { CancelledWrites++; throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Responses.Add(IpcTestFrames.Reply(buffer.Span));
            CompletedWrites++;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
