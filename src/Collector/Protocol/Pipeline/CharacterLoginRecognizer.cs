using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// Recognises the message the server sends once when a character enters the game, and reads from it
/// what tells that character apart for good.
///
/// Three guided evidence rounds on the Chinese service (2026-11-14 to 2026-11-16, same key, so the
/// digests compare across days) settled what is lasting and what is not
/// (docs/plans/per-character-progress.md section 2.2):
///
/// <list type="bullet">
///   <item><description>the actor every segment of a game connection is addressed to belongs to the
///   character, but it is not permanent: one character kept it for two days and then had another;
///   </description></item>
///   <item><description>the first eight bytes of the login message stayed the same for each
///   character on all three days and differ between characters - the position where the game puts
///   the character's permanent identifier.</description></item>
/// </list>
///
/// The login message is recognised by its shape, not by its opcode, which every patch reshuffles.
/// On every connection of the three rounds, the first server-to-client IPC message about this
/// client (segment source and target are the same actor) of at least <see cref="MinimumLength"/>
/// bytes decided the matter: on a game connection it was the login message, 3 to 186 ms after the
/// connection's first message, carrying the actor again at payload offset <see cref="ActorOffset"/>;
/// on the title and character-selection connection it was a message without it. So that first
/// message, if it comes within <see cref="Window"/> of the connection's first one, is the login
/// message or there is none on the connection: a later message is never taken for one, even of the
/// same shape. The identifier is refused when either half of it is the actor, which a lasting
/// identifier never is.
///
/// A message that does not fit - a patch moved the actor, say - recognises nobody, and the pipeline
/// files runs as it did before identification. Nothing is kept: the caller digests the identifier at
/// once (<see cref="CharacterIdentityKey"/>). Not thread-safe: the pipeline calls it under its own
/// lock.
/// </summary>
public sealed class CharacterLoginRecognizer
{
    /// <summary>Payload offset at which the login message repeats the client's own actor.</summary>
    public const int ActorOffset = 24;

    /// <summary>Leading payload bytes that tell the character apart.</summary>
    public const int IdentifierBytes = 8;

    /// <summary>
    /// Shortest message about this client that decides a connection; the login message seen so far
    /// has 2952 bytes and the selection screen's first one 1168.
    /// </summary>
    public const int MinimumLength = 1024;

    /// <summary>Connections remembered at once; a game session opens a handful.</summary>
    public const int MaxConnections = 256;

    /// <summary>How long after its connection's first inbound IPC message a login message may come.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    /// <summary>True once a connection could not be remembered because too many were.</summary>
    public bool Saturated { get; private set; }

    /// <summary>Forgets every connection: another capture session.</summary>
    public void Reset()
    {
        _connections.Clear();
        Saturated = false;
    }

    /// <summary>
    /// Looks at one message. When it is a login message, copies the character's identifier into
    /// <paramref name="identifier"/>, says which actor logged in, and returns true.
    /// </summary>
    /// <param name="message">Decoded message; nothing of it is retained.</param>
    /// <param name="identifier">At least <see cref="IdentifierBytes"/> bytes.</param>
    /// <param name="actor">The actor the character has on this connection.</param>
    public bool TryRecognise(DecodedMessage message, Span<byte> identifier, out uint actor)
    {
        ArgumentNullException.ThrowIfNull(message);
        actor = 0;
        if (message.Direction != MessageDirection.Inbound || message.SegmentType != FfxivFraming.SegmentTypeIpc)
        {
            return false;
        }

        if (!_connections.TryGetValue(message.ConnectionKey, out var connection))
        {
            if (_connections.Count >= MaxConnections)
            {
                Saturated = true;
                return false;
            }

            connection = new Connection(message.Mono, Decided: false);
            _connections[message.ConnectionKey] = connection;
        }

        if (connection.Decided)
        {
            return false;
        }

        if (message.Mono - connection.FirstMono > Window)
        {
            _connections[message.ConnectionKey] = connection with { Decided = true };
            return false;
        }

        if (!DecidesTheConnection(message))
        {
            return false;
        }

        _connections[message.ConnectionKey] = connection with { Decided = true };
        var payload = message.Payload.Span;
        var target = message.TargetActor;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload[ActorOffset..]) != target ||
            payload[..IdentifierBytes].IndexOfAnyExcept((byte)0) < 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload) == target ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[sizeof(uint)..]) == target)
        {
            return false;
        }

        payload[..IdentifierBytes].CopyTo(identifier);
        actor = target;
        return true;
    }

    /// <summary>A message about this client, long enough to be the login message or to rule it out.</summary>
    private static bool DecidesTheConnection(DecodedMessage message) =>
        message.TargetActor != 0 &&
        message.SourceActor == message.TargetActor &&
        message.Payload.Length >= MinimumLength;

    private readonly record struct Connection(TimeSpan FirstMono, bool Decided);
}
