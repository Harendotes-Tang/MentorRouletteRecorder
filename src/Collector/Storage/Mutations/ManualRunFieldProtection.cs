using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// 在自动写入的同一事务内读取人工修订，仅保留人实际更正过的字段。
/// 不建立第二份持久化标记，也不把系统恢复、创建记录或来源标记当成人工决定。
/// </summary>
internal sealed class ManualRunFieldProtection(SqliteDatabase database)
{
    /// <summary>
    /// 合并当前人工值与自动建议。调用者持有数据库事务；返回新行，不自行写库或追加修订。
    /// 人工标识所关联的显示字段一起保留，其他字段可继续由抓包补齐。
    /// </summary>
    /// <param name="current">事务中读取的当前行。</param>
    /// <param name="proposed">该次自动观察建议写入的行。</param>
    /// <param name="transaction">串行保护人工修订读取与自动写入的事务。</param>
    public MentorRun Merge(MentorRun current, MentorRun proposed, SqliteTransaction transaction)
    {
        // 以追加式修订为准：历史标记可能被撤销回退为 false，不能据此跳过重放。
        var fields = ReadFields(current.RunId, transaction);
        foreach (var change in RunMutationRules.Diff(proposed, current))
        {
            if (fields.Contains(change.Field))
            {
                proposed = RunFieldWriter.Apply(proposed, change.Field, change.NewValue);
            }
        }

        if (fields.Contains(RunFields.JobId))
        {
            proposed = proposed with { JobId = current.JobId, JobName = current.JobName, Role = current.Role };
        }

        if (fields.Contains(RunFields.ContentId))
        {
            proposed = proposed with
            {
                ContentId = current.ContentId,
                TerritoryId = current.TerritoryId,
                DutyName = current.DutyName,
                DutyCategory = current.DutyCategory,
            };
        }

        // 明确结果或待复核确认才表示结果决定；改备注不授予此保护。
        if (fields.Contains(RunFields.Result) || fields.Contains(RunFields.PendingReview))
        {
            proposed = proposed with
            {
                Result = current.Result,
                DetectionConfidence = current.DetectionConfidence,
                PendingReview = current.PendingReview,
            };
        }

        proposed = ReconcileTimeline(current, proposed, fields);
        return current.SoftDeleted ? proposed with { PendingReview = false } : proposed;
    }

    /// <summary>
    /// 让最终的时间组合自洽，按记录中是否存在人工决定分成两条路径。
    ///
    /// 有受保护的人工字段时，保留已存的合法时间组合，不把本次观察伪造成合法时间。纯自动写入
    /// 没有需要保护的决定，改为把越界端点贴到相邻端点上：整组时间回退会在本机时钟慢于服务器
    /// 时把一次正常收尾改写成 <c>ended_at_utc</c> 为 NULL 的未完结记录（评审 H-8）。
    /// </summary>
    /// <param name="current">事务中读取的当前行。</param>
    /// <param name="proposed">已按人工保护合并过的候选行。</param>
    /// <param name="fields">人实际决定过、且尚未撤回的字段集合。</param>
    private static MentorRun ReconcileTimeline(
        MentorRun current, MentorRun proposed, HashSet<string> fields)
    {
        if (fields.Count == 0)
        {
            return IsTimeInconsistent(proposed) ? ClampTimeline(proposed) : proposed;
        }

        if (!IsTimeInconsistent(proposed))
        {
            // 自动单调时长对应的是原始端点；人工改过端点时按最终时间组合推导。
            return !fields.Contains(RunFields.DurationMs)
                && (fields.Contains(RunFields.EnteredAtUtc) || fields.Contains(RunFields.EndedAtUtc))
                ? proposed with { DurationMs = RunMutationRules.DeriveDuration(proposed) }
                : proposed;
        }

        var restored = proposed with
        {
            MatchedAtUtc = current.MatchedAtUtc,
            EnteredAtUtc = current.EnteredAtUtc,
            EndedAtUtc = current.EndedAtUtc,
            DurationMs = current.DurationMs,
        };

        return fields.Contains(RunFields.Result) || fields.Contains(RunFields.PendingReview)
            ? restored
            : restored with
            {
                Result = RunResult.Unknown,
                DetectionConfidence = DetectionConfidence.Low,
                PendingReview = true,
            };
    }

    /// <summary>
    /// 把纯自动写入的墙钟端点收拢成数据库允许的顺序，绝不因此丢掉收尾。
    ///
    /// 时长来自单调时钟，不受本机时钟快慢影响，矛盾的只是墙钟标注本身。贴端点而非整组回退：
    /// 回退会在时钟慢于服务器时把一次正常收尾改写成 <c>ended_at_utc</c> 为 NULL 的未完结记录，
    /// 此后再无人收尾（评审 H-8）。
    /// </summary>
    /// <param name="run">本次自动观察建议写入的行。</param>
    private static MentorRun ClampTimeline(MentorRun run)
    {
        var matched = run.MatchedAtUtc;
        var ended = run.EndedAtUtc;

        if (matched is { } startedAt && run.EnteredAtUtc is { } entered && startedAt > entered)
        {
            matched = entered;
        }

        if (run.EnteredAtUtc is { } enteredAt && ended is { } endedAt && enteredAt > endedAt)
        {
            ended = enteredAt;
        }

        var clamped = run with { MatchedAtUtc = matched, EndedAtUtc = ended };

        // 端点顺序之外的另一条矛盾：结果为 COMPLETED，进入或结束时刻却为空。贴端点解决不了它
        // （评审 R-13）。缺端点时不能声称已完成：降级为未知并交给用户复核，与人工路径一致。
        return clamped.Result == RunResult.Completed
            && (clamped.EnteredAtUtc is null || clamped.EndedAtUtc is null)
            ? clamped with
            {
                Result = RunResult.Unknown,
                DetectionConfidence = DetectionConfidence.Low,
                PendingReview = true,
            }
            : clamped;
    }

    /// <summary>时间端点或“已完成缺少端点”是否与既有人工决定矛盾。</summary>
    /// <param name="run">本次合并后的候选行。</param>
    private static bool IsTimeInconsistent(MentorRun run) =>
        (run.MatchedAtUtc is { } matched && run.EnteredAtUtc is { } entered && matched > entered)
        || (run.EnteredAtUtc is { } enter && run.EndedAtUtc is { } ended && enter > ended)
        || (run.Result == RunResult.Completed
            && (run.EnteredAtUtc is null || run.EndedAtUtc is null));

    /// <summary>
    /// 人实际决定过、且尚未撤回的字段集合。
    ///
    /// 按修订顺序重放人工更正：字段被改成别的值即受保护，被改回“此人动手之前的值”（撤销的
    /// 典型形态）则解除保护，否则撤销会永久冻结该字段的自动补齐（评审 L-19）。
    ///
    /// 参照物是该字段第一条 USER/CORRECT 修订的 OldValue，而非创建修订的初值：字段在被人改动
    /// 之前往往已由抓包补过一次，用创建初值作参照会使撤销后字段仍被判为受保护（评审 R-8）。
    /// </summary>
    /// <param name="runId">记录标识。</param>
    /// <param name="transaction">串行保护读取与写入的事务。</param>
    private HashSet<string> ReadFields(string runId, SqliteTransaction transaction)
    {
        var baseline = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changes in ReadChangeSets(
            runId, "actor = 'USER' AND change_kind = 'CORRECT'", transaction))
        {
            foreach (var change in changes)
            {
                if (!baseline.ContainsKey(change.Field))
                {
                    baseline[change.Field] = change.OldValue;
                }

                if (Equals(baseline[change.Field], change.NewValue))
                {
                    fields.Remove(change.Field);
                }
                else
                {
                    fields.Add(change.Field);
                }
            }
        }

        return fields;
    }

    private List<IReadOnlyList<RunFieldChange>> ReadChangeSets(
        string runId, string predicate, SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT changes_json FROM run_revisions " +
            "WHERE run_id = $run_id AND " + predicate + " ORDER BY revision;";
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        var sets = new List<IReadOnlyList<RunFieldChange>>();
        while (reader.Read())
        {
            sets.Add(RunRevisionRepository.DeserializeChanges(reader.GetString(0)));
        }

        return sets;
    }
}
