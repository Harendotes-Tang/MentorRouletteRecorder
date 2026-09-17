using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MentorRecorder.Collector.Speech;

/// <summary>Which online speech service the user chose (docs/privacy-boundary.md §8.3).</summary>
public enum SpeechProvider
{
    /// <summary>No online speech: nothing is ever sent. The default.</summary>
    None,

    /// <summary>Microsoft Azure Speech, in the region the user named.</summary>
    Azure,

    /// <summary>Any service speaking the OpenAI <c>/audio/speech</c> API, at the address the user named.</summary>
    OpenAiCompatible,
}

/// <summary>
/// The lower-case wire tokens of <see cref="SpeechProvider"/>. Lower case on purpose, like
/// <c>ReflectionMood</c>: it is a value the user picks in the UI, not a captured domain enum.
/// </summary>
public static class SpeechProviderWire
{
    /// <summary>Every token, in declaration order.</summary>
    public static IReadOnlyList<string> AllTokens { get; } = Array.AsReadOnly(new[] { "none", "azure", "openai_compatible" });

    /// <summary>The token of a provider.</summary>
    /// <param name="provider">Provider.</param>
    public static string Format(SpeechProvider provider) => provider switch
    {
        SpeechProvider.None => "none",
        SpeechProvider.Azure => "azure",
        SpeechProvider.OpenAiCompatible => "openai_compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Parses a token; exact, case-sensitive match only.</summary>
    /// <param name="text">Token.</param>
    /// <param name="provider">Parsed provider.</param>
    public static bool TryParse(string? text, out SpeechProvider provider)
    {
        switch (text)
        {
            case "none":
                provider = SpeechProvider.None;
                return true;
            case "azure":
                provider = SpeechProvider.Azure;
                return true;
            case "openai_compatible":
                provider = SpeechProvider.OpenAiCompatible;
                return true;
            default:
                provider = SpeechProvider.None;
                return false;
        }
    }
}

/// <summary>
/// The online speech settings the Collector keeps, without the key. Every string is already
/// normalised and valid for its field; a field that does not apply to the provider is kept as the
/// user left it, so switching services back and forth does not lose what was typed.
/// </summary>
/// <param name="Provider">Service in force.</param>
/// <param name="AzureRegion">Azure region, lower case.</param>
/// <param name="OpenAiBaseUrl">OpenAI-compatible base address, without a trailing slash.</param>
/// <param name="OpenAiModel">OpenAI-compatible model name.</param>
/// <param name="Voice">Voice name of the service in force.</param>
public sealed record OnlineSpeechConfig(
    SpeechProvider Provider,
    string? AzureRegion,
    string? OpenAiBaseUrl,
    string? OpenAiModel,
    string? Voice)
{
    /// <summary>Online speech off, nothing filled in.</summary>
    public static OnlineSpeechConfig Off { get; } = new(SpeechProvider.None, null, null, null, null);

    /// <summary>
    /// True when a request could be built: a service is chosen and every field it needs is present
    /// and valid. The key is checked separately.
    /// </summary>
    public bool IsComplete => Provider switch
    {
        SpeechProvider.Azure =>
            SpeechValidation.IsRegion(AzureRegion) && SpeechValidation.IsVoice(Provider, Voice),
        SpeechProvider.OpenAiCompatible =>
            SpeechValidation.TryNormalizeBaseUrl(OpenAiBaseUrl, out _, out _) &&
            SpeechValidation.IsModel(OpenAiModel) &&
            SpeechValidation.IsVoice(Provider, Voice),
        _ => false,
    };
}

/// <summary>
/// Input rules for everything the user types into the online speech panel, and for the sentence
/// to speak. Each rule exists because the value ends up in an address, a request header, an XML
/// document or a JSON body.
/// </summary>
public static partial class SpeechValidation
{
    /// <summary>Longest sentence, in Unicode code points (what JSON Schema's maxLength counts).</summary>
    public const int MaxTextLength = 200;

    /// <summary>Slowest speaking rate, in percent of normal.</summary>
    public const int MinRatePercent = 50;

    /// <summary>Fastest speaking rate, in percent of normal.</summary>
    public const int MaxRatePercent = 200;

    /// <summary>Normal speaking rate.</summary>
    public const int DefaultRatePercent = 100;

    /// <summary>Longest accepted key.</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Longest accepted base address.</summary>
    public const int MaxBaseUrlLength = 2048;

    /// <summary>Longest model or OpenAI-compatible voice name.</summary>
    public const int MaxNameLength = 128;

    /// <summary>True for an Azure region: 2-32 lower-case letters or digits.</summary>
    /// <param name="region">Candidate.</param>
    public static bool IsRegion(string? region) => region is not null && RegionPattern().IsMatch(region);

    /// <summary>
    /// Trims and lower-cases a region the user typed. False when the result is not a region, so a
    /// dot, a slash or a port can never be smuggled into the host name built from it.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <param name="region">Normalised region.</param>
    public static bool TryNormalizeRegion(string raw, out string region)
    {
        ArgumentNullException.ThrowIfNull(raw);
        region = raw.Trim().ToLowerInvariant();
        return IsRegion(region);
    }

    /// <summary>
    /// True for a voice name the provider accepts. Azure voice names go into an SSML attribute and
    /// are held to letters, digits and dashes; OpenAI-compatible services name voices more freely
    /// (<c>alloy</c>, <c>vendor/model:speaker</c>), which a JSON string carries safely.
    /// </summary>
    /// <param name="provider">Provider the voice is for.</param>
    /// <param name="voice">Candidate.</param>
    public static bool IsVoice(SpeechProvider provider, string? voice) => voice is not null && provider switch
    {
        SpeechProvider.Azure => AzureVoicePattern().IsMatch(voice),
        SpeechProvider.OpenAiCompatible => NamePattern().IsMatch(voice),
        _ => false,
    };

    /// <summary>True for an OpenAI-compatible model name.</summary>
    /// <param name="model">Candidate.</param>
    public static bool IsModel(string? model) => model is not null && NamePattern().IsMatch(model);

    /// <summary>
    /// Normalises an OpenAI-compatible base address: absolute; <c>https</c>, or <c>http</c> for
    /// <c>127.0.0.1</c>, <c>::1</c> and <c>localhost</c> only; no user name or password, no query and no
    /// fragment; no whitespace; trailing slashes removed.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <param name="normalized">Normalised address, e.g. <c>https://api.example.com/v1</c>.</param>
    /// <param name="uri">The parsed address.</param>
    public static bool TryNormalizeBaseUrl(string? raw, out string normalized, out Uri? uri)
    {
        normalized = string.Empty;
        uri = null;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxBaseUrlLength ||
            text.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)) ||
            text.Contains('?') || text.Contains('#') || text.Contains('\\'))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            string.IsNullOrEmpty(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.Query.Length > 0 ||
            parsed.Fragment.Length > 0)
        {
            return false;
        }

        var secure = parsed.Scheme == Uri.UriSchemeHttps;
        var loopbackHttp = parsed.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(parsed);
        if (!secure && !loopbackHttp)
        {
            return false;
        }

        var left = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (!Uri.TryCreate(left, UriKind.Absolute, out var rebuilt))
        {
            return false;
        }

        normalized = left;
        uri = rebuilt;
        return true;
    }

    /// <summary>True for the three loopback names plain http is allowed to: 127.0.0.1, ::1, localhost.</summary>
    /// <param name="uri">An absolute address.</param>
    public static bool IsLoopbackHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host == "127.0.0.1" ||
               uri.Host == "[::1]";
    }

    /// <summary>
    /// Trims a key and checks it can travel in a request header: 1-512 visible ASCII characters, no
    /// space, no line break.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <param name="key">Trimmed key.</param>
    public static bool TryNormalizeKey(string raw, out string key)
    {
        ArgumentNullException.ThrowIfNull(raw);
        key = raw.Trim();
        return key.Length is > 0 and <= MaxKeyLength && key.All(ch => ch is >= '!' and <= '~');
    }

    /// <summary>
    /// Prepares the sentence to speak: tabs and line breaks become spaces, surrounding space is
    /// trimmed, and anything XML 1.0 cannot carry (other control characters, lone surrogates) is a
    /// refusal rather than a silent deletion. 1-200 code points.
    /// </summary>
    /// <param name="raw">Sentence as the Desktop rendered it.</param>
    /// <param name="text">Prepared sentence.</param>
    public static bool TryNormalizeText(string raw, out string text)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            builder.Append(ch is '\t' or '\r' or '\n' ? ' ' : ch);
        }

        text = builder.ToString().Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var codePoints = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (char.IsHighSurrogate(ch))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(ch) || !XmlConvert.IsXmlChar(ch))
            {
                return false;
            }

            codePoints++;
        }

        return codePoints <= MaxTextLength;
    }

    [GeneratedRegex(@"^[a-z0-9]{2,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]{3,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AzureVoicePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
