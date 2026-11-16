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
    const auto visibleFor = [this, &counts](const QString &owner) {
        return counts.value(owner) >= 2
               || m_achievements.value(owner).value(QStringLiteral("unassigned_run_count")).toInt() > 0;
    };
    if (!region.isEmpty())
        return visibleFor(region);
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
                                                  int goalCount, int baselineCount, bool makeCurrent)
{
    if (!m_backend)
        return;
    watchProfileRequest(QStringLiteral("create"),
                        m_backend->createCharacterProfile(region, displayName, goalCount, baselineCount,
                                                          makeCurrent, ipc::newRequestId()));
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

void StatisticsController::watchProfileRequest(const QString &action, BackendReply *reply)
{
    // Counted before the answer: a pipe that is down answers inside the call.
    if (m_profileSavesInFlight++ == 0)
        Q_EMIT characterProfileSavingChanged();
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, action, guard](bool ok, const QVariantMap &payload, const QString &code,
                                                const QString &message) {
        if (--m_profileSavesInFlight == 0)
            Q_EMIT characterProfileSavingChanged();
        if (!ok) {
            const QString text = collector_text::replyErrorText(guard, message).text();
            Q_EMIT characterProfileFailed(action, code, text.isEmpty() ? code : text);
            return;
        }
        // The reply's list is newer than every read sent before it.
        const QJsonObject answer = QJsonObject::fromVariantMap(payload);
        if (answer.contains(QStringLiteral("profiles")))
            applyProfiles(answer.value(QStringLiteral("profiles")).toArray(), ++m_achievementReadSerial);
        Q_EMIT characterProfileSaved(answer.value(QStringLiteral("profile_id")).toInt(), action);
    });
}

} // namespace mr
