using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// The only place that decides what a mentor roulette attempt was.
///
/// The class is pure in the sense that matters: it performs no I/O, reads no clock and knows
/// nothing about SQLite. It takes an ordered stream of verified semantic events and returns,
/// for each of them, the transition it caused plus a list of side effects for the host to
/// apply. That is what lets offline fixtures reproduce every branch without the game, Npcap
/// or a single real opcode (docs/state-machine.md section 6).
///
/// Two rules dominate the implementation: only a victory can produce COMPLETED, and an
/// unusable profile refuses everything.
/// </summary>
public sealed partial class MentorRunStateMachine
{
    private readonly ProfileBinding _profile;
    private readonly StateMachineOptions _options;
    private readonly Func<string> _newRunId;
    private readonly BoundedDedupSet _dedup;

    private RunState _state = RunState.Idle;
    private string? _runId;
    private TimeSpan _matchedMono;
    private TimeSpan? _enteredMono;
    private bool _entered;
    private int? _contentId;
    private int? _territoryId;
    private int? _jobId;
    private ContentFinderPop? _pendingQueue;
    private bool _matchObserved;

    // The queue request behind an announced match. The announcement only dates the match; the
    // request is still what says the player queued for the mentor roulette, and it goes on
    // saying so after the announcement's short window has closed (see CanEnterDuty).
    private ContentFinderPop? _announcedRequest;
    private int _announcedRefreshes;
    private int _matchOffers;

    /// <summary>
    /// How far apart two pops of one run must be to be two popups on the player's screen. A
    /// client sends one match as three or four messages inside a second; a match offered again
    /// after somebody withdrew comes after the accept timer at the very least.
    /// </summary>
    public static readonly TimeSpan NewOfferGap = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times the run in flight has been offered: 1 at the first pop, one more for every
    /// later pop that is a new popup rather than a copy of the last one. Zero with no run. The
    /// state does not change when a match is offered again, so this is what lets the host tell
    /// the desktop that there is something new to say.
    /// </summary>
    public int MatchOffers => _matchOffers;

    /// <summary>
    /// Trail rows one announced match may earn by being announced again. A client sends the
    /// announcement three or four times for one match; a learned message that turns out to be
    /// chatty must not write a row per arrival for as long as the match stands.
    /// </summary>
    public const int MaxAnnouncedRefreshes = 16;

    // Remembered across runs and across states, because both observations arrive outside the
    // run they belong to: the job is announced at login and on every class change, and the
    // territory is announced just before the entry marker (docs/state-machine.md section 3.11).
    private int? _lastKnownJobId;
    private TerritoryObserved? _lastTerritory;
    private bool _profileLost;

    /// <summary>Creates a machine bound to one protocol profile.</summary>
    /// <param name="profile">Profile in force. Anything not usable makes the machine refuse every event.</param>
    /// <param name="options">Tunables; defaults are used when omitted.</param>
    /// <param name="newRunId">Identifier factory; injected so replays can be deterministic.</param>
    public MentorRunStateMachine(
        ProfileBinding profile,
        StateMachineOptions? options = null,
        Func<string>? newRunId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _options = options ?? StateMachineOptions.Default;
        _newRunId = newRunId ?? (() => Guid.NewGuid().ToString("D"));
        _dedup = new BoundedDedupSet(_options.DedupCapacity);
    }

    /// <summary>Current state. A terminal state stays observable until the next event arrives.</summary>
    public RunState State => _state;

    /// <summary>Run currently being tracked, or null.</summary>
    public string? CurrentRunId => _runId;

    /// <summary>Number of events refused because the profile was not usable.</summary>
    public int ParserErrorCount { get; private set; }

    /// <summary>Number of events ignored because they duplicated an earlier observation.</summary>
    public int DuplicateCount { get; private set; }

    /// <summary>Current size of the bounded duplicate set.</summary>
    public int DedupSetCount => _dedup.Count;

    /// <summary>True when the machine will act on events at all.</summary>
    public bool IsUsable => _profile.IsUsable && !_profileLost;

    /// <summary>Whether this machine's bound profile uses a queue request as the match.</summary>
    public bool MatchFromQueue => _profile.MatchFromQueue;

    /// <summary>
    /// True while the match in flight was opened by the server's own announcement rather than
    /// inferred from the queue request. On a queue-inferred profile this is the difference
    /// between "you queued and then a duty loaded" and "the popup is on your screen now", which
    /// is the whole reason the desktop speaks at one of them and not the other.
    /// </summary>
    public bool MatchObserved => _matchObserved;

    /// <summary>
    /// True while a queue request is parked on a queue-inferred profile and could still become a run: the
    /// machine reads IDLE, but the player is queued, and replacing the machine now would lose the duty
    /// that request leads to. A request older than the match window no longer counts, exactly as the
    /// next event would forget it.
    /// </summary>
    /// <param name="mono">Monotonic reading of now, on the capture source's clock.</param>
    public bool HasParkedQueue(TimeSpan mono) =>
        _pendingQueue is { } pending && mono - pending.Mono <= _options.MatchWindow;

    /// <summary>
    /// How long the duty may take to load. An announced match has spent its queue already, so
    /// only the confirmation and the loading screen are left.
    /// </summary>
    private TimeSpan EntryWindow => _matchObserved ? _options.AnnouncedWindow : _options.MatchWindow;

    /// <summary>Monotonic reading taken when the current match popped.</summary>
    public TimeSpan MatchedMono => _matchedMono;

    /// <summary>Most recent job observed in any state, or null when none was ever seen.</summary>
    public int? LastKnownJobId => _lastKnownJobId;

    /// <summary>Most recent territory announcement, or null when none was ever seen.</summary>
    public TerritoryObserved? LastTerritory => _lastTerritory;

    /// <summary>What this machine knows about the player, independently of any run.</summary>
    public StateMachineMemory Memory => new(_lastKnownJobId);

    /// <summary>
    /// Collapses a terminal state to IDLE without waiting for the next declared message.
    ///
    /// Normalising only on the way into <see cref="Handle"/> leaves a player who finished a
    /// duty and then idled in a city reading as UNKNOWN_FINAL_STATE until the next
    /// profile-declared opcode arrives (review finding L-9). Idempotent, and never touches a
    /// run that is still in flight.
    /// </summary>
    public void NormalizeIfTerminal() => NormalizeTerminalState();

    /// <summary>Feeds one verified semantic event and returns what it caused.</summary>
    /// <param name="ev">
    /// The event. Producing it is a promise that every required field was parsed from a
    /// profile-declared opcode and structure.
    /// </param>
    public TransitionResult Handle(SemanticEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (!IsUsable)
        {
            ParserErrorCount++;
            var reason = _profileLost ? "PROFILE_LOST" : "PROFILE_NOT_USABLE";
            return TransitionResult.Ignored(
                _state,
                _runId,
                duplicate: false,
                commands: new StateCommand[]
                {
                    new RecordParserErrorCommand(reason, "refused " + ev.EventType + ": fail-closed"),
                });
        }

        var key = ev.Key.ToCanonicalString();
        if (!_dedup.Add(key))
        {
            DuplicateCount++;
            return TransitionResult.Ignored(_state, _runId, duplicate: true);
        }

        Remember(ev);
        NormalizeTerminalState();

        return _state switch
        {
            RunState.Idle => HandleIdle(ev),
            RunState.MentorMatched => HandleMentorMatched(ev),
            RunState.EnteredDuty => HandleEnteredDuty(ev),
            _ => TransitionResult.Ignored(_state, _runId),
        };
    }

    /// <summary>
    /// Files away the two observations that are about the player rather than about the run.
    ///
    /// Both routinely arrive in a state that has nowhere to put them -- the job at login, long
    /// before any pop, and the territory a few tens of milliseconds before the entry marker --
    /// so the machine keeps the latest of each and stamps it on the run once there is one.
    /// Remembering is not accepting: the event still goes through the per-state handlers.
    /// </summary>
    /// <param name="ev">Event about to be dispatched.</param>
    private void Remember(SemanticEvent ev)
    {
        switch (ev)
        {
            case PlayerJob job:
                _lastKnownJobId = job.JobId;
                break;

            case TerritoryObserved territory:
                _lastTerritory = territory;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The remembered territory announcement, when it is recent enough to be about the zone
    /// being entered now rather than about some earlier one.
    /// </summary>
    /// <param name="mono">Monotonic reading of the entry.</param>
    private TerritoryObserved? RecentTerritory(TimeSpan mono)
    {
        if (_lastTerritory is not { } territory)
        {
            return null;
        }

        var age = mono - territory.Mono;
        return age >= TimeSpan.Zero && age <= _options.TerritoryMemory ? territory : null;
    }

    private void NormalizeTerminalState()
    {
        if (_state is not (RunState.Idle or RunState.MentorMatched or RunState.EnteredDuty))
        {
            _state = RunState.Idle;
            _runId = null;
        }
    }

    private TransitionResult HandleIdle(SemanticEvent ev)
    {
        if (ev is ProfileLost lost)
        {
            _pendingQueue = null;
            _profileLost = true;
            return TransitionResult.Ignored(
                _state,
                null,
                commands: new StateCommand[]
                {
                    new RecordParserErrorCommand("PROFILE_LOST", lost.Detail ?? "profile became unusable"),
                });
        }

        if (_profile.MatchFromQueue)
        {
            return HandlePendingQueue(ev);
        }

        // A pop for any other roulette is somebody else's business, and a zone
        // initialization on its own is never evidence of a mentor roulette. Both leave the
        // machine in IDLE and create nothing at all.
        if (ev is not ContentFinderPop pop || pop.RouletteId != _profile.MentorRouletteId)
        {
            return TransitionResult.Ignored(_state, null);
        }

        return StartRun(pop, Array.Empty<StateCommand>());
    }

    private TransitionResult HandlePendingQueue(SemanticEvent ev)
    {
        // A request proves only that the player started queueing. Local calibration may not
        // know the cancellation message, so persisting it here leaves a phantom active run
        // after cancellation and an ordinary teleport. Keep only bounded session evidence.
        if (_pendingQueue is { } pending && ev.Mono - pending.Mono > _options.MatchWindow)
        {
            _pendingQueue = null;
        }

        switch (ev)
        {
            case ContentFinderPop pop:
                _pendingQueue = pop.RouletteId == _profile.MentorRouletteId ? pop : null;
                return new TransitionResult(_state, _state, false, true, false, null,
                    Array.Empty<StateCommand>());

            case MatchCancelled:
            case CaptureStopped:
            case ConnectionLost:
            case EventSequenceGap:
                _pendingQueue = null;
                break;

            // The server said the match is here. The roulette still comes from the request -
            // nothing in the announcement names one - but the moment is the server's, and the
            // run opens now rather than when the duty finishes loading.
            case MatchAnnounced announced when _pendingQueue is { } matched:
                _pendingQueue = null;
                var opened = StartRun(matched, Array.Empty<StateCommand>(), announced);
                _matchObserved = true;
                _announcedRequest = matched;
                _announcedRefreshes = 0;
                return opened;

            case ZoneInitialization zone when zone.IsDutyInstance != false &&
                _pendingQueue is { } queued && CanEnterDuty(zone, queued.Mono, queued.ContentId):
                // Create and enter in one transaction. No MENTOR_MATCHED event or incomplete
                // history row escapes between the queue request and the confirmed duty.
                _pendingQueue = null;
                var started = StartRun(queued, Array.Empty<StateCommand>());
                var entered = EnterDuty(zone);
                return entered with
                {
                    FromState = RunState.Idle,
                    Commands = started.Commands.Concat(entered.Commands).ToArray(),
                };
        }

        // A normal teleport can happen while the player is still queueing. It neither
        // creates a run nor proves cancellation, so the request remains usable until expiry.
        return TransitionResult.Ignored(_state, null);
    }

    /// <summary>Opens a run for a mentor match.</summary>
    /// <param name="pop">The pop, or the queue request standing in for it, that names the roulette.</param>
    /// <param name="before">Commands that must be applied first, such as closing a previous run.</param>
    /// <param name="trigger">
    /// The observation that dates the match, when it is not the pop itself. An announcement
    /// recognised by its timing carries no roulette, so it dates a match the request names: the
    /// run is stamped with the moment the popup appeared and the trail shows the announcement.
    /// </param>
    private TransitionResult StartRun(
        ContentFinderPop pop, IReadOnlyList<StateCommand> before, SemanticEvent? trigger = null)
    {
        var matched = trigger ?? pop;
        var runId = _newRunId();
        var commands = new List<StateCommand>(before.Count + 3);
        commands.AddRange(before);
        commands.Add(new CreateRunCommand(runId, matched.ObservedAtUtc, pop.RouletteId, pop.ContentId)
        {
            MatchedMono = matched.Mono,
        });

        // The job is announced at login and on every class change, so for most runs the only
        // observation of it happened while the machine was IDLE. Stamping the remembered one
        // here is how a run recorded from a pop knows the job; RecordJob still overwrites it.
        if (_lastKnownJobId is { } knownJob)
        {
            commands.Add(new SetJobCommand(runId, knownJob));
        }

        commands.Add(new AppendEventCommand(
            runId, matched, RunState.Idle, RunState.MentorMatched, DetectionConfidence.High));

        _runId = runId;
        _state = RunState.MentorMatched;
        _matchedMono = matched.Mono;
        _matchOffers = 1;
        _entered = false;
        _enteredMono = null;
        _contentId = pop.ContentId;
        _territoryId = null;
        _jobId = _lastKnownJobId;

        return new TransitionResult(
            RunState.Idle, RunState.MentorMatched, true, true, false, runId, commands);
    }

    private TransitionResult HandleMentorMatched(SemanticEvent ev)
    {
        switch (ev)
        {
            // A lost cancellation or replacement queue breaks the association with a later
            // duty. No entry was observed, but cancellation itself is uncertain: retain
            // review rather than guessing, and clear the announcement's parked request too.
            // Every game connection closing (docs/state-machine.md 3.6) is the same loss
            // in a different form: the server-side match cannot survive it, and what the
            // player does after relogging is fresh evidence, not this match's entry.
            case EventSequenceGap:
            case ConnectionLost:
                return Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Low, pendingReview: true);

            // Being placed back into a non-duty zone is an explicit return to idle.
            case ZoneInitialization { IsDutyInstance: false }:
                return Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Medium);

            case ZoneInitialization zone when CanEnterDuty(zone, _matchedMono, _contentId):
                return EnterDuty(zone);

            // A zone change the profile cannot classify, arriving after the match window has
            // lapsed, is the lapsed match itself: the player went somewhere without entering.
            case ZoneInitialization { IsDutyInstance: null } zone
                when zone.Mono - _matchedMono > EntryWindow:
                // An announced match that lapsed says nothing about the queue behind it: the
                // request has its own, much longer window, and a player who teleported after a
                // false alarm is still queued. Without this an early or wrong announcement would
                // cost the very record the queue request alone would have made.
                var standing = _announcedRequest;
                var lapsed = Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Low);
                if (standing is not null && zone.Mono - standing.Mono <= _options.MatchWindow)
                {
                    _pendingQueue = standing;
                }

                return lapsed;

            case ZoneInitialization:
                return TransitionResult.Ignored(_state, _runId);

            // The server announced the match again: somebody declined and the finder re-formed
            // the party, or this client simply sends the announcement three times for one match.
            // Either way it is the same run, and the entry window has to move with it.
            case MatchAnnounced announced when _matchObserved:
                if (_announcedRefreshes >= MaxAnnouncedRefreshes)
                {
                    return TransitionResult.Ignored(_state, _runId);
                }

                _announcedRefreshes++;
                return RefreshMatch(announced);

            // On a profile that stands the player's request in for the match, a CONTENT_FINDER_POP
            // is that request. Arriving while an announced match stands, it says the player let
            // that match go and asked the finder for something else; it is never the same match
            // being offered again, whatever the roulette.
            case ContentFinderPop pop when _matchObserved:
                return RestartOn(pop, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Medium);

            case ContentFinderPop pop when pop.Mono - _matchedMono >= _options.MatchWindow:
                return RestartOn(pop, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Medium);

            // The same match offered again: somebody declined and the finder re-formed the
            // party. It is one run, but the window has to move with it -- measured from the
            // first pop, the entry that follows a late re-pop falls outside the window and
            // the duty is lost entirely.
            case ContentFinderPop pop when pop.RouletteId == _profile.MentorRouletteId:
                return RefreshMatch(pop);

            // A verified pop for some other roulette, inside the window, reads as evidence
            // that the mentor match is over: the finder cannot offer two duties at once, so
            // the player declined this one and queued for something else. Ignoring it leaves
            // the run open and records the level-roulette entry seconds later as a mentor
            // entry (review finding M-3).
            //
            // "Some other roulette" rests entirely on the roulette id decoded at the profile's
            // offset, and no mentor roulette sample has been captured on the CN client to check
            // that offset against. Until one exists this closes the run at LOW confidence and
            // pending review, so a re-sent pop, or a field that means something else under a
            // different finder state, costs the user one entry to confirm rather than silently
            // rewriting a mentor run as cancelled (review finding R-7).
            case ContentFinderPop:
                return Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Low, pendingReview: true);

            case MatchCancelled:
                return Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Medium);

            // Time alone is not evidence of cancellation. The timeout only makes the match
            // stale; a later pop or explicit return-to-idle signal performs the transition.
            case TimeoutTick:
                return TransitionResult.Ignored(_state, _runId);

            // The duty was never entered, so the run can never be an attempt whatever
            // happened to the capture or to the connection.
            case CaptureStopped:
                return Finish(
                    ev, RunState.CancelledBeforeEntry, RunResult.CancelledBeforeEntry,
                    DetectionConfidence.Low);

            case PlayerJob job:
                return RecordJob(job);

            case ProfileLost lost:
                return LoseProfile(lost);

            default:
                return TransitionResult.Ignored(_state, _runId);
        }
    }

    private TransitionResult HandleEnteredDuty(SemanticEvent ev)
    {
        switch (ev)
        {
            // The single path to COMPLETED. There is no other one, by design.
            case DutyResult { Victory: true }:
                return Finish(ev, RunState.Completed, RunResult.Completed, CompletionConfidence(outcomeObserved: true));

            // A verified non-victory result is an observed outcome.
            case DutyResult:
                return Finish(
                    ev, RunState.LeftOrAbandoned, RunResult.LeftOrAbandoned, CompletionConfidence(outcomeObserved: true));

            // An exit alone cannot reveal whether a duty was won when the profile has
            // no result message, even if it can identify the departing zone precisely.
            case ZoneLeft:
            case InstanceLeft:
            case ZoneInitialization { IsDutyInstance: false }:
                return _profile.CanDetectDutyResult
                    ? Finish(ev, RunState.LeftOrAbandoned, RunResult.LeftOrAbandoned, CompletionConfidence(outcomeObserved: false))
                    : Finish(ev, RunState.UnknownFinalState, RunResult.Unknown, DetectionConfidence.Low,
                        pendingReview: true);

            // The profile cannot tell a duty zone from the open world, so any zone change
            // after entry is the duty ending. Without a victory it was not completed when the
            // profile can observe outcomes; when it cannot, the outcome is unknown and the
            // record waits for the user (docs/state-machine.md section 3.10).
            case ZoneInitialization { IsDutyInstance: null } when _profile.CanDetectDutyResult:
                return Finish(
                    ev, RunState.LeftOrAbandoned, RunResult.LeftOrAbandoned, CompletionConfidence(outcomeObserved: false));

            case ZoneInitialization { IsDutyInstance: null }:
                return Finish(
                    ev, RunState.UnknownFinalState, RunResult.Unknown, DetectionConfidence.Low,
                    pendingReview: true);

            case ZoneInitialization:
                return TransitionResult.Ignored(_state, _runId);

            case ConnectionLost:
                return Finish(ev, RunState.Disconnected, RunResult.Disconnected, DetectionConfidence.Low);

            case CaptureStopped _:
            case EventSequenceGap _:
                return Finish(ev, RunState.Interrupted, RunResult.Interrupted, DetectionConfidence.Low);

            case ContentFinderPop pop when _profile.CanDetectDutyResult:
                return RestartOn(pop, RunState.LeftOrAbandoned, RunResult.LeftOrAbandoned,
                    CompletionConfidence(outcomeObserved: false));

            case ContentFinderPop pop:
                return RestartOn(pop, RunState.UnknownFinalState, RunResult.Unknown,
                    DetectionConfidence.Low, pendingReview: true);

            case PlayerJob job:
                return RecordJob(job);

            // A territory announcement that arrives after the entry marker still identifies
            // the duty we are in, but only while the run has no territory yet and only for as
            // long as the announcement can still be about the entry: once the run has a
            // territory, or the entry is far behind, the next announcement is the zone the
            // player is leaving for (review finding L-6).
            case TerritoryObserved territory when _territoryId is null && BelongsToEntry(territory):
                return RecordTerritory(territory);

            case ProfileLost lost:
                return LoseProfile(lost);

            // Time alone never ends a duty. A tick inside a duty is deliberately inert.
            default:
                return TransitionResult.Ignored(_state, _runId);
        }
    }

    private TransitionResult EnterDuty(ZoneInitialization zone)
    {
        var runId = _runId!;
        _state = RunState.EnteredDuty;
        _entered = true;
        _enteredMono = zone.Mono;
        _contentId = zone.ContentId ?? _contentId;
        _territoryId = zone.TerritoryId ?? _territoryId;

        var commands = new List<StateCommand>(4)
        {
            new EnterDutyCommand(runId, zone.ObservedAtUtc, _contentId, _territoryId),
            new AppendEventCommand(
                runId, zone, RunState.MentorMatched, RunState.EnteredDuty, DetectionConfidence.High),
        };

        // The verified entry marker of the CN profile carries neither a territory nor a
        // content id, so without the announcement that preceded it the run would be stored
        // with no duty at all. The announcement is only borrowed when the entry itself said
        // nothing: a marker that does carry the zone is always the better answer.
        if (_territoryId is null && _contentId is null && RecentTerritory(zone.Mono) is { } observed)
        {
            _territoryId = observed.TerritoryId;
            commands.Add(new SetDutyCommand(runId, observed.TerritoryId));
            commands.Add(new AppendEventCommand(
                runId, observed, RunState.EnteredDuty, RunState.EnteredDuty,
                DetectionConfidence.Medium));
        }

        return new TransitionResult(
            RunState.MentorMatched, RunState.EnteredDuty, true, true, false, runId, commands);
    }

    /// <summary>
    /// Moves the match anchor to a repeated pop of the same roulette, keeping the run.
    ///
    /// The re-pop is appended to the trail rather than swallowed: the audit has to be able
    /// to show why an entry three minutes after the first pop was still accepted.
    /// </summary>
    /// <param name="match">The repeated mentor pop, or the repeated announcement of one.</param>
    private TransitionResult RefreshMatch(SemanticEvent match)
    {
        var runId = _runId!;
        if (match.Mono - _matchedMono >= NewOfferGap)
        {
            _matchOffers++;
        }

        _matchedMono = match.Mono;
        _contentId = (match as ContentFinderPop)?.ContentId ?? _contentId;

        var commands = new StateCommand[]
        {
            new AppendEventCommand(
                runId, match, RunState.MentorMatched, RunState.MentorMatched, DetectionConfidence.High),
        };

        return new TransitionResult(
            RunState.MentorMatched, RunState.MentorMatched, false, true, false, runId, commands);
    }

    private bool CanEnterDuty(ZoneInitialization zone, TimeSpan matchedMono, int? contentId)
    {
        var elapsed = zone.Mono - matchedMono;
        if (elapsed < TimeSpan.Zero)
        {
            return false;
        }

        // A queue request opens a queue window, not an accept timer. Only a zone the duty
        // table recognises confirms an entry; an ordinary teleport creates no run.
        if (_profile.MatchFromQueue && !EntersKnownDuty(zone))
        {
            return false;
        }

        if (contentId is { } matchedContent && zone.ContentId is { } zoneContent)
        {
            return matchedContent == zoneContent;
        }

        if (elapsed <= EntryWindow)
        {
            return true;
        }

        // Past the announcement's own window the request still stands behind a known duty, for
        // as long as it would have without any announcement (the MatchFromQueue test above has
        // already required the known duty). The announcement only ever adds.
        return _matchObserved && _announcedRequest is { } request &&
            zone.Mono - request.Mono <= _options.MatchWindow;
    }

    /// <summary>True when this zone change lands in a territory the duty table knows.</summary>
    /// <param name="zone">Entry marker being considered.</param>
    private bool EntersKnownDuty(ZoneInitialization zone)
    {
        if (_options.IsKnownDuty is not { } known)
        {
            return false;
        }

        var territory = zone.TerritoryId ?? _territoryId ?? RecentTerritory(zone.Mono)?.TerritoryId;
        return territory is { } id && known(id);
    }

    /// <summary>
    /// True when a territory announcement seen after the entry marker is still close enough
    /// to it to be the zone that was entered.
    /// </summary>
    /// <param name="territory">Announcement observed while inside a duty.</param>
    private bool BelongsToEntry(TerritoryObserved territory)
    {
        if (_enteredMono is not { } entered)
        {
            return false;
        }

        var age = territory.Mono - entered;
        return age >= TimeSpan.Zero && age <= _options.TerritoryMemory;
    }

    private TransitionResult RecordJob(PlayerJob job)
    {
        // The CN profile binds PLAYER_JOB to a status packet the client also sends on every
        // experience gain, so an unchanged job arrives dozens of times per duty; writing the
        // same value again would cost a row update, a trail row and two live events each time
        // (review finding M-4). Remember() has already filed the observation away.
        if (_jobId == job.JobId)
        {
            return TransitionResult.Ignored(_state, _runId);
        }

        var runId = _runId!;
        _jobId = job.JobId;
        var commands = new StateCommand[]
        {
            new SetJobCommand(runId, job.JobId),
            new AppendEventCommand(runId, job, _state, _state, DetectionConfidence.High),
        };
        return new TransitionResult(_state, _state, false, true, false, runId, commands);
    }

    /// <summary>Applies a territory announcement to the run already in a duty.</summary>
    /// <param name="territory">Announcement to apply.</param>
    private TransitionResult RecordTerritory(TerritoryObserved territory)
    {
        var runId = _runId!;
        _territoryId = territory.TerritoryId;
        var commands = new StateCommand[]
        {
            new SetDutyCommand(runId, territory.TerritoryId),
            new AppendEventCommand(runId, territory, _state, _state, DetectionConfidence.Medium),
        };
        return new TransitionResult(_state, _state, false, true, false, runId, commands);
    }

    private TransitionResult LoseProfile(ProfileLost lost)
    {
        var result = Finish(lost, RunState.UnknownFinalState, RunResult.Unknown, DetectionConfidence.None);
        _profileLost = true;
        return result;
    }

    /// <summary>Closes the run in flight and, when the new pop is a mentor pop, opens the next one.</summary>
    private TransitionResult RestartOn(
        ContentFinderPop pop, RunState terminal, RunResult result, DetectionConfidence confidence,
        bool pendingReview = false)
    {
        var closeCommands = FinishCommands(pop, terminal, result, confidence, pendingReview);
        var from = _state;
        ClearRun(terminal);
        _state = RunState.Idle;

        if (pop.RouletteId != _profile.MentorRouletteId)
        {
            return new TransitionResult(from, terminal, true, true, false, null, closeCommands);
        }

        if (_profile.MatchFromQueue)
        {
            _pendingQueue = pop;
            return new TransitionResult(from, RunState.Idle, true, true, false, null, closeCommands);
        }

        return StartRun(pop, closeCommands);
    }

    /// <summary>
    /// HIGH needs the duty named by the wire, not by one particular field. The CN client sends
    /// the territory and never a content id, so asking for the content id alone made HIGH
    /// unreachable there. Both fields only ever come from protocol events here; the local
    /// content-to-territory display mapping never reaches them (docs/state-machine.md section 4).
    /// An outcome read off a DUTY_RESULT is observed; one concluded from the player leaving the
    /// zone is not, however well the duty is known, and stays at MEDIUM.
    /// </summary>
    private DetectionConfidence CompletionConfidence(bool outcomeObserved) =>
        outcomeObserved && _entered && (_contentId is not null || _territoryId is not null) && _jobId is not null
            ? DetectionConfidence.High
            : DetectionConfidence.Medium;

    private IReadOnlyList<StateCommand> FinishCommands(
        SemanticEvent ev, RunState toState, RunResult result, DetectionConfidence confidence,
        bool pendingReview = false)
    {
        var runId = _runId!;
        long? durationMs = null;
        if (_enteredMono is { } entered)
        {
            var elapsed = ev.Mono - entered;
            durationMs = elapsed < TimeSpan.Zero ? 0L : (long)elapsed.TotalMilliseconds;
        }

        return new StateCommand[]
        {
            new FinishRunCommand(runId, ev.ObservedAtUtc, durationMs, result, confidence, pendingReview),
            new AppendEventCommand(runId, ev, _state, toState, confidence),
        };
    }

    private TransitionResult Finish(
        SemanticEvent ev, RunState toState, RunResult result, DetectionConfidence confidence,
        bool pendingReview = false)
    {
        var runId = _runId!;
        var commands = FinishCommands(ev, toState, result, confidence, pendingReview);
        var from = _state;
        ClearRun(toState);
        return new TransitionResult(from, toState, true, true, false, runId, commands);
    }

    private void ClearRun(RunState terminal)
    {
        _state = terminal;
        _runId = null;
        _entered = false;
        _enteredMono = null;
        _contentId = null;
        _territoryId = null;
        _jobId = null;
        _pendingQueue = null;
        _matchObserved = false;
        _announcedRequest = null;
        _announcedRefreshes = 0;
        _matchOffers = 0;
    }
}
