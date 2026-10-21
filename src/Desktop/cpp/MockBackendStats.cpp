// ---------------------------------------------------------------------------
// MockBackend, part two: filtering, paging and the statistics.
//
// Every number produced here follows docs/statistics-definitions.md literally;
// the awkward parts carry the section number they come from.
// ---------------------------------------------------------------------------

#include "MockBackend.h"
#include "DutyCatalog.h"
#include "JobCatalog.h"
#include "MockData.h"

#include <QDateTime>
#include <QHash>
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

/// DutyCatalog.UniqueTerritoriesOf: the zones of the duties asked for that host that duty
/// and no other. A run that observed only such a zone is a run of that duty.
QSet<qint64> zonesOfOnly(const QJsonArray &contentIds)
{
    QSet<qint64> wanted;
    for (const QJsonValue &id : contentIds) {
        if (id.isDouble())
            wanted.insert(qint64(id.toDouble()));
    }
    if (wanted.isEmpty())
        return {};
    QHash<qint64, QSet<qint64>> dutiesByZone;
    for (const QVariant &row : mr::DutyCatalog::shared()->allDuties()) {
        const QVariantMap duty = row.toMap();
        const QVariant zone = duty.value(QStringLiteral("territory_id"));
        if (zone.isValid())
            dutiesByZone[zone.toLongLong()].insert(duty.value(QStringLiteral("content_id")).toLongLong());
    }
    QSet<qint64> zones;
    for (auto it = dutiesByZone.constBegin(); it != dutiesByZone.constEnd(); ++it) {
        if (it.value().size() == 1 && wanted.contains(*it.value().constBegin()))
            zones.insert(it.key());
    }
    return zones;
}

/// RunFilterSql.AddContentIds: content_id IN (...), or - for a run that carries no
/// content id, because capture never back-infers one - a zone that hosts one of those
/// duties alone. A zone several duties share is never expanded (review S33-1).
bool matchesContentIds(const QJsonObject &filter, const QSet<qint64> &dutyZones,
                       const QJsonObject &run)
{
    if (matchesIntArray(filter, QStringLiteral("content_id"), run.value(QStringLiteral("content_id"))))
        return true;
    const QJsonValue zone = run.value(QStringLiteral("territory_id"));
    return run.value(QStringLiteral("content_id")).isNull() && zone.isDouble()
           && dutyZones.contains(qint64(zone.toDouble()));
}

QJsonValue fieldForSort(const QJsonObject &run, const QString &field)
{
    if (field == QLatin1String("history_date")) {
        for (const QString &key : {QStringLiteral("matched_at_utc"), QStringLiteral("entered_at_utc"), QStringLiteral("ended_at_utc")})
            if (run.value(key).isString()) return run.value(key);
        const QJsonObject source = run.value(QStringLiteral("import_metadata")).toObject();
        if (source.value(QStringLiteral("source_recorded_at_utc")).isString())
            return source.value(QStringLiteral("source_recorded_at_utc"));
        const QString raw = source.value(QStringLiteral("source_recorded_at")).toString();
        QDate date = QDate::fromString(raw, QStringLiteral("yyyy-MM-dd"));
        if (!date.isValid()) date = QDate::fromString(raw, QStringLiteral("yyyy/M/d"));
        if (!date.isValid()) date = QDate::fromString(raw, QStringLiteral("yyyy.M.d"));
        if (date.isValid()) return date.toString(QStringLiteral("yyyy-MM-dd")) + QStringLiteral("T00:00:00.000Z");
        return QJsonValue(QJsonValue::Null);
    }
    return run.value(field);
}

/// The raw source day has calendar semantics only when no actual/source instant exists.
QDate sourceCalendarDay(const QJsonObject &run)
{
    for (const QString &key : {QStringLiteral("matched_at_utc"), QStringLiteral("entered_at_utc"), QStringLiteral("ended_at_utc")})
        if (run.value(key).isString()) return {};
    const QJsonObject source = run.value(QStringLiteral("import_metadata")).toObject();
    if (source.value(QStringLiteral("source_recorded_at_utc")).isString()) return {};
    const QString raw = source.value(QStringLiteral("source_recorded_at")).toString();
    QDate date = QDate::fromString(raw, QStringLiteral("yyyy-MM-dd"));
    if (!date.isValid()) date = QDate::fromString(raw, QStringLiteral("yyyy/M/d"));
    if (!date.isValid()) date = QDate::fromString(raw, QStringLiteral("yyyy.M.d"));
    return date;
}

/// RunRepository.Query: ORDER BY (field IS NULL), field <direction>, run_id -
/// a missing value sorts last whichever way the list runs, and run_id breaks
/// ties (review OJ-6).
bool sortsBefore(const QJsonObject &a, const QJsonObject &b, const QString &field, bool ascending)
{
    const auto sortValue = [&field](const QJsonObject &run) {
        if (field == QLatin1String("history_date")) {
            const QDate sourceDay = sourceCalendarDay(run);
            if (sourceDay.isValid())
                return QJsonValue(isoUtc(sourceDay.startOfDay(QTimeZone::systemTimeZone())));
        }
        return fieldForSort(run, field);
    };
    const QJsonValue left = sortValue(a);
    const QJsonValue right = sortValue(b);

    if (left.isNull() != right.isNull())
        return right.isNull();
    if (!left.isNull()) {
        const bool numeric = left.isDouble() && right.isDouble();
        const bool less = numeric ? left.toDouble() < right.toDouble()
                                  : left.toString() < right.toString();
        const bool greater = numeric ? right.toDouble() < left.toDouble()
                                     : right.toString() < left.toString();
        if (less != greater)
            return ascending ? less : greater;
    }
    return a.value(QStringLiteral("run_id")).toString() < b.value(QStringLiteral("run_id")).toString();
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
    const bool deletedOnly = !forStatistics && filter.value(QStringLiteral("deleted_only")).toBool(false);
    const bool includeDeleted =
        !forStatistics && (deletedOnly || filter.value(QStringLiteral("include_deleted")).toBool(false));
    const bool correctedOnly = filter.value(QStringLiteral("corrected_only")).toBool(false);
    const bool withReflection = filter.value(QStringLiteral("with_reflection")).toBool(false);
    const QString text = filter.value(QStringLiteral("text")).toString().trimmed();
    const QString nameText = filter.value(QStringLiteral("name_text")).toString().trimmed();

    const QString dateField = filter.value(QStringLiteral("date_field"))
                                  .toString(QStringLiteral("entered_at_utc"));
    const QDateTime from = fromIso(filter.value(QStringLiteral("from_utc")));
    const QDateTime to = fromIso(filter.value(QStringLiteral("to_utc")));
    const QDate fromDay = QDate::fromString(filter.value(QStringLiteral("history_from_day")).toString(), Qt::ISODate);
    const QDate toDay = QDate::fromString(filter.value(QStringLiteral("history_to_day")).toString(), Qt::ISODate);
    const QSet<qint64> contentZones = zonesOfOnly(filter.value(QStringLiteral("content_id")).toArray());
    // RunFilterSql: pending_review true is the actionable review list, and imported
    // history - source IMPORT, import metadata, or an IMPORT creation revision - is never
    // on it (RunFilterSql.ImportedHistoryPredicate).
    const bool reviewList = filter.value(QStringLiteral("pending_review")).toBool(false);
    QSet<QString> importedByRevision;
    if (reviewList) {
        for (const QJsonValue &value : m_revisions) {
            const QJsonObject revision = value.toObject();
            if (revision.value(QStringLiteral("revision")).toInt() == 1
                && revision.value(QStringLiteral("change_kind")).toString() == QLatin1String("IMPORT"))
                importedByRevision.insert(revision.value(QStringLiteral("run_id")).toString());
        }
    }

    QList<QJsonObject> selected;
    selected.reserve(m_runs.size());

    for (const QJsonValue &value : m_runs) {
        const QJsonObject run = value.toObject();

        // Soft-deleted runs never take part in a statistic, whatever the
        // filter says (docs/statistics-definitions.md 0).
        if ((isSoftDeleted(run) && !includeDeleted) || (deletedOnly && !isSoftDeleted(run)))
            continue;
        if (!matchesStringArray(filter, QStringLiteral("run_id"), run.value(QStringLiteral("run_id"))))
            continue;
        if (forStatistics && !isConfirmedMentor(run))
            continue;
        // RunFilterSql: an automatic run still in flight - UNKNOWN, no end, no review
        // flag - has no outcome yet and counts in no statistic. One crash recovery
        // handed to the player (pending_review) is over and does (review DT4-X4).
        if (forStatistics && run.value(QStringLiteral("source")).toString() == QLatin1String("AUTO_NETWORK")
            && run.value(QStringLiteral("result")).toString() == QLatin1String("UNKNOWN")
            && !run.value(QStringLiteral("ended_at_utc")).isString()
            && !run.value(QStringLiteral("pending_review")).toBool(false)) {
            continue;
        }
        if (correctedOnly && !run.value(QStringLiteral("manually_corrected")).toBool(false))
            continue;
        // 待复核: $defs/RunFilter.pending_review has no default. true keeps the
        // flagged runs, false keeps the others, and only an omitted key is no
        // constraint (RunFilterSql; review OJ-6).
        const QJsonValue pendingReview = filter.value(QStringLiteral("pending_review"));
        if (pendingReview.isBool()
            && run.value(QStringLiteral("pending_review")).toBool(false) != pendingReview.toBool()) {
            continue;
        }
        if (reviewList
            && (run.value(QStringLiteral("source")).toString() == QLatin1String("IMPORT")
                || run.value(QStringLiteral("import_metadata")).isObject()
                || importedByRevision.contains(run.value(QStringLiteral("run_id")).toString()))) {
            continue;
        }
        // 有心得: the chip narrows the list to runs carrying a reflection; it
        // never widens it, so a false value is simply no filter at all.
        if (withReflection && !run.value(QStringLiteral("reflection")).isObject())
            continue;
        // 中途加入: true lists the runs recorded as joined in progress, false the ones
        // recorded as entered at the start; a run that says nothing matches neither.
        const QJsonValue joinedFilter = filter.value(QStringLiteral("joined_in_progress"));
        if (joinedFilter.isBool()) {
            const QJsonValue joined = run.value(QStringLiteral("joined_in_progress"));
            if (!joined.isBool() || joined.toBool() != joinedFilter.toBool())
                continue;
        }

        if (from.isValid() || to.isValid()) {
            const QDateTime moment = fromIso(fieldForSort(run, dateField));
            const QDate calendar = dateField == QLatin1String("history_date") ? sourceCalendarDay(run) : QDate();
            if (!moment.isValid())
                continue;
            if (from.isValid() && (calendar.isValid() && fromDay.isValid() ? calendar < fromDay : moment < from))
                continue;
            if (to.isValid() && (calendar.isValid() && toDay.isValid() ? calendar > toDay : moment > to))
                continue;
        }

        if (!matchesContentIds(filter, contentZones, run))
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

        if (!text.isEmpty() || !nameText.isEmpty()) {
            const QString haystack =
                run.value(QStringLiteral("duty_name")).toString()
                + run.value(QStringLiteral("job_name")).toString()
                + run.value(QStringLiteral("note")).toString();
            const bool originalMatch = !text.isEmpty() && haystack.contains(text, Qt::CaseInsensitive);
            const bool nameMatch = !nameText.isEmpty()
                && (run.value(QStringLiteral("duty_name")).toString().contains(nameText, Qt::CaseInsensitive)
                    || run.value(QStringLiteral("job_name")).toString().contains(nameText, Qt::CaseInsensitive));
            if (!originalMatch && !nameMatch)
                continue;
        }

        selected.append(run);
    }

    return selected;
}

QJsonObject MockBackend::queryRunCalendarDaysPayload(const QJsonObject &payload) const
{
    QJsonObject filter = payload.value(QStringLiteral("filter")).toObject();
    for (const QString &key : {QStringLiteral("from_utc"), QStringLiteral("to_utc"),
                               QStringLiteral("history_from_day"), QStringLiteral("history_to_day")})
        filter.remove(key);
    const QString month = payload.value(QStringLiteral("month")).toString();
    QSet<QString> unique;
    // selectRuns leaves historical imports out of the actionable review filter.
    for (const QJsonObject &run : selectRuns(filter, false)) {
        QDate day = sourceCalendarDay(run);
        if (!day.isValid())
            day = fromIso(fieldForSort(run, QStringLiteral("history_date"))).toLocalTime().date();
        if (day.isValid() && day.toString(QStringLiteral("yyyy-MM")) == month)
            unique.insert(day.toString(Qt::ISODate));
    }
    QStringList sorted = unique.values();
    sorted.sort();
    QJsonArray days;
    for (const QString &day : sorted) days.append(day);
    return {{QStringLiteral("month"), month}, {QStringLiteral("days"), days}};
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
                         return sortsBefore(a, b, field, ascending);
                     });

    // RunRepository.Query: the page asked for, even past the end (then empty). The request
    // path has already refused a page or page_size out of range (RequestParsers.Paging).
    const int total = int(rows.size());
    const int pageSize = payload.value(QStringLiteral("page_size")).toInt(50);
    const int page = payload.value(QStringLiteral("page")).toInt(1);

    QJsonArray items;
    const qint64 first = qint64(page - 1) * pageSize;
    for (qint64 i = first; i < qMin(qint64(total), first + pageSize); ++i)
        items.append(rows.at(int(i)));

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
        const QDateTime matched = fromIso(fieldForSort(run, QStringLiteral("history_date")));
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
    int joinedInProgress = 0;
    int entryKnown = 0;
    double durationSum = 0.0;
    int durationCount = 0;

    for (const QJsonObject &run : rows) {
        const QString result = run.value(QStringLiteral("result")).toString();
        if (hasEntered(run)) {
            ++attempts;
            // Joined in progress over the attempts, and its share over the attempts whose
            // entry was seen at all (docs/statistics-definitions.md 12.2).
            const QJsonValue joined = run.value(QStringLiteral("joined_in_progress"));
            if (joined.isBool()) {
                ++entryKnown;
                if (joined.toBool())
                    ++joinedInProgress;
            }
        }
        if (result == QLatin1String("COMPLETED"))
            ++completed;
        // Among the attempts only, like StatisticsRepository.BuildDashboard
        // (review OJ-6): a run that never entered has no share of attempt_count.
        if (result == QLatin1String("LEFT_OR_ABANDONED") && hasEntered(run))
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
    // whole-database count (docs/statistics-definitions.md 4). The baseline is what was
    // completed before the software was installed, and every contributing completion
    // it recorded is added on top, whenever it ended; baseline_effective_at is
    // information only (owner's report B3-1, withdrawing the CS7-D4 cutoff).
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
    stats.insert(QStringLiteral("joined_in_progress_count"), joinedInProgress);
    stats.insert(QStringLiteral("joined_in_progress_rate"),
                 nullableNumber(entryKnown > 0, double(joinedInProgress) / double(entryKnown)));
    stats.insert(QStringLiteral("trend"), trendSeries(filter, granularity));
    return stats;
}

namespace {

struct Aggregate {
    int attempts = 0;
    int completed = 0;
    int joinedInProgress = 0;
    double durationSum = 0.0;
    int durationCount = 0;
};

/// StatisticsRepository's per-group counts. Every run of the group is in it, an attempt or
/// not: attempt_count counts the attempts (IsAttempt), completed_count every COMPLETED
/// run, and a group whose runs never entered is still a row, with zero attempts.
void accumulate(Aggregate &aggregate, const QJsonObject &run)
{
    if (hasEntered(run)) {
        ++aggregate.attempts;
        const QJsonValue joined = run.value(QStringLiteral("joined_in_progress"));
        if (joined.isBool() && joined.toBool())
            ++aggregate.joinedInProgress;
    }
    if (run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED"))
        ++aggregate.completed;
    if (countsTowardsAverageDuration(run)) {
        aggregate.durationSum += run.value(QStringLiteral("duration_ms")).toDouble();
        ++aggregate.durationCount;
    }
}

QJsonObject aggregateToRow(const Aggregate &aggregate)
{
    QJsonObject row;
    row.insert(QStringLiteral("attempt_count"), aggregate.attempts);
    row.insert(QStringLiteral("completed_count"), aggregate.completed);
    row.insert(QStringLiteral("joined_in_progress_count"), aggregate.joinedInProgress);
    row.insert(QStringLiteral("completion_rate"),
               nullableNumber(aggregate.attempts > 0,
                              double(aggregate.completed) / double(aggregate.attempts)));
    row.insert(QStringLiteral("avg_duration_ms"),
               nullableNumber(aggregate.durationCount > 0,
                              aggregate.durationSum / double(aggregate.durationCount)));
    return row;
}

/// A JSON number as an id, or -1 for null / absent: no real id is negative.
qint64 idOf(const QJsonValue &value)
{
    return value.isDouble() ? qint64(value.toDouble()) : qint64(-1);
}

/// The catalogue's duties by zone, lowest content id first. DutyCatalog.FindByTerritory
/// names a zone by its first duty; FindUniqueByTerritory takes it as the duty's identity
/// only when it is the zone's one duty.
QHash<qint64, QList<qint64>> dutiesByZone()
{
    QHash<qint64, QList<qint64>> zones;
    for (const QVariant &row : mr::DutyCatalog::shared()->allDuties()) {
        const QVariantMap duty = row.toMap();
        const QVariant zone = duty.value(QStringLiteral("territory_id"));
        if (!zone.isValid() || zone.isNull())
            continue;
        QList<qint64> &ids = zones[zone.toLongLong()];
        const qint64 id = duty.value(QStringLiteral("content_id")).toLongLong();
        if (!ids.contains(id))
            ids.append(id);
    }
    for (QList<qint64> &ids : zones)
        std::sort(ids.begin(), ids.end());
    return zones;
}

} // namespace

QJsonArray MockBackend::dungeonStats(const QJsonObject &filter) const
{
    // StatisticsRepository.GetDungeonStats (docs/statistics-definitions.md 10): grouped by
    // duty identity, DutyKey - the content_id when the run observed one, else the one duty
    // the observed zone hosts, else the zone itself with a null content_id when several
    // duties share it, and (null, null) for a run with no duty identity at all - including a
    // zone the duty table does not know, which names no duty either. A zone several duties
    // share never stands for one of them. The (null, null) row is always 未知副本.
    struct Group {
        qint64 contentId = -1;
        qint64 zone = -1;
        Aggregate aggregate;
        QString name;
        QJsonValue category;
        QString nameUpdatedAt;
        QString lastSeen;
    };
    const QHash<qint64, QList<qint64>> zones = dutiesByZone();
    QList<Group> groups; // in order of first appearance, like LINQ's GroupBy
    QHash<QPair<qint64, qint64>, int> indexOf;

    for (const QJsonObject &run : selectRuns(filter, true)) {
        qint64 contentId = idOf(run.value(QStringLiteral("content_id")));
        qint64 zone = -1;
        if (contentId < 0) {
            const qint64 observed = idOf(run.value(QStringLiteral("territory_id")));
            const QList<qint64> hosted = zones.value(observed);
            if (hosted.size() == 1)
                contentId = hosted.first();
            else if (!hosted.isEmpty())
                zone = observed;
        }
        const QPair<qint64, qint64> key{contentId, zone};
        if (!indexOf.contains(key)) {
            indexOf.insert(key, int(groups.size()));
            groups.append(Group{contentId, zone});
        }
        Group &group = groups[indexOf.value(key)];
        accumulate(group.aggregate, run);

        // The name and category of the most recently updated run that names the duty. A row
        // without a duty identity is 未知副本, whatever free text a run in it calls its duty.
        const QString name = run.value(QStringLiteral("duty_name")).toString();
        const QString updated = run.value(QStringLiteral("updated_at_utc")).toString();
        const bool identified = contentId >= 0 || zone >= 0;
        if (identified && !name.trimmed().isEmpty() && (group.name.isEmpty() || updated > group.nameUpdatedAt)) {
            group.name = name;
            group.category = run.value(QStringLiteral("duty_category"));
            group.nameUpdatedAt = updated;
        }
        // MAX(matched_at_utc): when this duty was last matched into, attempt or not.
        const QString matched = run.value(QStringLiteral("matched_at_utc")).toString();
        if (matched > group.lastSeen)
            group.lastSeen = matched;
    }

    QList<QJsonObject> out;
    for (const Group &group : std::as_const(groups)) {
        // What the catalogue says: the duty itself, or the lowest content id of the zone.
        const QList<qint64> hosted = zones.value(group.zone);
        const QVariantMap mapped = group.contentId >= 0
            ? mr::DutyCatalog::shared()->lookup(group.contentId)
            : hosted.isEmpty() ? QVariantMap() : mr::DutyCatalog::shared()->lookup(hosted.first());
        const QString mappedName = mapped.value(QStringLiteral("duty_name")).toString();
        const QString mappedCategory = mapped.value(QStringLiteral("duty_category")).toString();

        QJsonObject row = aggregateToRow(group.aggregate);
        row.insert(QStringLiteral("content_id"),
                   group.contentId < 0 ? QJsonValue(QJsonValue::Null) : QJsonValue(group.contentId));
        row.insert(QStringLiteral("duty_name"),
                   !group.name.isEmpty() ? group.name
                   : !mappedName.isEmpty() ? mappedName
                                           : QString::fromUtf8("未知副本"));
        row.insert(QStringLiteral("duty_category"),
                   !group.name.isEmpty() && group.category.isString() && !group.category.toString().isEmpty()
                       ? group.category
                       : !mappedCategory.isEmpty() ? QJsonValue(mappedCategory) : QJsonValue(QJsonValue::Null));
        // No duty_meta: $defs/DungeonStatsRow cannot carry it. DungeonStatsModel
        // derives it from the duty catalogue, in the interface language.
        row.insert(QStringLiteral("last_seen_utc"),
                   group.lastSeen.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(group.lastSeen));
        out.append(row);
    }

    // Attempts first, then content_id with null first, then the name, ordinal.
    std::stable_sort(out.begin(), out.end(), [](const QJsonObject &a, const QJsonObject &b) {
        const int attemptsA = a.value(QStringLiteral("attempt_count")).toInt();
        const int attemptsB = b.value(QStringLiteral("attempt_count")).toInt();
        if (attemptsA != attemptsB)
            return attemptsA > attemptsB;
        const qint64 contentA = idOf(a.value(QStringLiteral("content_id")));
        const qint64 contentB = idOf(b.value(QStringLiteral("content_id")));
        if (contentA != contentB)
            return contentA < contentB;
        return a.value(QStringLiteral("duty_name")).toString() < b.value(QStringLiteral("duty_name")).toString();
    });

    QJsonArray array;
    for (const QJsonObject &row : std::as_const(out))
        array.append(row);
    return array;
}

QJsonArray MockBackend::jobStats(const QJsonObject &filter) const
{
    // StatisticsRepository.GetJobStats (docs/statistics-definitions.md 11): grouped by
    // job_id, null its own "未知" row and never folded into a real job; the name and the
    // role are the job catalogue's, not whatever a run carries.
    QList<qint64> order; // in order of first appearance, like LINQ's GroupBy
    QHash<qint64, Aggregate> byJob;
    for (const QJsonObject &run : selectRuns(filter, true)) {
        const qint64 jobId = idOf(run.value(QStringLiteral("job_id")));
        if (!byJob.contains(jobId))
            order.append(jobId);
        accumulate(byJob[jobId], run);
    }

    const JobCatalog jobs;
    QList<QJsonObject> out;
    for (const qint64 jobId : std::as_const(order)) {
        const QVariant id = jobId < 0 ? QVariant() : QVariant(jobId);
        QJsonObject row = aggregateToRow(byJob.value(jobId));
        row.insert(QStringLiteral("job_id"), jobId < 0 ? QJsonValue(QJsonValue::Null) : QJsonValue(jobId));
        row.insert(QStringLiteral("job_name"), jobs.jobName(id));
        row.insert(QStringLiteral("role"), jobs.role(id));
        row.insert(QStringLiteral("role_group"), jobs.roleGroup(id));
        out.append(row);
    }

    // Attempts first, then job_id with null first.
    std::stable_sort(out.begin(), out.end(), [](const QJsonObject &a, const QJsonObject &b) {
        const int attemptsA = a.value(QStringLiteral("attempt_count")).toInt();
        const int attemptsB = b.value(QStringLiteral("attempt_count")).toInt();
        if (attemptsA != attemptsB)
            return attemptsA > attemptsB;
        return idOf(a.value(QStringLiteral("job_id"))) < idOf(b.value(QStringLiteral("job_id")));
    });

    QJsonArray array;
    for (const QJsonObject &row : std::as_const(out))
        array.append(row);
    return array;
}

} // namespace mr
