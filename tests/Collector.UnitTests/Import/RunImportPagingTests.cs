using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunImportPagingTests
{
    private static JsonObject History(int index, int reflectionLength = 0) => new()
    {
        ["duty_name"] = "分页合成副本 " + index,
        ["job_name"] = "战士", ["result"] = "COMPLETED", ["source_recorded_at"] = "2022-09-28",
        ["reflection_text"] = reflectionLength == 0 ? "分页合成心得 " + index : new string('中', reflectionLength),
    };

    private static JsonArray Rows(int count, int reflectionLength = 0) =>
        new(Enumerable.Range(1, count).Select(index => (JsonNode)History(index, reflectionLength)).ToArray());

    private static string Id(JsonObject preview) => preview["preview_id"]!.GetValue<string>();

    private static long Count(TestDatabase fixture, string table = "mentor_runs")
    {
        using var command = fixture.Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void TwoThousandAndNinetyRowsAreFrozenBoundedAndCommitAsOneBatch()
    {
        using var fixture = new TestDatabase();
        new SettingsRepository(fixture.Database, fixture.Clock).EnsureDefaults();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = service.PreviewSource("XLSX", rows: Rows(2090), sourceName: "synthetic-2090", pageSize: 100);
        var selected = new List<int>();
        var page = first;
        for (var number = 1; selected.Count < 2090; number++)
        {
            if (number > 1) page = service.PreviewPage(Id(first), number);
            Assert.Equal(number, page["page_info"]!["page"]!.GetValue<int>());
            Assert.Equal(100, page["page_info"]!["page_size"]!.GetValue<int>());
            Assert.Equal(2090, page["page_info"]!["total"]!.GetValue<int>());
            Assert.Equal(first["summary"]!.ToJsonString(), page["summary"]!.ToJsonString());
            Assert.Equal("synthetic-2090", page["source_name"]!.GetValue<string>());
            Assert.InRange(page["rows"]!.AsArray().Count, 1, 100);
            Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString(Wire.JsonOptions)), 1, FrameCodec.MaxFrameBytes * 3 / 4);
            selected.AddRange(page["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()));
        }
        Assert.Equal(Enumerable.Range(1, 2090), selected);
        Assert.Equal(0, Count(fixture));
        var requestId = Guid.NewGuid().ToString("D");
        var result = service.Commit(Id(first), selected, true, requestId);
        Assert.Equal(2090, result["imported_count"]!.GetValue<int>());
        Assert.Equal(2090, Count(fixture));
        Assert.Equal(1, Count(fixture, "run_import_batches"));
        Assert.True(service.Commit(Id(first), selected, true, requestId)["replayed"]!.GetValue<bool>());
    }

    [Fact]
    public void CrossPageDuplicatesAndDatabaseChangesDoNotReclassifyTheFrozenSnapshot()
    {
        using var fixture = new TestDatabase();
        new SettingsRepository(fixture.Database, fixture.Clock).EnsureDefaults();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var stableId = Guid.NewGuid().ToString("D");
        var rows = Rows(102);
        rows[0]!["run_id"] = stableId;
        rows[100] = rows[0]!.DeepClone();
        rows[101]!["run_id"] = Guid.NewGuid().ToString("D");
        var first = service.PreviewSource("ROWS", rows: rows, pageSize: 100);
        Assert.Equal(1, first["summary"]!["duplicate"]!.GetValue<int>());
        var other = service.PreviewSource("ROWS", rows: new JsonArray(rows[101]!.DeepClone()));
        service.Commit(Id(other), new[] { 1 }, true, Guid.NewGuid().ToString("D"));
        var second = service.PreviewPage(Id(first), 2);
        Assert.Equal("duplicate", second["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(stableId, second["rows"]![0]!["matching_run_id"]!.GetValue<string>());
        Assert.Equal("new", second["rows"]![1]!["status"]!.GetValue<string>());
        Assert.Equal(first["summary"]!.ToJsonString(), second["summary"]!.ToJsonString());
        // Mutating a returned tree must not alter the session's immutable copy.
        second["rows"]![1]!["status"] = "invalid";
        Assert.Equal("new", service.PreviewPage(Id(first), 2)["rows"]![1]!["status"]!.GetValue<string>());
        var rechecked = service.Commit(Id(first), new[] { 1, 101, 102 }, true, Guid.NewGuid().ToString("D"));
        Assert.Equal(1, rechecked["imported_count"]!.GetValue<int>());
        Assert.Equal(2, rechecked["duplicate_count"]!.GetValue<int>());
    }

    [Fact]
    public void LegacyPreviewRemainsCompleteAndKeepsItsExistingCapacityRefusal()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: Rows(102));
        Assert.False(preview.ContainsKey("page_info"));
        Assert.Equal(102, preview["rows"]!.AsArray().Count);
        Assert.Throws<CollectorException>(() => service.PreviewSource("ROWS", rows: Rows(2090)));
        Assert.Throws<CollectorException>(() => service.PreviewPage(Id(preview), 1));
    }

    [Fact]
    public void ExpiryBoundsEmptyPagesAndCountEvictionAreExplicit()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: Rows(2), pageSize: 1);
        foreach (var page in new[] { int.MinValue, 0, 3, int.MaxValue })
            Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() => service.PreviewPage(Id(preview), page)).Code);
        Assert.Throws<CollectorException>(() => service.PreviewPage("not-a-uuid", 1));
        foreach (var pageSize in new[] { 0, 101 })
            Assert.Throws<CollectorException>(() => service.PreviewSource("ROWS", rows: Rows(1), pageSize: pageSize));
        var empty = service.PreviewSource("ROWS", rows: new JsonArray(), pageSize: 100);
        Assert.Empty(service.PreviewPage(Id(empty), 1)["rows"]!.AsArray());
        Assert.Throws<CollectorException>(() => service.PreviewPage(Id(empty), 2));
        fixture.Clock.UtcNow += RunImportService.PreviewLifetime + TimeSpan.FromMilliseconds(1);
        Assert.Throws<CollectorException>(() => service.PreviewPage(Id(preview), 1));
        var oldest = service.PreviewSource("ROWS", rows: Rows(1), pageSize: 1);
        for (var index = 0; index < RunImportService.MaxPreviews; index++)
        {
            fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
            service.PreviewSource("ROWS", rows: Rows(1), pageSize: 1);
        }
        Assert.Throws<CollectorException>(() => service.PreviewPage(Id(oldest), 1));
    }

    [Fact]
    public void PageSizeShrinksWithoutDroppingLongCandidateFields()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = Rows(100);
        foreach (var row in rows)
            for (var field = 0; field < 10; field++) row!["preserved_" + field] = new string('中', 1000);
        var preview = service.PreviewSource("ROWS", rows: rows, pageSize: 100);
        var size = preview["page_info"]!["page_size"]!.GetValue<int>();
        Assert.InRange(size, 1, 99);
        for (var number = 1; number <= (100 + size - 1) / size; number++)
        {
            var page = service.PreviewPage(Id(preview), number);
            Assert.True(Encoding.UTF8.GetByteCount(page.ToJsonString(Wire.JsonOptions)) <= FrameCodec.MaxFrameBytes * 3 / 4);
            Assert.All(page["rows"]!.AsArray(), row => Assert.Equal(new string('中', 1000), row!["candidate"]!["preserved_9"]!.GetValue<string>()));
        }
    }

    /// <summary>
    /// Audit 2026-10-20 H2-3: every edit or region change sends the whole batch back in one request, which
    /// one frame must hold. A paged preview whose candidates would not fit is refused up front, with the
    /// same words as any preview too large, and nothing is kept.
    /// </summary>
    [Fact]
    public void APagedBatchThatCouldNotBeSentBackForACheckIsRefusedUpFront()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = Rows(120);
        foreach (var row in rows)
            for (var field = 0; field < 10; field++) row!["preserved_" + field] = new string('中', 1000);
        var failure = Assert.Throws<CollectorException>(() => service.PreviewSource("ROWS", rows: rows, pageSize: 100));
        Assert.Equal(ErrorCodes.BadRequest, failure.Code);
        Assert.Equal("import.preview.too_large", failure.Text?.Key);
        Assert.Equal(0, CachedCount(service));
        Assert.Equal(0, Count(fixture));
    }

    [Fact]
    public void ContinuedPagesDoNotRereadTheirSourceFile()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "synthetic-pages.csv");
        File.WriteAllText(path, "副本名称,职业,结果\n分页来源一,战士,通关\n分页来源二,战士,通关\n");
        var first = service.PreviewSource("CSV", filePath: path, pageSize: 1);
        File.Delete(path);
        var second = service.PreviewPage(Id(first), 2);
        Assert.Equal("分页来源二", second["rows"]![0]!["run"]!["duty_name"]!.GetValue<string>());
        Assert.Equal(first["summary"]!.ToJsonString(), second["summary"]!.ToJsonString());
    }

    [Fact]
    public void ACompleteSingleRowMustFitTheResponseBudgetBeforeCaching()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var rows = Rows(1);
        for (var field = 0; field < 70; field++) rows[0]!["preserved_" + field] = new string('中', 8000);
        var failure = Assert.Throws<CollectorException>(() => service.PreviewSource("ROWS", rows: rows, pageSize: 1));
        Assert.Equal(ErrorCodes.BadRequest, failure.Code);
        Assert.Equal(0, CachedCount(service));
        Assert.Equal(0, Count(fixture));
    }

    [Fact]
    public void FrozenByteBudgetEvictsOldestAndRejectsAnOversizedBatchBeforeCaching()
    {
        using var fixture = new TestDatabase();
        Assert.Equal(64L * 1024 * 1024, RunImportService.MaxPreviewCacheBytes);
        // A small injected budget exercises the production accounting and eviction without
        // allocating several 64 MiB sessions concurrently with the rest of the test suite.
        var sample = new RunImportService(fixture.Database, fixture.Clock).PreviewSource("ROWS", rows: Rows(3));
        var bytes = Encoding.UTF8.GetByteCount(sample.ToJsonString(Wire.JsonOptions));
        var service = new RunImportService(fixture.Database, fixture.Clock, bytes + 100);
        var first = service.PreviewSource("ROWS", rows: Rows(3), pageSize: 1);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var second = service.PreviewSource("ROWS", rows: Rows(3), pageSize: 1);
        Assert.Throws<CollectorException>(() => service.PreviewPage(Id(first), 1));
        Assert.Equal(3, service.PreviewPage(Id(second), 1)["summary"]!["total"]!.GetValue<int>());
        var error = Assert.Throws<CollectorException>(() => service.PreviewSource("ROWS", rows: Rows(5), pageSize: 1));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal(1, CachedCount(service));
        Assert.Equal(0, Count(fixture));
    }

    [Fact]
    public void PreCancelledPreviewNeverOccupiesTheCache()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.PreviewSource("ROWS", rows: Rows(2), pageSize: 1, cancellationToken: cancellation.Token));
        Assert.Equal(0, CachedCount(service));
    }

    [Fact]
    public void CancellationAfterRenderingDoesNotEvictExistingPreviews()
    {
        using var fixture = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var clock = new CancelBeforeAdmissionClock(fixture.Clock.UtcNow, cancellation);
        var service = new RunImportService(fixture.Database, clock);
        var first = service.PreviewSource("ROWS", rows: Rows(1), pageSize: 1);
        for (var index = 1; index < RunImportService.MaxPreviews; index++)
            service.PreviewSource("ROWS", rows: Rows(1), pageSize: 1);
        clock.ReadsUntilCancel = 2; // Normalization timestamp, then admission timestamp.
        Assert.Throws<OperationCanceledException>(() => service.PreviewSource("ROWS", rows: Rows(2), pageSize: 1, cancellationToken: cancellation.Token));
        Assert.Equal(RunImportService.MaxPreviews, CachedCount(service));
        Assert.Single(service.PreviewPage(Id(first), 1)["rows"]!.AsArray());
    }

    private sealed class CancelBeforeAdmissionClock(DateTimeOffset now, CancellationTokenSource cancellation) : IClock
    {
        public int ReadsUntilCancel { get; set; } = -1;
        public DateTimeOffset UtcNow
        {
            get
            {
                if (ReadsUntilCancel > 0 && --ReadsUntilCancel == 0) cancellation.Cancel();
                return now;
            }
        }
        public TimeSpan Elapsed => TimeSpan.Zero;
    }

    private static int CachedCount(RunImportService service)
    {
        var field = typeof(RunImportService).GetField("_previews", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return ((System.Collections.IDictionary)field.GetValue(service)!).Count;
    }
}
