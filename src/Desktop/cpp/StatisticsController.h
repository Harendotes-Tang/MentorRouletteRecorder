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
    int pendingReviewCount() const;
    void updateAchievementBaseline(int goal, int baseline, const QString &reason);

Q_SIGNALS:
    void dashboardChanged();
    void trendChanged();
    void optionsChanged();
    /// Emitted after adoption/notifications, before the request callback.
    void dashboardRequestFinished(bool ok);
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
    QVariantList m_dutyOptions;
    QString m_trendMode = QStringLiteral("day");
    int m_completedLast7Days = 0;
    int m_completedLast30Days = 0;
    int m_trendWindowDays = 30;
};

} // namespace mr
