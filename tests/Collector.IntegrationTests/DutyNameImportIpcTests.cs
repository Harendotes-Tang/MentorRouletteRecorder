using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>真实隔离管道验证按名推断与显式编号；合成记录不代表反馈者原表验收。</summary>
public sealed class DutyNameImportIpcTests
{
    [Fact]
    public async Task AsuraPreviewRevalidationCommitRetryAndReimportPreserveSourceAndExistingIdentities()
    {
        var historicalUnknown = Historical(null);
        var historical69 = Historical(69);
        await using var fixture = ServerFixture.Start(seed: host => host.Database.RunInTransaction(transaction =>
        {
            host.Runs.Insert(historicalUnknown, transaction);
            host.Runs.Insert(historical69, transaction);
        }));
        await using var client = await fixture.ConnectAsync();
        var source = new JsonArray(
            Candidate("阿修罗歼灭战", "2026-10-20"),
            Candidate("阿修罗歼灭战", "2026-10-21"),
            Candidate("阿修罗歼灭战", "2026-10-22"));
        source[1]!["content_id"] = null;
        source[1]!["territory_id"] = null;
        source[1]!["duty_category"] = null;
        source[2]!["content_id"] = 69;
        async Task<JsonObject> Preview(JsonArray rows) => (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["source_name"] = "synthetic-asura", ["rows"] = rows.DeepClone(),
        })).Require();
        var preview = await Preview(source);
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "Asura inferred and explicit identities");
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(new int?[] { 944, 944, 69 }, rows.Select(row => row!["run"]!["content_id"]?.GetValue<int>()));
        Assert.All(rows, row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        Assert.False(rows[0]!["candidate"]!.AsObject().ContainsKey("content_id"));
        Assert.Null(rows[1]!["candidate"]!["content_id"]);
        Assert.Null(rows[1]!["candidate"]!["territory_id"]);
        Assert.Null(rows[1]!["candidate"]!["duty_category"]);
        Assert.Equal(69, rows[2]!["candidate"]!["content_id"]!.GetValue<int>());
        Assert.Equal(2, (await client.SendAsync("QueryRuns")).Require()["page_info"]!["total"]!.GetValue<int>());
        var candidates = new JsonArray(rows.Select(row => row!["candidate"]!.DeepClone()).ToArray());
        var revalidated = await Preview(candidates);
        Assert.Equal(new int?[] { 944, 944, 69 }, revalidated["rows"]!.AsArray()
            .Select(row => row!["run"]!["content_id"]?.GetValue<int>()));
        var payload = new JsonObject
        {
            ["preview_id"] = revalidated["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1, 2, 3),
            ["confirm_own_records"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(3, committed["imported_count"]!.GetValue<int>());
        Assert.True((await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId))
            .Require()["replayed"]!.GetValue<bool>());
        var query = (await client.SendAsync("QueryRuns")).Require();
        Assert.Equal(5, query["page_info"]!["total"]!.GetValue<int>());
        var ids = committed["run_ids"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(new int?[] { 944, 944, 69 }, ids.Select(id => fixture.Host.Runs.Get(id)!.ContentId));
        Assert.All(ids, id => Assert.Equal("阿修罗歼灭战", fixture.Host.Runs.Get(id)!.DutyName));
        Assert.Equal(historicalUnknown, fixture.Host.Runs.Get(historicalUnknown.RunId));
        Assert.Equal(historical69, fixture.Host.Runs.Get(historical69.RunId));
        var repeat = await Preview(source);
        Assert.All(repeat["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        var exportPath = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "asura-identities.json");
        (await client.SendAsync("ExportJson", new JsonObject { ["target_path"] = exportPath })).Require();
        var exported = JsonNode.Parse(File.ReadAllText(exportPath))!.AsArray();
        Assert.Equal(5, exported.Count);
        Assert.Null(exported.Single(node => node!["run_id"]!.GetValue<string>() == historicalUnknown.RunId)!["content_id"]);
        Assert.Equal(69, exported.Single(node => node!["run_id"]!.GetValue<string>() == historical69.RunId)!["content_id"]!.GetValue<int>());
    }

    [Fact]
    public async Task AllOtherDuplicateGroupsStayUnknownUntilTheCandidateSuppliesAnActualId()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var catalog = DutyCatalog.Default;
        var groups = catalog.Documents.SelectMany(document => document.Rows.Keys).Distinct()
            .Select(id => catalog.Find(id, Region.Cn)).OfType<DutyInfo>()
            .GroupBy(row => row.LocalizedName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && group.Key != "阿修罗歼灭战").ToArray();
        Assert.Equal(16, groups.Length);
        var source = new JsonArray(groups.Select(group => (JsonNode)Candidate(group.Key, "2026-10-22")).ToArray());
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = source,
        })).Require();
        var candidates = new JsonArray(preview["rows"]!.AsArray().Select((row, index) =>
        {
            Assert.Null(row!["run"]!["content_id"]);
            var candidate = row["candidate"]!.DeepClone().AsObject();
            Assert.False(candidate.ContainsKey("content_id"));
            candidate["content_id"] = groups[index].Max(duty => duty.ContentId);
            return (JsonNode)candidate;
        }).ToArray());
        var explicitPreview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = candidates,
        })).Require();
        Assert.Equal(groups.Select(group => (int?)group.Max(duty => duty.ContentId)),
            explicitPreview["rows"]!.AsArray().Select(row => row!["run"]!["content_id"]?.GetValue<int>()));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    private static JsonObject Candidate(string name, string day) => new()
    {
        ["region"] = "CN", ["duty_name"] = name, ["job_id"] = 19,
        ["source_recorded_at"] = day, ["result"] = "COMPLETED",
    };

    private static MentorRun Historical(int? contentId)
    {
        var time = DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new MentorRun
        {
            RunId = Guid.NewGuid().ToString("D"), Revision = 1, Region = Region.Cn, ContentId = contentId,
            DutyName = "阿修罗歼灭战", Source = RunSource.Manual, Result = RunResult.Completed,
            EnteredAtUtc = time, EndedAtUtc = time.AddMinutes(10), DurationMs = 600000,
            CreatedAtUtc = time, UpdatedAtUtc = time,
        };
    }
}
