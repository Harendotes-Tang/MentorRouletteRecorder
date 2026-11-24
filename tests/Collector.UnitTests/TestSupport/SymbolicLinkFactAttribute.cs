namespace MentorRecorder.Collector.UnitTests;

/// <summary>权限不足时由 xUnit 明确记录跳过，避免未执行链接断言却显示通过。</summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        using var root = new TempDirectory("LinkCapability");
        try
        {
            Directory.CreateSymbolicLink(root.Combine("link"), root.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip = "当前 Windows 环境没有创建符号链接的权限。";
        }
    }
}
