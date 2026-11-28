using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// Projects the three confirmed Keeper of the Lake encounters onto the overlay. The
/// evidence is the CN level-synced trace e4d00bea74caf3282481c57ebdebec9da6e4d84c9aeb9af9fd6276756d2bae37.
/// Only this run's connection can bind a boss. Actor ids and the rolling damage window are
/// discarded with the tracker and never enter the database, trace output or IPC.
/// </summary>
internal sealed class KeeperOfLakeDutyStage(string connection) : IDutyStage
{
    public const ushort Territory = 1063;
    public const int ContentId = 32;

    private const ushort DirectorOpcode = 0x00a9;
    private const ushort SystemLogOpcode = 0x01de;
    private const uint Director = 0x80030020;
    private const ushort DirectorNumber = 0x0020;
    private const int CountdownSeconds = 15;
    private const uint ClosingSoon = 2012;
    private const uint Sealed = 2013;
    private const uint Released = 2014;
    private const int MaxDamageSlots = 512;

    private static readonly TimeSpan PullWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefeatWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(CountdownSeconds);
    private static readonly string[] Stages = ["trash1", "boss1", "trash2", "boss2", "trash3", "boss3"];
    private static readonly int[] RequiredOpcodes =
        [DirectorOpcode, SystemLogOpcode, CnDutyCombatPackets.ActorControl, 0x0190, 0x0233, 0x0119, 0x039c, 0x00dd];

    private readonly Queue<(uint Actor, TimeSpan At)> _damage = new();
    private TimeSpan? _droppedDamageAt;
    private (int Boss, TimeSpan Countdown)? _pull;
    private (long Epoch, TimeSpan At)? _lastCountdown;
    private bool _anchored;
    private int _expectedBoss;
    private uint _bossActor;
    private bool _sealed;
    private TimeSpan? _releasedAt;
    private int _stage = -1;
    private long _lastEpoch;
    private TimeSpan _lastMono;

    public string Phase => _stage < 0 ? "unknown" : Stages[_stage];

    public static bool Supports(MentorRun? run, ProtocolProfile profile) =>
        run is { Region: Region.Cn, GameBuild: CnDutyCombatPackets.ClientBuild, TerritoryId: Territory }
        && run.ContentId is null or ContentId
        && !RequiredOpcodes.Any(profile.IsObfuscated);

    public bool Observe(DecodedMessage message)
    {
        if (message.ConnectionKey != connection || message.Direction != MessageDirection.Inbound ||
            message.SegmentType != FfxivFraming.SegmentTypeIpc || message.Epoch <= 0 ||
            message.Epoch < _lastEpoch || message.Mono < _lastMono)
        {
            return false;
        }

        _lastEpoch = message.Epoch;
        _lastMono = message.Mono;
        if (!_anchored)
        {
            return message.Opcode == DirectorOpcode && DutyStartSignal.ReadAnchor(message) is { } anchor &&
                ObserveStartAnchor(anchor, replayed: false);
        }

        // The current message closes an expired pull window before contributing to it.
        // Use the capture clock so the same packets replay in the same order.
        var changed = ConfirmPull(message.Mono);
        if (message.Opcode == SystemLogOpcode)
        {
            ObserveArena(message);
        }
        else if (CnDutyCombatPackets.IsDefeat(message.Opcode, message.Payload.Span))
        {
            changed |= ConfirmDefeat(message.SourceActor, message.Mono);
        }
        else
        {
            ObserveDamage(message);
        }

        return changed;
    }

    /// <summary>Anchors the verified entry, including a start replayed after shared calibration binds.</summary>
    public bool ObserveStartAnchor(DutyStartAnchor anchor, bool replayed)
    {
        if (_anchored || anchor.Opcode != DirectorOpcode || anchor.Director != DirectorNumber ||
            anchor.ConnectionKey != connection || anchor.Epoch <= 0 ||
            anchor.Epoch < _lastEpoch || anchor.Mono < _lastMono)
        {
            return false;
        }

        _anchored = true;
        _lastEpoch = anchor.Epoch;
        _lastMono = anchor.Mono;
        // Shared staging kept the verified start, but not the combat traffic that followed
        // it. It can prove the run's director, while only a later pull can locate the stage.
        if (replayed || anchor.Kind == DutyStartSignalKind.TimeRemaining)
            return false;

        _expectedBoss = 1;
        return SetStage(0);
    }

    private void ObserveArena(DecodedMessage message)
    {
        var body = message.Payload.Span;
        var now = message.Mono;
        if (body.Length != 24 || BinaryPrimitives.ReadUInt32LittleEndian(body) != Director ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[20..]) != 0)
        {
            return;
        }

        var boss = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]) switch
        {
            1503 => 1,
            1505 => 2,
            1507 => 3,
            _ => 0,
        };
        if (boss == 0 || (_expectedBoss != 0 && _expectedBoss != boss))
            return;

        var code = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var seconds = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
        if (code == ClosingSoon && seconds == CountdownSeconds && _pull is null &&
            (_lastCountdown is not { } previous ||
             message.Epoch > previous.Epoch && now - previous.At >= RetryInterval) &&
            (_releasedAt is not { } released || now - released > DefeatWindow))
        {
            // A distinct later countdown can be a retry even if the preceding pull
            // ended before the seal. Discard the old actor before considering new hits.
            _lastCountdown = (message.Epoch, now);
            _expectedBoss = boss;
            _bossActor = 0;
            _sealed = false;
            _releasedAt = null;
            _pull = (boss, now);
        }
        else if (_bossActor != 0 && seconds == 0)
        {
            if (code == Sealed && _releasedAt is null)
                _sealed = true;
            else if (code == Released && _sealed)
                _releasedAt ??= now;
        }
    }

    private void ObserveDamage(DecodedMessage message)
    {
        if (message.SourceActor == 0 || CnDutyCombatPackets.IsNpc(message.SourceActor))
            return;

        var action = CnDutyCombatPackets.ReadAction(message.Opcode, message.Payload.Span);
        if (action.Count == 0)
            return;

        var oldest = (_pull?.Countdown ?? message.Mono) - PullWindow;
        TrimDamage(oldest);
        for (var slot = 0; slot < action.Count; slot++)
        {
            var actor = action.TargetActor(slot);
            if (!CnDutyCombatPackets.IsNpc(actor) || !action.HasDamage(slot))
                continue;

            if (_damage.Count == MaxDamageSlots)
                _droppedDamageAt = _damage.Dequeue().At;
            _damage.Enqueue((actor, message.Mono));
        }
    }

    private bool ConfirmPull(TimeSpan now)
    {
        if (_pull is not { } pull || now - pull.Countdown <= PullWindow)
            return false;

        _pull = null;
        var oldest = pull.Countdown - PullWindow;
        TrimDamage(oldest);
        uint actor = 0;
        var ambiguous = _droppedDamageAt >= oldest;
        foreach (var hit in _damage)
        {
            if (actor != 0 && actor != hit.Actor)
                ambiguous = true;
            actor = hit.Actor;
        }

        _damage.Clear();
        _droppedDamageAt = null;
        if (actor == 0 || ambiguous)
            return SetStage(-1);

        _bossActor = actor;
        return SetStage(2 * pull.Boss - 1);
    }

    private bool ConfirmDefeat(uint actor, TimeSpan now)
    {
        if (_bossActor == 0 || actor != _bossActor || _expectedBoss >= 3 ||
            _releasedAt is not { } release || now - release > DefeatWindow)
        {
            return false;
        }

        var next = 2 * _expectedBoss;
        _expectedBoss++;
        _lastCountdown = null;
        _bossActor = 0;
        _sealed = false;
        _releasedAt = null;
        _damage.Clear();
        _droppedDamageAt = null;
        return SetStage(next);
    }

    private bool SetStage(int next)
    {
        if (_stage == next)
            return false;

        _stage = next;
        return true;
    }

    private void TrimDamage(TimeSpan oldest)
    {
        while (_damage.TryPeek(out var hit) && hit.At < oldest)
            _damage.Dequeue();
    }
}
