using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 导随心得 storage, filtering, serialisation, export and every refusal the write path owes
/// the contract.
///
/// The invariant under test: a reflection is not a run field. It is stored separately and
/// never bumps a revision, yet it is present on every run the Collector hands out.
/// </summary>
public sealed class ReflectionTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly RunRepository _runs;
    private readonly RunReflectionRepository _reflections;
    private readonly RunReflectionService _service;

    public ReflectionTests()
    {
        _runs = new RunRepository(_database.Database);
        _reflections = new RunReflectionRepository(_database.Database);
        _service = new RunReflectionService(_database.Database, _database.Clock);
    }

    public void Dispose() => _database.Dispose();

    private static string NewId() => Guid.NewGuid().ToString("D");

    private MentorRun Seed(MentorRun? run = null)
    {
        var seeded = run ?? TestDatabase.Run();
        _database.Database.RunInTransaction(tx => _runs.Insert(seeded, tx));
        return seeded;
    }

    private ReflectionMutationOutcome Set(
        string runId, ReflectionMood mood = ReflectionMood.Good, string text = "带新人打灯塔，一次过。",
        string? requestId = null) =>
        _service.Set(new SetRunReflectionCommand(requestId ?? NewId(), runId, mood, text));

    // Round-tripped through text on purpose: a payload built in memory holds boxed CLR
    // values, while the reader is written against the JsonElement-backed nodes a real frame
    // parses into. The in-memory shape would exercise a different reader.
    private static PayloadReader Payload(JsonObject payload) =>
        new((JsonNode.Parse(payload.ToJsonString()) as JsonObject)!);

    // -- storage ------------------------------------------------------------------------

    [Fact]
    public void Upsert_ReplacesTheTextAndKeepsTheOriginalCreatedAt()
    {
        var run = Seed();
        var first = Set(run.RunId).Reflection!;

        _database.Clock.UtcNow = _database.Clock.UtcNow.AddMinutes(30);
        var second = Set(run.RunId, ReflectionMood.Bad, "改主意了，其实挺糟心。").Reflection!;

        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.True(second.UpdatedAtUtc > second.CreatedAtUtc);
        Assert.Equal(ReflectionMood.Bad, second.Mood);
        Assert.Equal(second, _reflections.Get(run.RunId));
    }

    [Fact]
    public void Get_ReturnsNull_WhenTheRunHasNoReflection() =>
        Assert.Null(_reflections.Get(Seed().RunId));

    [Fact]
    public void Reflection_IsNotARunField_AndNeverBumpsTheRevision()
    {
        var run = Seed();

        Set(run.RunId);

        var reloaded = _runs.Get(run.RunId)!;
        Assert.Equal(run.Revision, reloaded.Revision);
        Assert.Equal(run.UpdatedAtUtc, reloaded.UpdatedAtUtc);
        Assert.NotNull(reloaded.Reflection);
    }

    [Fact]
    public void Query_HydratesTheReflectionOfEveryRunOnThePage()
    {
        var withOne = Seed();
        var withNone = Seed(TestDatabase.Run());
        Set(withOne.RunId, ReflectionMood.Ok, "还行。");

        var page = _runs.Query(null, RunSort.Default, 1, 50);

        Assert.Equal("还行。", page.Items.Single(item => item.RunId == withOne.RunId).Reflection!.Text);
        Assert.Null(page.Items.Single(item => item.RunId == withNone.RunId).Reflection);
    }

    // -- filtering ----------------------------------------------------------------------

    [Fact]
    public void Filter_WithReflection_KeepsOnlyRunsThatHaveOne()
    {
        var withOne = Seed();
        Seed(TestDatabase.Run());
        Set(withOne.RunId);

        var page = _runs.Query(new RunFilter { WithReflection = true }, RunSort.Default, 1, 50);

        Assert.Equal(1, page.Total);
        Assert.Equal(withOne.RunId, page.Items.Single().RunId);
    }

    [Fact]
    public void Filter_WithoutTheFlag_KeepsEveryRun()
    {
        Set(Seed().RunId);
        Seed(TestDatabase.Run());

        Assert.Equal(2, _runs.Query(RunFilter.Empty, RunSort.Default, 1, 50).Total);
    }

    // -- serialisation ------------------------------------------------------------------

    [Fact]
    public void Wire_Run_CarriesAnExplicitNull_WhenThereIsNoReflection()
    {
        var json = Wire.Run(Seed());

        Assert.True(json.ContainsKey("reflection"));
        Assert.Null(json["reflection"]);
    }

    [Fact]
    public void Wire_Run_RendersTheReflectionWithLowerCaseMoodTokens()
    {
        var run = Seed();
        Set(run.RunId, ReflectionMood.Bad, "糟心。");

        var reflection = Wire.Run(_runs.Get(run.RunId)!)["reflection"]!.AsObject();

        Assert.Equal("bad", reflection["mood"]!.GetValue<string>());
        Assert.Equal("糟心。", reflection["text"]!.GetValue<string>());
        Assert.Equal(
            new[] { "mood", "text", "created_at_utc", "updated_at_utc" },
            reflection.Select(pair => pair.Key).ToArray());
    }

    // -- request validation --------------------------------------------------------------

    [Fact]
    public void Parser_RefusesAnUnknownMood()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.SetRunReflection(
            NewId(),
            Payload(new JsonObject
            {
                ["run_id"] = NewId(),
                ["mood"] = "great",
                ["text"] = "文字",
            })));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.mood", error.Field);
    }

    [Fact]
    public void Parser_RefusesTextLongerThanTwoThousandCharacters()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.SetRunReflection(
            NewId(),
            Payload(new JsonObject
            {
                ["run_id"] = NewId(),
                ["mood"] = "good",
                ["text"] = new string('心', ReflectionText.MaxLength + 1),
            })));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.text", error.Field);
    }

    [Fact]
    public void Parser_AcceptsExactlyTwoThousandCharacters()
    {
        var command = RequestParsers.SetRunReflection(
            NewId(),
            Payload(new JsonObject
            {
                ["run_id"] = NewId(),
                ["mood"] = "ok",
                ["text"] = new string('心', ReflectionText.MaxLength),
            }));

        Assert.Equal(ReflectionText.MaxLength, command.Text!.Length);
    }

    [Fact]
    public void Parser_RefusesAFieldTheContractDoesNotDeclare()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.SetRunReflection(
            NewId(),
            Payload(new JsonObject
            {
                ["run_id"] = NewId(),
                ["mood"] = "good",
                ["text"] = "文字",
                ["reason"] = "心得不需要理由",
            })));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Parser_ReadsTheRecentLimitAndItsDefault()
    {
        Assert.Equal(3, RequestParsers.ReflectionRecentLimit(Payload(new JsonObject())));
        Assert.Equal(
            10, RequestParsers.ReflectionRecentLimit(Payload(new JsonObject { ["recent_limit"] = 10 })));
        Assert.Throws<CollectorException>(() =>
            RequestParsers.ReflectionRecentLimit(Payload(new JsonObject { ["recent_limit"] = 21 })));
    }

    // -- write path ----------------------------------------------------------------------

    [Fact]
    public void Set_EmptyTextDeletesTheReflection()
    {
        var run = Seed();
        Set(run.RunId);

        var cleared = Set(run.RunId, ReflectionMood.Good, "   ");

        Assert.Null(cleared.Reflection);
        Assert.Null(cleared.Run!.Reflection);
        Assert.Null(_reflections.Get(run.RunId));
    }

    [Fact]
    public void Set_TrimsTheStoredText()
    {
        var run = Seed();

        Assert.Equal("有空格", Set(run.RunId, ReflectionMood.Ok, "  有空格  ").Reflection!.Text);
    }

    [Fact]
    public void Set_RefusesAnUnknownRun()
    {
        var error = Assert.Throws<CollectorException>(() => Set(NewId()));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public void Set_AcceptsAReflectionOnASoftDeletedRun()
    {
        var run = Seed(TestDatabase.Run(softDeleted: true));

        var outcome = Set(run.RunId, ReflectionMood.Bad, "删掉了，但当时确实糟心。");

        Assert.NotNull(outcome.Reflection);
        Assert.True(outcome.Run!.SoftDeleted);
    }

    [Fact]
    public void Set_IsIdempotentByRequestId()
    {
        var run = Seed();
        var requestId = NewId();

        var first = Set(run.RunId, ReflectionMood.Good, "第一次写。", requestId);
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);
        var replay = Set(run.RunId, ReflectionMood.Good, "第一次写。", requestId);

        Assert.False(first.IdempotentReplay);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Reflection, replay.Reflection);
        Assert.Equal(first.Reflection!.UpdatedAtUtc, _reflections.Get(run.RunId)!.UpdatedAtUtc);
    }

    [Fact]
    public void Set_RefusesTheSameRequestIdWithADifferentBody()
    {
        var run = Seed();
        var requestId = NewId();
        Set(run.RunId, ReflectionMood.Good, "第一次写。", requestId);

        var error = Assert.Throws<CollectorException>(
            () => Set(run.RunId, ReflectionMood.Bad, "换了内容。", requestId));

        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
    }

    // -- summary -------------------------------------------------------------------------

    [Fact]
    public void GetSummary_CountsPendingRunsAndOrdersRecentByUpdatedAt()
    {
        var older = Seed(TestDatabase.Run(
            enteredAt: new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero)));
        var newer = Seed(TestDatabase.Run(
            enteredAt: new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero)));
        var pending = Seed(TestDatabase.Run(
            enteredAt: new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero)));
        Seed(TestDatabase.Run(result: RunResult.LeftOrAbandoned));

        Set(older.RunId, ReflectionMood.Ok, "先写的。");
        _database.Clock.UtcNow = _database.Clock.UtcNow.AddMinutes(5);
        Set(newer.RunId, ReflectionMood.Good, "后写的。");

        var summary = _reflections.GetSummary(3);

        Assert.Equal(2, summary.ReflectionCount);
        Assert.Equal(1, summary.PendingCompletedCount);
        Assert.Equal(new[] { newer.RunId, older.RunId }, summary.RecentRunIds);
        Assert.Equal(pending.RunId, summary.NextPendingRunId);
    }

    [Fact]
    public void GetSummary_IgnoresSoftDeletedRuns()
    {
        var deleted = Seed(TestDatabase.Run(softDeleted: true));
        Set(deleted.RunId);

        var summary = _reflections.GetSummary(3);

        Assert.Equal(0, summary.ReflectionCount);
        Assert.Equal(0, summary.PendingCompletedCount);
        Assert.Empty(summary.RecentRunIds);
        Assert.Null(summary.NextPendingRunId);
    }

    [Fact]
    public void GetSummary_HonoursARecentLimitOfZero()
    {
        Set(Seed().RunId);

        var summary = _reflections.GetSummary(0);

        Assert.Equal(1, summary.ReflectionCount);
        Assert.Empty(summary.RecentRunIds);
    }

    // -- export ---------------------------------------------------------------------------

    [Fact]
    public void Export_CarriesTheReflectionIntoBothFormats()
    {
        var run = Seed();
        Seed(TestDatabase.Run());
        Set(run.RunId, ReflectionMood.Good, "带新人，一次过。");

        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.ReflectionExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var exporter = new RunExporter(_runs, _database.Clock);

        try
        {
            var csv = File.ReadAllText(
                exporter.ExportCsv(Path.Combine(directory, "runs.csv"), null, false).TargetPath);
            var header = RunExporter.CsvHeader;
            Assert.Equal("reflection_mood", header[^2]);
            Assert.Equal("reflection_text", header[^1]);
            Assert.Contains("good,带新人，一次过。", csv, StringComparison.Ordinal);

            var json = JsonNode.Parse(File.ReadAllText(
                exporter.ExportJson(Path.Combine(directory, "runs.json"), null, false).TargetPath))!
                .AsArray();
            var exported = json.Single(item =>
                item!["run_id"]!.GetValue<string>() == run.RunId)!.AsObject();
            var other = json.Single(item =>
                item!["run_id"]!.GetValue<string>() != run.RunId)!.AsObject();

            Assert.Equal("good", exported["reflection"]!["mood"]!.GetValue<string>());
            Assert.True(other.ContainsKey("reflection"));
            Assert.Null(other["reflection"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
