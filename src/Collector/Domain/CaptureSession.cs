namespace MentorRecorder.Collector.Domain;

/// <summary>
/// One capture or replay session. Mirrors capture_sessions (docs/data-model.md section 7).
///
/// No MAC address, public IP or any other network-environment identifier is stored; the
/// adapter id is treated as an opaque string.
/// </summary>
public sealed record CaptureSession
{
    /// <summary>UUID primary key. Also the namespace of event deduplication keys.</summary>
    public required string CaptureSessionId { get; init; }

    /// <summary>Session start time.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Session end time; null means the session never closed cleanly.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>Collector version that owned the session.</summary>
    public required string CollectorVersion { get; init; }

    /// <summary>Service region.</summary>
    public Region Region { get; init; } = Region.Unknown;

    /// <summary>Client build identifier.</summary>
    public string? GameBuild { get; init; }

    /// <summary>Protocol profile in effect.</summary>
    public string? ProtocolProfileId { get; init; }

    /// <summary>Profile status in effect; anything but Verified means nothing was recorded live.</summary>
    public ProfileStatus ProfileStatus { get; init; } = ProfileStatus.None;

    /// <summary>Opaque capture device name.</summary>
    public string? AdapterId { get; init; }

    /// <summary>Number of filtered packets observed.</summary>
    public long PacketsObserved { get; init; }

    /// <summary>Number of packets dropped by the bounded queue.</summary>
    public long PacketsDropped { get; init; }

    /// <summary>Why the session ended.</summary>
    public CaptureEndReason? EndReason { get; init; }
}
