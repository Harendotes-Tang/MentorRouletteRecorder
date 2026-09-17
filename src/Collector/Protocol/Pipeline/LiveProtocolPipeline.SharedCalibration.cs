using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
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

    private void UseCalibrationRole(bool upgrading, bool retaining)
    {
        _calibration.UseProvisional(upgrading);
        _calibration.UseRetention(retaining && !upgrading);
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

    /// <summary>Hands the staged entries to the state machine through the path live events take, in order.</summary>
    private void DrainStaged(SharedCandidateStage stage)
    {
        var processor = _processor!;
        foreach (var entry in stage.Drain())
        {
            try
            {
                switch (entry.Kind)
                {
                    case StagedEntryKind.Event when entry.Event is { } semanticEvent:
                        ApplyAndPublish(() => processor.Accept(semanticEvent));
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

        _selection = selection;
        UseCalibrationRole(upgrading: profile.MatchFromQueue, retaining: true);
        var outcome = SharedBindOutcome.Selected;
        if (bindNow && _sessionId is { } sessionId && TryUpdateSessionProfile(sessionId, profile.ProfileId, ProfileStatus.Verified))
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

        var proven = HasFinishedRun(profile.ProfileId);
        if (proven)
        {
            FinishSharedRetention(profile.ProfileId, profile.MatchFromQueue);
        }

        return new SharedBindResult(outcome, outcome == SharedBindOutcome.Bound ? "BOUND" : "FROM_NEXT_SESSION", proven);
    }

    /// <summary>
    /// A withdrawn shared profile stops recording now, not at the end of the session: a run in flight is
    /// closed the way a stopped capture closes it, and the session row stops naming the profile.
    /// </summary>
    void ISharedCalibrationHost.UnbindSharedProfile(string profileId)
    {
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

    /// <summary>
    /// After a withdrawn shared profile's file is gone: adopt the reloaded catalogue and re-arm
    /// calibration, and if the running session is recording nothing and something usable is
    /// selected now, record with it. A profile that becomes unusable must lead to a re-arm.
    /// </summary>
    void ISharedCalibrationHost.ReselectAfterSharedChange(Func<GameProcessDetection, ProfileSelection> select)
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
