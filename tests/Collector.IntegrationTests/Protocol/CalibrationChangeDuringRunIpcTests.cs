using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Audit 2026-10-20, K2-2 (Collector side): 停用并重新校准, 恢复上一份本机校准 and 改为本机校准 put the calibration in force
/// away, which closes a run in flight the way a stopped capture closes it. While a run is matched or in its duty the
/// Collector refuses them itself - ERR_CALIBRATION_NOT_READY with a sentence the player can read - and the run goes on to
/// its result. Once it is over the refusal no longer applies.
/// </summary>
public sealed class CalibrationChangeDuringRunIpcTests
{
    [Theory]
    [InlineData("retire")]
    [InlineData("restore")]
    [InlineData("reject")]
    public async Task PuttingTheCalibrationInForceAwayIsRefusedWhileARunIsUnderWay(string change)
    {
        const string gameBuild = "integration-change-during-run";
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(
            CaptureFakes.Ready(source, gameBuild),
            Selector(gameBuild, change == "reject" ? ProfileOrigin.Shared : ProfileOrigin.Local));
        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 1001);
        source.PushRaw(IpcMessage(61441, pop));
        await WaitForCurrentRunAsync(fixture, "MENTOR_MATCHED");
        AssertRefused(await ChangeAsync(fixture, change));

        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 2001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 3001);
        zone[12] = 1;
        source.PushRaw(IpcMessage(61442, zone));
        await WaitForCurrentRunAsync(fixture, "ENTERED_DUTY");
        AssertRefused(await ChangeAsync(fixture, change));

        var result = new byte[4];
        result[0] = 1;
        source.PushRaw(IpcMessage(61443, result));
        await WaitForStoredResultAsync(fixture, "COMPLETED");

        var after = await ChangeAsync(fixture, change);
        Assert.NotEqual(CollectorTexts.CalibrationChangeDuringRun().Chinese, after.ErrorMessage);
        await fixture.CallAsync("StopCapture");
    }

    /// <summary>
    /// Re-review 2026-10-20, RV2-3: on a queue-inferred profile a player who has queued is still IDLE, but the request is
    /// parked and becomes the run when the duty loads - putting the profile away now drops it, and that duty is never
    /// recorded. The Collector refuses inside its own gate, by the same test the shared-calibration swap uses, until the
    /// match window has gone by without a duty. The window is the hour every queue-inferred profile this machine writes
    /// declares (re-review RV5-2): still refused two minutes after the request, as a cancelled queue is not seen, and the
    /// sentence promises no shorter wait. Time is the capture's own clock, carried by the messages, so nothing sleeps.
    /// </summary>
    [Theory]
    [InlineData("retire")]
    [InlineData("restore")]
    [InlineData("reject")]
    public async Task PuttingAQueueInferredCalibrationAwayIsRefusedWhileTheQueueIsParked(string change)
    {
        const string gameBuild = "integration-change-while-queued";
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(
            CaptureFakes.Ready(source, gameBuild),
            Selector(gameBuild, change == "reject" ? ProfileOrigin.Shared : ProfileOrigin.Local, queueInferred: true));
        Assert.True((await fixture.CallAsync("StartCapture")).Ok);
        var pipeline = fixture.Host.LiveProtocol!;
        var session = source.LastOptions!.CaptureSessionId;

        var request = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 1001);
        var requestedAt = TimeSpan.FromSeconds(10);
        pipeline.Accept(Message(session, MessageDirection.Outbound, 61441, request, requestedAt));
        Assert.Equal(RunState.Idle, pipeline.RunState);
        // No run has started yet, so the sentence speaks of the queue rather than of a run in progress.
        AssertRefused(await ChangeAsync(fixture, change), CollectorTexts.CalibrationChangeWhileQueued());

        // Two minutes on - long after a player who cancelled would have expected it - the request is still parked.
        pipeline.Accept(Message(session, MessageDirection.Inbound, 0x7FFF, new byte[4], requestedAt + TimeSpan.FromMinutes(2)));
        var stillQueued = await ChangeAsync(fixture, change);
        AssertRefused(stillQueued, CollectorTexts.CalibrationChangeWhileQueued());
        Assert.DoesNotContain("两分钟", stillQueued.ErrorMessage, StringComparison.Ordinal);

        // The hour goes by without a duty: the request is spent.
        pipeline.Accept(Message(session, MessageDirection.Inbound, 0x7FFF, new byte[4],
            requestedAt + CalibrationDraft.QueueWindow + TimeSpan.FromSeconds(1)));
        var after = await ChangeAsync(fixture, change);
        Assert.NotEqual(CollectorTexts.CalibrationChangeWhileQueued().Chinese, after.ErrorMessage);
        Assert.NotEqual(CollectorTexts.CalibrationChangeDuringRun().Chinese, after.ErrorMessage);
        await fixture.CallAsync("StopCapture");
    }

    private static DecodedMessage Message(
        string session, MessageDirection direction, ushort opcode, byte[] payload, TimeSpan mono) =>
        new(session, direction, DateTimeOffset.UtcNow, mono, 0, FfxivFraming.SegmentTypeIpc, opcode, payload, "integration");

    private static Task<IpcResponse> ChangeAsync(CaptureServerFixture fixture, string change) => change switch
    {
        "retire" => fixture.CallAsync("DiscardCalibration", new() { ["retire_local_profile"] = true }),
        "restore" => fixture.CallAsync("DiscardCalibration", new() { ["restore_local_profile"] = true }),
        _ => fixture.CallAsync("RejectSharedCalibration"),
    };

    private static void AssertRefused(IpcResponse response, UserText? expected = null)
    {
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.CalibrationNotReady, response.ErrorCode);
        Assert.Equal((expected ?? CollectorTexts.CalibrationChangeDuringRun()).Chinese, response.ErrorMessage);
    }

    /// <summary>
    /// The checked-in synthetic profile, presented as a verified profile of the given origin for this build; queue-inferred
    /// when asked, as the provisional CN calibration is: the player's own request stands in for the match announcement, and
    /// the match window is the hour LocalProfileWriter and SharedProfileBuilder write for such a profile.
    /// </summary>
    private static Func<GameProcessDetection, ProfileSelection> Selector(
        string gameBuild, ProfileOrigin origin, bool queueInferred = false)
    {
        var profilePath = Path.Combine(AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");
        var synthetic = ProfileLoader.Validate(profilePath).Profile
            ?? throw new InvalidOperationException("the checked-in synthetic profile must load");
        var verified = synthetic with
        {
            ProfileId = "integration-change-during-run",
            Region = Region.Cn,
            GameBuild = gameBuild,
            Status = ProfileCompatibilityStatus.Verified,
            Messages = synthetic.Messages
                .Select(message => message with { SegmentType = FfxivFraming.SegmentTypeIpc })
                .ToArray(),
        };
        if (queueInferred)
        {
            var zone = verified.Messages.Single(message => message.Name == "ZONE_INITIALIZATION");
            verified = verified with
            {
                MatchWindow = CalibrationDraft.QueueWindow,
                Messages = verified.Messages
                    .Select(message => message.Name == "CONTENT_FINDER_POP"
                        ? message with { Direction = PacketDirection.ClientToServer }
                        : message)
                    .Append(zone with { Name = "ZONE_TERRITORY", Opcode = 61450 })
                    .ToArray(),
            };
            Assert.True(verified.ToBinding().MatchFromQueue);
        }

        return game => new ProfileSelection(
            ProfileCompatibilityStatus.Verified,
            verified.ToBinding(),
            verified,
            game.Region,
            game.GameBuild,
            "integration test profile selected",
            origin);
    }

    private static async Task WaitForCurrentRunAsync(CaptureServerFixture fixture, string expectedState)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = (await fixture.CallAsync("GetCurrentRun")).Require();
            if (current["state"]!.GetValue<string>() == expectedState)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the current run never reached " + expectedState);
    }

    private static async Task WaitForStoredResultAsync(CaptureServerFixture fixture, string expectedResult)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
            if (rows.Count > 0 && rows[0]!["result"]!.GetValue<string>() == expectedResult)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("no stored run ever reached " + expectedResult);
    }

    private static byte[] IpcMessage(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode, payload.Length);
        payload.CopyTo(message.AsSpan(FfxivFraming.HeaderBytes));
        return message;
    }
}
