using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>What local traffic says about a shared calibration.</summary>
public enum SharedVerdict
{
    /// <summary>Every required message was seen doing what it should; the optional job message may still be waiting.</summary>
    Pass,

    /// <summary>Not enough evidence either way; keep observing.</summary>
    Wait,

    /// <summary>Two healthy capture sessions each observed something the declared message cannot explain.</summary>
    Contradicted,
}

/// <summary>What a criterion's verdict decides (plan §18.3).</summary>
public enum SharedGate
{
    /// <summary>Must pass before the candidate binds.</summary>
    Required,

    /// <summary>Judged after binding: a contradiction still withdraws the profile, waiting only keeps the watch on.</summary>
    Audit,

    /// <summary>Never holds anything back; a contradiction still rejects. The job message.</summary>
    Optional,
}

/// <summary>The verdict on one declared message.</summary>
/// <param name="Message">Semantic name of the message judged.</param>
/// <param name="Verdict">Pass, wait or contradicted.</param>
/// <param name="Reason">Plain-language reason, Chinese, no opcodes.</param>
/// <param name="ContradictingSessions">Healthy sessions that contradicted it, even when fewer than needed.</param>
/// <param name="Gate">Whether the verdict gates binding, is audited after it, or is optional.</param>
public sealed record SharedCriterion(
    string Message, SharedVerdict Verdict, string Reason, int ContradictingSessions = 0, SharedGate Gate = SharedGate.Required);

/// <summary>The verdict on a candidate as a whole, with the criterion behind it.</summary>
/// <param name="CandidateId">Identity of the candidate (its <c>code_sha256</c>).</param>
/// <param name="Verdict">Contradicted when any criterion is, pass when every required criterion is, wait otherwise.</param>
/// <param name="Criteria">One entry per declared message: pop, zone marker, then territory and job when declared.</param>
public sealed record SharedVerification(string CandidateId, SharedVerdict Verdict, IReadOnlyList<SharedCriterion> Criteria)
{
    /// <summary>True while an audited criterion is still waiting: the profile may record, and the watch stays on.</summary>
    public bool AuditPending => Criteria.Any(criterion => criterion.Gate == SharedGate.Audit && criterion.Verdict == SharedVerdict.Wait);
}

/// <summary>
/// Judges a shared calibration against this machine's own traffic.
///
/// Asymmetric on purpose. A pass may rest on any positive evidence, including evidence carried over
/// from an earlier run of the Collector. A contradiction rests only on absence or misbehaviour seen
/// by at least <see cref="SessionsToContradict"/> capture sessions that were healthy
/// (<see cref="CaptureSessionHealth.IsHealthy"/>), while no observation table overflowed, and only in
/// what arrived after the candidate was registered with the observer. Carried evidence belongs to no
/// live session and has no health, so it can never contradict anything.
///
/// Which criteria must pass before binding depends on where the code came from (plan §18.3). The
/// zone marker is always required: opcodes are reshuffled by every patch, so a code for another
/// build fails it at the first login. For a <see cref="SharedCandidateProvenance.Published"/> code that
/// is all: the pop and the duty entry are <see cref="SharedGate.Audit"/>ed while the profile records,
/// and a contradiction there withdraws it and flags what it recorded. For an
/// <see cref="SharedCandidateProvenance.Imported"/> code - pasted, and unknown to the index - they stay
/// required, so a code somebody edited by hand in a chat group records nothing until the match itself
/// has been seen to behave. The job is optional either way.
///
/// The criteria, one per declared message:
/// <list type="bullet">
/// <item><c>ZONE_INITIALIZATION</c> with a territory message: pass when one burst holds both exactly once with a
/// valid territory reading and both shapes stay within the draft's outside-a-burst tolerance; a session
/// contradicts when a burst holding the territory message has the marker twice, or not at all while
/// the marker was never seen outside a burst in that session, or the session's own outside count
/// breaks the tolerance.</item>
/// <item><c>ZONE_INITIALIZATION</c> alone: the same, on bursts not on the lobby connection, needing two such
/// bursts to pass; a session contradicts only when it has two or more such bursts and none of them
/// holds the marker exactly once (or it breaks the tolerance).</item>
/// <item><c>ZONE_TERRITORY</c>: pass when the first burst that named a known duty has a known-duty reading
/// on the declared opcode; a session contradicts when a known-duty burst lacks one (an absent message
/// only counts when the session never saw it outside a burst).</item>
/// <item><c>PLAYER_JOB</c>, optional - waiting on it never holds back a pass: pass when some burst vouches for it
/// (present at the template's length, every reading within the template's constraints and all agreeing) and at
/// least half of the complete bursts the healthy sessions observed since registration do; a session contradicts
/// only when one of its bursts holds a reading out of range or two readings that disagree. Absence never
/// contradicts it: neither the lobby handshake nor, on some builds, the login burst carries it, which is the
/// rule local calibration uses too (<see cref="CalibrationDraft"/>).</item>
/// <item><c>CONTENT_FINDER_POP</c> from the server: pass when it arrived carrying the roulette the connection
/// had queued (or earlier evidence shows the same); a session contradicts when a queue request was
/// followed within the template's match window by a known duty with no declared pop in between, or
/// when the declared pop arrived <see cref="UnrequestedPopsPerSession"/> or more times with no queue
/// outstanding. A duty entered after the window is ordinary play and only makes it wait.</item>
/// <item><c>CONTENT_FINDER_POP</c> from the client (queue inferred): pass when the declared request paired with
/// its echo; a session contradicts when its echo pairs are dominated by another request opcode that
/// paired at least twice on two different roulettes, and the declared one never paired.</item>
/// </list>
/// Pure and static: no clock, no IO.
/// </summary>
public static class SharedCandidateVerifier
{
    /// <summary>Healthy sessions that must each contradict a message before it is contradicted.</summary>
    public const int SessionsToContradict = 2;

    /// <summary>Unrequested sightings of the declared pop in one session that contradict it there.</summary>
    public const int UnrequestedPopsPerSession = 2;

    /// <summary>
    /// The candidate a payload declares on this machine, or null when the code is not for this
    /// template or cannot be built through it. Its id is the code's <c>code_sha256</c>.
    /// </summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="template">This machine's template.</param>
    public static DeclaredCandidate? Candidate(ShareCodePayload payload, CalibrationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(template);
        if (ShareCode.Check(payload) is not null || !SharedProfileBuilder.IsApplicable(payload, template) ||
            SharedProfileBuilder.ToValues(payload, template) is not { } values)
        {
            return null;
        }

        var shape = CalibratedShape.Messages(template, values);
        return shape.Error is null ? DeclaredCandidate.From(ShareCode.Sha256(payload), shape.Messages) : null;
    }

    /// <summary>Judges a candidate against a snapshot.</summary>
    /// <param name="snapshot">Observer snapshot, with session health and the candidate's counts.</param>
    /// <param name="template">Template the candidate was built through.</param>
    /// <param name="candidate">Candidate to judge.</param>
    /// <param name="provenance">Where the code came from; decides which criteria gate binding (plan §18.3).</param>
    public static SharedVerification Verify(
        CalibrationSnapshot snapshot, CalibrationTemplate template, DeclaredCandidate candidate, SharedCandidateProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(candidate);
        var evidence = new Evidence(snapshot, template, candidate);
        var afterBinding = provenance == SharedCandidateProvenance.Published ? SharedGate.Audit : SharedGate.Required;
        var criteria = new List<SharedCriterion>
        {
            (candidate.Pop.Direction == PacketDirection.ClientToServer ? QueuePop(evidence) : ServerPop(evidence)) with { Gate = afterBinding },
            candidate.Territory is null ? ZoneAlone(evidence) : ZoneWithTerritory(evidence),
        };
        if (candidate.Territory is not null)
        {
            criteria.Add(TerritoryEntry(evidence) with { Gate = afterBinding });
        }

        if (candidate.Job is not null)
        {
            // Optional for a profile, exactly as for local calibration: without it records carry an unknown job.
            criteria.Add(Job(evidence) with { Gate = SharedGate.Optional });
        }

        // Any contradiction rejects the code, whatever the gate. Only the required criteria hold binding back.
        var verdict = criteria.Any(criterion => criterion.Verdict == SharedVerdict.Contradicted)
            ? SharedVerdict.Contradicted
            : criteria.Where(criterion => criterion.Gate == SharedGate.Required).All(criterion => criterion.Verdict == SharedVerdict.Pass)
                ? SharedVerdict.Pass
                : SharedVerdict.Wait;
        return new SharedVerification(candidate.CandidateId, verdict, criteria);
    }

    private static SharedCriterion Judge(
        string message, bool pass, int contradicting, string passReason, string waitReason, string contradictedReason) =>
        contradicting >= SessionsToContradict
            ? new SharedCriterion(message, SharedVerdict.Contradicted,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, contradictedReason, contradicting), contradicting)
            : new SharedCriterion(message, pass ? SharedVerdict.Pass : SharedVerdict.Wait, pass ? passReason : waitReason, contradicting);

    // ------------------------------------------------------------------ zone marker

    private static SharedCriterion ZoneWithTerritory(Evidence evidence)
    {
        var zone = evidence.Candidate.ZoneKey;
        var territory = evidence.Candidate.TerritoryKey!.Value;
        var snapshot = evidence.Snapshot;
        var pass = snapshot.Clusters.Any(cluster =>
                Count(cluster, zone) == 1 && Count(cluster, territory) == 1 && ReadsValid(cluster, territory.Opcode)) &&
            CalibrationDraft.Behaves(snapshot, zone) && CalibrationDraft.Behaves(snapshot, territory);
        var contradicting = evidence.ObservedHealthySessions().Count(session =>
        {
            var clusters = evidence.ClustersIn(session.Id, session.Counts.ObservedFromUtc);
            return clusters.Any(cluster => Count(cluster, territory) >= 1 &&
                       (Count(cluster, zone) >= 2 || (Count(cluster, zone) == 0 && session.Counts.ZoneOutside == 0))) ||
                !Tolerated(session.Counts.ZoneOutside, clusters.Count(cluster => Count(cluster, zone) == 1)) ||
                !Tolerated(session.Counts.TerritoryOutside, clusters.Count(cluster => Count(cluster, territory) == 1));
        });
        return Judge(CalibratedShape.ZoneName, pass, contradicting,
            "换区报文与区域报文在同一次换区里各出现一次，区域编号合法，换区之外的出现次数在容差内。",
            "还没见到换区报文与区域报文在同一次换区里各出现一次；登录或换一次地图就能核实。",
            "在 {0} 个抓包完整的会话里，含区域报文的换区中这条换区报文缺席或重复，或者它在换区之外出现得太多。");
    }

    private static SharedCriterion ZoneAlone(Evidence evidence)
    {
        var zone = evidence.Candidate.ZoneKey;
        var snapshot = evidence.Snapshot;
        var pass = snapshot.Clusters.Count(cluster => cluster.Lobby == false && Count(cluster, zone) == 1) >= 2 &&
            CalibrationDraft.Behaves(snapshot, zone);
        var contradicting = evidence.ObservedHealthySessions().Count(session =>
        {
            var clusters = evidence.ClustersIn(session.Id, session.Counts.ObservedFromUtc)
                .Where(cluster => cluster.Lobby == false).ToArray();
            return (clusters.Length >= 2 && clusters.All(cluster =>
                       Count(cluster, zone) >= 2 || (Count(cluster, zone) == 0 && session.Counts.ZoneOutside == 0))) ||
                !Tolerated(session.Counts.ZoneOutside, clusters.Count(cluster => Count(cluster, zone) == 1));
        });
        return Judge(CalibratedShape.ZoneName, pass, contradicting,
            "换区报文在至少两次换区里各出现一次，换区之外的出现次数在容差内。",
            "还没见到换区报文在两次换区里各出现一次；换两次地图就能核实。",
            "在 {0} 个抓包完整的会话里，这条换区报文在换区中缺席或重复，或者它在换区之外出现得太多。");
    }

    // ------------------------------------------------------------------ territory and job

    private static SharedCriterion TerritoryEntry(Evidence evidence)
    {
        var territory = evidence.Candidate.TerritoryKey!.Value;
        var first = evidence.Snapshot.Clusters
            .Where(cluster => cluster.TerritoryHits.Count > 0)
            .OrderBy(cluster => cluster.LoadStartedAtUtc)
            .FirstOrDefault();
        var pass = first is not null && NamesDuty(first, territory);
        var contradicting = evidence.ObservedHealthySessions().Count(session =>
            evidence.ClustersIn(session.Id, session.Counts.ObservedFromUtc)
                .Any(cluster => cluster.TerritoryHits.Count > 0 && !NamesDuty(cluster, territory) &&
                    // Absent from the burst proves nothing if the session saw it just outside one.
                    (Count(cluster, territory) > 0 || session.Counts.TerritoryOutside == 0)));
        return Judge(CalibratedShape.TerritoryName, pass, contradicting,
            "第一次进入已知副本时，这条区域报文读出的编号在副本表里。",
            first is null
                ? "还没见到进入已知副本；进一次副本就能核实。"
                : "第一次进入已知副本时，这条区域报文没有读出副本表里的编号；再进一次副本看看。",
            "在 {0} 个抓包完整的会话里，进入已知副本时这条区域报文缺席，或者读出的编号不在副本表里。");
    }

    private static SharedCriterion Job(Evidence evidence)
    {
        var job = evidence.Candidate.JobKey!.Value;
        // A record needs only the job current on entry and on exit, and neither the lobby handshake nor, on some
        // builds, the login burst announces it (0.7.11). So a majority of bursts vouching is enough, and only a
        // reading no job message can produce speaks against it; a burst that lacks it proves nothing.
        bool Vouches(ZoneCluster cluster) => CalibrationDraft.VouchesForJob(cluster, job.Opcode, job.Direction, job.Length);
        var sessions = evidence.ObservedHealthySessions()
            .Select(session => evidence.ClustersIn(session.Id, session.Counts.ObservedFromUtc))
            .ToArray();
        var observed = sessions.SelectMany(clusters => clusters).ToArray();
        var pass = evidence.Snapshot.Clusters.Any(Vouches) && observed.Count(Vouches) * 2 >= observed.Length;
        var contradicting = sessions.Count(clusters => clusters.Any(cluster => CalibrationDraft.ContradictsJob(cluster, job.Opcode)));
        return Judge(CalibratedShape.JobName, pass, contradicting,
            "换区里见到过这条职业报文，取值合法、同一次换区里不变，而且开始核实以来至少一半的换区都带着它。",
            "还没在足够多的换区里见到这条职业报文（登录时那一次不带也正常）；它不影响启用。",
            "在 {0} 个抓包完整的会话里，有换区里这条职业报文读出的职业编号越界，或者同一次换区里前后不一致。");
    }

    // ------------------------------------------------------------------ pop

    private static SharedCriterion ServerPop(Evidence evidence)
    {
        var counts = evidence.Observation?.Sessions.Values ?? Enumerable.Empty<DeclaredCandidateCounts>();
        var pass = counts.Any(session => session.PopWithRequest > 0) || EarlierPopSupport(evidence);
        var requests = evidence.DominantPairs();
        var contradicting = evidence.ObservedHealthySessions().Count(session =>
            session.Counts.PopWithoutRequest >= UnrequestedPopsPerSession ||
            (session.Counts.SightingsComplete && DutyWithoutPop(evidence, session.Id, session.Counts, requests)));
        return Judge(CalibratedShape.PopName, pass, contradicting,
            "这条匹配报文在本机流量里跟在同一随机任务的排本之后出现过。",
            "还没见到这条匹配报文跟在排本之后出现；排一次随机任务就能核实。",
            "在 {0} 个抓包完整的会话里，排本后很快进了副本，这条匹配报文却没有出现，或者在没人排本时反复出现。");
    }

    private static SharedCriterion QueuePop(Evidence evidence)
    {
        var request = evidence.Candidate.Pop.Opcode;
        var snapshot = evidence.Snapshot;
        var pass = snapshot.Pairs.Any(pair => pair.RequestOpcode == request);
        // Like every other criterion: only sessions the candidate was watched in, and only pairs
        // whose request came after it was registered.
        var contradicting = evidence.ObservedHealthySessions().Count(session =>
        {
            var pairs = evidence.PairsIn(session.Id)
                .Where(pair => RequestAt(pair) >= session.Counts.ObservedFromUtc)
                .ToArray();
            if (pairs.Length == 0 || pairs.Any(pair => pair.RequestOpcode == request))
            {
                return false;
            }

            var largest = pairs.GroupBy(pair => pair.ReplyOpcode).OrderByDescending(group => group.Count()).First();
            if (CalibrationDraft.DominantRequest(largest) is not { } dominant || dominant == request)
            {
                return false;
            }

            // One pair can be a stray client message that happened to echo a small number. Only a
            // pairing the draft itself would lock on - two roulettes - speaks for "another opcode".
            var locked = largest.Where(pair => pair.RequestOpcode == dominant).ToArray();
            return locked.Length >= 2 && locked.Select(pair => pair.RouletteId).Distinct().Count() >= 2;
        });
        return Judge(CalibratedShape.PopName, pass, contradicting,
            "排本请求与服务器回执已经在本机流量里配对上。",
            "还没见到这条排本请求与回执配对；排一次随机任务就能核实。",
            "在 {0} 个抓包完整的会话里，排本回执对应的是另一条排本请求。");
    }

    /// <summary>
    /// True when, in one session, a queue request was followed within the match window by a known
    /// duty and no declared pop arrived in between. Only requests of the dominant request/echo
    /// pairing count, so a stray client message that happened to echo cannot start a chain.
    /// </summary>
    private static bool DutyWithoutPop(
        Evidence evidence, string session, DeclaredCandidateCounts counts, IReadOnlyList<FinderPairHit> requests)
    {
        var tags = evidence.TagsOf(session);
        var asked = requests
            .Where(pair => tags.Contains(pair.ConnectionTag))
            .Select(RequestAt)
            .Where(at => at >= counts.ObservedFromUtc)
            .Distinct()
            .OrderBy(at => at)
            .ToArray();
        var entries = evidence.ClustersIn(session, counts.ObservedFromUtc)
            .Where(cluster => cluster.TerritoryHits.Count > 0)
            .OrderBy(cluster => cluster.LoadStartedAtUtc)
            .ToArray();
        foreach (var entry in entries)
        {
            var before = asked.Where(at => at < entry.LoadStartedAtUtc).ToArray();
            if (before.Length == 0)
            {
                continue;
            }

            var request = before[^1];
            if (entry.LoadStartedAtUtc - request > evidence.Template.MatchWindow ||
                entries.Any(other => other.LoadStartedAtUtc > request && other.LoadStartedAtUtc < entry.LoadStartedAtUtc))
            {
                continue;
            }

            if (!counts.Sightings.Any(sighting => sighting.AtUtc > request && sighting.AtUtc < entry.LoadStartedAtUtc))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Positive evidence for the pop from the observer's own tables, which predate registration and
    /// may be carried from disk: a template-shaped pop with the declared state after a same-roulette
    /// request, the same announcement at the template's offset, or a marker at the declared offset.
    /// </summary>
    private static bool EarlierPopSupport(Evidence evidence)
    {
        var snapshot = evidence.Snapshot;
        var pop = evidence.Candidate.Pop;
        var templatePop = evidence.Template.Pop;
        if (pop.Field("roulette_id") is not { } roulette || templatePop.Field("roulette_id") is not { } templateRoulette)
        {
            return false;
        }

        var selectors = pop.Fields.Where(field => field.Role == ProfileFieldRole.Selector && field.Type != ProfileFieldType.Bytes)
            .ToArray();
        var atTemplateOffset = roulette.Offset == templateRoulette.Offset && roulette.Type == templateRoulette.Type;
        bool AfterRequest(string tag, long rouletteId, DateTimeOffset at) =>
            snapshot.Pairs.Any(pair => pair.ConnectionTag == tag && pair.RouletteId == rouletteId && pair.ReplyAtUtc < at);

        var fromPops = atTemplateOffset && pop.ExpectedLength == templatePop.ExpectedLength &&
            snapshot.Pops.Any(hit => hit.Opcode == pop.Opcode && !hit.WithinEcho &&
                selectors.All(field => hit.Selectors.Any(reading =>
                    string.Equals(reading.Field, field.Name, StringComparison.Ordinal) &&
                    field.Constraints.IsSatisfiedBy(reading.Value))) &&
                AfterRequest(hit.ConnectionTag, hit.RouletteId, hit.AtUtc));
        var fromEchoes = selectors.Length == 0 && atTemplateOffset &&
            snapshot.RouletteEchoHits.Any(hit => hit.Opcode == pop.Opcode && hit.Length == pop.ExpectedLength &&
                AfterRequest(hit.ConnectionTag, hit.RouletteId, hit.AtUtc));
        var fromMarkers = selectors.Length == 0 && roulette.Type == ProfileFieldType.U8 &&
            snapshot.Markers.Any(marker => marker.Opcode == pop.Opcode && marker.Length == pop.ExpectedLength &&
                marker.Offset == roulette.Offset && marker.Hits > 0);
        return fromPops || fromEchoes || fromMarkers;
    }

    // ------------------------------------------------------------------ helpers

    private static int Count(ZoneCluster cluster, MessageKey key) => cluster.Members.GetValueOrDefault(key);

    private static bool ReadsValid(ZoneCluster cluster, ushort opcode) =>
        cluster.TerritoryReadings.Any(reading => reading.Opcode == opcode && reading.Valid > 0 && reading.Invalid == 0);

    private static bool NamesDuty(ZoneCluster cluster, MessageKey territory) =>
        cluster.TerritoryHits.Any(hit => hit.Opcode == territory.Opcode && hit.Length == territory.Length);

    private static bool Tolerated(int outside, int marked) =>
        outside <= CalibrationDraft.MaxZoneOutside || outside * CalibrationDraft.ZoneOutsidePerBurst <= marked;

    private static DateTimeOffset RequestAt(FinderPairHit pair) =>
        pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs);

    /// <summary>The snapshot read the way every criterion needs it: by session, by health, from registration on.</summary>
    private sealed class Evidence
    {
        public Evidence(CalibrationSnapshot snapshot, CalibrationTemplate template, DeclaredCandidate candidate)
        {
            Snapshot = snapshot;
            Template = template;
            Candidate = candidate;
            Observation = snapshot.Candidates.FirstOrDefault(observation =>
                string.Equals(observation.CandidateId, candidate.CandidateId, StringComparison.Ordinal));
        }

        public CalibrationSnapshot Snapshot { get; }

        public CalibrationTemplate Template { get; }

        public DeclaredCandidate Candidate { get; }

        public DeclaredCandidateObservation? Observation { get; }

        /// <summary>Sessions whose absences count: healthy, with no observation table overflowed anywhere.</summary>
        public IEnumerable<string> HealthySessions() => Snapshot.OverflowCount != 0
            ? Enumerable.Empty<string>()
            : Snapshot.SessionHealth.Values.Where(health => health.IsHealthy).Select(health => health.CaptureSessionId);

        /// <summary>Healthy sessions in which this candidate was being counted.</summary>
        public IEnumerable<(string Id, DeclaredCandidateCounts Counts)> ObservedHealthySessions() =>
            HealthySessions()
                .Select(session => (Id: session, Counts: Observation?.Sessions.GetValueOrDefault(session)))
                .Where(session => session.Counts is not null)
                .Select(session => (session.Id, session.Counts!));

        public HashSet<string> TagsOf(string session) => Snapshot.ConnectionSessions
            .Where(pair => string.Equals(pair.Value, session, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        /// <summary>Complete bursts of one live session that loaded at or after <paramref name="from"/>.</summary>
        public ZoneCluster[] ClustersIn(string session, DateTimeOffset from)
        {
            var tags = TagsOf(session);
            return Snapshot.Clusters
                .Where(cluster => tags.Contains(cluster.ConnectionTag) && cluster.OverflowCount == 0 &&
                    cluster.LoadStartedAtUtc >= from)
                .ToArray();
        }

        public FinderPairHit[] PairsIn(string session)
        {
            var tags = TagsOf(session);
            return Snapshot.Pairs.Where(pair => tags.Contains(pair.ConnectionTag)).ToArray();
        }

        /// <summary>Pairs of the reply opcode most pairs share, narrowed to its dominant request; empty when nothing dominates.</summary>
        public IReadOnlyList<FinderPairHit> DominantPairs()
        {
            if (Snapshot.Pairs.Count == 0)
            {
                return Array.Empty<FinderPairHit>();
            }

            var largest = Snapshot.Pairs.GroupBy(pair => pair.ReplyOpcode).OrderByDescending(group => group.Count()).First();
            return CalibrationDraft.DominantRequest(largest) is { } request
                ? largest.Where(pair => pair.RequestOpcode == request).ToArray()
                : Array.Empty<FinderPairHit>();
        }
    }
}
