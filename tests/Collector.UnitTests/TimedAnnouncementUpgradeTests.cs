using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The player's own machine on CN 2026.09.15, one build later than the job upgrade: a
/// queue-inferred profile is in force and records correctly, and the popup still appears in
/// silence because the profile has nothing that fires when it does.
///
/// Once the timing evidence names the announcement, the draft underneath carries a message the
/// profile lacks, so the same mechanism that offered the job offers this - and from the next
/// mentor queue on, the moment the server announces the match is published as an observed match,
/// which is what makes the desktop speak.
/// </summary>
public sealed class TimedAnnouncementUpgradeTests : IDisposable
{
    private const ushort Announce = CalibrationTrafficCases.Announce;
    private const byte MentorRoulette = 9;
    private const int DutyTerritory = 1039;

    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private string ProfilePath => LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build);

    private static IReadOnlyDictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    private static DecodedMessage Announcement(long t) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, Announce, new byte[CalibrationTrafficCases.AnnouncedLength], t);

    private static async Task<JsonObject[]> DrainAsync(LiveEventSubscription subscription)
    {
        var payloads = new List<JsonObject>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None)
            is { } payload)
        {
            payloads.Add(payload);
        }

        return payloads.ToArray();
    }

    [Fact]
    public async Task AnInferredProfileGainsTheAnnouncementAndPublishesTheNextMatchAsObserved()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot), bus);
        pipeline.Refresh(Bed.Game());
        Assert.DoesNotContain("MATCH_ANNOUNCED", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        var session = _bed.Start(pipeline);

        // An evening in which the announcement can be recognised: two duties, two roulettes,
        // the same shape before each of them.
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));

        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Assert.Contains(offered.Events, item => item.Label.Contains("按出现时机认出", StringComparison.Ordinal));

        pipeline.ConfirmCalibration(AllCorrect(offered));

        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, new[]
        {
            // The player queues for the mentor roulette, and this time the server's
            // announcement is understood the moment it arrives.
            CalibrationObserverTests.Message(MessageDirection.Outbound, CalibrationTrafficCases.Request,
                CalibrationObserverTests.Bytes(24, (0, MentorRoulette)), 600_000),
            Announcement(610_000),
        }.Concat(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24)));

        var states = (await DrainAsync(live))
            .Where(payload => payload["kind"]!.GetValue<string>() == "run_state_changed")
            .ToArray();
        var matched = Assert.Single(states, payload => payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");

        // The one thing the desktop reads to decide whether to speak: this match was observed,
        // not inferred from the request.
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());

        // And the duty that follows still enters, still inferred like every other state.
        var entered = Assert.Single(states, payload => payload["state"]!.GetValue<string>() == "ENTERED_DUTY");
        Assert.True(entered["match_from_queue"]!.GetValue<bool>());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// A profile that already has the announcement is not asked about it again: the draft
    /// underneath keeps naming the same inferred match and the same message, and interrupting
    /// the player to confirm what they confirmed once is the failure this mechanism was built
    /// to avoid in the first place.
    /// </summary>
    [Fact]
    public void AProfileThatAlreadyHasTheAnnouncementIsNotAskedAgain()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequestAnnounced), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot));
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));

        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }
}
