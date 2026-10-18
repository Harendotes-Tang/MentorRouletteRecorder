using System.Globalization;
using System.Text;
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

// SharedCalibrationSession, continued: binding a verified candidate, withdrawing a profile in use, and the candidate bookkeeping both share.
internal sealed partial class SharedCalibrationSession
{
    // ------------------------------------------------------------------ binding and withdrawal

    /// <summary>Off the gate: build, write and reload; then commit under it.</summary>
    private void Bind(BindTicket ticket)
    {
        string? profileId = null;
        string? writtenSha = null;
        string? refusal = null;
        Func<Capture.GameProcessDetection, ProfileSelection>? select = null;

        // The file lock is held through the commit and not only through the write: a withdrawal scheduled before
        // this bind deletes the very path this one writes, and only an unbroken write-reload-commit lets Withdraw
        // ask whether the document on that path is still the one it is withdrawing and get a true answer
        // (review finding 12).
        lock (_file)
        {
            try
            {
                var built = SharedProfileBuilder.Build(
                    ticket.Candidate.Prepared.Payload, ticket.Template, _clock.UtcNow, ticket.Counts, ticket.ConsentAt);
                if (built is { Status: SharedProfileBuildStatus.Built, ProfileId: { } id })
                {
                    _services.WriteSharedProfile(built);
                    profileId = id;
                    writtenSha = built.ProfileSha256;
                    select = _services.ReloadSelect();
                }
                else
                {
                    refusal = "BUILD_" + built.Status.ToString().ToUpperInvariant();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Whatever failed, the commit below runs: it is what gives the binding claim back, and a candidate
                // left claimed is never chosen again in this arm (audit 2026-10-03, unclosed suspicion under OE).
                refusal = "WRITE_FAILED:" + ex.GetType().Name;
            }

            lock (_gate)
            {
                Commit(ticket, profileId, select, refusal, writtenSha);
            }
        }
    }

    /// <summary>Under the gate: the written profile becomes the one in use, unless something changed while it was written.</summary>
    private void Commit(
        BindTicket ticket, string? profileId, Func<Capture.GameProcessDetection, ProfileSelection>? select, string? refusal, string? writtenSha)
    {
        var candidate = ticket.Candidate;
        if (ReferenceEquals(_binding, candidate))
        {
            _binding = null;
        }

        if (_stopped || _key != ticket.Key || !_candidates.Contains(candidate))
        {
            // Disarmed, rebuilt, dropped or refused by the player meanwhile. A revoked code's
            // unfinished write must not leave a selectable document after its candidate was dropped.
            _lastRefusal = "STALE";
            if (_revoked.Contains(candidate.Sha) || BindingRestrictionChanged(ticket))
                DiscardUnadoptedWrite(ticket, writtenSha);
        }
        else if (refusal is not null || profileId is null || select is null)
        {
            if (BindingRestrictionChanged(ticket) && !BindingRestrictionStillPasses(ticket))
                DiscardUnadoptedWrite(ticket, writtenSha);
            Block(candidate, refusal ?? "WRITE_FAILED");
        }
        else if (!BindingRestrictionStillPasses(ticket))
        {
            // The writer still owns _file. Never publish its stale selector, and only remove the precise
            // document this ticket wrote: another document at that path must survive cancellation.
            DiscardUnadoptedWrite(ticket, writtenSha);
            candidate.Status = SharedCandidateStatus.Verifying;
            candidate.Blocked = false;
            _lastRefusal = "RECHECK_REQUIRED";
        }
        else
        {
            CommitWritten(ticket, profileId, select);
        }

        _host.SharedCalibrationChanged();
    }

    private static bool BindingRestrictionChanged(BindTicket ticket) => ticket.Candidate.Quarantined ||
        ticket.Candidate.QuarantineEpoch != ticket.QuarantineEpoch || ticket.Candidate.Gate != ticket.Gate;

    private void DiscardUnadoptedWrite(BindTicket ticket, string? writtenSha)
    {
        if (writtenSha is not null)
            Attempt(() => _services.DeleteSharedProfileIfMatches(ticket.Key.Region, ticket.Key.GameBuild, writtenSha));
    }

    private bool BindingRestrictionStillPasses(BindTicket ticket)
    {
        var candidate = ticket.Candidate;
        // Consensus may keep the original audit contract, but a new quarantine/epoch is a safety
        // restriction. It may only commit after fresh full verification, including during a slow write.
        if (!BindingRestrictionChanged(ticket)) return true;
        candidate.Gate = SharedCandidateProvenance.Imported;
        if (_host.SharedContext() is not { } context || context.Key != ticket.Key || _host.SharedEvidence() is not { } snapshot)
            return false;
        candidate.Verification = SharedCandidateVerifier.Verify(snapshot, context.Template, candidate.Prepared.Declared,
            SharedCandidateProvenance.Imported);
        return candidate.Verification.Verdict == SharedVerdict.Pass;
    }

    /// <summary>Hands the written profile to the pipeline; adopts it when selected, blocks the candidate for this session when not.</summary>
    private void CommitWritten(BindTicket ticket, string profileId, Func<Capture.GameProcessDetection, ProfileSelection> select)
    {
        var candidate = ticket.Candidate;
        // Consensus alone retains the chosen audit contract. A tighter quarantine has already been fully verified
        // by BindingRestrictionStillPasses before this document can reach the host.
        var verification = candidate.Verification;
        var auditPending = verification?.AuditPending == true;
        var result = _host.CommitSharedBind(new SharedBindRequest(profileId, select, candidate.Stage, auditPending));
        if (result.Outcome is SharedBindOutcome.NotSelected or SharedBindOutcome.SessionChanged)
        {
            // Delete nothing and leave calibration armed; the candidate may try again next session.
            Block(candidate, result.Reason);
            return;
        }

        // What the others said about it when it was chosen, kept with the profile in use for the card and the report: the
        // consensus it was bound under, and once the others are dropped below there is nobody left to ask anyway.
        var consensus = candidate.ChosenConsensus ?? ConsensusOf(Consensus(), candidate);
        foreach (var other in _candidates.Where(item => !ReferenceEquals(item, candidate)).ToArray())
        {
            Drop(other, rejected: false);
        }

        _candidates.Remove(candidate);
        ForgetBound();
        _bound = new BoundProfile(ticket.Key.Region, ticket.Key.GameBuild, profileId)
        {
            ProfileSha256 = _host.SharedSelection().Profile is { } written && string.Equals(written.ProfileId, profileId, StringComparison.Ordinal)
                ? written.ProfileSha256
                : null,
            Sha = candidate.Sha,
            Declared = candidate.Prepared.Declared,
            Source = candidate.Source,
            WatchGate = candidate.Provenance,
            Provenance = DisplayedProvenance(ticket.Key.Region, ticket.Key.GameBuild, candidate.Sha, candidate.Provenance),
            MatchSource = candidate.Prepared.Payload.MatchSource,
            BoundAtUtc = result.Outcome == SharedBindOutcome.Bound ? _clock.UtcNow : null,
            RecordsFromUtc = result.RecordsFromUtc,
            AwaitingSwap = result.SwapOwed,
            Verification = verification,
            RanComplete = result.RanComplete,
            Proven = result.Proven,
            Consensus = consensus,
            LocalShapePrior = candidate.LocalPrior,
        };
        RecordBound(_bound);

        // Unless the drained staging already finished a duty with nothing left to audit, it stays registered
        // with the observer and keeps being verified until it records one and the audit settles.
        if (result.Proven)
        {
            RecordSettled(_bound);
            _host.UnregisterSharedCandidate(candidate.Sha);
        }

        _superseded = false;
        _lastRefusal = null;
        ObserveSelectedEvidence();
    }

    private void Block(Candidate candidate, string reason)
    {
        candidate.Status = SharedCandidateStatus.CannotBindThisSession;
        candidate.Blocked = true;
        _lastRefusal = reason;
    }

    /// <summary>
    /// The profile in use is contradicted, revoked or refused by the player: stop recording with it now and
    /// remove it off the gate. Only a contradiction or a revocation marks its code as rejected; the player's
    /// refusal is about the build, not the code, and the store keeps it apart.
    /// </summary>
    /// <param name="reason">Refusal token; <c>CONTRADICTED</c>, <c>REVOKED</c> or the player's own.</param>
    /// <param name="contradicted">False for the player's refusal: no accusation, no flagged records.</param>
    /// <param name="pending">
    /// Codes of the index that revoked it, offered once the file is gone - a replacement is written to that
    /// same path, so it must not be written while the delete is still in flight.
    /// </param>
    private void Supersede(string reason, bool contradicted = true, IReadOnlyList<Prepared>? pending = null)
    {
        if (_bound is not { } bound)
        {
            RegisterDownloaded(pending ?? Array.Empty<Prepared>());
            return;
        }

        _bound = null;
        _superseded = true;
        _lastRefusal = reason;
        _withdrawn.Add(bound.ProfileId);
        if (contradicted && bound.Sha is { } sha)
        {
            _rejected.Add(sha);
            _rejectedSummaries.Add(Summary(bound) with { Status = SharedCandidateStatus.Rejected });
        }

        if (bound.Declared is { } declared)
        {
            _host.UnregisterSharedCandidate(declared.CandidateId);
        }

        _host.UnbindSharedProfile(bound.ProfileId);
        ObserveSelectedEvidence();
        if (bound.Sha is { } evidenceCode) ReleaseEvidenceCandidate(evidenceCode);
        if (contradicted && !bound.AwaitingSwap)
        {
            // What it recorded before the traffic caught it out is suspect (plan §18.4) - and only that: the profile id
            // is the build's, not the code's, so the marking starts where this binding began recording (audit
            // 2026-10-03, OE-2). Not for the player's own refusal: those records were made by a code nothing
            // contradicted. Nor for a code that never took over from the profile recording when it was written.
            _host.FlagSharedRecords(bound.ProfileId, bound.RecordsFromUtc, reason);
        }

        Schedule(() =>
        {
            Withdraw(bound, pending);
            return Task.CompletedTask;
        });
        _host.SharedCalibrationChanged();
    }

    private void Withdraw(BoundProfile bound, IReadOnlyList<Prepared>? pending = null)
    {
        // Under the same file lock the bind takes, so that "is this still the document I withdrew?" is asked and
        // answered with no write able to slip in between: a compare-and-delete on the file's own bytes would
        // leave exactly that window open, and a mutex on its own would only decide which of the two goes second -
        // whichever order it chose, a delete that ran last would still take the other profile away.
        lock (_file)
        {
            if (!Replaced(bound))
            {
                Attempt(() =>
                {
                    _services.DeleteSharedProfile(bound.Region, bound.GameBuild);
                    return true;
                });
            }

            var select = Attempt(() => _services.ReloadSelect());
            lock (_gate)
            {
                if (_stopped || select is null)
                {
                    return;
                }

                _host.ReselectAfterSharedChange(select);
                Sync();
                if (pending is not { Count: > 0 })
                {
                    // The other codes the store keeps for the build were dropped when this one bound, and no index read just
                    // now brings them along: they are offered again off the disk, with nothing sent (plan §20.5: its rival is
                    // verified afresh; docs/privacy-boundary.md §8.2). The rejection or revocation record keeps this one out.
                    // An empty list - a revocation found on adoption or held for the end of a run - needs this as much as no
                    // list at all.
                    ReofferStored();
                }
                else
                {
                    // The file is gone and the catalogue has been read again, so the next best code of the
                    // index that revoked this one may now be offered - and written to that same path.
                    RegisterDownloaded(pending);
                    Evaluate();
                }

                _host.SharedCalibrationChanged();
            }
        }
    }

    /// <summary>
    /// True when a bind that finished while this withdrawal waited has put a different document on the same path
    /// and the state machine is already recording with it. One file per region and build means the withdrawal
    /// would otherwise delete a profile verified and adopted a moment earlier with nothing in memory the wiser,
    /// and the next catalogue scan - a restart included - would find the build uncalibrated again for no reason
    /// (review finding 12). A document this session cannot name, a profile bound before its hash was known, is
    /// still removed: leaving a contradicted calibration recording is the worse of the two.
    /// </summary>
    /// <param name="withdrawn">The profile taken out of use.</param>
    private bool Replaced(BoundProfile withdrawn)
    {
        lock (_gate)
        {
            return IsReplacedBy(_bound?.ProfileSha256, withdrawn.ProfileSha256);
        }
    }

    /// <summary>
    /// The decision behind <see cref="Replaced"/>, free of the session's state so it can be pinned down on its own.
    /// Only a hash that is known on both sides and differs proves another document now stands on the path; an
    /// unknown on either side must not pass for "replaced", or a contradicted profile would go on recording.
    /// </summary>
    /// <param name="current">Hash of the profile bound now; null when none is bound or its hash is not known.</param>
    /// <param name="withdrawn">Hash of the profile being withdrawn; null when it was bound before its hash was known.</param>
    internal static bool IsReplacedBy(string? current, string? withdrawn) =>
        current is not null && withdrawn is not null && !string.Equals(current, withdrawn, StringComparison.Ordinal);

    // ------------------------------------------------------------------ bookkeeping

    /// <summary>
    /// Registers what a download offered. With nothing in force that is all of it; beside a profile that
    /// records, only a code that outranks it (plan §3), so a build already answered by an announcement code
    /// spends no observer slot and shows the player no candidate they have no use for.
    /// </summary>
    /// <param name="prepared">Codes rebuilt from the index just read, best first.</param>
    private void RegisterDownloaded(IReadOnlyList<Prepared> prepared)
    {
        var selection = _host.SharedSelection();
        foreach (var candidate in prepared.Where(item => CanReplace(selection, item.Payload.MatchSource)))
        {
            Register(candidate, SharedCandidateSource.Downloaded, SharedCandidateProvenance.Published);
        }
    }

    private string? Register(Prepared prepared, SharedCandidateSource source, SharedCandidateProvenance provenance)
    {
        // Downloaded cache entries, deferred offers and manual imports share this gate. A failed
        // disk write or a later re-arm must never make an already-read revocation disappear.
        if (_revoked.Contains(prepared.Sha))
        {
            return "REVOKED";
        }

        if (string.Equals(_bound?.Sha, prepared.Sha, StringComparison.Ordinal))
        {
            RegisterEvidenceCandidate(prepared.Declared, prepared.QuarantineEpoch);
            return null;
        }

        if (_candidates.FirstOrDefault(candidate => candidate.Sha == prepared.Sha) is { } known)
        {
            // One code, one candidate. A download of a code the player had pasted proves it published (plan §18.5), and
            // what the index now says about it - submitters and the shape prior - replaces what was known before; the
            // consensus reads it from there on its next use. The conflict mark has done its work in the download order.
            // The local prior is this machine's own and was read again with this code; it changes only when the machine
            // has since verified a calibration for another build.
            known.LocalPrior = prepared.LocalPrior;
            if (prepared.Quarantined || known.QuarantineEpoch != prepared.QuarantineEpoch)
                known.Gate = SharedCandidateProvenance.Imported;
            known.Quarantined = prepared.Quarantined;
            known.QuarantineEpoch = prepared.QuarantineEpoch;
            RegisterEvidenceCandidate(prepared.Declared, prepared.QuarantineEpoch);
            if (provenance == SharedCandidateProvenance.Published)
            {
                known.Provenance = SharedCandidateProvenance.Published;
                if (prepared.Submitters > 0)
                {
                    known.Submitters = prepared.Submitters;
                    known.ShapePrior = prepared.ShapePrior;
                }
            }

            return null;
        }

        if (_key is { } key && IsUserRejected(key.Region, key.GameBuild))
        {
            return UserRejectedReason;
        }

        if (_rejected.Contains(prepared.Sha))
        {
            return "REJECTED";
        }

        if (_candidates.Count + (_bound?.Declared is null ? 0 : 1) >= CalibrationObserver.MaxCandidates)
        {
            return "TOO_MANY_CANDIDATES";
        }

        _host.RegisterSharedCandidate(prepared.Declared);
        RegisterEvidenceCandidate(prepared.Declared, prepared.QuarantineEpoch);
        var candidate = new Candidate(prepared, source, provenance);
        if (_host.SharedContext()?.StagingSessionId is { } session)
        {
            candidate.Stage = new SharedCandidateStage(session, prepared.StagingProfile);
        }

        _candidates.Add(candidate);
        return null;
    }

    private void Drop(Candidate candidate, bool rejected)
    {
        // Judged while it still stands among the others, so a rejected code's summary says what it was disputed on.
        var consensus = rejected ? ConsensusOf(Consensus(), candidate) : SharedConsensus.Undisputed;
        _candidates.Remove(candidate);
        candidate.Stage = null;
        _host.UnregisterSharedCandidate(candidate.Sha);
        ReleaseEvidenceCandidate(candidate.Sha);
        if (ReferenceEquals(_binding, candidate))
        {
            _binding = null;
        }

        if (rejected && _rejected.Add(candidate.Sha) && _rejectedSummaries.Count < CalibrationObserver.MaxCandidates)
        {
            _rejectedSummaries.Add(Summary(candidate, consensus) with { Status = SharedCandidateStatus.Rejected });
        }
    }

    /// <summary>
    /// Makes the bound profile describe the shared profile the selection holds, and nothing else: one no longer
    /// selected is forgotten, a newly selected one adopted, with whether it already recorded a whole duty read from
    /// the run table so that survives a restart. Only <see cref="Sync"/> calls this, which is what lets
    /// <see cref="Retains"/> stay a question (B2a review finding 2). A shared profile selected for a build the
    /// player refused is withdrawn again, once per profile per process.
    /// </summary>
    private void ReconcileBound(ProfileSelection selection)
    {
        var selected = selection is { IsUsable: true, Origin: ProfileOrigin.Shared, Profile: { } profile } ? profile : null;
        if (_bound is { } bound && !string.Equals(bound.ProfileId, selected?.ProfileId, StringComparison.Ordinal))
        {
            ForgetBound();
        }

        if (selected is null || _bound is not null)
        {
            return;
        }

        // A recorded duty alone is not proof across a restart (plan §18.4): unless this very document was recorded
        // as settled, the profile is adopted as watched, its code recovered and the audit resumed by Evaluate. Only
        // the duties this document recorded count, by the bind time kept with it (audit 2026-10-03, OE-4).
        var since = BoundSince(selected);
        var ranComplete = _host.HasFinishedSharedRun(selected.ProfileId, since);
        _bound = new BoundProfile(selected.Region, selected.GameBuild, selected.ProfileId)
        {
            ProfileSha256 = selected.ProfileSha256,
            RecordsFromUtc = since,
            RanComplete = ranComplete,
            Proven = ranComplete && IsSettled(selected),
        };
        if (!_withdrawn.Contains(selected.ProfileId) && IsUserRejected(selected.Region, selected.GameBuild))
        {
            Supersede(UserRejectedReason, contradicted: false);
        }
    }

    private void AdoptBound(SharedContext context)
    {
        // The code behind a settled profile is recovered too, although its watch is over: a revocation
        // read from the index is matched by code, and this is where the profile in use learns its own
        // (plan §3). Nothing else about a settled profile changes - it is not registered for counting,
        // and no criterion is judged against it again.
        if (_bound is not { } bound)
        {
            return;
        }

        if (bound.Declared is null && context.Selection.Profile is { } profile &&
            SharedProfileBuilder.RecoverPayload(profile, context.Template) is { } payload &&
            SharedCandidateVerifier.Candidate(payload, context.Template) is { } declared)
        {
            // The code is the one recorded when the document was bound, when that was kept: read back through a template a
            // software update replaced, the profile names another identity than the one the repository published. The
            // observer still counts it under the identity this template gives it.
            bound.Sha = RecordedCode(bound) ?? declared.CandidateId;
            bound.Declared = declared;
            var storedEvidence = Attempt(() => _services.SharedCalibrations.LoadCandidates(
                context.Key.Region, context.Key.GameBuild, context.Key.TemplateSha256))
                ?.FirstOrDefault(item => item.CodeSha256 == bound.Sha);
            // A recovered identity different from the recorded code cannot represent that code's evidence.
            if (declared.CandidateId == bound.Sha)
                RegisterEvidenceCandidate(declared, storedEvidence?.QuarantineEpoch ?? 0);
            bound.MatchSource = payload.MatchSource;
            bound.Provenance = DisplayedProvenance(bound.Region, bound.GameBuild, bound.Sha, SharedCandidateProvenance.Published);

            // Adopted from disk - after a restart, or after a withdrawal whose delete failed: a code this
            // build's traffic already rejected is withdrawn again, never trusted. Once per profile per
            // process, so a file that cannot be deleted does not turn every selection into a retry.
            if (!bound.Proven && !_withdrawn.Contains(bound.ProfileId) &&
                (_rejected.Contains(declared.CandidateId) || Attempt(() => _services.SharedCalibrations.IsRejected(
                    context.Key.Region, context.Key.GameBuild, context.Key.TemplateSha256, declared.CandidateId))))
            {
                Supersede("REJECTED");
                return;
            }

            // So is a code the last index this machine read revoked (audit 2026-10-03, OE-1): the revocation may have
            // arrived mid-duty and been held for the run's end, which the process that read it never reached, and the
            // six-hour throttle keeps the index from being read again. Settled or not - a withdrawn code is
            // withdrawn - and held for the end of a run in flight exactly as a revocation read now is.
            if (IsRevoked(bound))
            {
                WithdrawRevoked(Array.Empty<Prepared>());
                return;
            }

            if (!bound.Proven && storedEvidence?.Quarantined == true && declared.CandidateId == bound.Sha &&
                Prepare(bound.Sha, payload, context.Template, _clock.UtcNow) is { } restricted)
            {
                bound.WatchGate = SharedCandidateProvenance.Imported;
                Supersede("QUARANTINED", contradicted: false, pending: new[]
                {
                    restricted with { Submitters = storedEvidence.Submitters, ShapePrior = storedEvidence.ShapePrior,
                        Quarantined = true, QuarantineEpoch = storedEvidence.QuarantineEpoch },
                });
                return;
            }
        }

        if (!bound.Proven && bound.Declared is { } watched)
        {
            _host.RegisterSharedCandidate(watched);
        }
    }

    /// <summary>
    /// What the card shows of a shared profile adopted from disk once it is proven (review 2026-10-15). One settled in an
    /// earlier process that reads the server's own match records on with calibration disarmed, so there is no context,
    /// <see cref="AdoptBound"/> never recovers its code, and the snapshot - which lists only a profile whose code is known -
    /// would show nothing: no 这份校准好用，告诉其他玩家 after a restart, although the share code can be given. Its code is
    /// recovered once through the shipped template, as the share export does. Whether the repository published it is read
    /// off this machine's own store - is the code kept there. Its shape prior stays unknown: nothing is left to compare it
    /// with, and the card does not need it. Nothing is fetched (docs/privacy-boundary.md §8.2), and a profile bound in this
    /// process, which knows all of this already, is left as it is.
    /// </summary>
    private void DescribeSettled(SharedContext? context)
    {
        if (_bound is not { Proven: true, Source: null, Described: false } bound)
        {
            return;
        }

        bound.Described = true;
        if (bound.Sha is null)
        {
            // The code recorded when the document was bound, else the one the profile reads as through the template.
            var template = context?.Template ?? Attempt(() => _services.SelectTemplate(bound.Region));
            var payload = template is not null && _host.SharedSelection().Profile is { } profile &&
                          string.Equals(profile.ProfileId, bound.ProfileId, StringComparison.Ordinal)
                ? Attempt(() => SharedProfileBuilder.RecoverPayload(profile, template))
                : null;
            if ((RecordedCode(bound) ?? (payload is null ? null : ShareCode.Sha256(payload))) is not { } recovered)
            {
                return;
            }

            bound.Sha = recovered;
            bound.MatchSource = payload?.MatchSource;
        }

        // The revocation check AdoptBound makes, which never ran: no calibration is armed beside a settled profile (review
        // 2026-10-15, M1). The store's record of the last index read answers it; nothing is fetched.
        if (IsRevoked(bound))
        {
            WithdrawRevoked(Array.Empty<Prepared>());
            return;
        }

        bound.Provenance = DisplayedProvenance(bound.Region, bound.GameBuild, bound.Sha, SharedCandidateProvenance.Published);
    }

    /// <summary>
    /// True when the last index this machine read revokes the code of the profile in use, and it has not been withdrawn
    /// in this process already - once per profile per process, so a file that cannot be deleted does not turn every
    /// selection into a retry.
    /// </summary>
    private bool IsRevoked(BoundProfile bound) =>
        bound.Sha is { } sha && !_withdrawn.Contains(bound.ProfileId) &&
        (_revoked.Contains(sha) ||
         Attempt(() => _services.SharedCalibrations.Publication(bound.Region, bound.GameBuild, sha)) == SharedPublication.Revoked);

    /// <summary>The code the store recorded the profile document in use was bound from; null when nothing was kept (review 2026-10-15, L2).</summary>
    private string? RecordedCode(BoundProfile bound) =>
        bound.ProfileSha256 is { } document
            ? Attempt(() => _services.SharedCalibrations.BoundCode(bound.Region, bound.GameBuild, document))
            : null;

    /// <summary>
    /// What the card is told about the profile in use (review 2026-10-15, M2): published only while this machine keeps the
    /// published code text - the one thing 这份校准好用，告诉其他玩家 hands back - and imported otherwise, whatever gate it
    /// is watched under, so the card never offers a share that can only fail.
    /// </summary>
    /// <param name="region">Region of the profile.</param>
    /// <param name="build">Build of the profile.</param>
    /// <param name="sha">Its code, when known.</param>
    /// <param name="claimed">What its candidate was known as; an imported one stays imported.</param>
    private SharedCandidateProvenance DisplayedProvenance(Region region, string build, string? sha, SharedCandidateProvenance claimed) =>
        claimed == SharedCandidateProvenance.Published && sha is not null &&
        Attempt(() => _services.SharedCalibrations.CodeText(region, build, sha)) is not null
            ? SharedCandidateProvenance.Published
            : SharedCandidateProvenance.Imported;

    private void ForgetBound()
    {
        var previous = _bound?.Sha;
        if (_bound?.Declared is { } declared)
        {
            _host.UnregisterSharedCandidate(declared.CandidateId);
        }

        _bound = null;
        if (previous is not null) ReleaseEvidenceCandidate(previous);
    }

    private void Reset(SharedKey? key)
    {
        CancelFetch();
        foreach (var candidate in _candidates.ToArray())
        {
            Drop(candidate, rejected: false);
        }

        _binding = null;
        _deferred = null;
        _key = key;
        _rejected.Clear();
        _rejectedSummaries.Clear();
        _contradictions.Clear();
        _nextAutoFetchAt = null;
        _lastFetchStatus = null;
        _lastAttempts = Array.Empty<SharedSourceAttempt>();
        _superseded = false;
        _lastRefusal = null;
        _consent = null;
    }

    private void CancelFetch()
    {
        if (_fetch is { } fetch)
        {
            fetch.Cancellation.Cancel();
        }

        _fetch = null;
        _fetchVisible = false;
    }

    private DateTimeOffset? ConsentAt(SharedKey key)
    {
        if (_consent is { } memo && memo.Region == key.Region && string.Equals(memo.Build, key.GameBuild, StringComparison.Ordinal))
        {
            return memo.At;
        }

        if (!ReadConsents().TryGetValue(ConsentName(key), out var at))
        {
            return null;
        }

        _consent = (key.Region, key.GameBuild, at);
        return at;
    }

    private void RememberConsent(SharedKey key, DateTimeOffset at)
    {
        _consent = (key.Region, key.GameBuild, at);
        if (_settings is null)
        {
            return;
        }

        var consents = ReadConsents();
        consents[ConsentName(key)] = at;
        var kept = new JsonObject();
        foreach (var (name, stamp) in consents.OrderByDescending(pair => pair.Value).Take(MaxConsents).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            kept[name] = CalibratedProfileDocument.Timestamp(stamp);
        }

        // Remembered for this run whatever happens; a failed write only means asking again after a restart.
        Attempt(() =>
        {
            _settings.SetSetting(QueueConsentSetting, kept.ToJsonString());
            return true;
        });
    }

    private Dictionary<string, DateTimeOffset> ReadConsents()
    {
        var consents = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var raw = _settings is null ? null : Attempt(() => _settings.GetSetting(QueueConsentSetting));

        // An unreadable value - not JSON, or a key or stamp that cannot be read as text - asks again; it is a question, not evidence.
        if (raw is null || !WellFormedJson.TryParse(Encoding.UTF8.GetBytes(raw), default, out var document))
        {
            return consents;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return consents;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    consents[property.Name] = at;
                }
            }
        }

        return consents;
    }

    private static string ConsentName(SharedKey key) => SharedCalibrationIndex.RegionDirectory(key.Region) + "/" + key.GameBuild;

    private void Schedule(Func<Task> work)
    {
        if (_pending++ == 0)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                lock (_gate)
                {
                    _lastRefusal = "INTERNAL:" + ex.GetType().Name;
                }
            }
            finally
            {
                TaskCompletionSource? idle = null;
                lock (_gate)
                {
                    if (--_pending == 0)
                    {
                        idle = _idle;
                    }
                }

                idle?.TrySetResult();
            }
        });
    }

    private static T? Attempt<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return default;
        }
    }
}
