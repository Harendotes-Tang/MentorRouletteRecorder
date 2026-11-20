using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Telling the logged-in character apart while capture runs: the login message that says who a
/// character is (<see cref="CharacterLoginRecognizer"/>) and what the pipeline does about it
/// (docs/state-machine.md section 3.1).
/// </summary>
public sealed class CharacterIdentificationTests
{
    private const ulong Main = 0x0040_1122_3344_5566;
    private const ulong Alt = 0x0040_7788_99AA_BBCC;
    private const ulong Third = 0x0040_DDEE_FF00_1122;
    private const string Build = "2026.11.03.0000.0000";

    /// <summary>A server-to-client IPC message about the client, as every game message is.</summary>
    private static DecodedMessage Inbound(
        uint actor, byte[] payload, TimeSpan mono, string connection = "zone", string session = "s",
        MessageDirection direction = MessageDirection.Inbound, ushort segment = FfxivFraming.SegmentTypeIpc,
        uint? source = null) =>
        new(session, direction, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).Add(mono), mono, 0,
            segment, 0x01ae, payload, connection)
        {
            SourceActor = source ?? actor,
            TargetActor = actor,
        };

    /// <summary>The login message of the 2026.11.03 client: 2952 bytes, identifier first, actor at 24.</summary>
    private static byte[] LoginPayload(ulong character, uint actor, int length = 2952, int actorOffset = 24)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, character);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(actorOffset), actor);
        return payload;
    }

    private static (bool Recognised, ulong Character, uint Actor) Recognise(
        CharacterLoginRecognizer recognizer, DecodedMessage message)
    {
        Span<byte> identifier = stackalloc byte[CharacterLoginRecognizer.IdentifierBytes];
        var recognised = recognizer.TryRecognise(message, identifier, out var actor);
        return (recognised, recognised ? BinaryPrimitives.ReadUInt64LittleEndian(identifier) : 0, actor);
    }

    [Fact]
    public void TheLoginMessageIsKnownByItsShapeAndGivesTheCharactersLastingIdentifier()
    {
        var recognizer = new CharacterLoginRecognizer();
        var start = TimeSpan.FromSeconds(10);

        // The burst that opens a game connection, then the login message 186 ms later.
        Assert.False(Recognise(recognizer, Inbound(0x1001, new byte[64], start)).Recognised);
        var login = Recognise(recognizer, Inbound(0x1001, LoginPayload(Main, 0x1001), start + TimeSpan.FromMilliseconds(186)));

        Assert.Equal((true, Main, 0x1001u), login);
        // Once per connection: a later message of the same shape says nothing new.
        Assert.False(Recognise(recognizer, Inbound(0x1001, LoginPayload(Alt, 0x1001), start + TimeSpan.FromSeconds(1))).Recognised);
        // The next login opens another connection, and the actor may be another one for the same character.
        Assert.Equal((true, Main, 0x2002u),
            Recognise(recognizer, Inbound(0x2002, LoginPayload(Main, 0x2002), start + TimeSpan.FromMinutes(1), "zone-2")));
    }

    public static TheoryData<string, DecodedMessage> NotALoginMessage()
    {
        var at = TimeSpan.FromSeconds(1);
        return new TheoryData<string, DecodedMessage>
        {
            { "client to server", Inbound(0x1001, LoginPayload(Main, 0x1001), at, direction: MessageDirection.Outbound) },
            { "not an IPC segment", Inbound(0x1001, LoginPayload(Main, 0x1001), at, segment: 7) },
            { "about somebody else", Inbound(0x1001, LoginPayload(Main, 0x1001), at, source: 0x3003) },
            { "no actor", Inbound(0, LoginPayload(Main, 0), at) },
            { "too short", Inbound(0x1001, LoginPayload(Main, 0x1001, length: CharacterLoginRecognizer.MinimumLength - 4), at) },
            { "actor elsewhere", Inbound(0x1001, LoginPayload(Main, 0x1001, actorOffset: 8), at) },
            { "no identifier", Inbound(0x1001, LoginPayload(0, 0x1001), at) },
            { "identifier is the actor", Inbound(0x1001, LoginPayload(0x0000_0001_0000_1001, 0x1001), at) },
            { "identifier holds the actor", Inbound(0x1001, LoginPayload(0x0000_1001_0000_0002, 0x1001), at) },
        };
    }

    [Theory]
    [MemberData(nameof(NotALoginMessage))]
    public void AMessageOfAnotherShapeIsNotTakenForALogin(string why, DecodedMessage message)
    {
        Assert.False(Recognise(new CharacterLoginRecognizer(), message).Recognised, why);
    }

    /// <summary>
    /// The first large message about the client decides a connection: on the selection screen's
    /// connection it is not a login, and nothing later on that connection is taken for one.
    /// </summary>
    [Fact]
    public void TheFirstLargeMessageAboutTheClientDecidesTheConnection()
    {
        var recognizer = new CharacterLoginRecognizer();
        var at = TimeSpan.FromSeconds(1);

        Assert.False(Recognise(recognizer, Inbound(0x1001, new byte[1168], at, "lobby")).Recognised);
        Assert.False(Recognise(recognizer, Inbound(0x1001, LoginPayload(Main, 0x1001), at + TimeSpan.FromMilliseconds(5), "lobby")).Recognised);
        // Small messages and messages about somebody else decide nothing.
        Assert.False(Recognise(recognizer, Inbound(0x2002, new byte[512], at, "zone")).Recognised);
        Assert.False(Recognise(recognizer, Inbound(0x2002, new byte[2000], at, "zone", source: 0x9999)).Recognised);
        Assert.True(Recognise(recognizer, Inbound(0x2002, LoginPayload(Main, 0x2002), at, "zone")).Recognised);
    }

    [Fact]
    public void ALoginMessageComesSoonAfterItsConnectionOpensOrNotAtAll()
    {
        var recognizer = new CharacterLoginRecognizer();
        var opened = TimeSpan.FromSeconds(5);
        Recognise(recognizer, Inbound(0x1001, new byte[64], opened));

        Assert.False(Recognise(recognizer, Inbound(0x1001, LoginPayload(Main, 0x1001), opened + CharacterLoginRecognizer.Window + TimeSpan.FromMilliseconds(1))).Recognised);

        recognizer.Reset();
        Assert.True(Recognise(recognizer, Inbound(0x1001, LoginPayload(Main, 0x1001), opened + TimeSpan.FromMinutes(1))).Recognised);
    }

    private sealed class Rig : IDisposable
    {
        private static readonly byte[] KeyBytes = Enumerable.Range(1, CaptureIdentityProbe.KeyBytes).Select(value => (byte)value).ToArray();
        private double _seconds;
        private int _connections;

        /// <summary>The game connection of the latest login.</summary>
        public string LastConnection { get; private set; } = "zone-0";

        public Rig(Region region = Region.Cn, bool recording = false)
        {
            Settings = new SettingsRepository(Db.Database, Db.Clock);
            Settings.EnsureDefaults();
            Profiles = new CharacterProfileService(Db.Database, Settings, Db.Clock);
            Statistics = new StatisticsRepository(Db.Database, Settings, clock: Db.Clock);
            Events = Bus.Subscribe("test");
            // The synthetic profile records runs; it names no region, so the rig lends it one.
            var synthetic = recording
                ? ProfileSelector.SelectExplicit(Path.Combine(
                    AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json"), allowSynthetic: true)
                    with { Region = region }
                : null;
            Pipeline = new LiveProtocolPipeline(Db.Database, Db.Clock, Bus, game => synthetic ?? new ProfileSelection(
                ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null, game.Region, game.GameBuild,
                ProfileSelector.NoProfileMatchesReason))
            {
                CharacterIdentityKeySource = Key,
            };
            Pipeline.CharacterIdentityNoted += Notes.Add;
            Pipeline.Refresh(GameProcessDetection.NotRunning with { Region = region, GameBuild = Build });
            StartSession(region);
        }

        /// <summary>The key source in use: the rig's own key, always ready, unless a test swaps it.</summary>
        public static CharacterIdentityKeyResult Key(bool replace) =>
            new(new CharacterIdentityKey(KeyBytes), CharacterIdentityKeyStatus.Ready);

        public List<CharacterIdentityNote> Notes { get; } = new();

        public TestDatabase Db { get; } = new();

        public LiveEventBus Bus => _bus ??= new LiveEventBus(Db.Clock);

        private LiveEventBus? _bus;

        public SettingsRepository Settings { get; }

        public CharacterProfileService Profiles { get; }

        public StatisticsRepository Statistics { get; }

        public LiveEventSubscription Events { get; }

        public LiveProtocolPipeline Pipeline { get; }

        public string Session { get; private set; } = string.Empty;

        public void StartSession(Region region = Region.Cn)
        {
            Session = Guid.NewGuid().ToString("D");
            Db.Database.RunInTransaction(tx => new CaptureSessionRepository(Db.Database).Insert(new CaptureSession
            {
                CaptureSessionId = Session, StartedAtUtc = Db.Clock.UtcNow, CollectorVersion = "test",
                Region = region, GameBuild = Build, ProfileStatus = ProfileStatus.UnsupportedBuild,
            }, tx));
            Pipeline.OnCaptureStarted(Session);
        }

        /// <summary>
        /// A character enters the game: a new game connection opens with a burst and the login
        /// message follows. <paramref name="actor"/> is the actor it has this time.
        /// </summary>
        public void Login(ulong character, uint actor)
        {
            var connection = "zone-" + ++_connections;
            LastConnection = connection;
            for (var index = 0; index < 50; index++)
            {
                Pipeline.Accept(Inbound(actor, new byte[64], Mono(0.001), connection, Session));
            }

            Pipeline.Accept(Inbound(actor, LoginPayload(character, actor), Mono(0.1), connection, Session));
            for (var index = 0; index < 50; index++)
            {
                Pipeline.Accept(Inbound(actor, new byte[64], Mono(0.01), connection, Session));
            }
        }

        /// <summary>
        /// The synthetic profile's pop on a game connection - the latest login's by default: a run
        /// is matched for whoever logged in there. An hour after the last one and with its own
        /// bundle time, so it is a run of its own: neither the last one offered again nor a repeat
        /// of the same message.
        /// </summary>
        public string Pop(uint actor, string? connection = null)
        {
            var pop = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
            BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 900_001);
            var at = Mono(3600);
            Pipeline.Accept(new DecodedMessage(Session, MessageDirection.Inbound, Db.Clock.UtcNow.Add(at),
                at, (long)at.TotalMilliseconds, 61440, 61441, pop, connection ?? LastConnection)
            {
                SourceActor = actor,
                TargetActor = actor,
            });
            return new RunRepository(Db.Database).Query(Domain.Queries.RunFilter.Empty, null, 1, 50).Items
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.RunId).First().RunId;
        }

        public long Current(Region region = Region.Cn) => Settings.Profiles.FindCurrent(region)!.ProfileId;

        public int BoundIdentities() => Statistics.GetProfiles().Sum(value => value.IdentityCount);

        public int Announcements()
        {
            var count = 0;
            while (Events.ReadAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None).GetAwaiter().GetResult() is { } payload)
            {
                if (payload["event_type"]?.GetValue<string>() == "DiagnosticsMessage" ||
                    payload["kind"]?.GetValue<string>() == "stats_invalidated")
                {
                    count++;
                }
            }

            return count;
        }

        public void Dispose()
        {
            Events.Dispose();
            Db.Dispose();
        }

        private TimeSpan Mono(double advanceSeconds)
        {
            _seconds += advanceSeconds;
            return TimeSpan.FromSeconds(_seconds);
        }
    }

    [Fact]
    public void ANewPlayerWithOneCharacterIsBoundWithoutPrompting()
    {
        using var rig = new Rig();
        var only = rig.Current();

        rig.Login(Main, 0x1001);

        Assert.Equal(1, rig.BoundIdentities());
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Equal(only, rig.Current());
        Assert.Equal(1, rig.Announcements());
    }

    /// <summary>
    /// What the real-machine rounds of 2026-11-14 to 2026-11-16 showed: one character kept its
    /// login identifier on all three days and had another actor on the third. It is the same
    /// character, so nothing is asked and nothing new is recorded.
    /// </summary>
    [Fact]
    public void ACharacterThatComesBackWithAnotherActorIsStillTheSameCharacter()
    {
        using var rig = new Rig();
        var main = rig.Current();
        rig.Login(Main, 0x1001);
        rig.Announcements();

        rig.StartSession();
        rig.Login(Main, 0x7777);

        Assert.Equal(1, rig.BoundIdentities());
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Equal(main, rig.Current());
        Assert.Equal(0, rig.Announcements());
    }

    [Fact]
    public void ASecondCharacterIsAnnouncedAndAKnownOneSwitchesTheCurrentProfileAtLogin()
    {
        using var rig = new Rig();
        var main = rig.Current();
        rig.Login(Main, 0x1001);
        rig.Announcements();

        rig.StartSession();
        rig.Login(Alt, 0x2002);

        var waiting = Assert.Single(rig.Statistics.GetIdentities());
        Assert.Equal(CharacterIdentityState.Pending, waiting.Identity.State);
        Assert.Equal(main, rig.Current());
        Assert.Equal(1, rig.Announcements());

        var alt = rig.Profiles.Create(new CreateCharacterProfileCommand(
            Guid.NewGuid().ToString("D"), Region.Cn, "小号", 2000, 0, false, waiting.Identity.IdentityId)).ProfileId;
        // The answer is about the character that is logged in, so its profile becomes current.
        Assert.Equal(main, rig.Current());
        rig.Pipeline.ReconsiderCharacter();
        Assert.Equal(alt, rig.Current());

        // Logging back in as the first character switches at once.
        rig.StartSession();
        rig.Login(Main, 0x1001);
        Assert.Equal(main, rig.Current());
        Assert.Equal(1, rig.Announcements());

        // Back to the selection screen and in again as the second, in the same session.
        rig.Login(Alt, 0x2002);
        Assert.Equal(alt, rig.Current());
    }

    [Fact]
    public void NobodyIsToldApartWhereTheRegionIsNotVerifiedOrTheSettingIsOff()
    {
        using (var elsewhere = new Rig(Region.Global))
        {
            elsewhere.Login(Main, 0x1001);
            Assert.Equal(0, elsewhere.BoundIdentities());
        }

        using var rig = new Rig();
        rig.Pipeline.ApplyCharacterIdentificationSetting(false);
        rig.Login(Main, 0x1001);
        Assert.Equal(0, rig.BoundIdentities());

        rig.Pipeline.ApplyCharacterIdentificationSetting(true);
        rig.Login(Main, 0x1001);
        Assert.Equal(1, rig.BoundIdentities());
    }

    [Fact]
    public void WithoutAKeyNothingIsRecordedAndNothingFails()
    {
        using var rig = new Rig();
        rig.Pipeline.CharacterIdentityKeySource = _ => CharacterIdentityKeyResult.Unavailable;

        rig.Login(Main, 0x1001);

        Assert.Equal(0, rig.BoundIdentities());
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Contains(rig.Notes, note => note.Event == "key_unavailable");
    }

    /// <summary>
    /// One failure to use the key file can be a bad moment; replacing the key forgets every
    /// character. So the file is replaced only after it has stayed unusable for three sessions,
    /// the characters written under the old key go with it, and the log says so.
    /// </summary>
    [Fact]
    public void AKeyFileThatStaysUnusableIsReplacedOnTheThirdSessionAndTheLogSaysSo()
    {
        using var rig = new Rig();
        rig.Login(Main, 0x1001);
        rig.StartSession();
        rig.Login(Alt, 0x2002);
        Assert.Equal(1, rig.BoundIdentities());
        Assert.Single(rig.Statistics.GetIdentities());
        var other = new CharacterIdentityKey(Enumerable.Repeat((byte)9, CaptureIdentityProbe.KeyBytes).ToArray());
        var replaced = 0;
        rig.Pipeline.CharacterIdentityKeySource = replace =>
        {
            if (!replace)
            {
                return new CharacterIdentityKeyResult(null, CharacterIdentityKeyStatus.Unusable);
            }

            replaced++;
            return new CharacterIdentityKeyResult(other, CharacterIdentityKeyStatus.Ready);
        };

        for (var session = 1; session < LiveProtocolPipeline.KeyFailuresBeforeReplacing; session++)
        {
            rig.StartSession();
            rig.Login(Main, 0x1001);
        }

        // Still the old identities, and nothing recorded meanwhile.
        Assert.Equal(0, replaced);
        Assert.Equal(1, rig.BoundIdentities());
        Assert.Single(rig.Statistics.GetIdentities());

        rig.StartSession();
        Assert.Equal(1, replaced);
        Assert.Equal(0, rig.BoundIdentities());
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Equal(
            new[] { "key_unusable", "key_unusable", "key_replaced", "identities_removed" },
            rig.Notes.Select(note => note.Event).ToArray());
        Assert.Equal(2, rig.Notes[^1].Count);

        // A key that works again starts the count over.
        rig.Pipeline.CharacterIdentityKeySource = Rig.Key;
        rig.StartSession();
        Assert.Equal("0", rig.Settings.GetSetting(CharacterIdentification.KeyFailuresSetting));
    }

    /// <summary>
    /// A run belongs to the character that logged in on the connection its matching message came
    /// on. A connection without a login of this session belongs to somebody unknown, so its run
    /// belongs to nobody rather than to the character played before it; a session in which no login
    /// message was recognised at all (a patch changed its shape, say) files runs as before
    /// identification, and the log says why.
    /// </summary>
    [Fact]
    public void ARunBelongsToTheCharacterThatLoggedInOnTheConnectionOfItsMatchingMessage()
    {
        using var rig = new Rig(recording: true);
        var main = rig.Current();
        var runs = new RunRepository(rig.Db.Database);

        rig.Login(Main, 0x1001);
        Assert.Equal(main, runs.Get(rig.Pop(0x1001))!.ProfileId);

        // The second character waits for an answer; its run is nobody's until then, and the
        // answer files it. A second later, so "seen last" below is never a tie by accident (audit
        // 2026-11-20, D1-2).
        rig.Db.Clock.UtcNow += TimeSpan.FromSeconds(1);
        rig.Login(Alt, 0x2002);
        var orphan = rig.Pop(0x2002);
        Assert.Null(runs.Get(orphan)!.ProfileId);
        var waiting = Assert.Single(rig.Statistics.GetIdentities());
        Assert.Equal(1, waiting.RunCount);
        var alt = rig.Profiles.Create(new CreateCharacterProfileCommand(
            Guid.NewGuid().ToString("D"), Region.Cn, "小号", 2000, 0, false, waiting.Identity.IdentityId)).ProfileId;
        Assert.Equal(alt, runs.Get(orphan)!.ProfileId);

        // The first character's connection still says who played there.
        Assert.Equal(main, runs.Get(rig.Pop(0x1001, "zone-1"))!.ProfileId);

        // A connection no login of this session opened: nobody's, whatever profile is current.
        Assert.Null(runs.Get(rig.Pop(0x3003, "zone-unknown"))!.ProfileId);
        Assert.Contains(rig.Notes, note => note.Event == "character_unknown_at_match");

        // No login message recognised in the session, and the character seen last - the main, back
        // a minute later - is the current profile's: the current profile, as before.
        rig.Db.Clock.UtcNow += TimeSpan.FromMinutes(1);
        rig.Login(Main, 0x1001);
        Assert.Equal(main, rig.Current());
        rig.StartSession();
        Assert.Equal(main, runs.Get(rig.Pop(0x1001))!.ProfileId);
        rig.Pop(0x1001);
        Assert.Single(rig.Notes, note => note.Event == "login_not_recognised");

        // The same, when the character seen last is not the current profile's: the alt played
        // last and the player then made the main current by hand. Whose run this is cannot be
        // told, so it is nobody's until the player says (owner decision 2026-11-19, audit D1-1).
        rig.Db.Clock.UtcNow += TimeSpan.FromMinutes(5);
        rig.Login(Alt, 0x2002);
        Assert.Equal(alt, rig.Current());
        rig.Profiles.Update(new UpdateCharacterProfileCommand(
            Guid.NewGuid().ToString("D"), main, null, MakeCurrent: true, ForgetIdentities: false));
        rig.StartSession();
        Assert.Null(runs.Get(rig.Pop(0x1001))!.ProfileId);

        // Two characters seen in the same instant: either being the current profile's is enough.
        rig.Db.Clock.UtcNow += TimeSpan.FromMinutes(5);
        rig.Login(Main, 0x1001);
        rig.Login(Alt, 0x2002);
        Assert.Equal(alt, rig.Current());
        rig.StartSession();
        Assert.Equal(alt, runs.Get(rig.Pop(0x2002))!.ProfileId);

        // A character still waiting for an answer played last: the current profile may be
        // somebody else's, so the run is nobody's.
        rig.Db.Clock.UtcNow += TimeSpan.FromMinutes(5);
        rig.Login(Third, 0x4004);
        Assert.Equal(alt, rig.Current());
        rig.StartSession();
        Assert.Null(runs.Get(rig.Pop(0x4004))!.ProfileId);
    }

    /// <summary>
    /// Another character can come with the actor an earlier one had: it is the login, not the
    /// actor, that says who plays on a connection.
    /// </summary>
    [Fact]
    public void AnotherCharacterWithAnActorAnEarlierOneHadIsStillAnotherCharacter()
    {
        using var rig = new Rig(recording: true);
        var main = rig.Current();
        var runs = new RunRepository(rig.Db.Database);
        rig.Login(Main, 0x1001);

        rig.Login(Alt, 0x1001);

        Assert.Equal(CharacterIdentityState.Pending, Assert.Single(rig.Statistics.GetIdentities()).Identity.State);
        Assert.Null(runs.Get(rig.Pop(0x1001))!.ProfileId);
        Assert.Equal(main, runs.Get(rig.Pop(0x1001, "zone-1"))!.ProfileId);
    }

    /// <summary>
    /// Identification turned on while a game connection is open must not take ordinary traffic on
    /// it for a login: the connection was decided while identification was off.
    /// </summary>
    [Fact]
    public void TurningIdentificationOnMidConnectionTakesNothingOnItForALogin()
    {
        using var rig = new Rig(recording: true);
        var runs = new RunRepository(rig.Db.Database);
        rig.Pipeline.ApplyCharacterIdentificationSetting(false);
        rig.Login(Main, 0x1001);

        rig.Pipeline.ApplyCharacterIdentificationSetting(true);
        rig.Pipeline.Accept(Inbound(0x1001, LoginPayload(Alt, 0x1001), TimeSpan.FromMilliseconds(1), rig.LastConnection, rig.Session));

        Assert.Equal(0, rig.BoundIdentities());
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Equal(rig.Current(), runs.Get(rig.Pop(0x1001))!.ProfileId);
        Assert.Contains(rig.Notes, note => note.Event == "login_not_recognised");
    }
}
