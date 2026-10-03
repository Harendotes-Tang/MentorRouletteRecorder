using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Recovery;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-03, V3-1. 1.5.0 stored the effective time each save of the achievement settings was sent with,
/// and its Desktop sent the save time every time, a goal-only edit included. With the baseline now counting only
/// the completions that ended after its effective time, such a database would drop every completion recorded
/// between the baseline's real entry and the last goal-only save. The first start puts the time back from the
/// baseline's own change history, once, and only where that history can tell.
/// </summary>
public sealed class BaselineEffectiveTimeRepairTests : IDisposable
{
    private static readonly DateTimeOffset Entered = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GoalOnly = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void AGoalOnlySaveOfAnEarlierVersionGivesTheDroppedCompletionsBack()
    {
        WriteAsAnEarlierVersionSaved((2000, 1500, Entered), (2500, 1500, GoalOnly));
        CompleteAt(Entered.AddDays(1));
        Assert.Equal(1500, Statistics().GetDashboard().AchievementProgress);

        int entries;
        using (var first = Restart())
        {
            var settings = first.Settings.GetAchievementSettings();
            Assert.Equal(Entered, settings.BaselineEffectiveAt);
            Assert.Equal(1500, settings.BaselineCompletedCount);
            Assert.Equal(2500, settings.GoalCount);
            Assert.Equal(1501, first.Statistics.GetDashboard().AchievementProgress);

            // Recorded in the baseline's own trail, as the system's change and with its reason.
            var history = first.Settings.ReadBaselineAudit();
            Assert.Equal(3, history.Count);
            var repair = history[^1];
            Assert.Equal(BaselineEffectiveTimeRepair.RequestId, repair.RequestId);
            Assert.False(string.IsNullOrWhiteSpace(repair.Reason));
            Assert.Equal(1500, repair.BaselineCompletedCount);
            Assert.Equal(2500, repair.GoalCount);
            Assert.Equal(Entered, UtcTimestamp.Parse(repair.BaselineEffectiveAt));
            Assert.NotNull(first.Settings.GetSetting(BaselineEffectiveTimeRepair.CheckedSetting));
            entries = history.Count;
        }

        using var second = Restart();
        Assert.Equal(Entered, second.Settings.GetAchievementSettings().BaselineEffectiveAt);
        Assert.Equal(entries, second.Settings.ReadBaselineAudit().Count);
    }

    /// <summary>
    /// A history at its cap still tells where the baseline was entered, when the entry before that is in it.
    /// </summary>
    [Fact]
    public void AFullHistoryStillTellsWhenTheBaselineWasEntered()
    {
        var saves = new List<(int, int, DateTimeOffset)> { (2000, 1400, Entered.AddDays(-1)) };
        saves.AddRange(Enumerable.Range(0, SettingsRepository.MaxBaselineAuditEntries - 1)
            .Select(i => (2000 + i, 1500, Entered.AddMinutes(i))));
        WriteAsAnEarlierVersionSaved(saves.ToArray());
        Assert.Equal(SettingsRepository.MaxBaselineAuditEntries, SettingsOf().ReadBaselineAudit().Count);

        using var host = Restart();
        Assert.Equal(Entered, host.Settings.GetAchievementSettings().BaselineEffectiveAt);
    }

    /// <summary>The history shows the baseline itself changed at the stored time: that time is right.</summary>
    [Fact]
    public void ABaselineThatChangedWhenItWasLastSavedKeepsItsTime()
    {
        WriteAsAnEarlierVersionSaved((2000, 1400, Entered), (2000, 1500, GoalOnly));

        using var host = Restart();
        Assert.Equal(GoalOnly, host.Settings.GetAchievementSettings().BaselineEffectiveAt);
        Assert.Equal(2, host.Settings.ReadBaselineAudit().Count);
        Assert.NotNull(host.Settings.GetSetting(BaselineEffectiveTimeRepair.CheckedSetting));
    }

    /// <summary>
    /// Nothing is moved where the history cannot tell: none at all, one cut at its cap with every entry kept naming
    /// the same baseline, one that does not end with the stored row, and a baseline of 0, which no time affects.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("trimmed")]
    [InlineData("disagrees")]
    [InlineData("zero")]
    public void WhenTheHistoryCannotTellTheStoredTimeIsKept(string history)
    {
        var stored = GoalOnly;
        switch (history)
        {
            case "none":
                WriteRow(2500, 1500, GoalOnly);
                break;
            case "trimmed":
                WriteAsAnEarlierVersionSaved(Enumerable.Range(0, SettingsRepository.MaxBaselineAuditEntries + 1)
                    .Select(i => (2000 + i, 1500, Entered.AddMinutes(i))).ToArray());
                stored = Entered.AddMinutes(SettingsRepository.MaxBaselineAuditEntries);
                break;
            case "disagrees":
                WriteAsAnEarlierVersionSaved((2000, 1500, Entered), (2500, 1500, Entered.AddDays(1)));
                WriteRow(2500, 1500, GoalOnly);
                break;
            case "zero":
                WriteAsAnEarlierVersionSaved((2000, 0, Entered), (2500, 0, GoalOnly));
                break;
        }

        var entries = SettingsOf().ReadBaselineAudit().Count;

        using var host = Restart();
        Assert.Equal(stored, host.Settings.GetAchievementSettings().BaselineEffectiveAt);
        Assert.Equal(entries, host.Settings.ReadBaselineAudit().Count);
        Assert.NotNull(host.Settings.GetSetting(BaselineEffectiveTimeRepair.CheckedSetting));
    }

    /// <summary>
    /// The check is made once, by the first start of this version: what is saved afterwards follows this version's
    /// rule and is never second-guessed, whatever shape the history takes.
    /// </summary>
    [Fact]
    public void TheCheckIsMadeOnlyOnce()
    {
        WriteAsAnEarlierVersionSaved((2000, 1500, Entered));
        using (var first = Restart())
        {
            Assert.Equal(Entered, first.Settings.GetAchievementSettings().BaselineEffectiveAt);
            Assert.NotNull(first.Settings.GetSetting(BaselineEffectiveTimeRepair.CheckedSetting));
        }

        WriteAsAnEarlierVersionSaved((2500, 1500, GoalOnly));

        using var second = Restart();
        Assert.Equal(GoalOnly, second.Settings.GetAchievementSettings().BaselineEffectiveAt);
        Assert.Equal(2, second.Settings.ReadBaselineAudit().Count);
    }

    private SettingsRepository SettingsOf() => new(_database.Database, _database.Clock);

    private StatisticsRepository Statistics() => new(_database.Database, SettingsOf());

    /// <summary>
    /// What 1.5.0 stored for each save: the row with the effective time the request carried - the save time, as
    /// its Desktop always sent - and the same values appended to the history.
    /// </summary>
    private void WriteAsAnEarlierVersionSaved(params (int Goal, int Baseline, DateTimeOffset At)[] saves)
    {
        _database.Reopen();
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
                settings.AppendBaselineAudit(Guid.NewGuid().ToString("D"), stored, "保存成就设置", Guid.NewGuid().ToString("D"), tx);
            }
        });
    }

    /// <summary>The row alone, as if its history had been lost or never matched it.</summary>
    private void WriteRow(int goal, int baseline, DateTimeOffset at)
    {
        _database.Reopen();
        _database.Database.RunInTransaction(tx => SettingsOf().UpdateAchievementSettings(new AchievementSettings
        {
            GoalCount = goal, BaselineCompletedCount = baseline, BaselineEffectiveAt = at, UpdatedAtUtc = at,
        }, tx));
    }

    private void CompleteAt(DateTimeOffset endedAt)
    {
        var settings = SettingsOf();
        settings.EnsureDefaults();
        new RunMutationService(_database.Database, settings, _database.Clock).CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
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
