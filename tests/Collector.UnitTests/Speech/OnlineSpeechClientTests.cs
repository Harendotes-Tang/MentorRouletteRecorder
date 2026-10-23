using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MentorRecorder.Collector.Speech;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The online speech client (docs/privacy-boundary.md §8.3),
/// driven entirely through its injected transport. No test here opens a connection or writes the Azure
/// host name; addresses are asserted against the client's own constants.
/// </summary>
public sealed class OnlineSpeechClientTests
{
    private const string Text = "匹配成功：导随任务";

    // ------------------------------------------------------------------ input rules

    [Theory]
    [InlineData("eastasia", true)]
    [InlineData("westus2", true)]
    [InlineData("ab", true)]
    [InlineData("a", false)]
    [InlineData("EastAsia", false)]
    [InlineData("east.asia", false)]
    [InlineData("east asia", false)]
    [InlineData("eastasia:443", false)]
    [InlineData("eastasia/x", false)]
    [InlineData("eastasia\n", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("", false)]
    public void ARegionIsTwoToThirtyTwoLowerCaseLettersOrDigits(string region, bool valid) =>
        Assert.Equal(valid, SpeechValidation.IsRegion(region));

    [Fact]
    public void ATypedRegionIsTrimmedAndLowerCasedBeforeItIsChecked()
    {
        Assert.True(SpeechValidation.TryNormalizeRegion("  EastAsia ", out var region));
        Assert.Equal("eastasia", region);
        Assert.False(SpeechValidation.TryNormalizeRegion("east-asia", out _));
    }

    [Theory]
    [InlineData("https://speech.example.com/v1/", "https://speech.example.com/v1")]
    [InlineData("  https://speech.example.com//  ", "https://speech.example.com")]
    [InlineData("HTTPS://Speech.Example.com:8443/openai/v1", "https://speech.example.com:8443/openai/v1")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://[::1]:8000/v1", "http://[::1]:8000/v1")]
    public void AnAcceptableBaseAddressIsNormalised(string raw, string expected)
    {
        Assert.True(SpeechValidation.TryNormalizeBaseUrl(raw, out var normalized, out var uri));
        Assert.Equal(expected, normalized);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("http://speech.example.com/v1")]
    [InlineData("http://127.0.0.2/v1")]
    [InlineData("http://localhost.example.com/v1")]
    [InlineData("https://user:secret@speech.example.com/v1")]
    [InlineData("https://user@speech.example.com/v1")]
    [InlineData("https://speech.example.com/v1?key=1")]
    [InlineData("https://speech.example.com/v1#part")]
    [InlineData("https://speech.example.com/v1?")]
    [InlineData("https://speech.example.com\\v1")]
    [InlineData("https://speech example.com/v1")]
    [InlineData("ftp://speech.example.com/v1")]
    [InlineData("file:///C:/speech")]
    [InlineData("speech.example.com/v1")]
    [InlineData("/v1/audio")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotABaseAddress(string? raw)
    {
        Assert.False(SpeechValidation.TryNormalizeBaseUrl(raw, out var normalized, out var uri));
        Assert.Equal(string.Empty, normalized);
        Assert.Null(uri);
    }

    [Fact]
    public void AKeyIsTrimmedVisibleAsciiOfBoundedLength()
    {
        Assert.True(SpeechValidation.TryNormalizeKey("  abc-123_XYZ  ", out var key));
        Assert.Equal("abc-123_XYZ", key);
        Assert.False(SpeechValidation.TryNormalizeKey("", out _));
        Assert.False(SpeechValidation.TryNormalizeKey("   ", out _));
        Assert.False(SpeechValidation.TryNormalizeKey("two words", out _));
        Assert.False(SpeechValidation.TryNormalizeKey("line\nbreak", out _));
        Assert.False(SpeechValidation.TryNormalizeKey("密钥", out _));
        Assert.True(SpeechValidation.TryNormalizeKey(new string('k', 512), out _));
        Assert.False(SpeechValidation.TryNormalizeKey(new string('k', 513), out _));
    }

    [Fact]
    public void TheSentenceIsOneLineOfAtMostTwoHundredCharacters()
    {
        Assert.True(SpeechValidation.TryNormalizeText("  匹配\r\n成功\t ", out var text));
        Assert.Equal("匹配  成功", text);
        Assert.True(SpeechValidation.TryNormalizeText(new string('字', 200), out _));
        Assert.False(SpeechValidation.TryNormalizeText(new string('字', 201), out _));

        // Code points, as JSON Schema counts them: an emoji is one character, not two.
        Assert.True(SpeechValidation.TryNormalizeText(string.Concat(Enumerable.Repeat("😀", 200)), out _));
        Assert.False(SpeechValidation.TryNormalizeText(string.Concat(Enumerable.Repeat("😀", 201)), out _));

        Assert.False(SpeechValidation.TryNormalizeText("", out _));
        Assert.False(SpeechValidation.TryNormalizeText(" \n\t ", out _));
        Assert.False(SpeechValidation.TryNormalizeText("bell\u0007", out _));
        Assert.False(SpeechValidation.TryNormalizeText("lone \uD800 surrogate", out _));
        Assert.False(SpeechValidation.TryNormalizeText("\uDC00", out _));
        Assert.False(SpeechValidation.TryNormalizeText("\uFFFE", out _));
    }

    [Theory]
    [InlineData("zh-CN-XiaoxiaoNeural", true, true)]
    [InlineData("alloy", true, true)]
    [InlineData("vendor/model:speaker", false, true)]
    [InlineData("zh", false, true)]
    [InlineData("x'onload='y", false, false)]
    [InlineData("a b", false, false)]
    [InlineData("", false, false)]
    public void VoiceNamesAreHeldToWhatTheirBodyCanCarry(string voice, bool azure, bool openAi)
    {
        Assert.Equal(azure, SpeechValidation.IsVoice(SpeechProvider.Azure, voice));
        Assert.Equal(openAi, SpeechValidation.IsVoice(SpeechProvider.OpenAiCompatible, voice));
        Assert.False(SpeechValidation.IsVoice(SpeechProvider.None, voice));
    }

    [Fact]
    public void TheCuratedAzureVoicesAreValidNames()
    {
        Assert.NotEmpty(OnlineSpeechService.AzureVoices);
        Assert.All(OnlineSpeechService.AzureVoices, voice =>
        {
            Assert.True(SpeechValidation.IsVoice(SpeechProvider.Azure, voice.Name), voice.Name);
            Assert.StartsWith("zh-CN-", voice.Name, StringComparison.Ordinal);
            Assert.EndsWith("Neural", voice.Name, StringComparison.Ordinal);
        });
        Assert.Contains(OnlineSpeechService.AzureVoices, voice => voice.Name == "zh-CN-XiaoxiaoNeural");
        Assert.All(OnlineSpeechService.OpenAiVoices, voice =>
            Assert.True(SpeechValidation.IsVoice(SpeechProvider.OpenAiCompatible, voice.Name)));
    }

    // ----------------------------------------------------------------- request bodies

    [Theory]
    [InlineData(100, "+0%")]
    [InlineData(150, "+50%")]
    [InlineData(200, "+100%")]
    [InlineData(80, "-20%")]
    [InlineData(50, "-50%")]
    public void TheAzureRateIsTheDifferenceFromNormal(int percent, string expected) =>
        Assert.Equal(expected, SpeechMarkup.AzureRate(percent));

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(50, 0.5)]
    [InlineData(125, 1.25)]
    [InlineData(200, 2.0)]
    public void TheOpenAiSpeedIsTheRateOverOneHundredWithinTheApiRange(int percent, double expected)
    {
        Assert.Equal(expected, SpeechMarkup.OpenAiSpeed(percent), precision: 6);
        Assert.InRange(SpeechMarkup.OpenAiSpeed(percent), SpeechMarkup.MinOpenAiSpeed, SpeechMarkup.MaxOpenAiSpeed);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(201)]
    [InlineData(0)]
    [InlineData(-100)]
    public void ARateOutsideFiftyToTwoHundredIsRefused(int percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechMarkup.AzureRate(percent));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechMarkup.OpenAiSpeed(percent));
    }

    [Fact]
    public void SsmlEscapesTheTextSoItCannotCloseAnElement()
    {
        const string hostile = "<b>A & B</b> 'single' \"double\" </prosody></voice><voice name='x'>";
        var ssml = SpeechMarkup.AzureSsml("zh-CN-YunxiNeural", hostile, 150);

        Assert.DoesNotContain("<b>", ssml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;A &amp; B&lt;/b&gt; &apos;single&apos; &quot;double&quot;", ssml, StringComparison.Ordinal);

        var document = XDocument.Parse(ssml);
        var speak = document.Root!;
        Assert.Equal("speak", speak.Name.LocalName);
        Assert.Equal("1.0", speak.Attribute("version")!.Value);
        Assert.Equal("zh-CN", speak.Attribute(XNamespace.Xml + "lang")!.Value);
        var voice = Assert.Single(speak.Elements());
        Assert.Equal("voice", voice.Name.LocalName);
        Assert.Equal("zh-CN-YunxiNeural", voice.Attribute("name")!.Value);
        var prosody = Assert.Single(voice.Elements());
        Assert.Equal("prosody", prosody.Name.LocalName);
        Assert.Equal("+50%", prosody.Attribute("rate")!.Value);
        Assert.Empty(prosody.Elements());
        Assert.Equal(hostile, prosody.Value);
    }

    [Fact]
    public void SsmlEscapesTheVoiceSoItCannotCloseTheAttribute()
    {
        var ssml = SpeechMarkup.AzureSsml("a'><evil/>", Text, 100);
        var voice = XDocument.Parse(ssml).Root!.Elements().Single();
        Assert.Equal("a'><evil/>", voice.Attribute("name")!.Value);
        Assert.Single(voice.Elements());
    }

    [Fact]
    public void TheOpenAiBodyIsExactlyFiveFields()
    {
        var body = SpeechMarkup.OpenAiBody("gpt-4o-mini-tts", "alloy", "他说\"你好\"\n<b>", 150);
        Assert.False(body.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            new[] { "model", "input", "voice", "response_format", "speed" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("gpt-4o-mini-tts", root.GetProperty("model").GetString());
        Assert.Equal("他说\"你好\"\n<b>", root.GetProperty("input").GetString());
        Assert.Equal("alloy", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1.5, root.GetProperty("speed").GetDouble());
    }

    // ----------------------------------------------------------------- request shapes

    [Fact]
    public void AnAzureRequestIsAPostOfSsmlToTheRegionEndpoint()
    {
        var request = OnlineSpeechClient.BuildRequest(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal("POST", request.Method);
        Assert.Equal(
            new Uri("https://eastasia" + OnlineSpeechClient.AzureHostSuffix + OnlineSpeechClient.AzurePath),
            request.Uri);
        Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
        Assert.True(request.Uri.IsDefaultPort);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal("application/ssml+xml", request.ContentType);
        Assert.Equal(
            new[] { OnlineSpeechClient.AzureKeyHeader, OnlineSpeechClient.AzureFormatHeader, "User-Agent" },
            request.Headers.Select(header => header.Key));
        Assert.Equal(SpeechFixtures.Key, request.Header("ocp-apim-subscription-key"));
        Assert.Equal("riff-24khz-16bit-mono-pcm", request.Header(OnlineSpeechClient.AzureFormatHeader));
        Assert.Equal("MentorRecorder", request.Header("User-Agent"));
        Assert.Null(request.Header("Authorization"));
        Assert.Equal(
            SpeechMarkup.AzureSsml("zh-CN-XiaoxiaoNeural", Text, 100),
            Encoding.UTF8.GetString(request.Body));
        Assert.Contains("rate='+0%'", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenAiRequestIsAPostOfJsonToTheAudioSpeechPath()
    {
        var request = OnlineSpeechClient.BuildRequest(
            SpeechFixtures.OpenAi("https://speech.example.com/openai/v1"), "  " + SpeechFixtures.Key + " ", Text, 200);

        Assert.Equal(new Uri("https://speech.example.com/openai/v1/audio/speech"), request.Uri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(new[] { "Authorization", "User-Agent" }, request.Headers.Select(header => header.Key));
        Assert.Equal("Bearer " + SpeechFixtures.Key, request.Header("authorization"));
        Assert.Null(request.Header(OnlineSpeechClient.AzureKeyHeader));
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(Text, document.RootElement.GetProperty("input").GetString());
        Assert.Equal(2.0, document.RootElement.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void ARequestNeverPrintsItsKeyOrBody()
    {
        var request = OnlineSpeechClient.BuildRequest(SpeechFixtures.OpenAi(), SpeechFixtures.Key, Text, 100);
        var printed = request.ToString();
        Assert.DoesNotContain(SpeechFixtures.Key, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Text, printed, StringComparison.Ordinal);
        Assert.Equal("POST speech.example.com/v1/audio/speech", printed);
    }

    [Fact]
    public void AnIncompleteConfigurationOrABadKeyBuildsNothing()
    {
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(OnlineSpeechConfig.Off, SpeechFixtures.Key, Text, 100));
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(SpeechFixtures.Azure() with { AzureRegion = null }, SpeechFixtures.Key, Text, 100));
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(SpeechFixtures.Azure(voice: "vendor/voice"), SpeechFixtures.Key, Text, 100));
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(SpeechFixtures.OpenAi("http://speech.example.com"), SpeechFixtures.Key, Text, 100));
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(SpeechFixtures.OpenAi() with { OpenAiModel = null }, SpeechFixtures.Key, Text, 100));
        Assert.Throws<ArgumentException>(() =>
            OnlineSpeechClient.BuildRequest(SpeechFixtures.Azure(), "bad key", Text, 100));
    }

    [Fact]
    public void TheTargetAndTheKeyBindingNameTheHostTheSentenceGoesTo()
    {
        Assert.Equal("eastasia" + OnlineSpeechClient.AzureHostSuffix, OnlineSpeechClient.TargetHost(SpeechFixtures.Azure()));
        Assert.Equal("azure:eastasia" + OnlineSpeechClient.AzureHostSuffix, OnlineSpeechClient.KeyBinding(SpeechFixtures.Azure()));
        Assert.Equal("speech.example.com", OnlineSpeechClient.TargetHost(SpeechFixtures.OpenAi()));
        Assert.Equal("openai_compatible:https://speech.example.com/v1", OnlineSpeechClient.KeyBinding(SpeechFixtures.OpenAi()));
        Assert.NotEqual(
            OnlineSpeechClient.KeyBinding(SpeechFixtures.OpenAi("https://speech.example.com/v1")),
            OnlineSpeechClient.KeyBinding(SpeechFixtures.OpenAi("https://speech.example.com/v2")));
        Assert.Null(OnlineSpeechClient.TargetHost(OnlineSpeechConfig.Off));
        Assert.Null(OnlineSpeechClient.KeyBinding(OnlineSpeechConfig.Off with { AzureRegion = "eastasia" }));
        Assert.Throws<ArgumentException>(() => OnlineSpeechClient.AzureHost("evil.example.com#"));
    }

    [Theory]
    [InlineData("https://eastasia{0}/cognitiveservices/v1", true)]
    [InlineData("https://eastasia{0}/other", false)]
    [InlineData("http://eastasia{0}/cognitiveservices/v1", false)]
    [InlineData("https://eastasia{0}:8443/cognitiveservices/v1", false)]
    [InlineData("https://evil.host{0}/cognitiveservices/v1", false)]
    [InlineData("https://speech.example.com/v1/audio/speech", true)]
    [InlineData("http://127.0.0.1:9000/audio/speech", true)]
    [InlineData("http://speech.example.com/v1/audio/speech", false)]
    [InlineData("https://speech.example.com/v1/audio/speech?x=1", false)]
    [InlineData("https://user@speech.example.com/v1/audio/speech", false)]
    [InlineData("https://speech.example.com/v1/other", false)]
    public void TheProductionTransportOnlyPostsToValidatedAddresses(string template, bool sendable) =>
        Assert.Equal(sendable, OnlineSpeechClient.IsSendable(new Uri(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, template, OnlineSpeechClient.AzureHostSuffix))));

    [Fact]
    public void TheProductionHandlerFollowsNothingAndSendsNoCredentials()
    {
        using var handler = OnlineSpeechClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(OnlineSpeechClient.DefaultRequestTimeout, handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), OnlineSpeechClient.DefaultRequestTimeout);
    }

    [Fact]
    public void TheProductionMessageCarriesExactlyTheBuiltHeaders()
    {
        var request = OnlineSpeechClient.BuildRequest(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);
        using var message = OnlineSpeechClient.CreateMessage(request);

        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal(HttpVersion.Version11, message.Version);
        Assert.Equal(request.Uri, message.RequestUri);
        Assert.Equal(
            new[] { OnlineSpeechClient.AzureKeyHeader, OnlineSpeechClient.AzureFormatHeader, "User-Agent" }.Order(),
            message.Headers.Select(header => header.Key).Order());
        Assert.Equal("MentorRecorder", Assert.Single(message.Headers.GetValues("User-Agent")));
        Assert.Equal("application/ssml+xml", message.Content!.Headers.ContentType!.MediaType);
        Assert.Null(message.Content.Headers.ContentType.CharSet);
    }

    // ------------------------------------------------------------------ kill switch

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("FALSE", false)]
    public void TheKillSwitchIsOnForAnyValueButEmptyZeroOrFalse(string? value, bool disabled) =>
        Assert.Equal(disabled, OnlineSpeechClient.IsDisabled(value));

    [Fact]
    public async Task TheKillSwitchIsReadBeforeEveryRequestAndStopsItBeforeTheTransport()
    {
        var transport = new FakeSpeechTransport();
        var value = "1";
        var asked = new List<string>();
        var client = new OnlineSpeechClient(transport.Send, readEnvironment: name =>
        {
            asked.Add(name);
            return value;
        });

        var first = await client.SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);
        Assert.Equal(SpeechOutcome.Disabled, first.Outcome);
        Assert.Null(first.Host);
        Assert.Empty(transport.Requests);

        value = "0";
        var second = await client.SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);
        Assert.Equal(SpeechOutcome.Ok, second.Outcome);
        Assert.Single(transport.Requests);

        value = "true";
        Assert.Equal(SpeechOutcome.Disabled, (await client.SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100)).Outcome);
        Assert.Single(transport.Requests);
        Assert.Equal(new[] { "MR_DISABLE_ONLINE_SPEECH" }, asked.Distinct());
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task NothingIsSentWithoutACompleteConfigurationAndAKey()
    {
        var transport = new FakeSpeechTransport();
        var client = SpeechFixtures.Client(transport);

        Assert.Equal(SpeechOutcome.NotConfigured, (await client.SynthesizeAsync(OnlineSpeechConfig.Off, SpeechFixtures.Key, Text, 100)).Outcome);
        Assert.Equal(SpeechOutcome.NotConfigured, (await client.SynthesizeAsync(SpeechFixtures.Azure(), null, Text, 100)).Outcome);
        Assert.Equal(SpeechOutcome.NotConfigured, (await client.SynthesizeAsync(SpeechFixtures.Azure(), " ", Text, 100)).Outcome);
        Assert.Equal(SpeechOutcome.NotConfigured, (await client.SynthesizeAsync(SpeechFixtures.OpenAi() with { Voice = null }, SpeechFixtures.Key, Text, 100)).Outcome);
        Assert.Empty(transport.Requests);
    }

    // --------------------------------------------------------------------- answers

    [Fact]
    public async Task AWavAnswerComesBackCanonical()
    {
        var transport = new FakeSpeechTransport();
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.OpenAi(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Ok, result.Outcome);
        Assert.Equal(WaveFile.Silence(), result.Audio);
        Assert.Equal("speech.example.com", result.Host);
        Assert.Equal(200, result.HttpStatus);
        Assert.Single(transport.Requests);
        Assert.DoesNotContain("AudioBytes = 0", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ARedirectIsRefusedNotFollowed(int status)
    {
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(status, request.Uri, "audio/wav", null, new MemoryStream(WaveFile.Silence()))));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Network, result.Outcome);
        Assert.Equal("REDIRECT_REFUSED", result.Reason);
        Assert.Equal(status, result.HttpStatus);
        Assert.Null(result.Audio);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task AnAnswerFromAnotherAddressIsRefused()
    {
        var transport = new FakeSpeechTransport((_, _) => Task.FromResult(new SpeechTransportResponse(
            200, new Uri("https://elsewhere.example.com/audio/speech"), "audio/wav", null, new MemoryStream(WaveFile.Silence()))));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.OpenAi(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Network, result.Outcome);
        Assert.Equal("REDIRECT_REFUSED", result.Reason);
    }

    [Theory]
    [InlineData(401, SpeechOutcome.Auth)]
    [InlineData(403, SpeechOutcome.Auth)]
    [InlineData(429, SpeechOutcome.Quota)]
    [InlineData(400, SpeechOutcome.Network)]
    [InlineData(404, SpeechOutcome.Network)]
    [InlineData(500, SpeechOutcome.Network)]
    [InlineData(503, SpeechOutcome.Network)]
    [InlineData(100, SpeechOutcome.Network)]
    public async Task AStatusMapsToAnOutcomeAndTheBodyIsNeverKept(int status, SpeechOutcome expected)
    {
        var echo = "{\"error\":\"invalid key " + SpeechFixtures.Key + "\"}";
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(FakeSpeechTransport.Status(request, status, echo)));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.HttpStatus);
        Assert.Null(result.Audio);
        Assert.DoesNotContain(SpeechFixtures.Key, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid key", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceThatNeverAnswersTimesOut()
    {
        var transport = new FakeSpeechTransport(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeSpeechTransport.Wav(request);
        });
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await SpeechFixtures.Client(transport, TimeSpan.FromMilliseconds(150))
            .SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Timeout, result.Outcome);
        Assert.Equal("eastasia" + OnlineSpeechClient.AzureHostSuffix, result.Host);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheBudgetAlsoCoversABodyThatTrickles()
    {
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(200, request.Uri, "audio/wav", null, new StallingStream())));
        var result = await SpeechFixtures.Client(transport, TimeSpan.FromMilliseconds(150))
            .SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task TheCallerCancellingIsNotATimeout()
    {
        using var cancel = new CancellationTokenSource();
        var transport = new FakeSpeechTransport(async (request, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return FakeSpeechTransport.Wav(request);
        });
        var result = await SpeechFixtures.Client(transport)
            .SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100, cancel.Token);

        Assert.Equal(SpeechOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task ConnectionFailuresBecomeOutcomesWithoutTheirMessages()
    {
        async Task<SpeechSynthesisResult> Throwing(Exception error) =>
            await SpeechFixtures.Client(new FakeSpeechTransport((_, _) => throw error))
                .SynthesizeAsync(SpeechFixtures.OpenAi(), SpeechFixtures.Key, Text, 100);

        var dns = await Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host 10.1.2.3"));
        Assert.Equal(SpeechOutcome.Network, dns.Outcome);
        Assert.Equal("NAME_RESOLUTION_ERROR", dns.Reason);
        Assert.DoesNotContain("10.1.2.3", dns.ToString(), StringComparison.Ordinal);

        var slow = await Throwing(new HttpRequestException("connect", new TimeoutException()));
        Assert.Equal(SpeechOutcome.Timeout, slow.Outcome);

        var io = await Throwing(new IOException("reset by 10.1.2.3"));
        Assert.Equal(SpeechOutcome.Network, io.Outcome);
        Assert.Equal("READ_FAILED", io.Reason);

        var other = await Throwing(new InvalidOperationException("proxy at 10.1.2.3"));
        Assert.Equal(SpeechOutcome.Network, other.Outcome);
        Assert.Equal("INVALID_OPERATION_EXCEPTION", other.Reason);
        Assert.DoesNotContain("10.1.2.3", other.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatFailsWhileReadingIsANetworkFailure()
    {
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(200, request.Uri, "audio/wav", null, new FailingStream())));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);
        Assert.Equal(SpeechOutcome.Network, result.Outcome);
        Assert.Equal("READ_FAILED", result.Reason);
    }

    [Fact]
    public async Task AnAnswerDeclaredOverFiveMegabytesIsRefusedUnread()
    {
        var body = new CountingStream(WaveFile.Silence());
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(200, request.Uri, "audio/wav", OnlineSpeechClient.MaxResponseBytes + 1L, body)));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Format, result.Outcome);
        Assert.Equal("TOO_LARGE", result.Reason);
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task AnUndeclaredBodyIsCountedAndAbandonedPastFiveMegabytes()
    {
        var big = new byte[OnlineSpeechClient.MaxResponseBytes + 1];
        WaveFile.Silence().CopyTo(big, 0);
        var body = new CountingStream(big);
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(200, request.Uri, "audio/wav", null, body)));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);

        Assert.Equal(SpeechOutcome.Format, result.Outcome);
        Assert.Equal("TOO_LARGE", result.Reason);
        Assert.True(body.BytesRead <= OnlineSpeechClient.MaxResponseBytes + 81920);

        var exact = new byte[OnlineSpeechClient.MaxResponseBytes];
        SpeechFixtures.Wave(dataBytes: OnlineSpeechClient.MaxResponseBytes - 44).CopyTo(exact, 0);
        var fits = new FakeSpeechTransport((request, _) => Task.FromResult(
            new SpeechTransportResponse(200, request.Uri, "audio/wav", null, new MemoryStream(exact))));
        Assert.Equal(SpeechOutcome.Ok,
            (await SpeechFixtures.Client(fits).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100)).Outcome);
    }

    [Theory]
    [InlineData("audio/wav", true)]
    [InlineData("audio/x-wav", true)]
    [InlineData("AUDIO/WAVE; codecs=1", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("text/html", false)]
    [InlineData("application/json", false)]
    [InlineData("audio/", false)]
    [InlineData("audio", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAudioOrOctetStreamIsAcceptedAsAType(string? mediaType, bool accepted) =>
        Assert.Equal(accepted, OnlineSpeechClient.IsAudioMediaType(mediaType));

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task AWavWithTheWrongTypeIsRefused(string? mediaType)
    {
        var transport = new FakeSpeechTransport((request, _) => Task.FromResult(FakeSpeechTransport.Wav(request, mediaType: mediaType)));
        var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.Azure(), SpeechFixtures.Key, Text, 100);
        Assert.Equal(SpeechOutcome.Format, result.Outcome);
        Assert.Equal("CONTENT_TYPE", result.Reason);
    }

    [Fact]
    public async Task AnAudioTypedBodyThatIsNotWavIsRefused()
    {
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        foreach (var body in new[] { mp3, Encoding.UTF8.GetBytes("{\"error\":\"quota\"}"), Array.Empty<byte>() })
        {
            var transport = new FakeSpeechTransport((request, _) => Task.FromResult(FakeSpeechTransport.Wav(request, body, "audio/mpeg")));
            var result = await SpeechFixtures.Client(transport).SynthesizeAsync(SpeechFixtures.OpenAi(), SpeechFixtures.Key, Text, 100);
            Assert.Equal(SpeechOutcome.Format, result.Outcome);
            Assert.Equal("NOT_RIFF_WAVE", result.Reason);
            Assert.Null(result.Audio);
        }
    }

    // -------------------------------------------------------------------- WAV rules

    [Theory]
    [InlineData(1, 8, "NOT_16_BIT")]
    [InlineData(1, 24, "NOT_16_BIT")]
    [InlineData(3, 16, "NOT_PCM")]
    [InlineData(2, 16, "NOT_PCM")]
    public void OnlySixteenBitPcmIsAccepted(int tag, int bits, string refusal)
    {
        Assert.False(WaveFile.TryNormalize(SpeechFixtures.Wave((ushort)tag, (ushort)bits), out var canonical, out var format, out var reason));
        Assert.Equal(refusal, reason);
        Assert.Empty(canonical);
        Assert.Null(format);
    }

    [Fact]
    public void BrokenStructuresAreRefused()
    {
        var good = SpeechFixtures.Wave();
        Assert.True(WaveFile.TryNormalize(good, out _, out _, out _));

        Assert.Equal("NOT_RIFF_WAVE", Refusal(good[..11]));
        var noFmt = good.ToArray();
        "junk"u8.CopyTo(noFmt.AsSpan(12));
        Assert.Equal("DATA_BEFORE_FMT", Refusal(noFmt));
        Assert.Equal("NO_DATA", Refusal(good[..36]));
        Assert.Equal("NO_SAMPLES", Refusal(SpeechFixtures.Wave(dataBytes: 0)));
        Assert.Equal("NO_SAMPLES", Refusal(SpeechFixtures.Wave(dataBytes: 1)));

        var shortFmt = good.ToArray();
        shortFmt[16] = 8;
        Assert.Equal("SHORT_FMT", Refusal(shortFmt));

        var hugeChunk = SpeechFixtures.Wave(extraChunk: "LIST"u8.ToArray().Concat(BitConverter.GetBytes(100000u)).ToArray());
        Assert.Equal("TRUNCATED_CHUNK", Refusal(hugeChunk));

        var badRate = good.ToArray();
        BitConverter.GetBytes(12345u).CopyTo(badRate, 28);
        Assert.Equal("BAD_FMT", Refusal(badRate));

        static string? Refusal(byte[] bytes)
        {
            Assert.False(WaveFile.TryNormalize(bytes, out _, out _, out var reason));
            return reason;
        }
    }

    [Fact]
    public void AStreamedWavWithUnknownSizesIsRewrittenWithTrueOnes()
    {
        var streamed = SpeechFixtures.Wave(dataBytes: 481, riffSize: uint.MaxValue, dataSize: uint.MaxValue,
            extraChunk: "LIST"u8.ToArray().Concat(BitConverter.GetBytes(5u)).Concat(new byte[] { 1, 2, 3, 4, 5, 0 }).ToArray());

        Assert.True(WaveFile.TryNormalize(streamed, out var canonical, out var format, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(new WaveFile.WaveFormat(1, 24000, 480), format);
        Assert.Equal(WaveFile.CanonicalHeaderBytes + 480, canonical.Length);
        Assert.Equal((uint)canonical.Length - 8, BitConverter.ToUInt32(canonical, 4));
        Assert.Equal(16u, BitConverter.ToUInt32(canonical, 16));
        Assert.Equal(480u, BitConverter.ToUInt32(canonical, 40));
        Assert.True(WaveFile.TryNormalize(canonical, out var again, out _, out _));
        Assert.Equal(canonical, again);
    }

    [Fact]
    public void ExtensiblePcmBecomesPlainPcm()
    {
        var extensible = new List<byte>();
        extensible.AddRange("RIFF"u8.ToArray());
        extensible.AddRange(BitConverter.GetBytes(0u));
        extensible.AddRange("WAVEfmt "u8.ToArray());
        extensible.AddRange(BitConverter.GetBytes(40u));
        extensible.AddRange(BitConverter.GetBytes((ushort)0xFFFE));
        extensible.AddRange(BitConverter.GetBytes((ushort)2));
        extensible.AddRange(BitConverter.GetBytes(48000u));
        extensible.AddRange(BitConverter.GetBytes(48000u * 4));
        extensible.AddRange(BitConverter.GetBytes((ushort)4));
        extensible.AddRange(BitConverter.GetBytes((ushort)16));
        extensible.AddRange(BitConverter.GetBytes((ushort)22));
        extensible.AddRange(BitConverter.GetBytes((ushort)16));
        extensible.AddRange(BitConverter.GetBytes(3u));
        extensible.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        extensible.AddRange("data"u8.ToArray());
        extensible.AddRange(BitConverter.GetBytes(8u));
        extensible.AddRange(new byte[8]);

        Assert.True(WaveFile.TryNormalize(extensible.ToArray(), out var canonical, out var format, out _));
        Assert.Equal(new WaveFile.WaveFormat(2, 48000, 8), format);
        Assert.Equal(1, BitConverter.ToUInt16(canonical, 20));

        extensible[44] = 0x03;
        Assert.False(WaveFile.TryNormalize(extensible.ToArray(), out _, out _, out var refusal));
        Assert.Equal("NOT_PCM", refusal);
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }

    private sealed class CountingStream(byte[] content) : MemoryStream(content)
    {
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
