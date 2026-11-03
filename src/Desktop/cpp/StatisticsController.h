#pragma once

#include <functional>
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
    explicit StatisticsController(IBackend *backend, QObject *parent = nullptr);
    QJsonObject dashboardSnapshot() const { return m_dashboard; }
    QVariantMap dashboard() const { return m_dashboard.toVariantMap(); }
    QVariantList achievements() const;
    QVariantList allAchievements() const;
    QVariantMap achievementForRegion(const QString &region) const;
    QString recordRegion() const { return m_recordRegion; }
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

private:
    void observeAchievement(const QJsonObject &dashboard, quint64 serial);
    void adoptAchievements(const QJsonObject &dashboard, const QString &region, quint64 serial);
    QJsonObject regionFilter() const;
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
    int m_completedLast7Days = 0;
    int m_completedLast30Days = 0;
    int m_trendWindowDays = 30;
};

} // namespace mr
