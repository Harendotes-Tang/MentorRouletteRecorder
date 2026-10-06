using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Tests;

/// <summary>
/// No test run resolves the real %LOCALAPPDATA%\MentorRecorder as its data root, a bare
/// <c>dotnet test</c> that scripts/test.ps1 did not start included. Compiled into both C# test
/// projects, so each assembly proves it for itself.
/// </summary>
public sealed class TestDataDirectoryTests
{
    [Fact]
    public void TheDataRootOfATestRunIsNeverTheUsersOwn()
    {
        var managed = DatabasePaths.ResolveRoot(null);
        var root = DatabasePaths.RootDirectory;

        Assert.False(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable)),
            DatabasePaths.DataDirectoryVariable + " 未设置，测试会读写真实用户的数据目录。");
        Assert.NotEqual(managed, root, StringComparer.OrdinalIgnoreCase);
        Assert.False(
            root.StartsWith(managed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            root + " 位于真实用户的数据目录之内。");
    }
}
