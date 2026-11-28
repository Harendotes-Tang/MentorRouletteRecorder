using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
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
        public readonly LiveEventBus LiveEvents;
        public Fixture()
        {
            LiveEvents = new LiveEventBus(Db.Clock);
            Mutations = new RunMutationService(Db.Database, Db.Settings(), Db.Clock, onPurged: LiveEvents.ForgetRuns);
            Retention = new HistoryRetentionService(Db.Database, Db.Clock, LiveEvents.ForgetRuns); Runs = new RunRepository(Db.Database);
        }
        public CreateManualRunCommand NewCommand() => new()
        {
            RequestId = Id(), Reason = "补录", Result = RunResult.Completed, JobId = 19,
            EnteredAtUtc = Db.Clock.UtcNow.AddHours(-1), EndedAtUtc = Db.Clock.UtcNow.AddMinutes(-30),
            Note = "purge-body-secret",
        };
        public MentorRun Create() => Mutations.CreateManualRun(NewCommand()).Run!;
        public BatchRunMutationOutcome Batch(string action, params MentorRun[] runs) => Mutations.BatchMutateRuns(new(
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
        Assert.Equal(2, f.Mutations.BatchMutateRuns(command).ChangedCount);
        Assert.True(f.Mutations.BatchMutateRuns(command).IdempotentReplay);
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
    public void PurgingAnIdMentionedInAnotherRunsNotePreservesItsReplay()
    {
        using var f = new Fixture();
        var removed = f.Create();
        var command = f.NewCommand() with { Note = removed.RunId };
        var other = f.Mutations.CreateManualRun(command).Run!;

        f.Batch("purge", f.Delete(removed));

        var replay = f.Mutations.CreateManualRun(command);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(other.RunId, replay.Run!.RunId);
        Assert.Equal(removed.RunId, replay.Run.Note);
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
    public async Task PurgeDatabaseFailureRollsBackBodiesReceiptsTombstonesAndAttachments()
    {
        using var f = new Fixture(); var first = f.Delete(f.Create()); var second = f.Delete(f.Create());
        f.LiveEvents.PublishRun(LiveEventKind.RunUpdated, first);
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
        f.LiveEvents.PublishRun(LiveEventKind.RunUpdated, second);
        using var subscriber = f.LiveEvents.Subscribe(Id());
        Assert.Equal(2, f.LiveEvents.ReplayCount);
        foreach (var expected in new[] { first.RunId, second.RunId })
            Assert.Equal(expected, (await subscriber.ReadAsync(TimeSpan.FromSeconds(2), CancellationToken.None))!["run"]!["run_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeStagedPurgeTombstonesPairedReceiptsAndRollsBackOnTheLastTarget(bool failLastTarget)
    {
        const int count = 2001;
        using var f = new Fixture();
        var runs = Enumerable.Range(0, count)
            .Select(_ => TestDatabase.Run(softDeleted: true))
            .OrderBy(run => run.RunId, StringComparer.Ordinal).ToArray();
        var kept = TestDatabase.Run();
        var both = (Request: Id(), Preview: Id());
        var ipcOnly = (Request: Id(), Preview: Id());
        var importOnly = (Request: Id(), Preview: Id());
        var unrelated = (Request: Id(), Preview: Id());
        var jsonBoth = ReceiptJson(runs[0].RunId, kept.RunId);
        var jsonIpcOnly = ReceiptJson(runs[1].RunId);
        var jsonKept = ReceiptJson(kept.RunId);
        var importFingerprint = "import-selection";

        f.Db.Database.RunInTransaction(tx =>
        {
            foreach (var run in runs) f.Runs.Insert(run, tx);
            f.Runs.Insert(kept, tx);

            using var seed = f.Db.Database.CreateCommand();
            seed.Transaction = tx;
            seed.CommandText = """
                INSERT INTO ipc_idempotency (request_id,message_type,response_json,created_at_utc)
                VALUES ($request,'BatchMutateRuns',$ipc,$now);
                INSERT INTO run_import_batches (preview_id,request_id,selection_fingerprint,response_json,committed_at_utc)
                VALUES ($preview,$request,$fingerprint,$import,$now);
                """;
            seed.Parameters.AddWithValue("$now", UtcTimestamp.ToText(f.Db.Clock.UtcNow));
            seed.Parameters.AddWithValue("$fingerprint", importFingerprint);
            var request = seed.Parameters.Add("$request", SqliteType.Text);
            var preview = seed.Parameters.Add("$preview", SqliteType.Text);
            var ipc = seed.Parameters.Add("$ipc", SqliteType.Text);
            var import = seed.Parameters.Add("$import", SqliteType.Text);
            foreach (var pair in new[]
            {
                (both, jsonBoth, ReceiptJson(runs[^1].RunId, kept.RunId)),
                (ipcOnly, jsonIpcOnly, jsonKept),
                (importOnly, jsonKept, ReceiptJson(runs[2].RunId)),
                (unrelated, jsonKept, jsonKept),
            })
            {
                request.Value = pair.Item1.Request;
                preview.Value = pair.Item1.Preview;
                ipc.Value = pair.Item2;
                import.Value = pair.Item3;
                seed.ExecuteNonQuery();
            }
        });

        using var selections = new HistorySelectionStore(f.Db.Database);
        var targets = runs.Select(run => new BatchRunTarget(run.RunId, run.Revision)).ToArray();
        var selectionId = HistorySelectionStore.Digest(targets);
        var chunks = targets.Chunk(Paging.MaxPageSize).ToArray();
        for (var index = 0; index < chunks.Length; index++)
            selections.Stage(selectionId, index, chunks[index], last: index == chunks.Length - 1);
        var command = new BatchMutateRunsCommand(Id(), "purge", [], "批量清除", SelectionId: selectionId);

        if (failLastTarget)
        {
            using var trigger = f.Db.Database.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_last_purge BEFORE DELETE ON mentor_runs WHEN OLD.run_id='" +
                runs[^1].RunId + "' BEGIN SELECT RAISE(ABORT,'synthetic purge failure'); END;";
            trigger.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => f.Mutations.BatchMutateRuns(command, selections.RequireStaged));

            Assert.Equal(count + 1L, f.Runs.Query(new RunFilter { IncludeDeleted = true }, null, 1, 1).Total);
            Assert.Equal(0, f.Count("purged_run_tombstones", runs[0].RunId));
            Assert.Equal(0, f.Count("purged_run_tombstones", runs[^1].RunId));
            Assert.Empty(f.Retention.PendingImageCleanup());
            foreach (var receipt in new[] { both, ipcOnly, importOnly, unrelated })
                Assert.Equal((true, true, null, null, null), ReadPairedReceipts(f, receipt.Request));
            Assert.Equal((false, false, null, null, null), ReadPairedReceipts(f, command.RequestId));
            return;
        }

        var outcome = f.Mutations.BatchMutateRuns(command, selections.RequireStaged);
        Assert.Equal(count, outcome.ChangedCount);
        var remaining = f.Runs.Query(new RunFilter { IncludeDeleted = true }, null, 1, 1);
        Assert.Equal(1, remaining.Total);
        Assert.Equal(kept.RunId, Assert.Single(remaining.Items).RunId);
        Assert.Equal(1, f.Count("pending_image_cleanup", runs[0].RunId));
        Assert.Equal(1, f.Count("pending_image_cleanup", runs[^1].RunId));
        Assert.Equal(0, f.Count("pending_image_cleanup", kept.RunId));
        Assert.Equal((true, true, null, null, null), ReadPairedReceipts(f, unrelated.Request));
        Assert.Equal((false, false, both.Preview, "BatchMutateRuns", HistoryRetentionService.Hash(jsonBoth)),
            ReadPairedReceipts(f, both.Request));
        Assert.Equal((false, false, ipcOnly.Preview, "BatchMutateRuns", HistoryRetentionService.Hash(jsonIpcOnly)),
            ReadPairedReceipts(f, ipcOnly.Request));
        Assert.Equal((false, false, importOnly.Preview, "CommitRunImport", HistoryRetentionService.Hash(importFingerprint)),
            ReadPairedReceipts(f, importOnly.Request));

        var idempotency = new IdempotencyRepository(f.Db.Database, f.Db.Clock);
        foreach (var receipt in new[] { both, ipcOnly, importOnly })
        {
            Assert.Equal(ErrorCodes.IdempotencyConflict,
                Assert.Throws<CollectorException>(() => idempotency.ThrowIfPurgedRequest(receipt.Request)).Code);
            Assert.Equal(ErrorCodes.IdempotencyConflict,
                Assert.Throws<CollectorException>(() => idempotency.ThrowIfPurgedRequest(Id(), previewId: receipt.Preview)).Code);
        }
        selections.Release(selectionId);
        Assert.True(f.Mutations.BatchMutateRuns(command, selections.RequireStaged).IdempotentReplay);
    }

    private static string ReceiptJson(params string[] runIds) =>
        new JsonObject { ["run_ids"] = new JsonArray(runIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }.ToJsonString();

    private static (bool Ipc, bool Import, string? Preview, string? Message, string? Fingerprint)
        ReadPairedReceipts(Fixture f, string requestId)
    {
        using var command = f.Db.Database.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ipc_idempotency WHERE request_id = $id),
                   EXISTS(SELECT 1 FROM run_import_batches WHERE request_id = $id),
                   preview_id, message_type, fingerprint
            FROM (SELECT $id AS request_id) AS request
            LEFT JOIN ipc_request_tombstones USING (request_id);
            """;
        command.Parameters.AddWithValue("$id", requestId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPurgesLiveBodiesAfterManualAndExpiredDeletion(bool expired)
    {
        using var fixture = new TestDatabase();
        using var host = CollectorHost.Open(Path.Combine(Path.GetDirectoryName(fixture.Path)!, "live-purge.db"),
            fixture.Clock, capture: InertCapture.Services());
        var run = host.Mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Id(), Reason = "测试删除", Result = RunResult.Completed, JobId = 19,
            EnteredAtUtc = fixture.Clock.UtcNow.AddHours(-1), EndedAtUtc = fixture.Clock.UtcNow.AddMinutes(-30),
            Note = "private-purge-body",
        }).Run!;
        host.ReflectionWrites.Set(new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, "private-reflection-body"));
        run = host.Runs.Get(run.RunId)!;
        run = host.Mutations.SoftDeleteRun(new RunReasonCommand(Id(), run.RunId, run.Revision, "删除")).Run!;
        Assert.Equal("private-reflection-body", run.Reflection!.Text);
        host.LiveEvents.PublishRun(LiveEventKind.RunUpdated, run);
        using var existing = host.LiveEvents.Subscribe("existing");
        if (expired)
        {
            fixture.Clock.UtcNow += TimeSpan.FromDays(31);
            Assert.Equal(1, host.HistoryRetention.CheckExpired());
        }
        else
        {
            var request = new BatchMutateRunsCommand(Id(), "purge", new[] { new BatchRunTarget(run.RunId, run.Revision) }, "清除");
            host.Mutations.BatchMutateRuns(request);
            Assert.True(host.Mutations.BatchMutateRuns(request).IdempotentReplay);
        }
        Assert.Null(host.Runs.Get(run.RunId));
        host.LiveEvents.PublishRun(LiveEventKind.RunUpdated, run);
        using var newcomer = host.LiveEvents.Subscribe("newcomer");
        foreach (var subscription in new[] { existing, newcomer })
        {
            JsonObject? payload;
            while ((payload = await subscription.ReadAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)) is not null)
            {
                Assert.DoesNotContain("private-purge-body", payload.ToJsonString());
                Assert.DoesNotContain("private-reflection-body", payload.ToJsonString());
                Assert.NotEqual(run.RunId, payload["run"]?["run_id"]?.GetValue<string>());
            }
        }
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
    public void MigrationStartsOldDeletedRowsAtTheUpgradeClock()
    {
        using var temp = new TempDirectory("RetentionMigration", strict: true);
        var path = temp.Combine("legacy.db");
        var clock = new TestClock(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        using (var old = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            old.Open();
            LegacySchema.ApplyThrough(old, 9, clock.UtcNow);
            using var seed = old.CreateCommand();
            seed.CommandText = "INSERT INTO mentor_runs(run_id,revision,result,source,soft_deleted,created_at_utc,updated_at_utc) " +
                "VALUES($id,1,'COMPLETED','MANUAL',1,'2000-01-01T00:00:00.000Z','2000-01-01T00:00:00.000Z');";
            seed.Parameters.AddWithValue("$id", Id()); seed.ExecuteNonQuery();
        }
        using var migrated = SqliteDatabase.Open(path, clock);
        var snapshotPath = Assert.Single(Directory.GetFiles(temp.Combine("backups"), "pre_schema_9_upgrade_*.db"));
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

    [Theory]
    [InlineData("soft_delete")]
    [InlineData("restore")]
    [InlineData("purge")]
    [InlineData("assign_profile")]
    [InlineData("move_region")]
    [InlineData("preview")]
    public void AStagedBatchGivesWayToAWaitingLiveCaptureWriteAndChangesNothing(string action)
    {
        using var f = new Fixture();
        var (selectionId, selections, targets) = SeedLargeHistory(f, 30, softDeleted: action is "restore" or "purge");
        using var staged = selections;
        var command = LargeBatchCommand(f, action == "preview" ? RunMutationService.MoveRegionAction : action, [], selectionId);
        var before = AllRuns(f);
        var revisions = CountRows(f, "run_revisions");

        var refusal = RefusedWhileALiveCaptureWriteWaits(f.Db.Database, () =>
        {
            if (action == "preview")
                f.Mutations.PreviewStagedRunRegionMigration(selections.RequireStaged(selectionId), Region.Tc, command.Migration!.ProfileId);
            else
                f.Mutations.BatchMutateRuns(command, selections.RequireStaged);
        });

        Assert.Equal((ErrorCodes.DbBusy, true, "history.batch.yielded_to_recording"),
            (refusal.Code, refusal.Retryable, refusal.Text?.Key));
        Assert.Equal(before, AllRuns(f));
        Assert.Equal(revisions, CountRows(f, "run_revisions"));
        Assert.Equal(0, CountRows(f, "ipc_idempotency"));
        Assert.Empty(f.Retention.PendingImageCleanup());

        // With no live write waiting the same request applies in full, with one receipt.
        if (action == "preview")
        {
            Assert.Equal(Region.Cn, f.Mutations.PreviewStagedRunRegionMigration(
                selections.RequireStaged(selectionId), Region.Tc, command.Migration!.ProfileId).SourceRegion);
            return;
        }
        var outcome = f.Mutations.BatchMutateRuns(command, selections.RequireStaged);
        Assert.Equal(action == RunMutationService.AssignProfileAction ? 15 : targets.Length, outcome.ChangedCount);
        Assert.False(outcome.IdempotentReplay);
        Assert.Equal(1, CountRows(f, "ipc_idempotency"));
        Assert.True(f.Mutations.BatchMutateRuns(command, selections.RequireStaged).IdempotentReplay);
    }

    [Fact]
    public void ALiveCaptureWriteWaitingBehindALongStagedBatchGetsTheGateWithinASecond()
    {
        using var f = new Fixture();
        var (selectionId, selections, targets) = SeedLargeHistory(f, 400, softDeleted: false);
        using var staged = selections;
        var command = LargeBatchCommand(f, "soft_delete", [], selectionId);
        var before = AllRuns(f);
        // Every changed row costs 10 ms here, so the batch alone would hold the gate for at least four seconds.
        using var batchStarted = new ManualResetEventSlim();
        using (var setup = f.Db.Database.CreateCommand())
        {
            setup.Connection!.CreateFunction("mr_test_pause", () => { batchStarted.Set(); Thread.Sleep(10); return 0L; });
            setup.CommandText = "CREATE TEMP TRIGGER slow_batch BEFORE UPDATE ON mentor_runs BEGIN SELECT mr_test_pause(); END;";
            setup.ExecuteNonQuery();
        }

        var batch = Task.Run(() => f.Mutations.BatchMutateRuns(command, selections.RequireStaged));
        Assert.True(batchStarted.Wait(TimeSpan.FromSeconds(30)));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        f.Db.Database.RunLiveCaptureTransaction(_ => { });
        waited.Stop();

        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(1), $"live write waited {waited.Elapsed.TotalMilliseconds:F0} ms");
        var refusal = Assert.Throws<CollectorException>(() => batch.GetAwaiter().GetResult());
        Assert.Equal((ErrorCodes.DbBusy, "history.batch.yielded_to_recording"), (refusal.Code, refusal.Text?.Key));
        Assert.Equal(before, AllRuns(f));
        Assert.Equal(0, CountRows(f, "ipc_idempotency"));

        using (var drop = f.Db.Database.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER temp.slow_batch;";
            drop.ExecuteNonQuery();
        }
        Assert.Equal(targets.Length, f.Mutations.BatchMutateRuns(command, selections.RequireStaged).ChangedCount);
    }

    [Fact]
    public void AStagedBatchIsRefusedWhileARunIsBeingRecorded()
    {
        using var f = new Fixture();
        var (selectionId, selections, targets) = SeedLargeHistory(f, 3, softDeleted: false);
        using var staged = selections;
        var session = f.Db.OpenCaptureSession(Region.Cn, gameBuild: null);
        f.Db.InsertWithFirstRevision(TestDatabase.Run(source: RunSource.AutoNetwork, result: RunResult.Unknown) with
            { EndedAtUtc = null, DurationMs = null, CaptureSessionId = session });
        var command = LargeBatchCommand(f, "soft_delete", [], selectionId);
        var before = AllRuns(f);

        var refusal = Assert.Throws<CollectorException>(() => f.Mutations.BatchMutateRuns(command, selections.RequireStaged));
        Assert.Equal((ErrorCodes.DbBusy, true, "history.batch.run_in_progress"),
            (refusal.Code, refusal.Retryable, refusal.Text?.Key));
        Assert.Equal(before, AllRuns(f));
        Assert.Equal(0, CountRows(f, "ipc_idempotency"));

        // Once that capture session is over, the run left in that shape no longer counts as being recorded.
        f.Db.Database.RunInTransaction(tx => new CaptureSessionRepository(f.Db.Database)
            .Close(session, f.Db.Clock.UtcNow, CaptureEndReason.UserStop, tx));
        Assert.Equal(targets.Length, f.Mutations.BatchMutateRuns(command, selections.RequireStaged).ChangedCount);

        // A replay of the applied batch is answered from its receipt even while another run is recorded.
        var next = f.Db.OpenCaptureSession(Region.Cn, gameBuild: null);
        f.Db.InsertWithFirstRevision(TestDatabase.Run(source: RunSource.AutoNetwork, result: RunResult.Unknown) with
            { EndedAtUtc = null, DurationMs = null, CaptureSessionId = next });
        Assert.True(f.Mutations.BatchMutateRuns(command, selections.RequireStaged).IdempotentReplay);
    }

    private static MentorRun[] AllRuns(Fixture f) =>
        f.Runs.Query(new RunFilter { IncludeDeleted = true }, null, 1, Paging.MaxPageSize).Items.ToArray();

    private static long CountRows(Fixture f, string table)
    {
        using var command = f.Db.Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Holds the database gate on this thread while a live-capture write waits for it on another, for longer
    /// than the yield threshold, then runs <paramref name="work"/> under the same (reentrant) gate and returns
    /// what it threw. The waiting write completes once the gate is free.
    /// </summary>
    private static CollectorException RefusedWhileALiveCaptureWriteWaits(SqliteDatabase database, Action work)
    {
        Thread? writer = null;
        var refusal = database.Read(_ =>
        {
            writer = new Thread(() => database.RunLiveCaptureTransaction(_ => { })) { IsBackground = true };
            writer.Start();
            Assert.True(SpinWait.SpinUntil(() => database.IsLiveCaptureWaiting, TimeSpan.FromSeconds(10)));
            Thread.Sleep(SqliteDatabase.LiveCaptureYieldAfterMs + 50);
            return Assert.Throws<CollectorException>(work);
        });
        Assert.True(writer!.Join(TimeSpan.FromSeconds(10)));
        Assert.False(database.IsLiveCaptureWaiting);
        return refusal;
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public HistoryRetentionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Measurement, not a gate (audit 2026-11-26 H1b-1): how long each long holder of the database gate
    /// keeps a live-capture write waiting. Prints one table row per case.
    /// </summary>
    [Theory]
    [Trait("Category", "Soak")]
    [InlineData(5000)]
    [InlineData(20000)]
    public void LongDatabaseWorkAgainstALiveCaptureWriterBenchmark(int count)
    {
        _output.WriteLine("| rows | case | duration ms | answer | longest live wait ms | live writes |");
        foreach (var action in new[] { "soft_delete", "restore", "purge", "assign_profile", "move_region" })
        {
            using var f = new Fixture();
            var (selectionId, selections, targets) = SeedLargeHistory(f, count, softDeleted: action is "restore" or "purge");
            using (selections)
            {
                var command = LargeBatchCommand(f, action, [], selectionId);
                if (action == "move_region")
                    Measure(count, "preview move_region", f.Db.Database, () => f.Mutations.PreviewStagedRunRegionMigration(
                        selections.RequireStaged(selectionId), Region.Tc, command.Migration!.ProfileId).ContributingCount);
                Measure(count, action, f.Db.Database,
                    () => f.Mutations.BatchMutateRuns(command, selections.RequireStaged).ChangedCount);
                if (action != "move_region")
                {
                    var inline = targets.Take(HistoryRetentionService.MaxBatchSize).ToArray();
                    if (action is "soft_delete" or "assign_profile") inline = inline.Select(target => target with
                        { ExpectedRevision = f.Runs.Get(target.RunId)!.Revision }).ToArray();
                    if (action is "restore" or "purge")
                    {
                        // The staged pass already changed these; run the inline case on a fresh history.
                        using var g = new Fixture();
                        var (_, other, fresh) = SeedLargeHistory(g, HistoryRetentionService.MaxBatchSize, softDeleted: true);
                        other.Dispose();
                        Measure(HistoryRetentionService.MaxBatchSize, action + " inline", g.Db.Database,
                            () => g.Mutations.BatchMutateRuns(LargeBatchCommand(g, action, fresh, null)).ChangedCount);
                    }
                    else if (action == "soft_delete")
                        Measure(inline.Length, "restore inline", f.Db.Database,
                            () => f.Mutations.BatchMutateRuns(LargeBatchCommand(f, "restore", inline, null)).ChangedCount);
                    else
                        Measure(inline.Length, "assign_profile inline (to none)", f.Db.Database, () => f.Mutations.BatchMutateRuns(
                            LargeBatchCommand(f, action, inline, null) with { ProfileId = null }).ChangedCount);
                }
            }

            if (action != "purge") continue;
            using var h = new Fixture();
            SeedLargeHistory(h, count, softDeleted: false).Selections.Dispose();
            var backup = Path.Combine(Path.GetDirectoryName(h.Db.Path)!, "backup.db");
            Measure(count, "BackupDatabase", h.Db.Database,
                () => (int)(h.Db.Database.BackupDatabase(backup, overwrite: false).ByteCount / 1024));
            var exporter = new RunExporter(h.Runs, h.Db.Clock, h.Db.Database);
            Measure(count, "ExportJson", h.Db.Database, () => (int)exporter.ExportJson(
                Path.Combine(Path.GetDirectoryName(h.Db.Path)!, "export.json"), null, overwrite: false).RowCount);
            using var expired = new Fixture();
            SeedLargeHistory(expired, count, softDeleted: true).Selections.Dispose();
            expired.Db.Clock.UtcNow += TimeSpan.FromDays(31);
            Measure(count, "retention CheckExpired", expired.Db.Database, () => expired.Retention.CheckExpired(force: true));
        }
    }

    private void Measure(int rows, string name, SqliteDatabase database, Func<int> work)
    {
        var (elapsed, answer, longest, writes) = AgainstLiveWriter(database, work);
        _output.WriteLine(FormattableString.Invariant(
            $"| {rows} | {name} | {elapsed.TotalMilliseconds:F0} | {answer} | {longest.TotalMilliseconds:F0} | {writes} |"));
    }

    /// <summary>
    /// Runs <paramref name="work"/> while another thread issues a trivial live-capture write every 50 ms,
    /// and returns how long the work took, what it answered, and the longest any of those writes took.
    /// </summary>
    private static (TimeSpan Elapsed, string Answer, TimeSpan LongestLiveWrite, int LiveWrites) AgainstLiveWriter(
        SqliteDatabase database, Func<int> work)
    {
        using var stop = new CancellationTokenSource();
        var longest = TimeSpan.Zero;
        var writes = 0;
        var writer = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                database.RunLiveCaptureTransaction(tx =>
                {
                    using var probe = database.CreateCommand();
                    probe.Transaction = tx;
                    probe.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                    probe.ExecuteScalar();
                });
                if (started.Elapsed > longest) longest = started.Elapsed;
                writes++;
                stop.Token.WaitHandle.WaitOne(50);
            }
        }) { IsBackground = true, Name = "benchmark-live-writer" };
        writer.Start();
        Thread.Sleep(120);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string answer;
        try { answer = work().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (CollectorException ex) { answer = ex.Code + " " + ex.Text?.Key; }
        var elapsed = clock.Elapsed;
        Thread.Sleep(120);
        stop.Cancel();
        writer.Join();
        return (elapsed, answer, longest, writes);
    }

    /// <summary>
    /// <paramref name="count"/> manual runs filed under the CN profile, each at revision 3 with its creation and two
    /// corrections in the trail, every fifth with a reflection; staged as one selection.
    /// </summary>
    private static (string SelectionId, HistorySelectionStore Selections, BatchRunTarget[] Targets) SeedLargeHistory(
        Fixture f, int count, bool softDeleted)
    {
        var profile = f.Db.CurrentProfile();
        f.Db.CurrentProfile(Region.Tc);
        var origin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var runs = Enumerable.Range(0, count).Select(index => TestDatabase.Run(softDeleted: softDeleted,
                enteredAt: origin.AddHours(index)) with
            { Revision = 3, ProfileId = index % 2 == 0 ? profile : null, DeletedAtUtc = softDeleted ? f.Db.Clock.UtcNow : null })
            .OrderBy(run => run.RunId, StringComparer.Ordinal).ToArray();
        var revisions = new RunRevisionRepository(f.Db.Database);
        var reflections = new RunReflectionRepository(f.Db.Database);
        f.Db.Database.RunInTransaction(tx =>
        {
            using var advance = f.Db.Database.CreateCommand();
            advance.Transaction = tx;
            advance.CommandText = "UPDATE mentor_runs SET revision = $revision WHERE run_id = $id;";
            var advanceTo = advance.Parameters.Add("$revision", SqliteType.Integer);
            var advanceId = advance.Parameters.Add("$id", SqliteType.Text);
            for (var index = 0; index < runs.Length; index++)
            {
                var run = runs[index];
                f.Runs.Insert(run with { Revision = 1 }, tx);
                for (var revision = 1; revision <= 3; revision++)
                {
                    if (revision > 1)
                    {
                        (advanceTo.Value, advanceId.Value) = (revision, run.RunId);
                        advance.ExecuteNonQuery();
                    }
                    revisions.Append(new RunRevision
                    {
                        RevisionId = Id(), RunId = run.RunId, Revision = revision, ChangedAtUtc = run.CreatedAtUtc,
                        ChangeKind = revision == 1 ? ChangeKind.CreateManual : ChangeKind.Correct,
                        Actor = RevisionActor.User, Reason = "测试", RequestId = Id(),
                        Changes = revision == 1 ? [] : [new RunFieldChange("note", null, "第 " + revision + " 次")],
                    }, tx);
                }
                if (index % 5 == 0) reflections.Upsert(run.RunId, ReflectionMood.Good, "心得", f.Db.Clock.UtcNow, tx);
            }
        });
        var targets = runs.Select(run => new BatchRunTarget(run.RunId, run.Revision)).ToArray();
        var selections = new HistorySelectionStore(f.Db.Database);
        var selectionId = HistorySelectionStore.Digest(targets);
        var chunks = targets.Chunk(Paging.MaxPageSize).ToArray();
        for (var index = 0; index < chunks.Length; index++)
            selections.Stage(selectionId, index, chunks[index], last: index == chunks.Length - 1);
        return (selectionId, selections, targets);
    }

    private static BatchMutateRunsCommand LargeBatchCommand(Fixture f, string action, BatchRunTarget[] runs, string? selectionId)
    {
        var command = new BatchMutateRunsCommand(Id(), action, runs, "批量操作", SelectionId: selectionId);
        return action switch
        {
            RunMutationService.AssignProfileAction => command with { ProfileId = f.Db.CurrentProfile() },
            RunMutationService.MoveRegionAction => command with
                { Migration = new RunRegionMigration(Region.Tc, f.Db.CurrentProfile(Region.Tc), 0, 0) },
            _ => command,
        };
    }
}
