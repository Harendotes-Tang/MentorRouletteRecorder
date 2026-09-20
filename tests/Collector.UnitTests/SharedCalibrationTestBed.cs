using System.Text;
using System.Text.Json;
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

    public SharedCalibrationTestBed()
    {
        Root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", "shared-pipeline-" + Guid.NewGuid().ToString("N"));
        Store = new SharedCalibrationStore(StoreRoot);
    }

    public string Root { get; }

    public string LocalRoot => Path.Combine(Root, "local");

    public string SharedRoot => Path.Combine(Root, "shared");

    public string StoreRoot => Path.Combine(Root, "store");

    public string SharedProfilePath => SharedProfileFiles.PathFor(SharedRoot, Region.Cn, Build);

    public TestDatabase Db { get; } = new();

    public SharedCalibrationStore Store { get; }

    public FakeSharedTransport Transport { get; } = new();

    public Func<string, string?> Environment { get; set; } = _ => null;

    public static CalibrationTemplate Template => CalibrationObserverTests.Template();

    public static string TemplateSha => Template.Source.ProfileSha256;

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Debris in the OS temp folder is not worth failing a test over.
        }
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
                DiskSelect,
                (draft, template, build, now) => LocalProfileWriter.Write(draft, template, build, now, LocalRoot))
            .WithSharedProfilesIn(SharedRoot);
        if (!fetch)
        {
            return services;
        }

        var client = new SharedCalibrationClient(Transport.Send, TimeSpan.FromSeconds(10), name => Environment(name));
        return services.WithSharedCalibration(client.FetchAsync, Store);
    }

    public LiveProtocolPipeline Pipeline(CalibrationServices services, LiveEventBus? bus = null) =>
        new(Db.Database, Db.Clock, bus ?? new LiveEventBus(Db.Clock), DiskSelect(), null, services);

    public static Task Idle(LiveProtocolPipeline pipeline) =>
        pipeline.WhenSharedCalibrationIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

    public static CaptureSessionHealth Healthy(string session) => new(session, CaptureSilentReason.None, 0, 0);

    public string OpenSession()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessions = new CaptureSessionRepository(Db.Database);
        Db.Database.RunInTransaction(tx => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = sessionId,
            StartedAtUtc = Db.Clock.UtcNow,
            CollectorVersion = "test",
            Region = Region.Cn,
            GameBuild = Build,
            ProtocolProfileId = null,
            ProfileStatus = ProfileStatus.UnsupportedBuild,
            PacketsObserved = 0,
        }, tx));
        return sessionId;
    }

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

    // ------------------------------------------------------------------ codes

    /// <summary>The code a player who played <paramref name="name"/> on evening A would share.</summary>
    public SharedCode CodeFromEveningA(string name)
    {
        var draft = CalibrationTrafficCases.Derive(name);
        var written = LocalProfileWriter.Write(draft, Template, Build, Confirmed,
            Path.Combine(Root, "evening-a", Guid.NewGuid().ToString("N")));
        var exported = SharedProfileBuilder.ToShareCode(ProfileLoader.Load(written.Path), Template);
        Assert.Null(exported.Reason);
        return new SharedCode(exported.Payload!, exported.Code!, exported.CodeSha256!);
    }

    public static SharedCode Encode(ShareCodePayload payload) => new(payload, ShareCode.Encode(payload), ShareCode.Sha256(payload));

    /// <summary>How one code stands in the index a test serves.</summary>
    /// <param name="Code">The code itself, always downloadable.</param>
    /// <param name="Revoked">True to list it as withdrawn from the repository.</param>
    /// <param name="Conflicting">True to mark it as disagreeing with another code of the same build.</param>
    internal sealed record Listing(SharedCode Code, bool Revoked = false, bool Conflicting = false);

    /// <summary>Serves an index listing the codes, and the codes, from the first source.</summary>
    public void Publish(params SharedCode[] codes) => Serve(codes.Select(code => new Listing(code)).ToArray());

    /// <summary>Serves an index that revokes the code.</summary>
    public void PublishRevoked(SharedCode code) => Serve(new[] { new Listing(code, Revoked: true) });

    /// <summary>Serves one index in which each code stands as the listing says.</summary>
    internal void PublishListed(params Listing[] listings) => Serve(listings);

    private void Serve(IReadOnlyList<Listing> codes)
    {
        var entries = codes
            .Select(listing => (JsonNode)SharedCalibrationIndexTests.Entry(
                listing.Code.Sha, build: listing.Code.Payload.GameBuild, revoked: listing.Revoked,
                matchSource: EnumWire<CalibrationMatchSource>.Format(listing.Code.Payload.MatchSource),
                conflicting: listing.Conflicting))
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

    /// <summary>
    /// A queue-inferred local profile without its territory message: it loads as VERIFIED and its binding
    /// is refused.
    /// </summary>
    public string WriteRefusedLocalProfile()
    {
        var written = LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Template, Build, Confirmed, LocalRoot);
        var document = JsonNode.Parse(File.ReadAllText(written.Path))!.AsObject();
        var messages = document["messages"]!.AsArray();
        messages.Remove(messages.Single(node => node!["name"]!.GetValue<string>() == "ZONE_TERRITORY"));
        document["profile_sha256"] = new string('0', 64);
        using (var draft = JsonDocument.Parse(document.ToJsonString()))
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(draft.RootElement);
        }

        File.WriteAllText(written.Path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return written.Path;
    }

    // ------------------------------------------------------------------ traffic

    /// <summary>
    /// Session1's shape with a mentor roulette: login, queue, pop, duty entry, duty exit. Every zone marker,
    /// territory and reply carries a distinct last byte, because the state machine ignores an event whose
    /// key - payload hash included - it has already seen in the session.
    /// </summary>
    public static IEnumerable<DecodedMessage> Evening(byte roulette = 9, bool pop = true)
    {
        var queue = CalibrationObserverTests.QueueAndPop(60_000, roulette, 120_000);
        return Distinct(CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.Noise(10_000, 60_000))
            .Concat(pop ? queue : queue.Take(2))
            .Concat(CalibrationObserverTests.Noise(61_000, 124_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Noise(130_000, 210_000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000))
            .Concat(CalibrationObserverTests.Noise(220_000, 240_000)), firstMarker: 1);
    }

    /// <summary>A second mentor roulette after <see cref="Evening"/>: queue, pop and the duty entry.</summary>
    public static IEnumerable<DecodedMessage> SecondDuty() => Distinct(
        CalibrationObserverTests.QueueAndPop(300_000, 9, 360_000)
            .Concat(CalibrationObserverTests.Noise(361_000, 364_000))
            .Concat(CalibrationObserverTests.Cluster(365_000, 1039))
            .Concat(CalibrationObserverTests.Noise(370_000, 380_000)), firstMarker: 101);

    public static DecodedMessage[] Before(IEnumerable<DecodedMessage> traffic, long ms) =>
        traffic.Where(message => message.Mono < TimeSpan.FromMilliseconds(ms)).ToArray();

    public static DecodedMessage[] From(IEnumerable<DecodedMessage> traffic, long ms) =>
        traffic.Where(message => message.Mono >= TimeSpan.FromMilliseconds(ms)).ToArray();

    private static IEnumerable<DecodedMessage> Distinct(IEnumerable<DecodedMessage> traffic, byte firstMarker)
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

/// <summary>Answers from a table of addresses; optionally holds each request or fails them all.</summary>
internal sealed class FakeSharedTransport
{
    private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly List<Uri> _requests = new();

    /// <summary>Runs before each answer; a test holds a request open with it.</summary>
    public Func<Uri, CancellationToken, Task>? BeforeAnswer { get; set; }

    /// <summary>Every request fails the way an unreachable network does.</summary>
    public bool Unreachable { get; set; }

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Serve(Uri uri, byte[] body)
    {
        lock (_routes)
        {
            _routes[uri.AbsoluteUri] = body;
        }
    }

    public async Task<SharedTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(uri);
        }

        if (BeforeAnswer is { } before)
        {
            await before(uri, cancellationToken).ConfigureAwait(false);
        }

        if (Unreachable)
        {
            throw new IOException("the network is unreachable");
        }

        byte[]? body;
        lock (_routes)
        {
            _routes.TryGetValue(uri.AbsoluteUri, out body);
        }

        return body is null
            ? new SharedTransportResponse(404, uri, 0, new MemoryStream())
            : new SharedTransportResponse(200, uri, body.Length, new MemoryStream(body));
    }
}
