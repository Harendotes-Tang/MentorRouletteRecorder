using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>What a <see cref="DutyClearNote"/> reports.</summary>
public enum DutyClearNoteKind
{
    /// <summary>The CN clear signal reached the state machine.</summary>
    Signal,

    /// <summary>
    /// The zone change that followed a clear which completed a run, on the same parser and with no
    /// loss in between: the player left the duty.
    /// </summary>
    Exit,
}

/// <summary>
/// One observation of the CN clear signal, for the diagnostic log. It names a run and counts
/// things; it never carries the payload, its hash, the opcode or the duty's director number
/// (duty-result brief, decision 7).
/// </summary>
/// <param name="Kind">What is reported.</param>
/// <param name="RunId">The run in flight when the signal arrived, or the run it completed; null for none.</param>
/// <param name="StateBefore">
/// State the signal met: IDLE, MENTOR_MATCHED or ENTERED_DUTY. A finished run's terminal state
/// reads IDLE, because that is how the machine meets it.
/// </param>
/// <param name="Completed">True when this signal completed the run (always true for an exit).</param>
/// <param name="ClearToExitMs">For an exit: milliseconds from the clear to the zone change.</param>
/// <param name="Signals">Signals seen in this capture session, this one included.</param>
/// <param name="Completions">Of those, the ones that completed a run.</param>
public sealed record DutyClearNote(
    DutyClearNoteKind Kind,
    string? RunId,
    RunState StateBefore,
    bool Completed,
    long? ClearToExitMs,
    long Signals,
    long Completions)
{
    /// <summary>Event name of the log line, under component <c>protocol</c>.</summary>
    public string LogEvent => Kind == DutyClearNoteKind.Exit ? "duty_clear_exit" : "duty_clear_signal";

    /// <summary>
    /// The fields of the log line, and nothing else: for a signal the run, the state it met,
    /// whether it completed the run and the two counts; for an exit the run and the gap.
    /// </summary>
    public IReadOnlyDictionary<string, object?> LogFields() => Kind == DutyClearNoteKind.Exit
        ? new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_id"] = RunId,
            ["clear_to_exit_ms"] = ClearToExitMs,
        }
        : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_id"] = RunId,
            ["state_before"] = EnumWire<RunState>.Format(StateBefore),
            ["completed"] = Completed,
            ["signals"] = Signals,
            ["completions"] = Completions,
        };
}

/// <summary>
/// The CN clear counts of one capture session. Every tally bound in the session adds to the same
/// counts, so a rebind, a swap or a withdrawal within it keeps them; the next session starts anew.
/// </summary>
public sealed class DutyClearCounts
{
    /// <summary>Clear signals that reached a state machine this session, each observation once.</summary>
    public long Signals { get; private set; }

    /// <summary>Of those, the ones that completed a mentor run.</summary>
    public long Completions { get; private set; }

    /// <summary>Counts one signal, and one completion when it completed a run.</summary>
    /// <param name="completed">True when the signal completed a run.</param>
    internal void Add(bool completed)
    {
        Signals++;
        if (completed)
        {
            Completions++;
        }
    }
}

/// <summary>
/// Counts and reports the CN clear signal on its way to the state machine of a profile that
/// observes it (<see cref="DutyClearSignal"/>, docs/protocol-profile-format.md section 12).
///
/// It decides nothing: every event reaches <paramref name="inner"/> unchanged and in order, and
/// the machine alone makes a clear a COMPLETED run. Around a clear it reads the machine before and
/// after: the signal counts once per observation (a copy the machine discards as a duplicate does
/// not), and it counts as a completion only when the machine went from ENTERED_DUTY to COMPLETED. A
/// clear the store refused leaves the machine where it was - the processor puts it back - so a run
/// that was never committed is never counted. After a completion the next zone change this tally
/// sees is the exit, and the time from the clear to it is reported once - unless a loss came in
/// between (<see cref="ForgetExit"/>), or the parser was replaced first, which takes this tally
/// and the exit it was waiting for with it.
///
/// The counts are what lets a pasted report tell "the signal never came" (another duty type, or a
/// protocol change) from "it came and no mentor run was in the duty"
/// (docs/capture-diagnostics.md sections 5.2 and 8). They are the capture session's
/// (<see cref="DutyClearCounts"/>), shared by every tally bound in it.
/// </summary>
/// <param name="inner">The sink the parser would otherwise feed: the state machine's processor, or a watch in front of it.</param>
/// <param name="machine">The state machine behind <paramref name="inner"/>.</param>
/// <param name="noted">Receives every note; called on the parser's thread, with the pipeline's lock held.</param>
/// <param name="counts">The capture session's counts to add to; counts of this tally alone when null.</param>
public sealed class DutyClearTally(
    ISemanticEventSink inner,
    MentorRunStateMachine machine,
    Action<DutyClearNote>? noted,
    DutyClearCounts? counts = null) : ISemanticEventSink
{
    private readonly ISemanticEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MentorRunStateMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    private readonly DutyClearCounts _counts = counts ?? new DutyClearCounts();

    /// <summary>The run the last clear completed, and when, until the zone change that follows it.</summary>
    private (string RunId, TimeSpan Mono)? _completed;

    /// <summary>Clear signals that reached a machine this capture session, each observation once.</summary>
    public long Signals => _counts.Signals;

    /// <summary>Of those, the ones that completed a mentor run.</summary>
    public long Completions => _counts.Completions;

    /// <summary>
    /// Forgets the exit the last completion is waiting for. Called when the machine is told of a loss -
    /// a gap, the game connection closing - that the clear's duty may have ended in: the next zone change
    /// is then no longer known to follow the clear (it may be the re-login, or the next duty's entry, much
    /// later), so no clear-to-exit time is reported for it.
    /// </summary>
    public void ForgetExit() => _completed = null;

    /// <inheritdoc />
    public void Accept(SemanticEvent semanticEvent)
    {
        if (semanticEvent is DutyResult result &&
            string.Equals(result.Key.SemanticKey, DutyClearSignal.SemanticKey, StringComparison.Ordinal))
        {
            AcceptClear(result);
            return;
        }

        _inner.Accept(semanticEvent);
        if (semanticEvent is ZoneInitialization zone && _completed is { } completed)
        {
            _completed = null;
            var gap = zone.Mono - completed.Mono;
            Note(new DutyClearNote(
                DutyClearNoteKind.Exit, completed.RunId, Met(_machine.State), Completed: true,
                (long)Math.Max(0, gap.TotalMilliseconds), Signals, Completions));
        }
    }

    private void AcceptClear(DutyResult clear)
    {
        var stateBefore = Met(_machine.State);
        var runBefore = stateBefore == RunState.Idle ? null : _machine.CurrentRunId;
        var duplicatesBefore = _machine.DuplicateCount;

        _inner.Accept(clear);

        if (_machine.DuplicateCount > duplicatesBefore)
        {
            // The same observation again: nothing new was seen.
            return;
        }

        var completed = stateBefore == RunState.EnteredDuty && _machine.State == RunState.Completed &&
            runBefore is not null;
        _counts.Add(completed);
        if (completed)
        {
            _completed = (runBefore!, clear.Mono);
        }

        Note(new DutyClearNote(
            DutyClearNoteKind.Signal, runBefore, stateBefore, completed, null, Signals, Completions));
    }

    /// <summary>The state an event meets: the machine collapses a terminal state to IDLE before acting.</summary>
    /// <param name="state">State as read off the machine.</param>
    private static RunState Met(RunState state) =>
        state is RunState.MentorMatched or RunState.EnteredDuty ? state : RunState.Idle;

    private void Note(DutyClearNote note)
    {
        try
        {
            noted?.Invoke(note);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Diagnostics never cost a record: the event has already reached the machine, and a
            // subscriber that throws here would otherwise be counted as a parser failure.
            _ = ex;
        }
    }
}
