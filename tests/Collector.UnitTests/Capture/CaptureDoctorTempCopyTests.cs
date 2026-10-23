using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The temp-directory check <c>--capture-doctor</c> promises.
///
/// docs/privacy-boundary.md invites the user to verify with the doctor that the temporary copy
/// of the game executable is gone, so that claim must be backed by an actual check (review
/// finding H-2). The check is deliberately narrow: it reads the manifest of copies this
/// software created and never enumerates the temp directory, because a doctor that listed TEMP
/// would be inspecting files that are none of its business.
/// </summary>
public sealed class CaptureDoctorTempCopyTests : IDisposable
{
    private readonly string _root;

    public CaptureDoctorTempCopyTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.DoctorTemp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
