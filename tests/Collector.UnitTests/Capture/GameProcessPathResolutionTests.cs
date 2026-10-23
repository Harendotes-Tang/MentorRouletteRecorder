using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class GameProcessPathResolutionTests
{
    /// <summary>
    /// Audit 2026-10-20, A2-4. The detection warnings every player sees when the install path or
    /// the region cannot be told used to end in the English design term "(fail-closed)". The
    /// sentence already says what happens: nothing is recorded automatically.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(@"D:\最终幻想\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe", false)]
    [InlineData(@"D:\Games\game\ffxiv_dx11.exe", false)]
    public void TheGameDetectionWarningsAreFreeOfDesignJargon(string? path, bool accessDenied)
    {
        var processes = new FakeGameProcessProvider().Add(
            GameProcessLocator.Dx11ProcessName, 55, DateTimeOffset.UnixEpoch, path, accessDenied);

        var detection = new GameProcessLocator(processes, new FakeGameFileReader()).Locate();

        Assert.Contains(detection.Warnings, warning => warning.Chinese.Contains("不会进行任何自动记录", StringComparison.Ordinal));
        Assert.All(detection.Warnings, warning =>
            Assert.DoesNotContain("fail-closed", warning.Chinese, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheProcessTableAnswerIsTakenAsIs()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"D:\最终幻想XIV\game\ffxiv_dx11.exe");

        Assert.Equal(@"D:\最终幻想XIV\game\ffxiv_dx11.exe", result);
    }

    /// <summary>
    /// The kernel process table is the only source there is. The module listing that used to
    /// answer here opens a handle to the game with <c>PROCESS_VM_READ</c> and reads its module
    /// table, which docs/privacy-boundary.md section 2, rule 3b forbids and the project promises
    /// never to do; it was deleted by the 2026-09-21 audit (finding 1). An answer the table
    /// cannot give must therefore leave the path unknown, whatever the reason -- an empty
    /// answer, an empty string, or a lookup that throws.
    /// </summary>
    [Fact]
    public void AnEmptyTableAnswerLeavesThePathUnknownRatherThanOpeningTheProcess()
    {
        foreach (var table in new Func<string?>[]
        {
            () => null,
            () => "",
            () => throw new DllNotFoundException(),
            () => throw new InvalidOperationException(),
        })
        {
            Assert.Null(WindowsGameProcessProvider.ResolveExecutablePath(table, "ffxiv_dx11"));
        }
    }

    /// <summary>
    /// The table lookup takes a process id, and Windows reuses process ids: between the snapshot
    /// that named the id as ffxiv_dx11 and the read, the id can belong to another process. A path
    /// whose file name does not match must be discarded, otherwise the capture layer reads a
    /// stranger's executable from disk (review finding L-4).
    /// </summary>
    [Fact]
    public void ATablePathThatDoesNotMatchTheProcessNameLeavesThePathUnknownRatherThanWrong()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"C:\Windows\System32\notepad.exe", "ffxiv_dx11");

        Assert.Null(result);
    }

    [Fact]
    public void AMatchingNameIsAcceptedWhateverItsCasingAndFolder()
    {
        var result = WindowsGameProcessProvider.ResolveExecutablePath(
            () => @"D:\最终幻想XIV\game\FFXIV_DX11.EXE", "ffxiv_dx11");

        Assert.Equal(@"D:\最终幻想XIV\game\FFXIV_DX11.EXE", result);
    }

    [Fact]
    public void ACallerThatNamesNoProcessStillGetsTheTableAnswer()
    {
        Assert.True(WindowsGameProcessProvider.NameMatches(@"C:\anything.exe", null));
        Assert.True(WindowsGameProcessProvider.NameMatches(@"C:\anything.exe", "  "));
        Assert.Equal(
            @"C:\anything.exe",
            WindowsGameProcessProvider.ResolveExecutablePath(() => @"C:\anything.exe"));
    }
}
