using System.ComponentModel;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class GameProcessPathTests
{
    [Theory]
    [InlineData(5, true)]
    [InlineData(299, false)]
    [InlineData(6, false)]
    [InlineData(87, false)]
    public void OnlyAccessDeniedIsReportedAsAPermissionFailure(int errorCode, bool accessDenied)
    {
        var result = WindowsGameProcessProvider.ReadExecutablePath(() => throw new Win32Exception(errorCode));

        Assert.Null(result.Path);
        Assert.Equal(accessDenied, result.AccessDenied);
    }

    [Fact]
    public void ReadablePathsAreReturnedWithoutAPermissionWarning()
    {
        const string path = @"D:\FF14\game\ffxiv_dx11.exe";
        var result = WindowsGameProcessProvider.ReadExecutablePath(() => path);

        Assert.Equal(path, result.Path);
        Assert.False(result.AccessDenied);
    }

    [Fact]
    public void AProcessThatExitsOrHasNoMainModuleIsNotAnAccessDeniedFailure()
    {
        foreach (var read in new Func<string?>[]
        {
            () => null,
            () => throw new InvalidOperationException(),
            () => throw new NotSupportedException(),
        })
        {
            var result = WindowsGameProcessProvider.ReadExecutablePath(read);
            Assert.Null(result.Path);
            Assert.False(result.AccessDenied);
        }
    }
}

public sealed class GameProcessPathResolutionTests
{
    [Fact]
    public void TheProcessTableAnswerWinsAndNeedsNoModuleListing()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"D:\最终幻想XIV\game\ffxiv_dx11.exe",
            () => throw new Win32Exception(5));

        Assert.Equal(@"D:\最终幻想XIV\game\ffxiv_dx11.exe", result.Path);
        Assert.False(result.AccessDenied);
    }

    [Fact]
    public void AnEmptyTableAnswerFallsBackToTheModuleListing()
    {
        foreach (var table in new Func<string?>[] { () => null, () => "", () => throw new DllNotFoundException() })
        {
            var result = WindowsGameProcessProvider.ResolveExecutablePath(table, () => @"C:\FF14\game\ffxiv_dx11.exe");
            Assert.Equal(@"C:\FF14\game\ffxiv_dx11.exe", result.Path);
            Assert.False(result.AccessDenied);
        }
    }

    [Fact]
    public void AccessDeniedIsStillReportedWhenBothSourcesFail()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(() => null, () => throw new Win32Exception(5));

        Assert.Null(result.Path);
        Assert.True(result.AccessDenied);
    }

    /// <summary>
    /// The table lookup takes a process id, and Windows reuses process ids: between the snapshot
    /// that named the id as ffxiv_dx11 and the read, the id can belong to another process. A path
    /// whose file name does not match must be discarded, otherwise the capture layer reads a
    /// stranger's executable from disk (review finding L-4).
    /// </summary>
    [Fact]
    public void ATablePathThatDoesNotMatchTheProcessNameIsDiscarded()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"C:\Windows\System32\notepad.exe",
            () => @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe",
            "ffxiv_dx11");

        Assert.Equal(@"D:\SdoA\FFXIV\game\ffxiv_dx11.exe", result.Path);
        Assert.False(result.AccessDenied);
    }

    [Fact]
    public void AMismatchWithNoModuleListingLeavesThePathUnknownRatherThanWrong()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"C:\Windows\System32\notepad.exe", () => null, "ffxiv_dx11");

        Assert.Null(result.Path);
        Assert.False(result.AccessDenied);
    }

    [Fact]
    public void AMatchingNameIsAcceptedWhateverItsCasingAndFolder()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"D:\最终幻想XIV\game\FFXIV_DX11.EXE",
            () => throw new Win32Exception(5),
            "ffxiv_dx11");

        Assert.Equal(@"D:\最终幻想XIV\game\FFXIV_DX11.EXE", result.Path);
    }

    [Fact]
    public void ACallerThatNamesNoProcessStillGetsTheTableAnswer()
    {
        Assert.True(WindowsGameProcessProvider.NameMatches(@"C:\anything.exe", null));
        Assert.True(WindowsGameProcessProvider.NameMatches(@"C:\anything.exe", "  "));
    }
}
