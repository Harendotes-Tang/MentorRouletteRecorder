using MentorRecorder.Collector;
using Xunit;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Phase 0 smoke tests: the build produces a runnable Collector whose
/// version banner is stable and whose IPC protocol version is 1.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class VersionTests
{
    [Fact]
    public void VersionString_HasExpectedFormat()
    {
        Assert.Equal("MentorRecorder.Collector " + Program.Version + " (ipc v1)", Program.VersionString);
        Assert.Matches(@"^\d+\.\d+\.\d+$", Program.Version);
    }

    [Fact]
    public void IpcProtocolVersion_IsOne()
    {
        Assert.Equal(1, Program.IpcProtocolVersion);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("/version")]
    public void Main_WithVersionFlag_ExitsZero(string flag)
    {
        Assert.Equal(0, Program.Main(new[] { flag }));
    }

    [Fact]
    public void Main_WithUnknownArgument_ExitsTwo()
    {
        Assert.Equal(2, Program.Main(new[] { "--not-a-flag" }));
    }

    [Fact]
    public void CaptureDoctorMode_IsParsedWithJsonOutput()
    {
        var options = CommandLineOptions.Parse(new[] { "--capture-doctor", "--json" });

        Assert.Equal(CollectorMode.CaptureDoctor, options.Mode);
        Assert.True(options.Json);
    }

    [Fact]
    public void CaptureDoctorMode_CannotBeCombinedWithAnotherMode()
    {
        Assert.Throws<FormatException>(() =>
            CommandLineOptions.Parse(new[] { "--capture-doctor", "--version" }));
    }

    [Fact]
    public void CaptureDoctorMode_RejectsIrrelevantFlags()
    {
        Assert.Throws<FormatException>(() =>
            CommandLineOptions.Parse(new[] { "--capture-doctor", "--db", "capture.db" }));
    }

    [Fact]
    public void CaptureTraceMode_IsParsedWithItsOwnFlags()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--capture-trace", "trace.jsonl",
            "--duration-seconds", "30",
            "--adapter", @"\Device\NPF_{0}",
            "--max-lines", "500",
        });

        Assert.Equal(CollectorMode.CaptureTrace, options.Mode);
        Assert.Equal("trace.jsonl", options.TracePath);
        Assert.Equal(30, options.DurationSeconds);
        Assert.Equal(@"\Device\NPF_{0}", options.AdapterId);
        Assert.Equal(500, options.MaxLines);
    }

    [Fact]
    public void TraceReportMode_IsParsedWithItsOwnFlags()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--trace-report", "trace.jsonl", "--around", "pop", "--window-ms", "2000",
        });

        Assert.Equal(CollectorMode.TraceReport, options.Mode);
        Assert.Equal("trace.jsonl", options.TracePath);
        Assert.Equal("pop", options.AroundMarker);
        Assert.Equal(2000, options.WindowMs);
    }

    /// <summary>
    /// A trace flag on the wrong mode is refused rather than ignored: the mistake would
    /// otherwise be discovered only after the game session it was meant to record was over.
    /// </summary>
    /// <param name="commandLine">Space-separated command line that must not parse.</param>
    [Theory]
    [InlineData("--capture-trace")]
    [InlineData("--trace-report")]
    [InlineData("--capture-trace t.jsonl --json")]
    [InlineData("--capture-trace t.jsonl --around pop")]
    [InlineData("--capture-trace t.jsonl --window-ms 1000")]
    [InlineData("--capture-trace t.jsonl --db capture.db")]
    [InlineData("--capture-trace t.jsonl --trace-report t.jsonl")]
    [InlineData("--trace-report t.jsonl --duration-seconds 5")]
    [InlineData("--trace-report t.jsonl --adapter eth0")]
    [InlineData("--trace-report t.jsonl --max-lines 5")]
    [InlineData("--serve --duration-seconds 5")]
    [InlineData("--capture-doctor --window-ms 5")]
    [InlineData("--capture-trace t.jsonl --duration-seconds -1")]
    [InlineData("--capture-trace t.jsonl --duration-seconds 999999")]
    [InlineData("--capture-trace t.jsonl --max-lines 0")]
    [InlineData("--trace-report t.jsonl --window-ms 0")]
    public void TraceFlags_AreRefusedOutsideTheirMode(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(args));
    }

    [Fact]
    public void Main_WithAnInvalidTraceCombination_ExitsTwo()
    {
        Assert.Equal(2, Program.Main(new[] { "--capture-trace", "t.jsonl", "--json" }));
    }
}
