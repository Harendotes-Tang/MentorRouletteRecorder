using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// The part of the machine that is about carrying state across a boundary rather than about
/// deciding what a run was: what one machine hands the next when a capture session is retried,
/// and what has to be put back when a storage commit fails and the transition is undone.
///
/// Both are mechanical, and both are a place where a field added to the machine is easy to
/// forget: everything mutable has to appear in <see cref="MentorRunStateMachine.Checkpoint"/>
/// and come back in <see cref="MentorRunStateMachine.Restore"/>, or a rolled-back commit leaves
/// the machine claiming something the traffic never said.
/// </summary>
public sealed partial class MentorRunStateMachine
{
    /// <summary>
    /// Adopts what an earlier machine knew about the player.
    ///
    /// A capture session that faulted and was retried builds a new machine. Without the seed
    /// it starts with no job, and because the job is announced only at login and on class
    /// changes, the next run would be recorded job-less for as long as the player keeps the
    /// same class (review finding L-8). Only player-level memory is carried; nothing about the
    /// run in flight crosses a session boundary, and a machine already tracking a run refuses
    /// the seed rather than rewriting its own history.
    ///
    /// The last territory announcement must <em>not</em> travel with it. Its age is measured
    /// against the capture source's stopwatch, which restarts at zero every session, so a stale
    /// announcement can compare as recent, fall inside the ten-second window, and have a city
    /// recorded as the duty the player just entered (review finding R-6). The job carries no
    /// such reading.
    /// </summary>
    /// <param name="memory">Memory captured from the previous machine, or null.</param>
    public void Seed(StateMachineMemory? memory)
    {
        if (memory is null || _state != RunState.Idle || _runId is not null)
        {
            return;
        }

        _lastKnownJobId = memory.LastKnownJobId ?? _lastKnownJobId;
    }

    /// <summary>Forgets all state, including the duplicate set. Used between replays.</summary>
    public void Reset()
    {
        ClearRun(RunState.Idle);
        _lastKnownJobId = null;
        _lastTerritory = null;
        _profileLost = false;
        _dedup.Clear();
        ParserErrorCount = 0;
        DuplicateCount = 0;
    }

    /// <summary>Captures the machine's mutable state so a failed storage commit can be undone.</summary>
    public StateMachineCheckpoint Checkpoint() =>
        new(
            _state,
            _runId,
            _matchedMono,
            _enteredMono,
            _entered,
            _contentId,
            _territoryId,
            _jobId,
            _lastKnownJobId,
            _lastTerritory,
            _profileLost,
            ParserErrorCount,
            DuplicateCount,
            _dedup.Snapshot())
            {
                PendingQueue = _pendingQueue,
                MatchObserved = _matchObserved,
                AnnouncedRequest = _announcedRequest,
                AnnouncedRefreshes = _announcedRefreshes,
                MatchOffers = _matchOffers,
            };

    /// <summary>Restores a snapshot captured by <see cref="Checkpoint"/>.</summary>
    /// <param name="checkpoint">State to restore.</param>
    public void Restore(StateMachineCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _state = checkpoint.State;
        _runId = checkpoint.RunId;
        _matchedMono = checkpoint.MatchedMono;
        _enteredMono = checkpoint.EnteredMono;
        _entered = checkpoint.Entered;
        _contentId = checkpoint.ContentId;
        _territoryId = checkpoint.TerritoryId;
        _jobId = checkpoint.JobId;
        _pendingQueue = checkpoint.PendingQueue;
        _matchObserved = checkpoint.MatchObserved;
        _announcedRequest = checkpoint.AnnouncedRequest;
        _announcedRefreshes = checkpoint.AnnouncedRefreshes;
        _matchOffers = checkpoint.MatchOffers;
        _lastKnownJobId = checkpoint.LastKnownJobId;
        _lastTerritory = checkpoint.LastTerritory;
        _profileLost = checkpoint.ProfileLost;
        ParserErrorCount = checkpoint.ParserErrorCount;
        DuplicateCount = checkpoint.DuplicateCount;
        _dedup.Restore(checkpoint.DedupKeys);
    }
}
