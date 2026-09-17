using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Shared calibration on the wire: the <c>SHARED_CALIBRATION</c> origin,
/// the whitelisted <c>calibration.shared</c> object, the issue form address, and the share code of the local
/// calibration in force, taken from its file.
/// </summary>
public sealed class SharedCalibrationWireTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    /// <summary>A snapshot with something in every field, including a refusal that names a file.</summary>
    internal static SharedCalibrationSnapshot Busy() => new(
        SharedCalibrationPhase.Verifying,
        new[]
        {
            new SharedCandidateSummary(
                "0123456789ab", SharedCandidateSource.Downloaded, CalibrationMatchSource.ReplyState, SharedCandidateStatus.Verifying,
                SharedVerdict.Wait, new[] { new SharedCriterion(CalibratedShape.ZoneName, SharedVerdict.Wait, "还没见到登录时的换区。", 1) },
                false)
            {
                Provenance = SharedCandidateProvenance.Published,
                AuditPending = true,
            },
            new SharedCandidateSummary(
                "ba9876543210", SharedCandidateSource.Manual, null, SharedCandidateStatus.Rejected, SharedVerdict.Contradicted,
                Array.Empty<SharedCriterion>(), true),
        },
        SharedFetchStatus.Ok,
        new[]
        {
            new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.HttpStatus, 404, "NOT_FOUND"),
            new SharedSourceAttempt(SharedCalibrationSource.CdnPrimary, SharedFetchOutcome.Ok),
        },
        null,
        null,
        @"NOT_SELECTED: C:\Users\someone\AppData\Local\MentorRecorder\protocol-profiles-shared\cn\cn.x.shared.json")
    {
        LastSentAtUtc = Bed.Confirmed,
        LastSentStatus = SharedFetchStatus.Ok,
    };

    private static ProfileSelection Selecting(ProtocolProfile profile, ProfileOrigin origin) =>
        new(ProfileCompatibilityStatus.Verified, profile.ToBinding(), profile, Region.Cn, Bed.Build, "selected", origin);

    private static Dictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    [Fact]
    public void TheOriginOfASharedProfileHasItsOwnToken()
    {
        Assert.Equal("SHARED_CALIBRATION", CalibrationWire.Origin(ProfileOrigin.Shared));
        Assert.Equal("LOCAL_CALIBRATION", CalibrationWire.Origin(ProfileOrigin.Local));
        Assert.Equal("SHIPPED", CalibrationWire.Origin(ProfileOrigin.Shipped));
        Assert.Null(CalibrationWire.Origin(null));
    }

    [Fact]
    public void TheSharedStatusCarriesTokensAndPrefixesButNoAddressDetailOrPath()
    {
        var node = CalibrationWire.Status(CalibrationStatusSnapshot.Idle with { Shared = Busy() })["shared"]!.AsObject();

        Assert.Equal("VERIFYING", node["phase"]!.GetValue<string>());
        var first = node["candidates"]![0]!;
        Assert.Equal("0123456789ab", first["sha12"]!.GetValue<string>());
        Assert.Equal("DOWNLOADED", first["source"]!.GetValue<string>());
        Assert.Equal("REPLY_STATE", first["match_source"]!.GetValue<string>());
        Assert.Equal("WAIT", first["verdict"]!.GetValue<string>());
        var criterion = first["criteria"]![0]!;
        Assert.Equal("ZONE_INITIALIZATION", criterion["message"]!.GetValue<string>());
        Assert.Equal(1, criterion["contradicting_sessions"]!.GetValue<int>());
        Assert.Equal("REQUIRED", criterion["gate"]!.GetValue<string>());
        var second = node["candidates"]![1]!;
        Assert.Equal("MANUAL", second["source"]!.GetValue<string>());
        Assert.Null(second["match_source"]);
        Assert.Equal("REJECTED", second["status"]!.GetValue<string>());
        Assert.True(second["staging_overflowed"]!.GetValue<bool>());
        Assert.Equal("OK", node["last_fetch_status"]!.GetValue<string>());
        var attempt = node["last_index_attempts"]![0]!.AsObject();
        Assert.Equal(new[] { "outcome", "source" }, attempt.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("GITHUB_RAW", attempt["source"]!.GetValue<string>());
        Assert.Equal("HTTP_STATUS", attempt["outcome"]!.GetValue<string>());
        Assert.Equal("NOT_SELECTED", node["last_refusal"]!.GetValue<string>());
        Assert.Equal(1, node["rejected_candidates"]!.GetValue<int>());
        Assert.False(node["user_rejected"]!.GetValue<bool>());
        // Plan §18: the gate set and the audit, per candidate and for the profile in use.
        Assert.Equal("PUBLISHED", first["provenance"]!.GetValue<string>());
        Assert.True(first["audit_pending"]!.GetValue<bool>());
        Assert.Null(second["provenance"]);
        Assert.False(second["audit_pending"]!.GetValue<bool>());
        Assert.False(node["audit_pending"]!.GetValue<bool>());

        var text = node.ToJsonString();
        Assert.DoesNotContain("AppData", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_FOUND", text, StringComparison.Ordinal);
        // HTTP_STATUS is an outcome token; an address would carry a scheme.
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(SharedCalibrationClient.AllowedHosts, host => Assert.DoesNotContain(host, text, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("REVOKED", "REVOKED")]
    [InlineData("INTERNAL:IOException", "INTERNAL")]
    [InlineData("NOT_SELECTED: no profile matches this region and build", "NOT_SELECTED")]
    [InlineData("WRITE_FAILED:UnauthorizedAccessException", "WRITE_FAILED")]
    [InlineData("", null)]
    [InlineData("lower case", null)]
    public void OnlyTheLeadingTokenOfARefusalLeavesTheMachine(string refusal, string? token) =>
        Assert.Equal(token, CalibrationWire.RefusalToken(refusal));

    [Fact]
    public void TheIssueLinkNamesTheFormTitleAndCodeAndLeavesACodeThatWouldNotFitOut()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);

        var (url, inUrl) = SharedCalibrationIssueLink.For(Region.Cn, Bed.Build, code.Code);

        Assert.True(inUrl);
        Assert.StartsWith(
            "https://github.com/" + SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository +
            "/issues/new?template=share-calibration.yml&title=", url, StringComparison.Ordinal);
        Assert.Contains("&title=" + Uri.EscapeDataString("[共享校准] CN " + Bed.Build) + "&", url, StringComparison.Ordinal);
        Assert.EndsWith("&code=" + Uri.EscapeDataString(code.Code), url, StringComparison.Ordinal);
        Assert.DoesNotContain("共享", url, StringComparison.Ordinal);
        Assert.True(url.Length <= SharedCalibrationIssueLink.MaxUrlLength);

        var (form, fits) = SharedCalibrationIssueLink.For(Region.Cn, Bed.Build, code.Code, url.Length - 1);

        Assert.False(fits);
        Assert.Equal(url[..url.IndexOf("&code=", StringComparison.Ordinal)], form);
    }

    [Fact]
    public void TheShareCodeIsTheOneTheLocalProfileFileGivesAndNoneOnceTheFileChanged()
    {
        var written = LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var profile = ProfileLoader.Load(written.Path);

        var result = SharedShareCodeExport.From(Selecting(profile, ProfileOrigin.Local), _ => Bed.Template);

        var code = Assert.IsType<SharedShareCode>(result.ShareCode);
        Assert.Null(result.Refusal);
        var expected = SharedProfileBuilder.ToShareCode(profile, Bed.Template);
        Assert.Equal(expected.Code, code.Code);
        Assert.Equal(expected.CodeSha256, code.CodeSha256);
        Assert.Equal(SharedCalibrationIssueLink.For(Region.Cn, Bed.Build, code.Code).Url, code.IssueUrl);
        Assert.True(code.CodeInUrl);

        // The file on disk is what is shared: once it is no longer the profile in force, there is no code.
        var rewritten = _bed.WriteRefusedLocalProfile();
        Assert.Equal(written.Path, rewritten);
        Assert.Equal(SharedShareCodeRefusal.NotShareable,
            SharedShareCodeExport.From(Selecting(profile, ProfileOrigin.Local), _ => Bed.Template).Refusal);
    }

    [Fact]
    public void AnythingButAUsableLocalCalibrationGivesNoCodeAndSaysWhyInChinese()
    {
        var local = ProfileLoader.Load(LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState), Bed.Template, Bed.Build, Bed.Confirmed,
            Path.Combine(_bed.Root, "other-local")).Path);
        var shared = SharedProfileBuilder.Build(
            _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState).Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()).Profile!;
        var refused = ProfileLoader.Load(_bed.WriteRefusedLocalProfile());
        var none = new ProfileSelection(
            ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null, Region.Cn, Bed.Build, ProfileSelector.NoProfileMatchesReason);

        var cases = new (ProfileSelection Selection, Func<Region, CalibrationTemplate?> Template, SharedShareCodeRefusal Refusal)[]
        {
            (none, _ => Bed.Template, SharedShareCodeRefusal.NoProfile),
            (Selecting(shared, ProfileOrigin.Shared), _ => Bed.Template, SharedShareCodeRefusal.Shared),
            (Selecting(local, ProfileOrigin.Shipped), _ => Bed.Template, SharedShareCodeRefusal.NotLocal),
            (Selecting(refused, ProfileOrigin.Local), _ => Bed.Template, SharedShareCodeRefusal.NotShareable),
            (Selecting(local, ProfileOrigin.Local), _ => null, SharedShareCodeRefusal.NotShareable),
        };

        foreach (var (selection, template, refusal) in cases)
        {
            var result = SharedShareCodeExport.From(selection, template);
            Assert.Null(result.ShareCode);
            Assert.Equal(refusal, result.Refusal);
            Assert.Matches("[\\u4e00-\\u9fff]", result.Message);
            Assert.DoesNotContain("0x", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(refused.ToBinding().IsUsable);
    }

    [Fact]
    public async Task APipelineSharesTheLocalCalibrationItConfirmedButNotASharedProfile()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        Assert.Equal(SharedShareCodeRefusal.NoProfile, pipeline.GetCalibrationShareCode().Refusal);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, CalibrationObserverTests.Session1());

        var confirmed = pipeline.ConfirmCalibration(AllCorrect(pipeline.CalibrationStatus()));

        var code = Assert.IsType<SharedShareCode>(pipeline.GetCalibrationShareCode().ShareCode);
        Assert.Equal(SharedProfileBuilder.ToShareCode(ProfileLoader.Load(confirmed.ProfilePath), Bed.Template).Code, code.Code);
        Assert.Equal(Bed.Build, ShareCode.Decode(code.Code).Payload!.GameBuild);

        var other = new SharedCalibrationTestBed();
        try
        {
            var sharedCode = other.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
            SharedProfileFiles.Write(
                SharedProfileBuilder.Build(sharedCode.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()), other.SharedRoot);
            var restarted = other.Pipeline(other.Services(fetch: false));
            Assert.Equal(ProfileOrigin.Shared, restarted.Refresh(Bed.Game()).Origin);
            Assert.Equal(SharedShareCodeRefusal.Shared, restarted.GetCalibrationShareCode().Refusal);
            await SharedCalibrationTestBed.Idle(restarted);
        }
        finally
        {
            other.Dispose();
        }
    }
}
