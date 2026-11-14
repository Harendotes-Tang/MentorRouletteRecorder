using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The opt-in identity probe of a live trace (<c>--identity-probe</c>): evidence for deciding which
/// field can tell the player's own characters apart, before anything is built on one
/// (docs/plans/per-character-progress.md section 2).
///
/// It writes keyed digests and nothing else. Two candidates are measured:
///
/// <list type="bullet">
///   <item><description>the actor of the segment header that names this client -- the target of a
///   server-to-client segment, the source of a client-to-server one. One line the first time an
///   actor appears on a connection, and a per-connection tally in the summary;</description></item>
///   <item><description>the first eight payload bytes of the first few occurrences of each
///   server-to-client opcode on a connection, where a login message would carry a character
///   identifier, together with the offset at which the header target reappears in the payload.
///   Only messages whose segment header names this client as both source and target are
///   measured: those are about the player's own character, which leaves out the spawns, chat and
///   examinations of other players. A message about this client can still lead with somebody
///   else's identifier (a party list, say), so a prefix digest is not guaranteed to be the
///   player's own (docs/capture-diagnostics.md section 9.2).</description></item>
/// </list>
///
/// Every value is an HMAC-SHA256 under a key that never leaves this machine
/// (<see cref="IdentityProbeKey"/>), cut to <see cref="IdLength"/> hexadecimal characters. A 32-bit
/// actor hashed without a key could be recovered by trying every value; with the key, the file
/// says only whether two observations on this machine were the same. No identifier, no name and no
/// payload byte is written, and the lines are bounded by <see cref="MaxLines"/>.
///
/// Not thread-safe: the trace sink calls it under its own lock.
/// </summary>
public sealed class CaptureIdentityProbe
{
    /// <summary>Size of the local key in bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>Hexadecimal characters kept of each keyed digest.</summary>
    public const int IdLength = 12;

    /// <summary>Hexadecimal characters of the key's own fingerprint.</summary>
    public const int KeyIdLength = 8;

    /// <summary>Default cap on identity lines.</summary>
    public const int DefaultMaxLines = 20_000;

    /// <summary>Connections whose actors are tallied; further ones are only counted.</summary>
    public const int MaxTrackedConnections = 64;

    /// <summary>Distinct actors tallied per connection and kind.</summary>
    public const int MaxActorsPerConnection = 16;

    /// <summary>Occurrences of one opcode on one connection that get a payload-prefix line.</summary>
    public const int OccurrencesPerOpcode = 2;

    /// <summary>Distinct (connection, opcode) pairs tracked for payload-prefix lines.</summary>
    public const int MaxTrackedOpcodes = 8192;

    /// <summary>Leading payload bytes digested.</summary>
    public const int PrefixBytes = 8;

    /// <summary>Leading payload bytes searched for the header target.</summary>
    public const int ScanBytes = 512;

    /// <summary>Actors whose digest is remembered, so a busy connection is not hashed per message.</summary>
    public const int MaxCachedIds = 1024;

    /// <summary>Line kind: the target actor of a server-to-client segment.</summary>
    public const string HeaderTargetKind = "header_target";

    /// <summary>Line kind: the source actor of a client-to-server segment.</summary>
    public const string HeaderSourceKind = "header_source";

    /// <summary>Line kind: digest of the leading payload bytes of a server-to-client message.</summary>
    public const string PayloadPrefixKind = "payload_prefix";

    private const byte ActorDomain = 0x01;
    private const byte PrefixDomain = 0x02;
    private const byte KeyIdDomain = 0x03;

    private static readonly IReadOnlyList<JsonObject> Nothing = Array.Empty<JsonObject>();

    private readonly byte[] _key;
    private readonly int _maxLines;
    private readonly Dictionary<(string Connection, string Kind), Dictionary<string, ActorTally>> _actors = new();
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Connection, ushort Opcode), int> _occurrences = new();
    private readonly Dictionary<uint, string> _idCache = new();
    private long _lines;
    private long _untrackedSightings;
    private bool _truncated;

    /// <summary>Creates a probe over a local key.</summary>
    /// <param name="key">Exactly <see cref="KeyBytes"/> bytes; copied.</param>
    /// <param name="maxLines">Cap on identity lines; at least one.</param>
    public CaptureIdentityProbe(ReadOnlySpan<byte> key, int maxLines = DefaultMaxLines)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("identity probe key must be 32 bytes", nameof(key));
        }

        _key = key.ToArray();
        _maxLines = Math.Max(1, maxLines);
        KeyId = Digest(KeyIdDomain, ReadOnlySpan<byte>.Empty)[..KeyIdLength];
    }

    /// <summary>
    /// Fingerprint of the key, so two files can be told to be comparable. It reveals nothing about
    /// the key and nothing about any actor.
    /// </summary>
    public string KeyId { get; }

    /// <summary>The keyed digest standing in for one actor.</summary>
    /// <param name="actor">Actor as read from a segment header.</param>
    public string ActorId(uint actor)
    {
        if (_idCache.TryGetValue(actor, out var cached))
        {
            return cached;
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, actor);
        var id = Digest(ActorDomain, bytes);
        if (_idCache.Count < MaxCachedIds)
        {
            _idCache[actor] = id;
        }

        return id;
    }

    /// <summary>Measures one message and returns the lines it earns, usually none.</summary>
    /// <param name="message">Decoded message; nothing of it is retained.</param>
    /// <param name="connection">Connection tag as written on the message line.</param>
    /// <param name="tMs">Milliseconds since the trace started, as on the message line.</param>
    /// <param name="seq">Sequence number of the message line.</param>
    public IReadOnlyList<JsonObject> Observe(DecodedMessage message, string connection, long tMs, long seq)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connection);

        var inbound = message.Direction == MessageDirection.Inbound;
        var actor = inbound ? message.TargetActor : message.SourceActor;
        List<JsonObject>? lines = null;

        if (actor != 0 && TallyActor(connection, inbound ? HeaderTargetKind : HeaderSourceKind, actor, tMs) is { } id)
        {
            Add(ref lines, new JsonObject
            {
                ["identity"] = inbound ? HeaderTargetKind : HeaderSourceKind,
                ["seq"] = seq,
                ["t_ms"] = tMs,
                ["conn"] = connection,
                ["id"] = id,
            });
        }

        if (inbound && message.SegmentType == FfxivFraming.SegmentTypeIpc && WantsPrefix(message, connection))
        {
            var payload = message.Payload.Span;
            Add(ref lines, new JsonObject
            {
                ["identity"] = PayloadPrefixKind,
                ["seq"] = seq,
                ["t_ms"] = tMs,
                ["conn"] = connection,
                ["op"] = CaptureTraceSink.FormatOpcode(message.Opcode),
                ["len"] = payload.Length,
                ["p8"] = Digest(PrefixDomain, payload[..PrefixBytes]),
                ["self_at"] = OffsetOf(payload, message.TargetActor),
            });
        }

        return lines ?? Nothing;
    }

    /// <summary>The section the trace summary carries: the key fingerprint and the actor tally.</summary>
    public JsonObject Summary()
    {
        var rows = new JsonArray();
        foreach (var (key, actors) in _actors.OrderBy(pair => pair.Value.Values.Min(tally => tally.FirstMs)))
        {
            foreach (var (id, tally) in actors.OrderBy(pair => pair.Value.FirstMs))
            {
                rows.Add(new JsonObject
                {
                    ["conn"] = key.Connection,
                    ["kind"] = key.Kind,
                    ["id"] = id,
                    ["count"] = tally.Count,
                    ["first_t_ms"] = tally.FirstMs,
                    ["last_t_ms"] = tally.LastMs,
                });
            }
        }

        return new JsonObject
        {
            ["key_id"] = KeyId,
            ["lines"] = _lines,
            ["truncated"] = _truncated,
            ["untracked_sightings"] = _untrackedSightings,
            ["actors"] = rows,
        };
    }

    /// <summary>Counts one sighting; returns the id only when this actor is new on the connection.</summary>
    private string? TallyActor(string connection, string kind, uint actor, long tMs)
    {
        if (!_connections.Contains(connection))
        {
            if (_connections.Count >= MaxTrackedConnections)
            {
                _untrackedSightings++;
                return null;
            }

            _connections.Add(connection);
        }

        if (!_actors.TryGetValue((connection, kind), out var actors))
        {
            actors = new Dictionary<string, ActorTally>(StringComparer.Ordinal);
            _actors[(connection, kind)] = actors;
        }

        var id = ActorId(actor);
        if (actors.TryGetValue(id, out var tally))
        {
            actors[id] = tally with { Count = tally.Count + 1, LastMs = Math.Max(tally.LastMs, tMs) };
            return null;
        }

        if (actors.Count >= MaxActorsPerConnection)
        {
            _untrackedSightings++;
            return null;
        }

        actors[id] = new ActorTally(1, tMs, tMs);
        return id;
    }

    private bool WantsPrefix(DecodedMessage message, string connection)
    {
        // About this client only: its own character is both the subject and the addressee.
        if (message.TargetActor == 0 || message.SourceActor != message.TargetActor)
        {
            return false;
        }

        var payload = message.Payload.Span;
        if (payload.Length < PrefixBytes || payload[..PrefixBytes].IndexOfAnyExcept((byte)0) < 0)
        {
            return false;
        }

        var key = (connection, message.Opcode);
        if (!_occurrences.TryGetValue(key, out var seen))
        {
            if (_occurrences.Count >= MaxTrackedOpcodes)
            {
                return false;
            }
        }
        else if (seen >= OccurrencesPerOpcode)
        {
            return false;
        }

        _occurrences[key] = seen + 1;
        return true;
    }

    /// <summary>First four-byte-aligned offset at which the actor appears, or null.</summary>
    private static int? OffsetOf(ReadOnlySpan<byte> payload, uint actor)
    {
        if (actor == 0)
        {
            return null;
        }

        var limit = Math.Min(payload.Length, ScanBytes) - sizeof(uint);
        for (var offset = 0; offset <= limit; offset += sizeof(uint))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]) == actor)
            {
                return offset;
            }
        }

        return null;
    }

    private void Add(ref List<JsonObject>? lines, JsonObject line)
    {
        if (_lines >= _maxLines)
        {
            _truncated = true;
            return;
        }

        _lines++;
        (lines ??= new List<JsonObject>(2)).Add(line);
    }

    private string Digest(byte domain, ReadOnlySpan<byte> value)
    {
        Span<byte> input = stackalloc byte[1 + value.Length];
        input[0] = domain;
        value.CopyTo(input[1..]);

        Span<byte> digest = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, input, digest);
        return Convert.ToHexString(digest).ToLowerInvariant()[..IdLength];
    }

    private readonly record struct ActorTally(long Count, long FirstMs, long LastMs);
}

/// <summary>
/// The key the identity probe digests with: thirty-two random bytes created on first use and kept
/// in the managed data root, never written to a trace, a log or a diagnostics export.
///
/// It is what makes two traces from this machine comparable and a trace from this machine
/// meaningless anywhere else. The file holds the key encrypted for the current Windows user
/// (DPAPI), so a copy of the data folder sent to somebody else does not carry a usable key. A file
/// that does not hold a key is refused rather than replaced: replacing it silently would make
/// earlier traces incomparable without saying so.
/// </summary>
public static class IdentityProbeKey
{
    private const int MaxFileBytes = 16 * 1024;
    private const int ReadAttempts = 5;

    private static readonly Speech.ISecretProtector DefaultProtector =
        Speech.DpapiProtector.ForPurpose("MentorRecorder.identity-probe-key.v1");

    /// <summary>Reads the key, creating it when the file does not exist yet.</summary>
    /// <param name="path">Key file.</param>
    /// <param name="protector">Encryption at rest; DPAPI for the current user when null.</param>
    /// <exception cref="InvalidDataException">The file exists but does not hold a key.</exception>
    public static byte[] LoadOrCreate(string path, Speech.ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var secrets = protector ?? DefaultProtector;
        Storage.DatabasePaths.EnsureParentDirectory(full);

        if (!File.Exists(full) && TryCreate(full, secrets) is { } created)
        {
            return created;
        }

        return Read(full, secrets);
    }

    /// <summary>
    /// Publishes a new key under its final name in one step, so a reader never sees a file that is
    /// still being written. Returns null when another trace published one first.
    /// </summary>
    private static byte[]? TryCreate(string full, Speech.ISecretProtector secrets)
    {
        var key = RandomNumberGenerator.GetBytes(CaptureIdentityProbe.KeyBytes);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, secrets.Protect(key));
            try
            {
                File.Move(temporary, full, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(full))
            {
                // Lost the race to a trace starting at the same moment; theirs is the key.
                return null;
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static byte[] Read(string full, Speech.ISecretProtector secrets)
    {
        byte[] blob;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                blob = File.ReadAllBytes(full);
                break;
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                // Briefly held by a virus scanner or by the trace that just published it.
                Thread.Sleep(50 * attempt);
            }
        }

        if (blob.Length is 0 or > MaxFileBytes)
        {
            throw new InvalidDataException("identity probe key file has an unexpected size");
        }

        byte[] key;
        try
        {
            key = secrets.Unprotect(blob);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("identity probe key file cannot be decrypted by this user", ex);
        }

        if (key.Length != CaptureIdentityProbe.KeyBytes)
        {
            throw new InvalidDataException("identity probe key file does not hold a key");
        }

        return key;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file holds only ciphertext of a key nothing was digested with.
        }
    }
}
