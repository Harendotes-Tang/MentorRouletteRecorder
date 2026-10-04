using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// Reads the checksum published beside every installer, <c>MentorRecorder-&lt;version&gt;-setup.exe.sha256</c>:
/// 64 hex digits, two spaces, the file name (scripts/package.ps1).
///
/// Untrusted input from the network, and the only thing that vouches for the installer, so it is read strictly:
/// exactly 64 hex characters at the very start, then the end of the document or white space. A byte-order mark, a
/// leading space, a 65th hex digit or anything else glued to the digest makes it no checksum at all. The file name
/// after it is not looked at: the address it came from already names the file. Pure: no IO.
/// </summary>
public static class UpdateChecksum
{
    /// <summary>Hex characters of a SHA-256.</summary>
    public const int HexLength = 64;

    /// <summary>Reads the digest at the start of a published checksum document.</summary>
    /// <param name="document">The document's bytes, as downloaded.</param>
    /// <param name="sha256">The digest in lower-case hex; null when false is returned.</param>
    public static bool TryRead(ReadOnlySpan<byte> document, [NotNullWhen(true)] out string? sha256)
    {
        sha256 = null;
        if (document.Length < HexLength)
        {
            return false;
        }

        foreach (var value in document[..HexLength])
        {
            if (!char.IsAsciiHexDigit((char)value))
            {
                return false;
            }
        }

        if (document.Length > HexLength && document[HexLength] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            return false;
        }

        sha256 = Encoding.ASCII.GetString(document[..HexLength]).ToLowerInvariant();
        return true;
    }
}
