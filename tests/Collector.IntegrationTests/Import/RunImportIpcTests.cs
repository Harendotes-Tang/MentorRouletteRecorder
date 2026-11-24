using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class RunImportIpcTests
{
    [Fact]
    public async Task MixedRegionImportAndBaselineDeductionStayIsolatedAcrossRealPipeRetries()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        foreach (var (region, baseline) in new[] { ("CN", 100), ("TC", 50) })
        {
            var saved = (await client.SendAsync("UpdateAchievementBaseline", new JsonObject
            {
                ["region"] = region, ["baseline_completed_count"] = baseline,
                ["baseline_effective_at"] = "2026-01-01T00:00:00.000Z", ["reason"] = "分区起始次数",
            })).Require();
            ContractSchema.Validate("$defs/Responses/UpdateAchievementBaseline", saved, "regional baseline");
            Assert.Equal(region, saved["region"]!.GetValue<string>());
        }
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS",
            ["rows"] = new JsonArray(new[] { "CN", "TC" }.Select(region => (JsonNode)new JsonObject
            {
                ["region"] = region, ["game_build"] = "synthetic-region-build",
                ["content_id"] = 900001, ["duty_name"] = "分区合成副本", ["result"] = "COMPLETED",
                ["entered_at_utc"] = "2026-09-01T00:00:00.000Z", ["ended_at_utc"] = "2026-09-01T00:30:00.000Z",
            }).ToArray()),
        })).Require();
        Assert.All(preview["rows"]!.AsArray(), row => Assert.Equal("new", row!["status"]!.GetValue<string>()));
        var commit = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1, 2),
            ["confirm_own_records"] = true, ["deduct_from_baseline"] = true,
        };
        var (result, replay) = await CommitTwiceAsync(client, commit);
        ContractSchema.Validate("$defs/Responses/CommitRunImport", result, "mixed regional import");
        Assert.Equal(2, result["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(2, result["baseline_adjustments"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(result["baseline_adjustments"], replay["baseline_adjustments"]));
        var stats = (await client.SendAsync("GetDashboardStats")).Require();
        ContractSchema.Validate("$defs/Responses/GetDashboardStats", stats, "mixed regional dashboard");
        Assert.Equal(100, stats["achievement_progress"]!.GetValue<int>());
        var achievements = stats["achievements"]!.AsArray();
        Assert.Equal(100, achievements.Single(row => row!["region"]!.GetValue<string>() == "CN")!["achievement_progress"]!.GetValue<int>());
        Assert.Equal(50, achievements.Single(row => row!["region"]!.GetValue<string>() == "TC")!["achievement_progress"]!.GetValue<int>());
        var filter = new JsonObject { ["region"] = "TC" };
        var query = (await client.SendAsync("QueryRuns", new JsonObject { ["filter"] = filter.DeepClone() })).Require();
        var run = Assert.Single(query["items"]!.AsArray())!;
        Assert.Equal("TC", run["region"]!.GetValue<string>());
        Assert.Equal("synthetic-region-build", run["game_build"]!.GetValue<string>());
        var regional = (await client.SendAsync("GetDashboardStats", new JsonObject
        {
            ["filter"] = new JsonObject { ["region"] = "TC", ["job_id"] = new JsonArray(999) },
        })).Require();
        Assert.Equal(0, regional["completed_count"]!.GetValue<int>());
        Assert.Equal(50, regional["achievement_progress"]!.GetValue<int>());
        Assert.Single(regional["achievements"]!.AsArray());
    }

    /// <summary>
    /// Commits <paramref name="payload"/>, then sends the same request again under the same request id; the second
    /// answer must be a replay of the first.
    /// </summary>
    private static async Task<(JsonObject Committed, JsonObject Replay)> CommitTwiceAsync(PipeClient client, JsonObject payload)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        var replay = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.True(replay["replayed"]!.GetValue<bool>());
        return (committed, replay);
    }

    [Fact]
    public async Task MalformedSourceIsActionableBadRequestAndConnectionRemainsUsable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var response = await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "JSON", ["text"] = "{}",
        });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Contains("无法读取导入内容", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task EditedPreviewOwnConfirmationCommitRetryReflectionAndStatsUseRealPipe()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "SCREENSHOT", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(new JsonObject
            {
                ["region"] = "CN", ["duty_name"] = "合成截图副本", ["reflection_text"] = "合成截图心得",
                ["source_recorded_at"] = "2026-10-08 20:30:00", ["job_name"] = "骑士",
            }),
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "import preview");
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var candidate = preview["rows"]![0]!["candidate"]!.DeepClone().AsObject();
        candidate["reflection_text"] = "合成截图心得经核对";
        var edited = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "SCREENSHOT", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(candidate),
        })).Require();
        var payload = new JsonObject
        {
            ["preview_id"] = edited["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1),
            ["confirm_own_records"] = false,
        };
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        payload["confirm_own_records"] = true;
        var (committed, retried) = await CommitTwiceAsync(client, payload);
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        ContractSchema.Validate("$defs/Responses/CommitRunImport", retried, "import retry");
        var query = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["with_reflection"] = true },
        })).Require();
        var run = Assert.Single(query["items"]!.AsArray())!;
        ContractSchema.Validate("$defs/Run", run, "imported history");
        Assert.Equal("合成截图心得经核对", run["reflection"]!["text"]!.GetValue<string>());
        Assert.Equal("unknown", run["reflection"]!["mood"]!.GetValue<string>());
        Assert.Equal("COMPLETED", run["result"]!.GetValue<string>());
        Assert.False(run["pending_review"]!.GetValue<bool>());
        Assert.True(run["import_metadata"]!["incomplete"]!.GetValue<bool>());
        Assert.Equal("IMPORT", run["source"]!.GetValue<string>());
        Assert.Null(run["matched_at_utc"]);
        Assert.Null(run["entered_at_utc"]);
        Assert.Null(run["ended_at_utc"]);
        Assert.Equal("2026-10-08T12:30:00.000Z", run["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
        Assert.Null(run["duration_ms"]);
        var initialStats = (await client.SendAsync("GetDashboardStats")).Require();
        Assert.Equal(1, initialStats["completed_count"]!.GetValue<int>());
        Assert.Equal(1, initialStats["achievement_progress"]!.GetValue<int>());
        Assert.Equal(0, initialStats["unfinished_pending_review"]!.GetValue<int>());
        Assert.Null(initialStats["avg_duration_ms"]);
        var runId = run["run_id"]!.GetValue<string>();
        var incompleteCorrection = new JsonObject
        {
            ["run_id"] = runId, ["expected_revision"] = 1, ["reason"] = "合成备注补充",
            ["changes"] = new JsonObject { ["note"] = "已确认通关，实际游戏时间不详" },
        };
        var noteEdited = (await client.SendAsync("CorrectRun", incompleteCorrection.DeepClone().AsObject())).Require()["run"]!;
        Assert.Null(noteEdited["entered_at_utc"]);
        Assert.Null(noteEdited["ended_at_utc"]);
        Assert.Null(noteEdited["duration_ms"]);
        Assert.True(noteEdited["import_metadata"]!["incomplete"]!.GetValue<bool>());
        incompleteCorrection["expected_revision"] = 2;
        incompleteCorrection["reason"] = "补充已知游戏事实";
        var changes = incompleteCorrection["changes"]!.AsObject();
        changes["entered_at_utc"] = "2026-10-08T12:00:00.000Z";
        changes["ended_at_utc"] = "2026-10-08T12:20:00.000Z";
        changes["duration_ms"] = null;
        Assert.True((await client.SendAsync("CorrectRun", incompleteCorrection)).Ok);
        Assert.Equal(1, (await client.SendAsync("GetDashboardStats")).Require()["completed_count"]!.GetValue<int>());
        Assert.Equal(3, (await client.SendAsync("GetRunRevisions", new JsonObject { ["run_id"] = runId })).Require()["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task LargeChinesePreviewRefusesWithTypedErrorAndKeepsPipeUsable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var rows = new JsonArray(Enumerable.Range(0, 600).Select(_ => (JsonNode)new JsonObject
        {
            ["duty_name"] = "合成副本", ["reflection_text"] = new string('中', 1000),
        }).ToArray());
        var response = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "SCREENSHOT", ["rows"] = rows });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Contains("分", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task DeductFromBaselineLowersTheStoredBaselineInTheSameCommit()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("UpdateAchievementBaseline", new JsonObject
        {
            ["goal_count"] = 2000, ["baseline_completed_count"] = 1500,
            ["baseline_effective_at"] = "2026-01-01T00:00:00.000Z", ["reason"] = "安装前已完成 1500 次",
        })).Require();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(new JsonObject
            {
                ["region"] = "CN", ["duty_name"] = "合成表格副本", ["job_name"] = "骑士", ["result"] = "COMPLETED",
                ["source_recorded_at"] = "2026-10-01",
            }),
        })).Require();
        Assert.True(preview["rows"]![0]!["incomplete"]!.GetValue<bool>());
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1),
            ["confirm_own_records"] = true, ["deduct_from_baseline"] = true,
        };
        var (committed, replayed) = await CommitTwiceAsync(client, payload);
        ContractSchema.Validate("$defs/Responses/CommitRunImport", committed, "import deducting from the baseline");
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        Assert.Equal(1, committed["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(1499, committed["baseline_completed_count"]!.GetValue<int>());
        Assert.Equal(1, replayed["baseline_deducted_count"]!.GetValue<int>());
        var dashboard = (await client.SendAsync("GetDashboardStats")).Require();
        Assert.Equal(1499, dashboard["baseline_completed_count"]!.GetValue<int>());
        Assert.Equal(1, dashboard["completed_count"]!.GetValue<int>());
        Assert.Equal(1500, dashboard["achievement_progress"]!.GetValue<int>());
    }

    // ------------------------------------------- entry mode from a real XLSX file ----
    // 合成的实际 XLSX 文件，经独立管道和临时库验证进入方式三态；不代表反馈者原表验收。

    [Theory]
    [InlineData("进入方式")]
    [InlineData("进本方式")]
    [InlineData("玩家自定义列")]
    public async Task ActualXlsxPreviewEditCommitRetryReadbackAndStatisticsPreserveUnknownEntry(string header)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(fixture.DataDirectory, "synthetic-entry-mode.xlsx");
        WriteWorkbook(path, header);
        var request = new JsonObject { ["source_kind"] = "XLSX", ["file_path"] = path, ["source_name"] = "合成进入方式工作簿" };
        if (header == "玩家自定义列") request["column_mapping"] = new JsonObject { [header] = "joined_in_progress" };
        var preview = (await client.SendAsync("PreviewRunImport", request.DeepClone().AsObject())).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "entry-mode actual XLSX preview");
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        Assert.Equal(new bool?[] { false, true, null }, rows.Select(row => row!["run"]!["joined_in_progress"]?.GetValue<bool>()));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var candidates = new JsonArray(rows.Select(row => row!["candidate"]!.DeepClone()).ToArray());
        candidates[0]!["joined_in_progress"] = "开局进入";
        candidates[1]!["joined_in_progress"] = "中途加入";
        candidates[2]!["joined_in_progress"] = " \t ";
        var checkedPreview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["rows"] = candidates, ["source_name"] = "合成进入方式工作簿",
        })).Require();
        Assert.All(checkedPreview["rows"]!.AsArray(), row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        var commitRequest = new JsonObject
        {
            ["preview_id"] = checkedPreview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1, 2, 3),
            ["confirm_own_records"] = true,
        };
        var (committed, _) = await CommitTwiceAsync(client, commitRequest);
        Assert.Equal(3, committed["imported_count"]!.GetValue<int>());
        var stored = (await client.SendAsync("QueryRuns")).Require();
        ContractSchema.Validate("$defs/Responses/QueryRuns", stored, "entry-mode stored three states");
        Assert.Equal(3, stored["page_info"]!["total"]!.GetValue<int>());
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"]?.GetValue<bool>() == false);
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"]?.GetValue<bool>() == true);
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"] is null);
        var stats = (await client.SendAsync("GetDashboardStats")).Require();
        Assert.Equal(3, stats["attempt_count"]!.GetValue<int>());
        Assert.Equal(1, stats["joined_in_progress_count"]!.GetValue<int>());
        Assert.Equal(0.5, stats["joined_in_progress_rate"]!.GetValue<double>());
        var repeated = (await client.SendAsync("PreviewRunImport", request.DeepClone().AsObject())).Require();
        Assert.All(repeated["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        foreach (var kind in new[] { "JSON", "CSV" })
        {
            var exportPath = Path.Combine(fixture.DataDirectory, "entry-export." + kind.ToLowerInvariant());
            (await client.SendAsync("Export" + (kind == "JSON" ? "Json" : "Csv"), new JsonObject { ["target_path"] = exportPath })).Require();
            var native = (await client.SendAsync("PreviewRunImport", new JsonObject
            {
                ["source_kind"] = kind, ["file_path"] = exportPath, ["time_zone"] = "+08:00",
            })).Require();
            Assert.All(native["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        }
    }

    private static void WriteWorkbook(string path, string header)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var output = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.Write(xml);
        }
        Add("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Add("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Add("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="合成记录" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Add("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement Row(int number, params string[] values) => new(ns + "row", new XAttribute("r", number),
            values.Select((value, column) => new XElement(ns + "c", new XAttribute("r", (char)('A' + column) + number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)))));
        var sheet = new XDocument(new XElement(ns + "worksheet", new XElement(ns + "sheetData",
            Row(1, "副本", "职业", "记录时间", header),
            Row(2, "水晶塔 古代人迷宫", "骑士", "2026-09-01", "正常"),
            Row(3, "水晶塔 古代人迷宫", "骑士", "2026-09-02", "中途"),
            Row(4, "水晶塔 古代人迷宫", "骑士", "2026-09-03", ""))));
        Add("xl/worksheets/sheet1.xml", sheet.ToString());
    }

    // ------------------------------------------------------------- paged previews ----

    private static JsonArray Rows(int count) => new(Enumerable.Range(1, count).Select(index => (JsonNode)new JsonObject
    {
        ["duty_name"] = "分页管道合成副本 " + index, ["job_name"] = "战士",
        ["result"] = "COMPLETED", ["source_recorded_at"] = "2022-09-28", ["reflection_text"] = "管道合成心得",
    }).ToArray());

    [Fact]
    public async Task All2090RowsTravelThroughRealPagesAndCommitWithOneIdempotentReceipt()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["rows"] = Rows(2090), ["page_size"] = 100,
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "paged import");
        var rowNumbers = new JsonArray();
        for (var page = 1; rowNumbers.Count < 2090; page++)
        {
            var result = page == 1 ? preview : (await client.SendAsync("PreviewRunImport", new JsonObject
            {
                ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = page,
            })).Require();
            ContractSchema.Validate("$defs/Responses/PreviewRunImport", result, "continued import");
            Assert.Equal(page, result["page_info"]!["page"]!.GetValue<int>());
            foreach (var row in result["rows"]!.AsArray()) rowNumbers.Add(row!["row_number"]!.DeepClone());
        }
        Assert.Equal(Enumerable.Range(1, 2090), rowNumbers.Select(node => node!.GetValue<int>()));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = rowNumbers,
            ["confirm_own_records"] = true,
        };
        var (committed, _) = await CommitTwiceAsync(client, payload);
        Assert.Equal(2090, committed["imported_count"]!.GetValue<int>());
        Assert.Equal(2090, (await client.SendAsync("QueryRuns")).Require()["page_info"]!["total"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("source_kind", "ROWS")]
    [InlineData("file_path", "ignored.xlsx")]
    [InlineData("text", "ignored")]
    [InlineData("time_zone", "+08:00")]
    [InlineData("source_name", "ignored")]
    [InlineData("page_size", 1)]
    [InlineData("column_mapping", null)]
    [InlineData("rows", null)]
    public async Task ContinuationCannotMixAnySourceOrEditingField(string field, object? value)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = Rows(2), ["page_size"] = 1,
        })).Require();
        var payload = new JsonObject { ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2 };
        payload[field] = value is int number ? JsonValue.Create(number) : JsonValue.Create(value as string);
        var invalid = await client.SendAsync("PreviewRunImport", payload);
        Assert.Equal(ErrorCodes.BadRequest, invalid.ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        Assert.True((await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2,
        })).Ok);
    }

    [Theory]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":null}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":0}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":101}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":1.5}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page\":1}")]
    [InlineData("{\"preview_id\":\"00000000-0000-0000-0000-000000000001\",\"page\":null}")]
    [InlineData("{\"preview_id\":\"00000000-0000-0000-0000-000000000001\",\"page\":0}")]
    public async Task InvalidPagingFieldsRemainTypedAndKeepConnectionUsable(string json)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("PreviewRunImport", JsonNode.Parse(json)!.AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task FailedCommitAcrossPagesRollsBackEverythingThenSameRequestCanRetry()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = Rows(102), ["page_size"] = 100,
        })).Require();
        (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2,
        })).Require();
        using (var trigger = fixture.Host.Database.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_last_page BEFORE INSERT ON mentor_runs WHEN NEW.duty_name='分页管道合成副本 102' BEGIN SELECT RAISE(ABORT,'synthetic paging failure'); END;";
            trigger.ExecuteNonQuery();
        }
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(),
            ["row_numbers"] = new JsonArray(Enumerable.Range(1, 102).Select(number => (JsonNode)JsonValue.Create(number)!).ToArray()),
            ["confirm_own_records"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        Assert.False((await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Ok);
        fixture.AssertTablesEmpty("mentor_runs", "run_import_metadata", "run_revisions", "run_reflections", "run_import_batches");
        using (var drop = fixture.Host.Database.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_last_page;";
            drop.ExecuteNonQuery();
        }
        var retried = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(102, retried["imported_count"]!.GetValue<int>());
        Assert.False(retried["replayed"]!.GetValue<bool>());
        Assert.True((await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require()["replayed"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------- real XLS files ----
    // 真实 XLS 文件经唯一命名管道和临时数据库覆盖预览、确认、重复和冲突。

    [Fact]
    public async Task BinaryFilePreviewCommitRetryDuplicateAndConflictPreserveLocalFacts()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(fixture.DataDirectory, "synthetic.xls");
        var id = Guid.NewGuid().ToString("D"); Write(path, id, "本地应保留的合成备注");
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLS", ["file_path"] = path, ["source_name"] = "合成二进制表格", ["time_zone"] = "+08:00",
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "binary XLS preview");
        Assert.Equal("XLS", preview["source_kind"]!.GetValue<string>());
        var row = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.Equal(2, row["row_number"]!.GetValue<int>()); Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var payload = new JsonObject { ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = false };
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        payload["confirm_own_records"] = true;
        var (committed, retry) = await CommitTwiceAsync(client, payload);
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        ContractSchema.Validate("$defs/Responses/CommitRunImport", retry, "binary XLS retry");
        var stored = Assert.Single((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray())!;
        ContractSchema.Validate("$defs/Run", stored, "binary XLS saved run");
        Assert.Equal(id, stored["run_id"]!.GetValue<string>());
        Assert.Equal("XLS", stored["import_metadata"]!["source_kind"]!.GetValue<string>());
        Assert.Equal("本地应保留的合成备注", stored["note"]!.GetValue<string>());
        Assert.Equal("2026-10-09T12:00:00.000Z", stored["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("合成导入心得", stored["reflection"]!["text"]!.GetValue<string>());

        async Task<JsonObject> Preview() => (await client.SendAsync("PreviewRunImport", new JsonObject
        { ["source_kind"] = "XLS", ["file_path"] = path, ["time_zone"] = "+08:00" })).Require();
        var duplicate = await Preview(); Assert.Equal("duplicate", duplicate["rows"]![0]!["status"]!.GetValue<string>());
        var duplicateCommit = (await client.SendAsync("CommitRunImport", new JsonObject
        { ["preview_id"] = duplicate["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = true })).Require();
        Assert.Equal(0, duplicateCommit["imported_count"]!.GetValue<int>()); Assert.Equal(1, duplicateCommit["duplicate_count"]!.GetValue<int>());
        Write(path, id, "外来冲突不能覆盖");
        var conflict = await Preview(); Assert.Equal("conflict", conflict["rows"]![0]!["status"]!.GetValue<string>());
        var conflictCommit = (await client.SendAsync("CommitRunImport", new JsonObject
        { ["preview_id"] = conflict["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = true })).Require();
        Assert.Equal(0, conflictCommit["imported_count"]!.GetValue<int>()); Assert.Equal(1, conflictCommit["conflict_count"]!.GetValue<int>());
        stored = Assert.Single((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray())!;
        Assert.Equal("本地应保留的合成备注", stored["note"]!.GetValue<string>());
        Assert.Equal(1, stored["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task DamagedRealFileReturnsActionableErrorAndPipeRemainsUsable()
    {
        await using var fixture = ServerFixture.Start(); await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(fixture.DataDirectory, "renamed.xls"); File.WriteAllText(path, "<html>not BIFF8</html>");
        var response = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "XLS", ["file_path"] = path });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode); Assert.Contains("不能只修改后缀", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    private static void Write(string path, string id, string note)
    {
        using var workbook = new HSSFWorkbook(); var sheet = workbook.CreateSheet("本人记录");
        Set(sheet.CreateRow(0), "run_id", "副本", "职业", "开始时间", "结束时间", "结果", "心得", "备注");
        Set(sheet.CreateRow(1), id, "合成导入副本", "骑士", "2026-10-09 20:00", "2026-10-09 20:20", "COMPLETED", "合成导入心得", note);
        using var file = File.Create(path); workbook.Write(file, leaveOpen: true);
    }
    private static void Set(IRow row, params string[] cells) { for (var i = 0; i < cells.Length; i++) row.CreateCell(i).SetCellValue(cells[i]); }

    // ------------------------------------------------------ duty identity by name ----
    // 真实隔离管道验证按名推断与显式编号；合成记录不代表反馈者原表验收。

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
        var (committed, _) = await CommitTwiceAsync(client, payload);
        Assert.Equal(3, committed["imported_count"]!.GetValue<int>());
        var query = (await client.SendAsync("QueryRuns")).Require();
        Assert.Equal(5, query["page_info"]!["total"]!.GetValue<int>());
        var ids = committed["run_ids"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(new int?[] { 944, 944, 69 }, ids.Select(id => fixture.Host.Runs.Get(id)!.ContentId));
        Assert.All(ids, id => Assert.Equal("阿修罗歼灭战", fixture.Host.Runs.Get(id)!.DutyName));
        Assert.Equal(historicalUnknown, fixture.Host.Runs.Get(historicalUnknown.RunId));
        Assert.Equal(historical69, fixture.Host.Runs.Get(historical69.RunId));
        var repeat = await Preview(source);
        Assert.All(repeat["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        var exportPath = Path.Combine(fixture.DataDirectory, "asura-identities.json");
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

    // ------------------------------------------------ import metadata on the wire ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentImportedRunProjectsBooleanAcceptedByContract(bool mentorConfirmed)
    {
        var wire = Wire.Run(ImportedRun(mentorConfirmed));

        Assert.Equal(mentorConfirmed, wire["import_metadata"]!["mentor_confirmed"]!.GetValue<bool>());
        ContractSchema.Validate("$defs/Run", wire, "Imported run with mentor confirmation");
    }

    [Fact]
    public void LegacyImportedMetadataWithoutMentorConfirmationRemainsAccepted()
    {
        var wire = Wire.Run(ImportedRun(mentorConfirmed: true));
        Assert.True(wire["import_metadata"]!.AsObject().Remove("mentor_confirmed"));

        ContractSchema.Validate("$defs/Run", wire, "Legacy imported run without mentor confirmation");
    }

    [Fact]
    public void MentorConfirmationRejectsAStringInsteadOfBoolean()
    {
        var metadata = Wire.Run(ImportedRun(mentorConfirmed: false))["import_metadata"]!.AsObject();
        metadata["mentor_confirmed"] = "false";

        Assert.False(ContractSchema.Evaluate("$defs/RunImportMetadata", metadata).IsValid);
    }

    private static MentorRun ImportedRun(bool mentorConfirmed)
    {
        var now = new DateTimeOffset(2026, 9, 4, 1, 2, 0, TimeSpan.Zero);
        return new MentorRun
        {
            RunId = Guid.NewGuid().ToString("D"),
            Revision = 1,
            Source = RunSource.Import,
            Result = RunResult.Completed,
            EnteredAtUtc = now.AddMinutes(-2),
            EndedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ImportMetadata = new RunImportMetadata("JSON", null, null, null, now,
                "synthetic-source-fingerprint", mentorConfirmed),
        };
    }
}
