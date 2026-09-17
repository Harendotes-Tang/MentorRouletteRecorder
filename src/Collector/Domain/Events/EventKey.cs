using System.Globalization;
using System.Text;

namespace MentorRecorder.Collector.Domain.Events;

/// <summary>Direction a message travelled, as far as the capture layer can tell.</summary>
public enum PacketDirection
{
    /// <summary>Not applicable, for example a synthetic or locally generated event.</summary>
    None,

    /// <summary>Server to client.</summary>
    ServerToClient,

    /// <summary>Client to server.</summary>
    ClientToServer,
}

/// <summary>
/// Identity of one observation, used for deduplication.
///
/// The key is deliberately built from metadata only: session, direction, the opcode (or a
/// symbolic kind when there is no opcode), a coarse epoch, a hash of the payload and the
/// semantic key of the event. It never embeds the payload itself
/// (docs/privacy-boundary.md section 5).
///
/// Two layers use it: the state machine keeps a bounded in-memory set so a duplicate inside
/// one session is ignored, and <c>run_events.event_key</c> carries a UNIQUE index so a
/// duplicate across process restarts or repeated replays cannot be written twice.
/// </summary>
/// <param name="CaptureSessionId">Session the observation belongs to.</param>
/// <param name="Direction">Direction of the underlying message.</param>
/// <param name="OpcodeOrKind">Numeric opcode when known, otherwise a symbolic kind name.</param>
/// <param name="Epoch">Coarse time bucket, in milliseconds, that separates genuine repeats.</param>
/// <param name="PayloadHash">Hash of the message body computed by the capture layer; may be null.</param>
/// <param name="SemanticKey">Stable identity of the parsed meaning, for example the run-scoped id.</param>
public sealed record EventKey(
    string CaptureSessionId,
    PacketDirection Direction,
    string OpcodeOrKind,
    long Epoch,
    string? PayloadHash,
    string SemanticKey)
{
    /// <summary>
    /// Canonical single-line form written to the database. Stable across processes and runs,
    /// so it is safe to use as a UNIQUE key.
    /// </summary>
    public string ToCanonicalString()
    {
        var builder = new StringBuilder(160);
        builder.Append(CaptureSessionId).Append('|')
            .Append(Direction.ToString()).Append('|')
            .Append(OpcodeOrKind).Append('|')
            .Append(Epoch.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(PayloadHash ?? string.Empty).Append('|')
            .Append(SemanticKey);
        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToCanonicalString();
}
