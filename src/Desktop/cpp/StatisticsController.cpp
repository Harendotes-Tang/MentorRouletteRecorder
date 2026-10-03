#include "StatisticsController.h"
#include "IBackend.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include <QCoreApplication>
#include <QDateTime>
#include <QJsonArray>
#include <QTimeZone>
#include <utility>

namespace {
QDateTime parseUtc(const QJsonValue &value)
{
    if (!value.isString())
        return {};
    QDateTime dt = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
    if (!dt.isValid())
        dt = QDateTime::fromString(value.toString(), Qt::ISODate);
    if (dt.isValid() && dt.timeSpec() == Qt::LocalTime)
        dt.setTimeZone(QTimeZone::UTC);
    return dt;
}

QVariantMap statCard(const QString &key, const QString &value)
{
    QVariantMap card;
    card.insert(QStringLiteral("k"), key);
    card.insert(QStringLiteral("v"), value);
    return card;
}
} // namespace

namespace mr {

StatisticsController::StatisticsController(IBackend *backend, QObject *parent)
    : QObject(parent)
    , m_backend(backend)
    , m_dungeons(new DungeonStatsModel(this))
    , m_jobs(new JobStatsModel(this))
{
    m_dungeons->setBackend(backend);
    m_jobs->setBackend(backend);
    connect(backend, &QObject::destroyed, this, [this] {
        m_dungeons->setBackend(nullptr);
        m_jobs->setBackend(nullptr);
    });
    connect(m_dungeons, &DungeonStatsModel::countChanged,
            this, &StatisticsController::rebuildDutyOptions);
}

void StatisticsController::refreshDashboard()
{
    requestDashboard({});
}

void StatisticsController::requestDashboard(std::function<void(bool ok)> then)
{
    if (!m_backend)
        return;

    // Always day granularity here. The dashboard's two "近 N 天完成" cards are
    // defined on days, so they are read off this series no matter which
    // granularity the chart happens to be showing.
    m_backend->getDashboardStats({}, QStringLiteral("day"))
        ->whenDone(this, [this, then = std::move(then), connection = m_connectionGeneration](
                             bool ok, const QVariantMap &payload, const QString &,
                             const QString &) {
            // A failed read is not evidence that the numbers changed: wiping the
            // snapshot would blank every card and make the achievement progress
            // read as "baseline + 0". The previous answer is only a moment stale.
            if (ok) {
                m_dashboard = QJsonObject::fromVariantMap(payload);

                const QJsonObject trend = m_dashboard.value(QStringLiteral("trend")).toObject();
                applyDayCounts(trend);
                if (m_trendMode == QLatin1String("day"))
                    applyTrendSeries(trend);

                rebuildDutyOptions();
                Q_EMIT dashboardChanged();
                Q_EMIT trendChanged();

                // After dashboardChanged, so a form that refills itself on the
                // flag already reads the new goal and baseline.
                if (connection == m_connectionGeneration && !m_achievementSettingsLoaded) {
                    m_achievementSettingsLoaded = true;
                    Q_EMIT achievementSettingsLoadedChanged();
                }
            }
            // Projections may coordinate dependent workflows before completion.
            Q_EMIT dashboardRequestFinished(ok);

            // Runs after the numbers have been adopted, which is the whole
            // point: a terminal announcement must speak this run's progress -
            // or, when this read failed, must not claim one at all.
            if (then)
                then(ok);
        });
}

void StatisticsController::refreshTrend()
{
    // Switching back to cached days must also invalidate an outstanding week
    // or month query; otherwise its late answer is shown under the day label.
    const quint64 generation = ++m_trendGeneration;
    if (!m_backend)
        return;

    // The day series arrives with the dashboard, so asking for it again would
    // be a second round trip for bytes already in hand.
    if (m_trendMode == QLatin1String("day")) {
        applyTrendSeries(m_dashboard.value(QStringLiteral("trend")).toObject());
        Q_EMIT trendChanged();
        return;
    }

    m_backend->getDashboardStats({}, m_trendMode)
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload, const QString &,
                                const QString &) {
            if (generation != m_trendGeneration)
                return;
            // A failed refresh is not evidence that the series changed: a chart
            // already showing this granularity keeps it (review OI-1). Only a
            // series of another granularity is dropped, so its bars are never
            // drawn under this one's label.
            if (!ok && m_trendSeriesGranularity == m_trendMode)
                return;
            applyTrendSeries(ok ? QJsonObject::fromVariantMap(payload)
                                      .value(QStringLiteral("trend"))
                                      .toObject()
                                : QJsonObject());
            Q_EMIT trendChanged();
        });
}

void StatisticsController::setTrendMode(const QString &mode)
{
    if (mode == m_trendMode)
        return;
    m_trendMode = mode;
    refreshTrend();
}

void StatisticsController::setTrendWindowDays(int days)
{
    const int clamped = days <= 7 ? 7 : 30;
    if (clamped == m_trendWindowDays)
        return;
    m_trendWindowDays = clamped;
    // The buckets themselves are not re-requested: the window only decides how
    // many of the day buckets already in hand the chart draws.
    Q_EMIT trendChanged();
}

void StatisticsController::applyTrendSeries(const QJsonObject &trend)
{
    // The buckets are the Collector's: it aggregated them in SQL over every
    // matching row, not over one page of 200. This function only turns
    // start_utc into a label the axis can carry, and it does that in local
    // time because that is the clock the user reads
    // (docs/statistics-definitions.md section 12.1).
    const QString granularity = trend.value(QStringLiteral("granularity")).toString();
    m_trendSeriesGranularity = granularity;
    const QString format = granularity == QLatin1String("month")
                               ? QStringLiteral("yyyy-MM")
                               : QStringLiteral("MM-dd");

    m_trendBuckets.clear();
    const QJsonArray buckets = trend.value(QStringLiteral("buckets")).toArray();
    for (const QJsonValue &value : buckets) {
        const QJsonObject entry = value.toObject();
        const QDateTime start = parseUtc(entry.value(QStringLiteral("start_utc")));

        QVariantMap bucket;
        bucket.insert(QStringLiteral("label"),
                      start.isValid() ? start.toLocalTime().toString(format) : QString());
        bucket.insert(QStringLiteral("count"),
                      entry.value(QStringLiteral("completed_count")).toInt());
        bucket.insert(QStringLiteral("start_utc"),
                      entry.value(QStringLiteral("start_utc")).toString());
        m_trendBuckets.append(bucket);
    }
}

void StatisticsController::applyDayCounts(const QJsonObject &trend)
{
    // Only a day series can answer "the last 7 days"; anything else is left
    // alone rather than approximated from wider buckets.
    if (trend.value(QStringLiteral("granularity")).toString() != QLatin1String("day"))
        return;

    const QJsonArray buckets = trend.value(QStringLiteral("buckets")).toArray();
    m_completedLast7Days = 0;
    m_completedLast30Days = 0;
    for (int i = 0; i < buckets.size(); ++i) {
        const int count =
            buckets.at(i).toObject().value(QStringLiteral("completed_count")).toInt();
        m_completedLast30Days += count;
        if (i >= buckets.size() - 7)
            m_completedLast7Days += count;
    }
}

void StatisticsController::rebuildDutyOptions()
{
    // Options come from the dungeon statistics model so the history filter can
    // offer the same content ids and labels the statistics pages aggregate by.
    // The rows are already joined with the bundled duty catalogue by the model,
    // so duty_level / duty_expansion are filled in even though the contract
    // cannot carry them.
    // A failed read emptied the model, but the duties did not go away: keep the
    // options, or the history page's 副本 chip loses its name (review S2-2).
    if (m_dungeons && !m_dungeons->loadError().isEmpty())
        return;
    const QVariantList rows = m_dungeons ? m_dungeons->topRows(0) : QVariantList();
    QVariantList options;
    options.reserve(rows.size());

    for (const QVariant &rowValue : rows) {
        const QVariantMap row = rowValue.toMap();
        const QVariant contentId = row.value(QStringLiteral("content_id"));
        if (!contentId.isValid() || contentId.isNull())
            continue;

        QVariantMap option;
        option.insert(QStringLiteral("content_id"), contentId);
        option.insert(QStringLiteral("duty_name"), row.value(QStringLiteral("duty_name")));
        option.insert(QStringLiteral("duty_category"),
                      row.value(QStringLiteral("duty_category")));
        option.insert(QStringLiteral("duty_level"), row.value(QStringLiteral("duty_level")));
        option.insert(QStringLiteral("duty_expansion"),
                      row.value(QStringLiteral("duty_expansion")));
        options.append(option);
    }

    if (options == m_dutyOptions)
        return;
    m_dutyOptions = options;
    Q_EMIT optionsChanged();
}

QVariantList StatisticsController::trendBuckets() const
{
    // Only a day series can be windowed to "the last 7 days"; a week or month
    // series is shown whole rather than truncated to something the label would
    // then misdescribe.
    if (m_trendMode != QLatin1String("day") || m_trendWindowDays >= 30)
        return m_trendBuckets;
    if (m_trendBuckets.size() <= m_trendWindowDays)
        return m_trendBuckets;
    return m_trendBuckets.mid(m_trendBuckets.size() - m_trendWindowDays);
}

QVariantList StatisticsController::resultBuckets() const
{
    QVariantList out;
    const QJsonArray buckets = m_dashboard.value(QStringLiteral("result_breakdown"))
                                   .toObject()
                                   .value(QStringLiteral("buckets"))
                                   .toArray();
    for (const QJsonValue &value : buckets) {
        QVariantMap bucket = value.toObject().toVariantMap();
        const QString code = bucket.value(QStringLiteral("result")).toString();
        bucket.insert(QStringLiteral("label"), Formatters::resultLabel(code));
        bucket.insert(QStringLiteral("color_token"), Formatters::resultColorToken(code));
        out.append(bucket);
    }
    return out;
}

QVariantList StatisticsController::statCards() const
{
    const QJsonObject breakdown = m_dashboard.value(QStringLiteral("result_breakdown")).toObject();
    QHash<QString, int> counts;
    for (const QJsonValue &value : breakdown.value(QStringLiteral("buckets")).toArray()) {
        const QJsonObject bucket = value.toObject();
        counts.insert(bucket.value(QStringLiteral("result")).toString(),
                      bucket.value(QStringLiteral("count")).toInt());
    }

    const QVariant completionRate =
        m_dashboard.value(QStringLiteral("completion_rate")).toVariant();
    const QVariant leaveRate = m_dashboard.value(QStringLiteral("leave_rate")).toVariant();
    const QVariant avgDuration =
        m_dashboard.value(QStringLiteral("avg_duration_ms")).toVariant();
    return {
        // Counts only what this software recorded; the 已有基数 typed in by hand is not in it,
        // so the card must not read as a lifetime total.
        statCard(QString::fromUtf8("已记录次数"),
                 QString::number(m_dashboard.value(QStringLiteral("attempt_count")).toInt())),
        statCard(QString::fromUtf8("完成次数"),
                 QString::number(m_dashboard.value(QStringLiteral("completed_count")).toInt())),
        statCard(QString::fromUtf8("通关率"), Formatters::percent(completionRate)),
        statCard(QString::fromUtf8("退出率"), Formatters::percent(leaveRate)),
        statCard(QString::fromUtf8("平均耗时"), Formatters::duration(avgDuration)),
        statCard(QString::fromUtf8("近 30 天完成"), QString::number(m_completedLast30Days)),
        statCard(QString::fromUtf8("断线 / 中断"),
                 QStringLiteral("%1 / %2")
                     .arg(counts.value(QStringLiteral("DISCONNECTED")))
                     .arg(counts.value(QStringLiteral("INTERRUPTED")))),
        statCard(QString::fromUtf8("未知结果"),
                 QString::number(counts.value(QStringLiteral("UNKNOWN")))),
    };
}

int StatisticsController::goalCount() const
{
    return m_dashboard.value(QStringLiteral("goal_count")).toInt(2000);
}

int StatisticsController::baselineCount() const
{
    return m_dashboard.value(QStringLiteral("baseline_completed_count")).toInt(0);
}

int StatisticsController::achievementProgress() const
{
    // The Collector's figure: baseline plus the COMPLETED runs that count
    // towards the goal. baseline + completed_count would also count a run the
    // user took off the goal (review OK-1). Before the first answer there is
    // only the baseline, which is 0 then as well.
    const QJsonValue progress = m_dashboard.value(QStringLiteral("achievement_progress"));
    return progress.isDouble() ? progress.toInt() : baselineCount();
}

int StatisticsController::remainingCount() const
{
    const QJsonValue remaining = m_dashboard.value(QStringLiteral("remaining"));
    return remaining.isDouble() ? remaining.toInt() : qMax(0, goalCount() - achievementProgress());
}

int StatisticsController::pendingReviewCount() const
{
    return m_dashboard.value(QStringLiteral("unfinished_pending_review")).toInt(0);
}

void StatisticsController::forgetAchievementSettings()
{
    ++m_connectionGeneration;
    if (!m_achievementSettingsLoaded)
        return;
    m_achievementSettingsLoaded = false;
    Q_EMIT achievementSettingsLoadedChanged();
}

void StatisticsController::updateAchievementBaseline(int goal, int baseline, const QString &reason)
{
    if (!m_backend)
        return;
    if (!m_achievementSettingsLoaded) {
        // The form was filled from defaults, not from what is stored: a goal-only
        // save would send baseline 0, a real change, and wipe the stored baseline
        // (audit 2026-10-03, CS7-D3). The pages do not offer 保存 in this state.
        const QString message = QString::fromUtf8("还没有读到已保存的目标与基数，请稍候再保存。");
        Q_EMIT baselineFailed(QStringLiteral("ERR_INTERNAL"), message);
        Q_EMIT mutationFailed(QStringLiteral("ERR_INTERNAL"), message);
        Q_EMIT toastRequested(message);
        return;
    }
    // Counted before the request: a pipe that is down answers inside the call.
    if (m_baselineSavesInFlight++ == 0)
        Q_EMIT baselineSavingChanged();
    m_backend->updateAchievementBaseline(goal, baseline, reason)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code,
                                const QString &message) {
            // Before the outcome, so whoever takes the answer may already send again.
            if (--m_baselineSavesInFlight == 0)
                Q_EMIT baselineSavingChanged();
            if (!ok) {
                // Baseline refusals need to stay next to the field, so the
                // settings card gets its own signal rather than only a toast.
                Q_EMIT baselineFailed(code, message);
                Q_EMIT mutationFailed(code, message);
                Q_EMIT toastRequested(message.isEmpty() ? code : message);
                return;
            }
            const int savedBaseline =
                payload.value(QStringLiteral("baseline_completed_count")).toInt();
            const int savedGoal = payload.value(QStringLiteral("goal_count")).toInt();
            Q_EMIT baselineSaved(savedGoal, savedBaseline);
            // The reply carries no achievement_progress (contracts/ipc-v1.schema.json);
            // the figure comes from the read that follows, and when that read
            // fails the toast names none rather than a wrong one (CS7-D1). The
            // Collector also publishes stats_invalidated for this change, but the
            // read here keeps the page correct even without the event.
            requestDashboard([this, savedBaseline, savedGoal](bool reread) {
                const QJsonValue progress =
                    m_dashboard.value(QStringLiteral("achievement_progress"));
                if (reread && progress.isDouble()) {
                    Q_EMIT toastRequested(
                        QString::fromUtf8("基数已设为 %1 · 目标 %2 · 进度 %3 · 已重算")
                            .arg(savedBaseline)
                            .arg(savedGoal)
                            .arg(progress.toInt()));
                } else {
                    Q_EMIT toastRequested(QString::fromUtf8("基数已设为 %1 · 目标 %2 · 已保存")
                                              .arg(savedBaseline)
                                              .arg(savedGoal));
                }
            });
            refreshTrend();
        });
}

} // namespace mr
