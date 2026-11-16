using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// Something about character identification worth a line in the log. It never carries an actor, a
/// digest or a key fingerprint: a name of what happened, a count, and the type of an exception.
/// </summary>
/// <param name="Event">Short machine-readable name.</param>
/// <param name="Count">How many rows or attempts it concerns, when it concerns any.</param>
/// <param name="Error">Type name of the exception behind it, when there is one.</param>
public sealed record CharacterIdentityNote(string Event, int? Count = null, string? Error = null);

/// <summary>
/// Telling the logged-in character apart while capture runs (docs/state-machine.md section 3.1).
///
/// Every message goes past <see cref="CharacterIdentityTracker"/>, whatever profile is bound. When
/// it names a character seen before, that character's profile becomes the current one of the
/// region; a character never seen before is recorded once it has stayed, or at the latest when a
/// run is matched for it. A run is stamped with the character logged in at the moment it is
/// matched (<see cref="IdentityAtMatch"/>), never with a profile the player chose for somebody
/// else.
///
/// Where nobody can be told apart - a region not verified for it, the setting off, no usable key,
/// no traffic that names anyone - nothing here does anything and a run goes to the current profile
/// exactly as before.
///
/// Identification is an extra: nothing in it may cost a recorded message. Whatever fails here is
/// noted (<see cref="CharacterIdentityNoted"/>) and recording goes on.
///
/// The actor and its digest stay in this class and the storage call. Neither is logged, published
/// or put on a status.
/// </summary>
public sealed partial class LiveProtocolPipeline
{
    /// <summary>
    /// Capture sessions in a row that must find the key file unusable before it is replaced. One
    /// failure can be the user's protection keys not being ready yet; replacing the key forgets
    /// every character, so it waits until the file has plainly come from somewhere else.
    /// </summary>
    public const int KeyFailuresBeforeReplacing = 3;

    private const string IdentityStatsMessage = "角色档案已更新，统计需要重新查询。";

    private readonly CharacterIdentityTracker _identityTracker = new();
    private CharacterIdentityService _identities = null!;
    private SettingsRepository _identitySettings = null!;
    private Func<bool, CharacterIdentityKeyResult> _identityKeySource = null!;
    private CharacterIdentityKey? _identityKey;
    private bool _identityKeyTried;
    private bool _identificationEnabled;
    private bool _identityStatsOwed;
    private uint _matchingActor;

    /// <summary>Raised for the log; see <see cref="CharacterIdentityNote"/>.</summary>
    public event Action<CharacterIdentityNote>? CharacterIdentityNoted;

    /// <summary>
    /// Where the key the identities are digested with comes from; by default the key file beside
    /// the database. The argument asks for an unusable file to be replaced. A test supplies its own
    /// so that it neither needs nor leaves a key file.
    /// </summary>
    public Func<bool, CharacterIdentityKeyResult> CharacterIdentityKeySource
    {
        get => _identityKeySource;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _identityKeySource = value;
                _identityKey = null;
                _identityKeyTried = false;
            }
        }
    }

    /// <summary>Applies <c>capture.character_identification_enabled</c>; off forgets who is logged in.</summary>
    /// <param name="enabled">The setting as it now stands.</param>
    public void ApplyCharacterIdentificationSetting(bool enabled)
    {
        lock (_gate)
        {
            _identificationEnabled = enabled;
            if (!enabled)
            {
                _identityTracker.Reset();
            }
        }
    }

    /// <summary>
    /// The player has just said which profile a character is. If the character logged in right now
    /// is bound to a profile, that profile becomes the current one - which is how an answer about
    /// the character being played takes effect at once, and why an answer about another character
    /// changes nothing about where the next run goes.
    /// </summary>
    public void ReconsiderCharacter()
    {
        lock (_gate)
        {
            if (!IdentificationActive || _identityTracker.Actor is not { } actor || _identityKey is not { } key)
            {
                return;
            }

            try
            {
                _identities.Recognise(_selection.Region, key.KeyId, key.Digest(actor));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Note(new CharacterIdentityNote("observation_failed", Error: ex.GetType().Name));
            }
        }
    }

    private void InitializeCharacterIdentity(SettingsRepository settings, bool enabled)
    {
        _identities = new CharacterIdentityService(_database, settings, _clock);
        _identitySettings = settings;
        _identityKeySource = replace => CharacterIdentityKey.LoadBeside(_database.Path, replace);
        _identificationEnabled = enabled;
    }

    /// <summary>True while runs of the session's region are filed by the character that is logged in.</summary>
    private bool IdentificationActive =>
        CharacterIdentification.ModeFor(_selection.Region, _identificationEnabled) ==
        CharacterIdentificationMode.Automatic;

    /// <summary>
    /// A capture session begins: who is logged in is learned again from its traffic, and the key
    /// is read now, before any message, rather than in the middle of a login.
    /// </summary>
    private void BeginCharacterIdentification()
    {
        _identityTracker.Reset();
        _matchingActor = 0;
        _identityKeyTried = false;
        _identityKey = null;
        if (IdentificationActive)
        {
            LoadIdentityKey();
        }
    }

    /// <summary>
    /// Reads the key, once per capture session. A file that has stayed unusable for
    /// <see cref="KeyFailuresBeforeReplacing"/> sessions is replaced; the identities written under
    /// a key that is gone are then removed before anything is compared with the new one. Any
    /// failure leaves the session without a key, which sends its runs to the current profile.
    /// </summary>
    private void LoadIdentityKey()
    {
        _identityKeyTried = true;
        _identityKey = null;
        try
        {
            var result = _identityKeySource(false);
            var failures = KeyFailures();
            if (result.Status == CharacterIdentityKeyStatus.Unusable)
            {
                failures++;
                if (failures >= KeyFailuresBeforeReplacing)
                {
                    result = _identityKeySource(true);
                    Note(new CharacterIdentityNote(
                        result.Key is null ? "key_replace_failed" : "key_replaced", failures));
                }
                else
                {
                    Note(new CharacterIdentityNote("key_unusable", failures));
                }

                SetKeyFailures(result.Key is null ? failures : 0);
            }
            else if (result.Status == CharacterIdentityKeyStatus.Unavailable)
            {
                Note(new CharacterIdentityNote("key_unavailable"));
            }
            else if (failures != 0)
            {
                SetKeyFailures(0);
            }

            if (result.Key is not { } key)
            {
                return;
            }

            var removed = _identities.ForgetOtherKeys(key.KeyId);
            if (removed > 0)
            {
                Note(new CharacterIdentityNote("identities_removed", removed));
                _identityStatsOwed = true;
            }

            // Only now: a key whose stale rows could not be cleared would count them as characters.
            _identityKey = key;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _identityKey = null;
            Note(new CharacterIdentityNote("key_load_failed", Error: ex.GetType().Name));
        }
    }

    private int KeyFailures() =>
        int.TryParse(
            _identitySettings.GetSetting(CharacterIdentification.KeyFailuresSetting),
            NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;

    private void SetKeyFailures(int count) => _identitySettings.SetSetting(
        CharacterIdentification.KeyFailuresSetting, count.ToString(CultureInfo.InvariantCulture));

    /// <summary>Lets the tracker see one message and acts on a character logging in.</summary>
    /// <param name="message">Message being accepted, under the gate.</param>
    private void ObserveCharacter(DecodedMessage message)
    {
        if (!IdentificationActive)
        {
            return;
        }

        try
        {
            var change = _identityTracker.Observe(message);
            if (change == CharacterIdentityChange.None || _identityTracker.Actor is not { } actor)
            {
                return;
            }

            // Identification switched on, or the region learned, after the session began.
            if (!_identityKeyTried)
            {
                LoadIdentityKey();
            }

            if (_identityKey is not { } key)
            {
                return;
            }

            var digest = key.Digest(actor);
            var observation = change == CharacterIdentityChange.Named
                ? _identities.Recognise(_selection.Region, key.KeyId, digest)
                : _identities.Observe(_selection.Region, key.KeyId, digest);
            if (change == CharacterIdentityChange.Settled && observation.IdentityId is null)
            {
                Note(new CharacterIdentityNote("identity_limit_reached", CharacterIdentity.MaxIdentitiesPerRegion));
            }

            _identityStatsOwed |= observation.StatisticsChanged;
            PublishOwedIdentityStats();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The run that follows is stamped at match time, where a failure is the processor's
            // to report.
            Note(new CharacterIdentityNote("observation_failed", Error: ex.GetType().Name));
        }
    }

    /// <summary>
    /// The actor a message names as this client: the one a server-to-client segment is addressed
    /// to, or the one a client-to-server segment comes from. Zero when it names nobody. Asked only
    /// of a message a profile is parsing, which is a message of the game connection.
    /// </summary>
    private static uint OwnActorOf(DecodedMessage message) =>
        message.Direction == MessageDirection.Inbound ? message.TargetActor : message.SourceActor;

    /// <summary>
    /// The character a run being created belongs to, read and - when it was never seen - recorded
    /// inside the transaction that creates the run. Null when nobody is being told apart, which
    /// sends the run to the current profile.
    ///
    /// The message that is matching the run says who the client is by itself, so a run matched
    /// before the tracker has named anyone - capture restarted in a quiet place - still gets its
    /// own character rather than the profile somebody chose by hand. A run created by anything
    /// else (a replayed message, a lifecycle event) takes the tracker's word.
    /// </summary>
    /// <param name="transaction">The processor's transaction.</param>
    private CharacterIdentityObservation? IdentityAtMatch(SqliteTransaction transaction)
    {
        // The key was read when the session began; it is not read here, inside somebody else's
        // transaction.
        if (!IdentificationActive || _identityKey is not { } key)
        {
            return null;
        }

        var actor = _matchingActor != 0 ? _matchingActor : _identityTracker.Actor ?? 0;
        if (actor == 0)
        {
            return null;
        }

        var observation = _identities.Observe(_selection.Region, key.KeyId, key.Digest(actor), transaction);
        if (observation.IdentityId is null)
        {
            Note(new CharacterIdentityNote("identity_limit_reached", CharacterIdentity.MaxIdentitiesPerRegion));
        }

        // Announced once the transaction is through (PublishOwedIdentityStats).
        _identityStatsOwed |= observation.StatisticsChanged;
        return observation;
    }

    /// <summary>Publishes what a login or a match changed about the profiles.</summary>
    private void PublishOwedIdentityStats()
    {
        if (!_identityStatsOwed)
        {
            return;
        }

        _identityStatsOwed = false;
        _liveEvents.PublishStatsInvalidated(IdentityStatsMessage);
    }

    private void Note(CharacterIdentityNote note)
    {
        try
        {
            CharacterIdentityNoted?.Invoke(note);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A log that cannot be written is not a reason to lose a message.
        }
    }
}
