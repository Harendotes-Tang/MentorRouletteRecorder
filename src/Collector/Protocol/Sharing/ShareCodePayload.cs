using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// The learned half of the pop as a share code carries it (JSON object <c>pop</c>).
///
/// Which keys must be present is decided by the match source, through
/// <see cref="CalibratedShape.InputsFor"/>, and every key the source does not use is forbidden:
/// <list type="table">
/// <listheader><term>match_source</term><description>pop keys (all required, nothing else allowed)</description></listheader>
/// <item><term>REPLY_STATE</term><description><c>opcode</c>, <c>selector_values</c></description></item>
/// <item><term>ANNOUNCEMENT</term><description><c>opcode</c>, <c>length</c></description></item>
/// <item><term>MARKER_OFFSET</term><description><c>opcode</c>, <c>length</c>, <c>roulette_offset</c></description></item>
/// <item><term>QUEUE_REQUEST</term><description><c>opcode</c> (the client's request opcode)</description></item>
/// </list>
/// A forbidden key would be ignored by the rebuild, so accepting one would give one profile two
/// identities; that is why it is refused rather than dropped.
/// </summary>
/// <param name="Opcode">Pop opcode, 0..65535; the request opcode for QUEUE_REQUEST.</param>
/// <param name="Length">Announcement payload length, 1..65535.</param>
/// <param name="RouletteOffset">Byte offset of the one-byte roulette id, 0..length-1.</param>
/// <param name="SelectorValues">
/// Values of the template pop's learnable selectors, in the template's declaration order, at most
/// <see cref="ShareCode.MaxSelectorValues"/>. Names are not carried: the receiver's template names
/// them, and a count that does not match it makes the code unusable there.
/// </param>
public sealed record ShareCodePop(
    ushort Opcode,
    int? Length = null,
    int? RouletteOffset = null,
    IReadOnlyList<long>? SelectorValues = null);

/// <summary>
/// Everything a share code says: who it is for and the handful of values calibration learned.
/// Structure, the mentor roulette id and the match window come from the receiver's own template,
/// so they are deliberately absent, as are timestamps, sample counts, the roulettes played,
/// notes and paths.
///
/// JSON keys: <c>v</c> (1), <c>region</c> (CN | GLOBAL), <c>game_build</c> (the profile schema's
/// build pattern), <c>template_profile_id</c>, <c>template_sha256</c> (64 lowercase hex),
/// <c>match_source</c> (REPLY_STATE | ANNOUNCEMENT | MARKER_OFFSET | QUEUE_REQUEST),
/// <c>pop</c> (<see cref="ShareCodePop"/>), <c>zone_opcode</c>; optional <c>territory_opcode</c>
/// (required for QUEUE_REQUEST) and <c>job_opcode</c>. An absent optional value is an absent key,
/// never <c>null</c>. No other key is accepted at any level.
/// </summary>
/// <param name="Region">CN or GLOBAL.</param>
/// <param name="GameBuild">Client build the values were learned on.</param>
/// <param name="TemplateProfileId">Profile id of the template the sharer calibrated under.</param>
/// <param name="TemplateSha256">Canonical hash of that template.</param>
/// <param name="MatchSource">Which kind of evidence named the match.</param>
/// <param name="Pop">Learned half of the pop.</param>
/// <param name="ZoneOpcode">Opcode of <c>ZONE_INITIALIZATION</c>.</param>
/// <param name="TerritoryOpcode">Opcode of <c>ZONE_TERRITORY</c>, when declared.</param>
/// <param name="JobOpcode">Opcode of <c>PLAYER_JOB</c>, when declared.</param>
public sealed record ShareCodePayload(
    Region Region,
    string GameBuild,
    string TemplateProfileId,
    string TemplateSha256,
    CalibrationMatchSource MatchSource,
    ShareCodePop Pop,
    ushort ZoneOpcode,
    ushort? TerritoryOpcode = null,
    ushort? JobOpcode = null);

/// <summary>What decoding a pasted or downloaded code produced.</summary>
/// <param name="Payload">The payload, or null when refused.</param>
/// <param name="CodeSha256">Identity of the code: SHA-256 of the canonical payload; null when refused.</param>
/// <param name="Rejection">Why it was refused; null when accepted.</param>
public sealed record ShareCodeDecodeResult(ShareCodePayload? Payload, string? CodeSha256, ShareCodeRejection? Rejection)
{
    /// <summary>True when the code was accepted.</summary>
    public bool IsValid => Payload is not null;
}
