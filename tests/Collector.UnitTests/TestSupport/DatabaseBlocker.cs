using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Holds the database's write gate until disposed; IpcImportAdmissionTests and PipeDeferredResponseBoundsTests use it.</summary>
internal sealed class DatabaseBlocker : IAsyncDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly Task _holding;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DatabaseBlocker(SqliteDatabase database) => _holding = Task.Factory.StartNew(() => database.Read(_ =>
    {
        _entered.TrySetResult();
        // Must outlive every bounded wait of its users (90 s): under the full gate the thread pool is
        // shared with the Soak suites and a reply can take tens of seconds to be scheduled.
        if (!_release.Wait(TimeSpan.FromSeconds(300))) throw new TimeoutException("Test database holder was not released.");
        return 0;
    }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    public Task Entered => _entered.Task;
    public bool Finished => _holding.IsCompleted;
    public async ValueTask DisposeAsync()
    {
        _release.Set();
        await _holding;
        _release.Dispose();
    }
}
