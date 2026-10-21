using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A hand-entered record may have no entry or end time, whatever its result
/// (docs/manual-correction.md §7); its known result still counts it as an attempt
/// (docs/statistics-definitions.md §2), and its duration stays unknown.
/// </summary>
public sealed class ManualTimeExemptionTests
{
    private static MentorRun Timeless(RunResult result) =>
        TestDatabase.Run(result: result, source: RunSource.Manual) with
        {
            EnteredAtUtc = null,
            EndedAtUtc = null,
            DurationMs = null,
        };

    [Theory]
    [InlineData(RunResult.Completed)]
    [InlineData(RunResult.LeftOrAbandoned)]
    [InlineData(RunResult.Interrupted)]
    [InlineData(RunResult.Unknown)]
    public void ManualRunWithoutTimesIsAccepted(RunResult result)
    {
        var validated = RunMutationRules.ValidateFinalValue(Timeless(result), durationExplicit: false);
        Assert.Null(validated.EnteredAtUtc);
        Assert.Null(validated.EndedAtUtc);
        Assert.Null(validated.DurationMs);
    }

    [Fact]
    public void CompletedRunWithOnlyAnEntryKeepsAnUnknownDuration()
    {
        var run = Timeless(RunResult.Completed) with { EnteredAtUtc = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero) };
        var validated = RunMutationRules.ValidateFinalValue(run, durationExplicit: false);
        Assert.Null(validated.DurationMs);
    }

    [Fact]
    public void SuppliedTimesStillCrossTheOrderingChecks()
    {
        var run = Timeless(RunResult.Completed) with
        {
            EnteredAtUtc = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 9, 4, 0, 30, 0, TimeSpan.Zero),
        };
        var failure = Assert.Throws<RunRuleViolationException>(() => RunMutationRules.ValidateFinalValue(run, false));
        Assert.Equal(RunRuleViolationKind.EnteredAfterEnded, failure.Kind);
    }

    [Fact]
    public void ManualRunsWithoutTimesCountByTheirResultLikeImportedHistory()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(Timeless(RunResult.Completed), tx);
            runs.Insert(Timeless(RunResult.LeftOrAbandoned), tx);
            // Unknown and cancelled-before-entry prove no entry, so they are no attempt.
            runs.Insert(Timeless(RunResult.Unknown), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.CancelledBeforeEntry, source: RunSource.Manual), tx);
            // A capture without an entry time was never entered, whatever it says.
            runs.Insert(TestDatabase.Run(result: RunResult.Interrupted, source: RunSource.AutoNetwork) with
            {
                EnteredAtUtc = null,
                DurationMs = null,
            }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var dashboard = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(2, dashboard.AttemptCount);
        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.AchievementProgress);
        Assert.Equal(0.5, dashboard.CompletionRate);
        Assert.Null(dashboard.AverageDurationMs);
    }
}
