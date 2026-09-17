namespace MentorRecorder.Collector.Domain.Queries;

/// <summary>Timestamp column a date range applies to.</summary>
public enum RunDateField
{
    /// <summary>matched_at_utc.</summary>
    MatchedAtUtc,

    /// <summary>entered_at_utc, the default.</summary>
    EnteredAtUtc,

    /// <summary>ended_at_utc.</summary>
    EndedAtUtc,
}

/// <summary>Column a run listing is ordered by.</summary>
public enum RunSortField
{
    /// <summary>matched_at_utc.</summary>
    MatchedAtUtc,

    /// <summary>entered_at_utc, the default.</summary>
    EnteredAtUtc,

    /// <summary>ended_at_utc.</summary>
    EndedAtUtc,

    /// <summary>duration_ms.</summary>
    DurationMs,

    /// <summary>duty_name.</summary>
    DutyName,
}

/// <summary>Sort direction.</summary>
public enum SortDirection
{
    /// <summary>Ascending.</summary>
    Asc,

    /// <summary>Descending, the default.</summary>
    Desc,
}

/// <summary>
/// Server-side filter for listings and statistics. Mirrors <c>$defs/RunFilter</c>.
///
/// Array members are OR within a field and AND across fields. Soft-deleted runs are excluded
/// unless <see cref="IncludeDeleted"/> is set, and even then they never reach statistics
/// (docs/statistics-definitions.md section 0).
/// </summary>
public sealed record RunFilter
{
    /// <summary>An empty filter that constrains nothing.</summary>
    public static RunFilter Empty { get; } = new();

    /// <summary>Inclusive lower bound of the date range.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Inclusive upper bound of the date range.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Column the date range applies to.</summary>
    public RunDateField DateField { get; init; } = RunDateField.EnteredAtUtc;

    /// <summary>Content ids to keep.</summary>
    public IReadOnlyList<int> ContentIds { get; init; } = Array.Empty<int>();

    /// <summary>Duty categories to keep.</summary>
    public IReadOnlyList<string> DutyCategories { get; init; } = Array.Empty<string>();

    /// <summary>Job ids to keep.</summary>
    public IReadOnlyList<int> JobIds { get; init; } = Array.Empty<int>();

    /// <summary>Results to keep.</summary>
    public IReadOnlyList<RunResult> Results { get; init; } = Array.Empty<RunResult>();

    /// <summary>Sources to keep.</summary>
    public IReadOnlyList<RunSource> Sources { get; init; } = Array.Empty<RunSource>();

    /// <summary>Keep only runs a human has corrected.</summary>
    public bool CorrectedOnly { get; init; }

    /// <summary>Include soft-deleted runs in listings. Never affects statistics.</summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>Keep only runs that carry a 导随心得.</summary>
    public bool WithReflection { get; init; }

    /// <summary>
    /// Keep only runs whose review flag has this value, or every run when null.
    ///
    /// Nullable rather than a bool with a default because "show me what still needs review"
    /// and "show me everything" are different questions, and the index
    /// <c>ix_runs_pending_review</c> exists precisely so the first one is answered by the
    /// server rather than by the client filtering a page it already received.
    /// </summary>
    public bool? PendingReview { get; init; }

    /// <summary>Case-insensitive substring matched against duty name and job name.</summary>
    public string? Text { get; init; }
}

/// <summary>Ordering of a run listing.</summary>
/// <param name="Field">Column to order by.</param>
/// <param name="Direction">Ascending or descending.</param>
public sealed record RunSort(
    RunSortField Field = RunSortField.EnteredAtUtc,
    SortDirection Direction = SortDirection.Desc)
{
    /// <summary>Newest entered runs first.</summary>
    public static RunSort Default { get; } = new();
}

/// <summary>One page of results plus the total row count.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items on this page.</param>
/// <param name="PageNumber">One-based page number.</param>
/// <param name="PageSize">Page size actually used.</param>
/// <param name="Total">Total number of matching rows, not pages.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);

/// <summary>Paging parameters, with the contract cap applied.</summary>
public static class Paging
{
    /// <summary>Largest page the contract permits.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Page size used when the client does not ask for one.</summary>
    public const int DefaultPageSize = 50;
}
