using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// One observation of a CN duty-start signal, for the diagnostic log. It names a run and says what
/// the machine made of it; it never carries the payload, its hash, the opcode or the duty's
/// director number, as for the clear.
/// </summary>
/// <param name="Commenced">True for the commencement, false for a remaining-time report.</param>
/// <param name="RunId">The run in flight when the signal arrived.</param>
/// <param name="Seconds">The time limit, or the seconds left, the signal carried.</param>
/// <param name="Decided">True when this signal decided how the player got in.</param>
/// <param name="JoinedInProgress">What was decided, when it was; what had been decided earlier otherwise.</param>
public sealed record DutyStartNote(
    bool Commenced,
    string? RunId,
    int Seconds,
    bool Decided,
    bool? JoinedInProgress)
{
    /// <summary>Event name of the log line, under component <c>protocol</c>.</summary>
    public string LogEvent => "duty_start_signal";

    /// <summary>The fields of the log line, and nothing else.</summary>
    public IReadOnlyDictionary<string, object?> LogFields() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["kind"] = Commenced ? "commenced" : "time_remaining",
        ["run_id"] = RunId,
        ["seconds"] = Seconds,
        ["decided"] = Decided,
        ["joined_in_progress"] = JoinedInProgress,
    };
}

/// <summary>
/// Reports the CN duty-start signals that reach a state machine with a run in a duty, on their way
/// to it (<see cref="Parsing.DutyStartSignal"/>, docs/protocol-profile-format.md section 13).
///
/// It decides nothing: every event reaches <paramref name="inner"/> unchanged and in order, and the
/// machine alone records how the player got in. Around a signal it reads the machine before and
/// after, and reports the signal when the machine met it inside a duty and either decided on it or
/// was told the duty commenced after it had already decided - the one shape that would contradict
/// what this project assumes about late joiners. A remaining-time report that decides nothing (the
/// five-minute ticks of a duty already classified) is passed on silently, so a two-hour duty does
/// not fill the log. The notes are what lets the first duty joined in progress on the gaming machine
/// confirm the assumption (docs/live-validation-guide.md section 4.1, scenario 4).
/// </summary>
/// <param name="inner">The sink the parser would otherwise feed.</param>
/// <param name="machine">The state machine behind <paramref name="inner"/>.</param>
/// <param name="noted">Receives every note; called on the parser's thread, with the pipeline's lock held.</param>
public sealed class DutyStartTally(
    ISemanticEventSink inner,
    MentorRunStateMachine machine,
    Action<DutyStartNote>? noted) : ISemanticEventSink
{
    private readonly ISemanticEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MentorRunStateMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));

    /// <inheritdoc />
    public void Accept(SemanticEvent semanticEvent)
    {
        var (commenced, seconds) = semanticEvent switch
        {
            DutyCommenced start => (true, start.TimeLimitSeconds),
            DutyTimeRemaining remaining => (false, remaining.RemainingSeconds),
            _ => (false, -1),
        };
        if (seconds < 0)
        {
            _inner.Accept(semanticEvent);
            return;
        }

        var inDuty = _machine.State == RunState.EnteredDuty;
        var runBefore = inDuty ? _machine.CurrentRunId : null;
        var before = _machine.JoinedInProgress;
        var duplicatesBefore = _machine.DuplicateCount;

        _inner.Accept(semanticEvent);

        if (!inDuty || runBefore is null || _machine.DuplicateCount > duplicatesBefore)
        {
            // Outside a duty nothing is decided; the same observation delivered again is
            // not a second signal, so it is not a commencement after the decision either.
            return;
        }

        var decided = before is null && _machine.JoinedInProgress is not null &&
            string.Equals(_machine.CurrentRunId, runBefore, StringComparison.Ordinal);
        if (decided || commenced)
        {
            Note(new DutyStartNote(commenced, runBefore, seconds, decided, decided ? _machine.JoinedInProgress : before));
        }
    }

    private void Note(DutyStartNote note)
    {
        try
        {
            noted?.Invoke(note);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A log line must never cost a record.
            _ = ex;
        }
    }
}
