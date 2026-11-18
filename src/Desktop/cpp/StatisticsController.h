#pragma once

#include <functional>
#include <QJsonArray>
#include <QJsonObject>
#include <QHash>
#include <QSet>
#include <QObject>
#include <QPointer>
#include <QVariantList>
#include <QVariantMap>
#include "StatsModels.h"

namespace mr {
class IBackend;
class BackendReply;

/// Owns the dashboard/trend snapshots, statistics models and baseline request handling.
/// The backend and its reply objects are borrowed; this QObject supplies the callback
/// context, while QPointer and backend-destruction handlers guard the borrowed backend.
class StatisticsController : public QObject
{
    Q_OBJECT
public:
    explicit StatisticsController(IBackend *backend, QObject *parent = nullptr, const QString &recordRegion = {});
    QJsonObject dashboardSnapshot() const { return m_dashboard; }
    QVariantMap dashboard() const { return m_dashboard.toVariantMap(); }
    QVariantList achievements() const;
    QVariantList allAchievements() const;
    QVariantMap achievementForRegion(const QString &region) const;
    QString recordRegion() const { return m_recordRegion; }
    static bool isRecordRegion(const QString &region);
    void setRecordRegion(const QString &region);
    QString baselineRegion() const { return m_baselineRegion; }
    void setBaselineRegion(const QString &region);
    DungeonStatsModel *dungeons() const { return m_dungeons; }
    JobStatsModel *jobs() const { return m_jobs; }
    QVariantList dutyOptions() const { return m_dutyOptions; }
    QString trendMode() const { return m_trendMode; }
    int trendWindowDays() const { return m_trendWindowDays; }
    int completedLast7Days() const { return m_completedLast7Days; }
    int completedLast30Days() const { return m_completedLast30Days; }

    void refreshDashboard();
    void requestDashboard(std::function<void(bool ok)> then);
    /// Reads lifetime progress without replacing the listing scope or its trend.
    void requestRegionalAchievement(const QString &region, std::function<void(bool ok)> then = {});
    void refreshAchievementRegions();
    void refreshTrend();
    void setTrendMode(const QString &mode);
    void setTrendWindowDays(int days);
    QVariantList trendBuckets() const;
    QVariantList resultBuckets() const;
    QVariantList statCards() const;
    int goalCount() const;
    int baselineCount() const;
    /// DashboardStats.achievement_progress / remaining as the Collector reported them.
    int achievementProgress() const;
    int remainingCount() const;
    int pendingReviewCount() const;
    /// True once a dashboard answer has been read on the current connection. Until then
    /// goalCount() / baselineCount() are defaults or a dropped connection's answer, and a
    /// save of them would overwrite what the Collector stores (audit 2026-10-03, CS7-D3).
    bool achievementSettingsLoaded() const { return m_achievementSettingsLoaded; }
    /// The connection dropped: what is stored is unknown again until the next read.
    void forgetAchievementSettings();
    /// Refused without a request until achievementSettingsLoaded().
    void updateAchievementBaseline(int goal, int baseline, const QString &reason);
    /// True while an UpdateAchievementBaseline is unanswered, whoever sent it.
    bool baselineSaving() const { return m_baselineSavesInFlight > 0; }

    // -- 角色档案 (StatisticsControllerProfiles.cpp) --------------------------
    /// $defs/DashboardStats.profiles of the newest answer, or of the newest profile
    /// change's reply: every profile of every region, unaffected by any filter.
    QVariantList characterProfiles() const { return m_profiles.toVariantList(); }
    QVariantList characterProfilesForRegion(const QString &region) const;
    /// The profile with \a profileId; empty when it is not known.
    QVariantMap characterProfile(int profileId) const;
    /// True when \a region holds two or more profiles or runs assigned to none; for an
    /// empty \a region, when any region does. The one switch that keeps a player with a
    /// single character on the interface they had before profiles existed.
    bool characterFeaturesVisible(const QString &region) const;
    /// The numbers a finished \a run's progress is spoken and shown with: its own
    /// profile's; for a run without profile_id (an older Collector) its region's entry;
    /// empty for a run assigned to no profile (profile_id null) or a profile not known.
    QVariantMap achievementForRun(const QJsonObject &run) const;
    /// True when \a run says it belongs to no character profile (profile_id: null).
    static bool runIsUnassigned(const QJsonObject &run);
    /// 0: goalCount() / baselineCount() / updateAchievementBaseline() address the current
    /// profile of baselineRegion(). Otherwise the profile they address; refused (left as
    /// it is) for a profile that is not known or not of baselineRegion(). Back to 0 when
    /// the region changes or the profile disappears.
    int baselineProfileId() const { return m_baselineProfileId; }
    void setBaselineProfileId(int profileId);
    bool characterProfileSaving() const { return m_profileSavesInFlight > 0; }
    /// \a identityId above 0 binds that identity to the new profile in the same request.
    void createCharacterProfile(const QString &region, const QString &displayName, int goalCount,
                                int baselineCount, bool makeCurrent, int identityId = 0);
    void renameCharacterProfile(int profileId, const QString &displayName);
    void selectCharacterProfile(int profileId);
    void deleteCharacterProfile(int profileId);

    // -- 自动识别角色 (StatisticsControllerIdentities.cpp) ----------------------
    /// $defs/DashboardStats.identities of the newest answer, or of the newest
    /// ResolveCharacterIdentity reply: every identity bound to no profile, as received.
    QVariantList characterIdentities() const { return m_identities.toVariantList(); }
    /// The PENDING identities of \a region in the Collector's order; "" for every region.
    QVariantList pendingCharacterIdentities(const QString &region) const;
    /// How many identities of \a region are IGNORED; "" for every region.
    int ignoredCharacterIdentityCount(const QString &region) const;
    /// RegionalAchievement.identification of \a region; "" when the Collector sent none.
    QString characterIdentification(const QString &region) const;
    /// True when \a region ("" = any region) has an identity in characterIdentities().
    bool hasCharacterIdentities(const QString &region) const;
    void bindCharacterIdentity(int identityId, int profileId, const QString &displayName = {});
    void ignoreCharacterIdentity(int identityId);
    /// ASK_AGAIN for every IGNORED identity of \a region, one request after the other; one
    /// characterProfileSaved(0, "ask_again") at the end, or characterProfileFailed at the
    /// first refusal (the rest are not sent).
    void askAgainAboutCharacterIdentities(const QString &region);
    void forgetCharacterIdentities(int profileId);

Q_SIGNALS:
    void dashboardChanged();
    /// A newly observed whole-goal milestone; first reads/settings/reconnects
    /// only prime the observation. UI consumers may discard it while hidden.
    void achievementMilestoneReached(int completedLaps);
    void regionalAchievementMilestoneReached(const QString &region, int completedLaps);
    void recordRegionChanged();
    void baselineRegionChanged();
    void trendChanged();
    void optionsChanged();
    void achievementSettingsLoadedChanged();
    /// Emitted after adoption/notifications, before the request callback.
    void dashboardRequestFinished(bool ok);
    void baselineSavingChanged();
    /// UpdateAchievementBaseline was accepted; \a goal and \a baseline are what the
    /// Collector stored. Emitted before the dashboard is read again.
    void baselineSaved(int goal, int baseline);
    void baselineFailed(const QString &code, const QString &message);
    void mutationFailed(const QString &code, const QString &message);
    void toastRequested(const QString &message);
    void characterProfilesChanged();
    void baselineProfileIdChanged();
    void characterProfileSavingChanged();
    void characterIdentitiesChanged();
    /// A profile change was accepted; \a action is "create", "rename", "select", "delete",
    /// "bind", "ignore", "ask_again" or "forget". characterProfiles() (and, for a
    /// ResolveCharacterIdentity, characterIdentities()) already hold the reply's lists.
    void characterProfileSaved(int profileId, const QString &action);
    /// A profile change was refused or went unanswered; \a message is the Collector's
    /// sentence in the interface language (collector_text::replyErrorText).
    void characterProfileFailed(const QString &action, const QString &code, const QString &message);

private:
    /// Adopts \a dashboard's profiles unless a newer list (by \a serial) is held.
    void adoptProfiles(const QJsonObject &dashboard, quint64 serial);
    void applyProfiles(const QJsonArray &profiles, quint64 serial);
    /// Watches one profile request through to characterProfileSaved / characterProfileFailed.
    /// With \a then, an accepted answer calls it instead of emitting characterProfileSaved,
    /// before characterProfileSaving drops, so a chain of requests reads as one save.
    /// \a movesRuns: the request files runs under a profile (BIND, create with an identity);
    /// while one is unanswered no lap is celebrated, and after it the next read only primes.
    /// \a boundIdentityId: on success that identity leaves characterIdentities() at once
    /// (a CharacterProfileResult carries no identities).
    void watchProfileRequest(const QString &action, BackendReply *reply,
                             std::function<void()> then = {}, bool movesRuns = false,
                             int boundIdentityId = 0);
    /// Drops \a identityId from characterIdentities(); true when it was there.
    bool dropIdentity(int identityId);
    /// Adopts \a dashboard's identities unless a newer list (by \a serial) is held.
    void adoptIdentities(const QJsonObject &dashboard, quint64 serial);
    void applyIdentities(const QJsonArray &identities, quint64 serial);
    /// A request is about to move runs onto a profile of \a region: the next read of the
    /// region's progress primes the milestone observation instead of celebrating a lap.
    void suspendMilestone(const QString &region);
    /// Sends ASK_AGAIN for the first of \a identityIds and the rest after it is accepted.
    void askAgainInTurn(QList<int> identityIds);
    void observeAchievement(const QJsonObject &dashboard, quint64 serial);
    void adoptAchievements(const QJsonObject &dashboard, const QString &region, quint64 serial);
    QJsonObject regionFilter() const;
    void applyRegionFilter();
    void applyTrendSeries(const QJsonObject &trend);
    void applyDayCounts(const QJsonObject &trend);
    void rebuildDutyOptions();
    QPointer<IBackend> m_backend;
    DungeonStatsModel *m_dungeons;
    JobStatsModel *m_jobs;
    QJsonObject m_dashboard;
    QString m_recordRegion;
    QString m_baselineRegion = QStringLiteral("CN");
    QHash<QString, QJsonObject> m_achievements;
    QSet<QString> m_loadedAchievementRegions;
    struct AchievementObservation {
        bool ready = false;
        int goal = 0;
        int baseline = 0;
        int highestLap = 0;
        /// The character profile the region's entry belonged to; 0 from an older Collector.
        int profileId = 0;
    };
    QHash<QString, AchievementObservation> m_regionalObservations;
    QHash<QString, quint64> m_acceptedRegionalSerial;
    quint64 m_scopeGeneration = 0;
    QVariantList m_trendBuckets;
    /// Granularity of m_trendBuckets as the Collector named it; empty when none.
    QString m_trendSeriesGranularity;
    QVariantList m_dutyOptions;
    QString m_trendMode = QStringLiteral("day");
    quint64 m_trendGeneration = 0;
    /// Bumped by forgetAchievementSettings(): a read asked before the drop does not
    /// count as this connection's.
    quint64 m_connectionGeneration = 0;
    quint64 m_achievementObservationGeneration = 0;
    quint64 m_achievementReadSerial = 0;
    quint64 m_lastAchievementReadSerial = 0;
    bool m_achievementSettingsLoaded = false;
    /// Set once the regions the first dashboard read of this connection left
    /// out have been asked for; cleared on disconnect (review I3-2 / K1-3).
    bool m_achievementRegionsSwept = false;
    /// Set by refreshAchievementRegions(): every region's settings are wanted,
    /// GLOBAL and UNKNOWN included, on this connection and the following ones.
    bool m_allAchievementRegionsWanted = false;
    int m_baselineSavesInFlight = 0;
    QJsonArray m_profiles;
    /// The read serial m_profiles came with; an older answer never replaces it.
    quint64 m_profilesSerial = 0;
    int m_baselineProfileId = 0;
    int m_profileSavesInFlight = 0;
    /// Unanswered requests that move runs onto a profile (watchProfileRequest movesRuns).
    int m_runMovesInFlight = 0;
    QJsonArray m_identities;
    /// The read serial m_identities came with; an older answer never replaces it.
    quint64 m_identitiesSerial = 0;
    int m_completedLast7Days = 0;
    int m_completedLast30Days = 0;
    int m_trendWindowDays = 30;
};

} // namespace mr
