using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;

namespace MentorRecorder.Collector.Capture;

/// <summary>每个主机的一份采集租约；等待、启动和停止期间也不得重叠使用 Oodle。</summary>
public sealed class CaptureOwnership
{
    private int _owned, _releasing;

    /// <summary>Whether a lease is held right now. A hint for polls, never a reservation.</summary>
    public bool IsHeld => Volatile.Read(ref _owned) != 0;

    /// <summary>
    /// Whether the lease is held only until a source that could not be released when its session
    /// ended is released: there is nothing left to stop, only to wait for (audit 2026-10-20, A2-2).
    /// </summary>
    public bool IsReleasing => Volatile.Read(ref _releasing) != 0;

    public IDisposable Acquire()
    {
        if (Interlocked.CompareExchange(ref _owned, 1, 0) != 0)
            throw CollectorException.BadRequest(CollectorTexts.CaptureOwnershipBusy());
        Volatile.Write(ref _releasing, 0);
        return new Lease(this);
    }

    /// <summary>Marks a lease this ownership handed out as kept only for a release still being retried.</summary>
    /// <param name="lease">A lease from <see cref="Acquire"/>; one already given back is ignored.</param>
    internal static void MarkReleasing(IDisposable lease)
    {
        if (lease is Lease held) held.MarkReleasing();
    }

    private sealed class Lease(CaptureOwnership owner) : IDisposable
    {
        private CaptureOwnership? _owner = owner;

        public void MarkReleasing()
        {
            if (Volatile.Read(ref _owner) is { } held) Volatile.Write(ref held._releasing, 1);
        }

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _owner, null);
            if (held is null) return;
            Volatile.Write(ref held._releasing, 0);
            Volatile.Write(ref held._owned, 0);
        }
    }
}
