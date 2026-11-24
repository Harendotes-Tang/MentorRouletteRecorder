using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Storage.Mutations;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Character profiles: each has its own goal and baseline, a run belongs to at most one of them,
/// and a player with one character never meets any of it (docs/data-model.md, character_profiles).
/// </summary>
public sealed class CharacterProfileTests
{
    private sealed class Fixture : CharacterProfileTestContext
    {
        public RunMutationOutcome Completed(Region region = Region.Cn, long? profileId = null) =>
            Service.CreateManualRun(TestDatabase.ManualRun() with
            {
                Region = region, ProfileId = profileId, MatchedAtUtc = null,
            });

        public BatchRunMutationOutcome Assign(long? profileId, params RunMutationOutcome[] runs) =>
            Service.BatchMutateRuns(new BatchMutateRunsCommand(
                NewId(),
                RunMutationService.AssignProfileAction,
                runs.Select(run => new BatchRunTarget(run.RunId, Runs.Get(run.RunId)!.Revision)).ToArray(),
                "这些是小号打的",
                profileId));

        public CharacterProfileStatistics Progress(long profileId) =>
            Statistics.GetProfiles().Single(value => value.Profile.ProfileId == profileId);
    }

    [Fact]
    public void ARegionStartsWithOneUnnamedCurrentProfileThatOwnsEveryNewRun()
    {
        using var fixture = new Fixture();

        var run = fixture.Completed();

        var profile = Assert.Single(fixture.Settings.Profiles.List(region: Region.Cn));
        Assert.True(profile.IsCurrent);
        Assert.Equal(string.Empty, profile.DisplayName);
        Assert.Equal(profile.ProfileId, fixture.Runs.Get(run.RunId)!.ProfileId);
        var regional = fixture.Statistics.GetDashboard().Achievements.Single(row => row.Region == Region.Cn);
        Assert.Equal((profile.ProfileId, 1, 0, 1), (regional.ProfileId, regional.ProfileCount, regional.UnassignedRunCount, regional.AchievementProgress));
    }

    [Fact]
    public void ARegionWithoutAProfileGetsOneWhenItsFirstRunIsCreated()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Settings.Profiles.List(region: Region.Global));

        var run = fixture.Completed(Region.Global);

        var profile = Assert.Single(fixture.Settings.Profiles.List(region: Region.Global));
        Assert.True(profile.IsCurrent);
        Assert.Equal(profile.ProfileId, fixture.Runs.Get(run.RunId)!.ProfileId);
    }

    [Fact]
    public void EachCharacterAddsOnlyItsOwnCompletionsToItsOwnBaseline()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        fixture.Completed();
        fixture.Completed();

        var alt = fixture.Add("小号", baseline: 300, makeCurrent: true);
        fixture.Completed();

        Assert.Equal((0, 2, 2), (fixture.Progress(main).Profile.BaselineCompletedCount, fixture.Progress(main).CompletedCount, fixture.Progress(main).AchievementProgress));
        Assert.Equal((300, 1, 301, 1), (fixture.Progress(alt).Profile.BaselineCompletedCount, fixture.Progress(alt).CompletedCount, fixture.Progress(alt).AchievementProgress, fixture.Progress(alt).RunCount));
        // The region's entry, and the scalar fields of an unfiltered dashboard, follow the current character.
        var dashboard = fixture.Statistics.GetDashboard();
        var regional = dashboard.Achievements.Single(row => row.Region == Region.Cn);
        Assert.Equal((alt, 2, 301), (regional.ProfileId, regional.ProfileCount, regional.AchievementProgress));
        Assert.Equal(301, dashboard.AchievementProgress);
        // A dashboard narrowed to a character shows that character, whichever is current.
        var narrowed = fixture.Statistics.GetDashboard(new RunFilter { ProfileId = main });
        Assert.Equal((2, 0, 2, 1998), (narrowed.CompletedCount, narrowed.BaselineCompletedCount, narrowed.AchievementProgress, narrowed.Remaining));
    }

    [Fact]
    public void ARunStaysWithTheCharacterItWasFiledUnderWhenTheCurrentOneChanges()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        var first = fixture.Completed();
        var alt = fixture.Add("小号");

        fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), alt, null, MakeCurrent: true));
        var second = fixture.Completed();
        var named = fixture.Completed(profileId: main);

        Assert.Equal(alt, fixture.Current());
        Assert.Equal(main, fixture.Runs.Get(first.RunId)!.ProfileId);
        Assert.Equal(alt, fixture.Runs.Get(second.RunId)!.ProfileId);
        Assert.Equal(main, fixture.Runs.Get(named.RunId)!.ProfileId);
        Assert.Single(fixture.Settings.Profiles.List(region: Region.Cn), profile => profile.IsCurrent);
    }

    /// <summary>
    /// The owner of a recorded run is fixed when the match is made: a result that arrives after the
    /// player has switched the software to another character still settles the run under the
    /// character it began on.
    /// </summary>
    [Fact]
    public void ARecordedRunBelongsToTheCharacterThatWasCurrentWhenItWasMatched()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        var alt = fixture.Add("小号");
        var live = new LiveRunTestBed(fixture.Database,
            ProfileBinding.Live("test", Region.Cn, ProfileStatus.Verified, 42, true));

        var runId = live.Pop();
        live.Enter();
        fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), alt, null, MakeCurrent: true));
        live.Victory();

        var run = fixture.Runs.Get(runId)!;
        Assert.Equal((main, RunResult.Completed), (run.ProfileId, run.Result));
        Assert.Equal((1, 0), (fixture.Progress(main).CompletedCount, fixture.Progress(alt).CompletedCount));
    }

    [Fact]
    public void AssigningRunsMovesThemWithARevisionThatCorrectsNothingAndCanBeUndone()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        var first = fixture.Completed();
        var second = fixture.Completed();
        var alt = fixture.Add("小号", baseline: 10);

        var outcome = fixture.Assign(alt, first, second);

        Assert.Equal((2, RunMutationService.AssignProfileAction), (outcome.ChangedCount, outcome.Action));
        var moved = fixture.Runs.Get(first.RunId)!;
        Assert.Equal((alt, 2, false), (moved.ProfileId, moved.Revision, moved.ManuallyCorrected));
        var revision = fixture.Revisions.GetAt(first.RunId, 2, null)!;
        var change = Assert.Single(revision.Changes);
        Assert.Equal((RunFields.ProfileId, (object?)main, (object?)alt), (change.Field, Convert.ToInt64(change.OldValue), Convert.ToInt64(change.NewValue)));
        Assert.Equal((0, 0), (fixture.Progress(main).CompletedCount, fixture.Progress(main).AchievementProgress));
        // Nobody's baseline moves with the runs.
        Assert.Equal((2, 12), (fixture.Progress(alt).CompletedCount, fixture.Progress(alt).AchievementProgress));

        fixture.Service.UndoRevision(new RunReasonCommand(Fixture.NewId(), first.RunId, 2, "分错了"));

        Assert.Equal(main, fixture.Runs.Get(first.RunId)!.ProfileId);
        Assert.Equal((1, 1), (fixture.Progress(main).CompletedCount, fixture.Progress(alt).CompletedCount));
    }

    [Fact]
    public void AssigningToNobodyLeavesTheRunsOutOfEveryCharactersProgress()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        var run = fixture.Completed();
        fixture.Add("小号");

        fixture.Assign(null, run);

        Assert.Null(fixture.Runs.Get(run.RunId)!.ProfileId);
        Assert.Equal(0, fixture.Progress(main).CompletedCount);
        var regional = fixture.Statistics.GetDashboard().Achievements.Single(row => row.Region == Region.Cn);
        Assert.Equal((1, 0), (regional.UnassignedRunCount, regional.AchievementProgress));
        // The run is still a record of its region.
        Assert.Equal(1, fixture.Statistics.GetDashboard(new RunFilter { Region = Region.Cn }).CompletedCount);
        Assert.Equal(run.RunId, Assert.Single(fixture.Runs.Query(
            new RunFilter { ProfileUnassigned = true }, null, 1, 50).Items).RunId);
        Assert.Empty(fixture.Runs.Query(new RunFilter { ProfileId = main }, null, 1, 50).Items);
    }

    [Fact]
    public void AssigningRefusesAnotherRegionsCharacterAndARequestThatMovesNothing()
    {
        using var fixture = new Fixture();
        var run = fixture.Completed();
        var traditional = fixture.Current(Region.Tc);

        var foreign = Assert.Throws<CollectorException>(() => fixture.Assign(traditional, run));
        Assert.Equal((ErrorCodes.BadRequest, "profile.region_mismatch"), (foreign.Code, foreign.Text?.Key));
        var unchanged = Assert.Throws<CollectorException>(() => fixture.Assign(fixture.Current(), run));
        Assert.Equal((ErrorCodes.NoChanges, "profile.assign_unchanged"), (unchanged.Code, unchanged.Text?.Key));
        var missing = Assert.Throws<CollectorException>(() => fixture.Assign(9999, run));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Equal(1, fixture.Runs.Get(run.RunId)!.Revision);
    }

    [Fact]
    public void TheDatabaseItselfRefusesARunFiledUnderAnotherRegionsCharacter()
    {
        using var fixture = new Fixture();
        var traditional = fixture.Current(Region.Tc);

        var refused = Assert.Throws<SqliteException>(() => fixture.Database.Database.RunInTransaction(tx =>
            fixture.Runs.Insert(TestDatabase.Run() with { Region = Region.Cn, ProfileId = traditional }, tx)));

        Assert.Contains("its own region", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACharactersGoalAndBaselineAreItsOwnAndSoIsTheirHistory()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        var alt = fixture.Add("小号");

        var namedRequest = new UpdateAchievementBaselineCommand(
            Fixture.NewId(), 1000, 40, fixture.Database.Clock.UtcNow, "小号游戏内显示 40 次", Region.Cn, alt);
        var named = fixture.Service.UpdateAchievementBaseline(namedRequest);
        // The region beside a named profile is ignored, so the same request with another one is the same request.
        Assert.True(fixture.Service.UpdateAchievementBaseline(namedRequest with { Region = Region.Tc }).IdempotentReplay);
        var current = fixture.Service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Fixture.NewId(), 2000, 500, fixture.Database.Clock.UtcNow, "大号游戏内显示 500 次"));

        Assert.Equal((alt, 1000, 40), (named.Settings.ProfileId, named.Settings.GoalCount, named.Settings.BaselineCompletedCount));
        Assert.Equal(main, current.Settings.ProfileId);
        Assert.Equal((1000, 40), (fixture.Progress(alt).Profile.GoalCount, fixture.Progress(alt).AchievementProgress));
        Assert.Equal((2000, 500), (fixture.Progress(main).Profile.GoalCount, fixture.Progress(main).AchievementProgress));
        Assert.Equal("小号游戏内显示 40 次", Assert.Single(fixture.Settings.ReadBaselineAudit(fixture.Settings.Profiles.Find(alt)!)).Reason);
        Assert.Equal("大号游戏内显示 500 次", Assert.Single(fixture.Settings.ReadBaselineAudit(region: Region.Cn)).Reason);
        var missing = Assert.Throws<CollectorException>(() => fixture.Service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(Fixture.NewId(), 2000, 1, fixture.Database.Clock.UtcNow, "不存在的角色", Region.Cn, 9999)));
        Assert.Equal((ErrorCodes.NotFound, "profile.not_found"), (missing.Code, missing.Text?.Key));
    }

    [Fact]
    public void ANameIsTrimmedRequiredAndUniqueInsideItsRegion()
    {
        using var fixture = new Fixture();
        var alt = fixture.Add("  小号  ");

        Assert.Equal("小号", fixture.Settings.Profiles.Find(alt)!.DisplayName);
        foreach (var bad in new[] { "", "   ", new string('字', CharacterProfile.MaxDisplayNameLength + 1), "两\n行" })
        {
            var refused = Assert.Throws<CollectorException>(() => fixture.Add(bad));
            Assert.Equal(("profile.name_required", "display_name"), (refused.Text?.Key, refused.Field));
        }

        Assert.Equal("profile.name_taken", Assert.Throws<CollectorException>(() => fixture.Add("小号")).Text?.Key);
        // The same name in another region is another character.
        fixture.Add("小号", Region.Tc);
        var renamed = fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), fixture.Current(), "大号", false));
        Assert.Equal("大号", renamed.Profile!.DisplayName);
        Assert.Equal("profile.name_taken", Assert.Throws<CollectorException>(() =>
            fixture.Profiles.Update(new UpdateCharacterProfileCommand(Fixture.NewId(), alt, "大号", false))).Text?.Key);
    }

    [Fact]
    public void ARegionHoldsALimitedNumberOfCharacters()
    {
        using var fixture = new Fixture();
        for (var index = 2; index <= CharacterProfile.MaxProfilesPerRegion; index++)
        {
            fixture.Add("角色" + index);
        }

        var refused = Assert.Throws<CollectorException>(() => fixture.Add("再多一个"));

        Assert.Equal("profile.limit_reached", refused.Text?.Key);
        Assert.Equal(CharacterProfile.MaxProfilesPerRegion, fixture.Settings.Profiles.List(region: Region.Cn).Count);
    }

    [Fact]
    public void ACharacterIsRemovedOnlyWhenNothingBelongsToItAndItIsNeitherCurrentNorTheLast()
    {
        using var fixture = new Fixture();
        var main = fixture.Current();
        string Refusal(long profileId) => Assert.Throws<CollectorException>(() =>
            fixture.Profiles.Delete(new DeleteCharacterProfileCommand(Fixture.NewId(), profileId))).Text!.Key;

        Assert.Equal("profile.delete_last", Refusal(main));
        var alt = fixture.Add("小号");
        Assert.Equal("profile.delete_current", Refusal(main));
        var run = fixture.Completed(profileId: alt);
        fixture.Service.SoftDeleteRun(new RunReasonCommand(Fixture.NewId(), run.RunId, 1, "误记"));
        // A run in the recycle bin still has an owner.
        Assert.Equal("profile.delete_has_runs", Refusal(alt));

        fixture.Assign(main, run);
        fixture.Service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Fixture.NewId(), 2000, 7, fixture.Database.Clock.UtcNow, "小号的起始次数", Region.Cn, alt));
        var removed = fixture.Profiles.Delete(new DeleteCharacterProfileCommand(Fixture.NewId(), alt));

        Assert.Null(removed.Profile);
        Assert.Null(fixture.Settings.Profiles.Find(alt));
        Assert.DoesNotContain(fixture.Settings.ReadAllBaselineAudit(), entry => entry.Reason == "小号的起始次数");
        // The revision that moved the run away still names the removed character; undoing it leaves the run with nobody.
        fixture.Service.UndoRevision(new RunReasonCommand(Fixture.NewId(), run.RunId, 3, "撤销改归属"));
        Assert.Null(fixture.Runs.Get(run.RunId)!.ProfileId);
    }

    [Fact]
    public void ARepeatedRequestChangesNothingTwiceAndAReusedIdIsRefused()
    {
        using var fixture = new Fixture();
        var request = Fixture.NewId();
        var command = new CreateCharacterProfileCommand(request, Region.Cn, "小号", 2000, 5, false);

        var first = fixture.Profiles.Create(command);
        var replay = fixture.Profiles.Create(command);

        Assert.Equal((first.ProfileId, false, true), (replay.ProfileId, first.IdempotentReplay, replay.IdempotentReplay));
        Assert.Equal(2, fixture.Settings.Profiles.List(region: Region.Cn).Count);
        var reused = Assert.Throws<CollectorException>(() => fixture.Profiles.Create(command with { DisplayName = "另一个" }));
        Assert.Equal((ErrorCodes.IdempotencyConflict, "profile.request_reused"), (reused.Code, reused.Text?.Key));
    }
}
