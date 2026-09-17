using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The audio cache, the DPAPI key store and the service in front of the client: settings round trips,
/// key binding, the order of the checks, the one-at-a-time slot and what reaches the log.
/// </summary>
public sealed class OnlineSpeechServiceTests : IDisposable
{
    private const string Text = "副本结算，距离目标还差 11 次";

    private readonly TestDatabase _database = new();
    private readonly string _directory;

    public OnlineSpeechServiceTests()
    {
        _directory = Path.GetDirectoryName(_database.Path)!;
    }

    public void Dispose() => _database.Dispose();

    private SettingsRepository Settings => new(_database.Database, _database.Clock);

    private OnlineSpeechService Service(
        FakeSpeechTransport transport, TimeSpan? timeout = null, string? killSwitch = null, RotatingFileLogger? logger = null) =>
        new(
            Settings,
            new SpeechKeyStore(_directory),
            new SpeechCache(_directory, _database.Clock),
            SpeechFixtures.Client(transport, timeout, killSwitch),
            logger is null ? null : () => logger,
            _database.Clock);

    private static SpeechSettingsUpdate AzureUpdate(string? key = SpeechFixtures.Key) => new()
    {
        Provider = SpeechProvider.Azure,
        AzureRegion = " EastAsia ",
        AzureRegionSpecified = true,
        Voice = "zh-CN-XiaoxiaoNeural",
        VoiceSpecified = true,
        ApiKey = key,
    };

    // ----------------------------------------------------------------------- DPAPI

    [Fact]
    public void DpapiRoundTripsForTheCurrentUserAndRefusesATamperedBlob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI exists only on Windows; the Collector targets nothing else.
        }

        var secret = Encoding.UTF8.GetBytes("dpapi-round-trip-" + Guid.NewGuid().ToString("N"));
        var blob = DpapiProtector.Instance.Protect(secret);

        Assert.NotEqual(secret, blob);
        Assert.Equal(-1, blob.AsSpan().IndexOf(secret));
        Assert.Equal(secret, DpapiProtector.Instance.Unprotect(blob));
        Assert.NotEqual(blob, DpapiProtector.Instance.Protect(secret));

        var tampered = blob.ToArray();
        tampered[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => DpapiProtector.Instance.Unprotect(tampered));
        Assert.Throws<CryptographicException>(() => DpapiProtector.Instance.Unprotect(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TheKeyFileHoldsOnlyCiphertextAndReadsBackOnlyForItsBinding()
    {
        var store = new SpeechKeyStore(_directory);
        Assert.Null(store.Read("azure:x"));
        Assert.False(store.Clear());

        store.Write("azure:x", SpeechFixtures.Key);
        Assert.Equal(Path.Combine(_directory, "speech-key.bin"), store.FilePath);
        var raw = File.ReadAllBytes(store.FilePath);
        Assert.DoesNotContain(SpeechFixtures.Key, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain(SpeechFixtures.Key, Encoding.Unicode.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("azure:x", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        Assert.Equal(SpeechFixtures.Key, store.Read("azure:x"));
        Assert.Null(store.Read("azure:y"));
        Assert.Null(store.Read(null));
        Assert.True(store.Has("azure:x"));
        Assert.DoesNotContain(SpeechFixtures.Key, store.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "speech-key.bin.*.tmp"));

        store.Write("azure:x", "replacement-key");
        Assert.Equal("replacement-key", store.Read("azure:x"));

        File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3, 4 });
        Assert.Null(store.Read("azure:x"));

        Assert.True(store.Clear());
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Read("azure:x"));
    }

    [Fact]
    public void AKeyStoreThatCannotDecryptReportsNoKey()
    {
        var store = new SpeechKeyStore(_directory, new BrokenProtector());
        File.WriteAllBytes(store.FilePath, new byte[] { 9 });
        Assert.Null(store.Read("azure:x"));
    }

    // ----------------------------------------------------------------------- cache

    [Fact]
    public void TheCacheKeyIsTheHashOfProviderVoiceRateAndText()
    {
        var key = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "t");
        Assert.Equal(64, key.Length);
        Assert.All(key, ch => Assert.True(char.IsAsciiHexDigitLower(ch)));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("azure|v|100|t"))).ToLowerInvariant(), key);
        Assert.Equal(key, SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "t"));
        Assert.Equal(5, new[]
        {
            key,
            SpeechCache.KeyFor(SpeechProvider.OpenAiCompatible, "v", 100, "t"),
            SpeechCache.KeyFor(SpeechProvider.Azure, "w", 100, "t"),
            SpeechCache.KeyFor(SpeechProvider.Azure, "v", 101, "t"),
            SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "u"),
        }.Distinct().Count());
    }

    [Fact]
    public void AStoredFileIsFoundWrittenWholeAndInsideTheCacheFolder()
    {
        var cache = new SpeechCache(_directory, _database.Clock);
        var key = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "t");
        Assert.Null(cache.TryGet(key));

        var path = cache.Store(key, WaveFile.Silence());
        Assert.Equal(Path.Combine(_directory, "tts-cache", key + ".wav"), path);
        Assert.Equal(WaveFile.Silence(), File.ReadAllBytes(path));
        Assert.Equal(path, cache.TryGet(key));
        Assert.True(cache.Contains(path));
        Assert.Empty(Directory.GetFiles(cache.Directory, "*.tmp"));

        Assert.False(cache.Contains(Path.Combine(_directory, key + ".wav")));
        Assert.False(cache.Contains(Path.Combine(cache.Directory, "..", key + ".wav")));
        Assert.False(cache.Contains(Path.Combine(cache.Directory, "notes.wav")));
        Assert.False(cache.Contains(Path.Combine(cache.Directory, key + ".exe")));
        Assert.Throws<ArgumentException>(() => cache.PathFor("..\\x"));
        Assert.Throws<ArgumentException>(() => cache.PathFor(key.ToUpperInvariant()));
    }

    [Fact]
    public void ADamagedCachedFileIsAMissAndIsRemoved()
    {
        var cache = new SpeechCache(_directory, _database.Clock);
        var key = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "t");
        var path = cache.Store(key, WaveFile.Silence());
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', 100)));

        Assert.Null(cache.TryGet(key));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheLeastRecentlyUsedFilesGoFirstOnceTheCapIsPassed()
    {
        var clock = _database.Clock;
        var audio = WaveFile.Silence(frames: 478); // 1000 bytes
        Assert.Equal(1000, audio.Length);
        var cache = new SpeechCache(_directory, clock, maxBytes: 2500);
        var first = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "1");
        var second = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "2");
        var third = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "3");

        cache.Store(first, audio);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        cache.Store(second, audio);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.NotNull(cache.TryGet(first)); // first is now the most recently used
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        cache.Store(third, audio);

        Assert.NotNull(cache.TryGet(first));
        Assert.Null(cache.TryGet(second));
        Assert.NotNull(cache.TryGet(third));
        Assert.Equal(2000, cache.SizeBytes());
        Assert.Equal(20L * 1024 * 1024, SpeechCache.DefaultMaxBytes);
    }

    [Fact]
    public void TheFileJustWrittenSurvivesEvenAboveTheCap()
    {
        var cache = new SpeechCache(_directory, _database.Clock, maxBytes: 10);
        var key = SpeechCache.KeyFor(SpeechProvider.Azure, "v", 100, "big");
        var path = cache.Store(key, WaveFile.Silence());
        Assert.True(File.Exists(path));
    }

    // -------------------------------------------------------------------- settings

    [Fact]
    public void SettingsStartOffWithNoKey()
    {
        var view = Service(new FakeSpeechTransport()).GetSettings();
        Assert.Equal(OnlineSpeechConfig.Off, view.Config);
        Assert.False(view.HasKey);
        Assert.False(view.Configured);
        Assert.Null(view.TargetHost);
    }

    [Fact]
    public void SettingsRoundTripAndTheKeyNeverComesBack()
    {
        var service = Service(new FakeSpeechTransport());
        var view = service.UpdateSettings(AzureUpdate());

        Assert.Equal(new OnlineSpeechConfig(SpeechProvider.Azure, "eastasia", null, null, "zh-CN-XiaoxiaoNeural"), view.Config);
        Assert.True(view.HasKey);
        Assert.True(view.Configured);
        Assert.Equal("eastasia" + OnlineSpeechClient.AzureHostSuffix, view.TargetHost);

        var reread = Service(new FakeSpeechTransport()).GetSettings();
        Assert.Equal(view, reread);

        var wire = SpeechHandlers.SettingsResponse(reread).ToJsonString();
        Assert.DoesNotContain(SpeechFixtures.Key, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(SpeechFixtures.Key, view.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SpeechFixtures.Key, AzureUpdate().ToString(), StringComparison.Ordinal);

        foreach (var key in new[] { "speech.provider", "speech.azure_region", "speech.voice", "speech.openai_base_url", "speech.openai_model" })
        {
            Assert.DoesNotContain(SpeechFixtures.Key, Settings.GetSetting(key) ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnOmittedFieldIsKeptAndAnEmptyOneIsCleared()
    {
        var service = Service(new FakeSpeechTransport());
        service.UpdateSettings(AzureUpdate());
        service.UpdateSettings(new SpeechSettingsUpdate
        {
            OpenAiBaseUrl = "https://speech.example.com/v1/", OpenAiBaseUrlSpecified = true,
            OpenAiModel = " tts-1 ", OpenAiModelSpecified = true,
        });

        var kept = service.GetSettings();
        Assert.Equal(SpeechProvider.Azure, kept.Config.Provider);
        Assert.Equal("eastasia", kept.Config.AzureRegion);
        Assert.Equal("https://speech.example.com/v1", kept.Config.OpenAiBaseUrl);
        Assert.Equal("tts-1", kept.Config.OpenAiModel);
        Assert.True(kept.HasKey);

        var cleared = service.UpdateSettings(new SpeechSettingsUpdate
        {
            OpenAiModel = "", OpenAiModelSpecified = true, Voice = null, VoiceSpecified = true,
        });
        Assert.Null(cleared.Config.OpenAiModel);
        Assert.Null(cleared.Config.Voice);
        Assert.True(cleared.HasKey);
        Assert.False(cleared.Configured);
    }

    [Fact]
    public void ChangingWhereTheSentenceGoesForgetsTheKey()
    {
        var service = Service(new FakeSpeechTransport());
        service.UpdateSettings(AzureUpdate());

        var otherRegion = service.UpdateSettings(new SpeechSettingsUpdate { AzureRegion = "westus2", AzureRegionSpecified = true });
        Assert.False(otherRegion.HasKey);
        Assert.False(File.Exists(Path.Combine(_directory, SpeechKeyStore.FileName)));

        service.UpdateSettings(new SpeechSettingsUpdate { ApiKey = SpeechFixtures.Key });
        Assert.True(service.GetSettings().HasKey);

        var otherService = service.UpdateSettings(new SpeechSettingsUpdate
        {
            Provider = SpeechProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://speech.example.com/v1", OpenAiBaseUrlSpecified = true,
            OpenAiModel = "tts-1", OpenAiModelSpecified = true,
            Voice = "alloy", VoiceSpecified = true,
        });
        Assert.False(otherService.HasKey);

        service.UpdateSettings(new SpeechSettingsUpdate { ApiKey = "openai-key" });
        Assert.True(service.GetSettings().HasKey);
        var otherAddress = service.UpdateSettings(new SpeechSettingsUpdate
        {
            OpenAiBaseUrl = "https://proxy.example.net/v1", OpenAiBaseUrlSpecified = true,
        });
        Assert.False(otherAddress.HasKey);

        // Same target, different spelling: the key stays.
        service.UpdateSettings(new SpeechSettingsUpdate { ApiKey = "proxy-key" });
        var sameTarget = service.UpdateSettings(new SpeechSettingsUpdate
        {
            OpenAiBaseUrl = "HTTPS://Proxy.Example.net/v1/", OpenAiBaseUrlSpecified = true, Voice = "nova", VoiceSpecified = true,
        });
        Assert.True(sameTarget.HasKey);

        // Switching off keeps nothing sendable.
        var off = service.UpdateSettings(new SpeechSettingsUpdate { Provider = SpeechProvider.None });
        Assert.False(off.HasKey);
        Assert.Null(off.TargetHost);
    }

    [Fact]
    public void AKeyChangeAndATargetChangeTogetherBindTheNewKeyToTheNewTarget()
    {
        var service = Service(new FakeSpeechTransport());
        service.UpdateSettings(AzureUpdate("first-key"));
        var moved = service.UpdateSettings(new SpeechSettingsUpdate
        {
            AzureRegion = "westus2", AzureRegionSpecified = true, ApiKey = "second-key",
        });

        Assert.True(moved.HasKey);
        var store = new SpeechKeyStore(_directory);
        Assert.Equal("second-key", store.Read(OnlineSpeechClient.KeyBinding(moved.Config)));
        Assert.Null(store.Read("azure:eastasia" + OnlineSpeechClient.AzureHostSuffix));
    }

    [Fact]
    public void AnEmptyKeyClearsIt()
    {
        var service = Service(new FakeSpeechTransport());
        service.UpdateSettings(AzureUpdate());
        var cleared = service.UpdateSettings(new SpeechSettingsUpdate { ApiKey = "" });
        Assert.False(cleared.HasKey);
        Assert.Equal("eastasia", cleared.Config.AzureRegion);
    }

    [Theory]
    [InlineData("region", "payload.azure_region")]
    [InlineData("url-http", "payload.openai_base_url")]
    [InlineData("url-query", "payload.openai_base_url")]
    [InlineData("model", "payload.openai_model")]
    [InlineData("voice", "payload.voice")]
    [InlineData("azure-voice", "payload.voice")]
    [InlineData("key-space", "payload.api_key")]
    [InlineData("key-without-target", "payload.api_key")]
    public void AnInvalidUpdateNamesTheFieldAndWritesNothing(string what, string field)
    {
        var service = Service(new FakeSpeechTransport());
        var update = what switch
        {
            "region" => new SpeechSettingsUpdate { AzureRegion = "east.asia", AzureRegionSpecified = true },
            "url-http" => new SpeechSettingsUpdate { OpenAiBaseUrl = "http://speech.example.com", OpenAiBaseUrlSpecified = true },
            "url-query" => new SpeechSettingsUpdate { OpenAiBaseUrl = "https://speech.example.com/?k=1", OpenAiBaseUrlSpecified = true },
            "model" => new SpeechSettingsUpdate { OpenAiModel = "a model", OpenAiModelSpecified = true },
            "voice" => new SpeechSettingsUpdate { Voice = "<voice>", VoiceSpecified = true },
            "azure-voice" => new SpeechSettingsUpdate { Provider = SpeechProvider.Azure, Voice = "vendor/voice", VoiceSpecified = true },
            "key-space" => AzureUpdate("a b"),
            _ => new SpeechSettingsUpdate { ApiKey = SpeechFixtures.Key },
        };

        var error = Assert.Throws<CollectorException>(() => service.UpdateSettings(update));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(OnlineSpeechConfig.Off, service.GetSettings().Config);
        Assert.False(File.Exists(Path.Combine(_directory, SpeechKeyStore.FileName)));
    }

    [Fact]
    public void UnreadableStoredSettingsReadAsUnset()
    {
        Settings.SetSetting("speech.provider", "\"AZURE\"");
        Settings.SetSetting("speech.azure_region", "\"east.asia\"");
        Settings.SetSetting("speech.openai_base_url", "\"http://speech.example.com\"");
        Settings.SetSetting("speech.voice", "42");
        Settings.SetSetting("speech.openai_model", "{not json");

        Assert.Equal(OnlineSpeechConfig.Off, SpeechSettingsStore.Read(Settings));
    }

    // -------------------------------------------------------------------- synthesis

    [Fact]
    public async Task TheKillSwitchComesBeforeTheCache()
    {
        var transport = new FakeSpeechTransport();
        var service = Service(transport);
        service.UpdateSettings(AzureUpdate());
        var spoken = await service.SynthesizeAsync(new SpeechRequest(Text, 100, false), default);
        Assert.True(File.Exists(spoken.AudioPath));

        var disabled = Service(transport, killSwitch: "1");
        var error = await Assert.ThrowsAsync<CollectorException>(() =>
            disabled.SynthesizeAsync(new SpeechRequest(Text, 100, false), default));
        Assert.Equal(ErrorCodes.SpeechDisabled, error.Code);
        Assert.Single(transport.Requests);
        Assert.True(disabled.Diagnostics().KillSwitch);
    }

    [Fact]
    public async Task NothingIsSentUntilServiceAndKeyAreThere()
    {
        var transport = new FakeSpeechTransport();
        var service = Service(transport);

        var off = await Assert.ThrowsAsync<CollectorException>(() => service.SynthesizeAsync(new SpeechRequest(Text, 100, false), default));
        Assert.Equal(ErrorCodes.SpeechNotConfigured, off.Code);

        service.UpdateSettings(AzureUpdate(key: null));
        var noKey = await Assert.ThrowsAsync<CollectorException>(() => service.SynthesizeAsync(new SpeechRequest(Text, 100, true), default));
        Assert.Equal(ErrorCodes.SpeechNotConfigured, noKey.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ASentenceIsRequestedOnceThenServedFromTheCacheAndATestAlwaysAsks()
    {
        var transport = new FakeSpeechTransport();
        var service = Service(transport);
        service.UpdateSettings(AzureUpdate());

        var first = await service.SynthesizeAsync(new SpeechRequest(Text, 120, false), default);
        Assert.False(first.FromCache);
        Assert.Equal(SpeechProvider.Azure, first.Provider);
        Assert.True(service.Cache.Contains(first.AudioPath));
        Assert.Equal(WaveFile.Silence(), File.ReadAllBytes(first.AudioPath));
        Assert.Contains("rate='+20%'", Encoding.UTF8.GetString(transport.Requests.Single().Body), StringComparison.Ordinal);

        var again = await service.SynthesizeAsync(new SpeechRequest(Text, 120, false), default);
        Assert.True(again.FromCache);
        Assert.Equal(first.AudioPath, again.AudioPath);
        Assert.Single(transport.Requests);

        var test = await service.SynthesizeAsync(new SpeechRequest(Text, 120, true), default);
        Assert.False(test.FromCache);
        Assert.Equal(2, transport.Requests.Count);

        var otherRate = await service.SynthesizeAsync(new SpeechRequest(Text, 100, false), default);
        Assert.False(otherRate.FromCache);
        Assert.NotEqual(first.AudioPath, otherRate.AudioPath);
        Assert.Equal(3, transport.Requests.Count);

        var diagnostics = service.Diagnostics();
        Assert.Equal("azure", diagnostics.Provider);
        Assert.Equal("OK", diagnostics.LastOutcome);
        Assert.Equal(_database.Clock.UtcNow, diagnostics.LastRequestAtUtc);
        Assert.False(diagnostics.KillSwitch);
    }

    [Theory]
    [InlineData(401, "ERR_SPEECH_AUTH")]
    [InlineData(403, "ERR_SPEECH_AUTH")]
    [InlineData(429, "ERR_SPEECH_QUOTA")]
    [InlineData(500, "ERR_SPEECH_NETWORK")]
    [InlineData(302, "ERR_SPEECH_NETWORK")]
    public async Task AFailedRequestIsOneErrorCodeWithoutTheServiceText(int status, string code)
    {
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            FakeSpeechTransport.Status(request, status, "denied for key " + SpeechFixtures.Key)));
        var service = Service(transport);
        service.UpdateSettings(AzureUpdate());

        var error = await Assert.ThrowsAsync<CollectorException>(() => service.SynthesizeAsync(new SpeechRequest(Text, 100, false), default));
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.Details!["http_status"]);
        Assert.DoesNotContain(SpeechFixtures.Key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", error.Message, StringComparison.Ordinal);
        Assert.All(error.Details.Values, value => Assert.DoesNotContain("denied", value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        Assert.Empty(Directory.Exists(service.Cache.Directory) ? Directory.GetFiles(service.Cache.Directory) : Array.Empty<string>());
        Assert.Equal(code, service.Diagnostics().LastOutcome);
    }

    [Fact]
    public async Task BadAudioAndTimeoutsMapToTheirCodes()
    {
        var format = Service(new FakeSpeechTransport((request, _) => Task.FromResult(
            FakeSpeechTransport.Wav(request, Encoding.UTF8.GetBytes("not a wav")))));
        format.UpdateSettings(AzureUpdate());
        var bad = await Assert.ThrowsAsync<CollectorException>(() => format.SynthesizeAsync(new SpeechRequest(Text, 100, false), default));
        Assert.Equal(ErrorCodes.SpeechFormat, bad.Code);
        Assert.Equal("NOT_RIFF_WAVE", bad.Details!["reason"]);
        Assert.False(bad.Retryable);

        var slow = Service(new FakeSpeechTransport(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeSpeechTransport.Wav(request);
        }), TimeSpan.FromMilliseconds(100));
        var timeout = await Assert.ThrowsAsync<CollectorException>(() => slow.SynthesizeAsync(new SpeechRequest(Text, 100, true), default));
        Assert.Equal(ErrorCodes.SpeechTimeout, timeout.Code);
        Assert.True(timeout.Retryable);
    }

    [Fact]
    public async Task OneRequestAtATimeThreeWaitingAndTheFifthFailsAtOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var transport = new FakeSpeechTransport(async (request, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (release)
            {
                maxInFlight = Math.Max(maxInFlight, now);
            }

            await release.Task;
            Interlocked.Decrement(ref inFlight);
            return FakeSpeechTransport.Wav(request);
        });
        var service = Service(transport, TimeSpan.FromSeconds(30));
        service.UpdateSettings(AzureUpdate());

        var admitted = Enumerable.Range(0, 4)
            .Select(index => service.SynthesizeAsync(new SpeechRequest(Text + index, 100, false), default))
            .ToArray();
        await WaitUntil(() => transport.Requests.Count == 1);

        var fifth = await Assert.ThrowsAsync<CollectorException>(() =>
            service.SynthesizeAsync(new SpeechRequest(Text + "5", 100, false), default));
        Assert.Equal(ErrorCodes.SpeechTimeout, fifth.Code);
        Assert.Equal("QUEUE_FULL", fifth.Details!["reason"]);
        Assert.Single(transport.Requests);

        release.SetResult();
        var results = await Task.WhenAll(admitted);
        Assert.All(results, result => Assert.False(result.FromCache));
        Assert.Equal(4, transport.Requests.Count);
        Assert.Equal(1, maxInFlight);

        // The slot is free again.
        Assert.False((await service.SynthesizeAsync(new SpeechRequest(Text + "6", 100, false), default)).FromCache);
        Assert.Equal(3, OnlineSpeechService.MaxQueued);
    }

    [Fact]
    public async Task ASentenceWaitsNoLongerThanOneRequestsBudget()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeSpeechTransport(async (request, _) =>
        {
            await release.Task; // ignores its budget on purpose: the slot stays taken
            return FakeSpeechTransport.Wav(request);
        });
        var service = Service(transport, TimeSpan.FromMilliseconds(200));
        service.UpdateSettings(AzureUpdate());

        var holder = service.SynthesizeAsync(new SpeechRequest(Text, 100, true), default);
        await WaitUntil(() => transport.Requests.Count == 1);

        var waited = await Assert.ThrowsAsync<CollectorException>(() =>
            service.SynthesizeAsync(new SpeechRequest(Text + "b", 100, true), default));
        Assert.Equal(ErrorCodes.SpeechTimeout, waited.Code);
        Assert.Equal("QUEUE_WAIT", waited.Details!["reason"]);

        release.SetResult();
        try
        {
            await holder; // past its own budget: either outcome is fine, it only has to finish
        }
        catch (CollectorException)
        {
        }
    }

    [Fact]
    public async Task TheLogHoldsErrorCodesAndHostsButNeverTheKeyTheTextOrTheAnswer()
    {
        var logDirectory = Path.Combine(_directory, "logs");
        using (var logger = new RotatingFileLogger(logDirectory, _database.Clock))
        {
            var status = 200;
            var transport = new FakeSpeechTransport((request, _) => Task.FromResult(status == 200
                ? FakeSpeechTransport.Wav(request)
                : FakeSpeechTransport.Status(request, status, "bad key " + SpeechFixtures.Key)));
            var service = Service(transport, logger: logger);
            service.UpdateSettings(AzureUpdate());
            await service.SynthesizeAsync(new SpeechRequest(Text, 100, false), default);
            status = 401;
            await Assert.ThrowsAsync<CollectorException>(() => service.SynthesizeAsync(new SpeechRequest(Text, 100, true), default));
            service.UpdateSettings(new SpeechSettingsUpdate { ApiKey = "another-secret-key" });
        }

        var log = string.Concat(Directory.GetFiles(logDirectory, "*.log").Select(path => File.ReadAllText(path)));
        Assert.Contains("speech_request", log, StringComparison.Ordinal);
        Assert.Contains("ERR_SPEECH_AUTH", log, StringComparison.Ordinal);
        Assert.Contains("\"OK\"", log, StringComparison.Ordinal);
        Assert.Contains("eastasia" + OnlineSpeechClient.AzureHostSuffix, log, StringComparison.Ordinal);
        Assert.DoesNotContain(SpeechFixtures.Key, log, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret-key", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bad key", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Text, log, StringComparison.Ordinal);
        Assert.DoesNotContain("cognitiveservices", log, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the fake transport");
            await Task.Delay(10);
        }
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => throw new CryptographicException("broken");

        public byte[] Unprotect(byte[] ciphertext) => throw new CryptographicException("broken");
    }
}
