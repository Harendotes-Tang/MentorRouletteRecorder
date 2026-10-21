#include "StatisticsOverlayController.h"
#include "AppSettings.h"
#include "LiveText.h"
#include <QGuiApplication>
#include <QScreen>
#include <cmath>

namespace {
struct Module { const char *key; const char *title; const char *hint; };
// Design order; every module is on by default. 当前副本 / 副本用时 only show
// during a run and 待处理记录 only while something waits, whatever the switch.
const Module kModules[] = {
    {"progress", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "累计进度"), nullptr},
    {"current_duty", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "当前副本"),
     QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "进本后显示")},
    {"elapsed", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "副本用时"),
     QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "进本后显示")},
    {"dungeons", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "副本统计"), nullptr},
    {"jobs", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "职业统计"), nullptr},
    {"pending_work", QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "待处理记录"),
     QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "有待处理时显示")},
};
QStringList moduleKeys()
{
    QStringList keys;
    for (const auto &module : kModules)
        keys.append(QString::fromLatin1(module.key));
    return keys;
}
QStringList validModules(const QStringList &input)
{
    const QStringList allowed = moduleKeys();
    QStringList result;
    for (const QString &key : input)
        if (allowed.contains(key) && !result.contains(key))
            result.append(key);
    return result;
}
/// \a input's known keys first, then whatever it left out in the default order.
QStringList completeOrder(const QStringList &input)
{
    QStringList result = validModules(input);
    for (const QString &key : moduleKeys())
        if (!result.contains(key))
            result.append(key);
    return result;
}
QStringList enabledInOrder(const QStringList &order, const QStringList &enabled)
{
    QStringList result;
    for (const QString &key : order)
        if (enabled.contains(key))
            result.append(key);
    return result;
}
bool validRowLimit(int limit)
{
    return limit == 0 || limit == 3 || limit == 5 || limit == 10
        || limit == mr::StatisticsOverlayController::kAllRows;
}
QString validForm(const QString &form)
{
    return form == QLatin1String("mini") ? QStringLiteral("mini") : QStringLiteral("full");
}
QScreen *screenFor(const QRect &geometry)
{
    QScreen *chosen = QGuiApplication::primaryScreen();
    qint64 bestArea = 0;
    for (QScreen *screen : QGuiApplication::screens()) {
        const QRect overlap = screen->availableGeometry().intersected(geometry);
        const qint64 area = overlap.isValid() ? qint64(overlap.width()) * overlap.height() : 0;
        if (area > bestArea) {
            bestArea = area;
            chosen = screen;
        }
    }
    return chosen;
}
QRect fitToAvailableScreen(const QRect &geometry)
{
    QScreen *chosen = QGuiApplication::primaryScreen();
    qint64 bestArea = 0;
    for (QScreen *screen : QGuiApplication::screens()) {
        const QRect overlap = screen->availableGeometry().intersected(geometry);
        const qint64 area = overlap.isValid() ? qint64(overlap.width()) * overlap.height() : 0;
        if (area > bestArea) {
            bestArea = area;
            chosen = screen;
        }
    }
    return chosen ? mr::StatisticsOverlayController::fitGeometry(geometry, chosen->availableGeometry())
                  : mr::StatisticsOverlayController::validGeometry(geometry);
}
} // namespace

namespace mr {
StatisticsOverlayController::StatisticsOverlayController(AppSettings *settings, QObject *parent)
    : QObject(parent), m_settings(settings)
    , m_geometry(validGeometry(settings ? settings->statisticsOverlayGeometry() : QRect()))
    , m_form(validForm(settings ? settings->statisticsOverlayForm() : QString()))
{
    const QStringList enabled = validModules(settings ? settings->statisticsOverlayModules(defaultModules()) : defaultModules());
    // The saved order; before it existed, the enabled list was the order.
    m_order = completeOrder(settings ? settings->statisticsOverlayModuleOrder(enabled) : enabled);
    m_modules = enabledInOrder(m_order, enabled);
    if (settings) {
        const double saved = settings->statisticsOverlayOpacity();
        m_opacity = std::isfinite(saved) ? qBound(kMinimumOpacity, saved, 1.0) : kDefaultOpacity;
        const int limit = settings->statisticsOverlayRowLimit();
        m_rowLimit = validRowLimit(limit) ? limit : kDefaultRowLimit;
        m_labels = settings->statisticsOverlayLabels();
    }
    onLanguageChanged(this, [this] { Q_EMIT moduleOptionsChanged(); Q_EMIT modulesChanged(); });
}
QStringList StatisticsOverlayController::defaultModules() { return moduleKeys(); }
QVariantList StatisticsOverlayController::moduleOptions() const
{
    QVariantList options;
    for (const auto &module : kModules)
        options.append(QVariantMap{{QStringLiteral("key"), QString::fromLatin1(module.key)},
                                   {QStringLiteral("title"), tr(module.title)},
                                   {QStringLiteral("hint"), module.hint ? tr(module.hint) : QString()}});
    return options;
}
QVariantList StatisticsOverlayController::moduleRows() const
{
    QVariantList rows;
    for (const QString &key : m_order) {
        for (const auto &module : kModules) {
            if (key != QLatin1String(module.key)) continue;
            rows.append(QVariantMap{{QStringLiteral("key"), key},
                                    {QStringLiteral("title"), tr(module.title)},
                                    {QStringLiteral("hint"), module.hint ? tr(module.hint) : QString()},
                                    {QStringLiteral("on"), m_modules.contains(key)}});
        }
    }
    return rows;
}
QRect StatisticsOverlayController::validGeometry(const QRect &geometry)
{
    if (!geometry.isValid())
        return {80, 80, 300, 560};
    return {qBound(-1000000, geometry.x(), 1000000), qBound(-1000000, geometry.y(), 1000000),
            qBound(240, geometry.width(), 520), qBound(150, geometry.height(), 1200)};
}
QRect StatisticsOverlayController::fitGeometry(const QRect &geometry, const QRect &available)
{
    QRect result = validGeometry(geometry);
    if (!available.isValid())
        return result;
    result.setWidth(qMin(result.width(), available.width()));
    result.setHeight(qMin(result.height(), available.height()));
    result.moveLeft(qBound(available.left(), result.left(), available.left() + available.width() - result.width()));
    result.moveTop(qBound(available.top(), result.top(), available.top() + available.height() - result.height()));
    return result;
}
void StatisticsOverlayController::show()
{
    if (m_visible)
        return;
    // The full form is fitted to a screen here. The mini bar shares only the
    // position, and its window fits the bar's own rectangle (setPosition): fitted
    // with the full size, a bar docked at the bottom edge would come back higher.
    if (m_form != QLatin1String("mini"))
        setGeometry(m_geometry);
    setLocked(false);
    m_visible = true;
    Q_EMIT visibleChanged();
    Q_EMIT opened();
}
void StatisticsOverlayController::hide()
{
    if (!m_visible)
        return;
    m_visible = false;
    setLocked(false);
    Q_EMIT visibleChanged();
}
void StatisticsOverlayController::toggle() { m_visible ? hide() : show(); }
void StatisticsOverlayController::setLocked(bool locked)
{
    if (m_locked == locked || (locked && !m_visible))
        return;
    m_locked = locked;
    Q_EMIT lockedChanged();
}
void StatisticsOverlayController::setOpacity(double opacity)
{
    if (!std::isfinite(opacity))
        return;
    const double bounded = qBound(kMinimumOpacity, opacity, 1.0);
    if (qFuzzyCompare(m_opacity, bounded))
        return;
    m_opacity = bounded;
    if (m_settings) m_settings->setStatisticsOverlayOpacity(m_opacity);
    Q_EMIT opacityChanged();
}
void StatisticsOverlayController::setGeometry(const QRect &geometry)
{
    // Fit after preference validation, including on delayed QML geometry writes.
    const QRect bounded = fitToAvailableScreen(geometry);
    if (m_geometry == bounded)
        return;
    m_geometry = bounded;
    if (m_settings) m_settings->setStatisticsOverlayGeometry(m_geometry);
    Q_EMIT geometryChanged();
}
void StatisticsOverlayController::storeModules()
{
    if (m_settings) {
        m_settings->setStatisticsOverlayModules(m_modules);
        m_settings->setStatisticsOverlayModuleOrder(m_order);
    }
    Q_EMIT modulesChanged();
}
void StatisticsOverlayController::setPosition(const QRect &shown)
{
    if (!shown.isValid())
        return;
    QRect fitted = shown;
    if (QScreen *screen = screenFor(shown)) {
        const QRect available = screen->availableGeometry();
        fitted.moveLeft(qBound(available.left(), fitted.left(), qMax(available.left(), available.left() + available.width() - fitted.width())));
        fitted.moveTop(qBound(available.top(), fitted.top(), qMax(available.top(), available.top() + available.height() - fitted.height())));
    }
    const QRect bounded = validGeometry(QRect(fitted.topLeft(), m_geometry.size()));
    if (m_geometry == bounded)
        return;
    m_geometry = bounded;
    if (m_settings) m_settings->setStatisticsOverlayGeometry(m_geometry);
    Q_EMIT geometryChanged();
}
void StatisticsOverlayController::setModules(const QStringList &modules)
{
    // The given order wins: the enabled modules come first, the others keep
    // their relative order behind them.
    const QStringList enabled = validModules(modules);
    QStringList order = enabled;
    for (const QString &key : std::as_const(m_order))
        if (!order.contains(key))
            order.append(key);
    if (m_modules == enabled && m_order == order)
        return;
    m_order = order;
    m_modules = enabled;
    storeModules();
}
void StatisticsOverlayController::toggleModule(const QString &key)
{
    if (!m_order.contains(key)) return;
    QStringList enabled = m_modules;
    if (!enabled.removeOne(key)) enabled.append(key);
    m_modules = enabledInOrder(m_order, enabled);
    storeModules();
}
void StatisticsOverlayController::moveModule(const QString &key, int direction)
{
    if (direction != -1 && direction != 1) return;
    moveModuleTo(key, int(m_order.indexOf(key)) + direction);
}
void StatisticsOverlayController::moveModuleTo(const QString &key, int index)
{
    const qsizetype from = m_order.indexOf(key);
    if (from < 0) return;
    const qsizetype to = qBound(qsizetype(0), qsizetype(index), m_order.size() - 1);
    if (from == to) return;
    m_order.move(from, to);
    m_modules = enabledInOrder(m_order, m_modules);
    storeModules();
}
void StatisticsOverlayController::setRowLimit(int limit)
{
    if (!validRowLimit(limit) || m_rowLimit == limit) return;
    m_rowLimit = limit;
    if (m_settings) m_settings->setStatisticsOverlayRowLimit(limit);
    Q_EMIT rowLimitChanged();
}
void StatisticsOverlayController::setForm(const QString &form)
{
    const QString bounded = validForm(form);
    if (m_form == bounded) return;
    m_form = bounded;
    if (m_settings) m_settings->setStatisticsOverlayForm(m_form);
    Q_EMIT formChanged();
}
void StatisticsOverlayController::setLabels(bool labels)
{
    if (m_labels == labels) return;
    m_labels = labels;
    if (m_settings) m_settings->setStatisticsOverlayLabels(m_labels);
    Q_EMIT labelsChanged();
}
void StatisticsOverlayController::reset()
{
    m_order = defaultModules();
    m_modules = defaultModules();
    storeModules();
    setRowLimit(kDefaultRowLimit);
    setOpacity(kDefaultOpacity);
    setLabels(false);
}
} // namespace mr
