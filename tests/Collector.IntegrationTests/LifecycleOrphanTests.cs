using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// What happens to a Collector when the process that launched it is killed outright.
///
/// The Desktop stops its child on the way out, but a hard kill (Task Manager,
/// <c>Stop-Process -Force</c>, a session that goes away) runs none of that code. An orphan
/// would keep serving a pipe nobody is connected to and keep holding the per-user serve lease,
/// so the next Desktop launch "reuses" an instance the user cannot see or stop and the
/// recording silently stops.
///
/// The kill is performed for real. A plain child process stands in for the Desktop: the
/// Collector is told a process id and watches that id, and nothing about the watching is
/// specific to Qt. The database is asserted intact as well as the process gone, because a
/// watchdog that avoids orphans by killing the writer mid-transaction is a worse bug.
/// </summary>
[Collection(CollectorProcessCollection.Name)]
public sealed class LifecycleOrphanTests : IDisposable
{
    /// <summary>How long the Collector gets to notice its parent is gone and exit.</summary>
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(15);

    private readonly List<Process> _started = new();
    private readonly string _directory;

    public LifecycleOrphanTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.Orphan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task KillingTheParentOutrightEndsTheCollectorAndLeavesTheDatabaseIntact()
    {
        var databasePath = Path.Combine(_directory, "orphan.db");
        var parent = StartIdleParent();

        var collector = await StartServingCollectorAsync(databasePath, parent.Id);

        // No warning and no chance to clean up: the condition the watchdog exists for.
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(ExitBudget).Token);

        Assert.True(
            collector.WaitForExit((int)ExitBudget.TotalMilliseconds),
            $"the Collector was still running {ExitBudget.TotalSeconds:F0}s after its parent was " +
            "killed; a Desktop that is force-killed must not leave a Collector behind");

        // Zero, not merely some exit code: a watchdog stop is a successful outcome like Ctrl+C,
        // and a non-zero code would mean the process fell over rather than stopped.
        Assert.Equal(0, collector.ExitCode);

        // ---- and it went out cleanly ------------------------------------------------
        using var database = SqliteDatabase.Open(databasePath, SystemClock.Instance);

        var integrity = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return command.ExecuteScalar() as string;
        });
        Assert.Equal("ok", integrity);

        // A capture session row still open would mean the process was cut down before it
        // closed its own bookkeeping, and the next start would read it as a crash.
        var open = new CaptureSessionRepository(database).ListOpen(null);
        Assert.True(
            open.Count == 0,
            "the Collector left " + open.Count.ToString(CultureInfo.InvariantCulture) +
            " capture session(s) open; a watchdog stop has to close them the way Ctrl+C does");
    }

    [Fact]
    public async Task ACollectorWhoseParentIsAlreadyGoneDoesNotKeepServing()
    {
        // The parent can die between spawning the Collector and the Collector first looking at
        // it. Arriving late must not mean never noticing.
        var databasePath = Path.Combine(_directory, "already-gone.db");
        var parent = StartIdleParent();
        var deadPid = parent.Id;
        parent.Kill(entireProcessTree: true);
        await parent.WaitForExitAsync(new CancellationTokenSource(ExitBudget).Token);

        // The serve lease is per user, so another instance can block the start for reasons
        // unrelated to the watchdog. Retry through that case rather than reading it as a pass:
        // a lease refusal otherwise looks like "noticed the dead parent and stopped".
        var deadline = DateTime.UtcNow + ServingCollector.LeaseWait;
        while (true)
        {
            var collector = CollectorProcessFixture.Start(
                "--serve",
                "--db",
                databasePath,
                "--parent-pid",
                deadPid.ToString(CultureInfo.InvariantCulture),
                "--json");
            _started.Add(collector);

            var stdout = collector.StandardOutput.ReadToEndAsync();
            var stderr = collector.StandardError.ReadToEndAsync();

            Assert.True(
                collector.WaitForExit((int)ExitBudget.TotalMilliseconds),
                "a Collector told to watch a process that no longer exists must stop, not serve");

            // The child is gone, so both readers see end of stream; the timeout is only there
            // so a lost pipe cannot hang the suite.
            await stdout.WaitAsync(TimeSpan.FromSeconds(15));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(15));

            if (collector.ExitCode == 0)
            {
                return;
            }

            if (!error.Contains(ServingCollector.LeaseRefusalMarker, StringComparison.Ordinal) ||
                DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    "the Collector exited with " +
                    collector.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " rather than stopping cleanly after noticing its parent was gone. stderr: " +
                    error);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>
    /// Starts the real Collector serving <paramref name="databasePath"/> under
    /// <paramref name="parentProcessId"/>, and waits for the startup line that proves it is
    /// actually serving.
    /// </summary>
    /// <param name="databasePath">Database the server should open.</param>
    /// <param name="parentProcessId">Process the Collector must follow into the grave.</param>
    private async Task<Process> StartServingCollectorAsync(string databasePath, int parentProcessId)
    {
        // The serve lease is per user, not per database, so the previous test class's process
        // can still hold it while it shuts down. That case is transient; anything longer is a
        // real conflict and fails loudly.
        var deadline = DateTime.UtcNow + ServingCollector.LeaseWait;
        while (true)
        {
            var process = CollectorProcessFixture.Start(
                "--serve",
                "--db",
                databasePath,
                "--parent-pid",
                parentProcessId.ToString(CultureInfo.InvariantCulture),
                "--json");
            _started.Add(process);

            var line = await ReadLineAsync(process, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (line is not null && JsonNode.Parse(line) is JsonObject startup)
            {
                // Keep the pipes drained; an undrained one is a stall waiting to happen.
                _ = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();

                Assert.Equal(parentProcessId, startup["parent_pid"]!.GetValue<int>());
                Assert.False(string.IsNullOrWhiteSpace(startup["pipe_name"]!.GetValue<string>()));
                return process;
            }

            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            TryKill(process);

            if (!error.Contains(ServingCollector.LeaseRefusalMarker, StringComparison.Ordinal) ||
                DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the Collector never started serving. stderr: " + error);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a child that stays alive until it is killed, standing in for the Desktop.
    ///
    /// <c>cmd.exe</c> with its input redirected and never written to blocks on its first read:
    /// alive on demand, gone on demand, with no timing race.
    /// </summary>
    private Process StartIdleParent()
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

        var parent = Process.Start(info)
            ?? throw new InvalidOperationException("could not start the stand-in parent");
        _started.Add(parent);
        return parent;
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            TryKill(process);
            process.Dispose();
        }

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
