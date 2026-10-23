using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Every fixture replayed against a real database, with the expected outcome written out by
/// hand from docs/state-machine.md and docs/statistics-definitions.md rather than copied
/// from what the code happens to produce.
/// </summary>
public sealed class ReplayIntegrationTests
{
    /// <summary>What one fixture is expected to produce.</summary>
    /// <param name="Fixture">Fixture id and file stem.</param>
    /// <param name="FinalState">State the machine must end in.</param>
    /// <param name="RunCount">Runs the replay must have touched.</param>
    /// <param name="AttemptCount">Expected attempt_count.</param>
    /// <param name="CompletedCount">Expected completed_count.</param>
    /// <param name="Results">Expected result of each run, in creation order.</param>
    /// <param name="ParserErrors">Expected number of fail-closed refusals.</param>
    /// <param name="DuplicateEvents">Expected number of ignored duplicate observations.</param>
    public sealed record Expectation(
        string Fixture,
        RunState FinalState,
        int RunCount,
        int AttemptCount,
        int CompletedCount,
        RunResult[] Results,
        int ParserErrors = 0,
        int DuplicateEvents = 0);

    private static readonly Expectation[] Expectations =
    {
        new("synthetic-completed-v1", RunState.Completed, 1, 1, 1, new[] { RunResult.Completed }),
        new("mentor_left_v1", RunState.LeftOrAbandoned, 1, 1, 0, new[] { RunResult.LeftOrAbandoned }),

        // No entry means no attempt: CANCELLED_BEFORE_ENTRY never reaches a rate denominator.
        new("mentor_cancelled_v1", RunState.CancelledBeforeEntry, 1, 0, 0,
            new[] { RunResult.CancelledBeforeEntry }),
        new("mentor_disconnected_v1", RunState.Disconnected, 1, 1, 0, new[] { RunResult.Disconnected }),
        new("mentor_interrupted_v1", RunState.Interrupted, 1, 1, 0, new[] { RunResult.Interrupted }),
        new("mentor_unknown_v1", RunState.UnknownFinalState, 1, 1, 0, new[] { RunResult.Unknown }),

        // A non-mentor roulette creates nothing at all.
        new("non_mentor_roulette_then_zone_v1", RunState.Idle, 0, 0, 0, Array.Empty<RunResult>()),

        // Repeated observations collapse into one run and one completion.
        new("duplicate_events_v1", RunState.Completed, 1, 1, 1,
            new[] { RunResult.Completed }, DuplicateEvents: 3),

        // Fail-closed: an unusable profile writes nothing and only counts refusals.
        new("unknown_profile_v1", RunState.Idle, 0, 0, 0, Array.Empty<RunResult>(), ParserErrors: 3),
        new("two_runs_sequence_v1", RunState.Completed, 2, 2, 2,
            new[] { RunResult.Completed, RunResult.Completed }),
    };

    /// <summary>Expectations as xunit theory data.</summary>
    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var expectation in Expectations)
            {
                data.Add(expectation.Fixture);
            }

            return data;
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".fixture.json");

    private static string TempDatabase() =>
        Path.Combine(
            Path.GetTempPath(),
            "MentorRecorder.ReplayIt",
            Guid.NewGuid().ToString("N"),
            "replay.db");

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ReplayProducesTheHandComputedOutcome(string name)
    {
        var expected = Expectations.Single(row => row.Fixture == name);

        var result = FixtureReplayRunner.Run(FixturePath(name), TempDatabase());

        Assert.Equal(expected.FinalState, result.FinalState);
        Assert.Equal(expected.RunCount, result.Runs.Count);
        Assert.Equal(expected.Results, result.Runs.Select(run => run.Result));
        Assert.Equal(expected.AttemptCount, result.Statistics.AttemptCount);
        Assert.Equal(expected.CompletedCount, result.Statistics.CompletedCount);
        Assert.Equal(expected.ParserErrors, result.Writes.ParserErrors);
        Assert.Equal(expected.DuplicateEvents, result.Writes.DuplicateEvents);
        Assert.Equal(expected.RunCount, result.Writes.RunsCreated);
        Assert.Equal(expected.RunCount, result.Writes.RevisionsAppended);
        Assert.NotEmpty(result.ParsedEvents);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ReplayingTwiceWritesNothingTheSecondTime(string name)
    {
        var expected = Expectations.Single(row => row.Fixture == name);
        var databasePath = TempDatabase();

        var first = FixtureReplayRunner.Run(FixturePath(name), databasePath);
        var second = FixtureReplayRunner.Run(FixturePath(name), databasePath);

        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(0, second.Writes.RevisionsAppended);
        Assert.Equal(expected.RunCount, second.Runs.Count);
        Assert.Equal(first.Runs, second.Runs);
        Assert.Equal(first.Statistics.AttemptCount, second.Statistics.AttemptCount);
        Assert.Equal(first.Statistics.CompletedCount, second.Statistics.CompletedCount);
    }

    [Fact]
    public void UnusableProfile_WritesNoRunAndCountsEveryRefusal()
    {
        var result = FixtureReplayRunner.Run(FixturePath("unknown_profile_v1"), TempDatabase());

        Assert.False(result.ProfileUsable);
        Assert.Equal("UNSUPPORTED_BUILD", result.ProfileStatus);
        Assert.Empty(result.Runs);
        Assert.Equal(0, result.Writes.EventsAppended);
        Assert.Equal(result.ParsedEvents.Count, result.Writes.ParserErrors);
        Assert.All(result.Transitions, transition => Assert.False(transition.Accepted));
    }

    [Fact]
    public void DuplicateFixture_ProducesOneRunAndOneCompletion()
    {
        var result = FixtureReplayRunner.Run(FixturePath("duplicate_events_v1"), TempDatabase());

        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(1, result.Statistics.CompletedCount);

        // Seven observations arrive; three are duplicates, so four rows reach the trail.
        Assert.Equal(7, result.ParsedEvents.Count);
        Assert.Equal(3, result.Writes.DuplicateEvents);
        Assert.Equal(4, result.Writes.EventsAppended);
    }

    [Fact]
    public void TwoRunSequence_KeepsTheRunsSeparate()
    {
        var result = FixtureReplayRunner.Run(FixturePath("two_runs_sequence_v1"), TempDatabase());

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(2, result.Runs.Select(run => run.RunId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new int?[] { 900001, 900002 }, result.Runs.Select(run => run.ContentId).ToArray());
        Assert.Equal(new int?[] { 19, 24 }, result.Runs.Select(run => run.JobId).ToArray());
        Assert.Equal(2, result.Statistics.ResultBreakdown.Buckets
            .Single(bucket => bucket.Result == RunResult.Completed).Count);
    }

    [Fact]
    public void CompletedFixture_MeasuresDurationFromTheMonotonicReadings()
    {
        var result = FixtureReplayRunner.Run(FixturePath("synthetic-completed-v1"), TempDatabase());

        // Entry at 6000 ms, victory at 126000 ms: two monotonic readings, never two wall clocks.
        Assert.Equal(120_000, Assert.Single(result.Runs).DurationMs);
        Assert.Equal(120_000d, result.Statistics.AverageDurationMs);
    }

    [Fact]
    public void CancelledFixture_HasNoEntryTimeAndNoDuration()
    {
        var result = FixtureReplayRunner.Run(FixturePath("mentor_cancelled_v1"), TempDatabase());

        var run = Assert.Single(result.Runs);
        Assert.Null(run.EnteredAtUtc);
        Assert.Null(run.DurationMs);
        Assert.Equal(0, result.Statistics.AttemptCount);
        Assert.Null(result.Statistics.CompletionRate);
    }

    [Fact]
    public void EveryDocumentedTerminalStateIsCoveredByAFixture()
    {
        var covered = Expectations.Select(row => row.FinalState).ToHashSet();

        Assert.Contains(RunState.Completed, covered);
        Assert.Contains(RunState.CancelledBeforeEntry, covered);
        Assert.Contains(RunState.LeftOrAbandoned, covered);
        Assert.Contains(RunState.Disconnected, covered);
        Assert.Contains(RunState.Interrupted, covered);
        Assert.Contains(RunState.UnknownFinalState, covered);
    }
}
