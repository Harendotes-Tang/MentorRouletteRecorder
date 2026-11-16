// ---------------------------------------------------------------------------
// 自动识别角色 (characters told apart in the game traffic) in the offline backend.
//
// The rules are the contract's (ResolveCharacterIdentityRequest, CharacterIdentity,
// CreateCharacterProfileRequest.identity_id, UpdateCharacterProfileRequest.forget_identities):
// an identity bound to no profile is listed as PENDING or IGNORED, and the runs recorded
// for it stay assigned to no profile; BIND moves exactly those runs to the profile, without
// a revision, and makes the profile current only when that character is the logged-in one
// (setLoggedInCharacterIdentity); IGNORE / ASK_AGAIN only flip the state. Only
// CN tells characters apart (AUTOMATIC); the switch off makes every region MANUAL_OFF.
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include "MockData.h"

#include <QJsonArray>
#include <QSet>

#include <algorithm>

using namespace mr::mock;

namespace mr {

MockBackend::CharacterIdentityEntry *MockBackend::findIdentity(int identityId)
{
    for (CharacterIdentityEntry &identity : m_identities) {
        if (identity.id == identityId)
            return &identity;
    }
    return nullptr;
}

QStringList MockBackend::unassignedIdentityRuns(const CharacterIdentityEntry &identity) const
{
    QStringList out;
    for (const QString &runId : identity.runIds) {
        const int index = indexOfRun(runId);
        if (index < 0)
            continue;
        const QJsonObject run = m_runs.at(index).toObject();
        if (!isSoftDeleted(run) && runProfileId(run) == 0)
            out.append(runId);
    }
    return out;
}

QJsonArray MockBackend::identitiesJson() const
{
    QList<const CharacterIdentityEntry *> listed;
    for (const CharacterIdentityEntry &identity : m_identities) {
        if (identity.boundProfileId == 0)
            listed.append(&identity);
    }
    // The ones waiting for the player's answer first, oldest first.
    std::ranges::stable_sort(listed, [](const CharacterIdentityEntry *left, const CharacterIdentityEntry *right) {
        const bool leftPending = left->state == QLatin1String("PENDING");
        if (leftPending != (right->state == QLatin1String("PENDING")))
            return leftPending;
        if (left->firstSeen != right->firstSeen)
            return left->firstSeen < right->firstSeen;
        return left->id < right->id;
    });
    QJsonArray out;
    for (const CharacterIdentityEntry *identity : std::as_const(listed)) {
        out.append(QJsonObject{{QStringLiteral("identity_id"), identity->id},
                               {QStringLiteral("region"), identity->region},
                               {QStringLiteral("state"), identity->state},
                               {QStringLiteral("first_seen_at_utc"), isoUtc(identity->firstSeen)},
                               {QStringLiteral("last_seen_at_utc"), isoUtc(identity->lastSeen)},
                               {QStringLiteral("run_count"),
                                static_cast<int>(unassignedIdentityRuns(*identity).size())}});
    }
    return out;
}

int MockBackend::boundIdentityCount(int profileId) const
{
    return static_cast<int>(std::ranges::count_if(m_identities, [profileId](const CharacterIdentityEntry &identity) {
        return identity.boundProfileId == profileId;
    }));
}

QString MockBackend::identificationOf(const QString &region) const
{
    if (!captureSettings().value(QStringLiteral("character_identification_enabled")).toBool(true))
        return QStringLiteral("MANUAL_OFF");
    // Verified on the CN client only (docs/plans/per-character-progress.md, phase 1).
    return region == QLatin1String("CN") ? QStringLiteral("AUTOMATIC") : QStringLiteral("MANUAL_REGION");
}

int MockBackend::addCharacterIdentity(const QString &region, const QString &state, int runCount)
{
    Q_ASSERT(isRegion(region));
    Q_ASSERT(state == QLatin1String("PENDING") || state == QLatin1String("IGNORED"));
    ensureCurrentProfile(region);
    CharacterIdentityEntry identity;
    identity.id = m_nextIdentityId++;
    identity.region = region;
    identity.state = state;
    identity.lastSeen = m_now.toUTC();
    identity.firstSeen = identity.lastSeen.addDays(-3).addSecs(-60 * identity.id);

    QSet<QString> claimed;
    for (const CharacterIdentityEntry &other : std::as_const(m_identities))
        claimed.unite(QSet<QString>(other.runIds.cbegin(), other.runIds.cend()));
    // Live runs of the region, the ones assigned to no profile first, then the newest.
    QList<QJsonObject> candidates;
    for (const auto &value : std::as_const(m_runs)) {
        const QJsonObject run = value.toObject();
        if (runRegion(run) == region && !isSoftDeleted(run)
            && !claimed.contains(run.value(QStringLiteral("run_id")).toString()))
            candidates.append(run);
    }
    std::ranges::stable_sort(candidates, [this](const QJsonObject &left, const QJsonObject &right) {
        const bool leftLoose = runProfileId(left) == 0;
        if (leftLoose != (runProfileId(right) == 0))
            return leftLoose;
        return left.value(QStringLiteral("matched_at_utc")).toString()
               > right.value(QStringLiteral("matched_at_utc")).toString();
    });
    for (const QJsonObject &candidate : std::as_const(candidates)) {
        if (identity.runIds.size() >= runCount)
            break;
        const QString runId = candidate.value(QStringLiteral("run_id")).toString();
        QJsonObject run = candidate;
        run.insert(QStringLiteral("profile_id"), QJsonValue(QJsonValue::Null));
        m_runs[indexOfRun(runId)] = run;
        identity.runIds.append(runId);
    }
    m_identities.append(identity);
    return identity.id;
}

int MockBackend::bindIdentity(CharacterIdentityEntry &identity, int profileId)
{
    const QStringList moving = unassignedIdentityRuns(identity);
    for (const QString &runId : moving) {
        const int index = indexOfRun(runId);
        QJsonObject run = m_runs.at(index).toObject();
        // Moved without a revision (ResolveCharacterIdentityRequest).
        run.insert(QStringLiteral("profile_id"), profileId);
        m_runs[index] = run;
    }
    identity.boundProfileId = profileId;
    identity.state = QStringLiteral("PENDING");
    identity.runIds.clear();
    // The Collector switches to the profile only when this character is the one logged in.
    if (identity.id == m_loggedInIdentityId) {
        for (CharacterProfileEntry &profile : m_profiles) {
            if (profile.region == identity.region)
                profile.current = profile.id == profileId;
        }
    }
    return static_cast<int>(moving.size());
}

void MockBackend::dropCharacterIdentity(int identityId)
{
    m_identities.removeIf([identityId](const CharacterIdentityEntry &entry) { return entry.id == identityId; });
}

void MockBackend::forgetIdentityStamps(const QStringList &runIds)
{
    for (CharacterIdentityEntry &identity : m_identities)
        identity.runIds.removeIf([&runIds](const QString &runId) { return runIds.contains(runId); });
}

MockBackend::CharacterIdentityEntry *MockBackend::identityForNewProfile(const QJsonObject &payload,
                                                                        const QString &region, bool *refused,
                                                                        QString *errorCode, QString *errorMessage)
{
    *refused = false;
    const QJsonValue requested = payload.value(QStringLiteral("identity_id"));
    if (requested.isUndefined())
        return nullptr;
    *refused = true;
    if (!requested.isDouble() || requested.toInt() < 1) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "ipc.field.at_least",
                   {{QStringLiteral("field"), QStringLiteral("identity_id")}, {QStringLiteral("min"), 1}});
        return nullptr;
    }
    // CharacterIdentityService.Require / Bind: an unknown identity is ERR_NOT_FOUND.
    CharacterIdentityEntry *identity = findIdentity(requested.toInt());
    if (!identity) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "identity.not_found");
        return nullptr;
    }
    if (identity->boundProfileId != 0) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "identity.already_bound");
        return nullptr;
    }
    if (identity->region != region) {
        refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"), "profile.region_mismatch");
        return nullptr;
    }
    *refused = false;
    return identity;
}

void MockBackend::forgetIdentitiesOf(int profileId)
{
    const QDateTime now = QDateTime::currentDateTimeUtc();
    for (CharacterIdentityEntry &identity : m_identities) {
        if (identity.boundProfileId != profileId)
            continue;
        // Runs keep their owner; the identity has none of its own left (run_count 0).
        identity.boundProfileId = 0;
        identity.state = QStringLiteral("PENDING");
        identity.runIds.clear();
        if (!identity.firstSeen.isValid())
            identity.firstSeen = now;
    }
}

QJsonObject MockBackend::resolveCharacterIdentity(const QJsonObject &payload, QString *errorCode,
                                                  QString *errorMessage)
{
    const QString badRequest = QStringLiteral("ERR_BAD_REQUEST");
    const auto refuse = [&](const char *key, const QVariantMap &args = {}) {
        return refuseWith(errorCode, errorMessage, badRequest, key, args);
    };
    static const QStringList kAllowed{QStringLiteral("identity_id"), QStringLiteral("action"),
                                      QStringLiteral("profile_id")};
    for (auto it = payload.constBegin(); it != payload.constEnd(); ++it) {
        if (!kAllowed.contains(it.key()))
            return refuse("ipc.field.unknown", {{QStringLiteral("field"), it.key()}});
    }
    const QJsonValue requestedIdentity = payload.value(QStringLiteral("identity_id"));
    if (!requestedIdentity.isDouble() || requestedIdentity.toInt() < 1)
        return refuse("ipc.field.missing", {{QStringLiteral("field"), QStringLiteral("identity_id")}});
    const QString action = payload.value(QStringLiteral("action")).toString();
    if (action != QLatin1String("BIND") && action != QLatin1String("IGNORE") && action != QLatin1String("ASK_AGAIN"))
        return refuse("ipc.field.not_one_of", {{QStringLiteral("field"), QStringLiteral("action")},
                                               {QStringLiteral("choices"), QStringLiteral("BIND / IGNORE / ASK_AGAIN")}});
    const QJsonValue requestedProfile = payload.value(QStringLiteral("profile_id"));
    const bool binding = action == QLatin1String("BIND");
    if (binding && requestedProfile.isUndefined())
        return refuse("identity.profile_required");
    if (!binding && !requestedProfile.isUndefined())
        return refuse("ipc.field.unknown", {{QStringLiteral("field"), QStringLiteral("profile_id")}});
    if (binding && (!requestedProfile.isDouble() || requestedProfile.toInt() < 1))
        return refuse("ipc.field.at_least", {{QStringLiteral("field"), QStringLiteral("profile_id")},
                                             {QStringLiteral("min"), 1}});

    CharacterIdentityEntry *identity = findIdentity(requestedIdentity.toInt());
    // CharacterIdentityService.Resolve: an unknown identity is ERR_NOT_FOUND, like a profile.
    if (!identity)
        return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "identity.not_found");
    if (identity->boundProfileId != 0)
        return refuse("identity.already_bound");

    int boundTo = 0;
    int moved = 0;
    if (binding) {
        const CharacterProfileEntry *profile = findProfile(requestedProfile.toInt());
        if (!profile)
            return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_NOT_FOUND"), "profile.not_found");
        if (profile->region != identity->region)
            return refuse("profile.region_mismatch");
        boundTo = profile->id;
        moved = bindIdentity(*identity, boundTo);
    } else if (action == QLatin1String("IGNORE")) {
        identity->state = QStringLiteral("IGNORED");
    } else {
        if (identity->state != QLatin1String("IGNORED"))
            return refuse("identity.not_ignored");
        identity->state = QStringLiteral("PENDING");
    }

    emitStatsInvalidatedLater();
    return {{QStringLiteral("identity_id"), requestedIdentity.toInt()},
            {QStringLiteral("profile_id"), boundTo > 0 ? QJsonValue(boundTo) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("assigned_run_count"), moved},
            {QStringLiteral("profiles"), characterProfiles()},
            {QStringLiteral("identities"), identitiesJson()},
            {QStringLiteral("idempotent_replay"), false}};
}

} // namespace mr
