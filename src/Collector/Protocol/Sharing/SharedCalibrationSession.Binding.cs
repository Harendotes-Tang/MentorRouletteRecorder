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
        string? refusal = null;
        Func<Capture.GameProcessDetection, ProfileSelection>? select = null;
        try
        {
            var built = SharedProfileBuilder.Build(
                ticket.Candidate.Prepared.Payload, ticket.Template, _clock.UtcNow, ticket.Counts, ticket.ConsentAt);
            if (built is { Status: SharedProfileBuildStatus.Built, ProfileId: { } id })
            {
                _services.WriteSharedProfile(built);
                profileId = id;
                select = _services.ReloadSelect();
            }
            else
            {
                refusal = "BUILD_" + built.Status.ToString().ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            refusal = "WRITE_FAILED:" + ex.GetType().Name;
        }

        lock (_gate)
        {
            Commit(ticket, profileId, select, refusal);
        }
    }

    /// <summary>Under the gate: the written profile becomes the one in use, unless something changed while it was written.</summary>
    private void Commit(
        BindTicket ticket, string? profileId, Func<Capture.GameProcessDetection, ProfileSelection>? select, string? refusal)
    {
        var candidate = ticket.Candidate;
        if (ReferenceEquals(_binding, candidate))
        {
            _binding = null;
        }

        if (_stopped || _key != ticket.Key || !_candidates.Contains(candidate))
        {
            // Disarmed, rebuilt, dropped or refused by the player meanwhile. The file, if written, is harmless:
            // selection decides, and a refused build withdraws any shared profile it selects (ReconcileBound).
            _lastRefusal = "STALE";
        }
        else if (refusal is not null || profileId is null || select is null)
        {
            Block(candidate, refusal ?? "WRITE_FAILED");
        }
        else
        {
            CommitWritten(ticket, profileId, select);
        }

        _host.SharedCalibrationChanged();
    }

    /// <summary>Hands the written profile to the pipeline; adopts it when selected, blocks the candidate for this session when not.</summary>
    private void CommitWritten(BindTicket ticket, string profileId, Func<Capture.GameProcessDetection, ProfileSelection> select)
    {
        var candidate = ticket.Candidate;
        var auditPending = candidate.Verification?.AuditPending == true;
        var result = _host.CommitSharedBind(new SharedBindRequest(profileId, select, candidate.Stage, auditPending));
        if (result.Outcome is SharedBindOutcome.NotSelected or SharedBindOutcome.SessionChanged)
        {
            // Delete nothing and leave calibration armed; the candidate may try again next session.
            Block(candidate, result.Reason);
            return;
        }

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
            Provenance = candidate.Provenance,
            MatchSource = candidate.Prepared.Payload.MatchSource,
            BoundAtUtc = result.Outcome == SharedBindOutcome.Bound ? _clock.UtcNow : null,
            Verification = candidate.Verification,
            RanComplete = result.RanComplete,
            Proven = result.Proven,
        };

        // Unless the drained staging already finished a duty with nothing left to audit, it stays registered
        // with the observer and keeps being verified until it records one and the audit settles.
        if (result.Proven)
        {
            RecordSettled(_bound);
            _host.UnregisterSharedCandidate(candidate.Sha);
        }

        _superseded = false;
        _lastRefusal = null;
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
        if (contradicted)
        {
            // What it recorded before the traffic caught it out is suspect (plan §18.4). Not for the player's own
            // refusal: those records were made by a code nothing contradicted.
            _host.FlagSharedRecords(bound.ProfileId, bound.BoundAtUtc, reason);
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
        Attempt(() =>
        {
            _services.DeleteSharedProfile(bound.Region, bound.GameBuild);
            return true;
        });
        var select = Attempt(() => _services.ReloadSelect());
        lock (_gate)
        {
            if (_stopped || select is null)
            {
                return;
            }

            _host.ReselectAfterSharedChange(select);
            Sync();
            if (pending is { Count: > 0 })
            {
                // The file is gone and the catalogue has been read again, so the next best code of the
                // index that revoked this one may now be offered - and written to that same path.
                RegisterDownloaded(pending);
                Evaluate();
            }

            _host.SharedCalibrationChanged();
        }
    }

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
        if (string.Equals(_bound?.Sha, prepared.Sha, StringComparison.Ordinal))
        {
            return null;
        }

        if (_candidates.FirstOrDefault(candidate => candidate.Sha == prepared.Sha) is { } known)
        {
            // One code, one candidate. A download of a code the player had pasted proves it published (plan §18.5).
            if (provenance == SharedCandidateProvenance.Published)
            {
                known.Provenance = SharedCandidateProvenance.Published;
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
        _candidates.Remove(candidate);
        candidate.Stage = null;
        _host.UnregisterSharedCandidate(candidate.Sha);
        if (ReferenceEquals(_binding, candidate))
        {
            _binding = null;
        }

        if (rejected && _rejected.Add(candidate.Sha) && _rejectedSummaries.Count < CalibrationObserver.MaxCandidates)
        {
            _rejectedSummaries.Add(Summary(candidate) with { Status = SharedCandidateStatus.Rejected });
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
        // as settled, the profile is adopted as watched, its code recovered and the audit resumed by Evaluate.
        var ranComplete = _host.HasFinishedSharedRun(selected.ProfileId);
        _bound = new BoundProfile(selected.Region, selected.GameBuild, selected.ProfileId)
        {
            ProfileSha256 = selected.ProfileSha256,
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
            bound.Sha = declared.CandidateId;
            bound.Declared = declared;
            bound.MatchSource = payload.MatchSource;

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
        }

        if (!bound.Proven && bound.Declared is { } watched)
        {
            _host.RegisterSharedCandidate(watched);
        }
    }

    private void ForgetBound()
    {
        if (_bound?.Declared is { } declared)
        {
            _host.UnregisterSharedCandidate(declared.CandidateId);
        }

        _bound = null;
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
        _nextAutoFetchAtUtc = null;
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
