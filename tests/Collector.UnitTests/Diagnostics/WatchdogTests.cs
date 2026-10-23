using System.Diagnostics;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The parent-process watchdog, and the command-line switch that turns it on.
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
        var stops = 0;
        using var watchdog = new ParentProcessWatchdog(
            parent.Id,
            requestStop: () => Interlocked.Increment(ref stops),
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();

        // While the parent lives the Collector must not be stopped: an early stop is worse
        // than no watchdog at all.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref stops));
        Assert.False(watchdog.ParentExited);

        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.ParentExited);
        Assert.False(watchdog.Unobservable);
        Assert.Equal(1, Volatile.Read(ref stops));
    }

    [Fact]
    public async Task AParentThatIsAlreadyGoneStopsTheCollectorAtOnce()
    {
        // The Desktop can die between spawning the Collector and the Collector getting far
        // enough to look, so a watch that starts late must still notice.
        var parent = StartIdleHelper();
        var deadPid = parent.Id;
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        var stops = 0;
        using var watchdog = new ParentProcessWatchdog(
            deadPid,
            requestStop: () => Interlocked.Increment(ref stops),
            hardExitDelay: TimeSpan.FromMinutes(5),
            hardExit: _ => throw new InvalidOperationException("the hard exit must not fire here"));

        watchdog.Start();
        await watchdog.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.True(watchdog.ParentExited);
        Assert.Equal(1, Volatile.Read(ref stops));
    }

    [Fact]
    public async Task TheHardExitFiresWhenTheGracefulStopDoesNotFinishInTime()
    {
        // The graceful path can wedge: a blocked capture read, a pipe write to a peer that is
        // gone. No Collector may outlive its Desktop, so the fallback is unconditional.
        var parent = StartIdleHelper();
        var deadPid = parent.Id;
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

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

        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, Volatile.Read(ref stops));
        Assert.False(watchdog.ParentExited);
    }

    // ----------------------------------------------------------------- the switch ----

    [Fact]
    public void ServeAcceptsAParentPidAndEveryOtherModeRefusesIt()
    {
        var options = CommandLineOptions.Parse(new[] { "--serve", "--db", "x.db", "--parent-pid", "4242" });
        Assert.Equal(CollectorMode.Serve, options.Mode);
        Assert.Equal(4242, options.ParentProcessId);

        // Omitted means "answer to nobody", which is what a Collector started by hand does.
        Assert.Null(CommandLineOptions.Parse(new[] { "--serve" }).ParentProcessId);

        // A one-shot mode that accepted the switch would let a caller believe it was being
        // supervised when there is nothing to supervise.
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--version", "--parent-pid", "4242" }));
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--capture-doctor", "--parent-pid", "4242" }));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-pid")]
    [InlineData("99999999999999999999")]
    public void AProcessIdThatCouldNotBeOneIsRefused(string value)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--serve", "--parent-pid", value }));
    }

    [Fact]
    public void AMissingProcessIdIsRefusedRatherThanTreatedAsZero()
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(new[] { "--serve", "--parent-pid" }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ParentProcessWatchdog(0, () => { }));
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
