using System.Security.Cryptography;
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
    private readonly TempDirectory _directory = new("Profiles");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TraditionalPlaceholderLoadsWithoutBorrowingAnyRecordingCapability()
    {
        var root = ProfileCatalog.FindDefaultRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root, "tc", "tc-unsupported.json");
        var report = ProfileLoader.Validate(path);
        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
        var profile = Assert.IsType<ProtocolProfile>(report.Profile);
        Assert.Equal(Region.Tc, profile.Region);
        Assert.Equal(ProfileCompatibilityStatus.Unsupported, profile.Status);
        Assert.Empty(profile.Messages);
        Assert.Null(profile.MentorRouletteId);
        Assert.False(profile.ToBinding().IsUsable);
        Assert.Null(Protocol.Calibration.CalibrationTemplate.Select(ProfileCatalog.LoadDefault(), Region.Tc));

        var wrongDirectory = _directory.Combine("cn");
        Directory.CreateDirectory(wrongDirectory);
        var wrongPath = Path.Combine(wrongDirectory, Path.GetFileName(path));
        File.Copy(path, wrongPath);
        Assert.Contains(ProfileLoader.Validate(wrongPath).Errors, error => error.Code == "E_PROFILE_REGION");
    }

    [Fact]
    public void ValidProfile_LoadsWithEveryDeclaredMessage()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "test-valid", ProfileTestFiles.Valid());

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

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-missing", document), "E_PROFILE_SCHEMA");
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
        var path = ProfileTestFiles.Write(_directory.Path, "test-empty-in", document);

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
            ProfileTestFiles.Write(_directory.Path, "test-selector", document));

        var message = profile.Message("CONTENT_FINDER_POP")!;
        Assert.Equal(ProfileFieldRole.Selector, message.Field("finder_state")!.Role);
        Assert.Equal(ProfileFieldRole.Value, message.Field("roulette_id")!.Role);
    }

    [Fact]
    public void ProfileWithAWronglyTypedProperty_IsRefusedBySchema()
    {
        var document = ProfileTestFiles.Valid();
        document["mentor_roulette_id"] = "42";

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-badtype", document), "E_PROFILE_SCHEMA");
    }

    [Fact]
    public void ProfileWithAnUndeclaredProperty_IsRefusedBySchema()
    {
        var document = ProfileTestFiles.Valid();
        document["extra_field"] = 1;

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-extra", document), "E_PROFILE_SCHEMA");
    }

    [Fact]
    public void ProfileWhoseHashDoesNotMatchItsBody_IsRefused()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "test-hash", ProfileTestFiles.Valid(), stamp: false);

        AssertRefused(path, "E_PROFILE_HASH");
    }

    [Fact]
    public void EditingAProfileAfterStampingIsDetected()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "test-tamper", ProfileTestFiles.Valid());
        var text = File.ReadAllText(path).Replace(
            "\"mentor_roulette_id\": 42", "\"mentor_roulette_id\": 43", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        AssertRefused(path, "E_PROFILE_HASH");
    }

    [Fact]
    public void ProfileReferencingAFixtureThatDoesNotMatchItsHash_IsRefused()
    {
        File.WriteAllText(_directory.Combine("referenced.json"), "{\"a\":1}");
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = Fixtures("referenced.json", new string('0', 64));

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-fixhash", document), "E_PROFILE_FIXTURE_HASH");
    }

    [Fact]
    public void ProfileReferencingAFixtureThatMatches_IsVerified()
    {
        var fixturePath = _directory.Combine("referenced.json");
        File.WriteAllText(fixturePath, "{\"a\":1}");
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = Fixtures("referenced.json", Sha256Hex(fixturePath));
        var path = ProfileTestFiles.Write(_directory.Path, "test-fixok", document);

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
        Assert.True(report.FixtureVerified);
    }

    [Fact]
    public void AnUnreadableFixtureRefusesOnlyItsProfileAndCatalogLoadingContinues()
    {
        var fixturePath = _directory.Combine("locked-fixture.bin");
        File.WriteAllText(fixturePath, "fixture content");
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = Fixtures("locked-fixture.bin", Sha256Hex(fixturePath));
        var path = ProfileTestFiles.Write(_directory.Path, "test-fixlocked", document);
        var healthyPath = ProfileTestFiles.Write(_directory.Path, "test-healthy", ProfileTestFiles.Valid());
        using var locked = new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Null(report.Profile);
        Assert.False(report.FixtureVerified);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_FIXTURE_UNREADABLE");
        var catalog = ProfileCatalog.LoadMerged(_directory.Path, null);
        Assert.Contains(catalog.Entries, entry => entry.Path == healthyPath && entry.Profile is not null);
        Assert.Contains(catalog.Entries, entry => entry.Path == path &&
            entry.Report.Errors.Any(error => error.Code == "E_PROFILE_FIXTURE_UNREADABLE"));
    }

    [Fact]
    public void ProfileReferencingAMissingFixture_LoadsButIsNotVerified()
    {
        var document = ProfileTestFiles.Valid();
        document["fixtures"] = Fixtures("absent.json", new string('a', 64));
        var path = ProfileTestFiles.Write(_directory.Path, "test-fixmissing", document);

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

        AssertRefused(
            ProfileTestFiles.Write(_directory.Path, "test-unsup", document), "E_PROFILE_UNSUPPORTED_MESSAGES");
    }

    [Fact]
    public void CandidateProfileWithoutARouletteId_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "CANDIDATE";
        document["mentor_roulette_id"] = null!;

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-cand", document), "E_PROFILE_NO_ROULETTE");
    }

    [Fact]
    public void VerifiedProfileWithoutEvidence_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "VERIFIED";

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-verified", document), "E_PROFILE_NO_EVIDENCE");
    }

    [Fact]
    public void DutyResultWithoutVictoryValues_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        ((Dictionary<string, object>)messages[2]).Remove("victory_values");

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-novictory", document), "E_PROFILE_NO_VICTORY");
    }

    [Fact]
    public void FieldReadingPastTheDeclaredMessageLength_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        var fields = (List<object>)((Dictionary<string, object>)messages[1])["fields"];
        ((Dictionary<string, object>)fields[0])["offset"] = 7;

        AssertRefused(ProfileTestFiles.Write(_directory.Path, "test-fieldoob", document), "E_PROFILE_FIELD_OOB");
    }

    [Fact]
    public void ProfileIdThatDoesNotMatchTheFileName_IsRefused()
    {
        var document = ProfileTestFiles.Valid();
        document["profile_id"] = "test-somethingelse";
        var path = ProfileTestFiles.Write(_directory.Path, "test-namemismatch", document, rewriteId: false);

        AssertRefused(path, "E_PROFILE_ID");
    }

    [Fact]
    public void TwoProfilesClaimingTheSameRegionAndBuild_RefuseEachOther()
    {
        ProfileTestFiles.Write(_directory.Path, "test-dup-a", ProfileTestFiles.Valid());
        ProfileTestFiles.Write(_directory.Path, "test-dup-b", ProfileTestFiles.Valid());

        var catalog = ProfileCatalog.Load(_directory.Path);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry =>
            Assert.Equal(ProfileCompatibilityStatus.Ambiguous, entry.Status));
        Assert.Empty(catalog.UsableFor(Region.Unknown));
        Assert.True(catalog.IsAmbiguous(Region.Unknown, "test-build-1"));
        Assert.Equal(ProfileSelectionReason.Ambiguous,
            new ProfileSelector(catalog, allowSynthetic: true).Select(Region.Unknown, "test-build-1").ReasonCode);
    }

    [Fact]
    public void Selector_MatchesRegionAndBuildAndExcludesSyntheticByDefault()
    {
        ProfileTestFiles.Write(_directory.Path, "test-sel", ProfileTestFiles.Valid());
        var catalog = ProfileCatalog.Load(_directory.Path);

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
        ProfileTestFiles.Write(_directory.Path, "test-sel2", ProfileTestFiles.Valid());
        var selector = new ProfileSelector(ProfileCatalog.Load(_directory.Path), allowSynthetic: true);

        var unknownBuild = selector.Select(Region.Unknown, "some-other-build");
        var noBuild = selector.Select(Region.Unknown, null);

        Assert.False(unknownBuild.IsUsable);
        Assert.False(noBuild.IsUsable);
        Assert.Same(ProfileBinding.FailClosed, unknownBuild.Binding);
    }

    [Fact]
    public void Selector_WithNoBuildSourceReportsAFailClosedSnapshot()
    {
        var selector = new ProfileSelector(ProfileCatalog.Load(_directory.Path));

        var snapshot = selector.GetProfileStatus();

        Assert.Null(snapshot.ProfileId);
        Assert.Equal("UNSUPPORTED", snapshot.Status);
        Assert.Equal(0, snapshot.MessageCount);
        Assert.NotNull(snapshot.LastError);
    }

    /// <summary>
    /// Audit 2026-11-03, D2-1. The selector's English reason is for logs and diagnostics; why nothing
    /// was selected also comes as a token, so the player is told in their own language - and a
    /// traditional-Chinese or global client is told plainly that its region is not supported yet,
    /// rather than that its build is unknown.
    /// </summary>
    [Fact]
    public void Selector_SaysWhyAsAToken()
    {
        var selector = new ProfileSelector(ProfileCatalog.LoadDefault());

        Assert.Equal(ProfileSelectionReason.Selected, selector.Select(Region.Cn, "2026.08.05.0000.0000").ReasonCode);
        Assert.Equal(ProfileSelectionReason.BuildUnknown, selector.Select(Region.Cn, null).ReasonCode);
        Assert.Equal(ProfileSelectionReason.NoMatch, selector.Select(Region.Cn, "2099.01.01.0000.0000").ReasonCode);
        Assert.Equal(ProfileSelectionReason.NotUsable, selector.Select(Region.Cn, "unknown").ReasonCode);
        Assert.Equal(ProfileSelectionReason.RegionUnsupported, selector.Select(Region.Tc, "2026.10.01.0000.0000").ReasonCode);
        Assert.Equal(ProfileSelectionReason.NoMatch, selector.Select(Region.Global, "2026.10.01.0000.0000").ReasonCode);
        Assert.Equal(ProfileSelectionReason.RegionUnknown, selector.Select(Region.Unknown, "2026.08.05.0000.0000").ReasonCode);
    }

    [Fact]
    public void ShippedProfiles_AreValidAndOnlyTheVerifiedAndSyntheticOnesAreUsable()
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
        Assert.Equal(new[] { "cn.2026.08.05", "global.2026.11.03", "synthetic-cn-shape-v1", "synthetic-v1" }, usable);
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
        // finder_state is a selector: the same opcode carries the application receipt and every
        // later state update, and only value 3 is the pop.
        Assert.Equal(ProfileFieldRole.Selector, pop.Field("finder_state")!.Role);
        Assert.Equal(ProfileFieldRole.Value, pop.Field("roulette_id")!.Role);

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
        Assert.True(binding.ObservesDutyClear);
        Assert.Equal(9, binding.MentorRouletteId);
    }

    /// <summary>
    /// The two halves of a duty result, told apart (duty-result brief, decisions 1 and 2): a
    /// CN profile without DUTY_RESULT lets the parser see the clear signal, so a victory can be
    /// observed, but an exit without it is not an observed non-victory.
    /// </summary>
    [Fact]
    public void ACnProfileWithoutDutyResultObservesTheClearButNotItsAbsence()
    {
        var profile = InMemoryProfiles.Cn();

        var binding = profile.ToBinding();

        Assert.True(profile.ObservesDutyClear);
        Assert.True(binding.IsUsable);
        Assert.True(binding.ObservesDutyClear);
        Assert.False(binding.CanDetectDutyResult);
    }

    /// <summary>A profile that declares DUTY_RESULT keeps today's behaviour byte for byte.</summary>
    [Fact]
    public void AProfileDeclaringDutyResultNeverObservesTheClear()
    {
        var profile = InMemoryProfiles.Cn(InMemoryProfiles.DeclaredResult);

        var binding = profile.ToBinding();

        Assert.False(profile.ObservesDutyClear);
        Assert.False(binding.ObservesDutyClear);
        Assert.True(binding.CanDetectDutyResult);
    }

    /// <summary>
    /// 2026-11-20: the clear, the commencement and the remaining-time report were observed byte
    /// for byte on a GLOBAL client, so a verified GLOBAL profile takes part like a CN one.
    /// </summary>
    [Fact]
    public void AGlobalProfileObservesTheContentSignalsSeenOnAGlobalClient()
    {
        var profile = InMemoryProfiles.Of(
            Region.Global, ProfileCompatibilityStatus.Verified, InMemoryProfiles.Pop, InMemoryProfiles.Zone);

        var binding = profile.ToBinding();

        Assert.True(profile.ObservesDutyClear);
        Assert.True(profile.ObservesDutyStart);
        Assert.True(binding.ObservesDutyClear);
        Assert.True(binding.ObservesDutyStart);
        // Declaring its own result message still switches the clear off, as on CN.
        Assert.False(InMemoryProfiles.Of(
            Region.Global, ProfileCompatibilityStatus.Verified,
            InMemoryProfiles.Pop, InMemoryProfiles.Zone, InMemoryProfiles.DeclaredResult).ObservesDutyClear);
    }

    /// <summary>A region with no observation of its own keeps the UNKNOWN path.</summary>
    [Theory]
    [InlineData(Region.Tc)]
    public void AnUnverifiedRegionNeverInheritsCnContentSignals(Region region)
    {
        var profile = InMemoryProfiles.Of(
            region, ProfileCompatibilityStatus.Verified, InMemoryProfiles.Pop, InMemoryProfiles.Zone);

        var binding = profile.ToBinding();

        Assert.False(profile.ObservesDutyClear);
        Assert.False(profile.ObservesDutyStart);
        Assert.False(binding.ObservesDutyStart);
        Assert.True(binding.IsUsable);
        Assert.False(binding.ObservesDutyClear);
        Assert.False(binding.CanDetectDutyResult);
    }

    [Theory]
    [InlineData(ProfileCompatibilityStatus.Candidate)]
    [InlineData(ProfileCompatibilityStatus.Unsupported)]
    [InlineData(ProfileCompatibilityStatus.Ambiguous)]
    public void ACandidateOrUnsupportedProfileDoesNotObserveTheClear(ProfileCompatibilityStatus status)
    {
        var binding = InMemoryProfiles.Of(Region.Cn, status, InMemoryProfiles.Pop, InMemoryProfiles.Zone).ToBinding();

        Assert.False(binding.IsUsable);
        Assert.False(binding.ObservesDutyClear);
        Assert.False(binding.CanDetectDutyResult);
    }

    [Fact]
    public void AQueueInferredProfileWithoutTerritoryStaysFailClosed()
    {
        var request = InMemoryProfiles.Pop with { Direction = PacketDirection.ClientToServer };
        var binding = InMemoryProfiles.Of(Region.Cn, ProfileCompatibilityStatus.Verified, request, InMemoryProfiles.Zone)
            .ToBinding();

        Assert.False(binding.IsUsable);
        Assert.False(binding.ObservesDutyClear);
        Assert.False(binding.CanDetectDutyResult);
    }

    /// <summary>
    /// An invented profile binds both halves from its own messages, the way a live one does:
    /// synthetic-v1 declares DUTY_RESULT, synthetic-cn-shape-v1 has the CN shape without it.
    /// </summary>
    [Fact]
    public void SyntheticProfilesBindBothHalvesFromTheirMessages()
    {
        var catalog = ProfileCatalog.LoadDefault();
        ProfileBinding Bind(string id) => Assert.IsType<ProtocolProfile>(
            Assert.Single(catalog.Entries, entry => entry.Report.ProfileId == id).Profile).ToBinding();

        var declared = Bind("synthetic-v1");
        Assert.True(declared.IsSynthetic);
        Assert.True(declared.CanDetectDutyResult);
        Assert.False(declared.ObservesDutyClear);

        var cnShape = Bind("synthetic-cn-shape-v1");
        Assert.True(cnShape.IsSynthetic);
        Assert.True(cnShape.IsUsable);
        Assert.False(cnShape.CanDetectDutyResult);
        Assert.True(cnShape.ObservesDutyClear);
    }

    [Fact]
    public void Catalog_IgnoresOodleSignatureJsonFiles()
    {
        ProfileTestFiles.Write(_directory.Path, "test-valid", ProfileTestFiles.Valid());

        var oodleDirectory = _directory.Combine(ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(oodleDirectory);
        File.WriteAllText(Path.Combine(oodleDirectory, "cn.2026.08.05.json"), """
        {
          "schema_version": 1,
          "status": "CANDIDATE",
          "signatures": {}
        }
        """);

        var catalog = ProfileCatalog.Load(_directory.Path);

        var entry = Assert.Single(catalog.Entries);
        Assert.True(entry.Report.Ok, string.Join("; ", entry.Report.Errors.Select(error => error.Message)));
        Assert.Equal("test-valid.json", Path.GetFileName(entry.Path));
    }

    [Fact]
    public void ShippedRegionProfiles_DeclareNothingAtAll()
    {
        var catalog = ProfileCatalog.LoadDefault();

        foreach (var entry in catalog.Entries.Where(entry =>
                     entry.Report.Region is "CN" or "GLOBAL" &&
                     entry.Report.ProfileId is not ("cn.2026.08.05" or "global.2026.11.03")))
        {
            var profile = Assert.IsType<ProtocolProfile>(entry.Report.Profile);
            Assert.Equal(ProfileCompatibilityStatus.Unsupported, profile.Status);
            Assert.Empty(profile.Messages);
            Assert.Null(profile.MentorRouletteId);
            Assert.False(profile.ToBinding().IsUsable);
            // Each says it is a placeholder. "No verified capture yet" stopped being true of CN on
            // 2026-09-07 and of GLOBAL on 2026-11-22 (audit 2026-11-03, D2-3).
            Assert.Contains("占位符", profile.ProvenanceSummary, StringComparison.Ordinal);
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

    /// <summary>Validates a written profile: refused, with no profile, and with this code among its errors.</summary>
    private static void AssertRefused(string path, string code)
    {
        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Null(report.Profile);
        Assert.Contains(report.Errors, error => error.Code == code);
    }

    /// <summary>The <c>fixtures</c> list of a profile that references one file.</summary>
    private static List<object> Fixtures(string path, string sha256) => new()
    {
        new Dictionary<string, object> { ["path"] = path, ["sha256"] = sha256 },
    };

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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
