using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ImportedTimeExemptionTests
{
    private static string Id() => Guid.NewGuid().ToString("D");
    private static string Import(TestDatabase fixture, JsonObject candidate)
    {
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(candidate), timeZone: "+08:00");
        Assert.Equal("new", preview["rows"]![0]!["status"]!.GetValue<string>());
        return service.Commit(preview["preview_id"]!.GetValue<string>(), [1], true, Id())["run_ids"]![0]!.GetValue<string>();
    }
    private static StatisticsRepository Statistics(TestDatabase fixture)
    {
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        return new StatisticsRepository(fixture.Database, settings, clock: fixture.Clock);
    }

    [Theory]
    [InlineData("2026-10-08", "2026-10-08T00:00:00.000Z")]
    [InlineData("2026/10/8", "2026-10-08T00:00:00.000Z")]
    [InlineData("2026-10-08 20:30:00", "2026-10-08T12:30:00.000Z")]
    public void ReviewedCompletionUsesSourceDateForHistoryAndTrendWithoutChangingActualFields(string source, string historyDate)
    {
        using var fixture = new TestDatabase();
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var id = Import(fixture, new JsonObject { ["duty_name"] = "合成测试副本", ["source_recorded_at"] = source, ["result"] = "COMPLETED" });
        var runs = new RunRepository(fixture.Database);
        var run = runs.Get(id)!;
        Assert.Equal(RunSource.Import, run.Source);
        Assert.False(run.PendingReview);
        Assert.True(run.IsIncompleteImport);
        Assert.True(run.IsConfirmedMentor);
        Assert.Null(run.MatchedAtUtc);
        Assert.Null(run.EnteredAtUtc);
        Assert.Null(run.EndedAtUtc);
        Assert.Null(run.DurationMs);
        Assert.Null(run.CaptureSessionId);
        var day = DateTimeOffset.Parse(historyDate, System.Globalization.CultureInfo.InvariantCulture);
        var filter = new RunFilter { DateField = RunDateField.HistoryDate, FromUtc = day, ToUtc = day };
        Assert.Equal(id, Assert.Single(runs.Query(filter, new RunSort(RunSortField.HistoryDate), 1, 50).Items).RunId);
        Assert.Empty(runs.Query(filter with { DateField = RunDateField.MatchedAtUtc }, null, 1, 50).Items);
        var stats = Statistics(fixture);
        var dashboard = stats.GetDashboard(filter);
        Assert.Equal(1, dashboard.AttemptCount);
        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.AchievementProgress);
        Assert.Equal(0, dashboard.UnfinishedPendingReview);
        Assert.Equal(1, dashboard.CompletionRate);
        Assert.Null(dashboard.AverageDurationMs);
        Assert.Equal(1, Assert.Single(dashboard.Trend.Buckets, b => b.StartUtc.Date == day.Date).CompletedCount);
        Assert.Equal(1, stats.GetTrend(granularity: TrendGranularity.Week).Buckets.Sum(b => b.CompletedCount));
        Assert.Equal(1, stats.GetTrend(granularity: TrendGranularity.Month).Buckets.Sum(b => b.CompletedCount));
        var dungeon = Assert.Single(stats.GetDungeonStats());
        Assert.Equal(1, dungeon.CompletedCount);
        Assert.Null(dungeon.LastSeenUtc); // Actual matched time is still unknown.
        var wire = Wire.Run(run);
        Assert.Null(wire["matched_at_utc"]);
        Assert.Null(wire["entered_at_utc"]);
        Assert.Null(wire["ended_at_utc"]);
        fixture.Reopen();
        Assert.Equal(run, new RunRepository(fixture.Database).Get(id));
    }

    [Fact]
    public void UnknownOutcomeAndExplicitNonMentorKeepHistoryWithoutCompletionOrReview()
    {
        using var fixture = new TestDatabase();
        var unknown = Import(fixture, new JsonObject { ["duty_name"] = "未知结果合成副本", ["result"] = "UNKNOWN" });
        var nonMentor = Import(fixture, new JsonObject { ["duty_name"] = "非导随合成副本", ["result"] = "COMPLETED", ["mentor_confirmed"] = false });
        var runs = new RunRepository(fixture.Database);
        Assert.False(runs.Get(unknown)!.PendingReview);
        Assert.False(runs.Get(nonMentor)!.IsConfirmedMentor);
        Assert.Empty(runs.Query(new RunFilter { PendingReview = true }, null, 1, 50).Items);
        Assert.Equal(2, runs.Query(null, null, 1, 50).Total);
        var stats = Statistics(fixture).GetDashboard();
        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal(0, stats.AchievementProgress);
        Assert.Equal(0, stats.UnfinishedPendingReview);
    }

    [Fact]
    public void ProvidedImportTimesStillRejectInvalidOrdering()
    {
        var run = TestDatabase.Run(source: RunSource.Import) with
        {
            EnteredAtUtc = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            DurationMs = null,
        };
        Assert.Throws<RunRuleViolationException>(() => RunMutationRules.ValidateFinalValue(run, false));
    }

    [Theory]
    [InlineData(RunSource.AutoNetwork)]
    [InlineData(RunSource.Import)]
    public void LegacyImportEvidenceIsAuditedBeforeRecoveryAndNeverInfersACancelledOutcome(RunSource oldSource)
    {
        using var fixture = new TestDatabase();
        var session = Id();
        var runs = new RunRepository(fixture.Database);
        var revisions = new RunRevisionRepository(fixture.Database);
        var imported = TestDatabase.Run(source: oldSource) with
        {
            CaptureSessionId = session, MatchedAtUtc = null, EnteredAtUtc = null, EndedAtUtc = null,
            DurationMs = null, PendingReview = true,
            ImportMetadata = new RunImportMetadata("JSON", "synthetic", "2026-09-04", null, fixture.Clock.UtcNow, Id()),
        };
        var auditOnly = imported with { RunId = Id(), Source = RunSource.AutoNetwork, Result = RunResult.Unknown, ImportMetadata = null };
        var cancelled = imported with { RunId = Id(), Source = RunSource.AutoNetwork, Result = RunResult.CancelledBeforeEntry, ImportMetadata = null, EndedAtUtc = fixture.Clock.UtcNow };
        fixture.Database.RunInTransaction(tx =>
        {
            new CaptureSessionRepository(fixture.Database).Insert(new CaptureSession { CaptureSessionId = session, StartedAtUtc = fixture.Clock.UtcNow.AddHours(-1), CollectorVersion = "legacy-test" }, tx);
            foreach (var row in new[] { imported, auditOnly, cancelled }) runs.Insert(row, tx);
            new RunImportRepository(fixture.Database).Insert(imported.RunId, imported.ImportMetadata!, tx);
            revisions.Append(new RunRevision { RevisionId = Id(), RunId = imported.RunId, Revision = 1, ChangedAtUtc = fixture.Clock.UtcNow,
                ChangeKind = ChangeKind.Import, Actor = RevisionActor.User, Reason = "本人导入", Changes = [new RunFieldChange("source", null, "IMPORT")] }, tx);
            revisions.Append(new RunRevision { RevisionId = Id(), RunId = auditOnly.RunId, Revision = 1, ChangedAtUtc = fixture.Clock.UtcNow,
                ChangeKind = ChangeKind.Import, Actor = RevisionActor.User, Reason = "本人导入", Changes = [new RunFieldChange("source", null, "IMPORT")] }, tx);
        });
        fixture.Database.Dispose();
        using (var host = CollectorHost.Open(fixture.Path, fixture.Clock, new CaptureServices()))
        {
            var saved = host.Runs.Get(imported.RunId)!;
            Assert.Equal(RunSource.Import, saved.Source);
            Assert.Equal(RunResult.Completed, saved.Result);
            Assert.False(saved.PendingReview);
            Assert.Null(saved.EndedAtUtc);
            Assert.Equal(2, saved.Revision);
            Assert.Equal(RevisionActor.System, host.Revisions.GetAt(saved.RunId, 2, null)!.Actor);
            var refused = Assert.Throws<CollectorException>(() => host.Mutations.UndoRevision(new RunReasonCommand(Id(), saved.RunId, 2, "尝试撤销来源维护")));
            Assert.Equal("ERR_UNDO_NOT_ALLOWED", refused.Code);
            Assert.Equal(saved, host.Runs.Get(saved.RunId));
            Assert.Empty(host.Events.ListForRun(saved.RunId));
            var audit = host.Runs.Get(auditOnly.RunId)!;
            Assert.Equal(RunSource.Import, audit.Source);
            Assert.Equal(RunResult.Unknown, audit.Result);
            Assert.False(audit.PendingReview);
            Assert.Null(audit.EndedAtUtc);
            Assert.Equal(cancelled, host.Runs.Get(cancelled.RunId));
            Assert.Equal(0, host.Recovery.RecoveredCount);
            Assert.Equal(0, ImportedHistoryMaintenance.Run(host));
        }
        fixture.Reopen();
        Assert.Equal(2, new RunRepository(fixture.Database).Get(imported.RunId)!.Revision);
    }

    [Fact]
    public void SourceCalendarDayFilteringSurvivesNegativeUtcOffsetAndKeepsKnownInstantsSeparate()
    {
        using var fixture = new TestDatabase();
        fixture.Clock.UtcNow = new DateTimeOffset(2024, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var dayOnly = Import(fixture, new JsonObject { ["duty_name"] = "日期未知时分", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024/9/24" });
        var instantInside = Import(fixture, new JsonObject { ["duty_name"] = "日期带时分", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024-09-24T18:00:00Z" });
        var instantOutside = Import(fixture, new JsonObject { ["duty_name"] = "UTC同日但本地前日", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024-09-24T02:00:00Z" });
        var nextDay = Import(fixture, new JsonObject { ["duty_name"] = "下一原站日期", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024/9/25" });
        var invalid = new RunImportService(fixture.Database, fixture.Clock).PreviewSource("ROWS", rows: new JsonArray(new JsonObject
            { ["duty_name"] = "非法原站日期", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024/99/24" }));
        Assert.Equal("invalid", invalid["rows"]![0]!["status"]!.GetValue<string>());
        var filter = new RunFilter { DateField = RunDateField.HistoryDate, HistoryFromDay = "2024-09-24", HistoryToDay = "2024-09-24",
            FromUtc = new DateTimeOffset(2024, 9, 24, 4, 0, 0, TimeSpan.Zero), ToUtc = new DateTimeOffset(2024, 9, 25, 3, 59, 59, 999, TimeSpan.Zero) };
        var runs = new RunRepository(fixture.Database);
        Assert.Equal(new[] { dayOnly, instantInside }.Order(), runs.Query(filter, null, 1, 50).Items.Select(row => row.RunId).Order());
        Assert.DoesNotContain(runs.Query(filter, null, 1, 50).Items, row => row.RunId == instantOutside);
        var stats = Statistics(fixture).GetDashboard(filter);
        Assert.Equal(2, stats.CompletedCount);
        Assert.Equal(2, stats.Trend.Buckets.Sum(b => b.CompletedCount));
        Assert.Null(runs.Get(dayOnly)!.ImportMetadata!.SourceRecordedAtUtc);
        Assert.Null(runs.Get(dayOnly)!.MatchedAtUtc);
        var withoutCalendarBounds = runs.Query(filter with { HistoryFromDay = null, HistoryToDay = null }, null, 1, 50).Items;
        Assert.DoesNotContain(withoutCalendarBounds, row => row.RunId == dayOnly);
        Assert.Contains(withoutCalendarBounds, row => row.RunId == nextDay);
    }

    [Fact]
    public void DeletedLegacyImportIsMaintainedWithDeletionTimeIntactAndRestoresToCountImmediately()
    {
        using var fixture = new TestDatabase();
        var id = Import(fixture, new JsonObject { ["duty_name"] = "回收站旧导入", ["result"] = "COMPLETED" });
        var runs = new RunRepository(fixture.Database);
        var revisions = new RunRevisionRepository(fixture.Database);
        var deletionTime = fixture.Clock.UtcNow;
        fixture.Database.RunInTransaction(tx =>
        {
            var before = runs.GetInternal(id, tx)!;
            var legacyDeleted = before with { Revision = 2, PendingReview = true, SoftDeleted = true, DeletedAtUtc = deletionTime };
            runs.Update(legacyDeleted, 1, tx);
            revisions.Append(new RunRevision { RevisionId = Id(), RunId = id, Revision = 2, ChangedAtUtc = deletionTime,
                ChangeKind = ChangeKind.SoftDelete, Actor = RevisionActor.User, Reason = "旧版删除", Changes = RunMutationRules.Diff(before, legacyDeleted) }, tx);
        });
        fixture.Database.Dispose();
        using (var host = CollectorHost.Open(fixture.Path, fixture.Clock, new CaptureServices()))
        {
            var maintained = host.Runs.Get(id)!;
            Assert.True(maintained.SoftDeleted);
            Assert.Equal(deletionTime, maintained.DeletedAtUtc);
            Assert.False(maintained.PendingReview);
            Assert.Equal(3, maintained.Revision);
            var restored = host.Mutations.RestoreRun(new RunReasonCommand(Id(), id, 3, "恢复本人历史")).Run!;
            Assert.False(restored.PendingReview);
            Assert.Null(restored.DeletedAtUtc);
            Assert.Null(restored.EnteredAtUtc);
            Assert.Null(restored.EndedAtUtc);
            Assert.Equal(1, host.Statistics.GetDashboard().CompletedCount);
            Assert.Equal(1, host.Statistics.GetDashboard().AchievementProgress);
        }
    }

    [Fact]
    public void SourceOnlyDateSortUsesLocalCalendarMidnightWithoutChangingTrendOrStoredFacts()
    {
        using var fixture = new TestDatabase();
        var id = Import(fixture, new JsonObject { ["duty_name"] = "原站九月二十四日", ["result"] = "COMPLETED", ["source_recorded_at"] = "2024/9/24" });
        var midnight = new DateTime(2024, 9, 24, 0, 0, 0, DateTimeKind.Local);
        var localMidnightUtc = new DateTimeOffset(midnight).ToUniversalTime();
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "SELECT " + RunFilterSql.ColumnOf(RunSortField.HistoryDate) + " FROM mentor_runs WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(UtcTimestamp.ToText(localMidnightUtc), (string)command.ExecuteScalar()!);
        }
        var previousEvening = TestDatabase.Run(enteredAt: localMidnightUtc.AddHours(-2));
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => runs.Insert(previousEvening, tx));
        Assert.Equal(id, runs.Query(null, new RunSort(RunSortField.HistoryDate), 1, 50).Items[0].RunId);
        fixture.Clock.UtcNow = new DateTimeOffset(2024, 9, 25, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, Assert.Single(Statistics(fixture).GetTrend(new RunFilter { RunIds = [id] }).Buckets,
            bucket => bucket.StartUtc.Date == new DateTime(2024, 9, 24)).CompletedCount);
        Assert.Null(runs.Get(id)!.MatchedAtUtc);
        Assert.Null(runs.Get(id)!.ImportMetadata!.SourceRecordedAtUtc);
    }
}
