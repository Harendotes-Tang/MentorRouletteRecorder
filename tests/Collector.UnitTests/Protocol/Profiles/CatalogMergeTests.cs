using System.Text;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <see cref="ProfileCatalog.LoadMerged"/> merges the shipped directory with a local
/// (self-calibration) one under precedence-before-ambiguity: a local file shadowed by a
/// loadable shipped one must disappear before the "two files claim the same build" rule runs,
/// so it never drags an unrelated shipped profile into AMBIGUOUS. Every fixture here is a
/// restamped copy of the real shipped <c>cn.2026.08.05.json</c> /
/// <c>cn.2026.08.05.candidate.json</c>, so the messages, evidence and fixtures already satisfy
/// every gate <see cref="ProfileLoader"/> enforces; only <c>profile_id</c>, <c>game_build</c>
/// and (for retention) <c>generated_at</c> change.
/// </summary>
public sealed class CatalogMergeTests : IDisposable
{
    private readonly TempDirectory _root = new("CatalogMerge");

    private static readonly string ShippedTemplatesRoot =
        ProfileCatalog.FindDefaultRoot() ?? throw new InvalidOperationException(
            "protocol-profiles not found next to the test binary; cannot build merge fixtures.");

    private static readonly string VerifiedTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn.2026.08.05.json");

    private static readonly string CandidateTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn.2026.08.05.candidate.json");

    private static readonly string UnsupportedTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn-unsupported.json");

    public void Dispose() => _root.Dispose();

    private string ShippedRoot => _root.Combine("shipped");

    private string LocalRoot => _root.Combine("local");

    /// <summary>Restamps a template profile file with a new identity, keeping its shape.</summary>
    /// <param name="sourcePath">Template file to copy (a real shipped profile).</param>
    /// <param name="rootDirectory">Shipped or local root; the file is written under <c>cn/</c>.</param>
    /// <param name="profileId">New <c>profile_id</c>, also used as the file name stem.</param>
    /// <param name="gameBuild">New <c>game_build</c>.</param>
    /// <param name="generatedAt">New <c>generated_at</c>, or the template's own when null.</param>
    private static string Restamp(
        string sourcePath,
        string rootDirectory,
        string profileId,
        string gameBuild,
        DateTimeOffset? generatedAt = null) =>
        ProfileTestFiles.WriteStamped(
            Path.Combine(rootDirectory, "cn"), profileId,
            ProfileTestFiles.Reidentified(sourcePath, profileId, gameBuild, generatedAt));

    private string RestampShipped(string profileId, string gameBuild, DateTimeOffset? generatedAt = null) =>
        Restamp(VerifiedTemplatePath, ShippedRoot, profileId, gameBuild, generatedAt);

    private string RestampLocal(string profileId, string gameBuild, DateTimeOffset? generatedAt = null) =>
        Restamp(VerifiedTemplatePath, LocalRoot, profileId, gameBuild, generatedAt);

    private string RestampShippedCandidate(string profileId, string gameBuild) =>
        Restamp(CandidateTemplatePath, ShippedRoot, profileId, gameBuild);

    [Fact]
    public void LocalOnlyBuild_IsUsableAndTaggedOriginLocal()
    {
        const string build = "2099.01.01.0000.0000";
        RestampLocal("cn.2099.01.01.local", build);

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(ProfileOrigin.Local, entry.Origin);
        Assert.False(entry.Shadowed);
        Assert.NotNull(entry.Profile);
        Assert.Equal(Path.GetFullPath(LocalRoot), catalog.LocalRoot, ignoreCase: true);

        var selector = new ProfileSelector(catalog);
        var selection = selector.Select(Region.Cn, build);

        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Local, selection.Origin);

        // GetProfileStatus() reports Current, which tracks the build source rather than an
        // explicit Select() call; a selector built with that identity picks it up at
        // construction, just as the real one does when the game process is detected.
        var boundToBuild = new ProfileSelector(catalog, new FixedGameBuildSource(Region.Cn, build));
        Assert.Equal(ProfileOrigin.Local, boundToBuild.GetProfileStatus().Origin);
    }

    private sealed record FixedGameBuildSource(Region Region, string? GameBuild) : IGameBuildSource;

    /// <summary>
    /// The critical case (review finding F-15): a shipped and a local file claim the exact
    /// same region and build. The local one is shadowed and dropped -- not merely outranked --
    /// before the ambiguity grouping ever sees it, so the shipped file stays usable and is
    /// never marked AMBIGUOUS.
    /// </summary>
    [Fact]
    public void ShippedAndLocalClaimTheSameBuild_LocalIsShadowed_ShippedStaysUsableAndUnambiguous()
    {
        const string build = "2099.02.02.0000.0000";
        RestampShipped("cn.2099.02.02", build);
        RestampLocal("cn.2099.02.02.local", build);

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot);

        var shippedEntry = Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Shipped);
        var localEntry = Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Local);

        Assert.False(shippedEntry.Shadowed);
        Assert.NotEqual(ProfileCompatibilityStatus.Ambiguous, shippedEntry.Status);
        Assert.NotNull(shippedEntry.Profile);

        Assert.True(localEntry.Shadowed);
        Assert.Null(localEntry.Profile);
        Assert.Same(localEntry, Assert.Single(catalog.ShadowedEntries));

        Assert.False(catalog.IsAmbiguous(Region.Cn, build));

        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Shipped, selection.Origin);
        Assert.Equal("cn.2099.02.02", selection.Profile!.ProfileId);
    }

    [Fact]
    public void TwoLocalFilesClaimingTheSameBuild_AreBothAmbiguous()
    {
        const string build = "2099.03.03.0000.0000";
        RestampLocal("cn.2099.03.03.local-a", build);
        RestampLocal("cn.2099.03.03.local-b", build);

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry =>
        {
            Assert.Equal(ProfileCompatibilityStatus.Ambiguous, entry.Status);
            Assert.False(entry.Shadowed);
            Assert.Null(entry.Profile);
        });
        Assert.True(catalog.IsAmbiguous(Region.Cn, build));
        Assert.False(new ProfileSelector(catalog).Select(Region.Cn, build).IsUsable);
    }

    /// <summary>Review finding F-16: candidate collisions never disable a formal profile.</summary>
    [Fact]
    public void ShippedCandidate_DoesNotShadowALocalVerifiedProfileForTheSameBuild()
    {
        const string build = "2099.04.04.0000.0000";
        RestampShippedCandidate("cn.2099.04.04.candidate", build);
        RestampLocal("cn.2099.04.04.local", build);

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, allowCandidate: true);

        var localEntry = Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Local);
        Assert.False(localEntry.Shadowed);
        Assert.NotNull(localEntry.Profile);
        Assert.Equal(ProfileCompatibilityStatus.Verified, localEntry.Status);

        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Local, selection.Origin);
    }

    /// <summary>
    /// The shipped placeholder for an unsupported build declares <c>game_build: "unknown"</c>
    /// and no messages. It shadows nothing because nothing else ever claims that literal
    /// build string -- there is no special case in the merge rule for it.
    /// </summary>
    [Fact]
    public void ShippedUnsupportedPlaceholder_ShadowsNoUnrelatedLocalBuild()
    {
        const string build = "2099.05.05.0000.0000";
        Directory.CreateDirectory(Path.Combine(ShippedRoot, "cn"));
        File.Copy(
            UnsupportedTemplatePath, Path.Combine(ShippedRoot, "cn", "cn-unsupported.json"));
        RestampLocal("cn.2099.05.05.local", build);

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot);

        var localEntry = Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Local);
        Assert.False(localEntry.Shadowed);
        Assert.NotNull(localEntry.Profile);

        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Local, selection.Origin);
    }

    [Fact]
    public void LoadDefault_IsUnaffectedByTheMergeChanges()
    {
        var catalog = ProfileCatalog.LoadDefault();

        Assert.Null(catalog.LocalRoot);
        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, entry => Assert.Equal(ProfileOrigin.Shipped, entry.Origin));
        Assert.All(catalog.Entries, entry => Assert.False(entry.Shadowed));
        Assert.Empty(catalog.ShadowedEntries);

        var usable = catalog.Entries.Where(entry => entry.Profile?.ToBinding().IsUsable == true)
            .Select(entry => entry.Profile!.ProfileId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "cn.2026.08.05", "global.2026.11.03", "synthetic-cn-shape-v1", "synthetic-v1" }, usable);
    }

    [Fact]
    public void PruneLocalProfiles_KeepsTheNewestPerRegionByGeneratedAtAndReportsWhatItDeleted()
    {
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oldest = RestampLocal("cn.oldest", "2099.06.01.0000.0000", baseline);
        var older = RestampLocal("cn.older", "2099.06.02.0000.0000", baseline.AddDays(1));
        var newer1 = RestampLocal("cn.newer1", "2099.06.03.0000.0000", baseline.AddDays(2));
        var newer2 = RestampLocal("cn.newer2", "2099.06.04.0000.0000", baseline.AddDays(3));
        var newest = RestampLocal("cn.newest", "2099.06.05.0000.0000", baseline.AddDays(4));

        var regionDirectory = Path.Combine(LocalRoot, "cn");
        var schemaGuard = Path.Combine(regionDirectory, ProfileCatalog.SchemaFileName);
        File.WriteAllText(schemaGuard, "{}");
        var strayFile = Path.Combine(regionDirectory, "notes.txt");
        File.WriteAllText(strayFile, "not a profile");

        var deleted = ProfileCatalog.PruneLocalProfiles(LocalRoot, keepPerRegion: 3);

        Assert.Equal(new[] { oldest, older }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            deleted.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer1));
        Assert.True(File.Exists(newer2));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(schemaGuard));
        Assert.True(File.Exists(strayFile));
    }

    /// <summary>
    /// Audit 2026-10-03 CS4-X1. A <c>generated_at</c> holding a lone surrogate escape parses as JSON
    /// but throws when read as text. Retention runs at startup and promises never to fail over one
    /// damaged file: that file falls back to its own write time like any other unreadable one.
    /// </summary>
    [Fact]
    public void PruneLocalProfiles_ATimeThatCannotBeReadAsTextFallsBackToTheFileTime()
    {
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var damaged = RestampLocal("cn.damaged", "2099.06.01.0000.0000", baseline);
        File.WriteAllText(damaged, File.ReadAllText(damaged).Replace(
            "\"generated_at\": \"" + baseline.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) + "\"",
            "\"generated_at\": \"\\ud800\"",
            StringComparison.Ordinal));
        Assert.Contains("\\ud800", File.ReadAllText(damaged), StringComparison.Ordinal);
        File.SetLastWriteTimeUtc(damaged, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var kept = new[]
        {
            RestampLocal("cn.a", "2099.06.02.0000.0000", baseline.AddDays(1)),
            RestampLocal("cn.b", "2099.06.03.0000.0000", baseline.AddDays(2)),
            RestampLocal("cn.c", "2099.06.04.0000.0000", baseline.AddDays(3)),
        };

        var deleted = ProfileCatalog.PruneLocalProfiles(LocalRoot, keepPerRegion: 3);

        Assert.Equal(new[] { damaged }, deleted);
        Assert.All(kept, path => Assert.True(File.Exists(path)));
    }

    /// <summary>
    /// Audit 2026-10-03 TL2-X1. A release build trusts only the shipped folder next to the
    /// executable. It used to walk every ancestor of the executable and of the working directory,
    /// so a <c>protocol-profiles</c> folder planted anywhere above either was loaded as the shipped
    /// catalogue whenever the installed one was missing. A development build keeps the walk, which
    /// is how tools run from inside the repository find it.
    /// </summary>
    [Fact]
    public void FindDefaultRoot_OutsideADevelopmentBuildTrustsOnlyTheApplicationFolder()
    {
        var application = Directory.CreateDirectory(_root.Combine("app", "bin")).FullName;
        var working = Directory.CreateDirectory(_root.Combine("work", "deep")).FullName;
        var plantedAboveApplication = Directory.CreateDirectory(
            _root.Combine("app", ProfileCatalog.DirectoryName)).FullName;
        Directory.CreateDirectory(_root.Combine("work", ProfileCatalog.DirectoryName));

        var found = ProfileCatalog.FindDefaultRoot(application, working);

        if (ProfileCatalog.SearchesAncestorsForProfiles)
        {
            Assert.Equal(plantedAboveApplication, found);
        }
        else
        {
            Assert.Null(found);
            var shipped = Directory.CreateDirectory(
                Path.Combine(application, ProfileCatalog.DirectoryName)).FullName;
            Assert.Equal(shipped, ProfileCatalog.FindDefaultRoot(application, working));
        }
    }

#if !DEBUG
    [Fact]
    public void FindDefaultRoot_AReleaseBuildDoesNotSearchAncestors() =>
        Assert.False(ProfileCatalog.SearchesAncestorsForProfiles);
#endif

    [Fact]
    public void PruneLocalProfiles_OnAMissingDirectoryDeletesNothing()
    {
        var deleted = ProfileCatalog.PruneLocalProfiles(_root.Combine("does-not-exist"));

        Assert.Empty(deleted);
    }

    [Fact]
    public void FindLocalRoot_HonoursMrDataDirAndIsNullUntilTheDirectoryExists()
    {
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, _root.Path);

            var expected = Path.Combine(Path.GetFullPath(_root.Path), ProfileCatalog.DirectoryName);
            Assert.Equal(expected, ProfileCatalog.LocalRootPath);
            Assert.Null(ProfileCatalog.FindLocalRoot());

            Directory.CreateDirectory(expected);

            Assert.Equal(expected, ProfileCatalog.FindLocalRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, previous);
        }
    }

    /// <summary>
    /// A maintainer's rehearsal (<see cref="CalibrationRehearsal"/>) withholds the shipped profile of exactly one
    /// region and build. The local profile of that build, which the shipped one shadows otherwise, is selected;
    /// other builds and regions are untouched; and with nothing else for the build it reads as a new build of a
    /// region that records (NoMatch, which the player reads as a new game version), never as a region that does
    /// not record yet.
    /// </summary>
    [Fact]
    public void Rehearsal_WithholdsOneShippedBuildFromSelection()
    {
        const string rehearsed = "2099.01.01.0000.0000";
        const string other = "2099.02.01.0000.0000";
        var shipped = RestampShipped("cn.2099.01.01", rehearsed);
        var otherShipped = RestampShipped("cn.2099.02.01", other);
        var local = RestampLocal("cn.2099.01.01.local", rehearsed);
        var rehearsal = new CalibrationRehearsal(Region.Cn, rehearsed);

        var plain = new ProfileSelector(ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, null, null)).Select(Region.Cn, rehearsed);
        Assert.Equal(("cn.2099.01.01", (ProfileOrigin?)ProfileOrigin.Shipped), (plain.Profile?.ProfileId, plain.Origin));

        var catalog = ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, null, rehearsal);
        Assert.Equal(shipped, Assert.Single(catalog.WithheldEntries).Path, ignoreCase: true);
        Assert.DoesNotContain(catalog.Entries, entry => string.Equals(entry.Path, shipped, StringComparison.OrdinalIgnoreCase));
        var selector = new ProfileSelector(catalog);
        var selection = selector.Select(Region.Cn, rehearsed);
        Assert.True(selection.IsUsable);
        Assert.Equal(("cn.2099.01.01.local", (ProfileOrigin?)ProfileOrigin.Local), (selection.Profile?.ProfileId, selection.Origin));
        Assert.Equal("cn.2099.02.01", selector.Select(Region.Cn, other).Profile?.ProfileId);
        Assert.Empty(ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, null,
            new CalibrationRehearsal(Region.Global, rehearsed)).WithheldEntries);

        File.Delete(local);
        File.Delete(otherShipped);
        var bare = new ProfileSelector(ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, null, rehearsal)).Select(Region.Cn, rehearsed);
        Assert.Equal((ProfileSelector.NoProfileMatchesReason, ProfileSelectionReason.NoMatch), (bare.Reason, bare.ReasonCode));
        File.Delete(shipped);
        Assert.Equal(ProfileSelectionReason.RegionUnsupported,
            new ProfileSelector(ProfileCatalog.LoadMerged(ShippedRoot, LocalRoot, null, rehearsal)).Select(Region.Cn, rehearsed).ReasonCode);
    }

    /// <summary>
    /// The switch file is written by hand, so the region's case and the surrounding spaces do not matter and the
    /// encodings Windows tools write are read; anything that does not name a known region and a build is no
    /// rehearsal, and neither is a missing or oversized file.
    /// </summary>
    [Theory]
    [InlineData("{\"region\":\"GLOBAL\",\"game_build\":\"2026.11.03.0000.0000\"}", true)]
    [InlineData("{\"region\":\" global \",\"game_build\":\" 2026.11.03.0000.0000 \",\"note\":1}", true)]
    [InlineData("{\"region\":\"UNKNOWN\",\"game_build\":\"2026.11.03.0000.0000\"}", false)]
    [InlineData("{\"region\":\"JP\",\"game_build\":\"2026.11.03.0000.0000\"}", false)]
    [InlineData("{\"region\":\"GLOBAL\",\"game_build\":\"../2026.11.03\"}", false)]
    [InlineData("{\"region\":\"GLOBAL\",\"game_build\":\"\"}", false)]
    [InlineData("{\"region\":\"GLOBAL\",\"game_build\":20261103}", false)]
    [InlineData("{\"region\":\"GLOBAL\"}", false)]
    [InlineData("[\"GLOBAL\",\"2026.11.03.0000.0000\"]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void Rehearsal_ReadsOnlyAWellFormedSwitchFile(string text, bool rehearses)
    {
        var path = _root.Combine(CalibrationRehearsal.FileName);
        var expected = rehearses ? new CalibrationRehearsal(Region.Global, "2026.11.03.0000.0000") : null;
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode })
        {
            File.WriteAllText(path, text, encoding);
            Assert.Equal(expected, CalibrationRehearsal.Read(path));
        }

        File.WriteAllText(path, text + new string(' ', CalibrationRehearsal.MaxFileBytes));
        Assert.Null(CalibrationRehearsal.Read(path));
        File.Delete(path);
        Assert.Null(CalibrationRehearsal.Read(path));
    }

    /// <summary>
    /// The catalogue live capture and every reselection use (<see cref="ProfileCatalog.LoadMergedDefault"/>) reads
    /// the switch from the data directory: with it, the shipped Global 2026.11.03 profile is withheld and the build
    /// reads as new; without it, the shipped profile is selected as before.
    /// </summary>
    [Fact]
    public void Rehearsal_TheDefaultCatalogueReadsTheSwitchFromTheDataDirectory()
    {
        const string build = "2026.11.03.0000.0000";
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, _root.Path);
            Assert.Equal(Path.Combine(Path.GetFullPath(_root.Path), CalibrationRehearsal.FileName), CalibrationRehearsal.DefaultPath);
            Assert.Equal("global.2026.11.03",
                new ProfileSelector(ProfileCatalog.LoadMergedDefault()).Select(Region.Global, build).Profile?.ProfileId);

            File.WriteAllText(CalibrationRehearsal.DefaultPath, "{\"region\":\"GLOBAL\",\"game_build\":\"" + build + "\"}");
            var catalog = ProfileCatalog.LoadMergedDefault();
            Assert.Equal("global.2026.11.03", Assert.Single(catalog.WithheldEntries).Report.Profile?.ProfileId);
            var selection = new ProfileSelector(catalog).Select(Region.Global, build);
            Assert.Equal((ProfileSelector.NoProfileMatchesReason, ProfileSelectionReason.NoMatch), (selection.Reason, selection.ReasonCode));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, previous);
        }
    }
}
