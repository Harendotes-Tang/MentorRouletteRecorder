using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class MigrationTests
{
    [Fact]
    public void Open_InitializesSchemaAndRecordsChecksum()
    {
        using var fixture = new TestDatabase();

        Assert.Equal(MigrationRunner.LatestVersion, fixture.Database.SchemaVersion);
        Assert.Equal(13, MigrationRunner.LatestVersion);

        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "SELECT version, name, length(checksum), applied_at_utc FROM schema_migrations " +
            "ORDER BY version;";
        using var reader = command.ExecuteReader();

        var applied = new List<(int Version, string Name)>();
        while (reader.Read())
        {
            applied.Add((reader.GetInt32(0), reader.GetString(1)));
            Assert.Equal(64, reader.GetInt32(2));
            Assert.True(UtcTimestamp.TryParse(reader.GetString(3), out _));
        }

        Assert.Equal(
            new[]
            {
                (1, "0001_initial.sql"),
                (2, "0002_pending_review_and_note.sql"),
                (3, "0003_run_reflections.sql"),
                (4, "0004_candidate_observations.sql"),
                (5, "0005_candidate_research_payload.sql"),
                (6, "0006_candidate_window_samples.sql"),
                (7, "0007_candidate_research_payload_512.sql"),
                (8, "0008_run_duty_source.sql"),
                (9, "0009_personal_record_import.sql"),
                (10, "0010_history_retention.sql"),
                (11, "0011_run_joined_in_progress.sql"),
                (12, "0012_regional_records.sql"),
                (13, "0013_legacy_region_cn.sql"),
            },
            applied);
    }

    /// <summary>
    /// Review finding M-5. <c>duty_source</c> records whether the duty on a run was observed
    /// as a content id, inferred from a territory, or typed by a person. It is nullable so
    /// every row written before this migration stays valid, and it accepts nothing outside
    /// the three declared tokens.
    /// </summary>
    [Fact]
    public void Migration_AddsANullableDutySourceWithAClosedVocabulary()
    {
        using var fixture = new TestDatabase();

        using (var columns = fixture.Database.CreateCommand())
        {
            columns.CommandText =
                "SELECT \"notnull\", dflt_value FROM pragma_table_info('mentor_runs') " +
                "WHERE name = 'duty_source';";
            using var reader = columns.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
        }

        var runs = new RunRepository(fixture.Database);
        var run = TestDatabase.Run();
        fixture.Database.RunInTransaction(tx => runs.Insert(run, tx));

        using var rejected = fixture.Database.CreateCommand();
        rejected.CommandText = "UPDATE mentor_runs SET duty_source = 'GUESS' WHERE run_id = $id;";
        rejected.Parameters.AddWithValue("$id", run.RunId);
        var error = Assert.Throws<SqliteException>(() => rejected.ExecuteNonQuery());
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void Migration_CreatesRunReflectionsWithItsDeclaredShape()
    {
        using var fixture = new TestDatabase();

        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "SELECT name FROM pragma_table_info('run_reflections') ORDER BY cid;";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(
            new[] { "run_id", "mood", "text", "created_at_utc", "updated_at_utc" },
            columns);
    }

    [Theory]
    // An undeclared mood, an empty text, a text past the 2000 character bound and a
    // timestamp without milliseconds: each is refused by the table itself, not by the code
    // above it, so no future write path can quietly bypass one.
    [InlineData("'great'", "'文字'", "'2026-09-04T01:02:03.456Z'")]
    [InlineData("'good'", "''", "'2026-09-04T01:02:03.456Z'")]
    [InlineData("'good'", "hex(randomblob(1200))", "'2026-09-04T01:02:03.456Z'")]
    [InlineData("'good'", "'文字'", "'2026-09-04T01:02:03Z'")]
    public void Migration_EnforcesTheReflectionConstraints(string mood, string text, string stamp)
    {
        using var fixture = new TestDatabase();
        var run = TestDatabase.Run();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => runs.Insert(run, tx));

        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "INSERT INTO run_reflections (run_id, mood, text, created_at_utc, updated_at_utc) " +
            $"VALUES ($run_id, {mood}, {text}, {stamp}, {stamp});";
        command.Parameters.AddWithValue("$run_id", run.RunId);

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Migration_RefusesAReflectionForARunThatDoesNotExist()
    {
        using var fixture = new TestDatabase();

        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "INSERT INTO run_reflections (run_id, mood, text, created_at_utc, updated_at_utc) " +
            "VALUES ('00000000-0000-4000-8000-0000000000ff', 'good', '文字', " +
            "'2026-09-04T01:02:03.456Z', '2026-09-04T01:02:03.456Z');";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Open_IsIdempotent_WhenSchemaIsCurrent()
    {
        using var fixture = new TestDatabase();

        fixture.Reopen();

        using var command = fixture.Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        Assert.Equal((long)MigrationRunner.LatestVersion, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Open_RejectsTamperedMigrationChecksum()
    {
        using var fixture = new TestDatabase();
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "UPDATE schema_migrations SET checksum = 'bad' WHERE version = 1;";
            command.ExecuteNonQuery();
        }

        fixture.Database.Dispose();
        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        AssertRefusalIsHonest(error, fixture);
    }

    [Fact]
    public void Open_RejectsASchemaFromANewerBuild()
    {
        using var fixture = new TestDatabase();
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO schema_migrations (version, name, checksum, applied_at_utc) " +
                "VALUES (9999, '9999_from_a_newer_build.sql', 'unread', '2026-09-21T00:00:00.000Z');";
            command.ExecuteNonQuery();
        }

        fixture.Database.Dispose();
        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        AssertRefusalIsHonest(error, fixture);
    }

    [Fact]
    public void Open_RejectsACorruptedFile()
    {
        using var fixture = new TestDatabase();

        // Land every page in the main file, then overwrite the second one with the database
        // closed - the corruption DatabaseIntegrityCheckTests already relies on.
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        fixture.Database.Dispose();
        using (var file = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(file.Length > 4096 * 2, "expected at least two pages after migration");
            file.Position = 4096;
            var junk = new byte[256];
            Array.Fill(junk, (byte)0xFF);
            file.Write(junk);
        }

        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        AssertRefusalIsHonest(error, fixture);
    }

    /// <summary>
    /// Audit 2026-10-03 OF-4. A file that is not an SQLite database fails inside SQLite on the very
    /// first statement. That used to leave Open as a raw SqliteException, which nothing above it
    /// caught; it is a refusal to open like every other, under the documented code.
    /// </summary>
    [Fact]
    public void Open_RefusesAFileThatIsNotADatabase()
    {
        using var fixture = new TestDatabase();
        fixture.Database.Dispose();
        File.Delete(fixture.Path + "-wal");
        File.Delete(fixture.Path + "-shm");
        File.WriteAllText(fixture.Path, new string('x', 4096));

        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        Assert.IsType<SqliteException>(error.InnerException);
        AssertRefusalIsHonest(error, fixture);
    }

    /// <summary>
    /// Another program holding the file locked is not damage: the same refusal to start, under the
    /// busy code, and still naming the file.
    /// </summary>
    [Fact]
    public void Open_RefusesALockedFileAsBusy()
    {
        using var fixture = new TestDatabase();
        fixture.Database.Dispose();
        using var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        holder.Open();
        using (var command = holder.CreateCommand())
        {
            command.CommandText = "PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbBusy, error.Code);
        Assert.Contains("无法启动", error.Message, StringComparison.Ordinal);
        Assert.Contains(System.IO.Path.GetFileName(fixture.Path), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed migration names the file to copy, like every other refusal to open.
    /// </summary>
    [Fact]
    public void Open_AFailedMigrationNamesTheFile()
    {
        using var fixture = new TestDatabase();
        using (var command = fixture.Database.CreateCommand())
        {
            // Version 8 was applied; forget it so the next open runs it again against the
            // column it already added, which fails inside the migration transaction.
            command.CommandText = "DELETE FROM schema_migrations WHERE version = 8;";
            command.ExecuteNonQuery();
        }

        fixture.Database.Dispose();
        var error = Assert.Throws<CollectorException>(() =>
            SqliteDatabase.Open(fixture.Path, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        AssertRefusalIsHonest(error, fixture);
    }

    /// <summary>
    /// Every refusal to open ends the process before the pipe is up, so the message may not
    /// promise a read-only mode nothing implements (2026-09-21 full audit, finding 15). It has to
    /// say the software will not start and name the file the user should copy first - that copy is
    /// the only backup available once the built-in one is out of reach.
    /// </summary>
    private static void AssertRefusalIsHonest(CollectorException error, TestDatabase fixture)
    {
        Assert.DoesNotContain("只读模式", error.Message, StringComparison.Ordinal);
        Assert.Contains("无法启动", error.Message, StringComparison.Ordinal);
        Assert.Contains(System.IO.Path.GetFileName(fixture.Path), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Schema 11. <c>joined_in_progress</c> is nullable so every row written before it stays valid
    /// and reads as "not seen", and it accepts nothing but 0 and 1.
    /// </summary>
    [Fact]
    public void Migration_AddsANullableJoinedInProgressFlagLimitedToZeroAndOne()
    {
        using var fixture = new TestDatabase();

        using (var columns = fixture.Database.CreateCommand())
        {
            columns.CommandText =
                "SELECT \"notnull\", dflt_value FROM pragma_table_info('mentor_runs') " +
                "WHERE name = 'joined_in_progress';";
            using var reader = columns.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
        }

        var runs = new RunRepository(fixture.Database);
        var run = TestDatabase.Run();
        fixture.Database.RunInTransaction(tx => runs.Insert(run, tx));
        Assert.Null(runs.Get(run.RunId)!.JoinedInProgress);

        using var rejected = fixture.Database.CreateCommand();
        rejected.CommandText = "UPDATE mentor_runs SET joined_in_progress = 2 WHERE run_id = $id;";
        rejected.Parameters.AddWithValue("$id", run.RunId);
        var error = Assert.Throws<SqliteException>(() => rejected.ExecuteNonQuery());
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void Migration_EnforcesUtcMillisecondsAndAppendOnlyRevisions()
    {
        using var fixture = new TestDatabase();
        using var command = fixture.Database.CreateCommand();
        command.CommandText =
            "INSERT INTO capture_sessions " +
            "(capture_session_id, started_at_utc, collector_version, region, profile_status) " +
            "VALUES ('00000000-0000-4000-8000-000000000001', '2026-09-04T01:02:03Z', 'test', 'CN', 'VERIFIED');";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void RegionalUpgradePreservesPopulatedHistoryAndSchemaObjects(int version)
    {
        using var fixture = new TestDatabase();
        var path = fixture.Path + ".legacy";
        using (var old = LegacyDatabase(path, fixture, version))
        {
            using var seed = old.CreateCommand();
            seed.CommandText = """
                INSERT INTO capture_sessions(capture_session_id,started_at_utc,collector_version,region)
                VALUES ('session','2026-09-04T01:02:03.456Z','legacy','CN');
                INSERT INTO mentor_runs(run_id,revision,capture_session_id,region,result,source,created_at_utc,updated_at_utc)
                VALUES ('cn',1,'session','CN','COMPLETED','MANUAL','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z'),
                       ('global',1,NULL,'GLOBAL','UNKNOWN','IMPORT','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z'),
                       ('unknown',1,NULL,'UNKNOWN','UNKNOWN','IMPORT','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                INSERT INTO run_events(event_id,run_id,sequence,occurred_at_utc,monotonic_offset_ms,event_type)
                VALUES ('event','cn',0,'2026-09-04T01:02:03.456Z',0,'test');
                INSERT INTO run_revisions(revision_id,run_id,revision,changed_at_utc,change_kind,actor,reason,changes_json)
                VALUES ('revision','cn',1,'2026-09-04T01:02:03.456Z','CREATE_MANUAL','USER','测试旧记录','[]');
                INSERT INTO achievement_settings(id,goal_count,baseline_completed_count,baseline_effective_at,updated_at_utc)
                VALUES (1,2400,1700,'2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                CREATE TABLE custom_audit (run_id TEXT);
                CREATE INDEX ix_custom_region ON mentor_runs(region,run_id);
                CREATE TRIGGER custom_run_update AFTER UPDATE OF result ON mentor_runs
                BEGIN INSERT INTO custom_audit VALUES (NEW.run_id); END;
                CREATE VIEW custom_history AS SELECT run_id,region FROM mentor_runs;
                CREATE INDEX ix_custom_goal ON achievement_settings(goal_count);
                """;
            seed.ExecuteNonQuery();
            if (version >= 11)
            {
                seed.CommandText = """
                    INSERT INTO run_reflections VALUES ('cn','good','心得','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                    INSERT INTO run_import_metadata(run_id,source_kind,source_fingerprint,imported_at_utc)
                    VALUES ('global','CSV','legacy-fingerprint','2026-09-04T01:02:03.456Z');
                    UPDATE mentor_runs SET joined_in_progress = 1, note = '原有备注' WHERE run_id = 'cn';
                    """;
                seed.ExecuteNonQuery();
            }
        }

        // The upgrade happens after the history was written (0013 compares creation with the schema-12 record).
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(1);
        using var database = SqliteDatabase.Open(path, fixture.Clock);
        var runs = new RunRepository(database);
        Assert.Equal(3, runs.Query(null, null, 1, 50).Total);
        Assert.Equal(Region.Cn, runs.Get("cn")!.Region);
        Assert.Equal(Region.Global, runs.Get("global")!.Region);
        // A regionless import from before schema 12 was a CN record; 0013 says so (audit 2026-11-03 H1-1).
        Assert.Equal(Region.Cn, runs.Get("unknown")!.Region);
        var settings = new SettingsRepository(database, fixture.Clock);
        settings.EnsureDefaults();
        Assert.Equal(1700, settings.GetAchievementSettings().BaselineCompletedCount);
        Assert.Equal(2400, settings.GetAchievementSettings().GoalCount);
        Assert.Equal(0, settings.GetAchievementSettings(region: Region.Tc).BaselineCompletedCount);
        using var check = database.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(check.ExecuteScalar());
        check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", check.ExecuteScalar());
        check.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT count(*) FROM run_events WHERE run_id='cn';";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT count(*) FROM run_revisions WHERE run_id='cn';";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN ('ix_custom_region','ix_custom_goal','custom_run_update','custom_history');";
        Assert.Equal(4L, check.ExecuteScalar());
        check.CommandText = "UPDATE mentor_runs SET result='UNKNOWN' WHERE run_id='cn'; SELECT count(*) FROM custom_audit;";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "UPDATE run_revisions SET actor='SYSTEM' WHERE revision_id='revision';";
        Assert.Throws<SqliteException>(() => check.ExecuteNonQuery());
        check.CommandText = "UPDATE mentor_runs SET region='TC' WHERE run_id='cn'; UPDATE capture_sessions SET region='TC';";
        check.ExecuteNonQuery();
        check.CommandText = "UPDATE mentor_runs SET region='WRONG' WHERE run_id='cn';";
        Assert.Throws<SqliteException>(() => check.ExecuteNonQuery());
        if (version >= 11)
        {
            Assert.Equal("心得", runs.Get("cn")!.Reflection!.Text);
            Assert.True(runs.Get("cn")!.JoinedInProgress);
            Assert.Equal("原有备注", runs.Get("cn")!.Note);
            Assert.Equal("legacy-fingerprint", runs.Get("global")!.ImportMetadata!.SourceFingerprint);
        }
    }

    [Fact]
    public void RegionalUpgradeRollsBackAndRestoresPragmasWhenForeignKeysAreInvalid()
    {
        using var fixture = new TestDatabase();
        using var old = LegacyDatabase(fixture.Path + ".invalid", fixture, 11);
        using var command = old.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF; " +
            "INSERT INTO run_events(event_id,run_id,sequence,occurred_at_utc,monotonic_offset_ms,event_type) " +
            "VALUES ('bad','missing',0,'2026-09-04T01:02:03.456Z',0,'test'); PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        var refusal = Assert.Throws<CollectorException>(() => MigrationRunner.MigrateToLatest(old, fixture.Clock));
        // The refusal names the file to copy, like every other one (audit 2026-11-03 H1-4).
        Assert.Equal(ErrorCodes.DbIntegrity, refusal.Code);
        Assert.Equal("startup.migration_failed", refusal.Text?.Key);
        Assert.Contains(System.IO.Path.GetFileName(fixture.Path + ".invalid"), refusal.Message, StringComparison.Ordinal);
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(11L, command.ExecuteScalar());
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "PRAGMA legacy_alter_table;";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM pragma_table_info('achievement_settings') WHERE name='region';";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    /// <summary>
    /// Audit 2026-11-03 H1-4: a region whitelist the rebuild does not recognise is refused naming the file.
    /// </summary>
    [Fact]
    public void RegionalUpgradeOfAnUnrecognisedTableNamesTheFile()
    {
        using var fixture = new TestDatabase();
        var path = fixture.Path + ".reworded";
        using (var old = LegacyDatabase(path, fixture, 11))
        {
            using var command = old.CreateCommand();
            command.CommandText = "PRAGMA writable_schema = ON; " +
                "UPDATE sqlite_schema SET sql = replace(sql, 'region IN (''CN'', ''GLOBAL'', ''UNKNOWN'')', " +
                "'region IN (''CN'',''GLOBAL'',''UNKNOWN'')') WHERE type = 'table' AND name = 'capture_sessions'; " +
                "PRAGMA writable_schema = OFF;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var refusal = Assert.Throws<CollectorException>(() => SqliteDatabase.Open(path, fixture.Clock).Dispose());

        Assert.Equal(ErrorCodes.DbIntegrity, refusal.Code);
        Assert.Equal("startup.migration_failed", refusal.Text?.Key);
        Assert.Contains(System.IO.Path.GetFileName(path), refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit 2026-11-03 H1-1. Until schema 12 the product served CN only, and 1.8.0 stored every hand-entered run and
    /// every import without a region column as UNKNOWN; those completions counted towards the one achievement, and a
    /// 「已包含」 import deducted them from its baseline, which 0012 moved to CN. After the upgrade they are CN records,
    /// so CN progress is what it was (statistics-definitions §4). A run a capture wrote keeps its session's region.
    /// </summary>
    [Fact]
    public void RegionlessManualAndImportedRunsCountTowardsChinaAfterTheUpgrade()
    {
        using var fixture = new TestDatabase();
        var path = fixture.Path + ".before-regions";
        using (var old = LegacyDatabase(path, fixture, 11))
        {
            using var seed = old.CreateCommand();
            seed.CommandText = """
                INSERT INTO achievement_settings(id,goal_count,baseline_completed_count,baseline_effective_at,updated_at_utc)
                VALUES (1,2000,200,'2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1500)
                INSERT INTO mentor_runs(run_id,revision,region,result,source,created_at_utc,updated_at_utc)
                SELECT printf('imported-%04d', i),1,'UNKNOWN','COMPLETED','IMPORT','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z' FROM n;
                INSERT INTO run_import_metadata(run_id,source_kind,source_fingerprint,imported_at_utc)
                SELECT run_id,'XLSX','fingerprint-' || run_id,'2026-09-04T01:02:03.456Z' FROM mentor_runs;
                INSERT INTO mentor_runs(run_id,revision,region,result,source,created_at_utc,updated_at_utc)
                VALUES ('manual',1,'UNKNOWN','LEFT_OR_ABANDONED','MANUAL','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                INSERT INTO capture_sessions(capture_session_id,started_at_utc,collector_version,region)
                VALUES ('session','2026-09-04T01:02:03.456Z','legacy','UNKNOWN');
                INSERT INTO mentor_runs(run_id,revision,capture_session_id,region,result,source,created_at_utc,updated_at_utc)
                VALUES ('captured',1,'session','UNKNOWN','UNKNOWN','AUTO_NETWORK','2026-09-04T01:02:03.456Z','2026-09-04T01:02:03.456Z');
                """;
            seed.ExecuteNonQuery();
        }

        // The upgrade happens after the history was written (0013 compares creation with the schema-12 record).
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(1);
        using var database = SqliteDatabase.Open(path, fixture.Clock);
        var settings = new SettingsRepository(database, fixture.Clock);
        settings.EnsureDefaults();
        var dashboard = new StatisticsRepository(database, settings, clock: fixture.Clock).GetDashboard();

        Assert.Equal(1700, dashboard.AchievementProgress);
        Assert.Equal(200, dashboard.BaselineCompletedCount);
        Assert.Equal(1500, dashboard.Achievements!.Single(row => row.Region == Region.Cn).CompletedCount);
        Assert.Equal(0, dashboard.Achievements!.Single(row => row.Region == Region.Unknown).CompletedCount);
        var runs = new RunRepository(database);
        Assert.Equal(Region.Cn, runs.Get("imported-0001")!.Region);
        Assert.Equal(Region.Cn, runs.Get("manual")!.Region);
        Assert.Equal(Region.Unknown, runs.Get("captured")!.Region);
    }

    /// <summary>
    /// Re-review 2026-11-03 RV2-4. A database that already reached schema 12 may hold runs stored as UNKNOWN on purpose
    /// (未知区服 picked in a schema-12 build, which deducted nothing from CN). 0013 moves only the runs created before the
    /// database reached schema 12.
    /// </summary>
    [Fact]
    public void RunsStoredAsUnknownAfterSchemaTwelveKeepTheirRegion()
    {
        using var fixture = new TestDatabase();
        var path = fixture.Path + ".schema12";
        using (var old = LegacyDatabase(path, fixture, 12))
        {
            using var seed = old.CreateCommand();
            seed.CommandText = """
                INSERT INTO mentor_runs(run_id,revision,region,result,source,created_at_utc,updated_at_utc)
                VALUES ('before-12',1,'UNKNOWN','COMPLETED','MANUAL','2026-09-03T01:02:03.456Z','2026-09-03T01:02:03.456Z'),
                       ('after-12',1,'UNKNOWN','COMPLETED','MANUAL','2026-09-05T01:02:03.456Z','2026-09-05T01:02:03.456Z');
                """;
            seed.ExecuteNonQuery();
        }

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        using var database = SqliteDatabase.Open(path, fixture.Clock);
        var runs = new RunRepository(database);

        Assert.Equal(Region.Cn, runs.Get("before-12")!.Region);
        Assert.Equal(Region.Unknown, runs.Get("after-12")!.Region);
    }

    private static SqliteConnection LegacyDatabase(string path, TestDatabase fixture, int version)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, ForeignKeys = true }.ConnectionString);
        connection.Open();
        foreach (var migration in MigrationRunner.Scripts.Where(script => script.Version <= version))
        {
            using var command = connection.CreateCommand();
            command.CommandText = migration.Sql;
            command.Parameters.AddWithValue("$migration_now_utc", UtcTimestamp.ToText(fixture.Clock.UtcNow));
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO schema_migrations VALUES ($version,$name,$checksum,$migration_now_utc);";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$checksum", migration.Checksum);
            command.ExecuteNonQuery();
        }
        return connection;
    }
}
