using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Ipc;

/// <summary>One decoded request envelope.</summary>
/// <param name="RequestId">Client-generated UUID.</param>
/// <param name="MessageType">Message name from the contract.</param>
/// <param name="Payload">Request payload object.</param>
public sealed record IpcRequest(string RequestId, string MessageType, JsonObject Payload);

/// <summary>
/// Encoding and decoding of the JSON envelope that rides inside a frame.
///
/// A response carries <c>ok</c> plus either <c>payload</c> or <c>error</c>. The Desktop
/// client accepts both that shape and the schema's <c>message_type = "Error"</c> shape
/// (<c>src/Desktop/cpp/IpcClient.cpp</c>), so an error envelope here fills in both: the
/// error object for the <c>ok</c> path and an error payload for the Error path.
/// </summary>
public static class IpcEnvelope
{
    /// <summary>Protocol version implemented by this build.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Message type of an event frame.</summary>
    public const string EventMessageType = "Event";

    /// <summary>Message type used when the request type could not be determined.</summary>
    public const string ErrorMessageType = "Error";

    /// <summary>
    /// Decodes one frame body. Every failure is expressed as a contract error code rather
    /// than an exception type the dispatcher would have to interpret.
    /// </summary>
    /// <param name="body">UTF-8 JSON body of a frame.</param>
    public static IpcRequest ParseRequest(ReadOnlySpan<byte> body)
    {
        var bytes = body.ToArray();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new CollectorException(
                ErrorCodes.BadRequest, "请求不是合法的 JSON。", inner: ex);
        }

        // JsonNode reads a key or string only when something touches it, and throws there for
        // a lone surrogate escape or non-UTF-8 bytes. Refused before any handler runs.
        if (!WellFormedJson.TryParse(bytes, default, out var readable))
        {
            throw CollectorException.BadRequest("请求不是合法的 JSON。");
        }

        readable.Dispose();

        if (node is not JsonObject envelope)
        {
            throw CollectorException.BadRequest("请求信封必须是一个 JSON 对象。");
        }

        var requestId = ReadString(envelope, "request_id");
        if (requestId is null || !Guid.TryParseExact(requestId, "D", out _))
        {
            throw CollectorException.BadRequest("request_id 必须是标准 UUID。", "request_id");
        }

        var version = ReadInt(envelope, "protocol_version");
        if (version != ProtocolVersion)
        {
            throw new CollectorException(
                ErrorCodes.ProtocolVersion,
                "协议版本不匹配，需要重新安装以使两端版本一致。",
                new Dictionary<string, object?>
                {
                    ["supported"] = ProtocolVersion,
                    ["received"] = version,
                });
        }

        var messageType = ReadString(envelope, "message_type");
        if (string.IsNullOrEmpty(messageType))
        {
            throw CollectorException.BadRequest("message_type 缺失。", "message_type");
        }

        var payload = envelope["payload"];
        if (payload is null)
        {
            return new IpcRequest(requestId, messageType, new JsonObject());
        }

        if (payload is not JsonObject payloadObject)
        {
            throw CollectorException.BadRequest("payload 必须是一个 JSON 对象。", "payload");
        }

        return new IpcRequest(requestId, messageType, payloadObject);
    }

    /// <summary>
    /// Best-effort extraction of <c>request_id</c> from a frame that failed to parse, so a
    /// rejection can still be correlated by the client instead of being reported against a
    /// zero id it never sent.
    /// </summary>
    /// <param name="body">UTF-8 JSON body of a frame.</param>
    public static string? TryPeekRequestId(ReadOnlySpan<byte> body)
    {
        try
        {
            if (JsonNode.Parse(body.ToArray()) is JsonObject envelope &&
                envelope["request_id"] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                Guid.TryParseExact(text, "D", out _))
            {
                return text;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or an id that cannot be read as text: answered against the zero id.
            return null;
        }

        return null;
    }

    /// <summary>Builds a success response envelope.</summary>
    /// <param name="requestId">Request being answered.</param>
    /// <param name="messageType">Message type of the request.</param>
    /// <param name="payload">Response payload.</param>
    public static JsonObject Success(string requestId, string messageType, JsonObject payload) => new()
    {
        ["protocol_version"] = ProtocolVersion,
        ["request_id"] = requestId,
        ["message_type"] = messageType,
        ["ok"] = true,
        ["payload"] = payload,
    };

    /// <summary>Builds a failure response envelope.</summary>
    /// <param name="requestId">Request being answered; a zero UUID when it was unreadable.</param>
    /// <param name="messageType">Message type of the request, or <c>Error</c> when unknown.</param>
    /// <param name="error">Error to report.</param>
    public static JsonObject Failure(string requestId, string messageType, CollectorException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var payload = new JsonObject
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["field"] = error.Field,
            ["retryable"] = error.Retryable,
        };

        if (error.Details is { Count: > 0 })
        {
            var details = new JsonObject();
            foreach (var (key, value) in error.Details)
            {
                details[key] = Wire.Value(value);
            }

            payload["details"] = details;
        }

        return new JsonObject
        {
            ["protocol_version"] = ProtocolVersion,
            ["request_id"] = requestId,
            ["message_type"] = messageType,
            ["ok"] = false,
            ["error"] = payload.DeepClone(),
            ["payload"] = payload,
        };
    }

    /// <summary>Builds an event envelope for a subscription.</summary>
    /// <param name="subscriptionRequestId">Request id of the subscription this event belongs to.</param>
    /// <param name="liveEvent">Rendered live event payload.</param>
    public static JsonObject Event(string subscriptionRequestId, JsonObject liveEvent) => new()
    {
        ["protocol_version"] = ProtocolVersion,
        ["request_id"] = subscriptionRequestId,
        ["message_type"] = EventMessageType,
        ["ok"] = true,
        ["payload"] = liveEvent,
    };

    /// <summary>Serialises an envelope to the UTF-8 bytes that go into a frame.</summary>
    /// <param name="envelope">Envelope to serialise.</param>
    public static byte[] ToBytes(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Encoding.UTF8.GetBytes(envelope.ToJsonString(Wire.JsonOptions));
    }

    private static string? ReadString(JsonObject envelope, string name) =>
        envelope[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? ReadInt(JsonObject envelope, string name) =>
        envelope[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
