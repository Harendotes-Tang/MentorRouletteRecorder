using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Tests;

/// <summary>
/// Points <c>MR_DATA_DIR</c> at another folder until disposed, then restores whatever it found there, the
/// folder <see cref="TestDataDirectory"/> gave the run included. The variable is process-wide: a test that uses
/// this must not run beside another test that resolves the data root.
/// </summary>
internal sealed class DataDirectoryOverride : IDisposable
{
    private readonly string? _previous;

    /// <summary>Sets the variable.</summary>
    /// <param name="path">The data root in force until disposal.</param>
    public DataDirectoryOverride(string path)
    {
        _previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, path);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, _previous);
}
