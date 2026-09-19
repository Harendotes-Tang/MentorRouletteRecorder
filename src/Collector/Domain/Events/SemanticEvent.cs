using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Domain.Events;

/// <summary>
/// A verified semantic event: something the state machine is allowed to react to.
///
/// These types are the only input the state machine accepts, and producing one is a promise
/// that the value came from a profile-declared opcode and structure and that every required
/// field parsed successfully. Partial parses, guesses and heuristics are not verified events
/// (docs/state-machine.md section 0).
/// </summary>
public abstract record SemanticEvent
{
    /// <summary>Deduplication identity of this observation.</summary>
    public required EventKey Key { get; init; }

    /// <summary>Wall-clock time the observation was made.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>
    /// Monotonic reading taken when the observation was made. Durations are always the
    /// difference of two of these, never of two wall-clock timestamps.
    /// </summary>
    public required TimeSpan Mono { get; init; }

    /// <summary>Stable name written to run_events.event_type.</summary>
    public abstract string EventType { get; }
}

/// <summary>The duty finder popped. Only a mentor roulette id may start a run.</summary>
public sealed record ContentFinderPop : SemanticEvent
{
    /// <summary>Roulette id carried by the pop.</summary>
    public required int RouletteId { get; init; }

    /// <summary>Content id carried by the pop, when the profile exposes one.</summary>
    public int? ContentId { get; init; }

    /// <inheritdoc />
    public override string EventType => "CONTENT_FINDER_POP";
}

/// <summary>A zone was initialised; entering a duty is observed through this.</summary>
public sealed record ZoneInitialization : SemanticEvent
{
    /// <summary>Content id of the zone, when known.</summary>
    public int? ContentId { get; init; }

    /// <summary>Territory id of the zone, when known.</summary>
    public int? TerritoryId { get; init; }

    /// <summary>Instance id, when known. Diagnostics only.</summary>
    public int? InstanceId { get; init; }

    /// <summary>
    /// True when the zone is a duty instance, false for an open-world area, null when the
    /// profile has no field that tells the two apart. In the null case the state machine
    /// falls back to the match window and the order of zone changes.
    /// </summary>
    public bool? IsDutyInstance { get; init; }

    /// <inheritdoc />
    public override string EventType => "ZONE_INITIALIZATION";
}

/// <summary>
/// The client was told which territory it is being placed in.
///
/// This is a separate observation from <see cref="ZoneInitialization"/> on purpose. On the
/// CN client the territory arrives in its own message roughly 50 ms before the message this
/// project uses as the verified entry/exit marker, and that marker carries no readable
/// territory of its own. Splitting the two lets the run learn which duty it was without
/// moving the entry decision onto a message we have only ever seen once.
/// </summary>
public sealed record TerritoryObserved : SemanticEvent
{
    /// <summary>Territory id the client was placed in.</summary>
    public required int TerritoryId { get; init; }

    /// <inheritdoc />
    public override string EventType => "ZONE_TERRITORY";
}

/// <summary>The duty result screen. Only a victory may complete a run.</summary>
public sealed record DutyResult : SemanticEvent
{
    /// <summary>True when the outcome field matched one of the profile victory values.</summary>
    public required bool Victory { get; init; }

    /// <inheritdoc />
    public override string EventType => "DUTY_RESULT";
}

/// <summary>The local player job was observed.</summary>
public sealed record PlayerJob : SemanticEvent
{
    /// <summary>Job id.</summary>
    public required int JobId { get; init; }

    /// <inheritdoc />
    public override string EventType => "PLAYER_JOB";
}

/// <summary>The local player left the duty zone or instance without a victory.</summary>
public sealed record ZoneLeft : SemanticEvent
{
    /// <summary>Territory the player moved to, when known.</summary>
    public int? TerritoryId { get; init; }

    /// <inheritdoc />
    public override string EventType => "ZONE_LEFT";
}

/// <summary>The duty instance ended for the local player without a victory.</summary>
public sealed record InstanceLeft : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "INSTANCE_LEFT";
}

/// <summary>The game connection was observed to drop.</summary>
public sealed record ConnectionLost : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "CONNECTION_LOST";
}

/// <summary>Capture stopped: the user stopped it, or the Collector is shutting down.</summary>
public sealed record CaptureStopped : SemanticEvent
{
    /// <summary>True when the game process itself went away rather than the capture.</summary>
    public bool GameExited { get; init; }

    /// <inheritdoc />
    public override string EventType => "CAPTURE_STOPPED";
}

/// <summary>
/// The bounded capture queue dropped events and created a known sequence gap. Once a duty
/// has been entered, reliable classification is no longer possible and the run is closed as
/// INTERRUPTED with LOW confidence.
/// </summary>
public sealed record EventSequenceGap : SemanticEvent
{
    /// <summary>Number of observations known to have been dropped.</summary>
    public required long DroppedCount { get; init; }

    /// <inheritdoc />
    public override string EventType => "EVENT_SEQUENCE_GAP";
}

/// <summary>
/// The server announced that a match was found, on a build where that message carries nothing
/// else - no roulette id at any offset, which is why no profile could find it by value.
///
/// It therefore says only <em>when</em>, never what. The roulette comes from the queue request
/// the player made earlier, which the profile is already standing in for the match; the
/// announcement moves that match to the moment the popup actually appeared and lets the desktop
/// say so out loud (docs/state-machine.md section 3.12).
/// </summary>
public sealed record MatchAnnounced : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "MATCH_ANNOUNCED";
}

/// <summary>Matching was cancelled or declined before entering the duty.</summary>
public sealed record MatchCancelled : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "MATCH_CANCELLED";
}

/// <summary>
/// A timer tick. Carries no observation of its own; it lets the state machine notice that a
/// match has been pending for longer than the configured timeout.
/// </summary>
public sealed record TimeoutTick : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "TIMEOUT_TICK";
}

/// <summary>
/// Crash recovery marker, emitted by the Collector itself on startup rather than by a parser.
/// </summary>
public sealed record ProcessRestart : SemanticEvent
{
    /// <inheritdoc />
    public override string EventType => "PROCESS_RESTART";

    /// <summary>Confidence to attach to the affected run; always Low.</summary>
    public DetectionConfidence Confidence => DetectionConfidence.Low;
}

/// <summary>
/// The protocol profile became unusable in the middle of a session, so no further event can
/// be trusted. Emitted by the Collector, not by a parser. Any run still open is closed as
/// UNKNOWN_FINAL_STATE (docs/state-machine.md section 3.8).
/// </summary>
public sealed record ProfileLost : SemanticEvent
{
    /// <summary>Non-sensitive explanation, for diagnostics.</summary>
    public string? Detail { get; init; }

    /// <inheritdoc />
    public override string EventType => "PROFILE_LOST";
}
