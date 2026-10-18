using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Protocol.Sharing;

// SharedCalibrationSession, continued: registering and claiming downloads, and importing a pasted code.
internal sealed partial class SharedCalibrationSession
{
    // A read revocation remains authoritative even when state.json cannot be replaced. Keep it
    // across capture/re-arm/build changes; the canonical code hash includes its region and build.
    // Accessed under _gate, unlike the off-gate disk cache used by PrepareAll.
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ fetching

    private SharedCheckOutcome MaybeFetch(SharedContext context, bool manual)
    {
        if (!_services.SharedFetchWired || !_enabled)
        {
            return manual ? SharedCheckOutcome.Disabled : SharedCheckOutcome.NotNeeded;
        }

        // The one outbound request is for a build nothing records yet - and, since 1.4.0, for the two
        // profiles in force that the index can still say something about: another player's calibration,
        // which the repository may have revoked, and one that infers the match, which a published
        // announcement code outranks (docs/privacy-boundary.md §8.2). Beside a shipped profile or a local
        // one that reads the match, manual checks included, nothing is sent at all.
        var recheck = SharedRecheck.ReasonFor(
            context.Selection.IsUsable, context.Selection.Origin, context.Selection.Profile is { MatchFromQueue: true });
        if (context.Selection.IsUsable && recheck is null)
        {
            return SharedCheckOutcome.NotNeeded;
        }

        // 不用共享的: the player said no for this build, so there is nothing to fetch for until 重新观察.
        if (IsUserRejected(context.Key.Region, context.Key.GameBuild))
        {
            return SharedCheckOutcome.NotNeeded;
        }

        if (_fetch is not null)
        {
            return SharedCheckOutcome.AlreadyFetching;
        }

        // A check that has only just been through every source is the answer to this one as well, whatever it
        // found: refusing it costs the player nothing, while granting it lets anything able to open the pipe keep
        // the repository and its mirrors busy for as long as it likes (review finding 13). It gets its own
        // outcome rather than borrowing AlreadyFetching: nothing is running here, and the desktop's line for
        // AlreadyFetching sends the player to watch the card for a result that is never coming. Read off the
        // monotonic clock, so a system clock moved backwards cannot stretch the gap into a lasting refusal.
        if (manual && _lastManualCheckAt is { } previous && _clock.Elapsed - previous < ManualCheckInterval)
        {
            return SharedCheckOutcome.RecentlyChecked;
        }

        // On the monotonic clock as well (audit 2026-10-03, OE-6b): a system clock moved back must not hold the next
        // automatic download back for six hours plus however far it moved.
        if (!manual && _nextAutoFetchAt is { } next && _clock.Elapsed < next)
        {
            return SharedCheckOutcome.NotNeeded;
        }

        var ticket = new FetchTicket(context.Key, context.Template, manual) { Recheck = recheck };
        _fetch = ticket;
        if (manual)
        {
            _lastManualCheckAt = _clock.Elapsed;
        }

        Schedule(() => FetchAsync(ticket));
        return SharedCheckOutcome.Started;
    }

    /// <summary>
    /// Offers the codes the store keeps for the build again, read off the disk and never fetched: the download path with
    /// its request left out (review 2026-10-15, L4). Nothing happens while a download runs - it reads the store too - nor
    /// while nothing is being calibrated, the setting is off, the download is not wired, or the player refused shared
    /// calibration for the build.
    /// </summary>
    private void ReofferStored()
    {
        if (_stopped || !_enabled || !_services.SharedFetchWired || _fetch is not null ||
            _host.SharedContext() is not { } context || IsUserRejected(context.Key.Region, context.Key.GameBuild))
        {
            return;
        }

        var ticket = new FetchTicket(context.Key, context.Template, manual: false) { DiskOnly = true };
        _fetch = ticket;
        Schedule(() => FetchAsync(ticket));
    }

    /// <summary>Off the gate: send when due and rebuild what came back; then claim it under the gate.</summary>
    private async Task FetchAsync(FetchTicket ticket)
    {
        try
        {
            var (sent, last, result) = ticket.DiskOnly
                ? (false, null, null)
                : await SendIfDueAsync(ticket).ConfigureAwait(false);
            var prepared = PrepareAll(ticket, result);
            lock (_gate)
            {
                ClaimFetch(ticket, sent, last, result, prepared);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Whatever threw, the download is over: left claimed, every later check would answer AlreadyFetching
            // for the rest of the arm. The failure itself is still recorded where Schedule records it.
            lock (_gate)
            {
                if (ReferenceEquals(_fetch, ticket))
                {
                    _fetch = null;
                    _fetchVisible = false;
                    _host.SharedCalibrationChanged();
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Off the gate: sends the download unless the six-hour throttle says an earlier attempt still stands, tells
    /// the card once it takes a noticeable moment, and records what it produced.
    /// </summary>
    private async Task<(bool Sent, SharedFetchRecord? Last, SharedCalibrationFetchResult? Result)> SendIfDueAsync(FetchTicket ticket)
    {
        var (region, build, templateSha, _) = ticket.Key;
        var store = _services.SharedCalibrations;
        var last = Attempt(() => store.LastFetch(region, build, templateSha));
        if (!SharedCalibrationStore.ShouldAutoFetch(last?.LastAttemptAtUtc, _clock.UtcNow, ticket.Manual))
        {
            return (false, last, null);
        }

        var fetch = FetchOrNullAsync(ticket);
        if (await Task.WhenAny(fetch, Task.Delay(FetchingNoticeDelay)).ConfigureAwait(false) != fetch)
        {
            ShowFetching(ticket);
        }

        var result = await fetch.ConfigureAwait(false);
        if (result is not null)
        {
            Attempt(() => store.RecordFetch(region, build, templateSha, result, _clock.UtcNow));
        }

        return (true, last, result);
    }

    private void ShowFetching(FetchTicket ticket)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_fetch, ticket))
            {
                _fetchVisible = true;
                _host.SharedCalibrationChanged();
            }
        }
    }

    /// <summary>Under the gate: a download counts only while what it was started for is still in force.</summary>
    private void ClaimFetch(
        FetchTicket ticket, bool sent, SharedFetchRecord? last, SharedCalibrationFetchResult? result, IReadOnlyList<Prepared> prepared)
    {
        if (ReferenceEquals(_fetch, ticket))
        {
            _fetch = null;
            _fetchVisible = false;
        }

        // Outbound activity is reported whatever becomes of the answer: the request did leave the machine.
        if (sent && result is { IndexAttempts.Count: > 0 })
        {
            _lastSentAtUtc = _clock.UtcNow;
            _lastSentStatus = result.Status;
        }

        // A read beside a profile already recording is reported apart, so the diagnostics report says
        // plainly which of the two relaxations of §8.2 this machine exercised, and how it ended.
        if (ticket.Recheck is { } why && result is { } read)
        {
            _recheck = new SharedRecheckRecord(_clock.UtcNow, read.Status, why);
        }

        if (_stopped || !_enabled || _key != ticket.Key || ticket.Cancellation.IsCancellationRequested ||
            IsUserRejected(ticket.Key.Region, ticket.Key.GameBuild))
        {
            // Setting off, disarmed, another build or template, or refused by the player: whatever came back is not for this.
            _host.SharedCalibrationChanged();
            return;
        }

        // Six hours from the attempt that counts - this one, or the stored one that made this one unnecessary - kept
        // on the monotonic clock. Only the age of the stored attempt is read off the wall clock, and only once. A read of
        // the disk alone is no attempt at all and leaves the schedule as it was.
        if (!ticket.DiskOnly)
        {
            var interval = SharedCalibrationStore.AutoFetchInterval;
            var age = sent || last is null ? TimeSpan.Zero : _clock.UtcNow - last.LastAttemptAtUtc;
            _nextAutoFetchAt = _clock.Elapsed + interval - TimeSpan.FromTicks(Math.Clamp(age.Ticks, 0, interval.Ticks));
        }
        if (result is not null && ApplyFetchResult(result, prepared))
        {
            // The revocation of the code in use took the rest of that index with it, to be offered again
            // once the withdrawal has actually happened.
            _host.SharedCalibrationChanged();
            return;
        }

        RegisterDownloaded(prepared);

        if (result is { IndexWasRead: true })
        {
            PromotePublishedImports(ticket.Key);
        }

        Evaluate();
        _host.SharedCalibrationChanged();
    }

    /// <summary>
    /// An index just read may list a code the player pasted earlier and the previous index did not know (the CDN
    /// mirrors lag by hours, §3.3): it is published after all, and is judged by the published gate from now on (plan §18.5).
    /// </summary>
    private void PromotePublishedImports(SharedKey key)
    {
        bool Published(string sha) =>
            Attempt(() => _services.SharedCalibrations.Publication(key.Region, key.GameBuild, sha)) == SharedPublication.Published;

        foreach (var candidate in _candidates.Where(item => item.Provenance == SharedCandidateProvenance.Imported))
        {
            if (Published(candidate.Sha))
            {
                candidate.Provenance = SharedCandidateProvenance.Published;
            }
        }

        // The profile in use too, so the card and the report keep saying where it stands - once the store keeps its code,
        // not merely because the index named it (a discard is named too): the card offers to hand that code back (review
        // 2026-10-15, M2). An imported code bound with every criterion already passed, so its watch is left as it is.
        if (_bound is { Provenance: SharedCandidateProvenance.Imported, Sha: { } bound } && Published(bound))
        {
            _bound.Provenance = DisplayedProvenance(_bound.Region, _bound.GameBuild, bound, SharedCandidateProvenance.Published);
        }
    }

    /// <summary>
    /// Keeps what a claimed download says and drops what its index revokes, the profile in use included.
    /// True when that revocation took charge of the rest of the index.
    /// </summary>
    /// <param name="result">What came back.</param>
    /// <param name="prepared">The rest of that index, to carry on with once a withdrawal actually happens.</param>
    private bool ApplyFetchResult(SharedCalibrationFetchResult result, IReadOnlyList<Prepared> prepared)
    {
        _lastFetchStatus = result.Status;
        _lastAttempts = result.IndexAttempts;
        if (!result.IndexWasRead)
        {
            return false;
        }

        _revoked.UnionWith(result.RevokedCodeSha256s);
        foreach (var candidate in _candidates.Where(item => _revoked.Contains(item.Sha)).ToArray())
        {
            Drop(candidate, rejected: true);
        }

        // Permanent revocation precedes reversible quarantine and also applies to settled profiles.
        if (_bound is { Sha: { } inUse } && _revoked.Contains(inUse))
        {
            WithdrawRevoked(prepared);
            return true;
        }

        // The download/cache shortlist is bounded. A live candidate or binding can fall below that
        // limit after its ranking changes; the readable index still tightens its current gate.
        foreach (var candidate in _candidates.ToArray())
        {
            if (result.IndexEntries.FirstOrDefault(entry => entry.CodeSha256 == candidate.Sha) is not { } entry)
                continue;
            Register(candidate.Prepared with
            {
                Submitters = entry.Submitters,
                ShapePrior = entry.ShapePrior,
                LocalPrior = candidate.LocalPrior,
                Quarantined = entry.Quarantined,
                QuarantineEpoch = entry.QuarantineEpoch,
            }, candidate.Source, candidate.Provenance);
        }

        if (_bound is { Sha: { } boundCode } bound)
        {
            var current = prepared.FirstOrDefault(item => item.Sha == boundCode);
            var entry = result.IndexEntries.FirstOrDefault(item => item.CodeSha256 == boundCode);
            // A new template may recover the bound document as another code. Its traffic cannot be
            // reported under the original published code's quarantine generation.
            if ((current?.Declared ?? bound.Declared) is { } declared && declared.CandidateId == boundCode &&
                (entry?.QuarantineEpoch ?? current?.QuarantineEpoch) is { } epoch)
                RegisterEvidenceCandidate(declared, epoch);
            if ((entry?.Quarantined ?? current?.Quarantined) == true && !bound.Proven)
            {
                // A login-only binding has not earned the exemption reserved for a proven local profile.
                // Keep its MRC and prior records; temporarily unload only the unproven derived profile.
                bound.WatchGate = SharedCandidateProvenance.Imported;
                Supersede("QUARANTINED", contradicted: false, pending: prepared);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the revoked profile out of use, or arranges for that to happen once the machine is back between
    /// runs: withdrawing mid-duty would end the run the way a stopped capture ends it, and the run is worth
    /// more finished and flagged than cut short.
    ///
    /// The rest of the index waits for the withdrawal either way. A replacement is written to the very path
    /// the withdrawal deletes, so the two must not be in flight together; the codes are offered again from
    /// inside <see cref="Withdraw"/>, once the file is gone and the catalogue has been read afresh.
    /// </summary>
    /// <param name="prepared">The other codes that index offered.</param>
    private void WithdrawRevoked(IReadOnlyList<Prepared> prepared)
    {
        if (_host.SharedRunInFlight())
        {
            _deferred = prepared;
            return;
        }

        Supersede("REVOKED", pending: prepared);
    }

    private async Task<SharedCalibrationFetchResult?> FetchOrNullAsync(FetchTicket ticket)
    {
        try
        {
            return await _services.FetchSharedCalibration(ticket.Key.Region, ticket.Key.GameBuild, ticket.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>Off the gate: stored codes first (best first), then fresh ones the store could not keep, rebuilt through the template.</summary>
    private IReadOnlyList<Prepared> PrepareAll(FetchTicket ticket, SharedCalibrationFetchResult? result)
    {
        if (ticket.Cancellation.IsCancellationRequested)
        {
            return Array.Empty<Prepared>();
        }

        var (region, build, templateSha, _) = ticket.Key;
        var store = _services.SharedCalibrations;
        var codes = (Attempt(() => store.LoadCandidates(region, build, templateSha)) ?? Array.Empty<SharedStoredCandidate>())
            .Select(code => (code.CodeSha256, code.Payload, code.Submitters, code.ShapePrior, code.Quarantined, code.QuarantineEpoch))
            .ToList();
        foreach (var fetched in result?.Candidates ?? Array.Empty<SharedCalibrationCandidate>())
        {
            var existing = codes.FindIndex(code => code.CodeSha256 == fetched.CodeSha256);
            if (existing >= 0)
            {
                // Fresh safety hints must not depend on whether the disk cache could be updated.
                codes[existing] = (fetched.CodeSha256, fetched.Payload, fetched.Submitters, fetched.ShapePrior,
                    fetched.Quarantined, fetched.QuarantineEpoch);
            }
            else if (!Attempt(() => store.IsRejected(region, build, templateSha, fetched.CodeSha256)))
            {
                codes.Add((fetched.CodeSha256, fetched.Payload, fetched.Submitters, fetched.ShapePrior, fetched.Quarantined, fetched.QuarantineEpoch));
            }
        }

        if (result is { IndexWasRead: true })
        {
            var revoked = result.RevokedCodeSha256s.ToHashSet(StringComparer.Ordinal);
            codes.RemoveAll(code => revoked.Contains(code.CodeSha256));
            foreach (var entry in result.IndexEntries)
            {
                var existing = codes.FindIndex(code => code.CodeSha256 == entry.CodeSha256);
                if (existing >= 0)
                {
                    // A readable index restricts the cached payload even if every file download or
                    // the state-file write failed. Never let a stale login gate commit meanwhile.
                    var cached = codes[existing];
                    codes[existing] = (cached.CodeSha256, cached.Payload, entry.Submitters, entry.ShapePrior,
                        entry.Quarantined, entry.QuarantineEpoch);
                }
            }
        }

        // What the index says about each code goes with it: with the other codes it decides which of them may bind on the
        // login burst (GateFor, through the consensus) and which binds first (ChooseCandidate). The conflict mark has
        // already ordered the list - stored codes come back in the index's order - and decides nothing further here. The
        // prior that may break a tie is this machine's own, read here off the gate from the calibrations it verified.
        var now = _clock.UtcNow;
        var verified = codes.Count == 0 ? Array.Empty<ShareCodePayload>() : VerifiedPayloads(region, ticket.Template);
        return codes.Take(SharedCalibrationIndex.MaxCandidates)
            .Select(code => Prepare(code.CodeSha256, code.Payload, ticket.Template, now) is { } prepared
                ? prepared with
                {
                    Submitters = code.Submitters,
                    ShapePrior = code.ShapePrior,
                    Quarantined = code.Quarantined,
                    QuarantineEpoch = code.QuarantineEpoch,
                    LocalPrior = SharedLocalShapePrior.For(prepared.Payload, verified),
                }
                : null)
            .OfType<Prepared>()
            .ToArray();
    }

    /// <summary>
    /// Off the gate: what the calibrations this machine verified for the region say about the match (plan §20.5, security
    /// review 2026-10-15) - its own, written only after the player confirmed the timeline, and other players' it recorded
    /// a whole duty with and settled (plan §18.4). Read from this machine's profile directories and store only; a shared
    /// profile still being checked, and anything the repository merely lists, does not count. Nothing is fetched.
    /// </summary>
    private IReadOnlyList<ShareCodePayload> VerifiedPayloads(Region region, CalibrationTemplate template)
    {
        var store = _services.SharedCalibrations;
        var own = Attempt(() => _services.LoadLocalProfiles(region)) ?? Array.Empty<ProtocolProfile>();
        var settled = (Attempt(() => _services.LoadSharedProfiles(region)) ?? Array.Empty<ProtocolProfile>())
            .Where(profile => Attempt(() => store.IsSettled(profile.Region, profile.GameBuild, profile.ProfileSha256)) &&
                              !WithdrawnByTheRepository(profile, template));
        return Attempt(() => SharedLocalShapePrior.Verified(own.Concat(settled), template)) ?? Array.Empty<ShareCodePayload>();
    }

    /// <summary>
    /// True when the last index this machine read for that build revokes the code a settled shared profile was written
    /// from (review 2026-10-15, M1). The settled mark survives a revocation, and a profile the repository withdrew vouches
    /// for nothing - least of all for the shape of the next build's codes. The code is the one recorded at the bind, else
    /// the one the profile reads as through the template.
    /// </summary>
    private bool WithdrawnByTheRepository(ProtocolProfile profile, CalibrationTemplate template)
    {
        var store = _services.SharedCalibrations;
        var code = Attempt(() => store.BoundCode(profile.Region, profile.GameBuild, profile.ProfileSha256)) ??
                   (Attempt(() => SharedProfileBuilder.RecoverPayload(profile, template)) is { } payload ? ShareCode.Sha256(payload) : null);
        return code is not null &&
               Attempt(() => store.Publication(profile.Region, profile.GameBuild, code)) == SharedPublication.Revoked;
    }

    private static Prepared? Prepare(string sha, ShareCodePayload payload, CalibrationTemplate template, DateTimeOffset now)
    {
        if (!string.Equals(ShareCode.Sha256(payload), sha, StringComparison.Ordinal) ||
            SharedCandidateVerifier.Candidate(payload, template) is not { } declared)
        {
            return null;
        }

        // The staging parser only needs the messages; consent changes the written provenance, never them.
        var built = SharedProfileBuilder.Build(payload, template, now, NoCounts, queueInferenceAcceptedAtUtc: now);
        return built is { Status: SharedProfileBuildStatus.Built, Profile: { } profile } ? new Prepared(sha, payload, declared, profile) : null;
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// A code the player pasted (plan §5.1, §18.5): decoded and checked against this client and template
    /// with no network and whatever the setting says. One the last index this machine read lists is verified
    /// like a downloaded one; one no index knows must have every criterion pass before it records; one the
    /// index revoked is refused. Refused too while the player's 不用共享的 stands for the build. Takes the gate
    /// itself and rebuilds the profile outside it.
    /// </summary>
    public SharedImportResult Import(string? code)
    {
        var decoded = ShareCode.Decode(code);
        if (decoded is not { Payload: { } payload, CodeSha256: { } sha })
        {
            return new SharedImportResult(SharedImportOutcome.Malformed, decoded.Rejection?.Code,
                CollectorTexts.SharingImportNotImported(decoded.Rejection?.Text ?? CollectorTexts.SharingCodePasteWhole()));
        }

        SharedContext? context;
        bool userRejected;
        lock (_gate)
        {
            context = _stopped ? null : _host.SharedContext();
            userRejected = context is not null && IsUserRejected(context.Key.Region, context.Key.GameBuild);
        }

        if (Inapplicable(payload, sha, context, userRejected) is { } refused)
        {
            return refused;
        }

        if (Prepare(sha, payload, context!.Template, _clock.UtcNow) is not { } prepared)
        {
            return new SharedImportResult(SharedImportOutcome.Malformed, "UNBUILDABLE", CollectorTexts.SharingImportUnbuildable(), sha);
        }

        // Read off the gate: the store has its own lock, and the index it holds was read by an earlier download -
        // nothing is sent to answer this.
        var publication = Attempt(() => _services.SharedCalibrations.Publication(context.Key.Region, context.Key.GameBuild, sha));
        if (publication == SharedPublication.Revoked)
        {
            return new SharedImportResult(SharedImportOutcome.NotApplicable, "REVOKED", RefusalMessage("REVOKED"), sha);
        }

        var provenance = publication == SharedPublication.Published ? SharedCandidateProvenance.Published : SharedCandidateProvenance.Imported;
        if (provenance == SharedCandidateProvenance.Published)
        {
            // What the index said about it - submitters and the shape prior - as for a downloaded code (GateFor, ChooseCandidate).
            var (region, build, templateSha, _) = context.Key;
            var stored = Attempt(() => _services.SharedCalibrations.LoadCandidates(region, build, templateSha))
                ?.FirstOrDefault(item => string.Equals(item.CodeSha256, sha, StringComparison.Ordinal));
            prepared = prepared with
            {
                Submitters = stored?.Submitters ?? 0, ShapePrior = stored?.ShapePrior,
                Quarantined = stored?.Quarantined ?? false, QuarantineEpoch = stored?.QuarantineEpoch ?? 0,
            };
        }

        // This machine's own prior, as for a downloaded code; read off the gate like everything above.
        prepared = prepared with { LocalPrior = SharedLocalShapePrior.For(prepared.Payload, VerifiedPayloads(context.Key.Region, context.Template)) };

        lock (_gate)
        {
            if (_stopped || _host.SharedContext() is not { } current || current.Key != context.Key)
            {
                return new SharedImportResult(SharedImportOutcome.NotApplicable, "CHANGED", CollectorTexts.SharingImportChanged(), sha);
            }

            if (Register(prepared, SharedCandidateSource.Manual, provenance) is { } refusal)
            {
                return new SharedImportResult(SharedImportOutcome.NotApplicable, refusal, RefusalMessage(refusal), sha);
            }

            // Said before Evaluate, which may bind it at once and take it off the candidate list.
            var atLogin = _candidates.FirstOrDefault(item => item.Sha == sha) is not { } registered ||
                          GateFor(current, registered) == SharedCandidateProvenance.Published;
            Evaluate();
            _host.SharedCalibrationChanged();
            return new SharedImportResult(SharedImportOutcome.Applied, null, ImportedMessage(provenance, atLogin), sha, provenance);
        }
    }

    private static UserText ImportedMessage(SharedCandidateProvenance provenance, bool atLogin) =>
        provenance != SharedCandidateProvenance.Published
            ? CollectorTexts.SharingImportAppliedUnpublished()
            : atLogin
                ? CollectorTexts.SharingImportAppliedAtLogin()
                : CollectorTexts.SharingImportApplied();

    private SharedImportResult? Inapplicable(ShareCodePayload payload, string sha, SharedContext? context, bool userRejected)
    {
        string? reason = null;
        UserText? message = null;
        if (context is null)
        {
            (reason, message) = ("NOT_CALIBRATING", CollectorTexts.SharingImportNotCalibrating());
        }
        else if (payload.Region != context.Key.Region)
        {
            (reason, message) = ("OTHER_REGION", CollectorTexts.SharingImportOtherRegion());
        }
        else if (!string.Equals(payload.GameBuild, context.Key.GameBuild, StringComparison.Ordinal))
        {
            (reason, message) = ("OTHER_BUILD", CollectorTexts.SharingImportOtherBuild(payload.GameBuild, context.Key.GameBuild));
        }
        else if (!SharedProfileBuilder.IsApplicable(payload, context.Template))
        {
            (reason, message) = ("OTHER_TEMPLATE", CollectorTexts.SharingImportOtherTemplate());
        }
        else if (userRejected)
        {
            (reason, message) = (UserRejectedReason, RefusalMessage(UserRejectedReason));
        }
        else if (Attempt(() => _services.SharedCalibrations.IsRejected(
                     context.Key.Region, context.Key.GameBuild, context.Key.TemplateSha256, sha)))
        {
            (reason, message) = ("REJECTED", RefusalMessage("REJECTED"));
        }

        return reason is null ? null : new SharedImportResult(SharedImportOutcome.NotApplicable, reason, message!, sha);
    }

    private static UserText RefusalMessage(string refusal) => refusal switch
    {
        "REJECTED" => CollectorTexts.SharingImportRejected(),
        "REVOKED" => CollectorTexts.SharingImportRevoked(),
        UserRejectedReason => CollectorTexts.SharingImportUserRejected(),
        _ => CollectorTexts.SharingImportCheckPending(),
    };
}
