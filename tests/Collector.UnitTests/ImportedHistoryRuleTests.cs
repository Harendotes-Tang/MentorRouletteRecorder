using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ImportedHistoryRuleTests
{
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
}
