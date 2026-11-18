// ---------------------------------------------------------------------------
// 自动识别角色 (characters told apart in the game traffic) on the Desktop side of
// StatisticsController.
//
// The list is the Collector's: every GetDashboardStats answer carries every identity bound
// to no profile (DashboardStats.identities), whatever its filter, and every
// ResolveCharacterIdentity answers with the list as it stands afterwards
// (CharacterIdentityResult.identities). The player's answers travel through the same
// saving flag and signals as the profile changes (characterProfileSaved / Failed).
// ---------------------------------------------------------------------------

#include "StatisticsController.h"

#include "IBackend.h"
#include "IpcFraming.h"

#include <algorithm>

namespace mr {
namespace {

bool inRegion(const QJsonObject &identity, const QString &region)
{
    return region.isEmpty() || identity.value(QStringLiteral("region")).toString() == region;
}

} // namespace

QVariantList StatisticsController::pendingCharacterIdentities(const QString &region) const
{
    QVariantList out;
    for (const auto &value : m_identities) {
        const QJsonObject identity = value.toObject();
        if (inRegion(identity, region)
            && (identity.value(QStringLiteral("state")).toString() == QLatin1String("PENDING")
                || identity.value(QStringLiteral("registration_profile_id")).toInt() > 0))
            out.append(identity.toVariantMap());
    }
    return out;
}

int StatisticsController::ignoredCharacterIdentityCount(const QString &region) const
{
    int count = 0;
    for (const auto &value : m_identities) {
        const QJsonObject identity = value.toObject();
        if (inRegion(identity, region)
            && identity.value(QStringLiteral("state")).toString() == QLatin1String("IGNORED"))
            ++count;
    }
    return count;
}

QString StatisticsController::characterIdentification(const QString &region) const
{
    return m_achievements.value(region).value(QStringLiteral("identification")).toString();
}

bool StatisticsController::hasCharacterIdentities(const QString &region) const
{
    return std::ranges::any_of(m_identities, [&region](const QJsonValue &value) {
        return inRegion(value.toObject(), region);
    });
}

void StatisticsController::adoptIdentities(const QJsonObject &dashboard, quint64 serial)
{
    // An older Collector sends no list; what is held stays.
    if (!dashboard.contains(QStringLiteral("identities")) || serial < m_identitiesSerial)
        return;
    applyIdentities(dashboard.value(QStringLiteral("identities")).toArray(), serial);
}

void StatisticsController::applyIdentities(const QJsonArray &identities, quint64 serial)
{
    m_identitiesSerial = serial;
    if (identities == m_identities)
        return;
    m_identities = identities;
    Q_EMIT characterIdentitiesChanged();
}

void StatisticsController::suspendMilestone(const QString &region)
{
    // Runs moved onto a profile are not completions just made: the read after the move
    // only primes the observation (observeAchievement), as after a baseline save.
    if (region.isEmpty()) {
        for (auto &observation : m_regionalObservations)
            observation.ready = false;
        return;
    }
    m_regionalObservations[region].ready = false;
}

void StatisticsController::bindCharacterIdentity(int identityId, int profileId, const QString &displayName)
{
    if (!m_backend)
        return;
    watchProfileRequest(displayName.isNull() ? QStringLiteral("bind") : QStringLiteral("register"),
                        m_backend->resolveCharacterIdentity(identityId, QStringLiteral("BIND"), profileId,
                                                            ipc::newRequestId(), displayName),
                        {}, true);
}

void StatisticsController::ignoreCharacterIdentity(int identityId)
{
    if (!m_backend)
        return;
    watchProfileRequest(QStringLiteral("ignore"),
                        m_backend->resolveCharacterIdentity(identityId, QStringLiteral("IGNORE"), 0,
                                                            ipc::newRequestId()));
}

void StatisticsController::askAgainAboutCharacterIdentities(const QString &region)
{
    if (!m_backend)
        return;
    QList<int> ignored;
    for (const auto &value : m_identities) {
        const QJsonObject identity = value.toObject();
        if (inRegion(identity, region)
            && identity.value(QStringLiteral("state")).toString() == QLatin1String("IGNORED"))
            ignored.append(identity.value(QStringLiteral("identity_id")).toInt());
    }
    if (ignored.isEmpty()) {
        // Nothing to ask again about: the answer the page waits for, without a request.
        Q_EMIT characterProfileSaved(0, QStringLiteral("ask_again"));
        return;
    }
    askAgainInTurn(ignored);
}

void StatisticsController::askAgainInTurn(QList<int> identityIds)
{
    const int identityId = identityIds.takeFirst();
    BackendReply *reply = m_backend->resolveCharacterIdentity(identityId, QStringLiteral("ASK_AGAIN"), 0,
                                                              ipc::newRequestId());
    if (identityIds.isEmpty()) {
        watchProfileRequest(QStringLiteral("ask_again"), reply);
        return;
    }
    watchProfileRequest(QStringLiteral("ask_again"), reply, [this, rest = std::move(identityIds)] {
        if (m_backend) {
            askAgainInTurn(rest);
        } else {
            Q_EMIT characterProfileFailed(QStringLiteral("ask_again"), QStringLiteral("ERR_PIPE_CLOSED"),
                                          QStringLiteral("ERR_PIPE_CLOSED"));
        }
    });
}

void StatisticsController::forgetCharacterIdentities(int profileId)
{
    if (!m_backend)
        return;
    watchProfileRequest(QStringLiteral("forget"),
                        m_backend->forgetCharacterIdentities(profileId, ipc::newRequestId()));
}

} // namespace mr
