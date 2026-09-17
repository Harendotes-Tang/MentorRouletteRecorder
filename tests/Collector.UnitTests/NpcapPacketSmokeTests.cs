using System.Net;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Opt-in real Npcap lifecycle check; never sends traffic or retains/logs received bytes.</summary>
public sealed class NpcapPacketSmokeTests
{
    [NpcapSmokeFact]
    public void ExactSelectedAdapterOpensReadsAndClosesWithoutGameOrOodle()
    {
        var ip = IPAddress.Parse(Environment.GetEnvironmentVariable("MR_NPCAP_SMOKE_IP")!);
        var id = Environment.GetEnvironmentVariable("MR_NPCAP_SMOKE_ADAPTER");
        var options = new CaptureStartOptions("native-lifecycle-smoke", Environment.ProcessId, ip, id,
            OodleMode.LibraryTcp, null, null);
        using var reader = new NpcapPacketReader(options);
        reader.Open();
        var until = Environment.TickCount64 + 250;
        while (Environment.TickCount64 < until)
        {
            reader.Read((_, _) => { });
            Thread.Sleep(5);
        }
        reader.Dispose();
    }

    private sealed class NpcapSmokeFactAttribute : FactAttribute
    {
        public NpcapSmokeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MR_NPCAP_SMOKE_IP")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MR_NPCAP_SMOKE_ADAPTER")))
                Skip = "Requires explicit MR_NPCAP_SMOKE_IP and MR_NPCAP_SMOKE_ADAPTER; no adapter fallback.";
        }
    }
}
