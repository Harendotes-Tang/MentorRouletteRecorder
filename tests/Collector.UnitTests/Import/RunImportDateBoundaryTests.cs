using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunImportDateBoundaryTests
{
    private static JsonObject Candidate(string entered) => new()
    {
        ["duty_name"] = "边界日期副本", ["entered_at_utc"] = entered,
    };

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z")]
    [InlineData("9999-12-31T23:59:59.999Z")]
    public void ExtremeDateDoesNotAbortPreviewOrCommitOfOtherRows(string entered)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(
            Candidate(entered), Candidate("2026-10-23T12:00:00.000Z")));
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("new", row!["status"]!.GetValue<string>()));
        Assert.Equal(entered, rows[0]!["run"]!["entered_at_utc"]!.GetValue<string>());

        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(),
            new[] { 1, 2 }, true, Guid.NewGuid().ToString("D"));
        var ids = committed["run_ids"]!.AsArray();
        Assert.Equal(2, ids.Count);
        var repository = new RunRepository(fixture.Database);
        Assert.Contains(ids, id => repository.Get(id!.GetValue<string>())!.EnteredAtUtc == UtcTimestamp.Parse(entered));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:00:00.000Z", true)]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:01:00.000Z", true)]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:01:00.001Z", false)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:59:59.999Z", true)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:58:59.999Z", true)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:58:59.998Z", false)]
    public void ExtremeDateKeepsTheInclusiveOneMinuteDuplicateWindow(string entered, string localTime, bool nearby)
    {
        using var fixture = new TestDatabase();
        var local = TestDatabase.Run() with
        {
            ContentId = null, TerritoryId = null, DutyName = "边界日期副本",
            EnteredAtUtc = UtcTimestamp.Parse(localTime), MatchedAtUtc = null, EndedAtUtc = null, DurationMs = null,
        };
        fixture.Database.RunInTransaction(tx => new RunRepository(fixture.Database).Insert(local, tx));
        var preview = new RunImportService(fixture.Database, fixture.Clock)
            .PreviewSource("ROWS", rows: new JsonArray(Candidate(entered)));
        var row = preview["rows"]![0]!;
        Assert.Equal(nearby ? "possible_duplicate" : "new", row["status"]!.GetValue<string>());
        Assert.Equal(nearby ? local.RunId : null, row["matching_run_id"]?.GetValue<string>());
    }
}
