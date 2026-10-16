using MentorRecorder.Collector.Contracts.Texts;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Why a share code was refused. <see cref="Code"/> is the stable token the Python tools and the
/// shared test vectors use; <see cref="Text"/> is what a player reads (a catalogue text, no opcodes) and
/// <see cref="Message"/> its Chinese; <see cref="Detail"/> names the offending key or step for logs and the
/// diagnostics report. tools/shared-calibration/test_public_repo_files.py reads the arms of <see cref="TextFor"/>
/// to hold sharecode.py's MESSAGES to the catalogue, so keep one <c>Token[ or Token] => CollectorTexts.X()</c> per arm.
/// </summary>
/// <param name="Code">Stable rejection token, <c>E_SHARE_CODE_*</c>.</param>
/// <param name="Text">Plain-language explanation for the player (contracts/collector-texts.json).</param>
/// <param name="Detail">Short technical detail; never contains payload content beyond a key name.</param>
public sealed record ShareCodeRejection(string Code, UserText Text, string Detail)
{
    /// <summary>The explanation in Simplified Chinese, as a Desktop that predates the catalogue shows it.</summary>
    public string Message => Text.Chinese;

    /// <summary>Nothing was pasted.</summary>
    public const string Empty = "E_SHARE_CODE_EMPTY";

    /// <summary>The text is longer than <see cref="ShareCode.MaxCodeLength"/>.</summary>
    public const string TooLong = "E_SHARE_CODE_TOO_LONG";

    /// <summary>The text does not start with a share-code prefix at all.</summary>
    public const string NotACode = "E_SHARE_CODE_NOT_A_CODE";

    /// <summary>The prefix names another code version.</summary>
    public const string Version = "E_SHARE_CODE_VERSION";

    /// <summary>The body holds a character outside base64url (padding included).</summary>
    public const string Characters = "E_SHARE_CODE_CHARACTERS";

    /// <summary>The body is empty or has a length base64 cannot produce.</summary>
    public const string Base64 = "E_SHARE_CODE_BASE64";

    /// <summary>The bytes are not a DEFLATE stream.</summary>
    public const string Compression = "E_SHARE_CODE_COMPRESSION";

    /// <summary>The stream inflates past <see cref="ShareCode.MaxInflatedBytes"/>.</summary>
    public const string InflatedTooLong = "E_SHARE_CODE_INFLATED_TOO_LONG";

    /// <summary>
    /// The inflated text is not a JSON object, or not readable as text: bytes that are not UTF-8, or a key or
    /// string holding a lone surrogate escape.
    /// </summary>
    public const string Json = "E_SHARE_CODE_JSON";

    /// <summary>An object declares the same key twice.</summary>
    public const string DuplicateKey = "E_SHARE_CODE_DUPLICATE_KEY";

    /// <summary>The payload layout version is not one this build reads.</summary>
    public const string PayloadVersion = "E_SHARE_CODE_PAYLOAD_VERSION";

    /// <summary>A key the format does not define.</summary>
    public const string UnknownKey = "E_SHARE_CODE_UNKNOWN_KEY";

    /// <summary>A required key is absent.</summary>
    public const string MissingKey = "E_SHARE_CODE_MISSING_KEY";

    /// <summary>A key holds a value of the wrong type or out of range.</summary>
    public const string Value = "E_SHARE_CODE_VALUE";

    /// <summary>The pop carries a key its match source does not use.</summary>
    public const string PopKeyForbidden = "E_SHARE_CODE_POP_KEY_FORBIDDEN";

    /// <summary>The pop lacks a key its match source needs.</summary>
    public const string PopKeyMissing = "E_SHARE_CODE_POP_KEY_MISSING";

    /// <summary>A queue-inferred code without the territory message.</summary>
    public const string TerritoryRequired = "E_SHARE_CODE_TERRITORY_REQUIRED";

    /// <summary>Valid content not written in the one canonical form.</summary>
    public const string NotCanonical = "E_SHARE_CODE_NOT_CANONICAL";

    /// <summary>Builds the rejection for a token, with the player's explanation looked up.</summary>
    /// <param name="code">Rejection token.</param>
    /// <param name="detail">Technical detail.</param>
    internal static ShareCodeRejection For(string code, string detail) => new(code, TextFor(code), detail);

    private static UserText TextFor(string code) => code switch
    {
        Empty => CollectorTexts.SharingCodeEmpty(),
        TooLong => CollectorTexts.SharingCodeTooLong(),
        NotACode => CollectorTexts.SharingCodeNotACode(),
        Version => CollectorTexts.SharingCodeNewerVersion(),
        Characters => CollectorTexts.SharingCodeCharacters(),
        Base64 => CollectorTexts.SharingCodeTruncated(),
        Compression => CollectorTexts.SharingCodeDamaged(),
        InflatedTooLong => CollectorTexts.SharingCodeTooLarge(),
        Json or DuplicateKey or UnknownKey or MissingKey or Value or PopKeyForbidden or PopKeyMissing or NotCanonical =>
            CollectorTexts.SharingCodeContentInvalid(),
        PayloadVersion => CollectorTexts.SharingCodeNewerVersion(),
        TerritoryRequired => CollectorTexts.SharingCodeTerritoryRequired(),
        _ => CollectorTexts.SharingCodeUnusable(),
    };
}
