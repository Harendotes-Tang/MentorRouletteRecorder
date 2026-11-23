using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>A database file as an earlier build of the schema left it.</summary>
internal static class LegacySchema
{
    /// <summary>
    /// Runs every migration up to and including <paramref name="version"/> on an open connection and records each
    /// in <c>schema_migrations</c>, stamped <paramref name="appliedAtUtc"/>, the way that build's runner did.
    /// The caller opens the connection with the options the test needs.
    /// </summary>
    /// <param name="connection">An open connection to the file.</param>
    /// <param name="version">The last schema version to apply.</param>
    /// <param name="appliedAtUtc">The migrations' clock.</param>
    public static void ApplyThrough(SqliteConnection connection, int version, DateTimeOffset appliedAtUtc)
    {
        var now = UtcTimestamp.ToText(appliedAtUtc);
        foreach (var script in MigrationRunner.Scripts.Where(script => script.Version <= version))
        {
            using var command = connection.CreateCommand();
            command.CommandText = script.Sql;
            command.Parameters.AddWithValue("$migration_now_utc", now);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO schema_migrations VALUES($version,$name,$sum,$now);";
            command.Parameters.AddWithValue("$version", script.Version);
            command.Parameters.AddWithValue("$name", script.Name);
            command.Parameters.AddWithValue("$sum", script.Checksum);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
    }
}
