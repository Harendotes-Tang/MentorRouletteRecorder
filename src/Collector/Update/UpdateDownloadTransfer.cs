using System.Security.Cryptography;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// One download of one installer, as <see cref="UpdateDownloadService"/> runs it on its background task.
///
/// In order: <c>updates\</c> is made ready, held until the download is done with it, and cleared of leftovers; the
/// published checksum is fetched; the installer is asked for and streamed into <c>&lt;name&gt;.part</c>, opened for
/// this process alone, hashed while it is written and abandoned the moment it passes the size cap; the file is flushed
/// to disk, its hash compared with the published one, and only then renamed to its final name - where it is hashed
/// once more, because READY describes the file kept, not the bytes written. Nothing is sent before the folder is
/// known to be usable.
///
/// The limits (<see cref="UpdateDownloadLimits"/>): a declared length over the cap is refused before a byte is read;
/// no byte for the idle limit, or the whole download past the overall limit, is TIMEOUT. A failure ends in
/// <see cref="UpdateDownloadFailedException"/>, the caller's cancel in <see cref="OperationCanceledException"/>; on
/// every way out but success the <c>.part</c> is deleted, and so is the file under the final name when it got that
/// far. Never names an address: the client builds them all.
/// </summary>
internal sealed class UpdateDownloadTransfer
{
    private const int BufferBytes = 81920;

    private readonly UpdateCheckClient _client;
    private readonly UpdateDownloadFiles _files;
    private readonly UpdateDownloadLimits _limits;
    private readonly string _version;
    private readonly Action<UpdateDownloadSnapshot> _publish;
    private long? _total;

    /// <summary>Prepares a download; starts nothing.</summary>
    /// <param name="client">Sends the two requests.</param>
    /// <param name="files">The <c>updates</c> folder.</param>
    /// <param name="limits">Size cap, idle and overall limits.</param>
    /// <param name="version">A published version: three plain numbers.</param>
    /// <param name="publish">Receives every change of state: progress, then VERIFYING.</param>
    public UpdateDownloadTransfer(
        UpdateCheckClient client,
        UpdateDownloadFiles files,
        UpdateDownloadLimits limits,
        string version,
        Action<UpdateDownloadSnapshot> publish)
    {
        _client = client;
        _files = files;
        _limits = limits;
        _version = UpdateMetadata.IsVersion(version)
            ? version
            : throw new ArgumentException("not a published version", nameof(version));
        _publish = publish;
    }

    /// <summary>Bytes written to the <c>.part</c> so far.</summary>
    public long ReceivedBytes { get; private set; }

    /// <summary>Runs the download to its end.</summary>
    /// <param name="cancellation">The user's cancel, or the Collector stopping.</param>
    /// <returns>The READY state, with the verified file.</returns>
    public async Task<UpdateDownloadSnapshot> RunAsync(CancellationToken cancellation)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        overall.CancelAfter(_limits.TotalTimeout);
        using var folder = OnDisk(() => _files.Prepare());
        OnDisk(() => _files.RemoveLeftovers());

        var published = await ChecksumAsync(cancellation, overall.Token).ConfigureAwait(false);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        idle.CancelAfter(_limits.IdleTimeout);
        using var installer = await OpenAsync(cancellation, idle.Token).ConfigureAwait(false);
        _total = installer.ContentLength;
        _publish(UpdateDownloadSnapshot.Downloading(_version, 0, _total));
        return await StoreAsync(installer.Body!, published, cancellation, overall.Token, idle).ConfigureAwait(false);
    }

    /// <summary>
    /// What a request outcome comes to for the download. A refusal of the checksum that means "there is no usable
    /// checksum" - not there, too large, not a checksum - is CHECKSUM_UNAVAILABLE; every other outcome keeps its name.
    /// </summary>
    /// <param name="outcome">The client's outcome; never OK or CANCELLED by the caller.</param>
    /// <param name="checksum">True for the checksum request.</param>
    internal static UpdateDownloadFailure Map(UpdateCheckOutcome outcome, bool checksum) => outcome switch
    {
        UpdateCheckOutcome.NotFound or UpdateCheckOutcome.TooLarge or UpdateCheckOutcome.Malformed when checksum =>
            UpdateDownloadFailure.ChecksumUnavailable,
        UpdateCheckOutcome.NotFound => UpdateDownloadFailure.NotFound,
        UpdateCheckOutcome.RateLimited => UpdateDownloadFailure.RateLimited,
        UpdateCheckOutcome.HttpStatus => UpdateDownloadFailure.HttpStatus,
        UpdateCheckOutcome.RedirectRefused => UpdateDownloadFailure.RedirectRefused,
        UpdateCheckOutcome.HostRefused => UpdateDownloadFailure.HostRefused,
        UpdateCheckOutcome.TooLarge => UpdateDownloadFailure.TooLarge,
        UpdateCheckOutcome.Timeout or UpdateCheckOutcome.Cancelled => UpdateDownloadFailure.Timeout,
        UpdateCheckOutcome.DnsOrConnect => UpdateDownloadFailure.DnsOrConnect,
        UpdateCheckOutcome.TlsFailed => UpdateDownloadFailure.TlsFailed,
        UpdateCheckOutcome.Disabled => UpdateDownloadFailure.Disabled,
        _ => UpdateDownloadFailure.TransportFailed,
    };

    private async Task<string> ChecksumAsync(CancellationToken cancellation, CancellationToken overall)
    {
        var result = await _client.FetchChecksumAsync(_version, overall).ConfigureAwait(false);
        return result.Outcome == UpdateCheckOutcome.Ok
            ? result.Sha256!
            : throw Refused(result.Outcome, result.Detail, checksum: true, cancellation);
    }

    private async Task<UpdateInstallerResponse> OpenAsync(CancellationToken cancellation, CancellationToken idle)
    {
        var installer = await _client.OpenInstallerAsync(_version, _limits.MaxBytes, idle).ConfigureAwait(false);
        if (installer.Result.Outcome == UpdateCheckOutcome.Ok)
        {
            return installer;
        }

        installer.Dispose();
        throw Refused(installer.Result.Outcome, installer.Result.Detail, checksum: false, cancellation);
    }

    // A request the client gave up on because its token was cancelled is the user's cancel when they asked for it,
    // and the idle or overall limit otherwise.
    private static Exception Refused(
        UpdateCheckOutcome outcome, string? detail, bool checksum, CancellationToken cancellation) =>
        outcome == UpdateCheckOutcome.Cancelled && cancellation.IsCancellationRequested
            ? new OperationCanceledException(cancellation)
            : new UpdateDownloadFailedException(Map(outcome, checksum), detail);

    private async Task<UpdateDownloadSnapshot> StoreAsync(
        Stream body,
        string published,
        CancellationToken cancellation,
        CancellationToken overall,
        CancellationTokenSource idle)
    {
        var promoted = false;
        var kept = false;
        try
        {
            string written;
            await using (var part = OnDisk(() => _files.CreatePart(_version)))
            {
                written = await CopyAsync(body, part, cancellation, overall, idle).ConfigureAwait(false);
                _publish(UpdateDownloadSnapshot.Verifying(_version, ReceivedBytes, _total));
                await part.FlushAsync(overall).ConfigureAwait(false);
                part.Flush(flushToDisk: true);
            }

            if (!string.Equals(written, published, StringComparison.Ordinal))
            {
                throw new UpdateDownloadFailedException(UpdateDownloadFailure.ChecksumMismatch);
            }

            var path = _files.Promote(_version);
            promoted = true;

            // Between the close above and the rename another process could have changed the file, so what READY
            // reports is the file now under its final name, hashed while nothing else may write to it. One that
            // cannot be held that way - someone still has it open for writing - is not kept either.
            var actual = await _files.HashInstallerAsync(_version, overall).ConfigureAwait(false);
            if (actual is null)
            {
                throw new UpdateDownloadFailedException(UpdateDownloadFailure.DiskFailed, "KEPT_FILE_UNREADABLE");
            }

            if (!string.Equals(actual, published, StringComparison.Ordinal))
            {
                throw new UpdateDownloadFailedException(UpdateDownloadFailure.ChecksumMismatch);
            }

            kept = true;
            return UpdateDownloadSnapshot.Ready(_version, ReceivedBytes, _total, path, actual);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // The overall limit ran out while a block was being written.
            throw new UpdateDownloadFailedException(UpdateDownloadFailure.Timeout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read failures were turned into outcomes in ReadAsync, so whatever is left here is the disk's.
            throw new UpdateDownloadFailedException(UpdateDownloadFailure.DiskFailed, inner: ex);
        }
        finally
        {
            if (!kept)
            {
                UpdateDownloadFiles.TryDelete(_files.PartPath(_version));
                if (promoted)
                {
                    UpdateDownloadFiles.TryDelete(_files.InstallerPath(_version));
                }
            }
        }
    }

    private async Task<string> CopyAsync(
        Stream body, FileStream part, CancellationToken cancellation, CancellationToken overall, CancellationTokenSource idle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        while (await ReadAsync(body, buffer, cancellation, idle).ConfigureAwait(false) is var read and > 0)
        {
            if (ReceivedBytes + read > _limits.MaxBytes)
            {
                throw new UpdateDownloadFailedException(UpdateDownloadFailure.TooLarge);
            }

            hash.AppendData(buffer, 0, read);
            await part.WriteAsync(buffer.AsMemory(0, read), overall).ConfigureAwait(false);
            ReceivedBytes += read;
            _publish(UpdateDownloadSnapshot.Downloading(_version, ReceivedBytes, _total));
        }

        if (_total is { } declared && declared != ReceivedBytes)
        {
            // The connection ended before the length it announced: not the file that was published.
            throw new UpdateDownloadFailedException(UpdateDownloadFailure.TransportFailed, "TRUNCATED");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// One read under the idle limit, armed for the read alone: the time spent writing the block to disk is not time
    /// spent waiting for the network.
    /// </summary>
    private async Task<int> ReadAsync(
        Stream body, byte[] buffer, CancellationToken cancellation, CancellationTokenSource idle)
    {
        idle.CancelAfter(_limits.IdleTimeout);
        try
        {
            var read = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            return read;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var described = UpdateCheckClient.Describe(ex, CancellationToken.None);
            throw new UpdateDownloadFailedException(
                Map(described.Outcome, checksum: false), described.Detail ?? ex.GetType().Name);
        }
    }

    private static T OnDisk<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateDownloadFailedException(UpdateDownloadFailure.DiskFailed, inner: ex);
        }
    }

    private static void OnDisk(Action action) => OnDisk(() =>
    {
        action();
        return true;
    });
}
