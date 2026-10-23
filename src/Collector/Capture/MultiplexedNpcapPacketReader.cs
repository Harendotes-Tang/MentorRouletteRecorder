namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Owns at most eight native readers and polls each once without blocking. The caller still
/// owns one read worker and one shared ingress budget; native resources remain owned until a
/// successful close so failed cleanup can be retried safely.
/// </summary>
internal sealed class MultiplexedNpcapPacketReader : INpcapPacketReader
{
    private sealed record Entry(CaptureInterfaceBinding Binding, INpcapPacketReader Reader);

    private readonly CapturePathPlan _plan;
    private readonly string? _requiredId;
    private readonly Func<CaptureInterfaceBinding, INpcapPacketReader> _factory;
    private readonly List<Entry> _owned = new();
    private readonly List<Entry> _active = new();
    private Entry[] _pollers = Array.Empty<Entry>();
    private CapturePathStatus _status = CapturePathStatus.Unmeasured;
    private bool _openAttempted;
    private int _next;

    internal MultiplexedNpcapPacketReader(CaptureStartOptions options,
        Func<CaptureInterfaceBinding, INpcapPacketReader>? factory = null)
    {
        _plan = CapturePathPlan.FromOptions(options);
        _requiredId = options.AdapterId;
        _factory = factory ?? (binding => new NpcapPacketReader(options, binding));
    }

    public CapturePathStatus PathStatus => Volatile.Read(ref _status);

    public long DroppedPackets => Volatile.Read(ref _pollers).Sum(entry => entry.Reader.DroppedPackets);

    public void Open()
    {
        if (_openAttempted || _owned.Count != 0) throw new InvalidOperationException("捕获路径已准备或尚未释放。");
        if (_plan.Bindings.Count is < 1 or > CapturePathPlan.MaxInterfaces)
            throw new InvalidOperationException("捕获路径候选数量超出限制。");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in _plan.Bindings)
        {
            NpcapPacketReader.ValidateBinding(binding);
            if (!identities.Add(CapturePathPlan.NormalizeIdentity(binding.AdapterId)))
                throw new InvalidOperationException("捕获路径含重复网卡。");
        }
        var required = _plan.Bindings.FirstOrDefault(binding => string.IsNullOrWhiteSpace(_requiredId)
            ? ReferenceEquals(binding, _plan.Bindings[0])
            : string.Equals(CapturePathPlan.NormalizeIdentity(binding.AdapterId),
                CapturePathPlan.NormalizeIdentity(_requiredId), StringComparison.OrdinalIgnoreCase));
        if (required is null) throw new InvalidOperationException("捕获路径未保留所选网卡。");

        _openAttempted = true;
        var failures = 0;
        foreach (var binding in new[] { required }.Concat(_plan.Bindings.Where(b => !ReferenceEquals(b, required))))
        {
            Entry? entry = null;
            try
            {
                entry = new Entry(binding, _factory(binding));
                _owned.Add(entry); // Own it before Open: a partial open may already hold a native handle.
                entry.Reader.Open();
                _active.Add(entry);
                Volatile.Write(ref _pollers, _active.ToArray());
            }
            catch (Exception openError)
            {
                failures++;
                Exception? cleanupError = null;
                if (entry is not null)
                {
                    try { entry.Reader.Dispose(); _owned.Remove(entry); }
                    catch (Exception error) { cleanupError = error; }
                }
                UpdateStatus(failures);
                if (cleanupError is not null)
                    throw new AggregateException("捕获路径启动失败且资源尚未释放，保留供重试。", openError, cleanupError);
                if (ReferenceEquals(binding, required)) throw;
                continue;
            }
            UpdateStatus(failures);
        }
    }

    private void UpdateStatus(int failures) => Volatile.Write(ref _status, new CapturePathStatus(
        _plan.Bindings.Count, _active.Count, failures, _plan.CandidateLimitReached,
        _active.Any(entry => entry.Binding.IsLoopback)));

    public bool Read(NpcapPacketReceiver receive)
    {
        var pollers = Volatile.Read(ref _pollers);
        if (pollers.Length == 0) throw new InvalidOperationException("Npcap 未打开。");
        var received = false;
        // Rotate the starting device so a busy first interface never starves the others.
        var first = _next++ % pollers.Length;
        if (_next == int.MaxValue) _next = 0;
        for (var i = 0; i < pollers.Length; i++)
            received |= pollers[(first + i) % pollers.Length].Reader.Read(receive);
        return received;
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        foreach (var entry in _owned.ToArray())
        {
            try
            {
                entry.Reader.Dispose();
                _owned.Remove(entry);
                _active.Remove(entry);
                Volatile.Write(ref _pollers, _active.ToArray());
            }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (_openAttempted) UpdateStatus(_status.OpenFailures ?? 0);
        if (failures is not null) throw new AggregateException("部分捕获路径资源尚未释放，保留供重试。", failures);
    }
}
