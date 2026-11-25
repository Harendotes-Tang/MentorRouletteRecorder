using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="BackupService"/>: where a backup is written, which older backups retention removes and
/// where it may not, and what is left when the copy cannot be made or verified.
///
/// Audit 2026-10-20 H1-5: a copy that fails its own integrity check is never published. These tests make the
/// check fail (the read-only connection that opens the copy refuses), so the guarantee is tested, not assumed.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    // A checkout on another drive must support real backups outside the user profile.
    private readonly TempDirectory _directory = new("BackupService", parent: AppContext.BaseDirectory);

    // The integrity tests' own database and its backups stay in %TEMP%, where their debris is cleaned.
    private readonly TempDirectory _integrity = new("BackupIntegrity");

    /// <summary>A database of the tests' own, for the ones that open it with a seam.</summary>
    private string DatabasePath => _integrity.Combine("mentor_recorder.db");
    private string BackupDirectory => _integrity.Combine("backups");
    private string DatabaseFolder => Path.GetDirectoryName(_database.Path)!;

    /// <summary>Opens copies normally, except a staged copy (".mentor-export-*.tmp"), whose check then fails.</summary>
    private static SqliteConnection CopyFailsItsCheck(string path) =>
        Path.GetFileName(path).StartsWith(AtomicExportFile.TemporaryPrefix, StringComparison.Ordinal)
            ? throw new SqliteException("synthetic: the copy is damaged", 11)
            : SqliteDatabase.OpenReadOnlyConnection(path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("directory")]
    [InlineData("file")]
    public void BackupDatabase_ResolvesDefaultDirectoryAndExplicitFileTargets(string? targetKind)
    {
        var service = new BackupService(_database.Database, _database.Clock);
        var fileName = BackupService.FileNameFor(_database.Clock.UtcNow);
        var target = targetKind switch
        {
            "directory" => _directory.Path,
            "file" => _directory.Combine("chosen.db"),
            _ => targetKind,
        };
        var expected = targetKind switch
        {
            "directory" => _directory.Combine(fileName),
            "file" => _directory.Combine("chosen.db"),
            _ => Path.Combine(service.DefaultDirectory, fileName),
        };

        var result = service.CreateBackup(target);

        Assert.Equal(expected, result.TargetPath);
        Assert.True(File.Exists(expected));
        Assert.True(result.IntegrityCheckPassed);
    }

    [Fact]
    public void BackupDatabase_WritesAVerifiedCopyAndKeepsTheNewestFourteen()
    {
        var service = new BackupService(_database.Database, _database.Clock);

        // Sixteen older files plus the one this call writes must leave exactly fourteen.
        SeedOlderBackups(service.DefaultDirectory, 16, _database.Clock.UtcNow);

        var result = service.CreateBackup();

        Assert.True(File.Exists(result.TargetPath));
        Assert.True(result.ByteCount > 0);
        Assert.True(result.PrunedCount > 0);
        Assert.Equal(
            BackupService.RetainedBackups,
            Directory.GetFiles(service.DefaultDirectory, BackupService.FileNamePrefix + "*.db").Length);
    }

    [Fact]
    public void RetentionKeepsThePublishedBackupWhenTheClockGoesBack()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        var backups = SeedOlderBackups(service.DefaultDirectory, BackupService.RetainedBackups, _database.Clock.UtcNow);
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddDays(-BackupService.RetainedBackups - 1);

        var result = service.CreateBackup();

        Assert.True(result.IntegrityCheckPassed);
        Assert.Equal(1, result.PrunedCount);
        Assert.Equal(result.ByteCount, new FileInfo(result.TargetPath).Length);
        Assert.Equal(backups.SkipLast(1).Append(result.TargetPath).Order(),
            Directory.GetFiles(service.DefaultDirectory).Order());
    }

    [Fact]
    public void ACustomFileInTheManagedFolderDoesNotTakeAnAutomaticRetentionSlot()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        var backups = SeedOlderBackups(service.DefaultDirectory, BackupService.RetainedBackups, _database.Clock.UtcNow);
        var target = Path.Combine(service.DefaultDirectory,
            Path.ChangeExtension(BackupService.FileNameFor(_database.Clock.UtcNow), ".snapshot"));

        var result = service.CreateBackup(target);

        Assert.Equal(0, result.PrunedCount);
        Assert.Equal(backups.Append(target).Order(), Directory.GetFiles(service.DefaultDirectory).Order());
    }

    // ---- M6: backup retention stays inside the managed folder ----------------------
    // A retention sweep in a folder the user chose would destroy something the user cannot get
    // back (review finding M6).

    [Fact]
    public void RetentionNeverSweepsAFolderTheUserChose()
    {
        var chosen = _directory.Combine("chosen");
        SeedOlderBackups(chosen, 20, _database.Clock.UtcNow);

        var service = new BackupService(_database.Database, _database.Clock);
        var result = service.CreateBackup(chosen);

        Assert.Equal(0, result.PrunedCount);
        Assert.Equal(21, Directory.GetFiles(chosen, BackupService.FileNamePrefix + "*.db").Length);
    }

    /// <summary>
    /// Review finding L-18. An atomic write stages beside its target and deletes on the way
    /// out, but a process that is terminated rather than stopped never gets there, so the
    /// managed folder must sweep abandoned staging files, each the size of the database.
    /// </summary>
    [Fact]
    public void AnAbandonedStagingFileIsSweptFromTheManagedFolderOnly()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        Directory.CreateDirectory(service.DefaultDirectory);
        var stale = StageFile(service.DefaultDirectory, _database.Clock.UtcNow.AddHours(-2));
        var fresh = StageFile(service.DefaultDirectory, _database.Clock.UtcNow.AddMinutes(-5));

        var chosen = _directory.Combine("chosen-temp");
        Directory.CreateDirectory(chosen);
        var untouched = StageFile(chosen, _database.Clock.UtcNow.AddDays(-3));

        service.CreateBackup();

        Assert.False(File.Exists(stale));

        // A staging file younger than the grace period may still belong to a live export.
        Assert.True(File.Exists(fresh));

        // A folder the user chose is never swept, for the same reason retention is not.
        Assert.True(File.Exists(untouched));
    }

    // ---- a copy that cannot be made or verified ------------------------------------

    [Fact]
    public void ABackupWhoseCopyFailsItsCheckIsRefusedAndPrunesNothing()
    {
        using (SqliteDatabase.Open(DatabasePath, _database.Clock)) { }
        var older = SeedOlderBackups(BackupDirectory, BackupService.RetainedBackups + 2, _database.Clock.UtcNow);

        using var database = SqliteDatabase.Open(DatabasePath, _database.Clock, CopyFailsItsCheck);
        var refusal = Assert.Throws<CollectorException>(() => new BackupService(database, _database.Clock).CreateBackup());
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
            LegacySchema.ApplyThrough(old, previous, _database.Clock.UtcNow);
        }

        var refusal = Assert.Throws<CollectorException>(
            () => SqliteDatabase.Open(DatabasePath, _database.Clock, CopyFailsItsCheck).Dispose());
        // A refusal to start, worded as one: the copy an upgrade needs failed, and the file to save is named
        // (audit 2026-11-03 H1-3) - not an export's or a backup's message.
        Assert.Equal(ErrorCodes.DbIntegrity, refusal.Code);
        Assert.Equal("startup.pre_upgrade_copy_failed", refusal.Text?.Key);
        Assert.Contains("无法启动", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(DatabasePath), refusal.Message, StringComparison.Ordinal);
        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadOnly;Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal((long)previous, version.ExecuteScalar());
        Assert.Empty(Directory.GetFiles(BackupDirectory));
    }

    [Fact]
    public void BackupWriteFailurePreservesVerifiedCopyAndDoesNotPruneHistory()
    {
        // The copy reads on a connection of its own (audit 2026-10-03, OG-7), which WAL never
        // blocks and the shared connection's query_only - this test's old injection - no longer
        // reaches. The failure is injected where the copy now reads.
        var failing = false;
        _database.Database.Dispose();
        using var database = SqliteDatabase.Open(_database.Path, _database.Clock, path => failing
            ? throw new SqliteException("disk I/O error", 10)
            : SqliteDatabase.OpenReadOnlyConnection(path));
        var service = new BackupService(database, _database.Clock);
        var target = Path.Combine(service.DefaultDirectory, "previous-verified.db");
        service.CreateBackup(target);
        var previous = File.ReadAllBytes(target);
        SeedOlderBackups(service.DefaultDirectory, BackupService.RetainedBackups + 1, _database.Clock.UtcNow);
        var before = Directory.GetFiles(service.DefaultDirectory).Order().ToArray();

        failing = true;
        var error = Assert.Throws<CollectorException>(() => service.CreateBackup(target, overwrite: true));
        Assert.Equal(ErrorCodes.ExportFailed, error.Code);

        Assert.Equal(previous, File.ReadAllBytes(target));
        Assert.Equal(before, Directory.GetFiles(service.DefaultDirectory).Order().ToArray());
        AssertNoTemporaryFiles();
    }

    /// <summary>
    /// Audit 2026-10-03 OG-7. A backup copies the whole file and used to do it under the gate every
    /// live-capture write waits on, with no timeout: a backup during a duty stalled the parser
    /// thread behind it. The copy now runs on a connection of its own, like the integrity check,
    /// and still sees every committed write.
    /// </summary>
    [Fact]
    public async Task ABackupDoesNotWaitForTheWriterGate()
    {
        var runs = new RunRepository(_database.Database);
        var committed = TestDatabase.Run();
        _database.Database.RunInTransaction(transaction => runs.Insert(committed, transaction));

        var target = Path.Combine(DatabaseFolder, "while-capture-writes.db");
        await _database.WhileTheWriterGateIsHeldAsync(() => _database.Database.BackupDatabase(target, overwrite: false));

        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        copy.Open();
        using var command = copy.CreateCommand();
        command.CommandText = "SELECT run_id FROM mentor_runs;";
        Assert.Equal(committed.RunId, command.ExecuteScalar() as string);
        AssertNoTemporaryFiles();
    }

    /// <summary>
    /// Writes <paramref name="count"/> automatically named backups, one a day before
    /// <paramref name="before"/>, and returns their paths.
    /// </summary>
    private static string[] SeedOlderBackups(string directory, int count, DateTimeOffset before)
    {
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, count)
            .Select(index => Path.Combine(directory, BackupService.FileNameFor(before.AddDays(-(index + 1)))))
            .ToArray();
        foreach (var path in paths)
        {
            File.WriteAllText(path, "older backup");
        }

        return paths;
    }

    private static string StageFile(string directory, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(
            directory, BackupService.TemporaryFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "abandoned");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(DatabaseFolder, ".mentor-export-*.tmp", SearchOption.AllDirectories));

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _integrity.Dispose();
        // Test debris under AppContext.BaseDirectory; a locked file is left for the next run to remove.
        _directory.Dispose();
    }
}
