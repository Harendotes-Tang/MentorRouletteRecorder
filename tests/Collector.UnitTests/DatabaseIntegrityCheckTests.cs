using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The data page's 完整性校验 asks the Collector,
/// which runs a read-only <c>PRAGMA integrity_check</c> on a connection of its own, never on the
/// gate live capture writes through.
/// </summary>
public sealed class DatabaseIntegrityCheckTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void AHealthyDatabasePassesWithOk()
    {
        var outcome = _database.Database.CheckIntegrity();
        Assert.Equal(new IntegrityCheckOutcome(true, "ok"), outcome);
    }

    [Fact]
    public void TheCheckWritesNothing()
    {
        long Changes() => _database.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT total_changes();";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

        var before = Changes();
        var size = new FileInfo(_database.Path).Length;
        for (var i = 0; i < 3; i++)
        {
            Assert.True(_database.Database.CheckIntegrity().Passed);
        }

        Assert.Equal(before, Changes());
        Assert.Equal(size, new FileInfo(_database.Path).Length);
    }

    [Fact]
    public async Task TheCheckDoesNotWaitForTheWriterGate()
    {
        // Hold the database gate the way a live-capture write does.
        using var gateHeld = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => _database.Database.Read(_ =>
        {
            gateHeld.Set();
            release.Wait();
            return 0;
        }));
        Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            // The scan must finish while the gate is still held: it never asks for it.
            var outcome = await Task.Run(() => _database.Database.CheckIntegrity())
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Passed);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void TheCheckSeesTheLatestCommittedWrite()
    {
        _database.Database.RunInTransaction(transaction =>
        {
            using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE integrity_probe (id INTEGER PRIMARY KEY, text TEXT NOT NULL);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO integrity_probe (text) VALUES ('after open');";
            command.ExecuteNonQuery();
        });

        Assert.Equal(new IntegrityCheckOutcome(true, "ok"), _database.Database.CheckIntegrity());
    }

    [Fact]
    public void ACorruptedFileFailsWithSqlitesOwnSentence()
    {
        // Land every page in the main file, then overwrite the second one underneath the open
        // connection.
        _database.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            return 0;
        });
        using (var file = new FileStream(_database.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(file.Length > 4096 * 2, "expected at least two pages after migration");
            file.Position = 4096;
            var junk = new byte[256];
            Array.Fill(junk, (byte)0xFF);
            file.Write(junk);
        }

        var outcome = _database.Database.CheckIntegrity();

        Assert.False(outcome.Passed);
        Assert.NotEqual("ok", outcome.Detail);
        Assert.NotEmpty(outcome.Detail);
        Assert.InRange(outcome.Detail.Length, 1, SqliteDatabase.MaxIntegrityDetailLength);
        Assert.DoesNotContain('\n', outcome.Detail);
    }

    [Fact]
    public void ALockedFileIsBusyNotBroken()
    {
        var ex = Assert.Throws<CollectorException>(() => _database.Database.CheckIntegrity(
            () => throw new SqliteException("database is locked", 5)));
        Assert.Equal(ErrorCodes.DbBusy, ex.Code);
        Assert.True(ex.Retryable);

        ex = Assert.Throws<CollectorException>(() => _database.Database.CheckIntegrity(
            () => throw new SqliteException("database table is locked", 6)));
        Assert.Equal(ErrorCodes.DbBusy, ex.Code);
    }

    [Fact]
    public void AFileTheCheckCannotOpenIsAFailedCheckNotAnException()
    {
        var outcome = _database.Database.CheckIntegrity(
            () => throw new SqliteException("file is not a database", 26));
        Assert.False(outcome.Passed);
        Assert.Equal("file is not a database", outcome.Detail);
    }

    [Fact]
    public void TheResponseCarriesTheOutcomeAndTheMoment()
    {
        var at = new DateTimeOffset(2026, 9, 16, 21, 38, 4, 123, TimeSpan.Zero);
        var passed = SpeechHandlers.IntegrityResponse(new IntegrityCheckOutcome(true, "ok"), at);
        Assert.True(passed["passed"]!.GetValue<bool>());
        Assert.Equal("ok", passed["detail"]!.GetValue<string>());
        Assert.Equal("2026-09-16T21:38:04.123Z", passed["checked_at_utc"]!.GetValue<string>());
        Assert.Equal(3, passed.Count);
    }

    [Fact]
    public void TheCheckIsAnsweredOffTheConnectionsThread() =>
        Assert.Contains("CheckDatabaseIntegrity", MessageDispatcher.AsynchronousMessageTypes);

    [Fact]
    public void AClosedDatabaseIsNotChecked()
    {
        using var other = new TestDatabase();
        other.Database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => other.Database.CheckIntegrity());
    }

    [Fact]
    public void TheDetailIsCappedAtTwoHundredCharacters()
    {
        Assert.Equal(200, SqliteDatabase.MaxIntegrityDetailLength);

        var detail = SqliteDatabase.ShortDetail(new string('x', 500));
        Assert.Equal(200, detail.Length);

        // Only the first line, trimmed; an empty answer still says something.
        Assert.Equal("*** in database main ***", SqliteDatabase.ShortDetail("  *** in database main ***  \nPage 2: broken\n"));
        Assert.Equal("integrity_check returned nothing", SqliteDatabase.ShortDetail("\n\n"));
    }

    [Fact]
    public void TheDetailIsRedactedLikeALogLine()
    {
        var detail = SqliteDatabase.ShortDetail(@"cannot open C:\Users\someone\mentor_recorder.db");
        Assert.DoesNotContain("someone", detail);
    }
}
