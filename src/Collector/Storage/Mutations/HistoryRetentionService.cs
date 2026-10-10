using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>Recycle-bin retention, controlled data purge and durable Desktop attachment cleanup.</summary>
public sealed class HistoryRetentionService : IDisposable
{
    public const int MaxBatchSize = 2000;
    public const int MaxRetentionDays = 36500;
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly object _maintenanceGate = new();
    private DateTimeOffset? _lastCheck;
    private Timer? _timer;
    private bool _disposed;

    public HistoryRetentionService(SqliteDatabase database, IClock clock)
    { _database = database; _clock = clock; }

    public int GetRetentionDays() => _database.Read(_ =>
    {
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT retention_days FROM history_retention_settings WHERE id = 1;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    /// <summary>Changing the interval preserves deletion dates; the next daily/startup sweep uses it.</summary>
    public int UpdateRetentionDays(int days)
    {
        if (days is < 0 or > MaxRetentionDays)
            throw CollectorException.BadRequest($"retention_days 必须在 0 到 {MaxRetentionDays} 之间，0 表示永不自动清理。", "retention_days");
        _database.RunInTransaction(tx =>
        {
            using var command = _database.CreateCommand(); command.Transaction = tx;
            command.CommandText = "UPDATE history_retention_settings SET retention_days = $days WHERE id = 1;";
            command.Parameters.AddWithValue("$days", days); command.ExecuteNonQuery();
        });
        return days;
    }

    public IReadOnlyList<string> PendingImageCleanup() => _database.RunInTransaction(tx =>
    {
        using var command = _database.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT run_id FROM pending_image_cleanup ORDER BY last_attempt_order, queued_at_utc, run_id LIMIT 2000;";
        var ids = new List<string>(); using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        if (ids.Count == 0) return ids;
        command.CommandText = "SELECT COALESCE(MAX(last_attempt_order),0)+1 FROM pending_image_cleanup;";
        var ordinal = (long)command.ExecuteScalar()!;
        foreach (var id in ids)
        {
            using var attempt = _database.CreateCommand(); attempt.Transaction = tx;
            attempt.CommandText = "UPDATE pending_image_cleanup SET last_attempt_order=$ordinal WHERE run_id=$id;";
            attempt.Parameters.AddWithValue("$ordinal", ordinal); attempt.Parameters.AddWithValue("$id", id); attempt.ExecuteNonQuery();
        }
        return ids;
    });

    /// <summary>Only the Desktop that successfully removed attachments acknowledges their stable IDs.</summary>
    public int AcknowledgeImageCleanup(IReadOnlyList<string> ids)
    {
        ValidateIds(ids, allowEmpty: true);
        return _database.RunInTransaction(tx =>
        {
            var count = 0;
            foreach (var id in ids)
            {
                using var command = _database.CreateCommand(); command.Transaction = tx;
                command.CommandText = "DELETE FROM pending_image_cleanup WHERE run_id = $id;";
                command.Parameters.AddWithValue("$id", id); count += command.ExecuteNonQuery();
            }
            return count;
        });
    }

    internal static void ValidateIds(IReadOnlyList<string> ids, bool allowEmpty = false)
    {
        if ((!allowEmpty && ids.Count == 0) || ids.Count > MaxBatchSize ||
            ids.Any(id => !Guid.TryParseExact(id, "D", out _)) || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
            throw CollectorException.BadRequest($"请选择不重复的标准记录编号，一次最多 {MaxBatchSize} 条。", "run_ids");
    }

    /// <summary>All callers validate soft-delete/revision prerequisites before entering this routine.</summary>
    internal void Purge(IReadOnlyList<MentorRun> runs, SqliteTransaction tx)
    {
        var now = UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow));
        foreach (var run in runs)
        {
            var id = run.RunId;
            // Mixed import/batch receipts are invalidated as a whole: they must never replay a purged body.
            using (var responses = _database.CreateCommand())
            {
                responses.Transaction = tx;
                responses.CommandText =
                    "SELECT request_id, message_type, response_json, NULL FROM ipc_idempotency " +
                    "WHERE EXISTS (SELECT 1 FROM json_tree(response_json) WHERE type = 'text' AND value = $id) " +
                    "UNION ALL SELECT request_id, 'CommitRunImport', selection_fingerprint, preview_id FROM run_import_batches " +
                    "WHERE EXISTS (SELECT 1 FROM json_tree(response_json) WHERE type = 'text' AND value = $id);";
                responses.Parameters.AddWithValue("$id", id);
                var receipts = new List<(string Request, string Type, string Fingerprint, string? Preview)>();
                using (var reader = responses.ExecuteReader())
                    while (reader.Read()) receipts.Add((reader.GetString(0), reader.GetString(1),
                        Hash(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
                foreach (var receipt in receipts)
                {
                    using var tombstone = _database.CreateCommand(); tombstone.Transaction = tx;
                    tombstone.CommandText =
                        "INSERT INTO ipc_request_tombstones(request_id,message_type,fingerprint,preview_id,purged_at_utc) " +
                        "VALUES($request,$type,$fingerprint,$preview,$now) ON CONFLICT(request_id) DO UPDATE " +
                        "SET preview_id = COALESCE(excluded.preview_id, ipc_request_tombstones.preview_id); " +
                        "DELETE FROM ipc_idempotency WHERE request_id = $request; " +
                        "DELETE FROM run_import_batches WHERE request_id = $request;";
                    tombstone.Parameters.AddWithValue("$request", receipt.Request);
                    tombstone.Parameters.AddWithValue("$type", receipt.Type);
                    tombstone.Parameters.AddWithValue("$fingerprint", receipt.Fingerprint);
                    tombstone.Parameters.AddWithValue("$preview", (object?)receipt.Preview ?? DBNull.Value);
                    tombstone.Parameters.AddWithValue("$now", now); tombstone.ExecuteNonQuery();
                }
            }
            using var purge = _database.CreateCommand(); purge.Transaction = tx;
            purge.CommandText =
                "INSERT OR IGNORE INTO ipc_request_tombstones(request_id,message_type,fingerprint,purged_at_utc) " +
                "SELECT request_id, change_kind, $fingerprint, $now FROM run_revisions WHERE run_id = $id AND request_id IS NOT NULL; " +
                "INSERT INTO purged_run_tombstones(run_id,source_fingerprint,purged_at_utc) " +
                "VALUES($id,(SELECT source_fingerprint FROM run_import_metadata WHERE run_id=$id),$now); " +
                "INSERT OR IGNORE INTO pending_image_cleanup(run_id,queued_at_utc) VALUES($id,$now); " +
                "INSERT INTO run_purge_authorizations(run_id) VALUES($id); " +
                "DELETE FROM run_events WHERE run_id=$id; DELETE FROM run_revisions WHERE run_id=$id; " +
                "DELETE FROM run_reflections WHERE run_id=$id; DELETE FROM run_import_metadata WHERE run_id=$id; " +
                "DELETE FROM mentor_runs WHERE run_id=$id AND soft_deleted=1; " +
                "DELETE FROM run_purge_authorizations WHERE run_id=$id;";
            purge.Parameters.AddWithValue("$id", id); purge.Parameters.AddWithValue("$now", now);
            purge.Parameters.AddWithValue("$fingerprint", Hash(id)); purge.ExecuteNonQuery();
        }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Runs once at startup and every elapsed day. Never touches backups or exported files.</summary>
    public int CheckExpired(bool force = false)
    {
        lock (_maintenanceGate)
        {
            if (_disposed) return 0;
            var now = UtcTimestamp.Truncate(_clock.UtcNow);
            if (!force && _lastCheck is { } last && now - last < TimeSpan.FromDays(1)) return 0;
            var count = _database.RunInTransaction(tx =>
            {
                using var command = _database.CreateCommand(); command.Transaction = tx;
                command.CommandText = "SELECT retention_days FROM history_retention_settings WHERE id=1;";
                var days = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (days == 0) return 0;
                command.CommandText = "SELECT run_id FROM mentor_runs WHERE soft_deleted=1 AND deleted_at_utc <= $cutoff ORDER BY run_id;";
                command.Parameters.AddWithValue("$cutoff", UtcTimestamp.ToText(now - TimeSpan.FromDays(days)));
                var ids = new List<string>(); using (var reader = command.ExecuteReader())
                    while (reader.Read()) ids.Add(reader.GetString(0));
                var repository = new Repositories.RunRepository(_database);
                var runs = ids.Select(id => repository.GetInternal(id, tx)!).ToArray();
                Purge(runs, tx); return runs.Length;
            });
            _lastCheck = now;
            return count;
        }
    }

    public void StartDailyChecks(Action<Exception> onFailure, Action<int>? onPurged = null)
    {
        _timer = new Timer(_ =>
        {
            try { var purged = CheckExpired(); if (purged > 0) onPurged?.Invoke(purged); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { onFailure(ex); }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        lock (_maintenanceGate) _disposed = true;
        if (_timer is not null)
        {
            using var done = new ManualResetEvent(false);
            if (_timer.Dispose(done)) done.WaitOne();
            _timer = null;
        }
    }
}
