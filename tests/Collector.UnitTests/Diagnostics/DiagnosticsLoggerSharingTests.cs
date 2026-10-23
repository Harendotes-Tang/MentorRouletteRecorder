using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Two writers, one log file.
///
/// <c>--capture-trace</c>, a departing process and the serving instance can all hold the same
/// day's file open, so the append must permit shared writes: a line the loser cannot append is
/// dropped into a catch block, and a log that silently loses lines is worse than no log
/// because it is trusted (review finding M7). Loss that still occurs is counted, and the count
/// is reachable from <c>GetStatus</c>.
///
/// The rest of the file covers what a line may contain: sanitisation must cover the component
/// and event names and every value that only becomes text when rendered, not string
/// <em>fields</em> alone (review finding L2).
/// </summary>
public sealed class DiagnosticsLoggerSharingTests : IDisposable
{
    private readonly string _directory;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero));

    public DiagnosticsLoggerSharingTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.LoggerSharing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task TwoLoggersOnTheSameFileBothGetTheirLinesIn()
    {
        const int perLogger = 60;
        using var first = new RotatingFileLogger(_directory, _clock);
        using var second = new RotatingFileLogger(_directory, _clock);

        using var start = new Barrier(2);
        var writers = new[] { first, second }.Select(logger => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perLogger; i++)
            {
                logger.Write(LogLevel.Info, "ipc", "probe");
            }
        })).ToArray();

        await Task.WhenAll(writers);

        var lines = File.ReadAllLines(first.CurrentPath);
        Assert.Equal(0, first.SuppressedWrites + second.SuppressedWrites);
        Assert.Equal(2 * perLogger, lines.Length);
    }

    [Fact]
    public void AWriteThatCannotBeMadeIsCountedRatherThanForgotten()
    {
        using var logger = new RotatingFileLogger(_directory, _clock);
        logger.Write(LogLevel.Info, "ipc", "probe");

        // A directory in place of the file makes every append fail permanently, which is the
        // case the counter has to survive.
        var path = logger.CurrentPath;
        File.Delete(path);
        Directory.CreateDirectory(path);

        logger.Write(LogLevel.Warn, "ipc", "probe");

        Assert.True(logger.SuppressedWrites > 0);
    }

    [Fact]
    public void TheComponentAndEventNamesAreSanitisedLikeEveryOtherString()
    {
        using var logger = new RotatingFileLogger(_directory, _clock);

        logger.Write(LogLevel.Warn, "ipc 10.1.2.3", "peer S-1-5-21-1-2-3-1013");

        var text = File.ReadAllText(logger.CurrentPath);
        Assert.DoesNotContain("10.1.2.3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-1-2-3-1013", text, StringComparison.Ordinal);
        Assert.Contains("[ip]", text, StringComparison.Ordinal);
        Assert.Contains("[sid]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldThatIsNotAStringUntilItIsRenderedIsSanitisedToo()
    {
        // Testing `value is string` is not enough: anything that only becomes text when the
        // line is rendered -- a DirectoryInfo, an IPAddress, any object with a revealing
        // ToString -- must be sanitised as well.
        using var logger = new RotatingFileLogger(_directory, _clock);

        logger.Write(LogLevel.Warn, "capture", "adapter", new Dictionary<string, object?>
        {
            ["folder"] = new DirectoryInfo(@"C:\Users\someone\AppData"),
            ["peer"] = System.Net.IPAddress.Parse("fe80::1"),
        });

        var text = File.ReadAllText(logger.CurrentPath);
        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fe80::1", text, StringComparison.Ordinal);
        Assert.Contains("[ip6]", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp debris only.
        }
    }
}
