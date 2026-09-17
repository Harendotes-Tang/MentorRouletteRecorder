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
                    Result = RunResult.Interrupted,
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
                        Reason = repaired.Result == RunResult.Interrupted && repaired.PendingReview
                            ? "程序重启时发现未完结记录，已置为 INTERRUPTED 并标记待复核。"
                            : "程序重启时发现未完结记录，已保留人工决定并处理未保护的结束字段。",
                        RequestId = null,
                        Changes = Changes(run, repaired),
                    },
                    tx);

                AppendRestartEvent(host, repaired, now, tx);
                recovered.Add(repaired);
            }

            return host.Sessions.CloseAllOpen(host.CaptureSessionId, now, CaptureEndReason.Unknown, tx);
        });

        foreach (var run in recovered)
        {
            host.LiveEvents.PublishRun(LiveEventKind.RunUpdated, run);
        }

        if (recovered.Count > 0)
        {
            var pendingCount = recovered.Count(run => run.PendingReview);
            host.LiveEvents.PublishStatsInvalidated(
                $"启动时恢复了 {recovered.Count} 条未完结记录，其中 {pendingCount} 条需要复核。");
        }

        return new CrashRecoveryReport(recovered.Select(run => run.RunId).ToArray(), closedSessions);
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
                ToState = run.PendingReview ? RunState.InterruptedPendingReview : TerminalState(run.Result),
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
