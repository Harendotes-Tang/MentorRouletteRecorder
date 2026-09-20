using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>Enough mutable state to roll a <see cref="MentorRunStateMachine"/> back after a failed commit.</summary>
/// <param name="State">Current state.</param>
/// <param name="RunId">Run in flight, if any.</param>
/// <param name="MatchedMono">Monotonic time of the current match.</param>
/// <param name="EnteredMono">Monotonic time of the duty entry, if any.</param>
/// <param name="Entered">Whether the duty has been entered.</param>
/// <param name="ContentId">Content currently tracked, if any.</param>
/// <param name="TerritoryId">Territory currently tracked, if any.</param>
/// <param name="JobId">Job currently tracked, if any.</param>
/// <param name="LastKnownJobId">Most recent job observed in any state, if any.</param>
/// <param name="ProfileLost">Whether the bound profile became unusable.</param>
/// <param name="ParserErrorCount">Refusal count.</param>
/// <param name="DuplicateCount">Duplicate count.</param>
/// <param name="DedupKeys">Retained dedup keys, oldest first.</param>
public sealed record StateMachineCheckpoint(
    RunState State,
    string? RunId,
    TimeSpan MatchedMono,
    TimeSpan? EnteredMono,
    bool Entered,
    int? ContentId,
    int? TerritoryId,
    int? JobId,
    int? LastKnownJobId,
    TerritoryObserved? LastTerritory,
    bool ProfileLost,
    int ParserErrorCount,
    int DuplicateCount,
    IReadOnlyList<string> DedupKeys)
{
    /// <summary>Queue evidence awaiting a duty entry; it has no persisted run yet.</summary>
    public ContentFinderPop? PendingQueue { get; init; }

    /// <summary>
    /// Whether the match in flight was opened by the server's own announcement rather than
    /// inferred from the queue. It decides what the desktop is told about this match and how
    /// long the entry may take, so a rolled-back commit has to roll it back too.
    /// </summary>
    public bool MatchObserved { get; init; }

    /// <summary>The queue request behind an announced match, which outlives the announcement's window.</summary>
    public ContentFinderPop? AnnouncedRequest { get; init; }

    /// <summary>How many times the announced match in flight has been announced again.</summary>
    public int AnnouncedRefreshes { get; init; }

    /// <summary>How many times the run in flight has been offered to the player.</summary>
    public int MatchOffers { get; init; }
}

/// <summary>
/// What one machine knew about the player rather than about a run, so a machine built for a
/// retried capture session can start from it (docs/state-machine.md section 3.11).
/// </summary>
/// <param name="LastKnownJobId">Most recent job observed in any state, if any.</param>
/// <param name="LastTerritory">Most recent territory announcement, if any.</param>
public sealed record StateMachineMemory(int? LastKnownJobId);
