using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Calibration end to end inside the live pipeline: a build with no profile runs as a
/// counting sink, the observer learns the opcodes, the user confirms, a local profile is
/// written and re-loaded from disk, and the same session starts recording with it.
/// </summary>
public sealed class CalibrationPipelineTests : IDisposable
{
    private const string Build = "2026.09.01.0000.0000";
    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "local-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_localRoot))
            {
                Directory.Delete(_localRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static ProfileSelection NoProfile(GameProcessDetection game) => new(
        ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null, game.Region, game.GameBuild,
        ProfileSelector.NoProfileMatchesReason);

    private static ProfileSelection Ambiguous(GameProcessDetection game) => new(
        ProfileCompatibilityStatus.Ambiguous, ProfileBinding.FailClosed, null, game.Region, game.GameBuild,
        "two or more profiles claim this region and build");

    private static GameProcessDetection NewBuild =>
        GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = Build };

    private CalibrationServices Services(CalibrationTemplate template) => new(
        _ => template,
        () =>
        {
            var selector = new ProfileSelector(ProfileCatalog.LoadMerged(null, _localRoot));
            return game => selector.Select(game.Region, game.GameBuild);
        },
        (draft, chosen, build, now) => LocalProfileWriter.Write(draft, chosen, build, now, _localRoot));

    private static string OpenSession(TestDatabase db)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessions = new CaptureSessionRepository(db.Database);
        db.Database.RunInTransaction(tx => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = sessionId,
            StartedAtUtc = db.Clock.UtcNow,
            CollectorVersion = "test",
            Region = Region.Cn,
            GameBuild = Build,
            ProtocolProfileId = null,
            ProfileStatus = ProfileStatus.UnsupportedBuild,
            PacketsObserved = 0,
        }, tx));
        return sessionId;
    }

    private static void Feed(LiveProtocolPipeline pipeline, string sessionId, IEnumerable<DecodedMessage> traffic)
    {
        foreach (var message in traffic)
        {
            pipeline.Accept(message with { CaptureSessionId = sessionId });
        }
    }

    private static Dictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    [Fact]
    public void ANewBuildRunsAsACountingSinkUntilTheUserConfirmsAndThenRecordsInTheSameSession()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, Services(template));
        var states = new List<CalibrationState>();
        pipeline.CalibrationChanged += states.Add;

        var before = pipeline.Refresh(NewBuild);
        Assert.Equal(ProfileStatus.UnsupportedBuild, before.Status);
        Assert.True(before.CalibrationActive);
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Waiting, pipeline.CalibrationStatus().State);

        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        Assert.Contains(CalibrationState.Observing, states);

        Feed(pipeline, sessionId, CalibrationObserverTests.Session1());
        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);
        Assert.Equal(Build, ready.GameBuild);
        Assert.Equal("cn.template", ready.TemplateProfileId);
        Assert.Equal(4, ready.Events.Count(item => item.RequiresConfirmation));
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Equal(0, pipeline.ParseOkCount);

        var result = pipeline.ConfirmCalibration(AllCorrect(ready));

        Assert.True(result.BoundInSession);
        Assert.Equal("cn.2026.09.01.0000.0000.local", result.ProfileId);
        Assert.True(File.Exists(result.ProfilePath));
        Assert.Empty(ProfileLoader.Validate(result.ProfilePath).Errors);
        var after = pipeline.Current;
        Assert.Equal(ProfileStatus.Verified, after.Status);
        Assert.Equal(result.ProfileId, after.ProfileId);
        Assert.Equal(ProfileOrigin.Local, after.Origin);
        Assert.False(after.CalibrationActive);
        var done = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Done, done.State);
        Assert.Equal(result.ProfileId, done.LocalProfileId);
        Assert.NotNull(done.BoundAtUtc);
        Assert.Equal(CalibrationState.Done, states[^1]);
        var row = new CaptureSessionRepository(db.Database).Get(sessionId);
        Assert.Equal(result.ProfileId, row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.Verified, row.ProfileStatus);

        // The same session now records: a mentor pop on the learned opcode, then a zone change.
        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xC002,
                CalibrationObserverTests.Bytes(40, (9, 3), (16, 9)), 300_000),
        });
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA107, new byte[456], 310_000),
        });
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
    }

    /// <summary>
    /// The job is announced at login and on zone changes, so the observer has already seen it
    /// by the time the user confirms the calibration. A pop straight after binding must carry
    /// that job, otherwise the record stays job-less until the next zone change.
    /// </summary>
    [Fact]
    public void TheJobSeenDuringCalibrationIsOnTheFirstRecord()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, Services(template));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Feed(pipeline, sessionId, CalibrationObserverTests.Session1(exitJob: 30));
        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);

        var result = pipeline.ConfirmCalibration(AllCorrect(ready));
        Assert.True(result.BoundInSession);

        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xC002,
                CalibrationObserverTests.Bytes(40, (9, 3), (16, 9)), 300_000),
        });
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        var run = pipeline.GetCurrentRun().Run;
        Assert.NotNull(run);
        Assert.Equal(30, run!.JobId);
    }

    /// <summary>
    /// Evidence must outlive the process. Installing a new version, or simply restarting the
    /// software, keeps what the observer has already learned instead of costing the player
    /// another evening of play.
    /// </summary>
    [Fact]
    public void EvidenceSurvivesARestartOfTheCollector()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var evidenceRoot = Path.Combine(_localRoot, "evidence");
        var services = Services(template).WithEvidenceIn(evidenceRoot);

        var first = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        first.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        first.OnCaptureStarted(sessionId);
        Feed(first, sessionId, CalibrationObserverTests.Session1());
        var before = first.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, before.State);
        // An ordinary quit stops the capture, which is when the evidence is written.
        first.OnCaptureStopped(sessionId, CaptureEndReason.UserStop);

        // A brand new process, pointed at the same data directory.
        var second = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        second.Refresh(NewBuild);
        second.OnCaptureStarted(OpenSession(db));
        var after = second.CalibrationStatus();

        Assert.Equal(CalibrationState.Ready, after.State);
        Assert.Equal(
            before.Events.Select(item => (item.Kind, item.Label)),
            after.Events.Select(item => (item.Kind, item.Label)));
    }

    /// <summary>
    /// 重新观察 must forget the evidence on disk as well as the evidence in memory; a file that
    /// hands it all back on the next launch would contradict the button.
    /// </summary>
    [Fact]
    public void DiscardingAlsoForgetsTheEvidenceOnDisk()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var evidenceRoot = Path.Combine(_localRoot, "evidence");
        var services = Services(template).WithEvidenceIn(evidenceRoot);

        var first = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        first.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        first.OnCaptureStarted(sessionId);
        Feed(first, sessionId, CalibrationObserverTests.Session1());
        first.OnCaptureStopped(sessionId, CaptureEndReason.UserStop);

        var second = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        second.Refresh(NewBuild);
        var secondSession = OpenSession(db);
        second.OnCaptureStarted(secondSession);
        second.DiscardCalibration();
        second.OnCaptureStopped(secondSession, CaptureEndReason.UserStop);

        var third = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        third.Refresh(NewBuild);
        third.OnCaptureStarted(OpenSession(db));

        Assert.Equal(CalibrationState.Observing, third.CalibrationStatus().State);
        Assert.Empty(third.CalibrationStatus().Events);
    }

    /// <summary>
    /// The 2026-09-01 CN client end to end: nothing in the traffic announces a match, so the
    /// player's own queue request stands in for it, the session starts recording with it, and
    /// calibration keeps running underneath so the real announcement can still replace it.
    /// </summary>
    [Fact]
    public void AQueueInferredProfileRecordsAndKeepsLookingForTheRealAnnouncement()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, Services(template));

        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);

        // Session1 with its match message removed, plus a second roulette so the reply opcode
        // can still be locked by two separated pairs.
        Feed(pipeline, sessionId, CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == 0xC002 && message.Mono == TimeSpan.FromMilliseconds(120_000)))
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 2)), 300_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 5), (16, 2)), 300_120),
            })
            .OrderBy(message => message.Mono));

        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);
        Assert.DoesNotContain(ready.Events, item => item.Kind == "pop");

        var result = pipeline.ConfirmCalibration(AllCorrect(ready));

        Assert.True(result.BoundInSession);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        // Recording works and the search goes on: the card says OBSERVING, not DONE.
        var after = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Observing, after.State);
        Assert.Equal(result.ProfileId, after.LocalProfileId);
        Assert.Contains(after.Blockers, text => text.Contains("已经可以正常记录导随", StringComparison.Ordinal));
        Assert.True(pipeline.CalibrationArmed);

        // A queue request is held in memory. It creates a run only on entry to a known duty.
        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Outbound, 0xC001,
                CalibrationObserverTests.Bytes(24, (0, 9)), 400_000),
        });
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Null(pipeline.GetCurrentRun().Run);

        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA108,
                CalibrationObserverTests.Bytes(136, (2, 136), (3, 19)), 410_000),
            // The dedup key includes the payload hash, so two zone markers in one session must
            // differ somewhere; the profile declares no field on this message.
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA107,
                CalibrationObserverTests.Bytes(456, (100, 1)), 410_100),
        });
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Null(pipeline.GetCurrentRun().Run);

        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA108,
                CalibrationObserverTests.Bytes(136, (2, 15), (3, 4)), 420_000),
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xA107,
                CalibrationObserverTests.Bytes(456, (100, 2)), 420_100),
        });
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
    }

    [Fact]
    public void AWrongVerdictVoidsTheDraftAndTheSessionKeepsCounting()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, Services(template));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Feed(pipeline, sessionId, CalibrationObserverTests.Session1());
        var ready = pipeline.CalibrationStatus();
        var verdicts = AllCorrect(ready);
        verdicts[ready.Events.First(item => item.Kind == "pop").EventId] = CalibrationVerdict.Wrong;

        var error = Assert.Throws<CollectorException>(() => pipeline.ConfirmCalibration(verdicts));

        Assert.Equal(ErrorCodes.CalibrationRejected, error.Code);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(Directory.Exists(_localRoot) && Directory.EnumerateFiles(_localRoot, "*.json", SearchOption.AllDirectories).Any());

        // The rejected pop opcode is not proposed again for this session's evidence.
        Assert.NotEqual(CalibrationState.Ready, pipeline.CalibrationStatus().State);
    }

    [Fact]
    public void ConfirmingWithoutAReadyDraftIsRefused()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);

        var error = Assert.Throws<CollectorException>(() =>
            pipeline.ConfirmCalibration(new Dictionary<string, CalibrationVerdict> { ["pop-1"] = CalibrationVerdict.Correct }));

        Assert.Equal(ErrorCodes.CalibrationNotReady, error.Code);
    }

    [Fact]
    public void OnlyAMissingProfileArmsCalibrationNeverAnAmbiguousDirectory()
    {
        using var db = new TestDatabase();
        var services = Services(CalibrationObserverTests.Template());
        var ambiguous = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), Ambiguous, null, services);
        Assert.False(ambiguous.Refresh(NewBuild).CalibrationActive);
        Assert.False(ambiguous.CalibrationArmed);

        var missing = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        Assert.True(missing.Refresh(NewBuild).CalibrationActive);

        missing.ApplyCalibrationSetting(false);
        Assert.False(missing.CalibrationArmed);
        Assert.False(missing.Refresh(NewBuild).CalibrationActive);

        missing.ApplyCalibrationSetting(true);
        Assert.True(missing.Refresh(NewBuild).CalibrationActive);

        // No template for the region: nothing to calibrate from.
        var templateless = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            services with { SelectTemplate = _ => null });
        Assert.False(templateless.Refresh(NewBuild).CalibrationActive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnprovenCalibrationEvidenceCannotWriteOrBindAProfile(int scenario)
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        var popBeforeRequest = scenario == 1;
        var traffic = scenario == 2
            ? CalibrationObserverTests.Session1(zoneInitLength: 464)
                .Concat(CalibrationObserverTests.Cluster(300_000, 5000, connection: "lobby"))
                .Concat(CalibrationObserverTests.Cluster(320_000, 5000, connection: "lobby"))
                .Concat(CalibrationObserverTests.Cluster(340_000, 5000, connection: "lobby"))
            : CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(
                60_000, 1, popBeforeRequest ? 50_000 : 61_500, popState: 7))
            .Concat(CalibrationObserverTests.Cluster(125_000, popBeforeRequest ? 1039 : 5000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        Feed(pipeline, sessionId, traffic.OrderBy(message => message.ObservedAtUtc));
        pipeline.OnCaptureStopped(sessionId, CaptureEndReason.ProcessExit);

        var status = pipeline.CalibrationStatus();
        Assert.NotEqual(CalibrationState.Ready, status.State);
        if (scenario != 2)
        {
            Assert.False(status.Progress!.PopSeen);
        }
        var error = Assert.Throws<CollectorException>(() => pipeline.ConfirmCalibration(AllCorrect(status)));

        Assert.Equal(ErrorCodes.CalibrationNotReady, error.Code);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Equal(0, pipeline.ParseOkCount);
        Assert.Null(new CaptureSessionRepository(db.Database).Get(sessionId)!.ProtocolProfileId);
        Assert.False(Directory.Exists(_localRoot) &&
            Directory.EnumerateFiles(_localRoot, "*.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void ADiscardDuringTheDiskWriteWinsOverTheConfirmation()
    {
        using var db = new TestDatabase();
        var template = CalibrationObserverTests.Template();
        LiveProtocolPipeline? pipeline = null;
        var services = Services(template);
        services = services with
        {
            Write = (draft, chosen, build, now) =>
            {
                // Another connection discards while this confirmation is writing the file.
                pipeline!.DiscardCalibration();
                return LocalProfileWriter.Write(draft, chosen, build, now, _localRoot);
            },
        };
        pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null, services);
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Feed(pipeline, sessionId, CalibrationObserverTests.Session1());
        var ready = pipeline.CalibrationStatus();

        var error = Assert.Throws<CollectorException>(() => pipeline.ConfirmCalibration(AllCorrect(ready)));

        Assert.Equal(ErrorCodes.CalibrationNotReady, error.Code);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Null(new CaptureSessionRepository(db.Database).Get(sessionId)!.ProtocolProfileId);
    }

    [Fact]
    public void ARestartedCaptureSessionKeepsTheEvidenceOfThePreviousOne()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var first = OpenSession(db);
        pipeline.OnCaptureStarted(first);
        Feed(pipeline, first, CalibrationObserverTests.Cluster(5_000, 5000));
        pipeline.OnCaptureStopped(first, CaptureEndReason.ProcessExit);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        var second = OpenSession(db);
        pipeline.OnCaptureStarted(second);
        var status = pipeline.CalibrationStatus();

        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.Equal(1, status.Progress!.ZoneClusters);
        Assert.Equal(2, status.Evidence!.Sessions);
    }

    [Fact]
    public void StoppingCaptureRefreshesACachedDraftAfterClosingTheLastCluster()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Feed(pipeline, sessionId, CalibrationObserverTests.Cluster(5_000, 5000));
        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Outbound, 0xE001,
                new byte[1], 5_900, "lobby"),
        });

        // Cache a draft while the zone burst is still open and the lobby message is
        // waiting in its own connection's ring. Stop must finalize both without new traffic.
        var before = pipeline.CalibrationStatus();
        var beforeEvidence = Assert.IsType<CalibrationEvidenceSummary>(before.Evidence);
        Assert.Equal(0, before.Progress!.ZoneClusters);
        Assert.Equal(0, beforeEvidence.Clusters);
        Assert.Equal(0, beforeEvidence.OutsideKeys);
        Assert.Empty(before.Events);

        pipeline.OnCaptureStopped(sessionId, CaptureEndReason.ProcessExit);
        var after = pipeline.CalibrationStatus();
        var afterEvidence = Assert.IsType<CalibrationEvidenceSummary>(after.Evidence);

        Assert.Equal(beforeEvidence.MessagesSeen, afterEvidence.MessagesSeen);
        Assert.Equal(1, afterEvidence.Clusters);
        Assert.Equal(afterEvidence.Clusters, after.Progress!.ZoneClusters);
        Assert.Equal(1, afterEvidence.OutsideKeys);
        Assert.Equal("login", Assert.Single(after.Events).Kind);
        Assert.Equal(CalibrationState.Observing, after.State);
    }

    [Fact]
    public void APopShapeOnlyProgressChangeStillTellsTheDesktop()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        var before = pipeline.CalibrationStatus();
        var beforeProgress = Assert.IsType<CalibrationProgress>(before.Progress);
        Assert.False(beforeProgress.PopShapeSeen);
        var notifications = new List<CalibrationStatusSnapshot>();
        pipeline.CalibrationChanged += _ => notifications.Add(pipeline.CalibrationStatus());

        // An unrelated shape identifies no request, match or zone. It only changes the
        // diagnostic progress bit, which still requires the normal live notification.
        Feed(pipeline, sessionId, new[]
        {
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xE002,
                new byte[40], 2_000),
        });

        var after = Assert.Single(notifications);
        Assert.Equal(beforeProgress with { PopShapeSeen = true }, after.Progress);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Blockers, after.Blockers);
        Assert.Empty(after.Events);
    }

    [Fact]
    public void ProgressThatDoesNotChangeTheStateStillTellsTheDesktop()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        var notifications = 0;
        pipeline.CalibrationChanged += _ => notifications++;

        // Login burst then a roulette request answered by the server: the state stays
        // OBSERVING throughout, but two progress ticks light up, and the card only learns
        // about them if the pipeline reports the change.
        Feed(pipeline, sessionId, CalibrationObserverTests.Cluster(5_000, 5000));
        Feed(pipeline, sessionId, CalibrationObserverTests.Noise(10_000, 60_000));
        Feed(pipeline, sessionId, CalibrationObserverTests.QueueAndPop(60_000, 1, 300_000).Take(2));
        Feed(pipeline, sessionId, CalibrationObserverTests.Noise(61_000, 64_000));

        var status = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.True(status.Progress!.FinderRequestSeen);
        Assert.Equal(1, status.Progress.ZoneClusters);
        Assert.True(notifications > 0, "a progress change must reach the desktop");
    }

    [Fact]
    public void DiscardingStartsObservingAgainWithinTheRunningSession()
    {
        using var db = new TestDatabase();
        var pipeline = new LiveProtocolPipeline(
            db.Database, db.Clock, new LiveEventBus(db.Clock), NoProfile, null,
            Services(CalibrationObserverTests.Template()));
        pipeline.Refresh(NewBuild);
        var sessionId = OpenSession(db);
        pipeline.OnCaptureStarted(sessionId);
        Feed(pipeline, sessionId, CalibrationObserverTests.Session1());
        Assert.Equal(CalibrationState.Ready, pipeline.CalibrationStatus().State);

        var discarded = pipeline.DiscardCalibration();

        Assert.Equal(CalibrationState.Observing, discarded.State);
        Assert.Empty(discarded.Events);
        Assert.False(discarded.Progress!.PopSeen);
    }
}
