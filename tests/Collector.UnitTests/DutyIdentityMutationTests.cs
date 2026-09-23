using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class DutyIdentityMutationTests
{
    [Theory]
    [InlineData(null, 1039, DutySource.Territory)]
    [InlineData(1, 1039, DutySource.ContentId)]
    [InlineData(null, null, null)]
    [InlineData(2, 1037, DutySource.Territory)]
    // Content changes but associated values stay the same: it must still be undoable.
    [InlineData(900001, 1037, DutySource.Manual)]
    public void CorrectThenUndo_RestoresCompleteIdentityAndCanUndoAgain(
        int? contentId, int? territoryId, DutySource? source)
    {
        using var fixture = new Fixture(contentId, territoryId, source);
        var original = fixture.Original;
        var corrected = fixture.CorrectDuty();
        Assert.Equal(2, corrected.ContentId);
        Assert.Equal(1037, corrected.TerritoryId);
        Assert.Equal(DutySource.Manual, corrected.DutySource);

        var revision = fixture.Revisions.GetAt(original.RunId, 2, null)!;
        Assert.Contains(revision.Changes, change => change.Field == RunAuditFields.DutyIdentity);
        Assert.All(revision.Changes, change => Assert.NotEqual(change.OldValue, change.NewValue));

        fixture.Service.UndoRevision(new RunReasonCommand(NewId(), original.RunId, 2, "撤销副本更正"));
        var restored = fixture.Runs.Get(original.RunId)!;
        AssertDuty(original, restored);
        fixture.Service.UndoRevision(new RunReasonCommand(NewId(), original.RunId, 3, "撤销上次撤销"));
        AssertDuty(corrected, fixture.Runs.Get(original.RunId)!);
    }

    [Fact]
    public void NameOnlyCorrection_DoesNotInventIdentityChangesAndRemainsUndoable()
    {
        using var fixture = new Fixture(2, 1037, DutySource.Manual);
        fixture.Service.CorrectRun(new CorrectRunCommand(NewId(), fixture.Original.RunId, 1, "更正名称",
            new RunChangeSet
            {
                DutyName = "更正后的名称",
                Specified = new HashSet<string> { RunFields.DutyName },
            }));
        var revision = fixture.Revisions.GetAt(fixture.Original.RunId, 2, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunAuditFields.DutyIdentity);
        fixture.Service.UndoRevision(new RunReasonCommand(NewId(), fixture.Original.RunId, 2, "撤销名称更正"));
        AssertDuty(fixture.Original, fixture.Runs.Get(fixture.Original.RunId)!);
    }

    [Fact]
    public void LegacyContentRevisionWithoutIdentity_IsRefusedWithoutWriting()
    {
        using var fixture = new Fixture(null, 1039, DutySource.Territory);
        var original = fixture.Original;
        var legacy = original with
        {
            Revision = 2, ContentId = 2, TerritoryId = 1037,
            DutyName = "旧版修订后的副本", DutySource = DutySource.Manual,
        };
        fixture.Database.Database.RunInTransaction(tx =>
        {
            fixture.Runs.Update(legacy, 1, tx);
            fixture.Revisions.Append(new RunRevision
            {
                RevisionId = NewId(), RunId = original.RunId, Revision = 2,
                ChangedAtUtc = fixture.Database.Clock.UtcNow, ChangeKind = ChangeKind.Correct,
                Actor = RevisionActor.User, Reason = "旧版副本更正", RequestId = NewId(),
                Changes = new[]
                {
                    new RunFieldChange(RunFields.ContentId, null, 2),
                    new RunFieldChange(RunFields.DutyName, original.DutyName, legacy.DutyName),
                },
            }, tx);
            return 0;
        });

        var error = Assert.Throws<CollectorException>(() => fixture.Service.UndoRevision(
            new RunReasonCommand(NewId(), original.RunId, 2, "尝试撤销旧版副本更正")));
        Assert.Equal(ErrorCodes.UndoNotAllowed, error.Code);
        Assert.Equal(legacy, fixture.Runs.Get(original.RunId));
        Assert.Equal(2, fixture.Revisions.ListForRun(original.RunId, 1, 50).Total);
    }

    [Fact]
    public void AutomaticMerge_PreservesManualIdentityButAllowsUnrelatedFieldsAndUndoReleasesIt()
    {
        using var fixture = new Fixture(null, 1039, DutySource.Territory);
        var corrected = fixture.CorrectDuty();
        var protection = new ManualRunFieldProtection(fixture.Database.Database);
        var proposed = corrected with
        {
            ContentId = 1, TerritoryId = 1039, DutySource = DutySource.ContentId,
            DutyName = "自动副本", JobId = 21, JobName = "战士", Role = Role.Tank,
        };
        var merged = fixture.Database.Database.RunInTransaction(tx => protection.Merge(corrected, proposed, tx));
        AssertDuty(corrected, merged);
        Assert.Equal(21, merged.JobId);
        Assert.Equal("战士", merged.JobName);

        fixture.Service.UndoRevision(new RunReasonCommand(NewId(), corrected.RunId, 2, "撤销副本更正"));
        var restored = fixture.Runs.Get(corrected.RunId)!;
        merged = fixture.Database.Database.RunInTransaction(tx => protection.Merge(restored, proposed, tx));
        AssertDuty(proposed, merged);
    }

    [Fact]
    public void LegacyNonDutyDiff_DoesNotGuessAnUnrecordedIdentityChange()
    {
        using var fixture = new Fixture(2, 1037, DutySource.Territory);
        var original = fixture.Original;
        // An old request resubmitted the same content id alongside a note. Its audit
        // retained only the note, losing the provenance change. That history is no
        // longer distinguishable from a genuine note-only revision after an auto update.
        var legacy = original with { Revision = 2, DutySource = DutySource.Manual, Note = "旧版备注" };
        fixture.Database.Database.RunInTransaction(tx =>
        {
            fixture.Runs.Update(legacy, 1, tx);
            fixture.Revisions.Append(new RunRevision
            {
                RevisionId = NewId(), RunId = original.RunId, Revision = 2,
                ChangedAtUtc = fixture.Database.Clock.UtcNow, ChangeKind = ChangeKind.Correct,
                Actor = RevisionActor.User, Reason = "旧版备注修订", RequestId = NewId(),
                Changes = new[] { new RunFieldChange(RunFields.Note, original.Note, legacy.Note) },
            }, tx);
            return 0;
        });

        fixture.Service.UndoRevision(new RunReasonCommand(NewId(), original.RunId, 2, "撤销备注"));
        var restored = fixture.Runs.Get(original.RunId)!;
        Assert.Equal(original.Note, restored.Note);
        Assert.Equal(legacy.DutySource, restored.DutySource);
        Assert.Equal(legacy.ContentId, restored.ContentId);
        Assert.Equal(legacy.TerritoryId, restored.TerritoryId);
    }

    [Theory]
    [InlineData("territory_id")]
    [InlineData("duty_source")]
    [InlineData("duty_identity")]
    public void ClientCannotCorrectInternalIdentityFields(string field)
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Changes(
            new PayloadReader(new JsonObject { [field] = null })));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"content_id\":1,\"territory_id\":2,\"duty_source\":\"bad\"}")]
    public void IncompleteOrInvalidIdentitySnapshotIsRefused(string snapshot)
    {
        var error = Assert.Throws<CollectorException>(() => RunFieldWriter.Apply(
            TestDatabase.Run(), RunAuditFields.DutyIdentity, snapshot));
        Assert.Equal(ErrorCodes.Internal, error.Code);
    }

    [Fact]
    public void RevisionWire_ExposesBusinessChangesWithoutInternalIdentitySnapshot()
    {
        using var fixture = new Fixture(null, 1039, DutySource.Territory);
        fixture.CorrectDuty();
        var revision = fixture.Revisions.GetAt(fixture.Original.RunId, 2, null)!;
        var fields = Wire.Revision(revision)["changes"]!.AsArray()
            .Select(change => change!["field"]!.GetValue<string>()).ToArray();
        Assert.Contains(RunFields.ContentId, fields);
        Assert.Contains(RunFields.DutyName, fields);
        Assert.DoesNotContain(RunAuditFields.DutyIdentity, fields);
        Assert.Contains(revision.Changes, change => change.Field == RunAuditFields.DutyIdentity);
    }

    private static void AssertDuty(MentorRun expected, MentorRun actual)
    {
        Assert.Equal(expected.ContentId, actual.ContentId);
        Assert.Equal(expected.TerritoryId, actual.TerritoryId);
        Assert.Equal(expected.DutySource, actual.DutySource);
        Assert.Equal(expected.DutyName, actual.DutyName);
        Assert.Equal(expected.DutyCategory, actual.DutyCategory);
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private sealed class Fixture : IDisposable
    {
        public TestDatabase Database { get; } = new();
        public RunRepository Runs { get; }
        public RunRevisionRepository Revisions { get; }
        public RunMutationService Service { get; }
        public MentorRun Original { get; }

        public Fixture(int? contentId, int? territoryId, DutySource? source)
        {
            Runs = new RunRepository(Database.Database);
            Revisions = new RunRevisionRepository(Database.Database);
            var settings = new SettingsRepository(Database.Database, Database.Clock);
            settings.EnsureDefaults();
            Service = new RunMutationService(Database.Database, settings, Database.Clock);
            Original = TestDatabase.Run(contentId: contentId) with
            {
                TerritoryId = territoryId, DutySource = source,
                DutyName = "原始副本", DutyCategory = "原始分类",
            };
            Database.Database.RunInTransaction(tx =>
            {
                Runs.Insert(Original, tx);
                Revisions.Append(new RunRevision
                {
                    RevisionId = NewId(), RunId = Original.RunId, Revision = 1,
                    ChangedAtUtc = Database.Clock.UtcNow, ChangeKind = ChangeKind.CreateManual,
                    Actor = RevisionActor.User, Reason = "测试初始记录", RequestId = NewId(),
                    Changes = Array.Empty<RunFieldChange>(),
                }, tx);
                return 0;
            });
        }

        public MentorRun CorrectDuty()
        {
            Service.CorrectRun(new CorrectRunCommand(NewId(), Original.RunId, 1, "选择另一个副本",
                new RunChangeSet { ContentId = 2, Specified = new HashSet<string> { RunFields.ContentId } }));
            return Runs.Get(Original.RunId)!;
        }

        public void Dispose() => Database.Dispose();
    }
}
