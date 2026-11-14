using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Checkpoints of the identity probe: what the guided round in the Desktop asks of it.
///
/// The player logs in as one character, then another, then the first again, and says so each
/// time (<see cref="CaptureTraceMarkerText.Identity"/>). At each of those moments the probe names
/// the actor the client's latest server-to-client segments were addressed to -- "who this client
/// is right now" -- as a keyed digest. Comparing the three answers is the whole verdict
/// (<see cref="Result"/>): the same digest for the first character both times and another one for
/// the second means the field tells characters apart.
///
/// A step confirmed too early must read as "nobody", never as somebody else, because a wrong
/// name is a wrong verdict. Three rules see to that (real-machine trace of 2026-11-14: each login
/// opens new game connections, the previous character's connections stay busy until the logout,
/// and the title and character-selection connection carries another id at a trickle):
///
/// <list type="bullet">
///   <item><description>a login step counts only connections first seen after the step before
///   it was confirmed, so the tail of the character just left cannot be read as the new one;
///   </description></item>
///   <item><description>only the latest <see cref="FreshSamples"/> of those segments are looked
///   at, no older than <see cref="RecentMs"/>, and one actor must hold
///   <see cref="DominantPercent"/> percent of at least <see cref="MinWindowSamples"/>;
///   </description></item>
///   <item><description>that actor must have been addressed within <see cref="FreshMs"/> of the
///   confirmation, so a connection that has gone quiet names nobody.</description></item>
/// </list>
///
/// A step can be confirmed again; the latest reading of each step is the one judged.
///
/// The probe also keeps, per interval between checkpoints, the last payload-prefix digest of
/// every opcode about this client, and lists in the summary the opcodes whose digest was the same
/// for the first character both times and different for the second: the candidates for a login
/// message that carries a lasting character identifier. Opcodes and lengths only; no value.
/// </summary>
public sealed partial class CaptureIdentityProbe
{
    /// <summary>Line kind: who the client was when the player confirmed a step.</summary>
    public const string CheckpointKind = "checkpoint";

    /// <summary>Server-to-client segments remembered for the next checkpoint.</summary>
    public const int WindowSize = 256;

    /// <summary>The latest eligible segments a checkpoint judges.</summary>
    public const int FreshSamples = 40;

    /// <summary>How far back a checkpoint looks, in milliseconds.</summary>
    public const int RecentMs = 15_000;

    /// <summary>How recently the named actor must have been addressed, in milliseconds.</summary>
    public const int FreshMs = 5_000;

    /// <summary>Fewest segments a checkpoint needs before it names anyone.</summary>
    public const int MinWindowSamples = 20;

    /// <summary>Share of the judged segments, in percent, one actor needs to be named.</summary>
    public const int DominantPercent = 80;

    /// <summary>Actors listed on a checkpoint line.</summary>
    public const int CheckpointTopActors = 3;

    /// <summary>Opcodes remembered per interval for the login-message candidates.</summary>
    public const int MaxIntervalOpcodes = 4096;

    /// <summary>Candidates listed in the summary.</summary>
    public const int MaxLoginCandidates = 16;

    /// <summary>Connections whose first sighting is remembered for the connection rule.</summary>
    public const int MaxKnownConnections = 256;

    /// <summary>Not every step confirmed yet, or a step had too little traffic to name anyone.</summary>
    public const string ResultIncomplete = "INCOMPLETE";

    /// <summary>The first character read the same both times and the second read differently.</summary>
    public const string ResultDistinguished = "DISTINGUISHED";

    /// <summary>Both characters read the same: the field does not tell them apart.</summary>
    public const string ResultSameIdentity = "SAME_IDENTITY";

    /// <summary>The first character read differently the second time, or after a zone change.</summary>
    public const string ResultUnstable = "UNSTABLE";

    private const string FirstMarker = "character_a";
    private const string SecondMarker = "character_b";
    private const string AgainMarker = "character_a_again";

    private readonly Sample[] _window = new Sample[WindowSize];
    private int _windowCount;
    private int _windowNext;
    private long _observed;
    private readonly Dictionary<string, long> _connectionFirstSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepReading> _readings = new(StringComparer.Ordinal);
    private string? _lastMarker;
    private Dictionary<ushort, IntervalPrefix> _interval = new();

    /// <summary>The verdict so far; one of the <c>Result*</c> tokens.</summary>
    public string Result
    {
        get
        {
            var first = Reading(FirstMarker)?.Dominant;
            var second = Reading(SecondMarker)?.Dominant;
            var again = Reading(AgainMarker);
            if (first is null || second is null || again?.Dominant is null)
            {
                return ResultIncomplete;
            }

            if (first == second)
            {
                return ResultSameIdentity;
            }

            if (first != again.Dominant)
            {
                return ResultUnstable;
            }

            // The optional steps are taken as the first character after its return: a zone change
            // must not move it. One confirmed earlier belongs to whoever was logged in then.
            foreach (var reading in _readings.Values)
            {
                if (reading.Marker is not (FirstMarker or SecondMarker or AgainMarker) &&
                    reading.Observed > again.Observed &&
                    reading.Dominant is { } later && later != again.Dominant)
                {
                    return ResultUnstable;
                }
            }

            return ResultDistinguished;
        }
    }

    /// <summary>
    /// Each confirmed step and whether it named anyone, in the order the steps are asked for.
    /// What the Desktop shows so that a step confirmed too early can be confirmed again; no id.
    /// </summary>
    public IReadOnlyList<(string Marker, bool Named)> Steps =>
        CaptureTraceMarkerText.Identity
            .Where(_readings.ContainsKey)
            .Select(marker => (marker, _readings[marker].Dominant is not null))
            .ToArray();

    /// <summary>
    /// Records who the client is at the moment the player confirmed a step, and starts the next
    /// interval. Returns the line to write, or null once the line cap has been reached.
    /// </summary>
    /// <param name="marker">One of <see cref="CaptureTraceMarkerText.Identity"/>.</param>
    /// <param name="tMs">Milliseconds since the trace started, as on the marker line.</param>
    public JsonObject? Checkpoint(string marker, long tMs)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var since = EligibleSince(marker);
        var counts = new Dictionary<uint, (int Count, long LastMs)>();
        var samples = 0;

        // Newest first, so the first FreshSamples eligible ones are the latest.
        for (var back = 1; back <= _windowCount && samples < FreshSamples; back++)
        {
            var sample = _window[(_windowNext - back + WindowSize) % WindowSize];
            if (sample.Ms < tMs - RecentMs)
            {
                break;
            }

            if (sample.ConnectionSeen <= since)
            {
                continue;
            }

            samples++;
            counts[sample.Actor] = counts.TryGetValue(sample.Actor, out var seen)
                ? (seen.Count + 1, seen.LastMs)
                : (1, sample.Ms);
        }

        var top = counts.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key)
            .Take(CheckpointTopActors).ToArray();
        var dominant = samples >= MinWindowSamples && top.Length > 0 &&
                       top[0].Value.Count * 100 >= DominantPercent * samples &&
                       top[0].Value.LastMs >= tMs - FreshMs
            ? ActorId(top[0].Key)
            : null;

        // The same step confirmed again continues its own interval: the login it is about
        // happened before the first confirmation.
        var prefixes = _interval;
        if (_lastMarker == marker && _readings.TryGetValue(marker, out var previous))
        {
            prefixes = new Dictionary<ushort, IntervalPrefix>(previous.Prefixes);
            foreach (var (opcode, prefix) in _interval)
            {
                prefixes[opcode] = prefix;
            }
        }

        // Replaced, not appended: the latest reading of a step is the one judged.
        _readings[marker] = new StepReading(marker, tMs, _observed, samples, dominant, prefixes);
        _lastMarker = marker;

        // The next interval earns a prefix line for each opcode again.
        _interval = new Dictionary<ushort, IntervalPrefix>();
        _occurrences.Clear();

        var rows = new JsonArray();
        foreach (var (actor, tally) in top)
        {
            rows.Add(new JsonObject { ["id"] = ActorId(actor), ["count"] = tally.Count });
        }

        List<JsonObject>? lines = null;
        Add(ref lines, new JsonObject
        {
            ["identity"] = CheckpointKind,
            ["marker"] = marker,
            ["t_ms"] = tMs,
            ["samples"] = samples,
            ["named"] = dominant is not null,
            ["top"] = rows,
        });
        return lines?[0];
    }

    /// <summary>
    /// The observation count a connection must have been first seen after to count for a step.
    /// A login step counts only connections opened since the step before it; the first step and
    /// the optional ones, taken as the character already logged in, count every connection.
    /// </summary>
    private long EligibleSince(string marker) => marker switch
    {
        SecondMarker => Reading(FirstMarker)?.Observed ?? 0,
        AgainMarker => Reading(SecondMarker)?.Observed ?? Reading(FirstMarker)?.Observed ?? 0,
        _ => 0,
    };

    /// <summary>Counts one observed message and says when its connection was first seen.</summary>
    private long NoteConnection(string connection)
    {
        _observed++;
        if (_connectionFirstSeen.TryGetValue(connection, out var seen))
        {
            return seen;
        }

        // Past the cap a connection reads as old as the trace: it can only fail a login step,
        // which then names nobody.
        if (_connectionFirstSeen.Count >= MaxKnownConnections)
        {
            return 0;
        }

        _connectionFirstSeen[connection] = _observed;
        return _observed;
    }

    /// <summary>Notes the actor one server-to-client segment was addressed to.</summary>
    private void RememberTarget(uint actor, long tMs, long connectionSeen)
    {
        _window[_windowNext] = new Sample(actor, tMs, connectionSeen);
        _windowNext = (_windowNext + 1) % WindowSize;
        if (_windowCount < WindowSize)
        {
            _windowCount++;
        }
    }

    /// <summary>Notes the latest prefix digest of an opcode about this client in this interval.</summary>
    private void RememberPrefix(ushort opcode, int length, string digest)
    {
        if (_interval.Count < MaxIntervalOpcodes || _interval.ContainsKey(opcode))
        {
            _interval[opcode] = new IntervalPrefix(length, digest);
        }
    }

    private StepReading? Reading(string marker) => _readings.GetValueOrDefault(marker);

    private JsonArray CheckpointRows()
    {
        var rows = new JsonArray();
        foreach (var reading in _readings.Values.OrderBy(reading => reading.Observed))
        {
            rows.Add(new JsonObject
            {
                ["marker"] = reading.Marker,
                ["t_ms"] = reading.Ms,
                ["samples"] = reading.Samples,
                ["id"] = reading.Dominant,
            });
        }

        return rows;
    }

    /// <summary>
    /// Opcodes whose prefix digest was the same for the first character both times and different
    /// for the second, by opcode.
    /// </summary>
    private JsonArray LoginCandidates()
    {
        var rows = new JsonArray();
        if (Reading(FirstMarker) is not { } first || Reading(SecondMarker) is not { } second ||
            Reading(AgainMarker) is not { } again)
        {
            return rows;
        }

        foreach (var (opcode, prefix) in first.Prefixes.OrderBy(pair => pair.Key))
        {
            if (rows.Count >= MaxLoginCandidates)
            {
                break;
            }

            if (again.Prefixes.TryGetValue(opcode, out var repeated) && repeated.Digest == prefix.Digest &&
                second.Prefixes.TryGetValue(opcode, out var other) && other.Digest != prefix.Digest)
            {
                rows.Add(new JsonObject
                {
                    ["op"] = CaptureTraceSink.FormatOpcode(opcode),
                    ["len"] = prefix.Length,
                });
            }
        }

        return rows;
    }

    private readonly record struct Sample(uint Actor, long Ms, long ConnectionSeen);

    private readonly record struct IntervalPrefix(int Length, string Digest);

    private sealed record StepReading(
        string Marker,
        long Ms,
        long Observed,
        int Samples,
        string? Dominant,
        Dictionary<ushort, IntervalPrefix> Prefixes);
}
