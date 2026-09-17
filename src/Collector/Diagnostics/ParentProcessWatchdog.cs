using System.ComponentModel;
using System.Diagnostics;

namespace MentorRecorder.Collector.Diagnostics;

/// <summary>
/// Watches the process that launched this one and asks the Collector to stop when it dies.
///
/// The Desktop starts the Collector as a child and stops it on the way out, but a Desktop
/// killed outright -- Task Manager, <c>Stop-Process -Force</c>, a lost session -- never gets
/// to. The Collector would then keep serving a pipe nobody is connected to, hold the per-user
/// serve lease, and have the next Desktop launch "reuse" a process the user cannot see.
///
/// The mechanism is deliberately the weakest one that works.
/// <see cref="Process.GetProcessById(int)"/> plus <see cref="Process.WaitForExitAsync"/> is a
/// process <em>existence</em> check and a wait on its lifetime, which is what
/// docs/privacy-boundary.md section 2 item 3b allows. There is no P/Invoke here, nothing in
/// this source opens a handle to another process, nothing touches anything's memory, and
/// nothing is read out of the parent -- not its command line, not its modules, not a byte of
/// it. The name of the forbidden Win32 call is deliberately not written anywhere in this
/// file: <c>tools/static-boundary-check</c> greps for it, and a rule that has to make
/// exceptions for comments is a rule with a hole in it.
///
/// Shutdown is graceful first: the watchdog raises the same request Ctrl+C raises, so capture
/// stops, the run in flight is closed through the ordinary lifecycle, the pipe server drains
/// and the host disposes. The hard exit covers only the case where that gets stuck -- a wedged
/// Npcap read, a pipe write to a peer that is gone -- because no Collector may outlive its
/// Desktop. Ten seconds is long enough for an orderly close of a SQLite database in WAL mode
/// and short enough that a user restarting the Desktop does not meet the previous instance
/// still holding the lease.
/// </summary>
public sealed class ParentProcessWatchdog : IDisposable
{
    /// <summary>How long the graceful stop is given before the process exits regardless.</summary>
    public static readonly TimeSpan DefaultHardExitDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Exit code used when the graceful stop did not finish and the process was cut short.
    ///
    /// It has to be non-zero: zero says "shut down cleanly", which a launcher, a script or the
    /// operating system's job accounting would believe of the one case where the database was
    /// closed by the runtime rather than by us.
    /// </summary>
    public const int HardExitCode = 5;

    /// <summary>
    /// How far the observed start time may differ from the one on the command line.
    ///
    /// The value crosses a command line as text and may lose sub-second precision on the way.
    /// A second is far tighter than any window in which an id could plausibly be recycled onto
    /// a different process, so it removes a whole class of spurious mismatches at no cost.
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly int _parentProcessId;
    private readonly DateTimeOffset? _parentStartTime;
    private readonly Action _requestStop;
    private readonly Action<int> _hardExit;
    private readonly Action<string, Exception?>? _log;
    private readonly TimeSpan _hardExitDelay;
    private readonly CancellationTokenSource _cancelled = new();
    private Task? _watch;
    private bool _disposed;

    /// <summary>Creates a watchdog. Nothing is observed until <see cref="Start"/> is called.</summary>
    /// <param name="parentProcessId">Process id given on the command line.</param>
    /// <param name="requestStop">Raises the same graceful stop that Ctrl+C raises.</param>
    /// <param name="parentStartTime">
    /// Start time of the process that id belongs to, when the caller knows it. Windows hands a
    /// freed process id straight back out, so an id on its own can name a stranger; the pair
    /// (id, start time) is unique in practice. Null skips the identity check: the argument is
    /// optional on the command line and older launchers do not pass it.
    /// </param>
    /// <param name="hardExitDelay">
    /// Grace period between the request and the unconditional exit; the default when null.
    /// </param>
    /// <param name="hardExit">
    /// Ends the process. Defaults to <see cref="Environment.Exit(int)"/>; tests substitute a
    /// recorder so the assertion does not take the test host down with it.
    /// </param>
    /// <param name="log">Optional diagnostic sink; never receives anything about the parent
    /// beyond the fact that it ended.</param>
    public ParentProcessWatchdog(
        int parentProcessId,
        Action requestStop,
        DateTimeOffset? parentStartTime = null,
        TimeSpan? hardExitDelay = null,
        Action<int>? hardExit = null,
        Action<string, Exception?>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parentProcessId, 0);
        ArgumentNullException.ThrowIfNull(requestStop);

        _parentProcessId = parentProcessId;
        _parentStartTime = parentStartTime;
        _requestStop = requestStop;
        _hardExitDelay = hardExitDelay ?? DefaultHardExitDelay;
        _hardExit = hardExit ?? Environment.Exit;
        _log = log;
    }

    /// <summary>Process id being watched.</summary>
    public int ParentProcessId => _parentProcessId;

    /// <summary>True once the parent has been observed to end.</summary>
    public bool ParentExited { get; private set; }

    /// <summary>
    /// True when the parent could not be watched at all -- the operating system refused to let
    /// this process wait on it. The Collector then keeps running: an unobservable parent is a
    /// reason to do nothing, never a reason to stop.
    /// </summary>
    public bool Unobservable { get; private set; }

    /// <summary>Starts watching. Returns immediately; the wait runs on a background task.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watch ??= Task.Run(WatchAsync, CancellationToken.None);
    }

    /// <summary>Awaits the watch task, for tests that need the outcome to have settled.</summary>
    /// <param name="cancellationToken">Cancels the wait, not the watch.</param>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        _watch is null ? Task.CompletedTask : _watch.WaitAsync(cancellationToken);

    private async Task WatchAsync()
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(_parentProcessId);
        }
        catch (ArgumentException)
        {
            // The parent was already gone when we looked. That is not an error condition; it
            // is the condition this class exists for, arriving early.
            OnParentEnded(alreadyGone: true);
            return;
        }
        catch (InvalidOperationException ex)
        {
            MarkUnobservable(ex);
            return;
        }

        using (parent)
        {
            if (!IsTheProcessWeWereToldAbout(parent))
            {
                return;
            }

            try
            {
                await parent.WaitForExitAsync(_cancelled.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // We are shutting down for our own reasons; the parent is fine.
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                MarkUnobservable(ex);
                return;
            }
        }

        OnParentEnded(alreadyGone: false);
    }

    /// <summary>
    /// Confirms that the process now wearing the id is the one the caller meant.
    ///
    /// A mismatch is treated as "unobservable" rather than as "the parent died": the two
    /// readings lead to opposite actions and only one is safe. Stopping would end a recording
    /// session on a guess about a stranger process, while doing nothing leaves the Collector
    /// running for a user who can close it themselves. Reading the start time needs no handle
    /// to the process's contents; it is the same process-listing fact as its existence
    /// (docs/privacy-boundary.md section 2, item 3b).
    /// </summary>
    /// <param name="parent">Process found under the configured id.</param>
    private bool IsTheProcessWeWereToldAbout(Process parent)
    {
        if (_parentStartTime is not DateTimeOffset expected)
        {
            return true;
        }

        try
        {
            if (StartTimeMatches(parent.StartTime, expected))
            {
                return true;
            }

            Unobservable = true;
            _log?.Invoke(
                "the process id was recycled onto another process; nothing is being watched", null);
            return false;
        }
        catch (Exception ex)
            when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            MarkUnobservable(ex);
            return false;
        }
    }

    /// <summary>
    /// Whether an observed start time can be the instant the launcher told us about.
    ///
    /// <see cref="Process.StartTime"/> is a local <see cref="DateTime"/>, and the conversion
    /// from the kernel's UTC creation time is not reversible inside the repeated hour of a
    /// daylight-saving fall-back, where one wall-clock reading stands for two real instants an
    /// hour apart. Every way of turning it back into an instant --
    /// <c>new DateTimeOffset(local)</c> and <c>local.ToUniversalTime()</c> alike -- resolves
    /// the ambiguity to standard time, so a process started during the daylight-saving half of
    /// that hour reads an hour late, the tolerance rejects it, and the watchdog stops watching
    /// the parent it exists to follow (review findings L-5, R-12).
    ///
    /// Both candidate instants are therefore accepted during an ambiguous hour. A false match
    /// would take an id reused onto a process started exactly one hour apart inside that one
    /// hour of the year, and costs no more than watching the right pid, while a false mismatch
    /// costs an orphaned Collector.
    /// </summary>
    /// <param name="processStartTime">Start time as the framework reported it.</param>
    /// <param name="expected">Instant the launcher passed on the command line.</param>
    /// <param name="zone">Time zone to interpret the reading in; the machine's when null.</param>
    internal static bool StartTimeMatches(
        DateTime processStartTime, DateTimeOffset expected, TimeZoneInfo? zone = null)
    {
        foreach (var candidate in CandidateInstants(processStartTime, zone))
        {
            if ((candidate - expected).Duration() <= StartTimeTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every absolute instant a local start-time reading could stand for. One, except inside
    /// a daylight-saving fall-back, where the same wall clock is shown twice.
    /// </summary>
    /// <param name="processStartTime">Start time as the framework reported it.</param>
    /// <param name="zone">Time zone to interpret the reading in; the machine's when null.</param>
    internal static IReadOnlyList<DateTimeOffset> CandidateInstants(
        DateTime processStartTime, TimeZoneInfo? zone = null)
    {
        var timeZone = zone ?? TimeZoneInfo.Local;
        var wall = DateTime.SpecifyKind(processStartTime, DateTimeKind.Unspecified);

        if (!timeZone.IsAmbiguousTime(wall))
        {
            return new[] { new DateTimeOffset(wall, timeZone.GetUtcOffset(wall)).ToUniversalTime() };
        }

        var offsets = timeZone.GetAmbiguousTimeOffsets(wall);
        var candidates = new DateTimeOffset[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
        {
            candidates[i] = new DateTimeOffset(wall, offsets[i]).ToUniversalTime();
        }

        return candidates;
    }

    /// <summary>
    /// Reads a process start time as an absolute instant, resolving an ambiguous hour the way
    /// the framework does. Kept for callers that need one answer rather than every candidate;
    /// the identity check uses <see cref="StartTimeMatches"/> instead.
    /// </summary>
    /// <param name="processStartTime">Start time as the framework reported it.</param>
    internal static DateTimeOffset ToUtcInstant(DateTime processStartTime) =>
        new(DateTime.SpecifyKind(processStartTime.ToUniversalTime(), DateTimeKind.Utc));

    private void MarkUnobservable(Exception error)
    {
        Unobservable = true;
        _log?.Invoke("parent process cannot be watched; the collector will keep running", error);
    }

    private void OnParentEnded(bool alreadyGone)
    {
        if (_cancelled.IsCancellationRequested)
        {
            return;
        }

        ParentExited = true;
        _log?.Invoke(
            alreadyGone
                ? "parent process was already gone at startup; stopping"
                : "parent process exited; stopping",
            null);

        try
        {
            _requestStop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A graceful stop that will not even start is precisely when the fallback matters.
            _log?.Invoke("the graceful stop request itself failed", ex);
        }

        ScheduleHardExit();
    }

    private void ScheduleHardExit()
    {
        var deadline = _hardExitDelay;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(deadline, _cancelled.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // The graceful stop finished and the watchdog was disposed on the way out,
                    // which is the wanted outcome; nothing is left to force. The disposed case
                    // is the same event seen from the other side, with the token's source gone
                    // between the check and the registration.
                    return;
                }

                _log?.Invoke("graceful stop did not finish in time; exiting", null);
                _hardExit(HardExitCode);
            },
            CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancelled.Cancel();
        _cancelled.Dispose();
    }
}
