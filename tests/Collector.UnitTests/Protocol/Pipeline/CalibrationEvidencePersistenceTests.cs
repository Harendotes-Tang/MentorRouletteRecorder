using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Pipeline;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationEvidencePersistenceTests
{
    [Fact]
    public void AFinalWriteSupersedesAnEarlierQueuedSnapshot()
    {
        var writes = 0;
        Action? queued = null;
        var persistence = new CalibrationEvidencePersistence(new object(), (_, _, _, _) => { writes++; return true; }, (_, _) => { })
        {
            RunWrite = write => { queued = write; return Task.CompletedTask; },
        };
        var evidence = Evidence();
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", evidence, 1));
        var completed = false;
        persistence.StartPending((_, written) => { Assert.False(written); completed = true; });

        Assert.True(persistence.Save(Region.Cn, "build", "template", evidence));
        queued!();

        Assert.Equal(1, writes);
        Assert.True(completed);
        Assert.False(persistence.IsPeriodicSaveInFlight);
    }

    [Fact]
    public void DeletePreventsQueuedEvidenceFromResurrectingTheFile()
    {
        var writes = 0;
        var deletes = 0;
        Action? queued = null;
        var persistence = new CalibrationEvidencePersistence(new object(), (_, _, _, _) => { writes++; return true; }, (_, _) => deletes++)
        {
            RunWrite = write => { queued = write; return Task.CompletedTask; },
        };
        Assert.True(persistence.TryStage(Region.Tc, "build", "template", Evidence(), 2));
        persistence.StartPending((pending, written) => { Assert.Equal(2, pending.Generation); Assert.False(written); });
        persistence.Delete(Region.Tc, "build");
        queued!();

        Assert.Equal(1, deletes);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void FailedDeleteDoesNotDiscardAnOtherwiseValidQueuedWrite()
    {
        var writes = 0;
        Action? queued = null;
        var persistence = new CalibrationEvidencePersistence(new object(), (_, _, _, _) => { writes++; return true; }, (_, _) => throw new IOException())
        {
            RunWrite = write => { queued = write; return Task.CompletedTask; },
        };
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 3));
        persistence.StartPending((_, written) => Assert.True(written));
        persistence.Delete(Region.Cn, "build");
        queued!();

        Assert.Equal(1, writes);
    }

    [Fact]
    public void OnlyOnePeriodicWriteCanBeStagedUntilItsCompletionIsReconciled()
    {
        var gate = new object();
        Action? queued = null;
        var persistence = new CalibrationEvidencePersistence(gate, (_, _, _, _) => true, (_, _) => { })
        {
            RunWrite = write => { queued = write; return Task.CompletedTask; },
        };
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 4));
        Assert.False(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 5));
        persistence.StartPending((_, _) =>
        {
            Assert.True(Monitor.IsEntered(gate));
            Assert.False(persistence.IsPeriodicSaveInFlight);
        });
        Assert.False(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 5));
        queued!();
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 5));
    }

    [Fact]
    public void FailedWriteReleasesThePeriodicSlotAndAllowsTheNextAttempt()
    {
        var fail = true;
        var persistence = new CalibrationEvidencePersistence(new object(), (_, _, _, _) =>
        {
            if (fail) throw new IOException();
            return true;
        }, (_, _) => { })
        {
            RunWrite = write => { write(); return Task.CompletedTask; },
        };
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 1));
        persistence.StartPending((_, written) => Assert.False(written));
        Assert.False(persistence.IsPeriodicSaveInFlight);
        fail = false;
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 1));
        persistence.StartPending((_, written) => Assert.True(written));
    }

    [Fact]
    public async Task DiskWriteDoesNotHoldTheSessionGateAndShutdownWaitsForCompletion()
    {
        var gate = new object();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var persistence = new CalibrationEvidencePersistence(gate, (_, _, _, _) =>
        {
            Assert.False(Monitor.IsEntered(gate));
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }, (_, _) => { });
        Assert.True(persistence.TryStage(Region.Cn, "build", "template", Evidence(), 1));
        var completed = false;
        persistence.StartPending((_, _) => completed = true);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var readState = Task.Run(() => { lock (gate) return 1; });
            Assert.Equal(1, await readState.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }

        await Task.Run(persistence.WaitForPendingWrite).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed);
    }

    private static CalibrationSnapshot Evidence() =>
        new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, "session").Snapshot();
}
