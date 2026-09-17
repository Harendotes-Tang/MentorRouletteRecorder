using System.Buffers.Binary;

namespace MentorRecorder.Collector.Speech;

/// <summary>
/// Checks that an online speech answer is RIFF/WAVE 16-bit PCM and rewrites it into the one plain
/// shape the Desktop plays: a 44-byte header followed by whole sample frames.
///
/// The rewrite is not cosmetic. A service that streams its WAV cannot know the length up front and
/// writes <c>0xFFFFFFFF</c> (or zero) into the RIFF and data sizes; a strict player refuses that.
/// Extra chunks (LIST, fact) are dropped, WAVE_FORMAT_EXTENSIBLE with a PCM sub-format becomes plain
/// PCM, and a trailing partial frame is cut. Anything else - another codec, another bit depth, a
/// chunk that runs past the end before the data - is refused, and a refused answer is never cached.
/// </summary>
public static class WaveFile
{
    /// <summary>Size of the canonical header this class writes.</summary>
    public const int CanonicalHeaderBytes = 44;

    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;
    private const int MaxChunks = 64;

    // KSDATAFORMAT_SUBTYPE_PCM, 00000001-0000-0010-8000-00aa00389b71, as stored in the file.
    private static readonly byte[] PcmSubFormat =
    {
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    };

    /// <summary>The format of a checked file.</summary>
    /// <param name="Channels">1 or 2.</param>
    /// <param name="SampleRate">Samples per second.</param>
    /// <param name="DataBytes">Bytes of sample data kept.</param>
    public sealed record WaveFormat(int Channels, int SampleRate, int DataBytes);

    /// <summary>Checks <paramref name="bytes"/> and returns the canonical file, or a refusal token.</summary>
    /// <param name="bytes">Body as received.</param>
    /// <param name="canonical">The rewritten file when accepted.</param>
    /// <param name="format">What the file holds when accepted.</param>
    /// <param name="refusal">Upper-case reason when refused.</param>
    public static bool TryNormalize(
        ReadOnlySpan<byte> bytes, out byte[] canonical, out WaveFormat? format, out string? refusal)
    {
        canonical = Array.Empty<byte>();
        format = null;
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            refusal = "NOT_RIFF_WAVE";
            return false;
        }

        (int Channels, int SampleRate)? fmt = null;
        var offset = 12;
        for (var chunk = 0; chunk < MaxChunks && offset + 8 <= bytes.Length; chunk++)
        {
            var id = bytes.Slice(offset, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var bodyStart = offset + 8;
            var remaining = bytes.Length - bodyStart;

            if (id.SequenceEqual("data"u8))
            {
                if (fmt is not { } shape)
                {
                    refusal = "DATA_BEFORE_FMT";
                    return false;
                }

                // A streamed answer declares an unknown length; the data then runs to the end.
                var length = declared == 0 || declared == uint.MaxValue || declared > remaining
                    ? remaining
                    : (int)declared;
                var blockAlign = shape.Channels * 2;
                length -= length % blockAlign;
                if (length <= 0)
                {
                    refusal = "NO_SAMPLES";
                    return false;
                }

                canonical = Build(shape.Channels, shape.SampleRate, bytes.Slice(bodyStart, length));
                format = new WaveFormat(shape.Channels, shape.SampleRate, length);
                refusal = null;
                return true;
            }

            if (declared > remaining)
            {
                refusal = "TRUNCATED_CHUNK";
                return false;
            }

            if (id.SequenceEqual("fmt "u8))
            {
                if (!TryReadFormat(bytes.Slice(bodyStart, (int)declared), out var shape, out refusal))
                {
                    return false;
                }

                fmt = shape;
            }

            offset = bodyStart + (int)declared + (int)(declared & 1);
        }

        refusal = fmt is null ? "NO_FMT" : "NO_DATA";
        return false;
    }

    private static bool TryReadFormat(
        ReadOnlySpan<byte> body, out (int Channels, int SampleRate) shape, out string? refusal)
    {
        shape = default;
        if (body.Length < 16)
        {
            refusal = "SHORT_FMT";
            return false;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);

        var pcm = tag == FormatPcm ||
                  (tag == FormatExtensible && body.Length >= 40 && body.Slice(24, 16).SequenceEqual(PcmSubFormat));
        if (!pcm)
        {
            refusal = "NOT_PCM";
            return false;
        }

        if (bits != 16)
        {
            refusal = "NOT_16_BIT";
            return false;
        }

        if (channels is < 1 or > 2 || sampleRate is < 8000 or > 192000 ||
            blockAlign != channels * 2 || byteRate != sampleRate * blockAlign)
        {
            refusal = "BAD_FMT";
            return false;
        }

        shape = (channels, (int)sampleRate);
        refusal = null;
        return true;
    }

    private static byte[] Build(int channels, int sampleRate, ReadOnlySpan<byte> samples)
    {
        var file = new byte[CanonicalHeaderBytes + samples.Length];
        var span = file.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(file.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)samples.Length);
        samples.CopyTo(span[CanonicalHeaderBytes..]);
        return file;
    }

    /// <summary>A canonical 16-bit PCM file of silence, for tests and the Desktop's mock backend.</summary>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="frames">Number of sample frames.</param>
    /// <param name="channels">1 or 2.</param>
    public static byte[] Silence(int sampleRate = 24000, int frames = 240, int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        return Build(channels, sampleRate, new byte[frames * channels * 2]);
    }
}
