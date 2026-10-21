#pragma once

#include <QMap>
#include <QObject>
#include <QPointer>
#include <QStringList>
#include <QVariantMap>

namespace mr {
class AppSettings;

/// The history table's columns as the user arranged them (docs/ui-design.md
/// §4.2): which ones are shown, in what order, and how wide the ones they
/// resized are. Columns are named by stable keys, never by their titles, so
/// the interface language has no say in what is stored. The page keeps each
/// column's title, default width and narrowest width; this object keeps only
/// what the user changed, in AppSettings under history/.
class HistoryColumnsController final : public QObject
{
    Q_OBJECT
    /// Every column key in the user's order, hidden ones included.
    Q_PROPERTY(QStringList order READ order NOTIFY layoutChanged)
    /// The shown columns, in order. Never empty.
    Q_PROPERTY(QStringList visible READ visible NOTIFY layoutChanged)
    /// key -> width of every column the user resized; the others take the page's default.
    Q_PROPERTY(QVariantMap widths READ widths NOTIFY layoutChanged)
    /// Something differs from the defaults: 恢复默认 has work to do.
    Q_PROPERTY(bool customised READ customised NOTIFY layoutChanged)
    Q_PROPERTY(int minimumWidth READ minimumWidth CONSTANT)
    Q_PROPERTY(int maximumWidth READ maximumWidth CONSTANT)
public:
    static constexpr int kMinimumWidth = 40;
    static constexpr int kMaximumWidth = 800;

    /// A null \a settings keeps the arrangement for this run only.
    explicit HistoryColumnsController(AppSettings *settings, QObject *parent = nullptr);

    /// The design order: 日期 进本 结束 副本 类型 职业 结果 耗时 来源 标记.
    static QStringList defaultOrder();

    QStringList order() const { return m_order; }
    QStringList visible() const;
    QVariantMap widths() const;
    bool customised() const;
    int minimumWidth() const { return kMinimumWidth; }
    int maximumWidth() const { return kMaximumWidth; }

    Q_INVOKABLE bool isVisible(const QString &key) const;
    /// False for the last shown column: the table never loses every column.
    Q_INVOKABLE bool canHide(const QString &key) const;
    /// The stored width, or 0 when the column takes the page's default.
    Q_INVOKABLE int width(const QString &key) const;

    Q_INVOKABLE void setVisible(const QString &key, bool visible);
    /// Clamped to kMinimumWidth..kMaximumWidth; 0 or less returns the column to its default.
    Q_INVOKABLE void setWidth(const QString &key, int width);
    /// Drag-and-drop on the header: put \a key at \a visibleIndex among the
    /// shown columns. Hidden columns keep their places between their neighbours.
    Q_INVOKABLE void moveTo(const QString &key, int visibleIndex);
    /// 恢复默认: every column shown, in the design order, at the page's widths.
    Q_INVOKABLE void reset();

Q_SIGNALS:
    void layoutChanged();

private:
    void store();

    QPointer<AppSettings> m_settings;
    QStringList m_order;
    QStringList m_hidden;
    QMap<QString, int> m_widths;
};

} // namespace mr
