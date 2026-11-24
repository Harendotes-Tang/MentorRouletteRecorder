using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
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
    private const int TownTerritory = 5000;

    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    /// <summary>A live pipeline over the bed's roots with the local-profile seams wired.</summary>
    private LiveProtocolPipeline Pipeline(LiveEventBus? bus = null) =>
        _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot), bus);

    /// <summary>
    /// An evening in which the announcement can be recognised: two duties, two roulettes, the same shape
    /// before each of them, and noise after it so the last burst closes.
    /// </summary>
    private static void PlayTheAnnouncedEvening(LiveProtocolPipeline pipeline, string session) =>
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(CalibrationObserverTests.Noise(470_000, 490_000)));

    /// <summary>The one live event that says a mentor match was observed.</summary>
    private static JsonObject Matched(IEnumerable<JsonObject> payloads) => Assert.Single(payloads, payload =>
        payload["kind"]!.GetValue<string>() == "run_state_changed" &&
        payload["state"]!.GetValue<string>() == "MENTOR_MATCHED");

    private static DecodedMessage Announcement(long t) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, Announce, new byte[CalibrationTrafficCases.AnnouncedLength], t);

    private static DecodedMessage MentorRequest(long t, byte marker) => CalibrationObserverTests.Message(
        MessageDirection.Outbound, CalibrationTrafficCases.Request,
        CalibrationObserverTests.Bytes(24, (0, MentorRoulette), (23, marker)), t);

    [Fact]
    public async Task AnInferredProfileGainsTheAnnouncementAndPublishesTheNextMatchAsObserved()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = Pipeline(bus);
        pipeline.Refresh(Bed.Game());
        Assert.DoesNotContain("MATCH_ANNOUNCED", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        var session = _bed.Start(pipeline);

        PlayTheAnnouncedEvening(pipeline, session);

        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Assert.Contains(offered.Events, item => item.Label.Chinese.Contains("按时间推测", StringComparison.Ordinal));

        pipeline.ConfirmCalibration(Bed.AllCorrect(offered));

        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
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

        var states = (await live.DrainAsync())
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfirmationWhileQueuedKeepsTheRequestAndRebindsOnceTheRunIsOver(bool failFirstRebind)
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = Pipeline(bus);
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        PlayTheAnnouncedEvening(pipeline, session);
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1) });
        var confirmation = pipeline.ConfirmCalibration(Bed.AllCorrect(offered));
        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        Assert.False(confirmation.BoundInSession);

        Bed.Feed(pipeline, session, new[] { Announcement(610_000) }
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));
        var run = Assert.Single(_bed.AllRuns());
        Assert.Equal(MentorRoulette, run.MentorRouletteId);
        Assert.NotNull(run.EnteredAtUtc);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        // The duty ends; from the next queue on the rewritten profile is in force and the
        // announcement is understood the moment it arrives.
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        if (failFirstRebind)
        {
            using var reject = _bed.Db.Database.CreateCommand();
            reject.CommandText = "CREATE TEMP TRIGGER reject_profile_update BEFORE UPDATE OF protocol_profile_id ON capture_sessions " +
                "BEGIN SELECT RAISE(ABORT, 'transient profile update failure'); END;";
            reject.ExecuteNonQuery();
        }

        Bed.Feed(pipeline, session, Bed.Marked(CalibrationObserverTests.Cluster(700_000, TownTerritory), 10));
        if (failFirstRebind)
        {
            using var recover = _bed.Db.Database.CreateCommand();
            recover.CommandText = "DROP TRIGGER reject_profile_update;";
            recover.ExecuteNonQuery();
            Bed.Feed(pipeline, session, CalibrationObserverTests.Noise(750_000, 760_000));
        }

        Bed.Feed(pipeline, session, new[] { MentorRequest(800_000, marker: 2) }
            .Append(Announcement(810_000))
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(820_000, DutyTerritory, job: 24), 20)));

        var matched = Matched(await live.DrainAsync());
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        Assert.Equal(2, _bed.AllRuns().Count);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The same owed rebind, on a duty that ends with the CN clear. The clear completes the run and leaves the
    /// queue-inferred machine between runs, so the rebind settles at the clear itself, before the player has left.
    /// The exit then reaches the new machine, which has no run and no parked request, and creates nothing; the
    /// next queue still opens a run, now from the announcement. The session's clear counts survive the rebind,
    /// and no exit line is written for a zone change the new parser saw no clear before (review of the
    /// duty-clear feature, L2 and L5).
    /// </summary>
    [Fact]
    public void AnOwedRebindThatSettlesAtTheClearKeepsTheCountsAndTheExitCreatesNothing()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var pipeline = Pipeline();
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        PlayTheAnnouncedEvening(pipeline, session);
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1) });
        Assert.False(pipeline.ConfirmCalibration(Bed.AllCorrect(offered)).BoundInSession);
        Bed.Feed(pipeline, session, new[] { Announcement(610_000) }
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        Bed.Feed(pipeline, session, new[] { CalibrationTrafficCases.Clear(686_000) });
        var cleared = Assert.Single(_bed.AllRuns());
        Assert.Equal(RunResult.Completed, cleared.Result);
        Assert.False(cleared.PendingReview);

        Bed.Feed(pipeline, session, Bed.Marked(CalibrationObserverTests.Cluster(700_000, TownTerritory), 10));
        Assert.Single(_bed.AllRuns());
        Assert.Equal(RunState.Idle, pipeline.RunState);

        var announcement = Announcement(810_000);
        Bed.Feed(pipeline, session, new[] { MentorRequest(800_000, marker: 2), announcement }
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(820_000, DutyTerritory, job: 24), 20)));
        var runs = _bed.AllRuns();
        Assert.Equal(2, runs.Count);
        var next = Assert.Single(runs, run => run.RunId != cleared.RunId);
        // Opened at the announcement, which only the rewritten profile understands: the rebind took place.
        Assert.Equal(announcement.ObservedAtUtc, next.MatchedAtUtc);
        Assert.NotNull(next.EnteredAtUtc);

        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
        var signal = Assert.Single(notes);
        Assert.Equal(DutyClearNoteKind.Signal, signal.Kind);
        Assert.True(signal.Completed);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
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
        _bed.WriteSharedProfile(code.Payload, Bed.Confirmed);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = Pipeline(bus);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        var session = _bed.Start(pipeline);

        PlayTheAnnouncedEvening(pipeline, session);

        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);
        Assert.Contains(offered.Events, item => item.Label.Chinese.Contains("按时间推测", StringComparison.Ordinal));

        var confirmation = pipeline.ConfirmCalibration(Bed.AllCorrect(offered));

        Assert.Contains("MATCH_ANNOUNCED", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        Assert.True(confirmation.BoundInSession);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1), Announcement(610_000) }
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));

        var matched = Matched(await live.DrainAsync());
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        var run = Assert.Single(_bed.AllRuns());
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
        _bed.WriteSharedProfile(code.Payload, Bed.Confirmed);
        var bus = new LiveEventBus(_bed.Db.Clock);
        var pipeline = Pipeline(bus);
        var shared = pipeline.Refresh(Bed.Game()).ProfileId;
        var session = _bed.Start(pipeline);
        PlayTheAnnouncedEvening(pipeline, session);
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        Bed.Feed(pipeline, session, new[] { MentorRequest(600_000, marker: 1) });
        var confirmation = pipeline.ConfirmCalibration(Bed.AllCorrect(offered));
        Assert.False(confirmation.BoundInSession);

        Bed.Feed(pipeline, session, new[] { Announcement(610_000) }
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(620_000, DutyTerritory, job: 24), 1)));
        var first = Assert.Single(_bed.AllRuns());
        Assert.Equal(shared, first.ProtocolProfileId);
        Assert.NotNull(first.EnteredAtUtc);

        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Bed.Feed(pipeline, session, Bed.Marked(CalibrationObserverTests.Cluster(700_000, TownTerritory), 10)
            .Append(MentorRequest(800_000, marker: 2))
            .Append(Announcement(810_000))
            .Concat(Bed.Marked(CalibrationObserverTests.Cluster(820_000, DutyTerritory, job: 24), 20)));

        var matched = Matched(await live.DrainAsync());
        Assert.False(matched["match_from_queue"]!.GetValue<bool>());
        Assert.Contains(_bed.AllRuns(),
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
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequestAnnounced);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        PlayTheAnnouncedEvening(pipeline, session);

        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }
}
