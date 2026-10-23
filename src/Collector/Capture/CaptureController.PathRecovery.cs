using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Capture;

public sealed partial class CaptureController
{
    private readonly PathRecovery _pathRecovery = new();
    private IDisposable? _networkChangeSubscription;
    private long _networkChangeRevision;
    private CapturePathPlan? _lastDiscoveredPlan;
    private CaptureRun? _recoveryRun;
    private long _recoveryIpcBaseline;
    private bool _replacementStartPending;
    private bool _knownPathsUnavailable;
    private bool _pathReopenPending;
    private long _recoveryCancellation;

    private void SubscribeToNetworkChanges()
    {
        if (!_services.EnableFollowTimer && _services.SubscribeNetworkChanges is null) return;
        try
        {
            Action invalidate = () => Interlocked.Increment(ref _networkChangeRevision);
            _networkChangeSubscription = _services.SubscribeNetworkChanges is { } subscribe
                ? subscribe(invalidate) : new NetworkChangeSubscription(invalidate);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Listening is an optimization. Periodic rediscovery still works; no native
            // exception, adapter identity or address belongs in this diagnostic event.
            _services.Logger.Write(Diagnostics.LogLevel.Warn, "capture", "network_notifications_unavailable");
        }
    }

    /// <summary>Called only with the state gate; keeps the global replacement throttle intact.</summary>
    private void ResetPathRecovery()
    {
        _pathRecovery.Reset(ProcessUptimeMs, Interlocked.Read(ref _networkChangeRevision));
        _lastDiscoveredPlan = null;
        _recoveryRun = null;
        _recoveryIpcBaseline = 0;
        _replacementStartPending = false;
        _knownPathsUnavailable = false;
        _pathReopenPending = false;
        _recoveryCancellation++;
    }

    private void BindPathRecovery(CaptureRun run)
    {
        if (run.AdapterWasExplicit) ResetPathRecovery();
        if (_lastDiscoveredPlan is null)
        {
            _lastDiscoveredPlan = run.PathPlan;
            _pathRecovery.Reset(ProcessUptimeMs, Interlocked.Read(ref _networkChangeRevision));
        }
        _recoveryRun = run;
        _knownPathsUnavailable = false;
        _pathReopenPending = false;
        // A synchronous Start callback may already have decoded IPC. It counts as new
        // recovery evidence, so the new session starts from a zero baseline.
        _recoveryIpcBaseline = 0;
        CompletePathRecoveryIfReadable(run);
    }

    private bool CompletePathRecoveryIfReadable(CaptureRun run)
    {
        if (_state != CaptureControllerState.Running || _replacementStartPending ||
            _knownPathsUnavailable || _pathReopenPending ||
            _pathRecovery.State == PathRecoveryState.None || !ReferenceEquals(run, _recoveryRun) ||
            run.IpcMessagesDecoded <= _recoveryIpcBaseline) return false;
        // Data on an older, still-covered path cannot satisfy a pending expansion. Keep
        // its desired plan until the global replacement throttle permits applying it.
        if (_lastDiscoveredPlan is { } desired && !SamePaths(run.PathPlan, desired)) return false;
        _pathRecovery.Succeeded(ProcessUptimeMs, Interlocked.Read(ref _networkChangeRevision));
        return true;
    }

    /// <summary>
    /// Reads host facts outside both controller gates, then validates the same game and run under
    /// the lifecycle gate before replacing anything. Unchanged plans keep their compression prefix.
    /// </summary>
    private void RediscoverNetworkPaths()
    {
        if (!FollowGameEnabled) return;
        CaptureRun run;
        ICaptureSource? source;
        long generation;
        var now = ProcessUptimeMs;
        lock (_gate)
        {
            if (_disposed || _state != CaptureControllerState.Running ||
                _run is not { AdapterWasExplicit: false } active) return;
            run = active;
            source = _source;
            generation = _generation;
            CompletePathRecoveryIfReadable(run);
            if (!_pathRecovery.ShouldInspect(now, Interlocked.Read(ref _networkChangeRevision))) return;
            _pathRecovery.Inspected(now);
        }

        var game = DetectGame(force: true);
        if (!run.Observes(game)) return;
        CapturePathPlan? next = null;
        var inventoryKnown = false;
        var pathsUnavailable = false;
        try
        {
            var adapters = _services.Adapters.List(game.ProcessId, ReadSettingString(AdapterSetting),
                out inventoryKnown);
            // Missing addresses can mean an individual IP-properties query failed. Only
            // an absent or down interface confirms that an old reader lost its binding.
            pathsUnavailable = inventoryKnown && AllBindingsUnavailable(run.PathPlan, adapters);
            var selected = adapters.FirstOrDefault(a => a.Recommended && CapturePathPlan.IsUsable(a))
                ?? adapters.FirstOrDefault(a => a.Id == run.Adapter?.Id && CapturePathPlan.IsUsable(a));
            if (selected is not null) next = CapturePathPlan.Create(adapters, selected, automatic: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Failed discovery is unknown, never an empty topology that tears down a reader.
        }
        var pathStatus = ReadPathStatus(source);

        lock (_lifecycleGate)
        {
            if (_disposed || !run.Observes(DetectGame(force: true))) return;
            bool replace;
            lock (_gate)
            {
                if (_state != CaptureControllerState.Running || generation != _generation ||
                    !ReferenceEquals(run, _run) || !FollowGameEnabled) return;
                now = ProcessUptimeMs;
                var changed = next is not null && !SamePaths(run.PathPlan, next);
                var newlyDiscovered = next is not null && !SamePaths(_lastDiscoveredPlan, next);
                var pathsLost = inventoryKnown && pathsUnavailable && !_knownPathsUnavailable;
                var pathsReturned = inventoryKnown && !pathsUnavailable && next is not null &&
                    _knownPathsUnavailable;
                if (inventoryKnown && pathsUnavailable) _knownPathsUnavailable = true;
                if (pathsReturned)
                {
                    _knownPathsUnavailable = false;
                    _pathReopenPending = true;
                }
                if (next is not null) _lastDiscoveredPlan = next;
                if (newlyDiscovered || pathsLost || pathsReturned)
                {
                    _pathRecovery.Begin(now, newPath: true);
                    _recoveryRun = run;
                    _recoveryIpcBaseline = run.IpcMessagesDecoded;
                }
                else if (_silentReason != CaptureSilentReason.None)
                {
                    _pathRecovery.Begin(now);
                }

                if (!_pathRecovery.AttemptDue(now)) return;
                // A previously failed optional open may be retried on the same plan; mere
                // silence cannot justify discarding a still-listening decoder repeatedly.
                replace = next is not null && (changed || _pathReopenPending || pathStatus.OpenFailures > 0);
                if (replace && !_pathRecovery.CanReplace(now))
                {
                    _pathRecovery.WaitForReplacement();
                    return;
                }
                _pathRecovery.Attempted(now);
                if (!replace) return;
                _pathRecovery.Replaced(now);
                _replacementStartPending = true;
                _state = CaptureControllerState.Stopping;
            }
            if (TeardownCore(CaptureEndReason.Unknown))
                Publish(Snapshot(), CollectorTexts.CaptureHintPathRecovering().Chinese);
        }
    }

    private static CapturePathStatus ReadPathStatus(ICaptureSource? source)
    {
        try { return source?.PathStatus ?? CapturePathStatus.Unmeasured; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return CapturePathStatus.Unmeasured;
        }
    }

    private static bool AllBindingsUnavailable(CapturePathPlan? plan,
        IReadOnlyList<CaptureAdapterView> adapters) => plan is { Bindings.Count: > 0 } &&
        plan.Bindings.All(binding => !adapters.Any(adapter => adapter.IsUp &&
            string.Equals(CapturePathPlan.NormalizeIdentity(adapter.Id),
                CapturePathPlan.NormalizeIdentity(binding.AdapterId), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A replacement whose start keeps failing must also exhaust its recovery budget. Once
    /// exhausted, bounded discovery can rearm only for a different effective path, a
    /// confirmed path loss and return, or a new game incarnation.
    /// </summary>
    private bool RecoveryAllowsStart(GameProcessDetection game)
    {
        var now = ProcessUptimeMs;
        CapturePathPlan? previous;
        long generation;
        long cancellation;
        lock (_gate)
        {
            if (_recoveryRun is { } last && !last.Observes(game))
            {
                ResetPathRecovery();
                _followRetryAtMs = -1;
                _faultRetryAtMs = -1;
                return true;
            }
            if (_pathRecovery.State == PathRecoveryState.None) return true;
            if (_pathRecovery.State == PathRecoveryState.Recovering)
            {
                // The discovery attempt already paid for the first replacement. Later
                // failed factories/starts consume the same budget before invoking either.
                if (_replacementStartPending)
                {
                    _replacementStartPending = false;
                    _pathRecovery.Replaced(now);
                    return true;
                }
                if (!_pathRecovery.AttemptDue(now) || !_pathRecovery.CanReplace(now)) return false;
                _pathRecovery.Attempted(now);
                _pathRecovery.Replaced(now);
                return true;
            }
            if (!_pathRecovery.ShouldInspect(now, Interlocked.Read(ref _networkChangeRevision))) return false;
            _pathRecovery.Inspected(now);
            previous = _lastDiscoveredPlan;
            generation = _generation;
            cancellation = _recoveryCancellation;
        }
        var adapters = _services.Adapters.List(game.ProcessId, ReadSettingString(AdapterSetting),
            out var inventoryKnown);
        var selected = adapters.FirstOrDefault(a => a.Recommended && CapturePathPlan.IsUsable(a));
        var next = selected is null ? null : CapturePathPlan.Create(adapters, selected, automatic: true);
        var pathsUnavailable = inventoryKnown && AllBindingsUnavailable(previous, adapters);
        lock (_gate)
        {
            if (_disposed || generation != _generation || _state != CaptureControllerState.Idle ||
                cancellation != _recoveryCancellation ||
                _game?.ProcessId != game.ProcessId || _game?.StartedAtUtc != game.StartedAtUtc) return false;
            // A failed factory can leave no running reader. Continue observing confirmed
            // loss/return here too, so the same interface can genuinely become new again.
            var pathsReturned = inventoryKnown && !pathsUnavailable && next is not null &&
                _knownPathsUnavailable;
            if (inventoryKnown && pathsUnavailable) _knownPathsUnavailable = true;
            if (next is null || (SamePaths(previous, next) && !pathsReturned) ||
                !_pathRecovery.CanReplace(now)) return false;
            _knownPathsUnavailable = false;
            _pathReopenPending = pathsReturned;
            _lastDiscoveredPlan = next;
            _pathRecovery.Begin(now, newPath: true);
            _pathRecovery.Attempted(now);
            _pathRecovery.Replaced(now);
            return true;
        }
    }

    private void StartFollowingWithRecovery(GameProcessDetection game)
    {
        long generation;
        long cancellation;
        lock (_gate) { generation = _generation; cancellation = _recoveryCancellation; }
        if (!RecoveryAllowsStart(game)) return;
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_disposed || _state != CaptureControllerState.Idle || generation != _generation ||
                    cancellation != _recoveryCancellation) return;
            }
            if (!FollowGameEnabled) return;
            var current = DetectGame(force: true);
            if (current.ProcessId != game.ProcessId || current.StartedAtUtc != game.StartedAtUtc) return;
            StartOwned(null, null);
        }
    }
}
