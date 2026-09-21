using System.Data;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;

namespace MentorRecorder.Collector.Storage;

/// <summary>
/// The one and only writer of the SQLite file (docs/architecture.md section 2.1).
///
/// Opening a database always performs, in this order: pragma setup, an integrity check, and
/// the forward migration inside a single transaction. Any failure surfaces as
/// <c>ERR_DB_INTEGRITY</c> rather than as a partially migrated file.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    private SqliteDatabase(SqliteConnection connection, string path, int schemaVersion)
    {
        _connection = connection;
        Path = path;
        SchemaVersion = schemaVersion;
    }

    /// <summary>Absolute path of the database file.</summary>
    public string Path { get; }

    /// <summary>Schema version after migration.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Opens (creating when absent) the database at <paramref name="path"/> and brings it to
    /// the highest schema version this build supports.
    /// </summary>
    /// <param name="path">Database file path.</param>
    /// <param name="clock">Clock used to stamp migration rows.</param>
    public static SqliteDatabase Open(string path, IClock clock)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(clock);

        var fullPath = System.IO.Path.GetFullPath(path);
        DatabasePaths.EnsureParentDirectory(fullPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            ApplyPragmas(connection);
            VerifyIntegrity(connection, fullPath);
            var version = MigrationRunner.MigrateToLatest(connection, clock);
            return new SqliteDatabase(connection, fullPath, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        // WAL keeps readers from blocking the single writer; FULL synchronous is the
        // conservative choice for a file the user cannot regenerate. busy_timeout bounds
        // lock waiting so a stuck writer becomes ERR_DB_BUSY instead of a hang.
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = FULL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, "PRAGMA temp_store = MEMORY;");
    }

    private static void VerifyIntegrity(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = command.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            // This is not a read-only mode, whatever the message used to claim. Throwing here ends
            // Open before it ever returns an instance, CollectorHost passes it up unchanged, and the
            // process exits with code 3 without opening the pipe - so BackupDatabase, which is an
            // instance method on this class, is out of reach too (2026-09-21 full audit, finding 15).
            // The message therefore says only what is true and what the person can act on: the
            // software will not start, and the file is theirs to copy somewhere safe first.
            throw new CollectorException(
                ErrorCodes.DbIntegrity,
                "数据库完整性校验失败，本软件无法启动。" +
                "请先把数据库文件复制一份保存到别处，再排查问题。" +
                $"数据库文件：{path}",
                new Dictionary<string, object?> { ["integrity_check"] = result });
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Creates a command on the shared connection.</summary>
    public SqliteCommand CreateCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.CreateCommand();
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction, serialised against every other
    /// caller. Committed on success, rolled back on any exception.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">Unit of work; receives the open transaction.</param>
    public T RunInTransaction<T>(Func<SqliteTransaction, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            try
            {
                // BeginTransaction 本身也会因其他连接持锁而失败；与提交错误统一映射。
                // 未提交的事务由 Dispose 回滚，包括 work 抛出的非 SQLite 异常。
                using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable);
                var result = work(transaction);
                transaction.Commit();
                return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                throw new CollectorException(
                    ErrorCodes.DbBusy,
                    "数据库正忙，请稍后用相同的请求编号重试。",
                    retryable: true,
                    inner: ex);
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> inside a transaction with no result.</summary>
    /// <param name="work">Unit of work; receives the open transaction.</param>
    public void RunInTransaction(Action<SqliteTransaction> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RunInTransaction<object?>(tx =>
        {
            work(tx);
            return null;
        });
    }

    /// <summary>How long a manual mutation waits for a lock before it gives up, in ms.</summary>
    public const int DefaultBusyTimeoutMs = 5000;

    /// <summary>
    /// How long one live-capture write waits for a lock before it gives up, in ms.
    ///
    /// The capture path retries three times, so this bounds a locked-database stall at three
    /// seconds rather than fifteen, keeping the pipeline lock off the bounded queue that
    /// carries run-ending messages. Manual mutations keep
    /// <see cref="DefaultBusyTimeoutMs"/>.
    /// </summary>
    public const int LiveCaptureBusyTimeoutMs = 1000;

    /// <summary>
    /// Runs one live-capture unit of work with the short busy timeout, restoring the default
    /// afterwards. Serialised like every other transaction.
    ///
    /// Both budgets must move together: <c>PRAGMA busy_timeout</c> bounds SQLite's own busy
    /// handler, while Microsoft.Data.Sqlite retries every prepare/step until
    /// <see cref="SqliteCommand.CommandTimeout"/>, which defaults to the connection's
    /// <see cref="SqliteConnection.DefaultTimeout"/>. Lowering <c>DefaultTimeout</c> for the
    /// scope also covers the transaction's own <c>BEGIN IMMEDIATE</c>, which the provider
    /// creates without passing through this class.
    /// </summary>
    /// <param name="work">Unit of work; receives the open transaction.</param>
    public void RunLiveCaptureTransaction(Action<SqliteTransaction> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            var restoreTimeout = _connection.DefaultTimeout;
            _connection.DefaultTimeout = LiveCaptureCommandTimeoutSeconds;
            SetBusyTimeout(LiveCaptureBusyTimeoutMs);
            try
            {
                RunInTransaction(work);
            }
            finally
            {
                SetBusyTimeout(DefaultBusyTimeoutMs);
                _connection.DefaultTimeout = restoreTimeout;
            }
        }
    }

    /// <summary>
    /// Managed-layer retry budget used for the duration of a live-capture transaction, in
    /// seconds. Kept in step with <see cref="LiveCaptureBusyTimeoutMs"/>: the provider only
    /// accepts whole seconds, so one second is the smallest budget that is not "no budget".
    /// </summary>
    internal const int LiveCaptureCommandTimeoutSeconds = 1;

    /// <summary>Command timeout currently in force on the shared connection, in seconds.</summary>
    internal int CommandTimeoutSeconds => Read(_ => _connection.DefaultTimeout);

    /// <summary>Current <c>busy_timeout</c> of the shared connection, in milliseconds.</summary>
    public int BusyTimeoutMs => Read(_ =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private void SetBusyTimeout(int milliseconds)
    {
        using var command = _connection.CreateCommand();

        // The value is an int constant of this class, never anything a client sent; SQLite
        // does not accept a bound parameter in a PRAGMA.
        command.CommandText = "PRAGMA busy_timeout = " +
            milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
        command.ExecuteNonQuery();
    }

    /// <summary>Longest problem line <see cref="CheckIntegrity"/> reports.</summary>
    public const int MaxIntegrityDetailLength = 200;

    /// <summary>
    /// How long the integrity check's own connection waits for a lock. In WAL mode a reader never
    /// waits for the writer, so waiting at all is the exceptional case, and one second bounds it.
    /// </summary>
    private const int IntegrityCheckTimeoutSeconds = 1;

    /// <summary>
    /// Runs <c>PRAGMA integrity_check</c> on its own read-only connection to the file and returns
    /// whether it passed and its first line: <c>ok</c>, or the first problem SQLite named.
    /// Read-only: the check never repairs anything.
    /// </summary>
    /// <remarks>
    /// The check reads the whole file and can take seconds, so it deliberately does not take
    /// <c>_gate</c>: that lock is held by every live-capture write
    /// (<see cref="RunLiveCaptureTransaction"/>). In WAL mode the extra reader and the writer do
    /// not block each other, and the check sees a consistent snapshot. A file too damaged for the
    /// check to run is a failed check carrying SQLite's own message, not an exception; a lock held
    /// elsewhere is <c>ERR_DB_BUSY</c>.
    /// </remarks>
    public IntegrityCheckOutcome CheckIntegrity() => CheckIntegrity(OpenReadOnlyConnection);

    /// <summary>
    /// <see cref="CheckIntegrity()"/> over a connection supplied by <paramref name="openConnection"/>,
    /// so the tests can hand it a connection that is locked or gone.
    /// </summary>
    /// <param name="openConnection">Opens the connection the check runs on; disposed after use.</param>
    internal IntegrityCheckOutcome CheckIntegrity(Func<SqliteConnection> openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var connection = openConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            var first = reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
            var passed = string.Equals(first, "ok", StringComparison.OrdinalIgnoreCase);
            return new IntegrityCheckOutcome(passed, passed ? "ok" : ShortDetail(first));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new CollectorException(
                ErrorCodes.DbBusy, "数据库正忙，请稍后再校验。", retryable: true, inner: ex);
        }
        catch (SqliteException ex)
        {
            return new IntegrityCheckOutcome(false, ShortDetail(ex.Message));
        }
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = IntegrityCheckTimeoutSeconds,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The first line of <paramref name="text"/>, redacted like a log line and cut to
    /// <see cref="MaxIntegrityDetailLength"/> characters.
    /// </summary>
    /// <param name="text">What SQLite said.</param>
    internal static string ShortDetail(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        if (line.Length == 0)
        {
            line = "integrity_check returned nothing";
        }

        line = Diagnostics.RotatingFileLogger.Sanitize(line);
        return line.Length > MaxIntegrityDetailLength ? line[..MaxIntegrityDetailLength] : line;
    }

    /// <summary>Runs a read-only <paramref name="work"/> serialised against writers.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">Unit of work.</param>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            return work(_connection);
        }
    }

    /// <summary>
    /// Writes a consistent copy of the database to <paramref name="targetPath"/> using
    /// VACUUM INTO a temporary file, verifies it, then atomically publishes the copy.
    /// A failed write or integrity check preserves an existing destination.
    /// </summary>
    /// <param name="targetPath">Destination file; must not already exist unless <paramref name="overwrite"/> is set.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    public BackupOutcome BackupDatabase(string targetPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = System.IO.Path.GetFullPath(targetPath);
        try
        {
            using var pending = new AtomicExportFile(fullPath, overwrite);
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "VACUUM INTO $target;";
                command.Parameters.AddWithValue("$target", pending.TemporaryPath);
                command.ExecuteNonQuery();
            }

            var byteCount = new FileInfo(pending.TemporaryPath).Length;
            pending.Commit(VerifyCopy);
            return new BackupOutcome(fullPath, byteCount, IntegrityCheckPassed: true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed,
                "备份失败：目标路径不可写或磁盘空间不足。",
                new Dictionary<string, object?> { ["target_path"] = fullPath },
                inner: ex);
        }
    }

    private static bool VerifyCopy(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>Outcome of a database backup.</summary>
/// <param name="TargetPath">Absolute path written.</param>
/// <param name="ByteCount">Size of the written file.</param>
/// <param name="IntegrityCheckPassed">Whether the copy passed its own integrity check.</param>
public sealed record BackupOutcome(string TargetPath, long ByteCount, bool IntegrityCheckPassed);

/// <summary>Outcome of <see cref="SqliteDatabase.CheckIntegrity"/>.</summary>
/// <param name="Passed">True when SQLite answered <c>ok</c>.</param>
/// <param name="Detail"><c>ok</c>, or the first problem line, sanitized and at most 200 characters.</param>
public sealed record IntegrityCheckOutcome(bool Passed, string Detail);
