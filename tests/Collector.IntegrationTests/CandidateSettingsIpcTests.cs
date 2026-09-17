using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class CandidateSettingsIpcTests
{
    [Fact]
    public async Task ExplicitSettingSelectsCandidateThroughRealPipeAlongsideTheFormalBinding()
    {
        await using var fixture = ServerFixture.Start();
        var initial = (await fixture.CallAsync("GetCaptureSettings")).Require();
        Assert.False(initial["candidate_validation_enabled"]!.GetValue<bool>());
        var updated = (await fixture.CallAsync("UpdateCaptureSettings", new JsonObject
            { ["candidate_validation_enabled"] = true })).Require();
        ContractSchema.Validate("$defs/Responses/UpdateCaptureSettings", updated, "candidate setting");
        Assert.True(updated["candidate_validation_enabled"]!.GetValue<bool>());
        var pipeline = fixture.Host.LiveProtocol!;
        var status = pipeline.Refresh(GameProcessDetection.NotRunning with
            { Region = Region.Cn, GameBuild = "2026.08.05.0000.0000" });
        Assert.Equal("cn.2026.08.05.candidate", pipeline.CandidateProfileId);
        // Candidate validation observes alongside the formal profile rather than replacing it.
        Assert.Equal(ProfileStatus.Verified, status.Status);
        Assert.Empty((await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray());
        Assert.True((await fixture.CallAsync("UpdateCaptureSettings", new JsonObject
            { ["candidate_validation_enabled"] = false })).Ok);
        Assert.Null(pipeline.CandidateProfileId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    public async Task CandidateSettingRequiresBoolean(string value)
    {
        await using var fixture = ServerFixture.Start();
        var response = await fixture.CallAsync("UpdateCaptureSettings", new JsonObject
            { ["candidate_validation_enabled"] = JsonNode.Parse(value) });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }
}
