using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// One arrival of a timed shape: when it happened and which connection carried it. No opcode,
/// no length and no payload - the shape it belongs to already says the first two and nothing
/// says the third.
/// </summary>
/// <param name="AtUtc">Wall-clock arrival time, so sightings relate across capture sessions.</param>
/// <param name="TMs">Session-relative arrival time.</param>
/// <param name="ConnectionTag">Redacted connection tag it arrived on.</param>
public readonly record struct TimedSighting(DateTimeOffset AtUtc, long TMs, string ConnectionTag);

/// <summary>
/// What one server shape - an opcode and an exact payload length - does in time.
///
/// The CN 2026.09.15 client announces a match with a message that carries the roulette id
/// nowhere in its payload, so every search by value fails and the profile falls back to the
/// player's own queue request. This is the other way to recognise it: a message that arrives
/// only while a queue stands, before every duty the player enters, and never inside a zone
/// load. Counts and times only; no byte of any payload has ever been in one of these.
/// </summary>
/// <param name="Opcode">Opcode of the shape.</param>
/// <param name="Length">Exact payload length of the shape.</param>
/// <param name="Total">Sightings outside every zone load, however they were classified.</param>
/// <param name="InQueue">Of those, the ones that arrived while this connection had a request outstanding.</param>
/// <param name="PreDuty">Of those, the ones with no request that a known duty's load followed in time.</param>
/// <param name="Stray">Of those, the ones that waited out the window with no duty behind them.</param>
/// <param name="Sightings">
/// The most recent sightings, bounded. They are what relates the shape to the duties the player
/// entered; <paramref name="SightingsComplete"/> says whether any had to be let go.
/// </param>
/// <param name="Pending">
/// Sightings still waiting to find out which they are: no request stood when they arrived, and
/// the window in which a duty could still explain them has not run out.
/// </param>
/// <param name="SightingsComplete">True while no sighting has been dropped for want of room.</param>
public sealed record TimedShape(
    ushort Opcode,
    int Length,
    int Total,
    int InQueue,
    int PreDuty,
    int Stray,
    IReadOnlyList<TimedSighting> Sightings,
    IReadOnlyList<TimedSighting> Pending,
    bool SightingsComplete)
{
    /// <summary>Shape this row is about; a timed shape is always a server message.</summary>
    public MessageKey Shape => new(PacketDirection.ServerToClient, Opcode, Length);

    /// <summary>
    /// The earliest moment this row can still speak about. With every sighting kept that is the
    /// beginning of time; once one has been dropped the row knows nothing about what the shape
    /// did before the oldest it still holds, and a duty entered back then can neither support
    /// nor refute it.
    /// </summary>
    public DateTimeOffset? Horizon => SightingsComplete || Sightings.Count == 0
        ? null
        : Sightings[0].AtUtc;

    /// <summary>
    /// One line per surviving shape for the diagnostics report, in the form
    /// <c>0xop:len=inqueue+preduty/total!stray e(entries preceded)/(entries) lead(smallest lead)s</c>,
    /// most duties covered first, at most eight rows.
    ///
    /// Without it a stuck report cannot separate "no shape behaves like an announcement on this
    /// build" from "one does and it was ruled out by a clause", and those two ask entirely
    /// different things of whoever reads it.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="window">Match window from the template.</param>
    public static IReadOnlyList<string> Report(CalibrationSnapshot snapshot, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = snapshot.Clusters.Where(cluster => cluster.TerritoryHits.Count > 0).ToArray();
        return snapshot.TimedShapes
            .Select(shape => (Shape: shape, Leads: entries
                .Select(entry => shape.Sightings
                    .Where(sighting => sighting.AtUtc < entry.LoadStartedAtUtc &&
                        entry.LoadStartedAtUtc - sighting.AtUtc <= window)
                    .Select(sighting => entry.LoadStartedAtUtc - sighting.AtUtc)
                    .DefaultIfEmpty(TimeSpan.MinValue)
                    .Max())
                .Where(lead => lead > TimeSpan.MinValue)
                .ToArray()))
            .OrderByDescending(row => row.Leads.Length)
            .ThenBy(row => row.Shape.Total)
            .ThenBy(row => row.Shape.Opcode)
            .Take(8)
            .Select(row =>
                $"0x{row.Shape.Opcode:x4}:{row.Shape.Length}={row.Shape.InQueue}+{row.Shape.PreDuty}" +
                $"/{row.Shape.Total}!{row.Shape.Stray} e{row.Leads.Length}/{entries.Length} " +
                $"lead{(row.Leads.Length == 0 ? -1 : (int)row.Leads.Min().TotalSeconds)}s")
            .ToArray();
    }
}
