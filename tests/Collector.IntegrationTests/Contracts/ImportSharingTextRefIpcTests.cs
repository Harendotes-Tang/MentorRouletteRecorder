using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Import sentences over a real Named Pipe carry their catalogue references beside the Chinese
/// (contracts/collector-texts.md §2.1): the preview rows' <c>error_refs</c> / <c>warning_refs</c> and
/// the refusals' <c>message_ref</c>. Each reference renders back to exactly the Chinese an older
/// Desktop shows. The <c>ImportCalibrationCode</c> answers' references are held by
/// <see cref="SharedCalibrationIpcTests"/>, which sends the same requests.
/// </summary>
public sealed class ImportSharingTextRefIpcTests
{
    [Fact]
    public async Task PreviewRowsCarryReferencesParallelToTheirErrorsAndWarnings()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "SCREENSHOT", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(
                new JsonObject
                {
                    ["duty_name"] = "合成截图副本", ["reflection_text"] = "合成截图心得",
                    ["source_recorded_at"] = "2026-10-08 20:30:00", ["warnings"] = new JsonArray("需要核对职业"),
                },
                new JsonObject { ["duty_name"] = "合成截图副本", ["matched_at_utc"] = "2026-10-08" }),
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "import preview with references");

        var rows = preview["rows"]!.AsArray();
        foreach (var row in rows)
        {
            AssertParallelAllowingVerbatim(row!["errors"], row["error_refs"]);
            AssertParallelAllowingVerbatim(row["warnings"], row["warning_refs"]);
        }

        var warnings = rows[0]!["warning_refs"]!.AsArray();
        Assert.Contains(warnings, item => item is not null && CollectorTextCatalogue.Key(item) == "import.warning.result_defaulted");
        Assert.Null(warnings[^1]);
        var error = Assert.Single(rows[1]!["error_refs"]!.AsArray());
        Assert.Equal("import.row.time_date_only", CollectorTextCatalogue.Key(error));
        Assert.Equal("import.time_field.matched", CollectorTextCatalogue.Key(error!["args"]!["field"]));
    }

    [Fact]
    public async Task AnUnreadableSourceAndACommitRefusalCarryTheirReferences()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var unreadable = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "JSON", ["text"] = "{}" });
        CollectorTextCatalogue.AssertRefusal(unreadable, "import.source.unreadable");
        Assert.Equal("import.json.not_array", CollectorTextCatalogue.Key(unreadable.Payload["message_ref"]!["args"]!["detail"]));
        ContractSchema.Validate("$defs/ErrorPayload", unreadable.Payload, "unreadable import source");

        var header = await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "PASTE", ["text"] = "\"副本\"x\tnote\r\n合成\t备注",
        });
        CollectorTextCatalogue.AssertRefusal(header, "import.source.unreadable");
        Assert.Equal("import.table.header_invalid", CollectorTextCatalogue.Key(header.Payload["message_ref"]!["args"]!["detail"]));

        var refused = await client.SendAsync("CommitRunImport", new JsonObject
        {
            ["preview_id"] = Guid.NewGuid().ToString("D"), ["row_numbers"] = new JsonArray(1), ["confirm_own_records"] = false,
        });
        CollectorTextCatalogue.AssertRefusal(refused, "import.commit.own_records_required");
    }

    /// <summary>As long and in the same order; a null entry is a sentence the catalogue does not word, such as the source's own.</summary>
    private static void AssertParallelAllowingVerbatim(JsonNode? chinese, JsonNode? references)
    {
        var texts = Assert.IsType<JsonArray>(chinese);
        var refs = Assert.IsType<JsonArray>(references);
        Assert.Equal(texts.Count, refs.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            if (refs[i] is { } reference)
            {
                Assert.Equal(texts[i]!.GetValue<string>(), CollectorTextCatalogue.Render(reference));
            }
        }
    }
}
