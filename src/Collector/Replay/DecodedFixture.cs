using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;

namespace MentorRecorder.Collector.Replay;

/// <summary>A validated synthetic decoded-message fixture.</summary>
/// <param name="FixtureId">Stable identifier, matching the file name stem.</param>
/// <param name="Description">Human explanation of what the fixture exercises.</param>
/// <param name="Sha256">Hash of the file, verified against the sidecar.</param>
/// <param name="ProfileId">Profile the fixture expects to be parsed with.</param>
/// <param name="GameBuild">Client build the fixture claims to come from.</param>
/// <param name="CaptureSessionId">Synthetic capture session id.</param>
/// <param name="StartedAtUtc">Wall-clock time of <c>t_ms = 0</c>.</param>
/// <param name="Messages">Ordered decoded messages.</param>
public sealed record DecodedFixture(
    string FixtureId,
    string Description,
    string Sha256,
    string ProfileId,
    string GameBuild,
    string CaptureSessionId,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<DecodedMessage> Messages);

/// <summary>
/// Loads synthetic decoded-message fixtures: the byte level of the offline test tree.
///
/// A fixture carries invented opcodes and invented payload bytes for the invented profile in
/// <c>protocol-profiles/synthetic/</c>. It must declare <c>synthetic: true</c>; a file that
/// does not is refused outright, so a real capture can never be smuggled into the test tree
/// and quietly replayed (docs/privacy-boundary.md section 7, tests/Fixtures/README.md).
/// </summary>
public static class DecodedFixtureLoader
{
    /// <summary>Only format version this build understands.</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>Loads and validates one decoded fixture file.</summary>
    /// <param name="path">Path to the <c>.decoded.json</c> file.</param>
    public static DecodedFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sidecarPath = fullPath + ".sha256";
        if (!File.Exists(sidecarPath))
        {
            throw new InvalidDataException("decoded fixture SHA-256 sidecar is missing: " + sidecarPath);
        }

        var expectedHash = File.ReadAllText(sidecarPath).Trim().ToLowerInvariant();
        if (expectedHash.Length != 64 || !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("decoded fixture SHA-256 mismatch");
        }

        var document = JsonSerializer.Deserialize<FixtureDocument>(bytes)
            ?? throw new InvalidDataException("decoded fixture document is empty");
        Validate(document);

        var fixtureId = document.FixtureId!;
        var sessionId = document.CaptureSessionId ?? DefaultSessionId(fixtureId);
        var started = document.StartedAtUtc is null
            ? new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero)
            : UtcTimestamp.Parse(document.StartedAtUtc);

        var messages = new List<DecodedMessage>(document.Messages!.Length);
        foreach (var row in document.Messages)
        {
            messages.Add(new DecodedMessage(
                sessionId,
                string.Equals(row.Direction, "CLIENT_TO_SERVER", StringComparison.Ordinal)
                    ? MessageDirection.Outbound
                    : MessageDirection.Inbound,
                started.AddMilliseconds(row.TimeMs),
                TimeSpan.FromMilliseconds(row.TimeMs),
                row.Epoch,
                (ushort)row.SegmentType,
                (ushort)row.Opcode,
                ParseHex(row.PayloadHex!),
                "synthetic-connection"));
        }

        return new DecodedFixture(
            fixtureId,
            document.Description ?? string.Empty,
            actualHash,
            document.Profile!,
            document.GameBuild!,
            sessionId,
            started,
            messages);
    }

    /// <summary>Deterministic session id used when a fixture does not declare one.</summary>
    /// <param name="fixtureId">Fixture identifier.</param>
    public static string DefaultSessionId(string fixtureId) =>
        SemanticEventProcessor.DeterministicId("decoded-fixture-session:" + fixtureId);

    private static void Validate(FixtureDocument document)
    {
        if (document.FormatVersion != SupportedFormatVersion ||
            string.IsNullOrWhiteSpace(document.FixtureId) ||
            string.IsNullOrWhiteSpace(document.Profile) ||
            string.IsNullOrWhiteSpace(document.GameBuild) ||
            document.Messages is null || document.Messages.Length == 0)
        {
            throw new InvalidDataException("decoded fixture header is invalid");
        }

        if (!document.Synthetic)
        {
            throw new InvalidDataException(
                "a decoded fixture must declare synthetic: true; real captures never enter the test tree");
        }

        if (document.CaptureSessionId is { } sessionId && !Guid.TryParseExact(sessionId, "D", out _))
        {
            throw new InvalidDataException("decoded fixture capture_session_id is not a UUID");
        }

        if (document.StartedAtUtc is { } started && !UtcTimestamp.TryParse(started, out _))
        {
            throw new InvalidDataException("decoded fixture started_at_utc is not a UTC timestamp");
        }

        long previous = -1;
        foreach (var row in document.Messages)
        {
            if (row.TimeMs < previous || row.TimeMs < 0 ||
                row.Opcode is < 0 or > ushort.MaxValue ||
                row.SegmentType is < 0 or > ushort.MaxValue ||
                row.Epoch < 0 || row.PayloadHex is null)
            {
                throw new InvalidDataException(
                    "decoded fixture messages must be ordered by t_ms and carry a payload");
            }

            if (row.Direction is not ("SERVER_TO_CLIENT" or "CLIENT_TO_SERVER"))
            {
                throw new InvalidDataException("decoded fixture direction must be SERVER_TO_CLIENT or CLIENT_TO_SERVER");
            }

            previous = row.TimeMs;
        }
    }

    private static byte[] ParseHex(string text)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length % 2 != 0)
        {
            throw new InvalidDataException("payload_hex must have an even number of hex digits");
        }

        var bytes = new byte[compact.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(
                    compact.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out bytes[i]))
            {
                throw new InvalidDataException("payload_hex contains a non-hex character");
            }
        }

        return bytes;
    }

    private sealed record FixtureDocument
    {
        [JsonPropertyName("fixture_id")] public string? FixtureId { get; init; }

        [JsonPropertyName("format_version")] public int FormatVersion { get; init; }

        [JsonPropertyName("synthetic")] public bool Synthetic { get; init; }

        [JsonPropertyName("description")] public string? Description { get; init; }

        [JsonPropertyName("profile")] public string? Profile { get; init; }

        [JsonPropertyName("game_build")] public string? GameBuild { get; init; }

        [JsonPropertyName("capture_session_id")] public string? CaptureSessionId { get; init; }

        [JsonPropertyName("started_at_utc")] public string? StartedAtUtc { get; init; }

        [JsonPropertyName("messages")] public MessageRow[]? Messages { get; init; }
    }

    private sealed record MessageRow
    {
        [JsonPropertyName("t_ms")] public long TimeMs { get; init; }

        [JsonPropertyName("direction")] public string? Direction { get; init; }

        [JsonPropertyName("segment_type")] public int SegmentType { get; init; }

        [JsonPropertyName("opcode")] public int Opcode { get; init; }

        [JsonPropertyName("epoch")] public long Epoch { get; init; }

        [JsonPropertyName("payload_hex")] public string? PayloadHex { get; init; }
    }
}
