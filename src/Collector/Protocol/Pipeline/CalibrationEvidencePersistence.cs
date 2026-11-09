using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>已冻结证据及所属会话代次；Sequence 表示冻结顺序，不是流量中的协议序号。</summary>
internal sealed record CalibrationEvidenceWrite(
    Region Region, string Build, string TemplateSha256, CalibrationSnapshot Evidence, int Generation, long Sequence);

/// <summary>
/// 串行保存/删除校准证据，持有冻结序号、周期写入槽和后台任务。只依赖不可变快照与文件操作，
/// 不解释校准状态；写入完成后的代次裁决交还管线。借用会话锁，使周期槽释放与裁决保持原子性。
/// </summary>
internal sealed class CalibrationEvidencePersistence
{
    private readonly object _sessionGate;
    private readonly object _writeGate = new();
    private readonly Func<Region, string, string, CalibrationSnapshot, bool> _save;
    private readonly Action<Region, string> _delete;
    private long _frozen;
    private long _written;
    private bool _inFlight;
    private CalibrationEvidenceWrite? _pending;
    private Task _flush = Task.CompletedTask;
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 借用管线的会话锁，不取得其生命周期所有权。允许会话锁后取得文件锁；后台写入必须先释放文件锁，
    /// 再取得会话锁调用完成回调，禁止反向嵌套两个锁。文件操作中的普通异常按原契约忽略。
    /// </summary>
    internal CalibrationEvidencePersistence(object sessionGate,
        Func<Region, string, string, CalibrationSnapshot, bool> save, Action<Region, string> delete)
    {
        _sessionGate = sessionGate;
        _save = save;
        _delete = delete;
    }

    /// <summary>后台执行入口；测试可以延迟执行，生产使用线程池，不能在交付线程同步执行磁盘操作。</summary>
    internal Func<Action, Task> RunWrite { get; set; } = static write => Task.Run(write);

    /// <summary>周期写入从暂存到完成裁决之前占用唯一槽位；调用者可据此避免昂贵的重复快照。</summary>
    internal bool IsPeriodicSaveInFlight { get { lock (_sessionGate) return _inFlight; } }

    /// <summary>冻结并立即保存最终或显式快照。调用者应持有会话锁；晚启动的旧周期写入不能覆盖它。</summary>
    internal bool Save(Region region, string build, string templateSha256, CalibrationSnapshot evidence)
    {
        lock (_sessionGate)
        {
            return Write(new CalibrationEvidenceWrite(region, build, templateSha256, evidence, 0, ++_frozen));
        }
    }

    /// <summary>删除指定证据，并在删除成功后废弃此前所有冻结写入；删除失败不废弃待写快照。</summary>
    internal void Delete(Region region, string build)
    {
        lock (_sessionGate)
        {
            try
            {
                lock (_writeGate)
                {
                    _delete(region, build);
                    _written = Math.Max(_written, _frozen);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
            }
        }
    }

    /// <summary>
    /// 在消息交付线程暂存一个周期快照；已有写入未裁决时返回 false，不改变快照序号或节流时间。
    /// 调用者负责在同次交付的 finally 中调用 StartPending，即使后续消息处理失败也必须启动已暂存快照。
    /// </summary>
    internal bool TryStage(Region region, string build, string templateSha256, CalibrationSnapshot evidence, int generation)
    {
        lock (_sessionGate)
        {
            if (_inFlight) return false;
            _inFlight = true;
            _pending = new CalibrationEvidenceWrite(region, build, templateSha256, evidence, generation, ++_frozen);
            return true;
        }
    }

    /// <summary>
    /// 由暂存快照的同一交付线程调用，将磁盘操作移到后台。完成回调在借用的会话锁内运行，
    /// 周期槽已释放，文件锁已释放；管线可以安全判断代次并删除/重存新证据。
    /// </summary>
    internal void StartPending(Action<CalibrationEvidenceWrite, bool> completed)
    {
        if (_pending is not { } pending) return;
        _pending = null;
        Volatile.Write(ref _flush, RunWrite(() =>
        {
            var written = Write(pending);
            lock (_sessionGate)
            {
                _inFlight = false;
                completed(pending, written);
            }
        }));
    }

    /// <summary>
    /// 在取得会话锁前等待周期保存，最多五秒；不能持锁等待，否则完成回调无法取得锁。
    /// 等待超时或任务失败不阻止停止流程；最终保存的更大序号仍能拒绝晚到旧写入。
    /// </summary>
    internal void WaitForPendingWrite()
    {
        try
        {
            Volatile.Read(ref _flush).Wait(ShutdownWait);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    /// <summary>只在序号比最近成功写入或删除更新时落盘；失败保留原序号，使后续尝试仍有效。</summary>
    private bool Write(CalibrationEvidenceWrite pending)
    {
        try
        {
            lock (_writeGate)
            {
                if (pending.Sequence <= _written) return false;
                var written = _save(pending.Region, pending.Build, pending.TemplateSha256, pending.Evidence);
                if (written) _written = pending.Sequence;
                return written;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }
}
