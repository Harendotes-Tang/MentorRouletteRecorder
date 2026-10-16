using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class LocalizedSearchTests
{
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
}
