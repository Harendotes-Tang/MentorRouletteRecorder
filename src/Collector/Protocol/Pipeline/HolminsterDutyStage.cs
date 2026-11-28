using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// Projects the observed Holminster Switch objective sequence onto the full overlay.
/// The rule is scoped to CN 2026.11.03 and is tied to the active run and its director
/// connection. It does not record a result or require an ACT log to run.
/// </summary>
internal sealed class HolminsterDutyStage
{
    public const string ClientBuild = "2026.11.03.0000.0000";
    public const ushort Territory = 837;
    public const ushort Opcode = 0x00a9;
    private const ushort Director = 72;
    private const int ContentId = 676;
    private const uint ObjectiveComplete = 0x80000016;
    private const uint ObjectiveNext = 0x80000015;

    // Both objective commands were observed at the same epoch for transitions 0..4 in the
    // blind trace 763e44d1d587af8590347099bc898947af40917ec6d307754f4108b1fff8c92e.
    // The even objective advances concurred with the arena release and preceded the delayed
    // ActorControl death notification. The existing run lifecycle handles duty clear.
    private static readonly string[] Stages = ["trash1", "boss1", "trash2", "boss2", "trash3", "boss3"];

    private string? _connection;
    private long _lastVerifiedEpoch;
    private (long Epoch, uint Objective)? _completed;
    private int _stage = -1;

    public string Phase => _stage < 0 ? "unknown" : Stages[_stage];

    public static bool Supports(MentorRun? run) =>
        run is { Region: Region.Cn, GameBuild: ClientBuild, TerritoryId: Territory }
        && (run.ContentId is null or ContentId);

    public void Reset(string? enteredFrom = null)
    {
        _connection = enteredFrom;
        _lastVerifiedEpoch = 0;
        _completed = null;
        _stage = -1;
    }

    /// <summary>Checks the fixed carrier without interpreting any other class of message.</summary>
    public static bool IsCarrier(DecodedMessage message)
    {
        var body = message.Payload.Span;
        return message is { Direction: MessageDirection.Inbound, SegmentType: FfxivFraming.SegmentTypeIpc, Opcode: Opcode }
            && body.Length == DutyClearSignal.Length
            && BinaryPrimitives.ReadUInt16LittleEndian(body) == DutyClearSignal.Category
            && BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) == Director
            && BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) == DutyClearSignal.DirectorType;
    }

    /// <summary>Observes a checked carrier for the active run and reports a stage change.</summary>
    public bool Observe(DecodedMessage message)
    {
        if (_connection is null || _connection != message.ConnectionKey ||
            message.Epoch <= 0 || message.Epoch < _lastVerifiedEpoch)
        {
            return false;
        }

        if (_completed is { } pending && message.Epoch > pending.Epoch)
        {
            _completed = null;
        }

        if (DutyStartSignal.TryMatch(message, out var start, out _))
        {
            // As in the run state machine, the first anchor distinguishes a fresh entry from
            // a late join. A later start cannot change that first observation.
            if (_lastVerifiedEpoch != 0)
            {
                return false;
            }

            _lastVerifiedEpoch = message.Epoch;
            return start == DutyStartSignalKind.Commenced && Advance(0);
        }

        if (_lastVerifiedEpoch == 0)
        {
            return false;
        }

        var body = message.Payload.Span;
        if (body[20..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        var command = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        var objective = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        var flag = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
        if (command == ObjectiveComplete && objective < 5 && flag == 1 && message.Epoch > 0)
        {
            _completed = (message.Epoch, objective);
            return false;
        }

        if (command != ObjectiveNext || objective is 0 or > 5 || flag != 0)
        {
            return false;
        }

        var pair = _completed;
        _completed = null;
        if (pair != (message.Epoch, objective - 1) || !Advance((int)objective))
        {
            return false;
        }

        _lastVerifiedEpoch = message.Epoch;
        return true;
    }

    private bool Advance(int objective)
    {
        if (objective <= _stage)
        {
            return false;
        }

        _stage = objective;
        _completed = null;
        return true;
    }
}
