using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

internal sealed partial class SharedCalibrationSession
{
    private readonly CalibrationEvidenceCollector _publicEvidence;
    private (string Profile, ProfileOrigin? Origin, string? Template)? _selectedEvidenceIdentity;
    private string? _selectedEvidenceCandidate;

    // Registration is repeated by an index refresh so new quarantine epochs never reuse old sessions.
    private void RegisterEvidenceCandidate(DeclaredCandidate candidate, int quarantineEpoch = 0)
    {
        if (_host.SharedContext() is not { } context) return;
        _publicEvidence.Register(context.Key.Region, context.Key.GameBuild, context.Template, candidate, quarantineEpoch);
    }

    private void ObserveSelectedEvidence()
    {
        var selection = _host.SharedSelection();
        var context = _host.SharedContext();
        if (!selection.IsUsable || selection.Origin is not (ProfileOrigin.Shared or ProfileOrigin.Local) || selection.Profile is not { } profile)
        {
            ForgetSelectedEvidence();
            return;
        }
        var identity = (profile.ProfileSha256, selection.Origin, context?.Template.Source.ProfileSha256);
        if (_selectedEvidenceIdentity == identity) return;
        if ((context?.Template ?? Attempt(() => _services.SelectTemplate(profile.Region))) is not { } template ||
            Attempt(() => SharedProfileBuilder.RecoverPayload(profile, template)) is not { } payload ||
            SharedCandidateVerifier.Candidate(payload, template) is not { } candidate)
        {
            ForgetSelectedEvidence();
            _selectedEvidenceIdentity = identity;
            return;
        }
        // A template update must not relabel a bound code whose original identity is different.
        if (selection.Origin == ProfileOrigin.Shared && _bound?.Sha is { } original && original != candidate.CandidateId)
        {
            ForgetSelectedEvidence();
            _selectedEvidenceIdentity = identity;
            return;
        }
        var stored = Attempt(() => _services.SharedCalibrations.LoadCandidates(profile.Region, profile.GameBuild, template.Source.ProfileSha256))
            ?.FirstOrDefault(item => item.CodeSha256 == candidate.CandidateId);
        var previous = _selectedEvidenceCandidate;
        _selectedEvidenceIdentity = identity;
        _selectedEvidenceCandidate = candidate.CandidateId;
        if (previous is not null && previous != candidate.CandidateId) ReleaseEvidenceCandidate(previous);
        // The live index may be newer than disk when a state write failed, or this code fell below the cache limit.
        var epoch = _publicEvidence.RegisteredEpoch(profile.Region, profile.GameBuild, template, candidate.CandidateId)
            ?? stored?.QuarantineEpoch ?? 0;
        _publicEvidence.Register(profile.Region, profile.GameBuild, template, candidate, epoch);
    }

    private void ForgetSelectedEvidence()
    {
        var previous = _selectedEvidenceCandidate;
        _selectedEvidenceCandidate = null;
        _selectedEvidenceIdentity = null;
        if (previous is not null) ReleaseEvidenceCandidate(previous);
    }

    // A selected local/shared profile keeps observing after its formal calibration watch has ended.
    // Only candidates with no active owner leave the bounded public observer; receipts live separately.
    private void ReleaseEvidenceCandidate(string candidateId)
    {
        if (_bound?.Sha != candidateId && _selectedEvidenceCandidate != candidateId &&
            !_candidates.Any(candidate => candidate.Sha == candidateId))
            _publicEvidence.Unregister(candidateId);
    }

    internal void ObservePublicEvidence(DecodedMessage message) => _publicEvidence.Accept(message);
    internal void PublicEvidenceHealth(CaptureSessionHealth health) => _publicEvidence.Health(health);
    internal void PublicEvidenceLoss(long decoded = 0, long damaged = 0) => _publicEvidence.Lost(decoded, damaged);
    internal void CompletePublicEvidence(string sessionId, CaptureEndReason reason) => _publicEvidence.Complete(sessionId, reason);
    internal CalibrationEvidenceResult PublicEvidence(Region region, string build, string? code) =>
        _publicEvidence.Export(region, build, code);
}
