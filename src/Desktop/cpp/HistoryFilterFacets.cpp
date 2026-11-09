#include "HistoryFilterFacets.h"
#include "IBackend.h"

#include <QDateTime>
#include <QJsonValue>
#include <QTimeZone>
#include <cmath>
#include <limits>

namespace mr {
HistoryFilterFacets::HistoryFilterFacets(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
{
    m_refreshTimer.setSingleShot(true);
    connect(&m_refreshTimer, &QTimer::timeout, this, &HistoryFilterFacets::queryBatch);
    if (backend) {
        connect(backend, &IBackend::connectionChanged, this, &HistoryFilterFacets::refresh);
        connect(backend, &QObject::destroyed, this, [this] {
            m_backend = nullptr;
            refresh();
        });
    }
}

void HistoryFilterFacets::setActive(bool active)
{
    if (m_active == active) return;
    m_active = active;
    refresh();
    Q_EMIT activeChanged();
}

void HistoryFilterFacets::setFilter(const QJsonObject &filter)
{
    if (m_filter == filter) return;
    m_filter = filter;
    refresh();
}

void HistoryFilterFacets::refresh()
{
    ++m_generation;
    m_refreshTimer.stop();
    m_dateCounts.clear();
    m_regionCounts.clear();
    m_pendingDates.clear();
    m_pendingRegions.clear();
    m_remaining = 0;
    m_failed = false;
    m_error.clear();
    m_loading = m_active && m_backend && m_backend->isConnected();
    if (m_loading)
        m_refreshTimer.start(0);
    else if (m_active)
        m_error = tr("服务未连接，无法读取筛选数量。");
    Q_EMIT changed();
}

QVariantMap HistoryFilterFacets::dateRange(const QString &preset) const
{
    const QDate today = QDate::currentDate();
    QDate first;
    if (preset == QLatin1String("today")) first = today;
    else if (preset == QLatin1String("last7")) first = today.addDays(-6);
    else if (preset == QLatin1String("last30")) first = today.addDays(-29);
    else if (preset == QLatin1String("month")) first = QDate(today.year(), today.month(), 1);
    return {{QStringLiteral("start"), first.isValid() ? first.toString(Qt::ISODate) : QString()},
            {QStringLiteral("end"), first.isValid() ? today.toString(Qt::ISODate) : QString()}};
}

void HistoryFilterFacets::queryBatch()
{
    if (!m_active || !m_backend || !m_backend->isConnected()) {
        refresh();
        return;
    }
    const quint64 generation = m_generation;
    struct Query { QString key; bool date = false; QJsonObject filter; };
    QList<Query> queries;
    QJsonObject dateFilter = m_filter;
    dateFilter.insert(QStringLiteral("date_field"), QStringLiteral("matched_at_utc"));
    for (const auto *key : {"from_utc", "to_utc", "history_from_day", "history_to_day"})
        dateFilter.remove(QString::fromLatin1(key));
    for (const auto *name : {"all", "today", "last7", "last30", "month"}) {
        const QString key = QString::fromLatin1(name);
        QJsonObject filter = dateFilter;
        const auto range = dateRange(key);
        const QDate first = QDate::fromString(range.value(QStringLiteral("start")).toString(), Qt::ISODate);
        const QDate last = QDate::fromString(range.value(QStringLiteral("end")).toString(), Qt::ISODate);
        if (first.isValid()) {
            const auto zone = QTimeZone::systemTimeZone();
            filter.insert(QStringLiteral("from_utc"), first.startOfDay(zone).toUTC().toString(Qt::ISODateWithMs));
            filter.insert(QStringLiteral("to_utc"), last.endOfDay(zone).toUTC().toString(Qt::ISODateWithMs));
        }
        queries.append({.key = key, .date = true, .filter = filter});
    }
    QJsonObject regionFilter = m_filter;
    regionFilter.insert(QStringLiteral("date_field"), QStringLiteral("matched_at_utc"));
    for (const auto *key : {"region", "history_from_day", "history_to_day"})
        regionFilter.remove(QString::fromLatin1(key));
    for (const auto *name : {"all", "CN", "TC", "GLOBAL"}) {
        const QString key = QString::fromLatin1(name);
        QJsonObject filter = regionFilter;
        if (key != QLatin1String("all")) filter.insert(QStringLiteral("region"), key);
        queries.append({.key = key, .date = false, .filter = filter});
    }
    m_remaining = static_cast<int>(queries.size());
    const QPointer<HistoryFilterFacets> self(this);
    for (const auto &query : queries) {
        auto *current = self.data();
        if (!current || generation != current->m_generation || !current->m_backend) return;
        BackendReply *reply = current->m_backend->queryRuns(query.filter, 1, 1);
        current = self.data();
        if (!current || generation != current->m_generation) return;
        reply->whenDone(this, [this, generation, key = query.key, date = query.date]
            (bool ok, const QVariantMap &payload, const QString &, const QString &) {
            if (generation != m_generation || !m_active) return;
            const QJsonValue total = QJsonObject::fromVariantMap(payload)
                .value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total"));
            const double value = total.toDouble(-1);
            const bool valid = ok && total.isDouble() && std::isfinite(value) && value >= 0
                && value <= std::numeric_limits<int>::max() && std::floor(value) == value;
            if (valid)
                (date ? m_pendingDates : m_pendingRegions).insert(key, static_cast<int>(value));
            else
                m_failed = true;
            if (--m_remaining != 0) return;
            m_loading = false;
            if (m_failed) {
                m_error = tr("读取筛选数量失败，请重试。");
            } else {
                m_dateCounts = m_pendingDates;
                m_regionCounts = m_pendingRegions;
            }
            m_pendingDates.clear();
            m_pendingRegions.clear();
            Q_EMIT changed();
        });
    }
}
} // namespace mr
