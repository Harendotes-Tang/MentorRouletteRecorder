using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The Collector's command line. Phase 0 smoke tests: the build produces a runnable Collector
/// whose version banner is stable and whose IPC protocol version is 1; then the switches only
/// the serving mode accepts, and the folders they resolve to.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class CommandLineOptionsTests
{
    // ---------------------------------------------------------- version and modes ----

    [Fact]
    public void VersionString_HasExpectedFormat()
    {
        Assert.Equal("MentorRecorder.Collector " + Program.Version + " (ipc v1)", Program.VersionString);

        // Three numbers, and on a test build the pre-release suffix Directory.Build.props
        // carries in <VersionSuffix> (1.4.0-beta.1). The build metadata a local build may
        // append after '+' is already stripped by Program.Version.
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", Program.Version);
        Assert.True(Update.UpdateVersion.TryParseLocal(Program.Version, out _));
    }

    [Fact]
    public void IpcProtocolVersion_IsOne()
    {
        Assert.Equal(1, Program.IpcProtocolVersion);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("/version")]
    public void Main_WithVersionFlag_ExitsZero(string flag)
    {
        Assert.Equal(0, Program.Main(new[] { flag }));
    }

    [Fact]
    public void Main_WithUnknownArgument_ExitsTwo()
    {
        Assert.Equal(2, Program.Main(new[] { "--not-a-flag" }));
    }

    [Fact]
    public void CaptureDoctorMode_IsParsedWithJsonOutput()
    {
        var options = CommandLineOptions.Parse(new[] { "--capture-doctor", "--json" });

        Assert.Equal(CollectorMode.CaptureDoctor, options.Mode);
        Assert.True(options.Json);
    }

    [Fact]
    public void CaptureDoctorMode_CannotBeCombinedWithAnotherMode()
    {
        Assert.Throws<FormatException>(() =>
            CommandLineOptions.Parse(new[] { "--capture-doctor", "--version" }));
    }

    [Fact]
    public void CaptureDoctorMode_RejectsIrrelevantFlags()
    {
        Assert.Throws<FormatException>(() =>
            CommandLineOptions.Parse(new[] { "--capture-doctor", "--db", "capture.db" }));
    }

    [Fact]
    public void CaptureTraceMode_IsParsedWithItsOwnFlags()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--capture-trace", "trace.jsonl",
            "--duration-seconds", "30",
            "--adapter", @"\Device\NPF_{0}",
            "--max-lines", "500",
        });

        Assert.Equal(CollectorMode.CaptureTrace, options.Mode);
        Assert.Equal("trace.jsonl", options.TracePath);
        Assert.Equal(30, options.DurationSeconds);
        Assert.Equal(@"\Device\NPF_{0}", options.AdapterId);
        Assert.Equal(500, options.MaxLines);
    }

    [Fact]
    public void TraceReportMode_IsParsedWithItsOwnFlags()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--trace-report", "trace.jsonl", "--around", "pop", "--window-ms", "2000",
        });

        Assert.Equal(CollectorMode.TraceReport, options.Mode);
        Assert.Equal("trace.jsonl", options.TracePath);
        Assert.Equal("pop", options.AroundMarker);
        Assert.Equal(2000, options.WindowMs);
    }

    /// <summary>
    /// A trace flag on the wrong mode is refused rather than ignored: the mistake would
    /// otherwise be discovered only after the game session it was meant to record was over.
    /// </summary>
    /// <param name="commandLine">Space-separated command line that must not parse.</param>
    [Theory]
    [InlineData("--capture-trace")]
    [InlineData("--trace-report")]
    [InlineData("--capture-trace t.jsonl --json")]
    [InlineData("--capture-trace t.jsonl --around pop")]
    [InlineData("--capture-trace t.jsonl --window-ms 1000")]
    [InlineData("--capture-trace t.jsonl --db capture.db")]
    [InlineData("--capture-trace t.jsonl --trace-report t.jsonl")]
    [InlineData("--trace-report t.jsonl --duration-seconds 5")]
    [InlineData("--trace-report t.jsonl --adapter eth0")]
    [InlineData("--trace-report t.jsonl --max-lines 5")]
    [InlineData("--serve --duration-seconds 5")]
    [InlineData("--capture-doctor --window-ms 5")]
    [InlineData("--capture-trace t.jsonl --duration-seconds -1")]
    [InlineData("--capture-trace t.jsonl --duration-seconds 999999")]
    [InlineData("--capture-trace t.jsonl --max-lines 0")]
    [InlineData("--trace-report t.jsonl --window-ms 0")]
    public void TraceFlags_AreRefusedOutsideTheirMode(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(args));
    }

    [Fact]
    public void Main_WithAnInvalidTraceCombination_ExitsTwo()
    {
        Assert.Equal(2, Program.Main(new[] { "--capture-trace", "t.jsonl", "--json" }));
    }

    // --------------------------------------------------------------------- --pipe ----
    // `--pipe` exists so a test harness can run a Collector on a throw-away database
    // without ever meeting the Collector the user's Desktop is talking to. The serving mode is
    // the only mode that accepts it, and its argument must be a bare pipe name.

    [Fact]
    public void ServeAcceptsABarePipeNameAndDefaultsToNone()
    {
        var options = CommandLineOptions.Parse(
            new[] { "--serve", "--db", "x.db", "--pipe", "MentorRecorder.test-abc.v1" });
        Assert.Equal(CollectorMode.Serve, options.Mode);
        Assert.Equal("MentorRecorder.test-abc.v1", options.PipeName);

        // Omitted means the per-user pipe, which is what the Desktop launches.
        Assert.Null(CommandLineOptions.Parse(new[] { "--serve" }).PipeName);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--pipe-name-only")]
    [InlineData("--capture-doctor")]
    [InlineData("--list-profiles")]
    public void EveryOneShotModeRefusesThePipeSwitch(string mode)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { mode, "--pipe", "MentorRecorder.test.v1" }));
    }

    [Fact]
    public void ReplayRefusesThePipeSwitchToo()
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--replay", "f.json", "--pipe", "x" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\.\pipe\MentorRecorder.test.v1")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    public void ANameThatIsNotABarePipeNameIsRefused(string value)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--serve", "--pipe", value }));
    }

    [Fact]
    public void AnOverlongPipeNameIsRefused()
    {
        var value = new string('a', CommandLineOptions.MaxPipeNameLength + 1);
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--serve", "--pipe", value }));
        Assert.Equal(
            CommandLineOptions.MaxPipeNameLength,
            CommandLineOptions.Parse(
                new[] { "--serve", "--pipe", new string('a', CommandLineOptions.MaxPipeNameLength) })
                .PipeName!.Length);
    }

    [Fact]
    public void AMissingPipeNameIsRefusedRatherThanTreatedAsDefault()
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(new[] { "--serve", "--pipe" }));
    }

    // ------------------------------------------------ --log-dir and the data root ----
    // Where a Collector run puts the files it owns.
    //
    // A throw-away database implies a throw-away log folder, and one environment variable moves
    // the whole root (review finding C1). Test suites and scripts/verify.ps1 start a Collector
    // with `--db &lt;throw-away&gt;`; a diagnostic logger that resolved its folder from
    // %LOCALAPPDATA% instead would let the harness write, rotate and prune the user's real
    // diagnostic logs.

    [Fact]
    public void ServeAcceptsALogDirectoryAndDefaultsToNone()
    {
        var options = CommandLineOptions.Parse(
            new[] { "--serve", "--db", @"C:\tmp\x.db", "--log-dir", @"C:\tmp\logs" });

        Assert.Equal(CollectorMode.Serve, options.Mode);
        Assert.Equal(@"C:\tmp\logs", options.LogDirectory);
        Assert.Null(CommandLineOptions.Parse(new[] { "--serve" }).LogDirectory);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--pipe-name-only")]
    [InlineData("--capture-doctor")]
    [InlineData("--list-profiles")]
    public void AOneShotModeRefusesTheLogDirectory(string mode)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { mode, "--log-dir", @"C:\tmp\logs" }));
    }

    [Fact]
    public void AMissingLogDirectoryValueIsRefusedRatherThanTreatedAsDefault()
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(new[] { "--serve", "--log-dir" }));
    }

    [Fact]
    public void AnExplicitDatabaseMovesTheLogsNextToIt()
    {
        var resolved = DatabasePaths.ResolveLogDirectory(
            logDirectory: null, databasePath: @"C:\tmp\harness\test.db");

        Assert.Equal(Path.Combine(@"C:\tmp\harness", DatabasePaths.LogFolderName), resolved);
    }

    [Fact]
    public void AnExplicitLogDirectoryWinsOverTheDatabaseFolder()
    {
        var resolved = DatabasePaths.ResolveLogDirectory(
            logDirectory: @"C:\tmp\elsewhere", databasePath: @"C:\tmp\harness\test.db");

        Assert.Equal(Path.GetFullPath(@"C:\tmp\elsewhere"), resolved);
    }

    [Fact]
    public void WithoutADatabaseTheLogsStayInTheManagedRoot()
    {
        Assert.Equal(
            DatabasePaths.LogDirectory,
            DatabasePaths.ResolveLogDirectory(logDirectory: null, databasePath: null));
    }

    [Fact]
    public void TheDataDirectoryOverrideMovesEveryManagedFolder()
    {
        var root = DatabasePaths.ResolveRoot(@"C:\tmp\isolated");

        Assert.Equal(Path.GetFullPath(@"C:\tmp\isolated"), root);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DatabasePaths.FolderName),
            DatabasePaths.ResolveRoot(null));
    }

    [Fact]
    public void TheEnvironmentVariableIsWhatTheRootReads()
    {
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        var isolated = Path.Combine(Path.GetTempPath(), "MentorRecorder.RootTest", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, isolated);

            Assert.Equal(Path.GetFullPath(isolated), DatabasePaths.RootDirectory);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(isolated), DatabasePaths.DatabaseFileName),
                DatabasePaths.DefaultDatabasePath);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(isolated), DatabasePaths.LogFolderName),
                DatabasePaths.LogDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, previous);
        }
    }
}
