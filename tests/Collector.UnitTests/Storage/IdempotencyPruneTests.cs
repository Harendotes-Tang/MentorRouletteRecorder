using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The idempotency table is a replay cache, not a journal.
///
/// Every mutating request writes one row, so the rows must be pruned: otherwise a user who
/// corrects records for a year carries every request id they ever sent in the database file
/// they back up. The window only has to cover "send, lose the pipe, resend", which is
/// seconds, so a day is generous (review finding M4).
/// </summary>
public sealed class IdempotencyPruneTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly IdempotencyRepository _repository;

    public IdempotencyPruneTests()
    {
        _repository = new IdempotencyRepository(_database.Database, _database.Clock);
    }

    [Fact]
    public void ARowOlderThanTheWindowIsRemovedByTheNextWrite()
    {
        Store("old-request", "{\"ok\":1}");
        Assert.NotNull(_repository.TryGetResponse("old-request"));

        _database.Clock.UtcNow = _database.Clock.UtcNow
            .Add(IdempotencyRepository.RetentionWindow)
            .AddMinutes(1);
        Store("new-request", "{\"ok\":2}");

        Assert.Null(_repository.TryGetResponse("old-request"));
        Assert.NotNull(_repository.TryGetResponse("new-request"));
    }

    [Fact]
    public void ARowInsideTheWindowSurvives()
    {
        Store("recent-request", "{\"ok\":1}");

        _database.Clock.UtcNow = _database.Clock.UtcNow.AddHours(1);
        Store("newer-request", "{\"ok\":2}");

        Assert.NotNull(_repository.TryGetResponse("recent-request"));
        Assert.NotNull(_repository.TryGetResponse("newer-request"));
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
        var settings = new SettingsRepository(_database.Database, _database.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(_database.Database, settings, _database.Clock);
        var runs = new RunRepository(_database.Database);
        var run = TestDatabase.Run(result: RunResult.Completed, source: RunSource.Manual);
        _database.Database.RunInTransaction(tx => runs.Insert(run, tx));
        _database.Database.RunInTransaction(tx => new RunRevisionRepository(_database.Database).Append(
            new RunRevision
            {
                RevisionId = Guid.NewGuid().ToString("D"),
                RunId = run.RunId,
                Revision = 1,
                ChangedAtUtc = _database.Clock.UtcNow,
                ChangeKind = ChangeKind.CreateManual,
                Actor = RevisionActor.User,
                Reason = "手工建立",
                Changes = Array.Empty<RunFieldChange>(),
            }, tx));

        var requestId = Guid.NewGuid().ToString("D");
        var command = new RunReasonCommand(requestId, run.RunId, 1, "这条记录无需保留在统计中");
        var first = service.SoftDeleteRun(command);
        Assert.False(first.IdempotentReplay);

        // A second attempt inside the window still replays the stored outcome verbatim.
        Assert.True(service.SoftDeleteRun(command).IdempotentReplay);

        // Move past the retention window and let another write sweep the cache.
        _database.Clock.UtcNow = _database.Clock.UtcNow
            .Add(IdempotencyRepository.RetentionWindow)
            .AddMinutes(1);
        Store(Guid.NewGuid().ToString("D"), "{\"ok\":1}");
        Assert.Null(_repository.TryGetResponse(requestId));

        var refused = Assert.Throws<CollectorException>(() => service.SoftDeleteRun(command));

        Assert.Equal(ErrorCodes.IdempotencyConflict, refused.Code);
        Assert.NotEqual(ErrorCodes.Internal, refused.Code);
        Assert.Equal("request_id", refused.Field);
    }

    private void Store(string requestId, string response) =>
        _database.Database.RunInTransaction(
            tx => _repository.Store(requestId, "CorrectRun", response, tx));

    public void Dispose() => _database.Dispose();
}
