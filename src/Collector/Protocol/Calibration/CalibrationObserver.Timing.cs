using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The half of the observer that watches <em>when</em> a server shape arrives instead of what it
/// carries.
///
/// Every other search this class performs reads a roulette id out of a payload. On the CN
/// 2026.09.15 client the message that announces a match carries one nowhere, and the three
/// value-based paths all come back empty, so the profile stands the player's own queue request in
/// for the announcement and the popup arrives in silence. What the announcement still does is
/// arrive at a particular moment, and that is what these tables count.
///
/// Boundedness is the whole design here, because the admission rule decides how much of a busy
/// city's traffic can reach the table at all: a shape is only ever admitted on a sighting made
/// while the player has a queue request outstanding. Everything after that - strays, the rare
/// ceiling, the dead set - prunes what did get in. Anything that could not be kept is counted in
/// <see cref="CalibrationSnapshot.TimingOverflow"/>, which the draft treats as a reason to name no
/// candidate at all rather than as a smaller sample.
/// </summary>
public sealed partial class CalibrationObserver
{
    /// <summary>Live shapes the timing table holds at once.</summary>
    public const int MaxTimingShapes = 512;

    /// <summary>Retired shapes remembered, so one cannot come back to life.</summary>
    public const int MaxTimingDead = 8192;

    /// <summary>Sightings kept per shape; the oldest goes when a new one does not fit.</summary>
    public const int MaxTimingSightings = 32;

    /// <summary>Sightings one shape may have waiting for a duty to explain them.</summary>
    public const int MaxTimingPending = 8;

    /// <summary>Sightings waiting across all shapes at once.</summary>
    public const int MaxTimingPendingTotal = 512;

    /// <summary>
    /// Sightings above which a shape cannot be the announcement however well it behaves. A match
    /// is announced a handful of times an evening; a shape seen hundreds of times is the client
    /// talking about something else that happens to keep pace with the queue.
    /// </summary>
    public const int RareCeiling = 400;

    /// <summary>
    /// Sightings a shape may have for each stray before it is retired. One stray in five is
    /// already far more than an announcement can produce, and with fewer than five sightings in
    /// total a single stray is enough.
    /// </summary>
    public const int StrayRatio = 5;

    private readonly Dictionary<(ushort Opcode, int Length), TimingStat> _timing = new();
    private readonly HashSet<(ushort Opcode, int Length)> _timingDead = new();
    private readonly List<PendingTimed> _timingPending = new();
    private int _timingOverflow;

    /// <summary>
    /// Files one server message under its shape's timing row.
    ///
    /// Called after the finder pairing, so an opcode a request/echo pair has just vouched for is
    /// already known to be the queue reply and is left out: the reply arrives while the queue
    /// stands every single time, which is exactly the pattern being looked for, and it is not an
    /// announcement.
    /// </summary>
    /// <param name="connection">Connection the message arrived on.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="length">Payload length; the payload itself is not passed and never kept.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="t">Session-relative arrival time.</param>
    /// <param name="at">Wall-clock arrival time.</param>
    private void TrackTiming(
        Connection connection, PacketDirection direction, int length, ushort opcode, long t, DateTimeOffset at)
    {
        // Every message ages the waiting sightings, including the ones this message is not
        // eligible to join: what makes a sighting a stray is time passing, not a shape arriving.
        ExpireTimedPending(at);
        if (direction != PacketDirection.ServerToClient || connection.Open is not null ||
            _replyOpcodes.Contains(opcode))
        {
            return;
        }

        var key = (opcode, length);
        if (_timingDead.Contains(key))
        {
            return;
        }

        if (!_timing.TryGetValue(key, out var stat))
        {
            // A shape first met outside every queue is ordinary traffic, of which a client sends
            // hundreds of kinds. The announcement cannot be one of them: the feature needs a queue
            // request to name the roulette anyway, so the shape it is looking for has necessarily
            // been seen while one stood.
            if (connection.Outstanding is null)
            {
                return;
            }

            if (_timing.Count >= MaxTimingShapes)
            {
                _timingOverflow++;
                return;
            }

            stat = new TimingStat();
            _timing.Add(key, stat);
        }

        stat.Total++;
        stat.Remember(new TimedSighting(at, t, connection.Tag));
        if (connection.Outstanding is not null)
        {
            stat.InQueue++;
        }
        else
        {
            Hold(key, stat, new TimedSighting(at, t, connection.Tag));
        }

        Retire(key, stat);
    }

    /// <summary>
    /// Sets a sighting aside until a duty explains it or the window runs out. A player who is
    /// brought into a duty by a party member never sends a request of their own and still
    /// receives the announcement, so "no request stood" is not yet evidence of anything.
    /// </summary>
    /// <param name="key">Shape the sighting belongs to.</param>
    /// <param name="stat">Its row.</param>
    /// <param name="sighting">The sighting.</param>
    private void Hold((ushort Opcode, int Length) key, TimingStat stat, TimedSighting sighting)
    {
        if (stat.Pending >= MaxTimingPending || _timingPending.Count >= MaxTimingPendingTotal)
        {
            // A shape with this many sightings in the air is not waiting on one duty, and the
            // list must not grow with the session; the newest is the one that gets no answer.
            stat.Stray++;
            return;
        }

        stat.Pending++;
        _timingPending.Add(new PendingTimed(key, sighting));
    }

    /// <summary>
    /// Turns sightings the wait has run out on into strays. The list is kept in arrival order, so
    /// only its head has to be looked at.
    /// </summary>
    /// <param name="now">Wall-clock time of the message being accepted.</param>
    private void ExpireTimedPending(DateTimeOffset now)
    {
        var window = _template.MatchWindow;
        var index = 0;
        while (index < _timingPending.Count && now - _timingPending[index].Sighting.AtUtc > window)
        {
            index++;
        }

        if (index == 0)
        {
            return;
        }

        // Taken off the list before any of it is counted: retiring a shape drops that shape's
        // other waiting sightings, and a loop that indexed the list while it was being rewritten
        // would silently lose the rest of the batch.
        var expired = _timingPending.GetRange(0, index);
        _timingPending.RemoveRange(0, index);
        foreach (var waiting in expired)
        {
            if (_timing.TryGetValue(waiting.Key, out var stat))
            {
                stat.Pending--;
                stat.Stray++;
                Retire(waiting.Key, stat);
            }
        }
    }

    /// <summary>
    /// A zone load that named a duty the duty table knows has begun: every sighting still waiting
    /// inside the match window before it is explained by this entry.
    /// </summary>
    /// <param name="loadStartedAtUtc">Wall-clock start of the load.</param>
    private void ResolveTimedPending(DateTimeOffset loadStartedAtUtc)
    {
        var window = _template.MatchWindow;
        for (var index = _timingPending.Count - 1; index >= 0; index--)
        {
            var waiting = _timingPending[index];
            var gap = loadStartedAtUtc - waiting.Sighting.AtUtc;
            if (gap < TimeSpan.Zero || gap > window)
            {
                continue;
            }

            if (_timing.TryGetValue(waiting.Key, out var stat))
            {
                stat.Pending--;
                stat.PreDuty++;
            }

            _timingPending.RemoveAt(index);
        }
    }

    /// <summary>
    /// Retires a shape that has strayed too often or arrived too many times, and remembers it so
    /// the next sighting cannot start it over. A dead set with no room is an overflow like any
    /// other: it can only make the draft refuse to name a candidate.
    /// </summary>
    /// <param name="key">Shape to judge.</param>
    /// <param name="stat">Its row.</param>
    private void Retire((ushort Opcode, int Length) key, TimingStat stat)
    {
        if (stat.Stray * StrayRatio <= stat.Total && stat.Total <= RareCeiling)
        {
            return;
        }

        _timing.Remove(key);
        _timingPending.RemoveAll(waiting => waiting.Key == key);
        if (_timingDead.Count < MaxTimingDead)
        {
            _timingDead.Add(key);
        }
        else
        {
            _timingOverflow++;
        }
    }

    /// <summary>A frozen copy of the timing table, newest-behaving shapes first.</summary>
    private IReadOnlyList<TimedShape> TimedShapes() => _timing
        .OrderBy(entry => entry.Key.Opcode)
        .ThenBy(entry => entry.Key.Length)
        .Select(entry => new TimedShape(
            entry.Key.Opcode,
            entry.Key.Length,
            entry.Value.Total,
            entry.Value.InQueue,
            entry.Value.PreDuty,
            entry.Value.Stray,
            entry.Value.Sightings.ToArray(),
            _timingPending.Where(waiting => waiting.Key == entry.Key)
                .Select(waiting => waiting.Sighting).ToArray(),
            entry.Value.SightingsComplete))
        .ToArray();

    /// <summary>
    /// Takes on the timing evidence of an earlier run. Sightings still waiting keep waiting: the
    /// software stopping is not the same as the window running out, and the next message accepted
    /// ages them by the wall clock exactly as it would have done without the restart.
    /// </summary>
    /// <param name="carried">Evidence read back from disk.</param>
    private void AdoptTiming(CalibrationSnapshot carried)
    {
        foreach (var shape in carried.TimedShapes)
        {
            var key = (shape.Opcode, shape.Length);
            _timing[key] = TimingStat.Restored(shape);
            foreach (var waiting in shape.Pending.Take(MaxTimingPending))
            {
                if (_timingPending.Count < MaxTimingPendingTotal)
                {
                    _timingPending.Add(new PendingTimed(key, waiting));
                }
            }
        }

        // Arrival order is what lets the expiry look only at the head of the list.
        _timingPending.Sort(static (left, right) => left.Sighting.AtUtc.CompareTo(right.Sighting.AtUtc));
        foreach (var dead in carried.TimedDead)
        {
            if (_timingDead.Count < MaxTimingDead)
            {
                _timingDead.Add(dead);
            }
        }

        _timingOverflow += carried.TimingOverflow;
    }

    /// <summary>One sighting waiting for a duty to explain it, with the shape it belongs to.</summary>
    /// <param name="Key">Opcode and length of the shape.</param>
    /// <param name="Sighting">The sighting itself.</param>
    private readonly record struct PendingTimed((ushort Opcode, int Length) Key, TimedSighting Sighting);

    /// <summary>Running counts for one shape. Holds times and counts, never a payload.</summary>
    private sealed class TimingStat
    {
        /// <summary>Rebuilds a row from evidence read back off disk.</summary>
        /// <param name="carried">The row as it was written.</param>
        public static TimingStat Restored(TimedShape carried)
        {
            var stat = new TimingStat
            {
                Total = carried.Total,
                InQueue = carried.InQueue,
                PreDuty = carried.PreDuty,
                Stray = carried.Stray,
                Pending = Math.Min(carried.Pending.Count, MaxTimingPending),
                SightingsComplete = carried.SightingsComplete,
            };
            stat.Sightings.AddRange(carried.Sightings.Take(MaxTimingSightings));
            return stat;
        }

        public int Total { get; set; }

        public int InQueue { get; set; }

        public int PreDuty { get; set; }

        public int Stray { get; set; }

        public int Pending { get; set; }

        public bool SightingsComplete { get; private set; } = true;

        public List<TimedSighting> Sightings { get; } = new();

        /// <summary>Keeps a sighting, letting the oldest go when there is no room for it.</summary>
        /// <param name="sighting">Sighting to keep.</param>
        public void Remember(TimedSighting sighting)
        {
            if (Sightings.Count >= MaxTimingSightings)
            {
                Sightings.RemoveAt(0);
                SightingsComplete = false;
            }

            Sightings.Add(sighting);
        }
    }
}
