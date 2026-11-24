using MentorRecorder.Collector.Domain;
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
        _settings = _database.Settings();
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
    /// Audit 2026-10-03 OG-2. Every repository shares one SQLite connection, and every status
    /// poll opens a transaction on it from the IPC thread. The transaction-less form used to run
    /// straight on that connection: ADO.NET refused it while the other thread's transaction was
    /// open, the shared bind swallowed the refusal and recorded nothing for the session, and a
    /// confirmed calibration failed after its file was written. It must wait its turn instead.
    /// </summary>
    [Fact]
    public void ACaptureSessionProfileUpdateWaitsForAnOpenTransactionToo()
    {
        var sessions = new CaptureSessionRepository(_database.Database);
        var sessionId = _database.OpenCaptureSession(Region.Cn, "2026.08.05.0000.0000", ProfileStatus.Verified);
        var changed = false;

        AssertWaitsForTheGate(() => changed = sessions.UpdateProfile(sessionId, "cn.2026.09.01.local", ProfileStatus.Verified));

        Assert.True(changed);
        Assert.Equal("cn.2026.09.01.local", sessions.Get(sessionId)!.ProtocolProfileId);
    }

    /// <summary>
    /// Runs <paramref name="read"/> while a transaction is open and asserts that it blocks on
    /// the gate rather than throwing, and completes once the writer commits.
    /// </summary>
    /// <param name="read">The transaction-less repository call under test.</param>
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
