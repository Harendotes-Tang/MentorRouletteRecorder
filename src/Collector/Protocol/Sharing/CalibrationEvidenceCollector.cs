using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// 与正式校准观察器隔离的有界观察器：正式档案绑定后仍等待真实 capture stop 和最终健康报告，
/// 每个结束会话仅冻结一次。所有入口由 pipeline gate 串行调用，不读取磁盘 carried evidence。
/// </summary>
internal sealed class CalibrationEvidenceCollector
{
    private readonly CalibrationEvidenceReceiptStore _store;
    private readonly Dictionary<string, (DeclaredCandidate Candidate, int Epoch)> _candidates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _epochBlocked = new(StringComparer.Ordinal);
    private readonly Queue<string> _endedSessions = new();
    private CalibrationTemplate? _template;
    private Region _region;
    private string? _build;
    private string? _session;
    private string? _alias;
    private CaptureSessionHealth? _health;
    private CalibrationObserver? _observer;
    private bool _storageFailed;

    internal CalibrationEvidenceCollector(CalibrationEvidenceReceiptStore store) => _store = store;

    internal void Register(Region region, string build, CalibrationTemplate template, DeclaredCandidate candidate, int epoch)
    {
        if (region != template.Region || !CalibrationEvidenceDocument.Hash(candidate.CandidateId) || epoch < 0) return;
        if (_region != region || _build != build || _template?.Source.ProfileSha256 != template.Source.ProfileSha256)
        {
            _candidates.Clear();
            _epochBlocked.Clear();
            _observer = null;
            _region = region;
            _build = build;
            _template = template;
        }
        if (!_candidates.ContainsKey(candidate.CandidateId) && _candidates.Count >= CalibrationObserver.MaxCandidates) return;
        var known = _candidates.TryGetValue(candidate.CandidateId, out var previous);
        // Candidate tally reset does not reset outstanding requests/bursts inside the observer. A new epoch
        // therefore waits for an entirely new capture session; no pre-epoch context can support recovery.
        if (_session is not null && (!known || previous.Epoch != epoch))
            _epochBlocked.Add(candidate.CandidateId);
        _candidates[candidate.CandidateId] = (candidate, epoch);
        EnsureObserver();
        _observer?.RegisterCandidate(candidate);
    }

    /// <summary>释放当前观察槽及未完成计数；已经冻结的回执仍由存储保留。</summary>
    internal void Unregister(string candidateId)
    {
        _candidates.Remove(candidateId);
        _epochBlocked.Remove(candidateId);
        _observer?.UnregisterCandidate(candidateId);
    }

    /// <summary>当前范围已经接收的索引代际；未观察此候选时为空，不能拿其他区服/版本的代际兜底。</summary>
    internal int? RegisteredEpoch(Region region, string build, CalibrationTemplate template, string candidateId) =>
        _region == region && _build == build && _template?.Source.ProfileSha256 == template.Source.ProfileSha256 &&
        _candidates.TryGetValue(candidateId, out var candidate) ? candidate.Epoch : null;

    internal void Start(string session)
    {
        if (session == _session || _endedSessions.Contains(session, StringComparer.Ordinal)) return;
        // Starting again abandons an unfinished observation; it never completes the previous one.
        _session = session;
        _alias = Guid.NewGuid().ToString("D");
        _health = null;
        _epochBlocked.Clear();
        _observer = null;
        EnsureObserver();
    }

    private void EnsureObserver()
    {
        if (_observer is not null || _session is null || _template is null || _candidates.Count == 0) return;
        _observer = new CalibrationObserver(_template, _region, _session);
        foreach (var candidate in _candidates.Values) _observer.RegisterCandidate(candidate.Candidate);
    }

    internal void Accept(DecodedMessage message)
    {
        if (message.CaptureSessionId == _session) _observer?.Accept(message);
    }

    internal void Health(CaptureSessionHealth health)
    {
        if (health.CaptureSessionId != _session) return;
        _health = _health is null ? health : _health.Merge(health);
    }

    internal void Lost(long decoded = 0, long damaged = 0)
    {
        if (_session is not null) Health(new CaptureSessionHealth(_session,
            Capture.CaptureSilentReason.None, null, 0, decoded, damaged));
    }

    internal void Complete(string session, CaptureEndReason reason)
    {
        if (session != _session) return;
        var observer = _observer;
        _observer = null;
        _session = null;
        _endedSessions.Enqueue(session);
        while (_endedSessions.Count > 64) _endedSessions.Dequeue();
        if (observer is null || _health?.IsHealthy != true || reason is not (CaptureEndReason.UserStop or CaptureEndReason.ProcessExit)) return;
        observer.Flush();
        observer.RecordSessionHealth(_health);
        var snapshot = observer.Snapshot();
        if (snapshot.OverflowCount != 0 || snapshot.TimingOverflow != 0 || snapshot.MarkerOverflow != 0 ||
            snapshot.Clusters.Any(cluster => cluster.OverflowCount != 0)) return;
        _storageFailed = false;
        foreach (var item in _candidates.Values)
        {
            var candidate = item.Candidate;
            if (_epochBlocked.Contains(candidate.CandidateId)) continue;
            var counts = snapshot.Candidates.FirstOrDefault(observation => observation.CandidateId == candidate.CandidateId)
                ?.Sessions.GetValueOrDefault(session);
            if (counts is null || !counts.SightingsComplete) continue;
            var isolated = Isolate(snapshot, candidate, counts, session, _health);
            var verification = SharedCandidateVerifier.Verify(isolated, _template!, candidate, SharedCandidateProvenance.Imported);
            var checks = verification.Criteria.Select(criterion => new CalibrationEvidenceCheck(Field(criterion.Message),
                criterion.ContradictingSessions > 0 ? "CONTRADICT" : criterion.Verdict == SharedVerdict.Pass ? "PASS" : "WAIT")).ToArray();
            var frozen = new CalibrationEvidenceSession(_alias!, true, new("NONE", 0, 0, 0, 0, false), checks);
            _storageFailed |= !_store.Append(Target(candidate.CandidateId, item.Epoch), frozen);
        }
    }

    internal CalibrationEvidenceResult Export(Region region, string build, string? code)
    {
        if (_storageFailed) return CalibrationEvidenceResult.Unavailable("EVIDENCE_STORE_UNAVAILABLE");
        if (_region != region || _build != build || _template is null) return CalibrationEvidenceResult.Unavailable("NO_CURRENT_CANDIDATE");
        if (code is null)
        {
            if (_candidates.Count != 1) return CalibrationEvidenceResult.Unavailable(_candidates.Count == 0 ? "NO_CURRENT_CANDIDATE" : "SELECT_CANDIDATE");
            code = _candidates.Keys.Single();
        }
        if (!_candidates.TryGetValue(code, out var candidate)) return CalibrationEvidenceResult.Unavailable("CANDIDATE_NOT_OBSERVED");
        return _store.Find(Target(code, candidate.Epoch)) is { } document
            ? new(true, document.Serialize(), "READY")
            : CalibrationEvidenceResult.Unavailable("NO_COMPLETED_HEALTHY_SESSION");
    }

    private CalibrationEvidenceTarget Target(string code, int epoch) => new(EnumWire<Region>.Format(_region), _build!,
        _template!.Source.ProfileId, _template.Source.ProfileSha256, code, epoch);

    private static string Field(string name) => name switch
    {
        CalibratedShape.ZoneName => "zone", CalibratedShape.TerritoryName => "territory",
        CalibratedShape.JobName => "job", CalibratedShape.PopName => "match", _ => throw new InvalidOperationException("unknown criterion"),
    };

    /// <summary>Positive rules must see only this candidate's post-registration session, never an aggregate PASS.</summary>
    private static CalibrationSnapshot Isolate(CalibrationSnapshot snapshot, DeclaredCandidate candidate,
        DeclaredCandidateCounts counts, string session, CaptureSessionHealth health)
    {
        var tags = snapshot.ConnectionSessions.Where(pair => pair.Value == session).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var outside = new Dictionary<MessageKey, int> { [candidate.ZoneKey] = counts.ZoneOutside };
        if (candidate.TerritoryKey is { } territory) outside[territory] = counts.TerritoryOutside;
        return snapshot with
        {
            CaptureSessionId = session,
            Pairs = snapshot.Pairs.Where(pair => tags.Contains(pair.ConnectionTag) &&
                pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs) >= counts.ObservedFromUtc).ToArray(),
            Pops = Array.Empty<PopHit>(), RouletteEchoHits = Array.Empty<RouletteEchoHit>(), Markers = Array.Empty<MarkerCandidate>(),
            Clusters = snapshot.Clusters.Where(cluster => tags.Contains(cluster.ConnectionTag) && cluster.LoadStartedAtUtc >= counts.ObservedFromUtc).ToArray(),
            OutsideCounts = outside,
            SessionHealth = new Dictionary<string, CaptureSessionHealth>(StringComparer.Ordinal) { [session] = health },
            Candidates = new[] { new DeclaredCandidateObservation(candidate.CandidateId,
                new Dictionary<string, DeclaredCandidateCounts>(StringComparer.Ordinal) { [session] = counts }) },
            CarriedMessages = 0, SessionCount = 1,
        };
    }
}
