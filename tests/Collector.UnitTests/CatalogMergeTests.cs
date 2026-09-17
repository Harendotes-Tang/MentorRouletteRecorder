using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly string _root = Directory.CreateTempSubdirectory("MentorRecorder.CatalogMerge.").FullName;

    private static readonly string ShippedTemplatesRoot =
        ProfileCatalog.FindDefaultRoot() ?? throw new InvalidOperationException(
            "protocol-profiles not found next to the test binary; cannot build merge fixtures.");

    private static readonly string VerifiedTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn.2026.08.05.json");

    private static readonly string CandidateTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn.2026.08.05.candidate.json");

    private static readonly string UnsupportedTemplatePath =
        Path.Combine(ShippedTemplatesRoot, "cn", "cn-unsupported.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test debris in the OS temp folder is not worth failing a test over.
        }
    }

    private string ShippedRoot => Path.Combine(_root, "shipped");

    private string LocalRoot => Path.Combine(_root, "local");

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
        DateTimeOffset? generatedAt = null)
    {
        var regionDirectory = Path.Combine(rootDirectory, "cn");
        Directory.CreateDirectory(regionDirectory);

        var document = JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
        document["profile_id"] = profileId;
        document["game_build"] = gameBuild;
        if (generatedAt is { } stamp)
        {
            document["generated_at"] = stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        // The hash covers everything except itself, so the placeholder below never affects it.
        document["profile_sha256"] = new string('0', 64);
        var options = new JsonSerializerOptions { WriteIndented = true };
        using var draft = JsonDocument.Parse(document.ToJsonString(options));
        document["profile_sha256"] = ProfileLoader.ComputeProfileHash(draft.RootElement);

        var path = Path.Combine(regionDirectory, profileId + ".json");
        File.WriteAllText(path, document.ToJsonString(options), new UTF8Encoding(false));
        return path;
    }

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
        Assert.Equal(new[] { "cn.2026.08.05", "synthetic-cn-shape-v1", "synthetic-v1" }, usable);
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

    [Fact]
    public void PruneLocalProfiles_OnAMissingDirectoryDeletesNothing()
    {
        var deleted = ProfileCatalog.PruneLocalProfiles(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(deleted);
    }

    [Fact]
    public void FindLocalRoot_HonoursMrDataDirAndIsNullUntilTheDirectoryExists()
    {
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, _root);

            var expected = Path.Combine(Path.GetFullPath(_root), ProfileCatalog.DirectoryName);
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
}
