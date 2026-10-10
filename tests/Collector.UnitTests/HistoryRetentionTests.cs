using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class HistoryRetentionTests
{
    private static string Id() => Guid.NewGuid().ToString("D");

    private sealed class Fixture : IDisposable
    {
        public readonly TestDatabase Db = new();
        public readonly RunMutationService Mutations;
        public readonly HistoryRetentionService Retention;
        public readonly RunRepository Runs;
        public Fixture()
        {
            var settings = new SettingsRepository(Db.Database, Db.Clock); settings.EnsureDefaults();
            Mutations = new RunMutationService(Db.Database, settings, Db.Clock);
            Retention = new HistoryRetentionService(Db.Database, Db.Clock); Runs = new RunRepository(Db.Database);
        }
        public CreateManualRunCommand NewCommand() => new()
        {
            RequestId = Id(), Reason = "补录", Result = RunResult.Completed, JobId = 19,
            EnteredAtUtc = Db.Clock.UtcNow.AddHours(-1), EndedAtUtc = Db.Clock.UtcNow.AddMinutes(-30),
            Note = "purge-body-secret",
        };
        public MentorRun Create() => Mutations.CreateManualRun(NewCommand()).Run!;
        public JsonObject Batch(string action, params MentorRun[] runs) => Mutations.BatchMutateRuns(new(
            Id(), action, runs.Select(run => new BatchRunTarget(run.RunId, run.Revision)).ToArray(), "批量操作"));
        public MentorRun Delete(MentorRun run)
        { Batch("soft_delete", run); return Runs.Get(run.RunId)!; }
        public long Count(string table, string id)
        {
            using var cmd = Db.Database.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE run_id=$id;";
            cmd.Parameters.AddWithValue("$id", id); return (long)cmd.ExecuteScalar()!;
        }
        public void Dispose() { Retention.Dispose(); Db.Dispose(); }
    }

    [Theory]
    [InlineData("soft_delete")]
    [InlineData("restore")]
    [InlineData("purge")]
    public void Batch_StaleRevisionRollsBackEveryTarget(string action)
    {
        using var f = new Fixture(); var first = f.Create(); var second = f.Create();
        if (action != "soft_delete") { first = f.Delete(first); second = f.Delete(second); }
        var command = new BatchMutateRunsCommand(Id(), action,
            new[] { new BatchRunTarget(first.RunId, first.Revision), new BatchRunTarget(second.RunId, second.Revision + 1) }, "批量操作");
        var error = Assert.Throws<CollectorException>(() => f.Mutations.BatchMutateRuns(command));
        Assert.Equal(ErrorCodes.RevisionConflict, error.Code);
        Assert.Equal(first, f.Runs.Get(first.RunId)); Assert.Equal(second, f.Runs.Get(second.RunId));
        Assert.Empty(f.Retention.PendingImageCleanup());
    }

    [Fact]
    public void Batch_PurgeLiveTargetRejectsTheWholeSelection()
    {
        using var f = new Fixture(); var deleted = f.Delete(f.Create()); var live = f.Create();
        Assert.Equal(ErrorCodes.NotDeleted, Assert.Throws<CollectorException>(() => f.Batch("purge", deleted, live)).Code);
        Assert.NotNull(f.Runs.Get(deleted.RunId)); Assert.NotNull(f.Runs.Get(live.RunId));
        Assert.Empty(f.Retention.PendingImageCleanup());
    }

    [Fact]
    public void Batch_ReplayIsAtomicAndChangedPayloadIsRejected()
    {
        using var f = new Fixture(); var first = f.Create(); var second = f.Create();
        var command = new BatchMutateRunsCommand(Id(), "soft_delete",
            new[] { new BatchRunTarget(first.RunId, 1), new BatchRunTarget(second.RunId, 1) }, "删除");
        Assert.Equal(2, f.Mutations.BatchMutateRuns(command)["changed_count"]!.GetValue<int>());
        Assert.True(f.Mutations.BatchMutateRuns(command)["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal(2, f.Runs.Get(first.RunId)!.Revision); Assert.Equal(2, f.Runs.Get(second.RunId)!.Revision);
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            f.Mutations.BatchMutateRuns(command with { Action = "restore" })).Code);
    }

    [Fact]
    public void RestoreCancelsDeadlineAndDeleteAgainRestartsIt()
    {
        using var f = new Fixture(); var run = f.Delete(f.Create());
        Assert.Equal(f.Db.Clock.UtcNow, run.DeletedAtUtc);
        f.Db.Clock.UtcNow += TimeSpan.FromDays(29); f.Batch("restore", run);
        run = f.Runs.Get(run.RunId)!; Assert.Null(run.DeletedAtUtc);
        f.Db.Clock.UtcNow += TimeSpan.FromDays(2); Assert.Equal(0, f.Retention.CheckExpired(force: true));
        run = f.Delete(run); Assert.Equal(f.Db.Clock.UtcNow, run.DeletedAtUtc);
        f.Db.Clock.UtcNow += TimeSpan.FromDays(29); Assert.Equal(0, f.Retention.CheckExpired(force: true));
        f.Db.Clock.UtcNow += TimeSpan.FromDays(1); Assert.Equal(1, f.Retention.CheckExpired(force: true));
        Assert.Null(f.Runs.Get(run.RunId)); Assert.Contains(run.RunId, f.Retention.PendingImageCleanup());
    }

    [Fact]
    public void NeverRetentionKeepsDeletedRowsAndCustomPeriodUsesDeletionDate()
    {
        using var f = new Fixture(); var run = f.Delete(f.Create());
        Assert.Equal(30, f.Retention.GetRetentionDays()); f.Retention.UpdateRetentionDays(0);
        f.Db.Clock.UtcNow += TimeSpan.FromDays(100); Assert.Equal(0, f.Retention.CheckExpired(force: true));
        Assert.NotNull(f.Runs.Get(run.RunId)); f.Retention.UpdateRetentionDays(101);
        Assert.Equal(0, f.Retention.CheckExpired(force: true));
        f.Db.Clock.UtcNow += TimeSpan.FromDays(1); Assert.Equal(1, f.Retention.CheckExpired(force: true));
        Assert.Throws<CollectorException>(() => f.Retention.UpdateRetentionDays(36501));
    }

    [Fact]
    public void PurgeRemovesEveryBodyAndKeepsNoBodyReplayProtectionAndQueue()
    {
        using var f = new Fixture(); var create = f.NewCommand(); var run = f.Mutations.CreateManualRun(create).Run!; var other = f.Create();
        var reflectionRequest = new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, "purge-reflection-secret");
        var reflectionWrites = new RunReflectionService(f.Db.Database, f.Db.Clock); reflectionWrites.Set(reflectionRequest);
        var importRequest = Id(); var preview = Id();
        f.Db.Database.RunInTransaction(tx =>
        {
            using var cmd = f.Db.Database.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO run_import_metadata(run_id,source_kind,source_fingerprint,imported_at_utc) VALUES($id,'JSON',$fingerprint,$now); " +
                "INSERT INTO run_events(event_id,run_id,sequence,occurred_at_utc,monotonic_offset_ms,event_type,detail_json) " +
                "VALUES($event,$id,0,$now,0,'TEST','purge-event-secret'); " +
                "INSERT INTO run_import_batches VALUES($preview,$request,'selection-hash',$json,$now);";
            cmd.Parameters.AddWithValue("$id", run.RunId); cmd.Parameters.AddWithValue("$fingerprint", "source-hash");
            cmd.Parameters.AddWithValue("$now", UtcTimestamp.ToText(f.Db.Clock.UtcNow)); cmd.Parameters.AddWithValue("$event", Id());
            cmd.Parameters.AddWithValue("$preview", preview); cmd.Parameters.AddWithValue("$request", importRequest);
            cmd.Parameters.AddWithValue("$json", new JsonObject { ["run_ids"] = new JsonArray(run.RunId), ["body"] = "purge-import-secret" }.ToJsonString());
            cmd.ExecuteNonQuery();
        });
        run = f.Delete(run); f.Batch("purge", run);
        foreach (var table in new[] { "mentor_runs", "run_events", "run_revisions", "run_reflections", "run_import_metadata", "run_purge_authorizations" })
            Assert.Equal(0, f.Count(table, run.RunId));
        Assert.NotNull(f.Runs.Get(other.RunId));
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() => f.Mutations.CreateManualRun(create)).Code);
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() => reflectionWrites.Set(reflectionRequest)).Code);
        var idem = new IdempotencyRepository(f.Db.Database, f.Db.Clock);
        Assert.True(idem.IsPurgedRun(run.RunId)); Assert.True(idem.IsPurgedRun(Id(), "source-hash"));
        Assert.Throws<CollectorException>(() => idem.ThrowIfPurgedRequest(importRequest, previewId: preview));
        using var body = f.Db.Database.CreateCommand();
        body.CommandText = "SELECT COUNT(*) FROM ipc_idempotency WHERE response_json LIKE '%" + run.RunId + "%' AND response_json LIKE '%secret%';";
        Assert.Equal(0L, body.ExecuteScalar());
        f.Db.Reopen();
        using var reopened = new HistoryRetentionService(f.Db.Database, f.Db.Clock);
        Assert.Contains(run.RunId, reopened.PendingImageCleanup());
        Assert.Equal(1, reopened.AcknowledgeImageCleanup(new[] { run.RunId }));
        Assert.Equal(0, reopened.AcknowledgeImageCleanup(new[] { run.RunId }));
        Assert.Empty(reopened.PendingImageCleanup());
    }

    [Fact]
    public void OrdinaryRevisionDeletionAndPurgedStableIdReinsertRemainForbidden()
    {
        using var f = new Fixture(); var run = f.Create();
        using var cmd = f.Db.Database.CreateCommand(); cmd.CommandText = "DELETE FROM run_revisions WHERE run_id=$id;";
        cmd.Parameters.AddWithValue("$id", run.RunId); Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        f.Batch("purge", f.Delete(run));
        Assert.Throws<SqliteException>(() => f.Db.Database.RunInTransaction(tx => f.Runs.Insert(run, tx)));
        Assert.Equal(0, f.Count("run_purge_authorizations", run.RunId));
    }

    [Fact]
    public void PurgeDatabaseFailureRollsBackBodiesReceiptsTombstonesAndAttachments()
    {
        using var f = new Fixture(); var first = f.Delete(f.Create()); var second = f.Delete(f.Create());
        var lastId = new[] { first.RunId, second.RunId }.OrderBy(id => id, StringComparer.Ordinal).Last();
        using (var trigger = f.Db.Database.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_second_purge BEFORE DELETE ON mentor_runs WHEN OLD.run_id='" + lastId +
                "' BEGIN SELECT RAISE(ABORT,'synthetic purge failure'); END;"; trigger.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => f.Batch("purge", first, second));
        Assert.Equal(first, f.Runs.Get(first.RunId)); Assert.Equal(second, f.Runs.Get(second.RunId));
        Assert.Empty(f.Retention.PendingImageCleanup());
        Assert.Equal(0, f.Count("purged_run_tombstones", first.RunId)); Assert.Equal(0, f.Count("purged_run_tombstones", second.RunId));
        Assert.Equal(0, f.Count("run_purge_authorizations", first.RunId));
    }

    [Fact]
    public void RealImportPurgeRejectsReceiptReplayAndReimportOfTheSameSource()
    {
        using var f = new Fixture(); var imports = new RunImportService(f.Db.Database, f.Db.Clock);
        var incoming = Wire.Run(TestDatabase.Run()); incoming["note"] = "purge-import-secret";
        var preview = imports.PreviewSource("JSON", rows: new JsonArray(incoming.DeepClone()));
        var previewId = preview["preview_id"]!.GetValue<string>(); var requestId = Id();
        var committed = imports.Commit(previewId, new[] { 1 }, true, requestId);
        var runId = Assert.Single(committed["run_ids"]!.AsArray())!.GetValue<string>();
        f.Batch("purge", f.Delete(f.Runs.Get(runId)!));
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() => imports.Commit(previewId, new[] { 1 }, true, requestId)).Code);
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() => imports.Commit(previewId, new[] { 1 }, true, Id())).Code);
        var next = imports.PreviewSource("JSON", rows: new JsonArray(incoming.DeepClone()));
        Assert.Equal("conflict", next["rows"]![0]!["status"]!.GetValue<string>());
        Assert.False(next["rows"]![0]!["can_import"]!.GetValue<bool>());
        Assert.Null(f.Runs.Get(runId));
        using var receipts = f.Db.Database.CreateCommand(); receipts.CommandText = "SELECT COUNT(*) FROM run_import_batches WHERE request_id=$request;";
        receipts.Parameters.AddWithValue("$request", requestId); Assert.Equal(0L, receipts.ExecuteScalar());
    }

    [Fact]
    public void MixedImportReceiptIsTombstonedWhileTheUnpurgedRecordAndExportStayComplete()
    {
        using var f = new Fixture(); var imports = new RunImportService(f.Db.Database, f.Db.Clock);
        var first = Wire.Run(TestDatabase.Run()); first["reflection_text"] = "erased-diary"; first["reflection_mood"] = "good";
        var second = Wire.Run(TestDatabase.Run()); second["reflection_text"] = "retained-diary"; second["reflection_mood"] = "good";
        var preview = imports.PreviewSource("JSON", rows: new JsonArray(first, second)); var request = Id();
        var previewId = preview["preview_id"]!.GetValue<string>();
        var receipt = imports.Commit(previewId, new[] { 1, 2 }, true, request);
        var firstId = receipt["run_ids"]![0]!.GetValue<string>(); var secondId = receipt["run_ids"]![1]!.GetValue<string>();
        f.Batch("purge", f.Delete(f.Runs.Get(firstId)!));
        Assert.Null(f.Runs.Get(firstId)); var retained = f.Runs.Get(secondId)!;
        Assert.Equal("retained-diary", retained.Reflection!.Text); Assert.Equal(1, f.Count("run_revisions", secondId));
        Assert.Equal(1, f.Count("run_import_metadata", secondId));
        var error = Assert.Throws<CollectorException>(() => imports.Commit(previewId, new[] { 1, 2 }, true, request));
        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
        Assert.Equal("RECORD_PURGED", error.Details!["reason"]);
        var target = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "retained.json");
        var exporter = new RunExporter(f.Runs, f.Db.Clock, f.Db.Database);
        Assert.Equal(1, exporter.ExportJson(target, new RunFilter { RunIds = new[] { secondId } }, false).RowCount);
        Assert.Contains("retained-diary", File.ReadAllText(target)); Assert.DoesNotContain("erased-diary", File.ReadAllText(target));
    }

    [Fact]
    public void CalendarCompanionsRequireRealDaysHistoryDateAndMatchingUtcBounds()
    {
        var filter = new JsonObject { ["date_field"] = "history_date", ["history_from_day"] = "2024-09-24", ["from_utc"] = "2024-09-24T04:00:00.000Z" };
        Assert.Equal("2024-09-24", RequestParsers.Filter(new PayloadReader(filter))!.HistoryFromDay);
        foreach (var badDay in new[] { "2024-02-30", "2024-9-24", "not-a-date" })
        {
            var invalid = filter.DeepClone().AsObject(); invalid["history_from_day"] = badDay;
            Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(invalid)));
        }
        var wrongField = filter.DeepClone().AsObject(); wrongField["date_field"] = "entered_at_utc";
        Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(wrongField)));
        var missingBound = filter.DeepClone().AsObject(); missingBound.Remove("from_utc");
        Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(missingBound)));
    }

    [Fact]
    public void PurgeKeepsReplayTombstoneAfterTheResponseWasAlreadyPruned()
    {
        using var f = new Fixture(); var command = f.NewCommand(); var run = f.Mutations.CreateManualRun(command).Run!;
        f.Db.Clock.UtcNow += TimeSpan.FromDays(2); _ = f.Create();
        using (var cache = f.Db.Database.CreateCommand())
        {
            cache.CommandText = "SELECT COUNT(*) FROM ipc_idempotency WHERE request_id=$id;";
            cache.Parameters.AddWithValue("$id", command.RequestId); Assert.Equal(0L, cache.ExecuteScalar());
        }
        f.Batch("purge", f.Delete(run));
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() => f.Mutations.CreateManualRun(command)).Code);
        Assert.Null(f.Runs.Get(run.RunId));
    }

    [Fact]
    public void ExactIdFilterSupportsTheDeclaredBoundAndRejectsMalformedIds()
    {
        using var f = new Fixture(); var run = f.Create();
        var ids = new[] { run.RunId }.Concat(Enumerable.Range(0, 1999).Select(_ => Id())).ToArray();
        Assert.Equal(run.RunId, Assert.Single(f.Runs.Query(new RunFilter { RunIds = ids }, null, 1, 50).Items).RunId);
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["run_id"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["date_field"] = "history_date" }));
        Assert.Equal(2000, parsed!.RunIds.Count); Assert.Equal(RunDateField.HistoryDate, parsed.DateField);
        foreach (var invalid in new[] { new JsonArray(run.RunId, run.RunId.ToUpperInvariant()), new JsonArray("invalid"), new JsonArray(ids.Concat(new[] { Id() }).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) })
            Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(new JsonObject { ["run_id"] = invalid })));
        Assert.Throws<CollectorException>(() => RequestParsers.Filter(new PayloadReader(new JsonObject { ["run_id"] = null })));
    }

    [Fact]
    public void CleanupAttemptsRotatePastTwoThousandPermanentFailures()
    {
        using var f = new Fixture(); var ids = Enumerable.Range(0, 2001).Select(_ => Id()).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        f.Db.Database.RunInTransaction(tx =>
        {
            using var seed = f.Db.Database.CreateCommand(); seed.Transaction = tx;
            seed.CommandText = "INSERT INTO pending_image_cleanup(run_id,queued_at_utc) VALUES($id,$now);";
            seed.Parameters.AddWithValue("$id", ""); seed.Parameters.AddWithValue("$now", UtcTimestamp.ToText(f.Db.Clock.UtcNow));
            foreach (var id in ids) { seed.Parameters["$id"].Value = id; seed.ExecuteNonQuery(); }
        });
        var blocked = f.Retention.PendingImageCleanup(); Assert.Equal(2000, blocked.Count); Assert.DoesNotContain(ids[^1], blocked);
        f.Db.Reopen(); using var restarted = new HistoryRetentionService(f.Db.Database, f.Db.Clock);
        Assert.Contains(ids[^1], restarted.PendingImageCleanup());
        Assert.Equal(1, restarted.AcknowledgeImageCleanup(new[] { ids[^1] }));
        Assert.Equal(2000, restarted.PendingImageCleanup().Count);
    }

    [Fact]
    public void ExactIdExportWritesOnlySelectionAndMissingIdDoesNotWriteAFile()
    {
        using var f = new Fixture(); var first = f.Create(); var other = f.Create(); var second = f.Create();
        var exporter = new RunExporter(f.Runs, f.Db.Clock, f.Db.Database);
        var path = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "selected.json");
        var outcome = exporter.ExportJson(path, new RunFilter { RunIds = new[] { second.RunId, first.RunId } }, false);
        Assert.Equal(2, outcome.RowCount);
        var ids = JsonNode.Parse(File.ReadAllText(path))!.AsArray().Select(node => node!["run_id"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain(other.RunId, ids); Assert.Contains(first.RunId, ids); Assert.Contains(second.RunId, ids);
        var missing = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "missing.json");
        Assert.Throws<CollectorException>(() => exporter.ExportJson(missing, new RunFilter { RunIds = new[] { first.RunId, Id() } }, false));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void MigrationStartsOldDeletedRowsAtTheUpgradeClock()
    {
        var path = Path.Combine(Path.GetTempPath(), "MentorRecorder.RetentionMigration", Id(), "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clock = new TestClock(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using (var old = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                old.Open();
                foreach (var script in MigrationRunner.Scripts.Where(script => script.Version < 10))
                {
                    using var cmd = old.CreateCommand(); cmd.CommandText = script.Sql; cmd.ExecuteNonQuery();
                    cmd.CommandText = "INSERT INTO schema_migrations VALUES($version,$name,$sum,$now);";
                    cmd.Parameters.AddWithValue("$version", script.Version); cmd.Parameters.AddWithValue("$name", script.Name);
                    cmd.Parameters.AddWithValue("$sum", script.Checksum); cmd.Parameters.AddWithValue("$now", UtcTimestamp.ToText(clock.UtcNow)); cmd.ExecuteNonQuery();
                }
                using var seed = old.CreateCommand();
                seed.CommandText = "INSERT INTO mentor_runs(run_id,revision,result,source,soft_deleted,created_at_utc,updated_at_utc) " +
                    "VALUES($id,1,'COMPLETED','MANUAL',1,'2000-01-01T00:00:00.000Z','2000-01-01T00:00:00.000Z');";
                seed.Parameters.AddWithValue("$id", Id()); seed.ExecuteNonQuery();
            }
            using var migrated = SqliteDatabase.Open(path, clock);
            var snapshotPath = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "backups"), "history_retention_upgrade_*.db"));
            using (var snapshot = new SqliteConnection("Data Source=" + snapshotPath + ";Mode=ReadOnly;Pooling=False"))
            {
                snapshot.Open(); using var snapshotCheck = snapshot.CreateCommand();
                snapshotCheck.CommandText = "SELECT MAX(version) FROM schema_migrations;"; Assert.Equal(9L, snapshotCheck.ExecuteScalar());
                snapshotCheck.CommandText = "PRAGMA integrity_check;"; Assert.Equal("ok", snapshotCheck.ExecuteScalar());
                snapshotCheck.CommandText = "SELECT updated_at_utc FROM mentor_runs;"; Assert.Equal("2000-01-01T00:00:00.000Z", snapshotCheck.ExecuteScalar());
            }
            using var check = migrated.CreateCommand(); check.CommandText = "SELECT deleted_at_utc FROM mentor_runs;";
            Assert.Equal(UtcTimestamp.ToText(clock.UtcNow), check.ExecuteScalar());
            using var retention = new HistoryRetentionService(migrated, clock);
            Assert.Equal(0, retention.CheckExpired(force: true));
            clock.UtcNow += TimeSpan.FromDays(30); Assert.Equal(1, retention.CheckExpired(force: true));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
