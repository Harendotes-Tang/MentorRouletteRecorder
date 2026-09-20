using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Pipeline;

// 重新校准: the player asks for the local profile in force to be put away and the build learned
// again from scratch.
//
// Once a local profile binds, the calibration card disappears (IDLE/DONE) and with it both ways
// back in: 清空进度并重新观察 and 导入校准码, which the Collector refuses while nothing is being
// calibrated. A player who suspects their own machine calibrated the wrong message - or who has
// a friend's better code in hand - had only one way out, renaming a file in Explorer. This is
// the way out.
//
// It is the contradiction withdrawal (LiveProtocolPipeline.LocalWithdrawal.cs) minus the
// accusation. The profile stops recording, its file is put away under a different suffix, and
// calibration re-arms inside the running session; but nothing it recorded is marked for review
// and its match message is not refused, because the traffic said nothing against it. The player
// asked, and a player may be wrong about their own profile in either direction.
public sealed partial class LiveProtocolPipeline
{
    /// <summary>
    /// The player asked to calibrate this build again: put the local profile in force away, if
    /// there is one, and then throw the draft away exactly as a plain discard does.
    ///
    /// <paramref name="retireLocalProfile"/> false is the behaviour every caller had before
    /// 重新校准 existed, and remains what the contract's absent field means.
    /// </summary>
    /// <param name="retireLocalProfile">True to also retire a local profile in force.</param>
    /// <param name="restoreLocalProfile">
    /// True to put the retired local profile back instead: the rollback, which is not a discard
    /// at all and therefore keeps the evidence and the shared bookkeeping exactly as they are.
    /// The two flags are opposites; the IPC handler refuses a request carrying both.
    /// </param>
    public CalibrationStatusSnapshot DiscardCalibration(
        bool retireLocalProfile = false, bool restoreLocalProfile = false)
    {
        lock (_gate)
        {
            if (restoreLocalProfile)
            {
                // Nothing of the plain discard runs: the player is putting a calibration back,
                // not asking to forget one. Throws when it cannot be done, so the desktop can
                // print the reason instead of quietly changing nothing.
                RestoreRetiredLocalProfile();
                NotifyCalibrationChanged();
                return _calibration.Snapshot() with
                {
                    Shared = _shared.Snapshot(),
                    RetiredLocalProfileAvailable = RetiredLocalProfileAvailable(),
                };
            }

            if (retireLocalProfile)
            {
                // Before the discard: retiring re-arms calibration for the build, and the discard
                // must then throw away what the re-arm carried in from disk. The other order
                // would leave the previous evening's evidence adopted by the new observer.
                RetireLocalProfileInForce();
            }

            // 重新观察 has to survive a restart, or the next launch would hand the player back
            // exactly what they asked the software to forget.
            ForgetCalibrationEvidence();
            _calibration.Discard(_active ? _sessionId : null);
            // 重新观察 also forgets which shared calibrations this build's traffic contradicted.
            _shared.OnDiscard();
            NotifyCalibrationChanged();
            return _calibration.Snapshot() with
            {
                Shared = _shared.Snapshot(),
                RetiredLocalProfileAvailable = RetiredLocalProfileAvailable(),
            };
        }
    }

    /// <summary>
    /// Whether 恢复上一份本机校准 has anything to offer: a retired profile is on disk for the
    /// running build and no local profile is in force.
    ///
    /// Answered on demand, where the status snapshot is built, rather than cached: it is one
    /// <c>File.Exists</c> per status read, the reads are request-driven (GetCaptureStatus, the
    /// desktop's two-second recording poll, the diagnostics report) and never on the capture
    /// thread's message path, and a cache would have to be invalidated by things outside this
    /// process - a file the player moved back by hand is exactly the case this feature exists to
    /// replace. Never throws; an unreadable directory answers "no rollback".
    /// </summary>
    private bool RetiredLocalProfileAvailable()
    {
        if (_selection is { IsUsable: true, Origin: ProfileOrigin.Local } ||
            RollbackBuild() is not { } target)
        {
            return false;
        }

        try
        {
            return _calibrationServices.HasRetiredLocalProfile(target.Region, target.GameBuild);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    /// <summary>The region and build a rollback would apply to, or null while the build is unknown.</summary>
    private (Region Region, string GameBuild)? RollbackBuild()
    {
        var build = _selection.GameBuild ?? _game.GameBuild;
        return string.IsNullOrWhiteSpace(build) ? null : (_selection.Region, build);
    }

    /// <summary>
    /// 恢复上一份本机校准: the undo of <see cref="RetireLocalProfileInForce"/>.
    ///
    /// Whatever records now - a shared profile the player bound after retiring, a shipped one,
    /// nothing at all - stops, the retired file comes back under its own name, and the reloaded
    /// catalogue selects it because a local profile outranks a shared one. The shared session is
    /// not told to withdraw anything: once the selection is no longer its profile, its own
    /// reconciliation lets the binding go (<c>ReconcileBound</c>), which unregisters the
    /// candidate without marking the code contradicted, without recording a refusal, and without
    /// flagging a single record. Nothing accused that code; it was simply outranked.
    ///
    /// Throws <see cref="CollectorException"/> rather than changing nothing in silence: every
    /// refusal here is something the player asked for and did not get.
    /// </summary>
    private void RestoreRetiredLocalProfile()
    {
        if (RollbackBuild() is not { } target)
        {
            throw new CollectorException(
                ErrorCodes.CalibrationNotReady, "还不知道游戏版本，暂时无法恢复上一份本机校准。");
        }

        var profileId = LocalProfileWriter.ProfileIdFor(target.Region, target.GameBuild);
        var restored = false;
        try
        {
            restored = _calibrationServices.RestoreLocalProfile(target.Region, target.GameBuild);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The seam promises not to throw; a caller that broke that promise still gets an
            // answer the player can read rather than a failed request with no explanation.
            restored = false;
        }

        if (!restored)
        {
            throw new CollectorException(
                ErrorCodes.CalibrationNotReady,
                "没有可以恢复的本机校准：上一份可能已经被新的校准覆盖，或者文件不在了。");
        }

        // Retiring put the id here so a file that could not be renamed was never bound again.
        // The file is back under its own name now, and it is the profile the player asked for.
        _withdrawnLocalProfiles.Remove(profileId);

        // Read the catalogue before anything in force is touched. A directory that cannot be read
        // just now says nothing about the file that came back, so it is no ground for condemning
        // that file, and none for stopping whatever is recording: the restore is undone and the
        // player asks again.
        if (ReloadedSelect() is not { } select)
        {
            UndoRestore(target, profileId);
            throw new CollectorException(
                ErrorCodes.CalibrationNotReady, "暂时读不到校准档案，上一份本机校准还没有换回来，请稍后再试一次。");
        }

        if ((_boundProfileId ?? _selection.Profile?.ProfileId) is { } inForce)
        {
            UnbindProfile(inForce);
        }

        ReselectAfterProfileChange(select);

        if (_selection is { IsUsable: true, Origin: ProfileOrigin.Local, Profile: { } profile } &&
            string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal))
        {
            return;
        }

        // It came back and the loader would not have it - written by a version whose output this
        // one no longer accepts, or damaged on disk. Leaving it in the directory would mean every
        // selection from now on refuses the same file, so it goes back into retirement and the
        // player is told the one thing that helps.
        try
        {
            _calibrationServices.RetireLocalProfile(
                target.Region, target.GameBuild, LocalProfileFiles.RetiredByRequestSuffix);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Then it stays where it is; ReloadedSelect below still refuses it by id.
        }

        _withdrawnLocalProfiles.Add(profileId);
        if (ReloadedSelect() is { } reselect)
        {
            ReselectAfterProfileChange(reselect);
        }

        throw new CollectorException(
            ErrorCodes.CalibrationNotReady, "上一份本机校准已经无法使用，请重新校准。");
    }

    /// <summary>
    /// Puts a profile that was just restored back into retirement, so a restore that could not
    /// be completed leaves the directory as the player found it. A rename that fails leaves the
    /// file under its own name, where the next selection takes it up - which is what was asked
    /// for - so the id is only withheld again when the file really went back.
    /// </summary>
    private void UndoRestore((Region Region, string GameBuild) target, string profileId)
    {
        try
        {
            _calibrationServices.RetireLocalProfile(
                target.Region, target.GameBuild, LocalProfileFiles.RetiredByRequestSuffix);
            if (_calibrationServices.HasRetiredLocalProfile(target.Region, target.GameBuild))
            {
                _withdrawnLocalProfiles.Add(profileId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Then the file stays under its own name and the next selection takes it up.
        }
    }

    /// <summary>
    /// Takes the local profile in force out of use. Called with the gate held, from the IPC
    /// thread rather than the capture thread, so no parser is mid-message; nothing here throws,
    /// because a request the player made must go through whatever the disk answers.
    ///
    /// A selection that is not this machine's own calibration is left exactly as it is: a shipped
    /// profile is not this machine's guess to retract, and a shared one has its own withdrawal
    /// (不用共享的，我自己校准). The request then degrades into the plain discard around it.
    /// </summary>
    private void RetireLocalProfileInForce()
    {
        if (_selection.Origin != ProfileOrigin.Local || _selection.Profile is not { } profile)
        {
            return;
        }

        var region = _selection.Region;
        var gameBuild = _selection.GameBuild ?? _game.GameBuild;
        // A file held open cannot be renamed while the directory still lists it, and the reloaded
        // catalogue would then hand back the very profile the player asked the software to stop
        // using. The same in-process guard the contradiction withdrawal keeps covers that.
        _withdrawnLocalProfiles.Add(profile.ProfileId);

        // Closes a run in flight the way a stopped capture closes it, drops the parser and stops
        // the session row naming the profile.
        UnbindProfile(profile.ProfileId);

        if (!string.IsNullOrWhiteSpace(gameBuild))
        {
            try
            {
                // Renamed, never deleted: a player who retires a working profile by mistake, or
                // who simply wants to see what the software believed, must be able to find it.
                _calibrationServices.RetireLocalProfile(
                    region, gameBuild, LocalProfileFiles.RetiredByRequestSuffix);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A file that cannot be moved is still listed; ReloadedSelect refuses it by id.
                // The retirement itself must still finish.
            }
        }

        if (ReloadedSelect() is { } select)
        {
            ReselectAfterProfileChange(select);
        }
    }
}
