using MentorRecorder.Collector.Export;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="ExportPaths"/>: an export goes only to a local destination, and the check that keeps
/// it there survives links.
/// </summary>
public sealed class ExportPathsTests
{
    [SymbolicLinkFact]
    public void ExportPaths_ResolveFinalPathFollowsALocalDirectoryLink()
    {
        using var root = new TempDirectory("LinkTests");
        using var target = new TempDirectory("LinkTarget");

        var link = root.Combine("link");
        try
        {
            Directory.CreateSymbolicLink(link, target.Path);
            Directory.CreateDirectory(target.Combine("nested"));
            File.WriteAllText(target.Combine("nested", "existing.csv"), "original");
            var through = Path.Combine(link, "nested", "runs.csv");

            var resolved = ExportPaths.ResolveFinalPath(through);

            Assert.NotEqual(Path.GetFullPath(through), resolved);
            Assert.StartsWith(
                Path.GetFullPath(target.Path), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(through), ExportPaths.Resolve(through));
            Assert.Equal(target.Combine("nested", "existing.csv"),
                ExportPaths.ResolveFinalPath(Path.Combine(link, "nested", "existing.csv")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [SymbolicLinkFact]
    public void ExportPaths_ValidatesFileLinksAndNetworkDirectoryLinks()
    {
        using var root = new TempDirectory("LinkTests");
        var target = root.Combine("target.csv");
        File.WriteAllText(target, "keep");
        var localFile = root.Combine("local.csv");
        File.CreateSymbolicLink(localFile, target);
        Assert.Equal(localFile, ExportPaths.Resolve(localFile));
        Assert.Equal(target, ExportPaths.ResolveFinalPath(localFile));

        var remoteFile = root.Combine("remote.csv");
        File.CreateSymbolicLink(remoteFile, @"\\export-server\share\runs.csv");
        Assert.Equal(ErrorCodes.ExportFailed,
            Assert.Throws<CollectorException>(() => ExportPaths.Resolve(remoteFile)).Code);

        var remoteDirectory = root.Combine("remote-directory");
        Directory.CreateSymbolicLink(remoteDirectory, @"\\export-server\share");
        Assert.Equal(ErrorCodes.ExportFailed,
            Assert.Throws<CollectorException>(() => ExportPaths.Resolve(Path.Combine(remoteDirectory, "runs.csv"))).Code);

        var localChain = root.Combine("chain");
        Directory.CreateSymbolicLink(localChain, remoteDirectory);
        Assert.Equal(ErrorCodes.ExportFailed,
            Assert.Throws<CollectorException>(() => ExportPaths.Resolve(Path.Combine(localChain, "runs.csv"))).Code);

        var cycle = root.Combine("cycle");
        Directory.CreateSymbolicLink(cycle, cycle);
        Assert.Equal(ErrorCodes.ExportFailed,
            Assert.Throws<CollectorException>(() => ExportPaths.Resolve(Path.Combine(cycle, "runs.csv"))).Code);

        // 重复子链的总工作量也受 64 跳限制，不能只限制递归深度。
        Directory.CreateSymbolicLink(root.Combine("repeat0"), root.Path);
        for (var index = 1; index <= 8; index++)
        {
            var previous = "repeat" + (index - 1);
            Directory.CreateSymbolicLink(root.Combine("repeat" + index),
                root.Combine(previous, previous));
        }
        Assert.Equal(ErrorCodes.ExportFailed,
            Assert.Throws<CollectorException>(() => ExportPaths.Resolve(root.Combine("repeat8", "runs.csv"))).Code);
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void ExportPaths_ResolveFinalPathIsIdentityForAnOrdinaryPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "MentorRecorder.Plain", "runs.csv");

        Assert.Equal(Path.GetFullPath(path), ExportPaths.ResolveFinalPath(path));
    }

    [Fact]
    public void ExportPaths_AcceptsPersonalFolderSiblingsAndOtherLocalDirectories()
    {
        var profile = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.False(string.IsNullOrEmpty(profile), "Windows always has a user profile");

        var sibling = profile + "2" + Path.DirectorySeparatorChar + "x.csv";
        Assert.Equal(sibling, ExportPaths.Resolve(sibling));
        Assert.Equal(@"D:\导出记录\x.csv", ExportPaths.Resolve(@"D:\导出记录\x.csv"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\0path")]
    [InlineData(@"\\export-server\share\runs.csv")]
    [InlineData(@"\\?\C:\runs.csv")]
    [InlineData(@"\\.\pipe\export")]
    [InlineData(@"C:\runs.csv:stream")]
    public void ExportPaths_RejectsInvalidOrNonLocalDestinations(string path)
    {
        var error = Assert.Throws<CollectorException>(() => ExportPaths.Resolve(path));
        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
    }
}
