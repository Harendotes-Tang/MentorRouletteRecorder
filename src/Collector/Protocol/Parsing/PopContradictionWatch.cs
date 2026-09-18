namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>
/// Watches the message a profile calls CONTENT_FINDER_POP and says when the traffic has
/// disproved it.
///
/// The duty finder offers one duty at a time: whatever else a build changes, it cannot announce
/// three different roulettes inside the same second, because there is only ever one queue
/// standing and one match to announce. A message that does exactly that is not an announcement.
/// A list is - the retainer bell's rows carry their slot number, 0 to 9, at the byte a learned
/// CN 2026.09.15 profile read the roulette id from, and the whole run of rows arrives within a
/// second of the player opening the bell. That profile was written as VERIFIED, and from then on
/// every bell opening announced a match that never happened.
///
/// Nothing here reads a clock, a file or a profile. It is given the monotonic reading each pop
/// carried and the roulette id that was parsed out of it, and answers one question. The pops it
/// is given have already passed the profile's own constraints, so a roulette id of 0 - which a
/// queue for one named duty legitimately pops with, and which the learned constraint
/// <c>min 1</c> refuses before this point - never reaches it; were it to, a lone 0 beside a
/// repeated id is still only two distinct values and contradicts nothing by itself.
/// </summary>
public sealed class PopContradictionWatch
{
    /// <summary>How close together the sightings must be to count as one announcement burst.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>How many different roulette ids inside the window disprove the declaration.</summary>
    public const int DistinctToContradict = 3;

    /// <summary>
    /// How many sightings are kept at most. A build that sends this shape every few
    /// milliseconds all evening must not be able to grow memory; the window is a second, so the
    /// cap only ever discards sightings that a burst long past the threshold would have held.
    /// </summary>
    public const int MaxSightings = 32;

    private readonly Queue<Sighting> _sightings = new();
    private TimeSpan? _last;

    /// <summary>True once the traffic disproved the declaration; stays true until <see cref="Reset"/>.</summary>
    public bool Contradicted { get; private set; }

    /// <summary>How many sightings are being kept, never more than <see cref="MaxSightings"/>.</summary>
    public int Sighted => _sightings.Count;

    /// <summary>
    /// Records one parsed pop and answers whether the declaration stands disproved - which, once
    /// it is, every later pop answers too, so a caller can drop them all until the profile is gone.
    /// </summary>
    /// <param name="mono">Monotonic reading the message carried, on the capture source's stopwatch.</param>
    /// <param name="rouletteId">Roulette id the profile read out of it.</param>
    public bool Observe(TimeSpan mono, int rouletteId)
    {
        if (Contradicted)
        {
            return true;
        }

        // A new capture session starts a new stopwatch, so readings begin again from zero. What
        // the previous session saw did not arrive in the same second as this, and reading it as
        // if it had would withdraw a profile the traffic never contradicted.
        if (_last is { } last && mono < last)
        {
            _sightings.Clear();
        }

        _last = mono;
        while (_sightings.Count > 0 && mono - _sightings.Peek().Mono > Window)
        {
            _sightings.Dequeue();
        }

        _sightings.Enqueue(new Sighting(mono, rouletteId));
        while (_sightings.Count > MaxSightings)
        {
            _sightings.Dequeue();
        }

        var distinct = new HashSet<int>();
        foreach (var sighting in _sightings)
        {
            distinct.Add(sighting.RouletteId);
        }

        Contradicted = distinct.Count >= DistinctToContradict;
        return Contradicted;
    }

    /// <summary>Forgets everything, for a watch that is armed again over another profile.</summary>
    public void Reset()
    {
        _sightings.Clear();
        _last = null;
        Contradicted = false;
    }

    /// <summary>One parsed pop, as the window remembers it.</summary>
    /// <param name="Mono">Monotonic reading it carried.</param>
    /// <param name="RouletteId">Roulette id read out of it.</param>
    private readonly record struct Sighting(TimeSpan Mono, int RouletteId);
}
