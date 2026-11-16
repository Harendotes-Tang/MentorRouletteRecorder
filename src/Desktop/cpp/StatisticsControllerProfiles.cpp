// ---------------------------------------------------------------------------
// 角色档案 (character profiles) on the Desktop side of StatisticsController.
//
// The list itself is the Collector's: every GetDashboardStats answer carries all profiles
// of all regions with their own progress (DashboardStats.profiles), whatever its filter,
// and every profile change answers with the list as it stands afterwards
// (CharacterProfileResult.profiles). Nothing here counts or derives a number.
// ---------------------------------------------------------------------------

#include "StatisticsController.h"

#include "CollectorText.h"
#include "IBackend.h"
#include "IpcFraming.h"

#include <QHash>
#include <QPointer>
#include <QSet>

#include <algorithm>

namespace mr {
namespace {

QJsonObject findProfile(const QJsonArray &profiles, int profileId)
{
    if (profileId <= 0)
        return {};
    for (const auto &value : profiles) {
        const QJsonObject profile = value.toObject();
        if (profile.value(QStringLiteral("profile_id")).toInt() == profileId)
            return profile;
    }
    return {};
}

} // namespace

QVariantList StatisticsController::characterProfilesForRegion(const QString &region) const
{
    QVariantList out;
    for (const auto &value : m_profiles) {
        const QJsonObject profile = value.toObject();
        if (profile.value(QStringLiteral("region")).toString() == region)
            out.append(profile.toVariantMap());
    }
    return out;
}

QVariantMap StatisticsController::characterProfile(int profileId) const
{
    return findProfile(m_profiles, profileId).toVariantMap();
}

bool StatisticsController::characterFeaturesVisible(const QString &region) const
{
    QHash<QString, int> counts;
    for (const auto &value : m_profiles)
        ++counts[value.toObject().value(QStringLiteral("region")).toString()];
    // A character told apart in the game traffic, waiting or ignored, is a second
    // character the player has to be able to see and answer for.
    const auto visibleFor = [this, &counts](const QString &owner) {
        return counts.value(owner) >= 2
               || m_achievements.value(owner).value(QStringLiteral("unassigned_run_count")).toInt() > 0
               || hasCharacterIdentities(owner);
    };
    if (!region.isEmpty())
        return visibleFor(region);
    if (hasCharacterIdentities(QString()))
        return true;
    QSet<QString> regions(counts.keyBegin(), counts.keyEnd());
    for (auto it = m_achievements.constBegin(); it != m_achievements.constEnd(); ++it)
        regions.insert(it.key());
    return std::ranges::any_of(regions, visibleFor);
}

bool StatisticsController::runIsUnassigned(const QJsonObject &run)
{
    return run.contains(QStringLiteral("profile_id")) && run.value(QStringLiteral("profile_id")).isNull();
}

QVariantMap StatisticsController::achievementForRun(const QJsonObject &run) const
{
    const QJsonValue owner = run.value(QStringLiteral("profile_id"));
    if (owner.isDouble())
        return characterProfile(owner.toInt());
    if (!owner.isUndefined())
        return {}; // Assigned to no profile: it counts in no one's progress.
    // An older Collector sends no profile_id; its runs count for their region.
    const QString region = run.value(QStringLiteral("region")).toString();
    return achievementForRegion(region.isEmpty() ? QStringLiteral("CN") : region);
}

void StatisticsController::setBaselineProfileId(int profileId)
{
    if (profileId == m_baselineProfileId)
        return;
    if (profileId != 0) {
        const QJsonObject profile = findProfile(m_profiles, profileId);
        if (profile.isEmpty() || profile.value(QStringLiteral("region")).toString() != m_baselineRegion)
            return;
    }
    m_baselineProfileId = profileId;
    Q_EMIT baselineProfileIdChanged();
    // goalCount / baselineCount follow it.
    Q_EMIT dashboardChanged();
}

void StatisticsController::adoptProfiles(const QJsonObject &dashboard, quint64 serial)
{
    // An older Collector sends no list; what is held stays.
    adoptIdentities(dashboard, serial);
    if (!dashboard.contains(QStringLiteral("profiles")) || serial < m_profilesSerial)
        return;
    applyProfiles(dashboard.value(QStringLiteral("profiles")).toArray(), serial);
}

void StatisticsController::applyProfiles(const QJsonArray &profiles, quint64 serial)
{
    m_profilesSerial = serial;
    if (profiles == m_profiles)
        return;
    m_profiles = profiles;
    const bool baselineProfileGone =
        m_baselineProfileId != 0 && findProfile(m_profiles, m_baselineProfileId).isEmpty();
    if (baselineProfileGone)
        m_baselineProfileId = 0;
    Q_EMIT characterProfilesChanged();
    if (baselineProfileGone)
        Q_EMIT baselineProfileIdChanged();
    // goalCount / baselineCount of a named profile come from this list.
    Q_EMIT dashboardChanged();
}

void StatisticsController::createCharacterProfile(const QString &region, const QString &displayName,
                                                  int goalCount, int baselineCount, bool makeCurrent,
                                                  int identityId)
{
    if (!m_backend)
        return;
    // Binding an identity moves its runs onto the new profile.
    watchProfileRequest(QStringLiteral("create"),
                        m_backend->createCharacterProfile(region, displayName, goalCount, baselineCount,
                                                          makeCurrent, ipc::newRequestId(), identityId),
                        {}, identityId > 0, qMax(identityId, 0));
}

void StatisticsController::renameCharacterProfile(int profileId, const QString &displayName)
{
    if (!m_backend)
        return;
    // A null name would leave the field out and ask for nothing; "" is sent and refused
    // with the Collector's own sentence.
    watchProfileRequest(QStringLiteral("rename"),
                        m_backend->updateCharacterProfile(profileId,
                                                          displayName.isNull() ? QStringLiteral("") : displayName,
                                                          false, ipc::newRequestId()));
}

void StatisticsController::selectCharacterProfile(int profileId)
{
    if (!m_backend)
        return;
    watchProfileRequest(QStringLiteral("select"),
                        m_backend->updateCharacterProfile(profileId, QString(), true, ipc::newRequestId()));
}

void StatisticsController::deleteCharacterProfile(int profileId)
{
    if (!m_backend)
        return;
    watchProfileRequest(QStringLiteral("delete"),
                        m_backend->deleteCharacterProfile(profileId, ipc::newRequestId()));
}

void StatisticsController::watchProfileRequest(const QString &action, BackendReply *reply,
                                               std::function<void()> then, bool movesRuns,
                                               int boundIdentityId)
{
    // Counted before the answer: a pipe that is down answers inside the call.
    if (m_profileSavesInFlight++ == 0)
        Q_EMIT characterProfileSavingChanged();
    // Runs moved onto a profile are not completions just made: while the request is out no
    // lap is celebrated, and no read answered meanwhile may arm the observation again.
    if (movesRuns) {
        ++m_runMovesInFlight;
        suspendMilestone(QString());
    }
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, action, guard, then = std::move(then), movesRuns, boundIdentityId](
                              bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
        if (movesRuns) {
            --m_runMovesInFlight;
            // The read that follows the move only primes the observation.
            if (ok)
                suspendMilestone(QString());
        }
        if (!ok) {
            if (--m_profileSavesInFlight == 0)
                Q_EMIT characterProfileSavingChanged();
            const QString text = collector_text::replyErrorText(guard, message).text();
            Q_EMIT characterProfileFailed(action, code, text.isEmpty() ? code : text);
            return;
        }
        // The reply's lists are newer than every read sent before it.
        const QJsonObject answer = QJsonObject::fromVariantMap(payload);
        const quint64 serial = ++m_achievementReadSerial;
        if (answer.contains(QStringLiteral("profiles")))
            applyProfiles(answer.value(QStringLiteral("profiles")).toArray(), serial);
        if (answer.contains(QStringLiteral("identities")))
            applyIdentities(answer.value(QStringLiteral("identities")).toArray(), serial);
        else if (boundIdentityId > 0)
            dropIdentity(boundIdentityId);
        if (then) {
            // The next request of the chain is counted before this one stops counting.
            then();
            if (--m_profileSavesInFlight == 0)
                Q_EMIT characterProfileSavingChanged();
            return;
        }
        if (--m_profileSavesInFlight == 0)
            Q_EMIT characterProfileSavingChanged();
        // profile_id is null for IGNORE / ASK_AGAIN: 0.
        Q_EMIT characterProfileSaved(answer.value(QStringLiteral("profile_id")).toInt(), action);
    });
}

bool StatisticsController::dropIdentity(int identityId)
{
    for (qsizetype index = 0; index < m_identities.size(); ++index) {
        if (m_identities.at(index).toObject().value(QStringLiteral("identity_id")).toInt() != identityId)
            continue;
        m_identities.removeAt(index);
        // A read sent before this answer still lists the identity: it must not bring it back.
        m_identitiesSerial = ++m_achievementReadSerial;
        Q_EMIT characterIdentitiesChanged();
        return true;
    }
    return false;
}

} // namespace mr
