using Microsoft.Data.Sqlite;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>验证实际加载的 SQLite 原生库，而非仅核对托管包的版本声明。</summary>
public sealed class NativeSqliteSecurityTests
{
    [Fact]
    public void LoadedNativeLibraryIncludesTheCve20256965Fix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var nativeVersion = Assert.IsType<string>(command.ExecuteScalar());

        // CVE-2025-6965 修复始于 SQLite 3.50.2；旧原生 DLL 混入输出也必须失败。
        Assert.True(Version.TryParse(nativeVersion, out var parsed) && parsed >= new Version(3, 50, 2),
            $"Loaded vulnerable SQLite native library {nativeVersion}; require 3.50.2 or later.");
    }
}
