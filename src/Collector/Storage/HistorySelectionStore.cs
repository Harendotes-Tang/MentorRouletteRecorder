using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage;

/// <summary>Only the history fields needed to display and classify a checked record.</summary>
internal sealed record HistorySelectionRow(string RunId, int Revision, bool SoftDeleted, RunSource Source,
    Region Region, string? CaptureSessionId);

/// <summary>Process-local frozen history reads and content-addressed mutation or export targets.</summary>
internal sealed class HistorySelectionStore(SqliteDatabase database) : IDisposable
{
    internal const int MaxRetainedHandlesPerKind = 4;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<string, Frozen> _frozen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Staged> _staged = new(StringComparer.Ordinal);
    private Timer? _cleanup;
    private int _freezeInFlight;
    private bool _disposed;

    /// <summary>Materializes lightweight rows in one read transaction; the page handle holds no live SQLite reader.</summary>
    public (string Id, Page<HistorySelectionRow> Page) Freeze(RunFilter? filter, CancellationToken cancellationToken)
    {
        // Bound outstanding copies of the same database; there is no row cap within a selection.
        if (Interlocked.CompareExchange(ref _freezeInFlight, 1, 0) != 0)
            throw new CollectorException(ErrorCodes.DbBusy, CollectorTexts.DatabaseBusyRetry(), retryable: true);
        try
        {
            var rows = database.ReadSnapshot((snapshot, transaction) =>
                new RunRepository(snapshot).ReadHistorySelection(filter, transaction, cancellationToken), cancellationToken);
            var id = Guid.NewGuid().ToString("D");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCleanup();
                if (_frozen.Count >= MaxRetainedHandlesPerKind)
                    _frozen.Remove(_frozen.MinBy(pair => pair.Value.Touched).Key);
                _frozen.Add(id, new Frozen(rows));
            }
            return (id, Slice(rows, 1));
        }
        finally
        {
            Volatile.Write(ref _freezeInFlight, 0);
        }
    }

    /// <summary>Pages frozen values, refreshing their idle lifetime.</summary>
    public Page<HistorySelectionRow> ReadPage(string id, int page)
    {
        lock (_gate)
        {
            if (!_frozen.TryGetValue(id, out var frozen) || Expired(frozen.Touched))
                throw Unavailable();
            frozen.Touched = Environment.TickCount64;
            return Slice(frozen.Rows, page);
        }
    }

    /// <summary>Releases a read or staged handle. A duplicate release succeeds.</summary>
    public void Release(string id)
    {
        lock (_gate)
        {
            _frozen.Remove(id);
            _staged.Remove(id);
        }
    }

    /// <summary>Adds a bounded ordered chunk, or acknowledges an identical retry of one already accepted.</summary>
    public (int NextIndex, bool Sealed) Stage(string digest, int index, IReadOnlyList<BatchRunTarget> runs, bool last)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_staged.TryGetValue(digest, out var expired) && Expired(expired.Touched))
                _staged.Remove(digest);

            if (!_staged.TryGetValue(digest, out var staged))
            {
                if (index != 0) throw Unavailable();
                staged = new Staged();
            }

            if (index < staged.Chunks.Count)
            {
                var previous = staged.Chunks[index];
                if (last != previous.Last || runs.Count != previous.Count ||
                    !staged.Targets.Skip(previous.Start).Take(previous.Count).SequenceEqual(runs))
                    throw InvalidChunk();
                staged.Touched = Environment.TickCount64;
                return (staged.Chunks.Count, staged.Sealed);
            }

            if (index != staged.Chunks.Count || staged.Sealed)
                throw InvalidChunk();
            string? previousId = staged.Targets.Count == 0 ? null : staged.Targets[^1].RunId;
            foreach (var target in runs)
            {
                if (string.CompareOrdinal(previousId, target.RunId) >= 0)
                    throw InvalidChunk();
                previousId = target.RunId;
            }

            if (last && !string.Equals(digest, Digest(staged.Targets.Concat(runs)), StringComparison.Ordinal))
                throw InvalidChunk();

            EnsureCleanup();
            if (index == 0 && _staged.Count >= MaxRetainedHandlesPerKind)
                _staged.Remove(_staged.MinBy(pair => pair.Value.Touched).Key);
            staged.Chunks.Add(new Chunk(staged.Targets.Count, runs.Count, last));
            staged.Targets.AddRange(runs);
            staged.Sealed = last;
            staged.Touched = Environment.TickCount64;
            _staged[digest] = staged;
            return (staged.Chunks.Count, staged.Sealed);
        }
    }

    /// <summary>Gets an immutable, sealed target set. Scoped cache release cannot revoke an admitted operation.</summary>
    public IReadOnlyList<BatchRunTarget> RequireStaged(string digest)
    {
        lock (_gate)
        {
            if (!_staged.TryGetValue(digest, out var staged) || !staged.Sealed || Expired(staged.Touched))
                throw Unavailable();
            staged.Touched = Environment.TickCount64;
            return staged.Targets.AsReadOnly();
        }
    }

    /// <summary>SHA-256 over sorted canonical lowercase UUID and invariant decimal revision lines.</summary>
    internal static string Digest(IEnumerable<BatchRunTarget> targets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var target in targets)
        {
            var revision = target.ExpectedRevision.ToString(CultureInfo.InvariantCulture);
            hash.AppendData(Encoding.UTF8.GetBytes(target.RunId + ":" + revision + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Page<HistorySelectionRow> Slice(IReadOnlyList<HistorySelectionRow> rows, int page)
    {
        var offset = (long)(page - 1) * Paging.MaxPageSize;
        var start = (int)Math.Min(offset, rows.Count);
        var length = Math.Min(Paging.MaxPageSize, rows.Count - start);
        var slice = new HistorySelectionRow[length];
        for (var index = 0; index < length; index++) slice[index] = rows[start + index];
        return new Page<HistorySelectionRow>(slice, page, Paging.MaxPageSize, rows.Count);
    }

    private void EnsureCleanup() => _cleanup ??= new Timer(_ => RemoveExpired(), null,
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private void RemoveExpired()
    {
        lock (_gate)
        {
            foreach (var id in _frozen.Where(pair => Expired(pair.Value.Touched)).Select(pair => pair.Key).ToArray())
                _frozen.Remove(id);
            foreach (var id in _staged.Where(pair => Expired(pair.Value.Touched)).Select(pair => pair.Key).ToArray())
                _staged.Remove(id);
        }
    }

    private static bool Expired(long touched) => Environment.TickCount64 - touched >= IdleLifetime.TotalMilliseconds;

    private static CollectorException Unavailable() => new(ErrorCodes.NotFound,
        CollectorTexts.HistorySelectionUnavailable(), field: "selection_id");

    private static CollectorException InvalidChunk() =>
        CollectorException.BadRequest(CollectorTexts.HistorySelectionChunkMismatch(), "runs");

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cleanup?.Dispose();
            _frozen.Clear();
            _staged.Clear();
        }
    }

    private sealed class Frozen(IReadOnlyList<HistorySelectionRow> rows)
    {
        public IReadOnlyList<HistorySelectionRow> Rows { get; } = rows;
        public long Touched { get; set; } = Environment.TickCount64;
    }

    private sealed class Staged
    {
        public List<BatchRunTarget> Targets { get; } = [];
        public List<Chunk> Chunks { get; } = [];
        public bool Sealed { get; set; }
        public long Touched { get; set; }
    }

    private readonly record struct Chunk(int Start, int Count, bool Last);
}
