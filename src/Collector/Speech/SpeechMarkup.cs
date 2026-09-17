using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;

namespace MentorRecorder.Collector.Speech;

/// <summary>
/// The two request bodies: SSML for Azure, JSON for OpenAI-compatible services. Both carry the
/// sentence, the voice and the rate, and nothing else (docs/privacy-boundary.md §8.3).
/// </summary>
public static class SpeechMarkup
{
    /// <summary>Language of every announcement; the templates are Chinese.</summary>
    public const string Language = "zh-CN";

    /// <summary>Smallest speed the OpenAI speech API accepts.</summary>
    public const double MinOpenAiSpeed = 0.25;

    /// <summary>Largest speed the OpenAI speech API accepts.</summary>
    public const double MaxOpenAiSpeed = 4.0;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The SSML prosody rate for a rate in percent: 100 is <c>+0%</c>, 150 is <c>+50%</c>, 50 is <c>-50%</c>.
    /// </summary>
    /// <param name="ratePercent">Rate in percent of normal, 50-200.</param>
    public static string AzureRate(int ratePercent)
    {
        CheckRate(ratePercent);
        var delta = ratePercent - SpeechValidation.DefaultRatePercent;
        return (delta >= 0 ? "+" : "-") + Math.Abs(delta).ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The SSML document for one sentence. Voice and text are XML-escaped, so neither can close an
    /// element or an attribute early.
    /// </summary>
    /// <param name="voice">Azure voice name.</param>
    /// <param name="text">Prepared sentence (<see cref="SpeechValidation.TryNormalizeText"/>).</param>
    /// <param name="ratePercent">Rate in percent of normal.</param>
    public static string AzureSsml(string voice, string text, int ratePercent)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(text);
        return "<speak version='1.0' xml:lang='" + Language + "'>" +
               "<voice name='" + Escape(voice) + "'>" +
               "<prosody rate='" + AzureRate(ratePercent) + "'>" +
               Escape(text) +
               "</prosody></voice></speak>";
    }

    /// <summary>The OpenAI speed for a rate in percent: rate / 100, held to 0.25-4.0.</summary>
    /// <param name="ratePercent">Rate in percent of normal, 50-200.</param>
    public static double OpenAiSpeed(int ratePercent)
    {
        CheckRate(ratePercent);
        return Math.Clamp(ratePercent / 100.0, MinOpenAiSpeed, MaxOpenAiSpeed);
    }

    /// <summary>
    /// The JSON body <c>{model, input, voice, response_format: "wav", speed}</c>, UTF-8 without a BOM.
    /// </summary>
    /// <param name="model">Model name.</param>
    /// <param name="voice">Voice name.</param>
    /// <param name="text">Prepared sentence.</param>
    /// <param name="ratePercent">Rate in percent of normal.</param>
    public static byte[] OpenAiBody(string model, string voice, string text, int ratePercent)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(text);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("input", text);
            writer.WriteString("voice", voice);
            writer.WriteString("response_format", "wav");
            writer.WriteNumber("speed", OpenAiSpeed(ratePercent));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>UTF-8 bytes of an SSML document, without a BOM.</summary>
    /// <param name="ssml">Document.</param>
    public static byte[] Encode(string ssml) => Utf8.GetBytes(ssml);

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static void CheckRate(int ratePercent)
    {
        if (ratePercent is < SpeechValidation.MinRatePercent or > SpeechValidation.MaxRatePercent)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePercent), "the rate must be 50-200 percent");
        }
    }
}
