namespace MentorRecorder.Collector.Domain;

/// <summary>
/// One entry of the event trail that explains why a run was judged the way it was.
///
/// Only metadata is stored. The detail payload may carry opcode numbers, direction,
/// message length and parsed id-shaped fields; raw bytes, hex dumps, chat text, character
/// names and any information about other players are forbidden
/// (docs/data-model.md section 2, docs/privacy-boundary.md section 5).
/// </summary>
public sealed record RunEvent
{
    /// <summary>UUID primary key.</summary>
    public required string EventId { get; init; }

    /// <summary>Owning run.</summary>
    public required string RunId { get; init; }

    /// <summary>Monotonically increasing position inside the run.</summary>
    public required int Sequence { get; init; }

    /// <summary>Wall-clock time of the event.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Monotonic offset from the start of the run, in milliseconds.</summary>
    public required long MonotonicOffsetMs { get; init; }

    /// <summary>Semantic event type, for example CONTENT_FINDER_POP.</summary>
    public required string EventType { get; init; }

    /// <summary>State before the transition, if any.</summary>
    public RunState? FromState { get; init; }

    /// <summary>State after the transition, if any.</summary>
    public RunState? ToState { get; init; }

    /// <summary>Confidence attached to this observation.</summary>
    public DetectionConfidence Confidence { get; init; } = DetectionConfidence.None;

    /// <summary>
    /// Deduplication key, unique across the database. Replaying the same source twice, or
    /// restarting mid-run, therefore cannot append the same observation again.
    /// </summary>
    public string? EventKey { get; init; }

    /// <summary>Structured metadata; never contains payload bytes.</summary>
    public string? DetailJson { get; init; }
}
