using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The finder on a template whose queue reply and match announcement are two messages
/// (<see cref="CalibrationTemplate.SeparateReply"/>; GLOBAL 2026.11.03, docs/protocol-profile-format.md section 11.3.1).
/// </summary>
public sealed partial class CalibrationObserver
{
    /// <summary>
    /// The finder on a template whose reply and pop are two messages (GLOBAL 2026.11.03,
    /// <see cref="CalibrationTemplate.SeparateReply"/>). The reply answers the request and echoes its
    /// roulette - <see cref="FindEcho"/> already read it by the reply's own shape - and nothing else; it
    /// is never a pop. The pop is a message of its own, read by the template's pop shape, and only while a
    /// queue the reply confirmed stands (<see cref="TrackDeclaredPop"/>). Unlike the one-message shape,
    /// the echo window says nothing about the pop: a tank queue can match in a third of a second, before
    /// the reply's window has closed, and the reply is already told apart by its own shape.
    /// </summary>
    /// <param name="connection">Connection the message arrived on.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="t">Session-relative arrival time.</param>
    /// <param name="at">Wall-clock arrival time.</param>
    /// <param name="echo">The request this message echoes, when <see cref="FindEcho"/> found one.</param>
    /// <param name="echoMax">Echo window from the template.</param>
    private void TrackSeparateReply(
        Connection connection, PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, long t,
        DateTimeOffset at, PendingRequest? echo, long echoMax)
    {
        if (echo is not null)
        {
            var job = ReplyJob(payload, out var jobViolation);
            RecordPair(new FinderPairHit(connection.Tag, echo.Opcode, opcode, echo.RouletteId, echo.TMs, t, at)
            {
                JobId = job,
                JobViolation = jobViolation,
            });
        }

        if (direction == _template.Reply.Direction && _replyOpcodes.Contains(opcode))
        {
            CountDiagnostic(_finderLengths, (opcode, payload.Length), MaxFinderLengths);
        }

        TrackDeclaredPop(connection, direction, payload, opcode, t, at, echoMax);
    }

    /// <summary>
    /// The job a reply carries when the template says the reply is also <c>PLAYER_JOB</c>: null when it
    /// does not, or when the reply is in a state whose selector says it carries no job. A reply in the
    /// job-carrying state whose job breaks the constraints is a violation, which keeps the reply from
    /// being written as the job message.
    /// </summary>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="violation">True when the state said "job" and the value was not one.</param>
    private long? ReplyJob(ReadOnlySpan<byte> payload, out bool violation)
    {
        violation = false;
        if (!_template.ReplyCarriesJob || _template.PlayerJob is not { } job || !job.AcceptsLength(payload.Length) ||
            job.Field("job_id") is not { } field || !SelectorsHold(payload, job) ||
            !FieldReader.TryRead(payload, field, out var value))
        {
            return null;
        }

        if (field.Constraints.IsSatisfiedBy(value))
        {
            return value;
        }

        violation = true;
        return null;
    }

    /// <summary>
    /// Pop-shaped server messages while a queue the reply confirmed stands, on a template whose pop is a
    /// message of its own. Each sighting is tallied per opcode as agreeing - it carried the queued roulette
    /// at the template's offset and satisfied every value field - or not, and only an agreeing one is kept
    /// as a sample (<see cref="CalibrationSnapshot.PopTallies"/>). The tally is what lets the draft refuse a
    /// shape that also carries other values while a queue stands - a list counting through small numbers
    /// has one row that agrees each time it is sent (the 1.2.1 retainer-bell accident) - and it includes the
    /// echo window, which the marker scan does not look at, because an instant match lands in it.
    ///
    /// Reply opcodes are tallied like any other: which of them is THE reply is the draft's decision, and it
    /// excludes only that one from the pop. Left out here, one stray message that happened to echo the
    /// roulette inside the reply window would have barred the real pop for the life of the evidence. The
    /// GLOBAL 2026.11.03 replies carry 0 and a small constant at the pop's offset, so they disagree with every
    /// queue but one and are refused by their own tally.
    ///
    /// The queue looked at is <c>DeclaredQueue</c>, not the one the marker scan uses: the game keeps a queue
    /// across a teleport, so a player who queued, went home and was matched there must still have the popup
    /// seen (review 2026-11-22, H1). It ends at a load into a known duty, a newer confirmed request, or after
    /// <see cref="CalibrationDraft.QueueWindow"/> - a queue the player withdrew is not visible in the traffic,
    /// and the hour bounds how long a later popup of someone else's queue can be counted against it. Nothing
    /// inside a load is sampled: an open load is skipped here, and the draft drops samples inside one.
    /// </summary>
    /// <param name="connection">Connection the message arrived on.</param>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="payload">Decoded payload; read now, never kept.</param>
    /// <param name="opcode">Opcode of the message.</param>
    /// <param name="t">Session-relative arrival time.</param>
    /// <param name="at">Wall-clock arrival time.</param>
    /// <param name="echoMax">Echo window from the template, to flag samples that arrived inside it.</param>
    private void TrackDeclaredPop(
        Connection connection, PacketDirection direction, ReadOnlySpan<byte> payload, ushort opcode, long t,
        DateTimeOffset at, long echoMax)
    {
        var pop = _template.Pop;
        if (direction != pop.Direction || !pop.AcceptsLength(payload.Length))
        {
            return;
        }

        _popShapeSeen++;
        if (connection.DeclaredQueue is { } stale && t - stale.TMs > (long)CalibrationDraft.QueueWindow.TotalMilliseconds)
        {
            connection.DeclaredQueue = null;
        }

        if (connection.Open is not null || connection.DeclaredQueue is not { } queued ||
            pop.Field("roulette_id") is not { } rouletteField)
        {
            return;
        }

        var agrees = FieldReader.TryReadSatisfied(payload, rouletteField, out var carried) &&
            carried == queued.RouletteId &&
            FirstUnsatisfied(payload, pop, rouletteField.Name, ProfileFieldRole.Value) is null;
        if (!TallyPop(opcode, agrees))
        {
            return;
        }

        if (!agrees)
        {
            HoldDisagreement(connection, opcode, t);
            return;
        }

        RememberDeclaredPop(new PopHit(connection.Tag, opcode, carried, t, at, t - queued.TMs <= echoMax,
            Array.Empty<CalibrationSelectorReading>()));
    }

    /// <summary>
    /// Remembers a disagreeing sighting for one match window, so that <see cref="ExcuseLonePopups"/> can take it back
    /// if a load into a known duty follows. It is counted at once and stays counted unless excused; a full list lets
    /// the oldest go, which only leaves it counted.
    /// </summary>
    /// <param name="connection">Connection it arrived on.</param>
    /// <param name="opcode">Opcode of the sighting.</param>
    /// <param name="t">Session-relative arrival time.</param>
    private void HoldDisagreement(Connection connection, ushort opcode, long t)
    {
        var window = (long)_template.MatchWindow.TotalMilliseconds;
        connection.PendingDisagreements.RemoveAll(pending => t - pending.TMs > window);
        if (connection.PendingDisagreements.Count >= MaxPendingDisagreements)
        {
            connection.PendingDisagreements.RemoveAt(0);
        }

        connection.PendingDisagreements.Add((opcode, t));
    }

    /// <summary>
    /// Takes back disagreements that were evidently someone else's match: a pop-shaped opcode that disagreed exactly
    /// once in the match window before a load into a known duty. The traffic does not show a withdrawn queue, so the
    /// popup of a party leader's queue or of a duty queued directly arrives while the withdrawn one still seems to
    /// stand, and the duty that follows it is what shows it was a match after all (review 2026-11-22, M1). A list
    /// sends a row per slot at once and disagrees several times in the window, so it is never excused; a popup that
    /// declined with no duty behind it stays counted. Called when a load that named a known duty closes.
    /// </summary>
    /// <param name="connection">Connection the load was on.</param>
    /// <param name="loadStartTMs">Session-relative start of the load.</param>
    private void ExcuseLonePopups(Connection connection, long loadStartTMs)
    {
        if (connection.PendingDisagreements.Count == 0)
        {
            return;
        }

        var window = (long)_template.MatchWindow.TotalMilliseconds;
        var lone = connection.PendingDisagreements
            .Where(pending => pending.TMs < loadStartTMs && loadStartTMs - pending.TMs <= window)
            .GroupBy(pending => pending.Opcode)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key);
        foreach (var opcode in lone)
        {
            if (_popTallies.TryGetValue(opcode, out var tally) && tally.Total > tally.Agreed)
            {
                _popTallies[opcode] = tally with { Total = tally.Total - 1 };
            }
        }

        connection.PendingDisagreements.Clear();
    }

    /// <summary>
    /// Keeps a pop sample of a two-message template, at most <see cref="MaxPopsPerBucket"/> per connection,
    /// opcode and roulette and <see cref="MaxPops"/> in all, the newest replacing the oldest. Unlike the
    /// one-message samples (<see cref="RememberPop"/>) a sample here only ever adds a chain, and losing one
    /// can only remove a chain - never name a pop that would not otherwise be named - so a full bucket or
    /// table is a smaller sample, not a reason to block the draft. Without this, a player who played five
    /// mentor roulettes on one connection before confirming the timeline would have been told the evidence
    /// overflowed; and keeping the oldest when the whole table is full would have left today's popup unseen
    /// for the life of the evidence (review 2026-11-22, L5).
    /// </summary>
    /// <param name="hit">Sample to keep.</param>
    private void RememberDeclaredPop(PopHit hit)
    {
        var oldest = -1;
        var held = 0;
        for (var index = 0; index < _pops.Count; index++)
        {
            var existing = _pops[index];
            if (existing.ConnectionTag == hit.ConnectionTag && existing.Opcode == hit.Opcode &&
                existing.RouletteId == hit.RouletteId)
            {
                held++;
                if (oldest < 0)
                {
                    oldest = index;
                }
            }
        }

        if (held >= MaxPopsPerBucket)
        {
            _pops.RemoveAt(oldest);
            _diagnosticsOverflow++;
        }
        else if (_pops.Count >= MaxPops)
        {
            _pops.RemoveAt(0);
            _diagnosticsOverflow++;
        }

        _pops.Add(hit);
    }

    /// <summary>
    /// Counts one pop-shaped sighting. False when the table is full and does not hold the opcode: a shape
    /// whose disagreements cannot be counted is not sampled at all, which can only make a pop harder to
    /// name, so it is a smaller sample rather than an overflow.
    /// </summary>
    /// <param name="opcode">Opcode of the sighting.</param>
    /// <param name="agrees">Whether it carried the queued roulette.</param>
    private bool TallyPop(ushort opcode, bool agrees)
    {
        if (!_popTallies.TryGetValue(opcode, out var tally))
        {
            if (_popTallies.Count >= MaxPopTallies)
            {
                _diagnosticsOverflow++;
                return false;
            }

            tally = new PopTally(0, 0);
        }

        _popTallies[opcode] = new PopTally(tally.Total + 1, tally.Agreed + (agrees ? 1 : 0));
        return true;
    }
}
