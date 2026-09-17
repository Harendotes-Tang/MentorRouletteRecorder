#pragma once

// ---------------------------------------------------------------------------
// Read-only list models over the statistics responses.
//
// Both models expose one role, "row", holding the whole record as a map, so
// the QML side reads the very field names of contracts/ipc-v1.schema.json.
// ---------------------------------------------------------------------------

#include <QAbstractListModel>
#include <QJsonObject>
#include <QQmlEngine>
#include <QVariantMap>

namespace mr {

class IBackend;

/// Common base: fetch a paged stats response, keep the items.
class StatsRowsModel : public QAbstractListModel
{
    Q_OBJECT
    Q_PROPERTY(int count READ rowCountProperty NOTIFY countChanged)
    /// How many distinct rows exist server-side, not how many this page holds.
    /// Reads $defs/DungeonStats.distinct_count when the Collector sends it and
    /// falls back to page_info.total; `count` is only the current page and is
    /// truncated by paging and by any Top-N view.
    Q_PROPERTY(int distinctCount READ distinctCount NOTIFY countChanged)
    Q_PROPERTY(int maxAttemptCount READ maxAttemptCount NOTIFY countChanged)
    Q_PROPERTY(int totalAttemptCount READ totalAttemptCount NOTIFY countChanged)
    Q_PROPERTY(bool loading READ isLoading NOTIFY loadingChanged)

public:
    enum Roles { RowRole = Qt::UserRole + 1 };

    explicit StatsRowsModel(QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    int rowCount(const QModelIndex &parent = {}) const override;
    QVariant data(const QModelIndex &index, int role) const override;
    QHash<int, QByteArray> roleNames() const override;

    int rowCountProperty() const { return int(m_rows.size()); }
    int distinctCount() const;
    /// Largest attempt_count in the set, for bar scaling. At least 1.
    int maxAttemptCount() const;
    /// Sum of attempt_count over every row, for share percentages.
    int totalAttemptCount() const;
    bool isLoading() const { return m_loading; }

public Q_SLOTS:
    void setFilter(const QVariantMap &filter);
    void reload();
    QVariantMap rowAt(int row) const;
    /// The first \a limit rows, for a "Top N" view.
    QVariantList topRows(int limit) const;

Q_SIGNALS:
    void countChanged();
    void loadingChanged();
    void loadFailed(const QString &code, const QString &message);

protected:
    /// Message type this model requests, e.g. "GetDungeonStats".
    virtual QString messageType() const = 0;

    /// Joins the bundled duty catalogue into one statistics row: duty_level,
    /// duty_expansion and the derived duty_meta the table shows under the
    /// name. Overridden as a no-op where it does not apply.
    virtual QJsonObject decorate(const QJsonObject &row) const { return row; }

    IBackend *m_backend = nullptr;
    QList<QJsonObject> m_rows;
    QJsonObject m_filter;
    int m_distinctCount = 0;
    bool m_loading = false;
};

class DungeonStatsModel : public StatsRowsModel
{
    Q_OBJECT
    QML_ELEMENT
    QML_UNCREATABLE("DungeonStatsModel is provided by the application controller")

public:
    using StatsRowsModel::StatsRowsModel;

protected:
    QString messageType() const override { return QStringLiteral("GetDungeonStats"); }
    QJsonObject decorate(const QJsonObject &row) const override;
};

class JobStatsModel : public StatsRowsModel
{
    Q_OBJECT
    QML_ELEMENT
    QML_UNCREATABLE("JobStatsModel is provided by the application controller")

public:
    using StatsRowsModel::StatsRowsModel;

    /// Aggregation by role group, for the donut chart and its legend.
    Q_INVOKABLE QVariantList roleBreakdown() const;

    /// The prototype's fine-grained Chinese role group for one $defs/JobStatsRow.
    /// Public so the unit tests can pin the contract-to-legend mapping.
    static QString roleGroupOf(const QJsonObject &row);

protected:
    QString messageType() const override { return QStringLiteral("GetJobStats"); }
};

} // namespace mr
