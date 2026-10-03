using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Capture;

/// <summary>A transient client identity safe to expose to the desktop.</summary>
public sealed record GameProcessOption(int ProcessId, DateTimeOffset? StartedAtUtc, string Token);

/// <summary>
/// Pins one process incarnation. A sole newly started client from the same installation may
/// continue an exited selection; an existing peer or ambiguous restart never does. Choices
/// expire with their incarnation, and this state is never persisted.
///
/// A process listing that fails is not "the client exited": the pin and the previous answer
/// stand until a listing succeeds (docs/state-machine.md 3.6). A choice is required only while
/// there is a client to choose; with none listed the reason is kept for diagnostics alone.
/// </summary>
internal sealed class GameProcessSelection(GameProcessLocator locator)
{
    private readonly object _gate = new();
    private Dictionary<int, (GameProcessCandidate Candidate, string Token)> _choices = new();
    private GameProcessCandidate? _selected;
    private GameProcessCandidate? _restartFrom;
    private readonly HashSet<(int ProcessId, DateTimeOffset? StartedAtUtc)> _existingPeers = new();
    private bool _restartAmbiguous;
    private bool _restartReplacementsAmbiguous;
    private string _reason = "NONE";
    private GameProcessDetection? _last;

    public GameProcessDetection Refresh()
    {
        lock (_gate) return RefreshCore();
    }

    /// <summary>
    /// The validation workflow explicitly asks for a restart before opening any source.
    /// Only a unique new incarnation of the same installation may satisfy that request;
    /// clients already present when the restart was requested are never replacements.
    /// </summary>
    public GameProcessDetection RefreshAfterValidationRestart(GameProcessDetection previous)
    {
        lock (_gate)
        {
            var current = RefreshCore();
            if (current.Running || _reason != "EXITED" || previous.StartedAtUtc is null ||
                string.IsNullOrWhiteSpace(previous.ExecutablePath)) return current;
            var replacements = _choices.Values.Where(choice =>
                (string.IsNullOrWhiteSpace(choice.Candidate.ExecutablePath) ||
                 string.Equals(choice.Candidate.ExecutablePath, previous.ExecutablePath, StringComparison.OrdinalIgnoreCase)) &&
                !previous.Processes.Any(old => old.ProcessId == choice.Candidate.ProcessId &&
                    old.StartedAtUtc is not null && old.StartedAtUtc == choice.Candidate.StartedAtUtc)).ToArray();
            if (_restartReplacementsAmbiguous || replacements.Length > 1)
            {
                // Once ambiguous, require an explicit choice even if one later disappears.
                _reason = "MULTIPLE";
                return _last = current with { SelectionReason = _reason };
            }
            if (replacements.Length != 1) return current;
            var replacement = replacements[0].Candidate;
            if (replacement.StartedAtUtc is not { } started || started <= previous.StartedAtUtc ||
                string.IsNullOrWhiteSpace(replacement.ExecutablePath)) return current;
            Pin(replacement);
            return RefreshCore();
        }
    }

    public GameProcessCandidate Validate(int processId, string token)
    {
        lock (_gate)
        {
            RefreshCore();
            // The contract's Uuid pattern accepts either letter case; the token is the same.
            if (!_choices.TryGetValue(processId, out var choice) ||
                !string.Equals(choice.Token, token, StringComparison.OrdinalIgnoreCase))
                throw new CollectorException(ErrorCodes.FfxivNotRunning,
                    "所选游戏已退出或重新启动，请重新选择游戏窗口。");
            if (choice.Candidate.StartedAtUtc is null)
                throw new CollectorException(ErrorCodes.FfxivNotRunning,
                    "暂时无法确认该游戏的启动时间，请重新检测后再选择。");
            return choice.Candidate;
        }
    }

    public void Select(int processId, string token)
    {
        lock (_gate)
        {
            Pin(Validate(processId, token));
        }
    }

    /// <summary>
    /// Pins a candidate <see cref="Validate"/> returned earlier, without listing again. Cannot
    /// fail: a caller that has already stopped the old capture must be able to commit the
    /// choice. Should the client have exited meanwhile, the next refresh reports it EXITED
    /// like any selected client that exits, with the other clients as existing peers.
    /// </summary>
    public void Commit(GameProcessCandidate chosen)
    {
        lock (_gate)
        {
            Pin(chosen);
        }
    }

    private GameProcessDetection RefreshCore()
    {
        // A listing that failed says nothing about the clients. Keep the pin and repeat the
        // last answer instead of reading "no game" into it (docs/state-machine.md 3.6).
        if (locator.ListCandidates() is not { } candidates)
            return _last ?? Project(GameProcessDetection.NotRunning, _choices.Count);
        _choices = candidates.ToDictionary(c => c.ProcessId, c =>
            (c, _choices.TryGetValue(c.ProcessId, out var old) && (Same(old.Candidate, c)
                || old.Candidate.StartedAtUtc is null && c.StartedAtUtc is null)
                ? old.Token : Guid.NewGuid().ToString("D")));
        if (_selected is { } selected && !candidates.Any(c => StillSelected(c, selected)))
        {
            _restartFrom = selected;
            _selected = null;
            _reason = "EXITED";
        }
        // A peer whose start time was never readable is known only by its process id; once
        // that id leaves the listing, a later process reusing it is somebody else.
        _existingPeers.RemoveWhere(peer => peer.StartedAtUtc is null &&
            !candidates.Any(candidate => candidate.ProcessId == peer.ProcessId));
        if (_selected is null && candidates.Count == 0)
        {
            // Nothing is left to be ambiguous about. An exited selection keeps the client it
            // came from, so a sole restart of the same installation still continues.
            if (_reason == "MULTIPLE") _reason = "NONE";
            _restartAmbiguous = false;
            _restartReplacementsAmbiguous = false;
            _existingPeers.Clear();
        }
        if (_selected is null && _reason == "EXITED" &&
            _restartFrom is { } exited && candidates.FirstOrDefault(c => Same(c, exited)) is { } same)
        {
            // The very incarnation that was selected is listed again: one listing missed it,
            // it never exited.
            Pin(same);
        }
        if (_selected is null && _reason == "EXITED")
        {
            // Keep the ordinary single-client restart stricter than validation's explicit
            // restart request. Seeing several replacements is sticky even if one later exits.
            _restartAmbiguous |= candidates.Count > 1;
            if (_restartFrom is { ExecutablePath: { } path })
                _restartReplacementsAmbiguous |= candidates.Count(candidate =>
                    !IsExistingPeer(candidate) &&
                    (string.IsNullOrWhiteSpace(candidate.ExecutablePath) ||
                     string.Equals(candidate.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))) > 1;
            if (!_restartAmbiguous && candidates.Count == 1 && _restartFrom is { } previous &&
                previous.StartedAtUtc is { } oldStart && !string.IsNullOrWhiteSpace(previous.ExecutablePath))
            {
                var replacement = candidates[0];
                if (replacement.StartedAtUtc is { } started && started > oldStart &&
                    string.Equals(replacement.ExecutablePath, previous.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    !IsExistingPeer(replacement))
                    Pin(replacement);
            }
        }
        if (_selected is null && _reason is "NONE" or "IDENTITY_UNAVAILABLE")
        {
            if (candidates.Count == 1 && candidates[0].StartedAtUtc is not null)
            {
                Pin(candidates[0]);
            }
            else _reason = candidates.Count > 1 ? "MULTIPLE"
                : candidates.Count == 1 ? "IDENTITY_UNAVAILABLE" : "NONE";
        }
        GameProcessCandidate? observed = null;
        if (_selected is { } pinned)
        {
            // A start time that is momentarily unreadable on the pinned process id does not
            // make it another process: it keeps the identity it was pinned with.
            observed = candidates.First(c => StillSelected(c, pinned)) with { StartedAtUtc = pinned.StartedAtUtc };
            // A client can expose its path only after startup. Remember the last confirmed
            // path for this incarnation without replacing it with a transient unreadable one.
            if (!string.IsNullOrWhiteSpace(observed.ExecutablePath)) _selected = observed;
            RememberPeers(pinned);
        }
        // With nothing selected, only the remembered installation is described: listing again
        // here could find a client this listing did not, and call it running with no choice.
        var game = observed is not null
            ? locator.Describe(observed, candidates.Count)
            : candidates.Count == 0 ? locator.FromRememberedInstall() : GameProcessDetection.NotRunning;
        return _last = Project(game, candidates.Count);
    }

    /// <summary>Adds the selection state to a detection.</summary>
    private GameProcessDetection Project(GameProcessDetection game, int count) => game with
    {
        InstanceCount = count,
        // A choice is asked for only while there is a client to choose.
        SelectionRequired = _reason != "NONE" && _choices.Count > 0,
        SelectionReason = _reason,
        Processes = _choices.Values.Select(c => new GameProcessOption(
            c.Candidate.ProcessId, c.Candidate.StartedAtUtc, c.Token)).ToArray(),
    };

    private void Pin(GameProcessCandidate candidate)
    {
        _selected = candidate;
        _restartFrom = null;
        _restartAmbiguous = false;
        _restartReplacementsAmbiguous = false;
        _existingPeers.Clear();
        _reason = "NONE";
        // The last answer described the previous state; a failed listing must not repeat it.
        _last = null;
        RememberPeers(candidate);
    }

    private bool IsExistingPeer(GameProcessCandidate candidate) =>
        _existingPeers.Contains((candidate.ProcessId, candidate.StartedAtUtc)) ||
        _existingPeers.Contains((candidate.ProcessId, null));

    private void RememberPeers(GameProcessCandidate selected)
    {
        foreach (var (candidate, _) in _choices.Values)
            if (!StillSelected(candidate, selected))
                _existingPeers.Add((candidate.ProcessId, candidate.StartedAtUtc));
    }

    private static bool Same(GameProcessCandidate a, GameProcessCandidate b) =>
        a.ProcessId == b.ProcessId && a.StartedAtUtc is not null && a.StartedAtUtc == b.StartedAtUtc;

    /// <summary>The selected incarnation, or its process id listed with no readable start time.</summary>
    private static bool StillSelected(GameProcessCandidate candidate, GameProcessCandidate selected) =>
        Same(candidate, selected) || candidate.ProcessId == selected.ProcessId && candidate.StartedAtUtc is null;
}
