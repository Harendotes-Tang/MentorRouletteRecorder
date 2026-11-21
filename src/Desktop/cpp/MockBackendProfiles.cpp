// ---------------------------------------------------------------------------
// 角色档案 (character profiles) in the offline backend.
//
// The rules are the Collector's (CharacterProfileService, RunMutationService.AssignProfile,
// StatisticsRepository.GetProfiles): every region holds at least one profile and exactly
// one current one, under which new runs are filed; a profile's progress is its own
// baseline plus the confirmed, contributing completions filed under it; a run assigned to
// no profile counts for none. Ids are never reused.
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include "IpcFraming.h"
#include "MockData.h"

#include <QHash>
#include <QJsonArray>
#include <QSet>
#include <QTimer>

#include <algorithm>
#include <limits>

using namespace mr::mock;

namespace mr {
namespace {

/// CharacterProfile.MaxProfilesPerRegion / MaxDisplayNameLength.
constexpr int kMaxProfilesPerRegion = 16;
constexpr int kMaxDisplayNameLength = 24;

bool countsTowardsProgress(const QJsonObject &run)
{
    return !isSoftDeleted(run) && isConfirmedMentor(run)
           && run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED")
           && run.value(QStringLiteral("contributes_to_goal")).toBool(true);
}

/// CharacterProfileService.RequireName: trimmed, 1 to 24 characters, no control character.
bool usableName(const QString &name)
{
    if (name.isEmpty() || name.size() > kMaxDisplayNameLength)
        return false;
    return std::ranges::none_of(name, [](QChar c) { return c.category() == QChar::Other_Control; });
}

/// RunRepository.MigrationIdentity: what a region move compares. An empty time is a missing one.
struct MigrationIdentity
{
    QJsonValue runId;
    QJsonValue contentId;
    QString dutyName;
    QJsonValue jobId;
    QString matched;
    QString entered;
    QString source;
};

MigrationIdentity migrationIdentity(const QJsonObject &run)
{
    const auto time = [](const QJsonValue &value) { return value.isString() ? value.toString() : QString(); };
    return {run.value(QStringLiteral("run_id")), run.value(QStringLiteral("content_id")),
            run.value(QStringLiteral("duty_name")).toString(), run.value(QStringLiteral("job_id")),
            time(run.value(QStringLiteral("matched_at_utc"))), time(run.value(QStringLiteral("entered_at_utc"))),
            time(run.value(QStringLiteral("import_metadata")).toObject()
                     .value(QStringLiteral("source_recorded_at_utc")))};
}

/// MigrationIdentity.Keys: the content id (the duty name only when there is none), the job,
/// and one key per known time: matched, entered and the import's source time.
QStringList migrationKeys(const MigrationIdentity &identity)
{
    const bool hasContent = identity.contentId.isDouble();
    if (!identity.jobId.isDouble() || (!hasContent && identity.dutyName.trimmed().isEmpty()))
        return {};
    const QChar separator(0x1f);
    const QString prefix = (hasContent ? QStringLiteral("c%1").arg(identity.contentId.toInt())
                                       : QStringLiteral("n") + identity.dutyName)
        + separator + QString::number(identity.jobId.toInt()) + separator;
    QStringList keys;
    if (!identity.matched.isEmpty())
        keys.append(prefix + QStringLiteral("matched") + separator + identity.matched);
    if (!identity.entered.isEmpty())
        keys.append(prefix + QStringLiteral("entered") + separator + identity.entered);
    if (!identity.source.isEmpty())
        keys.append(prefix + QStringLiteral("source") + separator + identity.source);
    return keys;
}

/// MigrationIdentity.CompatibleWith: no match or entry time that both know may differ.
bool migrationCompatible(const MigrationIdentity &left, const MigrationIdentity &right)
{
    const auto agree = [](const QString &a, const QString &b) { return a.isEmpty() || b.isEmpty() || a == b; };
    return agree(left.matched, right.matched) && agree(left.entered, right.entered);
}

} // namespace

bool MockBackend::isCharacterProfileMessage(const QString &messageType)
{
    return messageType == QLatin1String("CreateCharacterProfile")
           || messageType == QLatin1String("UpdateCharacterProfile")
           || messageType == QLatin1String("DeleteCharacterProfile");
}

int MockBackend::ensureCurrentProfile(const QString &region)
{
    if (const CharacterProfileEntry *current = currentProfile(region))
        return current->id;
    CharacterProfileEntry profile;
    profile.id = m_nextProfileId++;
    profile.region = region;
    profile.current = true;
    profile.settings.baselineEffectiveAt = m_now.toUTC();
    profile.settings.updatedAt = profile.settings.baselineEffectiveAt;
    m_profiles.append(profile);
    return profile.id;
}

const MockBackend::CharacterProfileEntry *MockBackend::findProfile(int profileId) const
{
    for (const CharacterProfileEntry &profile : m_profiles) {
        if (profile.id == profileId)
            return &profile;
    }
    return nullptr;
}

MockBackend::CharacterProfileEntry *MockBackend::findProfile(int profileId)
{
    for (CharacterProfileEntry &profile : m_profiles) {
        if (profile.id == profileId)
            return &profile;
    }
    return nullptr;
}

bool MockBackend::profileNameTaken(const QString &region, const QString &name, int exceptId) const
{
    return std::ranges::any_of(m_profiles, [&](const CharacterProfileEntry &profile) {
        return profile.region == region && profile.id != exceptId && profile.displayName == name;
    });
}

bool MockBackend::applyProfileName(CharacterProfileEntry *profile, const QJsonValue &requestedName,
                                   QString *errorCode, QString *errorMessage)
{
    const QString name = requestedName.toString().trimmed();
    if (!requestedName.isUndefined() && !usableName(name)) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "profile.name_required",
                   {{QStringLiteral("max"), kMaxDisplayNameLength}});
        return false;
    }
    if (!profile) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        return false;
    }
    if (requestedName.isUndefined() || name == profile->displayName)
        return true;
    if (profileNameTaken(profile->region, name, profile->id)) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "profile.name_taken");
        return false;
    }
    profile->displayName = name;
    return true;
}

const MockBackend::CharacterProfileEntry *MockBackend::currentProfile(const QString &region) const
{
    for (const CharacterProfileEntry &profile : m_profiles) {
        if (profile.region == region && profile.current)
            return &profile;
    }
    return nullptr;
}

QList<const MockBackend::CharacterProfileEntry *> MockBackend::profilesOf(const QString &region) const
{
    QList<const CharacterProfileEntry *> out;
    for (const CharacterProfileEntry &profile : m_profiles) {
        if (profile.region == region)
            out.append(&profile);
    }
    return out;
}

int MockBackend::runProfileId(const QJsonObject &run) const
{
    const QJsonValue owner = run.value(QStringLiteral("profile_id"));
    if (owner.isDouble())
        return owner.toInt();
    if (!owner.isUndefined())
        return 0;
    // A hand-written fixture run without the field: its region's first profile, as the
    // schema-14 migration filed every older record.
    const auto ofRegion = profilesOf(runRegion(run));
    return ofRegion.isEmpty() ? 0 : ofRegion.first()->id;
}

int MockBackend::unassignedRunCount(const QString &region) const
{
    int count = 0;
    for (const auto &value : m_runs) {
        const QJsonObject run = value.toObject();
        if (runRegion(run) == region && !isSoftDeleted(run) && runProfileId(run) == 0)
            ++count;
    }
    return count;
}

QJsonObject MockBackend::profileJson(const CharacterProfileEntry &profile) const
{
    int completed = 0;
    int runs = 0;
    for (const auto &value : m_runs) {
        const QJsonObject run = value.toObject();
        if (runProfileId(run) != profile.id)
            continue;
        if (!isSoftDeleted(run))
            ++runs;
        if (countsTowardsProgress(run))
            ++completed;
    }
    const int baseline = profile.settings.baselineCompletedCount;
    const int progress = static_cast<int>(qMin<qint64>(std::numeric_limits<int>::max(),
                                                       static_cast<qint64>(baseline) + completed));
    return {{QStringLiteral("profile_id"), profile.id},
            {QStringLiteral("region"), profile.region},
            {QStringLiteral("display_name"), profile.displayName},
            {QStringLiteral("is_current"), profile.current},
            {QStringLiteral("goal_count"), profile.settings.goalCount},
            {QStringLiteral("baseline_completed_count"), baseline},
            {QStringLiteral("achievement_progress"), progress},
            {QStringLiteral("remaining"), qMax(profile.settings.goalCount - progress, 0)},
            {QStringLiteral("completed_count"), completed},
            {QStringLiteral("run_count"), runs},
            {QStringLiteral("identity_count"), boundIdentityCount(profile.id)}};
}

QJsonArray MockBackend::characterProfiles() const
{
    QList<const CharacterProfileEntry *> ordered;
    for (const CharacterProfileEntry &profile : m_profiles)
        ordered.append(&profile);
    // CN first, then TC, then the other regions; inside a region in creation order.
    std::ranges::stable_sort(ordered, [](const CharacterProfileEntry *left, const CharacterProfileEntry *right) {
        if (left->region != right->region)
            return regionBefore(left->region, right->region);
        return left->id < right->id;
    });
    QJsonArray out;
    for (const CharacterProfileEntry *profile : std::as_const(ordered))
        out.append(profileJson(*profile));
    return out;
}

bool MockBackend::runMatchesProfileFilter(const QJsonObject &run, const QJsonObject &filter) const
{
    const QJsonValue named = filter.value(QStringLiteral("profile_id"));
    if (named.isDouble() && runProfileId(run) != named.toInt())
        return false;
    if (filter.value(QStringLiteral("profile_unassigned")).toBool(false) && runProfileId(run) != 0)
        return false;
    return true;
}

bool MockBackend::refusedProfileFilter(const QJsonObject &filter, QString *errorCode, QString *errorMessage)
{
    const QJsonValue named = filter.value(QStringLiteral("profile_id"));
    if (!named.isUndefined() && !named.isNull() && (!named.isDouble() || named.toInt() < 1)) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "ipc.field.at_least",
                   {{QStringLiteral("field"), QStringLiteral("profile_id")}, {QStringLiteral("min"), 1}});
        return true;
    }
    if (named.isDouble() && filter.value(QStringLiteral("profile_unassigned")).toBool(false)) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "ipc.filter.profile_conflict");
        return true;
    }
    return false;
}

int MockBackend::resolveProfileForNewRun(const QString &region, const QJsonValue &requested,
                                         QString *errorCode, QString *errorMessage)
{
    if (requested.isUndefined() || requested.isNull())
        return ensureCurrentProfile(region);
    const CharacterProfileEntry *profile = requested.isDouble() ? findProfile(requested.toInt()) : nullptr;
    if (!profile) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        return 0;
    }
    if (profile->region != region) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "profile.region_mismatch");
        return 0;
    }
    return profile->id;
}

void MockBackend::emitStatsInvalidatedLater()
{
    QTimer::singleShot(0, this, [this] {
        QVariantMap event = liveEventEnvelope(QStringLiteral("DiagnosticsMessage"),
                                              QStringLiteral("stats_invalidated"));
        event.insert(QStringLiteral("severity"), QStringLiteral("INFO"));
        event.insert(QStringLiteral("message"), QString::fromUtf8("角色档案已更新，统计需要重新查询。"));
        Q_EMIT liveEvent(event);
    });
}

int MockBackend::addCharacterProfile(const QString &region, const QString &displayName, int goalCount,
                                     int baselineCompletedCount, bool makeCurrent)
{
    Q_ASSERT(isRegion(region));
    // The region's own profile exists before a second one is added, so its records keep an owner.
    ensureCurrentProfile(region);
    CharacterProfileEntry profile;
    profile.id = m_nextProfileId++;
    profile.region = region;
    profile.displayName = displayName;
    profile.settings.goalCount = goalCount;
    profile.settings.baselineCompletedCount = baselineCompletedCount;
    profile.settings.baselineEffectiveAt = QDateTime::currentDateTimeUtc();
    profile.settings.updatedAt = profile.settings.baselineEffectiveAt;
    if (makeCurrent) {
        for (CharacterProfileEntry &other : m_profiles) {
            if (other.region == region)
                other.current = false;
        }
        profile.current = true;
    }
    m_profiles.append(profile);
    return profile.id;
}

void MockBackend::setCharacterProfilesFixture()
{
    // The CN sample history belongs to the first character; a second one, current now,
    // took over some of the recent runs, and three runs belong to nobody yet.
    CharacterProfileEntry *first = findProfile(ensureCurrentProfile(QStringLiteral("CN")));
    first->displayName = QString::fromUtf8("晨星");
    const int second = addCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("白露"), 1000, 260, true);
    int unassigned = 0;
    for (qsizetype index = 0; index < m_runs.size(); ++index) {
        QJsonObject run = m_runs.at(index).toObject();
        if (runRegion(run) != QLatin1String("CN") || isSoftDeleted(run))
            continue;
        if (index % 4 == 1) {
            run.insert(QStringLiteral("profile_id"), second);
        } else if (index % 9 == 2 && unassigned < 3) {
            run.insert(QStringLiteral("profile_id"), QJsonValue(QJsonValue::Null));
            ++unassigned;
        }
        m_runs[index] = run;
    }
}

QJsonObject MockBackend::applyCharacterProfile(const QString &messageType, const QJsonObject &payload,
                                               QString *errorCode, QString *errorMessage,
                                               QJsonObject *errorDetails)
{
    const auto refuse = [&](const QString &code, const char *key, const QVariantMap &args = {}) {
        return refuseWith(errorCode, errorMessage, code, key, args);
    };
    const QString badRequest = QStringLiteral("ERR_BAD_REQUEST");
    const QVariantMap nameLimit{{QStringLiteral("max"), kMaxDisplayNameLength}};

    int profileId = 0;
    QString region;
    bool deleted = false;
    if (messageType == QLatin1String("CreateCharacterProfile")) {
        region = payload.value(QStringLiteral("region")).toString();
        if (!isRegion(region))
            return refuse(badRequest, "ipc.field.missing", {{QStringLiteral("field"), QStringLiteral("region")}});
        const QString name = payload.value(QStringLiteral("display_name")).toString().trimmed();
        if (!usableName(name))
            return refuse(badRequest, "profile.name_required", nameLimit);
        const int goal = payload.value(QStringLiteral("goal_count")).toInt(2000);
        const int baseline = payload.value(QStringLiteral("baseline_completed_count")).toInt(0);
        if (goal < 1)
            return refuse(badRequest, "baseline.goal_at_least_one");
        if (baseline < 0)
            return refuse(badRequest, "baseline.completed_negative");
        ensureCurrentProfile(region);
        if (profilesOf(region).size() >= kMaxProfilesPerRegion)
            return refuse(badRequest, "profile.limit_reached", {{QStringLiteral("max"), kMaxProfilesPerRegion}});
        if (profileNameTaken(region, name, 0))
            return refuse(badRequest, "profile.name_taken");
        bool identityRefused = false;
        CharacterIdentityEntry *identity =
            identityForNewProfile(payload, region, &identityRefused, errorCode, errorMessage);
        if (identityRefused)
            return {};
        profileId = addCharacterProfile(region, name, goal, baseline,
                                        payload.value(QStringLiteral("make_current")).toBool(false));
        // In the same transaction, exactly as ResolveCharacterIdentity BIND.
        if (identity)
            bindIdentity(*identity, profileId);
    } else if (messageType == QLatin1String("UpdateCharacterProfile")) {
        const QJsonValue requestedName = payload.value(QStringLiteral("display_name"));
        const bool makeCurrent = payload.value(QStringLiteral("make_current")).toBool(false);
        const bool forget = payload.value(QStringLiteral("forget_identities")).toBool(false);
        if (!payload.value(QStringLiteral("profile_id")).isDouble())
            return refuse(badRequest, "ipc.field.missing", {{QStringLiteral("field"), QStringLiteral("profile_id")}});
        if (requestedName.isUndefined() && !makeCurrent && !forget)
            return refuse(badRequest, "mutation.no_changes");
        CharacterProfileEntry *profile = findProfile(payload.value(QStringLiteral("profile_id")).toInt());
        if (!applyProfileName(profile, requestedName, errorCode, errorMessage))
            return {};
        if (makeCurrent && !profile->current) {
            for (CharacterProfileEntry &other : m_profiles) {
                if (other.region == profile->region)
                    other.current = other.id == profile->id;
            }
        }
        if (forget)
            forgetIdentitiesOf(profile->id);
        profileId = profile->id;
        region = profile->region;
    } else {
        const CharacterProfileEntry *profile = findProfile(payload.value(QStringLiteral("profile_id")).toInt(0));
        if (!profile)
            return refuse(QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        if (profilesOf(profile->region).size() <= 1)
            return refuse(badRequest, "profile.delete_last");
        if (profile->current)
            return refuse(badRequest, "profile.delete_current");
        // The recycle bin included: no record loses its owner without a revision.
        int runs = 0;
        for (const auto &value : m_runs) {
            if (runProfileId(value.toObject()) == profile->id)
                ++runs;
        }
        if (runs > 0) {
            *errorDetails = {{QStringLiteral("profile_id"), profile->id}, {QStringLiteral("run_count"), runs}};
            return refuse(badRequest, "profile.delete_has_runs", {{QStringLiteral("count"), runs}});
        }
        profileId = profile->id;
        region = profile->region;
        deleted = true;
        // A character bound to a profile that goes is asked about again.
        forgetIdentitiesOf(profileId);
        m_registrationCandidates.remove(profileId);
        m_profiles.removeIf([profileId](const CharacterProfileEntry &entry) { return entry.id == profileId; });
    }

    emitStatsInvalidatedLater();
    return {{QStringLiteral("profile_id"), profileId},
            {QStringLiteral("region"), region},
            {QStringLiteral("deleted"), deleted},
            {QStringLiteral("profiles"), characterProfiles()},
            {QStringLiteral("idempotent_replay"), false}};
}

QJsonObject MockBackend::assignRunsToProfile(const QJsonObject &payload, QString *errorCode,
                                             QString *errorMessage, QJsonObject *errorDetails, bool previewRegion)
{
    const bool moving = previewRegion || payload.value(QStringLiteral("action")).toString() == QLatin1String("move_region");
    const QString reason = payload.value(QStringLiteral("reason")).toString().trimmed();
    const QJsonArray targets = payload.value(QStringLiteral("runs")).toArray();
    const QJsonValue requested = payload.value(QStringLiteral("profile_id"));
    // "profile_id": null files the runs under no profile, so the field itself has to be there.
    if (requested.isUndefined() || (!requested.isNull() && !requested.isDouble()))
        return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "ipc.field.missing",
                          {{QStringLiteral("field"), QStringLiteral("profile_id")}});
    if ((!previewRegion && (reason.isEmpty() || reason.size() > 500)) || targets.isEmpty()
        || (!payload.contains(QStringLiteral("selection_id")) && targets.size() > kInlineHistorySelectionLimit)) {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = tr("批量操作、原因或记录数量无效。");
        return {};
    }
    QList<QJsonObject> runs;
    QSet<QString> seen;
    for (const auto &target : targets) {
        const QString id = target.toObject().value(QStringLiteral("run_id")).toString();
        const int index = indexOfRun(id);
        if (seen.contains(id) || index < 0) {
            *errorCode = index < 0 ? QStringLiteral("ERR_NOT_FOUND") : QStringLiteral("ERR_BAD_REQUEST");
            *errorMessage = tr("记录不存在或重复，本批未应用。");
            return {};
        }
        seen.insert(id);
        const QJsonObject run = m_runs.at(index).toObject();
        if (target.toObject().value(QStringLiteral("expected_revision")).toInt(-1)
            != run.value(QStringLiteral("revision")).toInt()) {
            *errorCode = QStringLiteral("ERR_REVISION_CONFLICT");
            *errorMessage = tr("记录已更新，请重新勾选，本批未应用。");
            return {};
        }
        runs.append(run);
    }
    const int target = requested.isDouble() ? requested.toInt() : 0;
    CharacterProfileEntry *profile = target == 0 ? nullptr : findProfile(target);
    const QString region = payload.value(QStringLiteral("region")).toString();
    int overlap = 0;
    if (moving) {
        const QString sourceRegion = runRegion(runs.first());
        if (region == sourceRegion || (region != QLatin1String("CN") && region != QLatin1String("GLOBAL")
                                      && region != QLatin1String("TC")))
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "history.region.different_target");
        if (!profile)
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        if (profile->region != region)
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "profile.region_mismatch");
        int contributing = 0;
        for (const QJsonObject &run : std::as_const(runs)) {
            const QString source = run.value(QStringLiteral("source")).toString();
            if (isSoftDeleted(run) || !run.value(QStringLiteral("capture_session_id")).toString().isEmpty()
                || (source != QLatin1String("IMPORT") && source != QLatin1String("MANUAL")))
                return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "history.region.ineligible");
            if (runRegion(run) != sourceRegion)
                return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "history.region.mixed_source");
            if (countsTowardsProgress(run))
                ++contributing;
        }
        // RunRepository.FindRegionMigrationDuplicate: twice in the batch first, then already in
        // the target (under its profile or under nobody), by the same keys and compatibility.
        const auto refuseDuplicate = [&](const QJsonValue &runId, const QJsonValue &matchingRunId) {
            *errorDetails = {{QStringLiteral("run_id"), runId}, {QStringLiteral("matching_run_id"), matchingRunId}};
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "history.region.duplicate");
        };
        QHash<QString, QList<MigrationIdentity>> index;
        for (const QJsonObject &run : std::as_const(runs)) {
            const MigrationIdentity identity = migrationIdentity(run);
            for (const QString &key : migrationKeys(identity)) {
                QList<MigrationIdentity> &matches = index[key];
                for (const MigrationIdentity &earlier : std::as_const(matches)) {
                    if (migrationCompatible(identity, earlier))
                        return refuseDuplicate(identity.runId, earlier.runId);
                }
                matches.append(identity);
            }
        }
        for (const auto &value : std::as_const(m_runs)) {
            const QJsonObject other = value.toObject();
            const int owner = runProfileId(other);
            if (index.isEmpty() || runRegion(other) != region || (owner != 0 && owner != profile->id))
                continue;
            const MigrationIdentity identity = migrationIdentity(other);
            for (const QString &key : migrationKeys(identity)) {
                for (const MigrationIdentity &selected : index.value(key)) {
                    if (migrationCompatible(identity, selected))
                        return refuseDuplicate(selected.runId, identity.runId);
                }
            }
        }
        const int baseline = profile->settings.baselineCompletedCount;
        if (previewRegion) {
            return {{QStringLiteral("source_region"), sourceRegion},
                    {QStringLiteral("contributing_count"), contributing},
                    {QStringLiteral("target_baseline"), baseline},
                    {QStringLiteral("target_completed_count"), profileJson(*profile).value(QStringLiteral("completed_count"))}};
        }
        overlap = payload.value(QStringLiteral("baseline_overlap")).toInt(-1);
        if (payload.value(QStringLiteral("expected_baseline")).toInt(-1) != baseline)
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_REVISION_CONFLICT"), "history.region.baseline_changed");
        if (overlap < 0 || overlap > qMin(baseline, contributing))
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "history.region.invalid_overlap");
    } else if (target != 0) {
        if (!profile)
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        for (const QJsonObject &run : std::as_const(runs)) {
            if (runRegion(run) != profile->region) {
                *errorDetails = {{QStringLiteral("run_id"), run.value(QStringLiteral("run_id"))}};
                return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"),
                                  "profile.region_mismatch");
            }
        }
    }
    // Live and deleted runs may both move; a run already there is left as it is.
    std::ranges::sort(runs, [](const QJsonObject &left, const QJsonObject &right) {
        return left.value(QStringLiteral("run_id")).toString() < right.value(QStringLiteral("run_id")).toString();
    });
    QJsonArray moved;
    for (const QJsonObject &before : std::as_const(runs)) {
        const int owner = runProfileId(before);
        if (owner == target)
            continue;
        QJsonObject run = before;
        const QJsonValue next = target > 0 ? QJsonValue(target) : QJsonValue(QJsonValue::Null);
        run.insert(QStringLiteral("profile_id"), next);
        QJsonArray changes{QJsonObject{{QStringLiteral("field"), QStringLiteral("profile_id")},
                                     {QStringLiteral("old_value"), owner > 0 ? QJsonValue(owner) : QJsonValue(QJsonValue::Null)},
                                     {QStringLiteral("new_value"), next}}};
        if (moving) {
            run.insert(QStringLiteral("region"), region);
            changes.append(QJsonObject{{QStringLiteral("field"), QStringLiteral("region")},
                                       {QStringLiteral("old_value"), runRegion(before)},
                                       {QStringLiteral("new_value"), region}});
        }
        touchRun(run, QStringLiteral("CORRECT"), reason, changes);
        m_runs[indexOfRun(run.value(QStringLiteral("run_id")).toString())] = run;
        moved.append(run.value(QStringLiteral("run_id")));
    }
    if (moved.isEmpty())
        return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NO_CHANGES"), "profile.assign_unchanged");
    if (moving && overlap > 0)
        profile->settings.baselineCompletedCount -= overlap;
    // A run moved by hand no longer belongs to the character it was recorded for: no
    // identity counts it, and a later BIND does not move it.
    QStringList movedIds;
    for (const auto &id : std::as_const(moved))
        movedIds.append(id.toString());
    forgetIdentityStamps(movedIds);
    emitStatsInvalidatedLater();
    return {{QStringLiteral("changed_count"), moved.size()},
            {QStringLiteral("run_ids"), moved},
            {QStringLiteral("action"), moving ? QStringLiteral("move_region") : QStringLiteral("assign_profile")},
            {QStringLiteral("idempotent_replay"), false}};
}

} // namespace mr
