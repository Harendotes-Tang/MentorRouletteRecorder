using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Until 1.3.1 every CorrectRun set <c>manually_corrected</c>, including the player saying how a
/// pending run went, so every automatic record on a machine already carries the flag. The rule
/// changed (<see cref="RunMutationRules.OverrulesTheRecord(bool, IReadOnlyList{RunFieldChange})"/>);
/// the rows written under the old one are re-read against their own revision chains at startup.
/// </summary>
public sealed class CorrectedFlagMaintenanceTests
{
    private static string NewId() => Guid.NewGuid().ToString("D");

    [Fact]
    public void RowsThatWereOnlyConfirmedLoseTheFlagAndRealCorrectionsKeepIt()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var runs = new RunRepository(db.Database);
        var service = new RunMutationService(db.Database, settings, db.Clock);

        MentorRun Pending()
        {
            var created = service.CreateManualRun(new CreateManualRunCommand
            {
                RequestId = NewId(),
                Reason = "补录一次导随",
                Result = RunResult.Unknown,
                ContentId = 900001,
                JobId = 19,
                MatchedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
                EnteredAtUtc = new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
                EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero),
            });
            var stored = runs.Get(created.RunId)!;
            // Into review the way the machine does it: in place, without spending a revision.
            db.Database.RunInTransaction(tx => runs.Update(stored with { PendingReview = true }, stored.Revision, tx));
            return runs.Get(created.RunId)!;
        }

        var confirmed = Pending();
        service.CorrectRun(new CorrectRunCommand(NewId(), confirmed.RunId, confirmed.Revision, "确认通关",
            new RunChangeSet { Specified = new HashSet<string> { RunFields.Result }, Result = RunResult.Completed }));
        var corrected = Pending();
        service.CorrectRun(new CorrectRunCommand(NewId(), corrected.RunId, corrected.Revision, "职业记错了",
            new RunChangeSet { Specified = new HashSet<string> { RunFields.JobId }, JobId = 21 }));

        // What a database written by 1.3.0 and earlier looks like: both rows flagged.
        using (var command = db.Database.CreateCommand())
        {
            command.CommandText = "UPDATE mentor_runs SET manually_corrected = 1;";
            command.ExecuteNonQuery();
        }

        var changed = CorrectedFlagMaintenance.Run(db.Database);

        Assert.Equal(1, changed);
        Assert.False(runs.Get(confirmed.RunId)!.ManuallyCorrected);
        Assert.True(runs.Get(corrected.RunId)!.ManuallyCorrected);
        // Idempotent, and it spends no revision: the flag is derived, the audit chain is not touched.
        Assert.Equal(0, CorrectedFlagMaintenance.Run(db.Database));
        Assert.Equal(confirmed.Revision + 1, runs.Get(confirmed.RunId)!.Revision);
    }
}
