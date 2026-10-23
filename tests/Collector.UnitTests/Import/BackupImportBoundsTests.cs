using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Import;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>不可信 SQLite 文件不能通过重复连接键放大预览查询的中间结果。</summary>
public sealed class BackupImportBoundsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.BackupBounds." + Guid.NewGuid().ToString("N"));
    private static readonly string[] RunFields =
    [
        "run_id", "region", "content_id", "duty_name", "job_id", "job_name", "role", "matched_at_utc",
        "entered_at_utc", "ended_at_utc", "duration_ms", "result", "source", "soft_deleted", "created_at_utc", "updated_at_utc",
    ];

    public BackupImportBoundsTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("mentor_runs", false)]
    [InlineData("run_reflections", false)]
    [InlineData("run_import_metadata", false)]
    [InlineData("mentor_runs", true)]
    [InlineData("run_reflections", true)]
    [InlineData("run_import_metadata", true)]
    public void MissingOrCompositeIdentityKeyIsRejectedBeforeJoining(string malformedTable, bool composite)
    {
        var path = Path.Combine(_directory, "duplicate-identities.db");
        using (var connection = Open(path))
        {
            foreach (var (table, fields) in new[]
            {
                ("mentor_runs", RunFields),
                ("run_reflections", new[] { "run_id", "mood", "text" }),
                ("run_import_metadata", new[] { "run_id", "source_name", "source_recorded_at", "source_recorded_at_utc", "mentor_confirmed" }),
            })
            {
                using var command = connection.CreateCommand();
                // 两条重复键就能证明接受路径；不运行巨量笛卡尔连接来测拒绝行为。
                var primaryKey = table != malformedTable ? ", PRIMARY KEY(run_id)" :
                    composite ? ", PRIMARY KEY(run_id, discriminator)" : string.Empty;
                command.CommandText = "CREATE TABLE " + table + " (" + string.Join(',', fields.Select(field => field + " TEXT")) +
                    ", discriminator INTEGER" + primaryKey + ");";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO " + table + " (run_id,discriminator) VALUES ('synthetic-key',0);" +
                    (table == malformedTable ? "INSERT INTO " + table + " (run_id,discriminator) VALUES ('synthetic-key',1);" : string.Empty);
                command.ExecuteNonQuery();
            }
        }

        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("BACKUP", path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public void VirtualTableCannotMasqueradeAsTheHistoryTable()
    {
        var path = Path.Combine(_directory, "virtual-history.db");
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIRTUAL TABLE mentor_runs USING fts5(" + string.Join(',', RunFields) + ");" +
                "INSERT INTO mentor_runs(run_id) VALUES('synthetic-key');";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("BACKUP", path));
    }

    [Theory]
    [InlineData("run_reflections")]
    [InlineData("run_import_metadata")]
    public void ExternalColumnCollationCannotMakeSeveralIdentityRowsMatch(string unusualTable)
    {
        var path = Path.Combine(_directory, "column-collation.db");
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE mentor_runs (" + string.Join(',', RunFields.Select(field => field + " TEXT")) +
                ", PRIMARY KEY(run_id)); INSERT INTO mentor_runs(run_id) VALUES('synthetic-key');" +
                "CREATE TABLE " + unusualTable + " (run_id TEXT COLLATE NOCASE," +
                (unusualTable == "run_reflections" ? "mood TEXT,text TEXT," :
                    "source_name TEXT,source_recorded_at TEXT,source_recorded_at_utc TEXT,mentor_confirmed INTEGER,") +
                "PRIMARY KEY(run_id COLLATE BINARY));" +
                "INSERT INTO " + unusualTable + "(run_id) VALUES('synthetic-key'),('SYNTHETIC-KEY');";
            command.ExecuteNonQuery();
        }

        var row = Assert.Single(RunImportSourceParser.Parse("BACKUP", path));
        Assert.Equal("synthetic-key", row.Values["run_id"]!.GetValue<string>());
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
