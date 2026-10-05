using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What <c>--capture-doctor</c> prints under 协议档案, and with it the doctor report that
/// <c>--capture-trace</c> prints when it refuses. Neither command loads a protocol profile: they only
/// detect. The default of a controller given no profile provider was written for a repository that held
/// no profile at all, and on a machine whose installed software records normally it read as if nothing
/// were ever recorded there. The two commands say what is true for them instead, and point to the page
/// that shows the software's own profile status; the wording a service reports is left alone.
/// </summary>
public sealed class CaptureDoctorProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.DoctorProfile", Guid.NewGuid().ToString("N"));

    public CaptureDoctorProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheDoctorSaysItLoadsNoProfileAndWhereTheSoftwaresStatusIsShown()
    {
        var text = Doctor(Services());

        Assert.Contains("协议档案", text, StringComparison.Ordinal);
        Assert.Contains(CaptureCli.ProfileNotLoadedMessage, text, StringComparison.Ordinal);
        Assert.Contains("捕获诊断", text, StringComparison.Ordinal);
        Assert.DoesNotContain(NoProfileStatusProvider.Message, text, StringComparison.Ordinal);
    }

    /// <summary>A provider the caller supplied is reported as it stands; only the no-profile default is replaced.</summary>
    [Fact]
    public void AProviderTheCallerSuppliedIsReportedAsItStands()
    {
        var provider = new FakeProfileStatusProvider(Domain.ProfileStatus.Verified);
        provider.Current = provider.Current with { Message = "由调用方提供的档案说明" };

        var text = Doctor(Services() with { Profile = provider });

        Assert.Contains("由调用方提供的档案说明", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CaptureCli.ProfileNotLoadedMessage, text, StringComparison.Ordinal);
    }

    private static string Doctor(CaptureServices services)
    {
        var writer = new StringWriter();
        CaptureCli.Run(new[] { CaptureCli.Flag }, services, writer);
        return writer.ToString();
    }

    private CaptureServices Services() => new()
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader()),
        Adapters = new AdapterEnumerator(new FakeAdapterProvider(), new FakeProcessTcpTable()),
        EnableFollowTimer = false,
        OodleTempManifestPath = Path.Combine(_root, "absent.json"),
    };
}
