using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The wire layer: framing under partial reads, the oversize guard, envelope decoding, and
/// the pipe name both processes have to agree on.
/// </summary>
public sealed class IpcCodecTests
{
    private static byte[] Frame(string json) => FrameCodec.Encode(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void EncodeThenTake_RoundTripsOneFrame()
    {
        var buffer = new FrameBuffer();
        buffer.Append(Frame("{\"a\":1}"));

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var body));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(body));
        Assert.Equal(0, buffer.Buffered);
    }

    [Fact]
    public void TryTake_IsIncompleteUntilEveryByteHasArrived()
    {
        var frame = Frame("{\"hello\":\"世界\"}");
        var buffer = new FrameBuffer();

        // One byte at a time: only the very last byte may complete the frame.
        for (var i = 0; i < frame.Length - 1; i++)
        {
            buffer.Append(frame.AsSpan(i, 1));
            Assert.Equal(FrameStatus.Incomplete, buffer.TryTake(out _));
        }

        buffer.Append(frame.AsSpan(frame.Length - 1, 1));
        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var body));
        Assert.Equal("{\"hello\":\"世界\"}", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void TryTake_HandlesTwoFramesInOneRead()
    {
        var buffer = new FrameBuffer();
        var combined = Frame("{\"n\":1}").Concat(Frame("{\"n\":2}")).ToArray();
        buffer.Append(combined);

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var first));
        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var second));
        Assert.Equal(FrameStatus.Incomplete, buffer.TryTake(out _));
        Assert.Equal("{\"n\":1}", Encoding.UTF8.GetString(first));
        Assert.Equal("{\"n\":2}", Encoding.UTF8.GetString(second));
    }

    [Fact]
    public void TryTake_RejectsAnOversizedDeclaredLengthBeforeAllocating()
    {
        var prefix = new byte[FrameCodec.PrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, FrameCodec.MaxFrameBytes + 1u);
        var buffer = new FrameBuffer();
        buffer.Append(prefix);

        Assert.Equal(FrameStatus.Oversized, buffer.TryTake(out _));
    }

    [Fact]
    public void Encode_RefusesABodyOverTheCap()
    {
        var error = Assert.Throws<CollectorException>(() =>
            FrameCodec.Encode(new byte[FrameCodec.MaxFrameBytes + 1]));

        Assert.Equal(ErrorCodes.Internal, error.Code);
    }

    [Fact]
    public void MaxFrameBytes_MatchesTheDesktopClientCap() =>
        Assert.Equal(4 * 1024 * 1024, FrameCodec.MaxFrameBytes);

    [Fact]
    public void ParseRequest_AcceptsAWellFormedEnvelope()
    {
        var id = Guid.NewGuid().ToString("D");
        var json = $"{{\"protocol_version\":1,\"request_id\":\"{id}\"," +
            "\"message_type\":\"GetVersion\",\"payload\":{}}";

        var request = IpcEnvelope.ParseRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(id, request.RequestId);
        Assert.Equal("GetVersion", request.MessageType);
        Assert.Empty(request.Payload);
    }

    [Fact]
    public void ParseRequest_RejectsMalformedJson()
    {
        var error = Assert.Throws<CollectorException>(() =>
            IpcEnvelope.ParseRequest(Encoding.UTF8.GetBytes("{not json")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void ParseRequest_RejectsAWrongProtocolVersion()
    {
        var json = $"{{\"protocol_version\":2,\"request_id\":\"{Guid.NewGuid():D}\"," +
            "\"message_type\":\"GetVersion\",\"payload\":{}}";

        var error = Assert.Throws<CollectorException>(() =>
            IpcEnvelope.ParseRequest(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(ErrorCodes.ProtocolVersion, error.Code);
    }

    [Fact]
    public void ParseRequest_RejectsANonUuidRequestId()
    {
        var json = "{\"protocol_version\":1,\"request_id\":\"not-a-uuid\"," +
            "\"message_type\":\"GetVersion\",\"payload\":{}}";

        var error = Assert.Throws<CollectorException>(() =>
            IpcEnvelope.ParseRequest(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    private const string ImportRequestId = "00000000-0000-4000-8000-000000000001";

    private const string ImportEnvelope = "{\"protocol_version\":1,\"request_id\":\"" + ImportRequestId + "\"," +
        "\"message_type\":\"ImportCalibrationCode\",\"payload\":{\"code\":\"MRC1.x\"}}";

    /// <summary>
    /// .NET cannot read a key or string holding a lone surrogate escape. Such a frame is refused as malformed
    /// JSON before any handler reads it, instead of throwing out of whichever accessor touched it first.
    /// </summary>
    [Theory]
    [InlineData("\"code\":\"MRC1.x\"", "\"code\":\"\\ud800\"", ImportRequestId)]
    [InlineData("{\"code\"", "{\"\\udc00\":1,\"code\"", ImportRequestId)]
    [InlineData("\"message_type\":\"ImportCalibrationCode\"", "\"message_type\":\"Import\\ud800\"", ImportRequestId)]
    [InlineData("{\"protocol_version\"", "{\"\\ud800\":1,\"protocol_version\"", null)]
    public void ParseRequest_RefusesTextThatIsNotWellFormedUnicodeAsMalformedJson(
        string anchor, string replacement, string? peekedId)
    {
        Assert.Contains(anchor, ImportEnvelope, StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(ImportEnvelope.Replace(anchor, replacement, StringComparison.Ordinal));

        var error = Assert.Throws<CollectorException>(() => IpcEnvelope.ParseRequest(body));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        // The refusal is answered against the id whenever the id itself can be read.
        Assert.Equal(peekedId, IpcEnvelope.TryPeekRequestId(body));
    }

    [Fact]
    public void ParseRequest_RefusesBytesThatAreNotUtf8InsideAString()
    {
        var at = ImportEnvelope.IndexOf("MRC1.x", StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(ImportEnvelope[..at]).Append((byte)0xFF)
            .Concat(Encoding.UTF8.GetBytes(ImportEnvelope[at..])).ToArray();

        var error = Assert.Throws<CollectorException>(() => IpcEnvelope.ParseRequest(body));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal(ImportRequestId, IpcEnvelope.TryPeekRequestId(body));
    }

    [Fact]
    public void Failure_CarriesTheCodeInBothTheErrorObjectAndThePayload()
    {
        var id = Guid.NewGuid().ToString("D");

        var envelope = IpcEnvelope.Failure(
            id,
            "CorrectRun",
            new CollectorException(ErrorCodes.RevisionConflict, "冲突", field: "expected_revision"));

        Assert.False(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.RevisionConflict, envelope["error"]!["code"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.RevisionConflict, envelope["payload"]!["code"]!.GetValue<string>());
        Assert.Equal("expected_revision", envelope["error"]!["field"]!.GetValue<string>());
    }

    [Fact]
    public void PipeName_MatchesTheDocumentedDerivation()
    {
        // Fixed vector: the Desktop side computes the same thing in PipeName.cpp.
        var name = PipeNaming.ForSid("S-1-5-21-1111111111-2222222222-3333333333-1001");

        Assert.StartsWith("MentorRecorder.", name, StringComparison.Ordinal);
        Assert.EndsWith(".v1", name, StringComparison.Ordinal);

        var hex = name["MentorRecorder.".Length..^".v1".Length];
        Assert.Equal(PipeNaming.SidHashBytes * 2, hex.Length);
        Assert.All(hex, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));

        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("S-1-5-21-1111111111-2222222222-3333333333-1001"))
            .AsSpan(0, PipeNaming.SidHashBytes)).ToLowerInvariant();
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void PipeName_IsStableAndDistinctPerSid()
    {
        var first = PipeNaming.ForSid("S-1-5-21-10-20-30-1001");
        var second = PipeNaming.ForSid("S-1-5-21-10-20-30-1002");

        Assert.Equal(first, PipeNaming.ForSid("S-1-5-21-10-20-30-1001"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ServerName_UsesTheLocalPipeNamespace() =>
        Assert.Equal(
            @"\\.\pipe\MentorRecorder.x.v1",
            PipeNaming.ToServerName("MentorRecorder.x.v1"));

    [Fact]
    public void EventEnvelope_IsTaggedAsAnEvent()
    {
        var envelope = IpcEnvelope.Event(
            Guid.NewGuid().ToString("D"), new JsonObject { ["event_type"] = "RunUpdated" });

        Assert.Equal("Event", envelope["message_type"]!.GetValue<string>());
        Assert.Equal("RunUpdated", envelope["payload"]!["event_type"]!.GetValue<string>());
    }
}
