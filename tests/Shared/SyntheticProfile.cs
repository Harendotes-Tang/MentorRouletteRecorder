using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Tests;

/// <summary>
/// The synthetic protocol profile that ships with the tests (protocol-profiles/synthetic/synthetic-v1.json): it
/// names no region and only an explicit, synthetic-allowing selection loads it.
/// </summary>
internal static class SyntheticProfile
{
    /// <summary>The file, copied next to the test binary.</summary>
    public static string Path => System.IO.Path.Combine(
        AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");

    /// <summary>The file selected explicitly, synthetic profiles allowed.</summary>
    public static ProfileSelection Select() => ProfileSelector.SelectExplicit(Path, allowSynthetic: true);
}
