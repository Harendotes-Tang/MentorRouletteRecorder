using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// What the idempotency table stores for one applied <c>SetRunReflection</c>.
///
/// It is deliberately not a <see cref="MutationSnapshot"/>: a reflection write produces no
/// revision and no audit row, so that shape would store two meaningless zeroed fields.
/// </summary>
/// <param name="Fingerprint">Hash of the canonical request text.</param>
/// <param name="RunId">Run the reflection belongs to.</param>
/// <param name="Reflection">Reflection as stored, or null when the request cleared it.</param>
/// <param name="Run">Run as it stood right after the write.</param>
public sealed record ReflectionSnapshot(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("reflection")] RunReflection? Reflection,
    [property: JsonPropertyName("run")] MentorRun? Run);

/// <summary>
/// The single writer of <c>run_reflections</c>.
///
/// Two properties hold, and are why this is a service rather than a direct repository call:
/// <list type="number">
///   <item><description>the write is idempotent by <c>request_id</c>, through the same
///   <c>ipc_idempotency</c> table and the same fingerprint rule as every other mutating
///   message (contracts/error-codes.md);</description></item>
///   <item><description>an empty text deletes instead of storing a blank row, so
///   "clear my reflection" and "write a reflection" are one message, not two.</description></item>
/// </list>
/// A soft-deleted run may still receive a reflection.
/// </summary>
public sealed class RunReflectionService
{
    private readonly SqliteDatabase _database;
    private readonly RunRepository _runs;
    private readonly RunReflectionRepository _reflections;
    private readonly IdempotencyRepository _idempotency;
    private readonly IClock _clock;

    /// <summary>Creates the service over an open database.</summary>
    /// <param name="database">Open database; the single writer.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    public RunReflectionService(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        _database = database;
        _clock = clock;
        _runs = new RunRepository(database);
        _reflections = new RunReflectionRepository(database);
        _idempotency = new IdempotencyRepository(database, clock);
    }

    /// <summary>Writes, replaces or clears the reflection of one run.</summary>
    /// <param name="command">Validated request.</param>
    public ReflectionMutationOutcome Set(SetRunReflectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParseExact(command.RequestId, "D", out _))
        {
            throw CollectorException.BadRequest("request_id 必须是标准 UUID。", "request_id");
        }

        var text = ReflectionText.Normalize(command.Text);
        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "SetRunReflection", command.RunId, ReflectionText.Format(command.Mood), text);

        var (snapshot, replayed) = _database.RunInTransaction(tx =>
        {
            if (_idempotency.TryGetResponse(command.RequestId, tx) is { } stored)
            {
                return (Replay(stored, fingerprint), true);
            }

            var applied = Write(command, text, fingerprint, tx);
            _idempotency.Store(
                command.RequestId,
                "SetRunReflection",
                MutationSnapshotCodec.SerializeOther(applied),
                tx);
            return (applied, false);
        });

        return new ReflectionMutationOutcome(
            snapshot.RunId, snapshot.Reflection, snapshot.Run, replayed);
    }

    /// <summary>Applies the write itself, inside the caller's transaction.</summary>
    /// <param name="command">Validated request.</param>
    /// <param name="text">Trimmed text, or null to clear.</param>
    /// <param name="fingerprint">Fingerprint of this request.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    private ReflectionSnapshot Write(
        SetRunReflectionCommand command,
        string? text,
        string fingerprint,
        SqliteTransaction transaction)
    {
        var run = _runs.GetInternal(command.RunId, transaction)
            ?? throw CollectorException.NotFound(command.RunId);

        RunReflection? reflection = null;
        if (text is null)
        {
            _reflections.Delete(command.RunId, transaction);
        }
        else
        {
            reflection = _reflections.Upsert(
                command.RunId, command.Mood, text, _clock.UtcNow, transaction);
        }

        return new ReflectionSnapshot(
            fingerprint,
            command.RunId,
            reflection,
            run with { Reflection = reflection });
    }

    /// <summary>Replays a stored outcome, refusing a reused id that carries a different body.</summary>
    /// <param name="stored">Serialised snapshot from the idempotency table.</param>
    /// <param name="fingerprint">Fingerprint of the request that just arrived.</param>
    private static ReflectionSnapshot Replay(string stored, string fingerprint)
    {
        var snapshot = MutationSnapshotCodec.DeserializeOther<ReflectionSnapshot>(stored)
            ?? throw new CollectorException(
                ErrorCodes.Internal, "幂等记录已损坏，无法安全重放该请求。");

        if (!string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new CollectorException(
                ErrorCodes.IdempotencyConflict,
                "同一个 request_id 被用于内容不同的请求，已拒绝以免产生歧义的结果。",
                new Dictionary<string, object?> { ["conflict"] = "idempotency" },
                field: "request_id");
        }

        return snapshot;
    }
}
