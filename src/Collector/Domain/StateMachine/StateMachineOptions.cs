namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// Tunables of <see cref="MentorRunStateMachine"/>. All of them have conservative defaults
/// and none of them can make the machine guess a finer result than it observed.
/// </summary>
public sealed record StateMachineOptions
{
    /// <summary>Defaults used everywhere unless a test or a profile overrides them.</summary>
    public static StateMachineOptions Default { get; } = new();

    /// <summary>
    /// Duty finder acceptance window declared by the profile (docs/state-machine.md
    /// section 3.3 rule 2, default 45 seconds). After this window a match that is still
    /// pending is considered stale, so a new pop or an explicit return to idle closes it as
    /// CANCELLED_BEFORE_ENTRY instead of being treated as the same match.
    /// </summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a territory announcement stays usable as the territory of a duty that is
    /// entered afterwards. The CN client sends the two about 50 ms apart, so the window is
    /// generous by two orders of magnitude and still far too short to reach the previous
    /// zone change (docs/state-machine.md section 3.11).
    /// </summary>
    public TimeSpan TerritoryMemory { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Capacity of the duplicate-observation set. Bounded on purpose.</summary>
    public int DedupCapacity { get; init; } = 8192;

    /// <summary>
    /// Whether a territory id belongs to a duty instance, per the reference table. Only a
    /// profile that infers the match from the queue request consults it: the request is held
    /// in memory until the zone change that follows is shown to be a duty rather than a
    /// teleport, at which point the run is created and entered together. Null means the
    /// question cannot be answered, and an unanswerable question never admits an entry.
    /// </summary>
    public Func<int, bool>? IsKnownDuty { get; init; }
}
