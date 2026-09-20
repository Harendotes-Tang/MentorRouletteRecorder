using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The verification gate by provenance (plan §18): a published code binds on the login burst and records its
/// first match live while the match and the duty entry are audited; a pasted code no index knows records
/// nothing until both have been seen to behave; a pasted code the index lists is published, one it revoked is
/// refused, and one a later index lists is promoted. What a withdrawn profile recorded is marked pending review.
/// </summary>
public sealed class SharedCalibrationProvenanceTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    private static string ProfileId => SharedProfileBuilder.ProfileIdFor(Region.Cn, Bed.Build);

    /// <summary>The login burst and the idle minute after it: nothing queued yet.</summary>
    private static DecodedMessage[] Login(IEnumerable<DecodedMessage> evening) => Bed.Before(evening, 30_000);

    /// <summary>Queue, match and the duty entry, after the login burst.</summary>
    private static DecodedMessage[] UpToEntry(IEnumerable<DecodedMessage> evening) => Bed.From(Bed.Before(evening, 200_000), 30_000);

    [Fact]
    public async Task APublishedCodeBindsOnTheLoginBurstAndRecordsTheFirstMatchLive()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var candidate = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateProvenance.Published, candidate.Provenance);

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        // Bound before anything was queued: the zone marker vouched for the build, the rest is audited.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Empty(RunsOf(session));
        var bound = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Verified, bound.Phase);
        Assert.True(bound.AuditPending);
        var inUse = Assert.Single(bound.Candidates);
        Assert.Equal(SharedCandidateStatus.InUse, inUse.Status);
        Assert.True(inUse.AuditPending);
        Assert.True(pipeline.CalibrationArmed);

        // The match arrives on the bound parser, not in a staging list.
        Bed.Feed(pipeline, session, UpToEntry(evening));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(9, run.MentorRouletteId);
        Assert.Equal(ProfileId, run.ProtocolProfileId);

        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        // A complete duty, and the match and the duty entry both seen to behave: the watch is over.
        Assert.NotNull(Assert.Single(RunsOf(session)).EndedAtUtc);
        var done = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Done, done.State);
        Assert.False(done.Shared.AuditPending);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(done.Shared.Candidates).Status);
    }

    [Fact]
    public async Task AnImportedCodeUnknownToTheIndexRecordsNothingUntilTheMatchBehaves()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);

        var applied = pipeline.ImportCalibrationCode(code.Code);
        Assert.Equal(SharedImportOutcome.Applied, applied.Outcome);
        Assert.Equal(SharedCandidateProvenance.Imported, applied.Provenance);
        Assert.Contains("没有在公开仓库发布", applied.Message, StringComparison.Ordinal);
        Assert.Equal(SharedCandidateProvenance.Imported, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Provenance);

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.Equal(SharedCalibrationPhase.Verifying, pipeline.CalibrationStatus().Shared.Phase);
        Assert.False(File.Exists(_bed.SharedProfilePath));

        Bed.Feed(pipeline, session, UpToEntry(evening));
        await Bed.Idle(pipeline);

        // Only now, with the match and the duty entry seen: bound, and the staged evening drained into a run.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
        Assert.Empty(_bed.Transport.Requests);
    }

    [Fact]
    public async Task AnImportedCodeTheIndexListsIsPublishedAndOneItDoesNotKnowIsNot()
    {
        var listed = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var unlisted = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        _bed.Publish(listed);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var samePasted = pipeline.ImportCalibrationCode(listed.Code);
        var otherPasted = pipeline.ImportCalibrationCode(unlisted.Code);

        Assert.Equal(SharedImportOutcome.Applied, samePasted.Outcome);
        Assert.Equal(SharedCandidateProvenance.Published, samePasted.Provenance);
        Assert.Contains("其他玩家提交的一致", samePasted.Message, StringComparison.Ordinal);
        Assert.Equal(SharedCandidateProvenance.Imported, otherPasted.Provenance);
        // One code, one candidate: the download and the paste of the same code do not double up.
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(2, candidates.Count);
        Assert.Equal(SharedCandidateProvenance.Published, Assert.Single(candidates, item => item.Sha12 == listed.Sha[..12]).Provenance);
        Assert.Equal(SharedCandidateProvenance.Imported, Assert.Single(candidates, item => item.Sha12 == unlisted.Sha[..12]).Provenance);
    }

    [Fact]
    public async Task AnImportedCodeTheIndexRevokedIsRefused()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.PublishRevoked(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var refused = pipeline.ImportCalibrationCode(code.Code);

        Assert.Equal(SharedImportOutcome.NotApplicable, refused.Outcome);
        Assert.Equal("REVOKED", refused.Reason);
        Assert.Contains("撤回", refused.Message, StringComparison.Ordinal);
        Assert.Empty(pipeline.CalibrationStatus().Shared.Candidates);
    }

    [Fact]
    public async Task AnImportedCodeIsPromotedWhenALaterIndexListsIt()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish();
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCandidateProvenance.Imported, pipeline.ImportCalibrationCode(code.Code).Provenance);

        _bed.Publish(code);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        var promoted = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateSource.Manual, promoted.Source);
        Assert.Equal(SharedCandidateProvenance.Published, promoted.Provenance);

        // And it binds as a published code does: at login.
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Login(Bed.Evening()));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.Idle, pipeline.RunState);
    }

    [Fact]
    public async Task APublishedCodeContradictedWhileRecordingIsWithdrawnAndWhatItRecordedIsPendingReview()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        // Bound at login; a match recorded; the duty never exited, so the watch is still on when the evening ends.
        var first = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, first, Login(evening));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Bed.Feed(pipeline, first, UpToEntry(evening));
        var recorded = Assert.Single(RunsOf(first));
        Assert.False(recorded.PendingReview);
        pipeline.OnCaptureStopped(first, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        // Two healthy evenings on which the declared zone marker is not what the client sends.
        var without = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
            .Select(message => message.Opcode == CalibrationTrafficCases.ZoneInit ? message with { Opcode = 0xA1F7 } : message)
            .ToArray();
        _bed.Play(pipeline, without, hour: 48);
        await Bed.Idle(pipeline);
        Assert.True(File.Exists(_bed.SharedProfilePath));
        _bed.Play(pipeline, without, hour: 72);
        await Bed.Idle(pipeline);

        Assert.True(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(SharedCalibrationPhase.Rejected, pipeline.CalibrationStatus().Shared.Phase);
        Assert.True(pipeline.CalibrationArmed);
        var flagged = new RunRepository(_bed.Db.Database).Get(recorded.RunId)!;
        Assert.True(flagged.PendingReview);
        Assert.Equal(ProfileId, flagged.ProtocolProfileId);
    }

    /// <summary>A run row with its creation revision, as the processor writes them.</summary>
    private void Seed(MentorRun run)
    {
        var runs = new RunRepository(_bed.Db.Database);
        var revisions = new RunRevisionRepository(_bed.Db.Database);
        _bed.Db.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            revisions.Append(new RunRevision
            {
                RevisionId = Guid.NewGuid().ToString("D"), RunId = run.RunId, Revision = 1,
                ChangedAtUtc = run.CreatedAtUtc, ChangeKind = ChangeKind.CreateAuto, Actor = RevisionActor.System,
                Reason = null, Changes = Array.Empty<RunFieldChange>(),
            }, tx);
        });
    }

    /// <summary>
    /// Plan §18.4 across a restart: a complete duty recorded while the audit was still waiting does not prove the
    /// profile. It is adopted as watched, the audit resumes, and only once it settles is the document recorded as
    /// settled - after which the next restart trusts it without watching.
    /// </summary>
    [Fact]
    public async Task AfterARestartADutyRecordedWhileTheAuditWasStillWaitingKeepsTheWatchOn()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()), _bed.SharedRoot);
        var profileSha = ProfileLoader.Load(_bed.SharedProfilePath).ProfileSha256;
        Seed(TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: Bed.Confirmed.AddHours(1)) with { ProtocolProfileId = ProfileId });
        Assert.True(new RunRepository(_bed.Db.Database).AnyEnteredAndExited(ProfileId));

        // The store is wired (as in the shipping assembly); a shared profile still being watched is one of the
        // two cases in which the index is read again (docs/privacy-boundary.md §8.2), so a request does go out.
        var pipeline = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        Assert.True(pipeline.CalibrationArmed);
        var watched = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateStatus.InUse, watched.Status);
        Assert.Equal(code.Sha[..12], watched.Sha12);
        Assert.False(_bed.Store.IsSettled(Region.Cn, Bed.Build, profileSha));

        // An evening with a match, a duty entry and an exit settles the audit.
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        var done = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Done, done.State);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(done.Shared.Candidates).Status);
        Assert.True(_bed.Store.IsSettled(Region.Cn, Bed.Build, profileSha));

        // Settled, so the watch is over and calibration ends: nothing is read for this build any more.
        var sent = _bed.Transport.Requests.Count;
        var again = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, again.Refresh(Bed.Game()).Origin);
        await Bed.Idle(again);
        Assert.False(again.CalibrationArmed);
        Assert.Equal(SharedCalibrationPhase.Verified, again.CalibrationStatus().Shared.Phase);
        Assert.Equal(sent, _bed.Transport.Requests.Count);
    }

    [Fact]
    public void FlaggingMarksOnlyWhatTheBindingRecordedAndKeepsHumanDecisions()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        var runs = new RunRepository(_bed.Db.Database);
        var since = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);
        var recorded = TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: since.AddMinutes(30)) with { ProtocolProfileId = ProfileId };
        var earlier = TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: since.AddHours(-2)) with { ProtocolProfileId = ProfileId };
        var alreadyPending = TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: since.AddMinutes(40)) with { ProtocolProfileId = ProfileId, PendingReview = true };
        var deleted = TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: since.AddMinutes(50), softDeleted: true) with { ProtocolProfileId = ProfileId };
        var otherProfile = TestDatabase.Run(source: RunSource.AutoNetwork, enteredAt: since.AddMinutes(60)) with { ProtocolProfileId = "cn.other.local" };
        var revisions = new RunRevisionRepository(_bed.Db.Database);
        _bed.Db.Database.RunInTransaction(tx =>
        {
            foreach (var run in new[] { recorded, earlier, alreadyPending, deleted, otherProfile })
            {
                // As the processor writes them: the row and its creation revision, which the trail's trigger requires.
                runs.Insert(run, tx);
                revisions.Append(new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"), RunId = run.RunId, Revision = 1,
                    ChangedAtUtc = run.CreatedAtUtc, ChangeKind = ChangeKind.CreateAuto, Actor = RevisionActor.System,
                    Reason = null, Changes = Array.Empty<RunFieldChange>(),
                }, tx);
            }
        });

        var flagged = ((ISharedCalibrationHost)pipeline).FlagSharedRecords(ProfileId, since, "CONTRADICTED");

        Assert.Equal(1, flagged);
        var marked = runs.Get(recorded.RunId)!;
        Assert.True(marked.PendingReview);
        Assert.Equal(recorded.Revision + 1, marked.Revision);
        Assert.Equal(RunResult.Completed, marked.Result);
        var revision = revisions.ListForRun(recorded.RunId, 1, 10).Items.Single(item => item.Revision == 2);
        Assert.Equal(RevisionActor.System, revision.Actor);
        Assert.Contains("对不上", revision.Reason, StringComparison.Ordinal);
        Assert.Contains(revision.Changes, change => change.Field == "pending_review");
        Assert.False(runs.Get(earlier.RunId)!.PendingReview);
        Assert.False(runs.Get(otherProfile.RunId)!.PendingReview);
        Assert.Equal(alreadyPending.Revision, runs.Get(alreadyPending.RunId)!.Revision);

        // With no bound time everything under the profile id is suspect, and a second pass finds nothing left.
        Assert.Equal(1, ((ISharedCalibrationHost)pipeline).FlagSharedRecords(ProfileId, null, "REVOKED"));
        Assert.Contains("撤回", revisions.ListForRun(earlier.RunId, 1, 10).Items.Single(item => item.Revision == 2).Reason, StringComparison.Ordinal);
        Assert.Equal(0, ((ISharedCalibrationHost)pipeline).FlagSharedRecords(ProfileId, null, "REVOKED"));
    }
}
