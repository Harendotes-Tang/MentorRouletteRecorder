using System.Text;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>回归检查：写入失败时保留原文件、禁止覆盖模式下的并发提交、真实文件锁冲突与超大页码查询。</summary>
public sealed class StorageExportAtomicityTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private string DirectoryPath => Path.GetDirectoryName(_database.Path)!;
    private string Target(string name) => Path.Combine(DirectoryPath, name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialWriteFailureDoesNotPublishOrDamageAnExistingFile(bool overwrite)
    {
        var target = Target("partial.json");
        if (overwrite) File.WriteAllText(target, "existing user data");

        Assert.Throws<IOException>(() => AtomicExportFile.Write(target, overwrite, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial replacement"));
            throw new IOException("Injected disk write failure.");
        }));

        if (overwrite) Assert.Equal("existing user data", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedIntegrityCheckDoesNotPublishTheReplacement()
    {
        var target = Target("verified.db");
        File.WriteAllText(target, "previous verified copy");
        using (var pending = new AtomicExportFile(target, overwrite: true))
        {
            File.WriteAllText(pending.TemporaryPath, "invalid replacement");
            var error = Assert.Throws<CollectorException>(() => pending.Commit(_ => false));
            Assert.Equal(ErrorCodes.ExportFailed, error.Code);
            Assert.Equal("previous verified copy", File.ReadAllText(target));
        }
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentNoOverwriteExportsPublishExactlyOneCompleteFile()
    {
        var target = Target("concurrent.json");
        using var ready = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Export(string value)
        {
            using var pending = new AtomicExportFile(target, overwrite: false);
            File.WriteAllText(pending.TemporaryPath, value);
            ready.Signal();
            await release.Task;
            try { pending.Commit(); return true; }
            catch (IOException) { return false; }
        }

        var first = Export("first complete file");
        var second = Export("second complete file");
        Assert.True(ready.IsSet);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, success => success);
        Assert.Contains(File.ReadAllText(target), new[] { "first complete file", "second complete file" });
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedReplacementPreservesExistingFileAndCleansTemporaryFile()
    {
        var target = Target("locked.json");
        File.WriteAllText(target, "original content");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => AtomicExportFile.Write(target, overwrite: true,
                stream => stream.Write(Encoding.UTF8.GetBytes("replacement"))));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected a file-system refusal, received {error?.GetType().Name ?? "no error"}.");
        }
        Assert.Equal("original content", File.ReadAllText(target));
        AssertNoTemporaryFiles();
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
        for (var index = 0; index < BackupService.RetainedBackups + 1; index++)
        {
            File.WriteAllText(Path.Combine(service.DefaultDirectory,
                BackupService.FileNameFor(_database.Clock.UtcNow.AddDays(-index - 1))), "older backup");
        }
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

        using var gateHeld = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => _database.Database.Read(_ =>
        {
            gateHeld.Set();
            release.Wait();
            return 0;
        }));
        Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(10)));

        var target = Target("while-capture-writes.db");
        try
        {
            var outcome = await Task.Run(() => _database.Database.BackupDatabase(target, overwrite: false))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.IntegrityCheckPassed);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }

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

    [Fact]
    public void BusyAtTransactionStartUsesTheRetryableContractError()
    {
        _database.Database.Read(connection => { connection.DefaultTimeout = 1; return 0; });
        using var other = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ConnectionString);
        other.Open();
        var workCalled = false;
        using (var transaction = other.BeginTransaction())
        {
            var error = Assert.Throws<CollectorException>(() =>
                _database.Database.RunInTransaction(_ => { workCalled = true; }));
            Assert.Equal(ErrorCodes.DbBusy, error.Code);
            Assert.True(error.Retryable);
            Assert.False(workCalled);
            Assert.IsType<SqliteException>(error.InnerException);
        }
        _database.Database.RunInTransaction(_ => { workCalled = true; });
        Assert.True(workCalled);
    }

    [Fact]
    public void NonSqliteFailureStillRollsBackTheWholeTransaction()
    {
        var runs = new RunRepository(_database.Database);
        Assert.Throws<InvalidOperationException>(() => _database.Database.RunInTransaction(transaction =>
        {
            runs.Insert(TestDatabase.Run(), transaction);
            throw new InvalidOperationException("Injected failure after the write.");
        }));

        Assert.Equal(0, runs.Query(null, null, 1, 50).Total);
    }

    [Fact]
    public void HighestAcceptedPageIsEmptyForBothRunAndRevisionQueries()
    {
        var settings = new SettingsRepository(_database.Database, _database.Clock);
        settings.EnsureDefaults();
        var mutations = new RunMutationService(_database.Database, settings, _database.Clock);
        var created = mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Reason = "分页回归检查",
            Result = RunResult.CancelledBeforeEntry,
        });
        var runs = new RunRepository(_database.Database).Query(null, null, int.MaxValue, 200);
        var revisions = new RunRevisionRepository(_database.Database)
            .ListForRun(created.RunId, int.MaxValue, 200);

        Assert.Equal(1, runs.Total);
        Assert.Empty(runs.Items);
        Assert.Equal(1, revisions.Total);
        Assert.Empty(revisions.Items);
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(DirectoryPath, ".mentor-export-*.tmp", SearchOption.AllDirectories));

    public void Dispose() => _database.Dispose();
}
