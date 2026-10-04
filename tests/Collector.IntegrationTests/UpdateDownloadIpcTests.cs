using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// 下载并安装 over the real pipe (docs/reviews/2026-10-03/updater-design.md): <c>StartUpdateDownload</c>,
/// <c>CancelUpdateDownload</c> and the <c>download</c> object every <c>UpdateStatus</c> carries, each answer validated
/// against the contract. The transport is a fake that serves the three published files from memory; nothing here
/// reaches a release host.
/// </summary>
public sealed class UpdateDownloadIpcTests
{
    private const string Published = "9.8.7";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly byte[] Installer = Enumerable.Range(0, 150_000).Select(i => (byte)(i * 7 % 253)).ToArray();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonObject Download(JsonObject response) => response["update"]!["download"]!.AsObject();

    private static async Task<JsonObject> StatusAsync(PipeClient client)
    {
        var status = (await client.SendAsync("GetStatus")).Require();
        ContractSchema.Validate("$defs/Responses/GetStatus", status, "status with download");
        ContractSchema.Validate("$defs/UpdateDownload", status["update"]!["download"], "download in status");
        return status;
    }

    [Fact]
    public async Task EveryUpdateStatusCarriesTheDownloadIdleUntilOneIsAsked()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var status = await StatusAsync(client);
        var checkedNow = (await client.SendAsync("CheckUpdateNow")).Require();

        ContractSchema.Validate("$defs/Responses/CheckUpdateNow", checkedNow, "check now with download");
        foreach (var download in new[] { Download(status), Download(checkedNow) })
        {
            Assert.Equal("{\"state\":\"IDLE\"}", download.ToJsonString());
        }
    }

    /// <summary>
    /// The whole round: a check learns the version, the download is asked for, GetStatus is polled until it is
    /// READY, and every answer on the way validates. The three requests sent are the metadata, the checksum and the
    /// installer, in that order, and nothing else.
    /// </summary>
    [Fact]
    public async Task ADownloadOverThePipeEndsReadyAndEveryAnswerValidates()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(updateCheckClient: transport.Client());
        await using var client = await fixture.ConnectAsync();
        Assert.Equal("CHECKED", (await client.SendAsync("CheckUpdateNow")).Require()["outcome"]!.GetValue<string>());

        var started = (await client.SendAsync("StartUpdateDownload")).Require();
        ContractSchema.Validate("$defs/Responses/StartUpdateDownload", started, "download started");
        Assert.Contains(Download(started)["state"]!.GetValue<string>(), new[] { "DOWNLOADING", "VERIFYING", "READY" });

        var download = await PollAsync(client, state => state == "READY");
        var expectedPath = Path.Combine(
            Path.GetDirectoryName(fixture.DatabasePath)!, "updates", "MentorRecorder-" + Published + "-setup.exe");
        Assert.Equal(Published, download["version"]!.GetValue<string>());
        Assert.Equal(expectedPath, download["file_path"]!.GetValue<string>());
        Assert.Equal(Sha(Installer), download["sha256"]!.GetValue<string>());
        Assert.Equal(Installer.Length, download["received_bytes"]!.GetValue<long>());
        Assert.Equal(Installer.Length, download["total_bytes"]!.GetValue<long>());
        Assert.Equal(Installer, await File.ReadAllBytesAsync(expectedPath));

        // Cancel has nothing to stop once the installer is ready.
        var cancelled = (await client.SendAsync("CancelUpdateDownload")).Require();
        ContractSchema.Validate("$defs/Responses/CancelUpdateDownload", cancelled, "cancel when ready");
        Assert.Equal("READY", Download(cancelled)["state"]!.GetValue<string>());

        Assert.Equal(
            new[]
            {
                UpdateCheckClient.MetadataUri().AbsoluteUri,
                UpdateCheckClient.ChecksumUrl(Published),
                UpdateCheckClient.InstallerUrl(Published),
            },
            transport.Requests.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task NothingToDownloadIsAFailureWithASentenceAndSendsNothing()
    {
        // The fixture's own client throws on any request, so a NO_UPDATE answer is also proof that none was sent.
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        foreach (var payload in new[] { new JsonObject(), new JsonObject { ["reinstall"] = true } })
        {
            var answer = (await client.SendAsync("StartUpdateDownload", payload)).Require();

            ContractSchema.Validate("$defs/Responses/StartUpdateDownload", answer, "nothing to download");
            var download = Download(answer);
            Assert.Equal("FAILED", download["state"]!.GetValue<string>());
            Assert.Equal("NO_UPDATE", download["failure"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(download["message"]!.GetValue<string>()));
            Assert.False(download.ContainsKey("version"));
        }

        Assert.Equal("FAILED", Download(await StatusAsync(client))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSettingOffIsDisabledOverThePipe()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(updateCheckClient: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("CheckUpdateNow")).Require();
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = false })).Require();

        var answer = (await client.SendAsync("StartUpdateDownload")).Require();

        ContractSchema.Validate("$defs/Responses/StartUpdateDownload", answer, "download disabled");
        Assert.Equal("DISABLED", Download(answer)["failure"]!.GetValue<string>());
        Assert.Single(transport.Requests);
    }

    /// <summary>
    /// Switched off, nothing is offered (docs/privacy-boundary.md §8.4): turning the update check off in the settings
    /// stops a download in flight at once - the read abandoned, the partial file removed - and the status says why.
    /// </summary>
    [Fact]
    public async Task TurningTheCheckOffStopsARunningDownloadAndKeepsNothing()
    {
        var transport = new Transport { StallAfter = 40_000 };
        await using var fixture = ServerFixture.Start(updateCheckClient: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("CheckUpdateNow")).Require();
        (await client.SendAsync("StartUpdateDownload")).Require();
        await PollDownloadAsync(client, download => download["received_bytes"]?.GetValue<long>() > 0);
        var part = InstallerPath(fixture) + ".part";
        Assert.True(File.Exists(part));

        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = false })).Require();
        var download = Download(await StatusAsync(client));

        Assert.Equal("FAILED", download["state"]!.GetValue<string>());
        Assert.Equal("DISABLED", download["failure"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(download["message"]!.GetValue<string>()));
        await transport.BodyCancelled.WaitAsync(Patience);
        await EventuallyAsync(() => !File.Exists(part), "the partial file must be removed");
        Assert.False(File.Exists(InstallerPath(fixture)));
    }

    /// <summary>Turning the check off while an installer is READY withdraws it: no 立即安装 is left on offer.</summary>
    [Fact]
    public async Task TurningTheCheckOffWithdrawsAReadyInstaller()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(updateCheckClient: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("CheckUpdateNow")).Require();
        (await client.SendAsync("StartUpdateDownload")).Require();
        await PollAsync(client, state => state == "READY");
        Assert.True(File.Exists(InstallerPath(fixture)));

        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = false })).Require();
        var status = await StatusAsync(client);

        Assert.False(status["update"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("FAILED", Download(status)["state"]!.GetValue<string>());
        Assert.Equal("DISABLED", Download(status)["failure"]!.GetValue<string>());
        Assert.False(File.Exists(InstallerPath(fixture)));
    }

    [Theory]
    [InlineData("StartUpdateDownload", "{\"reinstall\":\"yes\"}")]
    [InlineData("StartUpdateDownload", "{\"reinstall\":1}")]
    [InlineData("StartUpdateDownload", "{\"version\":\"9.8.7\"}")]
    [InlineData("CancelUpdateDownload", "{\"reinstall\":true}")]
    public async Task APayloadTheContractDoesNotDeclareIsRefused(string messageType, string payload)
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync(messageType, JsonNode.Parse(payload)!.AsObject());

        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    /// <summary>
    /// Both messages answer at once: with the checksum request held open on the network side, a start, a status
    /// poll and a cancel on the same connection are all answered, each well inside an ordinary poll's deadline.
    /// </summary>
    [Fact]
    public async Task NeitherMessageHoldsUpOtherRequestsOnTheSameConnection()
    {
        var transport = new Transport { HoldChecksum = true };
        await using var fixture = ServerFixture.Start(updateCheckClient: transport.Client());
        await using var client = await fixture.ConnectAsync();
        try
        {
            (await client.SendAsync("CheckUpdateNow")).Require();
            var quick = TimeSpan.FromSeconds(3);
            var waited = Stopwatch.StartNew();

            var started = (await client.SendAsync("StartUpdateDownload", timeout: quick)).Require();
            await transport.ChecksumAsked.WaitAsync(Patience);
            var polled = (await client.SendAsync("GetStatus", timeout: quick)).Require();
            var cancelled = (await client.SendAsync("CancelUpdateDownload", timeout: quick)).Require();
            var after = (await client.SendAsync("GetStatus", timeout: quick)).Require();

            Assert.True(waited.Elapsed < Patience, "the answers must not wait for the download");
            Assert.Equal("DOWNLOADING", Download(started)["state"]!.GetValue<string>());
            Assert.Equal("DOWNLOADING", Download(polled)["state"]!.GetValue<string>());
            Assert.Equal("IDLE", Download(cancelled)["state"]!.GetValue<string>());
            Assert.Equal("IDLE", Download(after)["state"]!.GetValue<string>());
            ContractSchema.Validate("$defs/Responses/CancelUpdateDownload", cancelled, "cancel while downloading");
            ContractSchema.Validate("$defs/Responses/GetStatus", polled, "status while downloading");
            await transport.ChecksumReleased.WaitAsync(Patience);
        }
        finally
        {
            transport.Release();
        }
    }

    /// <summary>
    /// Stopping the Collector mid-download is a cancel: the read in flight is abandoned and the partial file is
    /// removed before the host has finished closing.
    /// </summary>
    [Fact]
    public async Task StoppingTheCollectorMidDownloadLeavesNoPartialFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var transport = new Transport { StallAfter = 40_000 };
        try
        {
            var host = CollectorHost.Open(
                Path.Combine(directory, "test.db"),
                capture: CaptureFakes.NoGame(),
                speechClient: ServerFixture.RefusingSpeechClient,
                updateCheckClient: transport.Client());
            var part = Path.Combine(directory, "updates", "MentorRecorder-" + Published + "-setup.exe.part");
            try
            {
                Assert.Equal(UpdateCheckRequestOutcome.Checked, await host.Updates.CheckNowIfAllowedAsync());
                Assert.Equal(UpdateDownloadState.Downloading, host.UpdateDownloads.Start().State);
                var waited = Stopwatch.StartNew();
                while (host.UpdateDownloads.Snapshot().ReceivedBytes is not > 0)
                {
                    Assert.True(waited.Elapsed < Patience, "the download must be under way");
                    await Task.Delay(10);
                }

                Assert.True(File.Exists(part));
            }
            finally
            {
                host.Dispose();
            }

            Assert.True(transport.BodyCancelled.IsCompleted, "the read in flight must have been cancelled");
            Assert.False(File.Exists(part));
            Assert.False(File.Exists(part[..^".part".Length]));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Windows can hold a WAL handle briefly; the temp cleaner will get it.
            }
        }
    }

    private static Task<JsonObject> PollAsync(PipeClient client, Func<string, bool> until) =>
        PollDownloadAsync(client, download => until(download["state"]!.GetValue<string>()));

    private static async Task<JsonObject> PollDownloadAsync(PipeClient client, Func<JsonObject, bool> until)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var download = Download(await StatusAsync(client));
            if (until(download))
            {
                return download;
            }

            Assert.True(waited.Elapsed < Patience, "download stuck in " + download.ToJsonString());
            await Task.Delay(20);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, what);
            await Task.Delay(10);
        }
    }

    private static string InstallerPath(ServerFixture fixture) => Path.Combine(
        Path.GetDirectoryName(fixture.DatabasePath)!, "updates", "MentorRecorder-" + Published + "-setup.exe");

    // ---------------------------------------------------------------------------- test double

    /// <summary>Serves the metadata, the checksum and the installer of <see cref="Published"/> from memory.</summary>
    private sealed class Transport
    {
        private readonly List<Uri> _requests = new();
        private readonly TaskCompletionSource _checksumAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _checksumReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bodyCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the checksum request until it is cancelled or <see cref="Release"/> is called.</summary>
        public bool HoldChecksum { get; init; }

        /// <summary>Serves this many installer bytes, then stalls until the read is cancelled.</summary>
        public int? StallAfter { get; init; }

        public Task ChecksumAsked => _checksumAsked.Task;

        /// <summary>Completes when the held checksum request has ended, cancelled or released.</summary>
        public Task ChecksumReleased => _checksumReleased.Task;

        public Task BodyCancelled => _bodyCancelled.Task;

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public UpdateCheckClient Client() => new(SendAsync, readEnvironment: _ => null);

        public void Release() => _release.TrySetResult();

        private async Task<UpdateTransportResponse> SendAsync(Uri uri, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(uri);
            }

            var address = uri.AbsoluteUri;
            if (address == UpdateCheckClient.MetadataUri().AbsoluteUri)
            {
                return Body(uri, Encoding.UTF8.GetBytes("{\"version\": \"" + Published + "\"}"));
            }

            if (address == UpdateCheckClient.ChecksumUrl(Published))
            {
                return await ChecksumAsync(uri, cancellationToken).ConfigureAwait(false);
            }

            if (address == UpdateCheckClient.InstallerUrl(Published))
            {
                Stream body = StallAfter is { } served
                    ? new StallingStream(Installer[..served], _bodyCancelled)
                    : new MemoryStream(Installer);
                return new UpdateTransportResponse(200, uri, null, Installer.Length, body);
            }

            return new UpdateTransportResponse(404, uri, null, 0, new MemoryStream());
        }

        private async Task<UpdateTransportResponse> ChecksumAsync(Uri uri, CancellationToken cancellationToken)
        {
            _checksumAsked.TrySetResult();
            if (HoldChecksum)
            {
                try
                {
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _checksumReleased.TrySetResult();
                }
            }

            return Body(uri, Encoding.ASCII.GetBytes(Sha(Installer) + "  MentorRecorder-" + Published + "-setup.exe\r\n"));
        }

        private static UpdateTransportResponse Body(Uri uri, byte[] bytes) =>
            new(200, uri, null, bytes.Length, new MemoryStream(bytes));
    }

    /// <summary>Serves a prefix, then waits for bytes that never come until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] prefix, TaskCompletionSource cancelled) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cancelled.TrySetResult();
            }

            return 0;
        }
    }
}
