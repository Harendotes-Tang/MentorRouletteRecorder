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

    // Ties the blob to its purpose: a DPAPI blob written by another program for the same user
    // does not decrypt here, and ours does not decrypt there without this value.
    private readonly byte[] _entropy;

    private DpapiProtector(string purpose) => _entropy = Encoding.UTF8.GetBytes(purpose);

    /// <summary>Shared instance for the online speech key.</summary>
    public static DpapiProtector Instance { get; } = new("MentorRecorder.speech-key.v1");

    /// <summary>
    /// A protector bound to another purpose: what it writes does not decrypt under any other
    /// purpose, the speech key's included.
    /// </summary>
    /// <param name="purpose">Stable, versioned name of what is being protected.</param>
    public static DpapiProtector ForPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return new DpapiProtector(purpose);
    }

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

    private byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var inBlob = Allocate(input);
        var entropyBlob = Allocate(_entropy);
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
/// binding is treated as absent. Speech settings updates add an opaque revision and stage the new
/// blob beside the main file. The revision committed with the settings selects which blob is active,
/// even if the process stops before it can promote the staged file.
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
        : this(dataDirectory, protector ?? DpapiProtector.Instance, FileName) { }

    internal SpeechKeyStore(string dataDirectory, ISecretProtector protector, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        FilePath = Path.Combine(Path.GetFullPath(dataDirectory), fileName);
        _protector = protector;
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
            return ReadFile(FilePath, binding)?.Key;
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
        lock (_gate)
        {
            WriteFile(FilePath, binding, key, revision: null);
        }
    }

    /// <summary>Reads only the revision committed with the settings. Null selects a legacy key,
    /// and an empty revision represents a cleared key.</summary>
    internal string? ReadSelected(string? binding, string? revision)
    {
        if (binding is null || revision == string.Empty)
        {
            return null;
        }

        lock (_gate)
        {
            var key = ReadMatchingKey(FilePath, binding, revision);
            return key ?? (revision is null ? null : ReadMatchingKey(PendingPath(Slot(revision)), binding, revision));
        }
    }

    /// <summary>Stages an encrypted replacement and returns its uncommitted revision.</summary>
    internal string Stage(string binding, string key, string? currentRevision)
    {
        lock (_gate)
        {
            // A committed key can still live in the staging slot after an interrupted promotion.
            // Always write the other slot, even when the selected file cannot currently be read.
            var slot = OtherSlot(currentRevision);
            var revision = slot + Guid.NewGuid().ToString("N");
            WriteFile(PendingPath(slot), binding, key, revision);
            return revision;
        }
    }

    /// <summary>Promotes a committed replacement. The committed revision can also read the staging
    /// file if a lock prevents this rename or the process stops before it completes.</summary>
    internal void PromoteCommitted(string revision)
    {
        lock (_gate)
        {
            try
            {
                File.Move(PendingPath(Slot(revision)), FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The committed revision still selects the staged key.
            }
            TryDelete(PendingPath(OtherSlot(revision)));
        }
    }

    /// <summary>Discards a staged replacement after its settings transaction rolls back.</summary>
    internal void DiscardPending(string revision)
    {
        lock (_gate)
        {
            TryDelete(PendingPath(Slot(revision)));
        }
    }

    /// <summary>Removes ciphertext after the settings transaction revokes the old key.</summary>
    internal void RemoveClearedKey()
    {
        lock (_gate)
        {
            TryDelete(FilePath);
            TryDelete(PendingPath('0'));
            TryDelete(PendingPath('1'));
        }
    }

    private static char Slot(string? revision) => revision?.StartsWith('0') == true ? '0' : '1';

    private static char OtherSlot(string? revision) => Slot(revision) == '0' ? '1' : '0';

    private string PendingPath(char slot) => FilePath + "." + slot + ".pending";

    private string? ReadMatchingKey(string path, string binding, string? revision) =>
        ReadFile(path, binding) is { } entry && entry.Revision == revision ? entry.Key : null;

    private (string Key, string? Revision)? ReadFile(string path, string binding)
    {
        byte[]? plaintext = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            plaintext = _protector.Unprotect(File.ReadAllBytes(path));
            if (JsonNode.Parse(plaintext) is not JsonObject document ||
                document["v"]?.GetValue<int>() != FormatVersion ||
                document["binding"]?.GetValue<string>() != binding ||
                document["key"]?.GetValue<string>() is not { Length: > 0 } key)
            {
                return null;
            }

            return (key, document["revision"]?.GetValue<string>());
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

    private void WriteFile(string path, string binding, string key, string? revision)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var document = new JsonObject { ["v"] = FormatVersion, ["binding"] = binding, ["key"] = key };
        if (revision is not null)
        {
            document["revision"] = revision;
        }
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

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(blob);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Deletes the key file. Returns false if the file or directory is missing.</summary>
    /// <exception cref="IOException">The file could not be inspected or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The credential path is inaccessible.</exception>
    public bool Clear()
    {
        lock (_gate)
        {
            try
            {
                // File.Exists also returns false for inaccessible paths.
                _ = File.GetAttributes(FilePath);
                File.Delete(FilePath);
                return true;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => "SpeechKeyStore(" + Path.GetFileName(FilePath) + ")";

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
            // Any leftover staging or revoked file holds only ciphertext.
        }
    }
}
