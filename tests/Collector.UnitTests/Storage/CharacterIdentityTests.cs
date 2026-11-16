using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Which profile a character seen in the game traffic is (docs/data-model.md,
/// character_identities): what the Collector decides by itself, what waits for the player, and
/// where a run goes in the meantime.
/// </summary>
public sealed class CharacterIdentityTests
{
    private const string Key = "0a1b2c3d";
    private const string Main = "11111111111111111111111111111111";
    private const string Alt = "22222222222222222222222222222222";
    private const string Third = "33333333333333333333333333333333";

    private sealed class Fixture : RunMutationTestContext
    {
        public Fixture()
        {
            Profiles = new CharacterProfileService(Database.Database, Settings, Database.Clock);
            Identities = new CharacterIdentityService(Database.Database, Settings, Database.Clock);
            Statistics = new StatisticsRepository(Database.Database, Settings, clock: Database.Clock);
        }

        public CharacterProfileService Profiles { get; }

        public CharacterIdentityService Identities { get; }

        public StatisticsRepository Statistics { get; }

        public static string NewId() => Guid.NewGuid().ToString("D");

        public long Current(Region region = Region.Cn) => Settings.Profiles.FindCurrent(region)!.ProfileId;

        public long Add(string name, Region region = Region.Cn, bool makeCurrent = false, long? identityId = null) =>
            Profiles.Create(new CreateCharacterProfileCommand(NewId(), region, name, 2000, 0, makeCurrent, identityId))
                .ProfileId;

        public CharacterIdentityObservation Login(string digest, Region region = Region.Cn) =>
            Identities.Observe(region, Key, digest);

        public CharacterIdentityOutcome Answer(long identityId, CharacterIdentityAction action, long? profileId = null) =>
            Identities.Resolve(new ResolveCharacterIdentityCommand(NewId(), identityId, action, profileId));

        /// <summary>Matches one run while the character behind <paramref name="digest"/> is logged in.</summary>
        public string Match(string? digest)
        {
            var session = Guid.NewGuid().ToString("D");
            var start = Database.Clock.UtcNow;
            Database.Database.RunInTransaction(tx => new CaptureSessionRepository(Database.Database).Insert(
                new CaptureSession
                {
                    CaptureSessionId = session, StartedAtUtc = start, CollectorVersion = "test",
                    Region = Region.Cn, ProfileStatus = ProfileStatus.Verified,
                }, tx));
            var processor = new SemanticEventProcessor(Database.Database,
                new MentorRunStateMachine(ProfileBinding.Live("test", Region.Cn, ProfileStatus.Verified, 42, true)),
                new SemanticEventProcessorOptions(session, Region.Cn, "test", session), Database.Clock,
                identityAtMatch: digest is null
                    ? null
                    : new Func<SqliteTransaction, CharacterIdentityObservation?>(
                        tx => Identities.Observe(Region.Cn, Key, digest, tx)));
            processor.Accept(new ContentFinderPop
            {
                Key = new EventKey(session, PacketDirection.ServerToClient, "pop", 0, null, "pop0"),
                ObservedAtUtc = start, Mono = TimeSpan.Zero, RouletteId = 42, ContentId = 900001,
            });
            Assert.Null(processor.LastStorageError);
            return processor.Machine.CurrentRunId!;
        }

        public int BoundTo(long profileId) =>
            Statistics.GetProfiles().Single(value => value.Profile.ProfileId == profileId).IdentityCount;
    }

    [Fact]
    public void TheFirstCharacterOfAOneProfileRegionIsBoundWithoutAskingOrAnnouncing()
    {
        using var fixture = new Fixture();
        var only = fixture.Current();

        var seen = fixture.Login(Main);

        Assert.Equal((only, false), (seen.ProfileId, seen.StatisticsChanged));
        Assert.NotNull(seen.IdentityId);
        Assert.Empty(fixture.Statistics.GetIdentities());
        Assert.Equal(1, fixture.BoundTo(only));
        // The same character again is the same identity, not a second one.
        Assert.Equal(seen.IdentityId, fixture.Login(Main).IdentityId);
        Assert.Equal(only, fixture.Runs.Get(fixture.Match(Main))!.ProfileId);
    }

    [Fact]
    public void ASecondCharacterWaitsForAnAnswerAndItsRunsBelongToNobody()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);

        var second = fixture.Login(Alt);
        var run = fixture.Match(Alt);

        Assert.Equal((null, true), (second.ProfileId, second.StatisticsChanged));
        // Never the character played before it, and never the current profile.
        Assert.Null(fixture.Runs.Get(run)!.ProfileId);
        Assert.Equal(main, fixture.Current());
        var waiting = Assert.Single(fixture.Statistics.GetIdentities());
        Assert.Equal((second.IdentityId, CharacterIdentityState.Pending, 1),
            (waiting.Identity.IdentityId, waiting.Identity.State, waiting.RunCount));
        var region = fixture.Statistics.GetDashboard().Achievements.Single(value => value.Region == Region.Cn);
        Assert.Equal((1, CharacterIdentificationMode.Automatic), (region.UnassignedRunCount, region.Identification));
    }

    [Fact]
    public void ARegionThatAlreadyHoldsTwoProfilesAsksAboutItsFirstCharacterToo()
    {
        using var fixture = new Fixture();
        fixture.Add("小号");

        var seen = fixture.Login(Main);

        Assert.Null(seen.ProfileId);
        Assert.Equal(CharacterIdentityState.Pending, Assert.Single(fixture.Statistics.GetIdentities()).Identity.State);
    }

    [Fact]
    public void AnsweringFilesTheRunsRecordedForTheCharacterAndLeavesTheCurrentProfileToTheLogin()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var second = fixture.Login(Alt).IdentityId!.Value;
        var before = fixture.Match(Alt);
        var mine = fixture.Match(Main);
        var alt = fixture.Add("小号");
        var revision = fixture.Runs.Get(before)!.Revision;

        var command = new ResolveCharacterIdentityCommand(Fixture.NewId(), second, CharacterIdentityAction.Bind, alt);
        var answered = fixture.Identities.Resolve(command);
        var replayed = fixture.Identities.Resolve(command);

        Assert.Equal((alt, 1, false), (answered.ProfileId, answered.AssignedRunCount, answered.IdempotentReplay));
        Assert.Equal((alt, 1, true), (replayed.ProfileId, replayed.AssignedRunCount, replayed.IdempotentReplay));
        // The stamp arriving late, not an edit: same revision, nothing in the history.
        Assert.Equal((alt, revision), (fixture.Runs.Get(before)!.ProfileId, fixture.Runs.Get(before)!.Revision));
        Assert.Null(fixture.Revisions.GetAt(before, revision + 1, null));
        Assert.Equal(main, fixture.Runs.Get(mine)!.ProfileId);
        // The answer does not say who is logged in, so it does not change the current profile.
        Assert.Equal(main, fixture.Current());
        Assert.Empty(fixture.Statistics.GetIdentities());
        Assert.Equal((1, 1), (fixture.BoundTo(main), fixture.BoundTo(alt)));
        // From now on the character's runs go straight to its profile, which a match makes current.
        Assert.Equal(alt, fixture.Runs.Get(fixture.Match(Alt))!.ProfileId);
        Assert.Equal(alt, fixture.Current());

        var reused = Assert.Throws<CollectorException>(() => fixture.Identities.Resolve(command with { ProfileId = main }));
        Assert.Equal(ErrorCodes.IdempotencyConflict, reused.Code);
    }

    [Fact]
    public void ANewCharacterIsNamedAndBoundInOneRequest()
    {
        using var fixture = new Fixture();
        fixture.Login(Main);
        var second = fixture.Login(Alt).IdentityId!.Value;
        var run = fixture.Match(Alt);

        var alt = fixture.Add("小号", identityId: second);

        Assert.Equal(alt, fixture.Runs.Get(run)!.ProfileId);
        Assert.NotEqual(alt, fixture.Current());
        Assert.Empty(fixture.Statistics.GetIdentities());
        // The character being the one logged in is what makes its profile current.
        Assert.True(fixture.Identities.Recognise(Region.Cn, Key, Alt).StatisticsChanged);
        Assert.Equal(alt, fixture.Current());

        // An identity that is somebody already cannot become a second new character.
        var refused = Assert.Throws<CollectorException>(() => fixture.Add("第三个", identityId: second));
        Assert.Equal("identity_id", refused.Field);
        Assert.DoesNotContain(fixture.Settings.Profiles.List(region: Region.Cn), profile => profile.DisplayName == "第三个");
    }

    [Fact]
    public void AKnownCharacterMakesItsProfileCurrentWhenItLogsIn()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var alt = fixture.Add("小号", makeCurrent: true);

        var back = fixture.Identities.Recognise(Region.Cn, Key, Main);
        var again = fixture.Identities.Recognise(Region.Cn, Key, Main);
        var stranger = fixture.Identities.Recognise(Region.Cn, Key, Third);

        Assert.Equal((main, true), (back.ProfileId, back.StatisticsChanged));
        Assert.Equal((main, false), (again.ProfileId, again.StatisticsChanged));
        Assert.Equal(main, fixture.Current());
        Assert.NotEqual(alt, fixture.Current());
        // A name that has only just appeared is not written down.
        Assert.Null(stranger.IdentityId);
        Assert.Empty(fixture.Statistics.GetIdentities());
    }

    [Fact]
    public void ARunGoesToTheLoggedInCharactersProfileWhateverProfileIsCurrent()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var alt = fixture.Add("小号");
        // Chosen by hand after the login; the match puts the character's own profile back.
        fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), alt, null, MakeCurrent: true));

        var run = fixture.Match(Main);

        Assert.Equal(main, fixture.Runs.Get(run)!.ProfileId);
        Assert.Equal(main, fixture.Current());
        // Nobody telling characters apart: the run goes to the profile chosen by hand.
        fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), alt, null, MakeCurrent: true));
        Assert.Equal(alt, fixture.Runs.Get(fixture.Match(null))!.ProfileId);
    }

    [Fact]
    public void AnIgnoredCharacterKeepsNoProgressAndCanBeAskedAboutAgain()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var guest = fixture.Login(Alt).IdentityId!.Value;

        fixture.Answer(guest, CharacterIdentityAction.Ignore);
        var run = fixture.Match(Alt);

        var ignored = Assert.Single(fixture.Statistics.GetIdentities());
        Assert.Equal((CharacterIdentityState.Ignored, 1), (ignored.Identity.State, ignored.RunCount));
        Assert.Null(fixture.Runs.Get(run)!.ProfileId);
        Assert.False(fixture.Login(Alt).StatisticsChanged);

        Assert.Equal("identity_id", Assert.Throws<CollectorException>(
            () => fixture.Answer(fixture.Login(Main).IdentityId!.Value, CharacterIdentityAction.AskAgain)).Field);
        fixture.Answer(guest, CharacterIdentityAction.AskAgain);
        Assert.Equal(CharacterIdentityState.Pending, Assert.Single(fixture.Statistics.GetIdentities()).Identity.State);

        // An ignored or waiting character can still be said to be somebody; a bound one cannot.
        Assert.Equal(1, fixture.Answer(guest, CharacterIdentityAction.Bind, main).AssignedRunCount);
        Assert.Equal("identity_id", Assert.Throws<CollectorException>(
            () => fixture.Answer(guest, CharacterIdentityAction.Bind, main)).Field);
        Assert.Equal("identity_id", Assert.Throws<CollectorException>(
            () => fixture.Answer(guest, CharacterIdentityAction.Ignore)).Field);
    }

    /// <summary>
    /// What the player filed by hand stays where the player put it: an answer about the character
    /// given later moves only the runs nobody has touched.
    /// </summary>
    [Fact]
    public void ARunThePlayerFiledByHandIsNotMovedByALaterAnswer()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var friends = fixture.Match(Main);
        var mine = fixture.Match(Main);
        var run = fixture.Runs.Get(friends)!;
        fixture.Service.BatchMutateRuns(new BatchMutateRunsCommand(
            Fixture.NewId(), RunMutationService.AssignProfileAction,
            new[] { new BatchRunTarget(run.RunId, run.Revision) }, "这场是朋友打的", null));

        fixture.Profiles.Update(new UpdateCharacterProfileCommand(
            Fixture.NewId(), main, null, MakeCurrent: false, ForgetIdentities: true));
        var asked = Assert.Single(fixture.Statistics.GetIdentities());
        var answered = fixture.Answer(asked.Identity.IdentityId, CharacterIdentityAction.Bind, main);

        Assert.Equal((0, 0), (asked.RunCount, answered.AssignedRunCount));
        Assert.Null(fixture.Runs.Get(friends)!.ProfileId);
        Assert.Equal(main, fixture.Runs.Get(mine)!.ProfileId);
    }

    [Fact]
    public void AnAnswerNeedsAnExistingIdentityAndAProfileOfTheSameRegion()
    {
        using var fixture = new Fixture();
        fixture.Login(Main);
        var second = fixture.Login(Alt).IdentityId!.Value;
        var elsewhere = fixture.Add("国际服角色", Region.Global);

        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(
            () => fixture.Answer(second + 100, CharacterIdentityAction.Ignore)).Code);
        Assert.Equal("profile_id", Assert.Throws<CollectorException>(
            () => fixture.Answer(second, CharacterIdentityAction.Bind)).Field);
        Assert.Equal("profile_id", Assert.Throws<CollectorException>(
            () => fixture.Answer(second, CharacterIdentityAction.Bind, elsewhere)).Field);
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(
            () => fixture.Answer(second, CharacterIdentityAction.Bind, 9999)).Code);
        Assert.Equal(CharacterIdentityState.Pending, Assert.Single(fixture.Statistics.GetIdentities()).Identity.State);
        // A new character of another region cannot be this one either, and nothing is created.
        Assert.Equal("identity_id", Assert.Throws<CollectorException>(
            () => fixture.Add("别的区服", Region.Global, identityId: second)).Field);
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(
            () => fixture.Add("不存在的", identityId: second + 100)).Code);
        Assert.DoesNotContain(fixture.Settings.Profiles.List(), profile => profile.DisplayName is "别的区服" or "不存在的");
    }

    [Fact]
    public void ForgettingOrRemovingAProfileAsksAboutItsCharactersAgainAndKeepsTheirRuns()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var run = fixture.Match(Main);

        fixture.Profiles.Update(new UpdateCharacterProfileCommand(
            Fixture.NewId(), main, null, MakeCurrent: false, ForgetIdentities: true));

        var asked = Assert.Single(fixture.Statistics.GetIdentities());
        // Nothing recorded for it is without an owner, so an answer would move nothing.
        Assert.Equal((CharacterIdentityState.Pending, 0), (asked.Identity.State, asked.RunCount));
        Assert.Equal(main, fixture.Runs.Get(run)!.ProfileId);
        Assert.Equal(0, fixture.BoundTo(main));

        var spare = fixture.Add("建错的", identityId: asked.Identity.IdentityId);
        fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), main, null, MakeCurrent: true));
        fixture.Profiles.Delete(new DeleteCharacterProfileCommand(Fixture.NewId(), spare));

        Assert.Equal(CharacterIdentityState.Pending, Assert.Single(fixture.Statistics.GetIdentities()).Identity.State);
    }

    [Fact]
    public void IdentitiesWrittenUnderAKeyThatIsGoneAreRemovedAndTheirRunsStay()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Login(Main);
        var orphan = fixture.Match(Alt);

        Assert.Equal(0, fixture.Identities.ForgetOtherKeys(Key));
        Assert.Equal(2, fixture.Identities.ForgetOtherKeys("ffffffff"));

        Assert.Empty(fixture.Statistics.GetIdentities());
        Assert.Equal(0, fixture.BoundTo(main));
        Assert.Null(fixture.Runs.Get(orphan)!.ProfileId);
        // With nothing left, the next character seen is the region's first again.
        Assert.Equal(main, fixture.Identities.Observe(Region.Cn, "ffffffff", Main).ProfileId);
    }

    [Fact]
    public void TheWayARegionFindsARunsProfileFollowsTheRegionAndTheSetting()
    {
        using var fixture = new Fixture();

        var on = fixture.Statistics.GetDashboard().Achievements.ToDictionary(value => value.Region, value => value.Identification);
        fixture.Settings.SetSetting(CharacterIdentification.EnabledSetting, "false");
        var off = fixture.Statistics.GetDashboard().Achievements.ToDictionary(value => value.Region, value => value.Identification);

        Assert.Equal(CharacterIdentificationMode.Automatic, on[Region.Cn]);
        Assert.Equal(CharacterIdentificationMode.ManualRegion, on[Region.Tc]);
        Assert.All(off.Values, mode => Assert.Equal(CharacterIdentificationMode.ManualOff, mode));
    }

    [Fact]
    public void NothingThatLeavesTheCollectorCarriesADigestOrTheKeyFingerprint()
    {
        using var fixture = new Fixture();
        fixture.Login(Main);
        fixture.Login(Alt);
        fixture.Match(Alt);

        var dashboard = Wire.Dashboard(fixture.Statistics.GetDashboard()).ToJsonString();
        var runs = string.Concat(fixture.Runs.Query(RunFilter.Empty, null, 1, 50).Items.Select(run => Wire.Run(run).ToJsonString()));

        Assert.Contains("\"identities\"", dashboard);
        foreach (var secret in new[] { Main, Alt, Key, "digest", "key_id" })
        {
            Assert.DoesNotContain(secret, dashboard);
            Assert.DoesNotContain(secret, runs);
        }

        Assert.DoesNotContain("identity_id", runs);
    }
}
