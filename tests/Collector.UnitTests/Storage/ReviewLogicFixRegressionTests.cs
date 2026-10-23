using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class BaselineExpiryRegressionTests
{
    private static string Id() => Guid.NewGuid().ToString("D");
    private static SettingsRepository Settings(TestDatabase fixture)
    {
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        return settings;
    }
    private static UpdateAchievementBaselineCommand Save(TestDatabase fixture, int count) =>
        new(Id(), 2000, count, fixture.Clock.UtcNow, "回归测试：保存基数");
    private static void AssertExpired(RunMutationService service, UpdateAchievementBaselineCommand command)
    {
        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(command));
        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
        Assert.Equal("request_id", error.Field);
        Assert.Equal("RESPONSE_EXPIRED", error.Details!["reason"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExpiredBaselineNeverOverwritesLaterValuesEvenWithoutRetainedAudit(bool unchanged, bool trimAudit)
    {
        using var fixture = new TestDatabase();
        var settings = Settings(fixture);
        var service = new RunMutationService(fixture.Database, settings, fixture.Clock);
        var original = Save(fixture, unchanged ? 0 : 10);
        service.UpdateAchievementBaseline(original);
        Assert.True(service.UpdateAchievementBaseline(original).IdempotentReplay);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        var writes = trimAudit ? SettingsRepository.MaxBaselineAuditEntries + 1 : 1;
        for (var index = 0; index < writes; ++index)
            service.UpdateAchievementBaseline(Save(fixture, 20 + index));
        if (unchanged || trimAudit)
            Assert.DoesNotContain(settings.ReadBaselineAudit(), row => row.RequestId == original.RequestId);
        Assert.Null(new IdempotencyRepository(fixture.Database, fixture.Clock).TryGetResponse(original.RequestId));

        fixture.Reopen();
        settings = Settings(fixture);
        service = new RunMutationService(fixture.Database, settings, fixture.Clock);
        var later = settings.GetAchievementSettings();
        AssertExpired(service, original);
        AssertExpired(service, original with { GoalCount = 2500 });
        Assert.Equal(later, settings.GetAchievementSettings());
    }

    [Fact]
    public void LegacyPrunedReceiptIsRecoveredBeforeItsLastAuditEntryIsTrimmed()
    {
        using var fixture = new TestDatabase();
        var settings = Settings(fixture);
        var service = new RunMutationService(fixture.Database, settings, fixture.Clock);
        var original = Save(fixture, 10);
        service.UpdateAchievementBaseline(original);
        // Model an older version: the response was deleted, but its bounded audit survives.
        fixture.Database.RunInTransaction(tx =>
        {
            using var command = fixture.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM ipc_idempotency WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", original.RequestId);
            command.ExecuteNonQuery();
        });
        Assert.True(new IdempotencyRepository(fixture.Database, fixture.Clock).WasAppliedBeforePrune(original.RequestId));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        for (var index = 0; index < SettingsRepository.MaxBaselineAuditEntries; ++index)
            service.UpdateAchievementBaseline(Save(fixture, 20 + index));
        Assert.DoesNotContain(settings.ReadBaselineAudit(), row => row.RequestId == original.RequestId);
        AssertExpired(service, original);
        Assert.Equal(119, settings.GetAchievementSettings().BaselineCompletedCount);
    }

    [Fact]
    public void ExpiredBaselineClearsResponseBodyButKeepsItsIdWhenOtherReceiptsAreRemoved()
    {
        using var fixture = new TestDatabase();
        var repository = new IdempotencyRepository(fixture.Database, fixture.Clock);
        var baselineId = Id();
        var ordinaryId = Id();
        fixture.Database.RunInTransaction(tx =>
        {
            repository.Store(baselineId, "UpdateAchievementBaseline", "{\"private_response\":\"expired\"}", tx);
            repository.Store(ordinaryId, "CorrectRun", "{\"ok\":true}", tx);
        });
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        fixture.Database.RunInTransaction(tx => repository.Store(Id(), "CorrectRun", "{}", tx));
        Assert.Null(repository.TryGetResponse(baselineId));
        Assert.True(repository.WasAppliedBeforePrune(baselineId));
        using var command = fixture.Database.CreateCommand();
        command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id = $id;";
        command.Parameters.AddWithValue("$id", baselineId);
        Assert.Equal("null", command.ExecuteScalar());
        command.Parameters["$id"].Value = ordinaryId;
        Assert.Null(command.ExecuteScalar());
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(-3);
        Assert.Null(repository.TryGetResponse(baselineId));
        Assert.True(repository.WasAppliedBeforePrune(baselineId));
    }
    [Fact]
    public void BaselineFailureRollsBackSettingsAuditRecoveredMarkerAndResponsePruning()
    {
        using var fixture = new TestDatabase();
        var settings = Settings(fixture);
        var service = new RunMutationService(fixture.Database, settings, fixture.Clock);
        var original = Save(fixture, 10);
        var legacy = Save(fixture, 20);
        service.UpdateAchievementBaseline(original);
        service.UpdateAchievementBaseline(legacy);
        fixture.Database.RunInTransaction(tx =>
        {
            using var command = fixture.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM ipc_idempotency WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", legacy.RequestId);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "CREATE TRIGGER fail_baseline_expiry BEFORE UPDATE OF response_json ON ipc_idempotency " +
                $"WHEN OLD.request_id = '{original.RequestId}' BEGIN SELECT RAISE(ABORT, 'expiry blocked for rollback regression'); END;";
            command.ExecuteNonQuery();
        });
        var priorSettings = settings.GetAchievementSettings();
        var priorAudit = settings.ReadBaselineAudit().ToArray();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        var failed = Save(fixture, 30);
        Assert.Throws<SqliteException>(() => service.UpdateAchievementBaseline(failed));
        Assert.Equal(priorSettings, settings.GetAchievementSettings());
        Assert.Equal(priorAudit, settings.ReadBaselineAudit());
        var repository = new IdempotencyRepository(fixture.Database, fixture.Clock);
        Assert.NotNull(repository.TryGetResponse(original.RequestId));
        Assert.Null(repository.TryGetResponse(failed.RequestId));
        Assert.False(repository.WasAppliedBeforePrune(failed.RequestId));
        using var lookup = fixture.Database.CreateCommand();
        lookup.CommandText = "SELECT COUNT(*) FROM ipc_idempotency WHERE request_id = $id;";
        lookup.Parameters.AddWithValue("$id", legacy.RequestId);
        Assert.Equal(0L, lookup.ExecuteScalar());
    }
    [Fact]
    public void PruningAndReceiptInsertionRollBackTogether()
    {
        using var fixture = new TestDatabase();
        var repository = new IdempotencyRepository(fixture.Database, fixture.Clock);
        var oldId = Id();
        fixture.Database.RunInTransaction(tx => repository.Store(oldId, "UpdateAchievementBaseline", "{\"ok\":true}", tx));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        var failedId = Id();
        Assert.Throws<InvalidOperationException>(() => fixture.Database.RunInTransaction(tx =>
        {
            repository.Store(failedId, "UpdateAchievementBaseline", "{}", tx);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal("{\"ok\":true}", repository.TryGetResponse(oldId));
        Assert.Null(repository.TryGetResponse(failedId));
        Assert.False(repository.WasAppliedBeforePrune(failedId));
    }
}

public sealed class ImportCompleteDateRegressionTests
{
    public static IEnumerable<object[]> IncompleteTimestamps()
    {
        foreach (var source in new[] { "ROWS", "CSV" })
        foreach (var timestamp in new[] { "09:00Z", "09:00+08:00", "09:00-05:30", "10-11 09:00Z", "10/11 09:00+08:00", "2026-10 09:00Z" })
            yield return new object[] { source, "entered_at_utc", timestamp };
        foreach (var field in new[] { "ended_at_utc", "matched_at_utc", "created_at_utc", "source_recorded_at", "source_recorded_at_utc" })
            yield return new object[] { "ROWS", field, "09:00Z" };
    }

    [Theory]
    [MemberData(nameof(IncompleteTimestamps))]
    public void MissingCalendarDateIsRejectedBeforeCommitAndCannotContributeToStatistics(string source, string field, string timestamp)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var input = new JsonObject
        {
            ["duty_name"] = "日期缺失回归", ["result"] = "COMPLETED",
            ["entered_at_utc"] = "2026-10-11T09:00:00Z", ["ended_at_utc"] = "2026-10-11T09:30:00Z",
        };
        input[field] = timestamp;
        var preview = source == "CSV"
            ? service.PreviewSource("CSV", text: string.Join(',', input.Select(pair => pair.Key)) + "\n" + string.Join(',', input.Select(pair => pair.Value!.GetValue<string>())) + "\n")
            : service.PreviewSource("ROWS", rows: new JsonArray(input));
        var row = preview["rows"]![0]!.AsObject();
        Assert.False(row["can_import"]!.GetValue<bool>());
        Assert.NotEmpty(row["errors"]!.AsArray());
        Assert.Null(row["run"]);
        Assert.Throws<CollectorException>(() => service.Commit(preview["preview_id"]!.GetValue<string>(),
            new[] { row["row_number"]!.GetValue<int>() }, true, Guid.NewGuid().ToString("D")));
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var stats = new StatisticsRepository(fixture.Database, settings).GetDashboard();
        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal(0, stats.AchievementProgress);
    }

    [Theory]
    [InlineData("2026-10-11T09:00:00Z", "2026-10-11T09:00:00.000Z")]
    [InlineData("2026-10-11 09:00Z", "2026-10-11T09:00:00.000Z")]
    [InlineData("2026-10-11T09:00:00.1234567+08:00", "2026-10-11T01:00:00.123Z")]
    [InlineData("2026/10/11 9:00+08:00", "2026-10-11T01:00:00.000Z")]
    [InlineData("2026-10-11T09:00:00-05:30", "2026-10-11T14:30:00.000Z")]
    public void CompleteDateAndOffsetKeepTheirExplicitInstant(string timestamp, string expected)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "明确日期回归", ["result"] = "UNKNOWN", ["entered_at_utc"] = timestamp,
        }));
        var row = preview["rows"]![0]!.AsObject();
        Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Empty(row["errors"]!.AsArray());
        Assert.Equal(expected, row["run"]!["entered_at_utc"]!.GetValue<string>());
    }
}