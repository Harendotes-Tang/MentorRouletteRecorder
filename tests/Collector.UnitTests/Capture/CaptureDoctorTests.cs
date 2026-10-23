using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What <c>--capture-doctor</c> prints under 协议档案, and with it the doctor report that
/// <c>--capture-trace</c> prints when it refuses. Neither command loads a protocol profile: they only
/// detect. The default of a controller given no profile provider was written for a repository that held
/// no profile at all, and on a machine whose installed software records normally it read as if nothing
/// were ever recorded there. The two commands say what is true for them instead, and point to the page
/// that shows the software's own profile status; the wording a service reports is left alone.
/// </summary>
public sealed class CaptureDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.CaptureDoctor", Guid.NewGuid().ToString("N"));

    public CaptureDoctorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // -------------------------------------------------------- the profile section ----

    [Fact]
    public void TheDoctorSaysItLoadsNoProfileAndWhereTheSoftwaresStatusIsShown()
    {
        var text = Doctor(Services());

        Assert.Contains("协议档案", text, StringComparison.Ordinal);
        Assert.Contains(CaptureCli.ProfileNotLoadedMessage, text, StringComparison.Ordinal);
        Assert.Contains("捕获诊断", text, StringComparison.Ordinal);
        Assert.DoesNotContain(NoProfileStatusProvider.Message.Chinese, text, StringComparison.Ordinal);
    }

    /// <summary>A provider the caller supplied is reported as it stands; only the no-profile default is replaced.</summary>
    [Fact]
    public void AProviderTheCallerSuppliedIsReportedAsItStands()
    {
        var provider = new FakeProfileStatusProvider(Domain.ProfileStatus.Verified);
        provider.Current = provider.Current with { Message = new UserText("test.profile_message", "由调用方提供的档案说明") };

        var text = Doctor(Services() with { Profile = provider });

        Assert.Contains("由调用方提供的档案说明", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CaptureCli.ProfileNotLoadedMessage, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- the temporary copies ----
    // The temp-directory check `--capture-doctor` promises.
    //
    // docs/privacy-boundary.md invites the user to verify with the doctor that the temporary copy
    // of the game executable is gone, so that claim must be backed by an actual check (review
    // finding H-2). The check is deliberately narrow: it reads the manifest of copies this
    // software created and never enumerates the temp directory, because a doctor that listed TEMP
    // would be inspecting files that are none of its business.

    [Fact]
    public void TheJsonReportCountsTheRegisteredCopiesStillOnDisk()
    {
        var manifest = WriteManifestWithOneCopy(out var bytes);
        var writer = new StringWriter();

        CaptureCli.Run(
            new[] { CaptureCli.Flag, CaptureCli.JsonFlag },
            new CaptureServices { EnableFollowTimer = false, OodleTempManifestPath = manifest },
            writer);

        var report = JsonNode.Parse(writer.ToString())!.AsObject();
        var temp = report["oodle_temp_copies"]!;
        Assert.Equal(1, temp["registered_present"]!.GetValue<int>());
        Assert.Equal(bytes, temp["registered_bytes"]!.GetValue<long>());
        Assert.Equal(0, temp["registered_missing"]!.GetValue<int>());

        // Nothing that could identify the machine: the count is a number, not a path.
        Assert.DoesNotContain(_root, writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHumanReportSaysHowMuchIsLeftAndThatItWillBeReclaimed()
    {
        var manifest = WriteManifestWithOneCopy(out var bytes);
        var writer = new StringWriter();

        CaptureCli.Run(
            new[] { CaptureCli.Flag },
            new CaptureServices { EnableFollowTimer = false, OodleTempManifestPath = manifest },
            writer);

        var text = writer.ToString();
        Assert.Contains("游戏程序临时副本", text, StringComparison.Ordinal);
        Assert.Contains(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains("自动删除", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A clean machine reports zeroes rather than failing or scanning anything.</summary>
    [Fact]
    public void NoManifestAtAllReportsACleanTempDirectory()
    {
        var writer = new StringWriter();

        CaptureCli.Run(
            new[] { CaptureCli.Flag, CaptureCli.JsonFlag },
            new CaptureServices
            {
                EnableFollowTimer = false,
                OodleTempManifestPath = Path.Combine(_root, "absent.json"),
            },
            writer);

        var temp = JsonNode.Parse(writer.ToString())!.AsObject()["oodle_temp_copies"]!;
        Assert.Equal(0, temp["registered_present"]!.GetValue<int>());
        Assert.Equal(0, temp["registered_bytes"]!.GetValue<long>());
    }

    private string WriteManifestWithOneCopy(out long bytes)
    {
        var machinaTemp = Path.Combine(_root, "temp", OodleTempCopyCleaner.MachinaTempFolderName);
        Directory.CreateDirectory(machinaTemp);
        var copy = Path.Combine(machinaTemp, Guid.NewGuid().ToString("N") + ".exe");
        bytes = 4096;
        File.WriteAllBytes(copy, new byte[bytes]);

        var manifest = Path.Combine(_root, "oodle-temp.json");
        File.WriteAllText(
            manifest,
            new JsonObject { ["version"] = 1, ["paths"] = new JsonArray(copy) }.ToJsonString());
        return manifest;
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
