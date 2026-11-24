using System.Diagnostics;
using System.Runtime.CompilerServices;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The parent-process watchdog. The command-line switches that turn it on are parsed in
/// <see cref="CommandLineOptionsTests"/>.
///
/// It covers one failure the Desktop cannot handle itself: being killed outright. A
/// <c>Stop-Process -Force</c> runs none of the Desktop's shutdown code, so the Collector it
/// started would keep serving a pipe with no client, hold the per-user serve lease, and the
/// next Desktop launch would "reuse" a process the user cannot see.
///
/// The tests drive a real child process rather than an abstraction over one, because the
/// mechanism under test <em>is</em> the operating system's notion of a process ending.
/// </summary>
public sealed class WatchdogTests : IDisposable
{
    private readonly List<Process> _helpers = new();

    [Fact]
    public async Task ALiveParentIsLeftAloneAndADeadOneStopsTheCollector()
    {
        var parent = StartIdleHelper();
        var stops = new StrongBox<int>();
        using var watchdog = Watch(parent.Id, stops);

        watchdog.Start();

        // While the parent lives the Collector must not be stopped: an early stop is worse
        // than no watchdog at all.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref stops.Value));
        Assert.False(watchdog.ParentExited);

        await KillAsync(parent);

        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.ParentExited);
        Assert.False(watchdog.Unobservable);
        Assert.Equal(1, Volatile.Read(ref stops.Value));
    }

    [Fact]
    public async Task AParentThatIsAlreadyGoneStopsTheCollectorAtOnce()
    {
        // The Desktop can die between spawning the Collector and the Collector getting far
        // enough to look, so a watch that starts late must still notice.
        var parent = StartIdleHelper();
        var deadPid = parent.Id;
        await KillAsync(parent);

        var stops = new StrongBox<int>();
        using var watchdog = Watch(deadPid, stops);

        watchdog.Start();
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.ParentExited);
        Assert.Equal(1, Volatile.Read(ref stops.Value));
    }

    [Fact]
    public async Task TheHardExitFiresWhenTheGracefulStopDoesNotFinishInTime()
    {
        // The graceful path can wedge: a blocked capture read, a pipe write to a peer that is
        // gone. No Collector may outlive its Desktop, so the fallback is unconditional.
        var parent = StartIdleHelper();
        var deadPid = parent.Id;
        await KillAsync(parent);

        using var exited = new SemaphoreSlim(0);
        var exitCode = -1;
        using var watchdog = new ParentProcessWatchdog(
            deadPid,
            requestStop: () => { /* deliberately does nothing: this is the wedged case */ },
            hardExitDelay: TimeSpan.FromMilliseconds(200),
            hardExit: code =>
            {
                exitCode = code;
                exited.Release();
            });

        watchdog.Start();

        Assert.True(
            await exited.WaitAsync(TimeSpan.FromSeconds(15)),
            "the hard exit never fired after the graceful stop failed to finish");

        // Non-zero on purpose: this path cuts the process short with the database closed by
        // the runtime rather than by the Collector, so a launcher, a script or the operating
        // system's job accounting must not read it as a clean shutdown.
        Assert.Equal(ParentProcessWatchdog.HardExitCode, exitCode);
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task DisposingTheWatchdogStopsItWatching()
    {
        // A Collector shutting down for its own reasons disposes the watchdog on the way out.
        // If the watch stayed running, the parent's later exit would fire a stop request at an
        // object graph that is already gone.
        var parent = StartIdleHelper();
        var stops = 0;
        var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => Interlocked.Increment(ref stops),
            hardExitDelay: TimeSpan.FromMilliseconds(50),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();
        watchdog.Dispose();

        await KillAsync(parent);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, Volatile.Read(ref stops));
        Assert.False(watchdog.ParentExited);
    }

    [Fact]
    public void AParentIdOfZeroIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParentProcessWatchdog(0, () => { }));

    // ------------------------------------------------------------ parent identity ----
    // Parent identity in the watchdog: the process bearing the parent id must be the process the
    // Collector was told to watch.
    //
    // Windows reuses process ids as soon as they are free, so an id alone can name an unrelated
    // program and tie the recorder's lifetime to it. The pair (id, start time) is unique in
    // practice: a mismatch means the named parent is already gone, and the watchdog must then
    // take no action at all (review finding M8).

    [Fact]
    public async Task AMismatchedStartTimeIsTreatedAsAnUnobservableParent()
    {
        var parent = StartIdleHelper();
        var stops = new StrongBox<int>();
        using var watchdog = Watch(parent.Id, stops, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));

        watchdog.Start();
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.Unobservable);
        Assert.False(watchdog.ParentExited);
        Assert.Equal(0, Volatile.Read(ref stops.Value));
    }

    /// <summary>
    /// The start time must be compared as an absolute instant.
    ///
    /// Process.StartTime is a local reading with no offset, and inside the repeated hour of a
    /// daylight-saving fall-back an ambiguous local time can resolve to the wrong offset,
    /// shifting it by an hour. The one-second tolerance then rejects the match, the watchdog
    /// treats the id as recycled and stops watching, and a killed Desktop leaves an orphaned
    /// Collector (review finding L-5).
    /// </summary>
    [Fact]
    public void TheStartTimeIsReadAsAnAbsoluteUtcInstant()
    {
        var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Local);

        var instant = ParentProcessWatchdog.ToUtcInstant(local);

        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(DateTimeKind.Utc, instant.UtcDateTime.Kind);
        Assert.Equal(local.ToUniversalTime().Ticks, instant.UtcTicks);
    }

    /// <summary>
    /// Inside a daylight-saving fall-back one local reading stands for two instants an hour
    /// apart, and every conversion back to UTC picks standard time. A parent started during the
    /// daylight half of that hour then reads an hour late, the tolerance rejects it, and the
    /// watchdog stops watching, leaving the orphaned Collector it exists to prevent
    /// (review findings L-5 and R-12).
    /// </summary>
    [Fact]
    public void AParentStartedInsideARepeatedHourIsStillRecognised()
    {
        var zone = FallBackZone();
        var wall = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        Assert.True(zone.IsAmbiguousTime(wall));

        var candidates = ParentProcessWatchdog.CandidateInstants(wall, zone);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(TimeSpan.FromHours(1), (candidates[0] - candidates[1]).Duration());

        foreach (var candidate in candidates)
        {
            Assert.True(
                ParentProcessWatchdog.StartTimeMatches(wall, candidate, zone),
                "both readings of the repeated hour are the same process");
        }

        // A genuinely different instant is still a mismatch, so recycling detection stands.
        Assert.False(ParentProcessWatchdog.StartTimeMatches(wall, candidates[0].AddHours(3), zone));
    }

    /// <summary>An unambiguous reading resolves to exactly one instant.</summary>
    [Fact]
    public void AnOrdinaryStartTimeHasOneCandidateInstant()
    {
        var zone = FallBackZone();
        var wall = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var candidate = Assert.Single(ParentProcessWatchdog.CandidateInstants(wall, zone));

        Assert.Equal(new DateTimeOffset(wall, TimeSpan.FromHours(-4)).ToUniversalTime(), candidate);
        Assert.False(ParentProcessWatchdog.StartTimeMatches(wall, candidate.AddMinutes(5), zone));
    }

    /// <summary>
    /// The start time matches the same running process whether it is stated in UTC, the form
    /// <c>--parent-start-time</c> carries, or as the local reading.
    /// </summary>
    /// <param name="statedInUtc">Whether the start time is given as a UTC instant.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStartTimeMatchingTheRunningProcessWatchesTheParent(bool statedInUtc)
    {
        var parent = StartIdleHelper();
        var stops = new StrongBox<int>();
        var startTime = statedInUtc
            ? ParentProcessWatchdog.ToUtcInstant(parent.StartTime)
            : new DateTimeOffset(parent.StartTime);
        using var watchdog = Watch(parent.Id, stops, startTime);

        watchdog.Start();
        await KillAsync(parent);
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.False(watchdog.Unobservable);
        Assert.True(watchdog.ParentExited);
        Assert.Equal(1, Volatile.Read(ref stops.Value));
    }

    [Fact]
    public async Task TheHardExitReportsAFailedShutdownWithANonZeroCode()
    {
        // The hard exit code must be non-zero: 0 reports a clean shutdown to the Desktop, to
        // scripts and to the operating system's job accounting, when the graceful stop in fact
        // hung and the process was cut short.
        var parent = StartIdleHelper();
        var codes = new List<int>();
        using var fired = new SemaphoreSlim(0);
        using var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => { },
            hardExitDelay: TimeSpan.FromMilliseconds(50),
            hardExit: code =>
            {
                lock (codes)
                {
                    codes.Add(code);
                }

                fired.Release();
            });

        watchdog.Start();
        await KillAsync(parent);
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        // The hard exit runs on a timer after the wait above returns. A fixed 500 ms was enough on an
        // idle machine and not under the full suite (it failed the 1.2.4 packaging gate once), so the
        // callback is waited for, and then given time to prove it does not fire twice.
        Assert.True(
            await fired.WaitAsync(TimeSpan.FromSeconds(15)),
            "the hard exit never fired after the graceful stop failed to finish");
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        lock (codes)
        {
            Assert.Single(codes);
            Assert.Equal(ParentProcessWatchdog.HardExitCode, codes[0]);
            Assert.NotEqual(0, codes[0]);
        }
    }

    /// <summary>A watchdog with a five-minute hard exit that must not fire; each stop request is counted.</summary>
    private static ParentProcessWatchdog Watch(
        int parentId, StrongBox<int> stops, DateTimeOffset? parentStartTime = null) =>
        new(
            parentId,
            requestStop: () => Interlocked.Increment(ref stops.Value),
            parentStartTime: parentStartTime,
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

    /// <summary>Kills a helper and waits until it is gone.</summary>
    private static async Task KillAsync(Process parent)
    {
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
    }

    /// <summary>
    /// Starts a child that stays alive until it is killed.
    ///
    /// <c>cmd.exe</c> with its input redirected and never written to blocks reading its first
    /// command: alive on demand, gone on demand, with no timing race in between.
    /// </summary>
    private Process StartIdleHelper()
    {
        var info = new ProcessStartInfo(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var helper = Process.Start(info)
            ?? throw new InvalidOperationException("could not start the helper process");
        _helpers.Add(helper);
        return helper;
    }

    /// <summary>A zone with an ordinary northern-hemisphere fall-back, declared rather than read.</summary>
    private static TimeZoneInfo FallBackZone() => TimeZoneInfo.CreateCustomTimeZone(
        "MentorRecorder.Test",
        TimeSpan.FromHours(-5),
        "MentorRecorder Test",
        "MR Standard",
        "MR Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    public void Dispose()
    {
        foreach (var helper in _helpers)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(15_000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                helper.Dispose();
            }
        }
    }
}
