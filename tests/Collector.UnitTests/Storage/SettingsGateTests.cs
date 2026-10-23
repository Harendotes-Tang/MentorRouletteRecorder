using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// One connection, one gate.
///
/// The whole process shares a single SQLite connection and <c>SqliteDatabase</c> serialises
/// access to it. A repository read that skips the gate is wrong, not merely unsynchronised:
/// ADO.NET refuses a command on a connection with a pending local transaction, so a dashboard
/// read fails with ERR_INTERNAL whenever the capture thread happens to be writing a run. The
/// dashboard is especially exposed because its three reads are three separate trips through
/// the gate (review finding H2).
///
/// Every transaction-less overload must therefore gate itself. These tests assert that by
/// holding a transaction open on another thread while calling them.
/// </summary>
public sealed class SettingsGateTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;

    public SettingsGateTests()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        _settings.EnsureDefaults();
    }

    [Fact]
    public void TheAchievementRowWaitsForAnOpenTransactionInsteadOfFailing()
    {
        AssertWaitsForTheGate(() => _settings.GetAchievementSettings());
    }

    [Fact]
    public void TheBaselineHistoryWaitsForAnOpenTransactionToo()
    {
        AssertWaitsForTheGate(() => _settings.ReadBaselineAudit());
    }

    [Fact]
    public async Task TheDashboardStillAnswersWhileTheCaptureThreadIsWriting()
    {
        var statistics = new StatisticsRepository(_database.Database, _settings);
        using var release = new ManualResetEventSlim();
        using var writing = new ManualResetEventSlim();

        var writer = Task.Run(() => _database.Database.RunInTransaction(_ =>
        {
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(20)));

        var reading = Task.Run(() => statistics.GetDashboard());
        release.Set();

        await writer.WaitAsync(TimeSpan.FromSeconds(20));
        var dashboard = await reading.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(dashboard);
    }

    /// <summary>
    /// Runs <paramref name="read"/> while a transaction is open and asserts that it blocks on
    /// the gate rather than throwing, and completes once the writer commits.
    /// </summary>
    /// <param name="read">The transaction-less repository read under test.</param>
    private void AssertWaitsForTheGate(Func<object> read)
    {
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var writer = Task.Run(() => _database.Database.RunInTransaction(_ =>
        {
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(20)));

        var reading = Task.Run(read);
        Assert.False(
            reading.Wait(TimeSpan.FromMilliseconds(400)),
            "the read did not wait for the open transaction; it is running outside the gate");

        release.Set();
        Assert.True(writer.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(20)));
        Assert.NotNull(reading.Result);
    }

    public void Dispose() => _database.Dispose();
}
