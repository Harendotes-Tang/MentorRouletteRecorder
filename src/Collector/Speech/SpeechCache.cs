using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Speech;

/// <summary>
/// Audio the online speech services returned, in <c>tts-cache\</c> beside the database
/// (docs/privacy-boundary.md §8.3).
///
/// One file per sentence, named by <see cref="KeyFor"/>: the same sentence with the same voice and rate
/// is spoken again without a request. The directory is capped at <see cref="DefaultMaxBytes"/>; the
/// least recently used files go first. "Used" is the file's last-write time, stamped on every hit:
/// Windows does not keep access times by default, and the content of a cached file never changes, so
/// the write time is free to carry the stamp. Files are written to a temporary name and moved into
/// place, so the Desktop never plays half a file.
/// </summary>
public sealed class SpeechCache
{
    /// <summary>Folder name inside the data directory.</summary>
    public const string FolderName = "tts-cache";

    /// <summary>Size cap of the folder.</summary>
    public const long DefaultMaxBytes = 20 * 1024 * 1024;

    /// <summary>Extension of a cached file.</summary>
    public const string Extension = ".wav";

    private const string TemporaryExtension = ".tmp";

    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    private readonly long _maxBytes;
    private readonly IClock _clock;
    private readonly object _gate = new();

    /// <summary>Creates a cache.</summary>
    /// <param name="dataDirectory">Directory holding the database.</param>
    /// <param name="clock">Stamps "last used"; the system clock when null.</param>
    /// <param name="maxBytes">Size cap; <see cref="DefaultMaxBytes"/> when null.</param>
    public SpeechCache(string dataDirectory, IClock? clock = null, long? maxBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);
        _clock = clock ?? SystemClock.Instance;
        _maxBytes = maxBytes ?? DefaultMaxBytes;
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxBytes, 1);
    }

    /// <summary>Full path of <c>tts-cache\</c>.</summary>
    public string Directory { get; }

    /// <summary>
    /// The cache key: lower-case hex SHA-256 of <c>provider|voice|rate|text</c> in UTF-8.
    /// </summary>
    /// <param name="provider">Service token.</param>
    /// <param name="voice">Voice name.</param>
    /// <param name="ratePercent">Rate in percent.</param>
    /// <param name="text">Prepared sentence.</param>
    public static string KeyFor(SpeechProvider provider, string voice, int ratePercent, string text)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(text);
        var material = SpeechProviderWire.Format(provider) + "|" + voice + "|" +
                       ratePercent.ToString(CultureInfo.InvariantCulture) + "|" + text;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>Path of the file for a key.</summary>
    /// <param name="key">Key from <see cref="KeyFor"/>.</param>
    public string PathFor(string key)
    {
        if (key is null || key.Length != 64 || !key.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("not a cache key", nameof(key));
        }

        return Path.Combine(Directory, key + Extension);
    }

    /// <summary>
    /// The cached file for a key, stamped as just used, or null. A file that does not start like a
    /// WAV is removed and reported as a miss.
    /// </summary>
    /// <param name="key">Key from <see cref="KeyFor"/>.</param>
    public string? TryGet(string key)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return null;
                }

                if (info.Length < WaveFile.CanonicalHeaderBytes || !StartsLikeWave(path))
                {
                    info.Delete();
                    return null;
                }

                File.SetLastWriteTimeUtc(path, _clock.UtcNow.UtcDateTime);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Writes a file for a key atomically, then trims the folder to its cap. Returns the file's path.
    /// </summary>
    /// <param name="key">Key from <see cref="KeyFor"/>.</param>
    /// <param name="audio">Canonical WAV bytes.</param>
    public string Store(string key, byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var path = PathFor(key);
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = Path.Combine(Directory, "." + key + "." + Guid.NewGuid().ToString("N") + TemporaryExtension);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(audio);
                    stream.Flush(flushToDisk: true);
                }

                File.SetLastWriteTimeUtc(temporary, _clock.UtcNow.UtcDateTime);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }

            Trim(keep: path);
            return path;
        }
    }

    /// <summary>Total bytes of the cached files.</summary>
    public long SizeBytes()
    {
        lock (_gate)
        {
            return Files().Sum(file => file.Length);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a file directly inside this cache folder with the cache's
    /// naming, the rule the Desktop applies before playing anything.
    /// </summary>
    /// <param name="path">Candidate path.</param>
    public bool Contains(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(full);
        return string.Equals(Path.GetDirectoryName(full), Directory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetExtension(full), Extension, StringComparison.OrdinalIgnoreCase) &&
               name.Length == 64 && name.All(char.IsAsciiHexDigitLower);
    }

    private void Trim(string keep)
    {
        var now = _clock.UtcNow.UtcDateTime;
        foreach (var stale in new DirectoryInfo(Directory).EnumerateFiles("*" + TemporaryExtension))
        {
            if (now - stale.LastWriteTimeUtc > StaleTemporaryAge)
            {
                TryDelete(stale.FullName);
            }
        }

        var files = Files().ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files
                     .Where(file => !string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (total <= _maxBytes)
            {
                break;
            }

            if (TryDelete(file.FullName))
            {
                total -= file.Length;
            }
        }
    }

    private IEnumerable<FileInfo> Files()
    {
        var directory = new DirectoryInfo(Directory);
        return directory.Exists
            ? directory.EnumerateFiles("*" + Extension).Where(file => Contains(file.FullName))
            : Enumerable.Empty<FileInfo>();
    }

    private static bool StartsLikeWave(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
               head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file the Desktop is playing right now cannot be deleted; the next store retries.
            return false;
        }
    }
}
