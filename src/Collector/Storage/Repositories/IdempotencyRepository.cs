using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Stores the response of every applied mutating request, keyed by its client-generated
/// request id.
///
/// This is what makes "send, lose the connection, resend" safe: the second attempt finds the
/// stored response and returns it verbatim rather than applying the change again
/// (docs/manual-correction.md section 4). The table is persisted, so it survives a restart.
/// </summary>
public sealed class IdempotencyRepository
{
    /// <summary>
    /// How long a stored response stays replayable.
    ///
    /// The window only has to outlast "send, lose the pipe, resend", which is seconds; a day is
    /// generous. It is bounded so that a long-lived database does not carry every request id the
    /// user ever sent (review finding M4).
    /// </summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    public IdempotencyRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>
    /// Looks up a stored response, or null when this request was never applied. The
    /// transaction-less form gates itself, because every repository shares one connection and
    /// a command issued outside the gate is refused while another transaction is open.
    /// </summary>
    /// <param name="requestId">Client-generated request id.</param>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public string? TryGetResponse(string requestId, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        return transaction is null
            ? _database.Read(_ => Lookup(requestId, transaction: null))
            : Lookup(requestId, transaction);
    }

    private string? Lookup(string requestId, SqliteTransaction? transaction)
    {
        ThrowIfPurgedRequest(requestId, transaction);
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id = $request_id;";
        command.Parameters.AddWithValue("$request_id", requestId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// True when this request id was applied once and its stored response has since been
    /// pruned.
    ///
    /// <c>run_revisions.request_id</c> and <c>candidate_reviews.request_id</c> are UNIQUE, so a
    /// replay arriving after the retention window would otherwise die on the constraint and be
    /// reported as <c>ERR_INTERNAL</c> (review finding M-7). The append-only audit chain is never
    /// pruned, so it is the durable record that the request already happened; its response is
    /// gone, hence a conflict rather than a replay. Runs in the caller's transaction so the
    /// answer cannot race the write.
    /// </summary>
    /// <param name="requestId">Client-generated request id.</param>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public bool WasAppliedBeforePrune(string requestId, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        return transaction is null
            ? _database.Read(_ => AppliedLookup(requestId, transaction: null))
            : AppliedLookup(requestId, transaction);
    }

    private bool AppliedLookup(string requestId, SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM run_revisions WHERE request_id = $request_id) " +
            "OR EXISTS(SELECT 1 FROM candidate_reviews WHERE request_id = $request_id) " +
            "OR EXISTS(SELECT 1 FROM run_import_batches WHERE request_id = $request_id) " +
            "OR EXISTS(SELECT 1 FROM ipc_request_tombstones WHERE request_id = $request_id);";
        command.Parameters.AddWithValue("$request_id", requestId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Refuses delayed requests without returning any permanently deleted content.</summary>
    public void ThrowIfPurgedRequest(string requestId, SqliteTransaction? transaction = null, string? previewId = null)
    {
        if (transaction is null)
        {
            _database.Read(_ => { ThrowIfPurgedRequestCore(requestId, null, previewId); return 0; });
            return;
        }
        ThrowIfPurgedRequestCore(requestId, transaction, previewId);
    }

    private void ThrowIfPurgedRequestCore(string requestId, SqliteTransaction? transaction, string? previewId)
    {
        using var command = _database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM ipc_request_tombstones WHERE request_id=$id OR preview_id=$preview);";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$preview", (object?)previewId ?? DBNull.Value);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new CollectorException(ErrorCodes.IdempotencyConflict,
                "该请求涉及已永久删除的记录，不能重放；请刷新历史记录。",
                new Dictionary<string, object?> { ["request_id"] = requestId, ["reason"] = "RECORD_PURGED" }, field: "request_id");
    }

    /// <summary>Prevents a new import preview from recreating an erased stable ID or source fingerprint.</summary>
    public bool IsPurgedRun(string runId, string? sourceFingerprint = null, SqliteTransaction? transaction = null)
    {
        if (transaction is null) return _database.Read(_ => PurgedRunLookup(runId, sourceFingerprint, null));
        return PurgedRunLookup(runId, sourceFingerprint, transaction);
    }

    private bool PurgedRunLookup(string runId, string? fingerprint, SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM purged_run_tombstones WHERE run_id=$id OR source_fingerprint=$fingerprint);";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$fingerprint", (object?)fingerprint ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Records the response of an applied request. Must run in the same transaction as the change.</summary>
    /// <param name="requestId">Client-generated request id.</param>
    /// <param name="messageType">Message type, for diagnostics.</param>
    /// <param name="responseJson">Serialized response payload to replay later.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Store(string requestId, string messageType, string responseJson, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(messageType);
        ArgumentNullException.ThrowIfNull(responseJson);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO ipc_idempotency (request_id, message_type, response_json, created_at_utc) " +
            "VALUES ($request_id, $message_type, $response_json, $created);";
        command.Parameters.AddWithValue("$request_id", requestId);
        command.Parameters.AddWithValue("$message_type", messageType);
        command.Parameters.AddWithValue("$response_json", responseJson);
        command.Parameters.AddWithValue("$created", UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)));
        command.ExecuteNonQuery();

        Prune(transaction);
    }

    /// <summary>
    /// Removes rows that can no longer be replayed, in the caller's transaction.
    ///
    /// It runs here rather than on a timer because this is the only moment the table is known
    /// to be growing, and because a sweep in the same transaction as the insert cannot leave
    /// the table half-pruned if the mutation is rolled back. The comparison is a plain string
    /// one: <c>created_at_utc</c> is stored as UTC ISO-8601, which sorts lexicographically.
    /// </summary>
    /// <param name="transaction">Transaction the new row was written in.</param>
    private void Prune(SqliteTransaction transaction)
    {
        var cutoff = UtcTimestamp.Truncate(_clock.UtcNow) - RetentionWindow;

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ipc_idempotency WHERE created_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", UtcTimestamp.ToText(cutoff));
        command.ExecuteNonQuery();
    }
}
