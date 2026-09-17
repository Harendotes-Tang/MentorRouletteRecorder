using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// B2a review finding 2: <see cref="SharedCalibrationSession.Retains"/> only answers a question, and the shared
/// profile the selection holds is adopted - its code recovered and registered for verification - by
/// <see cref="SharedCalibrationSession.Sync"/>, which every arm of calibration ends with.
/// </summary>
public sealed class SharedCalibrationSessionReconcileTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private (SharedCode Code, ProtocolProfile Profile) SharedProfileOnDisk()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var built = SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>());
        SharedProfileFiles.Write(built, _bed.SharedRoot);
        return (code, built.Profile!);
    }

    [Fact]
    public void RetainsIsAQueryAndSyncIsWhereTheSelectedSharedProfileIsAdopted()
    {
        var (code, profile) = SharedProfileOnDisk();
        var selection = new ProfileSelection(
            ProfileCompatibilityStatus.Verified, profile.ToBinding(), profile, Region.Cn, Bed.Build, "selected", ProfileOrigin.Shared);
        var host = new RecordingHost(selection, new SharedContext(
            new SharedKey(Region.Cn, Bed.Build, Bed.TemplateSha, 1), Bed.Template, selection, null));
        var session = new SharedCalibrationSession(new object(), host, _bed.Services(fetch: false), _bed.Db.Clock, null, enabled: false);
        var untouched = session.Signature();

        Assert.True(session.Retains(selection));
        Assert.True(session.Retains(selection));

        Assert.Equal(untouched, session.Signature());
        Assert.Null(session.Snapshot().ProfileId);
        Assert.Empty(host.Registered);

        session.Sync();

        var adopted = session.Snapshot();
        Assert.Equal(profile.ProfileId, adopted.ProfileId);
        Assert.Equal(code.Sha[..12], Assert.Single(adopted.Candidates).Sha12);
        Assert.Equal(new[] { code.Sha }, host.Registered);
        Assert.True(session.Retains(selection));
    }

    [Fact]
    public void EveryPathThatArmsCalibrationBesideASharedProfileWatchesItUnderItsCode()
    {
        var (code, _) = SharedProfileOnDisk();
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);

        pipeline.ApplyCalibrationSetting(false);
        Assert.False(pipeline.CalibrationArmed);
        pipeline.ApplyCalibrationSetting(true);

        Assert.True(pipeline.CalibrationArmed);
        var inUse = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(code.Sha[..12], inUse.Sha12);
        Assert.Equal(SharedCandidateStatus.InUse, inUse.Status);
    }

    /// <summary>Just enough of the pipeline to watch what the session asks of it.</summary>
    private sealed class RecordingHost : ISharedCalibrationHost
    {
        private readonly ProfileSelection _selection;
        private readonly SharedContext _context;

        public RecordingHost(ProfileSelection selection, SharedContext context)
        {
            _selection = selection;
            _context = context;
        }

        public List<string> Registered { get; } = new();

        SharedContext? ISharedCalibrationHost.SharedContext() => _context;

        ProfileSelection ISharedCalibrationHost.SharedSelection() => _selection;

        CalibrationSnapshot? ISharedCalibrationHost.SharedEvidence() => null;

        void ISharedCalibrationHost.RegisterSharedCandidate(DeclaredCandidate candidate)
        {
            if (!Registered.Contains(candidate.CandidateId))
            {
                Registered.Add(candidate.CandidateId);
            }
        }

        void ISharedCalibrationHost.UnregisterSharedCandidate(string candidateId) => Registered.Remove(candidateId);

        bool ISharedCalibrationHost.HasFinishedSharedRun(string profileId) => false;

        SharedBindResult ISharedCalibrationHost.CommitSharedBind(SharedBindRequest request) =>
            throw new InvalidOperationException("nothing binds in this test");

        void ISharedCalibrationHost.UnbindSharedProfile(string profileId) =>
            throw new InvalidOperationException("nothing is withdrawn in this test");

        void ISharedCalibrationHost.ReselectAfterSharedChange(Func<GameProcessDetection, ProfileSelection> select) =>
            throw new InvalidOperationException("nothing is withdrawn in this test");

        void ISharedCalibrationHost.SharedCalibrationChanged()
        {
        }
    }
}
