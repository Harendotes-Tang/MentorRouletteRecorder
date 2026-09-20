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
    [InlineData("1.2.3", 1, 2, 3, false, null)]
    [InlineData("1.2.3-rc.1", 1, 2, 3, true, "rc.1")]
    [InlineData("0.9.1-alpha", 0, 9, 1, true, "alpha")]
    [InlineData("0.9.1+2f3a4b5", 0, 9, 1, false, null)]
    [InlineData("0.9.1-rc.1+2f3a4b5", 0, 9, 1, true, "rc.1")]
    [InlineData("1.4.0-beta.1", 1, 4, 0, true, "beta.1")]
    [InlineData("  1.2.3  ", 1, 2, 3, false, null)]
    public void TheLocalSideKeepsItsPrereleaseLabelAndDropsItsBuildMetadata(
        string text, int major, int minor, int patch, bool prerelease, string? label)
    {
        Assert.True(UpdateVersion.TryParseLocal(text, out var version));
        Assert.Equal(new UpdateVersion(major, minor, patch, prerelease, label), version);
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

    [Theory]
    // A prerelease of X.Y.Z sits between the release before it and X.Y.Z itself.
    [InlineData("1.4.0-beta.1", "1.3.9", 1)]
    [InlineData("1.4.0-beta.1", "1.4.0", -1)]
    [InlineData("1.4.0", "1.3.9", 1)]
    // The beta counter is a number, not text: beta.10 comes after beta.9.
    [InlineData("1.4.0-beta.2", "1.4.0-beta.1", 1)]
    [InlineData("1.4.0-beta.10", "1.4.0-beta.9", 1)]
    [InlineData("1.4.0-beta.9", "1.4.0-beta.10", -1)]
    [InlineData("1.4.0-beta.2", "1.4.0-beta.2", 0)]
    // Identifier by identifier, the way semantic versioning orders them: text
    // compares as text, a numeric identifier ranks below an alphanumeric one, and
    // a longer set of identifiers wins a tie on the shared ones.
    [InlineData("1.4.0-alpha.9", "1.4.0-beta.1", -1)]
    [InlineData("1.4.0-beta.1", "1.4.0-beta.x", -1)]
    [InlineData("1.4.0-beta.1.1", "1.4.0-beta.1", 1)]
    // Build metadata is not a prerelease and takes no part in precedence.
    [InlineData("1.4.0+2f3a4b5", "1.4.0", 0)]
    public void PrereleasesOrderBeforeTheirReleaseAndAmongThemselves(string left, string right, int expected)
    {
        Assert.True(UpdateVersion.TryParseLocal(left, out var first));
        Assert.True(UpdateVersion.TryParseLocal(right, out var second));
        Assert.Equal(expected, Math.Sign(UpdateVersion.Compare(first, second)));
        Assert.Equal(-expected, Math.Sign(UpdateVersion.Compare(second, first)));
    }

    [Theory]
    // A machine on a beta is told about the release that beta was cut towards...
    [InlineData("1.4.0", "1.4.0-beta.2", true)]
    // ...and about a later beta of the same release...
    [InlineData("1.4.0-beta.2", "1.4.0-beta.1", true)]
    // ...but never about an older release, nor about its own or an earlier beta.
    [InlineData("1.3.9", "1.4.0-beta.2", false)]
    [InlineData("1.4.0-beta.1", "1.4.0-beta.2", false)]
    [InlineData("1.4.0-beta.2", "1.4.0-beta.2", false)]
    public void ABetaIsOfferedTheReleaseItWasCutTowards(string published, string running, bool newer)
    {
        Assert.True(UpdateVersion.TryParseLocal(published, out var remote));
        Assert.True(UpdateVersion.TryParseLocal(running, out var local));
        Assert.Equal(newer, UpdateVersion.IsNewer(remote, local));
    }

    [Theory]
    // A stable build is never sent to a prerelease, however high its numbers. The
    // published feed is releases/latest, which skips GitHub prereleases, and
    // TryParse refuses a suffix outright; this is the third layer, in the comparer
    // itself, for the day a prerelease is published as BUILD-METADATA.json anyway.
    [InlineData("1.5.0-beta.1", "1.4.0")]
    [InlineData("2.0.0-rc.1", "1.4.0")]
    public void AStableBuildIsNeverOfferedAPrerelease(string published, string running)
    {
        Assert.True(UpdateVersion.TryParseLocal(published, out var remote));
        Assert.True(UpdateVersion.TryParseLocal(running, out var local));

        // It orders strictly higher, and is still refused as an upgrade target.
        Assert.True(UpdateVersion.Compare(remote, local) > 0);
        Assert.False(UpdateVersion.IsNewer(remote, local));
    }

    [Fact]
    public void AVersionReadsBackAsThreeNumbers()
    {
        Assert.True(UpdateVersion.TryParse("1.20.300", out var version));
        Assert.Equal("1.20.300", version.ToString());
    }

    [Fact]
    public void APrereleaseReadsBackWithItsLabel()
    {
        Assert.True(UpdateVersion.TryParseLocal("1.4.0-beta.1+2f3a4b5", out var version));
        Assert.Equal("1.4.0-beta.1", version.ToString());
    }
}
