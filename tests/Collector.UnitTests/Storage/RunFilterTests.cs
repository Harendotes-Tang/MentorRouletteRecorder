using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The run filter as a request carries it and as SQL applies it: <c>deleted_only</c>,
/// <c>joined_in_progress</c> and the localized name search.
/// </summary>
public sealed class RunFilterTests
{
    [Theory]
    [InlineData("CN", Region.Cn)]
    [InlineData("TC", Region.Tc)]
    [InlineData("GLOBAL", Region.Global)]
    [InlineData("UNKNOWN", Region.Unknown)]
    [InlineData(null, null)]
    public void RegionalFilterAppliesToQueriesAndCounts(string? token, Region? expected)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            foreach (var region in Enum.GetValues<Region>())
                runs.Insert(TestDatabase.Run() with { Region = region }, tx);
        });
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["region"] = token }))!;
        Assert.Equal(expected, parsed.Region);
        var page = runs.Query(parsed, null, 1, 50);
        Assert.Equal(expected is null ? 4 : 1, page.Total);
        if (expected is { } owner) Assert.Equal(owner, Assert.Single(page.Items).Region);
        Assert.Equal(4, runs.Query(RequestParsers.Filter(new PayloadReader(new JsonObject())), null, 1, 50).Total);
    }

    [Theory]
    [InlineData("TW")]
    [InlineData("tc")]
    [InlineData("ALL")]
    public void RegionalFilterRejectsUndeclaredTokens(string token)
    {
        Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(new JsonObject { ["region"] = token })));
    }

    [Fact]
    public void DutyFilterUsesOnlyTheOwningRegionTerritoryAndUnknownRequiresConsensus()
    {
        var catalog = DutyCatalog.Compose(new[]
        {
            new DutyDocument(Region.Cn, "test", true, new Dictionary<int, DutyInfo>
                { [1] = new(1, 100, "国服副本", null, Region.Cn, true) }),
            new DutyDocument(Region.Tc, "test", true, new Dictionary<int, DutyInfo>
                { [1] = new(1, 200, "繁中副本", null, Region.Tc, true), [2] = new(2, 100, "繁中另一个副本", null, Region.Tc, true) }),
        });
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            foreach (var owner in new[] { Region.Cn, Region.Tc, Region.Unknown })
                foreach (var territory in new[] { 100, 200 })
                    runs.Insert(TestDatabase.Run(contentId: null) with { Region = owner, TerritoryId = territory }, tx);
        });
        int Count(Region? owner)
        {
            var filter = RunFilterSql.Build(new RunFilter { Region = owner, ContentIds = new[] { 1 } }, false, catalog);
            using var command = fixture.Database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM mentor_runs WHERE " + filter.Where;
            RunFilterSql.Bind(command, filter);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Equal(1, Count(Region.Cn));
        Assert.Equal(1, Count(Region.Tc));
        Assert.Equal(1, Count(Region.Unknown)); // Territory 100 disagrees, 200 has one unambiguous owner.
        Assert.Equal(3, Count(null));
    }

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

    [Fact]
    public void ExactNamesAccept4096ShortNamesAndMatchTheLastNameInSql()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var matching = TestDatabase.Run() with { JobName = "骑士" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(matching, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "暗黑骑士" }, tx);
        });
        var names = new JsonArray(Enumerable.Range(0, 4095)
            .Select(i => (JsonNode?)JsonValue.Create("名" + i))
            .Append(JsonValue.Create("骑士"))
            .ToArray());
        var filter = RequestParsers.Filter(new PayloadReader(new JsonObject { ["name_exact"] = names }));

        Assert.Equal(4096, filter!.NameExact.Count);
        var page = runs.Query(filter, null, 1, 50);
        Assert.Equal(1, page.Total);
        Assert.Equal(matching.RunId, Assert.Single(page.Items).RunId);
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
    public void ExactNamesReject201CharactersAnd4097Names()
    {
        var overlong = Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(
            new JsonObject { ["name_exact"] = new JsonArray(new string('字', 201)) })));
        Assert.Equal("payload.name_exact", overlong.Field);
        var tooMany = Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(
            new JsonObject
            {
                ["name_exact"] = new JsonArray(Enumerable.Range(0, 4097)
                    .Select(i => (JsonNode?)JsonValue.Create("名" + i)).ToArray()),
            })));
        Assert.Equal("payload.name_exact", tooMany.Field);
    }

    [Theory]
    [InlineData("灯塔")]
    [InlineData("一次过")]
    [InlineData("50%")]
    [InlineData("a_b")]
    [InlineData(@"a")]
    public void ReflectionTextFindsOnlyRunsWhoseReflectionContainsItOrWithOriginalText(string words)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var reflections = new RunReflectionRepository(fixture.Database);
        var now = fixture.Clock.UtcNow;
        var reflected = TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" };
        var noteMatch = TestDatabase.Run() with { JobName = "白魔法师", Note = "note says " + words };
        var otherReflection = TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" };
        var lookalike = TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(reflected, tx);
            runs.Insert(noteMatch, tx);
            runs.Insert(otherReflection, tx);
            runs.Insert(lookalike, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" }, tx);
            reflections.Upsert(reflected.RunId, ReflectionMood.Good, "带新人打 " + words + " 的心得", now, tx);
            reflections.Upsert(otherReflection.RunId, ReflectionMood.Ok, "nothing in common", now, tx);
            // The LIKE wildcards in the search text are literal: 50 percent, axb and a/b stay out.
            reflections.Upsert(lookalike.RunId, ReflectionMood.Bad, "50 percent, axb, a/b, ab", now, tx);
        });

        var onlyReflections = runs.Query(RequestParsers.Filter(new PayloadReader(new JsonObject
        {
            ["reflection_text"] = words,
        })), null, 1, 50);
        Assert.Equal(new[] { reflected.RunId }, onlyReflections.Items.Select(run => run.RunId));

        var either = runs.Query(RequestParsers.Filter(new PayloadReader(new JsonObject
        {
            ["text"] = words, ["reflection_text"] = words,
        })), null, 1, 50);
        Assert.Equal(new[] { reflected.RunId, noteMatch.RunId }.Order(),
            either.Items.Select(run => run.RunId).Order());

        var narrowed = runs.Query(RequestParsers.Filter(new PayloadReader(new JsonObject
        {
            ["text"] = words, ["reflection_text"] = words, ["with_reflection"] = true,
        })), null, 1, 50);
        Assert.Equal(new[] { reflected.RunId }, narrowed.Items.Select(run => run.RunId));
    }

    [Theory]
    [InlineData("{\"reflection_text\":null}", 3)]
    [InlineData("{\"reflection_text\":\"\"}", 3)]
    [InlineData("{\"reflection_text\":\"  \"}", 3)]
    [InlineData("{\"reflection_text\":\"灯塔\"}", 1)]
    [InlineData("{\"text\":\"灯塔\"}", 1)]
    [InlineData("{\"text\":\"灯塔\",\"reflection_text\":\"灯塔\"}", 2)]
    public void ReflectionTextKeepsItsDeclaredScope(string json, int expected)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var reflections = new RunReflectionRepository(fixture.Database);
        var reflected = TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(reflected, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "白魔法师", Note = "灯塔 in the note" }, tx);
            runs.Insert(TestDatabase.Run() with { JobName = "白魔法师", Note = "unrelated" }, tx);
            reflections.Upsert(reflected.RunId, ReflectionMood.Good, "灯塔一次过", fixture.Clock.UtcNow, tx);
        });
        var filter = RequestParsers.Filter(new PayloadReader(JsonNode.Parse(json)!.AsObject()));
        Assert.Equal(expected, runs.Query(filter, null, 1, 50).Total);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    public void ReflectionTextRejectsNonStringValues(string value)
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Filter(
            new PayloadReader(JsonNode.Parse("{\"reflection_text\":" + value + "}")!.AsObject())));
        Assert.Equal("ERR_BAD_REQUEST", error.Code);
        Assert.Equal("payload.reflection_text", error.Field);
    }

    [Fact]
    public void ReflectionTextAcceptsTwoHundredCharactersAndRejectsMore()
    {
        var accepted = RequestParsers.Filter(new PayloadReader(
            new JsonObject { ["reflection_text"] = new string('字', 200) }));
        Assert.Equal(200, accepted!.ReflectionText!.Length);
        var overlong = Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(
            new JsonObject { ["reflection_text"] = new string('字', 201) })));
        Assert.Equal("payload.reflection_text", overlong.Field);
    }
}
