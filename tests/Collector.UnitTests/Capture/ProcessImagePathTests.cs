using System.ComponentModel;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ProcessImagePathTests
{
    private static readonly Dictionary<string, string?> Drives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C:"] = @"\Device\HarddiskVolume1",
        ["D:"] = @"\Device\HarddiskVolume10",
        ["E:"] = null,
    };

    private static string? Lookup(string drive) => Drives.GetValueOrDefault(drive);

    [Fact]
    public void MapsTheDevicePrefixBackToItsDriveLetter()
    {
        var path = ProcessImagePath.ToDosPath(@"\Device\HarddiskVolume1\Games\FF14\game\ffxiv_dx11.exe", Lookup);

        Assert.Equal(@"C:\Games\FF14\game\ffxiv_dx11.exe", path);
    }

    [Fact]
    public void ALongerVolumeNumberIsNotClaimedByItsPrefix()
    {
        var path = ProcessImagePath.ToDosPath(@"\Device\HarddiskVolume10\最终幻想XIV\game\ffxiv_dx11.exe", Lookup);

        Assert.Equal(@"D:\最终幻想XIV\game\ffxiv_dx11.exe", path);
    }

    [Fact]
    public void DeviceComparisonIgnoresCase()
    {
        var path = ProcessImagePath.ToDosPath(@"\device\harddiskvolume1\x\ffxiv_dx11.exe", Lookup);

        Assert.Equal(@"C:\x\ffxiv_dx11.exe", path);
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume7\x\ffxiv_dx11.exe")]
    [InlineData(@"\Device\HarddiskVolume1")]
    [InlineData("")]
    public void UnmappedOrBareDevicePathsStayUnknown(string ntPath)
    {
        Assert.Null(ProcessImagePath.ToDosPath(ntPath, Lookup));
    }

    [Fact]
    public void ADriveLetterPathIsPassedThrough()
    {
        const string path = @"D:\FF14\game\ffxiv_dx11.exe";

        Assert.Equal(path, ProcessImagePath.ToDosPath(path, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void AFailingDeviceLookupIsSkippedRatherThanThrown()
    {
        var path = ProcessImagePath.ToDosPath(
            @"\Device\HarddiskVolume1\x\ffxiv_dx11.exe",
            drive => drive == "A:" ? throw new Win32Exception(5) : Lookup(drive));

        Assert.Equal(@"C:\x\ffxiv_dx11.exe", path);
    }

    [Fact]
    public void TheCurrentProcessIsNamedWithoutAHandle()
    {
        var path = ProcessImagePath.TryRead(Environment.ProcessId);

        Assert.NotNull(path);
        Assert.Equal(
            Path.GetFullPath(Environment.ProcessPath!),
            Path.GetFullPath(path),
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1024, 4096, 4096)]      // the kernel asked for more: take its number
    [InlineData(1024, 1024, 2048)]      // no usable hint: double
    [InlineData(1024, 0, 2048)]
    [InlineData(16384, 20000, 20000)]
    [InlineData(32768, 0, 65536)]       // one doubling past the cap ends the loop
    [InlineData(32768, 65535, 65535)]
    public void BufferGrowthUsesTheKernelHintOrDoubles(int current, int hint, int expected)
    {
        Assert.Equal(expected, ProcessImagePath.NextNameBytes(current, (ushort)hint));
    }

    [Fact]
    public void TheBufferCapKeepsOneMoreDoublingInsideSixteenBits()
    {
        Assert.True(ProcessImagePath.MaxNameBytes <= ushort.MaxValue);
        Assert.True(ProcessImagePath.NextNameBytes(ProcessImagePath.MaxNameBytes, 0) > ProcessImagePath.MaxNameBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AnUnknownProcessIdIsSimplyUnknown(int processId)
    {
        Assert.Null(ProcessImagePath.TryRead(processId));
    }
}
