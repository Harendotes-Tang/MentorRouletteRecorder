using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;

namespace MentorRecorder.Collector.Export;

/// <summary>
/// Decides where the Collector is willing to write a file the user asked for.
///
/// 用户主动选择的导出和备份可以写入个人文件夹之外的本地目录。
/// 本类负责排除网络及设备路径；实际写入权限、磁盘空间和覆盖控制由文件写入步骤检查。
/// </summary>
public static class ExportPaths
{
    /// <summary>
    /// Resolves and validates a destination path. Returns the full path, or throws
    /// <c>ERR_EXPORT_FAILED</c> when it is not a normal local file destination.
    /// </summary>
    /// <param name="targetPath">Path chosen by the user.</param>
    /// <param name="field">Field name to report on failure.</param>
    public static string Resolve(string targetPath, string field = "target_path")
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed, CollectorTexts.ExportPathEmpty(), field: field);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed, CollectorTexts.ExportPathInvalid(), field: field, inner: ex);
        }

        // 先检查输入，避免对明确的 UNC/设备目标做文件系统访问；再检查链接最终落点。
        if (!IsLocalFilePath(fullPath) || !IsLocalFilePath(ResolveFinalPath(fullPath)))
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed,
                CollectorTexts.ExportPathNotLocal(),
                field: field);
        }

        return fullPath;
    }

    /// <summary>
    /// Resolves reparse points (junctions and symbolic links) to the physical path a write
    /// would actually reach.
    ///
    /// 逐级检查已有祖先和目标文件，避免遗漏普通子目录上层的链接。
    /// 尚不存在的部分仅拼接；一旦发现非本地落点即停止继续访问，由调用者拒绝。
    /// 链接检查失败时返回导出错误，不把无法确认的路径视为可写目标。
    /// </summary>
    /// <param name="fullPath">Already-resolved absolute path.</param>
    public static string ResolveFinalPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        try
        {
            var linksRemaining = 64;
            return ResolveLinks(fullPath, ref linksRemaining);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed, CollectorTexts.ExportPathUncheckable(),
                field: "target_path", inner: ex);
        }
    }

    private static string ResolveLinks(string fullPath, ref int linksRemaining)
    {
        if (!IsLocalFilePath(fullPath))
            return fullPath;
        var root = Path.GetPathRoot(fullPath)!;
        var resolved = root;
        var parts = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var next = Path.Combine(resolved, part);
            if (!IsLocalFilePath(resolved))
            {
                resolved = next;
                continue;
            }

            // 读取链接自身的属性，不能用 Exists 跟随目标；悬空的 UNC 链接也要拒绝。
            var file = new FileInfo(next);
            var attributes = file.Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(next) : file;
                var target = entry.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                // 一次只解析一跳，在访问下一跳之前先判断是否仍为本地路径。
                // OneDrive 占位等非路径重定向重解析点返回 null，仍按原本地位置处理。
                if (target is null)
                {
                    resolved = next;
                }
                else
                {
                    if (--linksRemaining < 0)
                        throw new IOException("导出路径的链接数量过多或存在循环。");
                    resolved = ResolveLinks(Path.GetFullPath(target), ref linksRemaining);
                }
            }
            else
            {
                resolved = next;
            }
        }

        return Path.GetFullPath(resolved);
    }

    /// <summary>判断完整路径是否指向普通本地盘符，排除网络、设备与备用数据流。</summary>
    /// <param name="fullPath">Already-resolved absolute path.</param>
    public static bool IsLocalFilePath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        var root = Path.GetPathRoot(fullPath);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':'
            || fullPath.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Prepares the destination directory and rejects an existing file when overwrite is
    /// disabled. This is an early check only: the writer must also enforce overwrite at
    /// file creation or atomic promotion, because another process may create it meanwhile.
    /// </summary>
    /// <param name="fullPath">Resolved destination.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    public static void PrepareDestination(string fullPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        if (File.Exists(fullPath) && !overwrite)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed,
                CollectorTexts.ExportPathExists(),
                new Dictionary<string, object?> { ["target_path"] = fullPath });
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed, CollectorTexts.ExportPathNoDirectory(), field: "target_path");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed, CollectorTexts.ExportPathCannotCreateDirectory(), inner: ex);
        }
    }

}
