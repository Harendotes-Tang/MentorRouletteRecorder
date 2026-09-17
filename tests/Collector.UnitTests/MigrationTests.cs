using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;
using Xunit;

namespace MentorRecorder.Collector.UnitTests;

public sealed class MigrationTests
{
    [Fact]
    public void Open_InitializesSchemaAndRecordsChecksum()
    {
        using var fixture = new TestDatabase();

        Assert.Equal(MigrationRunner.LatestVersion, fixture.Database.SchemaVersion);
        Assert.Equal(8, MigrationRunner.LatestVersion);

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
}
