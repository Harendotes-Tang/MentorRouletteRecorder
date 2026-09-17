using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Where a Collector run puts the files it owns.
///
/// A throw-away database implies a throw-away log folder, and one environment variable moves
/// the whole root (review finding C1). Test suites and scripts/verify.ps1 start a Collector
/// with <c>--db &lt;throw-away&gt;</c>; a diagnostic logger that resolved its folder from
/// %LOCALAPPDATA% instead would let the harness write, rotate and prune the user's real
/// diagnostic logs.
/// </summary>
public sealed class DataPathOptionTests
{
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
