#pragma once

#include <functional>
#include <QJsonObject>
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
    DungeonStatsModel *dungeons() const { return m_dungeons; }
    JobStatsModel *jobs() const { return m_jobs; }
    QVariantList dutyOptions() const { return m_dutyOptions; }
    QString trendMode() const { return m_trendMode; }
    int trendWindowDays() const { return m_trendWindowDays; }
    int completedLast7Days() const { return m_completedLast7Days; }
    int completedLast30Days() const { return m_completedLast30Days; }

    void refreshDashboard();
    void requestDashboard(std::function<void(bool ok)> then);
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
    void applyTrendSeries(const QJsonObject &trend);
    void applyDayCounts(const QJsonObject &trend);
    void rebuildDutyOptions();
    QPointer<IBackend> m_backend;
    DungeonStatsModel *m_dungeons;
    JobStatsModel *m_jobs;
    QJsonObject m_dashboard;
    QVariantList m_trendBuckets;
    /// Granularity of m_trendBuckets as the Collector named it; empty when none.
    QString m_trendSeriesGranularity;
    QVariantList m_dutyOptions;
    QString m_trendMode = QStringLiteral("day");
    quint64 m_trendGeneration = 0;
    /// Bumped by forgetAchievementSettings(): a read asked before the drop does not
    /// count as this connection's.
    quint64 m_connectionGeneration = 0;
    bool m_achievementSettingsLoaded = false;
    int m_baselineSavesInFlight = 0;
    int m_completedLast7Days = 0;
    int m_completedLast30Days = 0;
    int m_trendWindowDays = 30;
};

} // namespace mr
