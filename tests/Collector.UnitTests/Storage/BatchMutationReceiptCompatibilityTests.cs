using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class BatchMutationReceiptCompatibilityTests
{
    private const string FirstId = "11111111-1111-1111-1111-111111111111";
    private const string SecondId = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("soft_delete", "18d3f4fe675be4e85aa847595ff95a17f07f4a7a7bde5a6f101d13c2caf7edcb")]
    [InlineData("restore", "1dd78c45541bf7b1ee1dc653fa34b95f52039db5bd763ef88132db0d44ebaf83")]
    [InlineData("purge", "ff36c6cb1a19194d5fec9e1b53178cae5ba16ae25fe246a667aa801ccd3c2acf")]
    public void LegacyReceipt_ReplaysAfterRestartWithoutApplyingTheBatchAgain(string action, string fingerprint)
    {
        using var db = new TestDatabase();
        var request = new BatchMutateRunsCommand(Guid.NewGuid().ToString("D"), action,
            [new(SecondId, 1), new(FirstId, 1)], "legacy reason");
        // Frozen pre-refactor format and fingerprints: never generate this fixture with the new codec.
        var legacy = $$$"""
            {"batch_fingerprint":"{{{fingerprint}}}","outcome":{"changed_count":2,"run_ids":["{{{FirstId}}}","{{{SecondId}}}"],"action":"{{{action}}}","idempotent_replay":false}}
            """;
        db.Database.RunInTransaction(tx => new IdempotencyRepository(db.Database, db.Clock)
            .Store(request.RequestId, "BatchMutateRuns", legacy, tx));
        db.Reopen();
        var mutations = Mutations(db);

        // No target rows exist, as after a purge. Replaying must read only the stored receipt.
        var replay = mutations.BatchMutateRuns(request);
        Assert.Equal(2, replay.ChangedCount);
        Assert.Equal(new[] { FirstId, SecondId }, replay.RunIds);
        Assert.Equal(action, replay.Action);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(legacy, new IdempotencyRepository(db.Database, db.Clock).TryGetResponse(request.RequestId));

        var conflict = Assert.Throws<CollectorException>(() =>
            mutations.BatchMutateRuns(request with { Reason = "changed reason" }));
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Code);
        Assert.Equal("request_id", conflict.Field);
    }

    [Fact]
    public void NewExecution_ReturnsTypedResultAndPersistsTheLegacyReceiptShape()
    {
        using var db = new TestDatabase();
        var runs = new RunRepository(db.Database);
        db.Database.RunInTransaction(tx =>
        {
            foreach (var id in new[] { FirstId, SecondId })
            {
                runs.Insert(TestDatabase.Run(id), tx);
                new RunRevisionRepository(db.Database).Append(new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"), RunId = id, Revision = 1,
                    ChangedAtUtc = db.Clock.UtcNow, ChangeKind = ChangeKind.CreateManual,
                    Actor = RevisionActor.User, Reason = "initial fixture record",
                    Changes = Array.Empty<RunFieldChange>(),
                }, tx);
            }
        });
        var request = new BatchMutateRunsCommand(Guid.NewGuid().ToString("D"), "soft_delete",
            [new(SecondId, 1), new(FirstId, 1)], "legacy reason");

        var outcome = Mutations(db).BatchMutateRuns(request);

        Assert.Equal(2, outcome.ChangedCount);
        Assert.Equal(new[] { FirstId, SecondId }, outcome.RunIds);
        Assert.Equal("soft_delete", outcome.Action);
        Assert.False(outcome.IdempotentReplay);
        var stored = new IdempotencyRepository(db.Database, db.Clock).TryGetResponse(request.RequestId)!;
        var expected = JsonNode.Parse($$$"""
            {"batch_fingerprint":"18d3f4fe675be4e85aa847595ff95a17f07f4a7a7bde5a6f101d13c2caf7edcb","outcome":{"changed_count":2,"run_ids":["{{{FirstId}}}","{{{SecondId}}}"],"action":"soft_delete","idempotent_replay":false}}
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(stored)));
        Assert.True(runs.Get(FirstId)!.SoftDeleted);
        Assert.True(runs.Get(SecondId)!.SoftDeleted);

        db.Reopen();
        var replay = Mutations(db).BatchMutateRuns(request with { Runs = request.Runs.Reverse().ToArray() });
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(outcome.RunIds, replay.RunIds);
        Assert.Equal(2, new RunRepository(db.Database).Get(FirstId)!.Revision);
        Assert.Equal(2, new RunRepository(db.Database).Get(SecondId)!.Revision);
    }

    [Fact]
    public void RequestIdAlreadyUsedByASingleMutationStillReturnsConflict()
    {
        using var db = new TestDatabase();
        var mutations = Mutations(db);
        var requestId = Guid.NewGuid().ToString("D");
        var created = mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = requestId,
            Reason = "original mutation",
            Result = RunResult.Completed,
            EnteredAtUtc = db.Clock.UtcNow.AddMinutes(-30),
            EndedAtUtc = db.Clock.UtcNow,
        });

        var conflict = Assert.Throws<CollectorException>(() => mutations.BatchMutateRuns(new(
            requestId, "soft_delete", [new(created.RunId, 1)], "batch mutation")));

        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Code);
        Assert.Equal("request_id", conflict.Field);
        Assert.False(new RunRepository(db.Database).Get(created.RunId)!.SoftDeleted);
    }

    private static RunMutationService Mutations(TestDatabase db)
    {
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        return new RunMutationService(db.Database, settings, db.Clock);
    }
}
