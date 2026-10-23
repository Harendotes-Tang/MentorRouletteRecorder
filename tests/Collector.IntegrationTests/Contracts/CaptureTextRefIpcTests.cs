using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Capture, validation, calibration and speech sentences over a real Named Pipe carry their
/// catalogue references beside the Chinese (contracts/collector-texts.md §2.1). Each reference is
/// rendered back with contracts/collector-texts.json and must give exactly the Chinese field, so
/// the companion is parallel to what an older Desktop shows; ContractSchemaTests checks the shapes.
/// </summary>
public sealed class CaptureTextRefIpcTests
{
    [Fact]
    public async Task GetStatus_CarriesAReferenceForEveryWarningAndTheNpcapHint()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled: false));

        var status = (await fixture.CallAsync("GetStatus")).Require();

        // Npcap is missing, so its guidance is one of the warnings.
        var warnings = status["warnings"]!.AsArray();
        Assert.NotEmpty(warnings);
        var refs = status["warning_refs"]!.AsArray();
        CollectorTextCatalogue.AssertParallelList(warnings, refs);
        Assert.Contains(refs, item => item!["key"]!.GetValue<string>() == "capture.npcap.not_installed");

        var npcap = status["npcap"]!;
        Assert.Equal("capture.npcap.not_installed", npcap["install_hint_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(npcap["install_hint"], npcap["install_hint_ref"]);

        // No capture is running, so there is no hint and no reference either.
        var capture = status["capture"]!.AsObject();
        Assert.True(capture.ContainsKey("hint_ref"));
        Assert.Null(capture["hint"]);
        Assert.Null(capture["hint_ref"]);
    }

    [Fact]
    public async Task ListCaptureAdapters_CarriesTheInstallHintReference()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled: false));

        var payload = (await fixture.CallAsync("ListCaptureAdapters")).Require();

        CollectorTextCatalogue.AssertParallel(payload["install_hint"], payload["install_hint_ref"]);
        Assert.Equal("capture.npcap.not_installed", payload["install_hint_ref"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetProtocolProfileStatus_CarriesTheMessageReference()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled: true));

        var payload = (await fixture.CallAsync("GetProtocolProfileStatus")).Require();

        Assert.True(payload.ContainsKey("message_ref"));
        CollectorTextCatalogue.AssertParallel(payload["message"], payload["message_ref"]);
    }

    [Theory]
    [InlineData(false, "ERR_NPCAP_MISSING", "capture.npcap.not_installed")]
    [InlineData(true, "ERR_FFXIV_NOT_RUNNING", "capture.start.game_not_running")]
    public async Task StartCapture_RefusesWithAReferenceBesideTheMessage(bool npcapInstalled, string code, string key)
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled));

        var response = await fixture.CallAsync("StartCapture");

        Assert.False(response.Ok);
        Assert.Equal(code, response.ErrorCode);
        AssertErrorRef(response, key);
    }

    [Fact]
    public async Task StopCapture_RefusesWithAReferenceBesideTheMessage()
    {
        await using var fixture = CaptureServerFixture.Start();

        AssertErrorRef(await fixture.CallAsync("StopCapture"), "capture.stop.not_running");
    }

    [Fact]
    public async Task TheValidationStatusCarriesItsMessageReference()
    {
        await using var fixture = CaptureServerFixture.Start();

        var status = (await fixture.CallAsync("GetCaptureValidationStatus")).Require();

        Assert.Equal("capture.validation.idle", status["message_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(status["message"], status["message_ref"]);

        AssertErrorRef(
            await fixture.CallAsync("StartCaptureValidation", new JsonObject { ["adapter_id"] = " " }),
            "capture.validation.adapter_id_invalid");
    }

    [Fact]
    public async Task CalibrationRefusalsCarryTheirReferences()
    {
        await using var fixture = CaptureServerFixture.Start();

        AssertErrorRef(
            await fixture.CallAsync("ConfirmCalibration", new JsonObject { ["verdicts"] = new JsonArray() }),
            "calibration.verdicts_empty");
        AssertErrorRef(
            await fixture.CallAsync("DiscardCalibration", new JsonObject
            {
                ["retire_local_profile"] = true,
                ["restore_local_profile"] = true,
            }),
            "calibration.discard_conflict");
    }

    [Fact]
    public async Task TheCuratedVoicesCarryLabelReferencesAndTheServicesOwnNamesDoNot()
    {
        await using var fixture = CaptureServerFixture.Start();

        var settings = (await fixture.CallAsync("GetSpeechSettings")).Require();

        var azure = settings["azure_voices"]!.AsArray();
        Assert.NotEmpty(azure);
        Assert.All(azure, voice =>
        {
            Assert.StartsWith("speech.voice.", voice!["label_ref"]!["key"]!.GetValue<string>(), StringComparison.Ordinal);
            CollectorTextCatalogue.AssertParallel(voice["label"], voice["label_ref"]);
        });
        Assert.Equal(
            "speech.voice.xiaoxiao",
            azure.Single(voice => voice!["name"]!.GetValue<string>() == "zh-CN-XiaoxiaoNeural")!["label_ref"]!["key"]!
                .GetValue<string>());

        // The OpenAI voices are labelled with their own names, which are not the catalogue's.
        Assert.All(settings["openai_voices"]!.AsArray(), voice => Assert.False(voice!.AsObject().ContainsKey("label_ref")));
    }

    private static void AssertErrorRef(IpcResponse response, string key)
    {
        Assert.False(response.Ok);
        Assert.Equal(key, response.Payload["message_ref"]!["key"]!.GetValue<string>());
        Assert.Equal(response.ErrorMessage, CollectorTextCatalogue.Render(response.Payload["message_ref"]));
    }
}
