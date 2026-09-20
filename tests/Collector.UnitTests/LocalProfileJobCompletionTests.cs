using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A finished local profile completes itself.
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

    private string ProfilePath => LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build);

    private static IReadOnlyDictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    /// <summary>
    /// The profile a machine wrote before the job rule could name the message: the very draft one
    /// evening produces, with the job message and its evidence entry taken back out again.
    /// </summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    private string WriteProfileLackingTheJob(string trafficCase)
    {
        var draft = CalibrationTrafficCases.Derive(trafficCase);
        Assert.Contains(draft.Messages, message => message.Name == "PLAYER_JOB");
        var withheld = draft with
        {
            Messages = draft.Messages.Where(message => message.Name != "PLAYER_JOB").ToArray(),
            SampleCounts = draft.SampleCounts
                .Where(sample => !sample.Key.Contains("PLAYER_JOB", StringComparison.Ordinal))
                .ToDictionary(sample => sample.Key, sample => sample.Value, StringComparer.Ordinal),
        };
        var written = LocalProfileWriter.Write(withheld, Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot).Path;
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(written), StringComparison.Ordinal);
        return written;
    }

    /// <summary>A live pipeline over the bed's roots with the local-profile seams wired.</summary>
    private Protocol.Pipeline.LiveProtocolPipeline Pipeline() =>
        _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot));

    /// <summary>An evening's traffic, padded so the last burst closes while the session runs on.</summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    private static IEnumerable<DecodedMessage> Evening(string trafficCase) =>
        CalibrationTrafficCases.Traffic(trafficCase).Concat(CalibrationObserverTests.Noise(360_000, 400_000));

    [Fact]
    public void AFinishedProfileWithoutTheJobIsOfferedTheJobAndUsesItAtOnce()
    {
        WriteProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, Evening(CalibrationTrafficCases.Announcement));

        // The same match the profile already reads, plus the job it never had.
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        var result = pipeline.ConfirmCalibration(AllCorrect(offered));

        Assert.Contains("PLAYER_JOB", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        Assert.NotEqual(CalibrationState.Ready, pipeline.CalibrationStatus().State);

        // Bound in the running session, not at the next launch: the next mentor run has the job.
        Bed.Feed(pipeline, session, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, CalibrationTrafficCases.Announce,
                CalibrationObserverTests.Bytes(64, (16, 9)), 500_000),
        }.Concat(CalibrationObserverTests.Cluster(520_000, 1039, job: 24)));

        var run = new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Single(item => string.Equals(item.ProtocolProfileId, result.ProfileId, StringComparison.Ordinal) &&
                            item.EnteredAtUtc is not null && item.MentorRouletteId == 9);
        Assert.Equal(24, run.JobId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// While the job is still missing the card must stay away entirely. The player is recording
    /// correctly; the only thing wrong with their records is a field, and a card that says
    /// 正在重新校准…期间不会生成记录 over a profile that is recording would be a lie.
    /// </summary>
    [Fact]
    public void TheCardStaysHiddenWhileTheCompletionIsStillBeingLookedFor()
    {
        WriteProfileLackingTheJob(CalibrationTrafficCases.Announcement);
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
        WriteProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        // An evening whose draft reads the match at an offset the traffic named instead: the
        // same opcode, a different shape, and the job along with it.
        Bed.Feed(pipeline, session, Evening(CalibrationTrafficCases.MarkerOffset));

        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>A profile that already has the job finished calibrating; nothing is armed beside it.</summary>
    [Fact]
    public void AFinishedProfileThatAlreadyHasTheJobIsNotCalibratedBeside()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.Announcement), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var pipeline = Pipeline();
        pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
    }
}
