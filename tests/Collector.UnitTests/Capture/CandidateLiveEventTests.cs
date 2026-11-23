using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateLiveEventTests
{
    [Fact]
    public async Task CandidateEventCarriesOnlyRefreshMetadataAndNoFormalEvent()
    {
        var bus = new LiveEventBus(new TestClock(DateTimeOffset.UnixEpoch));
        using var subscription = bus.Subscribe("candidate-test");
        var observation = Observation();

        bus.PublishCandidateObserved(observation);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = Assert.IsType<JsonObject>(await subscription.ReadAsync(deadline.Token));
        Assert.Equal("CandidateObserved", received["event_type"]!.GetValue<string>());
        Assert.Equal("candidate_observed", received["kind"]!.GetValue<string>());
        Assert.Equal(observation.HypothesisName, received["name"]!.GetValue<string>());
        Assert.Equal(observation.Group, received["group"]!.GetValue<string>());
        Assert.Equal(observation.TMs, received["t_ms"]!.GetValue<long>());
        Assert.Equal(observation.ObservationId, received["observation_id"]!.GetValue<string>());
        Assert.Equal(observation.CaptureSessionId, received["capture_session_id"]!.GetValue<string>());
        Assert.Equal(
            new[] { "capture_session_id", "emitted_at_utc", "event_id", "event_type", "group",
                "kind", "name", "observation_id", "sequence", "t_ms" },
            received.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1, bus.ReplayCount);
        Assert.Null(await subscription.ReadAsync(TimeSpan.FromMilliseconds(20), deadline.Token));
    }

    [Fact]
    public void CaptureStatusDefaultsToCandidateObservationDisabled()
    {
        using var capture = new CaptureController(InertCapture.Services());

        var status = CaptureWire.CaptureStatus(capture.Snapshot());

        Assert.False(status["candidate_validation_enabled"]!.GetValue<bool>());
        Assert.Null(status["candidate_profile_id"]);
        Assert.Equal(0, status["candidate_observation_count"]!.GetValue<int>());
        Assert.Equal("NONE", status["profile_status"]!.GetValue<string>());
    }

    [Fact]
    public void CaptureStatusListsCandidateHypothesesForWhitelistLabels()
    {
        var profile = CandidateObserverTests.Profile();
        using var capture = new CaptureController(InertCapture.Services() with
        {
            CandidateHypotheses = () => profile.Hypotheses
                .Select(h => CandidateHypothesisView.From(profile, h)).ToArray(),
        });

        var status = CaptureWire.CaptureStatus(capture.Snapshot());

        var hypotheses = status["candidate_hypotheses"]!.AsArray();
        Assert.Equal(7, hypotheses.Count);
        var first = hypotheses[0]!.AsObject();
        Assert.Equal("candidate-test", first["profile_id"]!.GetValue<string>());
        Assert.Equal("ZONE_MEMBER_1", first["name"]!.GetValue<string>());
        // No label in the profile: the name stands in, never an empty string.
        Assert.Equal("ZONE_MEMBER_1", first["label"]!.GetValue<string>());
        Assert.Equal("0xf101", first["opcode"]!.GetValue<string>());
        Assert.Equal("S2C", first["direction"]!.GetValue<string>());
        Assert.Equal("zone_load", first["group"]!.GetValue<string>());
        Assert.Equal(8, first["expected_length"]!.GetValue<int>());
        Assert.True(first["research_eligible"]!.GetValue<bool>());
        // Display data only: switching validation on is a separate reading.
        Assert.False(status["candidate_validation_enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void CaptureStatusCandidateHypothesesDefaultToEmpty()
    {
        using var capture = new CaptureController(InertCapture.Services());

        var status = CaptureWire.CaptureStatus(capture.Snapshot());

        Assert.Empty(status["candidate_hypotheses"]!.AsArray());
    }

    [Fact]
    public void CandidateStatusUsesOneIndependentReadingWithoutChangingFormalProfile()
    {
        var calls = 0;
        using var capture = new CaptureController(InertCapture.Services() with
        {
            CandidateStatus = () =>
            {
                calls++;
                return (true, "candidate-fixture", 42);
            },
        });

        var snapshot = capture.Snapshot();
        var status = CaptureWire.CaptureStatus(snapshot);

        Assert.Equal(1, calls);
        Assert.True(status["candidate_validation_enabled"]!.GetValue<bool>());
        Assert.Equal("candidate-fixture", status["candidate_profile_id"]!.GetValue<string>());
        Assert.Equal(42, status["candidate_observation_count"]!.GetValue<int>());
        Assert.Equal("NONE", status["profile_status"]!.GetValue<string>());
        var report = SanitizedDiagnosticsReport.Build(snapshot, "test", DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("candidate-fixture", report.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowRoundGuessReadDoesNotHoldTheCaptureGate()
    {
        using var resumeGuesses = new ManualResetEventSlim();
        var startedGuesses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var report = new CandidateRoundReport(DateTimeOffset.UnixEpoch, null, null, null, null);
        using var capture = new CaptureController(InertCapture.Services() with
        {
            CandidateStatus = () => (true, "candidate-fixture", 0),
            CandidateRoundReport = () =>
            {
                startedGuesses.SetResult();
                if (!resumeGuesses.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The test did not release the candidate guesses.");
                return report;
            },
        });
        var snapshot = Task.Run(capture.Snapshot);
        try
        {
            await startedGuesses.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // State requires the same gate as the decoder's health update.
            await Task.Run(() => capture.State).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            resumeGuesses.Set();
            Assert.Equal(report, (await snapshot.WaitAsync(TimeSpan.FromSeconds(5))).CandidateRoundGuesses);
        }
    }

    [Fact]
    public async Task FilteredCandidateTrafficDoesNotStarveSuccessiveHeartbeats()
    {
        using var database = new TestDatabase();
        using var host = CollectorHost.Open(
            Path.Combine(Path.GetDirectoryName(database.Path)!, "heartbeat.db"),
            database.Clock,
            capture: InertCapture.Services());
        using var stream = new ScriptedPipePeer(new IpcRequest(Guid.NewGuid().ToString("D"), "SubscribeLiveEvents", new JsonObject
        {
            ["event_types"] = new JsonArray("RunUpdated"),
            ["heartbeat_interval_ms"] = 1000,
        }));
        using var lifetime = new CancellationTokenSource();
        var serving = new PipeConnection(stream, new MessageDispatcher(host)).ServeAsync(lifetime.Token);
        Task publishing = Task.CompletedTask;

        try
        {
            using var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acknowledgement = await stream.NextWrittenAsync(firstDeadline.Token);
            Assert.Equal("SubscribeLiveEvents", acknowledgement["message_type"]!.GetValue<string>());
            Assert.True(acknowledgement["ok"]!.GetValue<bool>());

            publishing = Task.Run(async () =>
            {
                while (!lifetime.IsCancellationRequested)
                {
                    host.LiveEvents.PublishCandidateObserved(Observation());
                    await Task.Delay(15, lifetime.Token);
                }
            });

            for (var i = 0; i < 2; i++)
            {
                using var heartbeatDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var heartbeat = await stream.NextWrittenAsync(heartbeatDeadline.Token);
                Assert.Equal(IpcEnvelope.EventMessageType, heartbeat["message_type"]!.GetValue<string>());
                Assert.Equal("heartbeat", heartbeat["payload"]!["kind"]!.GetValue<string>());
                Assert.Equal("Heartbeat", heartbeat["payload"]!["event_type"]!.GetValue<string>());
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(serving, publishing);
            }
            catch (OperationCanceledException)
            {
                // The in-memory peer blocks its next read until this explicit shutdown.
            }
        }

        Assert.Equal(0, host.LiveEvents.SubscriberCount);
    }

    private static CandidateObservation Observation() => new(
        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "candidate-fixture",
        "FixtureCandidate", "FixtureGroup", "S2C", 123, 456, "abcdef123456",
        "connection-tag", DateTimeOffset.UnixEpoch, 250);
}
