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
