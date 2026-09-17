using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// A calibration somebody else declared - a share code rebuilt through this machine's template -
/// as the observer watches for it. Only the messages are kept; where they came from is the
/// caller's business, and <see cref="CandidateId"/> is typically the code's <c>code_sha256</c>.
/// </summary>
/// <param name="CandidateId">Stable identity of the candidate.</param>
/// <param name="Pop">The declared pop, exactly as the rebuilt profile would parse it.</param>
/// <param name="Zone">The declared zone-change marker.</param>
/// <param name="Territory">The declared territory message, when any.</param>
/// <param name="Job">The declared job message, when any.</param>
public sealed record DeclaredCandidate(
    string CandidateId,
    ProfileMessage Pop,
    ProfileMessage Zone,
    ProfileMessage? Territory = null,
    ProfileMessage? Job = null)
{
    /// <summary>Shape of the declared zone-change marker.</summary>
    public MessageKey ZoneKey => Key(Zone);

    /// <summary>Shape of the declared territory message, when any.</summary>
    public MessageKey? TerritoryKey => Territory is { } territory ? Key(territory) : null;

    /// <summary>Shape of the declared job message, when any.</summary>
    public MessageKey? JobKey => Job is { } job ? Key(job) : null;

    /// <summary>
    /// The candidate a set of rebuilt messages declares, or null when the pop or a
    /// fixed-length zone marker is missing.
    /// </summary>
    /// <param name="candidateId">Stable identity of the candidate.</param>
    /// <param name="messages">Messages from <see cref="CalibratedShape.Messages"/>.</param>
    public static DeclaredCandidate? From(string candidateId, IReadOnlyList<ProfileMessage> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateId);
        ArgumentNullException.ThrowIfNull(messages);
        ProfileMessage? Find(string name) =>
            messages.FirstOrDefault(message => string.Equals(message.Name, name, StringComparison.Ordinal));
        return Find(CalibratedShape.PopName) is { } pop && Find(CalibratedShape.ZoneName) is { ExpectedLength: not null } zone
            ? new DeclaredCandidate(candidateId, pop, zone, Find(CalibratedShape.TerritoryName), Find(CalibratedShape.JobName))
            : null;
    }

    private static MessageKey Key(ProfileMessage message) =>
        new(message.Direction, message.Opcode, message.ExpectedLength ?? -1);
}

/// <summary>One arrival of a candidate's declared pop.</summary>
/// <param name="AtUtc">Arrival time.</param>
/// <param name="ConnectionTag">Redacted connection tag.</param>
/// <param name="RouletteId">Roulette id at the declared offset.</param>
/// <param name="WithRequest">True when it carried the roulette the connection had queued and not yet loaded into.</param>
public readonly record struct CandidateSighting(DateTimeOffset AtUtc, string ConnectionTag, long RouletteId, bool WithRequest);

/// <summary>
/// What one capture session showed about one candidate, counted from the moment the candidate was
/// registered (or the session's first message after that) and never before.
/// </summary>
/// <param name="ObservedFromUtc">First message of the session the counts cover.</param>
/// <param name="PopWithRequest">Declared pops carrying the roulette the connection had queued and not yet loaded into.</param>
/// <param name="PopWithoutRequest">
/// Declared pops carrying a known roulette that was never requested on that connection - the thing
/// a true announcement never does. A pop whose queue was merely cleared by a load in between (a
/// teleport while queued) is neither: it is kept as a sighting and counted in neither total.
/// </param>
/// <param name="ZoneOutside">Declared zone markers that travelled outside every burst.</param>
/// <param name="TerritoryOutside">Declared territory messages that travelled outside every burst.</param>
/// <param name="Sightings">Pop sightings in arrival order, bounded.</param>
/// <param name="SightingsComplete">False when a sighting had to be dropped, so absence between two times cannot be claimed.</param>
public sealed record DeclaredCandidateCounts(
    DateTimeOffset ObservedFromUtc,
    int PopWithRequest,
    int PopWithoutRequest,
    int ZoneOutside,
    int TerritoryOutside,
    IReadOnlyList<CandidateSighting> Sightings,
    bool SightingsComplete);

/// <summary>Everything counted for one candidate, per capture session.</summary>
/// <param name="CandidateId">Identity of the candidate.</param>
/// <param name="Sessions">Counts keyed by capture session id; a session that is absent was not observed.</param>
public sealed record DeclaredCandidateObservation(string CandidateId, IReadOnlyDictionary<string, DeclaredCandidateCounts> Sessions);

/// <summary>
/// Bounded counters for declared candidates.
///
/// Registration is explicit rather than a generic per-shape table: whether a message is a
/// sighting depends on the candidate's own opcode, length, roulette offset and selector values,
/// so a generic table would have to scan every server message against every possible value to
/// answer "carried a known roulette nobody requested". Registration keeps the work at one
/// opcode comparison per candidate per message, the tables at
/// <see cref="CalibrationObserver.MaxCandidates"/> x <see cref="CalibrationObserver.MaxCandidateSessions"/>,
/// and matches the plan's rule that a candidate is watched from the moment it arrives (§4.1 step 3).
/// No payload byte is kept: a sighting is a time, a tag and the roulette id a profile would record.
/// </summary>
internal sealed class CandidateTallies
{
    private readonly List<Tally> _tallies = new();

    /// <summary>Candidates currently watched.</summary>
    public int Count => _tallies.Count;

    /// <summary>Starts watching a candidate; false when full or already watched.</summary>
    public bool Register(DeclaredCandidate candidate)
    {
        if (_tallies.Count >= CalibrationObserver.MaxCandidates ||
            _tallies.Any(tally => string.Equals(tally.Candidate.CandidateId, candidate.CandidateId, StringComparison.Ordinal)))
        {
            return false;
        }

        _tallies.Add(new Tally(candidate));
        return true;
    }

    /// <summary>Stops watching a candidate and forgets its counts.</summary>
    public bool Unregister(string candidateId) =>
        _tallies.RemoveAll(tally => string.Equals(tally.Candidate.CandidateId, candidateId, StringComparison.Ordinal)) > 0;

    /// <summary>Counts one accepted message against every candidate.</summary>
    /// <param name="sessionId">Capture session of the message.</param>
    /// <param name="connectionTag">Redacted connection tag.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="outstandingRoulette">Roulette the connection has queued and not yet loaded into, if any.</param>
    /// <param name="requested">Roulettes ever requested on the connection.</param>
    /// <param name="at">Arrival time.</param>
    /// <param name="roulettes">Roulette table, to tell a real roulette id from a byte that happens to be small.</param>
    /// <param name="region">Region of the running client.</param>
    public void Observe(
        string sessionId, string connectionTag, PacketDirection direction, ushort opcode, ReadOnlySpan<byte> payload,
        long? outstandingRoulette, IReadOnlySet<long> requested, DateTimeOffset at, RouletteCatalog roulettes, Region region)
    {
        foreach (var tally in _tallies)
        {
            if (tally.Session(sessionId, at) is not { } counts)
            {
                continue;
            }

            var pop = tally.Candidate.Pop;
            if (direction != PacketDirection.ServerToClient || pop.Direction != PacketDirection.ServerToClient ||
                opcode != pop.Opcode || !pop.AcceptsLength(payload.Length) || !Parses(pop, payload, out var roulette))
            {
                continue;
            }

            var withRequest = outstandingRoulette == roulette;
            var requestedHere = requested.Contains(roulette);
            if (withRequest)
            {
                counts.WithRequest++;
            }
            else if (!requestedHere && roulette is > 0 and <= int.MaxValue && roulettes.IsKnown((int)roulette, region))
            {
                counts.WithoutRequest++;
            }
            else if (!requestedHere)
            {
                continue;
            }

            if (counts.Sightings.Count < CalibrationObserver.MaxCandidateSightings)
            {
                counts.Sightings.Add(new CandidateSighting(at, connectionTag, roulette, withRequest));
            }
            else
            {
                counts.Complete = false;
            }
        }
    }

    /// <summary>Counts one message that aged out of the burst window without joining a burst.</summary>
    public void Outside(string sessionId, MessageKey key, DateTimeOffset at)
    {
        foreach (var tally in _tallies)
        {
            if (!tally.Sessions.TryGetValue(sessionId, out var counts) || at < counts.From)
            {
                continue;
            }

            if (key == tally.Candidate.ZoneKey)
            {
                counts.ZoneOutside++;
            }

            if (tally.Candidate.TerritoryKey is { } territory && key == territory)
            {
                counts.TerritoryOutside++;
            }
        }
    }

    /// <summary>A frozen copy of every candidate's counts.</summary>
    public IReadOnlyList<DeclaredCandidateObservation> Snapshot() => _tallies
        .Select(tally => new DeclaredCandidateObservation(
            tally.Candidate.CandidateId,
            tally.Sessions.ToDictionary(
                pair => pair.Key,
                pair => new DeclaredCandidateCounts(
                    pair.Value.From, pair.Value.WithRequest, pair.Value.WithoutRequest, pair.Value.ZoneOutside,
                    pair.Value.TerritoryOutside, pair.Value.Sightings.ToArray(), pair.Value.Complete),
                StringComparer.Ordinal)))
        .ToArray();

    /// <summary>True when the payload parses as the declared pop, the way the rebuilt profile's parser would.</summary>
    private static bool Parses(ProfileMessage pop, ReadOnlySpan<byte> payload, out long roulette)
    {
        roulette = 0;
        if (pop.Field("roulette_id") is not { } rouletteField || !FieldReader.TryReadSatisfied(payload, rouletteField, out roulette))
        {
            return false;
        }

        foreach (var field in pop.Fields)
        {
            if (field.Type != ProfileFieldType.Bytes && !ReferenceEquals(field, rouletteField) &&
                !FieldReader.TryReadSatisfied(payload, field, out _))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Tally
    {
        public Tally(DeclaredCandidate candidate)
        {
            Candidate = candidate;
        }

        public DeclaredCandidate Candidate { get; }

        public Dictionary<string, SessionTally> Sessions { get; } = new(StringComparer.Ordinal);

        /// <summary>The counts for a session, started at <paramref name="at"/>; null when the session table is full.</summary>
        public SessionTally? Session(string sessionId, DateTimeOffset at)
        {
            if (Sessions.TryGetValue(sessionId, out var counts))
            {
                return counts;
            }

            if (Sessions.Count >= CalibrationObserver.MaxCandidateSessions)
            {
                return null;
            }

            counts = new SessionTally(at);
            Sessions.Add(sessionId, counts);
            return counts;
        }
    }

    private sealed class SessionTally
    {
        public SessionTally(DateTimeOffset from)
        {
            From = from;
        }

        public DateTimeOffset From { get; }

        public int WithRequest { get; set; }

        public int WithoutRequest { get; set; }

        public int ZoneOutside { get; set; }

        public int TerritoryOutside { get; set; }

        public List<CandidateSighting> Sightings { get; } = new();

        public bool Complete { get; set; } = true;
    }
}
