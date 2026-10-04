using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-03, B3-1. The progress adds every recorded completion to the baseline whenever it ended, so the
/// stored effective time decides nothing, and a database an earlier version wrote is read and saved as it is.
/// Start-up leaves the baseline and its history exactly as stored, also where 1.5.0 moved the time on a goal-only
/// save. The two 1.5.1 betas checked that time once at start-up and may have left a setting recording the check
/// and a system entry in the history; such a database works like any other.
/// </summary>
public sealed class AchievementBaselineUpgradeTests : IDisposable
{
    private static readonly DateTimeOffset Entered = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GoalOnly = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The setting the betas wrote once they had checked the time.</summary>
    private const string BetaCheckedSetting = "achievement.baseline_effective_at_checked";

    /// <summary>Request id of the history entry the betas appended when they moved the time.</summary>
    private const string BetaRepairRequestId = "system:baseline-effective-at-repair";

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void StartUpLeavesTheBaselineAnEarlierVersionStoredAsItIs()
    {
        WriteAsAnEarlierVersionSaved((2000, 1500, Entered), (2500, 1500, GoalOnly));
        CompleteAt(Entered.AddDays(-1));
        CompleteAt(Entered.AddDays(1));
        var stored = SettingsOf().GetAchievementSettings();
        var history = SettingsOf().ReadBaselineAudit();

        using var host = Restart();

        Assert.Equal(stored, host.Settings.GetAchievementSettings());
        Assert.Equal(history, host.Settings.ReadBaselineAudit());
        Assert.Null(host.Settings.GetSetting(BetaCheckedSetting));
        Assert.Equal(1502, host.Statistics.GetDashboard().AchievementProgress);
    }

    [Fact]
    public void ADatabaseABetaCheckedReadsAndSavesNormally()
    {
        WriteAsAnEarlierVersionSaved((2000, 1500, Entered), (2500, 1500, GoalOnly));
        WriteAsABetaRepaired(2500, 1500, Entered, GoalOnly.AddDays(1));
        CompleteAt(Entered.AddDays(-1));
        CompleteAt(Entered.AddDays(1));
        var stored = SettingsOf().GetAchievementSettings();

        using var host = Restart();

        Assert.Equal(stored, host.Settings.GetAchievementSettings());
        Assert.Equal(3, host.Settings.ReadBaselineAudit().Count);
        Assert.Equal(1502, host.Statistics.GetDashboard().AchievementProgress);

        // Saved as it is: nothing is written, and the answer names the beta's entry, which stored these values.
        var unchanged = host.Mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2500, 1500, GoalOnly.AddDays(2), "原样保存"));
        var history = host.Settings.ReadBaselineAudit();
        Assert.False(unchanged.Changed);
        Assert.Equal(3, history.Count);
        Assert.Equal(BetaRepairRequestId, history[^1].RequestId);
        Assert.Equal(history[^1].AuditEventId, unchanged.AuditEventId);
        Assert.Equal(stored, host.Settings.GetAchievementSettings());

        // A new baseline is saved like any other, and every recorded completion stays on top of it.
        var changedAt = GoalOnly.AddDays(3);
        var changed = host.Mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2500, 1510, changedAt, "游戏内显示 1510 次"));
        history = host.Settings.ReadBaselineAudit();
        Assert.True(changed.Changed);
        Assert.Equal(changedAt, changed.Settings.BaselineEffectiveAt);
        Assert.Equal(4, history.Count);
        Assert.Equal(changed.AuditEventId, history[^1].AuditEventId);
        Assert.Equal(1512, host.Statistics.GetDashboard().AchievementProgress);
        Assert.Equal("true", host.Settings.GetSetting(BetaCheckedSetting));
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private SettingsRepository SettingsOf() => new(_database.Database, _database.Clock);

    /// <summary>
    /// What 1.5.0 stored for each save: the row with the effective time the request carried - the save time, as
    /// its Desktop always sent - and the same values appended to the history.
    /// </summary>
    private void WriteAsAnEarlierVersionSaved(params (int Goal, int Baseline, DateTimeOffset At)[] saves)
    {
        var settings = SettingsOf();
        _database.Database.RunInTransaction(tx =>
        {
            foreach (var (goal, baseline, at) in saves)
            {
                var stored = new AchievementSettings
                {
                    GoalCount = goal, BaselineCompletedCount = baseline, BaselineEffectiveAt = at, UpdatedAtUtc = at,
                };
                settings.UpdateAchievementSettings(stored, tx);
                settings.AppendBaselineAudit(NewId(), stored, "保存成就设置", NewId(), tx);
            }
        });
    }

    /// <summary>
    /// What a 1.5.1 beta's start-up check left when it moved the time: the row with the time put back, a system
    /// entry with the same values at the end of the history, and the setting recording the check.
    /// </summary>
    private void WriteAsABetaRepaired(int goal, int baseline, DateTimeOffset effectiveAt, DateTimeOffset at)
    {
        var settings = SettingsOf();
        _database.Database.RunInTransaction(tx =>
        {
            var repaired = new AchievementSettings
            {
                GoalCount = goal, BaselineCompletedCount = baseline, BaselineEffectiveAt = effectiveAt, UpdatedAtUtc = at,
            };
            settings.UpdateAchievementSettings(repaired, tx);
            settings.AppendBaselineAudit(
                NewId(),
                repaired,
                "早期版本在只修改目标或原样保存同一基数时也会改动基数的生效时间，使此前已记录的完成不再计入进度；" +
                "现按基数的修改记录改回首次填入该基数的时间。",
                BetaRepairRequestId,
                tx);
        });
        settings.SetSetting(BetaCheckedSetting, "true");
    }

    private void CompleteAt(DateTimeOffset endedAt)
    {
        var settings = SettingsOf();
        settings.EnsureDefaults();
        new RunMutationService(_database.Database, settings, _database.Clock).CreateManualRun(new CreateManualRunCommand
        {
            RequestId = NewId(),
            Reason = "补录一次导随",
            Result = RunResult.Completed,
            ContentId = 900001,
            JobId = 19,
            ContributesToGoal = true,
            MatchedAtUtc = endedAt.AddMinutes(-31),
            EnteredAtUtc = endedAt.AddMinutes(-30),
            EndedAtUtc = endedAt,
        });
    }

    private CollectorHost Restart()
    {
        _database.Database.Dispose();
        return CollectorHost.Open(_database.Path, _database.Clock, new CaptureServices());
    }
}
