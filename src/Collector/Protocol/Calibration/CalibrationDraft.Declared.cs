using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The pop on a template whose queue reply and match announcement are two messages
/// (<see cref="CalibrationTemplate.SeparateReply"/>; GLOBAL 2026.11.03, docs/protocol-profile-format.md
/// section 11.3).
///
/// The reply is read by its own shape and only confirms the request: it echoes the roulette within the
/// reply window and is never the pop. The pop is the template's pop shape, sent as a message of its own
/// when the queue matches - after the request, before the duty's loading screen, carrying the queued
/// roulette. Because the two shapes are told apart by their structure, the reply window says nothing about
/// the pop, and a tank queue that matches a third of a second after the request is an ordinary match rather
/// than "the structure changed".
/// </summary>
public sealed partial record CalibrationDraft
{
    /// <summary>The pop one reply opcode's pairs authenticate, and whether that may corroborate the reply.</summary>
    /// <param name="State">The pop samples and the entry they lead to; <see cref="MatchedState.None"/> when none.</param>
    /// <param name="Corroborates">
    /// True when the reply opcode is the one that answered the request of a supporting chain most often.
    /// Any server message of the reply's shape that happens to echo the roulette inside the reply window pairs
    /// with the same request, and every pop after that request would otherwise vouch for it too - two reply
    /// opcodes, a blocked draft. The GLOBAL 2026.11.03 reply answers each request twice.
    /// </param>
    private sealed record DeclaredMatch(MatchedState State, bool Corroborates);

    /// <summary>
    /// The declared pop per reply opcode. Plugged into <see cref="LockPop"/> exactly as the one-message states
    /// are - through <see cref="DeclaredMatch.Corroborates"/> - a declared pop corroborates its reply opcode the
    /// way a "matched" state corroborates its own opcode, so one queued roulette with one match is enough.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="pairs">Request/echo pairs, already stripped of rejected lines.</param>
    /// <param name="matchWindow">Maximum pop-to-entry interval from the template.</param>
    /// <param name="rejections">Candidates the user, or the traffic, already rejected.</param>
    private static Dictionary<ushort, DeclaredMatch> DeclaredMatches(
        CalibrationSnapshot snapshot, FinderPairHit[] pairs, TimeSpan matchWindow, CalibrationRejections rejections)
    {
        var answerers = pairs
            .GroupBy(pair => (pair.ConnectionTag, pair.RequestTMs, pair.RequestOpcode))
            .ToDictionary(group => group.Key, group => TopAnswerer(group));
        return pairs.GroupBy(pair => pair.ReplyOpcode).ToDictionary(group => group.Key,
            group => DeclaredState(snapshot, group.Key, group.ToArray(), answerers, matchWindow, rejections));
    }

    /// <summary>The reply opcode with strictly the most pairs for one request, or null on a tie.</summary>
    /// <param name="answers">Pairs of one request.</param>
    private static ushort? TopAnswerer(IEnumerable<FinderPairHit> answers)
    {
        var counts = answers.GroupBy(pair => pair.ReplyOpcode)
            .Select(group => (Opcode: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ToArray();
        return counts.Length == 1 || counts[0].Count > counts[1].Count ? counts[0].Opcode : null;
    }

    /// <summary>
    /// The pop the pairs of one reply opcode authenticate.
    ///
    /// A sample (<see cref="CalibrationObserver"/> keeps only pop-shaped messages that carried the queued
    /// roulette while a confirmed queue stood) forms a chain when it is not the reply opcode itself, is outside
    /// every load, a pair of this reply opcode and its dominant request on the same connection asked for the
    /// same roulette before it, no known duty was entered in between, and exactly one known-duty entry -
    /// naming one territory - follows it within the match window. A sample with no entry behind it, or two,
    /// says nothing either way: the player declined the match, or left and re-entered quickly.
    ///
    /// An opcode is refused outright when, while a queue stood, it carried something other than the queued
    /// roulette at least <see cref="MinDisagreements"/> times and at least as often as the queued one
    /// (<see cref="PopTally"/>, <see cref="DisagreesWhileQueued"/>): a list counting through small numbers - the
    /// CN retainer bell's rows - has one row that agrees every time it is sent. Of the opcodes left, one that also
    /// travels inside a zone load is dropped only to break a tie, and then the one that preceded the most distinct
    /// duty entries; a tie after that names nothing, and the draft falls through to the searches that stand behind it.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="reply">The reply opcode the pairs share; never the pop.</param>
    /// <param name="pairs">Pairs of that reply opcode.</param>
    /// <param name="answerers">Per request, the reply opcode that answered it most often.</param>
    /// <param name="matchWindow">Maximum pop-to-entry interval from the template.</param>
    /// <param name="rejections">Candidates the user, or the traffic, already rejected.</param>
    private static DeclaredMatch DeclaredState(
        CalibrationSnapshot snapshot, ushort reply, FinderPairHit[] pairs,
        IReadOnlyDictionary<(string, long, ushort), ushort?> answerers, TimeSpan matchWindow,
        CalibrationRejections rejections)
    {
        var none = new DeclaredMatch(MatchedState.None, false);
        if (snapshot.Pops.Count == 0 || DominantRequest(pairs) is not { } dominant)
        {
            return none;
        }

        pairs = pairs.Where(pair => pair.RequestOpcode == dominant).ToArray();
        var clusters = snapshot.Clusters;
        var chains = new Dictionary<ushort, List<(PopHit Pop, ZoneCluster Entry, FinderPairHit Request)>>();
        foreach (var pop in snapshot.Pops
                     .Where(pop => pop.Opcode != reply && !rejections.PopOpcodes.Contains(pop.Opcode))
                     .Where(pop => !clusters.Any(cluster =>
                         pop.AtUtc >= cluster.LoadStartedAtUtc && pop.AtUtc <= cluster.EndedAtUtc)))
        {
            var request = pairs.Where(pair => pair.ConnectionTag == pop.ConnectionTag &&
                    pair.RouletteId == pop.RouletteId && RequestAt(pair) < pop.AtUtc && pair.ReplyAtUtc <= pop.AtUtc)
                .OrderByDescending(RequestAt).FirstOrDefault();
            if (request is null || clusters.Any(cluster => cluster.TerritoryHits.Count > 0 &&
                    cluster.LoadStartedAtUtc > RequestAt(request) && cluster.LoadStartedAtUtc <= pop.AtUtc))
            {
                continue;
            }

            var entries = clusters.Where(cluster => cluster.TerritoryHits.Count > 0 &&
                !rejections.Entries.Contains(cluster.LoadStartedAtUtc) &&
                cluster.LoadStartedAtUtc > pop.AtUtc &&
                cluster.LoadStartedAtUtc - pop.AtUtc <= matchWindow).ToArray();
            if (entries.Length != 1 || entries[0].TerritoryHits.Select(hit => hit.TerritoryId).Distinct().Count() != 1)
            {
                continue;
            }

            if (!chains.TryGetValue(pop.Opcode, out var list))
            {
                list = new List<(PopHit, ZoneCluster, FinderPairHit)>();
                chains.Add(pop.Opcode, list);
            }

            list.Add((pop, entries[0], request));
        }

        var candidates = chains.Keys.Where(opcode => !DisagreesWhileQueued(snapshot, opcode)).ToArray();
        if (candidates.Length > 1)
        {
            candidates = candidates
                .Where(opcode => !clusters.Any(cluster => cluster.Members.Keys.Any(key =>
                    key.Direction == PacketDirection.ServerToClient && key.Opcode == opcode)))
                .ToArray();
        }

        if (candidates.Length > 1)
        {
            // Duty entries, not samples: a status message repeated between the popup and the loading screen
            // would otherwise outvote the popup, which is sent once per match (review 2026-11-22, M2).
            int Entries(ushort opcode) => chains[opcode].Select(chain => chain.Entry).Distinct().Count();
            var most = candidates.Max(Entries);
            candidates = candidates.Where(opcode => Entries(opcode) == most).ToArray();
        }

        if (candidates.Length != 1)
        {
            return none;
        }

        var supported = chains[candidates[0]].OrderBy(chain => chain.Pop.AtUtc).ToArray();
        var state = new MatchedState(Array.Empty<CalibrationSelectorReading>(),
            supported.Select(chain => chain.Pop).ToArray(), false, false)
        {
            Entry = PreferredEntry(supported.Select(chain => chain.Entry), clusters),
        };
        var corroborates = supported.Any(chain =>
            answerers.TryGetValue((chain.Request.ConnectionTag, chain.Request.RequestTMs, chain.Request.RequestOpcode),
                out var answerer) && answerer == reply);
        return new DeclaredMatch(state, corroborates);
    }

    /// <summary>
    /// True when a pop-shaped opcode carried something other than the queued roulette at least
    /// <see cref="MinDisagreements"/> times while a queue stood, and at least as often as it carried the queued one
    /// - or when it was never tallied at all, which cannot happen to a sample the observer kept and is refused rather
    /// than read as agreement.
    ///
    /// The ratio is there because the traffic does not show a withdrawn queue: until a duty load or the hour ends it,
    /// a popup of a party leader's queue or of a duty queued directly counts against the queue the player withdrew.
    /// A list counting through small numbers disagrees nine times for every row that agrees and is refused as before;
    /// the popup that agreed on every match survives a stale sighting or two (review 2026-11-22, M1).
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="opcode">Candidate pop opcode.</param>
    internal static bool DisagreesWhileQueued(CalibrationSnapshot snapshot, ushort opcode) =>
        !snapshot.PopTallies.TryGetValue(opcode, out var tally) ||
        (tally.Disagreed >= MinDisagreements && tally.Disagreed >= tally.Agreed);

    /// <summary>True when a burst named a known duty through this shape.</summary>
    /// <param name="cluster">Burst to look in.</param>
    /// <param name="key">Shape that would carry the territory.</param>
    internal static bool NamesDuty(ZoneCluster cluster, MessageKey key) =>
        cluster.TerritoryHits.Any(hit => hit.Opcode == key.Opcode && hit.Length == key.Length);

    /// <summary>
    /// How many replies of the locked pairing read a job, or null when the reply cannot be declared the job
    /// message: no reply read one, a reply in the job-carrying state carried an impossible job, or the
    /// replies to one request read two different jobs (the job cannot change in a fraction of a second).
    /// </summary>
    /// <param name="pairs">Request/echo pairs.</param>
    /// <param name="reply">Locked reply opcode.</param>
    /// <param name="request">Dominant request opcode of that pairing.</param>
    private static int? ReplyJobSamples(IEnumerable<FinderPairHit> pairs, ushort reply, ushort request)
    {
        var locked = pairs.Where(pair => pair.ReplyOpcode == reply && pair.RequestOpcode == request).ToArray();
        if (locked.Any(pair => pair.JobViolation))
        {
            return null;
        }

        var read = locked.Where(pair => pair.JobId is not null).ToArray();
        var consistent = read
            .GroupBy(pair => (pair.ConnectionTag, pair.RequestTMs))
            .All(group => group.Select(pair => pair.JobId).Distinct().Count() == 1);
        return read.Length > 0 && consistent ? read.Length : null;
    }
}
