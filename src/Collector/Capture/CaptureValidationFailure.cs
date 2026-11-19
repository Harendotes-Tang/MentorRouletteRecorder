using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Capture;

/// <summary>Safe failure categories shared by capture sources and the validation diagnostics.</summary>
internal enum CaptureFailureCategory
{
    MonitorFault,
    GameExecutableMissing,
    GameExecutableLoad,
    OodleSignatureMismatch,
    NetworkDataUnavailable,
    PcapError,
    PcapOpen,
    NpcapRead,
    OwnershipDecode,
    IngressCapacity,
    MonitorStart,
    IdentityKey,
    TraceWrite,
    ValidationFault,
    Cleanup,
}

/// <summary>Stage of the validation round when the failure first occurred.</summary>
internal enum CaptureValidationStage { Waiting, Starting, Recording, Finalizing }

/// <summary>The first failure in the most recent failed validation round.</summary>
internal sealed record CaptureValidationFailure(
    CaptureFailureCategory Category, CaptureValidationStage Stage, DateTimeOffset AtUtc)
{
    /// <summary>
    /// What the player can do about this failure, when the category says more than the
    /// general sentence; null keeps <c>capture.validation.failed</c>.
    /// </summary>
    internal UserText? Advice => Category switch
    {
        CaptureFailureCategory.PcapOpen or CaptureFailureCategory.PcapError or CaptureFailureCategory.NpcapRead
            => CollectorTexts.CaptureValidationFailedNpcap(),
        CaptureFailureCategory.OodleSignatureMismatch => CollectorTexts.CaptureValidationFailedGameDataFormat(),
        CaptureFailureCategory.GameExecutableMissing or CaptureFailureCategory.GameExecutableLoad
            => CollectorTexts.CaptureValidationFailedGameExecutable(),
        CaptureFailureCategory.NetworkDataUnavailable => CollectorTexts.CaptureValidationFailedNetworkData(),
        _ => null,
    };

    internal JsonObject ToJson() => new()
    {
        ["category"] = Category.ToString(),
        ["stage"] = Stage.ToString(),
        ["at_utc"] = UtcTimestamp.ToText(AtUtc),
    };
}
