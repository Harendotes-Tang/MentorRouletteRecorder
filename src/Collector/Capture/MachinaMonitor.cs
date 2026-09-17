using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MentorRecorder.Collector.UnitTests")]

namespace MentorRecorder.Collector.Capture;

/// <summary>原生监视器生命周期边界；测试仅替换此适配器，不替换 MachinaCaptureSource 的清理逻辑。</summary>
internal interface IMachinaMonitor : IDisposable
{
    /// <summary>Open and drain raw capture before Oodle initialization; legacy test doubles need no native preparation.</summary>
    void Prepare() { }
    void Start();

    /// <summary>
    /// What ingress saw below the message level. Default empty so a message-replaying test
    /// double need not invent counters it never measured.
    /// </summary>
    CaptureIngressCounters Ingress => CaptureIngressCounters.Empty;
    void Stop();
    void DetachCallbacks();
}
