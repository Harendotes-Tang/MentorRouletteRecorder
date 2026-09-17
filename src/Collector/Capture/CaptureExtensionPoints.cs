using MentorRecorder.Collector.Protocol.Profiles;
using System.Collections.Concurrent;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Told when a capture session begins and ends, so the state machine can treat a capture that
/// stopped mid-run as an interruption rather than a completion.
///
/// Until Phase 3 connects the real listener, <see cref="NullCaptureLifecycleListener"/> stands
/// in: with no parser there is no run in flight to interrupt.
/// </summary>
public interface ICaptureLifecycleListener
{
    /// <summary>A capture session started.</summary>
    /// <param name="captureSessionId">Session identifier.</param>
    void OnCaptureStarted(string captureSessionId);

    /// <summary>A capture session ended.</summary>
    /// <param name="captureSessionId">Session identifier.</param>
    /// <param name="reason">Why it ended.</param>
    void OnCaptureStopped(string captureSessionId, CaptureEndReason reason);

    /// <summary>
    /// Reports that the bounded hand-off queue discarded observations. Implementations that
    /// own a state machine use this to end an in-flight run as interrupted; the default keeps
    /// diagnostics-only listeners source compatible.
    /// </summary>
    /// <param name="captureSessionId">Session whose queue overflowed.</param>
    /// <param name="droppedCount">Number of observations discarded by the queue.</param>
    void OnEventsDropped(string captureSessionId, long droppedCount)
    {
    }

    /// <summary>
    /// Reports that a game connection which had been delivering decoded messages ended. A run
    /// inside a duty becomes DISCONNECTED, which is never merged with LEFT_OR_ABANDONED
    /// (docs/state-machine.md section 3.6). The default keeps diagnostics-only listeners
    /// source compatible.
    /// </summary>
    /// <param name="captureSessionId">Session whose connection ended.</param>
    void OnConnectionLost(string captureSessionId)
    {
    }
}

/// <summary>Does nothing. The default until the Phase 3 state machine is wired in.</summary>
public sealed class NullCaptureLifecycleListener : ICaptureLifecycleListener
{
    /// <summary>Shared instance.</summary>
    public static NullCaptureLifecycleListener Instance { get; } = new();

    /// <inheritdoc />
    public void OnCaptureStarted(string captureSessionId)
    {
    }

    /// <inheritdoc />
    public void OnCaptureStopped(string captureSessionId, CaptureEndReason reason)
    {
    }
}

/// <summary>
/// Told how far a running capture session's silence can be trusted: why it is silent, how many
/// game connections it attached to midway, and what the adapter lost. Only the capture
/// controller knows these facts.
///
/// Advisory, never a lifecycle event: a reading ends no run. Readings of one session arrive
/// repeatedly and the receiver merges them to the worst; an unreported session counts as not
/// healthy there, so a missing reading can never make an absence look meaningful.
/// </summary>
public interface ICaptureHealthListener
{
    /// <summary>One reading of a session's capture health.</summary>
    /// <param name="health">The reading.</param>
    void OnCaptureHealth(Protocol.Calibration.CaptureSessionHealth health);
}

/// <summary>Ignores every reading. The default when nothing verifies shared calibrations.</summary>
public sealed class NullCaptureHealthListener : ICaptureHealthListener
{
    /// <summary>Shared instance.</summary>
    public static NullCaptureHealthListener Instance { get; } = new();

    /// <inheritdoc />
    public void OnCaptureHealth(Protocol.Calibration.CaptureSessionHealth health)
    {
    }
}

/// <summary>
/// One parser refusal, reduced to what may leave this process.
///
/// Deliberately not the parser's own <c>ParserError</c>: the capture layer must not depend on
/// the parsing layer, and the wire shape is a whitelist. An opcode number, a direction and a
/// short sentence count as diagnostics; a payload byte never does
/// (docs/privacy-boundary.md section 5).
/// </summary>
/// <param name="AtUtc">When the refusal was counted, or null when the source did not stamp it.</param>
/// <param name="Code">Wire token of the refusal, for example <c>E_UNKNOWN_OPCODE</c>.</param>
/// <param name="Opcode">Opcode of the offending message, rendered as <c>0xNNNN</c> on the wire.</param>
/// <param name="Direction">Direction token, <c>S2C</c> / <c>C2S</c> / <c>NONE</c>.</param>
/// <param name="Message">Short, non-sensitive explanation. Never contains payload bytes.</param>
public sealed record ParserErrorView(
    DateTimeOffset? AtUtc,
    string Code,
    ushort Opcode,
    string Direction,
    string Message);

/// <summary>Counters the parser publishes so diagnostics can show them without knowing the parser.</summary>
public interface IParserStats
{
    /// <summary>
    /// The most recent refusals, oldest first, already reduced to the wire shape.
    ///
    /// Defaulted to empty so an implementation with no parser -- a counting sink, a test
    /// double -- reports nothing rather than something wrong.
    /// </summary>
    IReadOnlyList<ParserErrorView> RecentErrors => Array.Empty<ParserErrorView>();

    /// <summary>Messages the parser understood.</summary>
    long ParseOkCount { get; }

    /// <summary>Messages the parser could not interpret.</summary>
    long ParseFailCount { get; }

    /// <summary>Messages discarded as duplicates of one already seen.</summary>
    long DuplicateCount { get; }

    /// <summary>
    /// Messages no profile message claims. Ordinary client traffic the record does not need;
    /// counted so "packets arrive, nothing is relevant yet" is a number, but neither a
    /// refusal nor an error (contracts/CHANGELOG.md entry 18).
    /// </summary>
    long IgnoredCount { get; }

    /// <summary>When the parser last produced a semantic event.</summary>
    DateTimeOffset? LastValidEventAtUtc { get; }

    /// <summary>
    /// Semantic event type of that last event (e.g. <c>DUTY_RESULT</c>), or null. Defaults to null so
    /// a counting-only implementation says nothing rather than something wrong.
    /// </summary>
    string? LastValidEventKind => null;
}

/// <summary>All-zero parser statistics, for a build with no parser.</summary>
public sealed class NullParserStats : IParserStats
{
    /// <summary>Shared instance.</summary>
    public static NullParserStats Instance { get; } = new();

    /// <inheritdoc />
    public long ParseOkCount => 0;

    /// <inheritdoc />
    public long ParseFailCount => 0;

    /// <inheritdoc />
    public long DuplicateCount => 0;

    /// <inheritdoc />
    public long IgnoredCount => 0;

    /// <inheritdoc />
    public DateTimeOffset? LastValidEventAtUtc => null;

    /// <inheritdoc />
    public IReadOnlyList<ParserErrorView> RecentErrors => Array.Empty<ParserErrorView>();
}

/// <summary>Current protocol profile, as far as anyone outside the profile layer needs to know.</summary>
/// <param name="Status">Profile status; anything but Verified means nothing is recorded.</param>
/// <param name="ProfileId">Identifier of the loaded profile.</param>
/// <param name="Region">Region the profile applies to.</param>
/// <param name="GameBuild">Client build the profile applies to.</param>
/// <param name="VerifiedAtUtc">When the profile was verified against evidence.</param>
/// <param name="EvidenceNote">How it was verified. A profile without evidence is never Verified.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record ProfileStatusSnapshot(
    ProfileStatus Status,
    string? ProfileId,
    Region Region,
    string? GameBuild,
    DateTimeOffset? VerifiedAtUtc,
    string? EvidenceNote,
    string? Message,
    ProfileOrigin? Origin = null,
    bool CalibrationActive = false);

/// <summary>Publishes the current protocol profile status.</summary>
public interface IProfileStatusProvider
{
    /// <summary>The profile in effect right now.</summary>
    ProfileStatusSnapshot Current { get; }
}

/// <summary>
/// Optional extension for a profile provider whose answer depends on the game process that
/// the capture controller just detected. Passing the detection avoids a second process scan
/// and guarantees that pre-flight validation, the session row and the parser bind the same
/// region/build pair.
/// </summary>
public interface IGameAwareProfileStatusProvider : IProfileStatusProvider
{
    /// <summary>Refreshes profile selection for the detected client.</summary>
    /// <param name="game">Detection result used by this capture attempt.</param>
    /// <returns>The selected profile status; non-verified results remain fail-closed.</returns>
    ProfileStatusSnapshot Refresh(GameProcessDetection game);
}

/// <summary>
/// The default while this repository holds no protocol profile: the status is <c>NONE</c> and
/// can never be <c>VERIFIED</c>. Phase 3 replaces it with a provider backed by
/// <c>protocol-profiles/</c> and its evidence requirements.
/// </summary>
public sealed class NoProfileStatusProvider : IProfileStatusProvider
{
    /// <summary>Explanation shown while no profile exists.</summary>
    public const string Message =
        "仓库中没有任何协议档案（PROTOCOL_PROFILE_STATUS = NONE）。" +
        "按 fail-closed 规则，本版本不会解析任何报文，也不会自动写入任何记录，只能手工补录。";

    /// <summary>Shared instance.</summary>
    public static NoProfileStatusProvider Instance { get; } = new();

    /// <inheritdoc />
    public ProfileStatusSnapshot Current { get; } = new(
        ProfileStatus.None, null, Region.Unknown, null, null, null, Message);
}

/// <summary>
/// The Phase-2 sink: it counts messages per opcode and does nothing else, so the capture
/// pipeline can be exercised end to end before the parser lands. Counting an opcode is not
/// parsing: no field is read, no meaning is assigned, nothing is written to the database. The
/// table is capped so a stream of unexpected opcodes cannot grow memory without bound.
/// </summary>
public sealed class CountingSink : IDecodedMessageSink, IParserStats
{
    /// <summary>Most distinct opcodes tracked before new ones are lumped together.</summary>
    public const int MaxTrackedOpcodes = 2048;

    private readonly ConcurrentDictionary<ushort, long> _byOpcode = new();
    private long _accepted;
    private long _untracked;

    /// <summary>Total messages handed to this sink.</summary>
    public long AcceptedCount => Interlocked.Read(ref _accepted);

    /// <summary>Messages whose opcode was not tracked because the table was full.</summary>
    public long UntrackedCount => Interlocked.Read(ref _untracked);

    /// <summary>Number of distinct opcodes seen, up to <see cref="MaxTrackedOpcodes"/>.</summary>
    public int DistinctOpcodeCount => _byOpcode.Count;

    /// <inheritdoc />
    public long ParseOkCount => 0;

    /// <inheritdoc />
    public long ParseFailCount => 0;

    /// <inheritdoc />
    public long DuplicateCount => 0;

    /// <inheritdoc />
    /// <remarks>Zero, not the accepted count: without a profile nothing was classified.</remarks>
    public long IgnoredCount => 0;

    /// <inheritdoc />
    /// <remarks>Always null: counting is not interpreting, so no event is ever valid here.</remarks>
    public DateTimeOffset? LastValidEventAtUtc => null;

    /// <inheritdoc />
    /// <remarks>Always empty: nothing is parsed here, so nothing can be refused.</remarks>
    public IReadOnlyList<ParserErrorView> RecentErrors => Array.Empty<ParserErrorView>();

    /// <summary>Count for one opcode.</summary>
    /// <param name="opcode">Opcode to look up.</param>
    public long CountFor(ushort opcode) => _byOpcode.TryGetValue(opcode, out var count) ? count : 0;

    /// <inheritdoc />
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Interlocked.Increment(ref _accepted);
        if (_byOpcode.Count >= MaxTrackedOpcodes && !_byOpcode.ContainsKey(message.Opcode))
        {
            Interlocked.Increment(ref _untracked);
            return;
        }

        _byOpcode.AddOrUpdate(message.Opcode, 1, static (_, existing) => existing + 1);
    }
}
