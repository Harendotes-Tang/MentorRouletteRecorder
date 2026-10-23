using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>Original request identity and committed batch result recovered from a durable receipt.</summary>
internal sealed record BatchMutationReceipt(string Fingerprint, BatchRunMutationOutcome Outcome);

/// <summary>
/// Reads and writes the batch receipt format already persisted in <c>ipc_idempotency</c>.
/// Its private storage types keep that format stable independently of IPC rendering and domain member names.
/// </summary>
internal static class BatchMutationReceiptCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private sealed record StoredReceipt(
        [property: JsonPropertyName("batch_fingerprint")] string? Fingerprint,
        [property: JsonPropertyName("outcome")] StoredOutcome? Outcome);

    private sealed record StoredOutcome(
        [property: JsonPropertyName("changed_count")] int ChangedCount,
        [property: JsonPropertyName("run_ids")] string[]? RunIds,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("idempotent_replay")] bool IdempotentReplay);

    /// <summary>Encodes an original execution using the same field names and receipt envelope as older releases.</summary>
    /// <param name="fingerprint">Existing request fingerprint; its calculation is owned by the mutation service.</param>
    /// <param name="outcome">Committed batch result. The stored original execution always has replay set to false.</param>
    public static string Serialize(string fingerprint, BatchRunMutationOutcome outcome) =>
        JsonSerializer.Serialize(new StoredReceipt(fingerprint,
            new StoredOutcome(outcome.ChangedCount, outcome.RunIds.ToArray(), outcome.Action, false)), Options);

    /// <summary>
    /// Reads an existing batch receipt, or returns null when another mutation's receipt occupies the request id.
    /// Malformed JSON and incompatible primitive types still raise <see cref="JsonException"/>.
    /// </summary>
    /// <param name="json">Stored receipt body; it is not modified or rewritten by this read.</param>
    public static BatchMutationReceipt? Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredReceipt>(json, Options);
        if (stored?.Fingerprint is not { } fingerprint ||
            stored.Outcome is not { RunIds: { } runIds, Action: { } action } outcome)
            return null;

        return new BatchMutationReceipt(fingerprint,
            new BatchRunMutationOutcome(outcome.ChangedCount, runIds, action, outcome.IdempotentReplay));
    }
}
