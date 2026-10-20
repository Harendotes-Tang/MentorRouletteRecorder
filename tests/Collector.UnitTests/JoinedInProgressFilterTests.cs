using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <c>RunFilter.joined_in_progress</c>: true lists the runs recorded as joined in progress, false the
/// ones recorded as entered at the start, and a run whose entry the software never saw matches
/// neither (contracts/CHANGELOG.md 2026-10-20).
/// </summary>
public sealed class JoinedInProgressFilterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParserReadsTheOptionalFlag(bool joined)
    {
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["joined_in_progress"] = joined }))!;
        Assert.Equal(joined, parsed.JoinedInProgress);
        Assert.Null(RequestParsers.Filter(new PayloadReader(new JsonObject()))!.JoinedInProgress);
    }

    [Fact]
    public void ParserRefusesANonBooleanFlag() => Assert.Throws<CollectorException>(() =>
        RequestParsers.Filter(new PayloadReader(new JsonObject { ["joined_in_progress"] = "yes" })));

    [Fact]
    public void TheSqlNamesTheColumnOnlyWhenTheFlagIsGiven()
    {
        Assert.DoesNotContain("joined_in_progress", RunFilterSql.Build(new RunFilter(), false).Where);
        Assert.Contains("joined_in_progress = 1", RunFilterSql.Build(new RunFilter { JoinedInProgress = true }, false).Where);
        Assert.Contains("joined_in_progress = 0", RunFilterSql.Build(new RunFilter { JoinedInProgress = false }, true).Where);
    }

    [Fact]
    public void EachValueListsOnlyTheRunsRecordedWithIt()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var joined = TestDatabase.Run(result: RunResult.Completed, source: RunSource.AutoNetwork) with { JoinedInProgress = true };
        var fresh = TestDatabase.Run(result: RunResult.Completed, source: RunSource.AutoNetwork) with { JoinedInProgress = false };
        var unseen = TestDatabase.Run(result: RunResult.Completed, source: RunSource.AutoNetwork);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(joined, tx);
            runs.Insert(fresh, tx);
            runs.Insert(unseen, tx);
        });

        Assert.Equal(joined.RunId, Assert.Single(runs.Query(new RunFilter { JoinedInProgress = true }, null, 1, 50).Items).RunId);
        Assert.Equal(fresh.RunId, Assert.Single(runs.Query(new RunFilter { JoinedInProgress = false }, null, 1, 50).Items).RunId);
        Assert.Equal(3, runs.Query(new RunFilter(), null, 1, 50).Total);

        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var statistics = new StatisticsRepository(fixture.Database, settings);
        var dashboard = statistics.GetDashboard(new RunFilter { JoinedInProgress = true });
        Assert.Equal(1, dashboard.AttemptCount);
        Assert.Equal(1, dashboard.JoinedInProgressCount);
        Assert.Equal(1.0, dashboard.JoinedInProgressRate);
    }

    /// <summary>
    /// The import fingerprint of a row that says nothing about how the duty was entered must be
    /// byte-identical to what it was before the field existed, or every earlier screenshot,
    /// paste and third-party table import stops reading as a duplicate (review 2026-10-20, HIGH).
    /// </summary>
    [Fact]
    public void TheImportFactsNameTheFieldOnlyWhenTheSourceSaidSo()
    {
        var unsaid = MentorRecorder.Collector.Import.RunImportCandidateNormalizer.Facts(TestDatabase.Run());
        var said = MentorRecorder.Collector.Import.RunImportCandidateNormalizer.Facts(
            TestDatabase.Run() with { JoinedInProgress = true });

        Assert.DoesNotContain("joined_in_progress", unsaid, StringComparison.Ordinal);
        Assert.Contains("\"joined_in_progress\":true", said, StringComparison.Ordinal);
    }
}
