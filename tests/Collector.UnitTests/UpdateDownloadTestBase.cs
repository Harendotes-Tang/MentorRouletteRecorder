using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Update;
using Failure = MentorRecorder.Collector.Update.UpdateDownloadFailure;
using State = MentorRecorder.Collector.Update.UpdateDownloadState;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The fixture every 下载并安装 test of the Collector shares (docs/reviews/2026-10-03/updater-design.md): a real
/// settings repository over a throwaway database, a data directory and a log of the test's own, a fake transport that
/// answers by address, and bodies that stall, trickle or fail on cue. Nothing here opens a connection or writes a host
/// name; addresses come from the client's own builders. Timeouts are injected in milliseconds, so no test waits for
/// real seconds; the start interval is zero unless a test asks for one, and then the clock is moved by hand
/// (<see cref="Advance"/>).
/// </summary>
public abstract class UpdateDownloadTestBase : IDisposable
{
    protected const string Local = "1.0.0";
    protected const string Published = "2.0.0";

    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    protected static readonly byte[] Installer = Pattern(200_000);

    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;
    private readonly string _logDirectory;
    private readonly RotatingFileLogger _log;
    private readonly List<UpdateDownloadService> _services = new();
    private readonly List<UpdateCheckService> _checks = new();
    private readonly List<UpdateDownloadFiles> _files = new();

    protected UpdateDownloadTestBase()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        Root = Directory.CreateTempSubdirectory("MentorRecorder.UpdateDownload.").FullName;
        DataDirectory = Path.Combine(Root, "data");
        _logDirectory = Path.Combine(Root, "logs");
        Directory.CreateDirectory(DataDirectory);
        _log = new RotatingFileLogger(_logDirectory, _database.Clock);
    }

    /// <summary>The test's own temporary folder: the data directory, the log folder and anything a test adds.</summary>
    protected string Root { get; }

    /// <summary>The Collector's data directory, under <see cref="Root"/>.</summary>
    protected string DataDirectory { get; }

    /// <summary>The update check behind the service <see cref="Service"/> made last.</summary>
    protected UpdateCheckService LastCheck => _checks[^1];

    /// <summary>The <c>updates</c> folder of the service <see cref="Service"/> made last, for its seams.</summary>
    protected UpdateDownloadFiles LastFiles => _files[^1];

    /// <summary>Moves the services' monotonic clock forward; nothing waits.</summary>
    protected void Advance(TimeSpan by) => _database.Clock.Elapsed += by;

    protected string Updates => Path.Combine(DataDirectory, UpdateDownloadFiles.FolderName);

    protected string InstallerPath(string version = Published) =>
        Path.Combine(Updates, "MentorRecorder-" + version + "-setup.exe");

    protected string PartPath(string version = Published) => InstallerPath(version) + ".part";

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        foreach (var check in _checks)
        {
            check.Dispose();
        }

        _log.Dispose();
        _database.Dispose();
        try
        {
            // A junction is removed as the link it is, before the recursive delete, which refuses to meet one.
            if (Directory.Exists(Updates) && File.GetAttributes(Updates).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(Updates);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp cleaner.
        }
    }

    protected UpdateDownloadService Service(
        Transport transport,
        string? latest = Published,
        string local = Local,
        bool enabled = true,
        Func<string, string?>? environment = null,
        UpdateDownloadLimits? limits = null)
    {
        if (latest is not null)
        {
            _settings.SetSetting(UpdateCheckService.LatestVersionSetting, "\"" + latest + "\"");
        }

        var client = new UpdateCheckClient(transport.Send, TimeSpan.FromSeconds(5), environment ?? (_ => null));
        var check = new UpdateCheckService(_settings, client, local, _database.Clock, () => TimeSpan.Zero);
        check.ApplySetting(enabled);
        var files = new UpdateDownloadFiles(DataDirectory);
        var service = new UpdateDownloadService(check, client, files, _database.Clock, () => _log, limits ?? Limits());
        _checks.Add(check);
        _files.Add(files);
        _services.Add(service);
        return service;
    }

    /// <summary>Waits for the download the last <see cref="UpdateDownloadService.Start"/> began, and returns where it ended.</summary>
    protected static async Task<UpdateDownloadSnapshot> SettleAsync(UpdateDownloadService service)
    {
        if (service.Running is { } running)
        {
            await running.WaitAsync(Patience);
        }

        return service.Snapshot();
    }

    /// <summary>Polls until <paramref name="condition"/> holds; the steps are milliseconds, the bound is the test's patience.</summary>
    protected static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, what);
            await Task.Delay(10);
        }
    }

    /// <summary>The shipping limits, but with no start interval unless one is given.</summary>
    protected static UpdateDownloadLimits Limits(
        long? maxBytes = null, TimeSpan? idle = null, TimeSpan? total = null, TimeSpan? startInterval = null) => new(
        maxBytes ?? UpdateDownloadLimits.Default.MaxBytes,
        idle ?? UpdateDownloadLimits.Default.IdleTimeout,
        total ?? UpdateDownloadLimits.Default.TotalTimeout,
        startInterval ?? TimeSpan.Zero);

    protected void AssertNothingKept(string version = Published)
    {
        Assert.False(File.Exists(PartPath(version)), "the .part must be gone");
        Assert.False(File.Exists(InstallerPath(version)), "no installer may be kept");
    }

    protected static void AssertFailed(UpdateDownloadSnapshot snapshot, Failure failure)
    {
        Assert.Equal(State.Failed, snapshot.State);
        Assert.Equal(failure, snapshot.Failure);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Message));
        Assert.Null(snapshot.FilePath);
        Assert.Null(snapshot.Sha256);
    }

    protected List<JsonObject> UpdateLines()
    {
        _log.Dispose();
        return Directory.EnumerateFiles(_logDirectory)
            .SelectMany(File.ReadAllLines)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .Where(line => line["component"]?.GetValue<string>() == "update")
            .ToList();
    }

    // ---------------------------------------------------------------------------- test doubles

    /// <summary>
    /// A directory junction, which needs no elevation (a symbolic link does). <c>mklink</c> is a <c>cmd</c> built-in;
    /// the link is awaited by looking for it rather than by waiting on the process, whose members that open a handle
    /// are off limits outside a short allowed list (static rule INJ-009).
    /// </summary>
    protected static class Junction
    {
        public static async Task CreateAsync(string link, string target)
        {
            Mklink("/J", link, target);
            await EventuallyAsync(() => IsJunction(link), "mklink /J must create the junction");
        }

        /// <summary>The same, for a seam that runs synchronously on the download's own task.</summary>
        public static void Create(string link, string target)
        {
            Mklink("/J", link, target);
            var waited = Stopwatch.StartNew();
            while (!IsJunction(link))
            {
                Assert.True(waited.Elapsed < Patience, "mklink /J must create the junction");
                Thread.Sleep(10);
            }
        }

        private static bool IsJunction(string link) =>
            Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>A hard link, which needs no elevation either: a second name for the same file, made by <c>mklink /H</c>.</summary>
    protected static class HardLink
    {
        public static async Task CreateAsync(string link, string target)
        {
            Mklink("/H", link, target);
            await EventuallyAsync(() => File.Exists(link), "mklink /H must create the hard link");
        }
    }

    private static void Mklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", kind, link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using (Process.Start(start))
        {
        }
    }

    /// <summary>
    /// What another process of the same user can try against <c>updates</c> while the Collector uses it: move it aside
    /// (or remove it) and put a junction to <paramref name="elsewhere"/> in its place. True when that worked.
    /// </summary>
    protected bool TrySwapUpdates(string elsewhere, bool moveAside = true)
    {
        try
        {
            if (moveAside)
            {
                Directory.Move(Updates, Updates + ".aside");
            }
            else
            {
                Directory.Delete(Updates);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Junction.Create(Updates, elsewhere);
        return true;
    }

    protected static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 % 251);
        }

        return bytes;
    }

    protected static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    protected static Uri ChecksumUri(string version) => new(UpdateCheckClient.ChecksumUrl(version)!, UriKind.Absolute);

    protected static Uri InstallerUri(string version) => new(UpdateCheckClient.InstallerUrl(version)!, UriKind.Absolute);

    /// <summary>Answers by address, 404 for anything it was not told about, and records every request.</summary>
    protected sealed class Transport
    {
        private readonly Dictionary<string, Func<Uri, CancellationToken, Task<UpdateTransportResponse>>> _routes =
            new(StringComparer.Ordinal);

        private readonly List<Uri> _requests = new();

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

        public static UpdateTransportResponse Status(Uri uri, int status) => new(status, uri, null, 0, new MemoryStream());

        public static UpdateTransportResponse Redirect(Uri uri, Uri location) =>
            new(302, uri, location, 0, new MemoryStream());

        public Transport On(Uri uri, Func<Uri, CancellationToken, Task<UpdateTransportResponse>> respond)
        {
            lock (_routes)
            {
                _routes[uri.AbsoluteUri] = respond;
            }

            return this;
        }

        /// <summary>
        /// Publishes a version: its checksum and its installer. <paramref name="onChecksum"/> runs while the checksum
        /// is being asked for - after <c>updates</c> was prepared and cleared, before anything is written into it.
        /// </summary>
        public Transport Publish(
            string version,
            byte[] installer,
            string? checksum = null,
            Stream? body = null,
            bool declareLength = true,
            long? declaredLength = null,
            TaskCompletionSource? gate = null,
            Func<Task>? onChecksum = null)
        {
            var sidecar = Encoding.ASCII.GetBytes(
                (checksum ?? Sha(installer)) + (checksum is null ? "  MentorRecorder-" + version + "-setup.exe\r\n" : string.Empty));
            On(ChecksumUri(version), async (uri, _) =>
            {
                if (onChecksum is not null)
                {
                    await onChecksum().ConfigureAwait(false);
                }

                return new UpdateTransportResponse(200, uri, null, sidecar.Length, new MemoryStream(sidecar));
            });
            On(InstallerUri(version), async (uri, token) =>
            {
                if (gate is not null)
                {
                    await gate.Task.WaitAsync(token).ConfigureAwait(false);
                }

                return new UpdateTransportResponse(
                    200,
                    uri,
                    null,
                    declaredLength ?? (declareLength ? installer.Length : null),
                    body ?? new MemoryStream(installer));
            });
            return this;
        }

        public Task<UpdateTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(uri);
            }

            Func<Uri, CancellationToken, Task<UpdateTransportResponse>>? respond;
            lock (_routes)
            {
                _routes.TryGetValue(uri.AbsoluteUri, out respond);
            }

            return respond is null ? Task.FromResult(Status(uri, 404)) : respond(uri, cancellationToken);
        }
    }

    /// <summary>A read-only body that refuses every synchronous member; the service must read asynchronously.</summary>
    protected abstract class BodyStream : Stream
    {
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
    }

    /// <summary>Serves its bytes in fixed chunks and counts how many were taken.</summary>
    protected sealed class CountingStream(byte[] content) : BodyStream
    {
        public const int Chunk = 4096;

        private int _position;

        public int BytesRead => Volatile.Read(ref _position);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(Math.Min(buffer.Length, Chunk), content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            Volatile.Write(ref _position, _position + count);
            return ValueTask.FromResult(count);
        }
    }

    /// <summary>Serves a prefix, then waits for bytes that never come until the read is cancelled.</summary>
    protected sealed class StallingStream(byte[] prefix) : BodyStream
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        /// <summary>Completes once the stalled read has been cancelled.</summary>
        public Task Stalled => _stalled.Task;

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
                _stalled.TrySetResult();
            }

            return 0;
        }
    }

    /// <summary>One byte every few milliseconds, for ever.</summary>
    protected sealed class TricklingStream : BodyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = 0x4D;
            return 1;
        }
    }

    /// <summary>Serves a prefix, then fails the way a connection reset mid-answer does.</summary>
    protected sealed class FailingStream(byte[] prefix) : BodyStream
    {
        private bool _served;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return ValueTask.FromException<int>(new IOException("the connection was reset"));
            }

            _served = true;
            var count = Math.Min(buffer.Length, prefix.Length);
            prefix.AsMemory(0, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
    }
}
