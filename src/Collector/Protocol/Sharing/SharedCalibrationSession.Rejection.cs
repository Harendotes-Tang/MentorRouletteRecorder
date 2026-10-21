using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Sharing;

// SharedCalibrationSession, continued: the player's own refusal of shared calibration for a build.
internal sealed partial class SharedCalibrationSession
{
    /// <summary>
    /// 不用共享的，我自己校准. Cancels a download, drops every candidate, remembers the refusal for the region and
    /// build in the store - apart from the contradiction records - and withdraws the shared profile in force, if
    /// any, exactly as a contradicted one is withdrawn: recording with it stops now, its file is removed off the
    /// gate and calibration re-arms once the catalogue is reloaded. Local calibration is not touched. Until
    /// 重新观察 (<see cref="OnDiscard"/>) nothing shared is fetched, registered, imported or bound for the build.
    /// </summary>
    public SharedRejectResult RejectByUser()
    {
        if (_stopped || RefusalScope() is not { } target)
        {
            return SharedRejectResult.Nothing;
        }

        CancelFetch();
        var dropped = _candidates.Count;
        foreach (var candidate in _candidates.ToArray())
        {
            Drop(candidate, rejected: false);
        }

        _binding = null;
        _userRejection = (target.Region, target.Build, true);
        Attempt(() => _services.SharedCalibrations.RecordUserRejection(target.Region, target.Build, _clock.UtcNow));
        var withdrawn = _bound?.ProfileId;
        if (_bound is not null)
        {
            Supersede(UserRejectedReason, contradicted: false);
        }
        else
        {
            _lastRefusal = UserRejectedReason;
        }

        _host.SharedCalibrationChanged();
        return new SharedRejectResult(withdrawn, dropped);
    }

    /// <summary>
    /// The region and build a refusal is kept for: the shared profile in use, else the build being calibrated, else
    /// the build of the selection in force - so a refusal made while a local profile records is still shown and
    /// still cleared by 重新观察. Null while no build is known.
    /// </summary>
    private (Region Region, string Build)? RefusalScope()
    {
        if (_bound is { } bound)
        {
            return (bound.Region, bound.GameBuild);
        }

        if (_key is { } key)
        {
            return (key.Region, key.GameBuild);
        }

        var selection = _host.SharedSelection();
        return selection.Region is Region.Cn or Region.Global && SharedCalibrationIndex.IsBuild(selection.GameBuild)
            ? (selection.Region, selection.GameBuild!)
            : null;
    }

    /// <summary>
    /// True while the player's refusal stands for this region and build, and while the store cannot say whether it does:
    /// nothing shared is fetched, registered or imported on an unknown (audit 2026-10-20, E-2).
    /// </summary>
    private bool IsUserRejected(Region region, string build) => UserRejection(region, build) != false;

    /// <summary>
    /// The player's refusal for this region and build as the store shows it: read once per build and kept - except an
    /// unknown (null), when the state file cannot be read right now, which is read again next time and never kept.
    /// </summary>
    private bool? UserRejection(Region region, string build)
    {
        if (_userRejection is { } memo && memo.Region == region && string.Equals(memo.Build, build, StringComparison.Ordinal))
        {
            return memo.Rejected;
        }

        if (!SharedCalibrationIndex.IsBuild(build))
        {
            return false;
        }

        var rejected = Attempt(() => _services.SharedCalibrations.IsUserRejected(region, build));
        if (rejected is { } known)
        {
            _userRejection = (region, build, known);
        }

        return rejected;
    }

    /// <summary>
    /// Whether the refusal the store shows stands for the build it would be kept for right now (<see cref="RefusalScope"/>):
    /// what the card says, so an unknown is not shown as the player's refusal.
    /// </summary>
    private bool UserRejectedNow() => RefusalScope() is { } scope && UserRejection(scope.Region, scope.Build) == true;
}
