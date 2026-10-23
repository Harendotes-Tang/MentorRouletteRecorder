using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunMutationRuleTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequireReason_ReportsTypedDomainFailureWithoutContractVocabulary()
    {
        var failure = Assert.Throws<RunRuleViolationException>(
            () => RunMutationRules.RequireReason("   "));

        Assert.Equal(RunRuleViolationKind.ReasonRequired, failure.Kind);
        Assert.Equal(RunRuleProperty.Reason, failure.Property);
        Assert.Null(failure.RunId);
        Assert.Null(failure.RunResult);
        Assert.DoesNotContain("ERR_", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reason", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<MentorRun, RunRuleViolationKind, RunRuleProperty> InvalidRuns => new()
    {
        {
            ValidRun() with { MatchedAtUtc = Start.AddMinutes(2), EnteredAtUtc = Start.AddMinutes(1) },
            RunRuleViolationKind.MatchedAfterEntered,
            RunRuleProperty.MatchedAtUtc
        },
        {
            ValidRun() with { EnteredAtUtc = Start.AddMinutes(2), EndedAtUtc = Start.AddMinutes(1) },
            RunRuleViolationKind.EnteredAfterEnded,
            RunRuleProperty.EnteredAtUtc
        },
        {
            ValidRun() with { DurationMs = -1 },
            RunRuleViolationKind.NegativeDuration,
            RunRuleProperty.DurationMs
        },
    };

    [Theory]
    [MemberData(nameof(InvalidRuns))]
    public void ValidateFinalValue_ReportsTypedRunContext(
        MentorRun run,
        RunRuleViolationKind expectedKind,
        RunRuleProperty expectedProperty)
    {
        var failure = Assert.Throws<RunRuleViolationException>(
            () => RunMutationRules.ValidateFinalValue(run, durationExplicit: true));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(expectedProperty, failure.Property);
        Assert.Equal(run.RunId, failure.RunId);
        Assert.Equal(run.Result, failure.RunResult);
    }

    [Fact]
    public void ValidateFinalValue_RetainsExistingViolationPriority()
    {
        var run = ValidRun() with
        {
            MatchedAtUtc = Start.AddMinutes(3),
            EnteredAtUtc = Start.AddMinutes(2),
            EndedAtUtc = Start.AddMinutes(1),
            DurationMs = -1,
        };

        var failure = Assert.Throws<RunRuleViolationException>(
            () => RunMutationRules.ValidateFinalValue(run, durationExplicit: true));

        Assert.Equal(RunRuleViolationKind.MatchedAfterEntered, failure.Kind);
    }

    [Fact]
    public void RequireChanges_ReportsTypedDomainFailure()
    {
        var failure = Assert.Throws<RunRuleViolationException>(
            () => RunMutationRules.RequireChanges(Array.Empty<RunFieldChange>()));

        Assert.Equal(RunRuleViolationKind.NoChanges, failure.Kind);
        Assert.Equal(RunRuleProperty.Changes, failure.Property);
        Assert.Null(failure.RunId);
        Assert.Null(failure.RunResult);
    }

    // ----------------------------------------------------------- imported history ----

    [Fact]
    public void IncompleteRecordsKeepUnknownGameTimesWhateverTheirSource()
    {
        var stamp = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var candidate = new MentorRun
        {
            RunId = Guid.NewGuid().ToString("D"), Revision = 1,
            Source = RunSource.Import, Result = RunResult.Unknown,
            CreatedAtUtc = stamp, UpdatedAtUtc = stamp,
            ImportMetadata = new RunImportMetadata("SCREENSHOT", "synthetic", "2026-10-08 20:30", stamp, stamp, "synthetic"),
        };
        var validated = RunMutationRules.ValidateFinalValue(candidate, false);
        Assert.Null(validated.EnteredAtUtc);
        Assert.Null(validated.EndedAtUtc);
        Assert.Null(validated.DurationMs);
        // Since 2026-10-21 the entry and end times are optional for every source
        // (docs/manual-correction.md §7); the ordering checks still apply to supplied times.
        foreach (var source in new[] { RunSource.Manual, RunSource.AutoNetwork })
        {
            var other = RunMutationRules.ValidateFinalValue(candidate with { Source = source, ImportMetadata = null }, false);
            Assert.Null(other.EnteredAtUtc);
            Assert.Null(other.EndedAtUtc);
            Assert.Null(other.DurationMs);
        }
    }

    // ----------------------------------------- hand-entered records without times ----
    // A hand-entered record may have no entry or end time, whatever its result
    // (docs/manual-correction.md §7); its known result still counts it as an attempt
    // (docs/statistics-definitions.md §2), and its duration stays unknown.

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

    private static MentorRun ValidRun() => new()
    {
        RunId = "00000000-0000-4000-8000-000000000101",
        Revision = 1,
        MatchedAtUtc = Start,
        EnteredAtUtc = Start.AddMinutes(1),
        EndedAtUtc = Start.AddMinutes(31),
        DurationMs = 1_800_000,
        Result = RunResult.Completed,
        Source = RunSource.Manual,
        CreatedAtUtc = Start,
        UpdatedAtUtc = Start,
    };
}
