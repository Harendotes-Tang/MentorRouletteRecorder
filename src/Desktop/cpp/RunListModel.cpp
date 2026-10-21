#include "RunListModel.h"

#include "CollectorText.h"
#include "DutyCatalog.h"
#include "IBackend.h"

#include <QDate>
#include <QJsonArray>
#include <QRegularExpression>
#include <QJsonObject>
#include <QJsonValue>
#include <QPointer>

namespace mr {

RunListModel::RunListModel(QObject *parent) : QAbstractListModel(parent)
{
    // loadError is a recipe: the Collector's sentence follows the language.
    onLanguageChanged(this, [this] {
        if (!m_loadError.isEmpty())
            Q_EMIT loadErrorChanged();
    });
}

void RunListModel::setBackend(IBackend *backend)
{
    ++m_loadGeneration;
    m_backend = backend;
    setLoading(false);
    invalidateCalendar();
}

int RunListModel::rowCount(const QModelIndex &parent) const
{
    return parent.isValid() ? 0 : int(m_rows.size());
}

QVariant RunListModel::data(const QModelIndex &index, int role) const
{
    if (!index.isValid() || index.row() < 0 || index.row() >= m_rows.size())
        return {};
    if (role != RunRole)
        return {};
    // duty_level / duty_expansion are not fields $defs/Run can carry; they are
    // joined in from the bundled catalogue by content_id, for display only.
    return DutyCatalog::shared()->enrich(m_rows.at(index.row()).toVariantMap());
}

QHash<int, QByteArray> RunListModel::roleNames() const
{
    return {{RunRole, QByteArrayLiteral("run")}};
}

int RunListModel::pageCount() const
{
    if (m_pageSize <= 0)
        return 1;
    return qMax(1, (m_total + m_pageSize - 1) / m_pageSize);
}

void RunListModel::setPageSize(int pageSize)
{
    const int clamped = qBound(1, pageSize, 200);
    if (clamped == m_pageSize)
        return;
    // The page size follows the window height (HistoryPage.rowsThatFit), so a
    // resize keeps the navigation anchor in view. Reusing the current page's
    // rounded-down first row would accumulate drift on every resize.
    m_pageSize = clamped;
    m_page = m_anchorRow / clamped + 1;
    Q_EMIT pagingChanged();
    loadRows();
}

void RunListModel::setFilter(const QVariantMap &filter)
{
    m_filter = QJsonObject::fromVariantMap(filter);
    setCalendarFilter(filter);
    m_page = 1;
    m_anchorRow = 0;
    loadRows();
}

void RunListModel::goToPage(int page)
{
    const int clamped = qBound(1, page, pageCount());
    m_anchorRow = (clamped - 1) * m_pageSize;
    if (clamped == m_page)
        return;
    m_page = clamped;
    Q_EMIT pagingChanged();
    loadRows();
}

void RunListModel::nextPage()
{
    goToPage(m_page + 1);
}

void RunListModel::previousPage()
{
    goToPage(m_page - 1);
}

void RunListModel::sortBy(const QString &field)
{
    if (field == m_sortField)
        m_sortAscending = !m_sortAscending;
    else {
        m_sortField = field;
        m_sortAscending = false;
    }
    m_page = 1;
    m_anchorRow = 0;
    Q_EMIT sortChanged();
    loadRows();
}

QVariantMap RunListModel::runAt(int row) const
{
    if (row < 0 || row >= m_rows.size())
        return {};
    return DutyCatalog::shared()->enrich(m_rows.at(row).toVariantMap());
}

void RunListModel::setLoading(bool loading)
{
    if (m_loading == loading)
        return;
    m_loading = loading;
    Q_EMIT loadingChanged();
}

void RunListModel::setLoadError(const LiveText &error)
{
    if (m_loadError.isEmpty() == error.isEmpty() && m_loadError.text() == error.text())
        return;
    m_loadError = error;
    Q_EMIT loadErrorChanged();
}

void RunListModel::reload()
{
    invalidateCalendar();
    loadRows();
}

void RunListModel::loadRows()
{
    const quint64 generation = ++m_loadGeneration;
    if (!m_backend)
        return;

    QJsonObject sort;
    sort.insert(QStringLiteral("field"), m_sortField);
    sort.insert(QStringLiteral("direction"),
                m_sortAscending ? QStringLiteral("asc") : QStringLiteral("desc"));

    setLoading(true);
    BackendReply *reply = m_backend->queryRuns(m_filter, m_page, m_pageSize, sort);
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this,
            [this, generation, guard](bool ok, const QVariantMap &payload,
                   const QString &code, const QString &message) {
                if (generation != m_loadGeneration)
                    return;
                if (!ok) {
                    beginResetModel();
                    m_rows.clear();
                    m_total = 0;
                    endResetModel();
                    // The emptied list is not an answer; the page says so
                    // instead of "没有符合筛选条件的记录" (review OD-3).
                    setLoadError(message.isEmpty() ? LiveText::verbatim(code)
                                                   : collector_text::replyErrorText(guard, message));
                    setLoading(false);
                    Q_EMIT pagingChanged();
                    Q_EMIT loadFailed(code, message);
                    return;
                }

                const QJsonObject object = QJsonObject::fromVariantMap(payload);
                const QJsonArray items = object.value(QStringLiteral("items")).toArray();
                const QJsonObject pageInfo =
                    object.value(QStringLiteral("page_info")).toObject();
                const int total = pageInfo.value(QStringLiteral("total")).toInt();
                const int lastPage = qMax(1, (total + m_pageSize - 1) / m_pageSize);
                if (m_page > lastPage) {
                    // Deleting the last row on a page must not leave an empty
                    // page beyond pageCount until the user resizes the window.
                    m_total = total;
                    m_page = lastPage;
                    m_anchorRow = qBound(0, m_anchorRow, qMax(0, total - 1));
                    Q_EMIT pagingChanged();
                    loadRows();
                    return;
                }

                beginResetModel();
                m_rows.clear();
                m_rows.reserve(items.size());
                for (const auto &value : items)
                    m_rows.append(value.toObject());
                m_total = total;
                // A reload can observe deletions. Keep the position when it
                // still exists, otherwise anchor to the last surviving row.
                m_anchorRow = qBound(0, m_anchorRow, qMax(0, m_total - 1));
                m_page = qMax(1, pageInfo.value(QStringLiteral("page")).toInt(m_page));
                endResetModel();
                setLoadError(LiveText());
                setLoading(false);
                Q_EMIT pagingChanged();
            });
}

namespace {
bool validCalendarMonth(const QString &month)
{
    static const QRegularExpression pattern(QStringLiteral("^[0-9]{4}-[0-9]{2}$"));
    const QDate date = QDate::fromString(month + QStringLiteral("-01"), QStringLiteral("yyyy-MM-dd"));
    return pattern.match(month).hasMatch() && date.isValid() && date.year() >= 100;
}
bool validCalendarDay(const QString &day)
{
    static const QRegularExpression pattern(QStringLiteral("^[0-9]{4}-[0-9]{2}-[0-9]{2}$"));
    const QDate date = QDate::fromString(day, QStringLiteral("yyyy-MM-dd"));
    return pattern.match(day).hasMatch() && date.isValid() && date.year() >= 100;
}
}

QJsonObject RunListModel::calendarFilter() const
{
    QJsonObject filter = m_calendarFilter;
    for (const QString &key : {QStringLiteral("from_utc"), QStringLiteral("to_utc"),
                               QStringLiteral("history_from_day"), QStringLiteral("history_to_day")})
        filter.remove(key);
    return filter;
}

QStringList RunListModel::calendarAvailableDays() const
{
    QStringList days;
    for (const QStringList &monthDays : m_calendarDays)
        days.append(monthDays);
    return days;
}

bool RunListModel::calendarDayAvailable(const QString &day) const
{
    return validCalendarDay(day) && m_calendarDays.value(day.left(7)).contains(day);
}

bool RunListModel::calendarMonthKnown(const QString &month) const
{
    return m_calendarDays.contains(month);
}

QString RunListModel::calendarDayError(const QString &day) const
{
    return m_calendarErrors.value(day.left(7)).text();
}

void RunListModel::invalidateCalendar()
{
    ++m_calendarGeneration;
    m_calendarDays.clear();
    m_calendarErrors.clear();
    m_calendarPending.clear();
    Q_EMIT calendarChanged();
    if (!m_calendarMonth.isEmpty())
        loadCalendarMonth(m_calendarMonth, false);
}

void RunListModel::setCalendarFilter(const QVariantMap &filter)
{
    QJsonObject scope = QJsonObject::fromVariantMap(filter);
    for (const QString &key : {QStringLiteral("from_utc"), QStringLiteral("to_utc"),
                               QStringLiteral("history_from_day"), QStringLiteral("history_to_day")})
        scope.remove(key);
    if (scope == m_calendarFilter)
        return;
    m_calendarFilter = scope;
    invalidateCalendar();
}

void RunListModel::requestCalendarDays(const QString &month)
{
    m_calendarMonth = month;
    Q_EMIT calendarChanged();
    loadCalendarMonth(month, true);
}

void RunListModel::ensureCalendarDay(const QString &day)
{
    if (validCalendarDay(day))
        loadCalendarMonth(day.left(7), false);
}

void RunListModel::loadCalendarMonth(const QString &month, bool retryError)
{
    if (m_calendarDays.contains(month) || m_calendarPending.contains(month)
            || (!retryError && m_calendarErrors.contains(month)))
        return;
    if (!validCalendarMonth(month) || !m_backend) {
        m_calendarErrors.insert(month, !m_backend ? LiveText([] { return tr("日历服务尚未连接"); })
                                                 : LiveText([] { return tr("无效的日历月份"); }));
        Q_EMIT calendarChanged();
        return;
    }
    const quint64 generation = m_calendarGeneration;
    m_calendarErrors.remove(month);
    m_calendarPending.insert(month);
    Q_EMIT calendarChanged();
    BackendReply *reply = m_backend->queryRunCalendarDays(month, calendarFilter());
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, generation, month, guard](bool ok, const QVariantMap &payload,
                                                          const QString &code, const QString &message) {
        if (generation != m_calendarGeneration)
            return;
        m_calendarPending.remove(month);
        const QJsonObject answer = QJsonObject::fromVariantMap(payload);
        const QJsonValue daysValue = answer.value(QStringLiteral("days"));
        QStringList days;
        bool valid = ok && answer.value(QStringLiteral("month")).toString() == month
                        && daysValue.isArray();
        if (valid) {
            for (const auto &value : daysValue.toArray()) {
                const QString day = value.toString();
                if (!value.isString() || !validCalendarDay(day) || day.left(7) != month
                        || (!days.isEmpty() && day <= days.last())) {
                    valid = false;
                    break;
                }
                days.append(day);
            }
        }
        if (valid) {
            m_calendarDays.insert(month, days);
            m_calendarErrors.remove(month);
        } else {
            m_calendarErrors.insert(month, LiveText([ok, code, refusal = collector_text::replyErrorText(guard, message)] {
                return ok ? tr("暂时无法读取有记录的日期，请重新加载。")
                    : (!refusal.isEmpty() ? refusal.text() : (!code.isEmpty() ? code : tr("日历日期加载失败")));
            }));
        }
        Q_EMIT calendarChanged();
    });
}

} // namespace mr
