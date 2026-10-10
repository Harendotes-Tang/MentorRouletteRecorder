using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunImportServiceTests
{
    private static string Id() => Guid.NewGuid().ToString("D");
    private static JsonObject History(string text = "合成测试心得") => new()
    {
        ["duty_name"] = "合成测试副本", ["job_name"] = "骑士", ["reflection_text"] = text,
        ["source_recorded_at"] = "2026-10-08 20:30:00", ["result"] = "UNKNOWN",
    };
    private static JsonObject Row(JsonObject preview, int index = 0) => preview["rows"]![index]!.AsObject();
    private static JsonObject Commit(RunImportService service, JsonObject preview, string? requestId = null) =>
        service.Commit(preview["preview_id"]!.GetValue<string>(),
            preview["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(), true, requestId ?? Id());
    private static long Count(TestDatabase fixture, string table)
    {
        using var command = fixture.Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }
    private static SettingsRepository Settings(TestDatabase fixture)
    {
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        return settings;
    }
    private static void SetBaseline(TestDatabase fixture, int baseline)
    {
        var settings = Settings(fixture);
        fixture.Database.RunInTransaction(tx => settings.UpdateAchievementSettings(settings.GetAchievementSettings(tx) with
        {
            BaselineCompletedCount = baseline, BaselineEffectiveAt = fixture.Clock.UtcNow, UpdatedAtUtc = fixture.Clock.UtcNow,
        }, tx));
    }
    private static JsonObject CompletedRun(int hour, bool contributesToGoal = true) =>
        Wire.Run(TestDatabase.Run(enteredAt: new DateTimeOffset(2026, 9, 4, hour, 0, 0, TimeSpan.Zero), contributesToGoal: contributesToGoal));

    [Fact]
    public void MissingOutcomeUsesUserDefaultCompletionButMissingTimesRemainPending()
    {
        using var fixture = new TestDatabase();
        var candidate = History(); candidate.Remove("result");
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(candidate), timeZone: "+08:00");
        Assert.Equal("COMPLETED", Row(preview)["run"]!["result"]!.GetValue<string>());
        Assert.True(Row(preview)["incomplete"]!.GetValue<bool>());
        Assert.Contains(Row(preview)["warnings"]!.AsArray(), value => value!.GetValue<string>().Contains("默认通关", StringComparison.Ordinal));
        Commit(service, preview);
        Assert.Equal(0, new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard().CompletedCount);
    }

    [Fact]
    public void EditedRawSourceTimeRecalculatesUtcAndExplicitEmptyReflectionClearsNestedText()
    {
        using var fixture = new TestDatabase();
        var input = History();
        input.Remove("reflection_text");
        input["reflection"] = new JsonObject { ["text"] = "旧合成心得", ["mood"] = "good" };
        input["import_metadata"] = new JsonObject
        {
            ["source_recorded_at"] = "2026-10-08 20:30:00", ["source_recorded_at_utc"] = "2026-10-08T12:30:00.000Z",
        };
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("JSON", rows: new JsonArray(input), timeZone: "+08:00");
        var edited = Row(preview)["candidate"]!.DeepClone().AsObject();
        edited["source_recorded_at"] = "2026-10-08 19:00:00";
        edited["reflection_text"] = "";
        var second = service.PreviewSource("JSON", rows: new JsonArray(edited), timeZone: "+08:00");
        Assert.Null(Row(second)["run"]!["reflection"]);
        Assert.Equal("2026-10-08T11:00:00.000Z", Row(second)["run"]!["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
        var runId = Commit(service, second)["run_ids"]![0]!.GetValue<string>();
        var run = new RunRepository(fixture.Database).Get(runId)!;
        Assert.Null(run.Reflection);
        Assert.Equal("2026-10-08 19:00:00", run.ImportMetadata!.SourceRecordedAt);

        var cleared = Row(preview)["candidate"]!.DeepClone().AsObject();
        cleared["source_recorded_at"] = null;
        var third = service.PreviewSource("JSON", rows: new JsonArray(cleared), timeZone: "+08:00");
        Assert.Null(Row(third)["run"]!["import_metadata"]!["source_recorded_at"]);
        Assert.Null(Row(third)["run"]!["import_metadata"]!["source_recorded_at_utc"]);
    }

    [Fact]
    public void SourceDateOnlyKeepsOriginalDateWithoutCreatingGameOrUtcMidnight()
    {
        using var fixture = new TestDatabase();
        var candidate = History(); candidate["source_recorded_at"] = "2026-10-08";
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(candidate), timeZone: "+08:00");
        Assert.Equal("new", Row(preview)["status"]!.GetValue<string>());
        var runId = Commit(service, preview)["run_ids"]![0]!.GetValue<string>();
        var run = new RunRepository(fixture.Database).Get(runId)!;
        Assert.Equal("2026-10-08", run.ImportMetadata!.SourceRecordedAt);
        Assert.Null(run.ImportMetadata.SourceRecordedAtUtc);
        Assert.Null(run.EnteredAtUtc);
        Assert.Null(run.EndedAtUtc);
        Assert.Equal(0, new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard().CompletedCount);
    }

    [Fact]
    public void NativeStoredSourceUtcSurvivesADifferentCurrentImportTimezoneUntilRawIsEdited()
    {
        using var fixture = new TestDatabase();
        var candidate = History();
        candidate["source_recorded_at"] = "2026-10-08 20:30:00";
        candidate["import_metadata"] = new JsonObject
        {
            ["source_recorded_at"] = "2026-10-08 20:30:00", ["source_recorded_at_utc"] = "2026-10-08T20:30:00.000Z",
        };
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("JSON", rows: new JsonArray(candidate), timeZone: "+08:00");
        Assert.Equal("2026-10-08T20:30:00.000Z", Row(preview)["run"]!["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
        var edited = Row(preview)["candidate"]!.DeepClone().AsObject();
        edited["source_recorded_at"] = "2026-10-08 19:30:00";
        var second = service.PreviewSource("JSON", rows: new JsonArray(edited), timeZone: "+08:00");
        Assert.Equal("2026-10-08T11:30:00.000Z", Row(second)["run"]!["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
    }

    [Fact]
    public void PreviewDoesNotWriteAndUnknownHistoryKeepsSourceTimeAndRealReflection()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(History()), timeZone: "+08:00", sourceName: "synthetic");
        Assert.Equal("new", Row(preview)["status"]!.GetValue<string>());
        Assert.True(Row(preview)["incomplete"]!.GetValue<bool>());
        foreach (var table in new[] { "mentor_runs", "run_reflections", "run_revisions", "run_import_metadata", "run_import_batches" })
            Assert.Equal(0, Count(fixture, table));
        var result = Commit(service, preview);
        var run = new RunRepository(fixture.Database).Get(result["run_ids"]![0]!.GetValue<string>())!;
        Assert.Null(run.EnteredAtUtc);
        Assert.Null(run.EndedAtUtc);
        Assert.Null(run.DurationMs);
        Assert.Equal(RunResult.Unknown, run.Result);
        Assert.Equal(ReflectionMood.Unknown, run.Reflection!.Mood);
        Assert.Equal("合成测试心得", run.Reflection.Text);
        Assert.Equal("2026-10-08 20:30:00", run.ImportMetadata!.SourceRecordedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 30, 0, TimeSpan.Zero), run.ImportMetadata.SourceRecordedAtUtc);
        Assert.Single(new RunRepository(fixture.Database).Query(new RunFilter { WithReflection = true }, null, 1, 50).Items);
        var stats = new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard();
        Assert.Equal(0, stats.AttemptCount);
        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal(1, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void ActualCompletionCanBeCorrectedWithoutInventingDurationAndKeepsProvenance()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(History()), timeZone: "+08:00");
        var runId = Commit(service, preview)["run_ids"]![0]!.GetValue<string>();
        var changes = new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.Result, RunFields.PendingReview, RunFields.EnteredAtUtc, RunFields.EndedAtUtc, RunFields.DurationMs },
            Result = RunResult.Completed, PendingReview = false,
            EnteredAtUtc = fixture.Clock.UtcNow.AddMinutes(-3), EndedAtUtc = fixture.Clock.UtcNow, DurationMs = null,
        };
        new RunMutationService(fixture.Database, Settings(fixture), fixture.Clock)
            .CorrectRun(new CorrectRunCommand(Id(), runId, 1, "补齐实际游戏事实", changes));
        var run = new RunRepository(fixture.Database).Get(runId)!;
        Assert.False(run.IsIncompleteImport);
        Assert.True(run.IsConfirmedMentor);
        Assert.Null(run.DurationMs);
        Assert.NotNull(run.ImportMetadata!.SourceRecordedAtUtc);
        var stats = new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard();
        Assert.Equal(1, stats.AttemptCount);
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(1, stats.AchievementProgress);
        Assert.Null(stats.AverageDurationMs);
        Assert.Equal(2, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void CompletionConfirmationWithoutActualEndpointsIsRejectedAtomically()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(History()), timeZone: "+08:00");
        var runId = Commit(service, preview)["run_ids"]![0]!.GetValue<string>();
        var changes = new RunChangeSet { Specified = new HashSet<string> { RunFields.Result }, Result = RunResult.Completed };
        Assert.Throws<CollectorException>(() => new RunMutationService(fixture.Database, Settings(fixture), fixture.Clock)
            .CorrectRun(new CorrectRunCommand(Id(), runId, 1, "确认完成", changes)));
        Assert.Equal(RunResult.Unknown, new RunRepository(fixture.Database).Get(runId)!.Result);
        Assert.Equal(1, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void CompleteNativeRecordWithExplicitUnknownDurationStillCounts()
    {
        using var fixture = new TestDatabase();
        var run = TestDatabase.Run(durationMs: null);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("JSON", rows: new JsonArray(Wire.Run(run)));
        Assert.False(Row(preview)["incomplete"]!.GetValue<bool>());
        Commit(service, preview);
        var stats = new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard();
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(1, stats.AchievementProgress);
        Assert.Null(stats.AverageDurationMs);
    }

    [Theory]
    [InlineData("CSV")]
    [InlineData("JSON")]
    public void NativeExportRoundTripDeduplicatesUuidDespiteFieldsAbsentFromCsv(string kind)
    {
        using var fixture = new TestDatabase();
        var local = TestDatabase.Run() with { Note = "仅 JSON 含此字段" };
        var repository = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => repository.Insert(local, tx));
        var path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "export." + kind.ToLowerInvariant());
        var exporter = new RunExporter(repository, fixture.Clock, fixture.Database);
        if (kind == "CSV") exporter.ExportCsv(path, null, false); else exporter.ExportJson(path, null, false);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource(kind, filePath: path);
        Assert.Equal("duplicate", Row(preview)["status"]!.GetValue<string>());
        Assert.Equal(1, Commit(service, preview)["duplicate_count"]!.GetValue<int>());
        Assert.Equal("仅 JSON 含此字段", repository.Get(local.RunId)!.Note);
        Assert.Equal(1, Count(fixture, "mentor_runs"));
    }

    [Fact]
    public void RecheckAtCommitPreservesLocalUuidConflictAndImportsOtherSelectedRows()
    {
        using var fixture = new TestDatabase();
        var incoming = TestDatabase.Run();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var second = Wire.Run(TestDatabase.Run());
        var preview = service.PreviewSource("JSON", rows: new JsonArray(Wire.Run(incoming), second));
        var repository = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => repository.Insert(incoming with { Note = "本地更新" }, tx));
        var result = Commit(service, preview);
        Assert.Equal(1, result["conflict_count"]!.GetValue<int>());
        Assert.Equal(1, result["imported_count"]!.GetValue<int>());
        Assert.Equal("本地更新", repository.Get(incoming.RunId)!.Note);
        Assert.Equal(2, Count(fixture, "mentor_runs"));
        Assert.Equal(1, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void ExactSourceRetryAndDurableReceiptSurviveRestartButSimilarRowsRemainSeparate()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = new JsonArray(History(), History());
        var preview = service.PreviewSource("SCREENSHOT", rows: rows, timeZone: "+08:00");
        var requestId = Id();
        var committed = Commit(service, preview, requestId);
        Assert.Equal(2, committed["imported_count"]!.GetValue<int>());
        fixture.Reopen();
        service = new RunImportService(fixture.Database, fixture.Clock);
        var replay = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 1, 2 }, true, requestId);
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(committed["run_ids"]!.ToJsonString(), replay["run_ids"]!.ToJsonString());
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 1 }, true, requestId)).Code);
        var next = service.PreviewSource("SCREENSHOT", rows: rows, timeZone: "+08:00");
        Assert.All(next["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        Assert.Equal(2, Count(fixture, "mentor_runs"));
        Assert.Equal(2, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void ImportRequestIdentityIsReservedAcrossExistingMutationsAndRetentionPruning()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var requestId = Id();
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(History()), timeZone: "+08:00");
        var runId = Commit(service, preview, requestId)["run_ids"]![0]!.GetValue<string>();
        var change = new RunChangeSet { Specified = new HashSet<string> { RunFields.Note }, Note = "不可写入" };
        var mutations = new RunMutationService(fixture.Database, Settings(fixture), fixture.Clock);
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            mutations.CorrectRun(new CorrectRunCommand(requestId, runId, 1, "测试编号复用", change))).Code);
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "DELETE FROM ipc_idempotency";
            command.ExecuteNonQuery();
        }
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            mutations.CorrectRun(new CorrectRunCommand(requestId, runId, 1, "测试编号复用", change))).Code);
        Assert.Null(new RunRepository(fixture.Database).Get(runId)!.Note);
        Assert.Equal(1, Count(fixture, "run_revisions"));
    }

    [Fact]
    public void MerelySimilarRecordRequiresSelectionAndIsNeverAutomaticallyMerged()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        Commit(service, service.PreviewSource("SCREENSHOT", rows: new JsonArray(History()), timeZone: "+08:00"));
        var next = service.PreviewSource("SCREENSHOT", rows: new JsonArray(History("另一条合成心得")), timeZone: "+08:00");
        Assert.Equal("possible_duplicate", Row(next)["status"]!.GetValue<string>());
        Assert.True(Row(next)["can_import"]!.GetValue<bool>());
        Assert.Equal(1, Commit(service, next)["imported_count"]!.GetValue<int>());
        Assert.Equal(2, Count(fixture, "mentor_runs"));
    }

    [Fact]
    public void AnyInvalidSelectedRowRefusesTheWholeBatch()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var bad = History(); bad["duration_ms"] = "12:30";
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(History(), bad), timeZone: "+08:00");
        Assert.Equal("invalid", Row(preview, 1)["status"]!.GetValue<string>());
        Assert.Throws<CollectorException>(() => Commit(service, preview));
        Assert.Equal(0, Count(fixture, "mentor_runs"));
        Assert.Equal(0, Count(fixture, "run_import_batches"));
    }

    [Fact]
    public void DatabaseFailureOnSecondRowRollsBackRunsReflectionsAuditMetadataAndReceipt()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = History(); first["duty_name"] = "first";
        var second = History(); second["duty_name"] = "second";
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(first, second), timeZone: "+08:00");
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_second_import BEFORE INSERT ON mentor_runs WHEN NEW.duty_name='second' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => Commit(service, preview));
        foreach (var table in new[] { "mentor_runs", "run_reflections", "run_revisions", "run_import_metadata", "run_import_batches" })
            Assert.Equal(0, Count(fixture, table));
    }

    [Fact]
    public void BackupReadsOnlyPersonalHistoryAndDoesNotChangeSourceOrLocalSettings()
    {
        using var source = new TestDatabase();
        Settings(source);
        using (var command = source.Database.CreateCommand())
        {
            command.CommandText = "UPDATE achievement_settings SET baseline_completed_count = 300";
            command.ExecuteNonQuery();
        }
        source.Database.RunInTransaction(tx => new RunRepository(source.Database).Insert(TestDatabase.Run(), tx));
        var path = Path.Combine(Path.GetDirectoryName(source.Path)!, "backup.db");
        new BackupService(source.Database, source.Clock).CreateBackup(path);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        using var target = new TestDatabase();
        var settings = Settings(target);
        var baseline = settings.GetAchievementSettings();
        var service = new RunImportService(target.Database, target.Clock);
        Commit(service, service.PreviewSource("BACKUP", filePath: path));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(baseline, settings.GetAchievementSettings());
        Assert.Equal(0, Count(target, "capture_sessions"));
        Assert.Equal(1, Count(target, "mentor_runs"));
        var imported = new RunRepository(target.Database).Query(null, null, 1, 50).Items.Single();
        Assert.Null(imported.CaptureSessionId);
        Assert.Null(imported.ProtocolProfileId);
        Assert.Null(imported.GameBuild);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"warnings\":[\"unknown headers\"]}", null)]
    [InlineData("{\"reflection\":{\"mood\":\"good\"}}", null)]
    [InlineData("{\"entered_at_utc\":\"2026-10-08\",\"ended_at_utc\":\"2026-10-09\"}", "+08:00")]
    [InlineData("{\"source_recorded_at\":\"2026-10-08 20:30\"}", null)]
    [InlineData("{\"source_recorded_at\":\"2026-10-08 20:30\"}", "not-a-zone")]
    public void EmptyOrAmbiguousCandidateIsInvalidWithoutInventedFacts(string json, string? zone)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(JsonNode.Parse(json)), timeZone: zone);
        Assert.Equal("invalid", Row(preview)["status"]!.GetValue<string>());
        Assert.False(Row(preview)["can_import"]!.GetValue<bool>());
        Assert.Equal(0, Count(fixture, "mentor_runs"));
    }

    [Fact]
    public void OversizedChinesePreviewReturnsTypedBatchErrorBeforeCachingOrWriting()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = new JsonArray(Enumerable.Range(0, 700).Select(_ => (JsonNode)History(new string('中', 2000))).ToArray());
        var error = Assert.Throws<CollectorException>(() => service.PreviewSource("SCREENSHOT", rows: rows, timeZone: "+08:00"));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Contains("分", error.Message);
        Assert.Equal(0, Count(fixture, "mentor_runs"));
        Assert.Equal(0, Count(fixture, "run_import_batches"));
    }

    /// <summary>
    /// The baseline is what the game showed before this software was installed, so history imported
    /// from a spreadsheet or a screenshot of that time is already inside it. When the user says so, the
    /// commit deducts exactly the imported completions the progress would otherwise add a second time:
    /// a completion excluded from the goal and an incomplete record are imported but not deducted.
    /// </summary>
    [Fact]
    public void DeductingFromBaselineRemovesOnlyTheImportedCompletionsThatCountAndRecordsWhy()
    {
        using var fixture = new TestDatabase();
        SetBaseline(fixture, 1500);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = new JsonArray(CompletedRun(1), CompletedRun(2), CompletedRun(3, contributesToGoal: false), History());
        var preview = service.PreviewSource("JSON", rows: rows, timeZone: "+08:00");
        Assert.All(preview["rows"]!.AsArray(), row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        var requestId = Id();
        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 1, 2, 3, 4 }, true, requestId, deductFromBaseline: true);
        Assert.Equal(4, committed["imported_count"]!.GetValue<int>());
        Assert.Equal(2, committed["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(1498, committed["baseline_completed_count"]!.GetValue<int>());
        var settings = Settings(fixture);
        var stats = new StatisticsRepository(fixture.Database, settings).GetDashboard();
        Assert.Equal(1498, stats.BaselineCompletedCount);
        Assert.Equal(3, stats.CompletedCount);
        Assert.Equal(1500, stats.AchievementProgress);
        var audit = Assert.Single(settings.ReadBaselineAudit());
        Assert.Equal(requestId, audit.RequestId);
        Assert.Equal(1498, audit.BaselineCompletedCount);
        Assert.Contains("导入 2 条", audit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeductionStopsAtZeroAndTheChoiceBelongsToTheRetryIdentity()
    {
        using var fixture = new TestDatabase();
        SetBaseline(fixture, 1);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("JSON", rows: new JsonArray(CompletedRun(1), CompletedRun(2)));
        var previewId = preview["preview_id"]!.GetValue<string>();
        var requestId = Id();
        var committed = service.Commit(previewId, new[] { 1, 2 }, true, requestId, deductFromBaseline: true);
        Assert.Equal(1, committed["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(0, committed["baseline_completed_count"]!.GetValue<int>());
        Assert.Contains("扣到 0", Assert.Single(Settings(fixture).ReadBaselineAudit()).Reason, StringComparison.Ordinal);
        var replay = service.Commit(previewId, new[] { 1, 2 }, true, requestId, deductFromBaseline: true);
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(1, replay["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            service.Commit(previewId, new[] { 1, 2 }, true, requestId)).Code);
        Assert.Equal(0, Settings(fixture).GetAchievementSettings().BaselineCompletedCount);
        Assert.Equal(2, new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard().AchievementProgress);
    }

    [Fact]
    public void WithoutTheChoiceImportsAddOnTopOfTheBaselineAndDuplicatesNeverDeduct()
    {
        using var fixture = new TestDatabase();
        SetBaseline(fixture, 10);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = new JsonArray(CompletedRun(1));
        var first = Commit(service, service.PreviewSource("JSON", rows: rows));
        Assert.Equal(0, first["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(10, first["baseline_completed_count"]!.GetValue<int>());
        Assert.Equal(11, new StatisticsRepository(fixture.Database, Settings(fixture)).GetDashboard().AchievementProgress);
        var again = service.PreviewSource("JSON", rows: rows);
        Assert.Equal("duplicate", Row(again)["status"]!.GetValue<string>());
        var second = service.Commit(again["preview_id"]!.GetValue<string>(), new[] { 1 }, true, Id(), deductFromBaseline: true);
        Assert.Equal(0, second["imported_count"]!.GetValue<int>());
        Assert.Equal(1, second["duplicate_count"]!.GetValue<int>());
        Assert.Equal(0, second["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(10, Settings(fixture).GetAchievementSettings().BaselineCompletedCount);
        Assert.Empty(Settings(fixture).ReadBaselineAudit());
    }
}
