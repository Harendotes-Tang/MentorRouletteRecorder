using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Domain.Mutations;

/// <summary>
/// The validation rules of docs/manual-correction.md section 6, in one place.
///
/// Every rule is checked against the <em>final</em> value of the run, not against the fields
/// that happen to appear in a request. Correcting only the end time can therefore still
/// fail the time-order rule because of a stored entry time, which is exactly the intent:
/// the stored run must be valid after the change, not merely the patch.
/// </summary>
public static class RunMutationRules
{
    /// <summary>Rejects a missing or oversized reason.</summary>
    /// <param name="reason">Reason supplied by the client.</param>
    public static string RequireReason(string? reason)
    {
        if (!MutationText.IsUsableReason(reason))
        {
            throw new RunRuleViolationException(
                RunRuleViolationKind.ReasonRequired,
                RunRuleProperty.Reason);
        }

        return reason!.Trim();
    }

    /// <summary>
    /// Checks the invariants a stored run must satisfy and returns the run with its duration
    /// normalised. Throws a typed domain failure for the first violated rule.
    /// </summary>
    /// <param name="run">Candidate final value of the run.</param>
    /// <param name="durationExplicit">
    /// True when the client gave <c>duration_ms</c> itself. An explicit duration wins over
    /// the derived one and is never silently adjusted.
    /// </param>
    /// <param name="recalculateDuration">
    /// False when a correction leaves both duration endpoints unchanged; the stored
    /// monotonic or explicitly supplied duration must then survive unrelated field edits.
    /// </param>
    public static MentorRun ValidateFinalValue(
        MentorRun run, bool durationExplicit, bool recalculateDuration = true)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.MatchedAtUtc is { } matched && run.EnteredAtUtc is { } entered && matched > entered)
        {
            throw Violation(
                RunRuleViolationKind.MatchedAfterEntered,
                RunRuleProperty.MatchedAtUtc,
                run);
        }

        if (run.EnteredAtUtc is { } enter && run.EndedAtUtc is { } ended && enter > ended)
        {
            throw Violation(
                RunRuleViolationKind.EnteredAfterEnded,
                RunRuleProperty.EnteredAtUtc,
                run);
        }

        if (run.Result != RunResult.CancelledBeforeEntry && run.EnteredAtUtc is null)
        {
            throw Violation(
                RunRuleViolationKind.EntryRequired,
                RunRuleProperty.EnteredAtUtc,
                run);
        }

        if (run.Result == RunResult.Completed && run.EndedAtUtc is null)
        {
            throw Violation(
                RunRuleViolationKind.EndRequired,
                RunRuleProperty.EndedAtUtc,
                run);
        }

        var duration = durationExplicit || !recalculateDuration ? run.DurationMs : DeriveDuration(run);
        if (duration is < 0)
        {
            throw Violation(
                RunRuleViolationKind.NegativeDuration,
                RunRuleProperty.DurationMs,
                run);
        }

        return run with { DurationMs = duration };
    }

    /// <summary>
    /// Duration derived from the two timestamps. This is the only place a duration is ever
    /// computed from wall-clock values, and it exists solely because a hand-entered run has
    /// no monotonic reading to subtract (docs/manual-correction.md section 6).
    /// </summary>
    /// <param name="run">Run whose times are already final.</param>
    public static long? DeriveDuration(MentorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.EnteredAtUtc is not { } entered || run.EndedAtUtc is not { } ended)
        {
            return null;
        }

        return (long)(ended - entered).TotalMilliseconds;
    }

    /// <summary>
    /// Field-level difference between two versions of a run, in a stable column order.
    /// An empty list means the correction would change nothing.
    /// </summary>
    /// <param name="before">Stored run.</param>
    /// <param name="after">Candidate run.</param>
    public static IReadOnlyList<RunFieldChange> Diff(MentorRun before, MentorRun after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<RunFieldChange>(12);
        Compare(changes, RunFields.ContentId, before.ContentId, after.ContentId);
        Compare(changes, RunAuditFields.DutyIdentity,
            DutyIdentityAudit.Capture(before), DutyIdentityAudit.Capture(after));
        Compare(changes, RunFields.DutyName, before.DutyName, after.DutyName);
        Compare(changes, RunFields.DutyCategory, before.DutyCategory, after.DutyCategory);
        Compare(changes, RunFields.JobId, before.JobId, after.JobId);
        Compare(changes, RunFields.JobName, before.JobName, after.JobName);
        Compare(
            changes,
            RunFields.Role,
            EnumWire<Role>.Format(before.Role),
            EnumWire<Role>.Format(after.Role));
        Compare(
            changes,
            RunFields.MatchedAtUtc,
            UtcTimestamp.ToTextOrNull(before.MatchedAtUtc),
            UtcTimestamp.ToTextOrNull(after.MatchedAtUtc));
        Compare(
            changes,
            RunFields.EnteredAtUtc,
            UtcTimestamp.ToTextOrNull(before.EnteredAtUtc),
            UtcTimestamp.ToTextOrNull(after.EnteredAtUtc));
        Compare(
            changes,
            RunFields.EndedAtUtc,
            UtcTimestamp.ToTextOrNull(before.EndedAtUtc),
            UtcTimestamp.ToTextOrNull(after.EndedAtUtc));
        Compare(changes, RunFields.DurationMs, before.DurationMs, after.DurationMs);
        Compare(
            changes,
            RunFields.Result,
            EnumWire<RunResult>.Format(before.Result),
            EnumWire<RunResult>.Format(after.Result));
        Compare(changes, RunFields.ContributesToGoal, before.ContributesToGoal, after.ContributesToGoal);
        Compare(changes, RunFields.Note, before.Note, after.Note);
        Compare(changes, RunFields.SoftDeleted, before.SoftDeleted, after.SoftDeleted);
        Compare(changes, RunFields.PendingReview, before.PendingReview, after.PendingReview);
        Compare(changes, RunFields.ManuallyCorrected, before.ManuallyCorrected, after.ManuallyCorrected);
        return changes;
    }

    /// <summary>
    /// True when a change set overrules something the software had recorded, as opposed to
    /// supplying what it could not know.
    ///
    /// A profile without DUTY_RESULT ends every run pending review, and the player says how it
    /// went; a build whose job message was not identified leaves the job blank, and the player
    /// fills it in. Neither corrects anything, and filing both as corrections tagged every
    /// automatic record 已修正. A change is NOT a correction when it is
    /// <list type="bullet">
    /// <item>the outcome (<c>result</c>, <c>pending_review</c>, <c>contributes_to_goal</c>) of a
    /// run that was pending review - the question was open, and this answers it;</item>
    /// <item>the duty or the job being filled in where the software had none: the old value was
    /// null, or the 未知 / UNKNOWN placeholder the job name and role carry meanwhile. Times are
    /// not on this list - an end time typed into an open run decides when the run ended, and the
    /// field protection that follows from it is a correction in every sense;</item>
    /// <item>the note, which was never the software's to begin with.</item>
    /// </list>
    /// Everything else - a recorded duty, job, time, or a settled outcome changed - is one.
    /// </summary>
    /// <param name="before">The run as stored.</param>
    /// <param name="changes">The diff the correction produces.</param>
    public static bool OverrulesTheRecord(MentorRun before, IReadOnlyList<RunFieldChange> changes)
    {
        ArgumentNullException.ThrowIfNull(before);
        return OverrulesTheRecord(before.PendingReview, changes);
    }

    /// <summary>The same question asked of a stored revision, where only the diff survives.</summary>
    /// <param name="wasPendingReview">Whether the run was pending review before the change.</param>
    /// <param name="changes">The diff the correction produced.</param>
    public static bool OverrulesTheRecord(bool wasPendingReview, IReadOnlyList<RunFieldChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.Any(change => change.Field switch
        {
            RunFields.ManuallyCorrected or RunFields.Note or RunAuditFields.DutyIdentity => false,
            RunFields.Result or RunFields.PendingReview or RunFields.ContributesToGoal => !wasPendingReview,
            RunFields.JobId or RunFields.JobName or RunFields.Role or RunFields.ContentId or
                RunFields.DutyName or RunFields.DutyCategory => !IsBlank(change.OldValue),
            _ => true,
        });
    }

    private static bool IsBlank(object? value) =>
        value is null || value is "未知" or "UNKNOWN" || (value is string text && text.Length == 0);

    /// <summary>Rejects a correction that would change nothing.</summary>
    /// <param name="changes">Difference produced by <see cref="Diff"/>.</param>
    public static IReadOnlyList<RunFieldChange> RequireChanges(IReadOnlyList<RunFieldChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            throw new RunRuleViolationException(
                RunRuleViolationKind.NoChanges,
                RunRuleProperty.Changes);
        }

        return changes;
    }

    private static void Compare(List<RunFieldChange> changes, string field, object? before, object? after)
    {
        if (!Equals(before, after))
        {
            changes.Add(new RunFieldChange(field, before, after));
        }
    }

    private static RunRuleViolationException Violation(
        RunRuleViolationKind kind,
        RunRuleProperty property,
        MentorRun run) =>
        new(kind, property, run.RunId, run.Result);
}
