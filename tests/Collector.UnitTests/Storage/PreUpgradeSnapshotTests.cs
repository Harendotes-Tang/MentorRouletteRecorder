using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-20 H1-2: every schema upgrade keeps a verified copy of the file as it was, outside the
/// rolling backups, so a user can return to the previous version without losing the last session's runs.
/// </summary>
public sealed class PreUpgradeSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.PreUpgrade", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));

    private string DatabasePath => Path.Combine(_directory, "mentor_recorder.db");
    private string BackupDirectory => Path.Combine(_directory, "backups");

    /// <summary>A database file as a build of schema <paramref name="version"/> left it, with one run.</summary>
    private void CreateAtVersion(int version)
    {
        Directory.CreateDirectory(_directory);
        using var old = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        old.Open();
        foreach (var script in MigrationRunner.Scripts.Where(script => script.Version <= version))
        {
            using var command = old.CreateCommand();
            command.CommandText = script.Sql;
            command.Parameters.AddWithValue("$migration_now_utc", UtcTimestamp.ToText(_clock.UtcNow));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO schema_migrations VALUES($version,$name,$sum,$now);";
            command.Parameters.AddWithValue("$version", script.Version);
            command.Parameters.AddWithValue("$name", script.Name);
            command.Parameters.AddWithValue("$sum", script.Checksum);
            command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(_clock.UtcNow));
            command.ExecuteNonQuery();
        }
        using var seed = old.CreateCommand();
        seed.CommandText = "INSERT INTO mentor_runs(run_id,revision,result,source,soft_deleted,created_at_utc,updated_at_utc) " +
            "VALUES('5f0c7a52-56a7-4c43-8d0b-6a3f4b8e2b11',1,'COMPLETED','MANUAL',0,'2026-10-20T12:00:00.000Z','2026-10-20T12:00:00.000Z');";
        seed.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void UpgradingTheLastSchemaKeepsAVerifiedCopyOfTheFileAsItWas()
    {
        var previous = MigrationRunner.LatestVersion - 1;
        CreateAtVersion(previous);
        using (var database = SqliteDatabase.Open(DatabasePath, _clock))
            Assert.Equal(MigrationRunner.LatestVersion, database.SchemaVersion);
        var snapshot = Assert.Single(Directory.GetFiles(BackupDirectory, "pre_schema_*_upgrade_*.db"));
        Assert.StartsWith("pre_schema_" + previous + "_upgrade_20261021_080000_", Path.GetFileName(snapshot), StringComparison.Ordinal);
        Assert.Equal(previous, Scalar(snapshot, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, Scalar(snapshot, "SELECT COUNT(*) FROM mentor_runs;"));
        Assert.Empty(Directory.GetFiles(BackupDirectory, "mentor_*.db"));
    }

    /// <summary>
    /// Audit 2026-11-03 H1-2: an upgrade that fails leaves the file as it was, so the copy taken for it is removed again.
    /// A failure that repeats on every start (the Desktop retries a refused start every five minutes) therefore never
    /// piles up full copies of the database; the upgrade that finally succeeds keeps exactly one.
    /// </summary>
    [Fact]
    public void RepeatedFailedUpgradesKeepNoCopiesAndTheSuccessfulOneKeepsOne()
    {
        CreateAtVersion(11);
        using (var orphan = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            orphan.Open();
            using var command = orphan.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF; " +
                "INSERT INTO run_events(event_id,run_id,sequence,occurred_at_utc,monotonic_offset_ms,event_type) " +
                "VALUES ('orphan','missing',0,'2026-10-20T12:00:00.000Z',0,'test');";
            command.ExecuteNonQuery();
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
            var refusal = Assert.Throws<CollectorException>(() => SqliteDatabase.Open(DatabasePath, _clock).Dispose());
            Assert.Contains(Path.GetFileName(DatabasePath), refusal.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(BackupDirectory, "pre_schema_*_upgrade_*.db"));
        Assert.Empty(Directory.GetFiles(BackupDirectory, AtomicExportFile.TemporaryPrefix + "*"));
        Assert.Equal(11, Scalar(DatabasePath, "SELECT MAX(version) FROM schema_migrations;"));

        using (var repair = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            repair.Open();
            using var command = repair.CreateCommand();
            command.CommandText = "DELETE FROM run_events WHERE event_id = 'orphan';";
            command.ExecuteNonQuery();
        }

        using (var database = SqliteDatabase.Open(DatabasePath, _clock))
            Assert.Equal(MigrationRunner.LatestVersion, database.SchemaVersion);
        var snapshot = Assert.Single(Directory.GetFiles(BackupDirectory, "pre_schema_*_upgrade_*.db"));
        Assert.StartsWith("pre_schema_11_upgrade_", Path.GetFileName(snapshot), StringComparison.Ordinal);
        Assert.Equal(11, Scalar(snapshot, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public void OpeningAnUpToDateOrNewFileTakesNoCopy()
    {
        using (SqliteDatabase.Open(DatabasePath, _clock)) { }
        using (SqliteDatabase.Open(DatabasePath, _clock)) { }
        Assert.False(Directory.Exists(BackupDirectory) && Directory.EnumerateFiles(BackupDirectory).Any());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
