using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Queries;

namespace MentorRecorder.Collector.Ipc;

/// <summary>Ordering of a paged statistics table.</summary>
/// <param name="Field">Column to order by, in wire spelling.</param>
/// <param name="Direction">Ascending or descending.</param>
public sealed record StatsSort(string Field, SortDirection Direction)
{
    /// <summary>Most attempts first, the contract default.</summary>
    public static StatsSort Default { get; } = new("attempt_count", SortDirection.Desc);
}

/// <summary>
/// Ordering for the dungeon and job tables.
///
/// These two tables are aggregated in memory, so sorting happens here rather than in SQL.
/// The accepted field names are a closed set taken from the contract, which is also why no
/// user-supplied string ever reaches a SQL <c>ORDER BY</c>.
/// </summary>
public static class StatsSorting
{
    /// <summary>Reads a <c>PagedStatsRequest.sort</c> object.</summary>
    /// <param name="reader">Sort object, or null for the default.</param>
    public static StatsSort Read(PayloadReader? reader)
    {
        if (reader is null)
        {
            return StatsSort.Default;
        }

        reader.RejectUnknown("field", "direction");
        var field = reader.String("field", 32) ?? "attempt_count";
        if (field is not ("attempt_count" or "completed_count" or "completion_rate"
            or "avg_duration_ms" or "name"))
        {
            throw CollectorException.BadRequest("sort.field 不是契约声明的取值。", "sort.field");
        }

        var direction = reader.String("direction", 8) switch
        {
            null or "desc" => SortDirection.Desc,
            "asc" => SortDirection.Asc,
            _ => throw CollectorException.BadRequest("sort.direction 只能取 asc 或 desc。", "sort.direction"),
        };

        return new StatsSort(field, direction);
    }

    /// <summary>Orders aggregated rows by the requested column.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="rows">Rows to order.</param>
    /// <param name="sort">Requested ordering.</param>
    /// <param name="attempts">Reads the attempt count of a row.</param>
    /// <param name="completed">Reads the completed count of a row.</param>
    /// <param name="rate">Reads the completion rate of a row.</param>
    /// <param name="duration">Reads the average duration of a row.</param>
    /// <param name="name">Reads the display name of a row.</param>
    public static IReadOnlyList<T> Order<T>(
        IReadOnlyList<T> rows,
        StatsSort sort,
        Func<T, int> attempts,
        Func<T, int> completed,
        Func<T, double?> rate,
        Func<T, double?> duration,
        Func<T, string> name)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sort);

        var ascending = sort.Direction == SortDirection.Asc;
        return sort.Field switch
        {
            "completed_count" => Sort(rows, completed, ascending),

            // Nulls mean "no sample", so they sort last in both directions rather than
            // pretending to be zero (docs/statistics-definitions.md section 14).
            "completion_rate" => SortNullable(rows, rate, ascending),
            "avg_duration_ms" => SortNullable(rows, duration, ascending),
            "name" => ascending
                ? rows.OrderBy(name, StringComparer.Ordinal).ToArray()
                : rows.OrderByDescending(name, StringComparer.Ordinal).ToArray(),
            _ => Sort(rows, attempts, ascending),
        };
    }

    private static IReadOnlyList<T> Sort<T, TKey>(IReadOnlyList<T> rows, Func<T, TKey> key, bool ascending) =>
        ascending ? rows.OrderBy(key).ToArray() : rows.OrderByDescending(key).ToArray();

    private static IReadOnlyList<T> SortNullable<T>(
        IReadOnlyList<T> rows, Func<T, double?> key, bool ascending)
    {
        var ordered = rows.OrderBy(row => key(row) is null);
        return ascending
            ? ordered.ThenBy(row => key(row) ?? 0).ToArray()
            : ordered.ThenByDescending(row => key(row) ?? 0).ToArray();
    }
}
