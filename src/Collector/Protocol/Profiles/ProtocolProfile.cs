using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// How much a profile is trusted. Only <see cref="Verified"/> may drive live recording;
/// <see cref="Synthetic"/> may drive offline replay and tests only.
/// </summary>
public enum ProfileCompatibilityStatus
{
    /// <summary>No verified evidence exists for this region and build. Fail-closed.</summary>
    Unsupported,

    /// <summary>Structure proposed but not yet confirmed on a real client. Fail-closed.</summary>
    Candidate,

    /// <summary>Every declared constant is backed by evidence. The only live-usable status.</summary>
    Verified,

    /// <summary>Invented for offline tests. Never describes a real game build.</summary>
    Synthetic,

    /// <summary>Two or more profiles claimed the same region and build; all of them are refused.</summary>
    Ambiguous,
}

/// <summary>Width and signedness of one profile-declared field.</summary>
public enum ProfileFieldType
{
    /// <summary>Unsigned 8-bit.</summary>
    U8,

    /// <summary>Unsigned 16-bit.</summary>
    U16,

    /// <summary>Unsigned 32-bit.</summary>
    U32,

    /// <summary>Signed 32-bit.</summary>
    I32,

    /// <summary>Unsigned 64-bit.</summary>
    U64,

    /// <summary>Opaque byte run of a declared length.</summary>
    Bytes,
}

/// <summary>What a declared field is for, and therefore what a constraint miss means.</summary>
public enum ProfileFieldRole
{
    /// <summary>
    /// A field whose value the record needs. Its constraints describe the shape of a message
    /// this profile claims to understand, so a value outside them is a refusal the
    /// diagnostics page must show.
    /// </summary>
    Value,

    /// <summary>
    /// A field that only decides whether the message is the one we are after. Its constraints
    /// are a filter, not a promise: the CN duty finder sends the same opcode for the
    /// application receipt and for every later state update, and only one value of
    /// <c>finder_state</c> is the pop. A miss means "not this message" and is counted as
    /// ignored, exactly like an opcode the profile never declared
    /// (docs/protocol-profile-format.md, review finding M-2).
    /// </summary>
    Selector,
}

/// <summary>Byte order of a multi-byte field.</summary>
public enum ProfileEndian
{
    /// <summary>Least significant byte first.</summary>
    Little,

    /// <summary>Most significant byte first.</summary>
    Big,
}

/// <summary>Value range a field must fall in for the message to be a verified observation.</summary>
/// <param name="Min">Inclusive lower bound, when declared.</param>
/// <param name="Max">Inclusive upper bound, when declared.</param>
/// <param name="In">Closed set of permitted values, when declared.</param>
public sealed record ProfileFieldConstraints(long? Min, long? Max, IReadOnlyList<long>? In)
{
    /// <summary>No constraint at all.</summary>
    public static ProfileFieldConstraints None { get; } = new(null, null, null);

    /// <summary>True when <paramref name="value"/> satisfies every declared constraint.</summary>
    /// <param name="value">Value read out of the payload.</param>
    public bool IsSatisfiedBy(long value)
    {
        if (Min is { } min && value < min)
        {
            return false;
        }

        if (Max is { } max && value > max)
        {
            return false;
        }

        return In is null || In.Contains(value);
    }
}

/// <summary>One field of a profile-declared message.</summary>
/// <param name="Name">Field name the parser looks for, for example <c>roulette_id</c>.</param>
/// <param name="Offset">Byte offset into the IPC payload.</param>
/// <param name="Type">Width and signedness.</param>
/// <param name="Length">Length of a <see cref="ProfileFieldType.Bytes"/> field.</param>
/// <param name="Endian">Byte order of a multi-byte field.</param>
/// <param name="Constraints">Value constraints.</param>
/// <param name="Role">Whether a constraint miss is a refusal or simply "not this message".</param>
public sealed record ProfileField(
    string Name,
    int Offset,
    ProfileFieldType Type,
    int Length,
    ProfileEndian Endian,
    ProfileFieldConstraints Constraints,
    ProfileFieldRole Role = ProfileFieldRole.Value)
{
    /// <summary>Number of bytes this field reads.</summary>
    public int Size => Type switch
    {
        ProfileFieldType.U8 => 1,
        ProfileFieldType.U16 => 2,
        ProfileFieldType.U32 => 4,
        ProfileFieldType.I32 => 4,
        ProfileFieldType.U64 => 8,
        _ => Length,
    };
}

/// <summary>One message a profile teaches the parser to recognise.</summary>
/// <param name="Name">Semantic name, matching <see cref="SemanticEvent.EventType"/>.</param>
/// <param name="Opcode">IPC opcode.</param>
/// <param name="Direction">Direction the message travels.</param>
/// <param name="SegmentType">Segment type it must carry, when the profile declares one.</param>
/// <param name="ExpectedLength">Exact payload length, when the profile declares one.</param>
/// <param name="MinLength">Inclusive lower payload length bound, when declared.</param>
/// <param name="MaxLength">Inclusive upper payload length bound, when declared.</param>
/// <param name="VictoryValues">Outcome values that mean victory; only for DUTY_RESULT.</param>
/// <param name="Fields">Declared fields, in declaration order.</param>
public sealed record ProfileMessage(
    string Name,
    ushort Opcode,
    PacketDirection Direction,
    ushort? SegmentType,
    int? ExpectedLength,
    int? MinLength,
    int? MaxLength,
    IReadOnlyList<long> VictoryValues,
    IReadOnlyList<ProfileField> Fields)
{
    /// <summary>Finds a declared field by name, or null.</summary>
    /// <param name="name">Field name.</param>
    public ProfileField? Field(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    /// <summary>True when <paramref name="length"/> satisfies the declared length rules.</summary>
    /// <param name="length">Observed payload length.</param>
    public bool AcceptsLength(int length)
    {
        if (ExpectedLength is { } exact)
        {
            return length == exact;
        }

        return (MinLength is not { } min || length >= min) &&
               (MaxLength is not { } max || length <= max);
    }
}

/// <summary>One offline fixture a profile vouches for.</summary>
/// <param name="Path">Path relative to the profile file.</param>
/// <param name="Sha256">Expected lowercase hex digest of the file.</param>
public sealed record ProfileFixtureReference(string Path, string Sha256);

/// <summary>仅供候选时间线观察的 opcode 假设；不声明字段，不产生状态机事件。</summary>
/// <param name="Name">档案内唯一的候选名称。</param>
/// <param name="Opcode">观察的 IPC opcode。</param>
/// <param name="Direction">报文方向；与 opcode 一起匹配。</param>
/// <param name="ExpectedLength">精确负载长度；与区间互斥。</param>
/// <param name="MinLength">允许的最小负载长度，包含边界。</param>
/// <param name="MaxLength">允许的最大负载长度，包含边界。</param>
/// <param name="Note">证据范围与仍未确认的含义。</param>
/// <param name="Group">可选观察分组；zone_load 参与连接内区域簇聚合。</param>
/// <param name="Label">可选的界面显示名（中文）；缺省时界面回退到 <paramref name="Name"/>。</param>
public sealed record ProfileHypothesis(
    string Name,
    ushort Opcode,
    PacketDirection Direction,
    int? ExpectedLength,
    int? MinLength,
    int? MaxLength,
    string Note,
    string? Group,
    string? Label = null)
{
    /// <summary>界面上显示的名称：档案声明的 <c>label</c>，否则退回候选名。</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    /// <summary>负载长度满足精确值或闭区间时返回 true；负长度始终拒绝。</summary>
    /// <param name="length">解码报文实际负载字节数。</param>
    public bool AcceptsLength(int length) => length >= 0 &&
        (ExpectedLength is { } exact
            ? length == exact
            : (MinLength is not { } min || length >= min) &&
              (MaxLength is not { } max || length <= max));
}

/// <summary>A loaded, schema-valid, hash-verified protocol profile.</summary>
/// <param name="ProfileId">Identifier; equals the file name stem.</param>
/// <param name="Region">Region this profile claims.</param>
/// <param name="GameBuild">Client build string this profile claims.</param>
/// <param name="GeneratedAtUtc">When the profile was written.</param>
/// <param name="MentorRouletteId">Mentor roulette id, or null when unknown.</param>
/// <param name="Status">How far the profile is trusted.</param>
/// <param name="MatchWindow">Match acceptance window declared by the profile.</param>
/// <param name="Messages">Declared messages, keyed by name in <see cref="Message"/>.</param>
/// <param name="ObfuscatedOpcodes">Opcodes this build scrambles; the parser refuses them.</param>
/// <param name="Fixtures">Fixtures the profile vouches for.</param>
/// <param name="ProvenanceSummary">Human explanation of where the constants came from.</param>
/// <param name="ProfileSha256">Verified canonical hash of the document.</param>
/// <param name="SourcePath">Absolute path the profile was read from.</param>
/// <param name="FixturesVerified">True when every referenced fixture was found and matched.</param>
public sealed record ProtocolProfile(
    string ProfileId,
    Region Region,
    string GameBuild,
    DateTimeOffset GeneratedAtUtc,
    int? MentorRouletteId,
    ProfileCompatibilityStatus Status,
    TimeSpan MatchWindow,
    IReadOnlyList<ProfileMessage> Messages,
    IReadOnlyList<ProfileFixtureReference> Fixtures,
    string ProvenanceSummary,
    string ProfileSha256,
    string SourcePath,
    bool FixturesVerified,
    IReadOnlyList<int>? ObfuscatedOpcodes = null)
{
    /// <summary>候选观察定义；只有显式开启的候选观察器使用，正式解析器不读取。</summary>
    public IReadOnlyList<ProfileHypothesis> Hypotheses { get; init; } = Array.Empty<ProfileHypothesis>();

    /// <summary>
    /// 本机校准模板：换版本后重新认出报文所需的形状知识。
    /// 只有 VERIFIED 档案可以携带；正式解析器不读取。
    /// </summary>
    public ProfileCalibration? Calibration { get; init; }

    /// <summary>
    /// Opcodes the client build is known to scramble. Empty in every profile shipped so far.
    ///
    /// This project never descrambles anything: doing so needs constants dumped from the game
    /// executable, and re-distributing those is out of the question
    /// (docs/protocol-profile-format.md section 8). What the list does is turn the assumption
    /// "the four packets we need are outside the obfuscated set" into data the parser
    /// enforces, so a future patch that starts scrambling one of them produces a refusal
    /// instead of plausible-looking nonsense.
    /// </summary>
    public IReadOnlyList<int> Obfuscated { get; } =
        ObfuscatedOpcodes ?? Array.Empty<int>();

    /// <summary>True when the profile declares this opcode as scrambled by the client.</summary>
    /// <param name="opcode">Opcode read off the wire.</param>
    public bool IsObfuscated(int opcode) => Obfuscated.Contains(opcode);

    /// <summary>
    /// True when this profile has no server message announcing a match and uses the client's
    /// own queue request instead. The direction says it: a match is something the server tells
    /// the client, so a CONTENT_FINDER_POP travelling the other way is the player's request
    /// standing in for an announcement nobody could identify on this build.
    /// </summary>
    public bool MatchFromQueue =>
        Message("CONTENT_FINDER_POP") is { Direction: PacketDirection.ClientToServer };

    /// <summary>Finds a declared message by semantic name, or null.</summary>
    /// <param name="name">Semantic message name.</param>
    public ProfileMessage? Message(string name) =>
        Messages.FirstOrDefault(message => string.Equals(message.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The binding the state machine gets. A synthetic profile produces the offline binding;
    /// everything else produces a live binding whose status decides whether it is usable, so
    /// CANDIDATE and UNSUPPORTED are both fail-closed without any special case.
    /// </summary>
    public ProfileBinding ToBinding()
    {
        if (Status == ProfileCompatibilityStatus.Synthetic)
        {
            return ProfileBinding.Synthetic(ProfileId, MentorRouletteId ?? 0);
        }

        var status = Status switch
        {
            ProfileCompatibilityStatus.Verified => ProfileStatus.Verified,
            ProfileCompatibilityStatus.Candidate => ProfileStatus.Unverified,
            _ => ProfileStatus.UnsupportedBuild,
        };
        // A profile that infers the match from the queue recognises a duty only by the
        // territory it lands in. Without ZONE_TERRITORY it can never enter one: every run
        // starts when the player queues and stays at "matched" for ever, and every record ends
        // unknown. Since 0.7.2 no such profile is written; this also keeps one already on disk
        // out of force, so calibration takes over again and replaces it.
        if (MatchFromQueue && Message("ZONE_TERRITORY") is null)
        {
            return ProfileBinding.FailClosed with { ProfileId = ProfileId, Region = Region };
        }

        return ProfileBinding.Live(
            ProfileId, Region, status, MentorRouletteId,
            canDetectDutyResult: Messages.Any(message => string.Equals(message.Name, "DUTY_RESULT", StringComparison.Ordinal)),
            matchFromQueue: MatchFromQueue);
    }
}

/// <summary>
/// The duty-finder action the client sends when the player picks a roulette, as a shape:
/// direction, exact length and where the roulette id sits. On a build with no profile the
/// calibration observer looks for a message of this shape whose roulette id is echoed by a
/// server message within <see cref="ProfileCalibration.FinderReplyMax"/>; that echo is what
/// identifies both opcodes without anyone guessing.
/// </summary>
/// <param name="Direction">Direction of the request; CLIENT_TO_SERVER for every known client.</param>
/// <param name="ExpectedLength">Exact payload length of the request.</param>
/// <param name="RouletteField">Where the roulette id is read from; its constraints filter the request.</param>
public sealed record CalibrationFinderRequest(
    PacketDirection Direction,
    int ExpectedLength,
    ProfileField RouletteField);

/// <summary>
/// Shape knowledge a template profile lends to the calibration observer. Everything here is
/// observation-only: it configures how a draft is proposed, never how a message is parsed.
/// </summary>
/// <param name="FinderRequest">The roulette request shape.</param>
/// <param name="FinderReplyMax">How long after the request its echo may arrive.</param>
public sealed record ProfileCalibration(
    CalibrationFinderRequest FinderRequest,
    TimeSpan FinderReplyMax);
