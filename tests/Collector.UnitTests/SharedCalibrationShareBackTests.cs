using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Plan §20.5, 这份校准好用，告诉其他玩家: once this machine has proven another player's calibration - a complete duty
/// recorded and every audited criterion passed - it hands back the very code it downloaded, with the issue form preset
/// to say it was verified here, so the repository can count one more account behind it. Before that, or for a code the
/// store does not hold, there is nothing to hand back.
/// </summary>
public sealed class SharedCalibrationShareBackTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private string StoredFile(SharedCode code) =>
        Path.Combine(SharedCalibrationStore.DirectoryFor(_bed.StoreRoot, Region.Cn, Bed.Build), code.Sha[..12] + SharedCalibrationIndex.CodeExtension);

    private static void AssertRefused(SharedShareCodeResult result, SharedShareCodeRefusal refusal)
    {
        Assert.Null(result.ShareCode);
        Assert.Equal(refusal, result.Refusal);
        Assert.Matches("[\\u4e00-\\u9fff]", result.Message);
        Assert.DoesNotContain("0x", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Publishes a code, binds it on the login burst and records one whole evening with it.</summary>
    private async Task<(LiveProtocolPipeline Pipeline, SharedCode Code)> ProvenSharedProfile()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.PublishListed(new Bed.Listing(code, ShapePrior: true));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedShareCodeRefusal.NoProfile, pipeline.GetCalibrationShareCode().Refusal);

        var evening = Bed.Evening().ToArray();
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(evening, 30_000));
        await Bed.Idle(pipeline);

        // Bound and recording, with the match still being audited: nothing to vouch for yet.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.True(pipeline.CalibrationStatus().Shared.AuditPending);
        AssertRefused(pipeline.GetCalibrationShareCode(), SharedShareCodeRefusal.SharedNotProven);

        Bed.Feed(pipeline, session, Bed.From(evening, 30_000));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
        return (pipeline, code);
    }

    [Fact]
    public async Task AProvenSharedProfileIsHandedBackAsTheDownloadedCodeWithTheVerifiedBasis()
    {
        var (pipeline, published) = await ProvenSharedProfile();

        var result = pipeline.GetCalibrationShareCode();

        var code = Assert.IsType<SharedShareCode>(result.ShareCode);
        Assert.Null(result.Refusal);
        Assert.Equal(published.Code, code.Code);
        Assert.Equal(File.ReadAllText(StoredFile(published)), code.Code);
        Assert.Equal(published.Sha, code.CodeSha256);
        Assert.Equal(SharedShareBasis.Verified, code.Basis);
        Assert.Equal(SharedCalibrationIssueLink.For(Region.Cn, Bed.Build, published.Code, SharedShareBasis.Verified).Url, code.IssueUrl);
        Assert.Contains(
            "&basis=" + Uri.EscapeDataString(SharedCalibrationIssueLink.BasisOption(SharedShareBasis.Verified)), code.IssueUrl,
            StringComparison.Ordinal);
        Assert.True(code.CodeInUrl);
        Assert.Equal("VERIFIED", SharedCalibrationHandlers.ShareCodeResponse(code)["basis"]!.GetValue<string>());

        // The text must be the code the repository already holds: without the stored file there is nothing to hand back.
        File.Delete(StoredFile(published));
        AssertRefused(pipeline.GetCalibrationShareCode(), SharedShareCodeRefusal.Shared);
    }

    [Fact]
    public async Task AfterARestartTheSettledSharedProfileIsStillHandedBack()
    {
        var (_, published) = await ProvenSharedProfile();
        var requests = _bed.Transport.Requests.Count;

        // Settled: calibration is not armed beside it any more, so the code is recovered from the profile itself.
        var again = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, again.Refresh(Bed.Game()).Origin);
        await Bed.Idle(again);
        Assert.False(again.CalibrationArmed);

        // The card still sees it as another player's calibration proven here, published by the repository (its code is
        // kept in the store), with what the index said about it - and learning that sent nothing (privacy §8.2).
        var shared = again.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Verified, shared.Phase);
        Assert.False(shared.AuditPending);
        var proven = Assert.Single(shared.Candidates);
        Assert.Equal(published.Sha[..12], proven.Sha12);
        Assert.Equal(SharedCandidateStatus.Proven, proven.Status);
        Assert.Equal(SharedCandidateProvenance.Published, proven.Provenance);
        Assert.Equal(CalibrationMatchSource.ReplyState, proven.MatchSource);
        Assert.False(proven.AuditPending);
        Assert.Empty(proven.Disputed);
        Assert.True(proven.Preferred);
        // The prior is this machine's own comparison with an earlier build's calibration; nothing is left to compare with.
        Assert.Null(proven.LocalShapePrior);
        Assert.Equal(requests, _bed.Transport.Requests.Count);

        var code = Assert.IsType<SharedShareCode>(again.GetCalibrationShareCode().ShareCode);
        Assert.Equal(published.Code, code.Code);
        Assert.Equal(published.Sha, code.CodeSha256);
        Assert.Equal(SharedShareBasis.Verified, code.Basis);
        Assert.Equal(requests, _bed.Transport.Requests.Count);
    }

    /// <summary>The last index this machine read for the running build revokes <paramref name="sha"/>.</summary>
    private void RevokeInStore(string sha) => Assert.True(_bed.Store.RecordFetch(
        Region.Cn, Bed.Build, Bed.TemplateSha,
        new SharedCalibrationFetchResult(
            SharedFetchStatus.NoneForBuild,
            new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Ok, 200) },
            Array.Empty<SharedCalibrationCandidate>(), Array.Empty<SharedCodeDiscard>(), Array.Empty<SharedIndexSkip>(), new[] { sha }),
        _bed.Db.Clock.UtcNow).Recorded);

    /// <summary>A run row recorded under the shared profile id after its bind, with its creation revision, as the processor writes them.</summary>
    private MentorRun SeedRunUnderSharedProfile()
    {
        var run = TestDatabase.Run(
                source: RunSource.AutoNetwork, result: RunResult.CancelledBeforeEntry, enteredAt: _bed.Db.Clock.UtcNow.AddMinutes(2)) with
            {
                ProtocolProfileId = SharedProfileBuilder.ProfileIdFor(Region.Cn, Bed.Build),
            };
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
        Assert.False(new RunRepository(_bed.Db.Database).Get(run.RunId)!.PendingReview);
        return run;
    }

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Review 2026-10-15 (M1): a settled profile records with calibration disarmed, so the revocation check that runs when a
    /// profile is adopted under calibration never ran for it. After a restart the store's record of the revocation is
    /// read all the same: the profile is withdrawn, what it recorded is pending review, and - with no index read just now
    /// to bring them - the other codes the store keeps are offered again from the disk, sending nothing.
    /// </summary>
    [Fact]
    public async Task AfterARestartASettledProfileTheStoreRecordsAsRevokedIsWithdrawnAndTheStoredCodesOfferedAgain()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = Bed.Encode(code.Payload with { ZoneOpcode = 0xA1F7 });
        _bed.PublishListed(new Bed.Listing(code, Submitters: 2), new Bed.Listing(other));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
        Assert.Single(RunsOf(session));

        // A record the profile made that nothing else marked: the evening's own run ends without a clear on this build and
        // is pending review for that reason already.
        var recorded = SeedRunUnderSharedProfile();
        RevokeInStore(code.Sha);
        var requests = _bed.Transport.Requests.Count;
        var again = _bed.Pipeline(_bed.Services());
        again.Refresh(Bed.Game());
        await Bed.Idle(again);

        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.True(new RunRepository(_bed.Db.Database).Get(recorded.RunId)!.PendingReview);
        Assert.Equal(ProfileStatus.UnsupportedBuild, again.Current.Status);
        Assert.True(again.CalibrationArmed);
        var shared = again.CalibrationStatus().Shared;
        Assert.Equal(SharedCandidateStatus.Verifying, Assert.Single(shared.Candidates, item => item.Sha12 == other.Sha[..12]).Status);
        Assert.DoesNotContain(shared.Candidates, item => item.Sha12 == code.Sha[..12] && item.Status != SharedCandidateStatus.Rejected);
        Assert.Equal(requests, _bed.Transport.Requests.Count);
    }


    /// <summary>
    /// Review 2026-10-15 (M2): a profile adopted from disk is watched under the published gate, but what the card shows is
    /// whether this machine keeps its code to hand back. A pasted code no index listed, bound, restarted mid-watch and then
    /// proven, shows IMPORTED - no 这份校准好用 button - and the share code agrees: there is no stored text.
    /// </summary>
    [Fact]
    public async Task APastedCodeProvenAfterARestartMidWatchDoesNotClaimItsCodeIsKeptHere()
    {
        _bed.Publish();
        var pasted = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCandidateProvenance.Imported, pipeline.ImportCalibrationCode(pasted.Code).Provenance);

        // Bound once its match and duty entry behaved; the duty is not over when the session ends, so the watch is still on.
        var evening = Bed.Evening().ToArray();
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
        var inUse = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateStatus.InUse, inUse.Status);
        Assert.Equal(SharedCandidateProvenance.Imported, inUse.Provenance);

        var again = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, again.Refresh(Bed.Game()).Origin);
        await Bed.Idle(again);
        Assert.True(again.CalibrationArmed);
        Assert.Equal(SharedCandidateProvenance.Imported, Assert.Single(again.CalibrationStatus().Shared.Candidates).Provenance);

        var next = _bed.Start(again);
        Bed.Feed(again, next, Bed.Evening());
        again.OnCaptureStopped(next, CaptureEndReason.UserStop);
        await Bed.Idle(again);

        var proven = Assert.Single(again.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateStatus.Proven, proven.Status);
        Assert.Equal(SharedCandidateProvenance.Imported, proven.Provenance);
        AssertRefused(again.GetCalibrationShareCode(), SharedShareCodeRefusal.Shared);
    }

    /// <summary>
    /// Review 2026-10-15 (L2): after a software update brings another template, the settled profile's code is still the one
    /// recorded when it was bound - reading the profile back through the new template would name another - so the card
    /// lists it and the share code hands it back.
    /// </summary>
    [Fact]
    public async Task AfterARestartWithAnotherTemplateTheCodeTheProfileWasBoundFromIsStillKnown()
    {
        var (_, published) = await ProvenSharedProfile();
        var changed = CalibrationTemplate.From(Bed.Template.Source with { ProfileSha256 = new string('7', 64) })!;

        var again = _bed.Pipeline(_bed.Services() with { SelectTemplate = _ => changed });
        Assert.Equal(ProfileOrigin.Shared, again.Refresh(Bed.Game()).Origin);
        await Bed.Idle(again);

        var proven = Assert.Single(again.CalibrationStatus().Shared.Candidates);
        Assert.Equal(published.Sha[..12], proven.Sha12);
        Assert.Equal(SharedCandidateStatus.Proven, proven.Status);
        Assert.Equal(SharedCandidateProvenance.Published, proven.Provenance);
        var code = Assert.IsType<SharedShareCode>(again.GetCalibrationShareCode().ShareCode);
        Assert.Equal(published.Code, code.Code);
        Assert.Equal(SharedShareBasis.Verified, code.Basis);
    }

    [Fact]
    public async Task AProvenCodeThePlayerPastedAndNoIndexListedHasNoStoredTextToHandBack()
    {
        // The store is real and holds another code of the build, which never binds: its zone marker is not what the client sends.
        var pasted = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var stored = Bed.Encode(pasted.Payload with { ZoneOpcode = 0xA1F7 });
        _bed.Publish(stored);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.True(File.Exists(StoredFile(stored)));
        Assert.Equal(SharedCandidateProvenance.Imported, pipeline.ImportCalibrationCode(pasted.Code).Provenance);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
        var proven = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(pasted.Sha[..12], proven.Sha12);
        Assert.Equal(SharedCandidateStatus.Proven, proven.Status);
        Assert.False(File.Exists(StoredFile(pasted)));
        Assert.Null(_bed.Store.CodeText(Region.Cn, Bed.Build, pasted.Sha));

        AssertRefused(pipeline.GetCalibrationShareCode(), SharedShareCodeRefusal.Shared);

        // After a restart the card still lists it as proven - not as published, since the repository never held it.
        var requests = _bed.Transport.Requests.Count;
        var again = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, again.Refresh(Bed.Game()).Origin);
        await Bed.Idle(again);
        Assert.False(again.CalibrationArmed);
        var adopted = Assert.Single(again.CalibrationStatus().Shared.Candidates);
        Assert.Equal(pasted.Sha[..12], adopted.Sha12);
        Assert.Equal(SharedCandidateStatus.Proven, adopted.Status);
        Assert.Equal(SharedCandidateProvenance.Imported, adopted.Provenance);
        Assert.Null(adopted.LocalShapePrior);
        AssertRefused(again.GetCalibrationShareCode(), SharedShareCodeRefusal.Shared);
        Assert.Equal(requests, _bed.Transport.Requests.Count);
    }

    [Fact]
    public void WhatIsHandedBackMustBeTheProvenDocumentAndDecodeToItsOwnCode()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = _bed.CodeFromEveningA(CalibrationTrafficCases.MarkerOffset);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()), _bed.SharedRoot);
        var profile = ProfileLoader.Load(_bed.SharedProfilePath);
        var selection = new ProfileSelection(
            ProfileCompatibilityStatus.Verified, profile.ToBinding(), profile, Region.Cn, Bed.Build, "selected", ProfileOrigin.Shared);
        var proof = new SharedShareProof(profile.ProfileSha256, code.Sha);

        var honest = SharedShareCodeExport.From(selection, _ => Bed.Template, proof, new OneCodeStore(code.Code));
        Assert.Equal(code.Code, Assert.IsType<SharedShareCode>(honest.ShareCode).Code);
        Assert.Equal(SharedShareBasis.Verified, honest.ShareCode!.Basis);

        // Another document than the one proven, nothing proven, a store holding something else, or nothing at all.
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof with { ProfileSha256 = new string('0', 64) }, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.SharedNotProven);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, null, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.SharedNotProven);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof, new OneCodeStore(other.Code)),
            SharedShareCodeRefusal.Shared);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof, new OneCodeStore("MRC1.not-a-code")),
            SharedShareCodeRefusal.Shared);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof, SharedCalibrationStore.Inert),
            SharedShareCodeRefusal.Shared);

        // A code the session names that is not the one the profile in force was built from is not handed on, whatever the
        // store holds for it; nor is anything when the profile cannot be checked against the template.
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof with { CodeSha256 = other.Sha }, new OneCodeStore(other.Code)),
            SharedShareCodeRefusal.Shared);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => null, proof, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.Shared);

        // Review 2026-10-15 (L2): once a software update brings another template, reading the profile back through it gives
        // another identity; the code recorded when the profile was bound is what the profile was written from.
        var changed = CalibrationTemplate.From(Bed.Template.Source with { ProfileSha256 = new string('7', 64) })!;
        Assert.Equal(code.Code, SharedShareCodeExport.From(selection, _ => changed, proof, new OneCodeStore(code.Code, code.Sha)).ShareCode!.Code);
        Assert.Equal(code.Code, SharedShareCodeExport.From(
            selection, _ => changed, proof with { CodeSha256 = null }, new OneCodeStore(code.Code, code.Sha)).ShareCode!.Code);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => changed, proof, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.Shared);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => Bed.Template, proof, new OneCodeStore(code.Code, other.Sha)),
            SharedShareCodeRefusal.Shared);

        // With the code not known to the session, it is recovered from the profile through the template - or not at all.
        var unnamed = proof with { CodeSha256 = null };
        Assert.Equal(code.Sha, SharedShareCodeExport.From(selection, _ => Bed.Template, unnamed, new OneCodeStore(code.Code)).ShareCode!.CodeSha256);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => null, unnamed, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.Shared);
        AssertRefused(
            SharedShareCodeExport.From(selection, _ => throw new IOException("template unreadable"), unnamed, new OneCodeStore(code.Code)),
            SharedShareCodeRefusal.Shared);
    }

    /// <summary>A store that answers <see cref="ISharedCalibrationStore.CodeText"/> with one fixed text, whatever is asked.</summary>
    private sealed class OneCodeStore(string text, string? boundCode = null) : ISharedCalibrationStore
    {
        private static readonly ISharedCalibrationStore Nothing = SharedCalibrationStore.Inert;

        public string? CodeText(Region region, string gameBuild, string codeSha256) => text;

        public string? BoundCode(Region region, string gameBuild, string profileSha256) => boundCode;

        public SharedStoreWriteResult RecordFetch(
            Region region, string gameBuild, string templateSha256, SharedCalibrationFetchResult result, DateTimeOffset nowUtc) =>
            Nothing.RecordFetch(region, gameBuild, templateSha256, result, nowUtc);

        public IReadOnlyList<SharedStoredCandidate> LoadCandidates(Region region, string gameBuild, string templateSha256) =>
            Nothing.LoadCandidates(region, gameBuild, templateSha256);

        public SharedFetchRecord? LastFetch(Region region, string gameBuild, string templateSha256) =>
            Nothing.LastFetch(region, gameBuild, templateSha256);

        public SharedContradictionResult RecordContradiction(
            Region region, string gameBuild, string templateSha256, string codeSha256, string sessionId, DateTimeOffset nowUtc) =>
            Nothing.RecordContradiction(region, gameBuild, templateSha256, codeSha256, sessionId, nowUtc);

        public bool IsRejected(Region region, string gameBuild, string templateSha256, string codeSha256) =>
            Nothing.IsRejected(region, gameBuild, templateSha256, codeSha256);

        public bool RecordUserRejection(Region region, string gameBuild, DateTimeOffset nowUtc) =>
            Nothing.RecordUserRejection(region, gameBuild, nowUtc);

        public bool IsUserRejected(Region region, string gameBuild) => Nothing.IsUserRejected(region, gameBuild);

        public bool ClearRejections(Region region, string gameBuild) => Nothing.ClearRejections(region, gameBuild);

        public SharedPublication Publication(Region region, string gameBuild, string codeSha256) =>
            Nothing.Publication(region, gameBuild, codeSha256);

        public bool RecordSettled(Region region, string gameBuild, string profileSha256, DateTimeOffset nowUtc) =>
            Nothing.RecordSettled(region, gameBuild, profileSha256, nowUtc);

        public bool IsSettled(Region region, string gameBuild, string profileSha256) => Nothing.IsSettled(region, gameBuild, profileSha256);

        public bool RecordBound(Region region, string gameBuild, string profileSha256, string codeSha256, DateTimeOffset recordsFromUtc) =>
            Nothing.RecordBound(region, gameBuild, profileSha256, codeSha256, recordsFromUtc);

        public DateTimeOffset? BoundSince(Region region, string gameBuild, string profileSha256) =>
            Nothing.BoundSince(region, gameBuild, profileSha256);
    }
}
