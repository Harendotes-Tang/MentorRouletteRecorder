using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A share code becomes a profile only through the receiver's own template, and a local profile
/// becomes a share code only when it is exactly what that template and a few learned values
/// produce.
/// </summary>
public sealed class SharedProfileBuilderTests : IDisposable
{
    private static readonly DateTimeOffset Confirmed = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Verified = new(2026, 9, 16, 20, 5, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, int> Counts = new Dictionary<string, int>
    {
        ["messages.CONTENT_FINDER_POP.opcode"] = 2,
        ["messages.ZONE_INITIALIZATION.opcode"] = 3,
        ["messages.ZONE_TERRITORY.opcode"] = 1,
        ["messages.PLAYER_JOB.opcode"] = 3,
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "shared-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A root no test in this class created raises DirectoryNotFoundException.
        }
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CalibrationTrafficCases.All)
        {
            data.Add(name);
        }

        return data;
    }

    private ProtocolProfile WriteLocal(string name)
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(name);
        var written = LocalProfileWriter.Write(
            draft, template, CalibrationTrafficCases.Build, Confirmed, Path.Combine(_root, "protocol-profiles"));
        return ProfileLoader.Load(written.Path);
    }

    private static JsonObject WithoutIdentity(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var key in new[] { "provenance", "profile_id", "profile_sha256", "generated_at" })
        {
            node.Remove(key);
        }

        // The announcement recognised by its timing does not travel in a share code: its opcode
        // was named by evidence this machine collected, and the code carries none of it. A
        // profile holding one is shared as the queue-request profile underneath it, and the
        // receiving machine looks for its own announcement (docs/protocol-profile-format.md §11).
        var messages = node["messages"]!.AsArray();
        var announced = messages.FirstOrDefault(
            message => message!["name"]!.GetValue<string>() == "MATCH_ANNOUNCED");
        if (announced is not null)
        {
            messages.Remove(announced);
        }

        return node;
    }

    private static string Canonical(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return CanonicalJson.Serialize(document.RootElement);
    }

    /// <summary>
    /// Acceptance criterion of phase A: a code taken from a profile this machine wrote rebuilds,
    /// through the template, the same profile apart from its provenance.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ALocalProfileRoundTripsThroughItsShareCode(string name)
    {
        var template = CalibrationObserverTests.Template();
        var local = WriteLocal(name);

        var shared = SharedProfileBuilder.ToShareCode(local, template);
        Assert.Null(shared.Reason);
        Assert.Equal(CalibrationTrafficCases.Source(name), shared.Payload!.MatchSource);

        var built = SharedProfileBuilder.Build(shared.Code!, template, Verified, Counts,
            queueInferenceAcceptedAtUtc: Verified);

        Assert.Equal(SharedProfileBuildStatus.Built, built.Status);
        Assert.Equal(Canonical(WithoutIdentity(File.ReadAllText(local.SourcePath))), Canonical(WithoutIdentity(built.Json!)));
        Assert.Equal("cn.2026.09.01.0000.0000.shared", built.ProfileId);
        Assert.Equal(ProfileCompatibilityStatus.Verified, built.Profile!.Status);
        Assert.True(built.Profile.ToBinding().IsUsable);

        // The returned text is a file the ordinary loader accepts where the catalogue looks.
        var directory = Path.Combine(_root, ProfileCatalog.SharedDirectoryName, "cn");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, built.ProfileId + ".json");
        File.WriteAllText(path, built.Json);
        var report = ProfileLoader.Validate(path);
        Assert.Empty(report.Errors);
        Assert.Equal(built.ProfileSha256, report.Profile!.ProfileSha256);
    }

    [Fact]
    public void TheSharedProfileSaysWhereItCameFromInItsOwnWords()
    {
        var template = CalibrationObserverTests.Template();
        var code = SharedProfileBuilder.ToShareCode(WriteLocal(CalibrationTrafficCases.ReplyState), template);

        var built = SharedProfileBuilder.Build(code.Code!, template, Verified, Counts);

        var document = JsonNode.Parse(built.Json!)!.AsObject();
        var summary = document["provenance"]!["summary"]!.GetValue<string>();
        Assert.StartsWith("共享校准生成的档案", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("本机校准生成", summary, StringComparison.Ordinal);
        Assert.Contains(code.CodeSha256![..12], summary, StringComparison.Ordinal);
        Assert.Equal("2026-09-16T20:05:00.000Z", document["generated_at"]!.GetValue<string>());

        var evidence = document["provenance"]!["evidence"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        foreach (var message in new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB" })
        {
            var entry = Assert.Single(evidence, item => item["field"]!.GetValue<string>() == "messages." + message + ".opcode");
            Assert.Equal("OBSERVED_LOCAL_TRAFFIC", entry["method"]!.GetValue<string>());
            Assert.Equal(Counts["messages." + message + ".opcode"], entry["sample_count"]!.GetValue<int>());
            var note = entry["note"]!.GetValue<string>();
            Assert.Contains("共享校准码 " + code.CodeSha256[..12], note, StringComparison.Ordinal);
            Assert.Contains("按结构核实 " + Counts["messages." + message + ".opcode"] + " 次", note, StringComparison.Ordinal);
            Assert.Contains("时间线由分享者核对，未在本机重复", note, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(evidence, item => item["method"]!.GetValue<string>() == "USER_CONFIRMED");
    }

    [Fact]
    public void AQueueInferredCodeIsNotBuiltUntilThePlayerAcceptedTheInference()
    {
        var template = CalibrationObserverTests.Template();
        var code = SharedProfileBuilder.ToShareCode(WriteLocal(CalibrationTrafficCases.QueueRequest), template);

        var waiting = SharedProfileBuilder.Build(code.Code!, template, Verified, Counts);
        var accepted = SharedProfileBuilder.Build(code.Code!, template, Verified, Counts, queueInferenceAcceptedAtUtc: Verified);

        Assert.Equal(SharedProfileBuildStatus.ConsentRequired, waiting.Status);
        Assert.Null(waiting.Json);
        Assert.Equal(SharedProfileBuildStatus.Built, accepted.Status);
        var evidence = JsonNode.Parse(accepted.Json!)!["provenance"]!["evidence"]!.AsArray();
        Assert.Contains(evidence, item => item!["method"]!.GetValue<string>() == "USER_CONFIRMED" &&
            item["field"]!.GetValue<string>() == "messages.CONTENT_FINDER_POP.opcode");
        Assert.True(accepted.Profile!.MatchFromQueue);
    }

    public static TheoryData<string, Func<ShareCodePayload, ShareCodePayload>> Inapplicable() => new()
    {
        { "another template", payload => payload with { TemplateProfileId = "cn.2026.10.01" } },
        { "another template hash", payload => payload with { TemplateSha256 = new string('1', 64) } },
        { "another region", payload => payload with { Region = Region.Global } },
    };

    /// <summary>
    /// A code made against a template this machine does not have is not for this version of the
    /// software: it is neither built nor reported as an error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Inapplicable))]
    public void ACodeForAnotherTemplateIsNotApplicableRatherThanInvalid(string why, Func<ShareCodePayload, ShareCodePayload> change)
    {
        var template = CalibrationObserverTests.Template();
        var payload = ShareCodeTests.Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 64), territory: 0xA108);

        var built = SharedProfileBuilder.Build(ShareCode.Encode(change(payload)), template, Verified, Counts);

        Assert.True(built.Status == SharedProfileBuildStatus.NotApplicable, why);
        Assert.Null(built.Json);
        Assert.Null(built.Profile);
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "not a code", "hello" },
        { "selector count", ShareCode.Encode(ShareCodeTests.Payload(CalibrationMatchSource.ReplyState,
            new ShareCodePop(0xC002, SelectorValues: new long[] { 3, 4 }))) },
        { "selector out of range", ShareCode.Encode(ShareCodeTests.Payload(CalibrationMatchSource.ReplyState,
            new ShareCodePop(0xC002, SelectorValues: new long[] { 300 }))) },
        { "announcement too short for the template's roulette id", ShareCode.Encode(ShareCodeTests.Payload(
            CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 16))) },
        { "pop and zone on one opcode", ShareCode.Encode(ShareCodeTests.Payload(
            CalibrationMatchSource.Announcement, new ShareCodePop(0xA107, Length: 64))) },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void ACodeTheTemplateCannotBuildIsInvalid(string why, string code)
    {
        var built = SharedProfileBuilder.Build(code, CalibrationObserverTests.Template(), Verified, Counts);

        Assert.True(built.Status == SharedProfileBuildStatus.Invalid, why);
        Assert.False(string.IsNullOrWhiteSpace(built.Reason));
        Assert.Null(built.Json);
    }

    [Fact]
    public void AProfileThatCameFromSomeoneElseIsNotSharedAgain()
    {
        var template = CalibrationObserverTests.Template();
        var code = SharedProfileBuilder.ToShareCode(WriteLocal(CalibrationTrafficCases.Announcement), template);
        var built = SharedProfileBuilder.Build(code.Code!, template, Verified, Counts);

        var again = SharedProfileBuilder.ToShareCode(built.Profile!, template);

        Assert.Null(again.Code);
        Assert.NotNull(again.Reason);
    }

    [Fact]
    public void AProfileThatIsNotACalibratedShapeOfTheTemplateHasNoShareCode()
    {
        var template = CalibrationObserverTests.Template();
        var local = WriteLocal(CalibrationTrafficCases.ReplyState);
        var widened = local with
        {
            Messages = local.Messages.Select(message => message.Name == "ZONE_INITIALIZATION"
                ? message with { ExpectedLength = 460 }
                : message).ToArray(),
        };
        var otherWindow = local with { MatchWindow = TimeSpan.FromSeconds(45) };
        var otherRoulette = local with { MentorRouletteId = 8 };
        var shipped = local with { ProfileId = "cn.2026.09.01" };

        Assert.NotNull(SharedProfileBuilder.ToShareCode(widened, template).Reason);
        Assert.NotNull(SharedProfileBuilder.ToShareCode(otherWindow, template).Reason);
        Assert.NotNull(SharedProfileBuilder.ToShareCode(otherRoulette, template).Reason);
        Assert.NotNull(SharedProfileBuilder.ToShareCode(shipped, template).Reason);
    }

    [Fact]
    public void TheQueueInferredCodeCarriesTheRequestOpcodeAndTheTerritory()
    {
        var template = CalibrationObserverTests.Template();

        var shared = SharedProfileBuilder.ToShareCode(WriteLocal(CalibrationTrafficCases.QueueRequestNoJob), template);

        Assert.Equal(CalibrationMatchSource.QueueRequest, shared.Payload!.MatchSource);
        Assert.Equal(CalibrationTrafficCases.Request, shared.Payload.Pop.Opcode);
        Assert.Equal(CalibrationTrafficCases.Territory, shared.Payload.TerritoryOpcode);
        Assert.Null(shared.Payload.JobOpcode);
        Assert.Equal("cn.template", shared.Payload.TemplateProfileId);
        Assert.Equal(template.Source.ProfileSha256, shared.Payload.TemplateSha256);
    }

    [Theory]
    [InlineData(Region.Cn, "2026.09.01.0000.0000", "cn.2026.09.01.0000.0000.shared")]
    [InlineData(Region.Global, "2026.09.01.0000.0000", "global.2026.09.01.0000.0000.shared")]
    public void SharedProfileIdsKeepTheirSuffixAndTheSchemaPattern(Region region, string build, string expected)
    {
        Assert.Equal(expected, SharedProfileBuilder.ProfileIdFor(region, build));
        var longest = SharedProfileBuilder.ProfileIdFor(region, new string('9', 128));
        Assert.EndsWith(SharedProfileBuilder.ProfileIdSuffix, longest, StringComparison.Ordinal);
        Assert.Matches("^[a-z0-9][a-z0-9.-]{0,63}$", longest);
    }

    [Fact]
    public void TheBuiltPopTravelsTheWayTheSourceSays()
    {
        var template = CalibrationObserverTests.Template();
        var payload = ShareCodeTests.Payload(CalibrationMatchSource.MarkerOffset,
            new ShareCodePop(0xF00D, Length: 24, RouletteOffset: 8), territory: 0xA108, job: 0xA109);

        var built = SharedProfileBuilder.Build(ShareCode.Encode(payload), template, Verified, Counts);

        var pop = built.Profile!.Message("CONTENT_FINDER_POP")!;
        Assert.Equal(PacketDirection.ServerToClient, pop.Direction);
        Assert.Equal(8, pop.Field("roulette_id")!.Offset);
        Assert.Equal(TimeSpan.FromSeconds(120), built.Profile.MatchWindow);
    }
}
