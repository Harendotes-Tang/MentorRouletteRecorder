using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The checked-in synthetic profile driven through the real framing reader: the profile presented as a verified one,
/// and the payloads of a run laid out as protocol-profiles/synthetic/synthetic-v1.json declares them.
/// </summary>
internal static class SyntheticTraffic
{
    /// <summary>CONTENT_FINDER_POP: roulette_id u16 at 0, content_id u32 at 4.</summary>
    public const ushort PopOpcode = 61441;

    /// <summary>ZONE_INITIALIZATION: territory_id, content_id, instance_id u32 at 0, 4, 8; is_duty_instance u8 at 12.</summary>
    public const ushort ZoneOpcode = 61442;

    /// <summary>DUTY_RESULT: outcome u8 at 0.</summary>
    public const ushort ResultOpcode = 61443;

    /// <summary>PLAYER_JOB: job_id u8 at 0.</summary>
    public const ushort JobOpcode = 61444;

    /// <summary>
    /// The synthetic profile as a verified CN profile for <paramref name="gameBuild"/>, remapped onto the IPC segment
    /// type. The profile on disk declares segment type 61440 so it can never match a real client, but the framing
    /// reader exposes an opcode only for the IPC segment type. Opcodes, offsets, lengths and constraints are untouched.
    /// </summary>
    /// <param name="profileId">Identifier stamped on the runs it produces.</param>
    /// <param name="gameBuild">Client build the profile claims to cover.</param>
    public static ProtocolProfile VerifiedProfile(string profileId, string gameBuild)
    {
        var path = SyntheticProfile.Path;
        var loaded = ProfileLoader.Validate(path).Profile
            ?? throw new InvalidOperationException("the checked-in synthetic profile must load: " + path);

        return loaded with
        {
            ProfileId = profileId,
            Region = Region.Cn,
            GameBuild = gameBuild,
            Status = ProfileCompatibilityStatus.Verified,
            Messages = loaded.Messages
                .Select(message => message with { SegmentType = FfxivFraming.SegmentTypeIpc })
                .ToArray(),
        };
    }

    /// <summary>A profile selector that answers with <paramref name="profile"/>, verified, for whatever game it is shown.</summary>
    /// <param name="profile">The profile in force.</param>
    /// <param name="reason">The selection's reason text.</param>
    /// <param name="origin">Where the profile came from; unknown when null.</param>
    public static Func<GameProcessDetection, ProfileSelection> Selector(
        ProtocolProfile profile, string reason = "integration test profile selected", ProfileOrigin? origin = null) =>
        game => new ProfileSelection(
            ProfileCompatibilityStatus.Verified,
            profile.ToBinding(),
            profile,
            game.Region,
            game.GameBuild,
            reason,
            origin);

    /// <summary>A framed IPC message carrying <paramref name="payload"/>.</summary>
    /// <param name="opcode">The message's opcode.</param>
    /// <param name="payload">The bytes after the IPC header.</param>
    public static byte[] IpcMessage(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode, payload.Length);
        payload.CopyTo(message.AsSpan(FfxivFraming.HeaderBytes));
        return message;
    }

    /// <summary>The payload of a duty pop.</summary>
    /// <param name="rouletteId">roulette_id.</param>
    /// <param name="contentId">content_id.</param>
    public static byte[] Pop(ushort rouletteId, uint contentId)
    {
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, rouletteId);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), contentId);
        return pop;
    }

    /// <summary>The payload of entering a duty instance.</summary>
    /// <param name="territoryId">territory_id.</param>
    /// <param name="contentId">content_id.</param>
    /// <param name="instanceId">instance_id.</param>
    public static byte[] Zone(uint territoryId, uint contentId, uint instanceId)
    {
        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, territoryId);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), contentId);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), instanceId);
        zone[12] = 1;
        return zone;
    }

    /// <summary>The payload of the player's job.</summary>
    /// <param name="jobId">job_id.</param>
    public static byte[] Job(byte jobId)
    {
        var job = new byte[4];
        job[0] = jobId;
        return job;
    }

    /// <summary>The payload of a duty result.</summary>
    /// <param name="outcome">outcome; 1 is a victory.</param>
    public static byte[] Result(byte outcome)
    {
        var result = new byte[4];
        result[0] = outcome;
        return result;
    }
}
