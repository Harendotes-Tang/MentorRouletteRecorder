using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class HistorySelectionStoreTests
{
    private static readonly BatchRunTarget First = new("00000000-0000-4000-8000-000000000001", 12);
    private static readonly BatchRunTarget Second = new("00000000-0000-4000-8000-000000000002", 3);

    [Fact]
    public void StagedTargetsRequireAllChunksAndReplayIdenticalUploads()
    {
        using var fixture = new TestDatabase();
        using var store = new HistorySelectionStore(fixture.Database);
        var id = HistorySelectionStore.Digest([First, Second]);

        Assert.Equal((1, false), store.Stage(id, 0, [First], last: false));
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(() => store.RequireStaged(id)).Code);
        Assert.Equal((1, false), store.Stage(id, 0, [First], last: false));
        Assert.Equal((2, true), store.Stage(id, 1, [Second], last: true));
        Assert.Equal((2, true), store.Stage(id, 0, [First], last: false));
        Assert.Equal((2, true), store.Stage(id, 1, [Second], last: true));
        var resolved = store.RequireStaged(id);

        store.Release(id);
        store.Release(id);
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(() => store.RequireStaged(id)).Code);
        Assert.Equal(new[] { First, Second }, resolved);
    }

    [Fact]
    public void WrongOrderAndChangedRetriesAndWrongDigestCannotChangeAnUpload()
    {
        using var fixture = new TestDatabase();
        using var store = new HistorySelectionStore(fixture.Database);
        var id = HistorySelectionStore.Digest([First, Second]);

        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(() => store.Stage(id, 1, [Second], last: true)).Code);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() => store.Stage(id, 0, [Second, First], last: true)).Code);
        Assert.Equal((1, false), store.Stage(id, 0, [First], last: false));
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() =>
            store.Stage(id, 0, [First with { ExpectedRevision = 2 }], last: false)).Code);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() =>
            store.Stage(id, 1, [Second with { ExpectedRevision = 2 }], last: true)).Code);
        Assert.Equal((2, true), store.Stage(id, 1, [Second], last: true));
        Assert.Equal(new[] { First, Second }, store.RequireStaged(id));
    }

    [Fact]
    public void FrozenPagesRetainEarlierValuesAndBecomeUnavailableAfterRelease()
    {
        using var fixture = new TestDatabase();
        using var store = new HistorySelectionStore(fixture.Database);
        var runs = new RunRepository(fixture.Database);
        var rows = Enumerable.Range(0, 201).Select(index =>
            TestDatabase.Run(enteredAt: fixture.Clock.UtcNow.AddMinutes(index))).ToArray();
        fixture.InsertRuns(rows);
        var snapshot = store.Freeze(null, CancellationToken.None);
        Assert.Equal(200, snapshot.Page.Items.Count);
        Assert.Equal(201, snapshot.Page.Total);

        fixture.Database.RunInTransaction(transaction =>
        {
            runs.Update(rows[0] with { Revision = 2, SoftDeleted = true }, 1, transaction);
            runs.Insert(TestDatabase.Run(enteredAt: fixture.Clock.UtcNow.AddDays(1)), transaction);
        });
        var oldest = Assert.Single(store.ReadPage(snapshot.Id, 2).Items);
        Assert.Equal(rows[0].RunId, oldest.RunId);
        Assert.Equal(1, oldest.Revision);
        Assert.False(oldest.SoftDeleted);
        store.Release(snapshot.Id);
        Assert.Equal(ErrorCodes.NotFound, Assert.Throws<CollectorException>(() => store.ReadPage(snapshot.Id, 2)).Code);
    }

    [Fact]
    public void UnreleasedHandlesCannotKeepUnboundedCopies()
    {
        using var fixture = new TestDatabase();
        using var store = new HistorySelectionStore(fixture.Database);
        var snapshots = new List<string>();
        var staged = new List<string>();
        for (var index = 0; index <= HistorySelectionStore.MaxRetainedHandlesPerKind; index++)
        {
            snapshots.Add(store.Freeze(null, CancellationToken.None).Id);
            BatchRunTarget[] targets = [First with { ExpectedRevision = index + 1 }];
            var id = HistorySelectionStore.Digest(targets);
            store.Stage(id, 0, targets, last: true);
            staged.Add(id);
        }
        Assert.Single(snapshots.Select(id => Record.Exception(() => store.ReadPage(id, 1))).OfType<CollectorException>());
        Assert.Single(staged.Select(id => Record.Exception(() => store.RequireStaged(id))).OfType<CollectorException>());
        Assert.Empty(store.ReadPage(snapshots[^1], 1).Items);
        Assert.Equal(HistorySelectionStore.MaxRetainedHandlesPerKind + 1, Assert.Single(store.RequireStaged(staged[^1])).ExpectedRevision);
    }
}
