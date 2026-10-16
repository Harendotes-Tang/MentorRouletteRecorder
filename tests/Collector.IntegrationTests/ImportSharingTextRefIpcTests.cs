using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Import and shared-calibration sentences over a real Named Pipe carry their catalogue references
/// beside the Chinese (contracts/collector-texts.md §2.1): the <c>ImportCalibrationCode</c>
/// <c>message_ref</c>, the preview rows' <c>error_refs</c> / <c>warning_refs</c> and the refusals'
/// <c>message_ref</c>. Each reference renders back to exactly the Chinese an older Desktop shows.
/// </summary>
public sealed class ImportSharingTextRefIpcTests
{
    [Fact]
    public async Task EveryImportCalibrationCodeAnswerCarriesItsMessageAsAReference()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var garbage = Answered(await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" }));
        Assert.Equal("sharing.import.not_imported", Key(garbage["message_ref"]));
        Assert.Equal("sharing.code.characters", Key(garbage["message_ref"]!["args"]!["reason"]));

        var overlong = Answered(await client.SendAsync("ImportCalibrationCode",
            new JsonObject { ["code"] = ShareCode.Prefix + new string('A', ShareCode.MaxCodeLength) }));
        Assert.Equal("sharing.import.code_too_long", Key(overlong["message_ref"]));
        Assert.Equal(ShareCode.MaxCodeLength, overlong["message_ref"]!["args"]!["max"]!.GetValue<int>());

        var path = Path.Combine(ContractSchema.RepositoryRoot, "tests", "Fixtures", "shared-calibration", "vectors.json");
        var code = JsonNode.Parse(File.ReadAllText(path))!["valid"]![0]!["code"]!.GetValue<string>();
        var notCalibrating = Answered(await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = code }));
        Assert.Equal("sharing.import.not_calibrating", Key(notCalibrating["message_ref"]));

        var missing = await client.SendAsync("ImportCalibrationCode", new JsonObject());
        AssertErrorRef(missing, "ipc.field.missing");
        Assert.Equal("code", missing.Payload["message_ref"]!["args"]!["field"]!.GetValue<string>());

        AssertErrorRef(await client.SendAsync("GetCalibrationShareCode"), "sharing.share_code.no_profile");
    }

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
        Assert.Contains(warnings, item => item is not null && Key(item) == "import.warning.result_defaulted");
        Assert.Null(warnings[^1]);
        var error = Assert.Single(rows[1]!["error_refs"]!.AsArray());
        Assert.Equal("import.row.time_date_only", Key(error));
        Assert.Equal("import.time_field.matched", Key(error!["args"]!["field"]));
    }

    [Fact]
    public async Task AnUnreadableSourceAndACommitRefusalCarryTheirReferences()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var unreadable = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "JSON", ["text"] = "{}" });
        AssertErrorRef(unreadable, "import.source.unreadable");
        Assert.Equal("import.json.not_array", Key(unreadable.Payload["message_ref"]!["args"]!["detail"]));
        ContractSchema.Validate("$defs/ErrorPayload", unreadable.Payload, "unreadable import source");

        var header = await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "PASTE", ["text"] = "\"副本\"x\tnote\r\n合成\t备注",
        });
        AssertErrorRef(header, "import.source.unreadable");
        Assert.Equal("import.table.header_invalid", Key(header.Payload["message_ref"]!["args"]!["detail"]));

        var refused = await client.SendAsync("CommitRunImport", new JsonObject
        {
            ["preview_id"] = Guid.NewGuid().ToString("D"), ["row_numbers"] = new JsonArray(1), ["confirm_own_records"] = false,
        });
        AssertErrorRef(refused, "import.commit.own_records_required");
    }

    private static JsonObject Answered(IpcResponse response)
    {
        Assert.True(response.Ok, response.ErrorMessage);
        ContractSchema.Validate("$defs/Responses/ImportCalibrationCode", response.Payload, "ImportCalibrationCode response payload");
        CollectorTextCatalogue.AssertParallel(response.Payload["message"], response.Payload["message_ref"]);
        return response.Payload;
    }

    private static string Key(JsonNode? reference) => reference!["key"]!.GetValue<string>();

    private static void AssertErrorRef(IpcResponse response, string key)
    {
        Assert.False(response.Ok);
        Assert.Equal(key, Key(response.Payload["message_ref"]));
        Assert.Equal(response.ErrorMessage, CollectorTextCatalogue.Render(response.Payload["message_ref"]));
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
