using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The bounds a long-running Collector depends on, each pushed far past anything a real
/// session would produce.
///
/// The in-process half of <c>SoakTests</c>: the integration soak proves the whole stack stays
/// correct under load, this file proves each container inside it is genuinely bounded. A ring
/// that grows unnoticed is invisible in a fifteen second run and fatal in a six hour one.
/// </summary>
public sealed class SoakBoundsTests
{
    private const ushort UnknownOpcode = 60_000;

    /// <summary>The synthetic profile's PLAYER_JOB opcode; an 8-byte payload violates its length.</summary>
    private const ushort JobOpcode = 61_444;
    private const int Flood = 100_000;

    /// <summary>
    /// Undeclared opcodes are the bulk of ordinary traffic. A flood of them is counted as
    /// ignored and must not grow anything: no refusal, no ring entry
    /// (contracts/CHANGELOG.md entry 18).
    /// </summary>
    [Fact]
    public void AFloodOfUndeclaredOpcodesIsIgnoredWithoutGrowing()
    {
        var parser = new ProfileMessageParser(Profile(), new CountingSemanticSink());

        for (var index = 0; index < Flood; index++)
        {
            parser.Accept(Message(index, UnknownOpcode));
        }

        var stats = parser.GetParserStats();
        Assert.Equal(Flood, stats.Ignored);
        Assert.Equal(0, stats.ParseFailed);
        Assert.Equal(0, stats.ParseOk);
        Assert.Empty(stats.RecentErrors);
    }

    /// <summary>
    /// The parser keeps its most recent refusals for the diagnostics page. A stream of
    /// declared messages with a wrong length -- what a shifted client build produces -- must
    /// not turn that into an unbounded list.
    /// </summary>
    [Fact]
    public void TheParserErrorRingStaysAtItsCapacityUnderAFloodOfRefusals()
    {
        var parser = new ProfileMessageParser(Profile(), new CountingSemanticSink());

        for (var index = 0; index < Flood; index++)
        {
            parser.Accept(Message(index, JobOpcode));
        }

        var stats = parser.GetParserStats();

        Assert.Equal(Flood, stats.ParseFailed);
        Assert.Equal(0, stats.ParseOk);
        Assert.True(
            stats.RecentErrors.Count <= ProfileMessageParser.ErrorRingCapacity,
            $"the parser kept {stats.RecentErrors.Count} refusals; the ring is " +
            $"{ProfileMessageParser.ErrorRingCapacity}");

        // The ring is not merely bounded, it is full: anything less would mean refusals are
        // discarded before the bound is reached, leaving the diagnostics page with a shorter
        // history than it promises.
        Assert.Equal(ProfileMessageParser.ErrorRingCapacity, stats.RecentErrors.Count);
        Assert.All(
            stats.RecentErrors,
            error => Assert.Equal(ParserErrorCode.LengthMismatch, error.Code));
    }

    /// <summary>
    /// The parser's duplicate set is bounded too, and the bound must not cost correctness for
    /// anything recent: the tail of the stream is still recognised as already seen.
    /// </summary>
    [Fact]
    public void TheParserDuplicateSetStaysBoundedAndStillCatchesRecentRepeats()
    {
        var sink = new CountingSemanticSink();
        var parser = new ProfileMessageParser(Profile(), sink);

        for (var index = 0; index < Flood; index++)
        {
            parser.Accept(JobMessage(index));
        }

        Assert.Equal(Flood, parser.GetParserStats().ParseOk);
        Assert.Equal(0, parser.GetParserStats().Duplicates);

        // Replaying the tail is still caught; replaying the head is not, and that is the
        // documented trade-off of a bounded set rather than a bug.
        parser.Accept(JobMessage(Flood - 1));
        Assert.Equal(1, parser.GetParserStats().Duplicates);

        // Every message reached the sink either way: deduplication is diagnostics here, and
        // the state machine and the database own the real idempotency rule.
        Assert.Equal(Flood + 1, sink.Count);
    }

    /// <summary>
    /// The opcode table the fail-closed sink keeps is capped, so a stream of never-before-seen
    /// opcodes cannot grow memory. Counting an opcode is not parsing it, but it is still
    /// storage, and storage has to have a limit.
    /// </summary>
    [Fact]
    public void TheCountingSinkStopsTrackingNewOpcodesOnceItsTableIsFull()
    {
        var sink = new CountingSink();
        const int distinct = CountingSink.MaxTrackedOpcodes * 2;

        for (var opcode = 0; opcode < distinct; opcode++)
        {
            sink.Accept(Message(opcode, (ushort)opcode));
        }

        Assert.Equal(distinct, sink.AcceptedCount);
        Assert.Equal(CountingSink.MaxTrackedOpcodes, sink.DistinctOpcodeCount);

        // Nothing is silently lost: what stopped being tracked is counted as untracked.
        Assert.Equal(distinct - CountingSink.MaxTrackedOpcodes, sink.UntrackedCount);
    }

    /// <summary>
    /// A profile with no usable binding refuses everything, forever, without growing. This is
    /// the state a client update leaves the Collector in, and it may last for days.
    /// </summary>
    [Fact]
    public void AFailClosedParserRefusesIndefinitelyWithoutGrowing()
    {
        var sink = new CountingSemanticSink();
        var parser = new ProfileMessageParser(profile: null, sink);

        for (var index = 0; index < Flood; index++)
        {
            parser.Accept(Message(index, UnknownOpcode));
        }

        var stats = parser.GetParserStats();
        Assert.False(parser.IsUsable);
        Assert.Equal(Flood, stats.ParseFailed);
        Assert.Equal(0, sink.Count);
        Assert.Equal(ProfileMessageParser.ErrorRingCapacity, stats.RecentErrors.Count);
        Assert.All(
            stats.RecentErrors,
            error => Assert.Equal(ParserErrorCode.ProfileUnsupported, error.Code));
    }

    /// <summary>The checked-in synthetic profile, which is the only one that may be used offline.</summary>
    private static ProtocolProfile Profile()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");
        return ProfileLoader.Validate(path).Profile
            ?? throw new InvalidOperationException("the checked-in synthetic profile must load");
    }

    private static DecodedMessage Message(int index, ushort opcode) =>
        new(
            "bounds-session",
            MessageDirection.Inbound,
            DateTimeOffset.UnixEpoch.AddMilliseconds(index),
            TimeSpan.FromMilliseconds(index),
            index,
            61440,
            opcode,
            new byte[8],
            "bounds-connection");

    /// <summary>A PLAYER_JOB message the synthetic profile accepts.</summary>
    private static DecodedMessage JobMessage(int index) =>
        new(
            "bounds-session",
            MessageDirection.Inbound,
            DateTimeOffset.UnixEpoch.AddMilliseconds(index),
            TimeSpan.FromMilliseconds(index),
            index,
            61440,
            61444,
            new byte[] { 19, 0, 0, 0 },
            "bounds-connection");

    private sealed class CountingSemanticSink : ISemanticEventSink
    {
        public int Count { get; private set; }

        public void Accept(SemanticEvent semanticEvent) => Count++;
    }
}
