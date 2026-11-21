using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

public sealed partial class RunMutationService
{
    /// <summary>Reads a consistent preview without changing the selected rows or target role.</summary>
    public RunRegionMigrationPreview PreviewRunRegionMigration(IReadOnlyList<BatchRunTarget> targets,
        Region region, long profileId) =>
        PreviewStagedRunRegionMigration(ValidateBatchTargets(targets), region, profileId);

    /// <summary>Previews an already validated sealed set without the inline wire-array size constraint.</summary>
    internal RunRegionMigrationPreview PreviewStagedRunRegionMigration(IReadOnlyList<BatchRunTarget> sorted,
        Region region, long profileId) =>
        _database.RunInTransaction(tx =>
            InspectRegionMigration(sorted.Select(target => LoadBatchTarget(target, tx)).ToArray(),
                region, profileId, tx).Preview);

    private (RunRegionMigrationPreview Preview, CharacterProfile Profile) InspectRegionMigration(
        MentorRun[] runs, Region region, long profileId, SqliteTransaction transaction)
    {
        var source = runs[0].Region;
        if (region is not (Region.Cn or Region.Tc or Region.Global) || region == source)
            throw CollectorException.BadRequest(CollectorTexts.HistoryRegionDifferentTarget(), "region");
        if (runs.FirstOrDefault(run => run.SoftDeleted || run.CaptureSessionId is not null ||
                run.Source is not (RunSource.Manual or RunSource.Import)) is { } ineligible)
            throw new CollectorException(ErrorCodes.BadRequest, CollectorTexts.HistoryRegionIneligible(),
                new Dictionary<string, object?> { ["run_id"] = ineligible.RunId }, field: "runs");
        if (runs.Any(run => run.Region != source))
            throw CollectorException.BadRequest(CollectorTexts.HistoryRegionMixedSource(), "runs");

        var profile = _settings.Profiles.Find(profileId, transaction)
            ?? throw new CollectorException(ErrorCodes.NotFound, CollectorTexts.ProfileNotFound(), field: "profile_id");
        if (profile.Region != region)
            throw CollectorException.BadRequest(CollectorTexts.ProfileRegionMismatch(), "profile_id");

        var ids = runs.Select(run => run.RunId).ToArray();
        if (_runs.FindRegionMigrationDuplicate(runs, region, profileId, transaction) is { } duplicate)
            throw new CollectorException(ErrorCodes.BadRequest, CollectorTexts.HistoryRegionDuplicate(),
                new Dictionary<string, object?> { ["run_id"] = duplicate.RunId, ["matching_run_id"] = duplicate.MatchId },
                field: "runs");

        var stats = new StatisticsRepository(_database, _settings, clock: _clock);
        var contributing = (int)stats.CountContributingCompleted(ids, transaction, source);
        var targetCompleted = stats.CountContributingCompleted(profileId, transaction);
        return (new RunRegionMigrationPreview(source, contributing, profile.BaselineCompletedCount, targetCompleted), profile);
    }

    private void MoveRegion(MentorRun[] runs, RunRegionMigration migration, string reason, string requestId,
        string fingerprint, SqliteTransaction transaction)
    {
        var (preview, profile) = InspectRegionMigration(runs, migration.Region, migration.ProfileId, transaction);
        if (migration.ExpectedBaseline != preview.TargetBaseline)
            throw new CollectorException(ErrorCodes.RevisionConflict, CollectorTexts.HistoryRegionBaselineChanged(),
                field: "expected_baseline");
        if (migration.BaselineOverlap < 0 || migration.BaselineOverlap > Math.Min(preview.ContributingCount, preview.TargetBaseline))
            throw CollectorException.BadRequest(CollectorTexts.HistoryRegionInvalidOverlap(), "baseline_overlap");

        var baseline = preview.TargetBaseline - migration.BaselineOverlap;
        if (preview.TargetCompletedCount + baseline + preview.ContributingCount > int.MaxValue)
            throw CollectorException.BadRequest(CollectorTexts.BaselineTotalTooLarge(), "baseline_overlap");
        foreach (var run in runs)
        {
            Commit(run, run with { Region = migration.Region, ProfileId = profile.ProfileId }, ChangeKind.Correct,
                reason, AuditRequestId(requestId, run), fingerprint, transaction,
                markCorrected: false, clearPendingReview: false);
            _settings.Identities.ClearRunStamp(run.RunId, transaction);
        }
        // The baseline audit records changes of the baseline only (docs/data-model.md section 4):
        // a move that leaves the target's starting count as it was writes no entry.
        if (migration.BaselineOverlap == 0) return;
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var settings = profile.ToSettings() with
            { BaselineCompletedCount = baseline, BaselineEffectiveAt = now, UpdatedAtUtc = now };
        _settings.UpdateAchievementSettings(settings, transaction);
        _settings.AppendBaselineAudit(Guid.NewGuid().ToString("D"), settings,
            $"批量迁移区服，确认 {migration.BaselineOverlap} 条通关已计入目标起始次数。", requestId, transaction);
    }
}
