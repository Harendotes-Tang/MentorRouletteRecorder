using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="CaptureSessionRepository.UpdateProfile"/> is the write self-calibration's hot
/// bind uses to rebind a still-open session to a newly confirmed local profile. It must never
/// rewrite a session that has already closed: a closed session's profile is history.
/// </summary>
public sealed class CaptureSessionProfileUpdateTests
{
    private static CaptureSession OpenSession(string id, TestClock clock) => new()
    {
        CaptureSessionId = id,
        StartedAtUtc = clock.UtcNow,
        CollectorVersion = "test",
        Region = Region.Cn,
        GameBuild = "2026.08.05.0000.0000",
        ProtocolProfileId = "cn.2026.08.05",
        ProfileStatus = ProfileStatus.Verified,
        PacketsObserved = 0,
    };

    [Fact]
    public void UpdateProfile_OnAnOpenSession_RewritesProfileIdAndStatusAndRoundTripsThroughGet()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.Database.RunInTransaction(tx => sessions.Insert(OpenSession(sessionId, fixture.Clock), tx));

        var changed = sessions.UpdateProfile(sessionId, "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.True(changed);
        var row = sessions.Get(sessionId);
        Assert.NotNull(row);
        Assert.Equal("cn.2026.09.01.local", row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.Verified, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_CanClearTheProfileIdByPassingNull()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.Database.RunInTransaction(tx => sessions.Insert(OpenSession(sessionId, fixture.Clock), tx));

        var changed = sessions.UpdateProfile(sessionId, null, ProfileStatus.UnsupportedBuild);

        Assert.True(changed);
        var row = sessions.Get(sessionId);
        Assert.Null(row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.UnsupportedBuild, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_OnAClosedSession_RefusesAndLeavesTheRowUntouched()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.Database.RunInTransaction(tx =>
        {
            sessions.Insert(OpenSession(sessionId, fixture.Clock), tx);
            sessions.Close(sessionId, fixture.Clock.UtcNow, CaptureEndReason.UserStop, tx);
        });

        var changed = sessions.UpdateProfile(sessionId, "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.False(changed);
        var row = sessions.Get(sessionId);
        Assert.Equal("cn.2026.08.05", row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.Verified, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_OnAMissingSession_ReturnsFalse()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);

        var changed = sessions.UpdateProfile(Guid.NewGuid().ToString("D"), "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.False(changed);
    }

    [Fact]
    public void UpdateProfile_InsideAnEnclosingTransaction_ParticipatesInItsCommit()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.Database.RunInTransaction(tx => sessions.Insert(OpenSession(sessionId, fixture.Clock), tx));

        var changed = fixture.Database.RunInTransaction(tx =>
            sessions.UpdateProfile(sessionId, "cn.2026.09.01.local", ProfileStatus.Verified, tx));

        Assert.True(changed);
        Assert.Equal("cn.2026.09.01.local", sessions.Get(sessionId)!.ProtocolProfileId);
    }

    /// <summary>
    /// Audit 2026-10-03 OG-2. Every repository shares one SQLite connection, and every status
    /// poll opens a transaction on it from the IPC thread. The transaction-less form used to run
    /// straight on that connection: ADO.NET refused it while the other thread's transaction was
    /// open, the shared bind swallowed the refusal and recorded nothing for the session, and a
    /// confirmed calibration failed after its file was written. It must wait its turn instead.
    /// </summary>
    [Fact]
    public async Task UpdateProfile_WaitsForATransactionOpenOnAnotherThreadInsteadOfFailing()
    {
        using var fixture = new TestDatabase();
        var sessions = new CaptureSessionRepository(fixture.Database);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.Database.RunInTransaction(tx => sessions.Insert(OpenSession(sessionId, fixture.Clock), tx));
        var timeout = TimeSpan.FromSeconds(30);
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => fixture.Database.RunInTransaction(_ =>
        {
            inside.Set();
            release.Wait(timeout);
        }));
        Assert.True(inside.Wait(timeout));

        var update = Task.Run(() => sessions.UpdateProfile(sessionId, "cn.2026.09.01.local", ProfileStatus.Verified));
        // Long enough for an unguarded statement to have run into the open transaction.
        await Task.WhenAny(update, Task.Delay(500));
        release.Set();

        Assert.True(await update.WaitAsync(timeout));
        await holder.WaitAsync(timeout);
        Assert.Equal("cn.2026.09.01.local", sessions.Get(sessionId)!.ProtocolProfileId);
    }
}
