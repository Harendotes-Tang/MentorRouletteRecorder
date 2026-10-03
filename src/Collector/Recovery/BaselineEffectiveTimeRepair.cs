using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Recovery;

/// <summary>
/// Puts back, once, the effective time of the achievement baseline that 1.5.0 and earlier moved on saves that did
/// not change the baseline (audit 2026-10-03, V3-1).
///
/// Those versions stored the effective time every save was sent with, and their Desktop always sent the save time,
/// so changing only the goal moved it to that day. It decided nothing then. Now completions that ended before it
/// are taken to be inside the baseline already (docs/statistics-definitions.md section 4), and every completion
/// recorded between the baseline's real entry and the last such save would drop out of the progress for good.
///
/// The baseline's change history tells when the number was entered: the oldest of the newest entries that all name
/// the stored number - what this version's rule, that only a new baseline moves the time, would have stored for the
/// same saves. Nothing changes where the history cannot tell: there is none, its newest entry is not the stored row,
/// the run of entries reaches the start of a history cut at its cap, or the time found is not earlier than the one
/// stored. The change, when there is one, is appended to that history - the baseline's audit trail - as the
/// system's, with its reason, and the check is recorded as made so that nothing saved under the new rule is ever
/// second-guessed.
/// </summary>
internal static class BaselineEffectiveTimeRepair
{
    /// <summary>Setting whose presence records that the check was made.</summary>
    internal const string CheckedSetting = "achievement.baseline_effective_at_checked";

    /// <summary>Request id of the history entry the repair appends: no client request caused it.</summary>
    internal const string RequestId = "system:baseline-effective-at-repair";

    /// <summary>Reason kept with that entry.</summary>
    internal const string Reason =
        "早期版本在只修改目标或原样保存同一基数时也会改动基数的生效时间，使此前已记录的完成不再计入进度；" +
        "现按基数的修改记录改回首次填入该基数的时间。";

    /// <summary>Makes the check, unless it was made before.</summary>
    /// <param name="settings">Settings repository.</param>
    /// <param name="now">Discovery time.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>True when the stored effective time was moved.</returns>
    internal static bool Run(SettingsRepository settings, DateTimeOffset now, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transaction);
        if (settings.GetSetting(CheckedSetting, transaction) is not null)
        {
            return false;
        }

        var stored = settings.GetAchievementSettings(transaction);
        var entered = EnteredAt(stored, settings.ReadBaselineAudit(transaction));
        if (entered is { } effectiveAt)
        {
            var repaired = new AchievementSettings
            {
                GoalCount = stored.GoalCount,
                BaselineCompletedCount = stored.BaselineCompletedCount,
                BaselineEffectiveAt = effectiveAt,
                UpdatedAtUtc = now,
            };
            settings.UpdateAchievementSettings(repaired, transaction);
            settings.AppendBaselineAudit(Guid.NewGuid().ToString("D"), repaired, Reason, RequestId, transaction);
        }

        settings.SetSetting(CheckedSetting, "true", transaction);
        return entered is not null;
    }

    /// <summary>
    /// When the stored baseline was entered, by its history; null when the history cannot tell or the answer would
    /// change nothing.
    /// </summary>
    /// <param name="stored">The achievement row as stored.</param>
    /// <param name="history">Its change history, oldest first.</param>
    private static DateTimeOffset? EnteredAt(AchievementSettings stored, IReadOnlyList<BaselineAuditEntry> history)
    {
        var count = stored.BaselineCompletedCount;
        if (count <= 0 || history.Count == 0 || history[^1].BaselineCompletedCount != count ||
            !UtcTimestamp.TryParse(history[^1].BaselineEffectiveAt, out var newest) ||
            newest != stored.BaselineEffectiveAt)
        {
            return null;
        }

        var first = history.Count - 1;
        while (first > 0 && history[first - 1].BaselineCompletedCount == count)
        {
            first--;
        }

        // A history cut at its cap may have lost older entries that named this number as well.
        if (first == 0 && history.Count >= SettingsRepository.MaxBaselineAuditEntries)
        {
            return null;
        }

        return UtcTimestamp.TryParse(history[first].BaselineEffectiveAt, out var entered) &&
            entered < stored.BaselineEffectiveAt
                ? entered
                : null;
    }
}
