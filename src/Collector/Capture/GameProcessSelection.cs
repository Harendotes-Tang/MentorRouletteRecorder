using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Capture;

/// <summary>A transient client identity safe to expose to the desktop.</summary>
public sealed record GameProcessOption(int ProcessId, DateTimeOffset? StartedAtUtc, string Token);

/// <summary>
/// Pins one process incarnation. Choices expire when a client exits or its creation time
/// changes; a missing creation time never grants ownership. This state is never persisted.
/// </summary>
internal sealed class GameProcessSelection(GameProcessLocator locator)
{
    private readonly object _gate = new();
    private Dictionary<int, (GameProcessCandidate Candidate, string Token)> _choices = new();
    private GameProcessCandidate? _selected;
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
            if (replacements.Length > 1)
            {
                // Once ambiguous, require an explicit choice even if one later disappears.
                _reason = "MULTIPLE";
                return current with { SelectionReason = _reason };
            }
            if (replacements.Length != 1) return current;
            var replacement = replacements[0].Candidate;
            if (replacement.StartedAtUtc is not { } started || started <= previous.StartedAtUtc ||
                string.IsNullOrWhiteSpace(replacement.ExecutablePath)) return current;
            _selected = replacement;
            _reason = "NONE";
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
            _selected = Validate(processId, token);
            _reason = "NONE";
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
            _selected = null;
            _reason = "EXITED";
        }
        if (_selected is null && _reason is "NONE" or "IDENTITY_UNAVAILABLE")
        {
            if (candidates.Count == 1 && candidates[0].StartedAtUtc is not null)
            {
                _selected = candidates[0];
                _reason = "NONE";
            }
            else _reason = candidates.Count > 1 ? "MULTIPLE"
                : candidates.Count == 1 ? "IDENTITY_UNAVAILABLE" : "NONE";
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

    private static bool Same(GameProcessCandidate a, GameProcessCandidate b) =>
        a.ProcessId == b.ProcessId && a.StartedAtUtc is not null && a.StartedAtUtc == b.StartedAtUtc;
}
