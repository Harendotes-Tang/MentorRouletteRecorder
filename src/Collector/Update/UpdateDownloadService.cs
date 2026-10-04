using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// 下载并安装, the Collector's half: the installer of the newer version, fetched only when the user asks
/// (<c>StartUpdateDownload</c>), checked against the SHA-256 published beside it, and kept in <c>updates\</c> beside
/// the database for the Desktop to start. This service never runs what it downloaded.
///
/// One download at a time, held in memory: IDLE → DOWNLOADING → VERIFYING → READY | FAILED, and a cancel goes back to
/// IDLE. <see cref="Start"/> and <see cref="Cancel"/> answer at once - they are called on the IPC read loop - and the
/// download itself (<see cref="UpdateDownloadTransfer"/>) runs on a background task that touches neither the database
/// nor the capture thread. <see cref="Snapshot"/> is what every <c>UpdateStatus</c> reports, received bytes included,
/// so polling <c>GetStatus</c> shows the progress.
///
/// One background task at a time, too: a new one is never started while the last is still alive, not even after a
/// cancel - a step that cannot be cancelled, such as flushing or renaming a large file, may outlive any wait - so two
/// downloads never meet on the same file. And at most one new download per
/// <see cref="UpdateDownloadLimits.StartInterval"/>, however often it is asked for. Switching the update check off
/// withdraws a download that is running or ready (<see cref="Withdraw"/>).
///
/// A READY installer asked for again is hashed once more on the background task (VERIFYING) before it is called
/// READY a second time, so a file changed on disk since is downloaded afresh rather than offered - unless it was
/// verified less than the start interval ago. Every download that starts writes one log line, and one when it ends;
/// neither names an address, because a release asset's redirect target carries a signed query string.
/// </summary>
public sealed class UpdateDownloadService : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for a cancelled download to remove its partial file.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private const string LogComponent = "update";

    private readonly UpdateCheckService _check;
    private readonly UpdateCheckClient _client;
    private readonly UpdateDownloadFiles _files;
    private readonly IClock _clock;
    private readonly Func<RotatingFileLogger> _logger;
    private readonly UpdateDownloadLimits _limits;
    private readonly object _gate = new();

    private UpdateDownloadSnapshot _current = UpdateDownloadSnapshot.Idle;
    private Job? _job;
    private long _generation;
    private long? _withdrawn;
    private TimeSpan? _launchedAt;
    private TimeSpan? _verifiedAt;
    private bool _disposed;

    /// <summary>Creates the service. Sends nothing and touches no file.</summary>
    /// <param name="check">The update check: whether it is switched on, and the newest version it learned.</param>
    /// <param name="client">Sends the two requests of a download; the kill switch is read through it.</param>
    /// <param name="files">The <c>updates</c> folder in the Collector's data directory.</param>
    /// <param name="clock">Measures how long a download took, for the log, and the start interval.</param>
    /// <param name="logger">The local diagnostic log, read at every write; none when null.</param>
    /// <param name="limits">
    /// Size cap, time limits and start interval; <see cref="UpdateDownloadLimits.Default"/> when null.
    /// </param>
    public UpdateDownloadService(
        UpdateCheckService check,
        UpdateCheckClient client,
        UpdateDownloadFiles files,
        IClock clock,
        Func<RotatingFileLogger>? logger = null,
        UpdateDownloadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);

        _check = check;
        _client = client;
        _files = files;
        _clock = clock;
        _logger = logger ?? (static () => RotatingFileLogger.Disabled);
        _limits = limits ?? UpdateDownloadLimits.Default;
    }

    /// <summary>
    /// Called on the background task when a download or a re-check has ended, before its outcome is published. A seam
    /// for the tests that cancel or withdraw in the instant before READY; null in the shipping Collector.
    /// </summary>
    internal Action? BeforeOutcome { get; set; }

    /// <summary>The background task of the last download or re-check started; null until one is.</summary>
    internal Task? Running
    {
        get
        {
            lock (_gate)
            {
                return _job?.Task;
            }
        }
    }

    /// <summary>The download as it stands. Touches nothing.</summary>
    public UpdateDownloadSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    /// <summary>
    /// <c>StartUpdateDownload</c>. Answers at once with the state it leaves behind: the running download when one is
    /// running; FAILED (DISABLED) when the check is switched off or the kill switch is set, and FAILED (NO_UPDATE)
    /// when no version newer than this build is known - <paramref name="reinstall"/> waives "newer", not "known" -
    /// in both cases with nothing sent; READY at once when the installer of that version was verified less than the
    /// start interval ago; the state as it stands, with nothing started, while the last download is still finishing
    /// or when one was started less than the start interval ago; VERIFYING when the installer of that version is
    /// READY and is being hashed once more; otherwise DOWNLOADING, with the download started on a background task.
    /// </summary>
    /// <param name="reinstall">Download the newest published version whatever this build is (maintainer tools).</param>
    public UpdateDownloadSnapshot Start(bool reinstall = false)
    {
        lock (_gate)
        {
            if (_disposed || _current.IsRunning)
            {
                return _current;
            }

            var check = _check.Snapshot();
            if (Refusal(check, reinstall) is { } refusal)
            {
                // FAILED keeps nothing usable: an installer that was READY is no longer offered.
                Discard(_current);
                _current = refusal;
                return _current;
            }

            var version = check.LatestVersion!;
            var ready = _current is { State: UpdateDownloadState.Ready } current && current.Version == version &&
                        _files.InstallerLength(version) == current.ReceivedBytes
                ? current
                : null;
            if (ready is null && _current.State == UpdateDownloadState.Ready)
            {
                // Its file has gone or changed, or a newer version is out: that READY no longer holds, even if
                // nothing new may start yet.
                _current = UpdateDownloadSnapshot.Idle;
            }

            if ((ready is not null && IsRecent(_verifiedAt)) || _job is { Task.IsCompleted: false } ||
                IsRecent(_launchedAt))
            {
                return _current;
            }

            Launch(version, reinstall, ready);
            return _current;
        }
    }

    /// <summary>
    /// <c>CancelUpdateDownload</c>. Stops a running download: IDLE at once, the read in flight abandoned and the
    /// <c>.part</c> removed as the background task unwinds. Changes nothing in any other state.
    /// </summary>
    public UpdateDownloadSnapshot Cancel()
    {
        Job? job;
        lock (_gate)
        {
            if (!_current.IsRunning)
            {
                return _current;
            }

            job = _job;
            _generation++;
            _current = UpdateDownloadSnapshot.Idle;
        }

        // Outside the lock: cancelling can run the download's continuation inline, and that takes the lock too.
        job?.TryCancel();
        return UpdateDownloadSnapshot.Idle;
    }

    /// <summary>
    /// The update check was switched off, and then nothing is offered (docs/privacy-boundary.md §8.4). A running
    /// download or re-check is stopped the way <see cref="Cancel"/> stops it, an installer that was READY - or was
    /// being checked again - is deleted, and the download reports FAILED (DISABLED) with a sentence saying so.
    /// Changes nothing when no download was running or ready. Answers at once.
    /// </summary>
    public UpdateDownloadSnapshot Withdraw()
    {
        Job? job = null;
        UpdateDownloadSnapshot withdrawn;
        lock (_gate)
        {
            var previous = _current;
            if (_disposed || !(previous.IsRunning || previous.State == UpdateDownloadState.Ready))
            {
                return previous;
            }

            if (previous.IsRunning)
            {
                // The background task deletes what it wrote when it unwinds; DiscardIfWithdrawn the rest.
                job = _job;
                _withdrawn = _generation;
                _generation++;
            }
            else
            {
                Discard(previous);
            }

            _current = withdrawn = UpdateDownloadSnapshot.Failed(
                previous.Version, UpdateDownloadFailure.Disabled, UpdateDownloadMessages.Withdrawn);
        }

        // Outside the lock, as in Cancel.
        job?.TryCancel();
        return withdrawn;
    }

    /// <summary>
    /// Removes what an earlier run left in <c>updates\</c> - <c>.part</c> files, installers of versions no longer
    /// offered - and only that (<see cref="UpdateDownloadFiles.RemoveLeftovers"/>). Called once at startup; the
    /// installer of the newest version, while it is still newer than this build, stays.
    /// </summary>
    /// <returns>How many files were removed.</returns>
    public int RemoveLeftovers()
    {
        lock (_gate)
        {
            if (_disposed || _current.IsRunning)
            {
                return 0;
            }
        }

        var check = _check.Snapshot();
        return _files.RemoveLeftovers(check.UpdateAvailable ? check.LatestVersion : null);
    }

    /// <summary>
    /// Stops the Collector's side: a running download is cancelled the way <see cref="Cancel"/> does it and waited for,
    /// up to <see cref="StopTimeout"/>, so the process does not exit with a partial file. Nothing starts afterwards.
    /// </summary>
    public void Dispose()
    {
        Job? job;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            if (_current.IsRunning)
            {
                _current = UpdateDownloadSnapshot.Idle;
            }

            job = _job;
        }

        if (job is null)
        {
            return;
        }

        job.TryCancel();
        try
        {
            job.Task.Wait(StopTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The task handles its own failures; a wait that faults anyway must not stop the rest of the shutdown.
        }
    }

    private UpdateDownloadSnapshot? Refusal(UpdateCheckSnapshot check, bool reinstall)
    {
        if (_client.IsDisabledNow)
        {
            return UpdateDownloadSnapshot.Failed(null, UpdateDownloadFailure.Disabled);
        }

        if (!check.Enabled)
        {
            return UpdateDownloadSnapshot.Failed(null, UpdateDownloadFailure.Disabled, UpdateDownloadMessages.SettingOff);
        }

        return check.LatestVersion is null || (!reinstall && !check.UpdateAvailable)
            ? UpdateDownloadSnapshot.Failed(null, UpdateDownloadFailure.NoUpdate)
            : null;
    }

    private void Discard(UpdateDownloadSnapshot previous)
    {
        if (previous is { State: UpdateDownloadState.Ready, Version: { } version })
        {
            _files.DiscardInstaller(version);
        }
    }

    // Called under the lock. A moment ago means less than the start interval on the monotonic clock.
    private bool IsRecent(TimeSpan? at) => at is { } moment && _clock.Elapsed - moment < _limits.StartInterval;

    // Called under the lock, and only once the last task has ended (Start), so no two tasks ever touch updates\ at the
    // same time. The task is not handed the token: one cancelled before it ran would never run its body, and the body
    // is what removes the partial file.
    private void Launch(string version, bool reinstall, UpdateDownloadSnapshot? ready)
    {
        var generation = ++_generation;
        var cancel = new CancellationTokenSource();
        _launchedAt = _clock.Elapsed;
        _current = ready is null
            ? UpdateDownloadSnapshot.Downloading(version, 0, null)
            : UpdateDownloadSnapshot.Verifying(version, ready.ReceivedBytes ?? 0, ready.TotalBytes);
        var task = Task.Run(() => RunAsync(generation, version, reinstall, ready, cancel));
        _job = new Job(task, cancel);
    }

    private async Task RunAsync(
        long generation,
        string version,
        bool reinstall,
        UpdateDownloadSnapshot? ready,
        CancellationTokenSource cancel)
    {
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (ready is not null && await IsIntactAsync(ready, cancel.Token).ConfigureAwait(false))
            {
                BeforeOutcome?.Invoke();
                Publish(generation, ready);
                return;
            }

            await DownloadAsync(generation, version, reinstall, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancel, Withdraw or Dispose has already reported the state.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A download is a background convenience and must never surface as an unobserved task exception.
            _logger().WriteError(LogComponent, "update_download_crashed", ex);
            Publish(generation, UpdateDownloadSnapshot.Failed(version, UpdateDownloadFailure.TransportFailed));
        }
        finally
        {
            DiscardIfWithdrawn(generation, version);
            cancel.Dispose();
        }
    }

    // Withdraw took this task's download away while it ran: an installer it was checking again, or had just verified
    // and renamed, goes too. Nothing else can have written that name since, because no task starts before this ends.
    private void DiscardIfWithdrawn(long generation, string version)
    {
        lock (_gate)
        {
            if (_withdrawn == generation)
            {
                _files.DiscardInstaller(version);
            }
        }
    }

    private async Task<bool> IsIntactAsync(UpdateDownloadSnapshot ready, CancellationToken cancellationToken) =>
        ready.Sha256 is { } expected &&
        await _files.HashInstallerAsync(ready.Version!, cancellationToken).ConfigureAwait(false) == expected;

    private async Task DownloadAsync(long generation, string version, bool reinstall, CancellationToken cancellation)
    {
        Publish(generation, UpdateDownloadSnapshot.Downloading(version, 0, null));
        var started = _clock.Elapsed;
        _logger().Write(LogLevel.Info, LogComponent, "update_download_started", new Dictionary<string, object?>
        {
            ["version"] = version,
            ["reinstall"] = reinstall,
        });

        var transfer = new UpdateDownloadTransfer(
            _client, _files, _limits, version, snapshot => Publish(generation, snapshot));
        UpdateDownloadSnapshot outcome;
        string? errorType = null;
        try
        {
            outcome = await transfer.RunAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            LogFinished(version, "CANCELLED", transfer.ReceivedBytes, started, null);
            throw;
        }
        catch (UpdateDownloadFailedException failed)
        {
            outcome = UpdateDownloadSnapshot.Failed(version, failed.Failure);
            errorType = failed.ErrorType;
        }

        BeforeOutcome?.Invoke();
        if (!Publish(generation, outcome) && outcome.FilePath is not null)
        {
            // Cancelled, or the Collector stopping, between the rename and this line: what IDLE reports is what
            // stays on disk, so the installer goes too.
            _files.DiscardInstaller(version);
            LogFinished(version, "CANCELLED", transfer.ReceivedBytes, started, null);
            return;
        }

        var token = outcome.Failure is { } failure
            ? EnumWire<UpdateDownloadFailure>.Format(failure)
            : EnumWire<UpdateDownloadState>.Format(outcome.State);
        LogFinished(version, token, transfer.ReceivedBytes, started, errorType);
    }

    private void LogFinished(string version, string outcome, long bytes, TimeSpan started, string? errorType)
    {
        var fields = new Dictionary<string, object?>
        {
            ["version"] = version,
            ["outcome"] = outcome,
            ["bytes"] = bytes,
            ["duration_ms"] = (long)Math.Max(0, (_clock.Elapsed - started).TotalMilliseconds),
        };
        if (errorType is not null)
        {
            fields["error_type"] = errorType;
        }

        _logger().Write(outcome is "READY" or "CANCELLED" ? LogLevel.Info : LogLevel.Warn, LogComponent,
            "update_download_finished", fields);
    }

    // A state from a download that has since been cancelled, replaced or stopped is dropped; false then.
    private bool Publish(long generation, UpdateDownloadSnapshot next)
    {
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return false;
            }

            _current = next;
            if (next.State == UpdateDownloadState.Ready)
            {
                _verifiedAt = _clock.Elapsed;
            }

            return true;
        }
    }

    private sealed record Job(Task Task, CancellationTokenSource Cancellation)
    {
        public void TryCancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The download had already ended and released its source.
            }
            catch (AggregateException)
            {
                // A callback failed while the read was being abandoned; the task still unwinds and cleans up.
            }
        }
    }
}
