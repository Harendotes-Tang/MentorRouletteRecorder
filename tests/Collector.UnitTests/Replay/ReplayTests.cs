using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Offline replay: the semantic-event fixture through the state machine, and decoded traffic (the
/// shipped decoded fixtures, and an evening written for a queue-inferred local profile) through the
/// parser. The integrity checks that keep the fixtures synthetic are <see cref="FixtureIntegrityTests"/>.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ReplayTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "synthetic-completed-v1.fixture.json");

    // ---------------------------------------------------- semantic-event fixtures ----

    [Fact]
    public void Fixture_HasFixedHashAndContainsNoPersonalData()
    {
        var fixture = ReplayFixtureLoader.Load(FixturePath);

        Assert.Equal("125aa826ef13a5cbb473d7a6602f836e096c5002547e3e5a5e6775de0735ed81", fixture.Sha256);
        Assert.Equal(4, fixture.Events.Count);
        Assert.All(fixture.Events, ev => Assert.Equal("10000000-0000-4000-8000-000000000001", ev.Key.CaptureSessionId));
    }

    [Fact]
    public void Replay_ProducesTransitionsFinalRunDatabaseWritesAndStatistics()
    {
        using var temp = new TempDirectory("ReplayTests");
        var databasePath = temp.Combine("replay.db");

        var result = FixtureReplayRunner.Run(FixturePath, databasePath);

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Equal(new[]
        {
            RunState.MentorMatched,
            RunState.EnteredDuty,
            RunState.EnteredDuty,
            RunState.Completed,
        }, result.Transitions.Select(row => row.ToState));
        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
        Assert.Equal(1, result.Statistics.AttemptCount);
        Assert.Equal(1, result.Statistics.CompletedCount);
        Assert.Equal(1, result.Writes.RunsCreated);
        Assert.Equal(4, result.Writes.EventsAppended);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void Replay_IsIdempotentAcrossProcessEquivalentRuns()
    {
        using var temp = new TempDirectory("ReplayTests");
        var databasePath = temp.Combine("replay.db");
        var first = FixtureReplayRunner.Run(FixturePath, databasePath);

        var second = FixtureReplayRunner.Run(FixturePath, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.True(second.Writes.IdempotentReplay);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(1, second.Statistics.AttemptCount);
    }

    [Fact]
    public void Program_ReplayModeWritesJsonResult()
    {
        using var temp = new TempDirectory("ReplayTests");

        var (exitCode, output) = RunProgram("--replay", FixturePath, "--database", temp.Combine("replay.db"));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"final_state\": \"COMPLETED\"", output, StringComparison.Ordinal);
        Assert.Contains("\"statistics\"", output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- decoded fixtures ----

    private static string DecodedDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "decoded");

    [Fact]
    public void DecodedReplayOfTheCompleteFixtureProducesACompletedRun()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var result = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, temp.Combine("replay.db"));

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.True(result.BuildMatched);
        Assert.True(result.ProfileUsable);
        Assert.Equal(4, result.Parser.ParseOk);
        Assert.Equal(0, result.Parser.ParseFailed);
        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
    }

    [Fact]
    public void DecodedReplayIsIdempotent()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var databasePath = temp.Combine("replay.db");
        var first = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, databasePath);

        var second = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), SyntheticProfile.Path, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.True(second.Writes.IdempotentReplay);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(1, second.Statistics.AttemptCount);
    }

    [Fact]
    public void DecodedReplayResolvesTheProfileNamedByTheFixtureWhenNoneIsGiven()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");
        var result = DecodedReplayRunner.Run(
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"), null, temp.Combine("replay.db"));

        Assert.Equal("synthetic-v1", result.ProfileId);
        Assert.Equal(RunState.Completed, result.FinalState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueInferredReplayStartsOnlyForAKnownDuty(bool knownDuty)
    {
        using var root = TempDirectory.Reserve("QueueReplay", strict: true);
        try
        {
            var template = CalibrationObserverTests.Template();
            var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest);
            var written = LocalProfileWriter.Write(draft, template, CalibrationTrafficCases.Build,
                DateTimeOffset.Parse("2026-09-01T00:00:00Z"), root.Path);
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
                        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)) == 1039)
                    {
                        // A normal overworld territory must still fail the queue inference guard.
                        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 5000);
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
            var path = root.Combine("queue.decoded.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture);
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)));

            var replay = DecodedReplayRunner.Run(path, written.Path, root.Combine("replay.db"));

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
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Program_ReplayDecodedModeWritesJsonResult()
    {
        using var temp = TempDirectory.Reserve("DecodedReplay");

        var (exitCode, output) = RunProgram(
            "--replay-decoded", Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json"),
            "--profile", SyntheticProfile.Path,
            "--db", temp.Combine("replay.db"));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"final_state\": \"COMPLETED\"", output, StringComparison.Ordinal);
        Assert.Contains("\"parse_ok\": 4", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ValidateProfileReportsSuccessAndFailure()
    {
        Assert.Equal(0, RunProgram("--validate-profile", SyntheticProfile.Path).Code);
        Assert.Equal(1, RunProgram(
            "--validate-profile",
            Path.Combine(DecodedDirectory, "synthetic_complete.decoded.json")).Code);
    }

    [Fact]
    public void Program_ListProfilesPrintsEveryInstalledProfile()
    {
        var (exitCode, text) = RunProgram("--list-profiles");

        Assert.Equal(0, exitCode);
        Assert.Contains("synthetic-v1", text, StringComparison.Ordinal);
        Assert.Contains("cn-unsupported", text, StringComparison.Ordinal);
        Assert.Contains("global-unsupported", text, StringComparison.Ordinal);
    }

    /// <summary>Runs the Collector's entry point with its standard output captured and its errors discarded.</summary>
    private static (int Code, string Output) RunProgram(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var discarded = new StringWriter();
        Console.SetOut(output);
        Console.SetError(discarded);
        try
        {
            var exitCode = Program.Main(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
