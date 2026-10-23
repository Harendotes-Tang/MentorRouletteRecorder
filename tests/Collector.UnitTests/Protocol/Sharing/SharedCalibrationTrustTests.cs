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
/// Which published code may bind on the login burst alone (plan §18.3; audit 2026-10-03, ON1-1; plan §20.5). The
/// patch-day path stays: with nothing usable on this machine, the best-attested published code binds at login and its
/// match and duty entry are audited while it records. Everything else must have its match and its duty entry seen to
/// behave first: a code that would displace a calibration already usable here, a code another candidate outranks by
/// submitters, and a code the other candidates outvote on a field in dispute - so a code's kind does not let one or two
/// accounts put their code ahead of one many players submitted. Since §20 the repository's conflict mark alone no longer
/// holds a code back: this machine compares the same codes field by field (SharedCalibrationConsensusGateTests).
/// </summary>
public sealed class SharedCalibrationTrustTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    /// <summary>The login burst and the idle minute after it: nothing queued yet.</summary>
    private static DecodedMessage[] Login(IEnumerable<DecodedMessage> evening) => Bed.Before(evening, 30_000);

    /// <summary>Everything after the login burst.</summary>
    private static DecodedMessage[] AfterLogin(IEnumerable<DecodedMessage> evening) => Bed.From(evening, 30_000);

    /// <summary>The same code with its match message moved to an opcode the client never sends it on.</summary>
    private static SharedCode WithBogusPop(SharedCode code) =>
        Bed.Encode(code.Payload with { Pop = code.Payload.Pop with { Opcode = 0x7777 } });

    [Fact]
    public async Task APublishedCodeDoesNotDisplaceTheMachinesOwnQueueInferredProfileBeforeItsMatchIsSeenToBehave()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(WithBogusPop(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState)));
        var pipeline = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        // The zone marker vouches for the build, which is all a published code needs when nothing records yet.
        // Here the machine's own calibration records, so the code waits for its match.
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        var waiting = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedCandidateProvenance.Published, waiting.Provenance);
        Assert.Equal(SharedGate.Required, Assert.Single(waiting.Criteria, item => item.Message == CalibratedShape.PopName).Gate);

        Bed.Feed(pipeline, session, AfterLogin(evening));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        // The match never arrived on the code's opcode: the machine's own profile recorded the evening's duty.
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
        Assert.False(File.Exists(_bed.SharedProfilePath));
    }

    [Fact]
    public async Task APastedCodeThatWouldReplaceAProfileInForceIsToldItWaitsForAQueue()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var pasted = pipeline.ImportCalibrationCode(code.Code);

        Assert.Equal(SharedImportOutcome.Applied, pasted.Outcome);
        Assert.Equal(SharedCandidateProvenance.Published, pasted.Provenance);
        Assert.Contains("申请随机任务并进出副本", pasted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("登录时", pasted.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan §20.5: the gate no longer reads the repository's conflict mark on its own. The mark says some code of the build
    /// disagrees with this one; here no such code reaches this machine (revoked, rejected, or never downloaded), so nothing
    /// is in dispute and the mark holds nothing back. Until §20 it sent the code to verifying its match first, and this
    /// test pinned that.
    /// </summary>
    [Fact]
    public async Task TheConflictMarkAloneNoLongerHoldsAPublishedCodeBackFromTheLoginBurst()
    {
        _bed.PublishListed(new Bed.Listing(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState), Conflicting: true));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var marked = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Empty(marked.Disputed);
        Assert.True(marked.Preferred);

        // Judged once the session has an observer: the match is audited, not required.
        var session = _bed.Start(pipeline);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        marked = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(SharedGate.Audit, Assert.Single(marked.Criteria, item => item.Message == CalibratedShape.PopName).Gate);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        // Bound on the login burst; the match is audited while it records.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.True(pipeline.CalibrationStatus().Shared.AuditPending);

        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 200_000), 30_000));
        await Bed.Idle(pipeline);

        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }

    [Fact]
    public async Task AFewAccountsCannotPutACodeThatReadsTheMatchAheadOfOneManyPlayersSubmitted()
    {
        var many = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        var few = WithBogusPop(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        _bed.PublishListed(new Bed.Listing(many, Submitters: 3), new Bed.Listing(few, Submitters: 1));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);

        // Reading the server's match does not on its own outrank three submitters: the well-attested code passed at
        // login and waits for the player's consent; the other waits for its match.
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, shared.Phase);
        Assert.Equal(SharedCandidateStatus.AwaitingConsent, Assert.Single(shared.Candidates, item => item.Sha12 == many.Sha[..12]).Status);

        Assert.Equal(SharedConsentOutcome.Accepted, pipeline.AcceptSharedQueueInference());
        await Bed.Idle(pipeline);
        Bed.Feed(pipeline, session, AfterLogin(evening));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        var inUse = pipeline.CalibrationStatus().Shared.Candidates[0];
        Assert.Equal(many.Sha[..12], inUse.Sha12);
        Assert.Equal(CalibrationMatchSource.QueueRequest, inUse.MatchSource);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }

    [Fact]
    public async Task TheConflictMarkDoesNotLetALesserCodeOfAnotherKindBindAheadOfTheBetterAttestedOne()
    {
        // An honest code three players submitted, marked conflicting because someone published a second, differing
        // code of the same kind; and a code of another kind from one account, which no conflict mark touches.
        var honest = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var lesser = WithBogusPop(_bed.CodeFromEveningA(CalibrationTrafficCases.MarkerOffset));
        _bed.PublishListed(new Bed.Listing(honest, Conflicting: true, Submitters: 3), new Bed.Listing(lesser, Submitters: 1));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(2, pipeline.CalibrationStatus().Shared.Candidates.Count);

        // The two name the match in different ways and agree on everything else, so nothing is in dispute; the code three
        // accounts submitted binds at login despite the mark (§20), and the lesser one never gets the chance.
        var candidates = pipeline.CalibrationStatus().Shared.Candidates;
        Assert.All(candidates, candidate => Assert.Empty(candidate.Disputed));

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Login(evening));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(honest.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);

        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 200_000), 30_000));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(honest.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
    }
}
