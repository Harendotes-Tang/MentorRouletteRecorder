#include "RunListModel.h"

#include "DutyCatalog.h"
#include "IBackend.h"

#include <QJsonArray>
#include <QJsonObject>
#include <QJsonValue>

namespace mr {

RunListModel::RunListModel(QObject *parent) : QAbstractListModel(parent) {}

void RunListModel::setBackend(IBackend *backend)
{
    ++m_loadGeneration;
    m_backend = backend;
    setLoading(false);
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
    reload();
}

void RunListModel::setFilter(const QVariantMap &filter)
{
    m_filter = QJsonObject::fromVariantMap(filter);
    m_page = 1;
    m_anchorRow = 0;
    reload();
}

void RunListModel::goToPage(int page)
{
    const int clamped = qBound(1, page, pageCount());
    m_anchorRow = (clamped - 1) * m_pageSize;
    if (clamped == m_page)
        return;
    m_page = clamped;
    Q_EMIT pagingChanged();
    reload();
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
    reload();
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

void RunListModel::reload()
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
    reply->whenDone(this,
            [this, generation](bool ok, const QVariantMap &payload,
                   const QString &code, const QString &message) {
                if (generation != m_loadGeneration)
                    return;
                if (!ok) {
                    beginResetModel();
                    m_rows.clear();
                    m_total = 0;
                    endResetModel();
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
                    reload();
                    return;
                }

                beginResetModel();
                m_rows.clear();
                m_rows.reserve(items.size());
                for (const QJsonValue &value : items)
                    m_rows.append(value.toObject());
                m_total = total;
                // A reload can observe deletions. Keep the position when it
                // still exists, otherwise anchor to the last surviving row.
                m_anchorRow = qBound(0, m_anchorRow, qMax(0, m_total - 1));
                m_page = qMax(1, pageInfo.value(QStringLiteral("page")).toInt(m_page));
                endResetModel();
                setLoading(false);
                Q_EMIT pagingChanged();
            });
}

} // namespace mr
