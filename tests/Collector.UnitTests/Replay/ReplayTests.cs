using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Isolates process-wide console redirection from every other test collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Collector console output";
}

[Collection(ConsoleOutputCollection.Name)]
public sealed class ReplayTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "synthetic-completed-v1.fixture.json");

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
        var databasePath = TempDatabasePath();

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
        var databasePath = TempDatabasePath();
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
        var databasePath = TempDatabasePath();
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = Program.Main(new[] { "--replay", FixturePath, "--database", databasePath });
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("\"final_state\": \"COMPLETED\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"statistics\"", output.ToString(), StringComparison.Ordinal);
    }

    private static string TempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.ReplayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "replay.db");
    }
}
