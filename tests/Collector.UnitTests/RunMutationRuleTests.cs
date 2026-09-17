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
            ValidRun() with { Result = RunResult.Interrupted, EnteredAtUtc = null },
            RunRuleViolationKind.EntryRequired,
            RunRuleProperty.EnteredAtUtc
        },
        {
            ValidRun() with { EndedAtUtc = null },
            RunRuleViolationKind.EndRequired,
            RunRuleProperty.EndedAtUtc
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
