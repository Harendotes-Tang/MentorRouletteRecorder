using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Protocol.Pipeline;

// The pipeline's side of shared calibration: the entry points
// the IPC handlers call, the capture-health seam, and the few steps that need the parser, the selection
// and the session row. Orchestration - fetching, staging, verification, withdrawal - lives in
// SharedCalibrationSession, which calls back through ISharedCalibrationHost with the gate held.
public sealed partial class LiveProtocolPipeline
{
    /// <summary>
    /// 立即检查: downloads the shared calibrations for the build being calibrated now, past the six-hour
    /// throttle. The setting, the kill switch, the player's refusal and "a profile already records" still stop it.
    /// </summary>
    public SharedCheckOutcome CheckSharedCalibrationNow()
    {
        lock (_gate)
        {
            return _shared.CheckNow();
        }
    }

    /// <summary>
    /// 导入校准码: decodes a pasted code, checks it against the running region, build and template, and
    /// verifies it exactly like a downloaded one. No network; not gated by the setting.
    /// </summary>
    /// <param name="code">Pasted text.</param>
    public SharedImportResult ImportCalibrationCode(string code) => _shared.Import(code);

    /// <summary>
    /// The player accepts that a queue-inferred shared calibration records by inference. Remembered on this
    /// machine per region and build; the waiting code then binds.
    /// </summary>
    public SharedConsentOutcome AcceptSharedQueueInference()
    {
        lock (_gate)
        {
            return _shared.AcceptQueueInference();
        }
    }

    /// <summary>
    /// 不用共享的，我自己校准: withdraws a shared profile in force (calibration re-arms once its file is gone),
    /// drops every candidate being verified for the build, and remembers the refusal apart from the contradiction
    /// records, so no shared code - downloaded or pasted - binds for this region and build until 重新观察
    /// (<see cref="DiscardCalibration"/>). Local calibration is left exactly as it is.
    /// </summary>
    public SharedRejectResult RejectSharedCalibration()
    {
        lock (_gate)
        {
            return _shared.RejectByUser();
        }
    }

    /// <summary>
    /// 分享给其他玩家: the share code of the local calibration in force, taken from the profile file this machine
    /// wrote - read back off the gate - never from a draft in memory, with the issue form address that files it.
    /// </summary>
    public SharedShareCodeResult GetCalibrationShareCode()
    {
        ProfileSelection selection;
        lock (_gate)
        {
            selection = _selection;
        }

        return SharedShareCodeExport.From(selection, _calibrationServices.SelectTemplate);
    }

    /// <summary>Applies <c>capture.shared_calibration_enabled</c>; off cancels a download and keeps a verified profile.</summary>
    /// <param name="enabled">Setting value.</param>
    public void ApplySharedCalibrationSetting(bool enabled)
    {
        lock (_gate)
        {
            _shared.SetEnabled(enabled);
        }
    }

    /// <summary>The service is stopping: cancel any download and claim nothing more.</summary>
    public void StopSharedCalibration()
    {
        lock (_gate)
        {
            _shared.Stop();
        }
    }

    /// <summary>Completes when no shared-calibration work is in flight.</summary>
    internal Task WhenSharedCalibrationIdleAsync() => _shared.WhenIdleAsync();

    /// <inheritdoc />
    public void OnCaptureHealth(CaptureSessionHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        lock (_gate)
        {
            // Only the running session's readings are kept; the observer merges them to the worst.
            if (_active && SessionMatches(health.CaptureSessionId))
            {
                _calibration.RecordSessionHealth(health);
            }
        }
    }

    /// <summary>
    /// The shared profile recorded its first complete duty: calibration is over, exactly as a confirmed
    /// local profile ends it - unless the profile infers the match, which keeps looking underneath it.
    /// </summary>
    private void FinishSharedRetention(string profileId, bool matchFromQueue)
    {
        if (matchFromQueue)
        {
            SaveCalibrationEvidence();
        }
        else
        {
            ForgetCalibrationEvidence();
        }

        _calibration.MarkDone(profileId, _calibrationBoundAt, matchFromQueue);
        NotifyCalibrationChanged();
    }

    private void UseCalibrationRole(bool upgrading, bool retaining, bool completing = false)
    {
        _calibration.UseProvisional(
            upgrading,
            upgrading ? _selection.Profile?.ProfileId : null,
            // Only a profile this machine wrote can be rewritten by a confirmation here.
            lacking: _selection.Origin != ProfileOrigin.Local || _selection.Profile is not { } profile
                ? null
                : CalibrationCoordinator.Upgradable
                    .Where(name => profile.Message(name) is null)
                    .ToHashSet(StringComparer.Ordinal));
        _calibration.UseRetention(retaining && !upgrading);
        // The messages a completing draft has to reproduce exactly come from the profile in
        // force itself, not from anything this process remembers writing.
        _calibration.UseCompleting(completing, completing ? _selection.Profile?.Messages : null);
    }

    private bool HasFinishedRun(string profileId)
    {
        try
        {
            return _runs.AnyEnteredAndExited(profileId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Unknown counts as not yet proven: the profile stays watched, which is the safe side.
            return false;
        }
    }

    private bool TryUpdateSessionProfile(string sessionId, string? profileId, ProfileStatus status)
    {
        try
        {
            return new CaptureSessionRepository(_database).UpdateProfile(sessionId, profileId, status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands the staged entries to the state machine through the path live events take, in order. What was
    /// staged more than <see cref="FreshMatchAge"/> ago, on the capture source's clock, is replayed rather
    /// than announced (see <see cref="ApplyAndPublish"/>).
    /// </summary>
    private void DrainStaged(SharedCandidateStage stage)
    {
        var processor = _processor!;
        var now = LifecycleMono();
        foreach (var entry in stage.Drain())
        {
            try
            {
                switch (entry.Kind)
                {
                    case StagedEntryKind.Event when entry.Event is { } semanticEvent:
                        ApplyAndPublish(() => processor.Accept(semanticEvent), replayed: now - entry.Mono > FreshMatchAge);
                        break;
                    case StagedEntryKind.EventsDropped:
                        ApplyAndPublish(() => processor.OnEventsDropped(entry.DroppedCount, entry.AtUtc, entry.Mono));
                        break;
                    case StagedEntryKind.ConnectionLost:
                        ApplyAndPublish(() => processor.OnConnectionLost(entry.AtUtc, entry.Mono));
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                // The storage failure is latched on the processor; the next live message faults capture
                // exactly as it would have if the event had arrived live.
                return;
            }
        }
    }

    // ------------------------------------------------------------------ ISharedCalibrationHost

    SharedContext? ISharedCalibrationHost.SharedContext() =>
        _calibration.Armed && _calibration.Template is { } template && _calibration.GameBuild is { } build
            ? new SharedContext(
                new SharedKey(_calibration.Region, build, template.Source.ProfileSha256, _calibration.ArmEpoch),
                template,
                _selection,
                _active && _parser is null && _processor is null ? _sessionId : null)
            : null;

    ProfileSelection ISharedCalibrationHost.SharedSelection() => _selection;

    CalibrationSnapshot? ISharedCalibrationHost.SharedEvidence() => _calibration.Evidence();

    void ISharedCalibrationHost.RegisterSharedCandidate(DeclaredCandidate candidate) => _calibration.RegisterCandidate(candidate);

    void ISharedCalibrationHost.UnregisterSharedCandidate(string candidateId) => _calibration.UnregisterCandidate(candidateId);

    bool ISharedCalibrationHost.HasFinishedSharedRun(string profileId) => HasFinishedRun(profileId);

    /// <inheritdoc />
    bool ISharedCalibrationHost.SharedRunInFlight() =>
        _processor is { } processor && processor.Machine.State is RunState.MentorMatched or RunState.EnteredDuty;

    void ISharedCalibrationHost.SharedCalibrationChanged()
    {
        var signature = CalibrationSignature();
        if (!string.Equals(signature, _calibrationSignature, StringComparison.Ordinal))
        {
            NotifyCalibrationChanged(signature);
        }
    }

    /// <summary>
    /// The one step that turns a verified candidate into recording (plan §4.1 step 5): the reloaded
    /// catalogue must select exactly the written shared profile, the staging must belong to the running
    /// session and be complete, and then the session row, the parser and the drained staging change
    /// together under the gate. Any failed check changes nothing and deletes nothing.
    /// </summary>
    SharedBindResult ISharedCalibrationHost.CommitSharedBind(SharedBindRequest request)
    {
        _select = request.Select;
        var selection = SafeSelect(_game);
        if (!selection.IsUsable || selection.Profile is not { Status: ProfileCompatibilityStatus.Verified } profile ||
            selection.Origin != ProfileOrigin.Shared ||
            !string.Equals(profile.ProfileId, request.ProfileId, StringComparison.Ordinal))
        {
            return new SharedBindResult(SharedBindOutcome.NotSelected, "NOT_SELECTED: " + selection.Reason);
        }

        var bindNow = _active && _parser is null && _processor is null;
        if (bindNow && (request.Stage is not { Overflowed: false } stage || !SessionMatches(stage.CaptureSessionId)))
        {
            // Binding on staging from another session, or on part of this one, would record a partial evening.
            return new SharedBindResult(SharedBindOutcome.SessionChanged, "STAGING_NOT_FOR_THIS_SESSION");
        }

        // A profile the reloaded catalogue outranks is already recording, and the machine is between runs:
        // the parser is rebuilt over the better profile at once, the way a confirmed local calibration that
        // rewrites the profile in force is rebound. Nothing is staged in this case - staging only happens
        // while no parser is bound - and what the machine knows about the player is carried across.
        // The profile was written off the gate, so a run may have begun meanwhile, or the player may be
        // queued on a request the old machine has parked: then the swap is owed, and settled after the first
        // message that leaves the machine between runs (SettleOwedSharedSwap) - in this session, not the next.
        var recordingNow = !bindNow && _active && _processor is not null;
        var swapNow = recordingNow && BetweenRuns(_processor!);

        _selection = selection;
        UseCalibrationRole(upgrading: profile.MatchFromQueue, retaining: true);
        _sharedSwapOwed = null;
        var outcome = SharedBindOutcome.Selected;
        if (swapNow)
        {
            outcome = SwapParser(profile) ? SharedBindOutcome.Bound : SharedBindOutcome.Selected;
        }
        else if (recordingNow)
        {
            _sharedSwapOwed = profile.ProfileId;
        }
        else if (bindNow && _sessionId is { } sessionId && TryUpdateSessionProfile(sessionId, profile.ProfileId, ProfileStatus.Verified))
        {
            // Staged job events replay in order below, so with any staged the machine starts from the previous
            // session's memory. With none - the code arrived after login, or this build's duty bursts do not repeat
            // the job - the job the observer last read from the declared message is the latest known, exactly as
            // when a local calibration is confirmed.
            BindParser(profile, sessionId, request.Stage!.HoldsJob ? _sessionCarried : JobRemembered(profile) ?? _sessionCarried);
            _calibrationBoundAt = _clock.UtcNow;
            DrainStaged(request.Stage!);
            outcome = SharedBindOutcome.Bound;
        }

        // A duty the drained staging finished proves the profile only when nothing was left to audit (plan §18.4);
        // otherwise the session keeps watching and settles the watch once the audit passes.
        var ranComplete = HasFinishedRun(profile.ProfileId);
        var proven = ranComplete && !request.AuditPending;
        if (proven)
        {
            FinishSharedRetention(profile.ProfileId, profile.MatchFromQueue);
        }

        var reason = outcome == SharedBindOutcome.Bound ? "BOUND" : _sharedSwapOwed is null ? "FROM_NEXT_SESSION" : "AFTER_THIS_RUN";
        return new SharedBindResult(outcome, reason, proven, ranComplete);
    }

    /// <summary>
    /// True when replacing the state machine loses nothing: no run is in flight - a terminal state still on
    /// display until the next declared message is a finished run, not one in flight - and no queue request
    /// is parked that the old machine would still turn into a run when the duty loads. The new profile
    /// starts its runs from the server's announcement, which for a parked request may already have gone by.
    /// </summary>
    private bool BetweenRuns(SemanticEventProcessor recording) =>
        recording.Machine.State is not (RunState.MentorMatched or RunState.EnteredDuty) &&
        !recording.Machine.HasParkedQueue(LifecycleMono()) &&
        string.IsNullOrWhiteSpace(recording.LastStorageError);

    /// <summary>
    /// Rebuilds the parser and the state machine over <paramref name="profile"/> inside the running session,
    /// carrying across what the old machine knew about the player. The old machine's terminal state is
    /// collapsed through the ordinary publication path first, so the desktop is told the run is over by the
    /// machine that recorded it. False changes nothing.
    /// </summary>
    private bool SwapParser(ProtocolProfile profile)
    {
        if (_processor is not { } recording || _sessionId is not { } sessionId)
        {
            return false;
        }

        try
        {
            ApplyAndPublish(recording.Machine.NormalizeIfTerminal);
        }
        catch (InvalidOperationException)
        {
            // A latched storage failure: capture is about to fault, and a fresh processor would hide the latch.
            return false;
        }

        if (!TryUpdateSessionProfile(sessionId, profile.ProfileId, ProfileStatus.Verified))
        {
            return false;
        }

        BindParser(profile, sessionId, JobRemembered(profile) ?? recording.Machine.Memory);
        _calibrationBoundAt = _clock.UtcNow;
        return true;
    }

    /// <summary>
    /// A swap that had to wait - a run was under way when the better profile was written, or the player was
    /// queued - goes ahead after the first message that leaves the machine between runs. Called once the
    /// parser is done with that message, never underneath it. An owed swap the selection no longer stands
    /// behind (the profile was withdrawn, or another took its place) is forgotten.
    /// </summary>
    private void SettleOwedSharedSwap()
    {
        if (_sharedSwapOwed is not { } owed)
        {
            return;
        }

        if (!_active || _processor is not { } recording ||
            _selection is not { IsUsable: true, Origin: ProfileOrigin.Shared, Profile: { } profile } ||
            !string.Equals(profile.ProfileId, owed, StringComparison.Ordinal))
        {
            _sharedSwapOwed = null;
            return;
        }

        if (!BetweenRuns(recording))
        {
            return;
        }

        _sharedSwapOwed = null;
        if (SwapParser(profile))
        {
            _shared.OnBoundLater(owed, _calibrationBoundAt ?? _clock.UtcNow);
            NotifyCalibrationChanged();
        }
    }

    /// <summary>
    /// Plan §18.4: a withdrawn profile's records are marked pending review through a system revision, the way
    /// crash recovery marks an unfinished run. The trail stays append-only, a run a human already resolved keeps
    /// that decision (<see cref="ManualRunFieldProtection"/>), and a run already pending is left alone. The
    /// profile id is shared by every code of the build, so <paramref name="sinceUtc"/> limits the marking to
    /// what this binding recorded; a profile adopted from disk has no bound time and marks everything under
    /// the id.
    /// </summary>
    int ISharedCalibrationHost.FlagSharedRecords(string profileId, DateTimeOffset? sinceUtc, string reason) =>
        FlagRecords(profileId, sinceUtc, SharedWithdrawalReason(reason), "其他玩家分享的校准已撤下，");

    /// <summary>
    /// Marks every run a withdrawn profile recorded as pending review. Shared by the two
    /// withdrawals: a shared profile the build's traffic or the public repository disowned, and
    /// a local one this machine's own traffic disproved. The two differ only in what the system
    /// revision says and how the player is told.
    /// </summary>
    /// <param name="profileId">Profile whose records are suspect.</param>
    /// <param name="sinceUtc">Limits the marking to what one binding recorded; null marks everything under the id.</param>
    /// <param name="revisionReason">The reason on each system revision, in the player's words.</param>
    /// <param name="noticePrefix">How the statistics notice opens, before the count.</param>
    private int FlagRecords(string profileId, DateTimeOffset? sinceUtc, string revisionReason, string noticePrefix)
    {
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var flagged = new List<MentorRun>();
        IReadOnlyList<MentorRun> candidates;
        try
        {
            candidates = _database.RunInTransaction(tx => _runs.FindRecordedUnder(profileId, sinceUtc, tx));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The withdrawal itself must go through; the records stay findable by profile id (plan §13).
            return 0;
        }

        // One run per transaction: a manual correction landing on one run at this very moment (a revision
        // conflict) must not keep the others from being marked.
        var revisions = new RunRevisionRepository(_database);
        var protection = new ManualRunFieldProtection(_database);
        foreach (var candidate in candidates)
        {
            try
            {
                var marked = _database.RunInTransaction(tx =>
                {
                    var run = _runs.GetInternal(candidate.RunId, tx);
                    if (run is null || run.PendingReview || run.SoftDeleted)
                    {
                        return null;
                    }

                    var repaired = protection.Merge(run, run with { PendingReview = true, UpdatedAtUtc = now }, tx) with
                    {
                        Revision = run.Revision + 1,
                    };
                    if (!repaired.PendingReview)
                    {
                        return null;
                    }

                    _runs.Update(repaired, run.Revision, tx);
                    revisions.Append(
                        new RunRevision
                        {
                            RevisionId = Guid.NewGuid().ToString("D"),
                            RunId = repaired.RunId,
                            Revision = repaired.Revision,
                            ChangedAtUtc = now,
                            ChangeKind = ChangeKind.Correct,
                            Actor = RevisionActor.System,
                            Reason = revisionReason,
                            RequestId = null,
                            Changes = RunMutationRules.Diff(run, repaired),
                        },
                        tx);
                    return repaired;
                });
                if (marked is not null)
                {
                    flagged.Add(marked);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // This one stays unmarked but findable by profile id; the rest are still tried.
            }
        }

        foreach (var run in flagged)
        {
            _liveEvents.PublishRun(LiveEventKind.RunUpdated, run);
        }

        if (flagged.Count > 0)
        {
            _liveEvents.PublishStatsInvalidated(noticePrefix + $"它生成的 {flagged.Count} 条记录已标记待复核。");
        }

        return flagged.Count;
    }

    /// <summary>The reason on the system revision, in the player's words; the token stays in diagnostics.</summary>
    private static string SharedWithdrawalReason(string reason) => CalibrationWire.RefusalToken(reason) switch
    {
        "REVOKED" => "这条记录由其他玩家分享的校准生成，该校准已在公开仓库里被撤回，记录标记待复核。",
        _ => "这条记录由其他玩家分享的校准生成，该校准随后在本机流量里对不上而被撤下，记录标记待复核。",
    };

    /// <inheritdoc />
    void ISharedCalibrationHost.SharedRetentionFinished(string profileId, bool matchFromQueue) =>
        FinishSharedRetention(profileId, matchFromQueue);

    /// <inheritdoc />
    void ISharedCalibrationHost.UnbindSharedProfile(string profileId) => UnbindProfile(profileId);

    /// <summary>
    /// A withdrawn profile stops recording now, not at the end of the session: a run in flight is
    /// closed the way a stopped capture closes it, and the session row stops naming the profile.
    /// Shared by both withdrawals - a shared profile that was contradicted, revoked or refused,
    /// and a local one this machine's own traffic disproved.
    /// </summary>
    /// <param name="profileId">Profile that must stop recording.</param>
    private void UnbindProfile(string profileId)
    {
        if (string.Equals(_sharedSwapOwed, profileId, StringComparison.Ordinal))
        {
            _sharedSwapOwed = null;
        }

        if (_processor is { } processor && string.Equals(_boundProfileId, profileId, StringComparison.Ordinal))
        {
            try
            {
                ApplyAndPublish(() => processor.OnCaptureStopped(false, _clock.UtcNow, LifecycleMono()));
            }
            catch (InvalidOperationException)
            {
                // A latched storage failure; nothing more can be written through this processor anyway.
            }

            _parser = null;
            _processor = null;
            _boundProfileId = null;
            ForgetPopWatch();
            _runTimer?.Stop();
            _runTimer = null;
            if (_active && _sessionId is { } sessionId)
            {
                TryUpdateSessionProfile(sessionId, null, ProfileStatus.UnsupportedBuild);
            }
        }

        if (string.Equals(_selection.Profile?.ProfileId, profileId, StringComparison.Ordinal))
        {
            // Until the reload lands the build has no usable profile, and says so in the one reason
            // calibration is allowed to act on, so nothing disarms it in between.
            _selection = new ProfileSelection(
                ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null,
                _selection.Region, _selection.GameBuild, ProfileSelector.NoProfileMatchesReason);
        }

        UseCalibrationRole(upgrading: false, retaining: false);
    }

    /// <inheritdoc />
    void ISharedCalibrationHost.ReselectAfterSharedChange(Func<GameProcessDetection, ProfileSelection> select) =>
        ReselectAfterProfileChange(select);

    /// <summary>
    /// After a withdrawn profile's file is gone: adopt the reloaded catalogue and re-arm
    /// calibration, and if the running session is recording nothing and something usable is
    /// selected now, record with it. A profile that becomes unusable must lead to a re-arm.
    /// Shared by both withdrawals, shared and local.
    /// </summary>
    /// <param name="select">Selector over the catalogue as it now stands on disk.</param>
    private void ReselectAfterProfileChange(Func<GameProcessDetection, ProfileSelection> select)
    {
        _select = select;
        if (_active && _processor is not null)
        {
            return;
        }

        _selection = SafeSelect(_game);
        // A shared profile that had already recorded a whole duty finished calibration, and DONE keeps the
        // template, so arming the same build again would be a no-op. With that profile gone it starts over.
        if (_calibration.State == CalibrationState.Done && !_selection.IsUsable)
        {
            _calibration.Disarm();
        }

        ArmCalibration();
        if (!_active || _sessionId is not { } sessionId)
        {
            return;
        }

        if (_calibration.Armed && !_calibration.Active)
        {
            _calibration.Begin(sessionId);
            _calibrationLastDerive = TimeSpan.Zero;
            _calibrationLastSave = TimeSpan.Zero;
        }

        if (_selection.IsUsable && _selection.Profile is { } profile &&
            TryUpdateSessionProfile(sessionId, profile.ProfileId, ProfileStatus.Verified))
        {
            BindParser(profile, sessionId, _sessionCarried);
        }
    }
}
