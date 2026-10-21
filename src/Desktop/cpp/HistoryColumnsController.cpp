#include "HistoryColumnsController.h"
#include "AppSettings.h"

#include <algorithm>
#include <utility>

namespace {
// The design order (docs/ui-design.md §4.2). The row checkbox is not a column.
const char *const kColumns[] = {"date", "entered", "ended", "duty", "category",
                                "job", "result", "duration", "source", "marks"};

bool known(const QString &key)
{
    return std::ranges::any_of(kColumns, [&key](const char *column) {
        return key == QLatin1String(column);
    });
}

/// \a input's known keys first, without repeats, then whatever it left out in
/// the design order: a column added by a later build appears by itself.
QStringList completeOrder(const QStringList &input)
{
    QStringList order;
    for (const QString &key : input)
        if (known(key) && !order.contains(key))
            order.append(key);
    for (const QString &key : mr::HistoryColumnsController::defaultOrder())
        if (!order.contains(key))
            order.append(key);
    return order;
}

int clampWidth(int width)
{
    return qBound(mr::HistoryColumnsController::kMinimumWidth, width,
                  mr::HistoryColumnsController::kMaximumWidth);
}
} // namespace

namespace mr {

HistoryColumnsController::HistoryColumnsController(AppSettings *settings, QObject *parent)
    : QObject(parent)
    , m_settings(settings)
{
    m_order = completeOrder(settings ? settings->historyColumnOrder() : QStringList());
    if (settings) {
        for (const QString &key : settings->historyHiddenColumns())
            if (known(key) && !m_hidden.contains(key))
                m_hidden.append(key);
        const QVariantMap widths = settings->historyColumnWidths();
        for (auto it = widths.cbegin(); it != widths.cend(); ++it) {
            bool ok = false;
            const int width = it.value().toInt(&ok);
            if (known(it.key()) && ok && width > 0)
                m_widths.insert(it.key(), clampWidth(width));
        }
    }
    // A file that hides every column shows them all: the table never loses every column.
    if (m_hidden.size() >= m_order.size())
        m_hidden.clear();
}

QStringList HistoryColumnsController::defaultOrder()
{
    QStringList order;
    for (const char *column : kColumns)
        order.append(QString::fromLatin1(column));
    return order;
}

QStringList HistoryColumnsController::visible() const
{
    QStringList shown;
    for (const QString &key : m_order)
        if (!m_hidden.contains(key))
            shown.append(key);
    return shown;
}

QVariantMap HistoryColumnsController::widths() const
{
    QVariantMap widths;
    for (auto it = m_widths.cbegin(); it != m_widths.cend(); ++it)
        widths.insert(it.key(), it.value());
    return widths;
}

bool HistoryColumnsController::customised() const
{
    return m_order != defaultOrder() || !m_hidden.isEmpty() || !m_widths.isEmpty();
}

bool HistoryColumnsController::isVisible(const QString &key) const
{
    return m_order.contains(key) && !m_hidden.contains(key);
}

bool HistoryColumnsController::canHide(const QString &key) const
{
    return isVisible(key) && m_order.size() - m_hidden.size() > 1;
}

int HistoryColumnsController::width(const QString &key) const
{
    return m_widths.value(key, 0);
}

void HistoryColumnsController::setVisible(const QString &key, bool visible)
{
    if (!m_order.contains(key))
        return;
    if (visible) {
        if (!m_hidden.removeOne(key))
            return;
    } else {
        if (!canHide(key))
            return;
        m_hidden.append(key);
    }
    store();
}

void HistoryColumnsController::setWidth(const QString &key, int width)
{
    if (!m_order.contains(key))
        return;
    if (width <= 0) {
        if (m_widths.remove(key) == 0)
            return;
    } else {
        const int bounded = clampWidth(width);
        if (m_widths.value(key, 0) == bounded)
            return;
        m_widths.insert(key, bounded);
    }
    store();
}

void HistoryColumnsController::moveTo(const QString &key, int visibleIndex)
{
    if (!isVisible(key))
        return;
    QStringList shown = visible();
    const qsizetype from = shown.indexOf(key);
    const qsizetype to = qBound(static_cast<qsizetype>(0), static_cast<qsizetype>(visibleIndex),
                                shown.size() - 1);
    if (from == to)
        return;
    shown.move(from, to);
    // The hidden columns keep their slots; the shown ones take the new order.
    QStringList order;
    qsizetype next = 0;
    for (const QString &existing : std::as_const(m_order))
        order.append(m_hidden.contains(existing) ? existing : shown.at(next++));
    m_order = order;
    store();
}

void HistoryColumnsController::reset()
{
    if (!customised())
        return;
    m_order = defaultOrder();
    m_hidden.clear();
    m_widths.clear();
    store();
}

void HistoryColumnsController::store()
{
    if (m_settings) {
        m_settings->setHistoryColumnOrder(m_order == defaultOrder() ? QStringList() : m_order);
        m_settings->setHistoryHiddenColumns(m_hidden);
        m_settings->setHistoryColumnWidths(widths());
    }
    Q_EMIT layoutChanged();
}

} // namespace mr
