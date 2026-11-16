using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>What one observed message changed about who the client is.</summary>
public enum CharacterIdentityChange
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Another character is logged in now.</summary>
    Named,

    /// <summary>The character named earlier has stayed logged in long enough to be taken as real.</summary>
    Settled,
}

/// <summary>
/// Says which character this client is logged in as, from the segment headers of the live traffic.
///
/// Every server-to-client segment on the game connections is addressed to the player's own actor
/// (real-machine traces of 2026-11-14 and 2026-11-15, Chinese service: one value for a character,
/// the same after logging in again and through zone changes, a different one per character). The
/// title and character-selection connection carries another value that changes from one session to
/// the next and means nothing; it sends a few segments every eleven seconds.
///
/// So an actor is named only by density: it must be the target of
/// <see cref="DominantPercent"/> percent of the latest <see cref="WindowSize"/> IPC segments, all
/// of them inside <see cref="Recent"/>. A game connection does that within the first second of a
/// login; the selection screen never does. A name stays until another actor earns it: a quiet
/// moment, a reconnect or a zone change names nobody and so changes nothing.
///
/// <see cref="CharacterIdentityChange.Settled"/> follows once the same actor has held the name for
/// <see cref="SettleSamples"/> further segments and <see cref="SettleAfter"/>: the point after
/// which a character never seen before is recorded. A burst that named the wrong thing would not
/// last that long, and recording it would ask the player about a character that does not exist.
///
/// The actor is kept in memory only. Not thread-safe: the pipeline calls it under its own lock.
/// </summary>
public sealed class CharacterIdentityTracker
{
    /// <summary>The latest IPC segments judged.</summary>
    public const int WindowSize = 120;

    /// <summary>Share of those segments, in percent, one actor needs to be named.</summary>
    public const int DominantPercent = 90;

    /// <summary>Further segments addressed to the named actor before it counts as settled.</summary>
    public const int SettleSamples = 600;

    /// <summary>How far back the judged segments may reach.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(15);

    /// <summary>How long the named actor must have held the name before it counts as settled.</summary>
    public static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(20);

    private readonly Sample[] _window = new Sample[WindowSize];
    private int _count;
    private int _next;
    private TimeSpan _namedAt;
    private int _sinceNamed;

    /// <summary>The actor this client is logged in as, or null while nobody has been named.</summary>
    public uint? Actor { get; private set; }

    /// <summary>True once <see cref="Actor"/> has stayed long enough to be taken as real.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>Forgets everything: another capture session, or identification switched off.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _sinceNamed = 0;
        Actor = null;
        IsSettled = false;
    }

    /// <summary>Looks at one message; nothing of it is retained but the actor it was addressed to.</summary>
    /// <param name="message">Decoded message.</param>
    public CharacterIdentityChange Observe(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Direction != MessageDirection.Inbound ||
            message.SegmentType != FfxivFraming.SegmentTypeIpc ||
            message.TargetActor == 0)
        {
            return CharacterIdentityChange.None;
        }

        var actor = message.TargetActor;
        _window[_next] = new Sample(actor, message.Mono);
        _next = (_next + 1) % WindowSize;
        if (_count < WindowSize)
        {
            _count++;
        }

        if (Actor == actor)
        {
            if (IsSettled)
            {
                return CharacterIdentityChange.None;
            }

            _sinceNamed++;
            if (_sinceNamed < SettleSamples || message.Mono - _namedAt < SettleAfter)
            {
                return CharacterIdentityChange.None;
            }

            IsSettled = true;
            return CharacterIdentityChange.Settled;
        }

        if (!Dominates(actor, message.Mono))
        {
            return CharacterIdentityChange.None;
        }

        Actor = actor;
        IsSettled = false;
        _namedAt = message.Mono;
        _sinceNamed = 0;
        return CharacterIdentityChange.Named;
    }

    private bool Dominates(uint actor, TimeSpan now)
    {
        if (_count < WindowSize)
        {
            return false;
        }

        var same = 0;
        foreach (var sample in _window)
        {
            // One segment from before the window makes the whole reading stale: the burst that
            // would name this actor has not filled the window by itself.
            if (now - sample.Mono > Recent)
            {
                return false;
            }

            if (sample.Actor == actor)
            {
                same++;
            }
        }

        return same * 100 >= DominantPercent * WindowSize;
    }

    private readonly record struct Sample(uint Actor, TimeSpan Mono);
}
