using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Tests;

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
        // Plan §20.5: every combination of the consensus fields, the longest dispute included.
        var candidates = Enum.GetValues<SharedCandidateStatus>()
            .Select((status, index) => new SharedCandidateSummary(
                (index + 1).ToString("x12"), index % 2 == 0 ? SharedCandidateSource.Downloaded : null,
                index == 0 ? null : matchSources[index % matchSources.Length], status, SharedVerdict.Wait, criteria, index % 3 == 0)
            {
                Disputed = index == 1 ? SharedCandidateConsensus.Fields : index % 2 == 0 ? new[] { CalibratedShape.PopName } : Array.Empty<string>(),
                Preferred = index % 2 == 1,
                LocalShapePrior = (index % 3) switch { 0 => (bool?)null, 1 => true, _ => false },
            })
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
            Recheck = new SharedRecheckRecord(At, SharedFetchStatus.Ok, SharedRecheckReason.SharedInUse),
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

        // A Collector up to 1.7.2 sends candidates without the consensus fields.
        var beforeConsensus = status.DeepClone().AsObject();
        foreach (var candidate in beforeConsensus["shared"]!["candidates"]!.AsArray())
        {
            Assert.True(candidate!.AsObject().Remove("disputed"));
            Assert.True(candidate.AsObject().Remove("preferred"));
            Assert.True(candidate.AsObject().Remove("shape_prior"));
        }

        ContractSchema.Validate("$defs/CalibrationStatus", beforeConsensus, "calibration status from a Collector before the consensus");
    }

    [Fact]
    public void TheConsensusFieldsAreDeclaredWithTheMessageNamesTheyCarry()
    {
        var candidate = ContractSchema.Root["$defs"]!["SharedCalibrationCandidate"]!["properties"]!;

        Assert.Equal(
            SharedCandidateConsensus.Fields.Order(StringComparer.Ordinal),
            candidate["disputed"]!["items"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal("boolean", candidate["preferred"]!["type"]!.GetValue<string>());
        Assert.NotNull(candidate["shape_prior"]);
        Assert.DoesNotContain("disputed", ContractSchema.Root["$defs"]!["SharedCalibrationCandidate"]!["required"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void EverySuccessShapeOfTheSharedCalibrationMessagesMatchesTheContract()
    {
        const string code = ShareCode.Prefix + "XY_LasMwEEX";
        var sha = new string('a', 64);
        foreach (var basis in Enum.GetValues<SharedShareBasis>())
        {
            var (url, inUrl) = SharedCalibrationIssueLink.For(Region.Cn, "2026.09.01.0000.0000", code, basis);
            var form = url[..url.IndexOf("&code=", StringComparison.Ordinal)];

            foreach (var shareCode in new[] { new SharedShareCode(code, sha, url, inUrl, basis), new SharedShareCode(code, sha, form, false, basis) })
            {
                var response = SharedCalibrationHandlers.ShareCodeResponse(shareCode);
                Assert.Equal(EnumWire<SharedShareBasis>.Format(basis), response["basis"]!.GetValue<string>());
                ContractSchema.Validate("$defs/Responses/GetCalibrationShareCode", response, "share code " + basis);

                // A Collector up to 1.7.2 answers without the basis.
                Assert.True(response.Remove("basis"));
                ContractSchema.Validate("$defs/Responses/GetCalibrationShareCode", response, "share code without a basis");
            }
        }

        foreach (var imported in new[]
                 {
                     new SharedImportResult(SharedImportOutcome.Applied, null, CollectorTexts.SharingImportApplied(), sha),
                     new SharedImportResult(SharedImportOutcome.NotApplicable, "OTHER_BUILD",
                         CollectorTexts.SharingImportOtherBuild("2026.09.01.0000.0000", "2026.09.08.0000.0000"), sha),
                     new SharedImportResult(SharedImportOutcome.Malformed, "E_SHARE_CODE_BASE64",
                         CollectorTexts.SharingImportNotImported(CollectorTexts.SharingCodeTruncated())),
                 })
        {
            var response = SharedCalibrationHandlers.ImportResponse(imported);
            ContractSchema.Validate("$defs/Responses/ImportCalibrationCode", response, "import");
            CollectorTextCatalogue.AssertParallel(response["message"], response["message_ref"]);

            // A Collector before 2026-10-15 answers without the reference.
            Assert.True(response.Remove("message_ref"));
            ContractSchema.Validate("$defs/Responses/ImportCalibrationCode", response, "import without a reference");
        }

        foreach (var rejected in new[] { new SharedRejectResult("cn.2026.09.01.0000.0000.shared", 3), new SharedRejectResult(null, 0) })
        {
            ContractSchema.Validate("$defs/Responses/RejectSharedCalibration", SharedCalibrationHandlers.RejectResponse(rejected), "reject");
        }

        Assert.All(EnumWire<SharedCheckOutcome>.AllTokens, token => ContractSchema.Validate(
            "$defs/Responses/CheckSharedCalibration", SharedCalibrationHandlers.OutcomeResponse(token), token));
        foreach (var outcome in Enum.GetValues<SharedCheckOutcome>())
        {
            var plain = SharedCalibrationHandlers.CheckResponse(new SharedCheckAnswer(outcome));
            ContractSchema.Validate("$defs/Responses/CheckSharedCalibration", plain, "check " + outcome);
            Assert.False(plain.ContainsKey("message"));
            Assert.False(plain.ContainsKey("message_ref"));
        }

        // Re-review 2026-10-21, RV5-5: a check refused because the records cannot be read says so.
        var unreadable = SharedCalibrationHandlers.CheckResponse(
            new SharedCheckAnswer(SharedCheckOutcome.NotNeeded, CollectorTexts.SharingCheckStateUnreadable()));
        ContractSchema.Validate("$defs/Responses/CheckSharedCalibration", unreadable, "check, records unreadable");
        Assert.Equal("NOT_NEEDED", unreadable["outcome"]!.GetValue<string>());
        Assert.Equal("sharing.check.state_unreadable", unreadable["message_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(unreadable["message"], unreadable["message_ref"]);
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
        Assert.Equal(Tokens<SharedRecheckReason>(), Declared("$defs", "SharedRecheckReason", "enum"));
        Assert.Equal(Tokens<SharedCalibrationSource>(), Declared("$defs", "SharedCalibrationSource", "enum"));
        Assert.Equal(Tokens<SharedFetchOutcome>(), Declared("$defs", "SharedFetchOutcome", "enum"));
        Assert.Equal(Tokens<CalibrationMatchSource>(), Declared("$defs", "CalibrationMatchSource", "enum"));
        Assert.Equal(Tokens<SharedCandidateSource>(), Declared("$defs", "SharedCalibrationCandidate", "properties", "source", "enum"));
        Assert.Equal(Tokens<SharedCheckOutcome>(), Declared("$defs", "Responses", "CheckSharedCalibration", "properties", "outcome", "enum"));
        Assert.Equal(Tokens<SharedConsentOutcome>(), Declared("$defs", "Responses", "AcceptSharedQueueInference", "properties", "outcome", "enum"));
        Assert.Equal(Tokens<SharedImportOutcome>(), Declared("$defs", "Responses", "ImportCalibrationCode", "properties", "outcome", "enum"));
        Assert.Equal(Tokens<SharedShareBasis>(), Declared("$defs", "Responses", "GetCalibrationShareCode", "properties", "basis", "enum"));
        Assert.Equal(new[] { "CALIBRATED", "VERIFIED" }, Tokens<SharedShareBasis>());
        Assert.Equal(
            new[] { "NOT_LOCAL", "NOT_SHAREABLE", "NO_PROFILE", "SHARED", "SHARED_NOT_PROVEN" }, Tokens<SharedShareCodeRefusal>());

        var origins = Declared("$defs", "CaptureStatus", "properties", "profile_origin", "enum");
        Assert.All(Enum.GetValues<ProfileOrigin>(), origin => Assert.Contains(CalibrationWire.Origin(origin) ?? "UNMAPPED", origins));
    }
}
