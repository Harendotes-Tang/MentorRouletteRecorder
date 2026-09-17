using System.Buffers.Binary;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Parser contract, both halves: it reads exactly what the profile declares, and it refuses
/// everything else by counting rather than by throwing.
/// </summary>
public sealed class ProtocolParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Parser", Guid.NewGuid().ToString("N"));

    private readonly ProtocolProfile _profile;

    public ProtocolParserTests()
    {
        Directory.CreateDirectory(_directory);
        _profile = ProfileLoader.Load(
            ProfileTestFiles.Write(_directory, "parser-profile", ProfileTestFiles.Valid()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp debris only.
        }
    }

    [Fact]
    public void EveryFieldTypeAndEndiannessIsReadExactlyAsDeclared()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 42);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 900_001);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), 1234);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), -5);

        parser.Accept(Message(100, payload));

        var pop = Assert.IsType<ContentFinderPop>(Assert.Single(sink.Events));
        Assert.Equal(42, pop.RouletteId);
        Assert.Equal(900_001, pop.ContentId);
        Assert.Equal(1, parser.GetParserStats().ParseOk);
        Assert.Contains("token=1234", pop.Key.SemanticKey, StringComparison.Ordinal);
        Assert.Contains("marker=-5", pop.Key.SemanticKey, StringComparison.Ordinal);
    }

    [Fact]
    public void EndiannessIsNotGuessed()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 0x0102);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), 0x0304);
        payload[4] = 1;

        parser.Accept(Message(101, payload));

        var zone = Assert.IsType<ZoneInitialization>(Assert.Single(sink.Events));
        Assert.Equal(0x0102, zone.TerritoryId);
        Assert.Equal(0x0304, zone.ContentId);
        Assert.True(zone.IsDutyInstance);
    }

    [Fact]
    public void OnlyADeclaredVictoryValueCompletesADutyResult()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        parser.Accept(Message(102, new byte[] { 7, 0 }));
        parser.Accept(Message(102, new byte[] { 3, 0 }));

        Assert.Collection(
            sink.Events.Cast<DutyResult>(),
            first => Assert.True(first.Victory),
            second => Assert.False(second.Victory));
    }

    [Fact]
    public void TheKindOfTheLastValidEventIsKeptWithItsTime()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        Assert.Null(parser.GetParserStats().LastValidEventKind);

        parser.Accept(Message(102, new byte[] { 7, 0 }));
        var afterResult = parser.GetParserStats();
        Assert.Equal("DUTY_RESULT", afterResult.LastValidEventKind);
        Assert.NotNull(afterResult.LastValidEventAtUtc);
        Assert.Equal(sink.Events.Single().EventType, afterResult.LastValidEventKind);
        Assert.Contains(afterResult.LastValidEventKind, ProfileMessageParser.EventKinds);

        // Ignored traffic changes neither half of the pair.
        parser.Accept(Message(999, new byte[] { 1, 2, 3, 4 }));
        var afterIgnored = parser.GetParserStats();
        Assert.Equal("DUTY_RESULT", afterIgnored.LastValidEventKind);
        Assert.Equal(afterResult.LastValidEventAtUtc, afterIgnored.LastValidEventAtUtc);
    }

    [Fact]
    public void UnknownOpcodeIsIgnoredNotRefusedAndNeverProducesAnEvent()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        parser.Accept(Message(999, new byte[] { 1, 2, 3, 4 }));
        parser.Accept(Message(998, new byte[] { 1, 2, 3, 4 }));

        // Ordinary traffic the profile does not declare: counted as ignored, never as a
        // failure, never in the error ring (contracts/CHANGELOG.md entry 18).
        Assert.Empty(sink.Events);
        var stats = parser.GetParserStats();
        Assert.Equal(2, stats.Ignored);
        Assert.Equal(0, stats.ParseFailed);
        Assert.Equal(0, stats.ParseOk);
        Assert.Empty(stats.RecentErrors);
        Assert.Null(stats.LastValidEventAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ControlSegmentsWithoutAnOpcodeDoNotCountAsParserFailures(int segmentType)
    {
        var sink = new RecordingSink();
        foreach (var profile in new ProtocolProfile?[] { _profile, null })
        {
            var parser = new ProfileMessageParser(profile, sink);
            parser.Accept(Message(0, new byte[8], segmentType));

            var stats = parser.GetParserStats();
            Assert.Equal(0, stats.ParseOk);
            Assert.Equal(0, stats.ParseFailed);
            Assert.Equal(0, stats.Duplicates);
            Assert.Equal(0, stats.Ignored);
            Assert.Null(stats.LastValidEventAtUtc);
            Assert.Empty(stats.RecentErrors);
        }
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ZeroOpcodeInAnIpcSegmentIsStillAnUnknownOpcode()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        parser.Accept(Message(0, new byte[8], segmentType: 3));

        // Unlike a control segment, an IPC segment with opcode 0 is a real message the
        // profile does not claim: it is classified (ignored), not silently skipped.
        Assert.Empty(sink.Events);
        Assert.Equal(1, parser.GetParserStats().Ignored);
        Assert.Equal(0, parser.GetParserStats().ParseFailed);
        Assert.Empty(parser.GetParserStats().RecentErrors);
    }

    [Fact]
    public void RecordedErrorsNeverCarryPayloadBytes()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        // A declared opcode with a payload length the profile refuses: the refusal is
        // recorded, and the recognisable bytes must not leak into its message.
        parser.Accept(Message(101, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0 }));

        var error = Assert.Single(parser.GetParserStats().RecentErrors);
        Assert.DoesNotContain("dead", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("beef", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LengthMismatchIsRefused()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        parser.Accept(Message(101, new byte[7]));

        Assert.Empty(sink.Events);
        Assert.Equal(
            ParserErrorCode.LengthMismatch,
            Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    [Fact]
    public void FieldReadingPastAVariableLengthPayloadIsRefused()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        var zone = (Dictionary<string, object>)messages[1];
        zone.Remove("expected_length");
        zone["min_length"] = 1;
        zone["max_length"] = 8;
        var profile = ProfileLoader.Load(ProfileTestFiles.Write(_directory, "parser-oob", document));
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(profile, sink);

        parser.Accept(Message(101, new byte[1]));

        Assert.Empty(sink.Events);
        Assert.Equal(
            ParserErrorCode.OffsetOutOfBounds,
            Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    [Fact]
    public void FieldOutsideItsDeclaredConstraintIsRefused()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 0);

        parser.Accept(Message(100, payload));

        Assert.Empty(sink.Events);
        Assert.Equal(
            ParserErrorCode.FieldConstraint,
            Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    [Fact]
    public void SegmentTypeMismatchDoesNotMatchTheMessage()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        ((Dictionary<string, object>)messages[2])["segment_type"] = 3;
        var profile = ProfileLoader.Load(ProfileTestFiles.Write(_directory, "parser-seg", document));
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(profile, sink);

        parser.Accept(Message(102, new byte[] { 7, 0 }, segmentType: 4));

        Assert.Empty(sink.Events);
        Assert.Equal(1, parser.GetParserStats().Ignored);
        Assert.Equal(0, parser.GetParserStats().ParseFailed);
        Assert.Empty(parser.GetParserStats().RecentErrors);
    }

    [Fact]
    public void DirectionMismatchDoesNotMatchTheMessage()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        parser.Accept(Message(102, new byte[] { 7, 0 }, direction: MessageDirection.Outbound));

        Assert.Empty(sink.Events);
        Assert.Equal(1, parser.GetParserStats().Ignored);
        Assert.Equal(0, parser.GetParserStats().ParseFailed);
        Assert.Empty(parser.GetParserStats().RecentErrors);
    }

    [Fact]
    public void WithoutAUsableProfileEveryMessageIsRefused()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(null, sink);

        parser.Accept(Message(100, new byte[24]));

        Assert.Empty(sink.Events);
        Assert.False(parser.IsUsable);
        Assert.Equal(
            ParserErrorCode.ProfileUnsupported,
            Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    [Fact]
    public void RepeatedIdenticalObservationsAreCountedAsDuplicatesAndStillForwarded()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        var message = Message(102, new byte[] { 7, 0 });

        parser.Accept(message);
        parser.Accept(message);

        Assert.Equal(2, sink.Events.Count);
        var stats = parser.GetParserStats();
        Assert.Equal(2, stats.ParseOk);
        Assert.Equal(1, stats.Duplicates);
    }

    [Fact]
    public void TheErrorRingIsBounded()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        for (var i = 0; i < ProfileMessageParser.ErrorRingCapacity + 50; i++)
        {
            // A declared opcode with the wrong length: a genuine refusal, unlike an
            // undeclared opcode, which is merely ignored.
            parser.Accept(Message(101, new byte[7]));
        }

        var stats = parser.GetParserStats();
        Assert.Equal(ProfileMessageParser.ErrorRingCapacity + 50, stats.ParseFailed);
        Assert.Equal(ProfileMessageParser.ErrorRingCapacity, stats.RecentErrors.Count);
    }

    [Fact]
    public void TenThousandRandomBuffersNeverThrowAndNeverInventAMentorPop()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);
        var random = new Random(20260904);
        var opcodeMessages = 0;

        for (var i = 0; i < 10_000; i++)
        {
            var payload = new byte[random.Next(0, 64)];
            random.NextBytes(payload);
            var message = new DecodedMessage(
                "20000000-0000-4000-8000-00000000fuzz".Replace("fuzz", "0099", StringComparison.Ordinal),
                random.Next(2) == 0 ? MessageDirection.Inbound : MessageDirection.Outbound,
                new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero).AddMilliseconds(i),
                TimeSpan.FromMilliseconds(i),
                random.Next(0, 1000),
                (ushort)random.Next(0, 8),
                (ushort)random.Next(0, 200),
                payload,
                "fuzz");

            if (message.SegmentType == 3 || message.Opcode != 0)
                opcodeMessages++;
            parser.Accept(message);
        }

        var stats = parser.GetParserStats();
        Assert.Equal(opcodeMessages, stats.ParseOk + stats.ParseFailed + stats.Ignored);

        // Emitted events are structurally valid: nothing is half-parsed.
        foreach (var pop in sink.Events.OfType<ContentFinderPop>())
        {
            Assert.InRange(pop.RouletteId, 1, ushort.MaxValue);
        }
    }

    [Fact]
    public void ANullMessageIsCountedRatherThanThrown()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(_profile, sink);

        parser.Accept(null!);

        Assert.Equal(ParserErrorCode.Internal, Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    /// <summary>
    /// The two messages that name the duty and the job, in the shape the CN profile declares
    /// them: a 136-byte territory announcement whose territory id is a little-endian u16 at
    /// offset 2, and a 16-byte class update whose job id is the first byte. The opcodes are
    /// invented; what is under test is that the parser reads the declared offset out of a
    /// payload of the declared length, and refuses one that violates the constraints.
    /// </summary>
    [Fact]
    public void TerritoryAndJobAreReadFromTheOffsetsTheProfileDeclares()
    {
        var profile = ProfileLoader.Load(
            ProfileTestFiles.Write(_directory, "parser-identity", WithIdentityMessages()));
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(profile, sink);

        var territoryPayload = new byte[136];
        BinaryPrimitives.WriteUInt16LittleEndian(territoryPayload.AsSpan(0), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(territoryPayload.AsSpan(2), 1036);
        var jobPayload = new byte[16];
        jobPayload[0] = 19;
        jobPayload[2] = 100;

        parser.Accept(Message(103, territoryPayload));
        parser.Accept(Message(104, jobPayload));

        Assert.Collection(
            sink.Events,
            first =>
            {
                var territory = Assert.IsType<TerritoryObserved>(first);
                Assert.Equal(1036, territory.TerritoryId);
                Assert.Equal("ZONE_TERRITORY", territory.EventType);
            },
            second => Assert.Equal(19, Assert.IsType<PlayerJob>(second).JobId));
        Assert.Equal(2, parser.GetParserStats().ParseOk);
    }

    [Fact]
    public void AJobIdOutsideTheDeclaredRangeIsRefusedRatherThanStored()
    {
        var profile = ProfileLoader.Load(
            ProfileTestFiles.Write(_directory, "parser-identity-bad", WithIdentityMessages()));
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(profile, sink);

        // 44 is one past the last job in data/jobs/jobs.json: reading it would mean the
        // offset is wrong, so the whole message is dropped instead of naming a job.
        var payload = new byte[16];
        payload[0] = 44;

        parser.Accept(Message(104, payload));

        Assert.Empty(sink.Events);
        Assert.Equal(
            ParserErrorCode.FieldConstraint,
            Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    /// <summary>
    /// Review finding M-2. The CN duty finder sends one opcode for the application receipt,
    /// the pop and every later state update, and only one <c>finder_state</c> value is the
    /// pop. A profile that declares that field as a selector must treat the other values as
    /// "not this message": counted as ignored, with no error ring entry and no parser_errors
    /// row, so a healthy mentor roulette does not report failures.
    /// </summary>
    [Fact]
    public void ASelectorFieldOutsideItsConstraintsIsIgnoredNotRefused()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(SelectorProfile(), sink);
        var payload = new byte[24];

        // finder_state = 1: the application receipt, not the pop.
        payload[22] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 42);
        parser.Accept(Message(100, payload));

        Assert.Empty(sink.Events);
        var stats = parser.GetParserStats();
        Assert.Equal(1, stats.Ignored);
        Assert.Equal(0, stats.ParseFailed);
        Assert.Empty(stats.RecentErrors);
    }

    /// <summary>The same message with the declared selector value still parses normally.</summary>
    [Fact]
    public void ASelectorFieldOnItsDeclaredValueStillParses()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(SelectorProfile(), sink);
        var payload = new byte[24];
        payload[22] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 42);

        parser.Accept(Message(100, payload));

        Assert.Equal(42, Assert.IsType<ContentFinderPop>(Assert.Single(sink.Events)).RouletteId);
        Assert.Equal(1, parser.GetParserStats().ParseOk);
    }

    /// <summary>A value field outside its constraints stays a refusal the diagnostics show.</summary>
    [Fact]
    public void AValueFieldOutsideItsConstraintsIsStillAFieldConstraintRefusal()
    {
        var sink = new RecordingSink();
        var parser = new ProfileMessageParser(SelectorProfile(), sink);
        var payload = new byte[24];
        payload[22] = 3;

        // roulette_id declares min 1 and is not a selector.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), 0);
        parser.Accept(Message(100, payload));

        Assert.Empty(sink.Events);
        var stats = parser.GetParserStats();
        Assert.Equal(1, stats.ParseFailed);
        Assert.Equal(0, stats.Ignored);
        Assert.Equal(ParserErrorCode.FieldConstraint, Assert.Single(stats.RecentErrors).Code);
    }

    /// <summary>The valid test profile with a selector on CONTENT_FINDER_POP, in the CN shape.</summary>
    private ProtocolProfile SelectorProfile()
    {
        var document = ProfileTestFiles.Valid();
        var pop = (Dictionary<string, object>)((List<object>)document["messages"])[0];
        ((List<object>)pop["fields"]).Add(
            ProfileTestFiles.Field("finder_state", 22, "u8", allowed: new[] { 3 }, role: "selector"));
        return ProfileLoader.Load(
            ProfileTestFiles.Write(_directory, "selector-profile", document));
    }

    /// <summary>The valid test profile plus the two identity messages, in the CN shape.</summary>
    private static Dictionary<string, object> WithIdentityMessages()
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        messages.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = "ZONE_TERRITORY",
            ["opcode"] = 103,
            ["direction"] = "SERVER_TO_CLIENT",
            ["expected_length"] = 136,
            ["fields"] = new List<object>
            {
                ProfileTestFiles.Field("territory_id", 2, "u16", endian: "little", min: 1),
            },
        });
        messages.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = "PLAYER_JOB",
            ["opcode"] = 104,
            ["direction"] = "SERVER_TO_CLIENT",
            ["expected_length"] = 16,
            ["fields"] = new List<object>
            {
                ProfileTestFiles.Field("job_id", 0, "u8", min: 1, max: 43),
            },
        });
        return document;
    }

    [Fact]
    public void AThrowingSinkDoesNotEscapeTheParser()
    {
        var parser = new ProfileMessageParser(_profile, new ThrowingSink());

        parser.Accept(Message(102, new byte[] { 7, 0 }));

        Assert.Equal(ParserErrorCode.Internal, Assert.Single(parser.GetParserStats().RecentErrors).Code);
    }

    private static DecodedMessage Message(
        int opcode,
        byte[] payload,
        int segmentType = 0,
        MessageDirection direction = MessageDirection.Inbound) =>
        new(
            "20000000-0000-4000-8000-000000000099",
            direction,
            new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero),
            TimeSpan.Zero,
            1000,
            (ushort)segmentType,
            (ushort)opcode,
            payload,
            "test-connection");

    private sealed class RecordingSink : ISemanticEventSink
    {
        public List<SemanticEvent> Events { get; } = new();

        public void Accept(SemanticEvent semanticEvent) => Events.Add(semanticEvent);
    }

    private sealed class ThrowingSink : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent) =>
            throw new InvalidOperationException("the sink is broken");
    }
}
