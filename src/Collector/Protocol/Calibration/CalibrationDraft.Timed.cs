using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The message that announces a match, recognised by when it arrives rather than by anything it
/// carries.
///
/// It never names the roulette - the queue request does that, exactly as it did before - so this
/// is an add-on to a <see cref="CalibrationMatchSource.QueueRequest"/> draft and never a match
/// source of its own. What it buys is the moment: the player's screen shows the popup and the
/// software can say so, which on a queue-inferred profile it otherwise cannot do until the
/// loading screen ends.
/// </summary>
/// <param name="Shape">Opcode and exact payload length of the announcement.</param>
/// <param name="Samples">
/// One sighting per duty entry it explained, in time order: the first arrival inside the match
/// window before that entry. These are the lines the user confirms.
/// </param>
public sealed record TimedAnnouncement(MessageKey Shape, IReadOnlyList<PopHit> Samples);

public sealed partial record CalibrationDraft
{
    /// <summary>
    /// Duty entries a shape must have preceded before it can be the announcement. One is worth
    /// nothing: whatever was travelling while the player waited preceded that one too.
    /// </summary>
    public const int MinTimedEntries = 2;

    /// <summary>
    /// Distinct roulettes those entries must have been queued for. A message the client only
    /// sends while one particular roulette is queued would pass every test a single roulette can
    /// pose and then never fire again.
    /// </summary>
    public const int MinTimedRoulettes = 2;

    /// <summary>
    /// Arrivals a shape may have between a queue request and the duty it led to. One match is
    /// announced three or four times in a row on this client; a status the finder ticks over
    /// while the player waits arrives far more often than that, and dating a match by it would
    /// date it to the moment the player queued.
    /// </summary>
    public const int MaxPerWindow = 8;

    /// <summary>
    /// The announcement this draft recognised by its timing, or null when none could be. Never
    /// changes <see cref="MatchSource"/>: the roulette still comes from the player's own request.
    /// </summary>
    public TimedAnnouncement? TimedAnnouncement { get; init; }

    /// <summary>
    /// Finds the message that announces a match without reading a single byte of it.
    ///
    /// Tried only when all three searches by value came back empty and the queue request has been
    /// accepted as the match, which is the situation the CN 2026.09.15 client creates: the
    /// announcement carries the roulette id at no offset, so nothing can be found by looking for
    /// one. What is left is when it arrives, and the observer has been counting exactly that.
    ///
    /// A candidate must have arrived, inside the match window, before every duty the player
    /// queued themselves into - at least two of them, on at least two different roulettes - and
    /// it must not have arrived more than a handful of times inside any of those queues. It must
    /// never travel inside a zone load, because a message belonging to a load travels in every
    /// load and one of those always precedes a duty. And it must still be alive: the observer
    /// retires any shape that arrives when the player is neither queueing nor about to load.
    ///
    /// Two shapes can pass all of that - the popup, and whatever the client sends as the loading
    /// screen begins - so the one that leads by the most in its worst window wins, the popup
    /// being the earlier of the two by the length of the player's own decision. A tie is not
    /// broken by guessing.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the match window.</param>
    /// <param name="chains">Each duty entry with the queue request that explains it.</param>
    /// <param name="replies">Opcodes a request/echo pair vouched for; the queue reply is not an announcement.</param>
    /// <param name="rejections">Candidates the user already rejected.</param>
    private static TimedAnnouncement? LockTimedAnnouncement(
        CalibrationSnapshot snapshot,
        CalibrationTemplate template,
        IReadOnlyList<(PopHit Pop, ZoneCluster Entry)> chains,
        IReadOnlySet<ushort> replies,
        CalibrationRejections rejections)
    {
        // A shape the tables could not take is a shape nothing is known about, and the claim
        // being made is about every shape there was.
        if (snapshot.TimingOverflow > 0 || chains.Count < MinTimedEntries)
        {
            return null;
        }

        var window = template.MatchWindow;
        var candidates = new List<(MessageKey Shape, TimeSpan Lead, PopHit[] Samples)>();
        foreach (var shape in snapshot.TimedShapes)
        {
            if (replies.Contains(shape.Opcode) || rejections.TimedOpcodes.Contains(shape.Opcode) ||
                rejections.PopOpcodes.Contains(shape.Opcode) ||
                snapshot.Clusters.Any(cluster => cluster.Members.ContainsKey(shape.Shape)))
            {
                continue;
            }

            if (Supports(shape, chains, window) is { } support)
            {
                candidates.Add((shape.Shape, support.Lead, support.Samples));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = candidates.OrderByDescending(candidate => candidate.Lead).ToArray();
        if (ordered.Length > 1 && ordered[0].Lead == ordered[1].Lead)
        {
            return null;
        }

        return new TimedAnnouncement(ordered[0].Shape, ordered[0].Samples);
    }

    /// <summary>
    /// Whether one shape preceded every entry it can speak about, and by how little in its worst
    /// window. Null when it failed any of the clauses.
    /// </summary>
    /// <param name="shape">Timing row of the shape.</param>
    /// <param name="chains">Each duty entry with the queue request that explains it.</param>
    /// <param name="window">Match window from the template.</param>
    private static (TimeSpan Lead, PopHit[] Samples)? Supports(
        TimedShape shape, IReadOnlyList<(PopHit Pop, ZoneCluster Entry)> chains, TimeSpan window)
    {
        // Only entries the row still holds sightings for. Sightings are bounded, and an evening
        // the row has forgotten can neither support nor refute it.
        var considered = chains
            .Where(chain => shape.Horizon is not { } horizon || chain.Entry.LoadStartedAtUtc > horizon)
            .ToArray();
        if (considered.Length < MinTimedEntries ||
            considered.Select(chain => chain.Pop.RouletteId).Distinct().Count() < MinTimedRoulettes)
        {
            return null;
        }

        var samples = new List<PopHit>(considered.Length);
        var lead = TimeSpan.MaxValue;
        foreach (var (request, entry) in considered)
        {
            // The whole queue, not just the match window at the end of it: a status the finder
            // ticks over every half minute has only a few arrivals in the last two minutes of an
            // hour-long queue, and hundreds in the queue itself.
            var queued = shape.Sightings
                .Where(sighting => sighting.AtUtc > request.AtUtc &&
                    sighting.AtUtc < entry.LoadStartedAtUtc)
                .OrderBy(sighting => sighting.AtUtc)
                .ToArray();
            if (queued.Length == 0 || queued.Length > MaxPerWindow)
            {
                return null;
            }

            var announced = queued.FirstOrDefault(sighting =>
                entry.LoadStartedAtUtc - sighting.AtUtc <= window);
            if (announced == default)
            {
                return null;
            }

            var ahead = entry.LoadStartedAtUtc - announced.AtUtc;
            if (ahead < lead)
            {
                lead = ahead;
            }

            // The roulette comes from the request, never from the announcement: nothing in the
            // announcement says which duty it is about.
            samples.Add(new PopHit(
                announced.ConnectionTag, shape.Opcode, request.RouletteId, announced.TMs, announced.AtUtc,
                false, Array.Empty<CalibrationSelectorReading>()));
        }

        return (lead, samples.OrderBy(sample => sample.AtUtc).ToArray());
    }

    /// <summary>
    /// The timeline lines for a timed announcement: one per duty entry it explained, named after
    /// the roulette the request asked for and marked as recognised by its timing, because that is
    /// the one thing about it the player can check against what they saw.
    /// </summary>
    /// <param name="timed">The announcement, or null.</param>
    /// <param name="roulettes">Roulette names.</param>
    /// <param name="region">Region, for those names.</param>
    private static IEnumerable<CalibrationEvent> TimedEvents(
        TimedAnnouncement? timed, RouletteCatalog roulettes, Region region)
    {
        if (timed is null)
        {
            yield break;
        }

        foreach (var sample in timed.Samples.OrderBy(sample => sample.AtUtc))
        {
            yield return new CalibrationEvent(
                Id("pop", sample.AtUtc), "pop", sample.TMs, sample.AtUtc,
                "匹配弹窗：" + roulettes.DisplayName((int)Math.Min(sample.RouletteId, int.MaxValue), region) +
                "（按出现时机认出）",
                sample.RouletteId, null, null, true);
        }
    }
}
