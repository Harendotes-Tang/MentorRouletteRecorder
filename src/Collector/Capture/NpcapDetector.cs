using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace MentorRecorder.Collector.Capture;

/// <summary>Outcome of looking for a usable Npcap installation.</summary>
public enum NpcapStatus
{
    /// <summary>Installed, WinPcap-compatible, and usable by this process.</summary>
    Ready,

    /// <summary>Nothing found: no registry key and no library on disk.</summary>
    NotInstalled,

    /// <summary>Installed, but not in WinPcap API-compatible mode, which is the mode we need.</summary>
    NotWinPcapCompatible,

    /// <summary>Installed and restricted to administrators, and this process is not elevated.</summary>
    AdminOnly,

    /// <summary>Registered but its libraries are missing or unreadable.</summary>
    LoadFailed,
}

/// <summary>
/// What <see cref="NpcapDetector"/> found. Nothing here is ever used to download or install
/// anything: this software detects Npcap and explains how to install it, and does no more
/// (docs/privacy-boundary.md section 2, item 14).
/// </summary>
/// <param name="Status">Overall verdict.</param>
/// <param name="Version">Version string, when it could be read.</param>
/// <param name="WinPcapCompatible">True when the WinPcap API-compatible mode is installed.</param>
/// <param name="AdminOnly">True when the driver is restricted to administrators.</param>
/// <param name="Elevated">True when this process runs elevated.</param>
/// <param name="Guidance">User-facing explanation in Simplified Chinese.</param>
public sealed record NpcapDetection(
    NpcapStatus Status,
    string? Version,
    bool WinPcapCompatible,
    bool AdminOnly,
    bool Elevated,
    string Guidance)
{
    /// <summary>True when something recognisable as Npcap is present, usable or not.</summary>
    public bool Installed => Status != NpcapStatus.NotInstalled;

    /// <summary>True when capture may be attempted.</summary>
    public bool Usable => Status == NpcapStatus.Ready;

    /// <summary>
    /// The wire token for <see cref="Status"/>, spelled as docs/capture-diagnostics.md lists
    /// it. Written out rather than derived from the identifier because a mechanical
    /// conversion would render <c>NotWinPcapCompatible</c> as <c>NOT_WIN_PCAP_COMPATIBLE</c>.
    /// </summary>
    public string StatusToken => Status switch
    {
        NpcapStatus.Ready => "READY",
        NpcapStatus.NotInstalled => "NOT_INSTALLED",
        NpcapStatus.NotWinPcapCompatible => "NOT_WINPCAP_COMPATIBLE",
        NpcapStatus.AdminOnly => "NPCAP_ADMIN_ONLY",
        _ => "LOAD_FAILED",
    };
}

/// <summary>
/// The filesystem, registry and token facts <see cref="NpcapDetector"/> needs. Abstracted so
/// every verdict can be reproduced in a unit test on a machine that has no Npcap.
/// </summary>
public interface INpcapEnvironment
{
    /// <summary>Value of <c>%SystemRoot%</c>.</summary>
    string SystemRoot { get; }

    /// <summary>True when the current process runs with an elevated token.</summary>
    bool IsElevated { get; }

    /// <summary>True when a file exists.</summary>
    /// <param name="path">Absolute file path.</param>
    bool FileExists(string path);

    /// <summary>File version of a file, or null when it cannot be read.</summary>
    /// <param name="path">Absolute file path.</param>
    string? FileVersion(string path);

    /// <summary>True when an HKLM subkey exists.</summary>
    /// <param name="subKey">Subkey path below HKEY_LOCAL_MACHINE.</param>
    bool RegistryKeyExists(string subKey);

    /// <summary>Reads an HKLM value, or null when absent or unreadable.</summary>
    /// <param name="subKey">Subkey path below HKEY_LOCAL_MACHINE.</param>
    /// <param name="valueName">Value name.</param>
    object? RegistryValue(string subKey, string valueName);
}

/// <summary>
/// Detects Npcap: is it installed, which version, is the WinPcap-compatible mode present,
/// and is the driver restricted to administrators.
///
/// **Detection only.** This class never downloads, never installs, never starts a service and
/// never touches the driver. When Npcap is absent the answer is guidance naming the download
/// site and the install option to tick; nothing else happens
/// (docs/capture-diagnostics.md section 2).
/// </summary>
public sealed class NpcapDetector
{
    /// <summary>Primary registry key of an Npcap installation.</summary>
    public const string RegistryKey = @"SOFTWARE\Npcap";

    /// <summary>32-bit view of the same key, present on some installations.</summary>
    public const string RegistryKeyWow = @"SOFTWARE\WOW6432Node\Npcap";

    /// <summary>Registry value that records whether the WinPcap-compatible mode was installed.</summary>
    public const string WinPcapCompatibleValue = "WinPcapCompatible";

    /// <summary>Registry value that records whether the driver is restricted to administrators.</summary>
    public const string AdminOnlyValue = "AdminOnly";

    /// <summary>Guidance shown when nothing was found.</summary>
    public const string NotInstalledGuidance =
        "未检测到 Npcap。本软件需要 Npcap 才能被动读取本机网卡流量，" +
        "请从 Npcap 官方站点自行下载安装（安装时请勾选 “WinPcap API-compatible Mode”），" +
        "安装完成后重新启动本软件。本软件不会替您下载或安装任何驱动。";

    private readonly INpcapEnvironment _environment;

    /// <summary>Creates a detector over an environment.</summary>
    /// <param name="environment">Facts source; the real machine when null.</param>
    public NpcapDetector(INpcapEnvironment? environment = null) =>
        _environment = environment ?? WindowsNpcapEnvironment.Instance;

    /// <summary>Path of <c>wpcap.dll</c> inside the Npcap install folder.</summary>
    public string WpcapPath => Path.Combine(_environment.SystemRoot, "System32", "Npcap", "wpcap.dll");

    /// <summary>Path of <c>Packet.dll</c> inside the Npcap install folder.</summary>
    public string PacketPath => Path.Combine(_environment.SystemRoot, "System32", "Npcap", "Packet.dll");

    /// <summary>Path where the WinPcap-compatible mode places <c>wpcap.dll</c>.</summary>
    public string WinPcapCompatiblePath => Path.Combine(_environment.SystemRoot, "System32", "wpcap.dll");

    /// <summary>Runs the whole detection and returns a verdict with guidance.</summary>
    public NpcapDetection Detect()
    {
        var elevated = SafeElevated();
        var keyPresent = _environment.RegistryKeyExists(RegistryKey)
            || _environment.RegistryKeyExists(RegistryKeyWow);
        var wpcap = _environment.FileExists(WpcapPath);
        var packet = _environment.FileExists(PacketPath);

        if (!keyPresent && !wpcap && !packet)
        {
            return new NpcapDetection(
                NpcapStatus.NotInstalled, null, false, false, elevated, NotInstalledGuidance);
        }

        var version = _environment.FileVersion(WpcapPath) ?? ReadRegistryString("Version");
        var adminOnly = ReadFlag(AdminOnlyValue);

        // Registered but the libraries are gone: a partially removed or broken installation.
        // Saying NOT_INSTALLED here would send the user to reinstall without explaining why
        // their existing installation does not work.
        if (!wpcap || !packet)
        {
            return new NpcapDetection(
                NpcapStatus.LoadFailed,
                version,
                false,
                adminOnly,
                elevated,
                "检测到 Npcap 的安装记录，但 wpcap.dll 或 Packet.dll 不在 " +
                @"%SystemRoot%\System32\Npcap\ 下，安装可能不完整或已被部分卸载。" +
                "请重新安装 Npcap（安装时勾选 “WinPcap API-compatible Mode”）。");
        }

        var compatible = DetectWinPcapCompatible();
        if (!compatible)
        {
            return new NpcapDetection(
                NpcapStatus.NotWinPcapCompatible,
                version,
                false,
                adminOnly,
                elevated,
                "检测到 Npcap，但未安装 WinPcap 兼容模式。本软件通过 WinPcap 兼容接口抓包，" +
                "请重新运行 Npcap 安装程序并勾选 “WinPcap API-compatible Mode”。" +
                "（不需要勾选 “Support raw 802.11 traffic”。）");
        }

        if (adminOnly && !elevated)
        {
            return new NpcapDetection(
                NpcapStatus.AdminOnly,
                version,
                true,
                true,
                false,
                "Npcap 安装时启用了 “Restrict Npcap driver's access to Administrators only”，" +
                "当前进程没有管理员权限，无法打开网卡。请以管理员身份运行本软件，" +
                "或重新安装 Npcap 并取消该限制选项。");
        }

        return new NpcapDetection(
            NpcapStatus.Ready,
            version,
            true,
            adminOnly,
            elevated,
            "Npcap 可用。抓包全程只读，从不发送任何数据包。");
    }

    private bool DetectWinPcapCompatible()
    {
        // The registry flag is the installer's own record of the choice. When it is absent
        // (older builds did not write it) fall back to the observable consequence of the
        // option: the compatibility shim placed directly in System32.
        if (ReadRawFlag(WinPcapCompatibleValue) is { } declared)
        {
            return declared;
        }

        return _environment.FileExists(WinPcapCompatiblePath);
    }

    private bool ReadFlag(string valueName) => ReadRawFlag(valueName) ?? false;

    private bool? ReadRawFlag(string valueName)
    {
        foreach (var key in new[] { RegistryKey, RegistryKeyWow })
        {
            switch (_environment.RegistryValue(key, valueName))
            {
                case int number:
                    return number != 0;
                case long number:
                    return number != 0;
                case bool flag:
                    return flag;
                case string text when int.TryParse(text, out var parsed):
                    return parsed != 0;
            }
        }

        return null;
    }

    private string? ReadRegistryString(string valueName)
    {
        foreach (var key in new[] { RegistryKey, RegistryKeyWow })
        {
            if (_environment.RegistryValue(key, valueName) is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private bool SafeElevated()
    {
        try
        {
            return _environment.IsElevated;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>The real machine: file existence, file versions, HKLM values and the process token.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNpcapEnvironment : INpcapEnvironment
{
    /// <summary>Shared instance.</summary>
    public static WindowsNpcapEnvironment Instance { get; } = new();

    /// <inheritdoc />
    public string SystemRoot =>
        Environment.GetEnvironmentVariable("SystemRoot")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <inheritdoc />
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? FileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.FileVersion ?? info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool RegistryKeyExists(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public object? RegistryValue(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
