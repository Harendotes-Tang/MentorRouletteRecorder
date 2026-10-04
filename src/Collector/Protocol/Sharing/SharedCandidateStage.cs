using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>What one staged entry is.</summary>
internal enum StagedEntryKind
{
    /// <summary>A semantic event the candidate's profile parsed.</summary>
    Event,

    /// <summary>The capture queue dropped observations; the sequence has a hole here.</summary>
    EventsDropped,

    /// <summary>The game connection that had been delivering messages ended.</summary>
    ConnectionLost,

    /// <summary>
    /// The capture gave up one direction of one connection; a hole in the sequence only if the events
    /// drained before it came in that way (audit 2026-10-03, S33-5).
    /// </summary>
    DirectionDamaged,
}

/// <summary>
/// One staged observation. An event carries what the parser would have handed the state machine:
/// kind, times and the roulette, territory or job id - and the event key, whose payload part is a
/// SHA-256 digest the live path already persists, never payload bytes.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="AtUtc">Wall-clock time.</param>
/// <param name="Mono">Monotonic reading on the capture clock.</param>
/// <param name="Event">The event, for <see cref="StagedEntryKind.Event"/>.</param>
/// <param name="DroppedCount">Observations lost, for <see cref="StagedEntryKind.EventsDropped"/>.</param>
/// <param name="ConnectionKey">
/// Opaque key of the connection the event's message arrived on, or of the connection that lost a
/// direction, for <see cref="StagedEntryKind.Event"/> and <see cref="StagedEntryKind.DirectionDamaged"/>.
/// </param>
/// <param name="Direction">The direction that message travelled in, or the direction lost.</param>
internal sealed record StagedEntry(
    StagedEntryKind Kind, DateTimeOffset AtUtc, TimeSpan Mono, SemanticEvent? Event = null, long DroppedCount = 0,
    string? ConnectionKey = null, MessageDirection Direction = MessageDirection.Inbound);

/// <summary>
/// A candidate parser and its bounded staging list for one capture session.
///
/// The candidate's rebuilt profile parses the same live messages the observer sees, side-effect
/// free: events go into this list and nowhere else - no database, no live event, no announcement.
/// When the candidate passes verification the pipeline binds a real parser and drains the list
/// through the path live events take, in order, so whatever arrived while the profile was being
/// written is recorded exactly once. The list belongs to one capture session and is thrown away
/// when that session ends. It holds at most <see cref="MaxEntries"/>; a stage that outgrows it is
/// emptied and marked overflowed, and the candidate cannot bind in that session.
/// </summary>
internal sealed class SharedCandidateStage
{
    /// <summary>
    /// Entries kept per candidate per session. Only the handful of declared messages are staged -
    /// roughly four per zone change plus the queue - so this covers a few hundred zone changes.
    /// </summary>
    public const int MaxEntries = 1024;

    private readonly List<StagedEntry> _entries = new();
    private readonly ProfileMessageParser _parser;

    /// <summary>The message being parsed, so the event it yields is staged with its connection and direction.</summary>
    private DecodedMessage? _parsing;

    /// <summary>Stages what <paramref name="profile"/> parses out of one session's messages.</summary>
    /// <param name="captureSessionId">Session whose messages are staged; others are ignored.</param>
    /// <param name="profile">The candidate's rebuilt profile.</param>
    public SharedCandidateStage(string captureSessionId, ProtocolProfile profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);
        ArgumentNullException.ThrowIfNull(profile);
        CaptureSessionId = captureSessionId;
        _parser = new ProfileMessageParser(profile, new Sink(this));
    }

    /// <summary>Session the staged entries belong to.</summary>
    public string CaptureSessionId { get; }

    /// <summary>True once the list outgrew <see cref="MaxEntries"/>; nothing is staged after that.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Entries staged and not yet drained.</summary>
    public int Count => _entries.Count;

    /// <summary>True when a job event is staged: draining sets the job, in the order it arrived.</summary>
    public bool HoldsJob => _entries.Any(entry => entry.Event is PlayerJob);

    /// <summary>Wall-clock time of the earliest entry not yet drained, or null when there is none.</summary>
    public DateTimeOffset? FirstAtUtc => _entries.Count == 0 ? null : _entries.Min(entry => entry.AtUtc);

    /// <summary>Parses one message of this session into the list.</summary>
    /// <param name="message">Decoded message; its payload is read now and never kept.</param>
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Overflowed && string.Equals(message.CaptureSessionId, CaptureSessionId, StringComparison.Ordinal))
        {
            _parsing = message;
            try
            {
                _parser.Accept(message);
            }
            finally
            {
                _parsing = null;
            }
        }
    }

    /// <summary>Stages a hole in the event sequence.</summary>
    public void EventsDropped(long count, DateTimeOffset atUtc, TimeSpan mono) =>
        Add(new StagedEntry(StagedEntryKind.EventsDropped, atUtc, mono, DroppedCount: count));

    /// <summary>Stages the end of the game connection.</summary>
    public void ConnectionLost(DateTimeOffset atUtc, TimeSpan mono) =>
        Add(new StagedEntry(StagedEntryKind.ConnectionLost, atUtc, mono));

    /// <summary>Stages the loss of one direction of one connection, in its place among the events.</summary>
    public void DirectionDamaged(string connectionKey, MessageDirection direction, DateTimeOffset atUtc, TimeSpan mono) =>
        Add(new StagedEntry(
            StagedEntryKind.DirectionDamaged, atUtc, mono, ConnectionKey: connectionKey, Direction: direction));

    /// <summary>Hands over every staged entry in arrival order and empties the list.</summary>
    public IReadOnlyList<StagedEntry> Drain()
    {
        var drained = _entries.ToArray();
        _entries.Clear();
        return drained;
    }

    private void Add(StagedEntry entry)
    {
        if (Overflowed)
        {
            return;
        }

        if (_entries.Count >= MaxEntries)
        {
            // A partial sequence would record a partial evening; there is no honest way to bind on it.
            Overflowed = true;
            _entries.Clear();
            return;
        }

        _entries.Add(entry);
    }

    private sealed class Sink : ISemanticEventSink
    {
        private readonly SharedCandidateStage _stage;

        public Sink(SharedCandidateStage stage) => _stage = stage;

        public void Accept(SemanticEvent semanticEvent) =>
            _stage.Add(new StagedEntry(
                StagedEntryKind.Event, semanticEvent.ObservedAtUtc, semanticEvent.Mono, semanticEvent,
                ConnectionKey: _stage._parsing?.ConnectionKey,
                Direction: _stage._parsing?.Direction ?? MessageDirection.Inbound));
    }
}
