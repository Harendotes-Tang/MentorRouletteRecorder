using System.ComponentModel;
using System.Diagnostics;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Capture;

/// <summary>One running process that looks like the game client.</summary>
/// <param name="ProcessId">Process identifier.</param>
/// <param name="ProcessName">Process name without extension.</param>
/// <param name="StartedAtUtc">Process start time, when readable.</param>
/// <param name="ExecutablePath">Main module path, when readable without elevation.</param>
/// <param name="AccessDenied">True when the path could not be read for permission reasons.</param>
public sealed record GameProcessCandidate(
    int ProcessId,
    string ProcessName,
    DateTimeOffset? StartedAtUtc,
    string? ExecutablePath,
    bool AccessDenied);

/// <summary>Enumerates candidate game processes. Abstracted so the locator can be unit tested.</summary>
public interface IGameProcessProvider
{
    /// <summary>Lists running processes with the given name (no extension).</summary>
    /// <param name="processName">Process name such as <c>ffxiv_dx11</c>.</param>
    IReadOnlyList<GameProcessCandidate> ByName(string processName);
}

/// <summary>Reads the small text files that sit next to the game executable.</summary>
public interface IGameFileReader
{
    /// <summary>Reads a text file, or returns null when it is absent or unreadable.</summary>
    /// <param name="path">Absolute file path.</param>
    string? ReadText(string path);
}

/// <summary>
/// What the locator concluded about the game client.
///
/// <see cref="Running"/> and <see cref="GameBuild"/> are independent: with the client closed,
/// the build can still be known from the install directory this machine remembers, and a
/// caller that needs a process to observe must read <see cref="Running"/> for that.
/// </summary>
/// <param name="Running">True when a game process was found.</param>
/// <param name="ProcessId">Process identifier of the chosen instance.</param>
/// <param name="ProcessName">Process name of the chosen instance.</param>
/// <param name="StartedAtUtc">Start time of the chosen instance.</param>
/// <param name="Region">
/// Region guessed from the install path -- the running client's, or the remembered one when
/// the client is not running; Unknown when neither is readable.
/// </param>
/// <param name="GameBuild">
/// Build read from <c>ffxivgame.ver</c> in the running client's directory, or in the remembered
/// install directory when the client is not running; null when unavailable.
/// </param>
/// <param name="InstanceCount">How many candidate processes were seen.</param>
/// <param name="ExecutablePath">
/// Install path of the chosen instance. Never rendered to a client, and null whenever no
/// instance is running, the remembered path included.
/// </param>
/// <param name="Warnings">User-facing notes, free of paths and identifiers.</param>
public sealed record GameProcessDetection(
    bool Running,
    int? ProcessId,
    string? ProcessName,
    DateTimeOffset? StartedAtUtc,
    Region Region,
    string? GameBuild,
    int InstanceCount,
    string? ExecutablePath,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The "nothing is running" answer.</summary>
    public static GameProcessDetection NotRunning { get; } = new(
        false, null, null, null, Region.Unknown, null, 0, null, Array.Empty<string>());
}

/// <summary>
/// Finds the FFXIV client process and reads the two facts capture needs from it: its process
/// id, and enough context (region, build) for the protocol profile layer to fail closed.
///
/// Everything here is either an operating-system process listing or a small text file next to
/// the executable. Specifically: the client build comes from the launcher's
/// <c>ffxivgame.ver</c>, **a text file read from disk**, and the install path comes from the
/// kernel's process table (<see cref="ProcessImagePath"/>), which needs no process handle and
/// therefore also works when the launcher started the client elevated. This code never reads
/// the game's memory and never attaches to it in any way (docs/privacy-boundary.md section 2,
/// items 2 and 3). A path that still cannot be read is simply left unknown -- the region and
/// the build then stay unknown too, and the profile layer refuses to parse, which is the
/// correct fail-closed outcome.
///
/// With no client running, the same version file is read from the install directory this
/// machine remembers (<see cref="IGameInstallMemory"/>), so the build and the region are known
/// at startup rather than only after the player logs in. That answer says <c>Running = false</c>
/// and names no process: it is about the installation, not about a session to observe. The
/// memory is opt-in -- the default locator persists nothing -- and remains the same two reads
/// as before: the process table, and a small text file on disk.
/// </summary>
public sealed class GameProcessLocator
{
    /// <summary>DirectX 11 client, the one every supported region ships today.</summary>
    public const string Dx11ProcessName = "ffxiv_dx11";

    /// <summary>Legacy DirectX 9 client name, still checked so a running client is never missed.</summary>
    public const string LegacyProcessName = "ffxiv";

    /// <summary>Name of the launcher's version file that sits next to the executable.</summary>
    public const string VersionFileName = "ffxivgame.ver";

    /// <summary>Longest build string accepted from the version file.</summary>
    public const int MaxBuildLength = 64;

    private static readonly string[] CnMarkers =
    {
        "sdoa", "shanda", "sdogame", "sdo\\", "ff14cn", "盛趣", "最终幻想",
    };

    private static readonly string[] GlobalMarkers =
    {
        "square enix", "squareenix", "final fantasy xiv - a realm reborn", "ffxiv - a realm reborn",
    };

    private readonly IGameProcessProvider _processes;
    private readonly IGameFileReader _files;
    private readonly Func<Region?>? _regionOverride;
    private readonly IGameInstallMemory _installMemory;

    /// <summary>Creates a locator.</summary>
    /// <param name="processes">Process listing source; the real machine when null.</param>
    /// <param name="files">Text file source; the real filesystem when null.</param>
    /// <param name="regionOverride">
    /// Optional source of an explicit region, read on every <see cref="Locate"/>.
    /// <see cref="GuessRegion"/> matches substrings of the install path, and an ordinary CN
    /// installation under, say, <c>D:\Games\FF14\</c> contains none of the markers; without an
    /// override such a user is permanently locked out of every path that requires a known
    /// region.
    /// </param>
    /// <param name="installMemory">
    /// Where the install path is remembered across runs; inert by default, so only the shipping
    /// service persists anything and a test's locator never touches the real data directory.
    /// </param>
    public GameProcessLocator(
        IGameProcessProvider? processes = null,
        IGameFileReader? files = null,
        Func<Region?>? regionOverride = null,
        IGameInstallMemory? installMemory = null)
    {
        _processes = processes ?? WindowsGameProcessProvider.Instance;
        _files = files ?? WindowsGameFileReader.Instance;
        _regionOverride = regionOverride;
        _installMemory = installMemory ?? NullGameInstallMemory.Instance;
    }

    /// <summary>Returns a copy of this locator that consults an explicit region override.</summary>
    /// <param name="regionOverride">Source of the override; may return null for "not set".</param>
    public GameProcessLocator WithRegionOverride(Func<Region?> regionOverride)
    {
        ArgumentNullException.ThrowIfNull(regionOverride);
        return new GameProcessLocator(_processes, _files, regionOverride, _installMemory);
    }

    /// <summary>True when this locator has somewhere to remember the install path.</summary>
    public bool RemembersInstall => !ReferenceEquals(_installMemory, NullGameInstallMemory.Instance);

    /// <summary>Returns a copy of this locator that remembers where the client is installed.</summary>
    /// <param name="installMemory">The memory to read at startup and write while the client runs.</param>
    public GameProcessLocator WithInstallMemory(IGameInstallMemory installMemory)
    {
        ArgumentNullException.ThrowIfNull(installMemory);
        return new GameProcessLocator(_processes, _files, _regionOverride, installMemory);
    }

    /// <summary>
    /// Looks for a running client. Never throws, whatever the process list looks like: several
    /// instances, a process that exits mid-enumeration, or a path we may not read.
    /// </summary>
    public GameProcessDetection Locate()
    {
        var candidates = new List<GameProcessCandidate>();
        foreach (var name in new[] { Dx11ProcessName, LegacyProcessName })
        {
            candidates.AddRange(Safe(name));
        }

        if (candidates.Count == 0)
        {
            return FromRememberedInstall();
        }

        // Several clients can legitimately run at once (two accounts, or a stale process the
        // OS has not reaped). Picking the oldest is deterministic and matches the session the
        // user has been playing longest; the ambiguity is reported rather than hidden.
        var chosen = candidates
            .OrderBy(candidate => candidate.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.ProcessId)
            .First();

        Remember(chosen.ExecutablePath);

        var warnings = new List<string>();
        if (candidates.Count > 1)
        {
            warnings.Add(
                $"检测到 {candidates.Count} 个 FFXIV 进程，已选择最早启动的一个（PID {chosen.ProcessId}）。" +
                "若观察的不是这一个，请在诊断页手动指定进程 id。");
        }

        if (chosen.ExecutablePath is null)
        {
            warnings.Add(chosen.AccessDenied
                ? "没有权限读取游戏安装路径，区服与客户端版本将保持未知；此时不会进行任何自动记录（fail-closed）。"
                : "无法确定游戏安装路径，区服与客户端版本将保持未知；此时不会进行任何自动记录（fail-closed）。");
        }

        var detected = GuessRegion(chosen.ExecutablePath);
        var region = detected;
        if (SafeRegionOverride() is { } forced)
        {
            region = forced;
            if (detected != Region.Unknown && detected != forced)
            {
                warnings.Add(
                    "设置中手动指定的区服与安装路径推断出的区服不一致，已按手动指定的区服处理。" +
                    "若这不是你想要的，请到设置里清除区服覆盖。");
            }
        }
        else if (detected == Region.Unknown && chosen.ExecutablePath is not null)
        {
            // Distinct from "the path is unreadable": the path was read fine, it simply
            // carries none of the markers. That is a permanent condition and waiting will
            // never fix it, so the message has to say what the user can actually do (H-9).
            warnings.Add(
                "无法从安装路径判断区服（路径可读，但不含可识别的区服标记）。" +
                "请到设置里手动指定区服（国服 / 国际服）；在此之前不会进行任何自动记录（fail-closed）。");
        }

        var build = ReadBuild(chosen.ExecutablePath);
        if (chosen.ExecutablePath is not null && build is null)
        {
            warnings.Add($"未能在游戏目录中读取到 {VersionFileName}，客户端版本未知。");
        }

        return new GameProcessDetection(
            true,
            chosen.ProcessId,
            chosen.ProcessName,
            chosen.StartedAtUtc,
            region,
            build,
            candidates.Count,
            chosen.ExecutablePath,
            warnings);
    }

    /// <summary>
    /// Writes the install path to the memory, when there is one and the path was readable.
    /// A memory that fails, or one a caller supplied that throws, costs nothing here.
    /// </summary>
    /// <param name="executablePath">Main module path of the chosen instance, or null.</param>
    private void Remember(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            _installMemory.Remember(executablePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Remembering is an optimisation for the next launch, never a requirement of this one.
        }
    }

    /// <summary>
    /// What is known about the client while none is running: the build and the region, read
    /// from the install directory this machine remembers.
    ///
    /// The install can have moved, been uninstalled, or sit on a drive that is not plugged in
    /// today; the version file is then unreadable and the answer is the plain "nothing is
    /// running", the same fail-closed outcome as before. The memory is deliberately not erased
    /// over it -- an unplugged drive is not an uninstall, and the next run of the game rewrites
    /// it anyway.
    /// </summary>
    private GameProcessDetection FromRememberedInstall()
    {
        string? remembered;
        try
        {
            remembered = _installMemory.Recall();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return GameProcessDetection.NotRunning;
        }

        if (string.IsNullOrWhiteSpace(remembered) || ReadBuild(remembered) is not { } build)
        {
            return GameProcessDetection.NotRunning;
        }

        // Everything about a session stays empty: no process was found, and the path is not
        // carried out of here either, so nothing downstream can read this as a client to
        // observe or render a path it must not (docs/privacy-boundary.md section 5).
        return new GameProcessDetection(
            false, null, null, null,
            SafeRegionOverride() ?? GuessRegion(remembered),
            build, 0, null, Array.Empty<string>());
    }

    /// <summary>
    /// Guesses the service region from the install path. Only a confident match answers; an
    /// unrecognised path stays <see cref="Region.Unknown"/> rather than defaulting to one.
    /// </summary>
    /// <param name="executablePath">Main module path, or null.</param>
    public static Region GuessRegion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Region.Unknown;
        }

        var lowered = executablePath.ToLowerInvariant();
        foreach (var marker in CnMarkers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                return Region.Cn;
            }
        }

        foreach (var marker in GlobalMarkers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                return Region.Global;
            }
        }

        return Region.Unknown;
    }

    private Region? SafeRegionOverride()
    {
        if (_regionOverride is null)
        {
            return null;
        }

        try
        {
            var region = _regionOverride();
            return region is Region.Cn or Region.Global ? region : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The override comes from the settings table, which can be busy or corrupt.
            // Losing it degrades to path detection; it must never break process discovery.
            return null;
        }
    }

    /// <summary>
    /// Reads the client build from <c>ffxivgame.ver</c> next to the executable. Only a short,
    /// plainly-formed token is accepted; anything else is treated as unreadable so that a
    /// surprising file can never end up in a database column or on the wire.
    /// </summary>
    /// <param name="executablePath">Main module path, or null.</param>
    public string? ReadBuild(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(executablePath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var text = _files.ReadText(Path.Combine(directory, VersionFileName));
        if (text is null)
        {
            return null;
        }

        var build = text.Trim();
        if (build.Length == 0 || build.Length > MaxBuildLength)
        {
            return null;
        }

        foreach (var c in build)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-')
            {
                return null;
            }
        }

        return build;
    }

    /// <summary>
    /// Whether a client process with this id is still in the process listing.
    ///
    /// Asked at exactly one moment: a game connection has ended, and the answer decides
    /// between "the network dropped" (DISCONNECTED) and "the player closed the game"
    /// (INTERRUPTED). It is the same process-listing fact <see cref="Locate"/> already reads,
    /// nothing is opened and nothing is read out of the process, and a listing that fails
    /// answers "still running" so a diagnostic failure can never manufacture a terminal state.
    /// </summary>
    /// <param name="processId">Process id to look for; non-positive is never running.</param>
    public bool IsRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        var listed = false;
        foreach (var name in new[] { Dx11ProcessName, LegacyProcessName })
        {
            IReadOnlyList<GameProcessCandidate> candidates;
            try
            {
                candidates = _processes.ByName(name);
            }
            catch (Exception ex)
                when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Cannot tell. Say yes: the caller only ever uses "no" to withhold evidence.
                return true;
            }

            listed = true;
            foreach (var candidate in candidates)
            {
                if (candidate.ProcessId == processId)
                {
                    return true;
                }
            }
        }

        return !listed;
    }

    private IReadOnlyList<GameProcessCandidate> Safe(string processName)
    {
        try
        {
            return _processes.ByName(processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // A process listing that fails is a diagnostic, not a crash: the caller simply
            // learns that the game is not visible from here.
            return Array.Empty<GameProcessCandidate>();
        }
    }
}

/// <summary>Lists processes with <see cref="Process.GetProcessesByName(string)"/>.</summary>
public sealed class WindowsGameProcessProvider : IGameProcessProvider
{
    /// <summary>Shared instance.</summary>
    public static WindowsGameProcessProvider Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<GameProcessCandidate> ByName(string processName)
    {
        ArgumentException.ThrowIfNullOrEmpty(processName);

        var results = new List<GameProcessCandidate>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                results.Add(Describe(process));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // The process exited between the listing and the read. Skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    private static GameProcessCandidate Describe(Process process)
    {
        DateTimeOffset? started = null;
        try
        {
            started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Start time needs a query handle we may not have. Not fatal.
        }

        var (path, accessDenied) = ResolveExecutablePath(
            () => ProcessImagePath.TryRead(process.Id),
            () => process.MainModule?.FileName,
            process.ProcessName);
        return new GameProcessCandidate(process.Id, process.ProcessName, started, path, accessDenied);
    }

    /// <summary>
    /// Asks the kernel's process table first (<see cref="ProcessImagePath"/>): it needs no handle
    /// and works when the client was started by an elevated launcher, which is how the CN
    /// launcher runs. The module listing is tried only when that yields nothing, so the
    /// "access denied" diagnosis is still produced for the rare process neither can name.
    /// </summary>
    /// <param name="readFromProcessTable">Kernel process-table lookup for this process id.</param>
    /// <param name="readMainModule">Module-listing fallback for the same process.</param>
    /// <param name="processName">
    /// Process name from the same snapshot. The table lookup takes a process id and Windows
    /// reuses process ids, so between the snapshot and the read the id can belong to something
    /// else entirely. A path whose file name does not match the name we were looking at is
    /// therefore not this process's path, and is dropped rather than reported
    /// (review finding L-4).
    /// </param>
    internal static (string? Path, bool AccessDenied) ResolveExecutablePath(
        Func<string?> readFromProcessTable, Func<string?> readMainModule, string? processName = null)
    {
        string? fromTable = null;
        try
        {
            fromTable = readFromProcessTable();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The table lookup is best effort; the module listing below is the known fallback.
        }

        if (!string.IsNullOrEmpty(fromTable) && !NameMatches(fromTable, processName))
        {
            fromTable = null;
        }

        return string.IsNullOrEmpty(fromTable) ? ReadExecutablePath(readMainModule) : (fromTable, false);
    }

    /// <summary>
    /// True when <paramref name="path"/> names <paramref name="processName"/> plus
    /// <c>.exe</c>. A caller that did not state a name gets the benefit of the doubt.
    /// </summary>
    /// <param name="path">Path read from the process table.</param>
    /// <param name="processName">Process name from the snapshot, without extension.</param>
    internal static bool NameMatches(string path, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFileName(path),
                processName + ".exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the path without treating every Windows query failure as access denied.
    /// Module enumeration may also fail during process startup/exit or with a partial copy.
    /// </summary>
    internal static (string? Path, bool AccessDenied) ReadExecutablePath(Func<string?> readPath)
    {
        try
        {
            return (readPath(), false);
        }
        catch (Win32Exception ex)
        {
            // ERROR_ACCESS_DENIED is 5. ERROR_PARTIAL_COPY (299), invalid handles and
            // process-exit races say nothing about the launcher's elevation setting.
            return (null, ex.NativeErrorCode == 5);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return (null, false);
        }
    }
}

/// <summary>Reads small text files from the real filesystem.</summary>
public sealed class WindowsGameFileReader : IGameFileReader
{
    /// <summary>Largest file this reader will open, in bytes.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Shared instance.</summary>
    public static WindowsGameFileReader Instance { get; } = new();

    /// <inheritdoc />
    public string? ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
