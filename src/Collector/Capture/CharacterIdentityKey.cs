using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MentorRecorder.Collector.Capture;

/// <summary>What became of an attempt to read the character identity key.</summary>
public enum CharacterIdentityKeyStatus
{
    /// <summary>The key is in hand.</summary>
    Ready,

    /// <summary>
    /// There is no place to keep one, or the file could not be read just now. Nothing was changed;
    /// a later attempt may succeed.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The file is there and is not a key this Windows user can use: the data folder came from
    /// another account or machine, or the user's protection keys were unavailable at that moment.
    /// Nothing was changed.
    /// </summary>
    Unusable,
}

/// <summary>An attempt to read the key: the key when <see cref="Status"/> is Ready, else null.</summary>
/// <param name="Key">The key, or null.</param>
/// <param name="Status">What happened.</param>
public readonly record struct CharacterIdentityKeyResult(CharacterIdentityKey? Key, CharacterIdentityKeyStatus Status)
{
    /// <summary>The answer when no key could be had and nothing can be done about it now.</summary>
    public static CharacterIdentityKeyResult Unavailable { get; } = new(null, CharacterIdentityKeyStatus.Unavailable);
}

/// <summary>
/// The key the character identities in the database are digested with
/// (docs/privacy-boundary.md, character identification).
///
/// The database never holds a character's identifier, only an HMAC-SHA256 of it under this key. A
/// 32-bit identifier hashed without a key, or with a salt stored beside it, could be recovered by
/// trying every value; the key lives outside the database, in the data folder, encrypted for the
/// current Windows user (DPAPI). A copy of the database sent to somebody else therefore says how
/// many characters were seen and nothing about which.
///
/// It is not the key of the identity probe (<see cref="IdentityProbeKey"/>): that one is compared
/// across trace files the player hands over and may be deleted by hand, and a digest here uses its
/// own domain, so nothing in a trace can be matched against the database.
///
/// Reading never throws and never replaces a file by itself. A file this user cannot use is
/// reported as <see cref="CharacterIdentityKeyStatus.Unusable"/>; replacing it makes every
/// identity written under the old key match nothing, so the caller does that only once the file
/// has stayed unusable (<c>LiveProtocolPipeline</c>), and says so in the log.
/// </summary>
public sealed class CharacterIdentityKey
{
    /// <summary>File name of the key, beside the database.</summary>
    public const string FileName = "character-identity.key";

    /// <summary>Hexadecimal characters kept of each digest: 128 bits.</summary>
    public const int DigestLength = 32;

    /// <summary>Hexadecimal characters of the key's own fingerprint.</summary>
    public const int KeyIdLength = 8;

    private const byte ActorDomain = 0x10;
    private const byte KeyIdDomain = 0x11;

    private static readonly Speech.ISecretProtector DefaultProtector =
        Speech.DpapiProtector.ForPurpose("MentorRecorder.character-identity-key.v1");

    private readonly byte[] _key;

    /// <summary>Creates a key from its bytes.</summary>
    /// <param name="key">Exactly <see cref="CaptureIdentityProbe.KeyBytes"/> bytes; copied.</param>
    public CharacterIdentityKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != CaptureIdentityProbe.KeyBytes)
        {
            throw new ArgumentException("character identity key must be 32 bytes", nameof(key));
        }

        _key = key.ToArray();
        KeyId = Hash(KeyIdDomain, ReadOnlySpan<byte>.Empty)[..KeyIdLength];
    }

    /// <summary>
    /// Fingerprint of the key, stored with every identity so that rows written under another key
    /// can be told and removed. It reveals nothing about the key or about any character.
    /// </summary>
    public string KeyId { get; }

    /// <summary>The digest standing in for one character.</summary>
    /// <param name="actor">Actor as read from a segment header; never stored or logged as it is.</param>
    public string Digest(uint actor)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, actor);
        return Hash(ActorDomain, bytes)[..DigestLength];
    }

    /// <summary>Reads the key beside a database, creating it on first use.</summary>
    /// <param name="databasePath">Path of the open database.</param>
    /// <param name="replaceUnusable">
    /// True to replace a file this user cannot use with a new key instead of reporting it.
    /// </param>
    /// <param name="protector">Encryption at rest; DPAPI for the current user when null.</param>
    public static CharacterIdentityKeyResult LoadBeside(
        string? databasePath, bool replaceUnusable, Speech.ISecretProtector? protector = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath.Contains(":memory:", StringComparison.Ordinal))
        {
            return CharacterIdentityKeyResult.Unavailable;
        }

        // Whatever goes wrong here costs the player automatic switching for a while and nothing
        // else, so nothing may leave this method: it is called on the capture path.
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (string.IsNullOrEmpty(directory))
            {
                return CharacterIdentityKeyResult.Unavailable;
            }

            var path = Path.Combine(directory, FileName);
            var secrets = protector ?? DefaultProtector;
            try
            {
                return Ready(IdentityProbeKey.LoadOrCreate(path, secrets));
            }
            catch (InvalidDataException) when (!replaceUnusable)
            {
                return new CharacterIdentityKeyResult(null, CharacterIdentityKeyStatus.Unusable);
            }
            catch (InvalidDataException)
            {
                File.Delete(path);
                return Ready(IdentityProbeKey.LoadOrCreate(path, secrets));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CharacterIdentityKeyResult.Unavailable;
        }
    }

    private static CharacterIdentityKeyResult Ready(byte[] key) =>
        new(new CharacterIdentityKey(key), CharacterIdentityKeyStatus.Ready);

    private string Hash(byte domain, ReadOnlySpan<byte> value)
    {
        Span<byte> input = stackalloc byte[1 + value.Length];
        input[0] = domain;
        value.CopyTo(input[1..]);

        Span<byte> digest = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, input, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
