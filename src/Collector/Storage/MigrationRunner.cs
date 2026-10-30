using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage;

/// <summary>One migration script, embedded in the assembly.</summary>
/// <param name="Version">Monotonic version number parsed from the file name.</param>
/// <param name="Name">File name, for example 0001_initial.sql.</param>
/// <param name="Sql">Script body.</param>
/// <param name="Checksum">Lower-case hex SHA-256 of the script body.</param>
public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);

/// <summary>
/// Applies migrations forward, in one transaction, before IPC is opened.
///
/// Scripts are embedded resources, so the runtime never depends on the working directory
/// or on files a user could edit next to the executable. Three things make a database
/// refuse to open: a script whose recorded checksum no longer matches, a gap in the version
/// sequence, and a database whose version is higher than this build supports. All three
/// surface as <c>ERR_DB_INTEGRITY</c> (migrations/README.md, rules 1 and 5).
///
/// Refusing to open means the Collector does not start at all: there is no read-only fallback
/// anywhere in the tree (2026-09-21 full audit, finding 15). The messages below say so, and
/// point at the file the user should copy before touching anything.
/// </summary>
public static class MigrationRunner
{
    private const string ResourcePrefix = "MentorRecorder.Collector.Migrations.";

    /// <summary>Every embedded migration, ordered by version.</summary>
    public static IReadOnlyList<MigrationScript> Scripts { get; } = LoadScripts();

    /// <summary>Highest schema version this build supports.</summary>
    public static int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    /// <summary>
    /// Brings <paramref name="connection"/> up to <see cref="LatestVersion"/> and returns the
    /// resulting version. Re-running against an up-to-date database is a no-op.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="clock">Clock used to stamp applied rows.</param>
    public static int MigrateToLatest(SqliteConnection connection, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);

        var applied = ReadAppliedMigrations(connection);
        VerifyHistory(applied, DatabasePath(connection));

        // SQLite's documented create/copy/drop/rename rebuild requires FK enforcement to
        // be disabled outside BEGIN. legacy_alter_table keeps dependent trigger/view text
        // referring to the final table name while the original table is briefly absent.
        var rebuild = !applied.ContainsKey(12);
        var foreignKeys = Pragma(connection, "foreign_keys");
        var legacyAlter = Pragma(connection, "legacy_alter_table");
        if (rebuild) Execute(connection, null, "PRAGMA foreign_keys = OFF; PRAGMA legacy_alter_table = ON;");
        try
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var script in Scripts)
                {
                    if (applied.ContainsKey(script.Version))
                    {
                        continue;
                    }

                    IReadOnlyList<string> achievementObjects = Array.Empty<string>();
                    if (script.Version == 12)
                    {
                        RebuildRegionTable(connection, transaction, "capture_sessions");
                        RebuildRegionTable(connection, transaction, "mentor_runs");
                        achievementObjects = SchemaObjects(connection, transaction, "achievement_settings");
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = script.Sql;
                        command.Parameters.AddWithValue("$migration_now_utc", UtcTimestamp.ToText(UtcTimestamp.Truncate(clock.UtcNow)));
                        command.ExecuteNonQuery();
                    }

                    foreach (var sql in achievementObjects) Execute(connection, transaction, sql);

                    using (var record = connection.CreateCommand())
                    {
                        record.Transaction = transaction;
                        record.CommandText =
                            "INSERT INTO schema_migrations (version, name, checksum, applied_at_utc) " +
                            "VALUES ($version, $name, $checksum, $applied);";
                        record.Parameters.AddWithValue("$version", script.Version);
                        record.Parameters.AddWithValue("$name", script.Name);
                        record.Parameters.AddWithValue("$checksum", script.Checksum);
                        record.Parameters.AddWithValue("$applied", UtcTimestamp.ToText(clock.UtcNow));
                        record.ExecuteNonQuery();
                    }
                }

                if (rebuild)
                {
                    using var check = connection.CreateCommand();
                    check.Transaction = transaction;
                    check.CommandText = "PRAGMA foreign_key_check;";
                    using var reader = check.ExecuteReader();
                    if (reader.Read())
                        throw new CollectorException(ErrorCodes.DbIntegrity,
                            CollectorTexts.StartupMigrationFailedNoPath(),
                            new Dictionary<string, object?> { ["foreign_key_table"] = reader.GetString(0) });
                }
                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                throw new CollectorException(
                    ErrorCodes.DbIntegrity,
                    DatabasePath(connection) is { } path
                        ? CollectorTexts.StartupMigrationFailed(path)
                        : CollectorTexts.StartupMigrationFailedNoPath(),
                    new Dictionary<string, object?> { ["sqlite_error"] = ex.SqliteErrorCode },
                    inner: ex);
            }

        }
        finally
        {
            if (rebuild) Execute(connection, null,
                $"PRAGMA foreign_keys = {foreignKeys}; PRAGMA legacy_alter_table = {legacyAlter};");
        }

        return LatestVersion;
    }

    private static long Pragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + name + ";";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> SchemaObjects(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name = $table AND type IN ('index', 'trigger') " +
            "AND sql IS NOT NULL ORDER BY type, name;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var sql = new List<string>();
        while (reader.Read()) sql.Add(reader.GetString(0));
        return sql;
    }

    /// <summary>
    /// Rebuilds only the region whitelist; copying the actual schema retains every added
    /// column/constraint and explicit schema object. Child tables and audit rows are untouched.
    /// Names here are fixed migration-owned identifiers, never request text.
    /// </summary>
    private static void RebuildRegionTable(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        var objects = SchemaObjects(connection, transaction, table);
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = $table;";
        schema.Parameters.AddWithValue("$table", table);
        var sql = (string)schema.ExecuteScalar()!;
        const string oldRegion = "region IN ('CN', 'GLOBAL', 'UNKNOWN')";
        if (!sql.Contains(oldRegion, StringComparison.Ordinal))
            throw new CollectorException(ErrorCodes.DbIntegrity, CollectorTexts.StartupMigrationFailedNoPath());
        var replacement = "_regional_" + table;
        var body = sql[sql.IndexOf('(')..].Replace(oldRegion,
            "region IN ('CN', 'TC', 'GLOBAL', 'UNKNOWN')", StringComparison.Ordinal);
        Execute(connection, transaction, $"CREATE TABLE {replacement} " + body + ";");
        Execute(connection, transaction, $"INSERT INTO {replacement} SELECT * FROM {table}; DROP TABLE {table}; " +
            $"ALTER TABLE {replacement} RENAME TO {table};");
        foreach (var statement in objects) Execute(connection, transaction, statement);
    }

    /// <summary>Reads the migration rows already recorded in the database.</summary>
    /// <param name="connection">Open connection.</param>
    public static IReadOnlyDictionary<int, (string Name, string Checksum)> ReadAppliedMigrations(
        SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var applied = new Dictionary<int, (string, string)>();
        if (!TableExists(connection, "schema_migrations"))
        {
            return applied;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            applied[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
        }

        return applied;
    }

    private static void VerifyHistory(
        IReadOnlyDictionary<int, (string Name, string Checksum)> applied,
        string? path)
    {
        var known = Scripts.ToDictionary(s => s.Version);

        foreach (var (version, row) in applied)
        {
            if (!known.TryGetValue(version, out var script))
            {
                throw new CollectorException(
                    ErrorCodes.DbIntegrity,
                    path is not null
                        ? CollectorTexts.StartupSchemaTooNew(path)
                        : CollectorTexts.StartupSchemaTooNewNoPath(),
                    new Dictionary<string, object?>
                    {
                        ["database_version"] = version,
                        ["supported_version"] = LatestVersion,
                    });
            }

            if (!string.Equals(script.Name, row.Name, StringComparison.Ordinal) ||
                !string.Equals(script.Checksum, row.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new CollectorException(
                    ErrorCodes.DbIntegrity,
                    path is not null
                        ? CollectorTexts.StartupMigrationTampered(path)
                        : CollectorTexts.StartupMigrationTamperedNoPath(),
                    new Dictionary<string, object?>
                    {
                        ["version"] = version,
                        ["name"] = row.Name,
                    });
            }
        }
    }

    /// <summary>
    /// The file the user is being asked to copy, which the refusal names. Null for a connection with
    /// no file behind it (an in-memory database in tests): the refusal then names none, an empty path
    /// would read worse than none at all.
    /// </summary>
    private static string? DatabasePath(SqliteConnection connection) =>
        string.IsNullOrWhiteSpace(connection.DataSource) ? null : connection.DataSource;

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<MigrationScript> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).GetTypeInfo().Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var name = resource[ResourcePrefix.Length..];
            var separator = name.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 ||
                !int.TryParse(name[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException(
                    $"embedded migration '{name}' does not start with a numeric version");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"cannot open embedded migration '{resource}'");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();

            scripts.Add(new MigrationScript(version, name, sql, ComputeChecksum(sql)));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));

        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    "migration versions must be contiguous and start at 1; found " + scripts[i].Name);
            }
        }

        return scripts;
    }

    private static string ComputeChecksum(string sql)
    {
        // Normalise line endings so a checksum does not depend on how git checked the file out.
        var normalised = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
