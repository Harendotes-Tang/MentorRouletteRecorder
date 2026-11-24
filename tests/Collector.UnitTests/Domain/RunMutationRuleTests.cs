using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

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
        {
            // Audit 2026-11-03, D1-3: without an entry time the first two rules never meet.
            ValidRun() with { MatchedAtUtc = Start.AddMinutes(2), EnteredAtUtc = null, EndedAtUtc = Start.AddMinutes(1), DurationMs = null },
            RunRuleViolationKind.MatchedAfterEnded,
            RunRuleProperty.MatchedAtUtc
        },
    };

    /// <summary>
    /// Audit 2026-11-03, D1-3. A record that ends before it was matched is refused by the Collector as
    /// the Desktop already refuses it (结束时间早于匹配时间), with the time-order code every client knows.
    /// </summary>
    [Fact]
    public void AMatchAfterTheEndIsATimeOrderError()
    {
        var run = ValidRun() with { MatchedAtUtc = Start.AddMinutes(40), EnteredAtUtc = null, DurationMs = null };

        var error = Assert.Throws<CollectorException>(
            () => Application.Mutations.RunMutationValidation.ValidateFinalValue(run, durationExplicit: false));

        Assert.Equal(ErrorCodes.TimeOrder, error.Code);
        Assert.Equal("matched_at_utc", error.Field);
        Assert.Equal("mutation.matched_after_ended", error.Text!.Key);
        Assert.Null(RunMutationRules.ValidateFinalValue(run with { MatchedAtUtc = Start.AddMinutes(31) }, false).EnteredAtUtc);
    }

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
    // (docs/statistics-definitions.md §2; StatisticsTests counts them), and its duration stays unknown.

    /// <summary>A hand-entered record with no entry time, no end time and no duration.</summary>
    internal static MentorRun Timeless(RunResult result) =>
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
