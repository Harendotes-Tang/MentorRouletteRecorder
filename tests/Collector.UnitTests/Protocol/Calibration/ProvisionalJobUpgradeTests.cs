using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The player's own machine on CN 2026.09.15: a queue-inferred profile was in force without
/// PLAYER_JOB, because the job rule could not name the message when it was written. Once the rule
/// can, the draft underneath is the same inferred match plus the job - and a draft that repeats
/// the inferred match was never offered again, so the profile could never gain the job and every
/// record stayed 职业未知 for the life of the build.
/// </summary>
public sealed class ProvisionalJobUpgradeTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private string ProfilePath => LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build);

    private static IReadOnlyDictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    [Fact]
    public void AnInferredProfileWithoutTheJobIsOfferedTheJobAndUsesItAtOnce()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequestNoJob), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot));
        pipeline.Refresh(Bed.Game());
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(CalibrationObserverTests.Noise(250_000, 290_000)));

        // The same inferred match, but it brings something the profile in force lacks.
        var offered = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, offered.State);

        var result = pipeline.ConfirmCalibration(AllCorrect(offered));

        Assert.Contains("PLAYER_JOB", File.ReadAllText(ProfilePath), StringComparison.Ordinal);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        // Bound in the running session, not at the next launch: a mentor run queued now has the job.
        Bed.Feed(pipeline, session, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Outbound, CalibrationTrafficCases.Request,
                CalibrationObserverTests.Bytes(24, (0, 9)), 400_000),
        }.Concat(CalibrationObserverTests.Cluster(420_000, 1039, job: 24)));

        var run = new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Single(item => string.Equals(item.ProtocolProfileId, result.ProfileId, StringComparison.Ordinal) &&
                            item.EnteredAtUtc is not null);
        Assert.Equal(24, run.JobId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>A profile that already has the job is not asked about again for an identical draft.</summary>
    [Fact]
    public void AnInferredProfileThatAlreadyHasTheJobIsNotAskedAgain()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build,
            Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot));
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(CalibrationObserverTests.Noise(250_000, 290_000)));

        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }
}
