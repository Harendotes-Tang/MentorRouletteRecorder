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
/// Telling the logged-in character apart while capture runs: when the traffic names a character
/// (<see cref="CharacterIdentityTracker"/>) and what the pipeline does about it
/// (docs/state-machine.md section 3.1).
/// </summary>
public sealed class CharacterIdentificationTests
{
    private const uint Main = 0x1011_2233;
    private const uint Alt = 0x1044_5566;
    private const uint Lobby = 0x0000_7777;
    private const string Build = "2026.11.03.0000.0000";

    private static DecodedMessage Segment(
        uint target, double seconds, string session = "s", ushort type = FfxivFraming.SegmentTypeIpc,
        MessageDirection direction = MessageDirection.Inbound, string connection = "zone") =>
        new(session, direction, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
            TimeSpan.FromSeconds(seconds), 0, type, 0x0123, new byte[16], connection)
        {
            SourceActor = target,
            TargetActor = target,
        };

    /// <summary>Feeds <paramref name="count"/> segments spread evenly over a span and returns the changes.</summary>
    private static List<CharacterIdentityChange> Feed(
        CharacterIdentityTracker tracker, uint target, int count, double from, double to)
    {
        var changes = new List<CharacterIdentityChange>();
        for (var index = 0; index < count; index++)
        {
            var change = tracker.Observe(Segment(target, from + (to - from) * index / Math.Max(1, count - 1)));
            if (change != CharacterIdentityChange.None)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    [Fact]
    public void ADenseRunOfSegmentsNamesTheCharacterAndStayingSettlesIt()
    {
        var tracker = new CharacterIdentityTracker();

        Assert.Empty(Feed(tracker, Main, CharacterIdentityTracker.WindowSize - 1, 0, 1));
        Assert.Null(tracker.Actor);
        Assert.Equal(CharacterIdentityChange.Named, tracker.Observe(Segment(Main, 1)));
        Assert.Equal((Main, false), (tracker.Actor, tracker.IsSettled));

        // Enough segments but not enough time, then enough of both.
        Assert.Empty(Feed(tracker, Main, CharacterIdentityTracker.SettleSamples + 10, 1, 5));
        Assert.Equal(new[] { CharacterIdentityChange.Settled }, Feed(tracker, Main, 50, 21, 25));
        Assert.True(tracker.IsSettled);
        Assert.Empty(Feed(tracker, Main, 2000, 25, 60));
    }

    [Fact]
    public void TheSelectionScreensTrickleNamesNobodyAndTakesNoNameAway()
    {
        var tracker = new CharacterIdentityTracker();

        // A few segments every eleven seconds, for an hour: never dense enough.
        for (var second = 0.0; second < 3600; second += 11)
        {
            Assert.Empty(Feed(tracker, Lobby, 3, second, second + 0.2));
        }

        Assert.Null(tracker.Actor);

        Feed(tracker, Main, 200, 3600, 3602);
        Assert.Equal(Main, tracker.Actor);
        // Back at the selection screen the name stays: a quiet moment is not a logout.
        for (var second = 3700.0; second < 4000; second += 11)
        {
            Assert.Empty(Feed(tracker, Lobby, 3, second, second + 0.2));
        }

        Assert.Equal(Main, tracker.Actor);
    }

    [Fact]
    public void AnotherCharactersLoginTakesTheNameAndStartsSettlingAgain()
    {
        var tracker = new CharacterIdentityTracker();
        Feed(tracker, Main, 1000, 0, 30);
        Assert.True(tracker.IsSettled);

        // The new character's burst is interleaved with the tail of the selection screen.
        var changes = new List<CharacterIdentityChange>();
        for (var index = 0; index < 300; index++)
        {
            var target = index % 20 == 0 ? Lobby : Alt;
            var change = tracker.Observe(Segment(target, 60 + index * 0.01));
            if (change != CharacterIdentityChange.None)
            {
                changes.Add(change);
            }
        }

        Assert.Equal(new[] { CharacterIdentityChange.Named }, changes);
        Assert.Equal((Alt, false), (tracker.Actor, tracker.IsSettled));
    }

    [Fact]
    public void OnlyInboundIpcSegmentsWithATargetAreCounted()
    {
        var tracker = new CharacterIdentityTracker();

        for (var index = 0; index < 500; index++)
        {
            tracker.Observe(Segment(Main, index * 0.01, direction: MessageDirection.Outbound));
            tracker.Observe(Segment(Main, index * 0.01, type: 7));
            tracker.Observe(Segment(0, index * 0.01));
        }

        Assert.Null(tracker.Actor);
        tracker.Reset();
        Feed(tracker, Main, 200, 10, 11);
        tracker.Reset();
        Assert.Equal((null, false), (tracker.Actor, tracker.IsSettled));
    }

    private sealed class Rig : IDisposable
    {
        private static readonly byte[] KeyBytes = Enumerable.Range(1, CaptureIdentityProbe.KeyBytes).Select(value => (byte)value).ToArray();
        private double _seconds;

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

        /// <summary>The synthetic profile's pop: a run is matched for whoever <paramref name="actor"/> is.</summary>
        public string Pop(uint actor)
        {
            var pop = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 900_001);
            _seconds += 1;
            Pipeline.Accept(new DecodedMessage(Session, MessageDirection.Inbound, Db.Clock.UtcNow.AddSeconds(_seconds),
                TimeSpan.FromSeconds(_seconds), 0, 61440, 61441, pop, "zone")
            {
                SourceActor = actor,
                TargetActor = actor,
            });
            return new RunRepository(Db.Database).Query(Domain.Queries.RunFilter.Empty, null, 1, 50).Items
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.RunId).First().RunId;
        }

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

        /// <summary>A login: a dense burst, then enough ordinary traffic to settle when asked.</summary>
        public void Login(uint actor, bool stay = true)
        {
            Send(actor, 200, 2);
            if (stay)
            {
                Send(actor, CharacterIdentityTracker.SettleSamples + 50, 30);
            }
        }

        public void Send(uint actor, int count, double overSeconds)
        {
            for (var index = 0; index < count; index++)
            {
                Pipeline.Accept(Segment(actor, _seconds + overSeconds * index / count, Session));
            }

            _seconds += overSeconds;
        }

        public long Current(Region region = Region.Cn) => Settings.Profiles.FindCurrent(region)!.ProfileId;

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
    }

    [Fact]
    public void APlayerWithOneCharacterIsBoundSilentlyOnceTheCharacterHasStayed()
    {
        using var rig = new Rig();
        var only = rig.Current();

        rig.Login(Main, stay: false);
        // Named, but a name that has only just appeared is not written down.
        Assert.Equal(0, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));

        rig.Send(Main, CharacterIdentityTracker.SettleSamples + 50, 30);

        Assert.Equal(1, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
        Assert.Empty(rig.Statistics.GetIdentities());
        Assert.Equal(only, rig.Current());
        Assert.Equal(0, rig.Announcements());
    }

    [Fact]
    public void ASecondCharacterIsAnnouncedAndAKnownOneSwitchesTheCurrentProfileAtLogin()
    {
        using var rig = new Rig();
        var main = rig.Current();
        rig.Login(Main);
        rig.Announcements();

        rig.StartSession();
        rig.Login(Alt);

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

        // Logging back in as the first character switches at once, before it has settled.
        rig.StartSession();
        rig.Login(Main, stay: false);
        Assert.Equal(main, rig.Current());
        Assert.Equal(1, rig.Announcements());

        rig.StartSession();
        rig.Login(Alt, stay: false);
        Assert.Equal(alt, rig.Current());
    }

    [Fact]
    public void NobodyIsToldApartWhereTheRegionIsNotVerifiedOrTheSettingIsOff()
    {
        using (var elsewhere = new Rig(Region.Global))
        {
            elsewhere.Login(Main);
            Assert.Equal(0, elsewhere.Statistics.GetProfiles().Sum(value => value.IdentityCount));
        }

        using var rig = new Rig();
        rig.Pipeline.ApplyCharacterIdentificationSetting(false);
        rig.Login(Main);
        Assert.Equal(0, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));

        rig.Pipeline.ApplyCharacterIdentificationSetting(true);
        rig.Login(Main);
        Assert.Equal(1, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
    }

    [Fact]
    public void WithoutAKeyNothingIsRecordedAndNothingFails()
    {
        using var rig = new Rig();
        rig.Pipeline.CharacterIdentityKeySource = _ => CharacterIdentityKeyResult.Unavailable;

        rig.Login(Main);

        Assert.Equal(0, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
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
        rig.Login(Main);
        rig.StartSession();
        rig.Login(Alt);
        Assert.Equal(1, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
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
            rig.Login(Main);
        }

        // Still the old identities, and nothing recorded meanwhile.
        Assert.Equal(0, replaced);
        Assert.Equal(1, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
        Assert.Single(rig.Statistics.GetIdentities());

        rig.StartSession();
        Assert.Equal(1, replaced);
        Assert.Equal(0, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));
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
    /// The message that matches a run names the client by itself. A run matched before the traffic
    /// has been dense enough to name anyone - capture restarted somewhere quiet - still belongs to
    /// its own character, not to the profile chosen by hand.
    /// </summary>
    [Fact]
    public void ARunMatchedBeforeAnyoneIsNamedBelongsToTheCharacterItsOwnMessageNames()
    {
        using var rig = new Rig(recording: true);
        var main = rig.Current();
        var runs = new RunRepository(rig.Db.Database);

        // One profile, nobody seen yet: the character of the first run is that profile.
        Assert.Equal(main, runs.Get(rig.Pop(Main))!.ProfileId);
        Assert.Equal(1, rig.Statistics.GetProfiles().Sum(value => value.IdentityCount));

        // Another character, in a session whose traffic names nobody: its run is nobody's until
        // the player answers, and the answer files it.
        rig.StartSession();
        var orphan = rig.Pop(Alt);
        Assert.Null(runs.Get(orphan)!.ProfileId);
        var waiting = Assert.Single(rig.Statistics.GetIdentities());
        Assert.Equal(1, waiting.RunCount);
        var alt = rig.Profiles.Create(new CreateCharacterProfileCommand(
            Guid.NewGuid().ToString("D"), Region.Cn, "小号", 2000, 0, false, waiting.Identity.IdentityId)).ProfileId;
        Assert.Equal(alt, runs.Get(orphan)!.ProfileId);

        // A message that names nobody leaves the run to the current profile, as before.
        rig.StartSession();
        Assert.Equal(rig.Current(), runs.Get(rig.Pop(0))!.ProfileId);
    }
}
