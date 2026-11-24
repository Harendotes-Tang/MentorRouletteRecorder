using System.Text;
using MentorRecorder.Collector.Export;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="AtomicExportFile"/>：写入失败、取消或校验不通过时保留原文件且不留临时文件；禁止覆盖模式下的并发提交只有一个成功；
/// 真实文件锁冲突时原文件同样完好。
/// </summary>
public sealed class AtomicExportFileTests : IDisposable
{
    private readonly TempDirectory _directory = new("AtomicExport");

    private string Target(string name) => _directory.Combine(name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialWriteFailureDoesNotPublishOrDamageAnExistingFile(bool overwrite)
    {
        var target = Target("partial.json");
        if (overwrite) File.WriteAllText(target, "existing user data");

        Assert.Throws<IOException>(() => AtomicExportFile.Write(target, overwrite, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial replacement"));
            throw new IOException("Injected disk write failure.");
        }));

        if (overwrite) Assert.Equal("existing user data", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedIntegrityCheckDoesNotPublishTheReplacement()
    {
        var target = Target("verified.db");
        File.WriteAllText(target, "previous verified copy");
        using (var pending = new AtomicExportFile(target, overwrite: true))
        {
            File.WriteAllText(pending.TemporaryPath, "invalid replacement");
            var error = Assert.Throws<CollectorException>(() => pending.Commit(_ => false));
            Assert.Equal(ErrorCodes.ExportFailed, error.Code);
            Assert.Equal("previous verified copy", File.ReadAllText(target));
        }
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CancellationDuringWritePreservesExistingFileAndCleansTemporaryFile()
    {
        var target = Target("cancel-write.json");
        File.WriteAllText(target, "original content");
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => AtomicExportFile.Write(target, true, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("replacement"));
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal("original content", File.ReadAllText(target));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CancellationDuringVerificationPreservesExistingFileAndCleansTemporaryFile()
    {
        var target = Target("cancel-verify.json");
        File.WriteAllText(target, "original content");
        using var cancellation = new CancellationTokenSource();
        using (var pending = new AtomicExportFile(target, true))
        {
            File.WriteAllText(pending.TemporaryPath, "replacement");
            Assert.Throws<OperationCanceledException>(() => pending.Commit(_ =>
            {
                cancellation.Cancel();
                return true;
            }, cancellation.Token));
            Assert.Equal("original content", File.ReadAllText(target));
        }
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentNoOverwriteExportsPublishExactlyOneCompleteFile()
    {
        var target = Target("concurrent.json");
        using var ready = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Export(string value)
        {
            using var pending = new AtomicExportFile(target, overwrite: false);
            File.WriteAllText(pending.TemporaryPath, value);
            ready.Signal();
            await release.Task;
            try { pending.Commit(); return true; }
            catch (IOException) { return false; }
        }

        var first = Export("first complete file");
        var second = Export("second complete file");
        Assert.True(ready.IsSet);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, success => success);
        Assert.Contains(File.ReadAllText(target), new[] { "first complete file", "second complete file" });
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedReplacementPreservesExistingFileAndCleansTemporaryFile()
    {
        var target = Target("locked.json");
        File.WriteAllText(target, "original content");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => AtomicExportFile.Write(target, overwrite: true,
                stream => stream.Write(Encoding.UTF8.GetBytes("replacement"))));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected a file-system refusal, received {error?.GetType().Name ?? "no error"}.");
        }
        Assert.Equal("original content", File.ReadAllText(target));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(_directory.Path, ".mentor-export-*.tmp", SearchOption.AllDirectories));

    public void Dispose() => _directory.Dispose();
}
