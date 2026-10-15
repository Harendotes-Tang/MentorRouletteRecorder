using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Shared calibration inside the live pipeline: registers
/// downloads, claims what they found, stages what each candidate would have recorded, verifies,
/// writes and binds the one that passes, keeps watching it until it records a complete duty, and
/// withdraws it when local traffic contradicts it, when the index of a download that was already out
/// revokes it, or when the player refuses shared calibration for the build (不用共享的，我自己校准).
///
/// Threading. Every public member except <see cref="Import"/> and <see cref="WhenIdleAsync"/> is
/// called with the pipeline's gate held, and that gate is what this class locks for its own
/// background work. Downloads, profile writes and catalogue reloads run off the gate; their results
/// are claimed under it only while the <see cref="SharedKey"/> they were started for is still the one
/// in force. Nothing unverified reaches the state machine: candidates only stage, and the stage is
/// handed over inside <see cref="ISharedCalibrationHost.CommitSharedBind"/>.
/// </summary>
internal sealed partial class SharedCalibrationSession
{
    /// <summary>Setting that remembers, per region and build, when the player accepted queue inference for a shared code.</summary>
    internal const string QueueConsentSetting = "calibration.shared_queue_inference_consent";

    /// <summary>Region/build consents kept; the oldest are forgotten first.</summary>
    internal const int MaxConsents = 16;

    /// <summary>Refusal token of 不用共享的: the import refusal reason and the last refusal after a withdrawal.</summary>
    internal const string UserRejectedReason = "USER_REJECTED";

    /// <summary>
    /// How long a download runs before the card is told about it, so one that answers at once - the
    /// kill switch, an unreachable network - never flickers the card.
    /// </summary>
    internal static readonly TimeSpan FetchingNoticeDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Shortest gap between two 立即检查 that both reach the network. Kept in memory and quite apart from the
    /// six-hour throttle the store writes to disk: that one answers how often a build is worth asking about, this
    /// one answers how often the button may be pressed at all. One round is up to twenty-seven requests to the
    /// repository and its mirrors, and until now anything that could open the pipe could ask for another the
    /// instant the last one ended, however quickly it had failed (review finding 13). Five seconds, chosen for
    /// the player rather than against a caller in a loop: someone who watches a check fail, reads the card and
    /// decides to try again takes longer than that, so a genuine second press is never the one refused.
    /// </summary>
    internal static readonly TimeSpan ManualCheckInterval = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, int> NoCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>What a candidate the consensus does not cover counts as (<see cref="ConsensusOf"/>): not preferred.</summary>
    private static readonly SharedConsensus Unjudged = new(Array.Empty<string>(), Preferred: false);

    private readonly object _gate;

    // Serialises the two background tasks that touch <region>.<build>.shared.json - a bind writing it and a
    // withdrawal deleting it (review finding 12). Always taken before the gate and never while the gate is held;
    // one lock for the session rather than one per region and build, because a session answers for exactly one
    // of them and a change of build resets it.
    private readonly object _file = new();

    private readonly ISharedCalibrationHost _host;
    private readonly CalibrationServices _services;
    private readonly IClock _clock;
    private readonly SettingsRepository? _settings;
    private readonly List<Candidate> _candidates = new();
    private readonly List<SharedCandidateSummary> _rejectedSummaries = new();
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private readonly HashSet<(string Code, string Session)> _contradictions = new();
    private readonly HashSet<string> _withdrawn = new(StringComparer.Ordinal);
    private SharedKey? _key;
    private bool _enabled;
    private bool _stopped;
    private FetchTicket? _fetch;
    private bool _fetchVisible;
    private TimeSpan? _nextAutoFetchAt;
    private TimeSpan? _lastManualCheckAt;
    private SharedFetchStatus? _lastFetchStatus;
    private IReadOnlyList<SharedSourceAttempt> _lastAttempts = Array.Empty<SharedSourceAttempt>();
    private BoundProfile? _bound;
    private Candidate? _binding;
    private bool _superseded;
    private string? _lastRefusal;
    private (Region Region, string Build, DateTimeOffset At)? _consent;
    private (Region Region, string Build, bool Rejected)? _userRejection;
    private DateTimeOffset? _lastSentAtUtc;
    private SharedFetchStatus? _lastSentStatus;
    private SharedRecheckRecord? _recheck;
    private IReadOnlyList<Prepared>? _deferred;
    private int _pending;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates the session; nothing is scheduled until <see cref="Sync"/>.</summary>
    /// <param name="gate">The pipeline's gate.</param>
    /// <param name="host">The pipeline.</param>
    /// <param name="services">Fetch, store, write and reload seams.</param>
    /// <param name="clock">Clock for fetch bookkeeping and profile timestamps.</param>
    /// <param name="settings">Where consent is remembered; null remembers it for this run only.</param>
    /// <param name="enabled"><c>capture.shared_calibration_enabled</c> as it stands.</param>
    public SharedCalibrationSession(
        object gate, ISharedCalibrationHost host, CalibrationServices services, IClock clock, SettingsRepository? settings, bool enabled)
    {
        _gate = gate;
        _host = host;
        _services = services;
        _clock = clock;
        _settings = settings;
        _enabled = enabled;
        _idle.TrySetResult();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Brings the session in line with what calibration is armed for and with the selection in force, and
    /// fetches when due. Every arm of calibration ends here (<c>LiveProtocolPipeline.ArmCalibration</c>), so
    /// the shared profile the selection holds is always the one being watched.
    /// </summary>
    public void Sync()
    {
        if (_stopped)
        {
            return;
        }

        var context = _host.SharedContext();
        if (context?.Key != _key)
        {
            Reset(context?.Key);
        }

        ReconcileBound(_host.SharedSelection());
        if (context is not null)
        {
            AdoptBound(context);
            MaybeFetch(context, manual: false);
        }

        DescribeSettled(context);
    }

    /// <summary>
    /// True while <paramref name="selection"/> is a shared profile that has not yet recorded one complete
    /// entry and exit, so calibration must stay armed beside it (plan §4.1 step 7). A question only: the
    /// run table answers it for a profile not adopted yet, and adopting happens in <see cref="Sync"/>.
    /// </summary>
    public bool Retains(ProfileSelection selection)
    {
        if (!selection.IsUsable || selection.Origin != ProfileOrigin.Shared || selection.Profile is not { } profile)
        {
            return false;
        }

        // Across a restart a recorded duty alone does not prove the profile (plan §18.4): the watch ended only if
        // this very document was recorded as settled, which is what the store answers.
        var proven = _bound is { } bound && string.Equals(bound.ProfileId, profile.ProfileId, StringComparison.Ordinal)
            ? bound.Proven
            : _host.HasFinishedSharedRun(profile.ProfileId, BoundSince(profile)) && IsSettled(profile);
        return !proven;
    }

    private bool IsSettled(ProtocolProfile profile) =>
        Attempt(() => _services.SharedCalibrations.IsSettled(profile.Region, profile.GameBuild, profile.ProfileSha256));

    private void RecordSettled(BoundProfile bound)
    {
        if (bound.ProfileSha256 is { } sha)
        {
            Attempt(() => _services.SharedCalibrations.RecordSettled(bound.Region, bound.GameBuild, sha, _clock.UtcNow));
        }
    }

    /// <summary>When this profile document began recording, as kept with it; null when nothing was kept.</summary>
    private DateTimeOffset? BoundSince(ProtocolProfile profile) =>
        Attempt(() => _services.SharedCalibrations.BoundSince(profile.Region, profile.GameBuild, profile.ProfileSha256));

    /// <summary>Keeps when the bound document began recording, so a restart still tells its runs from an earlier code's.</summary>
    private void RecordBound(BoundProfile bound)
    {
        if (bound.ProfileSha256 is { } profile && bound.Sha is { } code && bound.RecordsFromUtc is { } from)
        {
            Attempt(() => _services.SharedCalibrations.RecordBound(bound.Region, bound.GameBuild, profile, code, from));
        }
    }

    /// <summary>The setting changed. Off cancels a download and drops downloaded candidates; a profile in use stays.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _nextAutoFetchAt = null;
            Sync();
        }
        else
        {
            CancelFetch();
            foreach (var candidate in _candidates.Where(item => item.Source == SharedCandidateSource.Downloaded).ToArray())
            {
                Drop(candidate, rejected: false);
            }
        }

        _host.SharedCalibrationChanged();
    }

    /// <summary>立即检查: fetch now, past the six-hour throttle. The setting and the kill switch still apply.</summary>
    public SharedCheckOutcome CheckNow() =>
        _stopped || _host.SharedContext() is not { } context ? SharedCheckOutcome.NotNeeded : MaybeFetch(context, manual: true);

    /// <summary>The player accepted queue inference; remembered for this region and build.</summary>
    public SharedConsentOutcome AcceptQueueInference()
    {
        if (_stopped || _host.SharedContext() is not { } context ||
            !_candidates.Any(candidate => candidate.Status == SharedCandidateStatus.AwaitingConsent))
        {
            return SharedConsentOutcome.NothingToAccept;
        }

        RememberConsent(context.Key, _clock.UtcNow);
        Evaluate();
        _host.SharedCalibrationChanged();
        return SharedConsentOutcome.Accepted;
    }

    /// <summary>
    /// 重新观察: forget the rejection records of this build - the player's own refusal included - and offer the
    /// stored codes again.
    /// </summary>
    public void OnDiscard()
    {
        if (_stopped || RefusalScope() is not { } scope)
        {
            return;
        }

        Attempt(() => _services.SharedCalibrations.ClearRejections(scope.Region, scope.Build));
        _userRejection = (scope.Region, scope.Build, false);
        _rejected.Clear();
        _rejectedSummaries.Clear();
        _contradictions.Clear();
        _superseded = false;
        _lastRefusal = null;
        _nextAutoFetchAt = null;
        if (_host.SharedContext() is { } context)
        {
            MaybeFetch(context, manual: false);
        }

        _host.SharedCalibrationChanged();
    }

    /// <summary>A capture session started; each candidate stages it when no parser will be bound.</summary>
    public void OnCaptureStarted(string captureSessionId, bool staging)
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage = staging ? new SharedCandidateStage(captureSessionId, candidate.Prepared.StagingProfile) : null;
            candidate.Blocked = false;
        }
    }

    /// <summary>
    /// The capture session ended: its staging is thrown away, and a swap still owed is dropped with the session - the
    /// profile it was owed to records from the next one instead. What it records therefore dates from now, not from
    /// when its file was written: until now the profile it was to take over from recorded, under the same id when
    /// that was another code of the build (audit 2026-10-03, V3-3).
    /// </summary>
    public void OnCaptureStopped()
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage = null;
        }

        if (_bound is { AwaitingSwap: true } bound)
        {
            bound.AwaitingSwap = false;
            bound.RecordsFromUtc = UtcTimestamp.Truncate(_clock.UtcNow - SharedBindResult.ClockAllowance);
            RecordBound(bound);
        }
    }

    /// <summary>One message of a session with no parser bound.</summary>
    public void Stage(DecodedMessage message)
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage?.Accept(message);
        }
    }

    /// <summary>The queue dropped observations while nothing was bound.</summary>
    public void EventsDropped(long count, DateTimeOffset atUtc, TimeSpan mono)
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage?.EventsDropped(count, atUtc, mono);
        }
    }

    /// <summary>The game connection ended while nothing was bound.</summary>
    public void ConnectionLost(DateTimeOffset atUtc, TimeSpan mono)
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage?.ConnectionLost(atUtc, mono);
        }
    }

    /// <summary>One direction of one connection was given up while nothing was bound.</summary>
    public void DirectionDamaged(string connectionKey, MessageDirection direction, DateTimeOffset atUtc, TimeSpan mono)
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage?.DirectionDamaged(connectionKey, direction, atUtc, mono);
        }
    }

    /// <summary>
    /// A run finished. True when it was the first complete duty of the shared profile in use and nothing is
    /// left to audit, so the watch ends now; a published code whose match is still being audited stays
    /// watched, and <see cref="SettleIfProven"/> ends the watch from the audit's side.
    /// </summary>
    public bool OnRunFinished(MentorRun run)
    {
        // The profile id is the build's: a run the profile this one is taking over from recorded under the same id,
        // before the swap or before the bind, is not this code's duty (audit 2026-10-03, OE-4).
        if (_bound is not { Proven: false } bound || !string.Equals(run.ProtocolProfileId, bound.ProfileId, StringComparison.Ordinal) ||
            bound.AwaitingSwap || (bound.RecordsFromUtc is { } from && run.CreatedAtUtc < from) ||
            run.EnteredAtUtc is null || run.EndedAtUtc is null ||
            run.Result is RunResult.Interrupted or RunResult.Disconnected or RunResult.CancelledBeforeEntry)
        {
            return false;
        }

        bound.RanComplete = true;
        if (bound.Verification is { AuditPending: true })
        {
            return false;
        }

        bound.Proven = true;
        RecordSettled(bound);
        if (bound.Declared is { } declared)
        {
            _host.UnregisterSharedCandidate(declared.CandidateId);
        }

        return true;
    }

    /// <summary>
    /// The profile in use was selected while the one it outranks was mid-run or queued, and has only now
    /// taken over the running session: what it records dates from here.
    /// </summary>
    /// <param name="profileId">Profile that began recording.</param>
    /// <param name="atUtc">When the parser was rebuilt over it.</param>
    public void OnBoundLater(string profileId, DateTimeOffset atUtc)
    {
        if (_bound is not { } bound || !string.Equals(bound.ProfileId, profileId, StringComparison.Ordinal))
        {
            return;
        }

        bound.BoundAtUtc ??= atUtc;
        if (bound.AwaitingSwap)
        {
            // Until now the profile it took over from recorded - under the same id when that was another code of the
            // build - so this binding's own runs begin here, not when its file was written.
            bound.AwaitingSwap = false;
            bound.RecordsFromUtc = UtcTimestamp.Truncate(atUtc - SharedBindResult.ClockAllowance);
            RecordBound(bound);
        }
    }

    /// <summary>The service is stopping: cancel the download and claim nothing more.</summary>
    public void Stop()
    {
        _stopped = true;
        CancelFetch();
    }

    /// <summary>Completes when no background work is left; for orderly shutdown and tests. Takes the gate itself.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _pending == 0 ? Task.CompletedTask : _idle.Task;
        }
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// Verifies every candidate and the profile in use against the evidence, records contradictions
    /// per healthy session, drops rejected candidates, withdraws a contradicted profile, and starts
    /// binding the best candidate that passed: one that reads the match before one that infers it.
    /// </summary>
    public void Evaluate()
    {
        if (_stopped)
        {
            return;
        }

        ReleaseDeferredWithdrawal();
        if ((_candidates.Count == 0 && _bound is not { Proven: false, Declared: not null }) ||
            _host.SharedContext() is not { } context || _host.SharedEvidence() is not { } snapshot)
        {
            return;
        }

        if (WithdrawIfContradicted(context, snapshot))
        {
            return;
        }

        if (ChooseCandidate(context, snapshot) is { } chosen && _binding is null)
        {
            StartBinding(context, snapshot, chosen);
        }
    }

    /// <summary>
    /// A revocation that arrived mid-duty takes effect now the run has ended, and the rest of the index it
    /// came with is offered as a fresh download would offer it. Runs before anything else in
    /// <see cref="Evaluate"/>, and whatever the candidate list holds, because a settled profile no longer
    /// answers any of the questions below.
    /// </summary>
    private void ReleaseDeferredWithdrawal()
    {
        if (_deferred is not { } prepared || _host.SharedRunInFlight())
        {
            return;
        }

        _deferred = null;
        Supersede("REVOKED", pending: prepared);
    }

    /// <summary>Verifies the profile in use while it is still watched. True when that withdrew it.</summary>
    private bool WithdrawIfContradicted(SharedContext context, CalibrationSnapshot snapshot)
    {
        if (_bound is not { Proven: false, Declared: { } inUse } retained)
        {
            return false;
        }

        retained.Verification = SharedCandidateVerifier.Verify(snapshot, context.Template, inUse, retained.WatchGate);
        if (!RecordContradictions(context, snapshot, inUse, retained.Verification) &&
            retained.Verification.Verdict != SharedVerdict.Contradicted)
        {
            SettleIfProven(retained);
            return false;
        }

        Supersede("CONTRADICTED");
        return true;
    }

    /// <summary>
    /// The watch on the profile in use ends once it has recorded a complete duty and no audited criterion is
    /// still waiting (plan §18.4). The run finishing may come first (a published code whose match has not
    /// yet been seen to behave) or the audit may (a duty entered but not yet exited); this is the second half
    /// of either order.
    /// </summary>
    private void SettleIfProven(BoundProfile bound)
    {
        if (bound.Proven || !bound.RanComplete || bound.Verification is { AuditPending: true })
        {
            return;
        }

        bound.Proven = true;
        RecordSettled(bound);
        if (bound.Declared is { } declared)
        {
            _host.UnregisterSharedCandidate(declared.CandidateId);
        }

        _host.SharedRetentionFinished(bound.ProfileId, bound.MatchSource == CalibrationMatchSource.QueueRequest);
    }

    /// <summary>
    /// Verifies every candidate; of those that may bind now, one reading the match before one inferring it, and within
    /// each kind the one the consensus prefers (plan §20.5), then the one more accounts submitted, then one with this
    /// machine's own shape prior, then one the index gives the shape prior, then the download order. Several pass together
    /// whenever their gates are met by the same evidence - on the login burst, or when the match and the duty entry of
    /// codes that had to wait for them arrive together.
    ///
    /// Two passes (review 2026-10-15). Whether a code is contradicted does not depend on its gate, but its gate depends on
    /// the codes still standing: judged while contradicted codes drop out one by one, a code could pass under the
    /// published gate thanks to a supporter dropped later in the same round. So every contradiction of the round first,
    /// then every gate and verdict over the list that is left.
    /// </summary>
    private Candidate? ChooseCandidate(SharedContext context, CalibrationSnapshot snapshot)
    {
        foreach (var candidate in _candidates.ToArray())
        {
            DropIfContradicted(context, snapshot, candidate);
        }

        var passed = _candidates.ToArray().Where(candidate => VerifyCandidate(context, snapshot, candidate)).ToArray();
        if (passed.Length == 0)
        {
            return null;
        }

        var consensus = Consensus();
        return passed
            .Select((candidate, order) => (Candidate: candidate, Order: order))
            .OrderBy(item => item.Candidate.Prepared.Payload.MatchSource == CalibrationMatchSource.QueueRequest)
            .ThenByDescending(item => ConsensusOf(consensus, item.Candidate).Preferred)
            .ThenByDescending(item => item.Candidate.Submitters)
            .ThenByDescending(item => item.Candidate.LocalPrior == true)
            .ThenByDescending(item => item.Candidate.ShapePrior == true)
            .ThenBy(item => item.Order)
            .First()
            .Candidate;
    }

    /// <summary>
    /// What the candidates being verified say about one another, field by field (plan §20.5). Judged afresh on every use
    /// rather than kept: it depends on the list and on what the latest index said about each code, both of which change
    /// under the gate in several places, and at most eight codes make it cheap.
    /// </summary>
    private IReadOnlyDictionary<string, SharedConsensus> Consensus() =>
        SharedCandidateConsensus.Judge(_candidates.Select(candidate =>
            new SharedConsensusCandidate(candidate.Sha, candidate.Prepared.Payload, candidate.Submitters, candidate.LocalPrior)));

    /// <summary>
    /// What the consensus says about one candidate. Fails closed (review 2026-10-15): a candidate the judgement does not
    /// cover - which only an inconsistency could produce - is not preferred, so it never earns the published gate by default.
    /// </summary>
    private static SharedConsensus ConsensusOf(IReadOnlyDictionary<string, SharedConsensus> consensus, Candidate candidate) =>
        consensus.GetValueOrDefault(candidate.Sha) ?? Unjudged;

    /// <summary>
    /// The first pass of <see cref="ChooseCandidate"/>: records the healthy sessions that contradict a candidate and drops
    /// it once that makes it rejected - the candidate being written included, whose commit then finds it gone. The gate
    /// only decides what must pass, never what contradicts, so the one it is judged under here changes nothing but what
    /// the rejected summary shows.
    /// </summary>
    private void DropIfContradicted(SharedContext context, CalibrationSnapshot snapshot, Candidate candidate)
    {
        var verification = SharedCandidateVerifier.Verify(snapshot, context.Template, candidate.Prepared.Declared, GateFor(context, candidate));
        if (RecordContradictions(context, snapshot, candidate.Prepared.Declared, verification) ||
            verification.Verdict == SharedVerdict.Contradicted)
        {
            candidate.Verification = verification;
            Drop(candidate, rejected: true);
        }
    }

    /// <summary>
    /// The second pass of <see cref="ChooseCandidate"/>, over candidates nothing contradicts: judges one under the gate
    /// that holds for the list as it now stands and updates its status. True when it passed, may replace what is in
    /// force, and has what it needs to bind in this capture session.
    /// </summary>
    private bool VerifyCandidate(SharedContext context, CalibrationSnapshot snapshot, Candidate candidate)
    {
        // The candidate being written stays under the gate it was chosen under, and its bind commits under it - audits
        // still pending or meanwhile passed, as the evidence now says - whatever an index read meanwhile does to the
        // consensus. Re-judged under a gate the consensus had just changed, a code chosen on the login burst would
        // commit with its audits gone, and could be proven by a staged duty without its match ever seen (review
        // 2026-10-15).
        var binding = ReferenceEquals(candidate, _binding);
        var gate = binding ? candidate.Gate : GateFor(context, candidate);
        var verification = SharedCandidateVerifier.Verify(snapshot, context.Template, candidate.Prepared.Declared, gate);
        candidate.Gate = gate;
        candidate.Verification = verification;
        if (binding)
        {
            return false;
        }

        candidate.Status = SharedCandidateStatus.Verifying;
        if (verification.Verdict != SharedVerdict.Pass || !CanReplace(context.Selection, candidate))
        {
            return false;
        }

        // Replacing what is already recording waits for the machine to be between runs: a fresh state
        // machine mid-duty would drop the run. Evaluate comes round again every couple of seconds, so the
        // swap happens as soon as the run ends, in this same session.
        if (context.Selection.IsUsable && _host.SharedRunInFlight())
        {
            return false;
        }

        if (candidate.Blocked ||
            (context.StagingSessionId is { } session &&
             (candidate.Stage is not { Overflowed: false } stage || !string.Equals(stage.CaptureSessionId, session, StringComparison.Ordinal))))
        {
            candidate.Status = SharedCandidateStatus.CannotBindThisSession;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The gate a candidate is judged by now (plan §18.3; audit 2026-10-03, ON1-1; plan §20.5). A published code binds on
    /// the login burst, with its match and duty entry audited while it records, only on patch day: nothing usable on this
    /// machine for it to displace, no other candidate submitted by more players, and the candidates being verified siding
    /// with it on every field in dispute - by more submitters, or on a tied match by the shape prior it alone has, the one
    /// this machine computed from the calibrations it verified itself and never the index's (<see cref="SharedCandidateConsensus"/>,
    /// <see cref="SharedLocalShapePrior"/>). Any other must see its match and duty entry behave first, exactly as a
    /// code no index knows must - so a code of another kind does not let one or two accounts put a code ahead of one many
    /// submitted, and a tie nothing decides is not settled by a coin toss. The repository conflict mark no longer
    /// decides this on its own: it was raised over the very codes this machine now compares field by field, and sending
    /// every code of a build back to full verification over one disputed field brought back the silent first duty that
    /// plan §18 removed. It stays in the download order. What the code is (its provenance, shown to the player) does not
    /// change; only how much it must prove.
    /// </summary>
    /// <param name="context">What is being calibrated, with the selection in force.</param>
    /// <param name="candidate">The candidate to judge.</param>
    private SharedCandidateProvenance GateFor(SharedContext context, Candidate candidate) =>
        candidate.Provenance == SharedCandidateProvenance.Published && candidate.Submitters > 0 &&
        !context.Selection.IsUsable && !_candidates.Any(other => other.Submitters > candidate.Submitters) &&
        ConsensusOf(Consensus(), candidate).Preferred
            ? SharedCandidateProvenance.Published
            : SharedCandidateProvenance.Imported;

    /// <summary>Starts writing the chosen candidate's profile, unless it infers the match and the player has not accepted that yet.</summary>
    private void StartBinding(SharedContext context, CalibrationSnapshot snapshot, Candidate chosen)
    {
        DateTimeOffset? consent = null;
        if (chosen.Prepared.Payload.MatchSource == CalibrationMatchSource.QueueRequest && (consent = ConsentAt(context.Key)) is null)
        {
            chosen.Status = SharedCandidateStatus.AwaitingConsent;
            return;
        }

        _binding = chosen;
        chosen.Status = SharedCandidateStatus.Writing;
        chosen.ChosenConsensus = ConsensusOf(Consensus(), chosen);
        var ticket = new BindTicket(context.Key, context.Template, chosen, Counts(snapshot, chosen.Prepared.Declared), consent);
        Schedule(() =>
        {
            Bind(ticket);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Records, once per candidate and capture session, every healthy session whose own evidence
    /// contradicts the candidate. True when the store now counts it as rejected.
    /// </summary>
    private bool RecordContradictions(
        SharedContext context, CalibrationSnapshot snapshot, DeclaredCandidate declared, SharedVerification verification)
    {
        if (!verification.Criteria.Any(criterion => criterion.ContradictingSessions > 0))
        {
            return false;
        }

        var rejected = false;
        foreach (var health in snapshot.SessionHealth.Values.Where(reading => reading.IsHealthy))
        {
            var key = (declared.CandidateId, health.CaptureSessionId);
            if (_contradictions.Contains(key))
            {
                continue;
            }

            // The verifier counts sessions, not which ones; judging each healthy session alone names them. The
            // gate does not enter into a contradiction, so either provenance gives the same count.
            var alone = snapshot with
            {
                SessionHealth = new Dictionary<string, CaptureSessionHealth>(StringComparer.Ordinal) { [health.CaptureSessionId] = health },
            };
            if (!SharedCandidateVerifier.Verify(alone, context.Template, declared, SharedCandidateProvenance.Imported).Criteria
                    .Any(criterion => criterion.ContradictingSessions > 0))
            {
                continue;
            }

            _contradictions.Add(key);
            var recorded = Attempt(() => _services.SharedCalibrations.RecordContradiction(
                context.Key.Region, context.Key.GameBuild, context.Key.TemplateSha256, declared.CandidateId,
                health.CaptureSessionId, _clock.UtcNow));
            rejected |= recorded?.Rejected == true;
        }

        return rejected;
    }

    private static bool CanReplace(ProfileSelection selection, Candidate candidate) =>
        CanReplace(selection, candidate.Prepared.Payload.MatchSource);

    /// <summary>
    /// Whether a code that names the match this way is worth having beside what is in force: everything is,
    /// while nothing records; otherwise only a code that reads the server's own match, and only against a
    /// profile this machine or another player produced by inferring it. A shipped profile and a local one
    /// that already reads the match are never replaced from the index.
    /// </summary>
    /// <param name="selection">What is in force.</param>
    /// <param name="match">How the code names the match.</param>
    private static bool CanReplace(ProfileSelection selection, CalibrationMatchSource match) =>
        !selection.IsUsable ||
        (selection.Profile is { MatchFromQueue: true } && selection.Origin is ProfileOrigin.Local or ProfileOrigin.Shared &&
         match != CalibrationMatchSource.QueueRequest);

    /// <summary>Structural matches per evidence key, for the written profile's provenance.</summary>
    private static IReadOnlyDictionary<string, int> Counts(CalibrationSnapshot snapshot, DeclaredCandidate declared)
    {
        int Bursts(MessageKey key) => snapshot.Clusters.Count(cluster => cluster.Members.GetValueOrDefault(key) > 0);
        static string Key(string name) => "messages." + name + ".opcode";
        var observation = snapshot.Candidates.FirstOrDefault(item =>
            string.Equals(item.CandidateId, declared.CandidateId, StringComparison.Ordinal));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Key(CalibratedShape.PopName)] = declared.Pop.Direction == PacketDirection.ClientToServer
                ? snapshot.Pairs.Count(pair => pair.RequestOpcode == declared.Pop.Opcode)
                : observation?.Sessions.Values.Sum(session => session.PopWithRequest) ?? 0,
            [Key(CalibratedShape.ZoneName)] = Bursts(declared.ZoneKey),
        };
        if (declared.TerritoryKey is { } territory)
        {
            counts[Key(CalibratedShape.TerritoryName)] = Bursts(territory);
        }

        if (declared.JobKey is { } job)
        {
            counts[Key(CalibratedShape.JobName)] = Bursts(job);
        }

        return counts;
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>The <c>shared</c> part of calibration status.</summary>
    public SharedCalibrationSnapshot Snapshot()
    {
        var summaries = new List<SharedCandidateSummary>();
        if (_bound is { Sha: not null } bound)
        {
            summaries.Add(Summary(bound));
        }

        var consensus = Consensus();
        summaries.AddRange(_candidates.Select(candidate => Summary(candidate, ConsensusOf(consensus, candidate))));
        summaries.AddRange(_rejectedSummaries);
        return new SharedCalibrationSnapshot(
            Phase(), summaries, _lastFetchStatus, _lastAttempts, _bound?.ProfileId, _bound?.BoundAtUtc, _lastRefusal)
        {
            UserRejected = UserRejectedNow(),
            AuditPending = _bound is { Proven: false, Verification.AuditPending: true },
            LastSentAtUtc = _lastSentAtUtc,
            LastSentStatus = _lastSentStatus,
            Recheck = _recheck,
        };
    }

    /// <summary>A short string that changes whenever the snapshot does in a way the card shows.</summary>
    public string Signature()
    {
        var consensus = Consensus();
        return string.Join(",",
            Phase(), _lastFetchStatus, _bound?.ProfileId, _bound?.Sha, _bound?.Provenance, _bound?.Proven,
            _bound?.Verification?.AuditPending, _rejectedSummaries.Count, UserRejectedNow(),
            string.Join(";", _candidates.Select(candidate =>
                candidate.Sha[..12] + ":" + candidate.Provenance + ":" + candidate.Status + ":" + candidate.Verification?.Verdict + ":" +
                ConsensusOf(consensus, candidate).Preferred + ":" + ConsensusOf(consensus, candidate).Disputed.Count)));
    }

    private SharedCalibrationPhase Phase() =>
        _bound is not null ? SharedCalibrationPhase.Verified
        : _candidates.Any(candidate => candidate.Status == SharedCandidateStatus.AwaitingConsent) ? SharedCalibrationPhase.AwaitingConsent
        : _candidates.Count > 0 ? SharedCalibrationPhase.Verifying
        : _fetchVisible ? SharedCalibrationPhase.Fetching
        : _superseded || _rejectedSummaries.Count > 0 || UserRejectedNow() ? SharedCalibrationPhase.Rejected
        : _lastFetchStatus is SharedFetchStatus.IndexUnavailable or SharedFetchStatus.CodesUnavailable ? SharedCalibrationPhase.Unavailable
        : SharedCalibrationPhase.None;

    private static SharedCandidateSummary Summary(Candidate candidate, SharedConsensus consensus) => new(
        candidate.Sha[..12], candidate.Source, candidate.Prepared.Payload.MatchSource, candidate.Status,
        candidate.Verification?.Verdict ?? SharedVerdict.Wait,
        candidate.Verification?.Criteria ?? Array.Empty<SharedCriterion>(),
        candidate.Stage?.Overflowed == true)
    {
        Provenance = candidate.Provenance,
        AuditPending = candidate.Verification?.AuditPending == true,
        Disputed = consensus.Disputed,
        Preferred = consensus.Preferred,
        LocalShapePrior = candidate.LocalPrior,
    };

    private static SharedCandidateSummary Summary(BoundProfile bound) => new(
        bound.Sha![..12], bound.Source, bound.MatchSource,
        bound.Proven ? SharedCandidateStatus.Proven : SharedCandidateStatus.InUse,
        bound.Verification?.Verdict ?? SharedVerdict.Pass,
        bound.Verification?.Criteria ?? Array.Empty<SharedCriterion>(),
        false)
    {
        Provenance = bound.Provenance,
        AuditPending = !bound.Proven && bound.Verification?.AuditPending == true,
        Disputed = bound.Consensus.Disputed,
        Preferred = bound.Consensus.Preferred,
        LocalShapePrior = bound.LocalShapePrior,
    };

    /// <summary>
    /// What sharing a proven shared profile back rests on (plan §20.5): the shared profile in force is the one this session
    /// bound or adopted, and it is proven - a complete duty recorded with no audited criterion left waiting (plan §18.4).
    /// Null otherwise. A question only, asked with the gate held; reading the code text is left to the caller, off the gate.
    /// </summary>
    /// <param name="selection">The selection in force, read under the same gate.</param>
    public SharedShareProof? ShareProof(ProfileSelection selection) =>
        _bound is { Proven: true } bound &&
        selection is { IsUsable: true, Origin: ProfileOrigin.Shared, Profile: { } profile } &&
        string.Equals(bound.ProfileId, profile.ProfileId, StringComparison.Ordinal) &&
        (bound.ProfileSha256 is null || string.Equals(bound.ProfileSha256, profile.ProfileSha256, StringComparison.Ordinal))
            ? new SharedShareProof(profile.ProfileSha256, bound.Sha)
            : null;

    /// <summary>
    /// A code rebuilt through the template, with what the index said about it that still matters once the list is in the
    /// index's order - its submitters and its shape prior (0 and null when nothing) - and the shape prior this machine
    /// computed itself from the calibrations it verified (<see cref="SharedLocalShapePrior"/>; null when nothing compares).
    /// </summary>
    private sealed record Prepared(
        string Sha, ShareCodePayload Payload, DeclaredCandidate Declared, ProtocolProfile StagingProfile, int Submitters = 0,
        bool? ShapePrior = null, bool? LocalPrior = null);

    private sealed class FetchTicket
    {
        public FetchTicket(SharedKey key, CalibrationTemplate template, bool manual)
        {
            Key = key;
            Template = template;
            Manual = manual;
        }

        public SharedKey Key { get; }

        public CalibrationTemplate Template { get; }

        public bool Manual { get; }

        /// <summary>Reads the store only and sends nothing: the offer of stored codes after a withdrawal (review 2026-10-15, L4).</summary>
        public bool DiskOnly { get; init; }

        /// <summary>Why it went out beside a profile already recording, or null when nothing was in force.</summary>
        public SharedRecheckReason? Recheck { get; init; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record BindTicket(
        SharedKey Key, CalibrationTemplate Template, Candidate Candidate, IReadOnlyDictionary<string, int> Counts, DateTimeOffset? ConsentAt);

    private sealed class Candidate
    {
        public Candidate(Prepared prepared, SharedCandidateSource source, SharedCandidateProvenance provenance)
        {
            Prepared = prepared;
            Source = source;
            Provenance = provenance;
            Submitters = prepared.Submitters;
            ShapePrior = prepared.ShapePrior;
            LocalPrior = prepared.LocalPrior;
        }

        public Prepared Prepared { get; }

        public SharedCandidateSource Source { get; }

        /// <summary>Published or imported; an imported code is promoted once an index this machine reads lists it.</summary>
        public SharedCandidateProvenance Provenance { get; set; }

        /// <summary>Distinct submitters the latest index this machine read gives it; 0 when no index said.</summary>
        public int Submitters { get; set; }

        /// <summary>
        /// The latest index this machine read gives it the shape prior (plan §20.3); null when it said nothing. Orders only:
        /// anyone who can publish can plant it, so it never decides a gate (security review 2026-10-15).
        /// </summary>
        public bool? ShapePrior { get; set; }

        /// <summary>
        /// The shape prior this machine computed from the calibrations it verified itself (<see cref="SharedLocalShapePrior"/>):
        /// what breaks a tied match in the consensus. Null when nothing compares.
        /// </summary>
        public bool? LocalPrior { get; set; }

        public string Sha => Prepared.Sha;

        public SharedCandidateStage? Stage { get; set; }

        public SharedVerification? Verification { get; set; }

        /// <summary>
        /// The gate <see cref="Verification"/> was judged under; while the candidate is being written, the one it was
        /// chosen under, kept until the commit.
        /// </summary>
        public SharedCandidateProvenance Gate { get; set; } = SharedCandidateProvenance.Imported;

        /// <summary>
        /// What the consensus said about it when it was chosen to bind, kept with <see cref="Gate"/> so the profile in use
        /// reports the consensus it was bound under, not one an index read during the write produced (review 2026-10-15).
        /// </summary>
        public SharedConsensus? ChosenConsensus { get; set; }

        public SharedCandidateStatus Status { get; set; }

        /// <summary>A bind of it was refused in this capture session; it waits for the next.</summary>
        public bool Blocked { get; set; }
    }

    private sealed class BoundProfile
    {
        public BoundProfile(Region region, string gameBuild, string profileId)
        {
            Region = region;
            GameBuild = gameBuild;
            ProfileId = profileId;
        }

        public Region Region { get; }

        public string GameBuild { get; }

        public string ProfileId { get; }

        /// <summary>Canonical hash of the profile document in use; what the store's settled mark is keyed by.</summary>
        public string? ProfileSha256 { get; init; }

        public string? Sha { get; set; }

        public DeclaredCandidate? Declared { get; set; }

        public SharedCandidateSource? Source { get; init; }

        /// <summary>
        /// Gate set the watch judges by. A profile adopted from disk is watched as published: what it records is
        /// already recording, and an imported code that bound had every criterion pass before it did.
        /// </summary>
        public SharedCandidateProvenance WatchGate { get; set; } = SharedCandidateProvenance.Published;

        /// <summary>
        /// What the card is told about it, kept apart from <see cref="WatchGate"/> (review 2026-10-15): published only while
        /// this machine keeps the published code text - what 这份校准好用，告诉其他玩家 hands back - and imported otherwise,
        /// so the card never offers a share that can only fail. Imported until the store has been asked.
        /// </summary>
        public SharedCandidateProvenance Provenance { get; set; } = SharedCandidateProvenance.Imported;

        public CalibrationMatchSource? MatchSource { get; set; }

        /// <summary>When it began recording in this process; null while it is selected but not yet recording.</summary>
        public DateTimeOffset? BoundAtUtc { get; set; }

        /// <summary>
        /// Earliest creation time a run this binding recorded can have - in this process or as kept with the document
        /// (audit 2026-10-03, OE-2/OE-4). Null only for a document adopted from disk with nothing kept: then every run
        /// under the id is taken for its own, as before.
        /// </summary>
        public DateTimeOffset? RecordsFromUtc { get; set; }

        /// <summary>Selected while another profile records a run; until it takes over, nothing recorded is its own.</summary>
        public bool AwaitingSwap { get; set; }

        public SharedVerification? Verification { get; set; }

        /// <summary>A complete duty was recorded under it; with no audit pending that makes it proven.</summary>
        public bool RanComplete { get; set; }

        public bool Proven { get; set; }

        /// <summary>What the candidates said about its code when it was bound; nothing disputed for one adopted from disk.</summary>
        public SharedConsensus Consensus { get; init; } = SharedConsensus.Undisputed;

        /// <summary>This machine's own shape prior for its code as the candidate had it when bound here; null for one adopted from disk.</summary>
        public bool? LocalShapePrior { get; init; }

        /// <summary>A profile adopted from disk and proven has been described for the card once; never retried.</summary>
        public bool Described { get; set; }
    }
}
