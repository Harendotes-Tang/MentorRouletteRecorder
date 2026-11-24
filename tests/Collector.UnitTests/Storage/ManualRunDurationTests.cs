using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>The duration of a manual run: stored as given, derived from its times, or left unknown.</summary>
public sealed class ManualRunDurationTests
{
    private static JsonObject Payload() => new()
    {
        ["reason"] = "补录遗漏记录",
        ["region"] = "CN",
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
        var settings = db.Settings();
        var service = db.Mutations();
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
        var service = db.Mutations();
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
        var settings = db.Settings();
        var service = db.Mutations();
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

    [Theory]
    [InlineData("note")]
    [InlineData("job_id")]
    [InlineData("contributes_to_goal")]
    [InlineData("matched_at_utc")]
    [InlineData("same_end_time")]
    public void UnrelatedCorrectionsPreserveTheRecordedDuration(string field)
    {
        using var db = new TestDatabase();
        var service = db.Mutations();
        var created = CreateWithDuration(service, db);
        var fields = new HashSet<string> { RunFields.Note };
        var changes = new RunChangeSet { Specified = fields, Note = "只补充记录说明" };
        switch (field)
        {
            case "job_id":
                fields.Add(RunFields.JobId);
                changes = changes with { JobId = 24 };
                break;
            case "contributes_to_goal":
                fields.Add(RunFields.ContributesToGoal);
                changes = changes with { ContributesToGoal = false };
                break;
            case "matched_at_utc":
                fields.Add(RunFields.MatchedAtUtc);
                changes = changes with { MatchedAtUtc = db.Clock.UtcNow.AddSeconds(-10) };
                break;
            case "same_end_time":
                fields.Add(RunFields.EndedAtUtc);
                changes = changes with { EndedAtUtc = created.Run!.EndedAtUtc };
                break;
        }

        var corrected = service.CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"), created.RunId, created.Revision, "补充说明", changes));

        Assert.Equal(30_000L, corrected.Run!.DurationMs);
        var revision = new RunRevisionRepository(db.Database)
            .GetAt(created.RunId, corrected.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.DurationMs);
    }

    [Theory]
    [InlineData(false, 90_000L)]
    [InlineData(true, 12_000L)]
    public void ChangedDurationEndpointsRecomputeUnlessAnExplicitDurationWins(bool explicitDuration, long expected)
    {
        using var db = new TestDatabase();
        var service = db.Mutations();
        var created = CreateWithDuration(service, db);
        var fields = new HashSet<string> { RunFields.EndedAtUtc };
        if (explicitDuration) fields.Add(RunFields.DurationMs);
        var corrected = service.CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"), created.RunId, created.Revision, "更正结束时间", new RunChangeSet
            {
                Specified = fields,
                EndedAtUtc = db.Clock.UtcNow.AddSeconds(90),
                DurationMs = 12_000,
            }));

        Assert.Equal(expected, corrected.Run!.DurationMs);
    }

    /// <summary>
    /// A CN completion entered now and ended a minute later whose recorded duration, 30 s, is not the
    /// span of its times, so a recomputation shows.
    /// </summary>
    private static RunMutationOutcome CreateWithDuration(RunMutationService service, TestDatabase db) =>
        service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"), Reason = "回归记录", Result = RunResult.Completed,
            Region = Region.Cn, EnteredAtUtc = db.Clock.UtcNow, EndedAtUtc = db.Clock.UtcNow.AddSeconds(60),
            DurationMs = 30_000,
        });
}
