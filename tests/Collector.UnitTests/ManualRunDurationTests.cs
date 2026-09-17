using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ManualRunDurationTests
{
    private static JsonObject Payload() => new()
    {
        ["reason"] = "补录遗漏记录",
        ["result"] = "COMPLETED",
        ["matched_at_utc"] = "2026-09-13T12:00:00.000Z",
        ["entered_at_utc"] = "2026-09-13T12:00:00.000Z",
        ["ended_at_utc"] = "2026-09-13T12:30:00.000Z",
    };

    [Theory]
    [InlineData(false, null, 1_800_000L)]
    [InlineData(true, null, null)]
    [InlineData(true, 0L, 0L)]
    [InlineData(true, 120_000L, 120_000L)]
    public void CreateDurationPresenceSurvivesParsingStorageAndStatistics(
        bool specified, long? duration, long? expected)
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(db.Database, settings, db.Clock);
        var payload = Payload();
        if (specified) payload["duration_ms"] = duration;
        var command = RequestParsers.CreateManualRun(Guid.NewGuid().ToString("D"), new PayloadReader(payload));

        var created = service.CreateManualRun(command);
        var replay = service.CreateManualRun(command);

        Assert.Equal(expected, new RunRepository(db.Database).Get(created.RunId)!.DurationMs);
        var statistics = new StatisticsRepository(db.Database, settings).GetDashboard();
        Assert.Equal(expected is { } value ? (double?)value : null, statistics.AverageDurationMs);
        Assert.Equal(1, statistics.AchievementProgress);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(created.RunId, replay.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OmittedAndExplicitNullDurationsAreDifferentIdempotentRequests(bool nullFirst)
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(db.Database, settings, db.Clock);
        var payload = Payload();
        var requestId = Guid.NewGuid().ToString("D");
        if (nullFirst) payload["duration_ms"] = null;
        service.CreateManualRun(RequestParsers.CreateManualRun(requestId, new PayloadReader(payload)));
        if (nullFirst) payload.Remove("duration_ms");
        else payload["duration_ms"] = null;

        var error = Assert.Throws<CollectorException>(() => service.CreateManualRun(
            RequestParsers.CreateManualRun(requestId, new PayloadReader(payload))));

        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
    }

    [Fact]
    public void AnExplicitUnknownDurationStaysOutOfStatisticsWhenTheEndIsCorrected()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(db.Database, settings, db.Clock);
        var payload = Payload();
        payload["duration_ms"] = null;
        var created = service.CreateManualRun(RequestParsers.CreateManualRun(
            Guid.NewGuid().ToString("D"), new PayloadReader(payload)));
        var changes = RequestParsers.Changes(new PayloadReader(new JsonObject
        {
            ["ended_at_utc"] = "2026-09-13T12:31:00.000Z",
            ["duration_ms"] = null,
        }));

        service.CorrectRun(new CorrectRunCommand(Guid.NewGuid().ToString("D"), created.RunId,
            created.Revision, "修正结束时间", changes));

        Assert.Null(new RunRepository(db.Database).Get(created.RunId)!.DurationMs);
        Assert.Null(new StatisticsRepository(db.Database, settings).GetDashboard().AverageDurationMs);
    }
}
