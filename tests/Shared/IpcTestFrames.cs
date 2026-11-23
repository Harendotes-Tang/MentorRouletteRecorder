using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.Tests;

/// <summary>Framed IPC envelopes as a client writes them and as the Collector answers them.</summary>
internal static class IpcTestFrames
{
    /// <summary>One request envelope, framed: protocol version, request id, message type and a copy of the payload.</summary>
    /// <param name="requestId">The request's id.</param>
    /// <param name="messageType">The message type.</param>
    /// <param name="payload">The payload; copied, so a node that already has a parent can be passed.</param>
    public static byte[] Request(string requestId, string messageType, JsonNode payload) =>
        FrameCodec.Encode(IpcEnvelope.ToBytes(new JsonObject
        {
            ["protocol_version"] = IpcEnvelope.ProtocolVersion,
            ["request_id"] = requestId,
            ["message_type"] = messageType,
            ["payload"] = payload.DeepClone(),
        }));

    /// <summary>The envelope of one framed write.</summary>
    /// <param name="written">A frame: the length prefix and the JSON envelope.</param>
    public static JsonObject Reply(ReadOnlySpan<byte> written) =>
        JsonNode.Parse(written[FrameCodec.PrefixBytes..])!.AsObject();
}
