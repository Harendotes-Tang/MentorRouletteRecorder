using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The shared-calibration shapes the Collector writes, taken straight from the renderers and
/// held against contracts/ipc-v1.schema.json, including the success shapes a pipe test on a
/// machine with no game cannot reach. Every token the Collector can write is checked against
/// the enums the contract declares.
/// </summary>
public sealed class SharedCalibrationContractTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Something in every field, every candidate status and every source outcome.</summary>
    private static SharedCalibrationSnapshot Everything()
    {
        var criteria = new[]
        {
            new SharedCriterion(CalibratedShape.PopName, SharedVerdict.Pass, "排本与弹窗对上了。"),
            new SharedCriterion(CalibratedShape.ZoneName, SharedVerdict.Contradicted, "两次登录都没有见到这条换区报文。", 2),
        };
        var matchSources = Enum.GetValues<CalibrationMatchSource>();
        var candidates = Enum.GetValues<SharedCandidateStatus>()
            .Select((status, index) => new SharedCandidateSummary(
                (index + 1).ToString("x12"), index % 2 == 0 ? SharedCandidateSource.Downloaded : null,
                index == 0 ? null : matchSources[index % matchSources.Length], status, SharedVerdict.Wait, criteria, index % 3 == 0))
            .ToArray();
        var sources = Enum.GetValues<SharedCalibrationSource>();
        var attempts = Enum.GetValues<SharedFetchOutcome>()
            .Select((outcome, index) => new SharedSourceAttempt(sources[index % sources.Length], outcome, 404, "DETAIL"))
            .Take(8)
            .ToArray();
        return new SharedCalibrationSnapshot(
            SharedCalibrationPhase.Rejected, candidates, SharedFetchStatus.CodesUnavailable, attempts,
            "cn.2026.09.01.0000.0000.shared", At, "INTERNAL:IOException")
        {
            UserRejected = true,
            LastSentAtUtc = At,
            LastSentStatus = SharedFetchStatus.Ok,
        };
    }

    private static string[] Declared(params string[] pointer)
    {
        JsonNode? node = ContractSchema.Root;
        foreach (var segment in pointer)
        {
            node = node![segment];
        }

        return node!.AsArray().OfType<JsonNode>().Select(item => item.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Tokens<TEnum>()
        where TEnum : struct, Enum =>
        EnumWire<TEnum>.AllTokens.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void CalibrationStatusWithTheSharedPartMatchesTheContractAndStillDoesWithoutIt()
    {
        var status = CalibrationWire.Status(CalibrationStatusSnapshot.Idle with { Shared = Everything() });

        ContractSchema.Validate("$defs/CalibrationStatus", status, "calibration status with everything shared");
        ContractSchema.Validate("$defs/CalibrationStatus", CalibrationWire.Status(CalibrationStatusSnapshot.Idle), "idle calibration status");
        var older = status.DeepClone().AsObject();
        Assert.True(older.Remove("shared"));
        ContractSchema.Validate("$defs/CalibrationStatus", older, "calibration status from a Collector before shared calibration");
    }

    [Fact]
    public void EverySuccessShapeOfTheSharedCalibrationMessagesMatchesTheContract()
    {
        const string code = ShareCode.Prefix + "XY_LasMwEEX";
        var sha = new string('a', 64);
        var (url, inUrl) = SharedCalibrationIssueLink.For(Region.Cn, "2026.09.01.0000.0000", code);
        var form = url[..url.IndexOf("&code=", StringComparison.Ordinal)];

        foreach (var shareCode in new[] { new SharedShareCode(code, sha, url, inUrl), new SharedShareCode(code, sha, form, false) })
        {
            ContractSchema.Validate("$defs/Responses/GetCalibrationShareCode",
                SharedCalibrationHandlers.ShareCodeResponse(shareCode), "share code");
        }

        foreach (var imported in new[]
                 {
                     new SharedImportResult(SharedImportOutcome.Applied, null, "校准码已导入。", sha),
                     new SharedImportResult(SharedImportOutcome.NotApplicable, "OTHER_BUILD", "这份校准码适用于另一个客户端版本。", sha),
                     new SharedImportResult(SharedImportOutcome.Malformed, "E_SHARE_CODE_BASE64", "这不是一份能识别的校准码。"),
                 })
        {
            ContractSchema.Validate("$defs/Responses/ImportCalibrationCode", SharedCalibrationHandlers.ImportResponse(imported), "import");
        }

        foreach (var rejected in new[] { new SharedRejectResult("cn.2026.09.01.0000.0000.shared", 3), new SharedRejectResult(null, 0) })
        {
            ContractSchema.Validate("$defs/Responses/RejectSharedCalibration", SharedCalibrationHandlers.RejectResponse(rejected), "reject");
        }

        Assert.All(EnumWire<SharedCheckOutcome>.AllTokens, token => ContractSchema.Validate(
            "$defs/Responses/CheckSharedCalibration", SharedCalibrationHandlers.OutcomeResponse(token), token));
        Assert.All(EnumWire<SharedConsentOutcome>.AllTokens, token => ContractSchema.Validate(
            "$defs/Responses/AcceptSharedQueueInference", SharedCalibrationHandlers.OutcomeResponse(token), token));
    }

    [Fact]
    public void TheContractDeclaresEveryTokenTheCollectorWrites()
    {
        Assert.Equal(Tokens<SharedCalibrationPhase>(), Declared("$defs", "SharedCalibrationPhase", "enum"));
        Assert.Equal(Tokens<SharedCandidateStatus>(), Declared("$defs", "SharedCandidateStatus", "enum"));
        Assert.Equal(Tokens<SharedVerdict>(), Declared("$defs", "SharedVerdict", "enum"));
        Assert.Equal(Tokens<SharedFetchStatus>(), Declared("$defs", "SharedFetchStatus", "enum"));
        Assert.Equal(Tokens<SharedCalibrationSource>(), Declared("$defs", "SharedCalibrationSource", "enum"));
        Assert.Equal(Tokens<SharedFetchOutcome>(), Declared("$defs", "SharedFetchOutcome", "enum"));
        Assert.Equal(Tokens<CalibrationMatchSource>(), Declared("$defs", "CalibrationMatchSource", "enum"));
        Assert.Equal(Tokens<SharedCandidateSource>(), Declared("$defs", "SharedCalibrationCandidate", "properties", "source", "enum"));
        Assert.Equal(Tokens<SharedCheckOutcome>(), Declared("$defs", "Responses", "CheckSharedCalibration", "properties", "outcome", "enum"));
        Assert.Equal(Tokens<SharedConsentOutcome>(), Declared("$defs", "Responses", "AcceptSharedQueueInference", "properties", "outcome", "enum"));
        Assert.Equal(Tokens<SharedImportOutcome>(), Declared("$defs", "Responses", "ImportCalibrationCode", "properties", "outcome", "enum"));

        var origins = Declared("$defs", "CaptureStatus", "properties", "profile_origin", "enum");
        Assert.All(Enum.GetValues<ProfileOrigin>(), origin => Assert.Contains(CalibrationWire.Origin(origin) ?? "UNMAPPED", origins));
    }
}
