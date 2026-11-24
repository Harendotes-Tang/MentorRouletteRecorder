using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="CaptureSessionRepository.UpdateProfile"/> is the write self-calibration's hot
/// bind uses to rebind a still-open session to a newly confirmed local profile. It must never
/// rewrite a session that has already closed: a closed session's profile is history.
/// </summary>
public sealed class CaptureSessionProfileUpdateTests : IDisposable
{
    private readonly TestDatabase _fixture = new();
    private readonly CaptureSessionRepository _sessions;
    private readonly string _sessionId = Guid.NewGuid().ToString("D");

    public CaptureSessionProfileUpdateTests()
    {
        _sessions = new CaptureSessionRepository(_fixture.Database);
        _fixture.Database.RunInTransaction(tx => _sessions.Insert(new CaptureSession
        {
            CaptureSessionId = _sessionId,
            StartedAtUtc = _fixture.Clock.UtcNow,
            CollectorVersion = "test",
            Region = Region.Cn,
            GameBuild = "2026.08.05.0000.0000",
            ProtocolProfileId = "cn.2026.08.05",
            ProfileStatus = ProfileStatus.Verified,
            PacketsObserved = 0,
        }, tx));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UpdateProfile_OnAnOpenSession_RewritesProfileIdAndStatusAndRoundTripsThroughGet()
    {
        var changed = _sessions.UpdateProfile(_sessionId, "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.True(changed);
        var row = _sessions.Get(_sessionId);
        Assert.NotNull(row);
        Assert.Equal("cn.2026.09.01.local", row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.Verified, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_CanClearTheProfileIdByPassingNull()
    {
        var changed = _sessions.UpdateProfile(_sessionId, null, ProfileStatus.UnsupportedBuild);

        Assert.True(changed);
        var row = _sessions.Get(_sessionId);
        Assert.Null(row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.UnsupportedBuild, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_OnAClosedSession_RefusesAndLeavesTheRowUntouched()
    {
        _fixture.Database.RunInTransaction(tx =>
            _sessions.Close(_sessionId, _fixture.Clock.UtcNow, CaptureEndReason.UserStop, tx));

        var changed = _sessions.UpdateProfile(_sessionId, "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.False(changed);
        var row = _sessions.Get(_sessionId);
        Assert.Equal("cn.2026.08.05", row!.ProtocolProfileId);
        Assert.Equal(ProfileStatus.Verified, row.ProfileStatus);
    }

    [Fact]
    public void UpdateProfile_OnAMissingSession_ReturnsFalse()
    {
        var changed = _sessions.UpdateProfile(Guid.NewGuid().ToString("D"), "cn.2026.09.01.local", ProfileStatus.Verified);

        Assert.False(changed);
    }

    [Fact]
    public void UpdateProfile_InsideAnEnclosingTransaction_ParticipatesInItsCommit()
    {
        var changed = _fixture.Database.RunInTransaction(tx =>
            _sessions.UpdateProfile(_sessionId, "cn.2026.09.01.local", ProfileStatus.Verified, tx));

        Assert.True(changed);
        Assert.Equal("cn.2026.09.01.local", _sessions.Get(_sessionId)!.ProtocolProfileId);
    }
}
