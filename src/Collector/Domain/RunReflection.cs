namespace MentorRecorder.Collector.Domain;

/// <summary>How a run felt to the mentor who ran it.</summary>
public enum ReflectionMood
{
    /// <summary>顺利 — the run went well.</summary>
    Good,

    /// <summary>一般 — nothing special either way.</summary>
    Ok,

    /// <summary>糟心 — the run was unpleasant.</summary>
    Bad,
}

/// <summary>
/// 导随心得: one diary entry the user wrote about a run.
///
/// Row-for-row mirror of <c>run_reflections</c> (docs/data-model.md section 1.3) and of
/// <c>$defs/RunReflection</c> in the IPC contract. It is deliberately not part of
/// <c>mentor_runs</c>: a reflection is the user's own writing rather than an observed fact,
/// so writing one bumps no revision and appends nothing to <c>run_revisions</c>.
/// </summary>
/// <param name="Mood">Mood the user picked.</param>
/// <param name="Text">Trimmed text; always between 1 and <see cref="ReflectionText.MaxLength"/> characters.</param>
/// <param name="CreatedAtUtc">When the first version of this reflection was written.</param>
/// <param name="UpdatedAtUtc">When it was last edited; equal to <paramref name="CreatedAtUtc"/> at first.</param>
public sealed record RunReflection(
    ReflectionMood Mood,
    string Text,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// The wire spelling of <see cref="ReflectionMood"/> and the bounds of the text.
///
/// Spelled out rather than derived from <see cref="EnumWire{TEnum}"/>: the three tokens are
/// lower case on the wire because the user picks them in the UI, exactly like
/// <c>trend_granularity</c>, and <c>EnumWire</c> would spell them UPPER_SNAKE_CASE.
/// </summary>
public static class ReflectionText
{
    /// <summary>Longest reflection the contract and the CHECK constraint accept.</summary>
    public const int MaxLength = 2000;

    /// <summary>Wire token of a mood.</summary>
    /// <param name="mood">Mood to render.</param>
    public static string Format(ReflectionMood mood) => mood switch
    {
        ReflectionMood.Good => "good",
        ReflectionMood.Ok => "ok",
        ReflectionMood.Bad => "bad",
        _ => throw new ArgumentOutOfRangeException(nameof(mood), mood, "not a declared mood"),
    };

    /// <summary>Parses a wire token. Returns false for anything unrecognised; never guesses.</summary>
    /// <param name="text">Token received from the client or read back from the database.</param>
    /// <param name="mood">Parsed mood.</param>
    public static bool TryParse(string? text, out ReflectionMood mood)
    {
        switch (text)
        {
            case "good":
                mood = ReflectionMood.Good;
                return true;
            case "ok":
                mood = ReflectionMood.Ok;
                return true;
            case "bad":
                mood = ReflectionMood.Bad;
                return true;
            default:
                mood = default;
                return false;
        }
    }

    /// <summary>Parses a wire token or throws. Used where the source is our own database.</summary>
    /// <param name="text">Stored token.</param>
    public static ReflectionMood Parse(string? text) =>
        TryParse(text, out var mood)
            ? mood
            : throw new FormatException($"'{text}' is not a valid ReflectionMood token");

    /// <summary>
    /// Normalises the text of a request: trimmed, and null when nothing is left. An empty
    /// reflection is not stored as an empty row -- it deletes the entry.
    /// </summary>
    /// <param name="text">Raw text from the request.</param>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
