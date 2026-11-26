using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The idempotency receipts behind every mutating request: when a resend counts as the same request,
/// how long a response is replayed, how an expired receipt is pruned, and how a request id whose
/// response expired is still refused.
///
/// The idempotency table is a replay cache, not a journal.
///
/// Every mutating request writes one row, so the rows must be pruned: otherwise a user who
/// corrects records for a year carries every request id they ever sent in the database file
/// they back up. The window only has to cover "send, lose the pipe, resend", which is
/// seconds, so a day is generous (review finding M4).
/// </summary>
public sealed class DurableMutationIdempotencyTests
{
    private static string Id() => Guid.NewGuid().ToString("D");

    private sealed class Fixture : IDisposable
    {
        public readonly TestDatabase Db = new();
        public RunMutationService Mutations => Db.Mutations();
        public RunReflectionService Reflections => new(Db.Database, Db.Clock);
        public RunRepository Runs => new(Db.Database);
        public IdempotencyRepository Receipts => new(Db.Database, Db.Clock);

        public CreateManualRunCommand NewRun(string? requestId = null) => new()
        {
            RequestId = requestId ?? Id(), Reason = "幂等回归", Result = RunResult.Completed,
            EnteredAtUtc = Db.Clock.UtcNow.AddHours(-1), EndedAtUtc = Db.Clock.UtcNow.AddMinutes(-30),
        };

        public MentorRun Create() => Mutations.CreateManualRun(NewRun()).Run!;

        public BatchMutateRunsCommand Batch(MentorRun run, string action = "soft_delete", string? requestId = null) =>
            new(requestId ?? Id(), action, [new BatchRunTarget(run.RunId, run.Revision)], "幂等回归");

        public UpdateAchievementBaselineCommand Baseline(int count) =>
            new(Id(), 2000, count, Db.Clock.UtcNow, "回归测试：保存基数");

        /// <summary>Stores one receipt in a transaction of its own; the store also prunes.</summary>
        public void Store(string requestId, string messageType, string response) =>
            Db.Database.RunInTransaction(tx => Receipts.Store(requestId, messageType, response, tx));

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

    // ---- what makes a resend the same request: an unambiguous fingerprint of its body ----

    [Fact]
    public void Fingerprint_DistinguishesAShiftedFieldBoundary()
    {
        // Plain concatenation would make these two hash alike, and the second request would
        // then be mistaken for a replay of the first.
        var first = MutationSnapshotCodec.Fingerprint("AB", "C");
        var second = MutationSnapshotCodec.Fingerprint("A", "BC");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fingerprint_DistinguishesAbsentFromEmpty() =>
        Assert.NotEqual(
            MutationSnapshotCodec.Fingerprint("x", null, "y"),
            MutationSnapshotCodec.Fingerprint("x", string.Empty, "y"));

    [Fact]
    public void Fingerprint_IsStableForTheSameParts() =>
        Assert.Equal(
            MutationSnapshotCodec.Fingerprint("a", null, "b"),
            MutationSnapshotCodec.Fingerprint("a", null, "b"));

    [Fact]
    public void Canonical_DistinguishesAValueThatLooksLikeAnotherField()
    {
        // A duty name may legitimately contain any character, including whatever a naive
        // encoding would have used as its separator.
        var honest = new RunChangeSet
        {
            Specified = new HashSet<string>(StringComparer.Ordinal) { RunFields.DutyName, RunFields.Note },
            DutyName = "A",
            Note = "B",
        };
        var forged = new RunChangeSet
        {
            Specified = new HashSet<string>(StringComparer.Ordinal) { RunFields.DutyName, RunFields.Note },
            DutyName = "A;note=B",
            Note = null,
        };

        Assert.NotEqual(
            MutationSnapshotCodec.Canonical(honest), MutationSnapshotCodec.Canonical(forged));
    }

    [Fact]
    public void SameRequestIdWithADifferentCreateBody_IsRefused()
    {
        using var f = new Fixture();
        var command = f.NewRun() with { DutyName = "AB", Note = "C" };
        f.Mutations.CreateManualRun(command);

        var error = Assert.Throws<CollectorException>(() =>
            f.Mutations.CreateManualRun(command with { DutyName = "A", Note = "BC" }));
        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
        Assert.Equal("idempotency", error.Details!["conflict"]);
    }

    // ---- how long a response is replayed, and what is left once it expires ----

    [Fact]
    public void ARowOlderThanTheWindowIsRemovedByTheNextWrite()
    {
        using var f = new Fixture();
        f.Store("old-request", "CorrectRun", "{\"ok\":1}");
        Assert.NotNull(f.Receipts.TryGetResponse("old-request"));

        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow
            .Add(IdempotencyRepository.RetentionWindow)
            .AddMinutes(1);
        f.Store("new-request", "CorrectRun", "{\"ok\":2}");

        Assert.Null(f.Receipts.TryGetResponse("old-request"));
        Assert.NotNull(f.Receipts.TryGetResponse("new-request"));
    }

    [Fact]
    public void ARowInsideTheWindowSurvives()
    {
        using var f = new Fixture();
        f.Store("recent-request", "CorrectRun", "{\"ok\":1}");

        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddHours(1);
        f.Store("newer-request", "CorrectRun", "{\"ok\":2}");

        Assert.NotNull(f.Receipts.TryGetResponse("recent-request"));
        Assert.NotNull(f.Receipts.TryGetResponse("newer-request"));
    }

    /// <summary>
    /// Review finding M-7. The replay cache is pruned, the audit chain is not. A resend that
    /// arrives after the window reaches the write and breaks
    /// <c>UNIQUE(run_revisions.request_id)</c>, which must surface as the contract's own
    /// idempotency code rather than <c>ERR_INTERNAL</c>.
    /// </summary>
    [Fact]
    public void AReplayAfterThePruneIsAContractConflictRatherThanAnInternalError()
    {
        using var f = new Fixture();
        var service = f.Mutations;
        var run = TestDatabase.Run(result: RunResult.Completed, source: RunSource.Manual);
        f.Db.InsertWithFirstRevision(run);

        var requestId = Guid.NewGuid().ToString("D");
        var command = new RunReasonCommand(requestId, run.RunId, 1, "这条记录无需保留在统计中");
        var first = service.SoftDeleteRun(command);
        Assert.False(first.IdempotentReplay);

        // A second attempt inside the window still replays the stored outcome verbatim.
        Assert.True(service.SoftDeleteRun(command).IdempotentReplay);

        // Move past the retention window and let another write sweep the cache.
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow
            .Add(IdempotencyRepository.RetentionWindow)
            .AddMinutes(1);
        f.Store(Guid.NewGuid().ToString("D"), "CorrectRun", "{\"ok\":1}");
        Assert.Null(f.Receipts.TryGetResponse(requestId));

        var refused = Assert.Throws<CollectorException>(() => service.SoftDeleteRun(command));

        Assert.Equal(ErrorCodes.IdempotencyConflict, refused.Code);
        Assert.NotEqual(ErrorCodes.Internal, refused.Code);
        Assert.Equal("request_id", refused.Field);
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
        Assert.True(f.Mutations.BatchMutateRuns(batch).IdempotentReplay);
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

    /// <summary>
    /// Audit 2026-11-26 H1b-2. A character profile request writes no row keyed by its request id, so once its
    /// response expired nothing proved it had run: a late replay of 设为当前角色 made the old character current
    /// again and filed newly recorded runs under it. contracts/error-codes.md rule 13 refuses such a replay.
    /// </summary>
    [Fact]
    public void ALateReplayOfACharacterProfileRequestIsRefusedAndChangesNothing()
    {
        using var f = new Fixture();
        var settings = f.Db.Settings();
        var profiles = new CharacterProfileService(f.Db.Database, settings, f.Db.Clock);
        var main = settings.Profiles.FindCurrent(Region.Cn)!.ProfileId;
        var alt = profiles.Create(new CreateCharacterProfileCommand(Id(), Region.Cn, "小号", 2000, 0, false, null)).ProfileId;
        var switchToAlt = new UpdateCharacterProfileCommand(Id(), alt, null, MakeCurrent: true);
        profiles.Update(switchToAlt);
        profiles.Update(new UpdateCharacterProfileCommand(Id(), main, null, MakeCurrent: true));

        f.Db.Clock.UtcNow += IdempotencyRepository.RetentionWindow + TimeSpan.FromHours(1);
        f.Create(); // Any later write prunes the expired response.
        Assert.Null(f.Receipts.TryGetResponse(switchToAlt.RequestId));
        f.Db.Reopen();

        settings = f.Db.Settings();
        profiles = new CharacterProfileService(f.Db.Database, settings, f.Db.Clock);
        AssertExpired(() => profiles.Update(switchToAlt));
        Assert.Equal(main, settings.Profiles.FindCurrent(Region.Cn)!.ProfileId);
    }

    [Theory]
    [InlineData("SetRunReflection")]
    [InlineData("BatchMutateRuns")]
    [InlineData("CreateCharacterProfile")]
    [InlineData("UpdateCharacterProfile")]
    [InlineData("DeleteCharacterProfile")]
    [InlineData("ResolveCharacterIdentity")]
    public void ExpiredBodiesBecomePersistentOpaqueMarkersAndClockRollbackCannotReplayThem(string messageType)
    {
        using var f = new Fixture();
        var requestId = Id();
        f.Store(requestId, messageType, "{\"text\":\"private-body\"}");
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExpiredBaselineNeverOverwritesLaterValuesEvenWithoutRetainedAudit(bool unchanged, bool trimAudit)
    {
        using var f = new Fixture();
        var settings = f.Db.Settings();
        var service = f.Mutations;
        var original = f.Baseline(unchanged ? 0 : 10);
        service.UpdateAchievementBaseline(original);
        Assert.True(service.UpdateAchievementBaseline(original).IdempotentReplay);
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(2);
        var writes = trimAudit ? SettingsRepository.MaxBaselineAuditEntries + 1 : 1;
        for (var index = 0; index < writes; ++index)
            service.UpdateAchievementBaseline(f.Baseline(20 + index));
        if (unchanged || trimAudit)
            Assert.DoesNotContain(settings.ReadBaselineAudit(), row => row.RequestId == original.RequestId);
        Assert.Null(f.Receipts.TryGetResponse(original.RequestId));

        f.Db.Reopen();
        settings = f.Db.Settings();
        service = f.Mutations;
        var later = settings.GetAchievementSettings();
        AssertExpired(() => service.UpdateAchievementBaseline(original));
        AssertExpired(() => service.UpdateAchievementBaseline(original with { GoalCount = 2500 }));
        Assert.Equal(later, settings.GetAchievementSettings());
    }

    [Fact]
    public void LegacyPrunedReceiptIsRecoveredBeforeItsLastAuditEntryIsTrimmed()
    {
        using var f = new Fixture();
        var settings = f.Db.Settings();
        var service = f.Mutations;
        var original = f.Baseline(10);
        service.UpdateAchievementBaseline(original);
        // Model an older version: the response was deleted, but its bounded audit survives.
        f.Db.Database.RunInTransaction(tx =>
        {
            using var command = f.Db.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM ipc_idempotency WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", original.RequestId);
            command.ExecuteNonQuery();
        });
        Assert.True(f.Receipts.WasAppliedBeforePrune(original.RequestId));
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(2);
        for (var index = 0; index < SettingsRepository.MaxBaselineAuditEntries; ++index)
            service.UpdateAchievementBaseline(f.Baseline(20 + index));
        Assert.DoesNotContain(settings.ReadBaselineAudit(), row => row.RequestId == original.RequestId);
        AssertExpired(() => service.UpdateAchievementBaseline(original));
        Assert.Equal(119, settings.GetAchievementSettings().BaselineCompletedCount);
    }

    [Fact]
    public void ExpiredBaselineClearsResponseBodyButKeepsItsIdWhenOtherReceiptsAreRemoved()
    {
        using var f = new Fixture();
        var repository = f.Receipts;
        var baselineId = Id();
        var ordinaryId = Id();
        f.Db.Database.RunInTransaction(tx =>
        {
            repository.Store(baselineId, "UpdateAchievementBaseline", "{\"private_response\":\"expired\"}", tx);
            repository.Store(ordinaryId, "CorrectRun", "{\"ok\":true}", tx);
        });
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(2);
        f.Store(Id(), "CorrectRun", "{}");
        Assert.Null(repository.TryGetResponse(baselineId));
        Assert.True(repository.WasAppliedBeforePrune(baselineId));
        using var command = f.Db.Database.CreateCommand();
        command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id = $id;";
        command.Parameters.AddWithValue("$id", baselineId);
        Assert.Equal("null", command.ExecuteScalar());
        command.Parameters["$id"].Value = ordinaryId;
        Assert.Null(command.ExecuteScalar());
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(-3);
        Assert.Null(repository.TryGetResponse(baselineId));
        Assert.True(repository.WasAppliedBeforePrune(baselineId));
    }

    [Fact]
    public void BaselineFailureRollsBackSettingsAuditRecoveredMarkerAndResponsePruning()
    {
        using var f = new Fixture();
        var settings = f.Db.Settings();
        var service = f.Mutations;
        var original = f.Baseline(10);
        var legacy = f.Baseline(20);
        service.UpdateAchievementBaseline(original);
        service.UpdateAchievementBaseline(legacy);
        f.Db.Database.RunInTransaction(tx =>
        {
            using var command = f.Db.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM ipc_idempotency WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", legacy.RequestId);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "CREATE TRIGGER fail_baseline_expiry BEFORE UPDATE OF response_json ON ipc_idempotency " +
                $"WHEN OLD.request_id = '{original.RequestId}' BEGIN SELECT RAISE(ABORT, 'expiry blocked for rollback regression'); END;";
            command.ExecuteNonQuery();
        });
        var priorSettings = settings.GetAchievementSettings();
        var priorAudit = settings.ReadBaselineAudit().ToArray();
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(2);
        var failed = f.Baseline(30);
        Assert.Throws<SqliteException>(() => service.UpdateAchievementBaseline(failed));
        Assert.Equal(priorSettings, settings.GetAchievementSettings());
        Assert.Equal(priorAudit, settings.ReadBaselineAudit());
        var repository = f.Receipts;
        Assert.NotNull(repository.TryGetResponse(original.RequestId));
        Assert.Null(repository.TryGetResponse(failed.RequestId));
        Assert.False(repository.WasAppliedBeforePrune(failed.RequestId));
        using var lookup = f.Db.Database.CreateCommand();
        lookup.CommandText = "SELECT COUNT(*) FROM ipc_idempotency WHERE request_id = $id;";
        lookup.Parameters.AddWithValue("$id", legacy.RequestId);
        Assert.Equal(0L, lookup.ExecuteScalar());
    }

    [Fact]
    public void PruningAndReceiptInsertionRollBackTogether()
    {
        using var f = new Fixture();
        var repository = f.Receipts;
        var oldId = Id();
        f.Store(oldId, "UpdateAchievementBaseline", "{\"ok\":true}");
        f.Db.Clock.UtcNow = f.Db.Clock.UtcNow.AddDays(2);
        var failedId = Id();
        Assert.Throws<InvalidOperationException>(() => f.Db.Database.RunInTransaction(tx =>
        {
            repository.Store(failedId, "UpdateAchievementBaseline", "{}", tx);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal("{\"ok\":true}", repository.TryGetResponse(oldId));
        Assert.Null(repository.TryGetResponse(failedId));
        Assert.False(repository.WasAppliedBeforePrune(failedId));
    }
}
