using MentorRecorder.Collector.Capture;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Marks every test class that starts a real <c>MentorRecorder.Collector.exe</c>.
///
/// The serving process claims the current user's pipe name and single-instance lease, both per
/// user rather than per database, so two such classes running at once would fight over them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CollectorProcessCollection : ICollectionFixture<CollectorProcessFixture>
{
    /// <summary>Name shared by every test class in this collection.</summary>
    public const string Name = "collector-process";
}

/// <summary>Starts and stops the real Collector executable.</summary>
public sealed class CollectorProcessFixture
{
    /// <summary>Path of the executable the tests drive.</summary>
    public static string ExecutablePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "MentorRecorder.Collector.exe");

    /// <summary>
    /// How the Collector's redirected console output has to be decoded.
    ///
    /// UTF-8 unconditionally, because <c>Program.UseUtf8Console</c> makes the Collector write
    /// UTF-8 unconditionally regardless of the machine's ANSI code page. Decoding it here the
    /// way a real consumer must is what makes these tests evidence for that guarantee.
    /// </summary>
    public static Encoding ConsoleEncoding { get; } = new UTF8Encoding(false);

    /// <summary>Output of a Collector process that ran to completion.</summary>
    /// <param name="ExitCode">Process exit code.</param>
    /// <param name="StandardOutput">Everything written to standard output.</param>
    /// <param name="StandardError">Everything written to standard error.</param>
    public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Runs the Collector with <paramref name="arguments"/> and waits for it to exit.</summary>
    /// <param name="arguments">Command line arguments.</param>
    public static ProcessResult RunToCompletion(params string[] arguments)
    {
        using var process = Start(arguments);

        // Both pipes must be drained concurrently. Reading one to the end first deadlocks as
        // soon as the child fills the other: the child blocks on write, the test blocks on
        // read. The replay modes print a large JSON document, so this is not hypothetical.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(15_000);
            throw new TimeoutException(
                "the Collector did not exit within 60s: " + string.Join(' ', arguments));
        }

        // WaitForExit(int) does not wait for the redirected readers to finish; give them a
        // bounded moment to observe end of stream now that the child is gone.
        Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(15));
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Starts <c>--serve --json</c> and waits for its startup line.</summary>
    /// <param name="databasePath">Database the server should open.</param>
    public async Task<ServingCollector> ServeAsync(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        return await ServingCollector.StartAsync(databasePath).ConfigureAwait(false);
    }

    internal static Process Start(params string[] arguments)
    {
        Assert.True(
            File.Exists(ExecutablePath),
            "the Collector executable must sit next to the test binaries: " + ExecutablePath);

        var info = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)
            ?? throw new InvalidOperationException("could not start " + ExecutablePath);
    }
}

/// <summary>A Collector process that is serving its Named Pipe right now.</summary>
public sealed class ServingCollector : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderrDrain;
    private bool _stopped;

    private ServingCollector(Process process, JsonObject startup, Task<string> stderrDrain)
    {
        _process = process;
        Startup = startup;
        _stderrDrain = stderrDrain;
    }

    /// <summary>Everything the served process wrote to stderr, once it has exited.</summary>
    public string StandardError =>
        _stderrDrain.IsCompletedSuccessfully ? _stderrDrain.Result : string.Empty;

    /// <summary>Fragment of the refusal a second instance prints; see Program.AcquireServeLease.</summary>
    public const string LeaseRefusalMarker = "已在本机运行";

    /// <summary>The <c>--serve --json</c> startup line, parsed.</summary>
    public JsonObject Startup { get; }

    /// <summary>Operating system process id, for the listener check.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Pipe name the server reported.</summary>
    public string PipeName => Startup["pipe_name"]!.GetValue<string>();

    /// <summary>Number of unfinished runs the startup recovery pass repaired.</summary>
    public int RecoveredRuns => Startup["recovered_runs"]!.GetValue<int>();

    /// <summary>True while the process is still running.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>Exit code, once the process has ended.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>Waits for the process to end on its own.</summary>
    /// <param name="timeout">Longest wait before giving up.</param>
    /// <returns>True when it ended within the timeout.</returns>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            _stopped = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>How long to keep trying when the single-instance lease is held by someone else.</summary>
    public static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(30);

    internal static async Task<ServingCollector> StartAsync(string databasePath)
    {
        // The lease is per user, not per database, so anything else serving as this user blocks
        // this start. A short wait absorbs the transient case, the previous test's process
        // finishing its shutdown; anything else is not ours to kill and fails with a message
        // naming the holder.
        var deadline = DateTime.UtcNow + LeaseWait;
        while (true)
        {
            var attempt = await TryStartAsync(databasePath).ConfigureAwait(false);
            if (attempt.Serving is not null)
            {
                return attempt.Serving;
            }

            if (!attempt.LeaseHeld || DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(attempt.Failure!);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    private static async Task<(ServingCollector? Serving, bool LeaseHeld, string? Failure)> TryStartAsync(
        string databasePath)
    {
        var process = CollectorProcessFixture.Start("--serve", "--db", databasePath, "--json");
        try
        {
            var line = await ReadLineAsync(process, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                TryKill(process);

                if (error.Contains(LeaseRefusalMarker, StringComparison.Ordinal))
                {
                    return (null, true, DescribeLeaseHolder());
                }

                return (null, false, "the Collector never printed its startup line. stderr: " + error);
            }

            var startup = JsonNode.Parse(line)?.AsObject();
            if (startup is null)
            {
                TryKill(process);
                return (null, false, "startup line was not a JSON object: " + line);
            }

            // Keep draining stderr for the rest of this process's life. The serve path writes
            // diagnostics to the rotating log instead, but an undrained pipe stalls the child
            // the moment anything does write to it.
            var drain = process.StandardError.ReadToEndAsync();
            return (new ServingCollector(process, startup, drain), false, null);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Names whoever is holding the lease, so the failure is actionable.</summary>
    private static string DescribeLeaseHolder()
    {
        var holders = new List<string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("MentorRecorder.Collector"))
            {
                using (process)
                {
                    holders.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"pid {process.Id} started {process.StartTime:HH:mm:ss}"));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Enumerating processes is best effort; the message below still stands.
        }

        return
            "another MentorRecorder.Collector.exe is already serving as this user and still held " +
            $"the single-instance lease after {LeaseWait.TotalSeconds:F0}s" +
            (holders.Count > 0 ? " (" + string.Join(", ", holders) + ")" : string.Empty) +
            ". These tests drive the real executable, and the lease is per user rather than per " +
            "database, so nothing else may be serving while they run. Stop it and run again: " +
            "Get-Process MentorRecorder.Collector | Stop-Process -Force";
    }

    /// <summary>Opens a client connected to this server's pipe.</summary>
    public async Task<PipeClient> ConnectAsync()
    {
        var client = new PipeClient(PipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Kills the process outright. An abrupt death is the condition crash recovery exists for;
    /// a graceful stop would not exercise it.
    /// </summary>
    public Task KillAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            TryKill(_process);
        }

        return Task.CompletedTask;
    }

    /// <summary>Local TCP endpoints owned by this process, as the operating system sees them.</summary>
    public IReadOnlyList<string> ListeningTcpEndpoints()
    {
        // netstat is what docs/privacy-boundary.md section 9 tells a user to run, so the test
        // checks exactly what the documentation promises they will see.
        using var netstat = Process.Start(new ProcessStartInfo("netstat", "-ano")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not run netstat");

        var output = netstat.StandardOutput.ReadToEnd();
        netstat.WaitForExit(30_000);

        var pid = ProcessId.ToString(CultureInfo.InvariantCulture);
        var owned = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || columns[^1] != pid)
            {
                continue;
            }

            owned.Add(line);
        }

        return owned;
    }

    private static async Task<string?> ReadLineAsync(Process process, TimeSpan timeout)
    {
        var read = process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == (Task)read ? await read.ConfigureAwait(false) : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(30_000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Already exiting.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await KillAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}

/// <summary>
/// What the shipping executable does about being started twice, and what the operating system
/// says it is listening on.
///
/// docs/privacy-boundary.md sections 8 and 9 invite a user to verify both claims, so they are
/// tested against the real process rather than an in-process object graph.
/// </summary>
[Collection(CollectorProcessCollection.Name)]
public sealed class LifecycleProcessTests : IDisposable
{
    private readonly CollectorProcessFixture _collector;
    private readonly string _directory;

    public LifecycleProcessTests(CollectorProcessFixture collector)
    {
        _collector = collector;
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.Lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task ASecondServeIsRefusedAndTheFirstKeepsServing()
    {
        var first = Path.Combine(_directory, "first.db");
        var second = Path.Combine(_directory, "second.db");

        await using var serving = await _collector.ServeAsync(first);
        Assert.True(serving.IsRunning);

        // A different database, so nothing but the per-user lease can refuse it.
        var refused = CollectorProcessFixture.RunToCompletion("--serve", "--db", second, "--json");

        // A distinct exit code, not merely a non-zero one: a launcher answers "already serving"
        // by connecting to the running instance, but must not answer a corrupt database or an
        // unwritable path (3) that way, and cannot parse text to tell them apart
        // (review finding M3).
        Assert.Equal(Program.ExitCodeAlreadyRunning, refused.ExitCode);
        Assert.Contains(ErrorCodes.AlreadyRunning, refused.StandardError, StringComparison.Ordinal);
        Assert.Contains(ServingCollector.LeaseRefusalMarker, refused.StandardError, StringComparison.Ordinal);
        Assert.Contains("请复用现有实例", refused.StandardError, StringComparison.Ordinal);

        // The refusal must not have created the second database: a Collector that cannot run
        // must not leave a half-initialised file behind.
        Assert.False(File.Exists(second), "a refused instance must not create its database");

        Assert.True(serving.IsRunning, "the refusal must not disturb the instance already serving");
        await using (var client = await serving.ConnectAsync())
        {
            var version = (await client.SendAsync("GetVersion", new JsonObject())).Require();
            Assert.Equal(Program.Version, version["collector_version"]!.GetValue<string>());
        }

        await serving.KillAsync();

        // Once the first process is gone the kernel drops its lease, so the next start works.
        // A lease that outlived its owner would make a crash unrecoverable without a reboot.
        await using var afterwards = await _collector.ServeAsync(second);
        Assert.True(afterwards.IsRunning);
        await afterwards.KillAsync();
    }

    [Fact]
    public async Task AServingCollectorOwnsNoTcpEndpointAtAll()
    {
        var databasePath = Path.Combine(_directory, "listeners.db");

        await using var serving = await _collector.ServeAsync(databasePath);

        // Prove the process really is serving before concluding anything from an empty list:
        // a dead process trivially owns no sockets.
        await using (var client = await serving.ConnectAsync())
        {
            (await client.SendAsync("GetVersion", new JsonObject())).Require();
        }

        var endpoints = serving.ListeningTcpEndpoints();

        Assert.True(
            endpoints.Count == 0,
            "the Collector must own no TCP endpoint, listening or established; netstat -ano " +
            "reported:" + Environment.NewLine + string.Join(Environment.NewLine, endpoints));

        await serving.KillAsync();
    }

    /// <summary>
    /// A Collector with no console can still be asked to stop.
    ///
    /// The Desktop launches it with <c>CREATE_NO_WINDOW</c> and stops it with
    /// <c>QProcess::terminate()</c>, which posts <c>WM_CLOSE</c> to top-level windows that a
    /// console-less child pumping no messages does not have, then kills it half a second later.
    /// The named stop event is therefore the only path on which graceful shutdown runs on a real
    /// machine (review finding H-2).
    /// </summary>
    [Fact]
    public async Task ANamedStopEventEndsAConsolelessCollectorGracefully()
    {
        var databasePath = Path.Combine(_directory, "stop-event.db");

        await using var serving = await _collector.ServeAsync(databasePath);
        await using (var client = await serving.ConnectAsync())
        {
            (await client.SendAsync("GetVersion", new JsonObject())).Require();
        }

        Assert.True(EventWaitHandle.TryOpenExisting(
            Program.StopEventName(serving.PipeName), out var stop),
            "a serving Collector must publish its graceful-stop event");

        using (stop)
        {
            stop.Set();
        }

        Assert.True(await serving.WaitForExitAsync(TimeSpan.FromSeconds(30)),
            "setting the stop event must end the process");
        Assert.Equal(0, serving.ExitCode);
    }

    /// <summary>
    /// The serving process writes its own id where a launcher can find it, and takes the file
    /// away again on a clean shutdown. Without it a launcher that finds the holder hung has
    /// nothing to act on (review finding H-5).
    /// </summary>
    [Fact]
    public async Task TheServingProcessNamesItselfInServePidAndRemovesItOnAGracefulStop()
    {
        var databasePath = Path.Combine(_directory, "pid.db");
        var pidFile = Path.Combine(_directory, "logs", Program.ServePidFileName);

        await using var serving = await _collector.ServeAsync(databasePath);
        await using (var client = await serving.ConnectAsync())
        {
            (await client.SendAsync("GetVersion", new JsonObject())).Require();
        }

        Assert.True(File.Exists(pidFile), "the serving process must name itself in " + pidFile);
        Assert.Equal(
            serving.ProcessId.ToString(CultureInfo.InvariantCulture),
            File.ReadAllText(pidFile).Trim());

        using (var stop = EventWaitHandle.OpenExisting(Program.StopEventName(serving.PipeName)))
        {
            stop.Set();
        }

        Assert.True(await serving.WaitForExitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(File.Exists(pidFile), "a clean shutdown must not leave a stale pid file");
    }

    /// <summary>
    /// A holder that never answers needs the opposite response from a holder to reuse, so it
    /// gets its own exit code. Reporting plain "already running" for both leaves the launcher
    /// restarting a child that exits the same way every time (review finding H-5).
    /// </summary>
    [Fact]
    public void APipeHeldBySomethingThatNeverAnswersExitsWithItsOwnCode()
    {
        var databasePath = Path.Combine(_directory, "unresponsive.db");
        var pipeName = "MentorRecorder.test." + Guid.NewGuid().ToString("N") + ".v1";

        // A pipe instance in the listening state that will never read a frame, let alone
        // answer one. This is what a wedged Collector looks like from outside.
        using var squatter = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var refused = CollectorProcessFixture.RunToCompletion(
            "--serve", "--db", databasePath, "--pipe", pipeName, "--json");

        Assert.Equal(Program.ExitCodeAlreadyRunningUnresponsive, refused.ExitCode);
        Assert.NotEqual(Program.ExitCodeAlreadyRunning, refused.ExitCode);
        Assert.Contains("没有响应", refused.StandardError, StringComparison.Ordinal);
        // The advice names the pid file that belongs to this pipe, not the per-user default a
        // harness Collector never writes (review finding R-10).
        Assert.Contains(
            Program.ServePidFileNameFor(pipeName), refused.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(databasePath), "a refused instance must not create its database");
    }

    /// <summary>
    /// An unusable log folder refuses to start rather than discarding every line in silence,
    /// which is indistinguishable from a quiet session (review finding L-2).
    /// </summary>
    [Fact]
    public void AnUnwritableLogFolderRefusesToStartTheSameWayAnUnwritableDatabaseDoes()
    {
        var databasePath = Path.Combine(_directory, "logdir.db");
        var occupied = Path.Combine(_directory, "not-a-folder");
        File.WriteAllText(occupied, "this is a file, not a log directory");

        var refused = CollectorProcessFixture.RunToCompletion(
            "--serve", "--db", databasePath, "--log-dir", occupied, "--json");

        Assert.Equal(3, refused.ExitCode);
        Assert.Contains("日志目录", refused.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(databasePath), "a refused instance must not create its database");
    }

    [Fact]
    public void TheVersionAndDoctorModesRunWithoutOpeningAnythingAtAll()
    {
        var version = CollectorProcessFixture.RunToCompletion("--version");
        Assert.Equal(0, version.ExitCode);
        Assert.Contains(Program.Version, version.StandardOutput, StringComparison.Ordinal);

        // The doctor reports 1 on a machine with no Npcap and no game, which is the correct
        // answer: it must degrade rather than crash.
        var doctor = CollectorProcessFixture.RunToCompletion("--capture-doctor", "--json");
        Assert.InRange(doctor.ExitCode, 0, 1);

        var report = JsonNode.Parse(doctor.StandardOutput)!.AsObject();
        Assert.Equal(CaptureDiagnosticsSnapshot.LiveCaptureStatus, report["live_capture_status"]!.GetValue<string>());
        Assert.Equal("WinPCap", report["boundary"]!["monitor_type"]!.GetValue<string>());
        Assert.False(report["boundary"]!["injected_hook_enabled"]!.GetValue<bool>());
        Assert.Equal(0, report["boundary"]!["listening_ports"]!.GetValue<int>());
        // Doctor mode opens no settings, so the setting reads as its default; the kill switch
        // follows this process's environment, and nothing was ever sent.
        Assert.False(report["boundary"]!.AsObject().ContainsKey("outbound_connections"));
        var outbound = report["boundary"]!["outbound"]!;
        Assert.True(outbound["shared_calibration_enabled"]!.GetValue<bool>());
        _ = outbound["kill_switch"]!.GetValue<bool>();
        Assert.Null(outbound["last_fetch_utc"]);
        Assert.Null(outbound["last_fetch_status"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a WAL handle briefly; the temp cleaner will get it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
