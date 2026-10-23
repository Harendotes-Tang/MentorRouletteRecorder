using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidatePayloadMigrationTests
{
    private const string MaximumId = "00000000-0000-4000-8000-000000000001";
    private const string NullId = "00000000-0000-4000-8000-000000000002";
    private const string EmptyId = "00000000-0000-4000-8000-000000000003";
    private const string AnchorId = "00000000-0000-4000-8000-000000000004";
    private const string WindowId = "00000000-0000-4000-8000-000000000005";
    private const string Stamp = "2026-09-04T01:02:03.456Z";

    // 固定已发布迁移脚本的摘要：若脚本被意外改写，旧库不得以新摘要冒充兼容。
    private static readonly string[] Schema6Checksums =
    [
        "0ba4d79a647699a74b4d595f44a808ce7f354fa238d243918133505da1a93962",
        "d3f2a356c1d58d716463ce98f115a3e5eb0731bd9339590b5e414b7cafea6be1",
        "fb9c0adca2c4f48510c10134c3ad5cdfed56b5ed02fd08535e94af435e3c255d",
        "f270c6ae61bb07c61461d6932066789fc7be6ae9ca9d28b6bd75b0a8bf1d332c",
        "73f074b0c24f1c75954d417006732314a88d08be1924b705a08e2f9f8bbd650a",
        "656df4c3090c4f36ca189ed906d6c5dee71c8a3946015617a94620d643eb9464",
    ];

    [Fact]
    public void Schema6UpgradePreservesEvidenceReviewOrderAndSchemaObjectsAcrossReopen()
    {
        using var fixture = new Schema6Database();
        EvidenceSnapshot before;
        using (var legacy = fixture.Connect())
        {
            before = Snapshot(legacy);
            Assert.Equal(6L, Scalar(legacy, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(5, before.Observations.Length);
            Assert.Equal(4, before.Reviews.Length);
        }

        string[] migratedHistory;
        using (var database = SqliteDatabase.Open(fixture.Path, fixture.Clock))
        {
            Assert.Equal(MigrationRunner.LatestVersion, database.SchemaVersion);
            database.Read(connection =>
            {
                AssertPreserved(before, Snapshot(connection));
                AssertDatabaseHealthy(connection);
                AssertPayloadBoundary(connection, 512);
                AssertReviewConstraints(connection);
                return true;
            });

            // 使用实际证据导出入口验证同时间审阅仍按 rowid 排序，而非 review_id 重排。
            new CandidateObservationRepository(database, fixture.Clock).VisitResearchEvidence(
                (_, observationCount, reviewCount, observations, reviews) =>
                {
                    Assert.Equal(5, observationCount);
                    Assert.Equal(4, reviewCount);
                    var entries = observations.ToArray();
                    Assert.Equal(new string('a', 512), entries.Single(x => x.Observation.ObservationId == MaximumId).Observation.PayloadHex);
                    Assert.Equal(string.Empty, entries.Single(x => x.Observation.ObservationId == EmptyId).Observation.PayloadHex);
                    Assert.Null(entries.Single(x => x.Observation.ObservationId == AnchorId).Observation.PayloadHex);
                    Assert.Equal(37, entries.Single(x => x.Observation.ObservationId == WindowId).Observation.Occurrences);
                    Assert.Equal(new[] { "review-z", "review-a", "review-m", "review-other" },
                        reviews.Select(x => x.ReviewId).ToArray());
                });
            migratedHistory = database.Read(connection => Rows(connection,
                "SELECT version, name, checksum, applied_at_utc FROM schema_migrations ORDER BY version;"));
            Assert.Equal(MigrationRunner.LatestVersion, migratedHistory.Length);
            var migration = Assert.Single(MigrationRunner.Scripts, script => script.Version == 7);
            Assert.Contains(migration.Checksum, migratedHistory[6]);
        }

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        using var reopened = SqliteDatabase.Open(fixture.Path, fixture.Clock);
        reopened.Read(connection =>
        {
            AssertPreserved(before, Snapshot(connection));
            Assert.Equal(migratedHistory, Rows(connection,
                "SELECT version, name, checksum, applied_at_utc FROM schema_migrations ORDER BY version;"));
            AssertDatabaseHealthy(connection);
            return true;
        });
    }

    [Fact]
    public void FailureAfterPayloadReplacementRollsBackSchemaDataAndMigrationHistory()
    {
        using var fixture = new Schema6Database();
        using var connection = fixture.Connect();
        // 版本记录发生在整段迁移 SQL 之后；这里失败能覆盖列替换及数据回填后的回滚。
        Execute(connection, """
            CREATE TRIGGER reject_schema7 BEFORE INSERT ON schema_migrations
            WHEN NEW.version = 7
            BEGIN SELECT RAISE(ABORT, 'injected schema 7 failure'); END;
            """);
        var before = Snapshot(connection);
        var schemaBefore = Rows(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;");

        var error = Assert.Throws<CollectorException>(() => MigrationRunner.MigrateToLatest(connection, fixture.Clock));

        Assert.Equal(ErrorCodes.DbIntegrity, error.Code);
        Assert.Contains("injected schema 7 failure", Assert.IsType<SqliteException>(error.InnerException).Message);
        AssertPreserved(before, Snapshot(connection));
        Assert.Equal(schemaBefore, Rows(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;"));
        Assert.Equal(6L, Scalar(connection, "SELECT MAX(version) FROM schema_migrations;"));
        AssertDatabaseHealthy(connection);
        AssertPayloadBoundary(connection, 256);
        AssertReviewConstraints(connection);

        Execute(connection, "DROP TRIGGER reject_schema7;");
        Assert.Equal(MigrationRunner.LatestVersion, MigrationRunner.MigrateToLatest(connection, fixture.Clock));
        AssertPreserved(before, Snapshot(connection));
        AssertPayloadBoundary(connection, 512);
        AssertDatabaseHealthy(connection);
    }

    private static void AssertPayloadBoundary(SqliteConnection connection, int maximum)
    {
        using var transaction = connection.BeginTransaction();
        const string update = "UPDATE candidate_observations SET payload_length = $length, payload_hex = $hex WHERE observation_id = $id;";
        Assert.Equal(1, Execute(connection, update, transaction,
            ("$length", maximum), ("$hex", new string('b', maximum * 2)), ("$id", NullId)));
        var error = Assert.Throws<SqliteException>(() => Execute(connection, update, transaction,
            ("$length", maximum + 1), ("$hex", new string('b', (maximum + 1) * 2)), ("$id", NullId)));
        Assert.Equal(19, error.SqliteErrorCode);
        transaction.Rollback();
    }

    private static void AssertReviewConstraints(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        var update = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE candidate_reviews SET note = 'changed' WHERE review_id = 'review-z';", transaction));
        Assert.Contains("append-only", update.Message);
        var orphan = Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO candidate_reviews (review_id, observation_id, request_id, verdict, reviewed_at_utc)
            VALUES ('orphan', 'missing', 'request-orphan', 'UNSURE', '2026-09-04T01:02:03.456Z');
            """, transaction));
        Assert.Equal(19, orphan.SqliteErrorCode);
        Execute(connection, "DELETE FROM candidate_observations WHERE observation_id = $id;", transaction, ("$id", MaximumId));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM candidate_reviews;", transaction));
        Assert.Equal("review-other", Scalar(connection, "SELECT review_id FROM candidate_reviews;", transaction));
        transaction.Rollback();
    }

    private static void AssertDatabaseHealthy(SqliteConnection connection)
    {
        Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
        Assert.Empty(Rows(connection, "PRAGMA foreign_key_check;"));
        Assert.Empty(Rows(connection, "SELECT name FROM sqlite_temp_master WHERE type = 'table';"));
    }

    private sealed record EvidenceSnapshot(string[] Observations, string[] Reviews, string[] Objects,
        string[] ForeignKeys, string[] History);

    private static EvidenceSnapshot Snapshot(SqliteConnection connection) => new(
        Rows(connection, """
            SELECT rowid, observation_id, capture_session_id, profile_id, hypothesis_name, group_name,
                direction, opcode, payload_length, payload_hash12, connection_tag, observed_at_utc, t_ms,
                first_observed_at_utc, last_observed_at_utc, first_t_ms, last_t_ms,
                review_verdict, review_note, reviewed_at_utc, payload_hex, occurrences
            FROM candidate_observations ORDER BY rowid;
            """),
        Rows(connection, "SELECT rowid, * FROM candidate_reviews ORDER BY reviewed_at_utc, rowid;"),
        Rows(connection, """
            SELECT type, name, tbl_name, sql FROM sqlite_master
            WHERE type IN ('index', 'trigger') AND tbl_name IN ('candidate_observations', 'candidate_reviews')
            ORDER BY type, name;
            """),
        Rows(connection, "PRAGMA foreign_key_list(candidate_reviews);"),
        Rows(connection, "SELECT version, name, checksum, applied_at_utc FROM schema_migrations WHERE version <= 6 ORDER BY version;"));

    private static void AssertPreserved(EvidenceSnapshot expected, EvidenceSnapshot actual)
    {
        Assert.Equal(expected.Observations, actual.Observations);
        Assert.Equal(expected.Reviews, actual.Reviews);
        Assert.Equal(expected.Objects, actual.Objects);
        Assert.Equal(expected.ForeignKeys, actual.ForeignKeys);
        Assert.Equal(expected.History, actual.History);
    }

    private static string[] Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.ToArray();
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    private static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private sealed class Schema6Database : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "MentorRecorder.PayloadMigrationTests", Guid.NewGuid().ToString("N"));

        public Schema6Database()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "schema6.db");
            using var connection = Connect();
            using (var transaction = connection.BeginTransaction())
            {
                var scripts = MigrationRunner.Scripts.Where(script => script.Version <= 6).ToArray();
                Assert.Equal(Enumerable.Range(1, 6), scripts.Select(script => script.Version));
                foreach (var script in scripts)
                {
                    Assert.Equal(Schema6Checksums[script.Version - 1], script.Checksum);
                    Execute(connection, script.Sql, transaction);
                    Execute(connection, """
                        INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                        VALUES ($version, $name, $checksum, $applied);
                        """, transaction, ("$version", script.Version), ("$name", script.Name),
                        ("$checksum", Schema6Checksums[script.Version - 1]), ("$applied", Stamp));
                }
                transaction.Commit();
            }
            Seed(connection);
        }

        public string Path { get; }
        public TestClock Clock { get; } = new(UtcTimestamp.Parse(Stamp));

        public SqliteConnection Connect()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                ForeignKeys = true,
                Pooling = false,
            }.ConnectionString);
            connection.Open();
            return connection;
        }

        private static void Seed(SqliteConnection connection)
        {
            const string packet = """
                INSERT INTO candidate_observations (rowid, observation_id, capture_session_id, profile_id,
                    hypothesis_name, direction, opcode, payload_length, payload_hash12, connection_tag,
                    observed_at_utc, t_ms, payload_hex, occurrences)
                VALUES ($rowid, $id, 'session', 'candidate-test', $hypothesis, 'S2C', 803,
                    $length, '0123456789ab', 'abcdef012345', $stamp, 1000, $hex, $occurrences);
                """;
            using var transaction = connection.BeginTransaction();
            foreach (var (rowid, id, length, hex, hypothesis, occurrences) in new[]
            {
                (101, MaximumId, 256, (string?)new string('a', 512), "FINDER_STATE_NOTIFICATION", 1),
                (203, NullId, 257, (string?)null, "FINDER_STATE_NOTIFICATION", 1),
                (309, EmptyId, 0, (string?)string.Empty, "FINDER_STATE_NOTIFICATION", 1),
                (509, WindowId, 456, (string?)null, "QUEUE_WINDOW_SAMPLE", 37),
            })
                Execute(connection, packet, transaction, ("$rowid", rowid), ("$id", id), ("$length", length),
                    ("$hex", hex), ("$hypothesis", hypothesis), ("$occurrences", occurrences), ("$stamp", Stamp));
            Execute(connection, """
                INSERT INTO candidate_observations (rowid, observation_id, capture_session_id, profile_id,
                    hypothesis_name, group_name, direction, connection_tag, observed_at_utc, t_ms,
                    first_observed_at_utc, last_observed_at_utc, first_t_ms, last_t_ms)
                VALUES (407, $id, 'session', 'candidate-test', 'ZONE_LOAD', 'zone_load', 'NONE',
                    'abcdef012345', $stamp, 3000, '2026-09-04T01:02:01.456Z', $stamp, 1000, 3000);
                """, transaction, ("$id", AnchorId), ("$stamp", Stamp));
            Execute(connection, """
                INSERT INTO candidate_reviews (rowid, review_id, observation_id, request_id, verdict, note, reviewed_at_utc)
                VALUES (41, 'review-z', $maximum, 'request-z', 'UNSURE', NULL, $stamp),
                       (99, 'review-a', $maximum, 'request-a', 'WRONG', '旧证据', $stamp),
                       (207, 'review-m', $maximum, 'request-m', 'CORRECT', '最终核对', $stamp),
                       (301, 'review-other', $null, 'request-other', 'UNSURE', NULL, $stamp);
                UPDATE candidate_observations
                SET review_verdict = 'CORRECT', review_note = '最终核对', reviewed_at_utc = $stamp
                WHERE observation_id = $maximum;
                """, transaction, ("$maximum", MaximumId), ("$null", NullId), ("$stamp", Stamp));
            transaction.Commit();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* Windows 可能尚未释放句柄；该隔离测试目录留待系统清理。 */ }
        }
    }
}
