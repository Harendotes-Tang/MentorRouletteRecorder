using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// Which profile a character seen in the game traffic is: what the Collector decides by itself when
/// a character logs in, and the player's answer for a character it could not decide about.
///
/// The rules (docs/state-machine.md section 3.1):
///
/// <list type="bullet">
///   <item><description>a character that is bound to a profile makes that profile the current one
///   of its region at login, and its runs are filed there;</description></item>
///   <item><description>the first character ever seen in a region that holds one profile is bound
///   to that profile without asking: a player with one character never hears of any of this;
///   </description></item>
///   <item><description>any other character seen for the first time waits for the player's answer.
///   Until then its runs are assigned to no profile - never to the character played before it -
///   and the answer files exactly those runs.</description></item>
/// </list>
/// </summary>
public sealed class CharacterIdentityService
{
    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly IdempotencyRepository _idempotency;

    /// <summary>Creates the service over an open database.</summary>
    /// <param name="database">Open database; the single writer.</param>
    /// <param name="settings">Settings repository holding the profiles and identities.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    public CharacterIdentityService(SqliteDatabase database, SettingsRepository settings, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _settings = settings;
        _idempotency = new IdempotencyRepository(database, clock);
    }

    /// <summary>
    /// Makes the profile of a known character the current one, and records nothing: what the
    /// pipeline asks after the player has answered about a character, to switch to it only when it
    /// is the one logged in. A character never seen comes back with no identity.
    /// </summary>
    /// <param name="region">Region the character is logged in to.</param>
    /// <param name="keyId">Fingerprint of the key the digest was made with.</param>
    /// <param name="digest">Keyed digest of the character's identifier.</param>
    public CharacterIdentityObservation Recognise(Region region, string keyId, string digest)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentException.ThrowIfNullOrEmpty(digest);

        return _database.RunInTransaction(tx =>
            _settings.Identities.FindByDigest(region, keyId, digest, tx) is { } known
                ? Returning(known, tx)
                : new CharacterIdentityObservation(null, null, StatisticsChanged: false));
    }

    /// <summary>
    /// Records that a character is logged in - its login message has just arrived, or a run is
    /// being matched for it - and decides what the Collector can decide by itself.
    /// </summary>
    /// <param name="region">Region the character is logged in to.</param>
    /// <param name="keyId">Fingerprint of the key the digest was made with.</param>
    /// <param name="digest">Keyed digest of the character's identifier.</param>
    public CharacterIdentityObservation Observe(Region region, string keyId, string digest) =>
        _database.RunInTransaction(tx => Observe(region, keyId, digest, tx));

    /// <summary>
    /// <see cref="Observe(Region, string, string)"/> inside the transaction that creates a run, so
    /// the run and the identity it is stamped with are written together.
    /// </summary>
    /// <param name="region">Region the character is logged in to.</param>
    /// <param name="keyId">Fingerprint of the key the digest was made with.</param>
    /// <param name="digest">Keyed digest of the character's identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterIdentityObservation Observe(Region region, string keyId, string digest, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentNullException.ThrowIfNull(transaction);

        if (_settings.Identities.FindByDigest(region, keyId, digest, transaction) is { } known)
        {
            return Returning(known, transaction);
        }

        var seen = _settings.Identities.List(transaction, region);
        if (seen.Count >= CharacterIdentity.MaxIdentitiesPerRegion)
        {
            // Somebody's character all the same, so not the current profile's: no owner.
            return new CharacterIdentityObservation(null, null, StatisticsChanged: false);
        }

        if (seen.Count == 0 && _settings.Profiles.List(transaction, region).Count <= 1)
        {
            // A player with one character is never held back by a question (owner decision
            // 2026-11-19, audit K1-1): the first character is that profile's. An upgraded profile
            // is still offered its one-time naming, which the player may leave unanswered.
            var only = _settings.Profiles.EnsureCurrent(region, transaction);
            var first = _settings.Identities.Insert(region, keyId, digest, only.ProfileId, transaction);
            _settings.Identities.OfferRegistration(only.ProfileId, first.IdentityId, transaction);
            return new CharacterIdentityObservation(first.IdentityId, only.ProfileId, StatisticsChanged: true);
        }

        var pending = _settings.Identities.Insert(region, keyId, digest, null, transaction);
        _settings.Identities.OfferUnboundRegistration(region, pending.IdentityId, transaction);
        return new CharacterIdentityObservation(pending.IdentityId, null, StatisticsChanged: true);
    }

    /// <summary>
    /// Whether the region's current profile may not be the character playing, for a session that
    /// recognised no login message and so cannot say who it is.
    ///
    /// There is no doubt while the region has one profile, or while no character was ever
    /// recognised in it. Otherwise the current profile is trusted only when the character seen
    /// last on this machine is bound to it: a character still waiting for an answer, one that is
    /// not counted, or one bound to another profile was the last to play, and the current profile
    /// is then somebody else's (owner decision 2026-11-19, audit D1-1).
    /// </summary>
    /// <param name="region">Region the session records.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public bool CurrentProfileIsInDoubt(Region region, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (_settings.Profiles.List(transaction, region).Count <= 1)
        {
            return false;
        }

        var seen = _settings.Identities.List(transaction, region);
        if (seen.Count == 0)
        {
            return false;
        }

        var current = _settings.Profiles.FindCurrent(region, transaction)?.ProfileId;
        var latest = seen.Max(identity => identity.LastSeenAtUtc);
        // Several characters seen in the same instant: any of them being the current one is enough.
        return !seen.Any(identity => identity.LastSeenAtUtc == latest && identity.ProfileId == current);
    }

    private CharacterIdentityObservation Returning(CharacterIdentity known, SqliteTransaction transaction)
    {
        _settings.Identities.Touch(known.IdentityId, transaction);
        if (known.ProfileId is not { } profileId)
        {
            return new CharacterIdentityObservation(known.IdentityId, null, StatisticsChanged: false);
        }

        var switched = _settings.Profiles.Find(profileId, transaction) is { IsCurrent: false };
        if (switched)
        {
            _settings.Profiles.SetCurrent(profileId, transaction);
        }

        var offered = known.RegistrationProfileId is null &&
            _settings.Identities.OfferRegistration(profileId, known.IdentityId, transaction);
        return new CharacterIdentityObservation(known.IdentityId, profileId, switched || offered);
    }

    /// <summary>
    /// Drops the identities written under a key that is gone. Called when the key in use is known,
    /// before anything is compared with it.
    /// </summary>
    /// <param name="keyId">Fingerprint of the key in use.</param>
    /// <returns>How many identities were removed.</returns>
    public int ForgetOtherKeys(string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        return _database.RunInTransaction(tx => _settings.Identities.DeleteUnderOtherKeys(keyId, tx));
    }

    /// <summary>Applies the player's answer about one identity.</summary>
    /// <param name="command">Validated request.</param>
    public CharacterIdentityOutcome Resolve(ResolveCharacterIdentityCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Guid.TryParseExact(command.RequestId, "D", out _))
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotUuid("request_id"), "request_id");
        }

        if (command.Action == CharacterIdentityAction.Bind && command.ProfileId is null)
        {
            throw CollectorException.BadRequest(CollectorTexts.IdentityProfileRequired(), "profile_id");
        }

        if (command.DisplayName is not null && command.Action != CharacterIdentityAction.Bind)
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldUnknown("display_name"), "display_name");
        }

        var name = command.DisplayName is null ? null : CharacterProfileService.RequireName(command.DisplayName);
        var parts = new List<string?>
        {
            "ResolveCharacterIdentity",
            command.IdentityId.ToString(CultureInfo.InvariantCulture),
            EnumWire<CharacterIdentityAction>.Format(command.Action),
            command.ProfileId?.ToString(CultureInfo.InvariantCulture),
        };
        if (name is not null)
        {
            parts.Add(name);
        }

        var fingerprint = MutationSnapshotCodec.Fingerprint(parts.ToArray());

        return _database.RunInTransaction(tx =>
        {
            if (_idempotency.TryGetResponse(command.RequestId, tx) is { } stored)
            {
                var receipt = MutationSnapshotCodec.DeserializeOther<IdentityReceipt>(stored);
                if (receipt?.IdentityFingerprint is null ||
                    !string.Equals(receipt.IdentityFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new CollectorException(
                        ErrorCodes.IdempotencyConflict,
                        CollectorTexts.IdentityRequestReused(),
                        new Dictionary<string, object?> { ["conflict"] = "idempotency" },
                        field: "request_id");
                }

                return new CharacterIdentityOutcome(
                    receipt.IdentityId, receipt.ProfileId, receipt.AssignedRunCount, IdempotentReplay: true);
            }

            if (_idempotency.WasAppliedBeforePrune(command.RequestId, tx))
            {
                throw CollectorException.ExpiredIdempotencyResponse(command.RequestId);
            }

            var identity = _settings.Identities.Find(command.IdentityId, tx) ?? throw NotFound(command.IdentityId);
            var assigned = 0;
            switch (command.Action)
            {
                case CharacterIdentityAction.Bind:
                    assigned = name is null
                        ? Bind(_settings, identity, command.ProfileId!.Value, tx)
                        : ConfirmRegistration(identity, command.ProfileId!.Value, name, tx);
                    break;
                case CharacterIdentityAction.Ignore:
                    RequireUnbound(identity);
                    _settings.Identities.SetAnswer(identity.IdentityId, null, ignored: true, tx);
                    break;
                default:
                    if (identity.State != CharacterIdentityState.Ignored)
                    {
                        throw CollectorException.BadRequest(CollectorTexts.IdentityNotIgnored(), "identity_id");
                    }

                    _settings.Identities.SetAnswer(identity.IdentityId, null, ignored: false, tx);
                    break;
            }

            var profileId = command.Action == CharacterIdentityAction.Bind ? command.ProfileId : null;
            _idempotency.Store(
                command.RequestId,
                "ResolveCharacterIdentity",
                MutationSnapshotCodec.SerializeOther(
                    new IdentityReceipt(fingerprint, identity.IdentityId, profileId, assigned)),
                tx);
            return new CharacterIdentityOutcome(identity.IdentityId, profileId, assigned, IdempotentReplay: false);
        });
    }

    private int ConfirmRegistration(CharacterIdentity identity, long profileId, string name, SqliteTransaction tx)
    {
        if (identity.RegistrationProfileId != profileId ||
            identity.Ignored || (identity.ProfileId is { } boundId && boundId != profileId))
        {
            throw CollectorException.BadRequest(CollectorTexts.ProfileNotFound(), "profile_id");
        }

        CharacterProfileService.RequireFreeName(_settings.Profiles.List(tx, identity.Region), name, profileId);
        var assigned = identity.ProfileId is null ? Bind(_settings, identity, profileId, tx) : 0;
        _settings.Profiles.Rename(profileId, name, tx);
        _settings.Identities.FinishRegistration(profileId, tx);
        return assigned;
    }

    /// <summary>
    /// Binds an identity that has no profile to one of its own region and files the runs recorded
    /// for it that have no owner. Which profile is current does not change here: the answer may be
    /// about a character that is not the one logged in, and the profile of the one that is becomes
    /// current through <see cref="Recognise"/> (<c>LiveProtocolPipeline.ReconsiderCharacter</c>).
    /// </summary>
    /// <param name="settings">Settings repository holding the profiles and identities.</param>
    /// <param name="identity">The identity as stored.</param>
    /// <param name="profileId">Profile it is.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>How many runs moved to the profile.</returns>
    public static int Bind(
        SettingsRepository settings, CharacterIdentity identity, long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identity);
        RequireUnbound(identity);

        var profile = settings.Profiles.Find(profileId, transaction) ?? throw new CollectorException(
            ErrorCodes.NotFound,
            CollectorTexts.ProfileNotFound(),
            new Dictionary<string, object?> { ["profile_id"] = profileId },
            field: "profile_id");
        if (profile.Region != identity.Region)
        {
            throw CollectorException.BadRequest(CollectorTexts.ProfileRegionMismatch(), "profile_id");
        }

        settings.Identities.SetAnswer(identity.IdentityId, profile.ProfileId, ignored: false, transaction);
        return settings.Identities.AssignUnownedRuns(identity.IdentityId, profile.ProfileId, transaction);
    }

    /// <summary>The identity a request names, which must exist.</summary>
    /// <param name="settings">Settings repository holding the identities.</param>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public static CharacterIdentity Require(SettingsRepository settings, long identityId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Identities.Find(identityId, transaction) ?? throw NotFound(identityId);
    }

    private static void RequireUnbound(CharacterIdentity identity)
    {
        if (identity.State == CharacterIdentityState.Bound)
        {
            throw CollectorException.BadRequest(CollectorTexts.IdentityAlreadyBound(), "identity_id");
        }
    }

    private static CollectorException NotFound(long identityId) => new(
        ErrorCodes.NotFound,
        CollectorTexts.IdentityNotFound(),
        new Dictionary<string, object?> { ["identity_id"] = identityId },
        field: "identity_id");

    /// <summary>What <c>ipc_idempotency</c> keeps for one applied answer.</summary>
    private sealed record IdentityReceipt(
        [property: JsonPropertyName("identity_fingerprint")] string? IdentityFingerprint,
        [property: JsonPropertyName("identity_id")] long IdentityId,
        [property: JsonPropertyName("profile_id")] long? ProfileId,
        [property: JsonPropertyName("assigned_run_count")] int AssignedRunCount);
}
