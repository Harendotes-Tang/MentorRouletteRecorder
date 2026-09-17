// ---------------------------------------------------------------------------
// MockBackend, part two: filtering, paging and the statistics.
//
// Every number produced here follows docs/statistics-definitions.md literally;
// the awkward parts carry the section number they come from.
// ---------------------------------------------------------------------------

#include "MockBackend.h"
#include "MockData.h"

#include <QDateTime>
#include <QJsonValue>
#include <QMap>
#include <QSet>
#include <QTimeZone>

#include <algorithm>

using namespace mr::mock;

namespace {

bool matchesStringArray(const QJsonObject &filter, const QString &key,
                        const QJsonValue &value)
{
    const QJsonArray allowed = filter.value(key).toArray();
    if (allowed.isEmpty())
        return true;
    for (const QJsonValue &entry : allowed) {
        if (entry == value)
            return true;
    }
    return false;
}

bool matchesIntArray(const QJsonObject &filter, const QString &key,
                     const QJsonValue &value)
{
    const QJsonArray allowed = filter.value(key).toArray();
    if (allowed.isEmpty())
        return true;
    for (const QJsonValue &entry : allowed) {
        if (value.isNull() && entry.isNull())
            return true;
        if (!value.isNull() && entry.toInt(-1) == value.toInt(-2))
            return true;
    }
    return false;
}

QJsonValue fieldForSort(const QJsonObject &run, const QString &field)
{
    return run.value(field);
}

bool lessThan(const QJsonObject &a, const QJsonObject &b, const QString &field)
{
    const QJsonValue left = fieldForSort(a, field);
    const QJsonValue right = fieldForSort(b, field);

    if (left.isNull() != right.isNull())
        return left.isNull(); // nulls sort first ascending
    if (left.isDouble() && right.isDouble())
        return left.toDouble() < right.toDouble();
    return left.toString() < right.toString();
}

/// docs/statistics-definitions.md 8 - only COMPLETED runs with a plausible,
/// non-negative duration and a correctly ordered pair of timestamps.
bool countsTowardsAverageDuration(const QJsonObject &run)
{
    if (run.value(QStringLiteral("result")).toString() != QLatin1String("COMPLETED"))
        return false;
    const QJsonValue duration = run.value(QStringLiteral("duration_ms"));
    if (duration.isNull() || duration.toDouble(-1.0) < 0.0)
        return false;
    const QDateTime entered = fromIso(run.value(QStringLiteral("entered_at_utc")));
    const QDateTime ended = fromIso(run.value(QStringLiteral("ended_at_utc")));
    if (!entered.isValid() || !ended.isValid())
        return false;
    return entered <= ended;
}

} // namespace

namespace mr {

// ---------------------------------------------------------------------------
// Candidate selection
// ---------------------------------------------------------------------------

QList<QJsonObject> MockBackend::selectRuns(const QJsonObject &filter,
                                           bool forStatistics) const
{
    const bool includeDeleted =
        !forStatistics && filter.value(QStringLiteral("include_deleted")).toBool(false);
    const bool correctedOnly = filter.value(QStringLiteral("corrected_only")).toBool(false);
    const bool withReflection = filter.value(QStringLiteral("with_reflection")).toBool(false);
    const QString text = filter.value(QStringLiteral("text")).toString().trimmed();

    const QString dateField = filter.value(QStringLiteral("date_field"))
                                  .toString(QStringLiteral("entered_at_utc"));
    const QDateTime from = fromIso(filter.value(QStringLiteral("from_utc")));
    const QDateTime to = fromIso(filter.value(QStringLiteral("to_utc")));

    QList<QJsonObject> selected;
    selected.reserve(m_runs.size());

    for (const QJsonValue &value : m_runs) {
        const QJsonObject run = value.toObject();

        // Soft-deleted runs never take part in a statistic, whatever the
        // filter says (docs/statistics-definitions.md 0).
        if (isSoftDeleted(run) && !includeDeleted)
            continue;
        if (forStatistics && !isConfirmedMentor(run))
            continue;
        if (correctedOnly && !run.value(QStringLiteral("manually_corrected")).toBool(false))
            continue;
        // 待复核: like 有心得, the predicate only narrows. A false value in the
        // filter is no filter at all, never "show me the confirmed ones".
        if (filter.value(QStringLiteral("pending_review")).toBool(false)
            && !run.value(QStringLiteral("pending_review")).toBool(false)) {
            continue;
        }
        // 有心得: the chip narrows the list to runs carrying a reflection; it
        // never widens it, so a false value is simply no filter at all.
        if (withReflection && !run.value(QStringLiteral("reflection")).isObject())
            continue;

        if (from.isValid() || to.isValid()) {
            const QDateTime moment = fromIso(run.value(dateField));
            if (!moment.isValid())
                continue;
            if (from.isValid() && moment < from)
                continue;
            if (to.isValid() && moment > to)
                continue;
        }

        if (!matchesIntArray(filter, QStringLiteral("content_id"),
                             run.value(QStringLiteral("content_id"))))
            continue;
        if (!matchesIntArray(filter, QStringLiteral("job_id"),
                             run.value(QStringLiteral("job_id"))))
            continue;
        if (!matchesStringArray(filter, QStringLiteral("duty_category"),
                                run.value(QStringLiteral("duty_category"))))
            continue;
        if (!matchesStringArray(filter, QStringLiteral("result"),
                                run.value(QStringLiteral("result"))))
            continue;
        if (!matchesStringArray(filter, QStringLiteral("source"),
                                run.value(QStringLiteral("source"))))
            continue;

        if (!text.isEmpty()) {
            const QString haystack =
                run.value(QStringLiteral("duty_name")).toString()
                + run.value(QStringLiteral("job_name")).toString()
                + run.value(QStringLiteral("note")).toString();
            if (!haystack.contains(text, Qt::CaseInsensitive))
                continue;
        }

        selected.append(run);
    }

    return selected;
}

QJsonObject MockBackend::queryRunsPayload(const QJsonObject &payload) const
{
    const QJsonObject filter = payload.value(QStringLiteral("filter")).toObject();
    QList<QJsonObject> rows = selectRuns(filter, false);

    const QJsonObject sort = payload.value(QStringLiteral("sort")).toObject();
    const QString field =
        sort.value(QStringLiteral("field")).toString(QStringLiteral("entered_at_utc"));
    const bool ascending =
        sort.value(QStringLiteral("direction")).toString(QStringLiteral("desc"))
        == QLatin1String("asc");

    std::stable_sort(rows.begin(), rows.end(),
                     [&](const QJsonObject &a, const QJsonObject &b) {
                         return ascending ? lessThan(a, b, field) : lessThan(b, a, field);
                     });

    const int total = int(rows.size());
    const int pageSize = qBound(1, payload.value(QStringLiteral("page_size")).toInt(50), 200);
    const int pageCount = qMax(1, (total + pageSize - 1) / pageSize);
    const int page = qBound(1, payload.value(QStringLiteral("page")).toInt(1), pageCount);

    QJsonArray items;
    for (int i = (page - 1) * pageSize; i < qMin(total, page * pageSize); ++i)
        items.append(rows.at(i));

    QJsonObject pageInfo;
    pageInfo.insert(QStringLiteral("page"), page);
    pageInfo.insert(QStringLiteral("page_size"), pageSize);
    pageInfo.insert(QStringLiteral("total"), total);

    QJsonObject result;
    result.insert(QStringLiteral("items"), items);
    result.insert(QStringLiteral("page_info"), pageInfo);
    return result;
}

// ---------------------------------------------------------------------------
// Statistics
// ---------------------------------------------------------------------------

QJsonObject MockBackend::resultStats(const QJsonObject &filter) const
{
    const QList<QJsonObject> rows = selectRuns(filter, true);

    QMap<QString, int> counts;
    int attempts = 0;
    for (const QJsonObject &run : rows) {
        counts[run.value(QStringLiteral("result")).toString()] += 1;
        if (hasEntered(run))
            ++attempts;
    }

    // All six buckets are emitted even when empty, and are never merged
    // (docs/statistics-definitions.md 9).
    static const char *kOrder[] = {
        "COMPLETED", "LEFT_OR_ABANDONED", "CANCELLED_BEFORE_ENTRY",
        "DISCONNECTED", "INTERRUPTED", "UNKNOWN",
    };

    QJsonArray buckets;
    for (const char *code : kOrder) {
        const QString key = QString::fromLatin1(code);
        const int count = counts.value(key, 0);

        // CANCELLED_BEFORE_ENTRY never entered a duty, so it has no share of
        // attempt_count; its share is always null.
        const bool shareDefined =
            attempts > 0 && key != QLatin1String("CANCELLED_BEFORE_ENTRY");

        QJsonObject bucket;
        bucket.insert(QStringLiteral("result"), key);
        bucket.insert(QStringLiteral("count"), count);
        bucket.insert(QStringLiteral("share"),
                      nullableNumber(shareDefined, double(count) / double(attempts)));
        buckets.append(bucket);
    }

    QJsonObject stats;
    stats.insert(QStringLiteral("attempt_count"), attempts);
    stats.insert(QStringLiteral("buckets"), buckets);
    return stats;
}

QJsonObject MockBackend::trendSeries(const QJsonObject &filter,
                                     const QString &granularity) const
{
    // Same shape and the same rules as the Collector's SQL aggregation
    // (docs/statistics-definitions.md 12.1): buckets are UTC, the window is
    // fixed-length, empty buckets are present, and only COMPLETED counts.
    const QString mode = granularity.isEmpty() ? QStringLiteral("day") : granularity;
    const QDateTime nowUtc = m_now.toUTC();
    const QDate today = nowUtc.date();

    QList<QDateTime> starts;
    if (mode == QLatin1String("week")) {
        const QDate thisWeek = today.addDays(-(today.dayOfWeek() - 1));
        for (int i = 11; i >= 0; --i)
            starts.append(QDateTime(thisWeek.addDays(-7 * i), QTime(0, 0), QTimeZone::UTC));
    } else if (mode == QLatin1String("month")) {
        const QDate thisMonth(today.year(), today.month(), 1);
        for (int i = 5; i >= 0; --i)
            starts.append(QDateTime(thisMonth.addMonths(-i), QTime(0, 0), QTimeZone::UTC));
    } else {
        for (int i = 29; i >= 0; --i)
            starts.append(QDateTime(today.addDays(-i), QTime(0, 0), QTimeZone::UTC));
    }

    QList<int> counts;
    counts.fill(0, starts.size());

    for (const QJsonObject &run : selectRuns(filter, true)) {
        if (run.value(QStringLiteral("result")).toString() != QLatin1String("COMPLETED"))
            continue;
        const QDateTime matched = QDateTime::fromString(
            run.value(QStringLiteral("matched_at_utc")).toString(), Qt::ISODateWithMs);
        if (!matched.isValid())
            continue;

        const QDateTime matchedUtc = matched.toUTC();
        for (int i = starts.size() - 1; i >= 0; --i) {
            if (matchedUtc >= starts.at(i)) {
                ++counts[i];
                break;
            }
        }
    }

    QJsonArray buckets;
    for (int i = 0; i < starts.size(); ++i) {
        QJsonObject bucket;
        bucket.insert(QStringLiteral("start_utc"),
                      starts.at(i).toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ")));
        bucket.insert(QStringLiteral("completed_count"), counts.at(i));
        buckets.append(bucket);
    }

    QJsonObject trend;
    trend.insert(QStringLiteral("granularity"), mode);
    trend.insert(QStringLiteral("buckets"), buckets);
    return trend;
}

QJsonObject MockBackend::dashboardStats(const QJsonObject &filter,
                                        const QString &granularity) const
{
    const QList<QJsonObject> rows = selectRuns(filter, true);

    int attempts = 0;
    int completed = 0;
    int left = 0;
    int pendingReview = 0;
    double durationSum = 0.0;
    int durationCount = 0;

    for (const QJsonObject &run : rows) {
        const QString result = run.value(QStringLiteral("result")).toString();
        if (hasEntered(run))
            ++attempts;
        if (result == QLatin1String("COMPLETED"))
            ++completed;
        if (result == QLatin1String("LEFT_OR_ABANDONED"))
            ++left;
        // The flag the Collector writes, not a heuristic re-derived from the
        // result: pending_review is what CrashRecoveryService sets and what
        // the 确认 action clears.
        if (run.value(QStringLiteral("pending_review")).toBool(false))
            ++pendingReview;
        if (countsTowardsAverageDuration(run)) {
            durationSum += run.value(QStringLiteral("duration_ms")).toDouble();
            ++durationCount;
        }
    }

    // achievement_progress ignores the filter entirely: it is always the
    // whole-database count (docs/statistics-definitions.md 4).
    int goalRuns = 0;
    for (const QJsonValue &value : m_runs) {
        const QJsonObject run = value.toObject();
        if (isSoftDeleted(run) || !isConfirmedMentor(run))
            continue;
        if (run.value(QStringLiteral("result")).toString() != QLatin1String("COMPLETED"))
            continue;
        if (!run.value(QStringLiteral("contributes_to_goal")).toBool(true))
            continue;
        ++goalRuns;
    }

    const int progress = m_baselineCompletedCount + goalRuns;
    const int remaining = qMax(m_goalCount - progress, 0);

    QJsonObject stats;
    stats.insert(QStringLiteral("attempt_count"), attempts);
    stats.insert(QStringLiteral("completed_count"), completed);
    stats.insert(QStringLiteral("baseline_completed_count"), m_baselineCompletedCount);
    stats.insert(QStringLiteral("achievement_progress"), progress);
    stats.insert(QStringLiteral("goal_count"), m_goalCount);
    stats.insert(QStringLiteral("remaining"), remaining);
    stats.insert(QStringLiteral("completion_rate"),
                 nullableNumber(attempts > 0, double(completed) / double(attempts)));
    // leave_rate counts LEFT_OR_ABANDONED only. DISCONNECTED, INTERRUPTED and
    // UNKNOWN stay in their own buckets (docs/statistics-definitions.md 7).
    stats.insert(QStringLiteral("leave_rate"),
                 nullableNumber(attempts > 0, double(left) / double(attempts)));
    stats.insert(QStringLiteral("avg_duration_ms"),
                 nullableNumber(durationCount > 0, durationSum / double(durationCount)));
    stats.insert(QStringLiteral("result_breakdown"), resultStats(filter));
    stats.insert(QStringLiteral("unfinished_pending_review"), pendingReview);
    stats.insert(QStringLiteral("trend"), trendSeries(filter, granularity));
    return stats;
}

namespace {

struct Aggregate {
    int attempts = 0;
    int completed = 0;
    double durationSum = 0.0;
    int durationCount = 0;
    QString label;
    QString extra;
    QString lastSeen;
};

QJsonObject aggregateToRow(const Aggregate &aggregate)
{
    QJsonObject row;
    row.insert(QStringLiteral("attempt_count"), aggregate.attempts);
    row.insert(QStringLiteral("completed_count"), aggregate.completed);
    row.insert(QStringLiteral("completion_rate"),
               nullableNumber(aggregate.attempts > 0,
                              double(aggregate.completed) / double(aggregate.attempts)));
    row.insert(QStringLiteral("avg_duration_ms"),
               nullableNumber(aggregate.durationCount > 0,
                              aggregate.durationSum / double(aggregate.durationCount)));
    return row;
}

} // namespace

QJsonArray MockBackend::dungeonStats(const QJsonObject &filter) const
{
    const QList<QJsonObject> rows = selectRuns(filter, true);

    // Aggregated by content_id. Runs without one collapse into a single
    // "未知副本" row (docs/statistics-definitions.md 10).
    QMap<int, Aggregate> byContent; // key -1 == unknown
    QMap<int, QString> categories;

    for (const QJsonObject &run : rows) {
        if (!hasEntered(run))
            continue;
        const QJsonValue contentId = run.value(QStringLiteral("content_id"));
        const int key = contentId.isNull() ? -1 : contentId.toInt();

        Aggregate &aggregate = byContent[key];
        ++aggregate.attempts;
        if (run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED"))
            ++aggregate.completed;
        if (countsTowardsAverageDuration(run)) {
            aggregate.durationSum += run.value(QStringLiteral("duration_ms")).toDouble();
            ++aggregate.durationCount;
        }

        const QString name = run.value(QStringLiteral("duty_name")).toString();
        if (!name.isEmpty())
            aggregate.label = name;
        const QString category = run.value(QStringLiteral("duty_category")).toString();
        if (!category.isEmpty())
            categories.insert(key, category);
        const QString matched = run.value(QStringLiteral("matched_at_utc")).toString();
        if (matched > aggregate.lastSeen)
            aggregate.lastSeen = matched;
        const QJsonValue expansion = run.value(QStringLiteral("duty_expansion"));
        const QJsonValue level = run.value(QStringLiteral("duty_level"));
        if (!expansion.isNull() && !level.isNull()) {
            aggregate.extra = QStringLiteral("%1 · %2级")
                                  .arg(expansion.toString())
                                  .arg(level.toInt());
        }
    }

    QList<QJsonObject> out;
    for (auto it = byContent.constBegin(); it != byContent.constEnd(); ++it) {
        QJsonObject row = aggregateToRow(it.value());
        row.insert(QStringLiteral("content_id"),
                   it.key() < 0 ? QJsonValue(QJsonValue::Null) : QJsonValue(it.key()));
        row.insert(QStringLiteral("duty_name"),
                   it.value().label.isEmpty() ? QString::fromUtf8("未知副本")
                                              : it.value().label);
        row.insert(QStringLiteral("duty_category"),
                   categories.contains(it.key()) ? QJsonValue(categories.value(it.key()))
                                                 : QJsonValue(QJsonValue::Null));
        row.insert(QStringLiteral("duty_meta"), it.value().extra);
        row.insert(QStringLiteral("last_seen_utc"),
                   it.value().lastSeen.isEmpty() ? QJsonValue(QJsonValue::Null)
                                                 : QJsonValue(it.value().lastSeen));
        out.append(row);
    }

    std::stable_sort(out.begin(), out.end(),
                     [](const QJsonObject &a, const QJsonObject &b) {
                         return a.value(QStringLiteral("attempt_count")).toInt()
                              > b.value(QStringLiteral("attempt_count")).toInt();
                     });

    QJsonArray array;
    for (const QJsonObject &row : std::as_const(out))
        array.append(row);
    return array;
}

QJsonArray MockBackend::jobStats(const QJsonObject &filter) const
{
    const QList<QJsonObject> rows = selectRuns(filter, true);

    // Aggregated by job_id. job_id null is its own row named "未知" and is
    // never folded into a real job (docs/statistics-definitions.md 11).
    QMap<int, Aggregate> byJob; // key -1 == unknown
    QMap<int, QString> roles;
    QMap<int, QString> roleGroups;

    for (const QJsonObject &run : rows) {
        if (!hasEntered(run))
            continue;
        const QJsonValue jobId = run.value(QStringLiteral("job_id"));
        const int key = jobId.isNull() ? -1 : jobId.toInt();

        Aggregate &aggregate = byJob[key];
        ++aggregate.attempts;
        if (run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED"))
            ++aggregate.completed;
        if (countsTowardsAverageDuration(run)) {
            aggregate.durationSum += run.value(QStringLiteral("duration_ms")).toDouble();
            ++aggregate.durationCount;
        }
        const QString name = run.value(QStringLiteral("job_name")).toString();
        if (!name.isEmpty())
            aggregate.label = name;
        roles.insert(key, run.value(QStringLiteral("role")).toString());
        const QString group = run.value(QStringLiteral("role_group")).toString();
        if (!group.isEmpty())
            roleGroups.insert(key, group);
    }

    QList<QJsonObject> out;
    for (auto it = byJob.constBegin(); it != byJob.constEnd(); ++it) {
        QJsonObject row = aggregateToRow(it.value());
        row.insert(QStringLiteral("job_id"),
                   it.key() < 0 ? QJsonValue(QJsonValue::Null) : QJsonValue(it.key()));
        row.insert(QStringLiteral("job_name"),
                   it.value().label.isEmpty() ? QString::fromUtf8("未知") : it.value().label);
        row.insert(QStringLiteral("role"),
                   roles.value(it.key(), QStringLiteral("UNKNOWN")));
        row.insert(QStringLiteral("role_group"),
                   roleGroups.value(it.key(), QString::fromUtf8("未知")));
        out.append(row);
    }

    std::stable_sort(out.begin(), out.end(),
                     [](const QJsonObject &a, const QJsonObject &b) {
                         return a.value(QStringLiteral("attempt_count")).toInt()
                              > b.value(QStringLiteral("attempt_count")).toInt();
                     });

    QJsonArray array;
    for (const QJsonObject &row : std::as_const(out))
        array.append(row);
    return array;
}

} // namespace mr
