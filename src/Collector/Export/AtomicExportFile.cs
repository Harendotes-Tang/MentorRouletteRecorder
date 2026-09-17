using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Export;

/// <summary>
/// 在目标目录暂存完整文件，再以同卷重命名提交。写入、校验或提交失败时只清理本次
/// 临时文件，既有目标保留；禁止覆盖的约束由提交原语执行，不能仅依赖预检查。
/// </summary>
internal sealed class AtomicExportFile : IDisposable
{
    /// <summary>暂存文件名前缀；进程被强杀后由 <see cref="BackupService"/> 在托管目录内清理。</summary>
    internal const string TemporaryPrefix = ".mentor-export-";

    private readonly string _targetPath;
    private readonly bool _overwrite;
    private bool _committed;

    /// <summary>预检查目标并独占创建空临时文件；调用者负责关闭写入句柄后再提交。</summary>
    internal AtomicExportFile(string targetPath, bool overwrite)
    {
        _targetPath = targetPath;
        _overwrite = overwrite;
        ExportPaths.PrepareDestination(targetPath, overwrite);
        TemporaryPath = Path.Combine(Path.GetDirectoryName(targetPath)!,
            TemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        using var reservation = new FileStream(
            TemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <summary>本次操作拥有的空文件路径；也可交给要求空目标的 VACUUM INTO。</summary>
    internal string TemporaryPath { get; }

    /// <summary>
    /// 刷新暂存内容，按需校验，再发布到目标。校验器返回 false 时不触碰目标。
    /// 不允许覆盖时，并发创建的目标同样使提交失败；允许覆盖时也不先截断旧文件。
    /// </summary>
    internal void Commit(Func<string, bool>? verify = null)
    {
        using (var file = new FileStream(
                   TemporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Flush(flushToDisk: true);
        }

        if (verify is not null && !verify(TemporaryPath))
        {
            throw new CollectorException(ErrorCodes.ExportFailed,
                "导出文件完整性校验未通过，已保留原文件。",
                new Dictionary<string, object?> { ["target_path"] = _targetPath });
        }

        // TemporaryPath 与目标位于同一目录；系统重命名一次性完成替换或拒绝覆盖。
        File.Move(TemporaryPath, _targetPath, _overwrite);
        _committed = true;
    }

    /// <summary>同步写入并提交；writer 抛出的异常会清理临时文件，目标保持原状。</summary>
    internal static void Write(string targetPath, bool overwrite, Action<Stream> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var pending = new AtomicExportFile(targetPath, overwrite);
        using (var file = new FileStream(
                   pending.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            writer(file);
        }
        pending.Commit();
    }

    /// <summary>未提交时尽力删除自身暂存文件，不删除目标，也不覆盖原始失败。</summary>
    public void Dispose()
    {
        if (_committed) return;
        try { File.Delete(TemporaryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
