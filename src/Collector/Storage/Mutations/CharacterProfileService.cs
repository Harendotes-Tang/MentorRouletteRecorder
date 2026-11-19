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
/// The player's changes to the character profiles: adding one, renaming one, choosing the one newly
/// recorded runs are filed under, and removing one nothing belongs to.
///
/// Each request is idempotent by <c>request_id</c>, like every other write a person can cause. The
/// goal and baseline of a profile are changed through
/// <see cref="RunMutationService.UpdateAchievementBaseline"/>, which keeps their history.
/// </summary>
public sealed class CharacterProfileService
{
    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly IdempotencyRepository _idempotency;

    /// <summary>Creates the service over an open database.</summary>
    /// <param name="database">Open database; the single writer.</param>
    /// <param name="settings">Settings repository holding the profiles.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    public CharacterProfileService(SqliteDatabase database, SettingsRepository settings, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _settings = settings;
        _idempotency = new IdempotencyRepository(database, clock);
    }

    /// <summary>
    /// The profile a newly created run of <paramref name="region"/> is filed under: the one the
    /// caller named, which must belong to that region, or the region's current profile.
    /// </summary>
    /// <param name="profiles">Profile repository.</param>
    /// <param name="region">Region of the run.</param>
    /// <param name="requested">Profile the caller named, or null for the current one.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public static long Resolve(
        CharacterProfileRepository profiles, Region region, long? requested, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (requested is not { } profileId)
        {
            return profiles.EnsureCurrent(region, transaction).ProfileId;
        }

        var profile = profiles.Find(profileId, transaction) ?? throw NotFound(profileId);
        if (profile.Region != region)
        {
            throw CollectorException.BadRequest(CollectorTexts.ProfileRegionMismatch(), "profile_id");
        }

        return profile.ProfileId;
    }

    /// <summary>Adds a profile to a region.</summary>
    /// <param name="command">Validated request.</param>
    public CharacterProfileOutcome Create(CreateCharacterProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = RequireName(command.DisplayName);
        if (command.GoalCount < 1)
        {
            throw CollectorException.BadRequest(CollectorTexts.BaselineGoalAtLeastOne(), "goal_count");
        }

        if (command.BaselineCompletedCount < 0)
        {
            throw CollectorException.BadRequest(
                CollectorTexts.BaselineCompletedNegative(), "baseline_completed_count");
        }

        // A request without an identity keeps the fingerprint it had before identities existed.
        var parts = new List<string?>
        {
            "CreateCharacterProfile",
            EnumWire<Region>.Format(command.Region),
            name,
            command.GoalCount.ToString(CultureInfo.InvariantCulture),
            command.BaselineCompletedCount.ToString(CultureInfo.InvariantCulture),
            command.MakeCurrent ? "1" : "0",
        };
        if (command.IdentityId is { } named)
        {
            parts.Add("identity");
            parts.Add(named.ToString(CultureInfo.InvariantCulture));
        }

        var fingerprint = MutationSnapshotCodec.Fingerprint(parts.ToArray());

        return Apply(command.RequestId, "CreateCharacterProfile", fingerprint, tx =>
        {
            // The region's own profile exists before a second one is added, so the records made so
            // far keep an owner.
            _settings.Profiles.EnsureCurrent(command.Region, tx);
            var existing = _settings.Profiles.List(tx, command.Region);
            if (existing.Count >= CharacterProfile.MaxProfilesPerRegion)
            {
                throw CollectorException.BadRequest(
                    CollectorTexts.ProfileLimitReached(CharacterProfile.MaxProfilesPerRegion), "region");
            }

            RequireFreeName(existing, name, exceptProfileId: null);
            // Checked before the profile is written, so a refusal names the identity, not a rollback.
            var identity = command.IdentityId is { } identityId
                ? CharacterIdentityService.Require(_settings, identityId, tx)
                : null;
            if (identity is not null && identity.Region != command.Region)
            {
                throw CollectorException.BadRequest(CollectorTexts.ProfileRegionMismatch(), "identity_id");
            }

            if (identity is { State: CharacterIdentityState.Bound })
            {
                throw CollectorException.BadRequest(CollectorTexts.IdentityAlreadyBound(), "identity_id");
            }

            var created = _settings.Profiles.Insert(
                command.Region, name, command.GoalCount, command.BaselineCompletedCount, command.MakeCurrent, tx);
            if (identity is null)
            {
                return created;
            }

            // The new profile takes the runs recorded for the character. Whether it becomes the
            // current one is decided by who is logged in, not here (CharacterIdentityService.Bind).
            CharacterIdentityService.Bind(_settings, identity, created.ProfileId, tx);
            return _settings.Profiles.Find(created.ProfileId, tx)!;
        });
    }

    /// <summary>Renames a profile, makes it the current one of its region, or both.</summary>
    /// <param name="command">Validated request.</param>
    public CharacterProfileOutcome Update(UpdateCharacterProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = command.DisplayName is null ? null : RequireName(command.DisplayName);
        var parts = new List<string?>
        {
            "UpdateCharacterProfile",
            command.ProfileId.ToString(CultureInfo.InvariantCulture),
            name,
            command.MakeCurrent ? "1" : "0",
        };
        if (command.ForgetIdentities)
        {
            parts.Add("forget_identities");
        }

        var fingerprint = MutationSnapshotCodec.Fingerprint(parts.ToArray());

        return Apply(command.RequestId, "UpdateCharacterProfile", fingerprint, tx =>
        {
            var profile = _settings.Profiles.Find(command.ProfileId, tx) ?? throw NotFound(command.ProfileId);
            if (name is not null && !string.Equals(name, profile.DisplayName, StringComparison.Ordinal))
            {
                RequireFreeName(_settings.Profiles.List(tx, profile.Region), name, profile.ProfileId);
                _settings.Profiles.Rename(profile.ProfileId, name, tx);
                // Naming an upgraded profile here answers its one-time registration as well.
                _settings.Identities.FinishRegistration(profile.ProfileId, tx);
            }

            if (command.MakeCurrent && !profile.IsCurrent)
            {
                _settings.Profiles.SetCurrent(profile.ProfileId, tx);
            }

            if (command.ForgetIdentities)
            {
                // The characters are asked about again; what they recorded stays where it is.
                _settings.Identities.ReleaseProfile(profile.ProfileId, tx);
            }

            return _settings.Profiles.Find(profile.ProfileId, tx)!;
        });
    }

    /// <summary>
    /// Removes a profile. Refused for the last profile of a region, for the current one, and while
    /// any run - the recycle bin included - still belongs to it: the player moves those first, so no
    /// record loses its owner without a revision saying so.
    /// </summary>
    /// <param name="command">Validated request.</param>
    public CharacterProfileOutcome Delete(DeleteCharacterProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "DeleteCharacterProfile", command.ProfileId.ToString(CultureInfo.InvariantCulture));

        return Apply(command.RequestId, "DeleteCharacterProfile", fingerprint, tx =>
        {
            var profile = _settings.Profiles.Find(command.ProfileId, tx) ?? throw NotFound(command.ProfileId);
            if (_settings.Profiles.List(tx, profile.Region).Count <= 1)
            {
                throw CollectorException.BadRequest(CollectorTexts.ProfileDeleteLast(), "profile_id");
            }

            if (profile.IsCurrent)
            {
                throw CollectorException.BadRequest(CollectorTexts.ProfileDeleteCurrent(), "profile_id");
            }

            var runs = _settings.Profiles.CountRuns(profile.ProfileId, tx);
            if (runs > 0)
            {
                throw new CollectorException(
                    ErrorCodes.BadRequest,
                    CollectorTexts.ProfileDeleteHasRuns(runs),
                    new Dictionary<string, object?> { ["profile_id"] = profile.ProfileId, ["run_count"] = runs },
                    field: "profile_id");
            }

            // The history goes with the profile; its request ids stay refused for a late replay.
            _idempotency.RememberBaselineHistory(_settings.ReadBaselineAudit(profile, tx), tx);
            _settings.DeleteBaselineAudit(profile.ProfileId, tx);
            // A character that was this profile is nobody's now, and is asked about again.
            _settings.Identities.ReleaseProfile(profile.ProfileId, tx);
            _settings.Profiles.Delete(profile.ProfileId, tx);
            return profile;
        }, deleted: true);
    }

    private CharacterProfileOutcome Apply(
        string requestId,
        string messageType,
        string fingerprint,
        Func<SqliteTransaction, CharacterProfile> work,
        bool deleted = false)
    {
        if (!Guid.TryParseExact(requestId, "D", out _))
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotUuid("request_id"), "request_id");
        }

        return _database.RunInTransaction(tx =>
        {
            if (_idempotency.TryGetResponse(requestId, tx) is { } stored)
            {
                var receipt = MutationSnapshotCodec.DeserializeOther<ProfileReceipt>(stored);
                if (receipt?.ProfileFingerprint is null ||
                    !string.Equals(receipt.ProfileFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new CollectorException(
                        ErrorCodes.IdempotencyConflict,
                        CollectorTexts.ProfileRequestReused(),
                        new Dictionary<string, object?> { ["conflict"] = "idempotency" },
                        field: "request_id");
                }

                // The answer is the profile as it stands now, not as the first answer described it:
                // the client reads its lists again either way.
                var current = receipt.Deleted ? null : _settings.Profiles.Find(receipt.ProfileId, tx);
                return new CharacterProfileOutcome(current, receipt.ProfileId, receipt.Region, IdempotentReplay: true);
            }

            if (_idempotency.WasAppliedBeforePrune(requestId, tx))
            {
                throw CollectorException.ExpiredIdempotencyResponse(requestId);
            }

            var profile = work(tx);
            _idempotency.Store(
                requestId,
                messageType,
                MutationSnapshotCodec.SerializeOther(
                    new ProfileReceipt(fingerprint, profile.ProfileId, profile.Region, deleted)),
                tx);
            return new CharacterProfileOutcome(
                deleted ? null : profile, profile.ProfileId, profile.Region, IdempotentReplay: false);
        });
    }

    /// <summary>
    /// A usable name: trimmed, 1 to <see cref="CharacterProfile.MaxDisplayNameLength"/> characters,
    /// one line, no control characters.
    /// </summary>
    internal static string RequireName(string? displayName)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > CharacterProfile.MaxDisplayNameLength || name.Any(char.IsControl))
        {
            throw CollectorException.BadRequest(
                CollectorTexts.ProfileNameRequired(CharacterProfile.MaxDisplayNameLength), "display_name");
        }

        return name;
    }

    internal static void RequireFreeName(
        IReadOnlyList<CharacterProfile> profiles, string name, long? exceptProfileId)
    {
        if (profiles.Any(profile => profile.ProfileId != exceptProfileId &&
                string.Equals(profile.DisplayName, name, StringComparison.Ordinal)))
        {
            throw CollectorException.BadRequest(CollectorTexts.ProfileNameTaken(), "display_name");
        }
    }

    private static CollectorException NotFound(long profileId) => new(
        ErrorCodes.NotFound,
        CollectorTexts.ProfileNotFound(),
        new Dictionary<string, object?> { ["profile_id"] = profileId },
        field: "profile_id");

    /// <summary>What <c>ipc_idempotency</c> keeps for one applied profile change.</summary>
    private sealed record ProfileReceipt(
        [property: JsonPropertyName("profile_fingerprint")] string? ProfileFingerprint,
        [property: JsonPropertyName("profile_id")] long ProfileId,
        [property: JsonPropertyName("region")] Region Region,
        [property: JsonPropertyName("deleted")] bool Deleted);
}
