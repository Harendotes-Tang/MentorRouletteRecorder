using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Shared calibration inside the live pipeline: a build with no profile downloads a code, stages what it
/// would record, verifies it against the login burst and the first match, and only then binds it inside the
/// running session - losing nothing while the profile is written, recording nothing before it passes, and
/// leaving local calibration untouched when nothing can be downloaded.
/// </summary>
public sealed class SharedCalibrationPipelineTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static Dictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public async Task ADownloadedCodeVerifiesOnLiveTrafficAndRecordsInsideTheSameSession()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var verifying = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Verifying, verifying.Phase);
        Assert.Equal(SharedCandidateSource.Downloaded, Assert.Single(verifying.Candidates).Source);
        Assert.Equal(SharedFetchStatus.Ok, verifying.LastFetchStatus);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);

        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        await Bed.Idle(pipeline);

        var current = pipeline.Current;
        Assert.Equal(ProfileStatus.Verified, current.Status);
        Assert.Equal(ProfileOrigin.Shared, current.Origin);
        Assert.Equal(SharedProfileBuilder.ProfileIdFor(Region.Cn, Bed.Build), current.ProfileId);
        Assert.True(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(current.ProfileId, new CaptureSessionRepository(_bed.Db.Database).Get(session)!.ProtocolProfileId);
        // The pop and the duty entry arrive before the profile exists; once drained they form a complete run.
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(9, run.MentorRouletteId);
        Assert.NotNull(run.EnteredAtUtc);
        Assert.Equal(current.ProfileId, run.ProtocolProfileId);
        var bound = pipeline.CalibrationStatus();
        Assert.Equal(SharedCalibrationPhase.Verified, bound.Shared.Phase);
        Assert.NotNull(bound.Shared.BoundAtUtc);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(bound.Shared.Candidates).Status);
        // Watched until it records a complete duty; meanwhile a ready local draft is not offered to the player.
        Assert.True(pipeline.CalibrationArmed);
        Assert.NotEqual(CalibrationState.Ready, bound.State);

        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));

        Assert.NotNull(Assert.Single(RunsOf(session)).EndedAtUtc);
        var done = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Done, done.State);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(done.Shared.Candidates).Status);
    }

    [Fact]
    public async Task ADutyEntryThatArrivesWhileTheSharedProfileIsBeingWrittenIsNotLost()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var services = _bed.Services() with
        {
            WriteSharedProfile = built =>
            {
                writing.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return SharedProfileFiles.Write(built, _bed.SharedRoot);
            },
        };
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();

        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(30)), "the candidate never passed");

        // The write is held open: the first duty ends and a second mentor roulette enters its duty meanwhile.
        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));
        Bed.Feed(pipeline, session, Bed.SecondDuty());
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Empty(RunsOf(session));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);

        release.Set();
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        var runs = RunsOf(session).OrderBy(run => run.EnteredAtUtc).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.NotNull(runs[0].EndedAtUtc);
        Assert.NotNull(runs[1].EnteredAtUtc);
        Assert.Null(runs[1].EndedAtUtc);
    }

    /// <summary>
    /// The CN clear arrives while the shared profile is still being written: the staging parses it with the
    /// candidate's profile, the drain hands it over in its place, and the drained duty ends as 通关. That
    /// complete duty proves the profile at the clear, before the player has even left, exactly as a profile
    /// with a result message would; and the drained clear is counted like a live one (duty-result design,
    /// section 4 item 8).
    /// </summary>
    [Fact]
    public async Task AClearStagedBeforeASharedBindCompletesTheDrainedRun()
    {
        using var release = new ManualResetEventSlim();
        var (pipeline, session) = await StartWithTheBindHeldAsync(release);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        var clear = ClearMessage(200_000);
        Bed.Feed(pipeline, session, new[] { clear });
        Assert.Empty(RunsOf(session));

        release.Set();
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.False(run.PendingReview);
        Assert.Equal(clear.ObservedAtUtc, run.EndedAtUtc);
        var done = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Done, done.State);
        Assert.Equal(SharedCandidateStatus.Proven, Assert.Single(done.Shared.Candidates).Status);
        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
    }

    /// <summary>
    /// The counters belong to the capture session, not to the parser in force. The revocation arrives
    /// mid-duty and is held until the clear ends the run, as every withdrawal is - so the withdrawal settles
    /// at the clear itself. The session that now records nothing still reports the clear that completed its
    /// run; the exit that follows reaches no parser, and no exit line is written for it.
    /// </summary>
    [Fact]
    public async Task AWithdrawalAtTheClearKeepsTheSessionsClearCounters()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening().ToArray(), 200_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        _bed.PublishRevoked(code);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);

        Bed.Feed(pipeline, session, new[] { ClearMessage(200_000) });
        await Bed.Idle(pipeline);

        Assert.Equal(RunResult.Completed, Assert.Single(RunsOf(session)).Result);
        Assert.Null(pipeline.CalibrationStatus().Shared.ProfileId);
        Bed.Feed(pipeline, session, Bed.From(Bed.Evening().ToArray(), 200_000));

        var signal = Assert.Single(notes);
        Assert.True(signal.Completed);
        Assert.Equal((1, 1), (signal.Signals, signal.Completions));
        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
    }

    /// <summary>
    /// The clear and then the loss of the game connection are staged while the shared profile is written; the
    /// drain completes the run and then hands the loss over. The zone change after it is no longer known to be
    /// the exit from that duty, so no exit line is written for it.
    /// </summary>
    [Fact]
    public async Task AConnectionLostAfterAStagedClearDropsTheExitLine()
    {
        using var release = new ManualResetEventSlim();
        var (pipeline, session) = await StartWithTheBindHeldAsync(release);
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        Bed.Feed(pipeline, session, new[] { ClearMessage(200_000) });
        pipeline.OnConnectionLost(session);

        release.Set();
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));

        Assert.Equal(RunResult.Completed, Assert.Single(RunsOf(session)).Result);
        Assert.True(Assert.Single(notes).Completed);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
    }

    /// <summary>The clear of the invented duty 0xF00D, on the zone connection and an opcode no profile declares.</summary>
    private static Protocol.Decoded.DecodedMessage ClearMessage(long monoMs)
    {
        var body = new byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body, 0x006D);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0xF00D);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0x8003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0x40000003);
        return CalibrationObserverTests.Message(Protocol.Decoded.MessageDirection.Inbound, 0x0204, body, monoMs);
    }

    /// <summary>
    /// Audit 2026-10-03, CS3a-X1. While nothing is bound, a direction given up is staged as a gap in
    /// sequence, as an overflow is: the duty the staging replays after the bind ends where the hole is,
    /// at LOW and pending review, instead of being entered across it.
    /// </summary>
    [Fact]
    public async Task ADirectionLostBeforeASharedBindIsReplayedAsAGapInItsPlace()
    {
        using var release = new ManualResetEventSlim();
        var (pipeline, session) = await StartWithTheBindHeldAsync(release);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 122_000));
        pipeline.OnDirectionDamaged(session, "zone", Protocol.Decoded.MessageDirection.Inbound);
        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 200_000), 122_000));

        release.Set();
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(RunResult.CancelledBeforeEntry, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.True(run.PendingReview);
    }

    /// <summary>
    /// Audit 2026-10-03, S33-5. A direction lost while nothing is bound is staged with its connection, and
    /// judged when the bind drains it the way the replay after a confirmed local calibration judges it: against
    /// the connections and directions the events drained before it came from. Every staged event of this duty
    /// came in on the zone connection from the server, so the chat server's connection, or the zone
    /// connection's outbound direction, losing a direction costs the duty nothing, and it is entered.
    /// </summary>
    [Theory]
    [InlineData("chat", Protocol.Decoded.MessageDirection.Inbound)]
    [InlineData("zone", Protocol.Decoded.MessageDirection.Outbound)]
    public async Task ADirectionLostBeforeASharedBindThatNoStagedEventCameFromIsNoGap(
        string connection, Protocol.Decoded.MessageDirection direction)
    {
        using var release = new ManualResetEventSlim();
        var (pipeline, session) = await StartWithTheBindHeldAsync(release);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 122_000));
        pipeline.OnDirectionDamaged(session, connection, direction);
        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 200_000), 122_000));

        release.Set();
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(9, run.MentorRouletteId);
        Assert.NotNull(run.EnteredAtUtc);
        Assert.Null(run.EndedAtUtc);
    }

    /// <summary>
    /// Audit 2026-10-03, CS3a-X1 and S33-5. Once a shared bind has drained its staging, the connections and
    /// directions the drained events came from are known, as they are after the replay of a confirmed local
    /// calibration: losing one of them ends the replayed duty INTERRUPTED at LOW, and losing a direction of the
    /// chat server's connection leaves it alone.
    /// </summary>
    [Theory]
    [InlineData("zone", RunResult.Interrupted)]
    [InlineData("chat", null)]
    public async Task ADirectionLostRightAfterASharedBindIsJudgedByWhereTheDrainedEventsCameFrom(
        string connection, RunResult? expected)
    {
        using var release = new ManualResetEventSlim();
        var (pipeline, session) = await StartWithTheBindHeldAsync(release);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening().ToArray(), 200_000));
        release.Set();
        await Bed.Idle(pipeline);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        pipeline.OnDirectionDamaged(session, connection, Protocol.Decoded.MessageDirection.Inbound);

        var run = Assert.Single(RunsOf(session));
        if (expected is { } result)
        {
            Assert.Equal(result, run.Result);
            Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        }
        else
        {
            Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
            Assert.Null(run.EndedAtUtc);
        }
    }

    /// <summary>
    /// A session verifying a published code whose shared profile is held at the write until the test sets
    /// <paramref name="release"/>, so everything fed before that is staged and the bind replays all of it.
    /// </summary>
    private async Task<(LiveProtocolPipeline Pipeline, string Session)> StartWithTheBindHeldAsync(ManualResetEventSlim release)
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var services = _bed.Services() with
        {
            WriteSharedProfile = built =>
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return SharedProfileFiles.Write(built, _bed.SharedRoot);
            },
        };
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        return (pipeline, _bed.Start(pipeline));
    }

    /// <summary>
    /// A code pasted (or downloaded) after login stages nothing of the login burst, and on a build whose duty bursts
    /// do not repeat the job the staging holds no job at all. The observer has read the job at login, so the first
    /// record after the shared bind carries it - as it does after a local confirmation - instead of 职业未知.
    /// </summary>
    [Fact]
    public async Task AJobReadBeforeTheCodeArrivedIsOnTheFirstSharedRecord()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var session = _bed.Start(pipeline);
        var evening = Bed.Evening()
            .Where(message => message.Opcode != CalibrationTrafficCases.Job || message.Mono < TimeSpan.FromMilliseconds(10_000))
            .Select(message => message.Opcode == CalibrationTrafficCases.Job
                ? message with { Payload = CalibrationObserverTests.Bytes(16, (0, 30)) }
                : message)
            .ToArray();

        Bed.Feed(pipeline, session, Bed.Before(evening, 10_000));
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);
        Bed.Feed(pipeline, session, Bed.From(Bed.Before(evening, 200_000), 10_000));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(30, Assert.Single(RunsOf(session)).JobId);
    }

    [Fact]
    public async Task WhenEverySourceFailsCalibrationCarriesOnAndALocalConfirmationStillWorks()
    {
        _bed.Transport.Unreachable = true;
        var pipeline = _bed.Pipeline(_bed.Services());

        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var status = pipeline.CalibrationStatus();
        Assert.Equal(SharedCalibrationPhase.Unavailable, status.Shared.Phase);
        Assert.Equal(SharedFetchStatus.IndexUnavailable, status.Shared.LastFetchStatus);
        Assert.Equal(SharedCalibrationClient.SourceOrder.Count, status.Shared.LastIndexAttempts.Count);
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Waiting, status.State);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, CalibrationObserverTests.Session1());
        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);

        var result = pipeline.ConfirmCalibration(AllCorrect(ready));

        Assert.True(result.BoundInSession);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        await Bed.Idle(pipeline);
    }

    [Fact]
    public async Task AQueueInferredCodeWaitsForConsentAndThenBinds()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedConsentOutcome.NothingToAccept, pipeline.AcceptSharedQueueInference());

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(pop: false), 200_000));
        await Bed.Idle(pipeline);

        var waiting = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, waiting.Phase);
        Assert.Equal(SharedCandidateStatus.AwaitingConsent, Assert.Single(waiting.Candidates).Status);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Empty(RunsOf(session));

        Assert.Equal(SharedConsentOutcome.Accepted, pipeline.AcceptSharedQueueInference());
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.NotNull(Assert.Single(RunsOf(session)).EnteredAtUtc);
        // Asked once per machine per build: the answer is kept with this machine's other settings.
        var remembered = new SettingsRepository(_bed.Db.Database, _bed.Db.Clock).GetSetting(SharedCalibrationSession.QueueConsentSetting);
        Assert.Contains("cn/" + Bed.Build, remembered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A real machine: the player had retired her local profile so she could use the code a
    /// friend sent her, and the client downloaded the published queue-inferred one first. It
    /// passed, so the card parked on "同意一次" - and the two answers on offer were to bind that
    /// weaker code or to refuse every shared code for the build. The Collector never refused an
    /// import in this phase; only the desktop hid the button. This pins both halves: the import
    /// is taken, and a code that reads the server's own match outranks one that infers it.
    /// </summary>
    [Fact]
    public async Task ACodePastedWhileConsentIsPendingIsTakenAndOutranksTheInferredOne()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 124_000));
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, pipeline.CalibrationStatus().Shared.Phase);
        Assert.False(File.Exists(_bed.SharedProfilePath));

        // The friend's code, which reads the server's own match message.
        var better = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(better.Code).Outcome);
        Bed.Feed(pipeline, session, Bed.From(Bed.Before(Bed.Evening(), 200_000), 124_000));
        await Bed.Idle(pipeline);

        // It binds without asking about queue inference at all, because it does not infer.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var bound = Assert.Single(
            pipeline.CalibrationStatus().Shared.Candidates,
            candidate => candidate.Status == SharedCandidateStatus.InUse);
        Assert.Equal(better.Sha[..12], bound.Sha12);
        Assert.NotEqual(CalibrationMatchSource.QueueRequest, bound.MatchSource);
        Assert.Null(new SettingsRepository(_bed.Db.Database, _bed.Db.Clock)
            .GetSetting(SharedCalibrationSession.QueueConsentSetting));
    }

    [Fact]
    public async Task AConsentSettingThatCannotBeReadAsksAgainInsteadOfFailing()
    {
        // A damaged value: a key and a stamp .NET cannot read as text; it reopens the question, never stands as consent.
        new SettingsRepository(_bed.Db.Database, _bed.Db.Clock).SetSetting(
            SharedCalibrationSession.QueueConsentSetting,
            "{\"\\ud800\":\"2026-09-01T00:00:00.000Z\",\"cn/" + Bed.Build + "\":\"\\udc00\"}");
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(pop: false), 200_000));
        await Bed.Idle(pipeline);

        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, pipeline.CalibrationStatus().Shared.Phase);
        Assert.Equal(SharedConsentOutcome.Accepted, pipeline.AcceptSharedQueueInference());
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        var remembered = new SettingsRepository(_bed.Db.Database, _bed.Db.Clock).GetSetting(SharedCalibrationSession.QueueConsentSetting);
        Assert.Contains("cn/" + Bed.Build, remembered, StringComparison.Ordinal);
        Assert.DoesNotContain("\\ud", remembered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APastedCodeBindsTheSameWayAndAnotherBuildsCodeIsRefusedWithAReason()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);

        var otherBuild = pipeline.ImportCalibrationCode(Bed.Encode(code.Payload with { GameBuild = Bed.OtherBuild }).Code);
        Assert.Equal(SharedImportOutcome.NotApplicable, otherBuild.Outcome);
        Assert.Equal("OTHER_BUILD", otherBuild.Reason);
        Assert.Contains(Bed.OtherBuild, otherBuild.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", otherBuild.Message, StringComparison.OrdinalIgnoreCase);

        var otherTemplate = pipeline.ImportCalibrationCode(Bed.Encode(code.Payload with { TemplateSha256 = new string('1', 64) }).Code);
        Assert.Equal("OTHER_TEMPLATE", otherTemplate.Reason);

        var garbage = pipeline.ImportCalibrationCode("MRC1.???");
        Assert.Equal(SharedImportOutcome.Malformed, garbage.Outcome);
        Assert.Matches("[\\u4e00-\\u9fff]", garbage.Message);
        Assert.Equal(SharedCalibrationPhase.None, pipeline.CalibrationStatus().Shared.Phase);

        var applied = pipeline.ImportCalibrationCode(code.Code);
        Assert.Equal(SharedImportOutcome.Applied, applied.Outcome);
        Assert.Equal(code.Sha, applied.CodeSha256);
        Assert.Equal(SharedCandidateSource.Manual, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Source);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 200_000));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Empty(_bed.Transport.Requests);
    }

    [Theory]
    [InlineData("setting-off")]
    [InlineData("disarm")]
    [InlineData("build-change")]
    [InlineData("service-stop")]
    public async Task AnInterruptedDownloadIsCancelledAndItsLateResultIgnored(string how)
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        using var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        CancellationToken seen = default;
        _bed.Transport.BeforeAnswer = async (_, token) =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0)
            {
                return;
            }

            seen = token;
            entered.Release();
            await release.Task.ConfigureAwait(false); // Deliberately ignores cancellation: the answer arrives late.
        };
        var pipeline = _bed.Pipeline(_bed.Services());

        pipeline.Refresh(Bed.Game());
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));
        switch (how)
        {
            case "setting-off":
                pipeline.ApplySharedCalibrationSetting(false);
                break;
            case "disarm":
                pipeline.ApplyCalibrationSetting(false);
                break;
            case "build-change":
                pipeline.Refresh(Bed.Game(Bed.OtherBuild));
                break;
            default:
                pipeline.StopSharedCalibration();
                break;
        }

        Assert.True(seen.IsCancellationRequested);
        release.SetResult();
        await Bed.Idle(pipeline);

        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Empty(shared.Candidates);
        Assert.NotEqual(SharedCalibrationPhase.Verifying, shared.Phase);
    }

    [Fact]
    public async Task TheKillSwitchMeansTheTransportIsNeverCalled()
    {
        _bed.Environment = name => name == SharedCalibrationClient.DisableVariable ? "1" : null;
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Empty(_bed.Transport.Requests);
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedFetchStatus.Disabled, shared.LastFetchStatus);
        Assert.Equal(SharedCalibrationPhase.None, shared.Phase);
        Assert.Empty(shared.Candidates);
    }

    [Fact]
    public async Task TheSettingIsOnByDefaultPersistsAndOffMeansNoDownloadButImportStillWorks()
    {
        var settings = new SettingsRepository(_bed.Db.Database, _bed.Db.Clock);
        Assert.True(CaptureSettingsStore.Read(settings).SharedCalibrationEnabled);

        var applied = CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate { SharedCalibrationEnabled = false });

        Assert.False(applied.SharedCalibrationEnabled);
        Assert.False(CaptureSettingsStore.Read(settings).SharedCalibrationEnabled);
        Assert.Equal("false", settings.GetSetting(CaptureSettingsStore.SharedCalibrationSetting));
        Assert.True(applied.AutoCalibrationEnabled);

        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        Assert.Empty(_bed.Transport.Requests);
        Assert.Equal(SharedCheckOutcome.Disabled, pipeline.CheckSharedCalibrationNow());
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);

        pipeline.ApplySharedCalibrationSetting(true);
        await Bed.Idle(pipeline);
        Assert.NotEmpty(_bed.Transport.Requests);
    }

    [Fact]
    public async Task ServicesATestBuildsForItselfNeverScheduleADownload()
    {
        var inert = new CalibrationServices(
            _ => Bed.Template,
            _bed.DiskSelect,
            (draft, template, build, now) => LocalProfileWriter.Write(draft, template, build, now, _bed.LocalRoot));
        Assert.False(inert.SharedFetchWired);
        var pipeline = _bed.Pipeline(inert);

        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        Assert.Equal(SharedCheckOutcome.Disabled, pipeline.CheckSharedCalibrationNow());
        Assert.Equal(SharedCalibrationPhase.None, pipeline.CalibrationStatus().Shared.Phase);
        Assert.Throws<InvalidOperationException>(() => inert.WriteSharedProfile(new SharedProfileBuildResult(SharedProfileBuildStatus.Built)));
    }
}
