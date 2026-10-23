using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A profile with a malformed field is a profile error, not an exception, and it never keeps the
/// catalogue from loading the healthy profiles beside it.
///
/// A key or a string holding a lone surrogate escape (<c>"\ud800"</c>) is valid JSON to the parser, and throws only
/// when something reads it. A profile like that - hand-edited, or written from untrusted input - is refused as text
/// that cannot be read, exactly like text that is not JSON, instead of throwing out of the loader and the catalogue
/// scan (audit 2026-10-03, R2T-X1).
/// </summary>
public sealed class ProtocolProfileMalformedFieldTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.ProfileMalformed", Guid.NewGuid().ToString("N"), "synthetic");

    public ProtocolProfileMalformedFieldTests() => Directory.CreateDirectory(_directory);

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

    // ----------------------------------------------------- lone surrogate escapes ----

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

    // -------------------------------------------------------- calendar timestamps ----
    // Calendar-invalid profile metadata must not prevent healthy profiles loading.

    [Theory]
    [InlineData("2026-99-99T99:99:99.000Z")]
    [InlineData("2026-02-29T00:00:00.000Z")]
    [InlineData("1900-02-29T00:00:00.000Z")]
    [InlineData("2026-04-31T00:00:00.000Z")]
    [InlineData("2026-09-08T24:00:00.000Z")]
    [InlineData("2026-09-08T00:00:60.000Z")]
    [InlineData("0000-01-01T00:00:00.000Z")]
    public void InvalidCalendarTimestamp_IsAProfileError(string timestamp)
    {
        var document = ProfileTestFiles.Valid();
        document["generated_at"] = timestamp;
        var path = ProfileTestFiles.Write(_directory, "invalid-date", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Null(report.Profile);
        Assert.Contains(report.Errors, error =>
            error.Code == "E_PROFILE_TIMESTAMP" && error.Path == "$.generated_at");
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z")]
    [InlineData("2000-02-29T23:59:59.999Z")]
    [InlineData("2028-02-29T00:00:00.000Z")]
    [InlineData("9999-12-31T23:59:59.999Z")]
    public void ValidCalendarBoundaries_StillLoad(string timestamp)
    {
        var document = ProfileTestFiles.Valid();
        document["generated_at"] = timestamp;
        var path = ProfileTestFiles.Write(_directory, "valid-date", document);

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
    }

    [Fact]
    public void InvalidDate_DoesNotHideAnotherUsableProfileInTheCatalog()
    {
        var invalid = ProfileTestFiles.Valid();
        invalid["generated_at"] = "2026-02-30T00:00:00.000Z";
        ProfileTestFiles.Write(_directory, "invalid-date", invalid);
        ProfileTestFiles.Write(_directory, "healthy", ProfileTestFiles.Valid());

        var catalog = ProfileCatalog.Load(_directory);
        var selected = new ProfileSelector(catalog, allowSynthetic: true)
            .Select(Region.Unknown, "test-build-1");

        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal("healthy", selected.Profile?.ProfileId);
        Assert.True(selected.IsUsable);
    }
}
