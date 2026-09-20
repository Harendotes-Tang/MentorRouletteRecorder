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
    public CalibrationStatusSnapshot DiscardCalibration(bool retireLocalProfile = false)
    {
        lock (_gate)
        {
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
            return _calibration.Snapshot() with { Shared = _shared.Snapshot() };
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
