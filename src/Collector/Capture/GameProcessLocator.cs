using System.ComponentModel;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Capture;

/// <summary>One running process that looks like the game client.</summary>
/// <param name="ProcessId">Process identifier.</param>
/// <param name="ProcessName">Process name without extension.</param>
/// <param name="StartedAtUtc">Process start time, when readable.</param>
/// <param name="ExecutablePath">
/// Image path from the kernel's process table, or null when it did not answer. It needs no
/// rights on the target, so whether the client was started elevated makes no difference.
/// </param>
/// <param name="AccessDenied">
/// True when the provider knows the path was withheld for permission reasons. The shipped
/// Windows provider never reports it any more: its only source is the kernel process table,
/// which needs no rights on the target at all and therefore has no permission failure to tell
/// apart from any other empty answer (audit 2026-09-21, finding 1). The flag stays part of the
/// contract because the diagnosis it drives is still the right one for a provider that can.
/// </param>
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
    /// <exception cref="InvalidOperationException">
    /// The listing itself failed. That is a different answer from an empty list, which says no
    /// such process is running, and the two must not be merged (docs/state-machine.md 3.6).
    /// </exception>
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
    /// <summary>
    /// Whether recording is waiting for an explicit client choice. Only ever true while
    /// <see cref="Processes"/> lists a client to choose; with none listed the reason alone says
    /// why nothing is locked.
    /// </summary>
    public bool SelectionRequired { get; init; }
    /// <summary>NONE, MULTIPLE, EXITED, or IDENTITY_UNAVAILABLE; kept for diagnostics even when no choice is asked for.</summary>
    public string SelectionReason { get; init; } = "NONE";
    /// <summary>Ephemeral choices; no paths or window titles cross IPC.</summary>
    public IReadOnlyList<GameProcessOption> Processes { get; init; } = Array.Empty<GameProcessOption>();
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
/// therefore also works when the launcher started the client elevated. The listing itself --
/// process ids and start times -- is one snapshot of the same table (<see cref="ProcessTable"/>),
/// so identifying a client opens no handle on it either. This code never reads
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
    private readonly Action<bool>? _listingObserved;

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
        : this(processes, files, regionOverride, installMemory, listingObserved: null)
    {
    }

    private GameProcessLocator(
        IGameProcessProvider? processes,
        IGameFileReader? files,
        Func<Region?>? regionOverride,
        IGameInstallMemory? installMemory,
        Action<bool>? listingObserved)
    {
        _processes = processes ?? WindowsGameProcessProvider.Instance;
        _files = files ?? WindowsGameFileReader.Instance;
        _regionOverride = regionOverride;
        _installMemory = installMemory ?? NullGameInstallMemory.Instance;
        _listingObserved = listingObserved;
    }

    /// <summary>Returns a copy of this locator that consults an explicit region override.</summary>
    /// <param name="regionOverride">Source of the override; may return null for "not set".</param>
    public GameProcessLocator WithRegionOverride(Func<Region?> regionOverride)
    {
        ArgumentNullException.ThrowIfNull(regionOverride);
        return new GameProcessLocator(_processes, _files, regionOverride, _installMemory, _listingObserved);
    }

    /// <summary>
    /// Returns a copy of this locator that reports the outcome of every process listing: true when
    /// the table was read, false when it could not be. A failed listing is "cannot tell" everywhere,
    /// so this is the only place it becomes visible (audit 2026-10-03, CS1-X1).
    /// </summary>
    /// <param name="listingObserved">Called once per listing, on the listing thread; must not throw.</param>
    public GameProcessLocator WithListingObserver(Action<bool> listingObserved)
    {
        ArgumentNullException.ThrowIfNull(listingObserved);
        return new GameProcessLocator(_processes, _files, _regionOverride, _installMemory, listingObserved);
    }

    /// <summary>True when this locator has somewhere to remember the install path.</summary>
    public bool RemembersInstall => !ReferenceEquals(_installMemory, NullGameInstallMemory.Instance);

    /// <summary>Returns a copy of this locator that remembers where the client is installed.</summary>
    /// <param name="installMemory">The memory to read at startup and write while the client runs.</param>
    public GameProcessLocator WithInstallMemory(IGameInstallMemory installMemory)
    {
        ArgumentNullException.ThrowIfNull(installMemory);
        return new GameProcessLocator(_processes, _files, _regionOverride, installMemory, _listingObserved);
    }

    /// <summary>
    /// Looks for a running client. Never throws, whatever the process list looks like: several
    /// instances, a process that exits mid-enumeration, or a path we may not read.
    /// </summary>
    public GameProcessDetection Locate()
    {
        // A process listing that fails is a diagnostic, not a crash: this caller simply learns
        // that the game is not visible from here.
        var candidates = ListCandidates() ?? Array.Empty<GameProcessCandidate>();

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

        return Describe(chosen, candidates.Count);
    }

    /// <summary>
    /// Lists candidates without selecting or remembering an installation, or returns null when
    /// the process listing itself failed. Null is not "no client is running": a caller deciding
    /// whether a client exited must read it as "cannot tell" (docs/state-machine.md 3.6).
    /// </summary>
    public IReadOnlyList<GameProcessCandidate>? ListCandidates()
    {
        var listed = new List<GameProcessCandidate>();
        foreach (var name in new[] { Dx11ProcessName, LegacyProcessName })
        {
            if (Safe(name) is not { } candidates)
            {
                _listingObserved?.Invoke(false);
                return null;
            }

            listed.AddRange(candidates);
        }

        _listingObserved?.Invoke(true);
        return listed.DistinctBy(candidate => candidate.ProcessId)
            .OrderBy(candidate => candidate.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.ProcessId).ToArray();
    }

    /// <summary>Reads metadata for this exact candidate, including its own client build.</summary>
    public GameProcessDetection Describe(GameProcessCandidate chosen, int instanceCount)
    {

        Remember(chosen.ExecutablePath);

        var warnings = new List<string>();
        if (instanceCount > 1)
            warnings.Add($"检测到 {instanceCount} 个 FFXIV 进程。请在总览或捕获诊断页确认当前记录对象。");

        if (chosen.ExecutablePath is null)
        {
            // Only a provider that can tell a permission failure from any other empty answer
            // reaches the first message; the Windows one cannot, so in practice this is the
            // second (see GameProcessCandidate.AccessDenied).
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
            // carries none of the markers, or markers of both regions. That is a permanent
            // condition and waiting will never fix it, so the message has to say what the
            // user can actually do (H-9).
            warnings.Add(
                (HasMarkersOfBothRegions(chosen.ExecutablePath)
                    ? "无法从安装路径判断区服（路径同时含有国服与国际服的标记）。"
                    : "无法从安装路径判断区服（路径可读，但不含可识别的区服标记）。") +
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
            instanceCount,
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
    internal GameProcessDetection FromRememberedInstall()
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
    /// unrecognised path stays <see cref="Region.Unknown"/> rather than defaulting to one, and
    /// so does a path carrying markers of both regions (a Global client in a folder named
    /// 最终幻想, say): which one wins would be a guess.
    /// </summary>
    /// <param name="executablePath">Main module path, or null.</param>
    public static Region GuessRegion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Region.Unknown;
        }

        var lowered = executablePath.ToLowerInvariant();
        var cn = CnMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
        var global = GlobalMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
        return cn == global ? Region.Unknown : cn ? Region.Cn : Region.Global;
    }

    /// <summary>True when the path carries markers of both regions.</summary>
    /// <param name="executablePath">Main module path.</param>
    private static bool HasMarkersOfBothRegions(string executablePath)
    {
        var lowered = executablePath.ToLowerInvariant();
        return CnMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)) &&
               GlobalMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
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
    /// Whether this client incarnation is still in the process listing. Reads only; it never
    /// changes which client is selected.
    ///
    /// Asked when a game connection has ended, where the answer decides between "the network
    /// dropped" (DISCONNECTED) and "the player closed the game" (INTERRUPTED), and after a
    /// client switch, to tell the user whether the chosen client is still there. It is the
    /// same process-listing fact <see cref="Locate"/> already reads,
    /// nothing is opened and nothing is read out of the process. A listing that fails, and the
    /// same process id listed without a readable start time, both answer "still running", so a
    /// diagnostic failure can never manufacture a terminal state (docs/state-machine.md 3.6).
    /// </summary>
    /// <param name="processId">Process id to look for; non-positive is never running.</param>
    /// <param name="startedAtUtc">
    /// Start time the incarnation was identified by. A process listed under the same id with a
    /// different start time is a reuse of the id, not this client; null matches any.
    /// </param>
    public bool IsRunning(int processId, DateTimeOffset? startedAtUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        // Cannot tell. Say yes: the caller only ever uses "no" to withhold evidence.
        return ListCandidates() is not { } candidates || candidates.Any(candidate =>
            candidate.ProcessId == processId &&
            (candidate.StartedAtUtc is null || startedAtUtc is null || candidate.StartedAtUtc == startedAtUtc));
    }

    private IReadOnlyList<GameProcessCandidate>? Safe(string processName)
    {
        try
        {
            return _processes.ByName(processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // A process listing that fails is a diagnostic, not a crash -- and not an empty
            // list either: the caller learns that it cannot tell.
            return null;
        }
    }
}

/// <summary>
/// Lists processes from one snapshot of the kernel's process table (<see cref="ProcessTable"/>):
/// process id, name and start time come from the same row, and no process is opened to read
/// any of them.
/// </summary>
public sealed class WindowsGameProcessProvider : IGameProcessProvider
{
    private readonly Func<IReadOnlyList<ProcessTableEntry>?> _readTable;

    /// <summary>Creates a provider over the real process table.</summary>
    public WindowsGameProcessProvider()
        : this(ProcessTable.TryRead)
    {
    }

    /// <summary>Creates a provider over a table source a test controls.</summary>
    /// <param name="readTable">One table snapshot, or null when it could not be read.</param>
    internal WindowsGameProcessProvider(Func<IReadOnlyList<ProcessTableEntry>?> readTable) =>
        _readTable = readTable;

    /// <summary>Shared instance.</summary>
    public static WindowsGameProcessProvider Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<GameProcessCandidate> ByName(string processName)
    {
        ArgumentException.ThrowIfNullOrEmpty(processName);

        // A table that could not be read is not an empty one; say so instead of reporting "no
        // game", which would read as the client having exited.
        var table = _readTable()
            ?? throw new InvalidOperationException("The kernel process table could not be read.");

        var results = new List<GameProcessCandidate>();
        foreach (var entry in table)
        {
            var name = ProcessTable.ShortName(entry.ImageName);
            if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // AccessDenied is false by construction: the process-table read is the only source
            // and it cannot report a permission failure (see GameProcessCandidate.AccessDenied).
            var path = ResolveExecutablePath(() => ProcessImagePath.TryRead(entry.ProcessId), name);
            results.Add(new GameProcessCandidate(entry.ProcessId, name, entry.CreatedAtUtc, path, AccessDenied: false));
        }

        return results;
    }

    /// <summary>
    /// Asks the kernel's process table (<see cref="ProcessImagePath"/>) for the executable path:
    /// it needs no handle and works when the client was started by an elevated launcher, which
    /// is how the CN launcher runs.
    ///
    /// There is deliberately no second source. The module listing that used to serve as one
    /// opens a handle to the game with <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c> and
    /// reads the target's module table, which docs/privacy-boundary.md section 2, rule 3b
    /// forbids outright and the project promises never to do; it was removed by the 2026-09-21
    /// audit (finding 1), and rule INJ-008 of the static boundary check now keeps it out. A path
    /// the table does not answer simply stays unknown, and the existing fail-closed chain
    /// handles that: region and build stay unknown and the profile layer refuses to parse.
    /// </summary>
    /// <param name="readFromProcessTable">Kernel process-table lookup for this process id.</param>
    /// <param name="processName">
    /// Process name from the same snapshot. The table lookup takes a process id and Windows
    /// reuses process ids, so between the snapshot and the read the id can belong to something
    /// else entirely. A path whose file name does not match the name we were looking at is
    /// therefore not this process's path, and is dropped rather than reported
    /// (review finding L-4).
    /// </param>
    internal static string? ResolveExecutablePath(
        Func<string?> readFromProcessTable, string? processName = null)
    {
        string? fromTable = null;
        try
        {
            fromTable = readFromProcessTable();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The table lookup is best effort; an answer it cannot give leaves the path unknown.
        }

        if (string.IsNullOrEmpty(fromTable) || !NameMatches(fromTable, processName))
        {
            return null;
        }

        return fromTable;
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
