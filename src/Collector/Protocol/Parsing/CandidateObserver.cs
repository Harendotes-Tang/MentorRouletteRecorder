using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>
/// 独立候选观测；可选研究负载只供专用账本/导出，不是状态机可消费的语义事件。
/// <c>OverflowCount</c> 只出现在窗口聚合行上：该窗口因键上限被丢弃的不同 (方向, opcode, 长度)
/// 数量，0 表示这段采样是完整的（评审 L-12）。
/// </summary>
public sealed record CandidateObservation(
    string ObservationId,
    string CaptureSessionId,
    string ProfileId,
    string HypothesisName,
    string? Group,
    string Direction,
    int? Opcode,
    int? Length,
    string? PayloadHash12,
    string ConnectionTag,
    DateTimeOffset ObservedAtUtc,
    long TMs,
    DateTimeOffset? FirstObservedAtUtc = null,
    DateTimeOffset? LastObservedAtUtc = null,
    long? FirstTMs = null,
    long? LastTMs = null,
    string? PayloadHex = null,
    int Occurrences = 1,
    int OverflowCount = 0);

/// <summary>
/// 在解析线程观察候选 opcode，显式研究白名单可附加至多 512 字节完整负载，超限不截断保存。调用者持有会话锁并负责将结果送往独立账本；
/// 本类型没有状态机、正式记录仓储、日志或语音依赖。连接键仅用于生成会话内短标识，不向外暴露。
/// </summary>
public sealed class CandidateObserver : IDecodedMessageSink
{
    public const long ZoneWindowMs = 3000;
    public const int ZoneThreshold = 5;
    public const int MaxConnections = 64;

    /// <summary>Hypothesis whose observation opens a queue window on its connection.</summary>
    public const string QueueWindowTrigger = "QUEUE_REGISTRATION";
    /// <summary>Name of the aggregated per-(direction, opcode, length) sample rows.</summary>
    public const string QueueWindowSampleName = "QUEUE_WINDOW_SAMPLE";
    public const string QueueWindowGroup = "queue_window";
    /// <summary>
    /// A window that never sees a zone load is flushed after this long. Two hours: a mentor
    /// roulette queue can stand for over half an hour, and a shorter cap loses the pop.
    /// </summary>
    public const long QueueWindowMaxMs = 2 * 60 * 60 * 1000;
    /// <summary>Hypothesis name that, like the finder group, ends a sampling segment.</summary>
    public const string FinderActionName = "FINDER_ACTION";
    public const string FinderGroup = "finder";
    public const string ZoneLoadGroup = "zone_load";
    /// <summary>Distinct (direction, opcode, length) keys kept per window; the rest are dropped.</summary>
    public const int MaxQueueWindowKeys = 512;
    /// <summary>Aggregated rows sampled between entering a duty and the next zone change.</summary>
    public const string DutyWindowSampleName = "DUTY_WINDOW_SAMPLE";
    public const string DutyWindowGroup = "duty_window";
    /// <summary>A duty window that never sees the exit is flushed after three hours.</summary>
    public const long DutyWindowMaxMs = 3 * 60 * 60 * 1000;
    /// <summary>
    /// A zone change this soon after a finder state update is taken as entering the duty,
    /// the same three-minute rule the desktop timeline applies.
    /// </summary>
    public const long DutyWindowFinderLeadMs = 3 * 60 * 1000;
    private readonly ProtocolProfile _profile;
    private readonly string _sessionId;
    private readonly Action<CandidateObservation> _emit;
    private readonly Dictionary<string, ZoneWindow> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<(PacketDirection, ushort), ProfileHypothesis> _hypotheses;
    private readonly Dictionary<string, QueueWindow> _queueWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueWindow> _dutyWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastFinderTMs = new(StringComparer.Ordinal);
    private IReadOnlySet<ushort> _researchOpcodes;

    /// <summary>只接受 CANDIDATE 档案；即使调用方误传 VERIFIED 也不会转换成候选旁路。</summary>
    public CandidateObserver(ProtocolProfile profile, string captureSessionId, Action<CandidateObservation> emit,
        IReadOnlySet<ushort>? researchOpcodes = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureSessionId);
        ArgumentNullException.ThrowIfNull(emit);
        if (profile.Status != ProfileCompatibilityStatus.Candidate)
            throw new ArgumentException("候选观测器只接受 CANDIDATE 档案。", nameof(profile));
        _profile = profile;
        _sessionId = captureSessionId;
        _emit = emit;
        _researchOpcodes = researchOpcodes?.ToHashSet() ?? new HashSet<ushort>();
        _hypotheses = profile.Hypotheses.ToDictionary(h => (h.Direction, h.Opcode));
    }

    /// <summary>调用方持有管线锁；空集合立即阻止后续负载保存，不修改已经发出的证据。</summary>
    public void SetResearchOpcodes(IReadOnlySet<ushort> opcodes) => _researchOpcodes = opcodes.ToHashSet();

    /// <summary>生成元数据和可选白名单负载，不解析字段；跨会话、长度不符或混淆条目直接忽略。</summary>
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.CaptureSessionId != _sessionId || message.Mono < TimeSpan.Zero) return;
        var direction = message.Direction == MessageDirection.Inbound
            ? PacketDirection.ServerToClient : PacketDirection.ClientToServer;
        var t = (long)message.Mono.TotalMilliseconds;
        var tag = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_sessionId + "|" + message.ConnectionKey)))[..12].ToLowerInvariant();

        // The queue window sees every message on its connection, hypothesised or not: its
        // whole point is to catch the packet nobody has a name for yet (the pop itself).
        SampleQueueWindow(tag, direction, message, t);
        SampleDutyWindow(tag, direction, message, t);

        if (!_hypotheses.TryGetValue((direction, message.Opcode), out var hypothesis) ||
            !hypothesis.AcceptsLength(message.Payload.Length) || _profile.IsObfuscated(message.Opcode)) return;

        var observation = new CandidateObservation(
            Guid.NewGuid().ToString("D"), _sessionId, _profile.ProfileId, hypothesis.Name, hypothesis.Group,
            direction == PacketDirection.ServerToClient ? "S2C" : "C2S", message.Opcode,
            message.Payload.Length, Convert.ToHexString(SHA256.HashData(message.Payload.Span))[..12].ToLowerInvariant(),
            tag, message.ObservedAtUtc, t,
            PayloadHex: _researchOpcodes.Contains(message.Opcode) && message.Payload.Length <= ResearchPayloadPolicy.MaxPayloadBytes &&
                ResearchPayloadPolicy.IsEligible(_profile, hypothesis)
                ? Convert.ToHexString(message.Payload.Span).ToLowerInvariant() : null);
        if (hypothesis.Group == ZoneLoadGroup)
        {
            // Zone-load members are only evidence as a burst. Emitting each one on arrival
            // fills the ledger to its cap (C2S 0x01a2 is also the movement packet), so members
            // are held back until the burst is real.
            if (!string.IsNullOrWhiteSpace(message.ConnectionKey)) TrackZoneMember(tag, direction, message, t, observation);
            return;
        }

        _emit(observation);

        if (hypothesis.Name == QueueWindowTrigger && !string.IsNullOrWhiteSpace(message.ConnectionKey))
        {
            OpenQueueWindow(tag, t);
        }
        else if (hypothesis.Group == FinderGroup || hypothesis.Name == FinderActionName)
        {
            // A finder message ends a sampling segment: whatever arrived since the previous
            // boundary is written out now, so "the one-off S2C packet just before the state
            // update" is readable straight from the ledger.
            if (hypothesis.Group == FinderGroup && !string.IsNullOrWhiteSpace(message.ConnectionKey))
            {
                if (!_lastFinderTMs.ContainsKey(tag) && _lastFinderTMs.Count >= MaxConnections)
                    _lastFinderTMs.Remove(_lastFinderTMs.MinBy(pair => pair.Value).Key);
                _lastFinderTMs[tag] = t;
            }
            FlushQueueSegment(tag);
        }
    }

    private void TrackZoneMember(string tag, PacketDirection direction, DecodedMessage message, long t, CandidateObservation member)
    {
        if (!_zones.TryGetValue(tag, out var window))
        {
            if (_zones.Count >= MaxConnections)
                _zones.Remove(_zones.MinBy(pair => pair.Value.LastTMs).Key);
            window = new ZoneWindow();
            _zones.Add(tag, window);
        }
        if (t < window.LastTMs) return; // 不用乱序样本证明三秒窗口。
        if (t - window.LastTMs > ZoneWindowMs)
        {
            window.Members.Clear();
            window.Emitted = false;
        }
        window.LastTMs = t;
        foreach (var key in window.Members.Where(pair => t - pair.Value.TMs > ZoneWindowMs)
                     .Select(pair => pair.Key).ToArray()) window.Members.Remove(key);
        var isNewKey = !window.Members.ContainsKey((direction, message.Opcode));
        window.Members[(direction, message.Opcode)] = (t, message.ObservedAtUtc, member);
        if (window.Members.Count < ZoneThreshold)
        {
            // 旧簇消退后重新允许下一簇；单成员持续出现不能永久锁住锚点。
            window.Emitted = false;
            return;
        }
        if (window.Emitted)
        {
            // A member joining an already-emitted burst is recorded once per opcode; a
            // repeated opcode inside the same burst is not.
            if (isNewKey) _emit(member);
            return;
        }

        var first = window.Members.Values.MinBy(value => value.TMs);
        window.Emitted = true;
        foreach (var row in window.Members.Values.OrderBy(value => value.TMs)) _emit(row.Row);
        _emit(new CandidateObservation(
            Guid.NewGuid().ToString("D"), _sessionId, _profile.ProfileId, "ZONE_LOAD", "zone_load",
            "NONE", null, null, null, tag, message.ObservedAtUtc, t,
            first.AtUtc, message.ObservedAtUtc, first.TMs, t));

        // A zone load after the registration is the duty entry (or a teleport after a
        // cancel); either way the pop, if there was one, is already inside the window.
        if (_queueWindows.TryGetValue(tag, out var queue) && queue.StartTMs < first.TMs)
        {
            FlushQueueWindow(tag);
        }

        // Any zone change closes the duty window of this connection; one that follows a
        // finder state update within three minutes opens the next one.
        FlushDutyWindow(tag);
        if (_lastFinderTMs.TryGetValue(tag, out var finderT) && first.TMs >= finderT &&
            first.TMs - finderT <= DutyWindowFinderLeadMs)
        {
            if (_dutyWindows.Count >= MaxConnections)
                FlushDutyWindow(_dutyWindows.MinBy(pair => pair.Value.StartTMs).Key);
            _dutyWindows[tag] = new QueueWindow { StartTMs = t };
        }
    }

    /// <summary>Emits every open queue and duty window. Called when the capture session ends.</summary>
    public void Flush()
    {
        foreach (var tag in _queueWindows.Keys.ToArray()) FlushQueueWindow(tag);
        foreach (var tag in _dutyWindows.Keys.ToArray()) FlushDutyWindow(tag);
    }

    private void OpenQueueWindow(string tag, long t)
    {
        if (_queueWindows.ContainsKey(tag)) return; // A second registration extends nothing.
        if (_queueWindows.Count >= MaxConnections)
            FlushQueueWindow(_queueWindows.MinBy(pair => pair.Value.StartTMs).Key);
        _queueWindows.Add(tag, new QueueWindow { StartTMs = t });
    }

    /// <summary>
    /// Folds one message into the open window of its connection: one row per
    /// (direction, opcode, length), counting occurrences, keeping the first time and the
    /// digest of the first payload. Metadata only, never the payload -- the research
    /// whitelist is the only path to bytes and it does not apply here.
    /// </summary>
    private void SampleQueueWindow(string tag, PacketDirection direction, DecodedMessage message, long t)
    {
        if (!_queueWindows.TryGetValue(tag, out var window)) return;
        if (t - window.StartTMs > QueueWindowMaxMs)
        {
            FlushQueueWindow(tag);
            return;
        }

        SampleInto(window, direction, message, t);
    }

    /// <summary>
    /// Same aggregation for the stretch between entering a duty and the next zone change,
    /// so the packet that ends a duty (the result nobody has named yet) can be read off the
    /// ledger as "the S2C row seen once, last, just before the exit". Zone-load members are
    /// left out because the cluster already accounts for them.
    /// </summary>
    private void SampleDutyWindow(string tag, PacketDirection direction, DecodedMessage message, long t)
    {
        if (!_dutyWindows.TryGetValue(tag, out var window)) return;
        if (t - window.StartTMs > DutyWindowMaxMs)
        {
            FlushDutyWindow(tag);
            return;
        }
        if (_hypotheses.TryGetValue((direction, message.Opcode), out var hypothesis) &&
            hypothesis.Group == ZoneLoadGroup) return;

        SampleInto(window, direction, message, t);
    }

    private static void SampleInto(QueueWindow window, PacketDirection direction, DecodedMessage message, long t)
    {
        var key = (direction, message.Opcode, message.Payload.Length);
        if (window.Samples.TryGetValue(key, out var sample))
        {
            sample.Count++;
            sample.LastTMs = t;
            sample.LastAtUtc = message.ObservedAtUtc;
            return;
        }
        if (window.Samples.Count >= MaxQueueWindowKeys)
        {
            window.Overflow++;
            return;
        }
        window.Samples[key] = new Sample
        {
            FirstTMs = t,
            FirstAtUtc = message.ObservedAtUtc,
            LastTMs = t,
            LastAtUtc = message.ObservedAtUtc,
            Hash12 = Convert.ToHexString(SHA256.HashData(message.Payload.Span))[..12].ToLowerInvariant(),
            Count = 1,
        };
    }

    /// <summary>Writes the current segment of an open window and keeps the window open.</summary>
    private void FlushQueueSegment(string tag)
    {
        if (!_queueWindows.TryGetValue(tag, out var window)) return;
        EmitSamples(tag, window, QueueWindowSampleName, QueueWindowGroup);
        window.Samples.Clear();
        window.Overflow = 0;
    }

    private void FlushQueueWindow(string tag)
    {
        if (!_queueWindows.Remove(tag, out var window)) return;
        EmitSamples(tag, window, QueueWindowSampleName, QueueWindowGroup);
    }

    private void FlushDutyWindow(string tag)
    {
        if (!_dutyWindows.Remove(tag, out var window)) return;
        EmitSamples(tag, window, DutyWindowSampleName, DutyWindowGroup);
    }

    /// <summary>
    /// Writes out one window's aggregated rows, each carrying how many distinct keys the window
    /// had to drop. Without that count a window that hit <see cref="MaxQueueWindowKeys"/> looks
    /// exactly like a quiet one, which is the difference between "this opcode never appeared"
    /// and "we stopped looking" (review finding L-12). Every row of the window carries the same
    /// count, because the omission is a property of the window rather than of any one key.
    /// </summary>
    private void EmitSamples(string tag, QueueWindow window, string name, string group)
    {
        foreach (var pair in window.Samples.OrderBy(pair => pair.Value.FirstTMs))
        {
            var (direction, opcode, length) = pair.Key;
            _emit(new CandidateObservation(
                Guid.NewGuid().ToString("D"), _sessionId, _profile.ProfileId, name, group,
                direction == PacketDirection.ServerToClient ? "S2C" : "C2S", opcode, length, pair.Value.Hash12,
                tag, pair.Value.FirstAtUtc, pair.Value.FirstTMs,
                pair.Value.FirstAtUtc, pair.Value.LastAtUtc, pair.Value.FirstTMs, pair.Value.LastTMs,
                Occurrences: pair.Value.Count, OverflowCount: window.Overflow));
        }
    }

    private sealed class QueueWindow
    {
        public long StartTMs { get; init; }
        public int Overflow { get; set; }
        public Dictionary<(PacketDirection Direction, ushort Opcode, int Length), Sample> Samples { get; } = new();
    }

    private sealed class Sample
    {
        public long FirstTMs { get; init; }
        public DateTimeOffset FirstAtUtc { get; init; }
        public long LastTMs { get; set; }
        public DateTimeOffset LastAtUtc { get; set; }
        public string Hash12 { get; init; } = "";
        public int Count { get; set; }
    }

    private sealed class ZoneWindow
    {
        public long LastTMs { get; set; }
        public bool Emitted { get; set; }
        public Dictionary<(PacketDirection, ushort), (long TMs, DateTimeOffset AtUtc, CandidateObservation Row)> Members { get; } = new();
    }
}
