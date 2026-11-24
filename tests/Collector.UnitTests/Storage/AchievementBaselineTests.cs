using MentorRecorder.Collector.Capture;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The achievement goal and baseline (基数): saving them with their history
/// (<see cref="RunMutationService.UpdateAchievementBaseline"/>), the checks a baseline must pass against the
/// recorded completions, and databases an earlier version wrote.
/// </summary>
public sealed class AchievementBaselineTests : IDisposable
{
    private static readonly DateTimeOffset Entered = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GoalOnly = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The setting the betas wrote once they had checked the time.</summary>
    private const string BetaCheckedSetting = "achievement.baseline_effective_at_checked";

    /// <summary>Request id of the history entry the betas appended when they moved the time.</summary>
    private const string BetaRepairRequestId = "system:baseline-effective-at-repair";

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// Audit 2026-10-03, B3-1. The progress adds every recorded completion to the baseline whenever it ended, so the
    /// stored effective time decides nothing, and a database an earlier version wrote is read and saved as it is.
    /// Start-up leaves the baseline and its history exactly as stored, also where 1.5.0 moved the time on a goal-only
    /// save.
    /// </summary>
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

    /// <summary>
    /// Audit 2026-10-03, B3-1. The two 1.5.1 betas checked the effective time once at start-up and may have left a
    /// setting recording the check and a system entry in the history; such a database works like any other.
    /// </summary>
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

    [Fact]
    public void RegionalBaselinesAuditsAndRetryIdentitiesRemainIndependent()
    {
        var settings = _database.Settings();
        var mutations = _database.Mutations();
        var cn = new UpdateAchievementBaselineCommand(NewId(), 2000, 1200, Entered, "国服进度");
        var tc = new UpdateAchievementBaselineCommand(NewId(), 1000, 500, Entered, "繁中进度", Region.Tc);
        var cnResult = mutations.UpdateAchievementBaseline(cn);
        var tcResult = mutations.UpdateAchievementBaseline(tc);
        Assert.Equal(Region.Cn, cnResult.Settings.Region);
        Assert.Equal(Region.Tc, tcResult.Settings.Region);
        Assert.Equal(1200, settings.GetAchievementSettings().BaselineCompletedCount);
        Assert.Equal(500, settings.GetAchievementSettings(region: Region.Tc).BaselineCompletedCount);
        Assert.Equal(Region.Cn, Assert.Single(settings.ReadBaselineAudit()).Region);
        Assert.Equal(Region.Tc, Assert.Single(settings.ReadBaselineAudit(region: Region.Tc)).Region);
        Assert.True(mutations.UpdateAchievementBaseline(tc).IdempotentReplay);
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            mutations.UpdateAchievementBaseline(tc with { Region = Region.Cn })).Code);

        // Updating the TC audit past its retention cap must not rotate out a CN entry.
        for (var i = 1; i <= SettingsRepository.MaxBaselineAuditEntries; i++)
            mutations.UpdateAchievementBaseline(tc with { RequestId = NewId(), BaselineCompletedCount = 500 + i });
        Assert.Equal(cnResult.AuditEventId, Assert.Single(settings.ReadBaselineAudit()).AuditEventId);
        Assert.Equal(SettingsRepository.MaxBaselineAuditEntries, settings.ReadBaselineAudit(region: Region.Tc).Count);
    }

    [Fact]
    public void LegacyMutationSnapshotWithoutRegionStillReplaysAsCn()
    {
        var settings = _database.Settings();
        var mutations = _database.Mutations();
        var request = new UpdateAchievementBaselineCommand(NewId(), 2000, 100, Entered, "旧请求");
        var first = mutations.UpdateAchievementBaseline(request);
        using (var command = _database.Database.CreateCommand())
        {
            command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id=$id;";
            command.Parameters.AddWithValue("$id", request.RequestId);
            var snapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            Assert.True(snapshot["settings"]!.AsObject().Remove("Region"));
            command.CommandText = "UPDATE ipc_idempotency SET response_json=$json WHERE request_id=$id;";
            command.Parameters.AddWithValue("$json", snapshot.ToJsonString());
            command.ExecuteNonQuery();
        }
        mutations.UpdateAchievementBaseline(request with { RequestId = NewId(), Region = Region.Tc, BaselineCompletedCount = 50 });
        var replay = mutations.UpdateAchievementBaseline(request);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(Region.Cn, replay.Settings.Region);
        Assert.Equal(first.Settings, replay.Settings);
        Assert.Equal(50, settings.GetAchievementSettings(region: Region.Tc).BaselineCompletedCount);
    }

    [Fact]
    public void UpdateAchievementBaseline_StoresGoalBaselineAndAudit()
    {
        var service = _database.Mutations();

        var outcome = service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(),
            2000,
            1500,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "开始使用本软件前已完成 1500 次"));

        Assert.Equal(1500, outcome.Settings.BaselineCompletedCount);
        Assert.Equal(2000, outcome.Settings.GoalCount);
        var audit = _database.Settings().ReadBaselineAudit();
        Assert.Equal(outcome.AuditEventId, Assert.Single(audit).AuditEventId);
    }

    [Fact]
    public void UpdateAchievementBaseline_GoalOnlyChange_AuditsTheEffectiveTimeThatWasKept()
    {
        var service = _database.Mutations();
        var effective = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1500, effective, "开始使用本软件前已完成 1500 次"));

        var outcome = service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2500, 1500, effective.AddMonths(9), "只调整目标"));

        var audit = _database.Settings().ReadBaselineAudit();
        Assert.Equal(2, audit.Count);
        Assert.Equal(outcome.AuditEventId, audit[1].AuditEventId);
        Assert.Equal(2500, audit[1].GoalCount);
        Assert.Equal(1500, audit[1].BaselineCompletedCount);
        Assert.Equal("2026-01-01T00:00:00.000Z", audit[1].BaselineEffectiveAt);
    }

    /// <summary>
    /// Audit 2026-10-03, S33-3. Saving the achievement settings as they are - the same goal and the
    /// same baseline, whatever time and reason come with them - changes nothing, so nothing is
    /// written: no history entry pushes an older one out of the capped history, and the stored row
    /// keeps its time. The answer is what is stored, under the history entry that stored it.
    /// </summary>
    [Fact]
    public void UpdateAchievementBaseline_UnchangedSave_WritesNothingAndAnswersTheStoredValues()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        var effective = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1500, effective, "开始使用本软件前已完成 1500 次"));
        var stored = settings.GetAchievementSettings();
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);

        var again = service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1500, effective.AddMonths(9), "原样保存"));

        Assert.Equal(first.AuditEventId, Assert.Single(settings.ReadBaselineAudit()).AuditEventId);
        Assert.Equal(stored, settings.GetAchievementSettings());
        Assert.Equal(stored, again.Settings);
        Assert.Equal(first.AuditEventId, again.AuditEventId);
        Assert.False(again.IdempotentReplay);
    }

    /// <summary>
    /// A database whose defaults were never saved has no history entry to name. Saving the defaults
    /// as they are writes nothing all the same, and the answer carries an identifier of its own that
    /// a replay of the request repeats.
    /// </summary>
    [Fact]
    public void UpdateAchievementBaseline_SavingTheUnsavedDefaultsAsTheyAre_WritesNothing()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        var defaults = settings.GetAchievementSettings();
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);
        var save = new UpdateAchievementBaselineCommand(
            NewId(), defaults.GoalCount, 0, _database.Clock.UtcNow, "从 0 开始");

        var outcome = service.UpdateAchievementBaseline(save);
        var replay = service.UpdateAchievementBaseline(save);

        Assert.Empty(settings.ReadBaselineAudit());
        Assert.Equal(defaults, settings.GetAchievementSettings());
        Assert.Equal(defaults, outcome.Settings);
        Assert.True(Guid.TryParseExact(outcome.AuditEventId, "D", out _));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(outcome.AuditEventId, replay.AuditEventId);
    }

    /// <summary>
    /// A save that changed nothing is still answered by its request id: replayed after a later change,
    /// it repeats its own answer and does not put its numbers back, and the same id with other numbers
    /// is refused.
    /// </summary>
    [Fact]
    public void UpdateAchievementBaseline_UnchangedSave_IsReplayedByItsRequestId()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        var effective = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1500, effective, "开始使用本软件前已完成 1500 次"));
        var unchanged = new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1500, effective.AddDays(1), "原样保存");
        var answered = service.UpdateAchievementBaseline(unchanged);
        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, 1600, effective.AddDays(2), "游戏内显示 1600 次"));

        var replay = service.UpdateAchievementBaseline(unchanged);
        var conflict = Assert.Throws<CollectorException>(() =>
            service.UpdateAchievementBaseline(unchanged with { GoalCount = 2500 }));

        Assert.True(replay.IdempotentReplay);
        Assert.Equal(answered.Settings, replay.Settings);
        Assert.Equal(answered.AuditEventId, replay.AuditEventId);
        Assert.Equal(1600, settings.GetAchievementSettings().BaselineCompletedCount);
        Assert.Equal(2, settings.ReadBaselineAudit().Count);
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Code);
    }

    [Theory]
    [InlineData(0, 0, ErrorCodes.BadRequest)]
    [InlineData(2000, -1, ErrorCodes.BadRequest)]
    public void UpdateAchievementBaseline_RejectsOutOfRangeValues(int goal, int baseline, string expected)
    {
        var service = _database.Mutations();

        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(
                NewId(), goal, baseline, DateTimeOffset.UnixEpoch, "越界")));

        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void UpdateAchievementBaseline_WithoutReason_IsRefused()
    {
        var service = _database.Mutations();

        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(
                NewId(), 2000, 10, DateTimeOffset.UnixEpoch, "  ")));

        Assert.Equal(ErrorCodes.ReasonRequired, error.Code);
    }

    [Fact]
    public void BaselineThatWouldOverflowTheCurrentProgressIsRejectedWithoutChangingSettings()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        CreateManual(service);
        var before = settings.GetAchievementSettings();

        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(NewId(), 2000, int.MaxValue, _database.Clock.UtcNow, "极值校验")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("baseline_completed_count", error.Field);
        Assert.Equal(before, settings.GetAchievementSettings());
        Assert.Empty(settings.ReadBaselineAudit());
    }

    /// <summary>
    /// The check adds every contributing completion, as the dashboard does: one that ended before the
    /// time the baseline is saved with counts as well (audit 2026-10-03, B3-1).
    /// </summary>
    [Fact]
    public void BaselineOverflowCheckCountsCompletionsThatEndedBeforeTheSaveTime()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        CreateManual(service);
        var before = settings.GetAchievementSettings();

        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(NewId(), 2000, int.MaxValue, _database.Clock.UtcNow.AddDays(1), "极值校验")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("baseline_completed_count", error.Field);
        Assert.Equal(before, settings.GetAchievementSettings());
    }

    /// <summary>
    /// The completion after the maximum baseline is an automatic one, recorded through the state machine: a
    /// manual run would take the validated path that refuses the overflow.
    /// </summary>
    [Fact]
    public void ACompletionAfterTheMaximumBaselineDoesNotBreakTheDashboard()
    {
        var settings = _database.Settings();
        _database.Mutations().UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, int.MaxValue, _database.Clock.UtcNow, "校验后续自动完成"));
        RecordAutomaticCompletion();

        var dashboard = new StatisticsRepository(_database.Database, settings).GetDashboard();

        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(int.MaxValue, dashboard.AchievementProgress);
        Assert.Equal(0, dashboard.Remaining);
        Assert.Equal(int.MaxValue, settings.GetAchievementSettings().BaselineCompletedCount);
    }

    [Fact]
    public void PreviouslySavedOversizedProgressRemainsReadable()
    {
        var settings = _database.Settings();
        CreateManual(_database.Mutations());
        _database.Database.RunInTransaction(tx => settings.UpdateAchievementSettings(
            settings.GetAchievementSettings(tx) with { BaselineCompletedCount = int.MaxValue }, tx));

        Assert.Equal(int.MaxValue, new StatisticsRepository(_database.Database, settings).GetDashboard().AchievementProgress);
    }

    [Fact]
    public void BaselineValidationUsesTheSameConfirmedAndContributingPopulationAsStatistics()
    {
        var settings = _database.Settings();
        var service = _database.Mutations();
        CreateManual(service, contributes: false);
        var deleted = CreateManual(service);
        service.SoftDeleteRun(new RunReasonCommand(NewId(), deleted.RunId, deleted.Revision, "排除已删除记录"));
        _database.InsertRuns(TestDatabase.Run(source: RunSource.Import) with { MentorRouletteId = null });

        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, int.MaxValue, _database.Clock.UtcNow, "排除不计入目标的记录"));

        Assert.Equal(int.MaxValue, new StatisticsRepository(_database.Database, settings).GetDashboard().AchievementProgress);
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>Settings as an earlier version left them: read without writing this version's defaults.</summary>
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

    private void CompleteAt(DateTimeOffset endedAt) =>
        _database.Mutations().CreateManualRun(TestDatabase.ManualRun(endedAt: endedAt));

    /// <summary>A counted manual completion entered now that lasted a minute.</summary>
    private RunMutationOutcome CreateManual(RunMutationService service, bool contributes = true) =>
        service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = NewId(), Reason = "回归记录", Result = RunResult.Completed, Region = Region.Cn,
            EnteredAtUtc = _database.Clock.UtcNow, EndedAtUtc = _database.Clock.UtcNow.AddSeconds(60),
            ContributesToGoal = contributes,
        });

    /// <summary>A completion the state machine records from a pop, an entry and a victory.</summary>
    private void RecordAutomaticCompletion()
    {
        var sessionId = _database.OpenCaptureSession(Region.Cn, null, ProfileStatus.Unverified);
        var machine = new MentorRunStateMachine(ProfileBinding.Synthetic("review", 42));
        var processor = new SemanticEventProcessor(_database.Database, machine,
            new SemanticEventProcessorOptions(sessionId, Region.Cn, "review", sessionId), _database.Clock);
        var start = _database.Clock.UtcNow;
        processor.Accept(new ContentFinderPop
        {
            Key = Key("pop", 0), ObservedAtUtc = start, Mono = TimeSpan.Zero, RouletteId = 42,
        });
        processor.Accept(new ZoneInitialization
        {
            Key = Key("entry", 1), ObservedAtUtc = start.AddSeconds(1),
            Mono = TimeSpan.FromSeconds(1), IsDutyInstance = true,
        });
        processor.Accept(new DutyResult
        {
            Key = Key("victory", 2), ObservedAtUtc = start.AddSeconds(2),
            Mono = TimeSpan.FromSeconds(2), Victory = true,
        });
        Assert.Null(processor.LastStorageError);
        Assert.Equal(RunState.Completed, machine.State);
    }

    private static EventKey Key(string kind, long epoch) =>
        new("review", PacketDirection.ServerToClient, kind, epoch, null, kind + epoch);

    private CollectorHost Restart()
    {
        _database.Database.Dispose();
        return CollectorHost.Open(_database.Path, _database.Clock, new CaptureServices());
    }
}
