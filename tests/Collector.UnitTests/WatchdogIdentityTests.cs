using System.Diagnostics;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Parent identity in the watchdog: the process bearing the parent id must be the process the
/// Collector was told to watch.
///
/// Windows reuses process ids as soon as they are free, so an id alone can name an unrelated
/// program and tie the recorder's lifetime to it. The pair (id, start time) is unique in
/// practice: a mismatch means the named parent is already gone, and the watchdog must then
/// take no action at all (review finding M8).
/// </summary>
public sealed class WatchdogIdentityTests : IDisposable
{
    private readonly List<Process> _helpers = new();

    [Fact]
    public void TheStartTimeIsAcceptedAsTicksAndAsAnIsoTimestamp()
    {
        var ticks = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero).UtcTicks;
        var byTicks = CommandLineOptions.Parse(
            new[] { "--serve", "--parent-pid", "1234", "--parent-start-time", ticks.ToString() });
        Assert.Equal(ticks, byTicks.ParentStartTime!.Value.UtcTicks);

        var byText = CommandLineOptions.Parse(
            new[] { "--serve", "--parent-pid", "1234", "--parent-start-time", "2026-09-08T10:00:00.000Z" });
        Assert.Equal(ticks, byText.ParentStartTime!.Value.UtcTicks);
    }

    [Fact]
    public void OmittingTheStartTimeStaysBackwardCompatible()
    {
        var options = CommandLineOptions.Parse(new[] { "--serve", "--parent-pid", "1234" });

        Assert.Equal(1234, options.ParentProcessId);
        Assert.Null(options.ParentStartTime);
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("-1")]
    [InlineData("")]
    public void AnUnreadableStartTimeIsRefused(string value)
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(
            new[] { "--serve", "--parent-pid", "1234", "--parent-start-time", value }));
    }

    [Fact]
    public void TheStartTimeWithoutAParentIdIsRefused()
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(
            new[] { "--serve", "--parent-start-time", "2026-09-08T10:00:00.000Z" }));
    }

    [Fact]
    public async Task AMismatchedStartTimeIsTreatedAsAnUnobservableParent()
    {
        var parent = StartIdleHelper();
        var stops = 0;
        using var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => Interlocked.Increment(ref stops),
            parentStartTime: new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero),
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.Unobservable);
        Assert.False(watchdog.ParentExited);
        Assert.Equal(0, Volatile.Read(ref stops));
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
    /// The same instant stated in UTC, the form <c>--parent-start-time</c> carries, still
    /// matches the running process.
    /// </summary>
    [Fact]
    public async Task AStartTimeStatedInUtcMatchesTheSameRunningProcess()
    {
        var parent = StartIdleHelper();
        var stops = 0;
        using var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => Interlocked.Increment(ref stops),
            parentStartTime: ParentProcessWatchdog.ToUtcInstant(parent.StartTime),
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.False(watchdog.Unobservable);
        Assert.True(watchdog.ParentExited);
        Assert.Equal(1, Volatile.Read(ref stops));
    }

    [Fact]
    public async Task AMatchingStartTimeWatchesTheParentAsBefore()
    {
        var parent = StartIdleHelper();
        var stops = 0;
        using var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => Interlocked.Increment(ref stops),
            parentStartTime: new DateTimeOffset(parent.StartTime),
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.False(watchdog.Unobservable);
        Assert.True(watchdog.ParentExited);
        Assert.Equal(1, Volatile.Read(ref stops));
    }

    [Fact]
    public async Task TheHardExitReportsAFailedShutdownWithANonZeroCode()
    {
        // The hard exit code must be non-zero: 0 reports a clean shutdown to the Desktop, to
        // scripts and to the operating system's job accounting, when the graceful stop in fact
        // hung and the process was cut short.
        var parent = StartIdleHelper();
        var codes = new List<int>();
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
            });

        watchdog.Start();
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        lock (codes)
        {
            Assert.Single(codes);
            Assert.Equal(ParentProcessWatchdog.HardExitCode, codes[0]);
            Assert.NotEqual(0, codes[0]);
        }
    }

    private Process StartIdleHelper()
    {
        var info = new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
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
