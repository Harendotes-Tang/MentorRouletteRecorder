using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The profile loader is the gate every protocol constant passes, so it is tested from the
/// outside: a well-formed profile loads, and each way of being malformed is refused with its
/// own code rather than tolerated or repaired.
/// </summary>
public sealed class ProtocolProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Profiles", Guid.NewGuid().ToString("N"));

    public ProtocolProfileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test debris in the OS temp folder is not worth failing a test over.
        }
    }

    [Fact]
    public void ValidProfile_LoadsWithEveryDeclaredMessage()
    {
        var path = ProfileTestFiles.Write(_directory, "test-valid", ProfileTestFiles.Valid());

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
        var profile = Assert.IsType<ProtocolProfile>(report.Profile);
        Assert.Equal("test-valid", profile.ProfileId);
        Assert.Equal(ProfileCompatibilityStatus.Synthetic, profile.Status);
        Assert.Equal(3, profile.Messages.Count);
        Assert.NotNull(profile.Message("CONTENT_FINDER_POP"));
        Assert.Equal(42, profile.MentorRouletteId);
        Assert.True(profile.ToBinding().IsUsable);
    }

    [Fact]
    public void ProfileMissingARequiredProperty_IsRefusedBySchema()
    {
        var document = ProfileTestFiles.Valid();
        document.Remove("game_build");
        var path = ProfileTestFiles.Write(_directory, "test-missing", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_SCHEMA");
        Assert.Null(report.Profile);
    }

    /// <summary>
    /// Review finding L-7. An empty permitted set rejects every value, so the message it
    /// belongs to can never parse: the profile looks healthy while refusing every instance of
    /// its own declared opcode. The schema declares <c>minItems: 1</c>, so this is refused
    /// before the semantic rules run.
    /// </summary>
    [Fact]
    public void ProfileWithAnEmptyPermittedValueSet_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var pop = (Dictionary<string, object>)((List<object>)document["messages"])[0];
        ((List<object>)pop["fields"])[0] =
            ProfileTestFiles.Field("roulette_id", 0, "u16", endian: "little", allowed: Array.Empty<int>());
        var path = ProfileTestFiles.Write(_directory, "test-empty-in", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Null(report.Profile);
        Assert.Contains(
            report.Errors,
            error => error.Code is "E_PROFILE_SCHEMA" or "E_PROFILE_CONSTRAINT");
    }

    /// <summary>The shipped schema is what makes the Python validator refuse it too.</summary>
    [Fact]
    public void ProfileSchemaRequiresAtLeastOnePermittedValue()
    {
        var constraints = ProfileLoader.SchemaDocument
            .GetProperty("$defs").GetProperty("constraints").GetProperty("properties").GetProperty("in");

        Assert.Equal(1, constraints.GetProperty("minItems").GetInt32());
    }

    /// <summary>
    /// Review finding M-2. A field may declare that it only selects which message this is, so
    /// the parser can tell "not the message we want" from "the profile is wrong".
    /// </summary>
    [Fact]
    public void FieldRoleSelectorIsLoadedAndDefaultsToValue()
    {
        var document = ProfileTestFiles.Valid();
        var pop = (Dictionary<string, object>)((List<object>)document["messages"])[0];
        ((List<object>)pop["fields"]).Add(
            ProfileTestFiles.Field("finder_state", 22, "u8", allowed: new[] { 3 }, role: "selector"));

        var profile = ProfileLoader.Load(
            ProfileTestFiles.Write(_directory, "test-selector", document));

        var message = profile.Message("CONTENT_FINDER_POP")!;
        Assert.Equal(ProfileFieldRole.Selector, message.Field("finder_state")!.Role);
        Assert.Equal(ProfileFieldRole.Value, message.Field("roulette_id")!.Role);
    }

    /// <summary>
    /// The shipped CN profile marks <c>finder_state</c> as a selector: the same opcode carries
    /// the application receipt and every later state update, and only value 3 is the pop.
    /// </summary>
    [Fact]
    public void ShippedCnProfile_DeclaresFinderStateAsASelector()
    {
        var catalog = ProfileCatalog.LoadDefault();
        var profile = Assert.IsType<ProtocolProfile>(
            Assert.Single(catalog.Entries, entry => entry.Report.ProfileId == "cn.2026.08.05").Profile);
        var pop = profile.Message("CONTENT_FINDER_POP")!;

        Assert.Equal(ProfileFieldRole.Selector, pop.Field("finder_state")!.Role);
        Assert.Equal(ProfileFieldRole.Value, pop.Field("roulette_id")!.Role);
    }

    [Fact]
    public void ProfileWithAWronglyTypedProperty_IsRefusedBySchema()
    {
        var document = ProfileTestFiles.Valid();
        document["mentor_roulette_id"] = "42";
        var path = ProfileTestFiles.Write(_directory, "test-badtype", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_SCHEMA");
    }

    [Fact]
    public void ProfileWithAnUndeclaredProperty_IsRefusedBySchema()
    {
        var document = ProfileTestFiles.Valid();
        document["extra_field"] = 1;
        var path = ProfileTestFiles.Write(_directory, "test-extra", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_SCHEMA");
    }

    [Fact]
    public void ProfileWhoseHashDoesNotMatchItsBody_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var path = ProfileTestFiles.Write(_directory, "test-hash", document, stamp: false);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_HASH");
    }

    [Fact]
    public void EditingAProfileAfterStampingIsDetected()
    {
        var path = ProfileTestFiles.Write(_directory, "test-tamper", ProfileTestFiles.Valid());
        var text = File.ReadAllText(path).Replace(
            "\"mentor_roulette_id\": 42", "\"mentor_roulette_id\": 43", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_HASH");
    }

    [Fact]
    public void ProfileReferencingAFixtureThatDoesNotMatchItsHash_IsRefused()
    {
        var fixturePath = Path.Combine(_directory, "referenced.json");
        File.WriteAllText(fixturePath, "{\"a\":1}");
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["path"] = "referenced.json",
                ["sha256"] = new string('0', 64),
            },
        };
        var path = ProfileTestFiles.Write(_directory, "test-fixhash", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_FIXTURE_HASH");
    }

    [Fact]
    public void ProfileReferencingAFixtureThatMatches_IsVerified()
    {
        var fixturePath = Path.Combine(_directory, "referenced.json");
        var content = "{\"a\":1}";
        File.WriteAllText(fixturePath, content);
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant();
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = new List<object>
        {
            new Dictionary<string, object> { ["path"] = "referenced.json", ["sha256"] = digest },
        };
        var path = ProfileTestFiles.Write(_directory, "test-fixok", document);

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
        Assert.True(report.FixtureVerified);
    }

    [Fact]
    public void ProfileReferencingAMissingFixture_LoadsButIsNotVerified()
    {
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["path"] = "absent.json",
                ["sha256"] = new string('a', 64),
            },
        };
        var path = ProfileTestFiles.Write(_directory, "test-fixmissing", document);

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok);
        Assert.False(report.FixtureVerified);
        Assert.Contains(report.Warnings, warning => warning.Code == "W_PROFILE_FIXTURE_MISSING");
    }

    [Fact]
    public void UnsupportedProfileDeclaringAMessage_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "UNSUPPORTED";
        document["mentor_roulette_id"] = null!;
        var path = ProfileTestFiles.Write(_directory, "test-unsup", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_UNSUPPORTED_MESSAGES");
    }

    [Fact]
    public void CandidateProfileWithoutARouletteId_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "CANDIDATE";
        document["mentor_roulette_id"] = null!;
        var path = ProfileTestFiles.Write(_directory, "test-cand", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_NO_ROULETTE");
    }

    [Fact]
    public void VerifiedProfileWithoutEvidence_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "VERIFIED";
        var path = ProfileTestFiles.Write(_directory, "test-verified", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_NO_EVIDENCE");
    }

    [Fact]
    public void DutyResultWithoutVictoryValues_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        ((Dictionary<string, object>)messages[2]).Remove("victory_values");
        var path = ProfileTestFiles.Write(_directory, "test-novictory", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_NO_VICTORY");
    }

    [Fact]
    public void FieldReadingPastTheDeclaredMessageLength_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        var fields = (List<object>)((Dictionary<string, object>)messages[1])["fields"];
        ((Dictionary<string, object>)fields[0])["offset"] = 7;
        var path = ProfileTestFiles.Write(_directory, "test-fieldoob", document);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_FIELD_OOB");
    }

    [Fact]
    public void ProfileIdThatDoesNotMatchTheFileName_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["profile_id"] = "test-somethingelse";
        var path = ProfileTestFiles.Write(_directory, "test-namemismatch", document, rewriteId: false);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_ID");
    }

    [Fact]
    public void TwoProfilesClaimingTheSameRegionAndBuild_RefuseEachOther()
    {
        ProfileTestFiles.Write(_directory, "test-dup-a", ProfileTestFiles.Valid());
        ProfileTestFiles.Write(_directory, "test-dup-b", ProfileTestFiles.Valid());

        var catalog = ProfileCatalog.Load(_directory);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry =>
            Assert.Equal(ProfileCompatibilityStatus.Ambiguous, entry.Status));
        Assert.Empty(catalog.UsableFor(Region.Unknown));
        Assert.True(catalog.IsAmbiguous(Region.Unknown, "test-build-1"));
    }

    [Fact]
    public void Selector_MatchesRegionAndBuildAndExcludesSyntheticByDefault()
    {
        ProfileTestFiles.Write(_directory, "test-sel", ProfileTestFiles.Valid());
        var catalog = ProfileCatalog.Load(_directory);

        var withoutSynthetic = new ProfileSelector(catalog).Select(Region.Unknown, "test-build-1");
        var withSynthetic =
            new ProfileSelector(catalog, allowSynthetic: true).Select(Region.Unknown, "test-build-1");

        Assert.Equal(ProfileCompatibilityStatus.Unsupported, withoutSynthetic.Status);
        Assert.False(withoutSynthetic.IsUsable);
        Assert.Equal(ProfileCompatibilityStatus.Synthetic, withSynthetic.Status);
        Assert.True(withSynthetic.IsUsable);
        Assert.Equal("test-sel", withSynthetic.Profile!.ProfileId);
    }

    [Fact]
    public void Selector_WithAnUnknownBuildFailsClosed()
    {
        ProfileTestFiles.Write(_directory, "test-sel2", ProfileTestFiles.Valid());
        var selector = new ProfileSelector(ProfileCatalog.Load(_directory), allowSynthetic: true);

        var unknownBuild = selector.Select(Region.Unknown, "some-other-build");
        var noBuild = selector.Select(Region.Unknown, null);

        Assert.False(unknownBuild.IsUsable);
        Assert.False(noBuild.IsUsable);
        Assert.Same(ProfileBinding.FailClosed, unknownBuild.Binding);
    }

    [Fact]
    public void Selector_WithNoBuildSourceReportsAFailClosedSnapshot()
    {
        var selector = new ProfileSelector(ProfileCatalog.Load(_directory));

        var snapshot = selector.GetProfileStatus();

        Assert.Null(snapshot.ProfileId);
        Assert.Equal("UNSUPPORTED", snapshot.Status);
        Assert.Equal(0, snapshot.MessageCount);
        Assert.NotNull(snapshot.LastError);
    }

    [Fact]
    public void ShippedProfiles_AreValidAndOnlyTheCnAndSyntheticOnesAreUsable()
    {
        var catalog = ProfileCatalog.LoadDefault();

        Assert.NotNull(catalog.Root);
        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, entry => Assert.True(
            entry.Report.Ok,
            entry.Path + ": " + string.Join("; ", entry.Report.Errors.Select(error => error.Message))));

        var usable = catalog.Entries.Where(entry => entry.Profile?.ToBinding().IsUsable == true)
            .Select(entry => entry.Profile!.ProfileId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        // The two synthetic ones are usable only when a path is passed explicitly, and
        // scripts/package.ps1 deletes protocol-profiles/synthetic/ from every packaged build.
        Assert.Equal(new[] { "cn.2026.08.05", "synthetic-cn-shape-v1", "synthetic-v1" }, usable);
    }

    [Fact]
    public void ShippedCnProfile_IsVerifiedFromPopToExitWithoutADutyResult()
    {
        var catalog = ProfileCatalog.LoadDefault();
        var entry = Assert.Single(catalog.Entries, entry => entry.Report.ProfileId == "cn.2026.08.05");
        var profile = Assert.IsType<ProtocolProfile>(entry.Profile);

        Assert.Equal(ProfileCompatibilityStatus.Verified, profile.Status);
        Assert.Equal(Region.Cn, profile.Region);
        Assert.Equal("2026.08.05.0000.0000", profile.GameBuild);
        Assert.Equal(9, profile.MentorRouletteId);
        Assert.Equal(TimeSpan.FromSeconds(120), profile.MatchWindow);
        Assert.Equal(
            new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB" },
            profile.Messages.Select(m => m.Name).ToArray());

        var pop = profile.Messages.Single(m => m.Name == "CONTENT_FINDER_POP");
        Assert.Equal(0x0323, pop.Opcode);
        Assert.Equal(PacketDirection.ServerToClient, pop.Direction);
        Assert.Equal(40, pop.ExpectedLength);
        Assert.Equal(16, pop.Fields.Single(f => f.Name == "roulette_id").Offset);
        Assert.Equal(9, pop.Fields.Single(f => f.Name == "finder_state").Offset);

        var zone = profile.Messages.Single(m => m.Name == "ZONE_INITIALIZATION");
        Assert.Equal(0x014a, zone.Opcode);
        Assert.Equal(456, zone.ExpectedLength);
        Assert.Empty(zone.Fields);

        // The two identity messages fill in the duty name and the job. Their field offsets
        // come from Sapphire's public struct rather than from a local payload sample, so they
        // are pinned here: a silent edit to either offset must fail a test.
        var territory = profile.Messages.Single(m => m.Name == "ZONE_TERRITORY");
        Assert.Equal(0x028d, territory.Opcode);
        Assert.Equal(136, territory.ExpectedLength);
        var territoryId = territory.Fields.Single(f => f.Name == "territory_id");
        Assert.Equal(2, territoryId.Offset);
        Assert.Equal(ProfileFieldType.U16, territoryId.Type);

        var job = profile.Messages.Single(m => m.Name == "PLAYER_JOB");
        Assert.Equal(0x0350, job.Opcode);
        Assert.Equal(16, job.ExpectedLength);
        var jobId = job.Fields.Single(f => f.Name == "job_id");
        Assert.Equal(0, jobId.Offset);
        Assert.Equal(ProfileFieldType.U8, jobId.Type);

        var binding = profile.ToBinding();
        Assert.True(binding.IsUsable);
        Assert.False(binding.IsSynthetic);
        Assert.False(binding.CanDetectDutyResult);
        Assert.Equal(9, binding.MentorRouletteId);
    }

    [Fact]
    public void Catalog_IgnoresOodleSignatureJsonFiles()
    {
        ProfileTestFiles.Write(_directory, "test-valid", ProfileTestFiles.Valid());

        var oodleDirectory = Path.Combine(_directory, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(oodleDirectory);
        File.WriteAllText(Path.Combine(oodleDirectory, "cn.2026.08.05.json"), """
        {
          "schema_version": 1,
          "status": "CANDIDATE",
          "signatures": {}
        }
        """);

        var catalog = ProfileCatalog.Load(_directory);

        var entry = Assert.Single(catalog.Entries);
        Assert.True(entry.Report.Ok, string.Join("; ", entry.Report.Errors.Select(error => error.Message)));
        Assert.Equal("test-valid.json", Path.GetFileName(entry.Path));
    }

    [Fact]
    public void ShippedRegionProfiles_DeclareNothingAtAll()
    {
        var catalog = ProfileCatalog.LoadDefault();

        foreach (var entry in catalog.Entries.Where(entry =>
                     entry.Report.Region is "CN" or "GLOBAL" && entry.Report.ProfileId != "cn.2026.08.05"))
        {
            var profile = Assert.IsType<ProtocolProfile>(entry.Report.Profile);
            Assert.Equal(ProfileCompatibilityStatus.Unsupported, profile.Status);
            Assert.Empty(profile.Messages);
            Assert.Null(profile.MentorRouletteId);
            Assert.False(profile.ToBinding().IsUsable);
            Assert.Contains("no verified capture yet", profile.ProvenanceSummary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SchemaUsesOnlyTheKeywordsTheValidatorImplements()
    {
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        Collect(ProfileLoader.SchemaDocument, unsupported);

        Assert.Empty(unsupported);
    }

    [Fact]
    public void CanonicalHashIgnoresFormattingButNotContent()
    {
        const string compact = "{\"b\":2,\"a\":[1,2],\"profile_sha256\":\"x\"}";
        const string spaced = "{\n  \"a\": [1, 2],\n  \"b\": 2,\n  \"profile_sha256\": \"y\"\n}";
        const string different = "{\"a\":[1,3],\"b\":2,\"profile_sha256\":\"x\"}";

        Assert.Equal(ProfileLoader.ComputeProfileHash(compact), ProfileLoader.ComputeProfileHash(spaced));
        Assert.NotEqual(ProfileLoader.ComputeProfileHash(compact), ProfileLoader.ComputeProfileHash(different));
    }

    private static void Collect(JsonElement node, SortedSet<string> unsupported)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (!JsonSchemaValidator.SupportedKeywords.Contains(property.Name) &&
                        property.Name is not ("$defs" or "properties"))
                    {
                        // Property bags are keyed by instance property name, not by keyword.
                        unsupported.Add(property.Name);
                    }

                    if (property.Name is "properties" or "$defs")
                    {
                        foreach (var child in property.Value.EnumerateObject())
                        {
                            Collect(child.Value, unsupported);
                        }
                    }
                    else
                    {
                        Collect(property.Value, unsupported);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Collect(item, unsupported);
                }

                break;

            default:
                break;
        }
    }
}

/// <summary>Builds well-formed and deliberately malformed profile files for the tests.</summary>
internal static class ProfileTestFiles
{
    /// <summary>A profile that loads: synthetic, three messages, every field type covered.</summary>
    public static Dictionary<string, object> Valid() => new(StringComparer.Ordinal)
    {
        ["schema_version"] = 1,
        ["profile_id"] = "placeholder",
        ["region"] = "UNKNOWN",
        ["game_build"] = "test-build-1",
        ["generated_at"] = "2026-09-04T00:00:00.000Z",
        ["mentor_roulette_id"] = 42,
        ["compatibility_status"] = "SYNTHETIC",
        ["match_window_seconds"] = 45,
        ["messages"] = new List<object>
        {
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "CONTENT_FINDER_POP",
                ["opcode"] = 100,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 24,
                ["fields"] = new List<object>
                {
                    Field("roulette_id", 0, "u16", endian: "little", min: 1),
                    Field("content_id", 4, "u32", endian: "big"),
                    Field("token", 8, "u64", endian: "little"),
                    Field("marker", 16, "i32", endian: "big"),
                    Field("padding", 20, "bytes", length: 4),
                },
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "ZONE_INITIALIZATION",
                ["opcode"] = 101,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 8,
                ["fields"] = new List<object>
                {
                    Field("territory_id", 0, "u16", endian: "little"),
                    Field("content_id", 2, "u16", endian: "big"),
                    Field("is_duty_instance", 4, "u8", allowed: new[] { 0, 1 }),
                },
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "DUTY_RESULT",
                ["opcode"] = 102,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 2,
                ["victory_values"] = new List<object> { 7 },
                ["fields"] = new List<object> { Field("outcome", 0, "u8") },
            },
        },
        ["fixtures"] = new List<object>(),
        ["provenance"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["summary"] = "SYNTHETIC test profile. Every constant below is invented.",
            ["capture_fixture"] = null!,
            ["capture_fixture_sha256"] = null!,
            ["evidence"] = new List<object>(),
        },
        ["profile_sha256"] = new string('0', 64),
    };

    /// <summary>Builds one field definition.</summary>
    public static Dictionary<string, object> Field(
        string name,
        int offset,
        string type,
        string? endian = null,
        int? length = null,
        long? min = null,
        long? max = null,
        int[]? allowed = null,
        string? role = null)
    {
        var field = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["offset"] = offset,
            ["type"] = type,
        };
        if (role is not null)
        {
            field["role"] = role;
        }

        if (length is { } declaredLength)
        {
            field["length"] = declaredLength;
        }

        if (endian is not null)
        {
            field["endian"] = endian;
        }

        if (min is not null || max is not null || allowed is not null)
        {
            var constraints = new Dictionary<string, object>(StringComparer.Ordinal);
            if (min is { } low)
            {
                constraints["min"] = low;
            }

            if (max is { } high)
            {
                constraints["max"] = high;
            }

            if (allowed is not null)
            {
                constraints["in"] = allowed.Select(value => (object)value).ToList();
            }

            field["constraints"] = constraints;
        }

        return field;
    }

    /// <summary>Writes a profile document, stamping its canonical hash unless told not to.</summary>
    /// <param name="directory">Directory to write into.</param>
    /// <param name="profileId">File name stem.</param>
    /// <param name="document">Document to write.</param>
    /// <param name="stamp">Whether to write a correct <c>profile_sha256</c>.</param>
    /// <param name="rewriteId">Whether to set <c>profile_id</c> to <paramref name="profileId"/>.</param>
    public static string Write(
        string directory,
        string profileId,
        Dictionary<string, object> document,
        bool stamp = true,
        bool rewriteId = true)
    {
        if (rewriteId)
        {
            document["profile_id"] = profileId;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(document, options);
        if (stamp)
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(json);
            json = JsonSerializer.Serialize(document, options);
        }

        var path = Path.Combine(directory, profileId + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
