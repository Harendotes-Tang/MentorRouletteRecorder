using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The identity earlier versions stored for an imported row, frozen as those versions computed it, so a test can
/// leave behind what such a version wrote. It is the tests' copy of the old rule, not a reference to today's.
/// </summary>
internal static class EarlierImports
{
    /// <summary>The key of a row by its own content: the hash of its canonical values, numbered as the first such row.</summary>
    /// <param name="row">The parsed row, before a source key is added to it.</param>
    public static string RowKey(ImportSourceRow row) =>
        RunImportCandidateNormalizer.Hash(RunImportCandidateNormalizer.CanonicalValues(row.Values)) + ":1";

    /// <summary>
    /// The key of a whole file: the hash of every parsed row's values. A row's identity was this key, a colon and the
    /// row number.
    /// </summary>
    /// <param name="rows">Every parsed row of the file, before a source key is added to any of them.</param>
    public static string WholeFileKey(IEnumerable<ImportSourceRow> rows) =>
        RunImportCandidateNormalizer.Hash(new JsonArray(rows.Select(row => (JsonNode)row.Values.DeepClone()).ToArray())
            .ToJsonString(Wire.JsonOptions));

    /// <summary>The source fingerprint stored for an imported row: its source, its identity and the facts recorded.</summary>
    /// <param name="kind">Source kind, such as CSV.</param>
    /// <param name="sourceName">Source name the import was given.</param>
    /// <param name="identity">The row's identity: its run id, <see cref="RowKey"/>, or the whole-file form.</param>
    /// <param name="stored">The run as that version recorded it.</param>
    public static string Fingerprint(string kind, string sourceName, string identity, MentorRun stored) =>
        RunImportCandidateNormalizer.Hash(string.Join('\n', kind, sourceName, identity,
            RunImportCandidateNormalizer.Facts(stored)));

    /// <summary>Records that the run with this id and fingerprint was permanently deleted.</summary>
    /// <param name="database">The database.</param>
    /// <param name="runId">Id of the deleted run.</param>
    /// <param name="fingerprint">Its source fingerprint.</param>
    /// <param name="tx">The open transaction to write in, or null.</param>
    public static void Tombstone(TestDatabase database, string runId, string fingerprint, SqliteTransaction? tx = null)
    {
        using var command = database.Database.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO purged_run_tombstones VALUES ($id,$fingerprint,'2026-10-22T00:00:00.000Z');";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.ExecuteNonQuery();
    }
}
