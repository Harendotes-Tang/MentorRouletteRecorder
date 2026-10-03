using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A key or a string holding a lone surrogate escape (<c>"\ud800"</c>) is valid JSON to the parser, and throws only
/// when something reads it. A profile like that - hand-edited, or written from untrusted input - is refused as text
/// that cannot be read, exactly like text that is not JSON, instead of throwing out of the loader and the catalogue
/// scan (audit 2026-10-03, R2T-X1).
/// </summary>
public sealed class ProtocolProfileTextTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.ProfileText", Guid.NewGuid().ToString("N"), "synthetic");

    public ProtocolProfileTextTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);

    /// <summary>Writes a valid profile, then puts a lone surrogate escape into a string value or into a key.</summary>
    private string WriteWithLoneSurrogate(string profileId, string where)
    {
        var path = ProfileTestFiles.Write(_directory, profileId, ProfileTestFiles.Valid());
        var text = File.ReadAllText(path);
        var damaged = where == "value"
            ? text.Replace("SYNTHETIC test profile.", "SYNTHETIC \\ud800 test profile.", StringComparison.Ordinal)
            : text.Replace("\"capture_fixture\"", "\"capture_\\udc00fixture\"", StringComparison.Ordinal);
        Assert.NotEqual(text, damaged);
        File.WriteAllText(path, damaged);
        return path;
    }

    [Theory]
    [InlineData("value")]
    [InlineData("key")]
    public void ALoneSurrogateEscapeIsAProfileErrorAndNotAnException(string where)
    {
        var path = WriteWithLoneSurrogate("lone-surrogate", where);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Null(report.Profile);
        Assert.Equal("E_PROFILE_PARSE", Assert.Single(report.Errors).Code);
    }

    [Fact]
    public void ALoneSurrogateInOneProfileDoesNotKeepTheCatalogueFromLoadingTheRest()
    {
        WriteWithLoneSurrogate("lone-surrogate", "value");
        ProfileTestFiles.Write(_directory, "healthy", ProfileTestFiles.Valid());

        var catalog = ProfileCatalog.Load(_directory);
        var selected = new ProfileSelector(catalog, allowSynthetic: true).Select(Region.Unknown, "test-build-1");

        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal("healthy", selected.Profile?.ProfileId);
        Assert.True(selected.IsUsable);
    }
}
