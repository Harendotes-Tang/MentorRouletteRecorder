using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A local profile without PLAYER_JOB gains it.
///
/// The player's own machine on CN 2026.09.15: a queue-inferred profile was in force without
/// PLAYER_JOB, because the job rule could not name the message when it was written. Once the rule
/// can, the draft underneath is the same inferred match plus the job - and a draft that repeats
/// the inferred match was never offered again, so the profile could never gain the job and every
/// record stayed 职业未知 for the life of the build.
///
/// The job upgrade of 1.2.3 reaches only a profile that infers the match from the queue, because
/// that is the one calibration keeps running beside. A machine whose profile reads the server's
/// own announcement finished calibrating: the card is gone, nothing observes any more, and a
/// profile written before the job rule could name the message records 职业未知 for the whole
/// life of the build with no way back in.
///
/// So calibration stays armed beside such a profile too - silently, because there is nothing to
/// nag about and recording already works - and offers itself exactly once: when a later draft
/// carries the job AND agrees with the profile in force about every other message it declares.
/// Anything else stays hidden, because replacing a working match message behind the player's
/// back is worse than 职业未知.
/// </summary>
public sealed class LocalProfileJobCompletionTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    /// <summary>A live pipeline over the bed's roots with the local-profile seams wired.</summary>
    private Protocol.Pipeline.LiveProtocolPipeline Pipeline() =>
        _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot));

    /// <summary>An evening's traffic, padded so the last burst closes while the session runs on.</summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    private static IEnumerable<DecodedMessage> Evening(string trafficCase) =>
        CalibrationTrafficCases.Traffic(trafficCase).Concat(CalibrationObserverTests.Noise(360_000, 400_000));

    // ------------------------------------------------------------------ a queue-inferred profile

    [Fact]
    public void AnInferredProfileWithoutTheJobIsOfferedTheJobAndUsesItAtOnce()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequestNoJob);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(CalibrationObserverTests.Noise(250_000, 290_000)));

        // The same inferred match, but it brings something the profile in force lacks.
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        var result = pipeline.ConfirmCalibration(Bed.AllCorrect(offered));

        Assert.Contains("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        // Bound in the running session, not at the next launch: a mentor run queued now has the job.
        Bed.Feed(pipeline, session, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Outbound, CalibrationTrafficCases.Request,
                CalibrationObserverTests.Bytes(24, (0, 9)), 400_000),
        }.Concat(CalibrationObserverTests.Cluster(420_000, 1039, job: 24)));

        var run = _bed.AllRuns()
            .Single(item => string.Equals(item.ProtocolProfileId, result.ProfileId, StringComparison.Ordinal) &&
                            item.EnteredAtUtc is not null);
        Assert.Equal(24, run.JobId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>A profile that already has the job is not asked about again for an identical draft.</summary>
    [Fact]
    public void AnInferredProfileThatAlreadyHasTheJobIsNotAskedAgain()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(CalibrationObserverTests.Noise(250_000, 290_000)));

        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    // ------------------------------------------------------------------ a profile that reads the announcement

    [Fact]
    public void AFinishedProfileWithoutTheJobIsOfferedTheJobAndUsesItAtOnce()
    {
        _bed.WriteLocalProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, Evening(CalibrationTrafficCases.Announcement));

        // The same match the profile already reads, plus the job it never had.
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        var result = pipeline.ConfirmCalibration(Bed.AllCorrect(offered));

        Assert.Contains("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        Assert.NotEqual(CalibrationState.Ready, pipeline.CalibrationStatus().State);

        // Bound in the running session, not at the next launch: the next mentor run has the job.
        Bed.Feed(pipeline, session, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, CalibrationTrafficCases.Announce,
                CalibrationObserverTests.Bytes(64, (16, 9)), 500_000),
        }.Concat(CalibrationObserverTests.Cluster(520_000, 1039, job: 24)));

        var run = _bed.AllRuns()
            .Single(item => string.Equals(item.ProtocolProfileId, result.ProfileId, StringComparison.Ordinal) &&
                            item.EnteredAtUtc is not null && item.MentorRouletteId == 9);
        Assert.Equal(24, run.JobId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Audit 2026-11-03, C-3. The completing role starts in the run that wrote a profile without the
    /// job, not only after the Collector restarts: with login autostart into the tray a restart can be
    /// days away, and every record meanwhile says 职业未知. Right after the confirmation the card still
    /// says calibration is done.
    /// </summary>
    [Fact]
    public void AProfileConfirmedWithoutTheJobIsCompletedWithoutARestart()
    {
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        Assert.True(pipeline.CalibrationArmed);
        var first = _bed.Start(pipeline);
        // The exit burst carried no job, so the evening's draft is ready without PLAYER_JOB.
        Bed.Feed(pipeline, first, CalibrationTrafficCases.WithoutExitJob(Evening(CalibrationTrafficCases.Announcement)));
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        pipeline.ConfirmCalibration(Bed.AllCorrect(offered));

        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        Assert.Equal(CalibrationState.Done, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(first, CaptureEndReason.UserStop);

        // The next evening, in the same run of the Collector, carries the job.
        var second = _bed.Start(pipeline);
        Bed.Feed(pipeline, second, Evening(CalibrationTrafficCases.Announcement), hour: 24);

        var completion = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, completion.State);
        pipeline.ConfirmCalibration(Bed.AllCorrect(completion));
        Assert.Contains("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        pipeline.OnCaptureStopped(second, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// While the job is still missing the card must stay away entirely. The player is recording
    /// correctly; the only thing wrong with their records is a field, and a card that says
    /// 正在重新校准…期间不会生成记录 over a profile that is recording would be a lie.
    /// </summary>
    [Fact]
    public void TheCardStaysHiddenWhileTheCompletionIsStillBeingLookedFor()
    {
        _bed.WriteLocalProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        // Login only: nothing yet re-derives the match, let alone the job.
        Bed.Feed(pipeline, session, CalibrationObserverTests.Cluster(5_000, 5000));

        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The one thing this must never do. A draft that names a different match message is not a
    /// completion but a replacement, and confirming it would silently swap out a match message
    /// that works today for one nobody asked about. Such a draft is never put to the player here.
    /// </summary>
    [Fact]
    public void ADraftThatNamesADifferentMatchMessageIsNeverOffered()
    {
        _bed.WriteLocalProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        // An evening whose draft reads the match at an offset the traffic named instead: the
        // same opcode, a different shape, and the job along with it.
        Bed.Feed(pipeline, session, Evening(CalibrationTrafficCases.MarkerOffset));

        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(_bed.LocalProfilePath), StringComparison.Ordinal);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>A profile that already has the job finished calibrating; nothing is armed beside it.</summary>
    [Fact]
    public void AFinishedProfileThatAlreadyHasTheJobIsNotCalibratedBeside()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
    }
}
