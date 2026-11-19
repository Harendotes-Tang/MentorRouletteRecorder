using System.Globalization;
using System.Security.Cryptography;
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
/// Every message goes past <see cref="CharacterLoginRecognizer"/>, whatever profile is bound. The
/// login message a character gets when it enters the game says, by a lasting identifier, who it is
/// and which actor it has on that game connection. At that moment a character seen before makes
/// its profile the current one of the region, and a character never seen is recorded. A run is
/// filed under the character that logged in on the game connection the matching message came on
/// (<see cref="IdentityAtMatch"/>), live or replayed: never under a profile the player chose for
/// somebody else, and never under the character played before.
///
/// Where nobody can be told apart - a region not verified for it, the setting off, no usable key,
/// no login message recognised in the session (one whose shape a patch has changed, say) - nothing
/// here does anything and a run goes to the current profile exactly as before.
///
/// Identification is an extra: nothing in it may cost a recorded message. Whatever fails here is
/// noted (<see cref="CharacterIdentityNoted"/>) and recording goes on.
///
/// The identifier is digested the moment it is read, and the digest stays in this class and the
/// storage call; neither it nor an actor is logged, published or put on a status.
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

    /// <summary>Game connections whose login is remembered per capture session.</summary>
    private const int MaxSessionLogins = 64;

    private readonly CharacterLoginRecognizer _logins = new();
    // Game connection of this session -> digest of the lasting identifier of the character that
    // logged in on it. Each login opens a connection of its own, and the match of a run comes on it.
    private readonly Dictionary<string, string> _loginDigests = new(StringComparer.Ordinal);
    private string? _lastLoginDigest;
    private bool _loginMissingNoted;
    private bool _saturationNoted;
    private CharacterIdentityService _identities = null!;
    private SettingsRepository _identitySettings = null!;
    private Func<bool, CharacterIdentityKeyResult> _identityKeySource = null!;
    private CharacterIdentityKey? _identityKey;
    private bool _identityKeyTried;
    private bool _identificationEnabled;
    private bool _identityStatsOwed;
    // The connection of the message a run is being matched from, while the parser is at it.
    private string? _matchingConnection;

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
                // Which connections have been decided stays known: turned on again mid-connection,
                // ordinary traffic must not be taken for a login.
                ForgetLogins(keepConnections: true);
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
            if (!IdentificationActive || _identityKey is not { } key || _lastLoginDigest is not { } digest)
            {
                return;
            }

            try
            {
                _identities.Recognise(_selection.Region, key.KeyId, digest);
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
        ForgetLogins();
        _matchingConnection = null;
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

    private void ForgetLogins(bool keepConnections = false)
    {
        if (!keepConnections)
        {
            _logins.Reset();
            _saturationNoted = false;
        }

        _loginDigests.Clear();
        _lastLoginDigest = null;
        _loginMissingNoted = false;
    }

    /// <summary>
    /// Acts on a character entering the game: the one message that says who it is. Every message is
    /// shown to the recogniser, identification on or off, so that it always knows which connections
    /// have had their login; only an active identification reads the identifier.
    /// </summary>
    /// <param name="message">Message being accepted, under the gate.</param>
    private void ObserveCharacter(DecodedMessage message)
    {
        Span<byte> identifier = stackalloc byte[CharacterLoginRecognizer.IdentifierBytes];
        try
        {
            var recognised = _logins.TryRecognise(message, identifier, out _);
            if (_logins.Saturated && !_saturationNoted)
            {
                _saturationNoted = true;
                Note(new CharacterIdentityNote("connection_limit_reached", CharacterLoginRecognizer.MaxConnections));
            }

            if (!recognised || !IdentificationActive)
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

            var digest = key.Digest(identifier);
            if (_loginDigests.Count >= MaxSessionLogins && !_loginDigests.ContainsKey(message.ConnectionKey))
            {
                _loginDigests.Clear();
            }

            _loginDigests[message.ConnectionKey] = digest;
            _lastLoginDigest = digest;
            var observation = _identities.Observe(_selection.Region, key.KeyId, digest);
            if (observation.IdentityId is null)
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
        finally
        {
            CryptographicOperations.ZeroMemory(identifier);
        }
    }

    /// <summary>
    /// Runs <paramref name="apply"/> - a live message or a replayed one being parsed - as matching
    /// from <paramref name="connection"/>, so a run it creates belongs to the character that logged in
    /// there.
    /// </summary>
    private void MatchingFrom(string? connection, Action apply)
    {
        var outer = _matchingConnection;
        _matchingConnection = connection;
        try
        {
            apply();
        }
        finally
        {
            _matchingConnection = outer;
        }
    }

    /// <summary>
    /// The character a run being created belongs to, read and - when it was never seen - recorded
    /// inside the transaction that creates the run.
    ///
    /// It is the character that logged in on the connection the matching message came on, whether
    /// the message is parsed live or replayed. Null - the run goes to the current profile - when
    /// nobody is being told apart. When no login message has been recognised in the session at all
    /// the current profile is used as well, unless the character seen last on this machine is not
    /// that profile's (CharacterIdentityService.CurrentProfileIsInDoubt): then the run is assigned to
    /// no profile. A run created from a connection without a login of this session, or from no
    /// message at all, belongs to somebody unknown: it is assigned to no profile rather than to the
    /// character played before it.
    /// </summary>
    /// <param name="transaction">The processor's transaction.</param>
    private CharacterIdentityObservation? IdentityAtMatch(SqliteTransaction transaction)
    {
        // The key was read when the session began; it is not read here, inside somebody else's
        // transaction.
        if (!IdentificationActive || _identityKey is null)
        {
            return null;
        }

        if (_lastLoginDigest is null)
        {
            if (!_loginMissingNoted)
            {
                _loginMissingNoted = true;
                Note(new CharacterIdentityNote("login_not_recognised"));
            }

            return _identities.CurrentProfileIsInDoubt(_selection.Region, transaction)
                ? new CharacterIdentityObservation(null, null, StatisticsChanged: false)
                : null;
        }

        if (_matchingConnection is not { } connection || !_loginDigests.TryGetValue(connection, out var digest))
        {
            Note(new CharacterIdentityNote("character_unknown_at_match"));
            return new CharacterIdentityObservation(null, null, StatisticsChanged: false);
        }

        // A replayed match may be an earlier character's: the session's latest login is who plays now.
        var observation = _identities.Observe(
            _selection.Region,
            _identityKey.KeyId,
            digest,
            transaction,
            loggedInNow: digest == _lastLoginDigest);
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
