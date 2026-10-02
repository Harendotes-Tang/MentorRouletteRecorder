using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Capture;

/// <summary>A transient client identity safe to expose to the desktop.</summary>
public sealed record GameProcessOption(int ProcessId, DateTimeOffset? StartedAtUtc, string Token);

/// <summary>
/// Pins one process incarnation. A sole newly started client from the same installation may
/// continue an exited selection; an existing peer or ambiguous restart never does. Choices
/// expire with their incarnation, and this state is never persisted.
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
                return current with { SelectionReason = _reason };
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
            if (!_choices.TryGetValue(processId, out var choice) || choice.Token != token)
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

    private GameProcessDetection RefreshCore()
    {
        var candidates = locator.ListCandidates();
        _choices = candidates.ToDictionary(c => c.ProcessId, c =>
            (c, _choices.TryGetValue(c.ProcessId, out var old) && (Same(old.Candidate, c)
                || old.Candidate.StartedAtUtc is null && c.StartedAtUtc is null)
                ? old.Token : Guid.NewGuid().ToString("D")));
        if (_selected is { } selected && !candidates.Any(c => Same(c, selected)))
        {
            _restartFrom = selected;
            _selected = null;
            _reason = "EXITED";
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
        if (_selected is { } pinned)
        {
            var observed = candidates.First(c => Same(c, pinned));
            // A client can expose its path only after startup. Remember the last confirmed
            // path for this incarnation without replacing it with a transient unreadable one.
            if (!string.IsNullOrWhiteSpace(observed.ExecutablePath)) _selected = observed;
            RememberPeers(pinned);
        }
        var game = _selected is { } current
            ? locator.Describe(candidates.First(c => Same(c, current)), candidates.Count)
            : candidates.Count == 0 ? locator.Locate() : GameProcessDetection.NotRunning;
        return game with
        {
            InstanceCount = candidates.Count,
            SelectionRequired = _reason != "NONE",
            SelectionReason = _reason,
            Processes = _choices.Values.Select(c => new GameProcessOption(
                c.Candidate.ProcessId, c.Candidate.StartedAtUtc, c.Token)).ToArray(),
        };
    }

    private void Pin(GameProcessCandidate candidate)
    {
        _selected = candidate;
        _restartFrom = null;
        _restartAmbiguous = false;
        _restartReplacementsAmbiguous = false;
        _existingPeers.Clear();
        _reason = "NONE";
        RememberPeers(candidate);
    }

    private bool IsExistingPeer(GameProcessCandidate candidate) =>
        _existingPeers.Contains((candidate.ProcessId, candidate.StartedAtUtc)) ||
        _existingPeers.Contains((candidate.ProcessId, null));

    private void RememberPeers(GameProcessCandidate selected)
    {
        foreach (var (candidate, _) in _choices.Values)
            if (!Same(candidate, selected))
                _existingPeers.Add((candidate.ProcessId, candidate.StartedAtUtc));
    }

    private static bool Same(GameProcessCandidate a, GameProcessCandidate b) =>
        a.ProcessId == b.ProcessId && a.StartedAtUtc is not null && a.StartedAtUtc == b.StartedAtUtc;
}
