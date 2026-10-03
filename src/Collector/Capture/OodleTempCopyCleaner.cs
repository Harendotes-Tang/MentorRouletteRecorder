using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machina.FFXIV.Oodle;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// What a manifest of owned temp copies says right now.
/// </summary>
/// <param name="Present">Registered paths that still exist on disk.</param>
/// <param name="Bytes">Total size of those files.</param>
/// <param name="Missing">Registered paths that are already gone.</param>
public sealed record OodleTempManifestReading(int Present, long Bytes, int Missing)
{
    /// <summary>Nothing registered, or no manifest at all.</summary>
    public static OodleTempManifestReading Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Cleans only paths obtained from native Oodle instances created by this capture. The
/// process TEMP directory is never enumerated and matching bytes never establish ownership.
///
/// The registered paths are also written to a small manifest so the next Collector can finish
/// the job. That is not a TEMP scan and does not weaken the rule in
/// docs/privacy-boundary.md section 4.2: the manifest holds paths this software created and
/// nothing else, and a path is still only deleted when it sits inside Machina's own temp
/// subdirectory. The manifest is needed because each copy is 49.5 MiB and the ordinary Desktop
/// exit kills this process outright, so a finally-block deletion never runs
/// (review finding H-2).
///
/// Registration sees every copy Machina makes. Machina 2.4.7.7 stores the copy's path in
/// <c>_libraryTempPath</c> before it copies the file, publishes the native instance before
/// initializing it, and writes a trace line on the initializing thread before any failure path
/// clears the path again; <see cref="TrackInitialization"/> reads the path on each of those lines.
/// That is why Machina's temp folder is not swept by file name: a copy no cleaner registered is
/// not this software's, and deleting one by its name once removed another Machina-based program's
/// copy between its copy and its load (audit 2026-10-03, OB-6). Several cleaners can share one
/// manifest - every capture start has its own - so each adds and removes its own entries and never
/// rewrites the others'.
/// </summary>
public sealed class OodleTempCopyCleaner
{
    /// <summary>Subdirectory of TEMP that Machina copies the game executable into.</summary>
    public const string MachinaTempFolderName = "Machina.FFXIV";

    /// <summary>Largest manifest this reader will open, in bytes.</summary>
    public const int MaxManifestBytes = 64 * 1024;

    private readonly string _directory;
    private readonly Action<string, int>? _log;
    private readonly string? _manifestPath;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private bool _armed;

    /// <summary>Creates a cleaner.</summary>
    /// <param name="directory">TEMP root; the process temp folder when null.</param>
    /// <param name="log">Structured local log callback.</param>
    /// <param name="manifestPath">
    /// Where the owned paths are persisted so a later process can finish the deletion. Null
    /// keeps ownership in memory only, which is what an in-process test wants.
    /// </param>
    public OodleTempCopyCleaner(
        string? directory = null, Action<string, int>? log = null, string? manifestPath = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.GetTempPath());
        _log = log;
        _manifestPath = string.IsNullOrWhiteSpace(manifestPath) ? null : Path.GetFullPath(manifestPath);
    }

    public bool IsArmed => _armed;

    /// <summary>Enables explicit ownership registration; does not inspect any directory.</summary>
    public void Arm(string? gameExecutablePath) => _armed = !string.IsNullOrWhiteSpace(gameExecutablePath);

    /// <summary>
    /// Reads the exact path stored by a known native object. Unknown dependency layouts or
    /// paths outside Machina's dedicated temp subdirectory are preserved, never guessed.
    /// </summary>
    internal void RememberNative(IOodleNative native)
    {
        if (!_armed || native is not OodleNative_Ffxiv) return;
        RememberPathField(native, typeof(OodleNative_Ffxiv).GetField("_libraryTempPath",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    internal void RememberPathField(object native, FieldInfo? field)
    {
        if (!_armed || field?.FieldType != typeof(string)) return;
        try
        {
            if (field.GetValue(native) is not string path || string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            var expectedParent = Path.Combine(_directory, MachinaTempFolderName);
            if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase)) return;
            if (_owned.Add(full)) UpdateManifest(Array.Empty<string>());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or MemberAccessException or TargetException)
        {
            // Losing cleanup evidence may retain our file, but must never select another file.
        }
    }

    /// <summary>
    /// Captures ownership even when initialization logs a failure and clears its path during
    /// rollback. Only traces on this initialization thread cause a read of this exact object.
    /// </summary>
    internal void TrackInitialization(IOodleNative native, Action initialize) =>
        Track(initialize, () => RememberNative(native));

    internal IOodleNative? TrackFactoryInitialization(Action initialize, Action<IOodleNative>? own = null)
    {
        var factory = typeof(OodleFactory);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var field = factory.GetField("_oodleNative", flags);
        var sync = factory.GetField("_lock", flags)?.GetValue(null);
        if (field?.FieldType != typeof(IOodleNative) || sync is null)
        {
            initialize(); // Incompatible cleanup layout cannot authorize deleting any file.
            return null;
        }
        lock (sync)
        {
            var previous = field.GetValue(null);
            IOodleNative? created = null;
            Track(initialize, () =>
            {
                if (field.GetValue(null) is IOodleNative current && !ReferenceEquals(current, previous))
                {
                    created = current;
                    own?.Invoke(current);
                    RememberNative(current);
                }
            });
            return created;
        }
    }

    private static void Track(Action initialize, Action observe)
    {
        using var listener = new InitializationListener(Environment.CurrentManagedThreadId, observe);
        Trace.Listeners.Add(listener);
        try { initialize(); }
        finally
        {
            try { observe(); }
            finally { Trace.Listeners.Remove(listener); }
        }
    }

    /// <summary>Releases only a native instance returned by this capture's factory initialization.</summary>
    internal void ReleaseNative(IOodleNative native)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var field = typeof(OodleFactory).GetField("_oodleNative", flags);
        var sync = typeof(OodleFactory).GetField("_lock", flags)?.GetValue(null);
        if (field?.FieldType != typeof(IOodleNative) || sync is null)
            throw new InvalidOperationException("Oodle factory ownership cannot be verified during release.");
        lock (sync)
        {
            RememberNative(native);
            native.UnInitialize();
            if (ReferenceEquals(field.GetValue(null), native)) field.SetValue(null, null);
        }
    }

    /// <summary>Retries deletion of explicitly owned files; failed deletions stay registered.</summary>
    public int Sweep()
    {
        var removed = 0;
        var released = new List<string>();
        foreach (var path in _owned.ToArray())
        {
            try
            {
                var existed = File.Exists(path);
                File.Delete(path);
                _owned.Remove(path);
                released.Add(path);
                if (existed) removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (released.Count > 0) UpdateManifest(released);
        if (removed > 0) _log?.Invoke("oodle_temp_copy_removed", removed);
        return removed;
    }

    /// <summary>Stops accepting new paths; any failed owned deletion remains available for retry.</summary>
    public void Disarm() => _armed = false;

    /// <summary>
    /// Adds the paths this cleaner owns to the manifest and takes out the ones it has released,
    /// leaving every other entry alone. Best effort.
    ///
    /// Merged, not rewritten: every capture start has a cleaner of its own on the same manifest,
    /// and rewriting it from one cleaner's set erased the entries of another - typically the copy
    /// that could not be deleted yet because it was still loaded, which only the manifest lets a
    /// later start remove (audit 2026-10-03, OB-6). A released path is one that is gone from disk,
    /// so taking it out is right whoever registered it.
    ///
    /// An empty result removes the file rather than writing an empty array, so "there is no
    /// manifest" and "this software owns nothing" are the same observable state on every path
    /// and not only on the startup sweep (review finding R-14).
    /// </summary>
    /// <param name="released">Paths deleted, or found already gone, since the last update.</param>
    private void UpdateManifest(IReadOnlyCollection<string> released)
    {
        if (_manifestPath is null) return;
        try
        {
            var registered = new HashSet<string>(ReadManifest(_manifestPath), StringComparer.OrdinalIgnoreCase);
            registered.UnionWith(_owned);
            registered.ExceptWith(released);
            var directory = Path.GetDirectoryName(_manifestPath);
            if (registered.Count > 0 && !string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RewriteManifest(_manifestPath, registered.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Losing the manifest costs a retry next time, never a wrong deletion.
        }
    }

    /// <summary>
    /// Reads a manifest without touching the files it names. Used by <c>--capture-doctor</c>
    /// so the promise in docs/privacy-boundary.md -- that the temp directory can be checked --
    /// is answerable without enumerating TEMP (review finding H-2).
    /// </summary>
    /// <param name="manifestPath">Manifest file; a missing file reads as empty.</param>
    public static OodleTempManifestReading InspectManifest(string? manifestPath)
    {
        var present = 0;
        var missing = 0;
        long bytes = 0;
        foreach (var path in ReadManifest(manifestPath))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { missing++; continue; }
                present++;
                bytes += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                missing++;
            }
        }

        return new OodleTempManifestReading(present, bytes, missing);
    }

    /// <summary>
    /// Deletes what a previous Collector registered and could not remove, then rewrites the
    /// manifest with whatever is left.
    ///
    /// Definite ownership, not a guess: only paths this software wrote into the manifest are
    /// considered, and each one still has to sit directly inside
    /// <c>&lt;temp&gt;\Machina.FFXIV</c> before it is deleted. Nothing is enumerated.
    /// </summary>
    /// <param name="manifestPath">Manifest file; a missing file is a no-op.</param>
    /// <param name="tempDirectory">TEMP root; the process temp folder when null.</param>
    /// <param name="log">Structured local log callback, called once when anything was removed.</param>
    /// <returns>Number of files actually deleted.</returns>
    public static int SweepManifest(
        string? manifestPath, string? tempDirectory = null, Action<string, int>? log = null)
    {
        var registered = ReadManifest(manifestPath);
        if (registered.Count == 0) return 0;

        var expectedParent = Path.Combine(
            Path.GetFullPath(tempDirectory ?? Path.GetTempPath()), MachinaTempFolderName);
        var removed = 0;
        var remaining = new List<string>();
        foreach (var path in registered)
        {
            if (!IsOwnedTempCopy(path, expectedParent)) continue;
            try
            {
                var existed = File.Exists(path);
                File.Delete(path);
                if (existed) removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(path);
            }
        }

        RewriteManifest(manifestPath, remaining);
        if (removed > 0) log?.Invoke("oodle_temp_copy_reclaimed", removed);
        return removed;
    }

    private static bool IsOwnedTempCopy(string path, string expectedParent)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(
                Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Reads the registered paths, or an empty list when there is no readable manifest.</summary>
    /// <param name="manifestPath">Manifest file.</param>
    public static IReadOnlyList<string> ReadManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return Array.Empty<string>();
        try
        {
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length > MaxManifestBytes) return Array.Empty<string>();
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject document ||
                document["paths"] is not JsonArray paths)
            {
                return Array.Empty<string>();
            }

            var results = new List<string>(paths.Count);
            foreach (var entry in paths)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var path)
                    && !string.IsNullOrWhiteSpace(path))
                {
                    results.Add(path);
                }
            }

            return results;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    private static void RewriteManifest(string? manifestPath, IReadOnlyList<string> paths)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return;
        try
        {
            if (paths.Count == 0)
            {
                File.Delete(manifestPath);
                return;
            }

            var array = new JsonArray();
            foreach (var path in paths) array.Add(path);
            File.WriteAllText(
                manifestPath,
                new JsonObject { ["version"] = 1, ["paths"] = array }.ToJsonString(),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Same rule as everywhere else here: losing the record costs a retry, never a file.
        }
    }

    private sealed class InitializationListener(int thread, Action observe) : TraceListener
    {
        public override void Write(string? message) { if (Environment.CurrentManagedThreadId == thread) observe(); }
        public override void WriteLine(string? message) => Write(message);
    }
}
