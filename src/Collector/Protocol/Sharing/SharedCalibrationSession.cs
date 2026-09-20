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

    private static readonly IReadOnlyDictionary<string, int> NoCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    private readonly object _gate;
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
    private DateTimeOffset? _nextAutoFetchAtUtc;
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
            : _host.HasFinishedSharedRun(profile.ProfileId) && IsSettled(profile);
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
            _nextAutoFetchAtUtc = null;
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
        _nextAutoFetchAtUtc = null;
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

    /// <summary>The capture session ended: its staging is thrown away.</summary>
    public void OnCaptureStopped()
    {
        foreach (var candidate in _candidates)
        {
            candidate.Stage = null;
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

    /// <summary>
    /// A run finished. True when it was the first complete duty of the shared profile in use and nothing is
    /// left to audit, so the watch ends now; a published code whose match is still being audited stays
    /// watched, and <see cref="SettleIfProven"/> ends the watch from the audit's side.
    /// </summary>
    public bool OnRunFinished(MentorRun run)
    {
        if (_bound is not { Proven: false } bound || !string.Equals(run.ProtocolProfileId, bound.ProfileId, StringComparison.Ordinal) ||
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
        if (_bound is { BoundAtUtc: null } bound && string.Equals(bound.ProfileId, profileId, StringComparison.Ordinal))
        {
            bound.BoundAtUtc = atUtc;
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

        retained.Verification = SharedCandidateVerifier.Verify(snapshot, context.Template, inUse, retained.Provenance);
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

    /// <summary>Verifies every candidate; the first that may bind now, one reading the match before one inferring it.</summary>
    private Candidate? ChooseCandidate(SharedContext context, CalibrationSnapshot snapshot)
    {
        Candidate? announcement = null;
        Candidate? queue = null;
        foreach (var candidate in _candidates.ToArray())
        {
            if (!VerifyCandidate(context, snapshot, candidate))
            {
                continue;
            }

            if (candidate.Prepared.Payload.MatchSource == CalibrationMatchSource.QueueRequest)
            {
                queue ??= candidate;
            }
            else
            {
                announcement ??= candidate;
            }
        }

        return announcement ?? queue;
    }

    /// <summary>
    /// Verifies one candidate and updates its status; a contradicted one is dropped. True when it passed, may
    /// replace what is in force, and has what it needs to bind in this capture session.
    /// </summary>
    private bool VerifyCandidate(SharedContext context, CalibrationSnapshot snapshot, Candidate candidate)
    {
        var verification = SharedCandidateVerifier.Verify(snapshot, context.Template, candidate.Prepared.Declared, candidate.Provenance);
        candidate.Verification = verification;
        if (RecordContradictions(context, snapshot, candidate.Prepared.Declared, verification) ||
            verification.Verdict == SharedVerdict.Contradicted)
        {
            Drop(candidate, rejected: true);
            return false;
        }

        if (ReferenceEquals(candidate, _binding))
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

        summaries.AddRange(_candidates.Select(Summary));
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
    public string Signature() => string.Join(",",
        Phase(), _lastFetchStatus, _bound?.ProfileId, _bound?.Proven, _bound?.Verification?.AuditPending, _rejectedSummaries.Count, UserRejectedNow(),
        string.Join(";", _candidates.Select(candidate =>
            candidate.Sha[..12] + ":" + candidate.Provenance + ":" + candidate.Status + ":" + candidate.Verification?.Verdict)));

    private SharedCalibrationPhase Phase() =>
        _bound is not null ? SharedCalibrationPhase.Verified
        : _candidates.Any(candidate => candidate.Status == SharedCandidateStatus.AwaitingConsent) ? SharedCalibrationPhase.AwaitingConsent
        : _candidates.Count > 0 ? SharedCalibrationPhase.Verifying
        : _fetchVisible ? SharedCalibrationPhase.Fetching
        : _superseded || _rejectedSummaries.Count > 0 || UserRejectedNow() ? SharedCalibrationPhase.Rejected
        : _lastFetchStatus is SharedFetchStatus.IndexUnavailable or SharedFetchStatus.CodesUnavailable ? SharedCalibrationPhase.Unavailable
        : SharedCalibrationPhase.None;

    private static SharedCandidateSummary Summary(Candidate candidate) => new(
        candidate.Sha[..12], candidate.Source, candidate.Prepared.Payload.MatchSource, candidate.Status,
        candidate.Verification?.Verdict ?? SharedVerdict.Wait,
        candidate.Verification?.Criteria ?? Array.Empty<SharedCriterion>(),
        candidate.Stage?.Overflowed == true)
    {
        Provenance = candidate.Provenance,
        AuditPending = candidate.Verification?.AuditPending == true,
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
    };

    private sealed record Prepared(string Sha, ShareCodePayload Payload, DeclaredCandidate Declared, ProtocolProfile StagingProfile);

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
        }

        public Prepared Prepared { get; }

        public SharedCandidateSource Source { get; }

        /// <summary>Published or imported; an imported code is promoted once an index this machine reads lists it.</summary>
        public SharedCandidateProvenance Provenance { get; set; }

        public string Sha => Prepared.Sha;

        public SharedCandidateStage? Stage { get; set; }

        public SharedVerification? Verification { get; set; }

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
        public SharedCandidateProvenance Provenance { get; set; } = SharedCandidateProvenance.Published;

        public CalibrationMatchSource? MatchSource { get; set; }

        /// <summary>When it began recording in this process; null while it is selected but not yet recording.</summary>
        public DateTimeOffset? BoundAtUtc { get; set; }

        public SharedVerification? Verification { get; set; }

        /// <summary>A complete duty was recorded under it; with no audit pending that makes it proven.</summary>
        public bool RanComplete { get; set; }

        public bool Proven { get; set; }
    }
}
