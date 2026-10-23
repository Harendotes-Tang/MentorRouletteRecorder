using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ImportedRestartMaintenanceTests
{
    private static string Id() => Guid.NewGuid().ToString("D");

    [Theory]
    [InlineData(RunResult.Completed, false)]
    [InlineData(RunResult.Completed, true)]
    [InlineData(RunResult.LeftOrAbandoned, false)]
    [InlineData(RunResult.LeftOrAbandoned, true)]
    public void CompleteLocalEvidenceRestoresKnownImportFactsAndPreservesUnrelatedHumanEdits(RunResult outcome, bool entered)
    {
        using var fixture = new TestDatabase();
        var (original, legacy) = Seed(fixture, outcome, entered, "notes-and-job");
        fixture.Database.Dispose();
        MentorRun maintained;
        using (var host = CollectorHost.Open(fixture.Path, fixture.Clock, new CaptureServices()))
        {
            maintained = host.Runs.Get(original.RunId)!;
            Assert.Equal(RunSource.Import, maintained.Source);
            Assert.Equal(original.Result, maintained.Result);
            Assert.Equal(original.MatchedAtUtc, maintained.MatchedAtUtc);
            Assert.Equal(original.EnteredAtUtc, maintained.EnteredAtUtc);
            Assert.Null(maintained.EndedAtUtc);
            Assert.Null(maintained.DurationMs);
            Assert.False(maintained.PendingReview);
            Assert.Equal(legacy.Note, maintained.Note);
            Assert.Equal(legacy.JobId, maintained.JobId);
            Assert.Equal(legacy.JobName, maintained.JobName);
            Assert.True(maintained.ManuallyCorrected);
            Assert.Equal(legacy.Revision + 1, maintained.Revision);
            var revision = host.Revisions.GetAt(maintained.RunId, maintained.Revision, null)!;
            Assert.Equal(ImportedHistoryMaintenance.AuditReason, revision.Reason);
            Assert.Equal(RevisionActor.System, revision.Actor);
            Assert.Contains(revision.Changes, change => change.Field == RunFields.Result && Equals(change.NewValue, EnumWire<RunResult>.Format(outcome)));
            Assert.Contains(revision.Changes, change => change.Field == RunFields.EndedAtUtc && change.NewValue is null);
            Assert.DoesNotContain(revision.Changes, change => change.Field is RunFields.Note or RunFields.JobId);
            Assert.Equal(0, host.Recovery.RecoveredCount);
            Assert.Equal(0, ImportedHistoryMaintenance.Run(host));
            Assert.Equal(outcome == RunResult.Completed ? 1 : 0, host.Statistics.GetDashboard().CompletedCount);
            var refused = Assert.Throws<CollectorException>(() => host.Mutations.UndoRevision(new RunReasonCommand(Id(), maintained.RunId, maintained.Revision, "不应撤销来源修复")));
            Assert.Equal("ERR_UNDO_NOT_ALLOWED", refused.Code);
        }
        using var reopened = CollectorHost.Open(fixture.Path, fixture.Clock, new CaptureServices());
        Assert.Equal(maintained, reopened.Runs.Get(original.RunId));
        Assert.Equal(0, reopened.Recovery.RecoveredCount);
    }

    [Theory]
    [InlineData("manual-result")]
    [InlineData("manual-endpoint")]
    [InlineData("manual-acknowledgement")]
    [InlineData("missing-import")]
    [InlineData("missing-creation-fact")]
    [InlineData("creation-null-element")]
    [InlineData("system-null-element")]
    [InlineData("user-null-element")]
    [InlineData("user-empty-field")]
    [InlineData("creation-invalid-json")]
    [InlineData("system-invalid-json")]
    [InlineData("huge-duration")]
    [InlineData("negative-duration")]
    [InlineData("noninteger-revision")]
    [InlineData("timestamp-number")]
    [InlineData("pending-object")]
    [InlineData("inverted-endpoints")]
    [InlineData("old-value-conflict")]
    [InlineData("current-value-conflict")]
    [InlineData("missing-last-revision")]
    [InlineData("missing-restart-event")]
    [InlineData("fake-restart-reason")]
    [InlineData("fake-restart-key")]
    [InlineData("external-backup-current-snapshot")]
    public void MissingConflictingOrMalformedEvidenceSkipsOutcomeRepairWithoutPreventingStartup(string defect)
    {
        using var fixture = new TestDatabase();
        var (original, legacy) = Seed(fixture, RunResult.Completed, false, defect);
        fixture.Database.Dispose();
        using var host = CollectorHost.Open(fixture.Path, fixture.Clock, new CaptureServices());
        var maintained = host.Runs.Get(original.RunId)!;
        Assert.Equal(legacy.Result, maintained.Result);
        Assert.Equal(legacy.MatchedAtUtc, maintained.MatchedAtUtc);
        Assert.Equal(legacy.EnteredAtUtc, maintained.EnteredAtUtc);
        Assert.Equal(legacy.EndedAtUtc, maintained.EndedAtUtc);
        Assert.Equal(legacy.DurationMs, maintained.DurationMs);
        Assert.Equal(defect == "missing-last-revision" ? RunSource.AutoNetwork : RunSource.Import, maintained.Source);
        Assert.Equal(0, host.Recovery.RecoveredCount);
        Assert.Equal(0, ImportedHistoryMaintenance.Run(host));
    }

    private static (MentorRun Original, MentorRun Legacy) Seed(TestDatabase fixture, RunResult result, bool entered, string defect)
    {
        var now = fixture.Clock.UtcNow;
        var session = Id();
        var original = TestDatabase.Run(result: result, source: RunSource.AutoNetwork) with
        {
            CaptureSessionId = session,
            MatchedAtUtc = null, EnteredAtUtc = entered ? now.AddMinutes(-2) : null,
            EndedAtUtc = null, DurationMs = null, PendingReview = true,
            ImportMetadata = new RunImportMetadata("JSON", "合成旧导入", "2026/9/3", null, now, Id()),
        };
        var first = Wire.Run(original).Select(pair => new RunFieldChange(pair.Key, null, AuditValue(pair.Value))).ToList();
        var firstKind = defect == "missing-import" ? ChangeKind.CreateAuto : ChangeKind.Import;
        if (defect == "missing-creation-fact") first.RemoveAll(change => change.Field == RunFields.EndedAtUtc);
        if (defect == "external-backup-current-snapshot")
            first[first.FindIndex(change => change.Field == RunFields.Result)] = new(RunFields.Result, null, "CANCELLED_BEFORE_ENTRY");
        if (defect == "inverted-endpoints")
        {
            first[first.FindIndex(change => change.Field == RunFields.MatchedAtUtc)] = new(RunFields.MatchedAtUtc, null, UtcTimestamp.ToText(now));
            first[first.FindIndex(change => change.Field == RunFields.EnteredAtUtc)] = new(RunFields.EnteredAtUtc, null, UtcTimestamp.ToText(now.AddMinutes(-1)));
        }
        var recovered = original with
        {
            Revision = 2, Result = entered ? RunResult.Interrupted : RunResult.CancelledBeforeEntry,
            EndedAtUtc = now, DurationMs = entered ? 120_000 : null, DetectionConfidence = DetectionConfidence.Low,
        };
        var systemChanges = RunMutationRules.Diff(original, recovered).ToList();
        systemChanges.Add(new("detection_confidence", "HIGH", "LOW"));
        if (defect == "old-value-conflict")
            systemChanges[systemChanges.FindIndex(change => change.Field == RunFields.Result)] = new(RunFields.Result, "UNKNOWN", "CANCELLED_BEFORE_ENTRY");
        var firstJson = RunRevisionRepository.SerializeChanges(first);
        var systemJson = RunRevisionRepository.SerializeChanges(systemChanges);
        if (defect == "creation-null-element") firstJson = "[null]";
        if (defect == "system-null-element") systemJson = "[null]";
        if (defect == "creation-invalid-json") firstJson = "{";
        if (defect == "system-invalid-json") systemJson = "{";
        foreach (var (name, field, raw) in new[]
        {
            ("huge-duration", RunFields.DurationMs, "1e100"), ("negative-duration", RunFields.DurationMs, "-1"),
            ("noninteger-revision", "revision", "1.5"), ("timestamp-number", RunFields.EnteredAtUtc, "12"),
            ("pending-object", RunFields.PendingReview, "{}"),
        })
            if (defect == name)
            {
                var rows = JsonNode.Parse(firstJson)!.AsArray();
                var row = rows.OfType<JsonObject>().Single(item => item["field"]!.GetValue<string>() == field);
                row["new_value"] = JsonNode.Parse(raw);
                firstJson = rows.ToJsonString();
            }
        var legacy = recovered;
        fixture.Database.RunInTransaction(tx =>
        {
            new CaptureSessionRepository(fixture.Database).Insert(new CaptureSession
                { CaptureSessionId = session, StartedAtUtc = now.AddHours(-1), CollectorVersion = "legacy-test" }, tx);
            var runs = new RunRepository(fixture.Database);
            runs.Insert(original, tx);
            new RunImportRepository(fixture.Database).Insert(original.RunId, original.ImportMetadata!, tx);
            AppendRaw(fixture, original, firstKind, RevisionActor.User, "本人导入历史", firstJson, tx);
            runs.Update(recovered, 1, tx);
            AppendRaw(fixture, recovered, ChangeKind.Correct, RevisionActor.System,
                defect == "fake-restart-reason" ? "没有真实重启依据" : entered
                    ? "程序重启时发现未完结记录，已记为中断并标记待复核。"
                    : "程序重启时发现未完结记录，它尚未进入副本，已记为进本前取消并标记待复核。", systemJson, tx);
            if (defect != "missing-restart-event")
                new RunEventRepository(fixture.Database).Append(new RunEvent
                {
                    EventId = Id(), RunId = original.RunId, Sequence = 1, OccurredAtUtc = now,
                    MonotonicOffsetMs = 0, EventType = "PROCESS_RESTART", Confidence = DetectionConfidence.Low,
                    ToState = entered ? RunState.InterruptedPendingReview : RunState.CancelledBeforeEntry,
                    EventKey = defect == "fake-restart-key" ? Id()
                        : new EventKey(session, PacketDirection.None, "PROCESS_RESTART", 0, null, "restart:" + original.RunId).ToCanonicalString(),
                }, tx);
            if (defect is "notes-and-job" or "manual-result" or "manual-endpoint" or "manual-acknowledgement" or "user-null-element" or "user-empty-field")
            {
                legacy = recovered with { Revision = 3, Note = "后来补充的备注保留", JobId = 34, JobName = "武士", Role = Role.Dps, ManuallyCorrected = true };
                if (defect == "manual-result") legacy = legacy with { Result = RunResult.LeftOrAbandoned, EnteredAtUtc = now.AddMinutes(-1) };
                if (defect == "manual-endpoint") legacy = legacy with { EndedAtUtc = now.AddSeconds(10) };
                if (defect == "manual-acknowledgement") legacy = legacy with { PendingReview = false };
                runs.Update(legacy, 2, tx);
                AppendRaw(fixture, legacy, ChangeKind.Correct, RevisionActor.User, "人工后续更正",
                    defect == "user-null-element" ? "[null]" : defect == "user-empty-field"
                        ? "[{\"field\":\"\",\"old_value\":null,\"new_value\":null}]"
                        : RunRevisionRepository.SerializeChanges(RunMutationRules.Diff(recovered, legacy)), tx);
            }
            if (defect is "current-value-conflict" or "missing-last-revision")
            {
                legacy = recovered with { EndedAtUtc = now.AddSeconds(20), Revision = defect == "missing-last-revision" ? 3 : 2 };
                runs.Update(legacy, 2, tx);
            }
        });
        return (original, legacy);
    }

    private static void AppendRaw(TestDatabase fixture, MentorRun run, ChangeKind kind, RevisionActor actor,
        string reason, string changes, SqliteTransaction tx)
    {
        using var command = fixture.Database.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO run_revisions VALUES ($id,$run,$revision,$at,$kind,$actor,$reason,NULL,$changes);";
        command.Parameters.AddWithValue("$id", Id()); command.Parameters.AddWithValue("$run", run.RunId);
        command.Parameters.AddWithValue("$revision", run.Revision); command.Parameters.AddWithValue("$at", UtcTimestamp.ToText(fixture.Clock.UtcNow));
        command.Parameters.AddWithValue("$kind", EnumWire<ChangeKind>.Format(kind)); command.Parameters.AddWithValue("$actor", EnumWire<RevisionActor>.Format(actor));
        command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$changes", changes);
        command.ExecuteNonQuery();
    }

    private static object? AuditValue(JsonNode? value) => value is null ? null : value is JsonValue scalar
        ? scalar.TryGetValue<string>(out var text) ? text : scalar.TryGetValue<bool>(out var flag) ? flag
            : scalar.TryGetValue<long>(out var integer) ? integer : scalar.GetValue<int>()
        : value.ToJsonString();
}
