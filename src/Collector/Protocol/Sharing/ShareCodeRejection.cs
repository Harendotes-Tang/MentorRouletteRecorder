namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Why a share code was refused. <see cref="Code"/> is the stable token the Python tools and the
/// shared test vectors use; <see cref="Message"/> is what a player reads (Chinese, no opcodes);
/// <see cref="Detail"/> names the offending key or step for logs and the diagnostics report.
/// </summary>
/// <param name="Code">Stable rejection token, <c>E_SHARE_CODE_*</c>.</param>
/// <param name="Message">Plain-language explanation for the player.</param>
/// <param name="Detail">Short technical detail; never contains payload content beyond a key name.</param>
public sealed record ShareCodeRejection(string Code, string Message, string Detail)
{
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

    /// <summary>Builds the rejection for a token, with the player's message looked up.</summary>
    /// <param name="code">Rejection token.</param>
    /// <param name="detail">Technical detail.</param>
    internal static ShareCodeRejection For(string code, string detail) => new(code, MessageFor(code), detail);

    private static string MessageFor(string code) => code switch
    {
        Empty => "没有粘贴任何内容。",
        TooLong => "内容太长，不是本软件生成的校准码。",
        NotACode => "这不是本软件的校准码：校准码以 MRC1. 开头。",
        Version => "这份校准码来自更新版本的软件，当前版本读不了；请先更新本软件。",
        Characters => "校准码里有不该出现的字符，可能复制时多了或少了内容；请重新完整复制一次。",
        Base64 => "校准码不完整，可能复制时少了一截；请重新完整复制一次。",
        Compression => "校准码已经损坏，解不开；请重新完整复制一次。",
        InflatedTooLong => "校准码解开后内容过大，不是有效的校准码。",
        Json or DuplicateKey or UnknownKey or MissingKey or Value or PopKeyForbidden or PopKeyMissing or NotCanonical =>
            "校准码的内容有误，不是本软件生成的有效校准码。",
        PayloadVersion => "这份校准码的内容格式来自更新版本的软件，当前版本读不了；请先更新本软件。",
        TerritoryRequired => "这份校准码缺少「这次进的是哪个副本」那条报文，按它记录只会记成未知副本，所以不能使用。",
        _ => "校准码无法使用。",
    };
}
