using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Storage.Mutations;

namespace MentorRecorder.Collector.Recovery;

/// <summary>What the startup recovery pass did.</summary>
/// <param name="RecoveredRunIds">Runs processed at restart, preserving their existing human decisions.</param>
/// <param name="ClosedSessionCount">Capture sessions that were still open and got closed.</param>
public sealed record CrashRecoveryReport(
    IReadOnlyList<string> RecoveredRunIds,
    int ClosedSessionCount)
{
    /// <summary>An empty report: nothing needed recovering.</summary>
    public static CrashRecoveryReport Empty { get; } = new(Array.Empty<string>(), 0);

    /// <summary>Number of open runs this pass processed.</summary>
    public int RecoveredCount => RecoveredRunIds.Count;
}

/// <summary>
/// Closes what a previous process left open (docs/state-machine.md section 3.9).
///
/// Any run that is still open (<c>ended_at_utc IS NULL</c>) under a capture session that is
/// not this process's own is closed as <c>INTERRUPTED</c> with <c>LOW</c> confidence and
/// <c>pending_review = 1</c> unless the human revision trail protects those fields, and a
/// <c>PROCESS_RESTART</c> event is appended to its trail. Its stable identity also prevents
/// repeated recovery when a protected human end time remains null.
/// A run that never entered a duty is closed as <c>CANCELLED_BEFORE_ENTRY</c> instead, at the
/// same <c>LOW</c> and pending review: it was a match nobody saw end, and a non-cancelled result
/// with no entry time is a row the correction rules refuse to let the player confirm or annotate
/// (audit 2026-10-03, ODp-3). Rows earlier versions already stored in that shape are put right
/// once, through a system revision like every other automatic change; so are automatic runs an
/// earlier version left looking as if still in flight after a restart had closed them, which go
/// back on the review list (V3-2). The same pass checks, once, the achievement baseline's
/// effective time those versions moved on goal-only saves (<see cref="BaselineEffectiveTimeRepair"/>, V3-1).
/// A run that already has an end time was finished by the state machine -- including the
/// <c>UNKNOWN</c> + pending review a profile without <c>DUTY_RESULT</c> produces for every
/// duty (section 3.10) -- and is left exactly as it was.
///
/// The one rule that matters here is what this code does <em>not</em> do: it never infers a
/// completion. A run that was in flight when the process died is unfinished; only a human,
/// through <c>CorrectRun</c> with a reason, can turn it into <c>COMPLETED</c>.
/// The <c>PROCESS_RESTART</c> event carries a stable <c>event_key</c>, so restarting twice
/// over the same run cannot append the marker twice.
/// </summary>
public static class CrashRecoveryService
{
    /// <summary>Event type written to the run trail.</summary>
    public const string EventType = "PROCESS_RESTART";

    /// <summary>Runs the recovery pass and publishes one live event per recovered run.</summary>
    /// <param name="host">Host whose database and event bus to use.</param>
    public static CrashRecoveryReport Run(CollectorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var now = UtcTimestamp.Truncate(host.Clock.UtcNow);
        var recovered = new List<MentorRun>();
        var repairedEarlier = new List<MentorRun>();
        var flaggedEarlier = new List<MentorRun>();
        var baselineRepaired = false;
        var protection = new ManualRunFieldProtection(host.Database);

        var closedSessions = host.Database.RunInTransaction(tx =>
        {
            foreach (var run in host.Runs.FindOpenRunsFromOtherSessions(host.CaptureSessionId, tx))
            {
                if (host.Events.ExistsByKey(RestartKey(run), tx))
                {
                    continue;
                }

                var repaired = protection.Merge(run, run with
                {
                    Result = run.EnteredAtUtc is null ? RunResult.CancelledBeforeEntry : RunResult.Interrupted,
                    DetectionConfidence = DetectionConfidence.Low,
                    PendingReview = true,
                    EndedAtUtc = run.EndedAtUtc ?? now,
                    UpdatedAtUtc = now,
                }, tx) with { Revision = run.Revision + 1 };

                host.Runs.Update(repaired, run.Revision, tx);
                host.Revisions.Append(
                    new RunRevision
                    {
                        RevisionId = Guid.NewGuid().ToString("D"),
                        RunId = repaired.RunId,
                        Revision = repaired.Revision,
                        ChangedAtUtc = now,
                        ChangeKind = ChangeKind.Correct,
                        Actor = RevisionActor.System,

                        // A system revision still carries a reason: the audit timeline in the
                        // UI must be able to say why a row changed without the user guessing.
                        Reason = (repaired.Result, repaired.PendingReview) switch
                        {
                            (RunResult.Interrupted, true) => "程序重启时发现未完结记录，已记为中断并标记待复核。",
                            (RunResult.CancelledBeforeEntry, true) =>
                                "程序重启时发现未完结记录，它尚未进入副本，已记为进本前取消并标记待复核。",
                            _ => "程序重启时发现未完结记录，已保留人工决定并处理未保护的结束字段。",
                        },
                        RequestId = null,
                        Changes = Changes(run, repaired),
                    },
                    tx);

                AppendRestartEvent(host, repaired, now, tx);
                recovered.Add(repaired);
            }

            repairedEarlier.AddRange(RepairNeverEnteredRuns(host, protection, now, tx));
            flaggedEarlier.AddRange(FlagRunsLeftInFlight(host, now, tx));
            baselineRepaired = BaselineEffectiveTimeRepair.Run(host.Settings, now, tx);
            return host.Sessions.CloseAllOpen(host.CaptureSessionId, now, CaptureEndReason.Unknown, tx);
        });

        foreach (var run in recovered.Concat(repairedEarlier).Concat(flaggedEarlier))
        {
            host.LiveEvents.PublishRun(LiveEventKind.RunUpdated, run);
        }

        if (recovered.Count > 0)
        {
            var pendingCount = recovered.Count(run => run.PendingReview);
            host.LiveEvents.PublishStatsInvalidated(
                $"启动时恢复了 {recovered.Count} 条未完结记录，其中 {pendingCount} 条需要复核。");
        }

        if (repairedEarlier.Count > 0)
        {
            host.LiveEvents.PublishStatsInvalidated(
                $"启动时更正了 {repairedEarlier.Count} 条尚未进入副本的记录，已改记为进本前取消并标记待复核。");
        }

        if (flaggedEarlier.Count > 0)
        {
            host.LiveEvents.PublishStatsInvalidated(
                $"启动时发现 {flaggedEarlier.Count} 条结果未知、没有结束时间的早期记录，已标记为待复核。");
        }

        if (baselineRepaired)
        {
            host.LiveEvents.PublishStatsInvalidated("启动时按基数的修改记录更正了成就基数的生效时间。");
        }

        return new CrashRecoveryReport(recovered.Select(run => run.RunId).ToArray(), closedSessions);
    }

    /// <summary>
    /// Puts right the closed automatic runs that never entered a duty yet carry a result other than
    /// <c>CANCELLED_BEFORE_ENTRY</c>: what crash recovery and a profile lost mid-match wrote before
    /// audit 2026-10-03 (ODp-3, OG-5). The correction rules refuse every edit of such a row, a note or
    /// a plain confirmation included, so the player could not resolve it from the review list it was
    /// put on. Each one gets a system revision, and fields a human corrected are kept; a row that
    /// field protection leaves unchanged is skipped. Once repaired a row no longer matches, so this
    /// runs once per row.
    /// </summary>
    /// <param name="host">Host whose database to use.</param>
    /// <param name="protection">Manual field protection, shared with the recovery pass.</param>
    /// <param name="now">Discovery time.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    private static List<MentorRun> RepairNeverEnteredRuns(
        CollectorHost host,
        ManualRunFieldProtection protection,
        DateTimeOffset now,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var runIds = new List<string>();
        using (var command = host.Database.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT run_id FROM mentor_runs " +
                "WHERE soft_deleted = 0 AND capture_session_id IS NOT NULL " +
                "AND entered_at_utc IS NULL AND ended_at_utc IS NOT NULL " +
                "AND result IN ($interrupted, $unknown) ORDER BY created_at_utc ASC;";
            command.Parameters.AddWithValue("$interrupted", EnumWire<RunResult>.Format(RunResult.Interrupted));
            command.Parameters.AddWithValue("$unknown", EnumWire<RunResult>.Format(RunResult.Unknown));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runIds.Add(reader.GetString(0));
            }
        }

        var repairedRuns = new List<MentorRun>();
        foreach (var runId in runIds)
        {
            if (host.Runs.GetInternal(runId, transaction) is not { } run)
            {
                continue;
            }

            var repaired = protection.Merge(run, run with
            {
                Result = RunResult.CancelledBeforeEntry,
                DetectionConfidence = DetectionConfidence.Low,
                PendingReview = true,
                UpdatedAtUtc = now,
            }, transaction) with { Revision = run.Revision + 1 };
            if (repaired.Result != RunResult.CancelledBeforeEntry)
            {
                continue;
            }

            host.Runs.Update(repaired, run.Revision, transaction);
            host.Revisions.Append(
                new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"),
                    RunId = repaired.RunId,
                    Revision = repaired.Revision,
                    ChangedAtUtc = now,
                    ChangeKind = ChangeKind.Correct,
                    Actor = RevisionActor.System,
                    Reason = "这条记录尚未进入副本，早期版本却记为中断或结局未知，因而无法确认或补充说明；" +
                        "现改记为进本前取消并标记待复核。",
                    RequestId = null,
                    Changes = Changes(run, repaired),
                },
                transaction);
            repairedRuns.Add(repaired);
        }

        return repairedRuns;
    }

    /// <summary>
    /// Puts back on the review list the automatic runs an earlier version left looking as if still in flight
    /// after a restart had already closed them: UNKNOWN, no end time, not pending review, with a restart marker
    /// (audit 2026-10-03, V3-2). 1.5.0 let the player undo the recovery revision, or correct the run, into that
    /// shape. Every statistic and the review list leave such a run out, and the recovery pass skips it because
    /// its marker exists, so nothing would ever bring it back. Only the review flag is set, through a system
    /// revision; the result, the times and every other field stay as they are, for the player to settle. The
    /// flag is set even where the player's own revision cleared it - the field protection would otherwise keep
    /// every such row exactly as lost as it is - and a deleted row is left alone. Once flagged a run no longer
    /// matches, so this runs once per run.
    /// </summary>
    /// <param name="host">Host whose database to use.</param>
    /// <param name="now">Discovery time.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    private static List<MentorRun> FlagRunsLeftInFlight(
        CollectorHost host,
        DateTimeOffset now,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var flagged = new List<MentorRun>();
        foreach (var run in host.Runs.FindOpenRunsFromOtherSessions(host.CaptureSessionId, transaction))
        {
            if (!RunMutationRules.ReadsAsInFlight(run) || !host.Events.ExistsByKey(RestartKey(run), transaction))
            {
                continue;
            }

            var repaired = run with
            {
                PendingReview = true,
                UpdatedAtUtc = now,
                Revision = run.Revision + 1,
            };
            host.Runs.Update(repaired, run.Revision, transaction);
            host.Revisions.Append(
                new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"),
                    RunId = repaired.RunId,
                    Revision = repaired.Revision,
                    ChangedAtUtc = now,
                    ChangeKind = ChangeKind.Correct,
                    Actor = RevisionActor.System,
                    Reason = "这条记录结果未知、没有结束时间，早期版本因此把它当作仍在进行：既不计入统计，" +
                        "也不出现在待复核中，以后也不会再被收尾。现标记为待复核，其他内容保持不变，请补上实际结果或结束时间。",
                    RequestId = null,
                    Changes = Changes(run, repaired),
                },
                transaction);
            flagged.Add(repaired);
        }

        return flagged;
    }

    private static void AppendRestartEvent(
        CollectorHost host,
        MentorRun run,
        DateTimeOffset now,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        host.Events.Append(
            new RunEvent
            {
                EventId = Guid.NewGuid().ToString("D"),
                RunId = run.RunId,
                Sequence = host.Events.NextSequence(run.RunId, transaction),
                OccurredAtUtc = now,
                MonotonicOffsetMs = 0,
                EventType = EventType,
                FromState = null,
                // INTERRUPTED_PENDING_REVIEW stores as INTERRUPTED; a run that never entered was cancelled.
                ToState = run.PendingReview && run.Result != RunResult.CancelledBeforeEntry
                    ? RunState.InterruptedPendingReview
                    : TerminalState(run.Result),
                Confidence = run.DetectionConfidence,
                EventKey = RestartKey(run),
                DetailJson = null,
            },
            transaction);
    }

    private static string RestartKey(MentorRun run) => new EventKey(
        run.CaptureSessionId ?? run.RunId, PacketDirection.None, EventType, 0, null,
        "restart:" + run.RunId).ToCanonicalString();

    private static RunState TerminalState(RunResult result) => result switch
    {
        RunResult.Completed => RunState.Completed,
        RunResult.LeftOrAbandoned => RunState.LeftOrAbandoned,
        RunResult.CancelledBeforeEntry => RunState.CancelledBeforeEntry,
        RunResult.Disconnected => RunState.Disconnected,
        RunResult.Interrupted => RunState.Interrupted,
        _ => RunState.UnknownFinalState,
    };

    private static IReadOnlyList<RunFieldChange> Changes(MentorRun before, MentorRun after)
    {
        var changes = RunMutationRules.Diff(before, after).ToList();
        if (before.DetectionConfidence != after.DetectionConfidence)
        {
            changes.Add(new RunFieldChange("detection_confidence",
                EnumWire<DetectionConfidence>.Format(before.DetectionConfidence),
                EnumWire<DetectionConfidence>.Format(after.DetectionConfidence)));
        }

        return changes;
    }
}
