using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateProfileTests : IDisposable
{
    private const string Build = "2026.08.05.0000.0000";
    private readonly string _directory = Directory.CreateTempSubdirectory("MentorRecorder.CandidateProfiles.").FullName;

    [Fact]
    public void DefaultCatalogDoesNotRetainCandidatesAndFormalSelectionNeverUsesThem()
    {
        Write("candidate-a", Candidate());

        Assert.Empty(ProfileCatalog.Load(_directory).Entries);
        var enabledCatalog = ProfileCatalog.Load(_directory, allowCandidate: true);
        Assert.Single(enabledCatalog.Entries);
        var selector = new ProfileSelector(enabledCatalog);
        var current = selector.Current;

        Assert.Null(selector.SelectCandidate(Region.Cn, Build, enabled: false));
        var profile = Assert.IsType<ProtocolProfile>(selector.SelectCandidate(Region.Cn, Build, enabled: true));
        Assert.Equal(ProfileCompatibilityStatus.Candidate, profile.Status);
        Assert.False(profile.ToBinding().IsUsable);
        Assert.NotEqual(ProfileStatus.Verified, profile.ToBinding().Status);
        Assert.Same(current, selector.Current);
        var formal = selector.Select(Region.Cn, Build);
        Assert.Equal(ProfileCompatibilityStatus.Unsupported, formal.Status);
        Assert.Null(formal.Profile);
        Assert.False(formal.IsUsable);
        Assert.Equal("UNSUPPORTED", selector.GetProfileStatus().Status);
    }

    [Theory]
    [InlineData(Region.Global, Build)]
    [InlineData(Region.Unknown, Build)]
    [InlineData(Region.Cn, "2026.08.06.0000.0000")]
    [InlineData(Region.Cn, null)]
    [InlineData(Region.Cn, "")]
    public void CandidateSelectionRequiresExactKnownIdentity(Region region, string? build)
    {
        Write("candidate-identity", Candidate());
        var selector = new ProfileSelector(ProfileCatalog.Load(_directory, allowCandidate: true));

        Assert.Null(selector.SelectCandidate(region, build, enabled: true));
    }

    [Fact]
    public void DuplicateCandidatesAreRefusedWithoutDisablingTheFormalProfile()
    {
        Write("candidate-a", Candidate());
        Write("candidate-b", Candidate());
        var formal = ProfileTestFiles.Valid();
        formal["compatibility_status"] = "VERIFIED";
        formal["region"] = "CN";
        formal["game_build"] = Build;
        ((Dictionary<string, object>)formal["provenance"])["evidence"] =
            ((List<object>)formal["messages"]).Cast<Dictionary<string, object>>()
            .Select(message => (object)new Dictionary<string, object>
            {
                ["field"] = "messages." + message["name"] + ".opcode",
                ["method"] = "USER_CONFIRMED",
                ["recorded_at_utc"] = "2026-09-05T00:00:00.000Z",
                ["note"] = "Synthetic test-only evidence for selection isolation.",
            }).ToList();
        Write("formal-test", formal);
        var catalog = ProfileCatalog.Load(_directory, allowCandidate: true);
        var selector = new ProfileSelector(catalog);

        Assert.True(catalog.IsAmbiguous(Region.Cn, Build, candidate: true));
        Assert.False(catalog.IsAmbiguous(Region.Cn, Build));
        Assert.Null(selector.SelectCandidate(Region.Cn, Build, enabled: true));
        Assert.Equal("formal-test", selector.Select(Region.Cn, Build).Profile?.ProfileId);
        Assert.True(selector.Select(Region.Cn, Build).IsUsable);
    }

    [Fact]
    public void OpcodeOnlyCandidateLoadsWithoutFieldsAndKeepsItsEvidenceNotes()
    {
        var profile = ProfileLoader.Load(Write("candidate-valid", Candidate()));

        Assert.Empty(profile.Messages);
        Assert.Null(profile.MentorRouletteId);
        var hypothesis = Assert.Single(profile.Hypotheses);
        Assert.Equal("QUEUE_REGISTRATION", hypothesis.Name);
        Assert.Equal(PacketDirection.ClientToServer, hypothesis.Direction);
        Assert.Equal(0x03bb, hypothesis.Opcode);
        Assert.Equal("queue", hypothesis.Group);
        Assert.Equal("Synthetic metadata-only candidate.", hypothesis.Note);
        Assert.True(hypothesis.AcceptsLength(128));
        Assert.False(hypothesis.AcceptsLength(127));
        Assert.False(hypothesis.AcceptsLength(129));
        Assert.False(hypothesis.AcceptsLength(-1));
    }

    [Fact]
    public void RangeLengthsAreInclusiveAndDoNotInventAnUpperBound()
    {
        var document = Candidate();
        var hypothesis = Hypothesis(document);
        hypothesis.Remove("expected_length");
        hypothesis["min_length"] = 8;
        hypothesis["max_length"] = 24;
        var bounded = Assert.Single(ProfileLoader.Load(Write("candidate-range", document)).Hypotheses);

        Assert.False(bounded.AcceptsLength(7));
        Assert.True(bounded.AcceptsLength(8));
        Assert.True(bounded.AcceptsLength(24));
        Assert.False(bounded.AcceptsLength(25));

        hypothesis.Remove("max_length");
        var unbounded = Assert.Single(ProfileLoader.Load(Write("candidate-unbounded", document)).Hypotheses);
        Assert.Null(unbounded.MaxLength);
        Assert.True(unbounded.AcceptsLength(257));
    }

    [Theory]
    [InlineData("roulette", "E_PROFILE_CANDIDATE_ROULETTE")]
    [InlineData("no-length", "E_PROFILE_LENGTH_RULE")]
    [InlineData("two-length-rules", "E_PROFILE_LENGTH_RULE")]
    [InlineData("reversed-range", "E_PROFILE_LENGTH_RULE")]
    [InlineData("fields", "E_PROFILE_SCHEMA")]
    [InlineData("duplicate-name", "E_PROFILE_DUPLICATE_HYPOTHESIS")]
    [InlineData("duplicate-opcode", "E_PROFILE_DUPLICATE_HYPOTHESIS_OPCODE")]
    [InlineData("empty", "E_PROFILE_NO_ROULETTE")]
    public void MalformedCandidatesAreRefused(string mutation, string errorCode)
    {
        var document = Candidate();
        var hypothesis = Hypothesis(document);
        switch (mutation)
        {
            case "roulette": document["mentor_roulette_id"] = 42; break;
            case "no-length": hypothesis.Remove("expected_length"); break;
            case "two-length-rules": hypothesis["min_length"] = 8; break;
            case "reversed-range":
                hypothesis.Remove("expected_length");
                hypothesis["min_length"] = 24;
                hypothesis["max_length"] = 8;
                break;
            case "fields": hypothesis["fields"] = new List<object>(); break;
            case "duplicate-name":
                ((List<object>)document["hypotheses"]).Add(new Dictionary<string, object>(hypothesis)
                    { ["opcode"] = 0x0104 });
                break;
            case "duplicate-opcode":
                ((List<object>)document["hypotheses"]).Add(new Dictionary<string, object>(hypothesis)
                    { ["name"] = "ANOTHER_CANDIDATE" });
                break;
            case "empty": document["hypotheses"] = new List<object>(); break;
        }

        var report = ProfileLoader.Validate(Write("candidate-bad", document));
        Assert.Null(report.Profile);
        Assert.Contains(report.Errors, error => error.Code == errorCode);
    }

    [Theory]
    [InlineData("VERIFIED")]
    [InlineData("UNSUPPORTED")]
    [InlineData("SYNTHETIC")]
    public void HypothesesCannotBePromotedByChangingStatus(string status)
    {
        var document = Candidate();
        document["compatibility_status"] = status;

        var report = ProfileLoader.Validate(Write("candidate-promoted", document));
        Assert.Null(report.Profile);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_HYPOTHESES_STATUS");
    }

    [Fact]
    public void ShippedCandidateHasEveryTaskBookMemberAndOnlyCandidateMetadata()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-profiles", "cn", "cn.2026.08.05.candidate.json");
        var profile = ProfileLoader.Load(path);
        Assert.Equal(ProfileCompatibilityStatus.Candidate, profile.Status);
        Assert.Equal(Build, profile.GameBuild);
        Assert.Equal(Region.Cn, profile.Region);
        Assert.Null(profile.MentorRouletteId);
        Assert.Empty(profile.Messages);
        Assert.False(profile.ToBinding().IsUsable);
        var expected = new (PacketDirection Direction, int Opcode, int Length)[]
        {
            (PacketDirection.ClientToServer, 0x03bb, 128),
            (PacketDirection.ClientToServer, 0x0104, 8),
            (PacketDirection.ServerToClient, 0x00b0, 8),
            (PacketDirection.ServerToClient, 0x020b, 8),
            (PacketDirection.ClientToServer, 0x034b, 24),
            (PacketDirection.ServerToClient, 0x0323, 40),
            (PacketDirection.ClientToServer, 0x0178, 72),
            (PacketDirection.ClientToServer, 0x008f, 8),
            (PacketDirection.ClientToServer, 0x00e8, 8),
            (PacketDirection.ClientToServer, 0x024d, 8),
            (PacketDirection.ClientToServer, 0x0187, 8),
            (PacketDirection.ClientToServer, 0x0281, 8),
            (PacketDirection.ClientToServer, 0x01a2, 24),
            (PacketDirection.ServerToClient, 0x01b8, 3672),
            (PacketDirection.ServerToClient, 0x0077, 640),
            (PacketDirection.ServerToClient, 0x0347, 808),
            (PacketDirection.ServerToClient, 0x014a, 456),
            (PacketDirection.ServerToClient, 0x0325, 144),
            (PacketDirection.ServerToClient, 0x031d, 448),
            (PacketDirection.ServerToClient, 0x0153, 360),
            (PacketDirection.ServerToClient, 0x0214, 424),
            (PacketDirection.ServerToClient, 0x0149, 104),
            (PacketDirection.ServerToClient, 0x025a, 104),
            (PacketDirection.ServerToClient, 0x02fc, 104),
            // 副本与职业识别：依据社区 7.55a 表、Sapphire 结构定义与本机冷启动 trace（长度一致）。
            (PacketDirection.ServerToClient, 0x028d, 136),
            (PacketDirection.ServerToClient, 0x0350, 16),
        };
        Assert.Equal(expected.Length, profile.Hypotheses.Count);
        foreach (var entry in expected)
            Assert.Contains(profile.Hypotheses, item => item.Direction == entry.Direction &&
                item.Opcode == entry.Opcode && item.ExpectedLength == entry.Length);
        Assert.Equal(18, profile.Hypotheses.Count(item => item.Group == "zone_load"));
        Assert.Equal(2, profile.Hypotheses.Count(item => item.Group == "identity"));
        // Every shipped hypothesis carries a human label so the Desktop never has to show a
        // bare opcode; the label is display-only and must never be mistaken for the name.
        Assert.All(profile.Hypotheses, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
            Assert.NotEqual(item.Name, item.DisplayLabel);
        });
        Assert.Equal("排本登记", profile.Hypotheses.Single(item => item.Name == "QUEUE_REGISTRATION").DisplayLabel);
        Assert.Equal("副本查找器操作（第 0 字节 = 随机任务编号）", profile.Hypotheses.Single(item => item.Name == "FINDER_ACTION").DisplayLabel);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var provenance = json.RootElement.GetProperty("provenance");
        Assert.Equal("7d74758061481862a943d659e52a8d29942cfa53a6c4a6b871364e91a82a370f",
            provenance.GetProperty("capture_fixture_sha256").GetString());
        Assert.Equal(new[] { 234100, 276500 }, provenance.GetProperty("evidence").EnumerateArray()
            .Where(item => item.TryGetProperty("t_ms", out _)).Select(item => item.GetProperty("t_ms").GetInt32()));
    }

    [Fact]
    public void HypothesisLabelIsOptionalAndFallsBackToTheName()
    {
        var unlabelled = ProfileLoader.Load(Write("unlabelled", Candidate()));
        Assert.Null(unlabelled.Hypotheses[0].Label);
        Assert.Equal("QUEUE_REGISTRATION", unlabelled.Hypotheses[0].DisplayLabel);

        var labelled = Candidate();
        Hypothesis(labelled)["label"] = "排本登记";
        var profile = ProfileLoader.Load(Write("labelled", labelled));
        Assert.Equal("排本登记", profile.Hypotheses[0].Label);
        Assert.Equal("排本登记", profile.Hypotheses[0].DisplayLabel);
        // Cosmetic: the label changes nothing about what the hypothesis matches.
        Assert.Equal(unlabelled.Hypotheses[0] with { Label = "排本登记" }, profile.Hypotheses[0]);
    }

    private string Write(string id, Dictionary<string, object> document) =>
        ProfileTestFiles.Write(_directory, id, document);

    private static Dictionary<string, object> Hypothesis(Dictionary<string, object> document) =>
        (Dictionary<string, object>)((List<object>)document["hypotheses"])[0];

    private static Dictionary<string, object> Candidate()
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "CANDIDATE";
        document["region"] = "CN";
        document["game_build"] = Build;
        document["mentor_roulette_id"] = null!;
        document["messages"] = new List<object>();
        document["hypotheses"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = "QUEUE_REGISTRATION",
                ["direction"] = "CLIENT_TO_SERVER",
                ["opcode"] = 0x03bb,
                ["expected_length"] = 128,
                ["note"] = "Synthetic metadata-only candidate.",
                ["group"] = "queue",
            },
        };
        return document;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
