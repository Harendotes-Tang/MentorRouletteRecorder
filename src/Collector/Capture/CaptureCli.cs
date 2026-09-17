using System.Globalization;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The <c>--capture-doctor</c> mode: report what this machine offers the capture layer, and
/// change nothing. The live-validation guide has testers run it before any capture is
/// attempted. Detection only: it never starts a capture, opens a device, touches the game
/// process or writes to the database.
/// </summary>
public static class CaptureCli
{
    /// <summary>Command-line flag that selects this mode.</summary>
    public const string Flag = "--capture-doctor";

    /// <summary>Flag that switches the output to the sanitized JSON report.</summary>
    public const string JsonFlag = "--json";

    /// <summary>True when the arguments select this mode.</summary>
    /// <param name="args">Raw command line.</param>
    public static bool Matches(string[] args) =>
        args is not null && Array.Exists(args, arg => string.Equals(arg, Flag, StringComparison.Ordinal));

    /// <summary>
    /// Runs the diagnostics and prints them. Returns 0 when capture could start on this
    /// machine, and 1 when it could not, so a script can gate on it.
    /// </summary>
    /// <param name="args">Raw command line; <c>--json</c> selects the sanitized report.</param>
    /// <param name="services">Dependencies; the real machine when null.</param>
    /// <param name="output">Where to print; standard output when null.</param>
    public static int Run(string[] args, CaptureServices? services = null, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var writer = output ?? Console.Out;
        var json = Array.Exists(args, arg => string.Equals(arg, JsonFlag, StringComparison.Ordinal));

        using var controller = new CaptureController(
            (services ?? new CaptureServices()) with { EnableFollowTimer = false });

        var snapshot = controller.Snapshot();
        var adapters = controller.RescanAdapters();
        var temporary = InspectTempCopies(services);

        if (json)
        {
            var report = SanitizedDiagnosticsReport.Build(
                snapshot, Program.Version, SystemClock.Instance.UtcNow);
            report["adapter_count"] = adapters.Count;
            report["oodle_temp_copies"] = new JsonObject
            {
                ["registered_present"] = temporary.Present,
                ["registered_bytes"] = temporary.Bytes,
                ["registered_missing"] = temporary.Missing,
            };
            writer.WriteLine(report.ToJsonString(Ipc.Wire.JsonOptions));
        }
        else
        {
            WriteHuman(writer, snapshot, adapters, temporary);
        }

        return snapshot.Npcap.Usable && snapshot.Game.Running ? 0 : 1;
    }

    /// <summary>
    /// Reads the manifest of temporary game-executable copies this software owns.
    ///
    /// docs/privacy-boundary.md invites the user to check that the temp directory is clean. The
    /// manifest holds only what this software created, and it is read instead of enumerating the
    /// directory: a doctor that listed TEMP would inspect files that are none of its business
    /// (review finding H-2).
    /// </summary>
    /// <param name="services">Capture dependencies; the manifest path may be overridden here.</param>
    private static OodleTempManifestReading InspectTempCopies(CaptureServices? services)
    {
        var manifest = services?.OodleTempManifestPath
            ?? Storage.DatabasePaths.ResolveOodleTempManifest(null);
        try
        {
            return OodleTempCopyCleaner.InspectManifest(manifest);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return OodleTempManifestReading.Empty;
        }
    }

    private static void WriteHuman(
        TextWriter writer,
        CaptureDiagnosticsSnapshot snapshot,
        IReadOnlyList<CaptureAdapterView> adapters,
        OodleTempManifestReading temporary)
    {
        writer.WriteLine("抓包自检 / capture doctor");
        writer.WriteLine("  LIVE_CAPTURE_STATUS: " + CaptureDiagnosticsSnapshot.LiveCaptureStatus);
        writer.WriteLine("  monitor_type:        " + CaptureDiagnosticsSnapshot.MonitorType);
        writer.WriteLine("  injected_hook:       " + CaptureDiagnosticsSnapshot.InjectedHookEnabled);
        writer.WriteLine("  oodle_mode:          " + snapshot.Oodle);
        writer.WriteLine("  读取游戏可执行文件:  " + (snapshot.ReadsGameExecutable ? "是" : "否"));
        writer.WriteLine();

        writer.WriteLine("Npcap");
        writer.WriteLine("  status:              " + snapshot.Npcap.StatusToken);
        writer.WriteLine("  version:             " + (snapshot.Npcap.Version ?? "<未知>"));
        writer.WriteLine("  winpcap_compatible:  " + snapshot.Npcap.WinPcapCompatible);
        writer.WriteLine("  admin_only:          " + snapshot.Npcap.AdminOnly);
        writer.WriteLine("  已提权:              " + snapshot.Npcap.Elevated);
        if (!snapshot.Npcap.Usable)
        {
            writer.WriteLine("  → " + snapshot.Npcap.Guidance);
        }

        writer.WriteLine();
        writer.WriteLine("游戏进程");
        writer.WriteLine("  running:             " + snapshot.Game.Running);
        writer.WriteLine("  process_id:          " + Text(snapshot.Game.ProcessId));
        writer.WriteLine("  region:              " + EnumWire<Region>.Format(snapshot.Game.Region));
        writer.WriteLine("  game_build:          " + (snapshot.Game.GameBuild ?? "<未知>"));
        writer.WriteLine("  实例数:              " + snapshot.Game.InstanceCount);
        foreach (var warning in snapshot.Game.Warnings)
        {
            writer.WriteLine("  → " + warning);
        }

        writer.WriteLine();
        writer.WriteLine($"网卡（{adapters.Count}）");
        foreach (var adapter in adapters)
        {
            // Addresses are already masked to a /24; the doctor never prints a full address.
            var addresses = adapter.MaskedIPv4.Count == 0
                ? "<无 IPv4>"
                : string.Join(", ", adapter.MaskedIPv4);
            writer.WriteLine(
                $"  [{(adapter.Recommended ? "*" : " ")}] {adapter.FriendlyName} " +
                $"up={adapter.IsUp} loopback={adapter.IsLoopback} 游戏流量={adapter.CarriesGameTraffic} {addresses}");
        }

        writer.WriteLine();
        writer.WriteLine("协议档案");
        writer.WriteLine("  status:              " + EnumWire<ProfileStatus>.Format(snapshot.Profile.Status));
        if (snapshot.Profile.Message is { } message)
        {
            writer.WriteLine("  → " + message);
        }

        writer.WriteLine();
        var orphans = OodleTempCopyCleaner.InspectOrphans();
        writer.WriteLine("游戏程序临时副本（Machina 临时目录）");
        writer.WriteLine("  文件数:              " + orphans.Removed.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("  占用字节:            " + orphans.Bytes.ToString(CultureInfo.InvariantCulture));
        if (orphans.Removed > 0)
        {
            writer.WriteLine("  → 采集服务启动与每次停止抓包时会删除没有被占用的副本。");
        }

        writer.WriteLine();
        writer.WriteLine("游戏程序临时副本（本软件登记过的文件）");
        writer.WriteLine("  尚未删除:            " + temporary.Present.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("  占用字节:            " + temporary.Bytes.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("  登记但已不存在:      " + temporary.Missing.ToString(CultureInfo.InvariantCulture));
        if (temporary.Present > 0)
        {
            writer.WriteLine("  → 这些副本会在下次启动采集服务时自动删除。");
        }
    }

    private static string Text(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "<无>";
}
