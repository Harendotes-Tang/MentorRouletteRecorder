using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class DurableMutationIdempotencyTests
{
    private static string Id() => Guid.NewGuid().ToString("D");

    private sealed class Fixture : IDisposable
    {
        public readonly TestDatabase Db = new();
        public RunMutationService Mutations => new(Db.Database, Settings(), Db.Clock);
        public RunReflectionService Reflections => new(Db.Database, Db.Clock);
        public RunRepository Runs => new(Db.Database);
        public IdempotencyRepository Receipts => new(Db.Database, Db.Clock);

        private SettingsRepository Settings()
        {
            var settings = new SettingsRepository(Db.Database, Db.Clock);
            settings.EnsureDefaults();
            return settings;
        }

        public CreateManualRunCommand NewRun(string? requestId = null) => new()
        {
            RequestId = requestId ?? Id(), Reason = "幂等回归", Result = RunResult.Completed,
            EnteredAtUtc = Db.Clock.UtcNow.AddHours(-1), EndedAtUtc = Db.Clock.UtcNow.AddMinutes(-30),
        };

        public MentorRun Create() => Mutations.CreateManualRun(NewRun()).Run!;

        public BatchMutateRunsCommand Batch(MentorRun run, string action = "soft_delete", string? requestId = null) =>
            new(requestId ?? Id(), action, [new BatchRunTarget(run.RunId, run.Revision)], "幂等回归");

        public void ExpireAndPrune()
        {
            Db.Clock.UtcNow += TimeSpan.FromDays(2);
            Create(); // A real successful mutation runs the production pruning path.
        }

        public string? StoredBody(string requestId)
        {
            using var command = Db.Database.CreateCommand();
            command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id=$id;";
            command.Parameters.AddWithValue("$id", requestId);
            return command.ExecuteScalar() as string;
        }

        public void Dispose() => Db.Dispose();
    }

    private static void AssertExpired(Action action)
    {
        var error = Assert.Throws<CollectorException>(action);
        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
        Assert.Equal("request_id", error.Field);
        Assert.NotNull(error.Details);
        Assert.Equal("idempotency", error.Details["conflict"]);
        Assert.Equal("RESPONSE_EXPIRED", error.Details["reason"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredReflectionCannotReplaceOrClearANewerReflectionAfterRestart(bool clear)
    {
        using var f = new Fixture();
        var run = f.Create();
        f.Reflections.Set(new(Id(), run.RunId, ReflectionMood.Ok, "初始心得"));
        var old = new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, clear ? "" : "旧心得");
        var first = f.Reflections.Set(old);
        Assert.True(f.Reflections.Set(old).IdempotentReplay);
        f.Db.Clock.UtcNow += TimeSpan.FromDays(2);
        var newer = f.Reflections.Set(new(Id(), run.RunId, ReflectionMood.Bad, "后保存的心得"));
        Assert.Null(f.Receipts.TryGetResponse(old.RequestId));

        f.Db.Reopen();
        AssertExpired(() => f.Reflections.Set(old));
        AssertExpired(() => f.Reflections.Set(old with { Text = "复用旧编号的新正文" }));
        Assert.Equal(newer.Reflection, f.Runs.Get(run.RunId)!.Reflection);
        Assert.Equal(run.Revision, f.Runs.Get(run.RunId)!.Revision);
        Assert.False(first.IdempotentReplay);
    }

    [Theory]
    [InlineData("soft_delete")]
    [InlineData("restore")]
    [InlineData("purge")]
    public void ExpiredBatchParentCannotBeReusedForAnotherMutation(string action)
    {
        using var f = new Fixture();
        var run = f.Create();
        if (action != "soft_delete")
        {
            f.Mutations.BatchMutateRuns(f.Batch(run));
            run = f.Runs.Get(run.RunId)!;
        }
        var batch = f.Batch(run, action);
        f.Mutations.BatchMutateRuns(batch);
        Assert.True(f.Mutations.BatchMutateRuns(batch)["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            f.Mutations.BatchMutateRuns(batch with { Reason = "不同内容" })).Code);
        f.ExpireAndPrune();
        f.Db.Reopen();

        AssertExpired(() => f.Mutations.BatchMutateRuns(batch));
        AssertExpired(() => f.Mutations.CreateManualRun(f.NewRun(batch.RequestId)));
        var other = f.Create();
        AssertExpired(() => f.Reflections.Set(new(batch.RequestId, other.RunId, ReflectionMood.Good, "不能写入")));
        Assert.Null(f.Runs.Get(other.RunId)!.Reflection);
    }

    [Fact]
    public void ExpiredReflectionRequestCannotBeReusedByRunMutations()
    {
        using var f = new Fixture();
        var run = f.Create();
        var original = new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, "心得");
        f.Reflections.Set(original);
        f.ExpireAndPrune();

        AssertExpired(() => f.Mutations.CreateManualRun(f.NewRun(original.RequestId)));
        AssertExpired(() => f.Mutations.BatchMutateRuns(f.Batch(run, requestId: original.RequestId)));
        Assert.False(f.Runs.Get(run.RunId)!.SoftDeleted);
    }

    [Theory]
    [InlineData("SetRunReflection")]
    [InlineData("BatchMutateRuns")]
    public void ExpiredBodiesBecomePersistentOpaqueMarkersAndClockRollbackCannotReplayThem(string messageType)
    {
        using var f = new Fixture();
        var requestId = Id();
        f.Db.Database.RunInTransaction(tx => f.Receipts.Store(requestId, messageType, "{\"text\":\"private-body\"}", tx));
        f.ExpireAndPrune();
        Assert.Equal("null", f.StoredBody(requestId));
        Assert.Null(f.Receipts.TryGetResponse(requestId));
        Assert.True(f.Receipts.WasAppliedBeforePrune(requestId));
        f.Db.Clock.UtcNow -= TimeSpan.FromDays(3);
        f.Db.Reopen();
        Assert.Null(f.Receipts.TryGetResponse(requestId));
        Assert.True(f.Receipts.WasAppliedBeforePrune(requestId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPruningRollsBackMutationReceiptAndOldResponseTogether(bool batch)
    {
        using var f = new Fixture();
        var run = f.Create();
        var original = new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, "保留原文");
        f.Reflections.Set(original);
        var body = f.StoredBody(original.RequestId);
        f.Db.Database.RunInTransaction(tx =>
        {
            using var command = f.Db.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "CREATE TRIGGER reject_receipt_expiry BEFORE UPDATE OF response_json ON ipc_idempotency " +
                $"WHEN OLD.request_id='{original.RequestId}' BEGIN SELECT RAISE(ABORT,'synthetic prune failure'); END;";
            command.ExecuteNonQuery();
        });
        f.Db.Clock.UtcNow += TimeSpan.FromDays(2);
        var requestId = Id();
        if (batch)
            Assert.Throws<SqliteException>(() => f.Mutations.BatchMutateRuns(f.Batch(run, requestId: requestId)));
        else
            Assert.Throws<SqliteException>(() => f.Reflections.Set(new(requestId, run.RunId, ReflectionMood.Bad, "应回滚")));

        Assert.Equal(body, f.StoredBody(original.RequestId));
        Assert.Null(f.StoredBody(requestId));
        Assert.False(f.Receipts.WasAppliedBeforePrune(requestId));
        var retained = f.Runs.Get(run.RunId)!;
        Assert.Equal("保留原文", retained.Reflection!.Text);
        Assert.Equal(run.Revision, retained.Revision);
        Assert.False(retained.SoftDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PurgingAfterExpiryRetainsNoBodyAndCannotReapplyTheRequest(bool batch)
    {
        using var f = new Fixture();
        var run = f.Create();
        var reflection = new SetRunReflectionCommand(Id(), run.RunId, ReflectionMood.Good, "永久删除的心得正文");
        f.Reflections.Set(reflection);
        var deletion = f.Batch(run);
        if (batch) f.Mutations.BatchMutateRuns(deletion);
        f.ExpireAndPrune();
        if (!batch) f.Mutations.BatchMutateRuns(deletion);
        f.Mutations.BatchMutateRuns(f.Batch(f.Runs.Get(run.RunId)!, "purge"));

        Assert.Null(f.Runs.Get(run.RunId));
        Assert.Equal("null", f.StoredBody(reflection.RequestId));
        AssertExpired(() => f.Reflections.Set(reflection));
        if (batch)
        {
            Assert.Equal("null", f.StoredBody(deletion.RequestId));
            AssertExpired(() => f.Mutations.BatchMutateRuns(deletion));
            AssertExpired(() => f.Mutations.CreateManualRun(f.NewRun(deletion.RequestId)));
        }
        Assert.Null(new RunReflectionRepository(f.Db.Database).Get(run.RunId));
    }

    [Fact]
    public void ReflectionRejectsAnExpiredRequestAlreadyExecutedByAnotherWriter()
    {
        using var f = new Fixture();
        var create = f.NewRun();
        var run = f.Mutations.CreateManualRun(create).Run!;
        f.ExpireAndPrune();
        AssertExpired(() => f.Reflections.Set(new(create.RequestId, run.RunId, ReflectionMood.Good, "不能复用其他操作的旧编号")));
        Assert.Null(f.Runs.Get(run.RunId)!.Reflection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportCommitReportsExpiredForReflectionAndBatchRequestMarkers(bool batch)
    {
        using var f = new Fixture();
        var run = f.Create();
        var requestId = Id();
        if (batch)
            f.Mutations.BatchMutateRuns(f.Batch(run, requestId: requestId));
        else
            f.Reflections.Set(new(requestId, run.RunId, ReflectionMood.Good, "原始心得"));
        f.ExpireAndPrune();

        var imports = new RunImportService(f.Db.Database, f.Db.Clock);
        AssertExpired(() => imports.Commit(Id(), [1], true, requestId));
    }
}
