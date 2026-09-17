using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Speech;

/// <summary>Encrypts and decrypts a small secret for the current user.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>.</summary>
    /// <param name="plaintext">Secret bytes.</param>
    byte[] Protect(byte[] plaintext);

    /// <summary>Decrypts what <see cref="Protect"/> wrote; throws <see cref="CryptographicException"/> otherwise.</summary>
    /// <param name="ciphertext">Encrypted bytes.</param>
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Windows DPAPI with the current user's key (<c>CryptProtectData</c>, no UI), called directly from
/// crypt32 so the Collector needs no extra package for it. Another Windows account, or another
/// machine, cannot decrypt what this writes.
/// </summary>
public sealed class DpapiProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    // Ties the blob to this purpose: a DPAPI blob written by another program for the same user
    // does not decrypt here, and ours does not decrypt there without this value.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MentorRecorder.speech-key.v1");

    /// <summary>Shared instance; the class is stateless.</summary>
    public static DpapiProtector Instance { get; } = new();

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return Transform(ciphertext, protect: false);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var inBlob = Allocate(input);
        var entropyBlob = Allocate(Entropy);
        var outBlob = default(DataBlob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob);
            if (!ok)
            {
                throw new CryptographicException(
                    protect ? "DPAPI could not encrypt the key." : "DPAPI could not decrypt the key.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var output = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
            return output;
        }
        finally
        {
            Release(ref inBlob, localFree: false);
            Release(ref entropyBlob, localFree: false);
            Release(ref outBlob, localFree: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = data };
    }

    /// <summary>Overwrites an unmanaged buffer with zeros, then frees it.</summary>
    private static void Release(ref DataBlob blob, bool localFree)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        for (var offset = 0; offset < blob.Size; offset++)
        {
            Marshal.WriteByte(blob.Data, offset, 0);
        }

        if (localFree)
        {
            LocalFree(blob.Data);
        }
        else
        {
            Marshal.FreeHGlobal(blob.Data);
        }

        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>
/// The online speech key, in <c>speech-key.bin</c> beside the database (docs/privacy-boundary.md §8.3).
///
/// The file is a DPAPI blob of <c>{"v":1,"binding":…,"key":…}</c>. The binding is the target the key
/// was entered for (<see cref="OnlineSpeechClient.KeyBinding"/>); a key read back under any other
/// binding is treated as absent, so a key typed for one service is never sent to another. The key is
/// write-only from the outside: nothing here renders it, and the store's <see cref="ToString"/> says
/// only where the file is.
/// </summary>
public sealed class SpeechKeyStore
{
    /// <summary>File name inside the data directory.</summary>
    public const string FileName = "speech-key.bin";

    private const int FormatVersion = 1;
    private const int MaxFileBytes = 16 * 1024;

    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    /// <summary>Creates a store.</summary>
    /// <param name="dataDirectory">Directory holding the database.</param>
    /// <param name="protector">Encryption; DPAPI when null.</param>
    public SpeechKeyStore(string dataDirectory, ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        FilePath = Path.Combine(Path.GetFullPath(dataDirectory), FileName);
        _protector = protector ?? DpapiProtector.Instance;
    }

    /// <summary>Full path of <c>speech-key.bin</c>.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The key stored for <paramref name="binding"/>, or null when there is none, it was stored for
    /// another binding, or the file cannot be read or decrypted.
    /// </summary>
    /// <param name="binding">Target the key must have been entered for; null reads nothing.</param>
    public string? Read(string? binding)
    {
        if (binding is null)
        {
            return null;
        }

        lock (_gate)
        {
            byte[]? plaintext = null;
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length > MaxFileBytes)
                {
                    return null;
                }

                plaintext = _protector.Unprotect(File.ReadAllBytes(FilePath));
                if (JsonNode.Parse(plaintext) is not JsonObject document ||
                    document["v"]?.GetValue<int>() != FormatVersion ||
                    document["binding"]?.GetValue<string>() != binding ||
                    document["key"]?.GetValue<string>() is not { Length: > 0 } key)
                {
                    return null;
                }

                return key;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                           or JsonException or InvalidOperationException or FormatException
                                           or PlatformNotSupportedException)
            {
                return null;
            }
            finally
            {
                if (plaintext is not null)
                {
                    Array.Clear(plaintext);
                }
            }
        }
    }

    /// <summary>True when a key is stored for <paramref name="binding"/>.</summary>
    /// <param name="binding">Target.</param>
    public bool Has(string? binding) => Read(binding) is not null;

    /// <summary>Encrypts and writes the key for a binding, replacing any earlier one atomically.</summary>
    /// <param name="binding">Target the key was entered for.</param>
    /// <param name="key">The key, already validated.</param>
    public void Write(string binding, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var document = new JsonObject { ["v"] = FormatVersion, ["binding"] = binding, ["key"] = key };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document);
        byte[] blob;
        try
        {
            blob = _protector.Protect(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, blob);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>Deletes the key file. Returns true when there was one.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            File.Delete(FilePath);
            return true;
        }
    }

    /// <inheritdoc />
    public override string ToString() => "SpeechKeyStore(" + FileName + ")";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file holds only ciphertext; the next write replaces the key anyway.
        }
    }
}
