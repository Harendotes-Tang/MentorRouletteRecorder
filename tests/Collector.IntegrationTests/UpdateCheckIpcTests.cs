using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The update check over the real pipe: what <c>GetStatus</c> says about it, and the switch that
/// governs it. The fixture's update client refuses to send, so a status frame that somehow reached
/// a release host would fail here rather than in the field.
/// </summary>
public sealed class UpdateCheckIpcTests
{
    [Fact]
    public async Task GetStatusCarriesTheUpdateObject()
    {
        await using var fixture = ServerFixture.Start();

        var status = (await fixture.CallAsync("GetStatus")).Require();
        ContractSchema.Validate("$defs/Responses/GetStatus", status, "status with update");
        var update = status["update"]!.AsObject();

        Assert.True(update["enabled"]!.GetValue<bool>());
        Assert.False(update["update_available"]!.GetValue<bool>());
        Assert.Equal(UpdateCheckClient.ReleaseUrl, update["release_url"]!.GetValue<string>());
        Assert.False(update.ContainsKey("installer_url"));
        ContractSchema.Validate("$defs/UpdateStatus", update, "update status");
    }

    /// <summary>
    /// Once a check has read a version, the status names that version's installer for the user's browser,
    /// in the answer to CheckUpdateNow and in GetStatus alike, and both still validate. The Collector only
    /// reports the address: the one request it sent was for the metadata document.
    /// </summary>
    [Fact]
    public async Task AfterASuccessfulCheckTheStatusNamesTheInstallerOfThatVersion()
    {
        var requested = new List<Uri>();
        var client = new UpdateCheckClient(
            (uri, _) =>
            {
                lock (requested)
                {
                    requested.Add(uri);
                }

                var body = Encoding.UTF8.GetBytes("{\"version\": \"9.8.7\"}");
                return Task.FromResult(new UpdateTransportResponse(200, uri, null, body.Length, new MemoryStream(body)));
            },
            readEnvironment: _ => null);
        await using var fixture = ServerFixture.Start(updateCheckClient: client);
        Assert.Null(fixture.Host.Updates.Snapshot().InstallerUrl);

        var checkedNow = (await fixture.CallAsync("CheckUpdateNow")).Require();
        var status = (await fixture.CallAsync("GetStatus")).Require();

        ContractSchema.Validate("$defs/Responses/CheckUpdateNow", checkedNow, "check now with installer");
        ContractSchema.Validate("$defs/Responses/GetStatus", status, "status with installer");
        var expected = UpdateCheckClient.InstallerUrl("9.8.7");
        Assert.NotNull(expected);
        foreach (var update in new[] { checkedNow["update"]!.AsObject(), status["update"]!.AsObject() })
        {
            ContractSchema.Validate("$defs/UpdateStatus", update, "update status with installer");
            Assert.Equal("9.8.7", update["latest_version"]!.GetValue<string>());
            Assert.Equal(expected, update["installer_url"]!.GetValue<string>());
        }

        lock (requested)
        {
            Assert.NotEmpty(requested);
            Assert.All(requested, uri => Assert.Equal(UpdateCheckClient.MetadataUri(), uri));
        }
    }

    [Fact]
    public async Task AnswersFromTheCacheEvenWhenNoCheckCanSucceed()
    {
        await using var fixture = ServerFixture.Start();

        // The transport throws on every attempt; the status must still answer, every time.
        for (var i = 0; i < 3; i++)
        {
            var update = (await fixture.CallAsync("GetStatus")).Require()["update"]!.AsObject();
            Assert.False(update.ContainsKey("latest_version"));
            Assert.True(update["enabled"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task TheSwitchRoundTripsThroughTheSettings()
    {
        await using var fixture = ServerFixture.Start();

        var initial = (await fixture.CallAsync("GetCaptureSettings")).Require();
        Assert.True(initial["update_check_enabled"]!.GetValue<bool>());

        var off = (await fixture.CallAsync(
            "UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = false })).Require();
        ContractSchema.Validate("$defs/Responses/UpdateCaptureSettings", off, "update switch off");
        Assert.False(off["update_check_enabled"]!.GetValue<bool>());
        Assert.False(fixture.Host.Updates.Snapshot().Enabled);
        Assert.False((await fixture.CallAsync("GetStatus")).Require()["update"]!["enabled"]!.GetValue<bool>());

        // A settings write that names nothing else leaves the other switches alone.
        Assert.True(off["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.Equal(
            initial["follow_game"]!.GetValue<bool>(), off["follow_game"]!.GetValue<bool>());

        var on = (await fixture.CallAsync(
            "UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = true })).Require();
        Assert.True(on["update_check_enabled"]!.GetValue<bool>());
        Assert.True(fixture.Host.Updates.Snapshot().Enabled);
    }

    [Fact]
    public void TheSwitchSurvivesAReopen()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "test.db");
        try
        {
            using (var host = CollectorHost.Open(
                       databasePath,
                       capture: CaptureFakes.NoGame(),
                       speechClient: ServerFixture.RefusingSpeechClient,
                       updateCheckClient: ServerFixture.RefusingUpdateCheckClient))
            {
                CaptureSettingsStore.Apply(host.Settings, new CaptureSettingsUpdate { UpdateCheckEnabled = false });
            }

            using var reopened = CollectorHost.Open(
                databasePath,
                capture: CaptureFakes.NoGame(),
                speechClient: ServerFixture.RefusingSpeechClient,
                updateCheckClient: ServerFixture.RefusingUpdateCheckClient);

            Assert.False(CaptureSettingsStore.Read(reopened.Settings).UpdateCheckEnabled);
            Assert.False(reopened.Updates.Snapshot().Enabled);
            Assert.False(reopened.Updates.Observe().Enabled);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Windows can hold a WAL handle briefly; the temp cleaner will get it.
            }
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"true\"")]
    public async Task TheSwitchRequiresABoolean(string value)
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync(
            "UpdateCaptureSettings", new JsonObject { ["update_check_enabled"] = JsonNode.Parse(value) });

        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    [Fact]
    public async Task TheExportedReportStatesTheCheckWithoutSayingWhereItGoes()
    {
        await using var fixture = ServerFixture.Start();
        var workspace = Directory.CreateTempSubdirectory("MentorRecorder.UpdateReport").FullName;
        try
        {
            var target = Path.Combine(workspace, "report.json");
            Assert.True((await fixture.CallAsync(
                "ExportDiagnosticsReport", new JsonObject { ["target_path"] = target })).Ok);

            var report = JsonNode.Parse(await File.ReadAllTextAsync(target))!.AsObject();
            var update = report["boundary"]!["outbound"]!["update_check"]!;

            Assert.True(update["enabled"]!.GetValue<bool>());
            Assert.False(update["kill_switch"]!.GetValue<bool>());
            Assert.DoesNotContain(
                UpdateCheckClient.Repository, report.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
