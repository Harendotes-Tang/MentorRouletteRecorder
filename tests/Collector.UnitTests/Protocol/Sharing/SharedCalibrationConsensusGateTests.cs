using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Plan §20.5 and §20.7: which published code may bind on the login burst is decided by the codes being verified for
/// the build, field by field. A code that carries the best-attested value on every field in dispute - or, on a tied
/// match, the only shape the previous build's codes had - keeps the patch-day path; a tie nothing decides sends every
/// code back to verifying its match and duty entry first (ON1-1). The repository's conflict mark no longer decides that
/// on its own. When the favoured code is later contradicted, it is withdrawn as before and its rival takes over.
/// </summary>
public sealed class SharedCalibrationConsensusGateTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static string ProfileId => SharedProfileBuilder.ProfileIdFor(Region.Cn, Bed.Build);

    /// <summary>The login burst and the idle minute after it: nothing queued yet.</summary>
    private static DecodedMessage[] Login(IEnumerable<DecodedMessage> evening) => Bed.Before(evening, 30_000);

    /// <summary>Everything after the login burst.</summary>
    private static DecodedMessage[] AfterLogin(IEnumerable<DecodedMessage> evening) => Bed.From(evening, 30_000);

    /// <summary>Queue, match and the duty entry, after the login burst.</summary>
    private static DecodedMessage[] UpToEntry(IEnumerable<DecodedMessage> evening) => Bed.From(Bed.Before(evening, 200_000), 30_000);

    /// <summary>The same code with its match message moved to an opcode the client never sends it on.</summary>
    private static SharedCode WithBogusPop(SharedCode code) =>
        Bed.Encode(code.Payload with { Pop = code.Payload.Pop with { Opcode = 0x7777 } });

    /// <summary>The same code with its zone marker moved to an opcode the client never sends it on.</summary>
    private static SharedCode WithBogusZone(SharedCode code) => Bed.Encode(code.Payload with { ZoneOpcode = 0xA1F7 });

    /// <summary>A build dated before <see cref="Bed.Build"/>.</summary>
    private const string Earlier = "2026.08.20.0000.0000";

    /// <summary>
    /// The same code naming the match with another shape: reply-state selector values the client never sends with the
    /// pop, so its match is never seen.
    /// </summary>
    private static SharedCode WithOtherSelectors(SharedCode code) => Bed.Encode(code.Payload with
    {
        Pop = code.Payload.Pop with { SelectorValues = code.Payload.Pop.SelectorValues!.Select(value => value + 1).ToArray() },
    });

    /// <summary>
    /// A shared profile this machine wrote from <paramref name="payload"/> for <paramref name="build"/>, recorded as settled
    /// - a whole duty recorded and every audit passed - unless told otherwise.
    /// </summary>
    private void SharedProfileFor(ShareCodePayload payload, string build, bool settled = true)
    {
        var path = SharedProfileFiles.Write(
            SharedProfileBuilder.Build(payload with { GameBuild = build }, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()),
            _bed.SharedRoot);
        if (settled)
        {
            Assert.True(_bed.Store.RecordSettled(Region.Cn, build, ProfileLoader.Load(path).ProfileSha256, Bed.Confirmed));
        }
    }

    /// <summary>This machine's own calibration of <paramref name="build"/>, as the player confirmed it from evening A's traffic.</summary>
    private void OwnCalibrationFor(string name, string build) =>
        LocalProfileWriter.Write(CalibrationTrafficCases.Derive(name), Bed.Template, build, Bed.Confirmed, _bed.LocalRoot);

    private static SharedGate GateOf(SharedCandidateSummary candidate, string message) =>
        Assert.Single(candidate.Criteria, criterion => criterion.Message == message).Gate;

    /// <summary>
    /// Starts a capture session and has every candidate judged at once, before any traffic: the observer exists from the
    /// session on, and a manual check claims the index again and evaluates what it offers. Nothing can pass yet.
    /// </summary>
    private async Task<string> StartJudged(LiveProtocolPipeline pipeline)
    {
        var session = _bed.Start(pipeline);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        return session;
    }

    private static SharedCandidateSummary Find(IEnumerable<SharedCandidateSummary> candidates, SharedCode code) =>
        Assert.Single(candidates, candidate => candidate.Sha12 == code.Sha[..12]);

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    /// <summary>A run row recorded under the shared profile id now, with its creation revision, as the processor writes them.</summary>
    private MentorRun SeedRunUnderSharedProfile()
    {
        var run = TestDatabase.Run(
                source: RunSource.AutoNetwork, result: RunResult.CancelledBeforeEntry, enteredAt: _bed.Db.Clock.UtcNow.AddMinutes(2)) with
            {
                ProtocolProfileId = ProfileId,
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
        return run;
    }

    /// <summary>
    /// §20.7, first case, with the prior this machine computed itself (security review 2026-10-15): two codes differ only on
    /// the match, one account each. The calibration this machine verified for the previous build had the shape of one of
    /// them; the repository's own <c>shape_prior</c> sides with the other, and does not count.
    /// </summary>
    [Fact]
    public async Task ATiedMatchGoesToTheShapeThisMachineVerifiedLastBuildAndToItsRivalOnceThatOneIsContradicted()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var favoured = WithOtherSelectors(truth);
        SharedProfileFor(favoured.Payload, Earlier);
        _bed.PublishListed(
            new Bed.Listing(favoured, Conflicting: true), new Bed.Listing(truth, Conflicting: true, ShapePrior: true));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        // Both are in dispute on the match only; this machine's prior decides the tie, the index's is ignored.
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        var first = Find(candidates, favoured);
        var second = Find(candidates, truth);
        Assert.Equal(new[] { CalibratedShape.PopName }, first.Disputed);
        Assert.True(first.Preferred);
        Assert.True(first.LocalShapePrior);
        Assert.Equal(new[] { CalibratedShape.PopName }, second.Disputed);
        Assert.False(second.Preferred);
        Assert.False(second.LocalShapePrior);

        var session = await StartJudged(pipeline);
        candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(SharedGate.Audit, GateOf(Find(candidates, favoured), CalibratedShape.PopName));
        Assert.Equal(SharedGate.Audit, GateOf(Find(candidates, favoured), CalibratedShape.TerritoryName));
        Assert.Equal(SharedGate.Required, GateOf(Find(candidates, truth), CalibratedShape.PopName));
        Assert.Equal(SharedGate.Required, GateOf(Find(candidates, truth), CalibratedShape.TerritoryName));

        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        // Bound on the login burst, as on patch day; what decided it is kept with the profile in use.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var inUse = pipeline.CalibrationStatus().Shared.Candidates[0];
        Assert.Equal(favoured.Sha[..12], inUse.Sha12);
        Assert.Equal(SharedCandidateStatus.InUse, inUse.Status);
        Assert.Equal(new[] { CalibratedShape.PopName }, inUse.Disputed);
        Assert.True(inUse.Preferred);
        Assert.True(inUse.LocalShapePrior);
        var recorded = SeedRunUnderSharedProfile();
        var requests = _bed.Transport.Requests.Count;

        // Its match never arrives on the declared opcode: one healthy evening says so, which is not yet enough.
        Bed.Feed(pipeline, session, AfterLogin(evening));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
        Assert.False(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, favoured.Sha));
        Assert.Equal(favoured.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);

        // A day later on the wall clock - past the six hours after which a download would be due again.
        _bed.Db.Clock.UtcNow += TimeSpan.FromHours(24);
        _bed.Play(pipeline, Bed.Evening(), hour: 24);
        await Bed.Idle(pipeline);

        // A second healthy evening: withdrawn, what it recorded is pending review, and the rival takes over.
        Assert.True(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, favoured.Sha));
        Assert.True(new RunRepository(_bed.Db.Database).Get(recorded.RunId)!.PendingReview);
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Verified, shared.Phase);
        Assert.Equal(ProfileId, shared.ProfileId);
        var takenOver = shared.Candidates[0];
        Assert.Equal(truth.Sha[..12], takenOver.Sha12);
        Assert.Contains(takenOver.Status, new[] { SharedCandidateStatus.InUse, SharedCandidateStatus.Proven });
        Assert.Empty(takenOver.Disputed);
        Assert.True(takenOver.Preferred);
        Assert.Equal(SharedCandidateStatus.Rejected, Find(shared.Candidates, favoured).Status);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        Assert.True(File.Exists(_bed.SharedProfilePath));

        // The rival came back off the disk: re-offering the stored codes sent nothing, although a download would have been
        // due by now (docs/privacy-boundary.md §8.2; review 2026-10-15, L4).
        Assert.Equal(requests, _bed.Transport.Requests.Count);
    }

    /// <summary>
    /// §20.7, second case: the same tie on a machine that verified nothing for an earlier build - nothing it trusts decides
    /// it, so neither binds at login (ON1-1), even though the repository's index gives one of them the shape prior.
    /// </summary>
    [Fact]
    public async Task OnAFreshMachineATiedMatchMakesBothCodesSeeTheirMatchAndDutyEntryFirstWhateverTheIndexSays()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        _bed.PublishListed(new Bed.Listing(other, Conflicting: true, ShapePrior: true), new Bed.Listing(truth, Conflicting: true));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = await StartJudged(pipeline);
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(new[] { CalibratedShape.PopName }, candidate.Disputed);
            Assert.False(candidate.Preferred);
            Assert.Null(candidate.LocalShapePrior);
            Assert.Equal(SharedGate.Required, GateOf(candidate, CalibratedShape.PopName));
        });

        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(File.Exists(_bed.SharedProfilePath));

        Bed.Feed(pipeline, session, UpToEntry(evening));
        await Bed.Idle(pipeline);

        // The code whose match behaved binds, and the staged duty is recorded.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(truth.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }

    /// <summary>
    /// The prior is read from this machine's own calibration of the previous build as well. A profile of a build released
    /// the same day as the running one cannot be ordered against it and is not read, whatever it says.
    /// </summary>
    [Fact]
    public async Task ThisMachinesOwnCalibrationOfTheLatestEarlierBuildDecidesATiedMatchAndASameDayBuildDoesNot()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        OwnCalibrationFor(CalibrationTrafficCases.ReplyState, Earlier);
        SharedProfileFor(other.Payload, "2026.09.01.0001.0000");
        _bed.PublishListed(new Bed.Listing(other, ShapePrior: true), new Bed.Listing(truth));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = await StartJudged(pipeline);
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.True(Find(candidates, truth).LocalShapePrior);
        Assert.True(Find(candidates, truth).Preferred);
        Assert.False(Find(candidates, other).LocalShapePrior);
        Assert.False(Find(candidates, other).Preferred);
        Assert.Equal(SharedGate.Audit, GateOf(Find(candidates, truth), CalibratedShape.PopName));
        Assert.Equal(SharedGate.Required, GateOf(Find(candidates, other), CalibratedShape.PopName));

        Bed.Feed(pipeline, session, Login(Bed.Evening()));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(truth.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
    }

    /// <summary>
    /// What this machine has not verified is no prior: a shared profile of an earlier build still being checked - not
    /// settled - and a settled one that names the match another way leave both codes without one, so neither binds at login.
    /// </summary>
    [Fact]
    public async Task AnEarlierProfileNotSettledHereOrOfAnotherKindOfMatchGivesNoPrior()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        SharedProfileFor(_bed.CodeFromEveningA(CalibrationTrafficCases.MarkerOffset).Payload, Earlier);
        SharedProfileFor(other.Payload, "2026.08.25.0000.0000", settled: false);
        _bed.PublishListed(new Bed.Listing(other), new Bed.Listing(truth));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = await StartJudged(pipeline);
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.All(candidates, candidate =>
        {
            Assert.Null(candidate.LocalShapePrior);
            Assert.False(candidate.Preferred);
            Assert.Equal(SharedGate.Required, GateOf(candidate, CalibratedShape.PopName));
        });

        Bed.Feed(pipeline, session, Login(Bed.Evening()));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
    }

    /// <summary>§20.7, third case: two codes differ on the zone marker; two accounts stand behind one, one behind the other.</summary>
    [Fact]
    public async Task ACodeMoreAccountsStandBehindBindsOnTheLoginBurstWhenTheZoneMarkerIsDisputed()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithBogusZone(truth);
        _bed.PublishListed(
            new Bed.Listing(truth, Conflicting: true, Submitters: 2), new Bed.Listing(other, Conflicting: true, Submitters: 1));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        var many = Find(candidates, truth);
        var few = Find(candidates, other);
        Assert.Equal(new[] { CalibratedShape.ZoneName }, many.Disputed);
        Assert.True(many.Preferred);
        Assert.Equal(new[] { CalibratedShape.ZoneName }, few.Disputed);
        Assert.False(few.Preferred);

        var session = await StartJudged(pipeline);
        candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(SharedGate.Audit, GateOf(Find(candidates, truth), CalibratedShape.PopName));
        Assert.Equal(SharedGate.Required, GateOf(Find(candidates, other), CalibratedShape.PopName));

        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Equal(truth.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);

        // Recording live from the first match on.
        Bed.Feed(pipeline, session, UpToEntry(evening));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }

    /// <summary>
    /// Review 2026-10-15: gates are judged over the candidates left once every contradiction of the round is in. The code
    /// listed first is preferred only thanks to a supporter whose second contradiction arrives in the very round in which
    /// the first code's zone marker is first seen; without the supporter its job is tied with a third code's, so it waits
    /// for its match instead of binding on the zone marker alone.
    /// </summary>
    [Fact]
    public async Task AGateIsJudgedOnlyOnceTheRoundsContradictionsHaveDroppedTheirCodes()
    {
        var first = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var supporter = WithBogusPop(first);
        var third = Bed.Encode(first.Payload with { JobOpcode = 0x7777 });

        // One healthy capture session of an earlier run of the Collector already contradicted the supporter.
        _bed.Store.RecordContradiction(
            Region.Cn, Bed.Build, Bed.TemplateSha, supporter.Sha, "an-earlier-capture-session", _bed.Db.Clock.UtcNow);
        _bed.PublishListed(new Bed.Listing(first, ShapePrior: true), new Bed.Listing(supporter), new Bed.Listing(third));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        // Job: two accounts behind the first code's, one behind the third's. Match: two behind the first code's pop.
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(first.Sha[..12], candidates[0].Sha12);
        Assert.Equal(new[] { CalibratedShape.JobName, CalibratedShape.PopName }, Find(candidates, first).Disputed);
        Assert.True(Find(candidates, first).Preferred);
        Assert.False(Find(candidates, supporter).Preferred);
        Assert.False(Find(candidates, third).Preferred);

        // No login burst, and a queue that is never matched: the first zone marker the session shows is the duty entry's,
        // and the same duty entry is the supporter's second contradiction.
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.From(Bed.Evening(pop: false), 30_000));
        await Bed.Idle(pipeline);

        Assert.True(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, supporter.Sha));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        var left = pipeline.CalibrationStatus().Shared.Candidates;
        var waiting = Find(left, first);
        Assert.Equal(new[] { CalibratedShape.JobName }, waiting.Disputed);
        Assert.False(waiting.Preferred);
        Assert.Equal(SharedGate.Required, GateOf(waiting, CalibratedShape.PopName));
        Assert.Equal(SharedCandidateStatus.Verifying, waiting.Status);
        Assert.Equal(SharedCandidateStatus.Rejected, Find(left, supporter).Status);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Review 2026-10-15: the profile of the chosen code is written off the gate, and an index read meanwhile can change the
    /// consensus. The code binds under the gate it was chosen under - its match and duty entry still audited while it
    /// records - and not under whatever a re-judgement in the meantime made of it.
    /// </summary>
    [Fact]
    public async Task AnIndexReadWhileTheChosenCodeIsWrittenDoesNotChangeTheGateItBindsUnder()
    {
        var chosen = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var rival = WithBogusPop(chosen);
        _bed.Publish(chosen);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        // While the chosen code's profile is being written, a new index lists a rival two accounts submitted.
        var held = 0;
        _bed.BeforeReload = () =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0)
            {
                return;
            }

            _bed.PublishListed(new Bed.Listing(chosen), new Bed.Listing(rival, Submitters: 2));
            Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
            Assert.True(SpinWait.SpinUntil(
                () => pipeline.CalibrationStatus().Shared.Candidates.Any(item => item.Sha12 == rival.Sha[..12]),
                TimeSpan.FromSeconds(30)));
        };

        var evening = Bed.Evening().ToArray();
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);
        Assert.Equal(1, held);

        // Bound on the login burst as chosen, with the match and the duty entry still to be seen while it records.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var shared = pipeline.CalibrationStatus().Shared;
        var inUse = shared.Candidates[0];
        Assert.Equal(chosen.Sha[..12], inUse.Sha12);
        Assert.True(shared.AuditPending);
        Assert.True(inUse.AuditPending);
        Assert.Equal(SharedGate.Audit, GateOf(inUse, CalibratedShape.PopName));
        Assert.Equal(SharedGate.Audit, GateOf(inUse, CalibratedShape.TerritoryName));

        // And it reports the consensus it was bound under, not the one the rival made of it meanwhile (review 2026-10-15, L3).
        Assert.Empty(inUse.Disputed);
        Assert.True(inUse.Preferred);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
    }

    /// <summary>The last index this machine read for <paramref name="build"/> revokes <paramref name="sha"/>.</summary>
    private void RevokeInStore(string build, string sha) => Assert.True(_bed.Store.RecordFetch(
        Region.Cn, build, Bed.TemplateSha,
        new SharedCalibrationFetchResult(
            SharedFetchStatus.NoneForBuild,
            new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Ok, 200) },
            Array.Empty<SharedCalibrationCandidate>(), Array.Empty<SharedCodeDiscard>(), Array.Empty<SharedIndexSkip>(), new[] { sha }),
        _bed.Db.Clock.UtcNow).Recorded);

    /// <summary>
    /// Review 2026-10-15 (M1): the settled mark survives a revocation, so a settled profile of the previous build whose code
    /// the repository withdrew would otherwise still vouch for its shape.
    /// </summary>
    [Fact]
    public async Task ASettledProfileOfThePreviousBuildWhoseCodeWasRevokedIsNoPrior()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        SharedProfileFor(other.Payload, Earlier);
        RevokeInStore(Earlier, ShareCode.Sha256(other.Payload with { GameBuild = Earlier }));
        _bed.PublishListed(new Bed.Listing(other), new Bed.Listing(truth));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = await StartJudged(pipeline);
        Assert.All(pipeline.CalibrationStatus().Shared.Candidates, candidate =>
        {
            Assert.Null(candidate.LocalShapePrior);
            Assert.Equal(SharedGate.Required, GateOf(candidate, CalibratedShape.PopName));
        });
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
    }

    /// <summary>
    /// The settled mark names one profile document. A later document written to the same build's path - another code,
    /// not yet settled - is not vouched for by it.
    /// </summary>
    [Fact]
    public async Task ASettledMarkForAnEarlierDocumentOfTheBuildDoesNotVouchForTheDocumentThatReplacedIt()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        SharedProfileFor(truth.Payload, Earlier);
        SharedProfileFor(other.Payload, Earlier, settled: false);
        _bed.PublishListed(new Bed.Listing(other), new Bed.Listing(truth));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        Assert.All(pipeline.CalibrationStatus().Shared.Candidates, candidate => Assert.Null(candidate.LocalShapePrior));
    }

    /// <summary>
    /// A pasted code gets this machine's prior too, read off the gate when it is imported; and when the same code arrives
    /// in a download later, the prior is read again - here after the machine settled a calibration of the previous build.
    /// </summary>
    [Fact]
    public async Task ThePriorIsReadForAPastedCodeAndReadAgainWhenTheSameCodeIsDownloaded()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = WithOtherSelectors(truth);
        _bed.Publish();
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(truth.Code).Outcome);
        Assert.Null(Find(pipeline.CalibrationStatus().Shared.Candidates, truth).LocalShapePrior);

        SharedProfileFor(truth.Payload, Earlier);
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(other.Code).Outcome);
        var pasted = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.False(Find(pasted, other).LocalShapePrior);
        Assert.Null(Find(pasted, truth).LocalShapePrior);

        _bed.PublishListed(new Bed.Listing(truth), new Bed.Listing(other));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        var downloaded = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.True(Find(downloaded, truth).LocalShapePrior);
        Assert.True(Find(downloaded, truth).Preferred);
        Assert.False(Find(downloaded, other).LocalShapePrior);
        Assert.Equal(SharedCandidateProvenance.Published, Find(downloaded, truth).Provenance);
    }

    /// <summary>
    /// A queue-inferred code waiting for the player's consent is bound under the consensus that holds when the player
    /// accepts, not the one that held when it first passed: an index read in between that lets a rival outvote it moves
    /// the bind to the rival.
    /// </summary>
    [Fact]
    public async Task TheConsensusThatHoldsWhenThePlayerAcceptsQueueInferenceDecidesTheBind()
    {
        var waiting = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        var rival = WithBogusPop(waiting);
        _bed.Publish(waiting);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Login(Bed.Evening()));
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, pipeline.CalibrationStatus().Shared.Phase);
        Assert.Equal(SharedCandidateStatus.AwaitingConsent, Find(pipeline.CalibrationStatus().Shared.Candidates, waiting).Status);

        _bed.PublishListed(new Bed.Listing(waiting), new Bed.Listing(rival, Submitters: 2));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.False(Find(candidates, waiting).Preferred);
        Assert.Equal(SharedCandidateStatus.Verifying, Find(candidates, waiting).Status);
        Assert.Equal(SharedCandidateStatus.AwaitingConsent, Find(candidates, rival).Status);

        Assert.Equal(SharedConsentOutcome.Accepted, pipeline.AcceptSharedQueueInference());
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var inUse = pipeline.CalibrationStatus().Shared.Candidates[0];
        Assert.Equal(rival.Sha[..12], inUse.Sha12);
        Assert.Equal(new[] { CalibratedShape.PopName }, inUse.Disputed);
        Assert.True(inUse.Preferred);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);
    }

    /// <summary>
    /// When several codes pass at once, the one the consensus prefers is bound - ahead of one more accounts submitted
    /// that is outvoted on a disputed field by codes of every kind together.
    /// </summary>
    [Fact]
    public async Task OfTheCodesThatPassedThePreferredOneBindsAheadOfOneWithMoreSubmitters()
    {
        var preferred = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var outvoted = Bed.Encode(preferred.Payload with { JobOpcode = 0x7777 });
        var queue = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        Assert.Equal(preferred.Payload.JobOpcode, queue.Payload.JobOpcode);
        _bed.PublishListed(
            new Bed.Listing(outvoted, Submitters: 2), new Bed.Listing(queue, Submitters: 2), new Bed.Listing(preferred, Submitters: 1));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        // The job: three accounts behind one value across both kinds of code, two behind the other.
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.Equal(3, candidates.Count);
        Assert.Equal(preferred.Sha[..12], candidates[^1].Sha12);
        Assert.All(candidates, candidate => Assert.Equal(new[] { CalibratedShape.JobName }, candidate.Disputed));
        Assert.False(Find(candidates, outvoted).Preferred);
        Assert.True(Find(candidates, queue).Preferred);
        Assert.True(Find(candidates, preferred).Preferred);

        var evening = Bed.Evening().ToArray();
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Login(evening));
        Bed.Feed(pipeline, session, UpToEntry(evening));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(preferred.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }

    /// <summary>A shared profile adopted from disk was bound by an earlier process: nothing is known to dispute it.</summary>
    [Fact]
    public async Task AProfileAdoptedFromDiskReportsNoDisputeAndNoPrior()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()), _bed.SharedRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        var adopted = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(code.Sha[..12], adopted.Sha12);
        Assert.Empty(adopted.Disputed);
        Assert.True(adopted.Preferred);
        Assert.Null(adopted.LocalShapePrior);
    }
}
