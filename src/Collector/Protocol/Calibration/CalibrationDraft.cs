using System.Globalization;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>Where a draft stands.</summary>
public enum CalibrationDraftStatus
{
    /// <summary>Evidence is still missing; keep playing.</summary>
    Observing,

    /// <summary>Every message is identified and every self-check passed; ask the user.</summary>
    Ready,

    /// <summary>The evidence contradicts the template or itself; more play will not help.</summary>
    Blocked,
}

/// <summary>Which kind of evidence named the message a run starts from.</summary>
public enum CalibrationMatchSource
{
    /// <summary>The queue reply carries a state that means "matched", as the template describes.</summary>
    ReplyState,

    /// <summary>A message of its own, found at the roulette offset the template declares.</summary>
    Announcement,

    /// <summary>A message of its own, found at an offset the traffic named.</summary>
    MarkerOffset,

    /// <summary>
    /// No announcement could be identified, so the queue request stands in for it: the run
    /// is created only when the duty they enter confirms the pending request.
    /// </summary>
    QueueRequest,
}

/// <summary>Candidates the user already rejected; excluded for the rest of the session.</summary>
/// <param name="PopOpcodes">Server opcodes rejected as the pop.</param>
/// <param name="ZoneKeys">Shapes rejected as the zone-change marker.</param>
public sealed record CalibrationRejections(IReadOnlySet<ushort> PopOpcodes, IReadOnlySet<MessageKey> ZoneKeys)
{
    /// <summary>Nothing rejected yet.</summary>
    public static CalibrationRejections None { get; } = new(new HashSet<ushort>(), new HashSet<MessageKey>());

    /// <summary>
    /// Server opcodes rejected as the timed announcement, kept apart from
    /// <see cref="PopOpcodes"/> on purpose. On a queue-inferred profile the pop is the player's
    /// own request and the announcement is an add-on beside it, so a popup line the player marks
    /// wrong must cost the draft its announcement and nothing else: the recording they already
    /// have rests on the request.
    /// </summary>
    public IReadOnlySet<ushort> TimedOpcodes { get; init; } = new HashSet<ushort>();
}

/// <summary>One line of the timeline the user is asked to confirm.</summary>
/// <param name="EventId">Stable id within the session.</param>
/// <param name="Kind">login, finder_request, pop, duty_enter, duty_exit or zone.</param>
/// <param name="TMs">Session-relative time.</param>
/// <param name="AtUtc">Wall-clock time.</param>
/// <param name="Label">Plain-language description, no opcodes.</param>
/// <param name="RouletteId">Roulette id the line carries, when any.</param>
/// <param name="TerritoryId">Territory id the line carries, when any.</param>
/// <param name="DutyName">Duty name the line carries, when any.</param>
/// <param name="RequiresConfirmation">Whether the user must mark it correct before the draft can be used.</param>
public sealed record CalibrationEvent(
    string EventId,
    string Kind,
    long TMs,
    DateTimeOffset AtUtc,
    string Label,
    long? RouletteId,
    long? TerritoryId,
    string? DutyName,
    bool RequiresConfirmation);

/// <summary>The four things the capture page shows as progress.</summary>
/// <param name="FinderRequestSeen">A roulette request was echoed by the server.</param>
/// <param name="PopSeen">A candidate has an earlier paired request and a unique subsequent known-duty entry.</param>
/// <param name="PopShapeSeen">A message of the pop's shape arrived at all, whatever it carried.</param>
/// <param name="ZoneClusters">Zone-load bursts closed so far.</param>
/// <param name="DutyEntrySeen">A known-duty burst uniquely supports the match within the template window.</param>
/// <param name="DutyExitSeen">A later burst followed the entry burst.</param>
/// <param name="DutyZoneSeen">A burst named a territory the duty table knows, whether or not it supports a match.</param>
/// <param name="JobSeen">The job message was identified; until then records carry no job.</param>
public sealed record CalibrationProgress(
    bool FinderRequestSeen,
    bool PopSeen,
    int ZoneClusters,
    bool DutyEntrySeen,
    bool DutyExitSeen,
    bool PopShapeSeen = false,
    bool DutyZoneSeen = false,
    bool JobSeen = false);

/// <summary>
/// What calibration proposes: the messages it identified, the timeline for the user to
/// confirm, and why it cannot propose more. A pure function of a snapshot and a template, so
/// it can be re-derived at any time and unit-tested without traffic.
/// </summary>
/// <param name="Status">Where the draft stands.</param>
/// <param name="Blockers">Plain-language reasons the draft is not ready, in display order.</param>
/// <param name="Progress">Progress indicators.</param>
/// <param name="Events">Timeline for the user.</param>
/// <param name="Messages">Identified messages with the template's fields and the new opcodes.</param>
/// <param name="FinderRequestOpcode">Client opcode of the roulette request, when identified.</param>
/// <param name="SampleCounts">Observation counts per evidence key, for the provenance section.</param>
/// <param name="ConfirmedRouletteIds">Roulette ids seen in the echoes and pops, for the provenance note.</param>
/// <param name="TemplateProfileId">Profile the shapes came from.</param>
public sealed partial record CalibrationDraft(
    CalibrationDraftStatus Status,
    IReadOnlyList<string> Blockers,
    CalibrationProgress Progress,
    IReadOnlyList<CalibrationEvent> Events,
    IReadOnlyList<ProfileMessage> Messages,
    ushort? FinderRequestOpcode,
    IReadOnlyDictionary<string, int> SampleCounts,
    IReadOnlyList<long> ConfirmedRouletteIds,
    string TemplateProfileId)
{
    /// <summary>
    /// What named the match. Everything but <see cref="CalibrationMatchSource.QueueRequest"/>
    /// identifies the server's own announcement; that one infers the match from the player's
    /// own request and the duty that followed it, and says so everywhere it is shown.
    /// </summary>
    public CalibrationMatchSource MatchSource { get; init; } = CalibrationMatchSource.ReplyState;

    /// <summary>
    /// Bursts that must contain the zone marker exactly once before it may be declared, and the
    /// number of bursts that must exist before the question is asked at all.
    ///
    /// Counted over bursts that contain the marker, not over every burst seen: logging in opens
    /// a burst of its own on the lobby connection, and the zone marker comes from the game
    /// server, so it is never in there. The template's exact length and no outside occurrence
    /// are still required. The count is necessary but not sufficient: the selected duty entry
    /// and exit must each contain it exactly once, so three lobby bursts cannot supply an
    /// unrelated marker.
    /// </summary>
    public const int MinClusters = 3;

    /// <summary>
    /// Occurrences per session above which an uncorroborated shape cannot be the request or
    /// the pop. A duty finder status opcode can legitimately repeat while a long queue ticks
    /// over, so the ceiling only ever removes candidates that nothing else supports
    /// (review finding M-4).
    /// </summary>
    public const int MaxCandidateOccurrences = 2000;

    /// <summary>
    /// How many zone loads a shape must mark for each sighting outside one before it can still
    /// be the zone marker.
    ///
    /// "Never outside a load" is too sharp for a boundary the software draws itself: a burst
    /// reaches back three seconds from its first large message, so a load whose marker arrives
    /// earlier has that marker filed as an outsider. The longer the player plays, the likelier
    /// one slow load is, so a single outsider must not rule out the true marker.
    ///
    /// The length still comes from the template and the shape must still appear exactly once in
    /// the duty it opened and the duty it closed, so this only forgives the boundary, never the
    /// behaviour: a message that genuinely travels on its own is outside far too often to pass.
    /// </summary>
    public const int ZoneOutsidePerBurst = 3;

    /// <summary>
    /// Sightings outside a load a zone marker may have before the ratio is even consulted.
    ///
    /// A floor, not a ceiling: as an absolute cap it would undo the tolerance above, throwing
    /// out a marker seen inside eighteen of twenty-eight zone loads and outside three. The
    /// ratio scales; this only stops a shape seen twice in total from qualifying on a
    /// technicality.
    /// </summary>
    public const int MaxZoneOutside = 2;

    /// <summary>Two echoes must be at least this far apart to count as two.</summary>
    public static readonly TimeSpan PairSeparation = TimeSpan.FromSeconds(1);

    /// <summary>The exit burst must follow the entry burst by at least this much.</summary>
    public static readonly TimeSpan MinDutyDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The client opcode that paired with one server opcode often enough to be its request,
    /// or null when nothing has a strict majority.
    ///
    /// A strict majority is the whole test. One request opcode against a single stray is
    /// decided; two opcodes with two pairs each is a genuine ambiguity, and calibration says
    /// so rather than tossing a coin.
    /// </summary>
    /// <param name="pairs">Pairs that share a reply opcode.</param>
    internal static ushort? DominantRequest(IEnumerable<FinderPairHit> pairs)
    {
        var counts = pairs.GroupBy(pair => pair.RequestOpcode)
            .Select(group => (Opcode: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ToArray();
        if (counts.Length == 0)
        {
            return null;
        }

        var total = counts.Sum(entry => entry.Count);
        return counts[0].Count * 2 > total ? counts[0].Opcode : null;
    }

    /// <summary>
    /// How long a queue may stand before the duty that follows it stops being explained by it.
    /// The match window measures announcement to loading screen and is counted in seconds; a
    /// queue is counted in minutes, and a mentor roulette queued as a damage dealer in tens of
    /// them. One hour is the profile format's ceiling and comfortably covers the worst wait.
    /// </summary>
    public static readonly TimeSpan QueueWindow = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Distinct roulettes a scanned position must have tracked before it can be the
    /// announcement. One is worth nothing: a byte that happens to equal the id the player
    /// queued stays equal to it for as long as that queue stands. Two means the position
    /// followed a number that changed, which ordinary traffic cannot do.
    /// </summary>
    public const int MinMarkerRoulettes = 2;

    /// <summary>Derives the draft for a snapshot.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the shapes.</param>
    /// <param name="rejections">Candidates the user already rejected.</param>
    /// <param name="roulettes">Roulette names for the timeline.</param>
    public static CalibrationDraft Derive(
        CalibrationSnapshot snapshot,
        CalibrationTemplate template,
        CalibrationRejections? rejections = null,
        RouletteCatalog? roulettes = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(template);
        rejections ??= CalibrationRejections.None;
        roulettes ??= RouletteCatalog.Default;

        var blockers = new List<string>();
        var blocked = false;
        var messages = new List<ProfileMessage>();
        var samples = new Dictionary<string, int>(StringComparer.Ordinal);
        var region = template.Region;

        // 1. The pop: a request/echo pair locked on one opcode pair.
        var pairs = snapshot.Pairs.Where(pair => !rejections.PopOpcodes.Contains(pair.ReplyOpcode)).ToArray();
        var matches = pairs.GroupBy(pair => pair.ReplyOpcode).ToDictionary(group => group.Key,
            group => MatchedState.From(snapshot.Pops.Where(pop => pop.Opcode == group.Key).ToArray(),
                snapshot.Clusters, group.ToArray(), template.MatchWindow));
        var lockedReply = LockPop(snapshot, pairs, matches, out var lockReason, out var ambiguous);
        var matched = lockedReply is { } reply ? matches[reply] : MatchedState.None;
        // Contradictory positive duty evidence must remain visible even when it cannot
        // corroborate an opcode or grant a frequency exemption.
        if (lockedReply is null && matches.Values.Any(match => match.Contradicted))
        {
            matched = MatchedState.None with { Contradicted = true };
        }
        // The template was written on a build where the queue reply and the match announcement
        // were two states of one message; later builds send them separately, the reply opcode
        // emitting only the echoes each request earns. So when the reply carries no match, the
        // announcement is looked for as a message of its own. The reply path answers first, so
        // a build that kept the old structure is untouched.
        var searchable = matched.Pops.Length == 0 && !matched.Ambiguous && !matched.Contradicted;
        var announcement = searchable ? LockAnnouncement(snapshot, template, pairs, matches.Keys, rejections) : null;
        // Both paths above read the one byte offset the template's pop declares. A build that
        // splits the reply and the announcement gives the announcement a structure the template
        // has never seen, and nothing says its roulette id sits where the old one's did. The
        // marker scan lets the traffic name the offset instead.
        var marker = searchable && announcement is null
            ? LockMarker(snapshot, template, pairs, matches.Keys, rejections)
            : null;
        // Last of all, the match can be inferred rather than read: the player asked for a
        // roulette and then entered a duty, which is the same chain the announcement would have
        // corroborated, minus the server saying so. It is weaker on purpose and labelled
        // everywhere it appears, and it exists so that a build whose announcement nobody can
        // find still records mentor roulettes instead of recording nothing at all.
        var queued = searchable && announcement is null && marker is null && lockedReply is not null
            ? InferFromQueue(snapshot, pairs, lockedReply.Value)
            : null;
        // Last of all, and only on top of the inferred match: the server's announcement
        // recognised by when it arrives rather than by anything it carries. It adds the moment
        // the popup appeared; the roulette still comes from the request above.
        var timed = queued is null
            ? null
            : LockTimedAnnouncement(snapshot, template, queued.Chains, new HashSet<ushort>(matches.Keys), rejections);
        var genuinePops = announcement?.Pops ?? marker?.Pops ?? queued?.Pops ?? matched.Pops;
        var source = announcement is not null
            ? CalibrationMatchSource.Announcement
            : marker is not null
                ? CalibrationMatchSource.MarkerOffset
                : queued is not null
                    ? CalibrationMatchSource.QueueRequest
                    : CalibrationMatchSource.ReplyState;
        ushort? requestOpcode = null;
        if (ambiguous)
        {
            blocked = true;
            blockers.Add("有两条报文都像轮盘申请的回执，分不清哪条是弹窗；请点下面的「重新观察」，再打一把随机任务。");
        }
        else if (matched.Ambiguous)
        {
            // More than one state or duty entry can explain the chain. Neither frequency
            // nor connection preference establishes which interpretation is correct.
            blocked = true;
            blockers.Add("这一版里有好几种报文都可能是「匹配成功」，本机分不出是哪一种；请点下面的「重新观察」再打一把，还是这样就说明这一版需要新版本的软件来支持。");
        }
        else if (matched.Contradicted)
        {
            // Every duty-supported candidate carried a state the server also sends as a
            // plain reply to the request. Nothing in the traffic separates "queued" from
            // "matched", so there is no honest constant to write.
            blocked = true;
            blockers.Add("这一版里「排队中」和「匹配成功」的报文长得一样，本机分不出来；这一版需要新版本的软件来支持，之前的记录不受影响。");
        }
        else if (lockedReply is null)
        {
            blockers.Add(lockReason ?? "在游戏里申请一次随机任务。");
        }
        else if (genuinePops.Length == 0)
        {
            // The request/echo pair already names the opcode; what is still missing is one
            // real match, which is the only thing that proves what "matched" looks like on
            // this build. Nothing else the player can do substitutes for it.
            blockers.Add("等这次申请匹配成功、进入副本，进本之后就可以开始记录了；想让判定更准，之后再打一把别的随机任务。");
        }
        else
        {
            var lockedPairs = pairs.Where(pair => pair.ReplyOpcode == lockedReply.Value).ToArray();
            // LockPop only ever returns a reply opcode whose pairs have a dominant request
            // opcode, so this cannot be null; the stray pairs are dropped with it.
            var request = DominantRequest(lockedPairs)!.Value;
            requestOpcode = request;
            lockedPairs = lockedPairs.Where(pair => pair.RequestOpcode == request).ToArray();
            messages.Add(CalibratedShape.Pop(template, announcement is { } found
                ? new CalibratedPop(CalibrationMatchSource.Announcement, found.Shape.Opcode, Length: found.Shape.Length)
                : marker is { } located
                    ? new CalibratedPop(CalibrationMatchSource.MarkerOffset, located.Shape.Opcode,
                        Length: located.Shape.Length, RouletteOffset: located.Offset)
                    : queued is not null
                        ? new CalibratedPop(CalibrationMatchSource.QueueRequest, request)
                        : new CalibratedPop(CalibrationMatchSource.ReplyState, lockedReply.Value,
                            Selectors: matched.Selectors)));
            samples["messages.CONTENT_FINDER_POP.opcode"] = lockedPairs.Length + genuinePops.Length;
            samples[ProfileLoader.CalibrationEvidenceKey] = lockedPairs.Length;
            if (timed is { } announced)
            {
                messages.Add(CalibratedShape.Announced(announced.Shape.Opcode, announced.Shape.Length));
                samples["messages." + CalibratedShape.AnnouncedName + ".opcode"] = announced.Samples.Count;
            }
        }

        // 2. Zone-load bursts, and which of them is the duty.
        var clusters = snapshot.Clusters;
        ZoneCluster? entry = announcement?.Entry ?? marker?.Entry ?? queued?.Entry ?? matched.Entry;
        var exit = entry is null ? null : FindExit(entry, clusters);

        if (clusters.Count == 0)
        {
            blockers.Add("先登录进入游戏。");
        }

        if (entry is not null && exit is null)
        {
            blockers.Add("打完这把副本，离开后再回来。");
        }

        // "换区次数不够" would only restate the two lines above once a duty is in play; it is
        // worth saying only when the player has finished a duty and the bursts still do not
        // add up, which means something was missed rather than not done yet.
        if (exit is not null && clusters.Count < MinClusters)
        {
            blockers.Add("再走一次完整的流程：登录、进本、出本各算一次换区。");
        }

        if (snapshot.OverflowCount > 0)
        {
            blocked = true;
            // Re-logging keeps the evidence on purpose, so it keeps the overflow too; only
            // 重新观察 builds a new observer (review finding H-2).
            blockers.Add("这次观察到的报文种类超出上限，换区报文无法确认；请点下面的「重新观察」重来一次。");
        }

        // 3. The zone marker: the template's length, once per burst, never outside one.
        var zoneLength = template.ZoneInitialization.ExpectedLength!.Value;
        if (clusters.Count >= MinClusters && snapshot.OverflowCount == 0 && entry is not null && exit is not null)
        {
            var zoneCandidates = ZoneShapes(snapshot, zoneLength)
                .Where(key => !rejections.ZoneKeys.Contains(key))
                .Where(key => MarksEnough(snapshot, key))
                .Where(key => OnceIn(entry, key) && OnceIn(exit, key))
                .ToArray();
            if (zoneCandidates.Length == 1)
            {
                messages.Add(CalibratedShape.Zone(template, zoneCandidates[0].Opcode));
                samples["messages.ZONE_INITIALIZATION.opcode"] = ExactlyOnceCount(clusters, zoneCandidates[0]);
            }
            else if (zoneCandidates.Length > 1)
            {
                blocked = true;
                blockers.Add("有多条报文都像换区标记，分不清；请点下面的「重新观察」重来一次，仍然如此请导出证据交给维护者。");
            }
            else if (NearlyZone(snapshot, zoneLength, rejections, entry, exit))
            {
                // The shape is there and behaves, it has just not been through enough zone
                // changes yet. Logging in only proves one of them; entering and leaving a duty
                // proves the other two, and one of the bursts on record is the lobby handshake,
                // which the game server's marker is never part of.
                blockers.Add("再走一次完整的流程：登录、进本、出本各算一次换区。");
            }
            else if (Contaminated(snapshot, zoneLength, rejections))
            {
                // The shape is in a burst but has also been seen on its own. On a clean capture
                // that disqualifies a zone marker; on a lossy one it is a trap, because a zone
                // load whose packets went missing never opens a burst, so the marker inside it
                // is filed as "travelled outside a load". The message must therefore not say
                // the build changed: that would send the player away to wait for a new version
                // of software that already works.
                blockers.Add("换区报文对不上：有一条在换区之外也出现过好几次，还不能当成换区标记。" +
                             "再完整走一遍（进本、出本），中间来回传送两次城；如果还是这样，请点右下角导出诊断报告。");
            }
            else
            {
                blocked = true;
                blockers.Add("这一版的换区报文和上一版长得不一样，本机认不出来；这一版需要新版本的软件来支持，之前的记录不受影响。");
            }

            if (template.ZoneTerritory is { ExpectedLength: { } territoryLength } && entry is not null)
            {
                var territoryCandidates = OncePerCluster(snapshot, territoryLength)
                    .Where(key => entry.TerritoryHits.Any(hit => hit.Opcode == key.Opcode && hit.Length == key.Length))
                    .Where(key => OnceIn(entry, key) && OnceIn(exit, key))
                    .ToArray();
                if (territoryCandidates.Length == 1)
                {
                    messages.Add(CalibratedShape.Territory(template, territoryCandidates[0].Opcode));
                    samples["messages.ZONE_TERRITORY.opcode"] =
                        clusters.Count(cluster => cluster.TerritoryHits.Any(hit => hit.Opcode == territoryCandidates[0].Opcode));
                }
            }

            if (template.PlayerJob is { ExpectedLength: { } jobLength } jobTemplate &&
                entry is not null && exit is not null)
            {
                // A record only ever needs the job that was current on entry and on exit, so
                // those two bursts must vouch for the shape and the rest need only agree by
                // majority: the lobby handshake does not announce the job on every build, and
                // requiring it in every burst records every run on such a build as 职业未知.
                // A burst that contradicts the shape -- a reading outside the constraints, or
                // two readings that disagree -- still rules it out; a stray reading outside
                // every burst does not, and is counted for the report only.
                var jobDirection = jobTemplate.Direction;
                var jobCandidates = clusters
                    .SelectMany(cluster => cluster.JobValues.Keys)
                    .Distinct()
                    .Where(opcode => VouchesForJob(entry, opcode, jobDirection, jobLength) &&
                                     VouchesForJob(exit, opcode, jobDirection, jobLength))
                    .Where(opcode => !clusters.Any(cluster => ContradictsJob(cluster, opcode)))
                    .Where(opcode => clusters.Count(cluster => VouchesForJob(cluster, opcode, jobDirection, jobLength)) * 2 >= clusters.Count)
                    .OrderBy(opcode => opcode)
                    .ToArray();
                // Some builds announce the job on two messages at once (CN 2026.09.15: two opcodes,
                // the same bursts, the same value through four class changes). Candidates that never
                // read a different job in any burst are one answer, and demanding a single opcode
                // left every record on such a build 职业未知. The lowest opcode is taken so that
                // every machine on the build writes the same profile and the same share code.
                if (jobCandidates.Length > 1 && JobReadingsAgree(clusters, jobCandidates))
                {
                    jobCandidates = jobCandidates[..1];
                }

                if (jobCandidates.Length == 1)
                {
                    messages.Add(CalibratedShape.Job(template, jobCandidates[0]));
                    samples["messages.PLAYER_JOB.opcode"] =
                        clusters.Count(cluster => VouchesForJob(cluster, jobCandidates[0], jobDirection, jobLength));
                }
            }
        }

        // 4. Timeline.
        var events = BuildEvents(
            snapshot, template, roulettes, region, lockedReply, pairs, genuinePops, entry, exit, messages, source,
            timed);
        // Entry and exit say whether a burst corroborated the match, so on their own they read
        // as "this software never noticed you were in a duty". DutyZoneSeen is the plainer fact
        // underneath, so the card can separate "we did not see you enter" from "we saw it and
        // could not tie it to the roulette".
        var progress = new CalibrationProgress(
            pairs.Length > 0,
            genuinePops.Length > 0,
            clusters.Count,
            entry is not null,
            exit is not null,
            snapshot.PopShapeSeen > 0,
            clusters.Any(cluster => cluster.TerritoryHits.Count > 0),
            messages.Any(message => message.Name == "PLAYER_JOB"));
        var confirmed = pairs.Where(pair => pair.ReplyOpcode == lockedReply)
            .Select(pair => pair.RouletteId)
            .Concat(genuinePops.Select(pop => pop.RouletteId))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        // A profile that infers the match from the queue can only tell a duty from a teleport
        // by the territory it lands in, so without ZONE_TERRITORY it can never enter one: the
        // run starts when the player queues and then sits at "matched" for ever, whatever they
        // play. Such a profile is worse than none, so it is not offered; calibration keeps
        // looking instead.
        var needsTerritory = CalibratedShape.RequiresTerritory(source);
        var ready = blockers.Count == 0 && CalibratedShape.IsComplete(source, messages);
        if (!blocked && !ready && needsTerritory && blockers.Count == 0)
        {
            blockers.Add("还差一样：本机还没认出「这次换区进的是哪个副本」那条报文。" +
                         "再进出一次副本就有机会认出来——在那之前就算开始记录，也只会记成「未知副本」。");
        }

        var status = blocked
            ? CalibrationDraftStatus.Blocked
            : ready
                ? CalibrationDraftStatus.Ready
                : CalibrationDraftStatus.Observing;
        return new CalibrationDraft(
            status,
            blockers,
            progress,
            events,
            status == CalibrationDraftStatus.Ready ? messages : Array.Empty<ProfileMessage>(),
            status == CalibrationDraftStatus.Ready ? requestOpcode : null,
            samples,
            confirmed,
            template.Source.ProfileId)
        {
            MatchSource = source,
            TimedAnnouncement = status == CalibrationDraftStatus.Ready ? timed : null,
        };
    }

    private static ushort? LockPop(
        CalibrationSnapshot snapshot,
        FinderPairHit[] pairs,
        IReadOnlyDictionary<ushort, MatchedState> matches,
        out string? reason,
        out bool ambiguous)
    {
        reason = null;
        ambiguous = false;
        if (pairs.Length == 0)
        {
            reason = "在游戏里申请一次随机任务。";
            return null;
        }

        var qualifying = new List<ushort>();
        foreach (var group in pairs.GroupBy(pair => pair.ReplyOpcode))
        {
            var reply = group.Key;
            // Two client opcodes pairing with one reply opcode is not by itself an ambiguity:
            // on the 2026-09-01 client one request earns two replies, so a client message that
            // happens to carry the same small number in the same second can pair with the spare
            // one. A dominant request opcode decides; a single stray sighting does not.
            if (DominantRequest(group) is not { } dominant)
            {
                continue;
            }

            var ordered = group.Where(pair => pair.RequestOpcode == dominant)
                .OrderBy(pair => pair.ReplyAtUtc).ToArray();
            var distinctValues = ordered.Select(pair => pair.RouletteId).Distinct().Count();
            var separated = ordered.Length >= 2 && ordered[^1].ReplyAtUtc - ordered[0].ReplyAtUtc >= PairSeparation;
            var corroborated = matches[reply].Pops.Length > 0 || matches[reply].Ambiguous;
            var supported = (distinctValues >= 2 && separated) || corroborated;
            if (!supported)
            {
                continue;
            }

            // A shape that travels hundreds of times a session is ordinary traffic that
            // happened to echo a byte; one that is also a genuine pop is not.
            if (!corroborated &&
                (TooFrequent(snapshot, PacketDirection.ServerToClient, reply) ||
                 TooFrequent(snapshot, PacketDirection.ClientToServer, group.First().RequestOpcode)))
            {
                continue;
            }

            qualifying.Add(reply);
        }

        if (qualifying.Count == 1)
        {
            return qualifying[0];
        }

        if (qualifying.Count > 1)
        {
            ambiguous = true;
            return null;
        }

        // "等这次匹配成功" is wrong advice for a player who already matched, entered and left.
        // When a burst has already named a duty, the thing that did not arrive is the match
        // message itself, and the only move left is another roulette.
        reason = snapshot.Clusters.Any(cluster => cluster.TerritoryHits.Count > 0)
            ? "已经看到你进过副本，但一直没等到对应的匹配成功报文。还差一步：去申请一个和刚才不一样的随机任务，" +
              "看到开始排队就可以马上取消，不用真的打完，也不要关掉本软件；只要申请这一下，" +
              "本机就能认出排本报文，接着就会请你核对。"
            : "再申请一个别的随机任务（申请完可以马上取消），或者等这次匹配成功。";
        return null;
    }

    /// <summary>The announcement learned as a message of its own, with the samples that named it.</summary>
    /// <param name="Shape">Opcode and length of the announcement.</param>
    /// <param name="Pops">One sample per duty entry, in order.</param>
    /// <param name="Entry">The entry the first sample supports.</param>
    private sealed record AnnouncedMatch(MessageKey Shape, PopHit[] Pops, ZoneCluster Entry);

    /// <summary>
    /// Finds the message that announces a match when the queue reply never carries one.
    ///
    /// The rule is the same one the reply path uses, applied to a shape the template does not
    /// name: it answers a request the player made on the connection they made it on, it arrives
    /// outside any load, and it precedes EVERY known-duty entry inside the match window. That
    /// last clause is necessary and, on a real machine, not sufficient - a duty load is escorted
    /// by traffic, and any of it may carry a small id at the right offset.
    ///
    /// What separates them, without asking anything of the player, is where the shape lives: a
    /// message belonging to a zone load travels in every load - duty entries, teleports, logins
    /// - while the announcement of a match is never inside one. So a shape that is a member of
    /// any burst is not a candidate, whatever a particular sample of it did.
    ///
    /// Only if that still leaves a tie does the last test apply: the announcement arrives when
    /// the player refuses the match, leaving no duty behind it at all, which an escort cannot do.
    /// It is deliberately last, because refusing a match costs the player a duty-finder penalty
    /// and a calibration must not require one.
    ///
    /// Before any of that, the shape must not have contradicted itself. Every test above looks
    /// only at the sightings that carried a requested roulette, and a list that counts through
    /// the small numbers - the retainer bell's rows carry their slot, 0 to 9, at this very byte
    /// on the CN 2026.09.15 client - always has one row that does. An announcement carries the
    /// queued roulette every time it is sent while the queue stands; the marker scan counted
    /// exactly that, so a shape it saw carrying anything else is refused (<see cref="Disagrees"/>).
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the match window and the roulette field.</param>
    /// <param name="pairs">Request/echo pairs, for the requests an announcement can answer.</param>
    /// <param name="replyOpcodes">Opcodes a pair vouched for; the queue reply is not the pop.</param>
    /// <param name="rejections">Candidates the user, or the traffic, already rejected.</param>
    private static AnnouncedMatch? LockAnnouncement(
        CalibrationSnapshot snapshot, CalibrationTemplate template, FinderPairHit[] pairs,
        IEnumerable<ushort> replyOpcodes, CalibrationRejections rejections)
    {
        var entries = snapshot.Clusters.Where(cluster => cluster.TerritoryHits.Count > 0).ToArray();
        if (entries.Length == 0 || pairs.Length == 0)
        {
            return null;
        }

        var replies = new HashSet<ushort>(replyOpcodes);
        var hits = snapshot.RouletteEchoHits
            .Where(hit => !replies.Contains(hit.Opcode) && !rejections.PopOpcodes.Contains(hit.Opcode))
            .Where(hit => !snapshot.Clusters.Any(cluster =>
                hit.AtUtc >= cluster.LoadStartedAtUtc && hit.AtUtc <= cluster.EndedAtUtc))
            .Where(hit => pairs.Any(pair => pair.ConnectionTag == hit.ConnectionTag &&
                pair.RouletteId == hit.RouletteId && pair.ReplyAtUtc < hit.AtUtc))
            .ToArray();
        var candidates = hits
            .GroupBy(hit => new MessageKey(PacketDirection.ServerToClient, hit.Opcode, hit.Length))
            .Select(group => (Shape: group.Key, Hits: group.ToArray()))
            .Where(candidate => !Disagrees(snapshot, template, candidate.Shape))
            .Where(candidate => !snapshot.Clusters.Any(cluster => cluster.Members.ContainsKey(candidate.Shape)))
            .Where(candidate => entries.All(entry => candidate.Hits.Any(hit => Precedes(hit, entry, template))))
            .ToArray();
        if (candidates.Length > 1)
        {
            candidates = candidates
                .Where(candidate => candidate.Hits.Any(hit => !entries.Any(entry => Precedes(hit, entry, template))))
                .ToArray();
        }

        if (candidates.Length != 1)
        {
            return null;
        }

        var chosen = candidates[0];
        var supporting = entries
            .Select(entry => (Entry: entry, Hit: chosen.Hits
                .Where(hit => Precedes(hit, entry, template))
                .OrderByDescending(hit => hit.AtUtc)
                .First()))
            .OrderBy(support => support.Hit.AtUtc)
            .ToArray();
        var pops = supporting
            .Select(support => new PopHit(
                support.Hit.ConnectionTag, support.Hit.Opcode, support.Hit.RouletteId, support.Hit.TMs,
                support.Hit.AtUtc, false, Array.Empty<CalibrationSelectorReading>()))
            .ToArray();
        return new AnnouncedMatch(chosen.Shape, pops, PreferredEntry(supporting.Select(item => item.Entry), snapshot.Clusters)!);
    }

    /// <summary>
    /// True when the marker scan saw this shape, while a queue stood, carry something other than
    /// the queued roulette at the template's roulette offset. A shape the scan never looked at
    /// has contradicted nothing and is left to the other tests.
    ///
    /// One stray sighting is not a contradiction: the evidence is carried from run to run, so a
    /// single odd message would bar the true announcement for good, while a list disagrees on
    /// every row but one each time it is sent. And when the position table overflowed before this
    /// position ever got a row, its silence is the table's, not the traffic's.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the roulette offset.</param>
    /// <param name="shape">Candidate announcement.</param>
    private static bool Disagrees(CalibrationSnapshot snapshot, CalibrationTemplate template, MessageKey shape)
    {
        if (template.Pop.Field("roulette_id") is not { } field ||
            !snapshot.MarkerShapeTotals.TryGetValue((shape.Opcode, shape.Length), out var total))
        {
            return false;
        }

        var agreed = snapshot.Markers.FirstOrDefault(marker =>
            marker.Opcode == shape.Opcode && marker.Length == shape.Length && marker.Offset == field.Offset)?.Hits ?? 0;
        if (agreed == 0 && snapshot.MarkerOverflow > 0)
        {
            return false;
        }

        return total - agreed >= MinDisagreements;
    }

    /// <summary>How close two sightings of one roulette must be to read as one popup on the timeline.</summary>
    internal static readonly TimeSpan PopRunGap = TimeSpan.FromSeconds(60);

    /// <summary>Groups time-ordered pops into runs: the same roulette, each within <see cref="PopRunGap"/> of the last.</summary>
    /// <param name="ordered">Pops in time order.</param>
    private static IEnumerable<IReadOnlyList<PopHit>> PopRuns(IEnumerable<PopHit> ordered)
    {
        List<PopHit>? run = null;
        foreach (var pop in ordered)
        {
            if (run is not null && run[^1].RouletteId == pop.RouletteId && pop.AtUtc - run[^1].AtUtc <= PopRunGap)
            {
                run.Add(pop);
                continue;
            }

            if (run is not null)
            {
                yield return run;
            }

            run = new List<PopHit> { pop };
        }

        if (run is not null)
        {
            yield return run;
        }
    }

    /// <summary>Sightings that must disagree before <see cref="Disagrees"/> refuses a shape.</summary>
    internal const int MinDisagreements = 2;

    /// <summary>True when the hit sits inside the match window before a burst's load.</summary>
    /// <param name="hit">Timed roulette-echo hit.</param>
    /// <param name="entry">Burst that named a duty.</param>
    /// <param name="template">Template lending the match window.</param>
    private static bool Precedes(RouletteEchoHit hit, ZoneCluster entry, CalibrationTemplate template) =>
        hit.AtUtc < entry.LoadStartedAtUtc && entry.LoadStartedAtUtc - hit.AtUtc <= template.MatchWindow;

    /// <summary>The announcement found at an offset the traffic named, with what named it.</summary>
    /// <param name="Shape">Opcode and length of the announcement.</param>
    /// <param name="Offset">Byte offset the roulette id sits at.</param>
    /// <param name="Pops">One sample per sighting, in order.</param>
    /// <param name="Entry">The first known-duty entry a sighting precedes, when there is one.</param>
    private sealed record MarkedMatch(MessageKey Shape, int Offset, PopHit[] Pops, ZoneCluster? Entry);

    /// <summary>
    /// Finds the announcement without assuming where its roulette id sits.
    ///
    /// The reply and the offset-16 announcement paths both read the byte the template's pop
    /// declares. On a build that splits the queue reply from the match announcement that byte
    /// is a guess, and a wrong guess is indistinguishable from silence. This path reads the
    /// observer's scanned positions instead (<see cref="MarkerCandidate"/>): a position
    /// qualifies when it carried the queued roulette on every occurrence of its shape the scan
    /// looked at, for at least <see cref="MinMarkerRoulettes"/> different roulettes.
    ///
    /// Shapes that travel inside zone loads are dropped only to break a tie, the same way the
    /// offset-16 path does it: a message belonging to a load appears in every load, and the
    /// announcement of a match appears in none.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="template">Template lending the match window.</param>
    /// <param name="pairs">Request/echo pairs; without one there is no request to answer.</param>
    /// <param name="replyOpcodes">Opcodes a pair vouched for; the queue reply is not the pop.</param>
    /// <param name="rejections">Candidates the user already rejected.</param>
    private static MarkedMatch? LockMarker(
        CalibrationSnapshot snapshot, CalibrationTemplate template, FinderPairHit[] pairs,
        IEnumerable<ushort> replyOpcodes, CalibrationRejections rejections)
    {
        if (pairs.Length == 0 || snapshot.Markers.Count == 0)
        {
            return null;
        }

        var replies = new HashSet<ushort>(replyOpcodes);
        var candidates = snapshot.Markers
            .Where(candidate => !replies.Contains(candidate.Opcode) &&
                !rejections.PopOpcodes.Contains(candidate.Opcode))
            .Where(candidate => candidate.RouletteIds.Count >= MinMarkerRoulettes)
            // Every message of this shape the scan looked at carried the queued roulette. A
            // position that hit on some of them is a byte that happens to land on a small
            // number, which roughly one message in 256 does.
            .Where(candidate => snapshot.MarkerShapeTotals.TryGetValue(
                (candidate.Opcode, candidate.Length), out var total) && total == candidate.Hits)
            .ToArray();
        if (candidates.Length > 1)
        {
            candidates = candidates
                .Where(candidate => !snapshot.Clusters.Any(cluster => cluster.Members.ContainsKey(candidate.Shape)))
                .ToArray();
        }

        if (candidates.Length != 1)
        {
            return null;
        }

        var chosen = candidates[0];
        var pops = chosen.Sightings
            .OrderBy(sighting => sighting.AtUtc)
            .Select(sighting => new PopHit(
                sighting.ConnectionTag, chosen.Opcode, sighting.RouletteId, sighting.TMs, sighting.AtUtc,
                false, Array.Empty<CalibrationSelectorReading>()))
            .ToArray();
        if (pops.Length == 0)
        {
            return null;
        }

        var entries = snapshot.Clusters
            .Where(cluster => cluster.TerritoryHits.Count > 0)
            .Where(cluster => pops.Any(pop => pop.AtUtc < cluster.LoadStartedAtUtc &&
                cluster.LoadStartedAtUtc - pop.AtUtc <= template.MatchWindow));
        return new MarkedMatch(chosen.Shape, chosen.Offset, pops, PreferredEntry(entries, snapshot.Clusters));
    }

    /// <summary>A match taken from the player's own request, with the duty that confirms it.</summary>
    /// <param name="Pops">One sample per request that a duty entry followed.</param>
    /// <param name="Entry">The first such duty entry.</param>
    private sealed record QueuedMatch(PopHit[] Pops, ZoneCluster Entry)
    {
        /// <summary>
        /// Every request with the duty entry it explains, in time order. The pop above keeps only
        /// the requests, and the timing rule has to ask what happened between each request and
        /// its own entry.
        /// </summary>
        public IReadOnlyList<(PopHit Pop, ZoneCluster Entry)> Chains { get; init; } =
            Array.Empty<(PopHit, ZoneCluster)>();
    }

    /// <summary>
    /// Reads the match from the player's own request instead of from the server's announcement.
    ///
    /// This is the weakest path and the last one tried. It exists because a build can put the
    /// announcement somewhere no scan finds, and the alternative is recording nothing at all.
    /// The chain it accepts is the one the announcement would have corroborated, minus the
    /// corroboration: the player asked the finder for a roulette, and the next thing that
    /// happened to them was entering a duty the duty table recognises.
    ///
    /// What it can get wrong is bounded and worth saying plainly: a player who queues, cancels,
    /// and then enters a duty some other way within the hour has that duty recorded against the
    /// roulette they cancelled. Nothing here can tell those apart, so everything downstream is
    /// labelled as inferred rather than observed.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="pairs">Request/echo pairs; the request is the message that stands in.</param>
    /// <param name="lockedReply">The reply opcode, so only vouched-for requests are used.</param>
    private static QueuedMatch? InferFromQueue(
        CalibrationSnapshot snapshot, FinderPairHit[] pairs, ushort lockedReply)
    {
        var entries = snapshot.Clusters
            .Where(cluster => cluster.TerritoryHits.Count > 0)
            .OrderBy(cluster => cluster.LoadStartedAtUtc)
            .ToArray();
        var requests = pairs
            .Where(pair => pair.ReplyOpcode == lockedReply)
            .Select(pair => (Pair: pair, At: RequestAt(pair)))
            .OrderBy(request => request.At)
            .ToArray();
        if (entries.Length == 0 || requests.Length == 0)
        {
            return null;
        }

        var chains = new List<(PopHit Pop, ZoneCluster Entry)>();
        foreach (var entry in entries)
        {
            // The request that explains an entry is the last one before it, and only when no
            // other duty stands between them: an older queue has already been spent.
            var request = requests
                .Where(candidate => candidate.At < entry.LoadStartedAtUtc &&
                    entry.LoadStartedAtUtc - candidate.At <= QueueWindow)
                .OrderByDescending(candidate => candidate.At)
                .Select(candidate => (Pair: candidate.Pair, At: (DateTimeOffset?)candidate.At))
                .FirstOrDefault();
            if (request.At is not { } requestedAt ||
                entries.Any(other => other.LoadStartedAtUtc > requestedAt &&
                    other.LoadStartedAtUtc < entry.LoadStartedAtUtc))
            {
                continue;
            }

            chains.Add((new PopHit(
                request.Pair.ConnectionTag, request.Pair.RequestOpcode, request.Pair.RouletteId,
                request.Pair.RequestTMs, requestedAt, false, Array.Empty<CalibrationSelectorReading>()), entry));
        }

        if (chains.Count == 0)
        {
            return null;
        }

        var ordered = chains.OrderBy(chain => chain.Pop.AtUtc).ToArray();
        return new QueuedMatch(ordered.Select(chain => chain.Pop).ToArray(),
            PreferredEntry(ordered.Select(chain => chain.Entry), snapshot.Clusters)!)
        {
            Chains = ordered,
        };
    }

    // A missed exit on an old connection can never arrive after a relogin. Prefer a
    // complete, already corroborated duty chain so retained evidence does not pin progress
    // to that old entry forever. Do not manufacture an exit across connections.
    private static ZoneCluster? PreferredEntry(IEnumerable<ZoneCluster> entries, IReadOnlyList<ZoneCluster> clusters) =>
        entries.OrderBy(entry => FindExit(entry, clusters) is null)
            .ThenBy(entry => entry.LoadStartedAtUtc).FirstOrDefault();

    private static ZoneCluster? FindExit(ZoneCluster entry, IReadOnlyList<ZoneCluster> clusters) =>
        clusters.Where(cluster => cluster.ConnectionTag == entry.ConnectionTag &&
                cluster.LoadStartedAtUtc - entry.LoadStartedAtUtc >= MinDutyDuration)
            .OrderBy(cluster => cluster.LoadStartedAtUtc).FirstOrDefault();

    /// <summary>Wall-clock time the client sent a paired request.</summary>
    /// <param name="pair">Request/echo pair.</param>
    private static DateTimeOffset RequestAt(FinderPairHit pair) =>
        pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs);

    private static bool TooFrequent(CalibrationSnapshot snapshot, PacketDirection direction, ushort opcode) =>
        snapshot.OpcodeCounts.TryGetValue((direction, opcode), out var count) && count > MaxCandidateOccurrences;

    internal static IReadOnlyList<MessageKey> OncePerCluster(CalibrationSnapshot snapshot, int length)
    {
        var clusters = snapshot.Clusters;
        var result = new List<MessageKey>();
        // A missed burst files both the marker and territory as outside. Apply the same
        // bounded tolerance to both, so one missed exit cannot poison all future calibration.
        foreach (var key in ZoneShapes(snapshot, length).Where(key => Behaves(snapshot, key)))
        {
            if (ExactlyOnceCount(clusters, key) >= MinClusters)
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>Bursts in which a shape appeared exactly once.</summary>
    /// <param name="clusters">Bursts to count over.</param>
    /// <param name="key">Shape to count.</param>
    internal static int ExactlyOnceCount(IReadOnlyList<ZoneCluster> clusters, MessageKey key) =>
        clusters.Count(cluster => cluster.Members.TryGetValue(key, out var count) && count == 1);

    /// <summary>
    /// True when a shape of the template's length lives inside a burst and has also been seen
    /// outside one. That combination is what a lossy capture looks like from in here: the shape
    /// did not change, some of the loads it belongs to were never assembled.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="length">Template's zone-marker length.</param>
    /// <param name="rejections">Shapes the user already rejected.</param>
    private static bool Contaminated(
        CalibrationSnapshot snapshot, int length, CalibrationRejections rejections) =>
        ContaminatedShapes(snapshot, length).Any(key => !rejections.ZoneKeys.Contains(key));

    /// <summary>Template-length shapes a burst holds that were also seen outside one.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="length">Template's zone-marker length.</param>
    internal static IEnumerable<MessageKey> ContaminatedShapes(CalibrationSnapshot snapshot, int length) =>
        snapshot.Clusters
            .SelectMany(cluster => cluster.Members.Keys)
            .Where(key => key.Direction == PacketDirection.ServerToClient && key.Length == length)
            .Distinct()
            .Where(snapshot.OutsideCounts.ContainsKey);

    /// <summary>
    /// True when a shape of the template's length is behaving like the marker but has not been
    /// through enough zone changes yet. This separates "keep playing" from "this build's zone
    /// message is a different size", which the 2026-09-10 session could not tell apart.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="length">The template's zone-marker length.</param>
    /// <param name="rejections">Shapes the user already rejected.</param>
    /// <param name="entry">Duty entry supported by the chosen match.</param>
    /// <param name="exit">Exit following that same entry.</param>
    private static bool NearlyZone(CalibrationSnapshot snapshot, int length, CalibrationRejections rejections,
        ZoneCluster entry, ZoneCluster exit) =>
        ZoneShapes(snapshot, length)
            .Where(key => !rejections.ZoneKeys.Contains(key))
            .Where(key => Outside(snapshot, key) <= MaxZoneOutside)
            .Any(key => OnceIn(entry, key) && OnceIn(exit, key));

    /// <summary>Server shapes of one length that a zone load holds, whatever they do elsewhere.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="length">Payload length to look for.</param>
    internal static IEnumerable<MessageKey> ZoneShapes(CalibrationSnapshot snapshot, int length) =>
        snapshot.Clusters
            .SelectMany(cluster => cluster.Members.Keys)
            .Where(key => key.Direction == PacketDirection.ServerToClient && key.Length == length)
            .Distinct();

    /// <summary>Occurrences of a shape outside every burst.</summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="key">Shape to count.</param>
    internal static int Outside(CalibrationSnapshot snapshot, MessageKey key) =>
        snapshot.OutsideCounts.TryGetValue(key, out var count) ? count : 0;

    /// <summary>
    /// True when a shape marks enough zone loads, exactly once each, to carry the sightings it
    /// has outside one. Both halves matter: the count alone would admit a shape that travels
    /// constantly on a long evening, and the ratio alone would admit one seen twice in total.
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="key">Shape to judge.</param>
    internal static bool MarksEnough(CalibrationSnapshot snapshot, MessageKey key) =>
        ExactlyOnceCount(snapshot.Clusters, key) >= MinClusters && Behaves(snapshot, key);

    /// <summary>
    /// True when a shape's sightings outside a load are few enough to be the burst boundary
    /// rather than the shape's own behaviour. Asked without the "enough loads yet" half, so the
    /// report can say "no shape fits" rather than "not enough zone changes yet".
    /// </summary>
    /// <param name="snapshot">Frozen observations.</param>
    /// <param name="key">Shape to judge.</param>
    internal static bool Behaves(CalibrationSnapshot snapshot, MessageKey key)
    {
        var marked = ExactlyOnceCount(snapshot.Clusters, key);
        var outside = Outside(snapshot, key);
        return marked > 0 && (outside <= MaxZoneOutside || outside * ZoneOutsidePerBurst <= marked);
    }

    private static bool OnceIn(ZoneCluster cluster, MessageKey key) =>
        cluster.Members.TryGetValue(key, out var count) && count == 1;

    /// <summary>
    /// A burst vouches for a job shape when it carried the shape at the template's length,
    /// every reading satisfied the constraints, and every reading agreed.
    /// </summary>
    internal static bool VouchesForJob(ZoneCluster cluster, ushort opcode, PacketDirection direction, int length) =>
        cluster.Members.ContainsKey(new MessageKey(direction, opcode, length)) &&
        !ContradictsJob(cluster, opcode) &&
        cluster.JobValues.TryGetValue(opcode, out var values) && values.Count > 0;

    /// <summary>
    /// True when, in every burst, all the candidates that spoke read the same job. A burst one of
    /// them is absent from says nothing; one where two of them name different jobs means at least
    /// one is not the job message.
    /// </summary>
    /// <param name="clusters">Every burst observed.</param>
    /// <param name="opcodes">Candidates that each passed the job rule on their own.</param>
    private static bool JobReadingsAgree(IReadOnlyList<ZoneCluster> clusters, IReadOnlyList<ushort> opcodes) =>
        clusters.All(cluster => opcodes
            .SelectMany(opcode => cluster.JobValues.TryGetValue(opcode, out var values) ? values : Array.Empty<long>())
            .Distinct()
            .Count() <= 1);

    /// <summary>A burst contradicts a job shape when a reading broke the constraints or two readings disagreed.</summary>
    internal static bool ContradictsJob(ZoneCluster cluster, ushort opcode) =>
        cluster.JobViolations.ContainsKey(opcode) ||
        (cluster.JobValues.TryGetValue(opcode, out var values) && values.Distinct().Count() > 1);

    /// <summary>
    /// Which state value means "matched" on this build, worked out from where messages of the
    /// locked opcode sit in time rather than from the value the template happens to carry.
    ///
    /// The template's value is only true of the build the template came from. The shipped CN
    /// profile's own provenance records three different values on one opcode - a reply to the
    /// request, the match itself, and a state update after leaving a duty - so a value cannot
    /// be assumed and cannot be ranked into place either. What the traffic does say is that a
    /// candidate follows a paired request for the same roulette, outside the echo and actual
    /// load, and precedes a unique known-duty entry within the template window. No reply state
    /// can become a match and no frequency or connection preference resolves an ambiguity.
    /// </summary>
    /// <param name="Selectors">The state that means "matched", as the template names its fields.</param>
    /// <param name="Pops">Samples carrying it, in order.</param>
    /// <param name="Ambiguous">Several states could be the match.</param>
    /// <param name="Contradicted">Everything that arrived on its own also arrives as a reply.</param>
    private sealed record MatchedState(
        IReadOnlyList<CalibrationSelectorReading> Selectors,
        PopHit[] Pops,
        bool Ambiguous,
        bool Contradicted)
    {
        /// <summary>The first uniquely supported entry; null while evidence is absent or ambiguous.</summary>
        public ZoneCluster? Entry { get; init; }

        /// <summary>Nothing decided yet.</summary>
        public static MatchedState None { get; } =
            new(Array.Empty<CalibrationSelectorReading>(), Array.Empty<PopHit>(), false, false);

        /// <summary>Works the state out from the samples on one opcode.</summary>
        /// <param name="pops">Samples on the locked opcode.</param>
        /// <param name="clusters">Zone-load bursts, to exclude anything that arrived inside one.</param>
        /// <param name="pairs">Pairs, for the roulette ids the player actually asked for.</param>
        /// <param name="matchWindow">Maximum candidate-to-entry interval from the template.</param>
        public static MatchedState From(
            IReadOnlyList<PopHit> pops, IReadOnlyList<ZoneCluster> clusters, IReadOnlyList<FinderPairHit> pairs,
            TimeSpan matchWindow)
        {
            if (pops.Count == 0 || DominantRequest(pairs) is not { } dominant)
            {
                return None;
            }

            pairs = pairs.Where(pair => pair.RequestOpcode == dominant).ToArray();

            var replies = pops.Where(pop => pop.WithinEcho).ToArray();
            var chains = new List<(PopHit Pop, ZoneCluster Entry)>();
            var ambiguousEntry = false;
            foreach (var pop in pops.Where(pop => !pop.WithinEcho)
                .Where(pop => !clusters.Any(cluster =>
                    pop.AtUtc >= cluster.LoadStartedAtUtc && pop.AtUtc <= cluster.EndedAtUtc)))
            {
                // Only this opcode pair and this connection can supply the earlier request.
                // Wall-clock times preserve ordering across retained capture sessions.
                var request = pairs.Where(pair => pair.ReplyOpcode == pop.Opcode &&
                        pair.ConnectionTag == pop.ConnectionTag && pair.RouletteId == pop.RouletteId &&
                        pair.ReplyAtUtc < pop.AtUtc && RequestAt(pair) < pop.AtUtc)
                    .OrderByDescending(RequestAt).FirstOrDefault();
                if (request is null || clusters.Any(cluster => cluster.TerritoryHits.Count > 0 &&
                    cluster.LoadStartedAtUtc > RequestAt(request) && cluster.LoadStartedAtUtc <= pop.AtUtc))
                {
                    // A request already followed by duty entry cannot authenticate a late
                    // in-duty or post-exit status, even if another load later follows it.
                    continue;
                }

                var entries = clusters.Where(cluster => cluster.TerritoryHits.Count > 0 &&
                    cluster.LoadStartedAtUtc > pop.AtUtc &&
                    cluster.LoadStartedAtUtc - pop.AtUtc <= matchWindow).ToArray();
                if (entries.Length == 0)
                {
                    continue;
                }

                if (replies.Any(reply => CalibrationObserver.SameSelectors(reply.Selectors, pop.Selectors)))
                {
                    // Retain contradiction only with a duty chain; an ordinary late echo
                    // without any entry is still observation, not version incompatibility.
                    chains.Add((pop, entries[0]));
                    continue;
                }

                if (entries.Length != 1 || entries[0].TerritoryHits.Select(hit => hit.TerritoryId).Distinct().Count() != 1)
                {
                    ambiguousEntry = true;
                    continue;
                }

                chains.Add((pop, entries[0]));
            }

            if (ambiguousEntry)
            {
                return None with { Ambiguous = true };
            }

            if (chains.Count == 0)
            {
                return None;
            }

            var states = new List<IReadOnlyList<CalibrationSelectorReading>>();
            foreach (var (pop, _) in chains)
            {
                if (replies.Any(reply => CalibrationObserver.SameSelectors(reply.Selectors, pop.Selectors)) ||
                    states.Any(state => CalibrationObserver.SameSelectors(state, pop.Selectors)))
                {
                    continue;
                }

                states.Add(pop.Selectors);
            }

            if (states.Count == 0)
            {
                return None with { Contradicted = true };
            }

            if (states.Count > 1)
            {
                return None with { Ambiguous = true };
            }

            var chosen = states[0];
            var supported = chains.Where(chain => CalibrationObserver.SameSelectors(chain.Pop.Selectors, chosen))
                .OrderBy(chain => chain.Pop.AtUtc).ToArray();
            return new MatchedState(
                chosen,
                supported.Select(chain => chain.Pop).ToArray(),
                false,
                false)
            {
                Entry = PreferredEntry(supported.Select(chain => chain.Entry), clusters),
            };
        }

        private static DateTimeOffset RequestAt(FinderPairHit pair) =>
            pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs);
    }

    /// <summary>Local wall-clock HH:mm, the clock the player watched while playing.</summary>
    /// <param name="at">Instant to render.</param>
    private static string Local(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static IReadOnlyList<CalibrationEvent> BuildEvents(
        CalibrationSnapshot snapshot,
        CalibrationTemplate template,
        RouletteCatalog roulettes,
        Region region,
        ushort? lockedReply,
        FinderPairHit[] pairs,
        PopHit[] genuinePops,
        ZoneCluster? entry,
        ZoneCluster? exit,
        IReadOnlyList<ProfileMessage> messages,
        CalibrationMatchSource source,
        TimedAnnouncement? timed)
    {
        var events = new List<CalibrationEvent>();
        events.AddRange(TimedEvents(timed, roulettes, region));
        var territoryOpcode = messages.FirstOrDefault(message => message.Name == "ZONE_TERRITORY")?.Opcode;
        DateTimeOffset? lastRequest = null;
        foreach (var pair in pairs.Where(pair => pair.ReplyOpcode == lockedReply).OrderBy(pair => pair.ReplyAtUtc))
        {
            var requestedAt = pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs);
            if (lastRequest is { } last && requestedAt - last < TimeSpan.FromSeconds(2))
            {
                continue;
            }

            lastRequest = requestedAt;
            events.Add(new CalibrationEvent(
                Id("finder_request", pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs)),
                "finder_request", pair.RequestTMs,
                pair.ReplyAtUtc - TimeSpan.FromMilliseconds(pair.ReplyTMs - pair.RequestTMs),
                "排本：" + roulettes.DisplayName((int)Math.Min(pair.RouletteId, int.MaxValue), region),
                pair.RouletteId, null, null, true));
        }

        // When the match is inferred from the queue, the samples ARE the requests already
        // listed above. Printing them again as "匹配弹窗" would put two lines on the same
        // millisecond and claim the software saw a popup it never identified.
        //
        // Some builds send the announcement several times for one match (four times for one
        // alliance-raid match on the CN 2026.09.15 client). That is one popup on the player's
        // screen and one thing to confirm, so a run of the same roulette is one line.
        var shown = source == CalibrationMatchSource.QueueRequest ? Array.Empty<PopHit>() : genuinePops;
        foreach (var run in PopRuns(shown.OrderBy(pop => pop.AtUtc)))
        {
            var pop = run[0];
            events.Add(new CalibrationEvent(
                Id("pop", pop.AtUtc), "pop", pop.TMs, pop.AtUtc,
                "匹配弹窗：" + roulettes.DisplayName((int)Math.Min(pop.RouletteId, int.MaxValue), region) +
                (run.Count > 1 ? $"（这条报文连发了 {run.Count} 次）" : string.Empty),
                pop.RouletteId, null, null, true));
        }

        var firstOnConnection = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in snapshot.Clusters)
        {
            string kind;
            string label;
            long? territoryId = null;
            string? dutyName = null;
            var required = false;
            if (ReferenceEquals(cluster, entry))
            {
                kind = "duty_enter";
                required = true;
                var hit = cluster.TerritoryHits.FirstOrDefault(hit => territoryOpcode is null || hit.Opcode == territoryOpcode);
                territoryId = hit?.TerritoryId;
                dutyName = hit?.DutyName;
                label = dutyName is null ? "进入副本" : "进入副本：" + dutyName;
                if (source == CalibrationMatchSource.QueueRequest)
                {
                    label += "（本机把上面那次排本记为这一把的匹配）";
                }
            }
            else if (ReferenceEquals(cluster, exit))
            {
                kind = "duty_exit";
                required = true;
                // Which entry it closes. Without that the row reads as the end of whatever line
                // sits above it, which may be a queue made during the duty rather than the
                // entry, making the run look far shorter than it was.
                label = entry is null
                    ? "离开副本"
                    : "离开副本（结束的是 " + Local(entry.LoadStartedAtUtc) + " 那次进本）";
            }
            else if (firstOnConnection.Add(cluster.ConnectionTag))
            {
                kind = "login";
                label = "登录进入游戏";
            }
            else if (cluster.TerritoryHits.FirstOrDefault(hit => territoryOpcode is null || hit.Opcode == territoryOpcode)
                     is { DutyName: not null } other)
            {
                // Only the entry the draft rests on is asked about, but every duty of the evening
                // was recognised the same way, and a bare "换区" reads as "the name is missing".
                kind = "zone";
                label = "换区：进入了 " + other.DutyName;
                territoryId = other.TerritoryId;
                dutyName = other.DutyName;
            }
            else
            {
                kind = "zone";
                label = "换区";
            }

            events.Add(new CalibrationEvent(
                Id(kind, cluster.LoadStartedAtUtc), kind, cluster.LoadStartTMs, cluster.LoadStartedAtUtc, label,
                null, territoryId, dutyName, required));
        }

        var ordered = events.OrderBy(item => item.AtUtc)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ToArray();
        // Two events of one kind can land on the same millisecond (two connections opening
        // their first burst in one tick); the verdicts are keyed by this id, so it has to be
        // unique whatever the clock says.
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var id = ordered[index].EventId;
            if (!used.Add(id))
            {
                var suffix = 2;
                while (!used.Add(id + "-" + suffix.ToString(CultureInfo.InvariantCulture)))
                {
                    suffix++;
                }

                ordered[index] = ordered[index] with
                {
                    EventId = id + "-" + suffix.ToString(CultureInfo.InvariantCulture),
                };
            }
        }

        return ordered;
    }

    private static string Id(string kind, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}-{at.ToUnixTimeMilliseconds()}");
}
