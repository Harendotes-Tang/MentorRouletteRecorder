using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Watches decoded traffic on a build that has no profile and collects the evidence a
/// <see cref="CalibrationDraft"/> needs: request/echo pairs, pop-shaped messages, zone-load
/// bursts and what travels inside and outside them. It never keeps a payload. It evaluates
/// template shapes at the moment a message arrives and remembers only the id-type values
/// those shapes yield (roulette, territory, job), the same values a formal profile would
/// write into a record.
///
/// Every table is bounded. When a bound is hit the count is remembered in
/// <see cref="CalibrationSnapshot.OverflowCount"/>, and the draft treats any overflow as a
/// reason to refuse the "never seen outside a burst" claim rather than as a smaller sample.
///
/// This is deliberately not <see cref="Parsing.CandidateObserver"/>: that one is keyed by
/// opcodes a CANDIDATE profile already names, and a new build names none.
/// </summary>
public sealed partial class CalibrationObserver
{
    /// <summary>Payload length from which a server message counts as large.</summary>
    public const int LargeBytes = 256;

    /// <summary>Payload length from which a server message anchors a zone-load burst.</summary>
    public const int AnchorBytes = 2000;

    /// <summary>Distinct large server shapes needed inside <see cref="Window"/> to open a burst.</summary>
    public const int MinDistinctLarge = 5;

    /// <summary>Connections tracked at once.</summary>
    public const int MaxConnections = 64;

    /// <summary>Bursts kept per session.</summary>
    public const int MaxClusters = 64;

    /// <summary>Shapes counted per burst.</summary>
    public const int MaxClusterKeys = 512;

    /// <summary>Shapes counted outside bursts.</summary>
    public const int MaxOutsideKeys = 4096;

    /// <summary>Messages held per connection while waiting to see whether a burst opens.</summary>
    public const int MaxRingEntries = 4096;

    /// <summary>Pairs kept per session.</summary>
    public const int MaxPairs = 256;

    /// <summary>Pops kept per session.</summary>
    public const int MaxPops = 256;

    /// <summary>Unanswered requests remembered per connection.</summary>
    public const int MaxPendingRequests = 64;

    /// <summary>Direction/opcode pairs counted per session.</summary>
    public const int MaxOpcodeCounts = 4096;

    /// <summary>Server opcodes a request/echo pair vouched for; the diagnostic tables follow these.</summary>
    public const int MaxReplyOpcodes = 16;

    /// <summary>Field refusals counted per session.</summary>
    public const int MaxPopRefusals = 32;

    /// <summary>Shapes counted by the roulette-echo scan.</summary>
    public const int MaxRouletteEchoes = 64;

    /// <summary>Roulette ids remembered per connection for that scan.</summary>
    public const int MaxRequestedRoulettes = 32;

    /// <summary>Timed hits kept by the roulette-echo scan, so they can be related to duty entries.</summary>
    public const int MaxEchoHits = 512;

    /// <summary>
    /// Timed hits kept per shape. The scan asks how many duty entries a shape preceded, and a
    /// shape that carries a roulette id hundreds of times is answering "by accident" however many
    /// of them are kept. Holding a few per shape leaves every rare shape - which is what the scan
    /// is for - intact, and stops one chatty shape from spending the whole table.
    /// </summary>
    public const int MaxEchoHitsPerShape = 8;

    /// <summary>Positions - opcode, length, offset - the marker scan tracks at once.</summary>
    public const int MaxMarkerKeys = 8192;

    /// <summary>Shapes the marker scan counts occurrences for.</summary>
    public const int MaxMarkerShapes = 1024;

    /// <summary>Distinct roulette ids remembered per position.</summary>
    public const int MaxMarkerIds = 4;

    /// <summary>Sighting times kept per position.</summary>
    public const int MaxMarkerTimes = 8;

    /// <summary>Length keys counted across all vouched opcodes in the diagnostic table.</summary>
    public const int MaxFinderLengths = 64;

    /// <summary>Selector-value keys counted across all vouched opcodes in the diagnostic table.</summary>
    public const int MaxFinderSelectors = 64;

    /// <summary>
    /// Pop-shaped samples kept per connection, opcode, roulette, echo side and selector value.
    /// Repeated known reply states can be compressed; dropping another timed candidate makes
    /// the evidence incomplete and blocks derivation.
    /// </summary>
    public const int MaxPopsPerBucket = 4;

    /// <summary>Declared candidates watched at once; a build is verified against at most eight codes.</summary>
    public const int MaxCandidates = 8;

    /// <summary>Capture sessions each declared candidate keeps counts for.</summary>
    public const int MaxCandidateSessions = 16;

    /// <summary>Timed pop sightings each declared candidate keeps per session.</summary>
    public const int MaxCandidateSightings = 32;

    /// <summary>Capture sessions whose health is remembered.</summary>
    public const int MaxSessionHealth = 64;

    /// <summary>Window in which the opening condition of a burst must be met.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    /// <summary>Required silence unless a repeated zone/territory signature corroborates the burst.</summary>
    public static readonly TimeSpan PreQuiet = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest gap between consecutive large server messages still counted as one load. A zone
    /// load's messages arrive milliseconds apart; a populated map's ordinary large messages are
    /// seconds apart. This only decides where the reported load starts, never whether a burst
    /// opens, so a load with an unusually slow middle is reported slightly late rather than lost.
    /// </summary>
    public static readonly TimeSpan LoadGap = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How far before the first large message a burst reaches back for members. It matches
    /// <see cref="Window"/> on purpose: the ring ages entries out at exactly that age and counts
    /// them as having travelled outside a burst, so anything shorter declares part of the zone
    /// load to be "outside the load" - and a single occurrence outside is enough to rule a shape
    /// out as the zone marker for good.
    /// </summary>
    public static readonly TimeSpan PreRoll = Window;

    /// <summary>A burst closes after this long without a new large shape.</summary>
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(2);

    /// <summary>A burst never lasts longer than this.</summary>
    public static readonly TimeSpan MaxClusterDuration = TimeSpan.FromSeconds(15);

    private readonly CalibrationTemplate _template;
    private readonly Region _region;
    private readonly string _sessionId;
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
    private readonly DutyCatalog _duties;
    private readonly RouletteCatalog _roulettes;
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly List<FinderPairHit> _pairs = new();
    private readonly List<PopHit> _pops = new();
    private readonly List<ZoneCluster> _clusters = new();
    private readonly Dictionary<MessageKey, int> _outside = new();
    private readonly Dictionary<(PacketDirection, ushort), int> _opcodeCounts = new();
    private readonly Dictionary<ushort, int> _jobViolations = new();
    private readonly Dictionary<ushort, long> _latestJobValues = new();
    private readonly HashSet<ushort> _replyOpcodes = new();
    private readonly HashSet<(ushort Reply, ushort Request)> _pairedOpcodes = new();
    private readonly Dictionary<(ushort, string), int> _popRefusals = new();
    private readonly Dictionary<(ushort, int), int> _rouletteEchoes = new();
    private readonly List<RouletteEchoHit> _echoHits = new();
    private readonly Dictionary<(ushort, int), int> _finderLengths = new();
    private readonly Dictionary<(ushort, string, long), int> _finderSelectors = new();
    private readonly Dictionary<(ushort Opcode, int Length, int Offset), MarkerStat> _markers = new();
    private readonly Dictionary<(ushort Opcode, int Length), int> _markerShapes = new();
    private readonly Dictionary<string, CaptureSessionHealth> _health = new(StringComparer.Ordinal);
    private readonly CandidateTallies _candidates = new();
    private int _markerOverflow;
    private int _adoptedSessions;
    private int _adoptedMessages;
    private DateTimeOffset? _firstAt;
    private DateTimeOffset? _lastAt;
    private int _popShapeSeen;
    private int _diagnosticsOverflow;
    private int _overflow;
    private int _seen;

    /// <summary>Creates an observer for one capture session.</summary>
    /// <param name="template">Template lending the shapes.</param>
    /// <param name="region">Region of the running client, for the duty and roulette tables.</param>
    /// <param name="captureSessionId">Session whose messages are accepted; others are ignored.</param>
    /// <param name="duties">Duty table used to recognise a territory id.</param>
    /// <param name="roulettes">Roulette table used to reject implausible roulette ids.</param>
    public CalibrationObserver(
        CalibrationTemplate template,
        Region region,
        string captureSessionId,
        DutyCatalog? duties = null,
        RouletteCatalog? roulettes = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);
        _template = template;
        _region = region;
        _sessionId = captureSessionId;
        _sessions.Add(captureSessionId);
        _duties = duties ?? DutyCatalog.Default;
        _roulettes = roulettes ?? RouletteCatalog.Default;
    }

    /// <summary>Feeds one decoded message. Messages from other sessions are ignored.</summary>
    /// <param name="message">Decoded message; its payload is read now and never kept.</param>
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_sessions.Contains(message.CaptureSessionId) || message.Mono < TimeSpan.Zero)
        {
            return;
        }

        _seen++;
        // When the evidence was collected, not just how much of it there is: "one queue, one
        // zone load" cannot be read without knowing whether the observer watched for twenty
        // minutes or went deaf after two.
        _firstAt ??= message.ObservedAtUtc;
        _lastAt = message.ObservedAtUtc;
        var t = (long)message.Mono.TotalMilliseconds;
        var direction = message.Direction == MessageDirection.Inbound
            ? PacketDirection.ServerToClient
            : PacketDirection.ClientToServer;
        var payload = message.Payload.Span;
        var key = new MessageKey(direction, message.Opcode, payload.Length);

        Count(_opcodeCounts, (direction, message.Opcode), MaxOpcodeCounts);

        if (!TryGetConnection(message.CaptureSessionId, message.ConnectionKey, out var connection))
        {
            return;
        }

        var entry = new RingEntry(t, message.ObservedAtUtc, key, Territory(direction, payload, message.Opcode),
            Job(direction, payload, message.Opcode, out var jobViolation), jobViolation, TerritoryReadingOf(direction, payload));
        if (entry.JobValue is { } latestJob &&
            (_latestJobValues.Count < MaxOpcodeCounts || _latestJobValues.ContainsKey(message.Opcode)))
        {
            _latestJobValues[message.Opcode] = latestJob;
        }

        TrackClusters(connection, entry);
        TrackFinder(connection, direction, payload, message.Opcode, t, message.ObservedAtUtc);
        // After the pairing, so an opcode a request/echo pair has just vouched for is already
        // excluded, and after the bursts, so nothing that travelled inside a load is timed.
        TrackTiming(connection, direction, payload.Length, message.Opcode, t, message.ObservedAtUtc);
        if (_candidates.Count > 0)
        {
            _candidates.Observe(connection.SessionId, connection.Tag, direction, message.Opcode, payload,
                connection.Outstanding?.RouletteId, connection.RequestedRoulettes, message.ObservedAtUtc, _roulettes, _region);
        }
    }

    /// <summary>Closes every open burst and counts everything still waiting as outside.</summary>
    public void Flush()
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.Open is not null)
            {
                CloseCluster(connection);
            }

            AgeRing(connection, long.MaxValue);
        }
    }

    /// <summary>A frozen copy of everything seen so far. Open bursts are not included.</summary>
    public CalibrationSnapshot Snapshot() => new(
        _sessionId,
        _pairs.ToArray(),
        _pops.ToArray(),
        _clusters.ToArray(),
        new Dictionary<MessageKey, int>(_outside),
        new Dictionary<(PacketDirection, ushort), int>(_opcodeCounts),
        new Dictionary<ushort, int>(_jobViolations),
        _overflow,
        _seen,
        _sessions.Count + _adoptedSessions)
    {
        PopShapeSeen = _popShapeSeen,
        LatestJobValues = new Dictionary<ushort, long>(_latestJobValues),
        PopRefusals = new Dictionary<(ushort, string), int>(_popRefusals),
        RouletteEchoes = new Dictionary<(ushort, int), int>(_rouletteEchoes),
        RouletteEchoHits = _echoHits.ToArray(),
        FinderLengths = new Dictionary<(ushort, int), int>(_finderLengths),
        FinderSelectors = new Dictionary<(ushort, string, long), int>(_finderSelectors),
        Markers = _markers
            .Select(entry => new MarkerCandidate(
                entry.Key.Opcode,
                entry.Key.Length,
                entry.Key.Offset,
                entry.Value.Hits,
                entry.Value.Ids.ToArray(),
                entry.Value.ConnectionTag,
                entry.Value.ManyConnections,
                entry.Value.Sightings.ToArray(),
                entry.Value.Hits <= MaxMarkerTimes))
            .ToArray(),
        MarkerShapeTotals = new Dictionary<(ushort, int), int>(_markerShapes),
        MarkerOverflow = _markerOverflow,
        FirstMessageAtUtc = _firstAt,
        LastMessageAtUtc = _lastAt,
        CarriedMessages = _adoptedMessages,
        DiagnosticsOverflow = _diagnosticsOverflow,
        ConnectionSessions = _connections.Values.ToDictionary(
            connection => connection.Tag, connection => connection.SessionId, StringComparer.Ordinal),
        SessionHealth = new Dictionary<string, CaptureSessionHealth>(_health, StringComparer.Ordinal),
        Candidates = _candidates.Snapshot(),
        TimedShapes = TimedShapes(),
        TimedDead = _timingDead.ToArray(),
        TimingOverflow = _timingOverflow,
    };

    private static string Tag(string sessionId, string connectionKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "|" + connectionKey)))[..12]
            .ToLowerInvariant();

    /// <summary>
    /// Keeps the evidence when a new capture session starts on the same build: a player who
    /// re-logs on patch day should not have to start the roulette over. Connections are
    /// tagged per session, so nothing from one session pairs with another by accident.
    /// </summary>
    /// <param name="captureSessionId">Session whose messages are accepted from now on.</param>
    public void AdoptSession(string captureSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);
        _sessions.Add(captureSessionId);
    }

    /// <summary>
    /// Records how healthy a capture session's capture was.
    /// Readings of one session merge to the worst of them. Returns false for a session this
    /// observer does not accept, or when the health table is full; either way nothing is kept and
    /// the session simply counts as not healthy.
    /// </summary>
    /// <param name="health">Reading from the capture controller.</param>
    public bool RecordSessionHealth(CaptureSessionHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (!_sessions.Contains(health.CaptureSessionId))
        {
            return false;
        }

        if (_health.TryGetValue(health.CaptureSessionId, out var known))
        {
            _health[health.CaptureSessionId] = known.Merge(health);
            return true;
        }

        if (_health.Count >= MaxSessionHealth)
        {
            return false;
        }

        _health.Add(health.CaptureSessionId, health);
        return true;
    }

    /// <summary>
    /// Starts counting a declared candidate from the next message on. False when
    /// <see cref="MaxCandidates"/> are already watched or this one already is.
    /// </summary>
    /// <param name="candidate">Candidate rebuilt from a share code.</param>
    public bool RegisterCandidate(DeclaredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return _candidates.Register(candidate);
    }

    /// <summary>Stops counting a candidate and drops its counts.</summary>
    /// <param name="candidateId">Identity it was registered under.</param>
    public bool UnregisterCandidate(string candidateId)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateId);
        return _candidates.Unregister(candidateId);
    }

    /// <summary>
    /// Takes on the evidence of an earlier run of the Collector, so a player who installed a new
    /// version - or simply restarted the software - does not start the evening over.
    ///
    /// Everything adopted is historical: closed bursts, pairs, pops and counts, all stamped with
    /// wall-clock times that the draft already relates across capture sessions. Nothing about a
    /// live connection is adopted, because none of it survives a process: the connection tags in
    /// the old evidence belong to sessions that ended, which is exactly how tags from two
    /// sessions of one run already behave.
    ///
    /// Call before the first message. Adopting into an observer that has already seen traffic
    /// would double-count it, so that is refused.
    /// </summary>
    /// <param name="carried">Evidence read back from disk.</param>
    public void AdoptEvidence(CalibrationSnapshot carried)
    {
        ArgumentNullException.ThrowIfNull(carried);
        if (_seen > 0 || _clusters.Count > 0 || _pairs.Count > 0)
        {
            throw new InvalidOperationException("calibration evidence can only be adopted before observing");
        }

        _pairs.AddRange(carried.Pairs);
        _pops.AddRange(carried.Pops);
        // Indices are positional and the restored bursts come first, so they keep theirs and
        // anything closed from here on continues the sequence.
        _clusters.AddRange(carried.Clusters);
        Merge(_outside, carried.OutsideCounts);
        Merge(_opcodeCounts, carried.OpcodeCounts);
        Merge(_jobViolations, carried.JobViolations);
        Merge(_popRefusals, carried.PopRefusals);
        Merge(_finderLengths, carried.FinderLengths);
        Merge(_rouletteEchoes, carried.RouletteEchoes);
        Merge(_finderSelectors, carried.FinderSelectors);
        Merge(_markerShapes, carried.MarkerShapeTotals);
        _echoHits.AddRange(carried.RouletteEchoHits);
        AdoptTiming(carried);
        foreach (var marker in carried.Markers)
        {
            _markers[(marker.Opcode, marker.Length, marker.Offset)] = MarkerStat.Restored(marker);
        }

        foreach (var pair in carried.Pairs)
        {
            if (_replyOpcodes.Count < MaxReplyOpcodes)
            {
                _replyOpcodes.Add(pair.ReplyOpcode);
            }

            if (_pairedOpcodes.Count < MaxReplyOpcodes * 2)
            {
                _pairedOpcodes.Add((pair.ReplyOpcode, pair.RequestOpcode));
            }
        }

        _overflow += carried.OverflowCount;
        _seen = carried.MessagesSeen;
        _popShapeSeen = carried.PopShapeSeen;
        _markerOverflow += carried.MarkerOverflow;
        _diagnosticsOverflow += carried.DiagnosticsOverflow;
        _adoptedSessions = Math.Max(0, carried.SessionCount);
        _adoptedMessages = carried.MessagesSeen;
        _firstAt = carried.FirstMessageAtUtc;
        _lastAt = carried.LastMessageAtUtc;
    }

    private static void Merge<TKey>(Dictionary<TKey, int> table, IReadOnlyDictionary<TKey, int> carried)
        where TKey : notnull
    {
        foreach (var (key, count) in carried)
        {
            table[key] = table.TryGetValue(key, out var existing) ? existing + count : count;
        }
    }

    /// <summary>Sessions whose messages have been accepted.</summary>
    public int SessionCount => _sessions.Count;

    private bool TryGetConnection(string sessionId, string connectionKey, out Connection connection)
    {
        var tag = Tag(sessionId, connectionKey);
        if (_connections.TryGetValue(tag, out var existing))
        {
            connection = existing;
            return true;
        }

        if (_connections.Count >= MaxConnections)
        {
            _overflow++;
            connection = null!;
            return false;
        }

        connection = new Connection(tag, sessionId);
        _connections.Add(tag, connection);
        return true;
    }

    private void Count<TKey>(Dictionary<TKey, int> table, TKey key, int capacity)
        where TKey : notnull
    {
        if (table.TryGetValue(key, out var count))
        {
            table[key] = count + 1;
        }
        else if (table.Count < capacity)
        {
            table[key] = 1;
        }
        else
        {
            _overflow++;
        }
    }

    /// <summary>
    /// Counts into a table that only feeds the report. A full table here is a smaller sample,
    /// never a reason to distrust a claim, so it is deliberately kept away from
    /// <see cref="_overflow"/>: a diagnostic must not be able to block a calibration.
    /// </summary>
    private void CountDiagnostic<TKey>(Dictionary<TKey, int> table, TKey key, int capacity)
        where TKey : notnull
    {
        if (table.TryGetValue(key, out var count))
        {
            table[key] = count + 1;
        }
        else if (table.Count < capacity)
        {
            table[key] = 1;
        }
        else
        {
            _diagnosticsOverflow++;
        }
    }

    private TerritoryHit? Territory(PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode)
    {
        if (_template.ZoneTerritory is not { } territory || direction != territory.Direction ||
            !territory.AcceptsLength(payload.Length) || territory.Field("territory_id") is not { } field ||
            !FieldReader.TryReadSatisfied(payload, field, out var value) || value > int.MaxValue ||
            _duties.FindByTerritory((int)value, _region) is not { } duty)
        {
            return null;
        }

        return new TerritoryHit(opcode, payload.Length, value, duty.LocalizedName);
    }

    /// <summary>
    /// Reads the template's territory field off any territory-shaped message, known duty or not.
    /// Only whether the value satisfied the constraints and whether the duty table knows it is
    /// kept; the value itself is not.
    /// </summary>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    private TerritoryFlags? TerritoryReadingOf(PacketDirection direction, ReadOnlySpan<byte> payload)
    {
        if (_template.ZoneTerritory is not { } territory || direction != territory.Direction ||
            !territory.AcceptsLength(payload.Length) || territory.Field("territory_id") is not { } field ||
            !FieldReader.TryRead(payload, field, out var value))
        {
            return null;
        }

        var valid = field.Constraints.IsSatisfiedBy(value);
        return new TerritoryFlags(valid,
            valid && value is >= 0 and <= int.MaxValue && _duties.FindByTerritory((int)value, _region) is not null);
    }

    private long? Job(PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, out bool violation)
    {
        violation = false;
        if (_template.PlayerJob is not { } job || direction != job.Direction || !job.AcceptsLength(payload.Length) ||
            job.Field("job_id") is not { } field || !FieldReader.TryRead(payload, field, out var value))
        {
            return null;
        }

        if (field.Constraints.IsSatisfiedBy(value))
        {
            return value;
        }

        violation = true;
        Count(_jobViolations, opcode, MaxOpcodeCounts);
        return null;
    }

    // ------------------------------------------------------------------ zone-load bursts

    private void TrackClusters(Connection connection, RingEntry entry)
    {
        var t = entry.TMs;
        if (connection.Open is { } open)
        {
            if (t - open.StartTMs > (long)MaxClusterDuration.TotalMilliseconds ||
                t - open.LastExtendTMs > (long)IdleClose.TotalMilliseconds)
            {
                CloseCluster(connection);
            }
            else
            {
                AddMember(open, entry);
                return;
            }
        }

        AgeRing(connection, t - (long)Window.TotalMilliseconds);
        if (connection.Ring.Count >= MaxRingEntries)
        {
            var oldest = connection.Ring[0];
            connection.Ring.RemoveAt(0);
            Count(_outside, oldest.Key, MaxOutsideKeys);
            _candidates.Outside(connection.SessionId, oldest.Key, oldest.AtUtc);
            _overflow++;
        }

        connection.Ring.Add(entry);
        if (IsAnchor(entry.Key))
        {
            connection.AnchorTimes.Add(t);
            var horizon = t - (long)(PreQuiet + Window).TotalMilliseconds - 1000;
            connection.AnchorTimes.RemoveAll(time => time < horizon);
        }

        // The opening condition can only become true when a large server message arrives;
        // everything else merely ages the window, so the ring scan is skipped for it.
        if (IsLarge(entry.Key))
        {
            TryOpenCluster(connection, t);
        }
    }

    private static bool IsLarge(MessageKey key) =>
        key.Direction == PacketDirection.ServerToClient && key.Length >= LargeBytes;

    /// <summary>A server message large enough to help establish a load; ordinary traffic can also be this large.</summary>
    /// <param name="key">Shape to classify.</param>
    private static bool IsAnchor(MessageKey key) =>
        key.Direction == PacketDirection.ServerToClient && key.Length >= AnchorBytes;

    private void TryOpenCluster(Connection connection, long now)
    {
        var windowStart = now - (long)Window.TotalMilliseconds;
        var kinds = new HashSet<(ushort, int)>();
        var anchored = false;
        long? run = null;
        var previous = 0L;
        foreach (var entry in connection.Ring)
        {
            if (entry.TMs < windowStart || !IsLarge(entry.Key))
            {
                continue;
            }

            kinds.Add((entry.Key.Opcode, entry.Key.Length));
            anchored |= IsAnchor(entry.Key);
            if (run is null || entry.TMs - previous > (long)LoadGap.TotalMilliseconds)
            {
                run = entry.TMs;
            }

            previous = entry.TMs;
        }

        if (kinds.Count < MinDistinctLarge || !anchored || run is not { } first)
        {
            return;
        }

        // Size alone does not distinguish loading from background traffic - ordinary messages
        // of anchor size also travel outside loads - so requiring silence of all of them can
        // hide the exit even when its marker and territory both arrive. The quiet rule stands
        // for unknown shapes, but the same unique marker/territory pair seen once in two earlier
        // loads (including a known duty) corroborates this one. This still needs the full
        // large-message burst and never selects a profile by itself.
        var quietFrom = windowStart - (long)PreQuiet.TotalMilliseconds;
        if (connection.AnchorTimes.Any(time => time >= quietFrom && time < windowStart) &&
            !HasRepeatedZoneSignature(connection, windowStart))
        {
            return;
        }

        var start = Math.Max(0, first - (long)PreRoll.TotalMilliseconds);
        var firstLarge = connection.Ring.First(entry => entry.TMs == first && IsLarge(entry.Key));
        var open = new OpenCluster(connection.Tag, start, now)
        {
            LoadStartedAtUtc = firstLarge.AtUtc,
            LoadStartTMs = firstLarge.TMs,
        };
        var kept = new List<RingEntry>();
        foreach (var entry in connection.Ring)
        {
            if (entry.TMs >= start)
            {
                if (open.StartedAtUtc == default)
                {
                    open.StartedAtUtc = entry.AtUtc;
                }

                AddMember(open, entry);
            }
            else
            {
                kept.Add(entry);
            }
        }

        connection.Ring.Clear();
        connection.Ring.AddRange(kept);
        connection.Open = open;
        // A match is announced before the loading screen, never during or after it. Forgetting
        // the queue as the load opens keeps every message the player receives while inside a
        // duty out of the marker scan, which is most of a session's traffic.
        connection.Outstanding = null;
    }

    private bool HasRepeatedZoneSignature(Connection connection, long windowStart)
    {
        if (_overflow != 0 || _template.ZoneTerritory is not { ExpectedLength: { } territoryLength } ||
            _template.ZoneInitialization.ExpectedLength is not { } markerLength)
        {
            return false;
        }

        var current = connection.Ring.Where(entry => entry.TMs >= windowStart).ToArray();
        if (!current.Any(entry => entry.Key.Direction == PacketDirection.ServerToClient &&
                                  entry.Key.Length == markerLength))
        {
            return false;
        }

        var prior = _clusters.Where(cluster => cluster.ConnectionTag == connection.Tag).ToArray();
        if (prior.Length < 2)
        {
            return false;
        }

        bool Once(ZoneCluster cluster, MessageKey key) => cluster.Members.GetValueOrDefault(key) == 1;
        bool Confined(MessageKey key)
        {
            var outside = _outside.GetValueOrDefault(key);
            return (outside <= CalibrationDraft.MaxZoneOutside ||
                    (long)outside * CalibrationDraft.ZoneOutsidePerBurst <= prior.Count(cluster => Once(cluster, key))) &&
                !prior.Any(cluster => cluster.Members.GetValueOrDefault(key) > 1);
        }

        var markers = prior.SelectMany(cluster => cluster.Members.Keys).Distinct()
            .Where(key => key.Direction == PacketDirection.ServerToClient && key.Length == markerLength)
            .Where(Confined).ToArray();
        var territories = prior.SelectMany(cluster => cluster.TerritoryHits)
            .Where(hit => hit.Length == territoryLength)
            .Select(hit => new MessageKey(PacketDirection.ServerToClient, hit.Opcode, hit.Length))
            .Distinct().Where(Confined).ToArray();
        var signatures = (from marker in markers
                          from territory in territories
                          where prior.Count(cluster => Once(cluster, marker) && Once(cluster, territory)) >= 2
                          where prior.Any(cluster => Once(cluster, marker) && Once(cluster, territory) &&
                              cluster.TerritoryHits.Any(hit => hit.Opcode == territory.Opcode && hit.Length == territory.Length))
                          select (Marker: marker, Territory: territory)).Take(2).ToArray();

        // Resolve ambiguity from the earlier loads, not from whichever candidate happens to
        // arrive first in this window. The other candidate could still be on its way.
        return signatures.Length == 1 &&
            current.Count(entry => entry.Key == signatures[0].Marker) == 1 &&
            current.Count(entry => entry.Key == signatures[0].Territory) == 1;
    }

    private void AddMember(OpenCluster open, RingEntry entry)
    {
        open.EndTMs = entry.TMs;
        open.EndedAtUtc = entry.AtUtc;
        if (open.Members.TryGetValue(entry.Key, out var count))
        {
            open.Members[entry.Key] = count + 1;
        }
        else if (open.Members.Count < MaxClusterKeys)
        {
            open.Members[entry.Key] = 1;
        }
        else
        {
            open.OverflowCount++;
            _overflow++;
        }

        if (IsLarge(entry.Key) && open.LargeKinds.Add((entry.Key.Opcode, entry.Key.Length)))
        {
            open.LastExtendTMs = entry.TMs;
        }

        if (entry.Territory is { } territory)
        {
            // This load is going into a duty the duty table knows, which is what a sighting with
            // no queue request behind it has been waiting for.
            ResolveTimedPending(open.LoadStartedAtUtc);
            if (open.TerritoryHits.Count < MaxClusterKeys)
            {
                open.TerritoryHits.Add(territory);
            }
            else
            {
                open.OverflowCount++;
                _overflow++;
            }
        }

        if (entry.TerritoryRead is { } read)
        {
            if (open.TerritoryReadings.TryGetValue(entry.Key.Opcode, out var tally) ||
                open.TerritoryReadings.Count < MaxClusterKeys)
            {
                open.TerritoryReadings[entry.Key.Opcode] = (
                    tally.Valid + (read.Valid ? 1 : 0),
                    tally.Invalid + (read.Valid ? 0 : 1),
                    tally.Known + (read.Known ? 1 : 0));
            }
            else
            {
                open.OverflowCount++;
                _overflow++;
            }
        }

        if (entry.JobValue is { } job)
        {
            if (!open.JobValues.TryGetValue(entry.Key.Opcode, out var values))
            {
                values = new List<long>();
                open.JobValues[entry.Key.Opcode] = values;
            }

            if (values.Count < MaxClusterKeys)
            {
                values.Add(job);
            }
            else
            {
                open.OverflowCount++;
                _overflow++;
            }
        }

        if (entry.JobViolation)
        {
            Count(open.JobViolations, entry.Key.Opcode, MaxClusterKeys);
        }
    }

    private void CloseCluster(Connection connection)
    {
        var open = connection.Open!;
        connection.Open = null;
        connection.AnchorTimes.Clear();
        connection.Clusters++;
        // The lobby opens one burst at login and never queues or enters a duty; the game
        // connection does all three. Only positive evidence moves a connection out of "lobby".
        if (connection.Clusters >= 2 || open.TerritoryHits.Count > 0)
        {
            ProveGameConnection(connection);
        }

        if (_clusters.Count >= MaxClusters)
        {
            _overflow++;
            return;
        }

        _clusters.Add(new ZoneCluster(
            _clusters.Count,
            open.ConnectionTag,
            open.StartTMs,
            open.EndTMs,
            open.StartedAtUtc,
            open.EndedAtUtc,
            new Dictionary<MessageKey, int>(open.Members),
            open.TerritoryHits.ToArray(),
            open.JobValues.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value.ToArray()),
            open.OverflowCount)
        {
            LoadStartedAtUtc = open.LoadStartedAtUtc,
            LoadStartTMs = open.LoadStartTMs,
            TerritoryReadings = open.TerritoryReadings
                .OrderBy(pair => pair.Key)
                .Select(pair => new TerritoryReading(pair.Key, pair.Value.Valid, pair.Value.Invalid, pair.Value.Known))
                .ToArray(),
            Lobby = !connection.Game,
            JobViolations = new Dictionary<ushort, int>(open.JobViolations),
        });
    }

    /// <summary>
    /// Marks a connection as the game connection and clears the lobby flag of every burst it
    /// already closed. Bursts are immutable records, so they are replaced; a draft only compares
    /// bursts within one snapshot, so no reader is holding the old instances against new ones.
    /// </summary>
    /// <param name="connection">Connection that just showed game traffic.</param>
    private void ProveGameConnection(Connection connection)
    {
        if (connection.Game)
        {
            return;
        }

        connection.Game = true;
        for (var index = 0; index < _clusters.Count; index++)
        {
            if (_clusters[index].Lobby == true &&
                string.Equals(_clusters[index].ConnectionTag, connection.Tag, StringComparison.Ordinal))
            {
                _clusters[index] = _clusters[index] with { Lobby = false };
            }
        }
    }

    private void AgeRing(Connection connection, long before)
    {
        var index = 0;
        while (index < connection.Ring.Count && connection.Ring[index].TMs < before)
        {
            Count(_outside, connection.Ring[index].Key, MaxOutsideKeys);
            _candidates.Outside(connection.SessionId, connection.Ring[index].Key, connection.Ring[index].AtUtc);
            index++;
        }

        if (index > 0)
        {
            connection.Ring.RemoveRange(0, index);
        }
    }

    // ------------------------------------------------------------------ finder pairs and pops

    private void TrackFinder(
        Connection connection, PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, long t, DateTimeOffset at)
    {
        // Recent requests only matter inside the echo window, so they are trimmed on every
        // message and capped like every other table; nothing here may grow with the session.
        var echoMax = (long)_template.Calibration.FinderReplyMax.TotalMilliseconds;
        connection.RecentRequests.RemoveAll(pending => t - pending.TMs > echoMax);
        // Nothing outside the echo window can answer anything, and a request is no longer
        // removed when it is answered, so the window is what keeps this queue bounded.
        while (connection.Requests.Count > 0 && t - connection.Requests.Peek().TMs > echoMax)
        {
            connection.Requests.Dequeue();
        }
        if (connection.RecentRequests.Count >= MaxPendingRequests)
        {
            connection.RecentRequests.RemoveAt(0);
            _overflow++;
        }

        var request = _template.Calibration.FinderRequest;
        if (direction == request.Direction && payload.Length == request.ExpectedLength &&
            FieldReader.TryReadSatisfied(payload, request.RouletteField, out var requested) &&
            requested is > 0 and <= int.MaxValue && _roulettes.IsKnown((int)requested, _region))
        {
            if (connection.Requests.Count >= MaxPendingRequests)
            {
                connection.Requests.Dequeue();
            }

            connection.Requests.Enqueue(new PendingRequest(t, opcode, requested));
            connection.RecentRequests.Add(new PendingRequest(t, opcode, requested));
            connection.Outstanding = new PendingRequest(t, opcode, requested);
            ProveGameConnection(connection);
            if (connection.RequestedRoulettes.Count < MaxRequestedRoulettes)
            {
                connection.RequestedRoulettes.Add(requested);
            }
        }

        TrackRouletteEcho(connection, direction, payload, opcode, t, at, echoMax);
        TrackMarkers(connection, direction, payload, opcode, t, at, echoMax);

        var pop = _template.Pop;
        if (direction != pop.Direction || !pop.AcceptsLength(payload.Length))
        {
            // An opcode a pair already vouched for that no longer carries the template's length
            // is the single most useful thing a stuck calibration can say, so its lengths are
            // counted even though such a message cannot be a pop under the template's shape.
            if (direction == pop.Direction && _replyOpcodes.Contains(opcode))
            {
                CountDiagnostic(_finderLengths, (opcode, payload.Length), MaxFinderLengths);
            }

            return;
        }

        _popShapeSeen++;
        if (_replyOpcodes.Contains(opcode))
        {
            CountDiagnostic(_finderLengths, (opcode, payload.Length), MaxFinderLengths);
        }

        if (pop.Field("roulette_id") is not { } rouletteField ||
            !FieldReader.TryRead(payload, rouletteField, out var echoed))
        {
            CountDiagnostic(_popRefusals, (opcode, "roulette_id"), MaxPopRefusals);
            return;
        }

        // "Within an echo window" is about the request this message could be answering, so
        // only requests carrying the same roulette id count, whether or not one already paired.
        var withinEcho = connection.RecentRequests.Any(pending =>
            pending.RouletteId == echoed && t - pending.TMs >= 0 && t - pending.TMs <= echoMax);
        // Which pending request this echo answers.
        //
        // The 2026-09-01 client answers one request with TWO messages on the same opcode, so a
        // matched request must not be consumed: the second message would then pair with whatever
        // else the client sent in the same second carrying the same small number, and because
        // the evidence is kept on disk that one coincidence would poison every later session.
        // The echo window is what bounds the queue instead, and an opcode pair already seen wins
        // over one that has not, so a stray client message can form a pair of its own but never
        // displace the real one.
        var candidates = connection.Requests
            .Where(pending => pending.RouletteId == echoed && t - pending.TMs >= 0 && t - pending.TMs <= echoMax)
            .ToArray();
        // The most recent request otherwise wins: the echo follows its request by tens of
        // milliseconds, and the client's movement packet has the request's length, so an older
        // pending entry is far more likely to be a coincidence than the real request.
        var match = candidates.LastOrDefault(pending => _pairedOpcodes.Contains((opcode, pending.Opcode)))
            ?? candidates.LastOrDefault();
        if (match is not null)
        {
            if (_pairs.Count < MaxPairs)
            {
                _pairs.Add(new FinderPairHit(connection.Tag, match.Opcode, opcode, echoed, match.TMs, t, at));
                if (_replyOpcodes.Count < MaxReplyOpcodes)
                {
                    _replyOpcodes.Add(opcode);
                }

                if (_pairedOpcodes.Count < MaxReplyOpcodes * 2)
                {
                    _pairedOpcodes.Add((opcode, match.Opcode));
                }
            }
            else
            {
                _overflow++;
            }
        }

        // The template's selector fields are read, not required. Their values mean what they mean
        // on the build the template came from; on this one the draft has to work out which value
        // means "matched" from where the message sits in time. Value fields keep the template's
        // constraints; changing a selector does not make an invalid payload valid.
        var selectors = ReadSelectors(payload, pop, rouletteField.Name);
        if (_replyOpcodes.Contains(opcode))
        {
            foreach (var reading in selectors)
            {
                CountDiagnostic(_finderSelectors, (opcode, reading.Field, reading.Value), MaxFinderSelectors);
            }
        }

        if (!rouletteField.Constraints.IsSatisfiedBy(echoed))
        {
            CountDiagnostic(_popRefusals, (opcode, rouletteField.Name), MaxPopRefusals);
            return;
        }

        var invalidValue = FirstUnsatisfied(payload, pop, rouletteField.Name, ProfileFieldRole.Value);
        if (invalidValue is not null)
        {
            CountDiagnostic(_popRefusals, (opcode, invalidValue), MaxPopRefusals);
            return;
        }

        var refused = FirstUnsatisfied(payload, pop, rouletteField.Name, ProfileFieldRole.Selector);
        if (refused is not null)
        {
            CountDiagnostic(_popRefusals, (opcode, refused), MaxPopRefusals);
        }

        // Kept when the template would have taken it outright - so a build whose structure did
        // not move behaves exactly as it did before - or when a request/echo pair already
        // vouched for the opcode. Anything else is ordinary traffic that happened to carry a
        // plausible byte at one offset, and letting that into the table is how the table fills.
        // Qualification uses the bounded evidence table: the smaller diagnostic opcode set
        // may omit later pairs, but must not hide changed or competing selector states.
        // Losing a pair at MaxPairs already makes the snapshot incomplete and blocks derivation.
        if (refused is not null && !_pairs.Any(pair => pair.ReplyOpcode == opcode))
        {
            return;
        }

        RememberPop(new PopHit(connection.Tag, opcode, echoed, t, at, withinEcho, selectors));
    }

    /// <summary>
    /// Counts server messages of ANY length that carry, at the template's roulette offset, an id
    /// the player actually asked for, outside the echo window that follows the request.
    ///
    /// The pop search proper requires the template's exact length, which is right only while a
    /// build merely renumbers its opcodes. A build that also moves the match out of the queue
    /// reply puts it somewhere the length rule cannot look. This scan is deliberately not an
    /// acceptance rule: it names shapes for a maintainer to read, and requiring an id the player
    /// requested keeps it from counting every message with a small byte in the right place.
    /// </summary>
    /// <param name="connection">Connection the message arrived on.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="t">Session-relative arrival time.</param>
    /// <param name="echoMax">Echo window from the template.</param>
    private void TrackRouletteEcho(
        Connection connection, PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, long t,
        DateTimeOffset at, long echoMax)
    {
        if (direction != _template.Pop.Direction || _template.Pop.Field("roulette_id") is not { } field ||
            !FieldReader.TryReadSatisfied(payload, field, out var value) ||
            !connection.RequestedRoulettes.Contains(value))
        {
            return;
        }

        // An echo is the reply to the request; the scan is looking for what comes later.
        if (connection.RecentRequests.Any(pending =>
                pending.RouletteId == value && t - pending.TMs >= 0 && t - pending.TMs <= echoMax))
        {
            return;
        }

        CountDiagnostic(_rouletteEchoes, (opcode, payload.Length), MaxRouletteEchoes);

        // Kept with its time so the summary can ask the one question that separates the message
        // announcing a match from a shape that merely happened to carry a small byte: did it
        // come in the couple of minutes before the player entered a duty, and every time they
        // did? A roulette id is a number between 1 and 17, so roughly one server message in a
        // hundred carries one by accident. Per-shape compression is not counted as a drop: the
        // count above already says how often the shape did this, and the times are only ever
        // asked a yes/no question.
        var held = 0;
        foreach (var existing in _echoHits)
        {
            if (existing.Opcode == opcode && existing.Length == payload.Length)
            {
                held++;
            }
        }

        if (held >= MaxEchoHitsPerShape)
        {
            return;
        }

        if (_echoHits.Count < MaxEchoHits)
        {
            _echoHits.Add(new RouletteEchoHit(connection.Tag, opcode, payload.Length, value, t, at));
        }
        else
        {
            _diagnosticsOverflow++;
        }
    }

    /// <summary>
    /// Looks for the message that announces a match anywhere in the payload, instead of at the
    /// one offset the template's pop declares.
    ///
    /// A fixed-offset scan can only be answered by a message built like the old build's queue
    /// reply. A client that sends the reply and the match separately gives the announcement a
    /// structure the template has never seen, whose roulette id may sit at any offset - the one
    /// case a fixed offset cannot report, because the result looks identical to "the message
    /// never arrived". So every byte of every server message is compared against the roulette
    /// the player has queued at that moment, and each hit is tallied per
    /// (opcode, length, offset); <see cref="MarkerCandidate"/> says what later makes one of
    /// those positions the announcement.
    ///
    /// Scanning is confined to the gap between a queue request and the loading screen that
    /// ends it, which is a small part of a session and the only part an announcement can be in.
    /// </summary>
    /// <param name="connection">Connection the message arrived on.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="t">Session-relative arrival time.</param>
    /// <param name="at">Wall-clock arrival time.</param>
    /// <param name="echoMax">Echo window from the template.</param>
    private void TrackMarkers(
        Connection connection, PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, long t,
        DateTimeOffset at, long echoMax)
    {
        if (direction != PacketDirection.ServerToClient || connection.Open is not null ||
            connection.Outstanding is not { } outstanding || payload.Length == 0)
        {
            return;
        }

        // Inside the echo window the server is answering the request; that answer is the reply
        // opcode the pairing already knows about, not an announcement.
        if (t - outstanding.TMs <= echoMax || outstanding.RouletteId is < 1 or > 255)
        {
            return;
        }

        var shape = (opcode, payload.Length);
        if (_markerShapes.TryGetValue(shape, out var total))
        {
            _markerShapes[shape] = total + 1;
        }
        else if (_markerShapes.Count < MaxMarkerShapes)
        {
            _markerShapes[shape] = 1;
        }
        else
        {
            // Without a denominator a position cannot be judged, so a shape the table could not
            // take is not scanned at all rather than scanned and misread.
            _markerOverflow++;
            return;
        }

        var wanted = (byte)outstanding.RouletteId;
        for (var offset = 0; offset < payload.Length; offset++)
        {
            if (payload[offset] != wanted)
            {
                continue;
            }

            var key = (opcode, payload.Length, offset);
            if (_markers.TryGetValue(key, out var stat))
            {
                stat.Record(connection.Tag, outstanding.RouletteId, at, t);
            }
            else if (_markers.Count < MaxMarkerKeys)
            {
                stat = new MarkerStat(connection.Tag);
                stat.Record(connection.Tag, outstanding.RouletteId, at, t);
                _markers[key] = stat;
            }
            else
            {
                _markerOverflow++;
            }
        }
    }

    /// <summary>Running tally for one scanned position. Holds ids and times, never a payload.</summary>
    private sealed class MarkerStat
    {
        public MarkerStat(string connectionTag)
        {
            ConnectionTag = connectionTag;
        }

        /// <summary>Rebuilds a tally from evidence read back off disk.</summary>
        /// <param name="carried">Position as it was written.</param>
        public static MarkerStat Restored(MarkerCandidate carried)
        {
            var stat = new MarkerStat(carried.ConnectionTag);
            stat.Hits = carried.Hits;
            stat.ManyConnections = carried.ManyConnections;
            stat.Ids.AddRange(carried.RouletteIds);
            stat.Sightings.AddRange(carried.Sightings);
            return stat;
        }

        public string ConnectionTag { get; }

        public bool ManyConnections { get; private set; }

        public int Hits { get; private set; }

        public List<long> Ids { get; } = new();

        public List<MarkerSighting> Sightings { get; } = new();

        public void Record(string connectionTag, long rouletteId, DateTimeOffset at, long tMs)
        {
            Hits++;
            if (!string.Equals(connectionTag, ConnectionTag, StringComparison.Ordinal))
            {
                ManyConnections = true;
            }

            if (!Ids.Contains(rouletteId) && Ids.Count < MaxMarkerIds)
            {
                Ids.Add(rouletteId);
            }

            if (Sightings.Count < MaxMarkerTimes)
            {
                Sightings.Add(new MarkerSighting(at, rouletteId, connectionTag, tMs));
            }
        }
    }

    /// <summary>Reads only declared selector fields, excluding the roulette field used for pairing.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="pop">Pop structure from the template.</param>
    /// <param name="rouletteFieldName">Field the pairing agreed on; skipped.</param>
    private static IReadOnlyList<CalibrationSelectorReading> ReadSelectors(
        ReadOnlySpan<byte> payload, ProfileMessage pop, string rouletteFieldName)
    {
        List<CalibrationSelectorReading>? readings = null;
        foreach (var field in pop.Fields)
        {
            if (field.Role != ProfileFieldRole.Selector || field.Type == ProfileFieldType.Bytes ||
                string.Equals(field.Name, rouletteFieldName, StringComparison.Ordinal) ||
                !FieldReader.TryRead(payload, field, out var value))
            {
                continue;
            }

            readings ??= new List<CalibrationSelectorReading>();
            readings.Add(new CalibrationSelectorReading(field.Name, value));
        }

        return readings is null ? Array.Empty<CalibrationSelectorReading>() : readings;
    }

    /// <summary>Names the first template field of the given role this payload fails, or null.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="pop">Pop structure from the template.</param>
    /// <param name="skip">Field already checked by the caller.</param>
    /// <param name="role">Value constraints are required; selector constraints may be relearned.</param>
    private static string? FirstUnsatisfied(
        ReadOnlySpan<byte> payload, ProfileMessage pop, string skip, ProfileFieldRole role)
    {
        foreach (var field in pop.Fields)
        {
            if (field.Role != role || field.Type == ProfileFieldType.Bytes ||
                string.Equals(field.Name, skip, StringComparison.Ordinal))
            {
                continue;
            }

            if (!FieldReader.TryReadSatisfied(payload, field, out _))
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps a timed pop candidate. Duplicate reply states cannot become matches and may be
    /// compressed, but other samples are not interchangeable: their roulette and their time
    /// relative to requests and bursts affect derivation. Losing one of those, or filling the
    /// whole table, invalidates the evidence through <see cref="_overflow"/>.
    /// </summary>
    /// <param name="hit">Sample to keep.</param>
    private void RememberPop(PopHit hit)
    {
        var held = 0;
        foreach (var existing in _pops)
        {
            if (existing.ConnectionTag == hit.ConnectionTag && existing.Opcode == hit.Opcode &&
                existing.RouletteId == hit.RouletteId && existing.WithinEcho == hit.WithinEcho &&
                SameSelectors(existing.Selectors, hit.Selectors))
            {
                held++;
            }
        }

        if (held >= MaxPopsPerBucket)
        {
            if (hit.WithinEcho || _pops.Any(existing =>
                    existing.Opcode == hit.Opcode && existing.WithinEcho &&
                    SameSelectors(existing.Selectors, hit.Selectors)))
            {
                _diagnosticsOverflow++;
            }
            else
            {
                _overflow++;
            }

            return;
        }

        if (_pops.Count >= MaxPops)
        {
            _overflow++;
            return;
        }

        _pops.Add(hit);
    }

    /// <summary>True when two samples carry the same template fields with the same values.</summary>
    /// <param name="left">One sample's readings.</param>
    /// <param name="right">The other sample's readings.</param>
    internal static bool SameSelectors(
        IReadOnlyList<CalibrationSelectorReading> left, IReadOnlyList<CalibrationSelectorReading> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Field, right[index].Field, StringComparison.Ordinal) ||
                left[index].Value != right[index].Value)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record PendingRequest(long TMs, ushort Opcode, long RouletteId);

    private readonly record struct RingEntry(
        long TMs,
        DateTimeOffset AtUtc,
        MessageKey Key,
        TerritoryHit? Territory,
        long? JobValue,
        bool JobViolation,
        TerritoryFlags? TerritoryRead);

    /// <summary>How one territory-shaped message read: within the template's constraints, and known to the duty table.</summary>
    private readonly record struct TerritoryFlags(bool Valid, bool Known);

    private sealed class Connection
    {
        public Connection(string tag, string sessionId)
        {
            Tag = tag;
            SessionId = sessionId;
        }

        public string Tag { get; }

        public string SessionId { get; }

        /// <summary>Bursts this connection closed, including any the burst table had no room for.</summary>
        public int Clusters { get; set; }

        /// <summary>True once the connection showed it is the game connection rather than the lobby.</summary>
        public bool Game { get; set; }

        public Queue<PendingRequest> Requests { get; set; } = new();

        public List<PendingRequest> RecentRequests { get; } = new();

        public List<RingEntry> Ring { get; } = new();

        public List<long> AnchorTimes { get; } = new();

        public HashSet<long> RequestedRoulettes { get; } = new();

        /// <summary>The queue the player is waiting on, or null between a load and the next request.</summary>
        public PendingRequest? Outstanding { get; set; }

        public OpenCluster? Open { get; set; }
    }

    private sealed class OpenCluster
    {
        public OpenCluster(string connectionTag, long startTMs, long lastExtendTMs)
        {
            ConnectionTag = connectionTag;
            StartTMs = startTMs;
            EndTMs = startTMs;
            LastExtendTMs = lastExtendTMs;
        }

        public string ConnectionTag { get; }

        public long StartTMs { get; }

        public long EndTMs { get; set; }

        public long LastExtendTMs { get; set; }

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset LoadStartedAtUtc { get; init; }

        public long LoadStartTMs { get; init; }

        public DateTimeOffset EndedAtUtc { get; set; }

        public Dictionary<MessageKey, int> Members { get; } = new();

        public HashSet<(ushort, int)> LargeKinds { get; } = new();

        public List<TerritoryHit> TerritoryHits { get; } = new();

        public Dictionary<ushort, List<long>> JobValues { get; } = new();

        public Dictionary<ushort, (int Valid, int Invalid, int Known)> TerritoryReadings { get; } = new();

        public Dictionary<ushort, int> JobViolations { get; } = new();

        public int OverflowCount { get; set; }
    }
}
