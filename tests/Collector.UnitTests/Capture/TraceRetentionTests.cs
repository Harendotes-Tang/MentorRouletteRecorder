using System.Globalization;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Retention of the forensic traces a validation session saves: the newest ten are kept, none
/// older than seven days (docs/privacy-boundary.md section 5, docs/capture-diagnostics.md
/// section 9.2), and only folders this service named are ever touched.
/// </summary>
public sealed class TraceRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void KeepsTheNewestTenSessionsAndLeavesForeignFoldersAlone()
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(new TestClock(Now)));
        var sessions = Enumerable.Range(0, 12).Select(hours => Session(controller, Now.AddHours(-hours))).ToArray();
        var foreign = Directory.CreateDirectory(Path.Combine(controller.TraceDirectory, "notes")).FullName;

        controller.SweepTraces();

        Assert.All(sessions.Take(10), path => Assert.True(Directory.Exists(path), path));
        Assert.All(sessions.Skip(10), path => Assert.False(Directory.Exists(path), path));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void RemovesSessionsOlderThanSevenDaysEvenWhenFewerThanTenAreKept()
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(new TestClock(Now)));
        var recent = Session(controller, Now.AddDays(-6));
        var aged = Session(controller, Now.AddDays(-8));

        controller.SweepTraces();

        Assert.True(Directory.Exists(recent));
        Assert.False(Directory.Exists(aged));
    }

    /// <summary>
    /// Audit 2026-11-03, A2-2. The folder name of a saved session is written in the invariant
    /// calendar whatever the user's culture. Named in the Umm al-Qura or Persian calendar it read
    /// as the year 1448 or 1405, and the sweep that follows every session deleted the evidence it
    /// had just saved; named in the Thai Buddhist calendar it never aged at all.
    /// </summary>
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    public void ASavedSessionIsNamedAndAgedInOneCalendarWhateverTheUsersCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var db = new TestDatabase();
            var clock = new TestClock(Now);
            using var controller = new CaptureValidationController(db.Path, new(), Services(clock));

            controller.Start("wifi");
            Until(controller, "state", "RECORDING");
            controller.Stop();
            Until(controller, "state", "COMPLETED");

            var trace = controller.Snapshot()["trace_path"]!.GetValue<string>();
            Assert.True(File.Exists(trace), trace);
            Assert.StartsWith("20261103T040506", Path.GetFileName(Path.GetDirectoryName(trace)), StringComparison.Ordinal);

            clock.UtcNow = Now.AddDays(8);
            controller.SweepTraces();
            Assert.False(File.Exists(trace), trace);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static string Session(CaptureValidationController controller, DateTimeOffset written)
    {
        var name = written.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("D");
        return Directory.CreateDirectory(Path.Combine(controller.TraceDirectory, name)).FullName;
    }

    private static CaptureValidationServices Services(TestClock clock) => new()
    {
        LocateGame = () => new GameProcessDetection(true, 42, "ffxiv_dx11", DateTimeOffset.UnixEpoch, Region.Cn,
            "2026.01", 1, @"D:\SdoA\game\ffxiv_dx11.exe", Array.Empty<UserText>()),
        PollInterval = TimeSpan.FromMilliseconds(10),
        Trace = new CaptureTraceServices
        {
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"),
                new FakeProcessTcpTable()),
            SourceFactory = () => new FakeCaptureSource(),
            TcpConnectionCounter = (_, _) => 0,
            Clock = clock,
        },
    };

    private static void Until(CaptureValidationController controller, string field, string value) =>
        Assert.True(SpinWait.SpinUntil(() => controller.Snapshot()[field]!.GetValue<string>() == value, 5000),
            controller.Snapshot().ToJsonString());
}
