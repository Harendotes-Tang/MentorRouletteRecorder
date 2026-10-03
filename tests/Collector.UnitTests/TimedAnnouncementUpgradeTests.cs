using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed =MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

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
    /// Audit 2026-10-03 ODp-1. The player queues for the mentor roulette and confirms the card while
    /// waiting. The confirmation rewrites the profile in force, and rebuilding the machine there and
    /// then threw away the queue request it had parked: the announcement and the duty that followed
    /// were ignored and the run was never recorded. The machine holding the request stays until that
    /// run is over, and the rewritten profile takes over then, in the same session.
    /// </summary>
    [Fact]
    public async Task AConfirmationWhileQueuedKeepsTheRequestAndRebindsOnceTheRunIsOver()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot), bus);
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1) });
        var confirmation = pipeline.ConfirmCalibration(AllCorrect(offered));
        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        Assert.False(confirmation.BoundInSession);

        Bed.Feed(pipeline, session, new[] { Announcement(610_000) }
            .Concat(Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));
        var run = Assert.Single(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(MentorRoulette, run.MentorRouletteId);
        Assert.NotNull(run.EnteredAtUtc);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        // The duty ends; from the next queue on the rewritten profile is in force and the
        // announcement is understood the moment it arrives.
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, Marked(CalibrationObserverTests.Cluster(700_000, TownTerritory), 10)
            .Append(MentorRequest(800_000, marker: 2))
            .Append(Announcement(810_000))
            .Concat(Marked(CalibrationObserverTests.Cluster(820_000, DutyTerritory, job: 24), 20)));

        var matched = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed" &&
            payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        Assert.Equal(2, new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items.Count);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    private const int TownTerritory = 5000;

    private static DecodedMessage MentorRequest(long t, byte marker) => CalibrationObserverTests.Message(
        MessageDirection.Outbound, CalibrationTrafficCases.Request,
        CalibrationObserverTests.Bytes(24, (0, MentorRoulette), (23, marker)), t);

    /// <summary>
    /// Gives every zone marker and territory its own last byte: the state machine ignores an event
    /// whose payload it has already seen in the session, and a burst helper repeats its payloads.
    /// </summary>
    private static IEnumerable<DecodedMessage> Marked(IEnumerable<DecodedMessage> traffic, byte first)
    {
        var marker = first;
        foreach (var message in traffic)
        {
            if (message.Opcode is CalibrationTrafficCases.ZoneInit or CalibrationTrafficCases.Territory)
            {
                var payload = message.Payload.ToArray();
                payload[^1] = marker++;
                yield return message with { Payload = payload };
            }
            else
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// Audit 2026-10-03, OCal-3. A share code never carries the announcement - its evidence is the
    /// sender's timing, not the receiver's - so a queue-inferred profile that arrived as a code is
    /// promised to find its own on the receiving machine. The offer was made only to profiles this
    /// machine wrote, and the draft that found it stayed hidden for the life of the build. Confirming
    /// it writes a local profile, which outranks the shared one, and between runs the session takes
    /// it over at once, as it does when a local profile is rewritten.
    /// </summary>
    [Fact]
    public async Task ASharedInferredProfileIsOfferedTheAnnouncementThisMachineFound()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>(), Bed.Confirmed),
            _bed.SharedRoot);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot), bus);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));

        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Assert.Contains(offered.Events, item => item.Label.Contains("按出现时机认出", StringComparison.Ordinal));

        var confirmation = pipeline.ConfirmCalibration(AllCorrect(offered));

        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        Assert.True(confirmation.BoundInSession);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1), Announcement(610_000) }
            .Concat(Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));

        var matched = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed" &&
            payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        var run = Assert.Single(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(confirmation.ProfileId, run.ProtocolProfileId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The same takeover confirmed while the player is queued for the mentor roulette: the shared
    /// profile's machine holds the request and records that run to its end, and the local profile
    /// takes over between runs.
    /// </summary>
    [Fact]
    public async Task ASharedInferredProfileConfirmedWhileQueuedHandsOverOnceTheRunIsOver()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>(), Bed.Confirmed),
            _bed.SharedRoot);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot), bus);
        var shared = pipeline.Refresh(Bed.Game()).ProfileId;
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1) });
        var confirmation = pipeline.ConfirmCalibration(AllCorrect(offered));
        Assert.False(confirmation.BoundInSession);

        Bed.Feed(pipeline, session, new[] { Announcement(610_000) }
            .Concat(Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));
        var first = Assert.Single(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(shared, first.ProtocolProfileId);
        Assert.NotNull(first.EnteredAtUtc);

        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, Marked(CalibrationObserverTests.Cluster(700_000, TownTerritory), 10)
            .Append(MentorRequest(800_000, marker: 2))
            .Append(Announcement(810_000))
            .Concat(Marked(CalibrationObserverTests.Cluster(820_000, DutyTerritory, job: 24), 20)));

        var matched = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed" &&
            payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        Assert.Contains(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items,
            item => string.Equals(item.ProtocolProfileId, confirmation.ProfileId, StringComparison.Ordinal));
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
