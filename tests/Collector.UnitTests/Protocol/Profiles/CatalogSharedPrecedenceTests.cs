using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The third catalogue root, and the rule that only a usable binding shadows. Ranking among
/// usable entries: a profile that reads the server's announcement beats one that infers the
/// match from the queue, then shipped, local, shared. Fixtures are restamped copies of the
/// shipped <c>cn.2026.08.05.json</c>, as in <see cref="CatalogMergeTests"/>; the
/// queue-inferred variants only turn the pop around and, for the one that must fail binding,
/// drop the territory message.
/// </summary>
public sealed class CatalogSharedPrecedenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("MentorRecorder.CatalogShared.").FullName;

    private static readonly string TemplatePath = Path.Combine(
        ProfileCatalog.FindDefaultRoot() ?? throw new InvalidOperationException("protocol-profiles not found"),
        "cn", "cn.2026.08.05.json");

    private static readonly string CandidateTemplatePath = Path.Combine(
        Path.GetDirectoryName(TemplatePath)!, "cn.2026.08.05.candidate.json");

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

    private string Shipped => Path.Combine(_root, "shipped");

    private string Local => Path.Combine(_root, "local");

    private string Shared => Path.Combine(_root, "shared");

    private enum Shape
    {
        Announcement,
        QueueInferred,
        QueueInferredWithoutTerritory,
    }

    private static string Write(
        string root, string profileId, string build, Shape shape = Shape.Announcement,
        DateTimeOffset? generatedAt = null, string? source = null)
    {
        var directory = Path.Combine(root, "cn");
        Directory.CreateDirectory(directory);
        var document = JsonNode.Parse(File.ReadAllText(source ?? TemplatePath))!.AsObject();
        document["profile_id"] = profileId;
        document["game_build"] = build;
        if (generatedAt is { } stamp)
        {
            document["generated_at"] = stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        var messages = document["messages"]!.AsArray();
        if (shape != Shape.Announcement)
        {
            messages.Single(node => node!["name"]!.GetValue<string>() == "CONTENT_FINDER_POP")!["direction"] = "CLIENT_TO_SERVER";
        }

        if (shape == Shape.QueueInferredWithoutTerritory)
        {
            messages.Remove(messages.Single(node => node!["name"]!.GetValue<string>() == "ZONE_TERRITORY"));
        }

        document["profile_sha256"] = new string('0', 64);
        var options = new JsonSerializerOptions { WriteIndented = true };
        using (var draft = JsonDocument.Parse(document.ToJsonString(options)))
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(draft.RootElement);
        }

        var path = Path.Combine(directory, profileId + ".json");
        File.WriteAllText(path, document.ToJsonString(options), new UTF8Encoding(false));
        return path;
    }

    private ProfileCatalog Merge(bool allowCandidate = false) => ProfileCatalog.LoadMerged(Shipped, Local, Shared, allowCandidate);

    private sealed record FixedGameBuildSource(Region Region, string? GameBuild) : IGameBuildSource;

    [Fact]
    public void TheFixturesHaveTheBindingsTheTestsNeed()
    {
        const string build = "2099.10.10.0000.0000";
        var announcement = ProfileLoader.Load(Write(Local, "cn.a.local", build));
        var queue = ProfileLoader.Load(Write(Local, "cn.b.local", build, Shape.QueueInferred));
        var broken = ProfileLoader.Load(Write(Local, "cn.c.local", build, Shape.QueueInferredWithoutTerritory));

        Assert.True(announcement.ToBinding().IsUsable);
        Assert.False(announcement.MatchFromQueue);
        Assert.True(queue.ToBinding().IsUsable);
        Assert.True(queue.MatchFromQueue);
        Assert.False(broken.ToBinding().IsUsable);
    }

    [Fact]
    public void ASharedOnlyBuildIsUsableAndTaggedShared()
    {
        const string build = "2099.11.01.0000.0000";
        Write(Shared, "cn.2099.11.01.shared", build);

        var catalog = Merge();

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(ProfileOrigin.Shared, entry.Origin);
        Assert.Equal(Path.GetFullPath(Shared), catalog.SharedRoot, ignoreCase: true);
        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Shared, selection.Origin);
        Assert.Equal(ProfileOrigin.Shared,
            new ProfileSelector(catalog, new FixedGameBuildSource(Region.Cn, build)).GetProfileStatus().Origin);
    }

    /// <summary>
    /// Review finding 1: a local profile the binding refuses must not hide anything else for
    /// its build, nor disable it beside another file. The usable shared profile shadows it
    /// instead of the two refusing each other.
    /// </summary>
    [Fact]
    public void ALocalProfileThatFailsBindingDoesNotHideAUsableSharedOne()
    {
        const string build = "2099.11.02.0000.0000";
        Write(Local, "cn.2099.11.02.local", build, Shape.QueueInferredWithoutTerritory);
        Write(Shared, "cn.2099.11.02.shared", build);

        var catalog = Merge();

        Assert.False(catalog.IsAmbiguous(Region.Cn, build));
        var local = Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Local);
        Assert.True(local.Shadowed);
        Assert.Same(local, Assert.Single(catalog.ShadowedEntries));
        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Shared, selection.Origin);
        Assert.Equal("cn.2099.11.02.shared", selection.Profile!.ProfileId);
    }

    [Fact]
    public void LocalAndSharedForTheSameBuildAreNotMutuallyAmbiguous()
    {
        const string build = "2099.11.03.0000.0000";
        Write(Local, "cn.2099.11.03.local", build);
        Write(Shared, "cn.2099.11.03.shared", build);

        var catalog = Merge();

        Assert.False(catalog.IsAmbiguous(Region.Cn, build));
        Assert.All(catalog.Entries, entry => Assert.NotEqual(ProfileCompatibilityStatus.Ambiguous, entry.Status));
        Assert.True(Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Shared).Shadowed);
        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Local, selection.Origin);
    }

    /// <summary>
    /// A profile that reads the match beats one that infers it, whatever its origin below
    /// shipped: that is what lets a shared announcement profile replace this machine's
    /// provisional queue-inferred one.
    /// </summary>
    [Fact]
    public void ASharedAnnouncementProfileOutranksALocalQueueInferredOne()
    {
        const string build = "2099.11.04.0000.0000";
        Write(Local, "cn.2099.11.04.local", build, Shape.QueueInferred);
        Write(Shared, "cn.2099.11.04.shared", build);

        var catalog = Merge();

        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Shared, selection.Origin);
        Assert.False(selection.Profile!.MatchFromQueue);
        Assert.True(Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Local).Shadowed);
    }

    [Fact]
    public void ALocalAnnouncementProfileOutranksASharedQueueInferredOne()
    {
        const string build = "2099.11.05.0000.0000";
        Write(Local, "cn.2099.11.05.local", build);
        Write(Shared, "cn.2099.11.05.shared", build, Shape.QueueInferred);

        var selection = new ProfileSelector(Merge()).Select(Region.Cn, build);

        Assert.Equal(ProfileOrigin.Local, selection.Origin);
        Assert.False(selection.Profile!.MatchFromQueue);
    }

    [Fact]
    public void ShippedStillWins()
    {
        const string build = "2099.11.06.0000.0000";
        Write(Shipped, "cn.2099.11.06", build);
        Write(Local, "cn.2099.11.06.local", build);
        Write(Shared, "cn.2099.11.06.shared", build);

        var catalog = Merge();

        Assert.False(catalog.IsAmbiguous(Region.Cn, build));
        Assert.Equal(2, catalog.ShadowedEntries.Count);
        Assert.DoesNotContain(catalog.ShadowedEntries, entry => entry.Origin == ProfileOrigin.Shipped);
        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.Equal(ProfileOrigin.Shipped, selection.Origin);
        Assert.Equal("cn.2099.11.06", selection.Profile!.ProfileId);
    }

    /// <summary>
    /// The same rule applied to a shipped file: shadowing is a usable binding's privilege, so
    /// a shipped profile the binding refuses does not hide a usable local one for its build.
    /// </summary>
    [Fact]
    public void AShippedProfileThatFailsBindingDoesNotHideAUsableLocalOne()
    {
        const string build = "2099.11.07.0000.0000";
        Write(Shipped, "cn.2099.11.07", build, Shape.QueueInferredWithoutTerritory);
        Write(Local, "cn.2099.11.07.local", build);

        var catalog = Merge();

        var selection = new ProfileSelector(catalog).Select(Region.Cn, build);
        Assert.True(selection.IsUsable);
        Assert.Equal(ProfileOrigin.Local, selection.Origin);
        Assert.True(Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Shipped).Shadowed);
    }

    /// <summary>Equal rank is still a collision: two shared files for one build refuse each other.</summary>
    [Fact]
    public void TwoSharedFilesForTheSameBuildAreBothAmbiguous()
    {
        const string build = "2099.11.08.0000.0000";
        Write(Shared, "cn.2099.11.08.shared-a", build);
        Write(Shared, "cn.2099.11.08.shared-b", build);

        var catalog = Merge();

        Assert.True(catalog.IsAmbiguous(Region.Cn, build));
        Assert.All(catalog.Entries, entry => Assert.Equal(ProfileCompatibilityStatus.Ambiguous, entry.Status));
        Assert.False(new ProfileSelector(catalog).Select(Region.Cn, build).IsUsable);
    }

    /// <summary>
    /// With no usable entry nothing may shadow, so the ambiguity rule applies unchanged. This
    /// pins a residual edge rather than a goal: two unusable files from different roots refuse
    /// each other, and an ambiguous selection keeps calibration disarmed. The shared builder
    /// never writes an unusable profile, so reaching this state takes a hand-placed file.
    /// </summary>
    [Fact]
    public void WithNoUsableEntryNothingIsShadowedAndTheAmbiguityRuleStands()
    {
        const string build = "2099.11.09.0000.0000";
        Write(Local, "cn.2099.11.09.local", build, Shape.QueueInferredWithoutTerritory);
        Write(Shared, "cn.2099.11.09.shared", build, Shape.QueueInferredWithoutTerritory);

        var catalog = Merge();

        Assert.Empty(catalog.ShadowedEntries);
        Assert.True(catalog.IsAmbiguous(Region.Cn, build));
    }

    [Fact]
    public void AShippedCandidateDoesNotShadowASharedVerifiedProfile()
    {
        const string build = "2099.11.10.0000.0000";
        Write(Shipped, "cn.2099.11.10.candidate", build, source: CandidateTemplatePath);
        Write(Shared, "cn.2099.11.10.shared", build);

        var catalog = Merge(allowCandidate: true);

        Assert.False(Assert.Single(catalog.Entries, entry => entry.Origin == ProfileOrigin.Shared).Shadowed);
        Assert.Equal(ProfileOrigin.Shared, new ProfileSelector(catalog).Select(Region.Cn, build).Origin);
    }

    [Fact]
    public void TheTwoRootMergeIsTheThreeRootMergeWithoutShared()
    {
        const string build = "2099.11.11.0000.0000";
        Write(Shipped, "cn.2099.11.11", build);
        Write(Local, "cn.2099.11.11.local", build);
        Write(Shared, "cn.2099.11.11.shared", build);

        var two = ProfileCatalog.LoadMerged(Shipped, Local);

        Assert.Null(two.SharedRoot);
        Assert.Equal(2, two.Entries.Count);
        Assert.DoesNotContain(two.Entries, entry => entry.Origin == ProfileOrigin.Shared);
        Assert.Equal(ProfileOrigin.Shipped, new ProfileSelector(two).Select(Region.Cn, build).Origin);
    }

    /// <summary>Retention is per directory: shared files never use up the local quota or the reverse.</summary>
    [Fact]
    public void PruningKeepsEachRootsOwnQuota()
    {
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var locals = Enumerable.Range(0, 4)
            .Select(i => Write(Local, $"cn.l{i}.local", $"2099.12.0{i + 1}.0000.0000", generatedAt: baseline.AddDays(i)))
            .ToArray();
        var shared = Enumerable.Range(0, 4)
            .Select(i => Write(Shared, $"cn.s{i}.shared", $"2099.12.0{i + 1}.0000.0000", generatedAt: baseline.AddDays(10 + i)))
            .ToArray();

        var deletedLocal = ProfileCatalog.PruneLocalProfiles(Local, keepPerRegion: 3);
        var deletedShared = ProfileCatalog.PruneLocalProfiles(Shared, keepPerRegion: 3);

        Assert.Equal(new[] { locals[0] }, deletedLocal);
        Assert.Equal(new[] { shared[0] }, deletedShared);
        Assert.All(locals.Skip(1).Concat(shared.Skip(1)), path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void TheSharedRootLivesUnderTheDataDirectory()
    {
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, _root);

            var expected = Path.Combine(Path.GetFullPath(_root), "protocol-profiles-shared");
            Assert.Equal(expected, ProfileCatalog.SharedRootPath);
            Assert.Null(ProfileCatalog.FindSharedRoot());
            Directory.CreateDirectory(expected);
            Assert.Equal(expected, ProfileCatalog.FindSharedRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, previous);
        }
    }
}
