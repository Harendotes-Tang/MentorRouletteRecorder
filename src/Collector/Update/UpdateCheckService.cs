using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// The update check as the IPC layer sees it: a cached answer, and a request sent at most once a
/// day in the background (docs/privacy-boundary.md §8.4).
///
/// Poll-driven rather than timer-driven. <see cref="Observe"/> is called from <c>GetStatus</c>, it
/// returns the cached state immediately and, when a check is due, schedules exactly one in the
/// background; a Collector nobody is looking at sends nothing. "Due" means no check has ever been
/// recorded, the recorded time lies in the future (a clock that was wrong or has been moved back),
/// or <see cref="CheckInterval"/> plus this process's <see cref="Jitter"/> has passed - the jitter
/// so that a thousand installs started by the same patch do not all ask at the same second.
///
/// Every attempt is stamped, successful or not, so a host that is down cannot turn into a request
/// on every status frame; the version last learned survives a failure and a restart. Notification
/// only: nothing is downloaded but the metadata document, and nothing is ever executed.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    /// <summary>Whether the check may run at all. On by default.</summary>
    public const string EnabledSetting = "update.check_enabled";

    /// <summary>When a check was last attempted, successfully or not.</summary>
    public const string LastCheckedSetting = "update.last_checked_at_utc";

    /// <summary>Newest published version this installation has learned of.</summary>
    public const string LatestVersionSetting = "update.latest_version";

    /// <summary>How the last attempt ended, as an <see cref="UpdateCheckOutcome"/> token.</summary>
    public const string LastOutcomeSetting = "update.last_outcome";

    /// <summary>Shortest time between two checks, before the jitter is added.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Exclusive upper bound of the per-process jitter.</summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromHours(2);

    /// <summary>
    /// A fresh process checks once at startup unless the last persisted attempt is younger than this: a
    /// user who restarts the software expects it to look, while a crash loop must not turn into a request
    /// per restart.
    /// </summary>
    public static readonly TimeSpan RestartGrace = TimeSpan.FromHours(1);

    /// <summary>How long <see cref="Dispose"/> waits for the check in flight to finish.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsRepository _settings;
    private readonly UpdateCheckClient _client;
    private readonly UpdateVersion? _local;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private DateTimeOffset? _lastCheckedAtUtc;
    private string? _latestVersion;
    private string? _lastOutcome;
    private bool _checkedThisProcess;
    private volatile bool _enabled;
    private bool _inFlight;
    private Task? _pending;
    private bool _disposed;

    /// <summary>Creates the service and reads the state a previous run left behind.</summary>
    /// <param name="settings">Settings repository; the only thing this service writes to.</param>
    /// <param name="client">Check client; the production one in the shipping Collector.</param>
    /// <param name="localVersion">This build's version stamp; an unreadable one means nothing is ever newer.</param>
    /// <param name="clock">Clock used to decide what is due and to stamp attempts.</param>
    /// <param name="jitter">
    /// Draws this process's jitter once; clamped into <c>[0, <see cref="MaxJitter"/>)</c>. A random
    /// draw when null.
    /// </param>
    public UpdateCheckService(
        SettingsRepository settings,
        UpdateCheckClient client,
        string localVersion,
        IClock clock,
        Func<TimeSpan>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);

        _settings = settings;
        _client = client;
        _clock = clock;
        _local = UpdateVersion.TryParseLocal(localVersion, out var parsed) ? parsed : null;
        Jitter = Clamp(jitter is null ? Random.Shared.NextDouble() * MaxJitter : jitter());

        _enabled = ReadBool(EnabledSetting) ?? true;
        _lastCheckedAtUtc = UtcTimestamp.TryParse(ReadString(LastCheckedSetting), out var lastChecked)
            ? lastChecked
            : null;
        _latestVersion = ReadString(LatestVersionSetting);
        _lastOutcome = ReadString(LastOutcomeSetting);
    }

    /// <summary>This process's jitter, drawn once at startup and constant afterwards.</summary>
    public TimeSpan Jitter { get; }

    /// <summary>
    /// The check in flight - scheduled or asked for - or the last one that ran; null until the
    /// first is claimed. Claim and publication happen in one locked step, so a caller that loses
    /// the claim always finds the task belonging to the check that won it.
    /// </summary>
    internal Task? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>
    /// The state <c>GetStatus</c> reports, answered from the cache, and a check scheduled when one is
    /// due. Never waits on a request.
    /// </summary>
    public UpdateCheckSnapshot Observe()
    {
        Schedule();
        return Snapshot();
    }

    /// <summary>The state as it stands. Reads nothing and sends nothing.</summary>
    public UpdateCheckSnapshot Snapshot()
    {
        DateTimeOffset? lastChecked;
        string? latest;
        string? outcome;
        lock (_gate)
        {
            lastChecked = _lastCheckedAtUtc;
            latest = _latestVersion;
            outcome = _lastOutcome;
        }

        return new UpdateCheckSnapshot(
            _enabled, IsNewer(latest), latest, lastChecked, outcome, UpdateCheckClient.ReleaseUrl);
    }

    /// <summary>What the sanitized diagnostics report may state. Never an address.</summary>
    public UpdateCheckDiagnostics Diagnostics()
    {
        var snapshot = Snapshot();
        return new UpdateCheckDiagnostics(
            snapshot.Enabled,
            _client.IsDisabledNow,
            snapshot.LastCheckedAtUtc,
            snapshot.LastOutcome,
            snapshot.LatestVersion);
    }

    /// <summary>
    /// Pushes <c>update.check_enabled</c> at the service, so that "saved" and "in force" are the same
    /// moment. The value itself is written by <c>CaptureSettingsStore</c>.
    /// </summary>
    /// <param name="enabled">Whether checks may run.</param>
    public void ApplySetting(bool enabled) => _enabled = enabled;

    /// <summary>
    /// Runs one check now, whatever is due, and records it. The seam tests drive; the shipping paths are
    /// <see cref="Observe"/> and <see cref="CheckNowIfAllowedAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops the check.</param>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        Record(result);
        return result;
    }

    /// <summary>
    /// 立即检查 (<c>CheckUpdateNow</c>): the user asked, so the daily throttle does not apply; the setting and
    /// the kill switch still do, and nothing is sent under either. A check already in flight, scheduled or
    /// asked for, is waited for rather than doubled. Returns once the cache holds the answer.
    /// </summary>
    /// <param name="cancellationToken">The connection's token.</param>
    public async Task<UpdateCheckRequestOutcome> CheckNowIfAllowedAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _client.IsDisabledNow)
        {
            return UpdateCheckRequestOutcome.Blocked;
        }

        if (!_enabled)
        {
            return UpdateCheckRequestOutcome.Disabled;
        }

        if (TryClaim() is not { } claim)
        {
            if (Pending is { } pending)
            {
                try
                {
                    await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // The in-flight check records its own outcome; the cache is what the caller reads.
                }
            }

            return UpdateCheckRequestOutcome.Checked;
        }

        try
        {
            lock (_gate)
            {
                _checkedThisProcess = true;
            }

            await CheckNowAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(claim);
        }

        return UpdateCheckRequestOutcome.Checked;
    }

    /// <summary>
    /// Stops scheduling and waits for the check in flight, so nothing writes to the database after
    /// the host has started closing it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        try
        {
            _pending?.Wait(StopTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The background task swallows its own failures; a wait that faults anyway must not
            // stop the rest of the shutdown.
        }

        _stopping.Dispose();
    }

    private void Schedule()
    {
        if (_disposed || !_enabled || !IsDue(_clock.UtcNow))
        {
            return;
        }

        if (TryClaim() is not { } claim)
        {
            return;
        }

        lock (_gate)
        {
            _checkedThisProcess = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckNowAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A check is a background convenience: a settings write that fails because the host
                // is shutting down must not surface as an unobserved task exception.
            }
            finally
            {
                Release(claim);
            }
        });
    }

    // Claiming the one check that may be in flight and publishing the task other callers wait on
    // are the same locked step. While they were two steps (2026-09-21 full audit, finding 22), a
    // request that arrived in between found the claim taken and _pending still holding the
    // previous check - null on the very first one - so it reported "checked" without waiting for
    // the check just claimed, and the snapshot it went on to read was the one from before it.
    // Null when a check is already claimed: that caller waits on Pending instead.
    private TaskCompletionSource? TryClaim()
    {
        lock (_gate)
        {
            if (_inFlight)
            {
                return null;
            }

            _inFlight = true;
            var claim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = claim.Task;
            return claim;
        }
    }

    // Frees the claim first, then wakes the waiters, so a waiter resumes with the outcome already
    // recorded - the only reason it waited. The claim never carries a failure: the check that took
    // it swallows and records its own, and a waiter reads the cache rather than a result.
    private void Release(TaskCompletionSource claim)
    {
        lock (_gate)
        {
            _inFlight = false;
        }

        claim.TrySetResult();
    }

    private bool IsDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastCheckedAtUtc is not { } last || last > now)
            {
                return true;
            }

            // Once per process start, past the restart grace; then once a day while it runs.
            return (!_checkedThisProcess && now - last >= RestartGrace) || now - last >= CheckInterval + Jitter;
        }
    }

    private void Record(UpdateCheckResult result)
    {
        if (result.Outcome == UpdateCheckOutcome.Cancelled)
        {
            // A check abandoned because the process is closing is not an attempt: stamping it would
            // cost the user a day's checking, and the write would race the database closing anyway.
            return;
        }

        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        lock (_gate)
        {
            _lastCheckedAtUtc = now;
            _lastOutcome = EnumWire<UpdateCheckOutcome>.Format(result.Outcome);
            if (result.LatestVersion is { } version)
            {
                _latestVersion = version;
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LastCheckedSetting] = Text(UtcTimestamp.ToText(now)),
            [LastOutcomeSetting] = Text(EnumWire<UpdateCheckOutcome>.Format(result.Outcome)),
        };

        // A failed check keeps whatever version the last successful one learned: the user is not
        // told the update vanished because a server was down.
        if (result.LatestVersion is { } latest)
        {
            values[LatestVersionSetting] = Text(latest);
        }

        _settings.SetSettings(values, _ => 0);
    }

    private bool IsNewer(string? latest) =>
        _local is { } running && UpdateVersion.TryParse(latest, out var published) &&
        UpdateVersion.IsNewer(published, running);

    private static TimeSpan Clamp(TimeSpan jitter) =>
        jitter <= TimeSpan.Zero ? TimeSpan.Zero
        : jitter >= MaxJitter ? MaxJitter - TimeSpan.FromTicks(1)
        : jitter;

    private static string Text(string value) => JsonValue.Create(value)!.ToJsonString();

    private JsonNode? ReadSetting(string key)
    {
        try
        {
            var raw = _settings.GetSetting(key);
            return raw is null ? null : JsonNode.Parse(raw);
        }
        catch (Exception ex) when (ex is JsonException or Contracts.Errors.CollectorException)
        {
            return null;
        }
    }

    private bool? ReadBool(string key) =>
        ReadSetting(key) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private string? ReadString(string key) =>
        ReadSetting(key) is JsonValue value && value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
