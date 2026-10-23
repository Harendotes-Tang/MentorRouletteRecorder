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
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));

    public CalibrationTemplateTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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
        var path = ProfileTestFiles.Write(_directory, "with-calibration", document);

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
        var path = ProfileTestFiles.Write(_directory, "bytes-field", document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_CALIBRATION_FIELD");
    }

    [Fact]
    public void VerifiedTemplateNeedsEvidenceForItsRequestShape()
    {
        var document = VerifiedDocument(withCalibrationEvidence: false);
        document["calibration"] = CalibrationSection();
        var missing = ProfileTestFiles.Write(_directory, "template-no-evidence", document);
        Assert.Contains(ProfileLoader.Validate(missing).Errors,
            issue => issue.Code == "E_PROFILE_NO_EVIDENCE" && issue.Message.Contains("calibration.finder_request"));

        var withEvidence = VerifiedDocument();
        withEvidence["calibration"] = CalibrationSection();
        var covered = ProfileTestFiles.Write(_directory, "template-with-evidence", withEvidence);
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
        var path = ProfileTestFiles.Write(_directory, "without-calibration", ProfileTestFiles.Valid());

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
        var path = ProfileTestFiles.Write(_directory, "refused-" + status.ToLowerInvariant(), document);

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
        var path = ProfileTestFiles.Write(_directory, "unknown-key", document);

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
        var path = ProfileTestFiles.Write(_directory, "oob", document);

        var report = ProfileLoader.Validate(path);

        Assert.Contains(report.Errors, issue => issue.Code == "E_PROFILE_FIELD_OOB");
    }

    [Fact]
    public void CalibrationSectionTakesPartInTheCanonicalHash()
    {
        var document = VerifiedDocument();
        document["calibration"] = CalibrationSection();
        var path = ProfileTestFiles.Write(_directory, "stamped", document);
        var stamped = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        var without = VerifiedDocument();
        without["profile_id"] = "stamped";
        var bare = JsonSerializer.Serialize(without);

        Assert.NotEqual(ProfileLoader.ComputeProfileHash(bare), stamped.GetProperty("profile_sha256").GetString());
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
