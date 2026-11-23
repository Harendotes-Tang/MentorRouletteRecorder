using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>A share code as a test publishes or pastes it.</summary>
internal sealed record SharedCode(ShareCodePayload Payload, string Code, string Sha);

/// <summary>
/// A live pipeline wired for shared calibration over temporary directories and an injected transport. No
/// connection is opened, the production transport and host are never named, the real data directory is never
/// touched, and the kill switch is read through an injected reader so the shell's value is irrelevant.
/// </summary>
internal sealed class SharedCalibrationTestBed : IDisposable
{
    internal const string Build = CalibrationTrafficCases.Build;
    internal const string OtherBuild = "2026.09.02.0000.0000";
    internal static readonly DateTimeOffset Confirmed = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _root = TempDirectory.Reserve();

    public SharedCalibrationTestBed()
    {
        Store = new SharedCalibrationStore(StoreRoot);
    }

    public string Root => _root.Path;

    public string LocalRoot => Path.Combine(Root, "local");

    public string SharedRoot => Path.Combine(Root, "shared");

    public string StoreRoot => Path.Combine(Root, "store");

    public string SharedProfilePath => SharedProfileFiles.PathFor(SharedRoot, Region.Cn, Build);

    /// <summary>Where this machine's own calibration of <see cref="Build"/> is written.</summary>
    public string LocalProfilePath => LocalProfileFiles.PathFor(LocalRoot, Region.Cn, Build);

    /// <summary>The shared store's state file for <see cref="Build"/>.</summary>
    public string StatePath => Path.Combine(
        SharedCalibrationStore.DirectoryFor(StoreRoot, Region.Cn, Build), SharedCalibrationStore.StateFileName);

    /// <summary>The id a shared profile of <see cref="Build"/> is recorded under.</summary>
    public static string SharedProfileId => SharedProfileBuilder.ProfileIdFor(Region.Cn, Build);

    public TestDatabase Db { get; } = new();

    public SharedCalibrationStore Store { get; }

    public FakeSharedTransport Transport { get; } = new();

    public Func<string, string?> Environment { get; set; } = _ => null;

    /// <summary>Runs before each catalogue reload, off the pipeline's gate; a test holds a bind open with it.</summary>
    public Action? BeforeReload { get; set; }

    public static CalibrationTemplate Template => CalibrationObserverTests.Template();

    public static string TemplateSha => Template.Source.ProfileSha256;

    public void Dispose()
    {
        Db.Dispose();
        _root.Dispose();
    }

    public static GameProcessDetection Game(string build = Build) =>
        GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = build };

    /// <summary>The formal selector over the local and shared test roots, as a restart would build it.</summary>
    public Func<GameProcessDetection, ProfileSelection> DiskSelect()
    {
        var selector = new ProfileSelector(ProfileCatalog.LoadMerged(null, LocalRoot, SharedRoot));
        return game => selector.Select(game.Region, game.GameBuild);
    }

    /// <summary>Calibration services over the test roots; <paramref name="fetch"/> wires the client over the fake transport.</summary>
    public CalibrationServices Services(bool fetch = true)
    {
        var services = new CalibrationServices(
                _ => Template,
                () =>
                {
                    BeforeReload?.Invoke();
                    return DiskSelect();
                },
                (draft, template, build, now) => LocalProfileWriter.Prepare(draft, template, build, now, LocalRoot))
            .WithSharedProfilesIn(SharedRoot) with
            {
                // The machine's own calibrations, which its shape prior is read from (plan §20.5).
                LoadLocalProfiles = CalibrationServices.ProfilesIn(LocalRoot),
            };
        if (!fetch)
        {
            return services;
        }

        var client = new SharedCalibrationClient(Transport.Send, TimeSpan.FromSeconds(10), name => Environment(name));
        return services.WithSharedCalibration(client.FetchAsync, Store);
    }

    public LiveProtocolPipeline Pipeline(CalibrationServices services, LiveEventBus? bus = null) =>
        new(Db.Database, Db.Clock, bus ?? new LiveEventBus(Db.Clock), DiskSelect(), null, services);

    /// <summary>A pipeline selected for <see cref="Build"/> once whatever the selection started has landed.</summary>
    /// <param name="services">Services to use; <see cref="Services"/> with the fetch wired by default.</param>
    public async Task<LiveProtocolPipeline> ReadyPipelineAsync(CalibrationServices? services = null)
    {
        var pipeline = Pipeline(services ?? Services());
        pipeline.Refresh(Game());
        await Idle(pipeline);
        return pipeline;
    }

    public static Task Idle(LiveProtocolPipeline pipeline) =>
        pipeline.WhenSharedCalibrationIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

    public static CaptureSessionHealth Healthy(string session) => new(session, CaptureSilentReason.None, 0, 0);

    public string OpenSession() => Db.OpenCaptureSession(Region.Cn, Build);

    /// <summary>Starts a capture session and, when asked to, reports <paramref name="health"/> or a healthy default.</summary>
    public string Start(LiveProtocolPipeline pipeline, CaptureSessionHealth? health = null, bool reportHealth = true)
    {
        var session = OpenSession();
        pipeline.OnCaptureStarted(session);
        if (reportHealth)
        {
            pipeline.OnCaptureHealth(health is null ? Healthy(session) : health with { CaptureSessionId = session });
        }

        return session;
    }

    /// <summary>One whole capture session of traffic, <paramref name="hour"/> hours after the helpers' clock.</summary>
    public string Play(
        LiveProtocolPipeline pipeline, IEnumerable<DecodedMessage> traffic, int hour, CaptureSessionHealth? health = null,
        bool reportHealth = true)
    {
        var session = Start(pipeline, health, reportHealth);
        Feed(pipeline, session, traffic, hour);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        return session;
    }

    public static void Feed(LiveProtocolPipeline pipeline, string session, IEnumerable<DecodedMessage> traffic, int hour = 0)
    {
        var shift = TimeSpan.FromHours(hour);
        foreach (var message in traffic.OrderBy(item => item.Mono))
        {
            pipeline.Accept(message with
            {
                CaptureSessionId = session,
                ObservedAtUtc = message.ObservedAtUtc + shift,
                Mono = message.Mono + shift,
            });
        }
    }

    /// <summary><see cref="Feed"/>, then lets the background bind or withdrawal the traffic started land.</summary>
    public static Task FeedAndSettleAsync(
        LiveProtocolPipeline pipeline, string session, IEnumerable<DecodedMessage> traffic, int hour = 0)
    {
        Feed(pipeline, session, traffic, hour);
        return Idle(pipeline);
    }

    /// <summary>The user stops the capture, then whatever the stop started lands.</summary>
    public static Task StopAndSettleAsync(LiveProtocolPipeline pipeline, string session)
    {
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        return Idle(pipeline);
    }

    /// <summary>Holds each catalogue reload open: signals <paramref name="writing"/> and waits up to 30 s for <paramref name="release"/>.</summary>
    public void HoldReload(ManualResetEventSlim writing, ManualResetEventSlim release) => BeforeReload = () =>
    {
        writing.Set();
        Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
    };

    /// <summary>
    /// Six seconds later, asks for a check now and waits until the pipeline has claimed the index at that moment.
    /// </summary>
    /// <param name="pipeline">Pipeline to ask.</param>
    /// <param name="because">Failure message if the claim never happens.</param>
    public void CheckNowAndAwaitClaim(LiveProtocolPipeline pipeline, string because)
    {
        Db.Clock.UtcNow += TimeSpan.FromSeconds(6);
        var claimedAt = Db.Clock.UtcNow;
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        Assert.True(SpinWait.SpinUntil(() => pipeline.CalibrationStatus().Shared.LastSentAtUtc == claimedAt,
            TimeSpan.FromSeconds(30)), because);
    }

    /// <summary>Binds the true code on an evening that records no mentor duty, so the watch continues afterwards.</summary>
    public async Task<LiveProtocolPipeline> BoundAndStillWatchedAsync(SharedCode code)
    {
        Publish(code);
        var pipeline = await ReadyPipelineAsync();
        var session = Start(pipeline);
        await FeedAndSettleAsync(pipeline, session, TrueEvening, hour: 24);
        await StopAndSettleAsync(pipeline, session);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Game()).Origin);
        Assert.True(File.Exists(SharedProfilePath));
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
        return pipeline;
    }

    /// <summary>Opens the store's state file: <see cref="FileShare.Read"/> blocks its write, <see cref="FileShare.None"/> its read too.</summary>
    public FileStream HoldStateFile(FileShare share) => new(StatePath, FileMode.Open, FileAccess.Read, share);

    // ------------------------------------------------------------------ records

    /// <summary>The runs recorded in one capture session.</summary>
    public IReadOnlyList<MentorRun> RunsOf(string session) =>
        AllRuns().Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal)).ToArray();

    /// <summary>Every run on the first history page.</summary>
    public IReadOnlyList<MentorRun> AllRuns() => new RunRepository(Db.Database).Query(null, null, 1, 50).Items;

    /// <summary>A run row recorded under the shared profile id now, with its creation revision, as the processor writes them.</summary>
    public MentorRun SeedRunUnderSharedProfile()
    {
        var run = TestDatabase.Run(
                source: RunSource.AutoNetwork, result: RunResult.CancelledBeforeEntry, enteredAt: Db.Clock.UtcNow.AddMinutes(2)) with
            {
                ProtocolProfileId = SharedProfileId,
            };
        Db.InsertWithFirstRevision(run);
        return run;
    }

    /// <summary>Every event the user is asked about, confirmed as correct.</summary>
    internal static Dictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    // ------------------------------------------------------------------ codes

    /// <summary>The code a player who played <paramref name="name"/> on evening A would share.</summary>
    public SharedCode CodeFromEveningA(string name)
    {
        var written = WriteLocalProfile(name, root: Path.Combine(Root, "evening-a", Guid.NewGuid().ToString("N")));
        var exported = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written), Template);
        Assert.Null(exported.Reason);
        return new SharedCode(exported.Payload!, exported.Code!, exported.CodeSha256!);
    }

    public static SharedCode Encode(ShareCodePayload payload) => new(payload, ShareCode.Encode(payload), ShareCode.Sha256(payload));

    /// <summary>The same code with its match message moved to an opcode the client never sends it on.</summary>
    public static SharedCode WithBogusPop(SharedCode code) =>
        Encode(code.Payload with { Pop = code.Payload.Pop with { Opcode = 0x7777 } });

    /// <summary>How one code stands in the index a test serves.</summary>
    /// <param name="Code">The code itself, always downloadable.</param>
    /// <param name="Revoked">True to list it as withdrawn from the repository.</param>
    /// <param name="Conflicting">True to mark it as disagreeing with another code of the same build.</param>
    /// <param name="Submitters">Distinct accounts the index says submitted it.</param>
    /// <param name="ShapePrior">The index's shape prior for it; null leaves the key out.</param>
    internal sealed record Listing(
        SharedCode Code, bool Revoked = false, bool Conflicting = false, int Submitters = 1, bool? ShapePrior = null,
        bool Quarantined = false, int QuarantineEpoch = 0);

    /// <summary>Serves an index listing the codes, and the codes, from the first source.</summary>
    public void Publish(params SharedCode[] codes) => Serve(codes.Select(code => new Listing(code)).ToArray());

    /// <summary>Serves an index that revokes the code.</summary>
    public void PublishRevoked(SharedCode code) => Serve(new[] { new Listing(code, Revoked: true) });

    /// <summary>Serves one index in which each code stands as the listing says.</summary>
    internal void PublishListed(params Listing[] listings) => Serve(listings);

    private void Serve(IReadOnlyList<Listing> codes)
    {
        var entries = codes
            .Select(listing =>
            {
                var entry = SharedCalibrationIndexTests.Entry(
                listing.Code.Sha, submitters: listing.Submitters, build: listing.Code.Payload.GameBuild, revoked: listing.Revoked,
                matchSource: EnumWire<CalibrationMatchSource>.Format(listing.Code.Payload.MatchSource),
                conflicting: listing.Conflicting, shapePrior: listing.ShapePrior);
                entry["quarantined"] = listing.Quarantined;
                entry["quarantine_epoch"] = listing.QuarantineEpoch;
                return (JsonNode)entry;
            })
            .ToArray();
        Transport.Serve(SharedCalibrationClient.IndexUri(SharedCalibrationSource.GithubRaw), SharedCalibrationIndexTests.Index(entries));
        foreach (var listing in codes)
        {
            var code = listing.Code;
            Transport.Serve(
                SharedCalibrationClient.CodeUri(SharedCalibrationSource.GithubRaw, SharedCalibrationIndexTests.Commit(),
                    SharedCalibrationIndex.CodePath(code.Payload.Region, code.Payload.GameBuild, code.Sha)),
                Encoding.UTF8.GetBytes(code.Code));
        }
    }

    /// <summary>A fetch that reached the first source (or, for an unavailable index or a cancel, timed out there).</summary>
    internal static SharedCalibrationFetchResult Fetched(SharedFetchStatus status, params SharedCalibrationCandidate[] candidates)
    {
        var reached = status is not (SharedFetchStatus.IndexUnavailable or SharedFetchStatus.Cancelled);
        return new SharedCalibrationFetchResult(
            status,
            new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, reached ? SharedFetchOutcome.Ok : SharedFetchOutcome.Timeout, reached ? 200 : null) },
            candidates,
            Array.Empty<SharedCodeDiscard>(),
            Array.Empty<SharedIndexSkip>(),
            Array.Empty<string>());
    }

    /// <summary>The last index this machine read for <paramref name="build"/> revokes <paramref name="sha"/>.</summary>
    public void RevokeInStore(string sha, string build = Build) => Assert.True(Store.RecordFetch(
        Region.Cn, build, TemplateSha,
        Fetched(SharedFetchStatus.NoneForBuild) with { RevokedCodeSha256s = new[] { sha } },
        Db.Clock.UtcNow).Recorded);

    // ------------------------------------------------------------------ profiles on disk

    /// <summary>
    /// This machine's own calibration of <paramref name="build"/>, as the player confirmed it from one synthetic
    /// evening's traffic; returns the file's path.
    /// </summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    /// <param name="build">Build the profile is for.</param>
    /// <param name="root">Directory to write into; <see cref="LocalRoot"/> by default.</param>
    public string WriteLocalProfile(string trafficCase, string build = Build, string? root = null) =>
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(trafficCase), Template, build, Confirmed, root ?? LocalRoot).Path;

    /// <summary>
    /// The profile a machine wrote before the job rule could name the message: the very draft one evening produces,
    /// with the job message and its evidence entry taken back out again.
    /// </summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>; its draft must declare the job.</param>
    public string WriteLocalProfileLackingTheJob(string trafficCase)
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
        var written = LocalProfileWriter.Write(withheld, Template, Build, Confirmed, LocalRoot).Path;
        Assert.DoesNotContain("PLAYER_JOB", File.ReadAllText(written), StringComparison.Ordinal);
        return written;
    }

    /// <summary>
    /// A queue-inferred local profile without its territory message: it loads as VERIFIED and its binding
    /// is refused.
    /// </summary>
    public string WriteRefusedLocalProfile()
    {
        var path = WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var messages = document["messages"]!.AsArray();
        messages.Remove(messages.Single(node => node!["name"]!.GetValue<string>() == "ZONE_TERRITORY"));
        return ProfileTestFiles.WriteStamped(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), document);
    }

    /// <summary>A shared profile as this machine builds it from a payload: the bed's template, verified at <see cref="Confirmed"/>, no counts.</summary>
    /// <param name="payload">The share code's payload.</param>
    /// <param name="queueInferenceAcceptedAtUtc">When queue inference was accepted for it, if it was.</param>
    public static SharedProfileBuildResult BuildSharedProfile(
        ShareCodePayload payload, DateTimeOffset? queueInferenceAcceptedAtUtc = null) =>
        SharedProfileBuilder.Build(payload, Template, Confirmed, new Dictionary<string, int>(), queueInferenceAcceptedAtUtc);

    /// <summary>Writes <see cref="BuildSharedProfile"/> into <see cref="SharedRoot"/> and returns the file's path.</summary>
    public string WriteSharedProfile(ShareCodePayload payload, DateTimeOffset? queueInferenceAcceptedAtUtc = null) =>
        SharedProfileFiles.Write(BuildSharedProfile(payload, queueInferenceAcceptedAtUtc), SharedRoot);

    // ------------------------------------------------------------------ traffic

    /// <summary>
    /// Session1's shape with a mentor roulette: login, queue, pop, duty entry, duty exit. Every zone marker,
    /// territory and reply carries a distinct last byte, because the state machine ignores an event whose
    /// key - payload hash included - it has already seen in the session.
    /// </summary>
    public static IEnumerable<DecodedMessage> Evening(byte roulette = 9, bool pop = true)
    {
        var queue = CalibrationObserverTests.QueueAndPop(60_000, roulette, 120_000);
        return Marked(CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.Noise(10_000, 60_000))
            .Concat(pop ? queue : queue.Take(2))
            .Concat(CalibrationObserverTests.Noise(61_000, 124_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Noise(130_000, 210_000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000))
            .Concat(CalibrationObserverTests.Noise(220_000, 240_000)), firstMarker: 1);
    }

    /// <summary>A second mentor roulette after <see cref="Evening"/>: queue, pop and the duty entry.</summary>
    public static IEnumerable<DecodedMessage> SecondDuty() => Marked(
        CalibrationObserverTests.QueueAndPop(300_000, 9, 360_000)
            .Concat(CalibrationObserverTests.Noise(361_000, 364_000))
            .Concat(CalibrationObserverTests.Cluster(365_000, 1039))
            .Concat(CalibrationObserverTests.Noise(370_000, 380_000)), firstMarker: 101);

    /// <summary>The reply-state evening of <see cref="CalibrationTrafficCases"/>, which every published code reads.</summary>
    public static IEnumerable<DecodedMessage> TrueEvening => CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState);

    /// <summary>The same evening on a client whose zone-change marker is not the declared opcode.</summary>
    public static IEnumerable<DecodedMessage> EveningWithoutTheDeclaredMarker => TrueEvening.Select(message =>
        message.Opcode == CalibrationTrafficCases.ZoneInit ? message with { Opcode = 0xA1F7 } : message);

    /// <summary>The login burst and the idle minute after it: nothing queued yet.</summary>
    public static DecodedMessage[] Login(IEnumerable<DecodedMessage> evening) => Before(evening, 30_000);

    /// <summary>Everything after the login burst.</summary>
    public static DecodedMessage[] AfterLogin(IEnumerable<DecodedMessage> evening) => From(evening, 30_000);

    /// <summary>Queue, match and the duty entry, after the login burst.</summary>
    public static DecodedMessage[] UpToEntry(IEnumerable<DecodedMessage> evening) => From(Before(evening, 200_000), 30_000);

    /// <summary>One match of the shape a learned announcement profile declares: the roulette id at offset 16.</summary>
    /// <param name="rouletteId">Value at the declared roulette offset.</param>
    /// <param name="at">Session time in milliseconds.</param>
    public static DecodedMessage Announced(byte rouletteId, long at) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, CalibrationTrafficCases.Announce,
        CalibrationObserverTests.Bytes(64, (16, rouletteId)), at);

    public static DecodedMessage[] Before(IEnumerable<DecodedMessage> traffic, long ms) =>
        traffic.Where(message => message.Mono < TimeSpan.FromMilliseconds(ms)).ToArray();

    public static DecodedMessage[] From(IEnumerable<DecodedMessage> traffic, long ms) =>
        traffic.Where(message => message.Mono >= TimeSpan.FromMilliseconds(ms)).ToArray();

    /// <summary>
    /// The traffic in session-time order with a distinct last byte, counting up from <paramref name="firstMarker"/>, on every
    /// zone marker, territory and reply: the state machine drops an event whose payload key it has already seen.
    /// </summary>
    internal static IEnumerable<DecodedMessage> Marked(IEnumerable<DecodedMessage> traffic, byte firstMarker)
    {
        var marker = firstMarker;
        foreach (var message in traffic.OrderBy(item => item.Mono))
        {
            if (message.Opcode is CalibrationTrafficCases.ZoneInit or CalibrationTrafficCases.Territory or CalibrationTrafficCases.Reply)
            {
                var payload = message.Payload.ToArray();
                payload[^1] = marker++;
                yield return message with { Payload = payload };
            }
            else
            {
                yield return message;
            }
        }
    }
}
