using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateSettingsAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFieldFailureRollsBackEverySetting(bool candidateEnabled)
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        var before = CaptureSettingsStore.Apply(settings, Initial(candidateEnabled));
        RejectRegionWrite(db);

        Assert.Throws<SqliteException>(() => CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate
        {
            CandidateValidationEnabled = !candidateEnabled,
            FollowGame = false,
            Autostart = true,
            AdapterSpecified = true,
            AdapterId = "new-adapter",
            LogRetentionDays = 30,
            AllowWithoutProfile = true,
            RegionOverrideSpecified = true,
            RegionOverride = Region.Global,
        }));

        Assert.Equal(before, CaptureSettingsStore.Read(settings));
        db.Reopen();
        Assert.Equal(before, CaptureSettingsStore.Read(new SettingsRepository(db.Database, db.Clock)));
    }

    [Fact]
    public void FailedDisableKeepsPersistedAndRunningCandidateModesAligned()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        CaptureSettingsStore.Apply(settings, Initial(candidateEnabled: true));
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
                null, Region.Cn, "test-build", "synthetic atomicity test"));
        pipeline.OnCaptureStarted("session");
        RejectRegionWrite(db);
        var update = new CaptureSettingsUpdate
        {
            CandidateValidationEnabled = false,
            RegionOverrideSpecified = true,
            RegionOverride = Region.Global,
        };

        Assert.Throws<SqliteException>(() => pipeline.ApplyCandidateSettings(false,
            () => CaptureSettingsStore.Apply(settings, update)));
        Assert.True(pipeline.CandidateValidationEnabled);
        Assert.True(CaptureSettingsStore.Read(settings).CandidateValidationEnabled);

        Execute(db, "DROP TRIGGER reject_region_write;");
        pipeline.ApplyCandidateSettings(false, () => CaptureSettingsStore.Apply(settings, update));
        Assert.False(pipeline.CandidateValidationEnabled);
        Assert.False(CaptureSettingsStore.Read(settings).CandidateValidationEnabled);
    }

    [Fact]
    public void LaterWriteFailurePreservesBothPersistedAndRunningResearchWhitelist()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate
        {
            CandidateValidationEnabled = true,
            ResearchPayloadOpcodes = new[] { "0x03bb" },
        });
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, new LiveEventBus(db.Clock),
            _ => new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed,
                null, Region.Cn, "2026.08.05.0000.0000", "test"));
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "2026.08.05.0000.0000" });
        var rows = new List<CandidateObservation>();
        pipeline.CandidateObserved += rows.Add;
        var session = Guid.NewGuid().ToString("D");
        pipeline.OnCaptureStarted(session);
        RejectRegionWrite(db);
        var update = new CaptureSettingsUpdate
        {
            ResearchPayloadOpcodes = new[] { "0x0323" },
            RegionOverrideSpecified = true,
            RegionOverride = Region.Global,
        };
        Assert.Throws<SqliteException>(() => pipeline.ApplyCandidateSettings(null,
            () => CaptureSettingsStore.Apply(settings, update), update.ResearchPayloadOpcodes));
        Assert.Equal(new[] { "0x03bb" }, CaptureSettingsStore.Read(settings).ResearchPayloadOpcodes);
        pipeline.Accept(new DecodedMessage(session, MessageDirection.Outbound, db.Clock.UtcNow,
            TimeSpan.FromMilliseconds(1), 0, 3, 0x03bb, new byte[128], "synthetic connection"));
        Assert.Equal(new string('0', 256), Assert.Single(rows).PayloadHex);
    }

    [Fact]
    public void ProjectionFailureAlsoRollsBackTheSettingWrite()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "true");

        Assert.Throws<InvalidOperationException>(() => settings.SetSettings<int>(
            new Dictionary<string, string> { [CaptureSettingsStore.CandidateValidationSetting] = "false" },
            transaction =>
            {
                Assert.Equal("false", settings.GetSetting(CaptureSettingsStore.CandidateValidationSetting, transaction));
                throw new InvalidOperationException("synthetic projection failure");
            }));

        Assert.Equal("true", settings.GetSetting(CaptureSettingsStore.CandidateValidationSetting));
    }

    [Fact]
    public void SuccessfulPartialUpdatePreservesOmittedValuesAndAcceptsExplicitNull()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        var before = CaptureSettingsStore.Apply(settings, Initial(candidateEnabled: true));

        var after = CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate
        {
            CandidateValidationEnabled = false,
            AdapterSpecified = true,
            AdapterId = null,
            RegionOverrideSpecified = true,
            RegionOverride = null,
        });

        Assert.Equal(before with { CandidateValidationEnabled = false, AdapterId = null, RegionOverride = null }, after);
        Assert.Equal(after, CaptureSettingsStore.Read(settings));
    }

    private static CaptureSettingsUpdate Initial(bool candidateEnabled) => new()
    {
        CandidateValidationEnabled = candidateEnabled,
        FollowGame = true,
        Autostart = false,
        AdapterSpecified = true,
        AdapterId = "original-adapter",
        LogRetentionDays = 7,
        AllowWithoutProfile = false,
        RegionOverrideSpecified = true,
        RegionOverride = Region.Cn,
    };

    private static void RejectRegionWrite(TestDatabase db) => Execute(db, """
        CREATE TRIGGER reject_region_write BEFORE INSERT ON application_settings
        WHEN NEW.key = 'capture.region_override'
        BEGIN
            SELECT RAISE(ABORT, 'synthetic later-setting write failure');
        END;
        """);

    private static void Execute(TestDatabase db, string sql) => db.Database.RunInTransaction(transaction =>
    {
        using var command = db.Database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    });
}
