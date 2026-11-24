using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Loaded-profile records built in memory, for the binding and parsing rules alone. The opcodes are invented;
/// the CN shape is a pop and a zone change, with no DUTY_RESULT.
/// </summary>
internal static class InMemoryProfiles
{
    /// <summary>A server pop of 8 bytes whose roulette is a little-endian u16 at 0, at least 1.</summary>
    internal static readonly ProfileMessage Pop = new(
        "CONTENT_FINDER_POP", 0xA001, PacketDirection.ServerToClient, null, 8, null, null,
        Array.Empty<long>(),
        new[]
        {
            new ProfileField("roulette_id", 0, ProfileFieldType.U16, 0, ProfileEndian.Little,
                new ProfileFieldConstraints(1, null, null)),
        });

    /// <summary>An 8-byte zone change with no field.</summary>
    internal static readonly ProfileMessage Zone = new(
        "ZONE_INITIALIZATION", 0xA002, PacketDirection.ServerToClient, null, 8, null, null,
        Array.Empty<long>(), Array.Empty<ProfileField>());

    /// <summary>A declared 40-byte DUTY_RESULT whose first byte is the outcome; 7 is a victory.</summary>
    internal static readonly ProfileMessage DeclaredResult = new(
        "DUTY_RESULT", 0xA003, PacketDirection.ServerToClient, null, 40, null, null,
        new long[] { 7 },
        new[]
        {
            new ProfileField("outcome", 0, ProfileFieldType.U8, 0, ProfileEndian.Little, ProfileFieldConstraints.None),
        });

    /// <summary>A profile of one region and status declaring the given messages; an UNSUPPORTED one has no roulette.</summary>
    /// <param name="region">Region the profile records.</param>
    /// <param name="status">Compatibility status.</param>
    /// <param name="messages">Declared messages.</param>
    internal static ProtocolProfile Of(
        Region region, ProfileCompatibilityStatus status, params ProfileMessage[] messages) => new(
        "in-memory", region, "2026.09.15.0000.0000", new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
        status is ProfileCompatibilityStatus.Unsupported ? null : 9, status, TimeSpan.FromSeconds(120),
        messages, Array.Empty<ProfileFixtureReference>(), "in-memory test profile", new string('0', 64), "", false);

    /// <summary>A VERIFIED CN profile with the CN shape, plus any extra messages.</summary>
    /// <param name="extra">Messages declared after the pop and the zone change.</param>
    internal static ProtocolProfile Cn(params ProfileMessage[] extra) =>
        Of(Region.Cn, ProfileCompatibilityStatus.Verified, [Pop, Zone, .. extra]);

    /// <summary>
    /// The same profile with these opcodes declared scrambled. Built anew rather than with <c>with</c>:
    /// <see cref="ProtocolProfile.Obfuscated"/> is computed once, at construction.
    /// </summary>
    /// <param name="profile">Profile to copy.</param>
    /// <param name="opcodes">Opcodes the client build scrambles.</param>
    internal static ProtocolProfile WithObfuscated(ProtocolProfile profile, params int[] opcodes) => new(
        profile.ProfileId, profile.Region, profile.GameBuild, profile.GeneratedAtUtc, profile.MentorRouletteId,
        profile.Status, profile.MatchWindow, profile.Messages, profile.Fixtures, profile.ProvenanceSummary,
        profile.ProfileSha256, profile.SourcePath, profile.FixturesVerified, opcodes)
    {
        Hypotheses = profile.Hypotheses,
        Calibration = profile.Calibration,
    };
}
