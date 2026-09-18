using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// What the player said about one line of the timeline.
///
/// Three answers, not two. WRONG is the strongest: the software identified the wrong message,
/// so the candidate is thrown away and the search starts again - another evening of play.
/// <see cref="Relabel"/> covers the case where the message was right and only its name was
/// wrong (a roulette name against the wrong id in the shipped table): the event is accepted
/// exactly as CORRECT does, and the name the player gives is remembered for the roulette id
/// the software read.
/// </summary>
/// <param name="Verdict">CORRECT, WRONG or RELABEL.</param>
/// <param name="RouletteName">For RELABEL: what the player says that roulette is really called.</param>
public sealed record CalibrationVerdict(string Verdict, string? RouletteName = null)
{
    /// <summary>The software got this line right.</summary>
    public static CalibrationVerdict Correct { get; } = new("CORRECT");

    /// <summary>The software identified the wrong message.</summary>
    public static CalibrationVerdict Wrong { get; } = new("WRONG");

    /// <summary>The right message under the wrong name.</summary>
    /// <param name="name">Name the player gave.</param>
    public static CalibrationVerdict Relabel(string name) => new("RELABEL", name);

    /// <summary>True when the player rejected the line outright.</summary>
    public bool IsWrong => string.Equals(Verdict, "WRONG", StringComparison.Ordinal);

    /// <summary>True when the player corrected the name and kept the line.</summary>
    public bool IsRelabel =>
        string.Equals(Verdict, "RELABEL", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(RouletteName);
}

/// <summary>A roulette the player named for themselves, to be remembered on this machine.</summary>
/// <param name="RouletteId">Id the software read off the wire.</param>
/// <param name="Name">What the player says it is called.</param>
public sealed record RouletteRenaming(int RouletteId, string Name);

/// <summary>Where calibration stands for the running client.</summary>
public enum CalibrationState
{
    /// <summary>Not calibrating: a profile matches, no template exists, or the game is not running.</summary>
    Idle,

    /// <summary>Armed for a build with no profile, but no capture session is running yet.</summary>
    Waiting,

    /// <summary>Capture is running on a build with no profile; evidence is being collected.</summary>
    Observing,

    /// <summary>The draft passed every self-check; the user is asked to confirm the timeline.</summary>
    Ready,

    /// <summary>The evidence contradicts the template; more play will not help.</summary>
    Blocked,

    /// <summary>A local profile was written and, when a session was running, bound to it.</summary>
    Done,
}

/// <summary>What the capture page shows about calibration; part of <c>CaptureStatus</c>.</summary>
/// <param name="State">Where calibration stands.</param>
/// <param name="GameBuild">Build being calibrated.</param>
/// <param name="TemplateProfileId">Shipped profile lending the shapes.</param>
/// <param name="Progress">Progress indicators, once observation started.</param>
/// <param name="Blockers">Why the draft is not ready, in display order.</param>
/// <param name="Events">Timeline for the user to confirm.</param>
/// <param name="BoundAtUtc">When the local profile was bound inside the running session.</param>
/// <param name="LocalProfileId">Profile id written by the last confirmation.</param>
/// <param name="Evidence">Opcode-level summary for diagnostics; null before observation starts.</param>
/// <param name="CarriedSource">Why this run started from the evidence it did; null when not armed.</param>
public sealed record CalibrationStatusSnapshot(
    CalibrationState State,
    string? GameBuild,
    string? TemplateProfileId,
    CalibrationProgress? Progress,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<CalibrationEvent> Events,
    DateTimeOffset? BoundAtUtc,
    string? LocalProfileId,
    CalibrationEvidenceSummary? Evidence = null,
    string? CarriedSource = null)
{
    /// <summary>Shared calibration for the running client; none by default.</summary>
    public SharedCalibrationSnapshot Shared { get; init; } = SharedCalibrationSnapshot.None;

    /// <summary>Nothing is being calibrated.</summary>
    public static CalibrationStatusSnapshot Idle { get; } = new(
        CalibrationState.Idle, null, null, null, Array.Empty<string>(), Array.Empty<CalibrationEvent>(), null, null);
}

/// <summary>
/// Holds the calibration state for one client build across capture sessions: the template,
/// the observer of the current session, the draft derived from it, and what the user has
/// already rejected. Everything that touches disk or the parser stays outside this class; it
/// only decides what may happen next. Callers serialise access.
/// </summary>
public sealed class CalibrationCoordinator
{
    private readonly DutyCatalog _duties;
    private RouletteCatalog _roulettes;
    private CalibrationTemplate? _template;
    private Region _region;
    private string? _build;
    private CalibrationObserver? _observer;
    private CalibrationSnapshot? _carried;
    private string? _carriedSource;
    private CalibrationRejections _rejections = CalibrationRejections.None;
    private CalibrationDraft? _draft;
    private int _draftAtMessage = -1;
    private CalibrationState _state = CalibrationState.Idle;
    private DateTimeOffset? _boundAt;
    private string? _localProfileId;
    private bool _provisional;
    private bool _retaining;
    private int _generation;
    private int _armEpoch;
    private readonly List<DeclaredCandidate> _declared = new();

    /// <summary>Creates a coordinator over the reference tables.</summary>
    /// <param name="duties">Duty table for territory recognition.</param>
    /// <param name="roulettes">Roulette table for plausibility and labels.</param>
    public CalibrationCoordinator(DutyCatalog? duties = null, RouletteCatalog? roulettes = null)
    {
        _duties = duties ?? DutyCatalog.Default;
        _roulettes = roulettes ?? RouletteCatalog.Default;
    }

    /// <summary>Where calibration stands.</summary>
    public CalibrationState State => _state;

    /// <summary>Template in force, when armed.</summary>
    public CalibrationTemplate? Template => _template;

    /// <summary>Build being calibrated, when armed.</summary>
    public string? GameBuild => _build;

    /// <summary>
    /// Bumped by every change of what is being calibrated (arm, disarm, session start, discard,
    /// rejection). A caller that released its lock to do disk work compares it afterwards to
    /// learn whether the draft it acted on is still the one in force.
    /// </summary>
    public int Generation => _generation;

    /// <summary>
    /// Bumped only by arming for another build or template and by disarming - not by a capture
    /// session starting, which <see cref="Generation"/> counts. A download or a bind started for one
    /// epoch is claimed only in that epoch.
    /// </summary>
    public int ArmEpoch => _armEpoch;

    /// <summary>Region of the client being calibrated, when armed.</summary>
    public Region Region => _region;

    /// <summary>
    /// True while a shared profile records and is still being watched (plan §4.1 step 7). A ready draft
    /// is then not put to the player: the profile in force already records, and local calibration only
    /// matters again if that profile is withdrawn.
    /// </summary>
    public bool Retaining => _retaining;

    /// <summary>True while a session may be captured for calibration purposes.</summary>
    public bool Armed => _template is not null && _state != CalibrationState.Done;

    /// <summary>
    /// True while the profile in force infers the match from the queue request. Calibration
    /// keeps running in that case: the profile records mentor roulettes today, and the search
    /// for the server's own announcement continues in the background so it can be replaced by
    /// a stricter one without the player having to notice.
    /// </summary>
    public bool Provisional => _provisional;

    /// <summary>True while evidence is being collected or a draft awaits the user.</summary>
    public bool Active => _state is CalibrationState.Observing or CalibrationState.Ready or CalibrationState.Blocked;

    /// <summary>
    /// Makes the coordinator ready for a build with no profile. Re-arming with the same build
    /// keeps the evidence; a different build starts over, including the rejections.
    /// </summary>
    /// <param name="template">Template lending the shapes.</param>
    /// <param name="region">Region of the client.</param>
    /// <param name="gameBuild">Build of the client.</param>
    public void Arm(CalibrationTemplate template, Region region, string gameBuild)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        // The catalogue is re-read on every selection, so the same template is a different
        // object each time; identity is the profile, not the reference.
        if (_template is not null && string.Equals(_build, gameBuild, StringComparison.Ordinal) && _region == region &&
            string.Equals(_template.Source.ProfileSha256, template.Source.ProfileSha256, StringComparison.Ordinal))
        {
            return;
        }

        Disarm();
        _template = template;
        _region = region;
        _build = gameBuild;
        _generation++;
        _armEpoch++;
    }

    /// <summary>
    /// Hands the coordinator the evidence an earlier run of the Collector left behind, to be
    /// adopted by the next observer it creates. Ignored once observation has started, and
    /// dropped by <see cref="Discard"/> like everything else the player asked to forget.
    /// </summary>
    /// <param name="carried">Evidence read back from disk, or null when there was none.</param>
    public void Carry(CalibrationSnapshot? carried, string? source = null)
    {
        if (_observer is null)
        {
            _carried = carried;
            _carriedSource = source;
        }
    }

    /// <summary>Why this run started from what it did: OK, NO_FILE, OTHER_TEMPLATE and so on.</summary>
    public string? CarriedSource => _carriedSource;

    /// <summary>
    /// The evidence as it stands, for writing to disk, or null when there is none worth
    /// writing. Taking it costs nothing and changes nothing.
    /// </summary>
    public CalibrationSnapshot? Evidence() => _observer?.Snapshot();

    /// <summary>Forgets everything; the build got a profile or the game went away for good.</summary>
    public void Disarm()
    {
        _template = null;
        _build = null;
        _observer = null;
        _rejections = CalibrationRejections.None;
        _draft = null;
        _draftAtMessage = -1;
        _state = CalibrationState.Idle;
        _boundAt = null;
        _localProfileId = null;
        _provisional = false;
        _retaining = false;
        _carried = null;
        _declared.Clear();
        _generation++;
        _armEpoch++;
    }

    /// <summary>A capture session started on the armed build: start observing it.</summary>
    /// <param name="captureSessionId">Session whose messages will be fed.</param>
    public void Begin(string captureSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);
        if (_template is null || _state == CalibrationState.Done)
        {
            return;
        }

        // Evidence survives a re-login on the same build: the new session is adopted by the
        // observer that already holds the previous clusters and pairs.
        if (_observer is null)
        {
            _observer = new CalibrationObserver(_template, _region, captureSessionId, _duties, _roulettes);
            if (_carried is { } carried)
            {
                _observer.AdoptEvidence(carried);
                _carried = null;
            }

            // Candidates are counted from registration on, in every observer of this arm.
            foreach (var candidate in _declared)
            {
                _observer.RegisterCandidate(candidate);
            }
        }
        else
        {
            _observer.AdoptSession(captureSessionId);
        }

        _draft = null;
        _draftAtMessage = -1;
        _state = CalibrationState.Observing;
        _generation++;
    }

    /// <summary>
    /// Starts counting a shared candidate in the current observer and in any observer this arm creates
    /// later (a discard, the first session). Idempotent; forgotten on disarm.
    /// </summary>
    /// <param name="candidate">Candidate rebuilt from a share code.</param>
    public void RegisterCandidate(DeclaredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_declared.All(known => !string.Equals(known.CandidateId, candidate.CandidateId, StringComparison.Ordinal)))
        {
            _declared.Add(candidate);
        }

        _observer?.RegisterCandidate(candidate);
    }

    /// <summary>Stops counting a shared candidate.</summary>
    /// <param name="candidateId">Identity it was registered under.</param>
    public void UnregisterCandidate(string candidateId)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateId);
        _declared.RemoveAll(known => string.Equals(known.CandidateId, candidateId, StringComparison.Ordinal));
        _observer?.UnregisterCandidate(candidateId);
    }

    /// <summary>Records a capture-health reading; false when nothing observes that session.</summary>
    /// <param name="health">Reading from the capture controller.</param>
    public bool RecordSessionHealth(CaptureSessionHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return _observer?.RecordSessionHealth(health) ?? false;
    }

    /// <summary>Says whether the profile in force infers the match from the queue (derived from the profile, not from memory).</summary>
    /// <param name="provisional">True for a queue-inferred local or shared profile.</param>
    public void UseProvisional(bool provisional)
    {
        if (_provisional != provisional)
        {
            _provisional = provisional;
            _draft = null;
            _draftAtMessage = -1;
        }
    }

    /// <summary>Says whether a shared profile in force is still being watched.</summary>
    /// <param name="retaining">True while it has not recorded a complete duty.</param>
    public void UseRetention(bool retaining)
    {
        if (_retaining != retaining)
        {
            _retaining = retaining;
            _draft = null;
            _draftAtMessage = -1;
        }
    }

    /// <summary>Feeds one decoded message to the observer, when observing.</summary>
    /// <param name="message">Decoded message.</param>
    public void Accept(DecodedMessage message)
    {
        if (_state is CalibrationState.Observing or CalibrationState.Ready or CalibrationState.Blocked)
        {
            _observer?.Accept(message);
        }
    }

    /// <summary>The session ended: close open bursts and re-derive from the retained evidence on the next read.</summary>
    public void Stop()
    {
        if (_observer is null)
        {
            return;
        }

        _observer.Flush();
        // Flush changes cluster membership and outside counts without accepting a message.
        // The message-count cache must not reuse a draft made before those changes.
        _draft = null;
        _draftAtMessage = -1;
    }

    /// <summary>The draft for the evidence seen so far, re-derived when new messages arrived.</summary>
    public CalibrationDraft? CurrentDraft() =>
        _observer is null || _template is null || _state == CalibrationState.Done
            ? _draft
            : CurrentDraft(_observer.Snapshot());

    /// <summary>The draft for one already-taken snapshot, so a caller that also needs the
    /// evidence summary derives both from the same observations.</summary>
    /// <param name="snapshot">Frozen observations to derive from.</param>
    private CalibrationDraft? CurrentDraft(CalibrationSnapshot snapshot)
    {
        if (_observer is null || _template is null || _state == CalibrationState.Done)
        {
            return _draft;
        }

        if (_draft is null || snapshot.MessagesSeen != _draftAtMessage)
        {
            _draft = CalibrationDraft.Derive(snapshot, _template, _rejections, _roulettes);
            _draftAtMessage = snapshot.MessagesSeen;
            _state = _draft.Status switch
            {
                // A provisional profile is already in force, so re-proposing the same inferred
                // match would ask the player to confirm what they confirmed once already. Only
                // a draft that found the server's own announcement is worth interrupting for.
                CalibrationDraftStatus.Ready
                    when _provisional && _draft.MatchSource == CalibrationMatchSource.QueueRequest
                    => CalibrationState.Observing,
                CalibrationDraftStatus.Ready when _retaining => CalibrationState.Observing,
                CalibrationDraftStatus.Ready => CalibrationState.Ready,
                CalibrationDraftStatus.Blocked => CalibrationState.Blocked,
                _ => CalibrationState.Observing,
            };
        }

        return _draft;
    }

    /// <summary>What the capture page shows.</summary>
    public CalibrationStatusSnapshot Snapshot()
    {
        // One snapshot feeds both halves: two would let a cached draft sit beside a freshly
        // counted evidence summary and contradict it.
        var observations = _observer is not null && _template is not null ? _observer.Snapshot() : null;
        var draft = observations is null ? CurrentDraft() : CurrentDraft(observations);
        if (_template is null && _state == CalibrationState.Idle)
        {
            return CalibrationStatusSnapshot.Idle;
        }

        var blockers = draft?.Blockers ?? Array.Empty<string>();
        if (_provisional)
        {
            // The player is recording already; what is left is an upgrade, so the card must not
            // read like a failure. The line replaces the draft's own blockers rather than
            // joining them: those describe a calibration that has not started working yet.
            blockers = new[]
            {
                "已经可以正常记录导随了：目前按「你申请了哪个随机任务 + 你进了哪个副本」判定。" +
                "软件还在后台找这一版真正的「匹配成功」报文，找到后会请你再核对一次，之后判定会更准。" +
                "想帮忙的话，打两把不同的随机任务就够了。",
            };
        }
        else if (_retaining)
        {
            blockers = new[]
            {
                "正在用其他玩家分享的校准记录导随，这份校准已经在本机流量里核实过。" +
                "完整记录一次进本和出本之后校准就结束；在那之前软件会继续在后台核对，对不上会自动撤下并改回本机校准。",
            };
        }

        return new CalibrationStatusSnapshot(
            _state == CalibrationState.Idle && Armed ? CalibrationState.Waiting : _state,
            _build,
            _template?.Source.ProfileId,
            draft?.Progress,
            blockers,
            draft?.Events ?? Array.Empty<CalibrationEvent>(),
            _boundAt,
            _localProfileId,
            observations is not null && _template is not null
                ? CalibrationEvidenceSummary.From(observations, _template)
                : null,
            _carriedSource);
    }

    /// <summary>
    /// Checks the user's verdicts against the current draft. Returns the draft when every
    /// event that needs confirmation was marked correct; records the rejection and returns
    /// null when any was marked wrong. Missing verdicts are a bad request.
    /// </summary>
    /// <param name="verdicts">Verdict per event id: CORRECT or WRONG.</param>
    public CalibrationDraft? Judge(IReadOnlyDictionary<string, CalibrationVerdict> verdicts) =>
        Judge(verdicts, out _);

    /// <summary>
    /// Checks the user's verdicts against the current draft, and reports the names they
    /// corrected along the way.
    /// </summary>
    /// <param name="verdicts">Verdict per event id.</param>
    /// <param name="renamings">Roulettes the player named; empty unless they corrected one.</param>
    public CalibrationDraft? Judge(
        IReadOnlyDictionary<string, CalibrationVerdict> verdicts, out IReadOnlyList<RouletteRenaming> renamings)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        renamings = Array.Empty<RouletteRenaming>();
        var draft = CurrentDraft();
        if (draft is null || draft.Status != CalibrationDraftStatus.Ready || _state != CalibrationState.Ready)
        {
            throw new CollectorException(
                ErrorCodes.CalibrationNotReady,
                "校准还没有完成，暂时没有可核对的内容。",
                new Dictionary<string, object?> { ["state"] = _state.ToString().ToUpperInvariant() });
        }

        var wrongKinds = new HashSet<string>(StringComparer.Ordinal);
        var corrected = new List<RouletteRenaming>();
        foreach (var item in draft.Events.Where(item => item.RequiresConfirmation))
        {
            if (!verdicts.TryGetValue(item.EventId, out var verdict))
            {
                throw CollectorException.BadRequest("还有事件没有核对：" + item.Label, "payload.verdicts");
            }

            if (verdict.IsWrong)
            {
                wrongKinds.Add(item.Kind);
                continue;
            }

            if (!verdict.IsRelabel)
            {
                continue;
            }

            // A name can only be corrected on a line that carries an id to attach it to. On
            // anything else the correction has nowhere to go, and silently dropping it would
            // tell the player their answer was taken when it was not.
            if (item.RouletteId is not { } rouletteId || rouletteId is < 0 or > int.MaxValue)
            {
                throw CollectorException.BadRequest(
                    "这一条没有随机任务编号，改不了名字：" + item.Label, "payload.verdicts");
            }

            corrected.Add(new RouletteRenaming((int)rouletteId, verdict.RouletteName!.Trim()));
        }

        renamings = corrected;

        if (wrongKinds.Count == 0)
        {
            return draft;
        }

        // Only the candidate the wrong line was built on is excluded: a wrong pop says nothing
        // about the zone marker, and the other way round.
        var pops = new HashSet<ushort>(_rejections.PopOpcodes);
        var zones = new HashSet<MessageKey>(_rejections.ZoneKeys);
        var popWrong = wrongKinds.Contains("finder_request") || wrongKinds.Contains("pop");
        var zoneWrong = wrongKinds.Contains("duty_enter") || wrongKinds.Contains("duty_exit");
        foreach (var message in draft.Messages)
        {
            if (popWrong && message.Name == "CONTENT_FINDER_POP")
            {
                pops.Add(message.Opcode);
            }
            else if (zoneWrong && message.Name == "ZONE_INITIALIZATION" && message.ExpectedLength is { } length)
            {
                zones.Add(new MessageKey(message.Direction, message.Opcode, length));
            }
        }

        _rejections = new CalibrationRejections(pops, zones);
        _draft = null;
        _draftAtMessage = -1;
        _state = CalibrationState.Observing;
        _generation++;
        return null;
    }

    /// <summary>
    /// Refuses a CONTENT_FINDER_POP opcode for good, the way a WRONG verdict refuses one the
    /// player rejected: the traffic itself disproved it - a message that carried three different
    /// roulette ids inside one second is a list, not an announcement - so proposing it again
    /// would walk the same machine back into the same wrong profile.
    ///
    /// Arming for another build or template forgets it, exactly as it forgets what the player
    /// rejected, because an opcode means nothing across builds. Callers withdrawing a profile
    /// therefore reject after re-arming, not before.
    /// </summary>
    /// <param name="opcode">Opcode the traffic disproved.</param>
    public void RejectPopOpcode(ushort opcode)
    {
        if (_rejections.PopOpcodes.Contains(opcode))
        {
            return;
        }

        _rejections = new CalibrationRejections(
            new HashSet<ushort>(_rejections.PopOpcodes) { opcode }, _rejections.ZoneKeys);
        _draft = null;
        _draftAtMessage = -1;
        _generation++;
    }

    /// <summary>
    /// The local profile was written; remember it. A profile that reads the server's own
    /// announcement finishes calibration. One that infers the match from the queue keeps the
    /// observer alive instead, because the announcement is still worth finding and the evidence
    /// collected so far is the head start.
    /// </summary>
    /// <param name="localProfileId">Profile id written.</param>
    /// <param name="boundAtUtc">When it was bound inside the running session, or null.</param>
    /// <param name="provisional">True when the written profile infers the match from the queue.</param>
    public void MarkDone(string localProfileId, DateTimeOffset? boundAtUtc, bool provisional = false)
    {
        _localProfileId = localProfileId;
        _boundAt = boundAtUtc;
        _provisional = provisional;
        if (provisional)
        {
            _draft = null;
            _draftAtMessage = -1;
            _state = _observer is null ? CalibrationState.Waiting : CalibrationState.Observing;
            _generation++;
            return;
        }

        _observer = null;
        _retaining = false;
        _state = CalibrationState.Done;
    }

    /// <summary>
    /// Replaces the roulette names the timeline is written with, and drops the cached draft so
    /// the next read renders under them.
    /// </summary>
    /// <param name="roulettes">Catalogue to use from now on.</param>
    public void UseRoulettes(RouletteCatalog roulettes)
    {
        ArgumentNullException.ThrowIfNull(roulettes);
        _roulettes = roulettes;
        _draft = null;
        _draftAtMessage = -1;
    }

    /// <summary>Throws away the evidence of the current session and starts observing again.</summary>
    /// <param name="captureSessionId">Running session, or null when none is running.</param>
    public void Discard(string? captureSessionId)
    {
        if (_template is null || _state == CalibrationState.Done)
        {
            return;
        }

        _draft = null;
        _draftAtMessage = -1;
        _generation++;
        _observer = null;
        // 重新观察 means "forget what you saw", and evidence read back from disk is exactly
        // that; leaving it to be adopted by the next observer would make the button a no-op.
        _carried = null;
        if (captureSessionId is not null)
        {
            Begin(captureSessionId);
        }
        else
        {
            _observer = null;
            _state = CalibrationState.Idle;
        }
    }
}
