using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-20 H1-5: a copy that fails its own integrity check is never published. These tests make the
/// check fail (the read-only connection that opens the copy refuses), so the guarantee is tested, not assumed.
/// </summary>
public sealed class BackupIntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.BackupIntegrity", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));

    public BackupIntegrityTests() => Directory.CreateDirectory(_directory);

    private string DatabasePath => Path.Combine(_directory, "mentor_recorder.db");
    private string BackupDirectory => Path.Combine(_directory, "backups");

    /// <summary>Opens copies normally, except a staged copy (".mentor-export-*.tmp"), whose check then fails.</summary>
    private static SqliteConnection CopyFailsItsCheck(string path) =>
        Path.GetFileName(path).StartsWith(AtomicExportFile.TemporaryPrefix, StringComparison.Ordinal)
            ? throw new SqliteException("synthetic: the copy is damaged", 11)
            : SqliteDatabase.OpenReadOnlyConnection(path);

    [Fact]
    public void ABackupWhoseCopyFailsItsCheckIsRefusedAndPrunesNothing()
    {
        using (SqliteDatabase.Open(DatabasePath, _clock)) { }
        Directory.CreateDirectory(BackupDirectory);
        var older = Enumerable.Range(0, BackupService.RetainedBackups + 2)
            .Select(index => Path.Combine(BackupDirectory, BackupService.FileNameFor(_clock.UtcNow.AddDays(-30 + index))))
            .ToArray();
        foreach (var file in older) File.WriteAllText(file, "older backup");

        using var database = SqliteDatabase.Open(DatabasePath, _clock, CopyFailsItsCheck);
        var refusal = Assert.Throws<CollectorException>(() => new BackupService(database, _clock).CreateBackup());
        Assert.Equal(ErrorCodes.ExportFailed, refusal.Code);
        Assert.Equal("export.backup_integrity_failed", refusal.Text?.Key);
        Assert.Equal(older.OrderBy(path => path, StringComparer.Ordinal),
            Directory.GetFiles(BackupDirectory).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void AnUpgradeWhoseSnapshotFailsItsCheckDoesNotHappen()
    {
        var previous = MigrationRunner.LatestVersion - 1;
        using (var old = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            old.Open();
            foreach (var script in MigrationRunner.Scripts.Where(script => script.Version <= previous))
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
        }

        Assert.Throws<CollectorException>(() => SqliteDatabase.Open(DatabasePath, _clock, CopyFailsItsCheck).Dispose());
        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadOnly;Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal((long)previous, version.ExecuteScalar());
        Assert.Empty(Directory.GetFiles(BackupDirectory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
