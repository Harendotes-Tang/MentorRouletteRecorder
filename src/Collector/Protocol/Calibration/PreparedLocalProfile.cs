namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Owns one validated profile's unpublished temporary file. Use on one thread; the caller must
/// serialize Commit with the state that authorized it. Disposal never touches the destination
/// profile, including after a failed commit or when another confirmation replaced that profile.
/// </summary>
public sealed class PreparedLocalProfile : IDisposable
{
    private string? _temporary;

    internal PreparedLocalProfile(string temporary, LocalProfileWriteResult result)
    {
        _temporary = temporary;
        Result = result;
    }

    /// <summary>The destination and identity of the profile, before or after publication.</summary>
    public LocalProfileWriteResult Result { get; }

    /// <summary>
    /// Atomically renames the already-written file over its destination and returns that
    /// destination. May be called once; a failed move leaves the old destination untouched.
    /// </summary>
    public LocalProfileWriteResult Commit()
    {
        var temporary = _temporary ?? throw new InvalidOperationException("the prepared profile is no longer pending");
        File.Move(temporary, Result.Path, overwrite: true);
        _temporary = null;
        return Result;
    }

    /// <summary>Removes this preparation's temporary file; never removes a committed profile.</summary>
    public void Dispose()
    {
        if (_temporary is not { } temporary)
        {
            return;
        }

        _temporary = null;
        RemoveTemporary(temporary);
    }

    internal static void RemoveTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide a refusal or write error. A leftover has no .json suffix
            // and cannot become a selectable profile on this or a later launch.
        }
    }
}
