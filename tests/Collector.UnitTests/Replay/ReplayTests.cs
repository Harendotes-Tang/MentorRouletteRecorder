using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Offline replay: the semantic-event fixture through the state machine and the decoded fixtures
/// through the parser. The integrity checks that keep both synthetic are <see cref="FixtureIntegrityTests"/>.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ReplayTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "synthetic-completed-v1.fixture.json");

    // ---------------------------------------------------- semantic-event fixtures ----

    [Fact]
    public void Fixture_HasFixedHashAndContainsNoPersonalData()
    {
        var fixture = ReplayFixtureLoader.Load(FixturePath);

        Assert.Equal("125aa826ef13a5cbb473d7a6602f836e096c5002547e3e5a5e6775de0735ed81", fixture.Sha256);
        Assert.Equal(4, fixture.Events.Count);
        Assert.All(fixture.Events, ev => Assert.Equal("10000000-0000-4000-8000-000000000001", ev.Key.CaptureSessionId));
    }

    [Fact]
    public void Replay_ProducesTransitionsFinalRunDatabaseWritesAndStatistics()
    {
        using var temp = new TempDirectory("ReplayTests");
        var databasePath = temp.Combine("replay.db");

        var result = FixtureReplayRunner.Run(FixturePath, databasePath);

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Equal(new[]
        {
            RunState.MentorMatched,
            RunState.EnteredDuty,
            RunState.EnteredDuty,
            RunState.Completed,
        }, result.Transitions.Select(row => row.ToState));
        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
        Assert.Equal(1, result.Statistics.AttemptCount);
        Assert.Equal(1, result.Statistics.CompletedCount);
        Assert.Equal(1, result.Writes.RunsCreated);
        Assert.Equal(4, result.Writes.EventsAppended);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void Replay_IsIdempotentAcrossProcessEquivalentRuns()
    {
        using var temp = new TempDirectory("ReplayTests");
        var databasePath = temp.Combine("replay.db");
        var first = FixtureReplayRunner.Run(FixturePath, databasePath);

        var second = FixtureReplayRunner.Run(FixturePath, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.True(second.Writes.IdempotentReplay);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(1, second.Statistics.AttemptCount);
    }

    [Fact]
    public void Program_ReplayModeWritesJsonResult()
    {
        using var temp = new TempDirectory("ReplayTests");

        var (exitCode, output) = RunProgram("--replay", FixturePath, "--database", temp.Combine("replay.db"));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"final_state\": \"COMPLETED\"", output, StringComparison.Ordinal);
        Assert.Contains("\"statistics\"", output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- decoded fixtures ----

    private static string DecodedDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "decoded");

    [Fact]
    public void DecodedReplayOfTheCompleteFixtureProducesACompletedRun()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var result = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, temp.Combine("replay.db"));

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.True(result.BuildMatched);
        Assert.True(result.ProfileUsable);
        Assert.Equal(4, result.Parser.ParseOk);
        Assert.Equal(0, result.Parser.ParseFailed);
        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
    }

    [Fact]
    public void DecodedReplayIsIdempotent()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var databasePath = temp.Combine("replay.db");
        var first = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, databasePath);

        var second = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.True(second.Writes.IdempotentReplay);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(1, second.Statistics.AttemptCount);
    }

    [Fact]
    public void DecodedReplayResolvesTheProfileNamedByTheFixtureWhenNoneIsGiven()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var result = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), null, temp.Combine("replay.db"));

        Assert.Equal("synthetic-v1", result.ProfileId);
        Assert.Equal(RunState.Completed, result.FinalState);
    }

    [Fact]
    public void Program_ReplayDecodedModeWritesJsonResult()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");

        var (exitCode, output) = RunProgram(
            "--replay-decoded", Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"),
            "--profile", SyntheticProfile.Path,
            "--db", temp.Combine("replay.db"));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"final_state\": \"COMPLETED\"", output, StringComparison.Ordinal);
        Assert.Contains("\"parse_ok\": 4", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ValidateProfileReportsSuccessAndFailure()
    {
        Assert.Equal(0, RunProgram("--validate-profile", SyntheticProfile.Path).Code);
        Assert.Equal(1, RunProgram(
            "--validate-profile",
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json")).Code);
    }

    [Fact]
    public void Program_ListProfilesPrintsEveryInstalledProfile()
    {
        var (exitCode, text) = RunProgram("--list-profiles");

        Assert.Equal(0, exitCode);
        Assert.Contains("synthetic-v1", text, StringComparison.Ordinal);
        Assert.Contains("cn-unsupported", text, StringComparison.Ordinal);
        Assert.Contains("global-unsupported", text, StringComparison.Ordinal);
    }

    /// <summary>Runs the Collector's entry point with its standard output captured and its errors discarded.</summary>
    private static (int Code, string Output) RunProgram(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var discarded = new StringWriter();
        Console.SetOut(output);
        Console.SetError(discarded);
        try
        {
            var exitCode = Program.Main(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
