using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-03 CS1-X1. A failed process listing is not "the game exited" anywhere any more,
/// which also made it invisible: a process-table read that kept failing for an hour left no trace
/// in the log. The locator now reports every listing to an observer, and the shipping observer
/// writes a rate-limited line.
/// </summary>
public sealed class ProcessListingLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("MentorRecorder.ListingLog.").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test debris in the OS temp folder is not worth failing a test over.
        }
    }

    [Fact]
    public void TheLocatorReportsEveryListingAndWhetherItWasRead()
    {
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7);
        var seen = new List<bool>();
        var locator = new GameProcessLocator(processes, new FakeGameFileReader())
            .WithListingObserver(seen.Add)
            .WithRegionOverride(() => null);

        locator.Locate();
        processes.Fails = true;
        locator.Locate();
        Assert.True(locator.IsRunning(7, null));

        Assert.Equal(new[] { true, false, false }, seen);
    }

    [Fact]
    public void AListingThatKeepsFailingIsLoggedOnceAMinuteAndItsRecoveryOnce()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero));
        var now = TimeSpan.Zero;
        using var logger = new RotatingFileLogger(_directory, clock);
        var log = new ProcessListingLog(logger, "capture", () => now);

        log.Observe(succeeded: true);
        log.Observe(succeeded: false);
        now += TimeSpan.FromSeconds(1);
        log.Observe(succeeded: false);
        now += ProcessListingLog.RepeatInterval;
        log.Observe(succeeded: false);
        log.Observe(succeeded: true);
        log.Observe(succeeded: true);

        var lines = File.ReadAllLines(logger.CurrentPath);
        Assert.Equal(2, lines.Count(line => line.Contains("\"event\":\"process_listing_failed\"", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.Contains("\"consecutive_failures\":1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"consecutive_failures\":3", StringComparison.Ordinal));
        var recovered = Assert.Single(lines, line => line.Contains("\"event\":\"process_listing_recovered\"", StringComparison.Ordinal));
        Assert.Contains("\"failed_readings\":3", recovered, StringComparison.Ordinal);
    }
}
