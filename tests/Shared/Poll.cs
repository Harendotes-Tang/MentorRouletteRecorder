using System.Diagnostics;

namespace MentorRecorder.Collector.Tests;

/// <summary>Waiting for something another thread makes true.</summary>
internal static class Poll
{
    /// <summary>
    /// Checks <paramref name="condition"/> every 10 ms until it holds; fails with <paramref name="what"/> once
    /// <paramref name="patience"/> (10 s by default) has passed without it.
    /// </summary>
    /// <param name="condition">What must become true.</param>
    /// <param name="what">The failure message.</param>
    /// <param name="patience">How long to wait.</param>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? patience = null)
    {
        var limit = patience ?? TimeSpan.FromSeconds(10);
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < limit, what);
            await Task.Delay(10);
        }
    }
}
