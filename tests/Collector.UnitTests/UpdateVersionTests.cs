using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Version comparison for the update check. Pure: no clock, no IO, no network.
///
/// The two sides are read differently on purpose. The local side is this build's own version
/// stamp and may carry a prerelease or build suffix; the remote side comes off the network and
/// is accepted only as three plain numbers, so nothing a published file says can be interpreted.
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.9.1", 0, 9, 1)]
    [InlineData("999999.999999.999999", 999999, 999999, 999999)]
    [InlineData("01.02.03", 1, 2, 3)]
    public void ThreeNumbersParse(string text, int major, int minor, int patch)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(new UpdateVersion(major, minor, patch), version);
        Assert.False(version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3 ")]
    [InlineData(" 1.2.3")]
    [InlineData("1234567.0.0")]
    [InlineData("0.1234567.0")]
    [InlineData("0.0.1234567")]
    [InlineData("1.2.3\n")]
    public void AnythingElseIsRefused(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Theory]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3+build.7")]
    [InlineData("1.2.3-rc.1+build.7")]
    public void ARemoteSuffixIsMalformed(string text) =>
        Assert.False(UpdateVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, false)]
    [InlineData("1.2.3-rc.1", 1, 2, 3, true)]
    [InlineData("0.9.1-alpha", 0, 9, 1, true)]
    [InlineData("0.9.1+2f3a4b5", 0, 9, 1, false)]
    [InlineData("0.9.1-rc.1+2f3a4b5", 0, 9, 1, true)]
    [InlineData("  1.2.3  ", 1, 2, 3, false)]
    public void TheLocalSideDropsItsSuffix(string text, int major, int minor, int patch, bool prerelease)
    {
        Assert.True(UpdateVersion.TryParseLocal(text, out var version));
        Assert.Equal(new UpdateVersion(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("-1.2.3")]
    [InlineData("1.2-rc.3")]
    [InlineData("1.2.3.4-rc")]
    public void AnEmptyOrMisplacedSuffixIsStillMalformedLocally(string text) =>
        Assert.False(UpdateVersion.TryParseLocal(text, out _));

    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.3.0", "1.2.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    public void NewerIsStrictlyGreater(string remote, string local, bool newer)
    {
        Assert.True(UpdateVersion.TryParse(remote, out var published));
        Assert.True(UpdateVersion.TryParseLocal(local, out var running));
        Assert.Equal(newer, UpdateVersion.IsNewer(published, running));
    }

    [Fact]
    public void ARunningPrereleaseIsOlderThanItsOwnRelease()
    {
        Assert.True(UpdateVersion.TryParse("1.2.3", out var published));
        Assert.True(UpdateVersion.TryParseLocal("1.2.3-rc.1", out var prerelease));
        Assert.True(UpdateVersion.TryParseLocal("1.2.3", out var release));
        Assert.True(UpdateVersion.TryParseLocal("1.2.3+2f3a4b5", out var build));

        Assert.True(UpdateVersion.IsNewer(published, prerelease));
        Assert.False(UpdateVersion.IsNewer(published, release));

        // Build metadata is not a prerelease: the same numbers are the same version.
        Assert.False(UpdateVersion.IsNewer(published, build));
    }

    [Fact]
    public void AVersionReadsBackAsThreeNumbers()
    {
        Assert.True(UpdateVersion.TryParse("1.20.300", out var version));
        Assert.Equal("1.20.300", version.ToString());
    }
}
