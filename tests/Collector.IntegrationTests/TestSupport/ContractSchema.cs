using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Loads contracts/ipc-v1.schema.json and validates real wire JSON against it.
///
/// The schema is read from the repository, never restated here: a test that hard-codes the
/// expected shape only proves the test agrees with itself. This one fails when the Collector
/// writes a field the contract does not declare, and when the contract declares a field the
/// Collector never writes.
///
/// Subschemas are extracted by JSON pointer and re-rooted with a copy of <c>$defs</c>, so a
/// pointer into <c>$defs/Responses</c> (a documentation object, not a schema keyword) resolves
/// like a pointer into <c>$defs</c>, and the <c>#/$defs/...</c> references inside the extracted
/// fragment keep working.
/// </summary>
public static class ContractSchema
{
    private static readonly Lazy<JsonObject> Document = new(Load, isThreadSafe: true);

    private static readonly Dictionary<string, JsonSchema> Cache = new(StringComparer.Ordinal);

    // One evaluation at a time. JsonSchema.Net builds a schema's constraints and resolves its
    // $ref targets lazily on first evaluation, so two test classes evaluating the same cached
    // instance concurrently can each see a half-built schema and accept an instance the contract
    // refuses. Evaluations take microseconds, so serialising them costs nothing.
    private static readonly object EvaluationGate = new();

    /// <summary>Absolute path of the contract file, found by walking up from the test binary.</summary>
    public static string Path { get; } = RepositoryFiles.Find("contracts", "ipc-v1.schema.json");

    /// <summary>The repository root: the folder that holds <c>contracts/</c>.</summary>
    public static string RepositoryRoot => System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(Path))!;

    /// <summary>The whole contract document, parsed.</summary>
    public static JsonObject Root => Document.Value;

    /// <summary>
    /// The schema at a JSON pointer inside the contract, for example
    /// <c>$defs/ResponseEnvelope</c> or <c>$defs/Responses/GetVersion</c>.
    /// </summary>
    /// <param name="pointer">
    /// Slash-separated path under the document root; the empty string is the whole contract,
    /// including request, business reply, event, and error envelopes.
    /// </param>
    public static JsonSchema At(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);

        lock (Cache)
        {
            if (Cache.TryGetValue(pointer, out var cached))
            {
                return cached;
            }

            var built = Build(pointer);
            Cache[pointer] = built;
            return built;
        }
    }

    /// <summary>
    /// Evaluates <paramref name="instance"/> against the schema at <paramref name="pointer"/>.
    /// Always use this rather than <c>At(pointer).Evaluate(...)</c>: see <see cref="EvaluationGate"/>.
    /// </summary>
    /// <param name="pointer">Pointer of the schema to evaluate against.</param>
    /// <param name="instance">JSON to evaluate.</param>
    /// <param name="options">Evaluation options; the library defaults when null.</param>
    public static EvaluationResults Evaluate(string pointer, JsonNode? instance, EvaluationOptions? options = null)
    {
        var schema = At(pointer);
        lock (EvaluationGate)
        {
            return options is null ? schema.Evaluate(instance) : schema.Evaluate(instance, options);
        }
    }

    /// <summary>
    /// Validates <paramref name="instance"/> and throws with the failing keyword locations
    /// when it does not match.
    /// </summary>
    /// <param name="pointer">Pointer of the schema to validate against.</param>
    /// <param name="instance">JSON to validate.</param>
    /// <param name="what">What the instance is, for the failure message.</param>
    public static void Validate(string pointer, JsonNode? instance, string what)
    {
        var results = Evaluate(
            pointer,
            instance,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = false });

        if (results.IsValid)
        {
            return;
        }

        var message = new StringBuilder();
        message.Append(what).Append(" does not match ").Append(pointer).AppendLine(":");
        Describe(results, message);
        message.AppendLine("instance:");
        message.AppendLine(instance?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// The payload of an answered request, validated against the response schema of its message type. A refusal
    /// fails the test with its message.
    /// </summary>
    /// <param name="response">The response to a request.</param>
    /// <param name="what">What the payload is, for the failure message.</param>
    public static JsonObject Answered(IpcResponse response, string what)
    {
        Assert.True(response.Ok, response.MessageType + ": " + response.ErrorMessage);
        Validate("$defs/Responses/" + response.MessageType, response.Payload, what);
        return response.Payload;
    }

    /// <summary>
    /// The string members of the enum at a path below the document root, ordinal-sorted; a <c>null</c> member is
    /// left out.
    /// </summary>
    /// <param name="pointer">Path segments, for example <c>"$defs", "SpeechProvider", "enum"</c>.</param>
    public static string[] DeclaredEnum(params string[] pointer)
    {
        JsonNode? node = Root;
        foreach (var segment in pointer)
        {
            node = node![segment];
        }

        return node!.AsArray().OfType<JsonNode>().Select(item => item.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A request envelope as a client would frame it, for evaluating against the contract.</summary>
    /// <param name="messageType">Message type from the contract.</param>
    /// <param name="payload">Request payload; copied, so the caller can keep using it.</param>
    public static JsonObject RequestEnvelope(string messageType, JsonNode? payload) => new()
    {
        ["protocol_version"] = 1,
        ["request_id"] = "11111111-2222-4333-8444-555555555555",
        ["message_type"] = messageType,
        ["payload"] = payload?.DeepClone(),
    };

    /// <summary>Every message type the contract declares, minus <c>Event</c> and <c>Error</c>.</summary>
    public static IReadOnlyList<string> BusinessMessageTypes { get; } = Root["$defs"]!["MessageType"]!["enum"]!
        .AsArray()
        .Select(node => node!.GetValue<string>())
        .Where(name => name is not ("Event" or "Error"))
        .ToArray();

    /// <summary>Every message type that has a declared response payload schema.</summary>
    public static IReadOnlyList<string> ResponseMessageTypes { get; } = Root["$defs"]!["Responses"]!
        .AsObject()
        .Select(pair => pair.Key)
        .Where(name => !name.StartsWith("//", StringComparison.Ordinal) && name != "description")
        .ToArray();

    private static void Describe(EvaluationResults results, StringBuilder message)
    {
        if (results.HasErrors && results.Errors is { } errors)
        {
            foreach (var (keyword, detail) in errors)
            {
                message
                    .Append("  at ").Append(results.InstanceLocation)
                    .Append(" [").Append(keyword).Append("]: ")
                    .AppendLine(detail);
            }
        }

        if (results.Details is null)
        {
            return;
        }

        foreach (var nested in results.Details.Where(detail => !detail.IsValid))
        {
            Describe(nested, message);
        }
    }

    private static JsonSchema Build(string pointer)
    {
        if (pointer.Length == 0)
        {
            return JsonSchema.FromText(Root.ToJsonString());
        }

        JsonNode? node = Root;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node?[segment]
                ?? throw new InvalidOperationException($"契约中没有 {pointer}（缺少 {segment}）。");
        }

        var fragment = node!.DeepClone().AsObject();

        // Re-root: the fragment keeps its own "#/$defs/..." references, so it needs its own
        // copy of $defs, and its own $id so two fragments never collide in the registry.
        fragment["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        fragment["$id"] = "https://mentorrecorder.local/contracts/test/" +
            Uri.EscapeDataString(pointer);
        fragment["$defs"] = Root["$defs"]!.DeepClone();

        return JsonSchema.FromText(fragment.ToJsonString());
    }

    private static JsonObject Load()
    {
        var text = File.ReadAllText(Path);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("契约文件不是一个 JSON 对象。");
    }
}
