using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace MentorRecorder.Collector.Domain;

/// <summary>
/// Naming policy that turns a PascalCase C# identifier into the UPPER_SNAKE_CASE
/// token used on the wire and in the database (for example
/// <c>LeftOrAbandoned</c> becomes <c>LEFT_OR_ABANDONED</c>).
///
/// One policy is shared by JSON serialisation and by SQLite persistence so a
/// value can never be spelled one way in the contract and another way in a row.
/// </summary>
public sealed class UpperSnakeCaseNamingPolicy : JsonNamingPolicy
{
    /// <summary>Singleton instance.</summary>
    public static readonly UpperSnakeCaseNamingPolicy Instance = new();

    private UpperSnakeCaseNamingPolicy()
    {
    }

    /// <inheritdoc />
    public override string ConvertName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}

/// <summary>
/// Bidirectional mapping between an enum value and its UPPER_SNAKE_CASE wire token.
/// The maps are built once per closed generic type.
/// </summary>
/// <typeparam name="TEnum">Any enum declared in this namespace.</typeparam>
public static class EnumWire<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> ToText = Enum
        .GetValues<TEnum>()
        .ToFrozenDictionary(v => v, v => UpperSnakeCaseNamingPolicy.Instance.ConvertName(v.ToString()));

    private static readonly FrozenDictionary<string, TEnum> FromText = ToText
        .ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>All wire tokens of this enum, in declaration order.</summary>
    public static IReadOnlyList<string> AllTokens { get; } =
        Enum.GetValues<TEnum>().Select(Format).ToArray();

    /// <summary>All values of this enum, in declaration order.</summary>
    public static IReadOnlyList<TEnum> AllValues { get; } = Enum.GetValues<TEnum>();

    /// <summary>Formats <paramref name="value"/> as its wire token.</summary>
    public static string Format(TEnum value) =>
        ToText.TryGetValue(value, out var text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), value, "value is not a declared enum member");

    /// <summary>Parses a wire token. Returns false for anything unrecognised; never guesses.</summary>
    public static bool TryParse(string? text, out TEnum value)
    {
        if (text is not null && FromText.TryGetValue(text, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Parses a wire token or throws. Used where the source is our own database.</summary>
    public static TEnum Parse(string? text) =>
        TryParse(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a valid {typeof(TEnum).Name} token");
}
