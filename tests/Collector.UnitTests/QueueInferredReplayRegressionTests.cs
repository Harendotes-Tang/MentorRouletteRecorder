using System.Security.Cryptography;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

public sealed class QueueInferredReplayRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "MentorRecorder.QueueReplay", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueInferredReplayStartsOnlyForAKnownDuty(bool knownDuty)
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest);
        var written = LocalProfileWriter.Write(draft, template, CalibrationTrafficCases.Build,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"), _root);
        var profile = ProfileLoader.Load(written.Path);
        Assert.True(profile.ToBinding().MatchFromQueue);
        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Select(message =>
            {
                var payload = message.Payload.ToArray();
                if (message.Opcode == CalibrationTrafficCases.Request && payload[0] == 1)
                {
                    payload[0] = (byte)profile.MentorRouletteId.GetValueOrDefault();
                }
                if (!knownDuty && message.Opcode == CalibrationTrafficCases.Territory &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)) == 1039)
                {
                    // A normal overworld territory must still fail the queue inference guard.
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 5000);
                }
                return message with { Payload = payload, Epoch = (long)message.Mono.TotalMilliseconds };
            }).ToArray();
        var fixture = new
        {
            format_version = 1,
            fixture_id = "queue-inferred-review-regression",
            synthetic = true,
            profile = profile.ProfileId,
            game_build = CalibrationTrafficCases.Build,
            messages = traffic.Select(message => new
            {
                t_ms = (long)message.Mono.TotalMilliseconds,
                epoch = message.Epoch,
                segment_type = message.SegmentType,
                opcode = message.Opcode,
                direction = message.Direction == MessageDirection.Inbound ? "SERVER_TO_CLIENT" : "CLIENT_TO_SERVER",
                payload_hex = Convert.ToHexString(message.Payload.Span),
            }),
        };
        var path = Path.Combine(_root, "queue.decoded.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture);
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)));

        var replay = DecodedReplayRunner.Run(path, written.Path, Path.Combine(_root, "replay.db"));

        Assert.True(replay.ProfileUsable);
        Assert.True(replay.BuildMatched);
        Assert.True(replay.Parser.ParseOk > 0);
        Assert.Equal(0, replay.Parser.ParseFailed);
        Assert.Equal(knownDuty ? 1 : 0, replay.Runs.Count);
        if (knownDuty)
        {
            Assert.Contains(replay.Transitions, transition => transition.RunId is not null);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}