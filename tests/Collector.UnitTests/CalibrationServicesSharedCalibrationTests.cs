using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The shared-calibration seams of <see cref="CalibrationServices"/> follow the evidence seams: inert
/// until a caller opts in, so services built by a test never reach the network or the data directory
/// (plans/shared-calibration.md §3.3, review finding 12).
/// </summary>
public sealed class CalibrationServicesSharedCalibrationTests : IDisposable
{
    private const string Build = SharedCalibrationIndexTests.Build;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static CalibrationServices Bare() => new(
        _ => null,
        () => _ => throw new InvalidOperationException("not used here"),
        (_, _, _, _) => throw new InvalidOperationException("not used here"));

    [Fact]
    public async Task TheSharedCalibrationSeamsDoNothingUntilWiredIn()
    {
        var services = Bare();

        var fetch = services.FetchSharedCalibration(Region.Cn, Build, CancellationToken.None);

        Assert.True(fetch.IsCompletedSuccessfully);
        var result = await fetch;
        Assert.Equal(SharedFetchStatus.Disabled, result.Status);
        Assert.Empty(result.IndexAttempts);
        Assert.Same(SharedCalibrationStore.Inert, services.SharedCalibrations);
    }

    [Fact]
    public async Task WithSharedCalibrationUsesTheGivenFetchAndStore()
    {
        var asked = new List<(Region Region, string Build)>();
        var store = new SharedCalibrationStore(_root);

        var services = Bare().WithSharedCalibration(
            (region, build, _) =>
            {
                asked.Add((region, build));
                return Task.FromResult(SharedCalibrationFetchResult.Disabled with { Status = SharedFetchStatus.NoneForBuild });
            },
            store);

        Assert.Equal(SharedFetchStatus.NoneForBuild, (await services.FetchSharedCalibration(Region.Cn, Build, CancellationToken.None)).Status);
        Assert.Equal(new[] { (Region.Cn, Build) }, asked);
        Assert.Same(store, services.SharedCalibrations);
    }

    [Fact]
    public void WithSharedCalibrationInPointsARealStoreAtTheRootWithoutFetchingOrWriting()
    {
        var services = Bare().WithSharedCalibrationIn(_root);

        var store = Assert.IsType<SharedCalibrationStore>(services.SharedCalibrations);
        Assert.Equal(Path.GetFullPath(_root), store.Root);
        Assert.False(Directory.Exists(_root));
    }
}
