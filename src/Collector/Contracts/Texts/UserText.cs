using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Contracts.Texts;

/// <summary>What an argument of a <see cref="UserText"/> holds (contracts/collector-texts.md).</summary>
public enum UserTextArgKind
{
    /// <summary>An integer, written as plain digits.</summary>
    Number,

    /// <summary>Shown as it is: a file name, a path, a field path, a version, the player's own words.</summary>
    Text,

    /// <summary>A duty name as the Chinese client spells it; the Desktop shows the official name in its language.</summary>
    Duty,

    /// <summary>A job name as the Chinese client spells it.</summary>
    Job,

    /// <summary>A roulette name as the Chinese client spells it.</summary>
    Roulette,

    /// <summary>An instant, shown as local <c>HH:mm</c>.</summary>
    Clock,

    /// <summary>Another catalogue text, rendered in the same language.</summary>
    Ref,
}

/// <summary>One named argument of a <see cref="UserText"/>.</summary>
public sealed class UserTextArg
{
    private UserTextArg(string name, UserTextArgKind kind, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Kind = kind;
        Value = value;
    }

    /// <summary>
    /// The longest text argument <c>$defs/TextRef</c> accepts. A longer value - a request key echoed
    /// back, an operating-system message - is cut to it, in the Chinese and on the wire alike, so the
    /// two never disagree and the reference stays valid.
    /// </summary>
    public const int MaxTextLength = 4000;

    /// <summary>The placeholder name, <c>row</c> for <c>{row}</c>.</summary>
    public string Name { get; }

    /// <summary>What the value is.</summary>
    public UserTextArgKind Kind { get; }

    /// <summary>A <see cref="long"/>, a <see cref="string"/>, a <see cref="DateTimeOffset"/> or a <see cref="UserText"/>.</summary>
    public object Value { get; }

    /// <summary>An integer argument.</summary>
    public static UserTextArg Number(string name, long value) => new(name, UserTextArgKind.Number, value);

    /// <summary>A verbatim argument; null becomes the empty string.</summary>
    public static UserTextArg Text(string name, string? value) => new(name, UserTextArgKind.Text, Clamp(value));

    /// <summary>A duty name in the Chinese client's spelling.</summary>
    public static UserTextArg Duty(string name, string? value) => new(name, UserTextArgKind.Duty, Clamp(value));

    /// <summary>A job name in the Chinese client's spelling.</summary>
    public static UserTextArg Job(string name, string? value) => new(name, UserTextArgKind.Job, Clamp(value));

    /// <summary>A roulette name in the Chinese client's spelling.</summary>
    public static UserTextArg Roulette(string name, string? value) => new(name, UserTextArgKind.Roulette, Clamp(value));

    /// <summary>An instant, shown as local <c>HH:mm</c>.</summary>
    public static UserTextArg Clock(string name, DateTimeOffset value) => new(name, UserTextArgKind.Clock, value);

    /// <summary>A nested catalogue text.</summary>
    public static UserTextArg Ref(string name, UserText value) => new(name, UserTextArgKind.Ref, value);

    private static string Clamp(string? value)
    {
        value ??= string.Empty;
        if (value.Length <= MaxTextLength)
        {
            return value;
        }

        // Room for the ellipsis, and never half of a surrogate pair.
        var cut = MaxTextLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "\u2026");
    }

    /// <summary>The value as the Simplified Chinese sentence spells it.</summary>
    internal string Chinese => Value switch
    {
        long number => number.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset at => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        UserText text => text.Chinese,
        _ => (string)Value,
    };

    /// <summary>The value as it travels in <c>$defs/TextRef.args</c>.</summary>
    internal JsonNode Wire => Value switch
    {
        long number => JsonValue.Create(number),
        DateTimeOffset at => JsonValue.Create(UtcTimestamp.ToText(at)),
        UserText text => text.ToJson(),
        _ => JsonValue.Create((string)Value),
    };
}

/// <summary>
/// One sentence the Collector shows the player: a key of contracts/collector-texts.json and its
/// arguments, together with the Simplified Chinese wording of that key.
///
/// On the wire the sentence travels twice: as the Chinese text in the field it always used
/// (<c>message</c>, <c>hint</c>, <c>blockers</c>, ...), for a Desktop that predates the keys, and as
/// a <c>$defs/TextRef</c> in the companion <c>*_ref</c> field, which the Desktop renders in the
/// interface language. Instances come from the generated factories on <see cref="CollectorTexts"/>.
/// </summary>
public sealed class UserText : IEquatable<UserText>
{
    private string? _chinese;

    /// <summary>Creates a text; called by the generated factories.</summary>
    /// <param name="key">The catalogue key.</param>
    /// <param name="template">The Simplified Chinese template with <c>{name}</c> placeholders.</param>
    /// <param name="args">One argument per placeholder.</param>
    public UserText(string key, string template, params UserTextArg[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(template);
        ArgumentNullException.ThrowIfNull(args);
        Key = key;
        Template = template;
        Args = args;
    }

    /// <summary>The catalogue key, for example <c>import.row.missing_duty</c>.</summary>
    public string Key { get; }

    /// <summary>The Simplified Chinese template of <see cref="Key"/>.</summary>
    public string Template { get; }

    /// <summary>The arguments, in the catalogue's order.</summary>
    public IReadOnlyList<UserTextArg> Args { get; }

    /// <summary>The finished Simplified Chinese sentence, exactly as a Desktop without the keys shows it.</summary>
    public string Chinese => _chinese ??= Render();

    /// <summary>The <c>$defs/TextRef</c> object: <c>{ key, args? }</c>.</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject { ["key"] = Key };
        if (Args.Count > 0)
        {
            var args = new JsonObject();
            foreach (var arg in Args)
            {
                args[arg.Name] = arg.Wire;
            }

            node["args"] = args;
        }

        return node;
    }

    /// <summary>A <c>*_refs</c> array parallel to the Chinese array of the same texts.</summary>
    /// <param name="texts">The texts, in the order of the Chinese array.</param>
    public static JsonArray ToJsonArray(IEnumerable<UserText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var array = new JsonArray();
        foreach (var text in texts)
        {
            array.Add(text.ToJson());
        }

        return array;
    }

    /// <summary>The Chinese sentence.</summary>
    public override string ToString() => Chinese;

    /// <inheritdoc />
    public bool Equals(UserText? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal) &&
        string.Equals(Chinese, other.Chinese, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UserText other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Chinese);

    private string Render()
    {
        var builder = new StringBuilder(Template.Length + 16);
        var index = 0;
        while (index < Template.Length)
        {
            var open = Template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(Template, index, Template.Length - index);
                break;
            }

            var close = Template.IndexOf('}', open + 1);
            if (close < 0)
            {
                throw new InvalidOperationException($"{Key}: an unclosed placeholder.");
            }

            builder.Append(Template, index, open - index);
            var name = Template.Substring(open + 1, close - open - 1);
            builder.Append(ArgNamed(name).Chinese);
            index = close + 1;
        }

        return builder.ToString();
    }

    private UserTextArg ArgNamed(string name)
    {
        foreach (var arg in Args)
        {
            if (string.Equals(arg.Name, name, StringComparison.Ordinal))
            {
                return arg;
            }
        }

        throw new InvalidOperationException($"{Key}: no argument for {{{name}}}.");
    }
}
