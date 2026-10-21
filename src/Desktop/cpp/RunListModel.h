#pragma once

// ---------------------------------------------------------------------------
// Paged list of runs.
//
// The model holds one page at a time and asks the backend for the next one,
// so a database with tens of thousands of rows is never loaded at once.
// ---------------------------------------------------------------------------

#include <QAbstractListModel>
#include <QJsonObject>
#include <QMap>
#include <QSet>
#include <QStringList>
#include <QQmlEngine>
#include <QVariantMap>

#include "LiveText.h"

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
    /// Why the last query failed, or empty after an answer. While it is set the
    /// rows and total are not a result, and the page must not call them one.
    Q_PROPERTY(QString loadError READ loadError NOTIFY loadErrorChanged)
    Q_PROPERTY(QString sortField READ sortField NOTIFY sortChanged)
    Q_PROPERTY(bool sortAscending READ sortAscending NOTIFY sortChanged)

    Q_PROPERTY(QString calendarMonth READ calendarMonth NOTIFY calendarChanged)
    Q_PROPERTY(bool calendarLoading READ calendarLoading NOTIFY calendarChanged)
    Q_PROPERTY(QString calendarError READ calendarError NOTIFY calendarChanged)
    Q_PROPERTY(QStringList calendarAvailableDays READ calendarAvailableDays NOTIFY calendarChanged)
    Q_PROPERTY(QStringList calendarKnownMonths READ calendarKnownMonths NOTIFY calendarChanged)

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
    QString loadError() const { return m_loadError.text(); }
    QString sortField() const { return m_sortField; }
    bool sortAscending() const { return m_sortAscending; }

    void setPageSize(int pageSize);
    QString calendarMonth() const { return m_calendarMonth; }
    bool calendarLoading() const { return m_calendarPending.contains(m_calendarMonth); }
    QString calendarError() const { return m_calendarErrors.value(m_calendarMonth).text(); }
    QStringList calendarAvailableDays() const;
    QStringList calendarKnownMonths() const { return m_calendarDays.keys(); }
    Q_INVOKABLE bool calendarDayAvailable(const QString &day) const;
    Q_INVOKABLE bool calendarMonthKnown(const QString &month) const;
    Q_INVOKABLE QString calendarDayError(const QString &day) const;
    Q_INVOKABLE void setCalendarFilter(const QVariantMap &filter);
    Q_INVOKABLE void requestCalendarDays(const QString &month);
    Q_INVOKABLE void ensureCalendarDay(const QString &day);

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
    void calendarChanged();
    void pagingChanged();
    void loadingChanged();
    void sortChanged();
    void loadFailed(const QString &code, const QString &message);
    void loadErrorChanged();

private:
    void loadRows();
    QJsonObject calendarFilter() const;
    void invalidateCalendar();
    void loadCalendarMonth(const QString &month, bool retryError);
    void setLoading(bool loading);
    void setLoadError(const LiveText &error);

    IBackend *m_backend = nullptr;
    QList<QJsonObject> m_rows;
    QJsonObject m_filter;
    // The 日期 column's key (HistoryPage), so the list opens sorted by the date
    // it shows - an import's original-site day included - with its arrow on
    // (docs/ui-design.md §4.2; review K1-8).
    QString m_sortField = QStringLiteral("history_date");
    bool m_sortAscending = false;
    int m_page = 1;
    int m_pageSize = 10;
    // Zero-based row selected by navigation; resizing preserves this position
    // instead of adopting the rounded-down first row of each new page.
    int m_anchorRow = 0;
    int m_total = 0;
    bool m_loading = false;
    /// A recipe, so the Collector's sentence follows a language switch.
    LiveText m_loadError;
    quint64 m_loadGeneration = 0;
    QJsonObject m_calendarFilter;
    QString m_calendarMonth;
    QMap<QString, QStringList> m_calendarDays;
    /// Recipes, read by QML bindings wrapped in Theme.live() (LiveText.h).
    QMap<QString, LiveText> m_calendarErrors;
    QSet<QString> m_calendarPending;
    quint64 m_calendarGeneration = 0;
};

} // namespace mr
