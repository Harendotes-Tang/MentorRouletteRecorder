namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Owns at most eight native readers and polls each once without blocking. The caller still
/// owns one read worker and one shared ingress budget; native resources remain owned until a
/// successful close so failed cleanup can be retried safely.
///
/// Only the selected path is required. An optional path that fails while reading -- a tunnel
/// switched off, an adapter unplugged, sustained loss on the loopback -- is closed and left out,
/// counted in <see cref="PathStatus"/>, and the capture carries on over the others; only the
/// failure of the selected path, or of one that carried the game (<see cref="TreatAsRequired"/>),
/// ends it (audit 2026-11-03, A1-2; re-review RV1-1). Loss is kept per path, so the caller can
/// count only the paths that carried the game.
/// </summary>
internal sealed class MultiplexedNpcapPacketReader : INpcapPacketReader
{
    private sealed record Entry(CaptureInterfaceBinding Binding, INpcapPacketReader Reader);

    private readonly CapturePathPlan _plan;
    private readonly string? _requiredId;
    private readonly Func<CaptureInterfaceBinding, INpcapPacketReader> _factory;
    private readonly NpcapPacketReceiver _guarded;
    private readonly List<Entry> _owned = new();
    private readonly List<Entry> _active = new();
    private Entry[] _pollers = Array.Empty<Entry>();
    private (CaptureInterfaceBinding Binding, long Dropped)[] _closed = Array.Empty<(CaptureInterfaceBinding, long)>();
    private CapturePathStatus _status = CapturePathStatus.Unmeasured;
    private CaptureInterfaceBinding? _required;
    private Func<CaptureInterfaceBinding, bool>? _essential;
    private NpcapPacketReceiver? _receive;
    private bool _receiving, _openAttempted;
    private int _next, _failures;

    internal MultiplexedNpcapPacketReader(CaptureStartOptions options,
        Func<CaptureInterfaceBinding, INpcapPacketReader>? factory = null)
    {
        _plan = CapturePathPlan.FromOptions(options);
        _requiredId = options.AdapterId;
        _factory = factory ?? (binding => new NpcapPacketReader(options, binding));
        _guarded = Receive;
    }

    public CapturePathStatus PathStatus => Volatile.Read(ref _status);

    /// <summary>
    /// Makes the paths <paramref name="essential"/> selects as essential as the selected one: their
    /// read failure ends the capture instead of closing them. The monitor passes "carried one of the
    /// game's connections": a closed path that carried the game would leave it unreadable while the
    /// running plan still looks intact (re-review 2026-11-03, RV1-1). Asked only when a path fails.
    /// </summary>
    /// <param name="essential">Selects the paths whose failure must end the capture.</param>
    internal void TreatAsRequired(Func<CaptureInterfaceBinding, bool> essential) =>
        Volatile.Write(ref _essential, essential ?? throw new ArgumentNullException(nameof(essential)));

    public long DroppedPackets => DroppedPacketsOn(static _ => true);

    public long DroppedPacketsOn(Func<CaptureInterfaceBinding, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        // Read in the opposite order to Close's writes: a path being closed right now is then
        // counted twice for an instant, never left out of the loss reading.
        var pollers = Volatile.Read(ref _pollers);
        var closed = Volatile.Read(ref _closed);
        return closed.Where(path => include(path.Binding)).Sum(path => path.Dropped) +
            pollers.Where(entry => include(entry.Binding)).Sum(entry => entry.Reader.DroppedPackets);
    }

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
        _required = required;
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
                _failures++;
                Exception? cleanupError = null;
                if (entry is not null)
                {
                    try { entry.Reader.Dispose(); _owned.Remove(entry); }
                    catch (Exception error) { cleanupError = error; }
                }
                UpdateStatus();
                if (cleanupError is not null)
                    throw new AggregateException("捕获路径启动失败且资源尚未释放，保留供重试。", openError, cleanupError);
                if (ReferenceEquals(binding, required)) throw;
                continue;
            }
            UpdateStatus();
        }
    }

    private void UpdateStatus() => Volatile.Write(ref _status, new CapturePathStatus(
        _plan.Bindings.Count, _active.Count, _failures, _plan.CandidateLimitReached,
        _active.Any(entry => entry.Binding.IsLoopback)));

    public bool Read(NpcapPacketReceiver receive)
    {
        var pollers = Volatile.Read(ref _pollers);
        if (pollers.Length == 0) throw new InvalidOperationException("Npcap 未打开。");
        var received = false;
        // Rotate the starting device so a busy first interface never starves the others.
        var first = _next++ % pollers.Length;
        if (_next == int.MaxValue) _next = 0;
        _receive = receive;
        for (var i = 0; i < pollers.Length; i++)
        {
            var entry = pollers[(first + i) % pollers.Length];
            if (ReferenceEquals(entry.Binding, _required))
            {
                received |= entry.Reader.Read(receive);
                continue;
            }

            _receiving = false;
            try
            {
                received |= entry.Reader.Read(_guarded);
            }
            // A failure inside the receiver is the reassembler's, not this path's: it still ends
            // the capture, as it always did. So does the failure of a path that carried the game.
            catch (Exception error) when (!_receiving && !IsEssential(entry.Binding) &&
                error is not OutOfMemoryException and not StackOverflowException)
            {
                Close(entry);
            }
        }
        return received;
    }

    private bool IsEssential(CaptureInterfaceBinding binding) =>
        Volatile.Read(ref _essential) is { } essential && essential(binding);

    /// <summary>Hands a packet on, remembering whether a failure came from the receiver rather than the path.</summary>
    private void Receive(ReadOnlySpan<byte> packet, int linkType)
    {
        _receiving = true;
        _receive!(packet, linkType);
        _receiving = false;
    }

    /// <summary>
    /// Leaves out an optional path that failed while reading. Its loss so far is kept; its native
    /// resources stay owned for <see cref="Dispose"/> to retry when they cannot be released now.
    /// Runs on the one reader thread, the only one that changes the path lists after Open.
    /// </summary>
    private void Close(Entry entry)
    {
        long dropped;
        try { dropped = entry.Reader.DroppedPackets; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { dropped = 0; }
        Volatile.Write(ref _closed, _closed.Append((entry.Binding, dropped)).ToArray());
        _active.Remove(entry);
        Volatile.Write(ref _pollers, _active.ToArray());
        _failures++;
        UpdateStatus();
        try
        {
            entry.Reader.Dispose();
            _owned.Remove(entry);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Retained in _owned: the capture's own release retries it.
        }
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
        if (_openAttempted) UpdateStatus();
        if (failures is not null) throw new AggregateException("部分捕获路径资源尚未释放，保留供重试。", failures);
    }
}
