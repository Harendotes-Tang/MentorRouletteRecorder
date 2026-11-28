using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using static MentorRecorder.Collector.UnitTests.CaptureValidationTestKit;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The guided identity round: what a checkpoint says about who the client is, how the three
/// logins are judged, and that a validation session started for it carries the verdict.
/// </summary>
public sealed class CaptureIdentityCheckpointTests
{
    private const uint CharacterA = 0x1034_5678;
    private const uint CharacterB = 0x1098_7654;
    private const uint Stranger = 0x10AA_BB01;
    private const ushort LoginOpcode = 0x0222;
    private const ushort SharedOpcode = 0x0333;

    private static readonly byte[] Key = Enumerable.Range(1, CaptureIdentityProbe.KeyBytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void Markers_ListTheEventsThenTheIdentitySteps()
    {
        Assert.Equal(new[] { "queued", "pop", "entered", "victory", "left" }, CaptureTraceMarkerText.Events);
        Assert.Equal(
            new[] { "character_a", "character_b", "character_a_again", "after_teleport", "after_duty" },
            CaptureTraceMarkerText.Identity);
        Assert.Equal(CaptureTraceMarkerText.Events.Concat(CaptureTraceMarkerText.Identity), CaptureTraceMarkerText.Allowed);
        Assert.True(CaptureTraceMarkerText.IsIdentity("character_b"));
        Assert.False(CaptureTraceMarkerText.IsIdentity("pop"));
        Assert.Null(CaptureTraceMarkerText.Sanitize("character_c"));
    }

    [Fact]
    public void TraceAcceptsFixedStageMarkersOutsideTheValidationAllowlist()
    {
        using var writer = new StringWriter();
        var sink = new CaptureTraceSink(writer);
        foreach (var marker in new[] { "trash1", "boss1", "boss1_clear", "trash2", "boss3_clear" })
        {
            Assert.Null(CaptureTraceMarkerText.Sanitize(marker));
            Assert.True(sink.WriteMarker($" {marker.ToUpperInvariant()} "));
        }

        Assert.False(sink.WriteMarker("boss4"));
        Assert.False(sink.WriteMarker("trash2 extra text"));
        Assert.Equal(5, sink.MarkerCount);
        Assert.Contains("\"boss3_clear\"", writer.ToString());
    }

    [Fact]
    public void ACheckpoint_NamesTheActorMostOfTheWindowWasAddressedTo()
    {
        var probe = new CaptureIdentityProbe(Key);
        Traffic(probe, CharacterA, 40);
        Traffic(probe, Stranger, 5);

        var line = probe.Checkpoint("character_a", 1234)!;

        Assert.Equal(CaptureIdentityProbe.CheckpointKind, (string)line["identity"]!);
        Assert.Equal("character_a", (string)line["marker"]!);
        Assert.Equal(1234, (long)line["t_ms"]!);
        // The latest forty are judged: thirty-five of them the character's.
        Assert.Equal(CaptureIdentityProbe.FreshSamples, (int)line["samples"]!);
        Assert.True((bool)line["named"]!);
        var top = line["top"]!.AsArray();
        Assert.Equal(probe.ActorId(CharacterA), (string)top[0]!["id"]!);
        Assert.Equal(35, (int)top[0]!["count"]!);
        Assert.Equal(probe.ActorId(CharacterA), (string)probe.Summary()["checkpoints"]![0]!["id"]!);
    }

    [Fact]
    public void ACheckpoint_NamesNobodyOnTooLittleOrTooMixedTraffic()
    {
        var sparse = new CaptureIdentityProbe(Key);
        Traffic(sparse, CharacterA, CaptureIdentityProbe.MinWindowSamples - 1);
        sparse.Checkpoint("character_a", 1);

        var mixed = new CaptureIdentityProbe(Key);
        Traffic(mixed, CharacterA, 30);
        Traffic(mixed, Stranger, 30);
        mixed.Checkpoint("character_a", 1);

        Assert.Null(sparse.Summary()["checkpoints"]![0]!["id"]);
        Assert.Null(mixed.Summary()["checkpoints"]![0]!["id"]);
    }

    [Fact]
    public void ACheckpoint_LooksOnlyAtTheLatestTraffic()
    {
        var probe = new CaptureIdentityProbe(Key);
        Traffic(probe, CharacterA, 5000);
        Traffic(probe, CharacterB, CaptureIdentityProbe.WindowSize);

        probe.Checkpoint("character_b", 1);

        Assert.Equal(probe.ActorId(CharacterB), (string)probe.Summary()["checkpoints"]![0]!["id"]!);
    }

    [Fact]
    public void ACheckpoint_IgnoresWhatArrivedLongBeforeIt()
    {
        var probe = new CaptureIdentityProbe(Key);
        // Minutes at the title screen, where the header names something else...
        for (var index = 0; index < 200; index++)
        {
            probe.Observe(About(Stranger, SharedOpcode, 0x0102_0304_0506_0708), "lobby", index * 1000, index + 1);
        }

        // ...then the first seconds in the game.
        for (var index = 0; index < 30; index++)
        {
            probe.Observe(About(CharacterA, SharedOpcode, 0x0102_0304_0506_0708), "zone", 300_000 + index, 300 + index);
        }

        var line = probe.Checkpoint("character_a", 301_000)!;

        Assert.Equal(30, (int)line["samples"]!);
        Assert.Equal(probe.ActorId(CharacterA), (string)probe.Summary()["checkpoints"]![0]!["id"]!);
    }

    [Theory]
    [InlineData(CharacterA, CharacterB, CharacterA, CaptureIdentityProbe.ResultDistinguished)]
    [InlineData(CharacterA, CharacterA, CharacterA, CaptureIdentityProbe.ResultSameIdentity)]
    [InlineData(CharacterA, CharacterB, Stranger, CaptureIdentityProbe.ResultUnstable)]
    public void TheThreeLogins_AreJudged(uint first, uint second, uint again, string expected)
    {
        var probe = new CaptureIdentityProbe(Key);
        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, probe.Result);

        Step(probe, first, "character_a");
        Step(probe, second, "character_b");
        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, probe.Result);
        Step(probe, again, "character_a_again");

        Assert.Equal(expected, probe.Result);
        Assert.Equal(expected, (string)probe.Summary()["result"]!);
    }

    [Fact]
    public void TheTailOfTheCharacterJustLeft_IsNeverReadAsTheNewOne()
    {
        var probe = new CaptureIdentityProbe(Key);
        Traffic(probe, CharacterA, 30, "zone-a");
        probe.Checkpoint("character_a", 1);

        // Still the first character until the logout; the second has barely arrived.
        Traffic(probe, CharacterA, 200, "zone-a");
        Traffic(probe, CharacterB, 10, "zone-b");
        var early = probe.Checkpoint("character_b", 1)!;

        Assert.False((bool)early["named"]!);
        Assert.Equal(10, (int)early["samples"]!);
        Assert.Equal(new[] { ("character_a", true), ("character_b", false) }, probe.Steps);
        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, probe.Result);

        // Confirmed again a few seconds later, the same step reads the second character.
        Traffic(probe, CharacterB, 30, "zone-b");
        Assert.True((bool)probe.Checkpoint("character_b", 1)!["named"]!);
        Traffic(probe, CharacterA, 30, "zone-a2");
        probe.Checkpoint("character_a_again", 1);

        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, probe.Result);
        Assert.Equal(3, probe.Summary()["checkpoints"]!.AsArray().Count);
    }

    [Fact]
    public void TheCharacterSelectionScreen_IsNotACharacter()
    {
        var probe = new CaptureIdentityProbe(Key);
        Step(probe, CharacterA, "character_a");

        // Back at character selection: a new connection, another id, a short burst.
        Traffic(probe, Stranger, 12, "lobby-2");

        Assert.False((bool)probe.Checkpoint("character_b", 1)!["named"]!);
    }

    [Theory]
    [InlineData(0, 20_000)]
    [InlineData(8_000, 14_000)]
    public void AConnectionThatWentQuiet_NamesNobody(long trafficAtMs, long confirmedAtMs)
    {
        var probe = new CaptureIdentityProbe(Key);
        Traffic(probe, CharacterA, 30, "zone-a", trafficAtMs);

        Assert.False((bool)probe.Checkpoint("character_a", confirmedAtMs)!["named"]!);
    }

    [Fact]
    public void AnOptionalStepConfirmedBeforeTheReturn_IsNotJudged()
    {
        var probe = new CaptureIdentityProbe(Key);
        Step(probe, CharacterA, "character_a");
        Traffic(probe, CharacterB, 30, "zone-b");
        probe.Checkpoint("character_b", 1);
        // Typed while still the second character: on its connection, and none of the first's business.
        Traffic(probe, CharacterB, 30, "zone-b");
        probe.Checkpoint("after_teleport", 1);
        Step(probe, CharacterA, "character_a_again");

        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, probe.Result);
    }

    [Fact]
    public void AStepConfirmedAgain_KeepsTheLoginMessageOfItsFirstConfirmation()
    {
        var probe = new CaptureIdentityProbe(Key);
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a");
        probe.Observe(About(CharacterB, LoginOpcode, 0x2222_2222_2222_2222), "zone-b", 1, 1);
        probe.Checkpoint("character_b", 1);
        Traffic(probe, CharacterB, 30, "zone-b");
        probe.Checkpoint("character_b", 1);
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a_again");

        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, probe.Result);
        Assert.Single(probe.Summary()["login_candidates"]!.AsArray());
    }

    [Fact]
    public void AStepWithoutEnoughTraffic_LeavesTheRoundIncomplete()
    {
        var probe = new CaptureIdentityProbe(Key);
        Step(probe, CharacterA, "character_a");
        probe.Checkpoint("character_b", 2);
        Step(probe, CharacterA, "character_a_again");

        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, probe.Result);
    }

    [Fact]
    public void AnOptionalStepThatReadsDifferently_MakesTheRoundUnstable()
    {
        var probe = new CaptureIdentityProbe(Key);
        Step(probe, CharacterA, "character_a");
        Step(probe, CharacterB, "character_b");
        Step(probe, CharacterA, "character_a_again");
        Step(probe, CharacterA, "after_teleport");
        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, probe.Result);

        // Enough of it to be named: a handful among the character's own would name nobody.
        Traffic(probe, Stranger, CaptureIdentityProbe.FreshSamples, "zone-duty");
        probe.Checkpoint("after_duty", 1);

        Assert.Equal(CaptureIdentityProbe.ResultUnstable, probe.Result);
    }

    [Fact]
    public void LoginCandidates_AreTheOpcodesThatFollowTheCharacter()
    {
        var probe = new CaptureIdentityProbe(Key);
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a");
        Login(probe, CharacterB, 0x2222_2222_2222_2222, "character_b");
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a_again");

        var candidate = Assert.Single(probe.Summary()["login_candidates"]!.AsArray())!;

        Assert.Equal("0x0222", (string)candidate["op"]!);
        Assert.Equal(32, (int)candidate["len"]!);
    }

    [Fact]
    public void LoginCandidates_UseTheLastPrefixOfAnInterval()
    {
        var probe = new CaptureIdentityProbe(Key);
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a");
        // Still the first character for a moment after the step was confirmed.
        probe.Observe(About(CharacterA, LoginOpcode, 0x1111_1111_1111_1111), "conn", 1, 1);
        Login(probe, CharacterB, 0x2222_2222_2222_2222, "character_b");
        Login(probe, CharacterA, 0x1111_1111_1111_1111, "character_a_again");

        Assert.Single(probe.Summary()["login_candidates"]!.AsArray());
    }

    [Fact]
    public void ACheckpoint_LetsEachOpcodeEarnItsPrefixLinesAgain()
    {
        var probe = new CaptureIdentityProbe(Key);
        int Lines() => probe.Observe(About(CharacterA, LoginOpcode, 0x1111_1111_1111_1111), "conn", 1, 1)
            .Count(line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);

        var before = Enumerable.Range(0, 5).Sum(_ => Lines());
        probe.Checkpoint("character_a", 1);
        var after = Enumerable.Range(0, 5).Sum(_ => Lines());

        Assert.Equal(CaptureIdentityProbe.OccurrencesPerOpcode, before);
        Assert.Equal(CaptureIdentityProbe.OccurrencesPerOpcode, after);
    }

    [Fact]
    public void TheSink_WritesACheckpointForAnIdentityMarkerOnly_AndPublishesTheVerdict()
    {
        var writer = new StringWriter();
        var sink = new CaptureTraceSink(writer, identityProbe: new CaptureIdentityProbe(Key));
        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, sink.IdentityResult);

        foreach (var (actor, marker) in new[]
                 {
                     (CharacterA, "character_a"), (CharacterB, "character_b"), (CharacterA, "character_a_again"),
                 })
        {
            for (var index = 0; index < 30; index++)
            {
                sink.Accept(About(actor, SharedOpcode, 0x0102_0304_0506_0708, ConnectionOf(marker)));
            }

            Assert.True(sink.WriteMarker(marker));
        }

        Assert.True(sink.WriteMarker("pop"));
        sink.WriteSummary(dropped: 0);
        var text = writer.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();

        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, sink.IdentityResult);
        Assert.Equal(
            new[] { ("character_a", true), ("character_b", true), ("character_a_again", true) }, sink.IdentitySteps);
        Assert.Equal(3, lines.Count(line => line["identity"] is JsonValue kind &&
                                            kind.GetValue<string>() == CaptureIdentityProbe.CheckpointKind));
        Assert.Equal(4, lines.Count(line => line["marker"] is not null && line["identity"] is null));
        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, (string)lines[^1]["identity"]!["result"]!);
        Assert.DoesNotMatch("[0-9a-fA-F]{13,}", text);
        Assert.DoesNotContain(CharacterA.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(CharacterA.ToString("x8"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSink_WithoutAProbe_TreatsAnIdentityMarkerAsAPlainMarker()
    {
        var writer = new StringWriter();
        var sink = new CaptureTraceSink(writer);

        Assert.True(sink.WriteMarker("character_a"));

        Assert.Null(sink.IdentityResult);
        Assert.Null(sink.IdentitySteps);
        Assert.DoesNotContain("identity", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"marker\":\"character_a\"", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AValidationSession_StartedForIdentity_CarriesTheVerdictAndIsNoTemplateObservation()
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        using var controller = new CaptureValidationController(db.Path, new(), Services(source: source));
        Assert.False((bool)controller.Snapshot()["identity_probe"]!);
        Assert.Null(controller.Snapshot()["identity_result"]);
        Assert.Null(controller.Snapshot()["identity_steps"]);

        controller.Start("wifi", identityProbe: true);
        Until(controller, "state", "RECORDING");
        Assert.True((bool)controller.Snapshot()["identity_probe"]!);
        Assert.Equal(CaptureIdentityProbe.ResultIncomplete, (string)controller.Snapshot()["identity_result"]!);

        long messages = 0, markers = 0;
        ushort connection = 10;
        foreach (var (actor, marker) in new[]
                 {
                     (CharacterA, "character_a"), (CharacterB, "character_b"), (CharacterA, "character_a_again"),
                 })
        {
            connection++;
            for (var index = 0; index < 30; index++)
            {
                source.PushRaw(Raw(actor), connectionSuffix: connection);
            }

            messages += 30;
            Until(controller, "message_count", messages);
            controller.AddMarker(marker);
            Until(controller, "marker_count", ++markers);
        }

        Assert.True(SpinWait.SpinUntil(
            () => (string?)controller.Snapshot()["identity_result"] == CaptureIdentityProbe.ResultDistinguished, 5000));
        controller.Stop();
        Until(controller, "state", "COMPLETED");

        var final = controller.Snapshot();
        Assert.Equal(CaptureIdentityProbe.ResultDistinguished, (string)final["identity_result"]!);
        Assert.Equal(
            new[] { "character_a:True", "character_b:True", "character_a_again:True" },
            final["identity_steps"]!.AsArray().Select(step => (string)step!["marker"]! + ":" + (bool)step["named"]!));
        var text = File.ReadAllText((string)final["trace_path"]!);
        Assert.Contains("\"identity_probe\":true", text, StringComparison.Ordinal);
        Assert.Contains(CaptureIdentityProbe.CheckpointKind, text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(db.Path)!, Storage.DatabasePaths.IdentityProbeKeyFileName)));
        Assert.Equal("NO_OBSERVATION", (string)controller.GetTemplateObservation()["reason"]!);
    }

    [Theory]
    [InlineData(false, "capture.validation.failed_identity_key_invalid")]
    [InlineData(true, "capture.validation.failed_identity_key_unreadable")]
    public void AValidationSession_WhoseKeyCannotBeUsed_FailsSayingWhy(bool unreadable, string expectedKey)
    {
        using var db = new TestDatabase();
        var keyPath = Path.Combine(Path.GetDirectoryName(db.Path)!, Storage.DatabasePaths.IdentityProbeKeyFileName);
        if (unreadable)
        {
            // Something that is there and cannot be read as a file at all.
            Directory.CreateDirectory(keyPath);
        }
        else
        {
            File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });
        }

        using var controller = new CaptureValidationController(db.Path, new(), Services());
        controller.Start("wifi", identityProbe: true);
        Until(controller, "state", "FAILED");

        var failed = controller.Snapshot();
        Assert.Equal(expectedKey, (string)failed["message_ref"]!["key"]!);
        Assert.Equal(ErrorCodes.Internal, (string)failed["error_code"]!);
        // Nothing was recorded and nothing was replaced.
        Assert.Null(failed["trace_path"]);
        Assert.Null(failed["sha256"]);
        Assert.True(unreadable ? Directory.Exists(keyPath) : new FileInfo(keyPath).Length == 3);

        // The capture is free again: an ordinary round starts and records.
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        controller.Stop();
        Until(controller, "state", "COMPLETED");
    }

    [Fact]
    public void AValidationSession_StartedWithoutIt_SaysSo()
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(source: new FakeCaptureSource()));

        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        // A step of the identity round would make this trace useless as a template observation.
        var refused = Assert.Throws<CollectorException>(() => controller.AddMarker("character_a"));
        Assert.Equal(ErrorCodes.BadRequest, refused.Code);
        controller.AddMarker("pop");
        controller.Stop();
        Until(controller, "state", "COMPLETED");

        var final = controller.Snapshot();
        Assert.False((bool)final["identity_probe"]!);
        Assert.Null(final["identity_result"]);
        Assert.Null(final["identity_steps"]);
        Assert.Equal(1, (long)final["marker_count"]!);
        Assert.DoesNotContain("identity", File.ReadAllText((string)final["trace_path"]!), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(db.Path)!, Storage.DatabasePaths.IdentityProbeKeyFileName)));
    }

    private static void Traffic(
        CaptureIdentityProbe probe, uint target, int count, string connection = "conn", long atMs = 0)
    {
        for (var index = 0; index < count; index++)
        {
            probe.Observe(About(target, SharedOpcode, 0x0102_0304_0506_0708), connection, atMs + index, index + 1);
        }
    }

    private static int _connections;

    /// <summary>One login as the game does it: a connection of its own, then the confirmation.</summary>
    private static void Step(CaptureIdentityProbe probe, uint actor, string marker)
    {
        Traffic(probe, actor, 30, "zone-" + Interlocked.Increment(ref _connections));
        Assert.NotNull(probe.Checkpoint(marker, 1));
    }

    private static void Login(CaptureIdentityProbe probe, uint actor, ulong characterIdentifier, string marker)
    {
        probe.Observe(About(actor, LoginOpcode, characterIdentifier), "conn", 1, 1);
        Step(probe, actor, marker);
    }

    /// <summary>A server-to-client message about <paramref name="actor"/>, led by eight chosen bytes.</summary>
    private static DecodedMessage About(
        uint actor, ushort opcode, ulong prefix, string connectionKey = "connection-not-written") =>
        CaptureIdentityProbeTests.Decoded(
            MessageDirection.Inbound, opcode, actor, actor, CaptureIdentityProbeTests.Payload(prefix, 32)) with
        {
            ConnectionKey = connectionKey,
        };

    /// <summary>A connection key whose eight-character tag differs per step.</summary>
    private static string ConnectionOf(string marker) =>
        (Array.IndexOf(CaptureTraceMarkerText.Identity.ToArray(), marker) + 1).ToString("d8") + "-" + marker;

    private static byte[] Raw(uint actor)
    {
        var payload = new byte[16];
        payload[0] = 1;
        return CaptureIdentityProbeTests.Message(SharedOpcode, payload, actor, actor);
    }
}
