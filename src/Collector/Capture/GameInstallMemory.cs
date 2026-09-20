using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Where this machine last saw the game installed.
///
/// It exists for one question: which client version is installed, asked while the game is
/// closed. The launcher's <c>ffxivgame.ver</c> sits next to the executable, so answering it
/// needs the directory, and the directory is only learnt while a client runs. Remembering it
/// turns "the version is unknown until you start the game" into "the version is known at
/// startup", which is what lets the profile layer match early and shared calibration be
/// fetched before the player logs in.
/// </summary>
public interface IGameInstallMemory
{
    /// <summary>
    /// The remembered executable path, or null when nothing trustworthy is remembered. Never
    /// throws: an unreadable or surprising memory is simply no memory.
    /// </summary>
    string? Recall();

    /// <summary>
    /// Remembers where a running client was just found. Never throws, and writes nothing when
    /// the value has not changed.
    /// </summary>
    /// <param name="executablePath">Main module path of the client that was just observed.</param>
    void Remember(string executablePath);
}

/// <summary>A memory that remembers nothing; the default, so nothing persists unless asked.</summary>
public sealed class NullGameInstallMemory : IGameInstallMemory
{
    /// <summary>Shared instance.</summary>
    public static NullGameInstallMemory Instance { get; } = new();

    /// <inheritdoc />
    public string? Recall() => null;

    /// <inheritdoc />
    public void Remember(string executablePath)
    {
        // Deliberately inert.
    }
}

/// <summary>
/// The remembered install path, kept in one small JSON file beside the database
/// (docs/privacy-boundary.md section 9, <c>game-install.json</c>).
///
/// The file holds a path and nothing else, and that path never leaves this process: it is fed
/// to the same bounded text read the running client's directory gets, and the region guess. A
/// value read back is therefore treated as untrusted input -- it must be an absolute path whose
/// file name is one of the two client executables, or it is ignored. Every failure, from a
/// locked file to a file somebody hand-edited, degrades to "nothing is remembered".
/// </summary>
public sealed class FileGameInstallMemory : IGameInstallMemory
{
    /// <summary>The one field the file holds.</summary>
    public const string PathField = "executable_path";

    /// <summary>Largest file this reader will open, in bytes.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Longest path accepted, read or written.</summary>
    public const int MaxPathLength = 1024;

    private static readonly string[] ExecutableNames =
    {
        GameProcessLocator.Dx11ProcessName + ".exe",
        GameProcessLocator.LegacyProcessName + ".exe",
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private string? _known;
    private bool _loaded;

    /// <summary>Creates a memory over one file.</summary>
    /// <param name="filePath">File to keep the path in; its directory is created on the first write.</param>
    public FileGameInstallMemory(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// A memory over <paramref name="filePath"/>, or an inert one when there is no file to use.
    /// </summary>
    /// <param name="filePath">File to keep the path in; null or blank means "do not persist".</param>
    public static IGameInstallMemory At(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath) ? NullGameInstallMemory.Instance : new FileGameInstallMemory(filePath);

    /// <inheritdoc />
    public string? Recall()
    {
        lock (_gate)
        {
            return Known();
        }
    }

    /// <summary>
    /// What the file says, read once: "nothing is remembered" is an answer too, and a closed
    /// game asks every few seconds. Only <see cref="Remember"/> changes it afterwards.
    /// </summary>
    private string? Known()
    {
        if (!_loaded)
        {
            _known = Read();
            _loaded = true;
        }

        return _known;
    }

    /// <inheritdoc />
    public void Remember(string executablePath)
    {
        if (Validated(executablePath) is not { } valid)
        {
            return;
        }

        lock (_gate)
        {
            // The status poll asks this about once a second for as long as the game runs, so
            // the ordinary case must not be a write. What is already on disk counts as known:
            // a restart on the same installation rewrites nothing.
            if (Same(Known(), valid))
            {
                return;
            }

            if (Write(valid))
            {
                _known = valid;
            }
        }
    }

    private static bool Same(string? left, string? right) =>
        left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path when it is one this software could have written, null otherwise. Absolute, of
    /// bounded length, and named after one of the two clients: nothing else may be handed to a
    /// file read on the strength of this file's contents. A client installed on a network
    /// location is deliberately not remembered: its version stays unknown until it runs.
    /// </summary>
    /// <param name="executablePath">Candidate path, from the process table or from the file.</param>
    private static string? Validated(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Length > MaxPathLength)
        {
            return null;
        }

        try
        {
            // A plain local drive letter only. A UNC, mapped-network or device path would make
            // the version read an outbound connection nobody listed (docs/privacy-boundary.md
            // section 8), and one to an unreachable host stalls the status poll besides.
            if (!Path.IsPathFullyQualified(executablePath) || !Export.ExportPaths.IsLocalFilePath(executablePath))
            {
                return null;
            }

            var name = Path.GetFileName(executablePath);
            foreach (var executable in ExecutableNames)
            {
                if (string.Equals(name, executable, StringComparison.OrdinalIgnoreCase))
                {
                    return executablePath;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the runtime cannot even inspect is not one we remembered.
        }

        return null;
    }

    private string? Read()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(_filePath)) is not JsonObject document ||
                document[PathField] is not JsonValue value ||
                !value.TryGetValue<string>(out var path))
            {
                return null;
            }

            return Validated(path);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool Write(string executablePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new JsonObject { [PathField] = executablePath };
            File.WriteAllText(_filePath, document.ToJsonString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Losing the memory costs the version being unknown until the game next starts,
            // which is exactly where this software stood before it remembered anything.
            return false;
        }
    }
}
