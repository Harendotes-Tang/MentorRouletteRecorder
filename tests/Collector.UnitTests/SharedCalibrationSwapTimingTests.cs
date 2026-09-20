using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// When a shared code that reads the server's own match takes over from a queue-inferred profile that is
/// already recording (1.4.0-beta.1, real machine): inside the capture session, between two runs, never
/// mid-run, never at the cost of a run the player is already queued for.
/// </summary>
public sealed class SharedCalibrationSwapTimingTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    /// <summary>
    /// The evening of the report. Evidence had been wiped and capture began after the login burst, so the
    /// first burst holding both the zone marker and the territory is the first duty entry: the required
    /// criterion passes mid-run. Two mentor roulettes follow each other with fifteen seconds between them.
    /// </summary>
    private static DecodedMessage[] ReportedEvening() => Marked(
        CalibrationObserverTests.Noise(1_000, 100_000)
            .Concat(CalibrationObserverTests.QueueAndPop(100_000, 9, 116_000))
            .Concat(CalibrationObserverTests.Noise(101_000, 124_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Noise(130_000, 210_000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000))
            .Concat(CalibrationObserverTests.Noise(220_000, 230_000))
            .Concat(CalibrationObserverTests.QueueAndPop(230_000, 9, 247_000))
            .Concat(CalibrationObserverTests.Noise(231_000, 253_000))
            .Concat(CalibrationObserverTests.Cluster(254_000, 1039))
            .Concat(CalibrationObserverTests.Noise(260_000, 400_000))
            .Concat(CalibrationObserverTests.Cluster(405_000, 5000))
            .Concat(CalibrationObserverTests.Noise(410_000, 430_000)));

    private static DecodedMessage[] Marked(IEnumerable<DecodedMessage> traffic)
    {
        byte marker = 1;
        return traffic.OrderBy(message => message.Mono).Select(message =>
        {
            if (message.Opcode is not (CalibrationTrafficCases.ZoneInit or CalibrationTrafficCases.Territory or CalibrationTrafficCases.Reply or CalibrationTrafficCases.Request))
            {
                return message;
            }

            var payload = message.Payload.ToArray();
            payload[^1] = marker++;
            return message with { Payload = payload };
        }).ToArray();
    }

    /// <summary>Feeds one message at a time and lets background work land after each: a bind that takes no time at all.</summary>
    private static async Task FeedSettling(LiveProtocolPipeline pipeline, string session, IEnumerable<DecodedMessage> traffic)
    {
        foreach (var message in traffic)
        {
            Bed.Feed(pipeline, session, new[] { message });
            await Bed.Idle(pipeline);
        }
    }

    /// <summary>Weak, not wrong: no record is marked the way a withdrawn calibration marks what it recorded.</summary>
    private void AssertNeverFlagged(IEnumerable<MentorRun> runs)
    {
        var revisions = new RunRevisionRepository(_bed.Db.Database);
        Assert.All(runs, run => Assert.DoesNotContain(
            revisions.ListForRun(run.RunId, 1, 50).Items, revision => revision.Reason?.Contains("校准", StringComparison.Ordinal) == true));
    }

    private LiveProtocolPipeline QueueInferredLocalWithBetterCodePublished(LiveEventBus? bus = null)
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services(), bus);
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        return pipeline;
    }

    [Fact]
    public async Task ACodeVerifiedMidRunIsSwappedInBetweenTheTwoRunsOfTheSameSession()
    {
        var pipeline = QueueInferredLocalWithBetterCodePublished();
        await Bed.Idle(pipeline);
        var evening = ReportedEvening();
        var session = _bed.Start(pipeline);

        // Through the first duty: recorded by the queue-inferred profile, the swap held back while it runs.
        await FeedSettling(pipeline, session, Bed.Before(evening, 214_000));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);

        // The run ends; fifteen idle seconds later the player queues again.
        await FeedSettling(pipeline, session, Bed.From(Bed.Before(evening, 229_000), 214_000));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.NotNull(pipeline.CalibrationStatus().Shared.BoundAtUtc);

        await FeedSettling(pipeline, session, Bed.From(evening, 229_000));

        var runs = new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items.OrderBy(run => run.MatchedAtUtc).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.NotNull(run.EnteredAtUtc));
        Assert.All(runs, run => Assert.NotNull(run.EndedAtUtc));
        AssertNeverFlagged(runs);
        // The second run was opened by the popup itself, seventeen seconds after the request.
        Assert.Equal(evening.Single(message => message.Opcode == CalibrationTrafficCases.Reply && message.Mono == TimeSpan.FromMilliseconds(247_000)).ObservedAtUtc, runs[1].MatchedAtUtc);
        Assert.NotEqual(runs[0].ProtocolProfileId, runs[1].ProtocolProfileId);
    }

    [Fact]
    public async Task ASwapThatLandsWhileThePlayerIsAlreadyQueuedStillRecordsTheRun()
    {
        var pipeline = QueueInferredLocalWithBetterCodePublished();
        await Bed.Idle(pipeline);
        var evening = ReportedEvening();
        var session = _bed.Start(pipeline);

        // The bind is slow: the profile is written when the first run ends, but the reload behind it lands
        // only after the player has queued again and the popup has gone by.
        await FeedSettling(pipeline, session, Bed.Before(evening, 214_000));
        using var release = new ManualResetEventSlim(false);
        _bed.BeforeReload = () => release.Wait(TimeSpan.FromSeconds(30));
        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 250_000), 214_000));
        release.Set();
        await Bed.Idle(pipeline);
        _bed.BeforeReload = null;

        // Selected, but the machine that holds the player's queue request is left alone until that run is over.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Null(pipeline.CalibrationStatus().Shared.BoundAtUtc);

        await FeedSettling(pipeline, session, Bed.From(Bed.Before(evening, 400_000), 250_000));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Null(pipeline.CalibrationStatus().Shared.BoundAtUtc);

        await FeedSettling(pipeline, session, Bed.From(evening, 400_000));
        var runs = new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items.OrderBy(run => run.MatchedAtUtc).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.NotNull(run.EnteredAtUtc));
        Assert.All(runs, run => Assert.NotNull(run.EndedAtUtc));
        AssertNeverFlagged(runs);
        // Owed, not forgotten: it took over the moment that run ended, in this same session.
        Assert.NotNull(pipeline.CalibrationStatus().Shared.BoundAtUtc);
    }

    /// <summary>
    /// First run with nothing in force: the code binds when the duty's own loading burst completes the
    /// required criterion, and the staged popup and the staged entry reach the desktop back to back. A popup
    /// that is nine seconds old is history, not news - only an explicit <c>match_from_queue: false</c> is
    /// spoken, so the replayed match leaves the field out. The entry, and the next live popup, still speak.
    /// </summary>
    [Fact]
    public async Task APopupReplayedFromStagingIsNotAnnouncedAsAFreshMatch()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = _bed.Pipeline(_bed.Services(), bus);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Refresh(Bed.Game()).Status);
        await Bed.Idle(pipeline);
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        var evening = ReportedEvening();
        var session = _bed.Start(pipeline);

        await FeedSettling(pipeline, session, Bed.Before(evening, 214_000));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        var replayed = await StateChanges(live);
        var stale = Assert.Single(replayed, payload => payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");
        Assert.Null(stale["match_from_queue"]);
        Assert.NotNull(stale["run"]);
        Assert.Contains(replayed, payload => payload["state"]!.GetValue<string>() == "ENTERED_DUTY");

        await FeedSettling(pipeline, session, Bed.From(evening, 214_000));
        var fresh = Assert.Single(await StateChanges(live), payload => payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");
        Assert.False(fresh["match_from_queue"]!.GetValue<bool>());
        Assert.Equal(2, new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items.Count);
    }

    private static async Task<IReadOnlyList<JsonObject>> StateChanges(LiveEventSubscription subscription)
    {
        var events = new List<JsonObject>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None) is { } payload)
        {
            if (payload["kind"]?.GetValue<string>() == LiveEventBus.KindToken(LiveEventKind.RunStateChanged))
            {
                events.Add(payload);
            }
        }

        return events;
    }
}
