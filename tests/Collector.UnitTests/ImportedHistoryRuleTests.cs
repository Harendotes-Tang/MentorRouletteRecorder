using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ImportedHistoryRuleTests
{
    [Fact]
    public void IncompleteImportKeepsUnknownGameTimesWithoutWeakeningManualRules()
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
        Assert.Throws<RunRuleViolationException>(() =>
            RunMutationRules.ValidateFinalValue(candidate with { Source = RunSource.Manual, ImportMetadata = null }, false));
        Assert.Throws<RunRuleViolationException>(() =>
            RunMutationRules.ValidateFinalValue(candidate with { Source = RunSource.AutoNetwork, ImportMetadata = null }, false));
    }
}
