using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Capture;

/// <summary>每个主机的一份采集租约；等待、启动和停止期间也不得重叠使用 Oodle。</summary>
public sealed class CaptureOwnership
{
    private int _owned;

    /// <summary>Whether a lease is held right now. A hint for polls, never a reservation.</summary>
    public bool IsHeld => Volatile.Read(ref _owned) != 0;

    public IDisposable Acquire()
    {
        if (Interlocked.CompareExchange(ref _owned, 1, 0) != 0)
            throw CollectorException.BadRequest("正式采集或验证会话尚未结束，请先停止并等待资源释放。");
        return new Lease(this);
    }

    private sealed class Lease(CaptureOwnership owner) : IDisposable
    {
        private CaptureOwnership? _owner = owner;
        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _owner, null);
            if (held is not null) Volatile.Write(ref held._owned, 0);
        }
    }
}
