using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Calendar-invalid profile metadata must not prevent healthy profiles loading.</summary>
public sealed class ProtocolProfileTimestampTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.ProfileDates", Guid.NewGuid().ToString("N"), "synthetic");

    public ProtocolProfileTimestampTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);

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
