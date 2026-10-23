using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The run filter as a request carries it and as SQL applies it: <c>deleted_only</c>,
/// <c>joined_in_progress</c> and the localized name search.
/// </summary>
public sealed class RunFilterTests
{
    // --------------------------------------------------------------- deleted only ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParserReadsOptionalDeletedOnly(bool only)
    {
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["deleted_only"] = only }))!;
        Assert.Equal(only, parsed.DeletedOnly);
        Assert.False(parsed.IncludeDeleted);
        Assert.False(RequestParsers.Filter(new PayloadReader(new JsonObject()))!.DeletedOnly);
    }

    [Fact]
    public void ParserRefusesNonBooleanDeletedOnly() => Assert.Throws<CollectorException>(() =>
        RequestParsers.Filter(new PayloadReader(new JsonObject { ["deleted_only"] = "true" })));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedOnlyOverridesIncludeDeletedButStatisticsStillExcludeDeleted(bool include)
    {
        var filter = new RunFilter { DeletedOnly = true, IncludeDeleted = include };
        var history = RunFilterSql.Build(filter, false).Where;
        Assert.Contains("soft_deleted = 1", history);
        Assert.DoesNotContain("soft_deleted = 0", history);
        var statistics = RunFilterSql.Build(filter, true).Where;
        Assert.Contains("soft_deleted = 0", statistics);
        Assert.DoesNotContain("soft_deleted = 1", statistics);
    }

    // --------------------------------------------------------- joined in progress ----
    // `RunFilter.joined_in_progress`: true lists the runs recorded as joined in progress, false the
    // ones recorded as entered at the start, and a run whose entry the software never saw matches
    // neither (contracts/CHANGELOG.md 2026-10-20).

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParserReadsTheOptionalJoinedInProgressFlag(bool joined)
    {
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["joined_in_progress"] = joined }))!;
        Assert.Equal(joined, parsed.JoinedInProgress);
        Assert.Null(RequestParsers.Filter(new PayloadReader(new JsonObject()))!.JoinedInProgress);
    }

    [Fact]
    public void ParserRefusesANonBooleanJoinedInProgressFlag() => Assert.Throws<CollectorException>(() =>
        RequestParsers.Filter(new PayloadReader(new JsonObject { ["joined_in_progress"] = "yes" })));

    [Fact]
    public void TheSqlNamesTheJoinedInProgressColumnOnlyWhenTheFlagIsGiven()
    {
        Assert.DoesNotContain("joined_in_progress", RunFilterSql.Build(new RunFilter(), false).Where);
        Assert.Contains("joined_in_progress = 1", RunFilterSql.Build(new RunFilter { JoinedInProgress = true }, false).Where);
        Assert.Contains("joined_in_progress = 0", RunFilterSql.Build(new RunFilter { JoinedInProgress = false }, true).Where);
    }

    [Fact]
    public void EachJoinedInProgressValueListsOnlyTheRunsRecordedWithIt()
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
    public void TheImportFactsNameTheJoinedInProgressFieldOnlyWhenTheSourceSaidSo()
    {
        var unsaid = MentorRecorder.Collector.Import.RunImportCandidateNormalizer.Facts(TestDatabase.Run());
        var said = MentorRecorder.Collector.Import.RunImportCandidateNormalizer.Facts(
            TestDatabase.Run() with { JoinedInProgress = true });

        Assert.DoesNotContain("joined_in_progress", unsaid, StringComparison.Ordinal);
        Assert.Contains("\"joined_in_progress\":true", said, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ localized name search ----

    [Theory]
    [InlineData("Paladin", "骑士")]
    [InlineData("Sastasha", "天然要害沙斯塔夏溶洞")]
    [InlineData("raw percent", "50%")]
    [InlineData("raw underscore", "a_b")]
    [InlineData("raw slash", @"a\b")]
    public void NameAliasIsOrWithOriginalTextButDoesNotSearchNotes(string text, string nameText)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var nameMatch = TestDatabase.Run() with { JobName = nameText, Note = "unrelated" };
        var noteMatch = TestDatabase.Run() with { JobName = "白魔法师", Note = "helped " + text.ToLowerInvariant() };
        var originalNameMatch = TestDatabase.Run() with { DutyName = text + " hall", JobName = "白魔法师" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(nameMatch, tx);
            runs.Insert(noteMatch, tx);
            runs.Insert(originalNameMatch, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "白魔法师", Note = "only " + nameText }, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "50 percent, axb, ab", Note = "unrelated" }, tx);
        });
        var filter = RequestParsers.Filter(new PayloadReader(new JsonObject
        {
            ["text"] = text, ["name_text"] = nameText,
        }));

        var page = runs.Query(filter, null, 1, 50);
        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { nameMatch.RunId, noteMatch.RunId, originalNameMatch.RunId }.Order(),
            page.Items.Select(run => run.RunId).Order());
    }

    [Theory]
    [InlineData("{\"name_text\":\"骑士\"}", 1)]
    [InlineData("{\"text\":\"Paladin\"}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_text\":null}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_text\":\"\"}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_text\":\"  \"}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_text\":\"骑士\"}", 2)]
    public void EmptyAndOmittedSearchFieldsKeepTheirDeclaredScope(string json, int expected)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run() with { JobName = "骑士" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "helped a Paladin" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "helped 骑士" }, tx);
        });
        var filter = RequestParsers.Filter(new PayloadReader(JsonNode.Parse(json)!.AsObject()));
        Assert.Equal(expected, runs.Query(filter, null, 1, 50).Total);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void NameAliasRejectsNonStringValues(string value)
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Filter(
            new PayloadReader(JsonNode.Parse("{\"name_text\":" + value + "}")!.AsObject())));
        Assert.Equal("ERR_BAD_REQUEST", error.Code);
        Assert.Equal("payload.name_text", error.Field);
    }

    [Fact]
    public void NameAliasRejectsMoreThanTwoHundredCharacters()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Filter(
            new PayloadReader(new JsonObject { ["name_text"] = new string('字', 201) })));
        Assert.Equal("ERR_BAD_REQUEST", error.Code);
        Assert.Equal("payload.name_text", error.Field);
    }

    [Fact]
    public void NameAliasAcceptsTwoHundredCharacters()
    {
        var text = new string('字', 200);
        var filter = RequestParsers.Filter(new PayloadReader(new JsonObject { ["name_text"] = text }));
        Assert.Equal(text, filter!.NameText);
    }

    // Recorded names hold one another: 骑士 is inside 暗黑骑士 and 龙骑士, and a normal
    // duty's name inside its Savage mode's. Exact names find only the runs they name.
    [Fact]
    public void ExactNamesFindOnlyTheNamedJobsAndDutiesOrWithOriginalText()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var paladin = TestDatabase.Run() with { JobName = "骑士" };
        var note = TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "helped a Paladin" };
        var normal = TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", DutyName = "万魔殿 边境之狱4" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(paladin, tx);
            runs.Insert(note, tx);
            runs.Insert(normal, tx);
            runs.Insert(TestDatabase.Run(jobId: 32) with { JobName = "暗黑骑士" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 22) with { JobName = "龙骑士" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", DutyName = "圆桌骑士歼灭战" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", DutyName = "零式万魔殿 边境之狱4" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "骑士" }, tx);
        });
        var filter = RequestParsers.Filter(new PayloadReader(new JsonObject
        {
            ["text"] = "Paladin", ["name_exact"] = new JsonArray("骑士", "万魔殿 边境之狱4"),
        }));

        var page = runs.Query(filter, null, 1, 50);
        Assert.Equal(new[] { paladin.RunId, note.RunId, normal.RunId }.Order(),
            page.Items.Select(run => run.RunId).Order());
        Assert.Equal(3, page.Total);
    }

    [Theory]
    [InlineData("{\"name_exact\":[\"骑士\"]}", 1)]
    [InlineData("{\"name_exact\":[\" 骑士\"]}", 0)]
    [InlineData("{\"name_exact\":[\"骑\"]}", 0)]
    [InlineData("{\"text\":\"Paladin\",\"name_exact\":[]}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_exact\":null}", 1)]
    [InlineData("{\"text\":\"Paladin\",\"name_exact\":[\"骑士\"]}", 2)]
    [InlineData("{\"text\":\"Paladin\",\"name_text\":\"骑士\",\"name_exact\":[\"骑士\"]}", 3)]
    public void ExactNamesKeepTheirDeclaredScope(string json, int expected)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run() with { JobName = "骑士" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "helped a Paladin" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 32) with { JobName = "暗黑骑士" }, tx);
            runs.Insert(TestDatabase.Run(jobId: 24) with { JobName = "白魔法师", Note = "骑士" }, tx);
        });
        var filter = RequestParsers.Filter(new PayloadReader(JsonNode.Parse(json)!.AsObject()));
        Assert.Equal(expected, runs.Query(filter, null, 1, 50).Total);
    }

    [Fact]
    public void ExactNamesAcceptAThousandNamesOfTwoHundredCharacters()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => runs.Insert(TestDatabase.Run() with { JobName = "骑士" }, tx));
        var names = new JsonArray(Enumerable.Range(0, 999)
            .Select(i => (JsonNode?)JsonValue.Create(i.ToString("D3") + new string('字', 197)))
            .Append(JsonValue.Create("骑士"))
            .ToArray());
        var filter = RequestParsers.Filter(new PayloadReader(new JsonObject { ["name_exact"] = names }));

        Assert.Equal(1000, filter!.NameExact.Count);
        Assert.Equal(1, runs.Query(filter, null, 1, 50).Total);
    }

    [Theory]
    [InlineData("\"骑士\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[42]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"骑士\",\"\"]")]
    public void ExactNamesRejectAnythingButNonEmptyStrings(string value)
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Filter(
            new PayloadReader(JsonNode.Parse("{\"name_exact\":" + value + "}")!.AsObject())));
        Assert.Equal("ERR_BAD_REQUEST", error.Code);
        Assert.Equal("payload.name_exact", error.Field);
    }

    [Fact]
    public void ExactNamesRejectAnOverlongNameAndMoreThanAThousand()
    {
        var overlong = Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(
            new JsonObject { ["name_exact"] = new JsonArray(new string('字', 201)) })));
        Assert.Equal("payload.name_exact", overlong.Field);
        var tooMany = Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(
            new JsonObject
            {
                ["name_exact"] = new JsonArray(Enumerable.Range(0, 1001)
                    .Select(i => (JsonNode?)JsonValue.Create("名" + i)).ToArray()),
            })));
        Assert.Equal("payload.name_exact", tooMany.Field);
    }
}
