using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Pipeline;

// A confirmed calibration bound inside the running session without losing the mentor run the
// player is already in (audit 2026-10-03, ODp-1).
//
// A confirmation replaces the state machine, and a new machine knows nothing of what happened
// before it. That used to lose a real run two ways. A confirmation that rewrote the profile in
// force rebuilt the machine while it held a queue request, and the duty the request led to was
// ignored: that rebind now waits until the machine is between runs, exactly as a shared swap
// does. And a session that had only been counting bound its first machine while the player was
// already queued: what the session saw of the confirmed profile's messages while the card waited
// is now handed to the new machine first, in order - with what the session lost meanwhile in its
// place among them, so the run the replay follows ends as it would have live (audit 2026-10-03, V2-2).
public sealed partial class LiveProtocolPipeline
{
    /// <summary>Most entries kept while the card waits; the oldest go first.</summary>
    internal const int MaxCardWaitMessages = 2048;

    /// <summary>What one entry kept while the card waits stands for.</summary>
    private enum CardWaitKind
    {
        /// <summary>A message the ready draft declares.</summary>
        Message,

        /// <summary>The queue overflowed and dropped observations.</summary>
        EventsDropped,

        /// <summary>Every decoded game connection ended.</summary>
        ConnectionLost,

        /// <summary>The capture gave up one direction of a connection.</summary>
        DirectionDamaged,
    }

    /// <summary>One entry kept while the card waits, in the order the session saw it.</summary>
    /// <param name="Kind">What it stands for.</param>
    /// <param name="Mono">Monotonic reading: the message's own, or the one a loss is stamped with.</param>
    /// <param name="Message">The message, for <see cref="CardWaitKind.Message"/>.</param>
    /// <param name="AtUtc">When a loss was noticed.</param>
    /// <param name="DroppedCount">Observations lost, for <see cref="CardWaitKind.EventsDropped"/>.</param>
    /// <param name="ConnectionKey">Connection that lost a direction, for <see cref="CardWaitKind.DirectionDamaged"/>.</param>
    /// <param name="Direction">The direction it lost.</param>
    private readonly record struct CardWaitEntry(
        CardWaitKind Kind,
        TimeSpan Mono,
        DecodedMessage? Message = null,
        DateTimeOffset AtUtc = default,
        long DroppedCount = 0,
        string? ConnectionKey = null,
        MessageDirection Direction = MessageDirection.Inbound);

    /// <summary>
    /// Id of the local profile in force that a confirmation rewrote, or wrote over a shared one, while
    /// its machine could not be replaced - a run was in flight, or a queue request was parked. Settled after the first
    /// message that leaves the machine between runs (<see cref="SettleOwedLocalRebind"/>); never
    /// outlives the session.
    /// </summary>
    private string? _localRebindOwed;

    /// <summary>
    /// Id of the profile the machine was recording with when <see cref="_localRebindOwed"/> was owed:
    /// the local profile's previous version, or the shared profile it takes over from (audit
    /// 2026-10-03, OCal-3). Read only while a rebind is owed.
    /// </summary>
    private string? _localRebindFrom;

    /// <summary>
    /// Messages of the opcodes the ready draft declares, kept while the session records nothing,
    /// oldest first, with the losses seen between them, for <see cref="ReplayWhileCardWaited"/>.
    /// </summary>
    private readonly Queue<CardWaitEntry> _cardWait = new();

    /// <summary>
    /// The messages the ready draft has declared this session, by opcode and direction. Only grows
    /// until a parser binds, so a draft re-derived with one message more cannot open a hole in
    /// what was kept of the others.
    /// </summary>
    private readonly HashSet<(ushort Opcode, MessageDirection Direction)> _cardWaitKeys = new();

    /// <summary>
    /// The confirmation rewrote the profile this session already records with - it gained a message
    /// it lacked - or wrote the local profile that takes over from the shared one in force. Between
    /// runs the parser is rebuilt over the new file at once; with a run in flight or a queue request
    /// parked a new machine would lose that run, so the rebind is owed instead.
    /// </summary>
    /// <param name="recording">Processor recording with the profile's previous version, or with the shared profile.</param>
    /// <param name="profile">The profile as just reloaded from disk.</param>
    /// <returns>True when the parser was rebuilt now.</returns>
    private bool RebindConfirmed(SemanticEventProcessor recording, ProtocolProfile profile)
    {
        if (BetweenRuns(recording) && SwapParser(profile))
        {
            _localRebindOwed = null;
            return true;
        }

        _localRebindOwed = profile.ProfileId;
        _localRebindFrom = _boundProfileId;
        return false;
    }

    /// <summary>
    /// An owed rebind goes ahead after the first message that leaves the machine between runs.
    /// Called once the parser is done with that message, never underneath it. An owed rebind the
    /// selection no longer stands behind - the profile was withdrawn, or another took its place -
    /// is forgotten.
    /// </summary>
    private void SettleOwedLocalRebind()
    {
        if (_localRebindOwed is not { } owed)
        {
            return;
        }

        if (!_active || _processor is not { } recording ||
            !string.Equals(_boundProfileId, _localRebindFrom, StringComparison.Ordinal) ||
            _selection is not { IsUsable: true, Origin: ProfileOrigin.Local, Profile: { } profile } ||
            !string.Equals(profile.ProfileId, owed, StringComparison.Ordinal))
        {
            _localRebindOwed = null;
            return;
        }

        if (!BetweenRuns(recording))
        {
            return;
        }

        _localRebindOwed = null;
        SwapParser(profile);
    }

    /// <summary>
    /// Remembers the messages the ready draft declares, so a confirmation can hand the new machine
    /// what happened while the card waited. Called on the calibration refresh, while nothing records.
    /// </summary>
    private void NoteReadyDraft()
    {
        if (_parser is not null || _calibration.State != CalibrationState.Ready ||
            _calibration.CurrentDraft() is not { } draft)
        {
            return;
        }

        foreach (var message in draft.Messages)
        {
            _cardWaitKeys.Add((message.Opcode, message.Direction == PacketDirection.ClientToServer
                ? MessageDirection.Outbound
                : MessageDirection.Inbound));
        }
    }

    /// <summary>
    /// Keeps a message the ready draft declares while the session records nothing. Bounded by
    /// count and by the queue window: a request older than that could not become a run anyway.
    /// </summary>
    /// <param name="message">Message just counted.</param>
    private void KeepWhileCardWaits(DecodedMessage message)
    {
        if (!_cardWaitKeys.Contains((message.Opcode, message.Direction)))
        {
            return;
        }

        _cardWait.Enqueue(new CardWaitEntry(CardWaitKind.Message, message.Mono, message));
        while (_cardWait.Count > MaxCardWaitMessages ||
            message.Mono - _cardWait.Peek().Mono > CalibrationDraft.QueueWindow)
        {
            _cardWait.Dequeue();
        }
    }

    /// <summary>
    /// Keeps, in its place among the messages, a loss the session saw while it records nothing: the
    /// replay has to follow the run through it as the live machine would have. A loss with nothing
    /// kept before it changes nothing the replay hands over, and is not kept.
    /// </summary>
    /// <param name="kind">What was lost.</param>
    /// <param name="droppedCount">Observations lost, for an overflow.</param>
    /// <param name="connectionKey">Connection that lost a direction.</param>
    /// <param name="direction">The direction it lost.</param>
    private void KeepLossWhileCardWaits(
        CardWaitKind kind, long droppedCount = 0, string? connectionKey = null,
        MessageDirection direction = MessageDirection.Inbound)
    {
        if (_cardWait.Count == 0)
        {
            return;
        }

        _cardWait.Enqueue(new CardWaitEntry(
            kind, LifecycleMono(), AtUtc: _clock.UtcNow, DroppedCount: droppedCount,
            ConnectionKey: connectionKey, Direction: direction));
        if (_cardWait.Count > MaxCardWaitMessages)
        {
            _cardWait.Dequeue();
        }
    }

    /// <summary>Hands over what was kept while the card waited and forgets it.</summary>
    private CardWaitEntry[] TakeCardWait()
    {
        var waited = _cardWait.ToArray();
        ForgetCardWait();
        return waited;
    }

    /// <summary>Forgets what was kept while the card waited: a parser is bound, or the session changed.</summary>
    private void ForgetCardWait()
    {
        _cardWait.Clear();
        _cardWaitKeys.Clear();
    }

    /// <summary>
    /// Replays, through the parser a confirmation just bound, what the session saw of its messages
    /// while the card waited: the queue request of a player who confirmed while queued is parked in
    /// the new machine, and a duty already loading is entered. What is older than
    /// <see cref="FreshMatchAge"/> is replayed rather than announced (see <see cref="ApplyAndPublish"/>).
    /// A loss seen between them reaches the machine in its place, as it would have live: an overflow
    /// or a direction given up that carried the replayed messages is a gap, every connection closing
    /// is a lost connection (audit 2026-10-03, V2-2).
    /// </summary>
    /// <param name="waited">Entries taken by <see cref="TakeCardWait"/>, oldest first.</param>
    private void ReplayWhileCardWaited(IReadOnlyList<CardWaitEntry> waited)
    {
        if (_parser is not { } parser || _processor is not { } processor)
        {
            return;
        }

        var now = LifecycleMono();
        foreach (var entry in waited)
        {
            try
            {
                switch (entry.Kind)
                {
                    case CardWaitKind.Message when entry.Message is { } message:
                        var parsedBefore = parser.GetParserStats().ParseOk;
                        ApplyAndPublish(() => parser.Accept(message), replayed: now - message.Mono > FreshMatchAge);
                        NoteProfileConnection(message, parsedBefore, parser.GetParserStats().ParseOk);
                        break;
                    case CardWaitKind.EventsDropped:
                        ApplyAndPublish(() => processor.OnEventsDropped(entry.DroppedCount, entry.AtUtc, entry.Mono));
                        break;
                    case CardWaitKind.ConnectionLost:
                        ApplyAndPublish(() => processor.OnConnectionLost(entry.AtUtc, entry.Mono));
                        break;
                    case CardWaitKind.DirectionDamaged when CarriesProfileMessages(entry.ConnectionKey!, entry.Direction):
                        ApplyAndPublish(() => processor.OnEventsDropped(1, entry.AtUtc, entry.Mono));
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                // The storage failure is latched on the processor; the next live message faults capture
                // exactly as it would have if the message had arrived live.
                return;
            }
        }
    }
}
