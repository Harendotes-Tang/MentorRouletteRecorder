using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Speech;

/// <summary>One voice the settings page offers for a service.</summary>
/// <param name="Name">Name sent to the service.</param>
/// <param name="Label">What the settings page shows.</param>
public sealed record SpeechVoiceOption(string Name, string Label);

/// <summary>What <c>GetSpeechSettings</c> reports. There is deliberately no key here, only whether one is stored.</summary>
/// <param name="Config">Settings in force.</param>
/// <param name="HasKey">A key is stored for the settings' target.</param>
/// <param name="TargetHost">Host a sentence would be sent to, or null.</param>
public sealed record SpeechSettingsView(OnlineSpeechConfig Config, bool HasKey, string? TargetHost)
{
    /// <summary>A request could be sent right now, kill switch aside.</summary>
    public bool Configured => Config.IsComplete && HasKey;
}

/// <summary>One <c>SynthesizeSpeech</c> request, validated.</summary>
/// <param name="Text">Prepared sentence.</param>
/// <param name="RatePercent">Rate in percent, 50-200.</param>
/// <param name="Test">The settings page's 测试 button: skips the cache read.</param>
public sealed record SpeechRequest(string Text, int RatePercent, bool Test);

/// <summary>A sentence ready to play.</summary>
/// <param name="AudioPath">Full path of the WAV inside <c>tts-cache\</c>.</param>
/// <param name="FromCache">True when no request was sent.</param>
/// <param name="Provider">Service that produced it.</param>
public sealed record SpeechSynthesis(string AudioPath, bool FromCache, SpeechProvider Provider);

/// <summary>What the sanitized diagnostics report may say about online speech: never where, never the key.</summary>
/// <param name="Provider">Service token in force.</param>
/// <param name="KillSwitch">True when <c>MR_DISABLE_ONLINE_SPEECH</c> is set.</param>
/// <param name="LastRequestAtUtc">When this process last sent a request.</param>
/// <param name="LastOutcome">Error code of that request, or <c>OK</c>.</param>
public sealed record OnlineSpeechDiagnostics(
    string Provider, bool KillSwitch, DateTimeOffset? LastRequestAtUtc, string? LastOutcome)
{
    /// <summary>Nothing configured, nothing sent.</summary>
    public static OnlineSpeechDiagnostics None { get; } = new("none", false, null, null);
}

/// <summary>
/// Online speech as the IPC layer sees it: settings, the key,
/// the cache, and a one-at-a-time request slot in front of <see cref="OnlineSpeechClient"/>.
///
/// Order of a synthesis: the kill switch; the settings and the key; the cache (unless it is a test);
/// a place in the queue - one request in flight and at most <see cref="MaxQueued"/> waiting, each
/// waiting no longer than one request's budget; the cache again, in case the sentence ahead was the
/// same one; the request; the cache write. Every failure is one <c>ERR_SPEECH_*</c> error with no
/// response text in it, and one log line with the error code and the host.
/// </summary>
public sealed class OnlineSpeechService : IDisposable
{
    /// <summary>Sentences allowed to wait behind the one in flight; the next one fails at once.</summary>
    public const int MaxQueued = 3;

    /// <summary>
    /// The zh-CN neural voices the settings page offers for Azure. Names checked against Microsoft's
    /// "Language and voice support for the Speech service" table on 2026-09-16; any other name
    /// matching <c>^[A-Za-z0-9-]{3,64}$</c> is accepted too.
    /// </summary>
    public static IReadOnlyList<SpeechVoiceOption> AzureVoices { get; } = Array.AsReadOnly(new[]
    {
        new SpeechVoiceOption("zh-CN-XiaoxiaoNeural", "晓晓（女声）"),
        new SpeechVoiceOption("zh-CN-YunxiNeural", "云希（男声）"),
        new SpeechVoiceOption("zh-CN-XiaoyiNeural", "晓伊（女声）"),
        new SpeechVoiceOption("zh-CN-YunjianNeural", "云健（男声）"),
        new SpeechVoiceOption("zh-CN-XiaochenNeural", "晓辰（女声）"),
        new SpeechVoiceOption("zh-CN-YunyangNeural", "云扬（男声，新闻）"),
    });

    /// <summary>
    /// The voices the OpenAI speech API has offered since its first model. Other compatible services
    /// name theirs differently, so any name is accepted.
    /// </summary>
    public static IReadOnlyList<SpeechVoiceOption> OpenAiVoices { get; } = Array.AsReadOnly(new[]
    {
        new SpeechVoiceOption("alloy", "alloy"),
        new SpeechVoiceOption("echo", "echo"),
        new SpeechVoiceOption("fable", "fable"),
        new SpeechVoiceOption("onyx", "onyx"),
        new SpeechVoiceOption("nova", "nova"),
        new SpeechVoiceOption("shimmer", "shimmer"),
    });

    private readonly SettingsRepository _settings;
    private readonly SpeechKeyStore _keys;
    private readonly SpeechCache _cache;
    private readonly OnlineSpeechClient _client;
    private readonly Func<RotatingFileLogger> _logger;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private int _admitted;
    private DateTimeOffset? _lastRequestAtUtc;
    private string? _lastOutcome;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">Settings repository.</param>
    /// <param name="keys">Key store.</param>
    /// <param name="cache">Audio cache.</param>
    /// <param name="client">Client; the production client in the shipping Collector.</param>
    /// <param name="logger">The diagnostic log in force, read on every write.</param>
    /// <param name="clock">Clock for the diagnostics stamp.</param>
    public OnlineSpeechService(
        SettingsRepository settings,
        SpeechKeyStore keys,
        SpeechCache cache,
        OnlineSpeechClient client,
        Func<RotatingFileLogger>? logger = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(client);
        _settings = settings;
        _keys = keys;
        _cache = cache;
        _client = client;
        _logger = logger ?? (() => RotatingFileLogger.Disabled);
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The audio cache.</summary>
    public SpeechCache Cache => _cache;

    /// <summary>The key store.</summary>
    public SpeechKeyStore Keys => _keys;

    /// <summary>The settings in force, without the key.</summary>
    public SpeechSettingsView GetSettings() => View(SpeechSettingsStore.Read(_settings));

    /// <summary>
    /// Applies an update. Everything is validated first. A new key is encrypted for the target the
    /// update leads to; changing the target without a new key forgets the old one, so a key is only
    /// ever sent where it was entered for.
    /// </summary>
    /// <param name="update">Requested changes.</param>
    public SpeechSettingsView UpdateSettings(SpeechSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = SpeechSettingsStore.Read(_settings);
        var next = SpeechSettingsStore.Merge(current, update);
        var oldBinding = OnlineSpeechClient.KeyBinding(current);
        var newBinding = OnlineSpeechClient.KeyBinding(next);

        string? newKey = null;
        if (update.ApiKey is { Length: > 0 } supplied)
        {
            if (!SpeechValidation.TryNormalizeKey(supplied, out var trimmed))
            {
                throw CollectorException.BadRequest(
                    "密钥只能包含可见的英文字符，不能有空格，最多 512 个字符。", "payload.api_key");
            }

            if (newBinding is null)
            {
                throw CollectorException.BadRequest(
                    "请先选择在线语音服务并填写区域或接口地址，再填写密钥。", "payload.api_key");
            }

            newKey = trimmed;
        }

        try
        {
            if (newKey is not null)
            {
                _keys.Write(newBinding!, newKey);
            }
            else if (update.ApiKey is { Length: 0 })
            {
                _keys.Clear();
            }

            var stored = SpeechSettingsStore.Write(_settings, next);
            if (update.ApiKey is null && !string.Equals(oldBinding, newBinding, StringComparison.Ordinal))
            {
                _keys.Clear();
            }

            var view = View(stored);
            _logger().Write(LogLevel.Info, "speech", "speech_settings_updated", new Dictionary<string, object?>
            {
                ["provider"] = SpeechProviderWire.Format(stored.Provider),
                ["host"] = view.TargetHost,
                ["has_key"] = view.HasKey,
            });
            return view;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException)
        {
            _logger().Write(LogLevel.Error, "speech", "speech_key_write_failed", new Dictionary<string, object?>
            {
                ["error_type"] = ex.GetType().Name,
            });
            throw new CollectorException(ErrorCodes.Internal, "无法在本机保存语音服务密钥，请检查数据目录的写入权限。");
        }
    }

    /// <summary>
    /// Speaks one sentence into a file. Throws <see cref="CollectorException"/> with an
    /// <c>ERR_SPEECH_*</c> code on every failure.
    /// </summary>
    /// <param name="request">Validated request.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    public async Task<SpeechSynthesis> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client.IsDisabledNow)
        {
            throw Failure(SpeechOutcome.Disabled, null);
        }

        var config = SpeechSettingsStore.Read(_settings);
        var binding = OnlineSpeechClient.KeyBinding(config);
        if (!config.IsComplete || !_keys.Has(binding))
        {
            throw Failure(SpeechOutcome.NotConfigured, null);
        }

        var cacheKey = SpeechCache.KeyFor(config.Provider, config.Voice!, request.RatePercent, request.Text);
        if (!request.Test && _cache.TryGet(cacheKey) is { } cached)
        {
            return new SpeechSynthesis(cached, true, config.Provider);
        }

        if (Interlocked.Increment(ref _admitted) > MaxQueued + 1)
        {
            Interlocked.Decrement(ref _admitted);
            throw Failure(SpeechOutcome.Timeout, null, reason: "QUEUE_FULL");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            bool entered;
            try
            {
                entered = await _slot.WaitAsync(_client.RequestTimeout, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw Failure(SpeechOutcome.Cancelled, null);
            }

            if (!entered)
            {
                throw Failure(SpeechOutcome.Timeout, null, reason: "QUEUE_WAIT");
            }

            try
            {
                if (!request.Test && _cache.TryGet(cacheKey) is { } filled)
                {
                    return new SpeechSynthesis(filled, true, config.Provider);
                }

                // The key is read at the last moment and only for the binding in force, so a
                // settings change while this sentence waited cannot send a key somewhere else.
                var result = await _client
                    .SynthesizeAsync(config, _keys.Read(binding), request.Text, request.RatePercent, linked.Token)
                    .ConfigureAwait(false);
                Record(result);
                if (result.Outcome != SpeechOutcome.Ok || result.Audio is null)
                {
                    throw Failure(result.Outcome, result);
                }

                try
                {
                    return new SpeechSynthesis(_cache.Store(cacheKey, result.Audio), false, config.Provider);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger().Write(LogLevel.Error, "speech", "speech_cache_write_failed", new Dictionary<string, object?>
                    {
                        ["error_type"] = ex.GetType().Name,
                    });
                    throw Failure(SpeechOutcome.Format, result, reason: "CACHE_WRITE");
                }
            }
            finally
            {
                _slot.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _admitted);
        }
    }

    /// <summary>What the diagnostics report may say.</summary>
    public OnlineSpeechDiagnostics Diagnostics()
    {
        var provider = SpeechProviderWire.Format(SpeechSettingsStore.Read(_settings).Provider);
        lock (_gate)
        {
            return new OnlineSpeechDiagnostics(provider, _client.IsDisabledNow, _lastRequestAtUtc, _lastOutcome);
        }
    }

    /// <summary>The error code for an outcome.</summary>
    /// <param name="outcome">Outcome other than Ok.</param>
    public static string ErrorCodeFor(SpeechOutcome outcome) => outcome switch
    {
        SpeechOutcome.Disabled => ErrorCodes.SpeechDisabled,
        SpeechOutcome.NotConfigured => ErrorCodes.SpeechNotConfigured,
        SpeechOutcome.Auth => ErrorCodes.SpeechAuth,
        SpeechOutcome.Quota => ErrorCodes.SpeechQuota,
        SpeechOutcome.Network => ErrorCodes.SpeechNetwork,
        SpeechOutcome.Timeout or SpeechOutcome.Cancelled => ErrorCodes.SpeechTimeout,
        SpeechOutcome.Format => ErrorCodes.SpeechFormat,
        _ => ErrorCodes.Internal,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private SpeechSettingsView View(OnlineSpeechConfig config) =>
        new(config, _keys.Has(OnlineSpeechClient.KeyBinding(config)), OnlineSpeechClient.TargetHost(config));

    private void Record(SpeechSynthesisResult result)
    {
        if (result.Host is null)
        {
            // Stopped before anything was sent (the kill switch flipped, or the key went away
            // while the sentence waited): not a request, so neither stamped nor logged as one.
            return;
        }

        var code = result.Outcome == SpeechOutcome.Ok ? "OK" : ErrorCodeFor(result.Outcome);
        lock (_gate)
        {
            _lastRequestAtUtc = _clock.UtcNow;
            _lastOutcome = code;
        }

        // Error code, host and status only: never the address, the body, the headers or the text.
        _logger().Write(
            result.Outcome == SpeechOutcome.Ok ? LogLevel.Info : LogLevel.Warn,
            "speech",
            "speech_request",
            new Dictionary<string, object?>
            {
                ["result"] = code,
                ["host"] = result.Host,
                ["http_status"] = result.HttpStatus,
                ["reason"] = result.Reason,
            });
    }

    private static CollectorException Failure(SpeechOutcome outcome, SpeechSynthesisResult? result, string? reason = null)
    {
        var details = new Dictionary<string, object?>();
        if (result?.HttpStatus is { } status && outcome != SpeechOutcome.Ok)
        {
            details["http_status"] = status;
        }

        if ((reason ?? result?.Reason) is { } token)
        {
            details["reason"] = token;
        }

        if (outcome == SpeechOutcome.Cancelled)
        {
            details["reason"] = "CANCELLED";
        }

        var message = outcome switch
        {
            SpeechOutcome.Disabled => "在线语音已被环境变量 MR_DISABLE_ONLINE_SPEECH 关闭。",
            SpeechOutcome.NotConfigured => "在线语音还没有配置好：请选择服务，填写区域或接口地址、音色和密钥。",
            SpeechOutcome.Auth => "语音服务拒绝了密钥，请检查密钥与区域是否匹配。",
            SpeechOutcome.Quota => "语音服务的额度或频率用完了，请稍后再试。",
            SpeechOutcome.Network => "连不上语音服务，这一句改用本机语音。",
            SpeechOutcome.Timeout when reason is "QUEUE_FULL" or "QUEUE_WAIT" => "排队播报的句子太多，这一句改用本机语音。",
            SpeechOutcome.Timeout or SpeechOutcome.Cancelled => "语音服务响应超时，这一句改用本机语音。",
            SpeechOutcome.Format => "语音服务返回的不是可播放的 WAV 音频。",
            _ => "在线语音出错。",
        };
        return new CollectorException(
            ErrorCodeFor(outcome), message, details.Count == 0 ? null : details,
            retryable: outcome is SpeechOutcome.Network or SpeechOutcome.Timeout or SpeechOutcome.Quota);
    }
}
