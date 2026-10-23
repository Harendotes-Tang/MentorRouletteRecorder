using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Import;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-20 H2-4: game times written without a zone are read in the region the user picks. The
/// candidate keeps them as written, so choosing another region and checking again reads them again.
/// </summary>
public sealed class RunImportTimeZoneTests
{
    private static JsonObject Row(JsonObject preview) => preview["rows"]![0]!.AsObject();

    [Fact]
    public void ChangingTheRegionRereadsLocalGameTimes()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = service.PreviewSource("PASTE", text: "副本\t进本时间\t结束时间\tmatched_at\n合成副本\t2026-10-10 20:00\t2026-10-10 20:30\t2026-10-10 19:59",
            timeZone: "+08:00", sourceName: "粘贴的表格");
        Assert.Equal("2026-10-10T12:00:00.000Z", Row(first)["run"]!["entered_at_utc"]!.GetValue<string>());
        var candidate = Row(first)["candidate"]!.DeepClone().AsObject();
        Assert.Equal("2026-10-10 20:00", candidate["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10 20:30", candidate["ended_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10 19:59", candidate["matched_at_utc"]!.GetValue<string>());

        var rechecked = service.PreviewSource("PASTE", rows: new JsonArray(candidate), timeZone: "+09:00", sourceName: "粘贴的表格");
        var run = Row(rechecked)["run"]!;
        Assert.Equal("2026-10-10T11:00:00.000Z", run["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10T11:30:00.000Z", run["ended_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10T10:59:00.000Z", run["matched_at_utc"]!.GetValue<string>());
        Assert.Equal(1_800_000, run["duration_ms"]!.GetValue<long>());
        Assert.Equal(candidate["source_key"]!.GetValue<string>(), Row(rechecked)["candidate"]!["source_key"]!.GetValue<string>());
    }

    /// <summary>
    /// Audit 2026-10-20 H2-7: a file the import will not read because it is not on a local disk is refused
    /// as an import request, about the file_path field and in import words, not as a failed export.
    /// </summary>
    [Theory]
    [InlineData(@"\\synthetic-server\share\personal-records.csv")]
    [InlineData(@"\\?\UNC\synthetic-server\share\personal-records.csv")]
    public void AFileOffTheLocalDiskIsRefusedInImportWords(string path)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var refusal = Assert.Throws<CollectorException>(() => service.PreviewSource("CSV", filePath: path));
        Assert.Equal(ErrorCodes.BadRequest, refusal.Code);
        Assert.Equal("file_path", refusal.Field);
        Assert.Equal("import.preview.path_not_local", refusal.Text?.Key);
        Assert.DoesNotContain("导出", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesWithTheirOwnOffsetIgnoreTheRegionAndStayCanonical()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = service.PreviewSource("ROWS", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "合成副本", ["entered_at_utc"] = "2026-10-10T20:00:00+08:00",
        }), timeZone: "+08:00");
        var candidate = Row(first)["candidate"]!.DeepClone().AsObject();
        Assert.Equal("2026-10-10T12:00:00.000Z", candidate["entered_at_utc"]!.GetValue<string>());
        var rechecked = service.PreviewSource("ROWS", rows: new JsonArray(candidate), timeZone: "+09:00");
        Assert.Equal("2026-10-10T12:00:00.000Z", Row(rechecked)["run"]!["entered_at_utc"]!.GetValue<string>());
    }
}
