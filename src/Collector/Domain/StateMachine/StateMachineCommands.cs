using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// A side effect the state machine asks its host to perform. The machine itself never
/// touches the database, the clock or the network: it returns commands and the host applies
/// them. That is what makes offline fixture replay deterministic
/// (docs/state-machine.md section 6).
/// </summary>
public abstract record StateCommand;

/// <summary>Create a new run row for a mentor roulette match.</summary>
/// <param name="RunId">Identifier assigned by the machine.</param>
/// <param name="MatchedAtUtc">Wall-clock time of the pop.</param>
/// <param name="MentorRouletteId">Roulette id that made this a mentor roulette.</param>
/// <param name="ContentId">Content id carried by the pop, when present.</param>
public sealed record CreateRunCommand(
    string RunId,
    DateTimeOffset MatchedAtUtc,
    int MentorRouletteId,
    int? ContentId) : StateCommand
{
    /// <summary>Original match/queue clock anchor when creation is deferred until entry.</summary>
    public TimeSpan? MatchedMono { get; init; }
}

/// <summary>Record that the duty was entered.</summary>
/// <param name="RunId">Run being updated.</param>
/// <param name="EnteredAtUtc">Wall-clock time of entry.</param>
/// <param name="ContentId">Content id, when known.</param>
/// <param name="TerritoryId">Territory id, when known.</param>
public sealed record EnterDutyCommand(
    string RunId,
    DateTimeOffset EnteredAtUtc,
    int? ContentId,
    int? TerritoryId) : StateCommand;

/// <summary>
/// Record which duty the run is in, identified by territory rather than by content id.
///
/// The CN client announces the territory in a message of its own, roughly 50 ms before the
/// zone initialisation this project treats as the verified entry marker, and that marker
/// carries no readable content id. Without this command a mentor run is stored with no duty
/// name at all, which is most of what the record is for. The host resolves the territory
/// against the local duty reference file; the state machine never names a duty itself.
/// </summary>
/// <param name="RunId">Run being updated.</param>
/// <param name="TerritoryId">Observed territory id.</param>
public sealed record SetDutyCommand(string RunId, int TerritoryId) : StateCommand;

/// <summary>Record the observed job of the local player.</summary>
/// <param name="RunId">Run being updated.</param>
/// <param name="JobId">Observed job id.</param>
public sealed record SetJobCommand(string RunId, int JobId) : StateCommand;

/// <summary>Close the run with a final result.</summary>
/// <param name="RunId">Run being closed.</param>
/// <param name="EndedAtUtc">Wall-clock end time.</param>
/// <param name="DurationMs">Monotonic duration, or null when entry was never observed.</param>
/// <param name="Result">Final result bucket.</param>
/// <param name="Confidence">Confidence of the determination.</param>
/// <param name="PendingReview">
/// True when the profile could observe the duty ending but not its outcome, so the result
/// is UNKNOWN until the user confirms it (docs/state-machine.md section 3.10).
/// </param>
public sealed record FinishRunCommand(
    string RunId,
    DateTimeOffset EndedAtUtc,
    long? DurationMs,
    RunResult Result,
    DetectionConfidence Confidence,
    bool PendingReview = false) : StateCommand;

/// <summary>Append one row to the event trail of a run.</summary>
/// <param name="RunId">Run the event belongs to.</param>
/// <param name="Event">Semantic event that produced it.</param>
/// <param name="FromState">State before the transition.</param>
/// <param name="ToState">State after the transition.</param>
/// <param name="Confidence">Confidence of the observation.</param>
public sealed record AppendEventCommand(
    string RunId,
    SemanticEvent Event,
    RunState FromState,
    RunState ToState,
    DetectionConfidence Confidence) : StateCommand;

/// <summary>
/// Count a parser or fail-closed refusal. Diagnostics only; it never produces a run.
/// </summary>
/// <param name="Kind">Short machine-readable reason.</param>
/// <param name="Detail">Non-sensitive explanatory detail.</param>
public sealed record RecordParserErrorCommand(string Kind, string Detail) : StateCommand;

/// <summary>Result of feeding one event to the state machine.</summary>
/// <param name="FromState">State before the event.</param>
/// <param name="ToState">State after the event.</param>
/// <param name="Transitioned">True when the state actually changed.</param>
/// <param name="Accepted">True when the event was acted on rather than ignored or deduplicated.</param>
/// <param name="Duplicate">True when the event was rejected as a duplicate.</param>
/// <param name="RunId">Run the event applies to, when any.</param>
/// <param name="Commands">Side effects the host must apply, in order.</param>
public sealed record TransitionResult(
    RunState FromState,
    RunState ToState,
    bool Transitioned,
    bool Accepted,
    bool Duplicate,
    string? RunId,
    IReadOnlyList<StateCommand> Commands)
{
    /// <summary>An event that changed nothing.</summary>
    /// <param name="state">Current state.</param>
    /// <param name="runId">Current run, if any.</param>
    /// <param name="duplicate">Whether the event was a duplicate.</param>
    /// <param name="commands">Optional diagnostic commands.</param>
    public static TransitionResult Ignored(
        RunState state,
        string? runId,
        bool duplicate = false,
        IReadOnlyList<StateCommand>? commands = null) =>
        new(state, state, false, false, duplicate, runId, commands ?? Array.Empty<StateCommand>());
}
