using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using MentorRecorder.Collector.Speech;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A speech transport that answers from a script and remembers what it was asked. No test in this
/// project opens a connection: everything goes through this delegate.
/// </summary>
internal sealed class FakeSpeechTransport
{
    private readonly Func<SpeechHttpRequest, CancellationToken, Task<SpeechTransportResponse>> _answer;
    private readonly ConcurrentQueue<SpeechHttpRequest> _requests = new();

    public FakeSpeechTransport(Func<SpeechHttpRequest, CancellationToken, Task<SpeechTransportResponse>>? answer = null)
    {
        _answer = answer ?? ((request, _) => Task.FromResult(Wav(request)));
    }

    public IReadOnlyList<SpeechHttpRequest> Requests => _requests.ToArray();

    public Task<SpeechTransportResponse> Send(SpeechHttpRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return _answer(request, cancellationToken);
    }

    public static SpeechTransportResponse Wav(SpeechHttpRequest request, byte[]? body = null, string? mediaType = "audio/wav") =>
        new(200, request.Uri, mediaType, null, new MemoryStream(body ?? WaveFile.Silence()));

    public static SpeechTransportResponse Status(SpeechHttpRequest request, int status, string body = "") =>
        new(status, request.Uri, "text/plain", null, new MemoryStream(Encoding.UTF8.GetBytes(body)));
}

internal static class SpeechFixtures
{
    public const string Key = "unit-test-key-4f1c9a";

    public static OnlineSpeechConfig Azure(string region = "eastasia", string voice = "zh-CN-XiaoxiaoNeural") =>
        new(SpeechProvider.Azure, region, null, null, voice);

    public static OnlineSpeechConfig OpenAi(string baseUrl = "https://speech.example.com/v1", string voice = "alloy") =>
        new(SpeechProvider.OpenAiCompatible, null, baseUrl, "gpt-4o-mini-tts", voice);

    public static OnlineSpeechClient Client(FakeSpeechTransport transport, TimeSpan? timeout = null, string? killSwitch = null) =>
        new(transport.Send, timeout ?? TimeSpan.FromSeconds(5), name => name == OnlineSpeechClient.DisableVariable ? killSwitch : null);

    /// <summary>A RIFF/WAVE file with a chosen format tag and bit depth.</summary>
    public static byte[] Wave(ushort formatTag = 1, ushort bits = 16, ushort channels = 1, int sampleRate = 24000,
        int dataBytes = 480, uint? riffSize = null, uint? dataSize = null, byte[]? extraChunk = null)
    {
        using var stream = new MemoryStream();
        void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
        void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); stream.Write(b); }
        var blockAlign = (ushort)(channels * bits / 8);
        stream.Write("RIFF"u8);
        U32(riffSize ?? 0);
        stream.Write("WAVE"u8);
        stream.Write("fmt "u8);
        U32(16);
        U16(formatTag);
        U16(channels);
        U32((uint)sampleRate);
        U32((uint)(sampleRate * blockAlign));
        U16(blockAlign);
        U16(bits);
        if (extraChunk is not null)
        {
            stream.Write(extraChunk);
        }

        stream.Write("data"u8);
        U32(dataSize ?? (uint)dataBytes);
        stream.Write(new byte[dataBytes]);
        return stream.ToArray();
    }
}
