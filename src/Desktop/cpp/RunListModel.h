#pragma once

// ---------------------------------------------------------------------------
// Paged list of runs.
//
// The model holds one page at a time and asks the backend for the next one,
// so a database with tens of thousands of rows is never loaded at once.
// ---------------------------------------------------------------------------

#include <QAbstractListModel>
#include <QJsonObject>
#include <QQmlEngine>
#include <QVariantMap>

namespace mr {

class IBackend;

class RunListModel : public QAbstractListModel
{
    Q_OBJECT
    QML_ELEMENT
    QML_UNCREATABLE("RunListModel is provided by the application controller")

    Q_PROPERTY(int page READ page NOTIFY pagingChanged)
    Q_PROPERTY(int pageCount READ pageCount NOTIFY pagingChanged)
    Q_PROPERTY(int pageSize READ pageSize WRITE setPageSize NOTIFY pagingChanged)
    Q_PROPERTY(int total READ total NOTIFY pagingChanged)
    Q_PROPERTY(bool loading READ isLoading NOTIFY loadingChanged)
    Q_PROPERTY(QString sortField READ sortField NOTIFY sortChanged)
    Q_PROPERTY(bool sortAscending READ sortAscending NOTIFY sortChanged)

public:
    enum Roles { RunRole = Qt::UserRole + 1 };

    explicit RunListModel(QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    int rowCount(const QModelIndex &parent = {}) const override;
    QVariant data(const QModelIndex &index, int role) const override;
    QHash<int, QByteArray> roleNames() const override;

    int page() const { return m_page; }
    int pageCount() const;
    int pageSize() const { return m_pageSize; }
    int total() const { return m_total; }
    bool isLoading() const { return m_loading; }
    QString sortField() const { return m_sortField; }
    bool sortAscending() const { return m_sortAscending; }

    void setPageSize(int pageSize);

public Q_SLOTS:
    /// Replace the filter and go back to page 1.
    void setFilter(const QVariantMap &filter);
    void reload();
    void goToPage(int page);
    void nextPage();
    void previousPage();
    /// Toggle direction when \a field is already the sort key.
    void sortBy(const QString &field);
    /// The run at \a row as a plain map, or an empty map.
    QVariantMap runAt(int row) const;

Q_SIGNALS:
    void pagingChanged();
    void loadingChanged();
    void sortChanged();
    void loadFailed(const QString &code, const QString &message);

private:
    void setLoading(bool loading);

    IBackend *m_backend = nullptr;
    QList<QJsonObject> m_rows;
    QJsonObject m_filter;
    QString m_sortField = QStringLiteral("matched_at_utc");
    bool m_sortAscending = false;
    int m_page = 1;
    int m_pageSize = 10;
    int m_total = 0;
    bool m_loading = false;
};

} // namespace mr
