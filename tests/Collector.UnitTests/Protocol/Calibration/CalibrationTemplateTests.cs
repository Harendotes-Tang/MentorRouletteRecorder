using System.Text.Json;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The optional <c>calibration</c> section of a profile: the shape knowledge that lets the
/// Collector re-learn opcodes on a client build it has never seen.
/// </summary>
public sealed class CalibrationTemplateTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    internal static Dictionary<string, object> CalibrationSection() => new(StringComparer.Ordinal)
    {
        ["finder_request"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["direction"] = "CLIENT_TO_SERVER",
            ["expected_length"] = 24,
            ["roulette_field"] = ProfileTestFiles.Field("roulette_id", 0, "u8", min: 1),
        },
        ["finder_reply_max_ms"] = 1000,
    };

    /// <summary>A VERIFIED document with evidence for its three messages; add the calibration key yourself.</summary>
    internal static Dictionary<string, object> VerifiedDocument(bool withCalibrationEvidence = true)
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = "VERIFIED";
        var evidence = new List<object>();
        foreach (var name in new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "DUTY_RESULT" })
        {
            evidence.Add(Evidence("messages." + name + ".opcode"));
        }

        if (withCalibrationEvidence)
        {
            evidence.Add(Evidence(ProfileLoader.CalibrationEvidenceKey));
        }

        ((Dictionary<string, object>)document["provenance"])["evidence"] = evidence;
        return document;
    }

    [Fact]
    public void CalibrationSectionLoadsWithEveryValue()
    {
        var document = VerifiedDocument();
        document["calibration"] = CalibrationSection();
        var path = ProfileTestFiles.Write(_directory.Path, "with-calibration", document);

        var report = ProfileLoader.Validate(path);

        Assert.Empty(report.Errors);
        var calibration = Assert.IsType<ProfileCalibration>(report.Profile!.Calibration);
        Assert.Equal(PacketDirection.ClientToServer, calibration.FinderRequest.Direction);
        Assert.Equal(24, calibration.FinderRequest.ExpectedLength);
        Assert.Equal(0, calibration.FinderRequest.RouletteField.Offset);
        Assert.Equal(ProfileFieldType.U8, calibration.FinderRequest.RouletteField.Type);
        Assert.Equal(TimeSpan.FromSeconds(1), calibration.FinderReplyMax);
        Assert.Equal(1, calibration.FinderRequest.RouletteField.Constraints.Min);
    }

    [Fact]
    public void CalibrationRequestFieldMustBeAFixedWidthInteger()
    {
        var document = VerifiedDocument();
        var section = CalibrationSection();
        ((Dictionary<string, object>)section["finder_request"])["roulette_field"] =
            ProfileTestFiles.Field("roulette_id", 0, "bytes", length: 1);
        document["calibration"] = section;
        var path = ProfileTestFiles.Write(_directory.Path, "bytes-field", document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_CALIBRATION_FIELD");
    }

    [Fact]
    public void VerifiedTemplateNeedsEvidenceForItsRequestShape()
    {
        var document = VerifiedDocument(withCalibrationEvidence: false);
        document["calibration"] = CalibrationSection();
        var missing = ProfileTestFiles.Write(_directory.Path, "template-no-evidence", document);
        Assert.Contains(ProfileLoader.Validate(missing).Errors,
            issue => issue.Code == "E_PROFILE_NO_EVIDENCE" && issue.Message.Contains("calibration.finder_request"));

        var withEvidence = VerifiedDocument();
        withEvidence["calibration"] = CalibrationSection();
        var covered = ProfileTestFiles.Write(_directory.Path, "template-with-evidence", withEvidence);
        Assert.Empty(ProfileLoader.Validate(covered).Errors);
    }

    private static Dictionary<string, object> Evidence(string field) => new(StringComparer.Ordinal)
    {
        ["field"] = field,
        ["method"] = "OBSERVED_LOCAL_TRAFFIC",
        ["recorded_at_utc"] = "2026-09-04T00:00:00.000Z",
        ["note"] = "test",
    };

    [Fact]
    public void ProfileWithoutCalibrationSectionHasNoTemplate()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "without-calibration", ProfileTestFiles.Valid());

        var report = ProfileLoader.Validate(path);

        Assert.Empty(report.Errors);
        Assert.Null(report.Profile!.Calibration);
    }

    [Theory]
    [InlineData("CANDIDATE")]
    [InlineData("UNSUPPORTED")]
    [InlineData("SYNTHETIC")]
    public void CalibrationSectionIsRefusedOutsideVerifiedProfiles(string status)
    {
        var document = ProfileTestFiles.Valid();
        document["compatibility_status"] = status;
        if (status != "SYNTHETIC")
        {
            document["messages"] = new List<object>();
            document["mentor_roulette_id"] = null!;
        }

        if (status == "CANDIDATE")
        {
            document["hypotheses"] = new List<object>
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = "SOMETHING",
                    ["direction"] = "SERVER_TO_CLIENT",
                    ["opcode"] = 7,
                    ["expected_length"] = 8,
                    ["note"] = "synthetic",
                },
            };
        }

        document["calibration"] = CalibrationSection();
        var path = ProfileTestFiles.Write(_directory.Path, "refused-" + status.ToLowerInvariant(), document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_CALIBRATION_STATUS");
        Assert.Null(report.Profile);
    }

    [Fact]
    public void CalibrationSectionWithAnUndeclaredKeyIsASchemaError()
    {
        var document = VerifiedDocument();
        var section = CalibrationSection();
        section["guess"] = true;
        document["calibration"] = section;
        var path = ProfileTestFiles.Write(_directory.Path, "unknown-key", document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_SCHEMA");
    }

    [Fact]
    public void CalibrationRouletteFieldMustFitTheRequestLength()
    {
        var document = VerifiedDocument();
        var section = CalibrationSection();
        ((Dictionary<string, object>)section["finder_request"])["roulette_field"] =
            ProfileTestFiles.Field("roulette_id", 24, "u8");
        document["calibration"] = section;
        var path = ProfileTestFiles.Write(_directory.Path, "oob", document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_FIELD_OOB");
    }

    [Fact]
    public void CalibrationSectionTakesPartInTheCanonicalHash()
    {
        var document = VerifiedDocument();
        document["calibration"] = CalibrationSection();
        var path = ProfileTestFiles.Write(_directory.Path, "stamped", document);
        var stamped = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        var without = VerifiedDocument();
        without["profile_id"] = "stamped";
        var bare = JsonSerializer.Serialize(without);

        Assert.NotEqual(ProfileLoader.ComputeProfileHash(bare), stamped.GetProperty("profile_sha256").GetString());
    }

    /// <summary>The fixture's pop is 24 bytes with the roulette as a u16 at 0; this reply is 40 bytes with it at byte 16.</summary>
    private static Dictionary<string, object> ReplySection(bool playerJob = false)
    {
        var reply = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["direction"] = "SERVER_TO_CLIENT",
            ["expected_length"] = 40,
            ["roulette_field"] = ProfileTestFiles.Field("roulette_id", 16, "u8", min: 1, max: 255),
        };
        if (playerJob)
        {
            reply["player_job"] = true;
        }

        return reply;
    }

    private static Dictionary<string, object> WithReply(Dictionary<string, object> reply, bool withReplyEvidence = true)
    {
        var document = VerifiedDocument();
        var section = CalibrationSection();
        section["finder_reply"] = reply;
        document["calibration"] = section;
        if (withReplyEvidence)
        {
            ((List<object>)((Dictionary<string, object>)document["provenance"])["evidence"])
                .Add(Evidence(ProfileLoader.CalibrationReplyEvidenceKey));
        }

        return document;
    }

    [Fact]
    public void FinderReplyLoadsAsTheReplysOwnShape()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "with-reply", WithReply(ReplySection()));

        var report = ProfileLoader.Validate(path);

        Assert.Empty(report.Errors);
        var reply = Assert.IsType<CalibrationFinderReply>(report.Profile!.Calibration!.FinderReply);
        Assert.Equal((PacketDirection.ServerToClient, 40, 16, ProfileFieldType.U8, false),
            (reply.Direction, reply.ExpectedLength, reply.RouletteField.Offset, reply.RouletteField.Type, reply.CarriesPlayerJob));
        var template = MentorRecorder.Collector.Protocol.Calibration.CalibrationTemplate.From(report.Profile)!;
        Assert.True(template.SeparateReply);
        Assert.False(template.ReplyCarriesJob);
    }

    [Fact]
    public void FinderReplyNeedsEvidenceOfItsOwn()
    {
        var path = ProfileTestFiles.Write(_directory.Path, "reply-no-evidence", WithReply(ReplySection(), withReplyEvidence: false));

        Assert.Contains(ProfileLoader.Validate(path).Errors, issue =>
            issue.Code == "E_PROFILE_NO_EVIDENCE" && issue.Message.Contains(ProfileLoader.CalibrationReplyEvidenceKey));
    }

    [Fact]
    public void FinderReplyThatIsThePopItselfIsRefused()
    {
        var reply = ReplySection();
        reply["expected_length"] = 24;
        reply["roulette_field"] = ProfileTestFiles.Field("roulette_id", 0, "u16", endian: "little", min: 1);
        var path = ProfileTestFiles.Write(_directory.Path, "reply-is-pop", WithReply(reply));

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_CALIBRATION_REPLY");
        Assert.Null(report.Profile);
    }

    [Fact]
    public void FinderReplyRequiresAPopWithoutSelectors()
    {
        var document = WithReply(ReplySection());
        var pop = (Dictionary<string, object>)((List<object>)document["messages"])[0];
        ((List<object>)pop["fields"]).Add(ProfileTestFiles.Field("finder_state", 12, "u8", allowed: new[] { 3 }, role: "selector"));
        var path = ProfileTestFiles.Write(_directory.Path, "reply-pop-selector", document);

        Assert.Contains(ProfileLoader.Validate(path).Errors, issue =>
            issue.Code == "E_PROFILE_CALIBRATION_REPLY" && issue.Message.Contains("selector"));
    }

    [Fact]
    public void FinderReplyCarryingTheJobNeedsAJobMessageOfItsShape()
    {
        var withoutJob = ProfileTestFiles.Write(_directory.Path, "reply-job-missing", WithReply(ReplySection(playerJob: true)));
        Assert.Contains(ProfileLoader.Validate(withoutJob).Errors, issue =>
            issue.Code == "E_PROFILE_CALIBRATION_REPLY" && issue.Path.EndsWith("player_job", StringComparison.Ordinal));

        var document = WithReply(ReplySection(playerJob: true));
        ((List<object>)document["messages"]).Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = "PLAYER_JOB",
            ["opcode"] = 103,
            ["direction"] = "SERVER_TO_CLIENT",
            ["expected_length"] = 40,
            ["fields"] = new List<object>
            {
                ProfileTestFiles.Field("finder_state", 0, "u8", allowed: new[] { 1 }, role: "selector"),
                ProfileTestFiles.Field("job_id", 1, "u8", min: 1, max: 43),
            },
        });
        ((List<object>)((Dictionary<string, object>)document["provenance"])["evidence"])
            .Add(Evidence("messages.PLAYER_JOB.opcode"));
        var withJob = ProfileTestFiles.Write(_directory.Path, "reply-job", document);

        var report = ProfileLoader.Validate(withJob);
        Assert.Empty(report.Errors);
        Assert.True(report.Profile!.Calibration!.FinderReply!.CarriesPlayerJob);

        // A job message of another length is not the reply.
        var job = (Dictionary<string, object>)((List<object>)document["messages"])[^1];
        job["expected_length"] = 16;
        job["fields"] = new List<object> { ProfileTestFiles.Field("job_id", 0, "u8", min: 1, max: 43) };
        var mismatch = ProfileTestFiles.Write(_directory.Path, "reply-job-length", document);
        Assert.Contains(ProfileLoader.Validate(mismatch).Errors, issue =>
            issue.Code == "E_PROFILE_CALIBRATION_REPLY" && issue.Path.EndsWith("player_job", StringComparison.Ordinal));
    }

    [Fact]
    public void FinderReplyRouletteFieldGetsTheRequestsChecks()
    {
        var bytes = ReplySection();
        bytes["roulette_field"] = ProfileTestFiles.Field("roulette_id", 16, "bytes", length: 1);
        Assert.Contains(ProfileLoader.Validate(ProfileTestFiles.Write(_directory.Path, "reply-bytes", WithReply(bytes))).Errors,
            issue => issue.Code == "E_PROFILE_CALIBRATION_FIELD");

        var outside = ReplySection();
        outside["roulette_field"] = ProfileTestFiles.Field("roulette_id", 40, "u8");
        Assert.Contains(ProfileLoader.Validate(ProfileTestFiles.Write(_directory.Path, "reply-oob", WithReply(outside))).Errors,
            issue => issue.Code == "E_PROFILE_FIELD_OOB");
    }

    [Fact]
    public void CnTemplateReadsTheReplyAsThePop()
    {
        var template = CalibrationObserverTests.Template();

        Assert.False(template.SeparateReply);
        Assert.False(template.ReplyCarriesJob);
        Assert.False(template.TerritoryInZone);
        Assert.Same(template.Pop, template.Reply);
        Assert.Same(template.Pop.Field("roulette_id"), template.ReplyRoulette);
        Assert.Same(template.ZoneTerritory, template.TerritoryShape);
    }

    [Fact]
    public void ShippedCnProfileCarriesACalibrationTemplate()
    {
        var catalog = ProfileCatalog.Load(Path.Combine(AppContext.BaseDirectory, "protocol-profiles"));
        var cn = Assert.Single(catalog.UsableFor(MentorRecorder.Collector.Domain.Region.Cn),
            profile => profile.ProfileId == "cn.2026.08.05");

        var calibration = Assert.IsType<ProfileCalibration>(cn.Calibration);
        Assert.Equal(24, calibration.FinderRequest.ExpectedLength);
        Assert.Equal(PacketDirection.ClientToServer, calibration.FinderRequest.Direction);
        Assert.Equal(0, calibration.FinderRequest.RouletteField.Offset);
    }
}
