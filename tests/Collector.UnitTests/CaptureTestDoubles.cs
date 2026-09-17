using System.Net;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A filesystem, registry and elevation token a test declares outright.
///
/// Development machines have no Npcap installed, so NOT_INSTALLED would otherwise be the only
/// reachable verdict. Declaring the machine lets all five statuses be exercised.
/// </summary>
internal sealed class FakeNpcapEnvironment : INpcapEnvironment
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Key, string Value), object?> _values = new();

    public string SystemRoot { get; set; } = @"C:\Windows";

    public bool IsElevated { get; set; }

    public string? Version { get; set; }

    public FakeNpcapEnvironment WithFile(string path)
    {
        _files.Add(path);
        return this;
    }

    public FakeNpcapEnvironment WithKey(string subKey)
    {
        _keys.Add(subKey);
        return this;
    }

    public FakeNpcapEnvironment WithValue(string subKey, string valueName, object? value)
    {
        _keys.Add(subKey);
        _values[(subKey, valueName)] = value;
        return this;
    }

    /// <summary>A machine with a complete, WinPcap-compatible, unrestricted installation.</summary>
    public static FakeNpcapEnvironment Healthy()
    {
        var environment = new FakeNpcapEnvironment { Version = "1.79" };
        environment
            .WithFile(@"C:\Windows\System32\Npcap\wpcap.dll")
            .WithFile(@"C:\Windows\System32\Npcap\Packet.dll")
            .WithFile(@"C:\Windows\System32\wpcap.dll")
            .WithValue(NpcapDetector.RegistryKey, NpcapDetector.WinPcapCompatibleValue, 1)
            .WithValue(NpcapDetector.RegistryKey, NpcapDetector.AdminOnlyValue, 0);
        return environment;
    }

    public bool FileExists(string path) => _files.Contains(path);

    public string? FileVersion(string path) => _files.Contains(path) ? Version : null;

    public bool RegistryKeyExists(string subKey) => _keys.Contains(subKey);

    public object? RegistryValue(string subKey, string valueName) =>
        _values.TryGetValue((subKey, valueName), out var value) ? value : null;
}

/// <summary>A process list a test declares outright.</summary>
internal sealed class FakeGameProcessProvider : IGameProcessProvider
{
    private readonly Dictionary<string, List<GameProcessCandidate>> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public FakeGameProcessProvider Add(
        string processName,
        int processId,
        DateTimeOffset? startedAt = null,
        string? path = null,
        bool accessDenied = false)
    {
        if (!_byName.TryGetValue(processName, out var list))
        {
            list = new List<GameProcessCandidate>();
            _byName[processName] = list;
        }

        list.Add(new GameProcessCandidate(processId, processName, startedAt, path, accessDenied));
        return this;
    }

    /// <summary>Removes every process, as if the game had exited.</summary>
    public void Clear() => _byName.Clear();

    public IReadOnlyList<GameProcessCandidate> ByName(string processName) =>
        _byName.TryGetValue(processName, out var list)
            ? list
            : Array.Empty<GameProcessCandidate>();
}

/// <summary>A set of files a test declares outright.</summary>
internal sealed class FakeGameFileReader : IGameFileReader
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeGameFileReader With(string path, string content)
    {
        _files[path] = content;
        return this;
    }

    public string? ReadText(string path) => _files.TryGetValue(path, out var content) ? content : null;
}

/// <summary>An adapter list a test declares outright.</summary>
internal sealed class FakeAdapterProvider : IAdapterProvider
{
    private readonly List<AdapterInfo> _adapters = new();

    public FakeAdapterProvider Add(
        string id,
        string name,
        bool isUp = true,
        bool isLoopback = false,
        bool hasDefaultRoute = false,
        params string[] addresses)
    {
        _adapters.Add(new AdapterInfo(
            id,
            name,
            name + " driver",
            isUp,
            isLoopback,
            addresses.Select(IPAddress.Parse).ToArray(),
            hasDefaultRoute));
        return this;
    }

    public IReadOnlyList<AdapterInfo> List() => _adapters;
}

/// <summary>A TCP table a test declares outright.</summary>
internal sealed class FakeProcessTcpTable : IProcessTcpTable
{
    private readonly Dictionary<int, List<IPAddress>> _byPid = new();

    public FakeProcessTcpTable With(int processId, params string[] addresses)
    {
        _byPid[processId] = addresses.Select(IPAddress.Parse).ToList();
        return this;
    }

    public IReadOnlyList<IPAddress> LocalAddresses(int processId) =>
        _byPid.TryGetValue(processId, out var list) ? list : Array.Empty<IPAddress>();
}

/// <summary>A profile status a test declares outright.</summary>
internal sealed class FakeProfileStatusProvider : IProfileStatusProvider
{
    public FakeProfileStatusProvider(Domain.ProfileStatus status) =>
        Current = new ProfileStatusSnapshot(status, null, Domain.Region.Unknown, null, null, null, null);

    public ProfileStatusSnapshot Current { get; set; }
}

/// <summary>Records the capture status changes a controller published.</summary>
internal sealed class RecordingStatusListener : ICaptureStatusListener
{
    private readonly List<(CaptureControllerState State, string Message)> _entries = new();

    public IReadOnlyList<(CaptureControllerState State, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void OnCaptureStatusChanged(CaptureDiagnosticsSnapshot snapshot, string message)
    {
        lock (_entries)
        {
            _entries.Add((snapshot.State, message));
        }
    }
}

/// <summary>Records the lifecycle callbacks a controller made.</summary>
internal sealed class RecordingLifecycleListener : ICaptureLifecycleListener
{
    private readonly List<string> _events = new();

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>Total observations reported as dropped, summed across all calls.</summary>
    public long DroppedTotal
    {
        get
        {
            lock (_events)
            {
                return _events
                    .Where(entry => entry.StartsWith("dropped:", StringComparison.Ordinal))
                    .Sum(entry => long.Parse(entry["dropped:".Length..], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    public void OnCaptureStarted(string captureSessionId)
    {
        lock (_events)
        {
            _events.Add("started");
        }
    }

    public void OnCaptureStopped(string captureSessionId, Domain.CaptureEndReason reason)
    {
        lock (_events)
        {
            _events.Add("stopped:" + reason);
        }
    }

    public void OnEventsDropped(string captureSessionId, long droppedCount)
    {
        lock (_events)
        {
            _events.Add("dropped:" + droppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public void OnConnectionLost(string captureSessionId)
    {
        lock (_events)
        {
            _events.Add("connection_lost");
        }
    }
}
