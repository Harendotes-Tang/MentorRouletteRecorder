using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>Identity of one message shape as calibration counts it: direction, opcode, exact length.</summary>
/// <param name="Direction">Direction the message travelled.</param>
/// <param name="Opcode">IPC opcode.</param>
/// <param name="Length">Payload length.</param>
public readonly record struct MessageKey(PacketDirection Direction, ushort Opcode, int Length);

/// <summary>
/// One template-declared selector field of the pop, read off a message calibration is testing. The
/// roulette id is not one of these: it is the value the pairing already agreed on. These are
/// the fields that say <em>which</em> state the finder is in, and on a new build their values
/// are exactly what nobody knows yet.
/// </summary>
/// <param name="Field">Field name as the template declares it.</param>
/// <param name="Value">Value read at the template's offset.</param>
public readonly record struct CalibrationSelectorReading(string Field, long Value);

/// <summary>
/// A client request whose roulette id came back from the server within the template's echo
/// window. Two of these on the same opcode pair, or one plus a genuine pop, identify the
/// pop opcode on a build nobody has a profile for.
/// </summary>
/// <param name="ConnectionTag">Redacted connection tag both halves travelled on.</param>
/// <param name="RequestOpcode">Opcode of the client request.</param>
/// <param name="ReplyOpcode">Opcode of the server echo.</param>
/// <param name="RouletteId">Roulette id carried by both halves.</param>
/// <param name="RequestTMs">Session-relative time of the request.</param>
/// <param name="ReplyTMs">Session-relative time of the echo.</param>
/// <param name="ReplyAtUtc">Wall-clock time of the echo.</param>
public sealed record FinderPairHit(
    string ConnectionTag,
    ushort RequestOpcode,
    ushort ReplyOpcode,
    long RouletteId,
    long RequestTMs,
    long ReplyTMs,
    DateTimeOffset ReplyAtUtc);

/// <summary>
/// A server message shaped like the pop: the template's direction and length, carrying a
/// plausible roulette id at the template's offset. Whether it <em>is</em> a pop is not decided
/// here. The template's selector value means "matched" only on the build the template came
/// from, so the value is recorded rather than required, and the draft works out which value
/// means "matched" on this build from where the message sits in time.
/// </summary>
/// <param name="ConnectionTag">Redacted connection tag.</param>
/// <param name="Opcode">Opcode of the message.</param>
/// <param name="RouletteId">Roulette id it carried.</param>
/// <param name="TMs">Session-relative time.</param>
/// <param name="AtUtc">Wall-clock time.</param>
/// <param name="WithinEcho">True when a request on the same connection preceded it within the echo window.</param>
/// <param name="Selectors">Only the template's declared selector fields as this message carried them.</param>
public sealed record PopHit(
    string ConnectionTag,
    ushort Opcode,
    long RouletteId,
    long TMs,
    DateTimeOffset AtUtc,
    bool WithinEcho,
    IReadOnlyList<CalibrationSelectorReading> Selectors);

/// <summary>A server message of the territory shape whose territory id names a known duty.</summary>
/// <param name="Opcode">Opcode of the message.</param>
/// <param name="Length">Payload length.</param>
/// <param name="TerritoryId">Territory id read from the template's field.</param>
/// <param name="DutyName">Display name of the duty in that territory.</param>
public sealed record TerritoryHit(ushort Opcode, int Length, long TerritoryId, string DutyName);

/// <summary>
/// One server message that carried a roulette id the player asked for, after the echo window
/// closed. Only the shape and the time are kept; the id itself is not.
/// </summary>
/// <param name="ConnectionTag">Connection it arrived on.</param>
/// <param name="Opcode">Opcode of the message.</param>
/// <param name="Length">Payload length.</param>
/// <param name="RouletteId">The id it carried; the same class of value a pop keeps.</param>
/// <param name="TMs">Session-relative arrival time.</param>
/// <param name="AtUtc">Arrival time.</param>
public sealed record RouletteEchoHit(
    string ConnectionTag, ushort Opcode, int Length, long RouletteId, long TMs, DateTimeOffset AtUtc);

/// <summary>One sighting of a scanned position: when it happened and which queue was outstanding.</summary>
/// <param name="AtUtc">Wall-clock arrival time.</param>
/// <param name="RouletteId">Roulette the player had queued at that moment.</param>
/// <param name="ConnectionTag">Connection it arrived on.</param>
/// <param name="TMs">Session-relative arrival time.</param>
public readonly record struct MarkerSighting(
    DateTimeOffset AtUtc, long RouletteId, string ConnectionTag, long TMs);

/// <summary>
/// One position - opcode, payload length and byte offset - that was seen carrying the roulette
/// id of the queue the player had outstanding at that moment, outside the reply window and
/// outside any zone load.
///
/// The roulette-echo scan reads only the offset the template's pop declares. That is right
/// while a build merely renumbers its opcodes, and wrong for a build that splits the queue
/// reply and the match announcement into two messages: the new message is a new structure, and
/// nothing says its roulette id sits where the old one's did. This scan makes no assumption
/// about the offset and lets the evidence name it.
///
/// A single byte equal to a small id is nearly no evidence: a position hits by chance about
/// once in 256 messages. What is not chance is a position that carries whichever roulette the
/// player queued <em>this</em> time, every time it appears and never anything else. Two
/// different roulettes settle it, and neither costs the player a declined match.
/// </summary>
/// <param name="Opcode">Opcode of the message.</param>
/// <param name="Length">Payload length.</param>
/// <param name="Offset">Byte offset the id was read at.</param>
/// <param name="Hits">Times this position carried the outstanding queue's id.</param>
/// <param name="RouletteIds">Distinct ids it carried, bounded.</param>
/// <param name="ConnectionTag">Connection of the first sighting.</param>
/// <param name="ManyConnections">True when sightings came from more than one connection.</param>
/// <param name="Sightings">Individual sightings kept, bounded; enough to relate them to duty entries.</param>
/// <param name="SightingsComplete">True when no sighting had to be dropped.</param>
public sealed record MarkerCandidate(
    ushort Opcode,
    int Length,
    int Offset,
    int Hits,
    IReadOnlyList<long> RouletteIds,
    string ConnectionTag,
    bool ManyConnections,
    IReadOnlyList<MarkerSighting> Sightings,
    bool SightingsComplete)
{
    /// <summary>Shape this position belongs to.</summary>
    public MessageKey Shape => new(PacketDirection.ServerToClient, Opcode, Length);
}

/// <summary>
/// How the territory-shaped messages of one opcode read inside one burst, whether or not the
/// duty table knows them. <see cref="TerritoryHit"/> keeps only the known duties and does not
/// say which readings failed; the shared-calibration verifier needs both, per opcode. Counts
/// only, never the values.
/// </summary>
/// <param name="Opcode">Opcode of the territory-shaped messages.</param>
/// <param name="Valid">Readings that satisfied the template field's constraints.</param>
/// <param name="Invalid">Readings that did not.</param>
/// <param name="KnownDuty">Valid readings the duty table names.</param>
public sealed record TerritoryReading(ushort Opcode, int Valid, int Invalid, int KnownDuty);

/// <summary>
/// One zone-load burst: everything that travelled on the connection from the burst's start
/// until it closed, counted per shape. Payloads are never kept; only the template-shaped
/// values (territory, job) extracted at the moment of arrival.
/// </summary>
/// <param name="Index">Zero-based order of the cluster in the session.</param>
/// <param name="ConnectionTag">Redacted connection tag.</param>
/// <param name="StartTMs">Session-relative start.</param>
/// <param name="EndTMs">Session-relative time of the last member.</param>
/// <param name="StartedAtUtc">Wall-clock start.</param>
/// <param name="EndedAtUtc">Wall-clock time of the last member.</param>
/// <param name="Members">Occurrences per shape.</param>
/// <param name="TerritoryHits">Territory-shaped members naming a known duty.</param>
/// <param name="JobValues">Job-shaped members' values per opcode, in arrival order.</param>
/// <param name="OverflowCount">Shapes dropped because the per-cluster table was full.</param>
public sealed record ZoneCluster(
    int Index,
    string ConnectionTag,
    long StartTMs,
    long EndTMs,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    IReadOnlyDictionary<MessageKey, int> Members,
    IReadOnlyList<TerritoryHit> TerritoryHits,
    IReadOnlyDictionary<ushort, IReadOnlyList<long>> JobValues,
    int OverflowCount)
{
    /// <summary>First large server message establishing this load, excluding buffered pre-roll members.</summary>
    public DateTimeOffset LoadStartedAtUtc { get; init; } = StartedAtUtc;

    /// <summary>Session-relative time corresponding to <see cref="LoadStartedAtUtc"/>.</summary>
    public long LoadStartTMs { get; init; } = StartTMs;

    /// <summary>Territory-shaped members per opcode, by how they read. Empty in evidence written before it existed.</summary>
    public IReadOnlyList<TerritoryReading> TerritoryReadings { get; init; } = Array.Empty<TerritoryReading>();

    /// <summary>
    /// Whether the burst is on the lobby connection. Logging in opens a burst on the lobby
    /// connection, which never carries the game server's messages, so shared-calibration
    /// criteria that expect a message in "every burst" exclude it.
    ///
    /// Nothing on the wire names the lobby, so the flag is conservative: true until the burst's
    /// connection shows it is the game connection - a second burst, a roulette request, or a
    /// burst naming a known duty - and false from then on. Errors only ever run the safe way: a
    /// lobby burst is never counted as a game burst. Null in evidence written before the flag
    /// existed, which means "not known" and is never treated as a game burst either.
    /// </summary>
    public bool? Lobby { get; init; }

    /// <summary>
    /// Job-shaped members per opcode whose value broke the template's constraints inside this
    /// burst. A burst with one of these contradicts the shape; the session-wide count on the
    /// snapshot is only for the report.
    /// </summary>
    public IReadOnlyDictionary<ushort, int> JobViolations { get; init; } = NoJobViolations;

    private static readonly IReadOnlyDictionary<ushort, int> NoJobViolations = new Dictionary<ushort, int>();
}

/// <summary>
/// Everything the observer has seen, frozen. <see cref="CalibrationDraft.Derive"/> is a pure
/// function over this, so the same snapshot always yields the same draft.
/// </summary>
/// <param name="CaptureSessionId">Session the observations belong to.</param>
/// <param name="Pairs">Request/echo pairs.</param>
/// <param name="Pops">Pop-shaped messages that satisfied the template.</param>
/// <param name="Clusters">Closed zone-load bursts, in order.</param>
/// <param name="OutsideCounts">Occurrences per shape outside every cluster.</param>
/// <param name="OpcodeCounts">Occurrences per direction and opcode over the whole session.</param>
/// <param name="JobViolations">Job-shaped messages per opcode whose value broke the template's constraints.</param>
/// <param name="OverflowCount">Anything dropped because a bounded table was full; non-zero voids every "never" claim.</param>
/// <param name="MessagesSeen">Messages accepted.</param>
/// <param name="SessionCount">Capture sessions the evidence spans.</param>
public sealed record CalibrationSnapshot(
    string CaptureSessionId,
    IReadOnlyList<FinderPairHit> Pairs,
    IReadOnlyList<PopHit> Pops,
    IReadOnlyList<ZoneCluster> Clusters,
    IReadOnlyDictionary<MessageKey, int> OutsideCounts,
    IReadOnlyDictionary<(PacketDirection Direction, ushort Opcode), int> OpcodeCounts,
    IReadOnlyDictionary<ushort, int> JobViolations,
    int OverflowCount,
    int MessagesSeen,
    int SessionCount = 1)
{
    /// <summary>Server messages that had the pop's direction and the template's exact length.</summary>
    public int PopShapeSeen { get; init; }

    /// <summary>
    /// The most recent constraint-satisfying job reading per opcode, in or out of a burst. It
    /// seeds the state machine when a local or shared profile is bound mid-session, so the first record
    /// is not job-less until the next zone change. Not persisted: a restart re-observes it at login.
    /// </summary>
    public IReadOnlyDictionary<ushort, long> LatestJobValues { get; init; } = new Dictionary<ushort, long>();

    /// <summary>
    /// Of those, the first template field that refused them, per opcode. Without the near
    /// misses, "the popup was on screen and the software says it never saw one" is the same
    /// evidence as "nothing arrived".
    /// </summary>
    public IReadOnlyDictionary<(ushort Opcode, string Field), int> PopRefusals { get; init; } =
        new Dictionary<(ushort, string), int>();

    /// <summary>
    /// Lengths carried by opcodes a request/echo pair already identified, so a pop that simply
    /// grew or shrank with the patch shows up as a length rather than as silence. Scoped to
    /// paired opcodes: unscoped, ordinary traffic fills the table on any busy client.
    /// </summary>
    public IReadOnlyDictionary<(ushort Opcode, int Length), int> FinderLengths { get; init; } =
        new Dictionary<(ushort, int), int>();

    /// <summary>
    /// Shapes of any length that carried a requested roulette id at the template's roulette
    /// offset after the echo window closed. Only the shape and the count are kept.
    /// </summary>
    public IReadOnlyDictionary<(ushort Opcode, int Length), int> RouletteEchoes { get; init; } =
        new Dictionary<(ushort, int), int>();

    /// <summary>Those same hits with their arrival time, so they can be related to duty entries.</summary>
    public IReadOnlyList<RouletteEchoHit> RouletteEchoHits { get; init; } = Array.Empty<RouletteEchoHit>();

    /// <summary>
    /// Values the template's selector fields carried on those same paired opcodes. Values are
    /// small integers of a field the template names; they are what separates "the state number
    /// changed" from "the field moved" (docs/privacy-boundary.md §5.2).
    /// </summary>
    public IReadOnlyDictionary<(ushort Opcode, string Field, long Value), int> FinderSelectors { get; init; } =
        new Dictionary<(ushort, string, long), int>();

    /// <summary>
    /// Positions - opcode, length, byte offset - that carried the outstanding queue's roulette
    /// id, wherever in the payload it sat.
    /// </summary>
    public IReadOnlyList<MarkerCandidate> Markers { get; init; } = Array.Empty<MarkerCandidate>();

    /// <summary>
    /// How many messages of each shape the marker scan looked at. A position that hit on every
    /// single one of them is describing the message; one that hit on a few of many is noise.
    /// </summary>
    public IReadOnlyDictionary<(ushort Opcode, int Length), int> MarkerShapeTotals { get; init; } =
        new Dictionary<(ushort, int), int>();

    /// <summary>
    /// Positions dropped because the marker table was full. Unlike <see cref="OverflowCount"/>
    /// this only weakens the marker scan, so it is reported and never blocks anything.
    /// </summary>
    public int MarkerOverflow { get; init; }

    /// <summary>
    /// What each server shape does in time: only while a queue stands, before a duty, or on its
    /// own. This is the one table that can name the message announcing a match on a build whose
    /// announcement carries no roulette id at all.
    /// </summary>
    public IReadOnlyList<TimedShape> TimedShapes { get; init; } = Array.Empty<TimedShape>();

    /// <summary>
    /// Shapes the timing rule has retired. Kept so a shape that strayed once cannot come back to
    /// life on its next sighting, which matters because the evidence outlives the session.
    /// </summary>
    public IReadOnlyCollection<(ushort Opcode, int Length)> TimedDead { get; init; } =
        Array.Empty<(ushort, int)>();

    /// <summary>
    /// Anything the timing tables could not keep. Unlike <see cref="MarkerOverflow"/> this is not
    /// merely a weaker scan: the timing rule's claim is "this shape appeared before every entry",
    /// and a shape that never got a row cannot support or refute it, so any overflow means no
    /// timed candidate is named at all.
    /// </summary>
    public int TimingOverflow { get; init; }

    /// <summary>First message the observer accepted, or null when it has accepted none.</summary>
    public DateTimeOffset? FirstMessageAtUtc { get; init; }

    /// <summary>Last message the observer accepted, or null when it has accepted none.</summary>
    public DateTimeOffset? LastMessageAtUtc { get; init; }

    /// <summary>
    /// Messages this observer inherited from an earlier run of the Collector. Zero means it
    /// started from nothing, which is the difference between "the player has not played yet"
    /// and "the evidence on disk was not picked up".
    /// </summary>
    public int CarriedMessages { get; init; }

    /// <summary>
    /// Anything dropped from a table that only feeds the report. Unlike
    /// <see cref="OverflowCount"/> this never voids a claim and never blocks a draft: a full
    /// diagnostic table must not be able to stop calibration working.
    /// </summary>
    public int DiagnosticsOverflow { get; init; }

    /// <summary>
    /// Capture session of every connection this observer saw live, keyed by connection tag. A
    /// burst, pair or pop whose tag is not here was carried over from an earlier run.
    /// Not persisted.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConnectionSessions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Capture health per session, as the capture controller reported it. Not persisted.</summary>
    public IReadOnlyDictionary<string, CaptureSessionHealth> SessionHealth { get; init; } =
        new Dictionary<string, CaptureSessionHealth>(StringComparer.Ordinal);

    /// <summary>Counts for every declared candidate being watched. Not persisted.</summary>
    public IReadOnlyList<DeclaredCandidateObservation> Candidates { get; init; } = Array.Empty<DeclaredCandidateObservation>();
}

/// <summary>
/// Opcode-level summary of the evidence for the diagnostics report and the log: enough to see
/// why a draft is stuck without a payload byte or a timeline in it.
/// </summary>
/// <param name="Sessions">Capture sessions the evidence spans.</param>
/// <param name="MessagesSeen">Messages accepted.</param>
/// <param name="Pairs">Request/echo pairs per opcode pair, as "0xreq->0xreply=count".</param>
/// <param name="Pops">Pop-shaped hits per opcode, as "0xop=total/withinEcho".</param>
/// <param name="Clusters">Zone-load bursts closed.</param>
/// <param name="ZoneCandidates">Shapes of the template's zone-marker length that appear once per burst and never outside.</param>
/// <param name="OutsideKeys">Distinct shapes seen outside bursts.</param>
/// <param name="Overflow">Bounded-table overflow count.</param>
/// <param name="PopShapes">Server messages that had the pop's direction and the template's length.</param>
/// <param name="PopRefusals">Which template field turned those away, per opcode.</param>
/// <param name="FinderLengths">Lengths carried by opcodes a pair vouched for.</param>
/// <param name="FinderStates">State values carried by those same opcodes.</param>
/// <param name="RouletteEchoes">Shapes of any length carrying a requested roulette id after the echo window.</param>
/// <param name="MatchEchoes">Of those, how many duty entries each shape preceded inside the match window.</param>
/// <param name="ZoneOnceOnly">Shapes of any length that appear once per burst and never outside.</param>
/// <param name="DiagnosticsOverflow">Dropped from a report-only table; never blocks anything.</param>
/// <param name="DutyZones">Bursts carrying a territory the duty table recognises, whatever the draft made of them.</param>
/// <param name="TerritoryCandidates">Shapes of the template's territory length that fit the burst/outside tolerance.</param>
/// <param name="Markers">Positions that carried the queued roulette, as "0xop:len@off=hits/total x ids".</param>
/// <param name="MarkerOverflow">Positions the marker table could not take; weakens that scan only.</param>
/// <param name="Carried">Messages inherited from an earlier run of the Collector; 0 means a fresh start.</param>
/// <param name="WatchedSeconds">Seconds between the first and last message the observer accepted.</param>
/// <param name="QuietSeconds">Seconds since the last one, so a deaf observer is visible as such.</param>
/// <param name="ZoneOutside">Template-length shapes a burst holds that were also seen outside one.</param>
/// <param name="ZoneShapes">Per shape at that length, as "0xop:len=marked/bursts+outside".</param>
/// <param name="ClustersAt">How many seconds ago each zone load started, newest last.</param>
/// <param name="PairsAt">How many seconds ago each request/echo pair happened, newest last.</param>
/// <param name="TimedCandidates">
/// Per surviving timed shape, as "0xop:len=inqueue+preduty/total!stray e(duty entries it preceded)
/// /(duty entries)  lead(smallest lead)s": what each shape does in time, which is the only thing
/// that can name the announcement on a build whose announcement carries no roulette id at all.
/// </param>
/// <param name="TimingOverflow">Dropped from a timing table; any of it means no timed candidate is named.</param>
/// <param name="JobShapes">
/// Per job-shaped opcode, as "0xop=vouching bursts/bursts!contradicting bursts v values": the job rule
/// wants exactly one opcode the entry and exit bursts vouch for, and without this row a report cannot
/// tell "none qualifies" from "two tie" from "one was contradicted".
/// </param>
public sealed record CalibrationEvidenceSummary(
    int Sessions,
    int MessagesSeen,
    IReadOnlyList<string> Pairs,
    IReadOnlyList<string> Pops,
    int Clusters,
    int ZoneCandidates,
    int OutsideKeys,
    int Overflow,
    int PopShapes,
    IReadOnlyList<string> PopRefusals,
    IReadOnlyList<string> FinderLengths,
    IReadOnlyList<string> FinderStates,
    IReadOnlyList<string> ZoneOnceOnly,
    int DiagnosticsOverflow,
    IReadOnlyList<string>? RouletteEchoes = null,
    IReadOnlyList<string>? MatchEchoes = null,
    int DutyZones = 0,
    int TerritoryCandidates = 0,
    IReadOnlyList<string>? Markers = null,
    int MarkerOverflow = 0,
    int Carried = 0,
    long WatchedSeconds = 0,
    long QuietSeconds = -1,
    int ZoneOutside = 0,
    IReadOnlyList<string>? ZoneShapes = null,
    IReadOnlyList<long>? ClustersAt = null,
    IReadOnlyList<long>? PairsAt = null,
    IReadOnlyList<string>? JobShapes = null,
    IReadOnlyList<string>? TimedCandidates = null,
    int TimingOverflow = 0)
{
    /// <summary>Whole seconds since this shape last carried a requested roulette id.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="key">Shape to look up.</param>
    /// <param name="asOf">Report time.</param>
    private static long Ago(CalibrationSnapshot snapshot, (ushort Opcode, int Length) key, DateTimeOffset asOf) =>
        snapshot.RouletteEchoHits
            .Where(hit => hit.Opcode == key.Opcode && hit.Length == key.Length)
            .Select(hit => (long)(asOf - hit.AtUtc).TotalSeconds)
            .DefaultIfEmpty(-1)
            .Min();

    /// <summary>How long a hit came before a burst's load, or null when it came after it.</summary>
    /// <param name="hit">Timed roulette-echo hit.</param>
    /// <param name="entry">Burst that named a duty.</param>
    private static TimeSpan? Gap(RouletteEchoHit hit, ZoneCluster entry) =>
        hit.AtUtc < entry.LoadStartedAtUtc ? entry.LoadStartedAtUtc - hit.AtUtc : null;

    /// <summary>Builds the summary for a snapshot.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the zone-marker length.</param>
    /// <param name="now">Clock for the "how long ago" column; the caller's own report time.</param>
    public static CalibrationEvidenceSummary From(
        CalibrationSnapshot snapshot, CalibrationTemplate template, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(template);
        var asOf = now ?? DateTimeOffset.UtcNow;
        var pairs = snapshot.Pairs
            .GroupBy(pair => (pair.RequestOpcode, pair.ReplyOpcode))
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => $"0x{group.Key.RequestOpcode:x4}->0x{group.Key.ReplyOpcode:x4}={group.Count()}")
            .ToArray();
        var pops = snapshot.Pops
            .GroupBy(pop => pop.Opcode)
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => $"0x{group.Key:x4}={group.Count()}/{group.Count(pop => pop.WithinEcho)}")
            .ToArray();
        // Near misses separate a state value that changed, a message that changed length, a
        // field that moved, and traffic that never reached the parser at all.
        var refusals = snapshot.PopRefusals
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"0x{entry.Key.Opcode:x4}.{entry.Key.Field}={entry.Value}")
            .ToArray();
        var lengths = snapshot.FinderLengths
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}={entry.Value}")
            .ToArray();
        // Ordered by how much of the shape's own traffic the scan matched: the message that
        // says "matched" carries a roulette id every time it is sent, while an unrelated shape
        // lands on one only by accident.
        var echoes = snapshot.RouletteEchoes
            .Select(entry => (
                entry.Key,
                Hits: entry.Value,
                Total: snapshot.OpcodeCounts.GetValueOrDefault((PacketDirection.ServerToClient, entry.Key.Opcode))))
            .OrderByDescending(entry => entry.Total > 0 ? (double)entry.Hits / entry.Total : 0)
            .ThenBy(entry => entry.Total)
            .Take(8)
            // How long ago the shape last did this, so a player can queue, refuse the match and
            // export at once: the shape whose last hit matches that moment is the announcement,
            // and no duty has to be played to find it.
            .Select(entry => $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}={entry.Hits}/{entry.Total}" +
                $"~{Ago(snapshot, entry.Key, asOf)}s")
            .ToArray();
        // The scan above cannot tell a match announcement from an accident: a roulette id is a
        // number between 1 and 17, so roughly one server message in a hundred carries one at the
        // right offset by chance. Time separates them - the announcement arrives in the couple
        // of minutes before the player enters the duty, and does so every time - so each shape
        // is scored as "duty entries it preceded" over "duty entries there were".
        var dutyEntries = snapshot.Clusters.Where(cluster => cluster.TerritoryHits.Count > 0).ToArray();
        var matchEchoes = dutyEntries.Length == 0
            ? Array.Empty<string>()
            : snapshot.RouletteEchoHits
                .Where(hit => !snapshot.Clusters.Any(cluster =>
                    hit.AtUtc >= cluster.LoadStartedAtUtc && hit.AtUtc <= cluster.EndedAtUtc))
                .GroupBy(hit => (hit.Opcode, hit.Length))
                .Select(group => (
                    group.Key,
                    Preceded: dutyEntries.Count(entry => group.Any(hit =>
                        Gap(hit, entry) is { } gap && gap <= template.MatchWindow)),
                    // A shape that fires when no duty follows does not need one: the announcement
                    // is sent whether the player accepts or refuses, while traffic that merely
                    // accompanies a load cannot exist without the load.
                    Loose: group.Count(hit => !dutyEntries.Any(entry =>
                        Gap(hit, entry) is { } gap && gap <= template.MatchWindow)),
                    // How long before the load. The announcement waits out the player's decision
                    // and then the loading screen, so it sits tens of seconds out; whatever the
                    // client sends as the load itself begins sits at zero.
                    Gaps: group
                        .Select(hit => dutyEntries
                            .Select(entry => Gap(hit, entry))
                            .Where(gap => gap is { } value && value <= template.MatchWindow)
                            .OrderBy(gap => gap)
                            .FirstOrDefault())
                        .Where(gap => gap is not null)
                        .Select(gap => (int)gap!.Value.TotalSeconds)
                        .OrderBy(seconds => seconds)
                        .Take(4)
                        .ToArray()))
                .Where(entry => entry.Preceded > 0)
                .OrderByDescending(entry => entry.Preceded)
                .ThenBy(entry => snapshot.OpcodeCounts.GetValueOrDefault(
                    (PacketDirection.ServerToClient, entry.Key.Opcode)))
                .Take(8)
                .Select(entry =>
                    $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}={entry.Preceded}/{dutyEntries.Length}" +
                    $"+{entry.Loose}@{string.Join(";", entry.Gaps)}s")
                .ToArray();
        // The one table that can name the announcement without a duty being played and without
        // a match being declined: every (opcode, length, offset) position that held the roulette
        // the player had queued at the time. Ordered so the answer floats to the top - a
        // position that tracked two different roulettes and hit on every message of its shape is
        // the announcement; one that hit on a handful of hundreds is a byte that happens to be
        // small. "x1" is shown only so a stuck report can name the near misses.
        var markers = snapshot.Markers
            .Select(candidate => (
                candidate,
                Total: snapshot.MarkerShapeTotals.GetValueOrDefault((candidate.Opcode, candidate.Length))))
            .Where(entry => entry.Total > 0)
            .OrderByDescending(entry => entry.candidate.RouletteIds.Count)
            .ThenByDescending(entry => entry.Total > 0 ? (double)entry.candidate.Hits / entry.Total : 0)
            .ThenBy(entry => entry.Total)
            .Take(8)
            .Select(entry => $"0x{entry.candidate.Opcode:x4}:{entry.candidate.Length}@{entry.candidate.Offset}" +
                $"={entry.candidate.Hits}/{entry.Total}x{entry.candidate.RouletteIds.Count}")
            .ToArray();
        // Counts alone cannot say whether the player's evening is missing from the evidence or
        // never reached it. These four do: how long the observer has been listening, how long it
        // has been silent, and when the events it did see actually happened.
        var watched = snapshot.FirstMessageAtUtc is { } first && snapshot.LastMessageAtUtc is { } last
            ? (long)(last - first).TotalSeconds
            : 0L;
        var quiet = snapshot.LastMessageAtUtc is { } latest ? (long)(asOf - latest).TotalSeconds : -1L;
        var clustersAt = snapshot.Clusters
            .OrderBy(cluster => cluster.LoadStartedAtUtc)
            .Select(cluster => (long)(asOf - cluster.LoadStartedAtUtc).TotalSeconds)
            .Take(16)
            .ToArray();
        var pairsAt = snapshot.Pairs
            .OrderBy(pair => pair.ReplyAtUtc)
            .Select(pair => (long)(asOf - pair.ReplyAtUtc).TotalSeconds)
            .Take(16)
            .ToArray();
        var states = snapshot.FinderSelectors
            .OrderByDescending(entry => entry.Value)
            .Take(12)
            .Select(entry => $"0x{entry.Key.Opcode:x4}.{entry.Key.Field}={entry.Key.Value}x{entry.Value}")
            .ToArray();
        // Visit each member once. Scanning every cluster again for every distinct shape makes
        // a full bounded snapshot unnecessarily expensive while the capture lock is held.
        // Insert even a non-single occurrence so ties retain their original encounter order.
        var onceCounts = new Dictionary<MessageKey, int>();
        foreach (var cluster in snapshot.Clusters)
        {
            foreach (var (key, occurrences) in cluster.Members)
            {
                if (key.Direction != PacketDirection.ServerToClient || snapshot.OutsideCounts.ContainsKey(key))
                {
                    continue;
                }

                onceCounts.TryGetValue(key, out var count);
                onceCounts[key] = count + (occurrences == 1 ? 1 : 0);
            }
        }

        var onceOnly = onceCounts
            .Select(entry => (Key: entry.Key, Bursts: entry.Value))
            .Where(entry => entry.Bursts >= 1)
            .OrderByDescending(entry => entry.Bursts)
            .ThenBy(entry => entry.Key.Length)
            .Take(8)
            .Select(entry => $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}@{entry.Bursts}")
            .ToArray();
        var zoneLength = template.ZoneInitialization.ExpectedLength ?? -1;
        // Counted by the rule the draft applies, over however many bursts there are, so the
        // number says "no shape fits" rather than "not enough bursts yet"; the draft applies
        // its own minimum.
        var zoneCandidates = zoneLength < 0
            ? 0
            : CalibrationDraft.ZoneShapes(snapshot, zoneLength).Count(key => CalibrationDraft.Behaves(snapshot, key));
        // The difference between "this build moved the zone message" and "this capture lost the
        // packets that would have proved it did not". Both read as zone_candidates = 0.
        var zoneOutside = zoneLength < 0
            ? 0
            : CalibrationDraft.ContaminatedShapes(snapshot, zoneLength).Count();
        // Why zone_candidates reads as it does: "marked" is the bursts the shape appears in
        // exactly once, "bursts" is how many there were, "outside" is how often it travelled
        // on its own - a real marker is high, high, low.
        var zoneShapes = zoneLength < 0
            ? Array.Empty<string>()
            : CalibrationDraft.ZoneShapes(snapshot, zoneLength)
                .Select(key => (
                    Key: key,
                    Marked: CalibrationDraft.ExactlyOnceCount(snapshot.Clusters, key),
                    Outside: CalibrationDraft.Outside(snapshot, key)))
                .OrderByDescending(entry => entry.Marked)
                .ThenBy(entry => entry.Outside)
                .Take(8)
                .Select(entry =>
                    $"0x{entry.Key.Opcode:x4}:{entry.Key.Length}={entry.Marked}/{snapshot.Clusters.Count}" +
                    $"+{entry.Outside}")
                .ToArray();
        // The same count for the territory shape, and the bursts that actually named a duty.
        // Without these two a report cannot separate "the player has not been in a duty yet"
        // from "this build moved the duty messages and no amount of playing will help": both
        // arrive as duty_entry_seen = false.
        var territoryLength = template.ZoneTerritory?.ExpectedLength ?? -1;
        var territoryCandidates = territoryLength < 0
            ? 0
            : CalibrationDraft.ZoneShapes(snapshot, territoryLength).Count(key => CalibrationDraft.Behaves(snapshot, key));
        var dutyZones = snapshot.Clusters.Count(cluster => cluster.TerritoryHits.Count > 0);
        // Every opcode a burst read at the job message's shape. The values are ClassJob ids, the
        // same number every record already stores; up to four are shown because the row exists to
        // tell the true job message (the player's job) from a neighbour that carries a small constant.
        var jobShapes = template.PlayerJob is { ExpectedLength: { } jobLength } jobTemplate
            ? snapshot.Clusters
                .SelectMany(cluster => cluster.JobValues.Keys.Concat(cluster.JobViolations.Keys))
                .Distinct()
                .Select(opcode => (
                    Opcode: opcode,
                    Vouching: snapshot.Clusters.Count(cluster =>
                        CalibrationDraft.VouchesForJob(cluster, opcode, jobTemplate.Direction, jobLength)),
                    Contradicting: snapshot.Clusters.Count(cluster => CalibrationDraft.ContradictsJob(cluster, opcode)),
                    Values: snapshot.Clusters
                        .SelectMany(cluster => cluster.JobValues.TryGetValue(opcode, out var values) ? values : Array.Empty<long>())
                        .Distinct().OrderBy(value => value).Take(4).ToArray()))
                .OrderByDescending(row => row.Vouching).ThenBy(row => row.Contradicting).ThenBy(row => row.Opcode)
                .Take(8)
                .Select(row => $"0x{row.Opcode:x4}={row.Vouching}/{snapshot.Clusters.Count}!{row.Contradicting} v" +
                    string.Join(";", row.Values))
                .ToArray()
            : Array.Empty<string>();
        return new CalibrationEvidenceSummary(
            snapshot.SessionCount,
            snapshot.MessagesSeen,
            pairs,
            pops,
            snapshot.Clusters.Count,
            zoneCandidates,
            snapshot.OutsideCounts.Count,
            snapshot.OverflowCount,
            snapshot.PopShapeSeen,
            refusals,
            lengths,
            states,
            onceOnly,
            snapshot.DiagnosticsOverflow,
            echoes,
            matchEchoes,
            dutyZones,
            territoryCandidates,
            markers,
            snapshot.MarkerOverflow,
            snapshot.CarriedMessages,
            watched,
            quiet,
            zoneOutside,
            zoneShapes,
            clustersAt,
            pairsAt,
            jobShapes,
            TimedShape.Report(snapshot, template.MatchWindow),
            snapshot.TimingOverflow);
    }
}
