using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Derivation of the per-user Named Pipe name.
///
/// <code>
///   name        = "MentorRecorder." + &lt;sid hash&gt; + ".v1"
///   &lt;sid hash&gt;  = lower-case hex of the first 16 bytes of
///                 SHA-256(UTF-8 bytes of the user's SID string)
/// </code>
///
/// The Desktop process computes the same string in <c>src/Desktop/cpp/PipeName.cpp</c>; the
/// two implementations must agree byte for byte or the two processes simply never meet.
/// Hashing keeps two users on one machine apart without putting a raw SID into a globally
/// visible object name, and the SID itself is never logged.
/// </summary>
public static class PipeNaming
{
    /// <summary>Number of SHA-256 bytes kept, hex-encoded, in the pipe name.</summary>
    public const int SidHashBytes = 16;

    /// <summary>Prefix of every pipe name this build creates.</summary>
    public const string Prefix = "MentorRecorder.";

    /// <summary>Suffix carrying the wire protocol version.</summary>
    public const string Suffix = ".v1";

    /// <summary>Win32 local pipe namespace prefix.</summary>
    public const string LocalPipeNamespace = @"\\.\pipe\";

    /// <summary>Derives the pipe name from a SID string such as <c>S-1-5-21-...</c>.</summary>
    /// <param name="sidString">SID in string form.</param>
    public static string ForSid(string sidString)
    {
        ArgumentException.ThrowIfNullOrEmpty(sidString);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sidString));
        var hex = Convert.ToHexString(digest.AsSpan(0, SidHashBytes)).ToLowerInvariant();
        return Prefix + hex + Suffix;
    }

    /// <summary>The current process token's user SID in string form.</summary>
    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new CollectorException(
                ErrorCodes.Internal,
                "无法读取当前用户的 SID，命名管道名称无法确定。");
    }

    /// <summary>The pipe name for the user this process runs as.</summary>
    public static string CurrentUserPipeName() => ForSid(CurrentUserSid());

    /// <summary>The full <c>\\.\pipe\...</c> name a client connects to.</summary>
    /// <param name="pipeName">Bare pipe name.</param>
    public static string ToServerName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        return LocalPipeNamespace + pipeName;
    }
}
