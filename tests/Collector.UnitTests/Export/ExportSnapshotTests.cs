using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>真实 SQLite 快照、写入并发和命名管道导出响应的隔离回归。</summary>
public sealed class ExportSnapshotTests : IDisposable
{
    private readonly TestDatabase _fixture = new();
    private string Target(string name) => Path.Combine(Path.GetDirectoryName(_fixture.Path)!, name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportDoesNotWaitForTheLiveWriterGate(bool csv)
    {
        var runs = new RunRepository(_fixture.Database);
        var original = TestDatabase.Run();
        _fixture.InsertRuns(original);
        var exporter = new RunExporter(runs, _fixture.Clock, _fixture.Database);
        var path = Target(csv ? "unblocked.csv" : "unblocked.json");
        var outcome = await _fixture.WhileTheWriterGateIsHeldAsync(() => csv
            ? exporter.ExportCsv(path, null, overwrite: false)
            : exporter.ExportJson(path, null, overwrite: false));
        Assert.Equal(1, outcome.RowCount);
        Assert.Contains(original.RunId, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRowsAndAttachmentsKeepThePreWriteSnapshotWhileALiveWriteCommits()
    {
        var database = _fixture.Database;
        var runs = new RunRepository(database);
        var reflections = new RunReflectionRepository(database);
        var imports = new RunImportRepository(database);
        var originals = Enumerable.Range(0, 405).Select(index => TestDatabase.Run(
            source: RunSource.Import, enteredAt: _fixture.Clock.UtcNow.AddMinutes(index))).ToArray();
        var victim = originals[0]; // The oldest record is hydrated in the final batch.
        database.RunInTransaction(tx =>
        {
            foreach (var run in originals) runs.Insert(run, tx);
            reflections.Upsert(victim.RunId, ReflectionMood.Good, "before-write", _fixture.Clock.UtcNow, tx);
            imports.Insert(victim.RunId, new RunImportMetadata("SCREENSHOT", "before-import", "2026-09-04",
                null, _fixture.Clock.UtcNow, Guid.NewGuid().ToString("N"), true), tx);
        });
        var inserted = TestDatabase.Run(source: RunSource.Import, enteredAt: _fixture.Clock.UtcNow.AddDays(1));
        var filter = new RunFilter { Sources = new[] { RunSource.Import } };

        var exported = database.ReadSnapshot((snapshot, tx) =>
        {
            var reader = new RunRepository(snapshot);
            var rows = new List<MentorRun>();
            var count = reader.VisitExportRows(filter, RunSort.Default, tx, restrictToSelection: false, row =>
            {
                rows.Add(row);
                if (rows.Count != Paging.MaxPageSize) return;

                // Commit through the live-capture writer while the export cursor is still
                // active, before fetching the later batch with the old record and attachments.
                Task.Run(() => database.RunLiveCaptureTransaction(write =>
                {
                    runs.Update(victim with { Revision = 2, SoftDeleted = true }, 1, write);
                    reflections.Upsert(victim.RunId, ReflectionMood.Good, "after-write", _fixture.Clock.UtcNow, write);
                    using var command = database.CreateCommand();
                    command.Transaction = write;
                    command.CommandText = "UPDATE run_import_metadata SET source_name = 'after-import' WHERE run_id = $id;";
                    command.Parameters.AddWithValue("$id", victim.RunId);
                    command.ExecuteNonQuery();
                    runs.Insert(inserted, write);
                })).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }, CancellationToken.None);
            Assert.Equal(originals.Length, count);
            return rows;
        });

        Assert.Equal(Enumerable.Reverse(originals).Select(run => run.RunId), exported.Select(run => run.RunId));
        Assert.Equal(405, exported.Select(run => run.RunId).Distinct().Count());
        var oldVictim = Assert.Single(exported, run => run.RunId == victim.RunId);
        Assert.Equal(1, oldVictim.Revision);
        Assert.Equal("before-write", oldVictim.Reflection!.Text);
        Assert.Equal("before-import", oldVictim.ImportMetadata!.SourceName);
        Assert.True(runs.Get(victim.RunId)!.SoftDeleted);
        Assert.Equal("after-write", runs.Get(victim.RunId)!.Reflection!.Text);
        Assert.NotNull(runs.Get(inserted.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportKeepsFiltersOrderAttachmentsAndSelectedIdFailure(bool csv)
    {
        var runs = new RunRepository(_fixture.Database);
        var originals = Enumerable.Range(0, 207).Select(index => TestDatabase.Run(
            source: RunSource.Import, enteredAt: _fixture.Clock.UtcNow.AddMinutes(index)) with { Note = "keep" }).ToArray();
        _fixture.Database.RunInTransaction(tx =>
        {
            foreach (var run in originals) runs.Insert(run, tx);
            runs.Insert(TestDatabase.Run() with { Note = "keep" }, tx);
            runs.Insert(TestDatabase.Run(source: RunSource.Import) with { Note = "other" }, tx);
            runs.Insert(TestDatabase.Run(source: RunSource.Import, softDeleted: true) with { Note = "keep" }, tx);
            new RunReflectionRepository(_fixture.Database).Upsert(originals[0].RunId,
                ReflectionMood.Good, "snapshot-reflection", _fixture.Clock.UtcNow, tx);
            new RunImportRepository(_fixture.Database).Insert(originals[0].RunId,
                new RunImportMetadata("SCREENSHOT", "snapshot-import", "2026-09-04", null,
                    _fixture.Clock.UtcNow, Guid.NewGuid().ToString("N"), true), tx);
        });
        // Also exercise the compatibility constructor without an explicit database argument.
        var exporter = new RunExporter(runs, _fixture.Clock);
        var filter = new RunFilter { Sources = new[] { RunSource.Import }, Text = "keep" };
        var path = Target(csv ? "filtered.csv" : "filtered.json");
        var outcome = csv ? exporter.ExportCsv(path, filter, false) : exporter.ExportJson(path, filter, false);
        Assert.Equal(207, outcome.RowCount);
        var content = File.ReadAllText(path);
        Assert.Contains("snapshot-reflection", content, StringComparison.Ordinal);
        if (!csv)
        {
            var array = JsonNode.Parse(content)!.AsArray();
            var expected = runs.Query(filter, RunSort.Default, 1, 200).Items
                .Concat(runs.Query(filter, RunSort.Default, 2, 200).Items).ToArray();
            Assert.Equal(expected.Select(run => Wire.Run(run).ToJsonString()), array.Select(row => row!.ToJsonString()));
            var expectedFile = new JsonArray(expected.Select(run => (JsonNode?)Wire.Run(run)).ToArray());
            Assert.Equal(Encoding.UTF8.GetBytes(expectedFile.ToJsonString(Wire.IndentedJsonOptions)), File.ReadAllBytes(path));
            Assert.Contains("snapshot-import", content, StringComparison.Ordinal);
        }
        else
        {
            var positions = Enumerable.Reverse(originals).Select(run => content.IndexOf(run.RunId, StringComparison.Ordinal)).ToArray();
            Assert.All(positions, position => Assert.True(position >= 0));
            Assert.Equal(positions.Order(), positions);
        }

        var selected = filter with { RunIds = new[] { originals[0].RunId, Guid.NewGuid().ToString("D") } };
        var before = File.ReadAllBytes(path);
        var error = Assert.Throws<CollectorException>(() => csv
            ? exporter.ExportCsv(path, selected, true)
            : exporter.ExportJson(path, selected, true));
        Assert.Equal(ErrorCodes.NotFound, error.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, AtomicExportFile.TemporaryPrefix + "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledSnapshotLeavesTheExistingFileAndRemovesTheTemporaryExport(bool csv)
    {
        var target = Target(csv ? "cancel.csv" : "cancel.json");
        File.WriteAllText(target, "previous export");
        _fixture.Database.Dispose();
        using var cancellation = new CancellationTokenSource();
        using var database = SqliteDatabase.Open(_fixture.Path, _fixture.Clock, path =>
        {
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!, AtomicExportFile.TemporaryPrefix + "*.tmp"));
            cancellation.Cancel();
            return SqliteDatabase.OpenReadOnlyConnection(path);
        });
        var exporter = new RunExporter(new RunRepository(database), _fixture.Clock);

        Assert.Throws<OperationCanceledException>(() => csv
            ? exporter.ExportCsv(target, null, true, null, cancellation.Token)
            : exporter.ExportJson(target, null, true, null, cancellation.Token));
        Assert.Equal("previous export", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, AtomicExportFile.TemporaryPrefix + "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPipeServicesOrdinaryRequestsWhileAnExportIsReadingAndRetainsErrors(bool csv)
    {
        _fixture.Database.Dispose();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var openings = 0;
        using var database = SqliteDatabase.Open(_fixture.Path, _fixture.Clock, path =>
        {
            var connection = SqliteDatabase.OpenReadOnlyConnection(path);
            Interlocked.Increment(ref openings);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                connection.Dispose();
                throw new TimeoutException("The test did not release its snapshot reader.");
            }
            return connection;
        });
        var runs = new RunRepository(database);
        database.RunInTransaction(tx => runs.Insert(TestDatabase.Run(), tx));
        var exporter = new RunExporter(runs, _fixture.Clock, database);
        using var host = CollectorHost.Open(Target("pipe-host.db"), _fixture.Clock, capture: InertCapture.Services(),
            speechClient: new OnlineSpeechClient((_, _) => throw new InvalidOperationException("test transport is offline")),
            updateCheckClient: new UpdateCheckClient((_, _) => throw new InvalidOperationException("test transport is offline"),
                readEnvironment: _ => null));
        var pipeName = "MentorRecorder.export." + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(new MessageDispatcher(host, exporter), pipeName);
        using var stopping = new CancellationTokenSource();
        var serving = server.RunAsync(stopping.Token);
        await using var client = new PipeClient(pipeName);
        Task<IpcResponse>? export = null;
        Task<IpcResponse>? queued = null;
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(10));
            var message = csv ? "ExportCsv" : "ExportJson";
            var target = Target(csv ? "pipe.csv" : "pipe.json");
            export = client.SendAsync(message, new JsonObject { ["target_path"] = target });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            queued = client.SendAsync(message, new JsonObject { ["target_path"] = Target("queued-export") });
            Assert.True((await client.SendAsync("GetVersion", timeout: TimeSpan.FromSeconds(5))).Ok);
            Assert.True((await client.SendAsync("GetStatus", timeout: TimeSpan.FromSeconds(5))).Ok);
            Assert.False(export.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref openings));

            release.Set();
            Assert.Equal(1, (await export.WaitAsync(TimeSpan.FromSeconds(10))).Require()["row_count"]!.GetValue<int>());
            Assert.True((await queued.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            var before = File.ReadAllBytes(target);
            var failure = await client.SendAsync(message, new JsonObject
            {
                ["target_path"] = target, ["overwrite"] = true,
                ["filter"] = new JsonObject { ["run_id"] = new JsonArray(Guid.NewGuid().ToString("D")) },
            });
            Assert.False(failure.Ok);
            Assert.Equal(message, failure.MessageType);
            Assert.Equal(ErrorCodes.NotFound, failure.ErrorCode);
            Assert.Equal(before, File.ReadAllBytes(target));
            Assert.True((await client.SendAsync("GetVersion")).Ok);
        }
        finally
        {
            release.Set();
            if (export is not null) await export.WaitAsync(TimeSpan.FromSeconds(10));
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(10));
            await stopping.CancelAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _fixture.Dispose();
}
