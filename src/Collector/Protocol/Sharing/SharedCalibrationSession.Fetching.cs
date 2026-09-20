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

// SharedCalibrationSession, continued: registering and claiming downloads, and importing a pasted code.
internal sealed partial class SharedCalibrationSession
{
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

        if (!manual && _nextAutoFetchAtUtc is { } next && _clock.UtcNow < next)
        {
            return SharedCheckOutcome.NotNeeded;
        }

        var ticket = new FetchTicket(context.Key, context.Template, manual) { Recheck = recheck };
        _fetch = ticket;
        Schedule(() => FetchAsync(ticket));
        return SharedCheckOutcome.Started;
    }

    /// <summary>Off the gate: send when due and rebuild what came back; then claim it under the gate.</summary>
    private async Task FetchAsync(FetchTicket ticket)
    {
        var (sent, last, result) = await SendIfDueAsync(ticket).ConfigureAwait(false);
        var prepared = PrepareAll(ticket, result);
        lock (_gate)
        {
            ClaimFetch(ticket, sent, last, result, prepared);
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

        _nextAutoFetchAtUtc = (sent ? _clock.UtcNow : last?.LastAttemptAtUtc ?? _clock.UtcNow) + SharedCalibrationStore.AutoFetchInterval;
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

        // The profile in use too, so the card and the report keep saying where it stands. An imported code bound
        // with every criterion already passed, so this changes nothing about its watch.
        if (_bound is { Provenance: SharedCandidateProvenance.Imported, Sha: { } bound } && Published(bound))
        {
            _bound.Provenance = SharedCandidateProvenance.Published;
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

        var revoked = result.RevokedCodeSha256s.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in _candidates.Where(item => revoked.Contains(item.Sha)).ToArray())
        {
            Drop(candidate, rejected: true);
        }

        // The whole point of reading the index beside a profile that records (plan §3): the machine still
        // recording with a code the repository has withdrawn is the one that most needs to hear about it.
        // Whether it is still watched or long settled makes no difference - a withdrawn code is withdrawn.
        if (_bound is not { Sha: { } inUse } || !revoked.Contains(inUse))
        {
            return false;
        }

        WithdrawRevoked(prepared);
        return true;
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
            .Select(code => (code.CodeSha256, code.Payload))
            .ToList();
        foreach (var fetched in result?.Candidates ?? Array.Empty<SharedCalibrationCandidate>())
        {
            if (codes.All(code => code.CodeSha256 != fetched.CodeSha256) &&
                !Attempt(() => store.IsRejected(region, build, templateSha, fetched.CodeSha256)))
            {
                codes.Add((fetched.CodeSha256, fetched.Payload));
            }
        }

        var now = _clock.UtcNow;
        return codes.Take(SharedCalibrationIndex.MaxCandidates)
            .Select(code => Prepare(code.CodeSha256, code.Payload, ticket.Template, now))
            .OfType<Prepared>()
            .ToArray();
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
                "这不是一份能识别的校准码：" + (decoded.Rejection?.Message ?? "内容为空。"));
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
            return new SharedImportResult(SharedImportOutcome.Malformed, "UNBUILDABLE", "这份校准码无法在本机的随包模板上生成协议档案。", sha);
        }

        // Read off the gate: the store has its own lock, and the index it holds was read by an earlier download -
        // nothing is sent to answer this.
        var publication = Attempt(() => _services.SharedCalibrations.Publication(context.Key.Region, context.Key.GameBuild, sha));
        if (publication == SharedPublication.Revoked)
        {
            return new SharedImportResult(SharedImportOutcome.NotApplicable, "REVOKED", RefusalMessage("REVOKED"), sha);
        }

        var provenance = publication == SharedPublication.Published ? SharedCandidateProvenance.Published : SharedCandidateProvenance.Imported;
        lock (_gate)
        {
            if (_stopped || _host.SharedContext()?.Key != context.Key)
            {
                return new SharedImportResult(SharedImportOutcome.NotApplicable, "CHANGED", "导入期间游戏版本或校准状态发生了变化，请重新导入。", sha);
            }

            if (Register(prepared, SharedCandidateSource.Manual, provenance) is { } refusal)
            {
                return new SharedImportResult(SharedImportOutcome.NotApplicable, refusal, RefusalMessage(refusal), sha);
            }

            Evaluate();
            _host.SharedCalibrationChanged();
            return new SharedImportResult(SharedImportOutcome.Applied, null, ImportedMessage(provenance), sha, provenance);
        }
    }

    private static string ImportedMessage(SharedCandidateProvenance provenance) => provenance == SharedCandidateProvenance.Published
        ? "校准码已导入。它与公开仓库里其他玩家提交的一致，登录时在本机流量里核实通过就会启用。"
        : "校准码已导入。它没有在公开仓库发布过，所以要在本机登录并排一次本、核实通过后才会启用；在那之前不会生成记录。";

    private SharedImportResult? Inapplicable(ShareCodePayload payload, string sha, SharedContext? context, bool userRejected)
    {
        string? reason = null;
        string? message = null;
        if (context is null)
        {
            (reason, message) = ("NOT_CALIBRATING", "当前客户端不在校准中，暂时不需要导入校准码。");
        }
        else if (payload.Region != context.Key.Region)
        {
            (reason, message) = ("OTHER_REGION", "这份校准码属于另一个服务器区域（国服与国际服不能通用）。");
        }
        else if (!string.Equals(payload.GameBuild, context.Key.GameBuild, StringComparison.Ordinal))
        {
            (reason, message) = ("OTHER_BUILD",
                $"这份校准码适用于客户端版本 {payload.GameBuild}，当前客户端版本是 {context.Key.GameBuild}，不能通用。");
        }
        else if (!SharedProfileBuilder.IsApplicable(payload, context.Template))
        {
            (reason, message) = ("OTHER_TEMPLATE", "这份校准码是用另一个版本的导随记录器生成的，与本机的版本对不上；请双方更新到同一版本。");
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

    private static string RefusalMessage(string refusal) => refusal switch
    {
        "REJECTED" => "这份校准码已经在本机流量里对不上，不再使用；在校准卡片上点「重新观察」可以清除这个判定。",
        "REVOKED" => "这份校准码已经在公开仓库里被撤回，不能再使用；请向分享者要一份新的，或等待其他玩家的分享。",
        UserRejectedReason => "你已经选择这个游戏版本不用其他玩家的共享校准；在校准卡片上点「重新观察」后才能导入校准码。",
        _ => "正在核实的校准码已经太多，请等当前的核实有结果后再导入。",
    };
}
