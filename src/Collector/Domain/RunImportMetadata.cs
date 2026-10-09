namespace MentorRecorder.Collector.Domain;

/// <summary>Local provenance of a merged personal record; source time is never a game endpoint.</summary>
/// <param name="SourceKind">CSV, XLSX, JSON, BACKUP, PASTE or SCREENSHOT.</param>
/// <param name="SourceName">User-visible origin name, without a local input path.</param>
/// <param name="SourceRecordedAt">Original source record-time text, when present.</param>
/// <param name="SourceRecordedAtUtc">Source record time converted using the explicitly selected timezone.</param>
/// <param name="ImportedAtUtc">Time this library first accepted the record.</param>
/// <param name="SourceFingerprint">Exact source identity retained across retries; never used for approximate merging.</param>
/// <param name="MentorConfirmed">Whether the imported source explicitly identifies personal mentor records.</param>
public sealed record RunImportMetadata(
    string SourceKind,
    string? SourceName,
    string? SourceRecordedAt,
    DateTimeOffset? SourceRecordedAtUtc,
    DateTimeOffset ImportedAtUtc,
    string SourceFingerprint,
    bool MentorConfirmed = true);
