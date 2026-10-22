#include "StatisticsOverlayController.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "LiveText.h"
#include <QGuiApplication>
#include <QScreen>
#include <QTimer>
#include <algorithm>
#include <cmath>
#include <limits>
#include <utility>

namespace {
struct Module { const char *key; const char *title; const char *hint; };
// Design order; every module is on by default. 当前副本 / 副本用时 only show
// during a run and 待处理记录 only while something waits, whatever the switch.
const Module kModules[] = {
    {.key = "attempts", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "排本次数"), .hint = nullptr},
    {.key = "progress", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "累计进度"), .hint = nullptr},
    {.key = "current_duty", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "当前副本"),
     .hint = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "进本后显示")},
    {.key = "elapsed", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "副本用时"),
     .hint = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "进本后显示")},
    {.key = "dungeons", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "副本统计"), .hint = nullptr},
    {.key = "jobs", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "职业统计"), .hint = nullptr},
    {.key = "pending_work", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "待处理记录"),
     .hint = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "有待处理时显示")},
};
struct Period { const char *key; const char *title; int days; };
const Period kPeriods[] = {
    {.key = "today", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "当日"), .days = 1},
    {.key = "last7", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "近七天"), .days = 7},
    {.key = "last30", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "近30天"), .days = 30},
    {.key = "all", .title = QT_TRANSLATE_NOOP("mr::StatisticsOverlayController", "全部"), .days = 0},
};
QString validPeriod(const QString &period)
{
    const bool valid = std::ranges::any_of(kPeriods, [&period](const Period &option) {
        return period == QLatin1String(option.key);
    });
    return valid ? period : QStringLiteral("all");
}
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
    // Pre-period preferences never named this key; preserve the relative order
    // of their old modules and insert the new count first.
    if (!result.contains(QStringLiteral("attempts")))
        result.prepend(QStringLiteral("attempts"));
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
        const qint64 area = overlap.isValid() ? static_cast<qint64>(overlap.width()) * overlap.height() : 0;
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
        const qint64 area = overlap.isValid() ? static_cast<qint64>(overlap.width()) * overlap.height() : 0;
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
StatisticsOverlayController::StatisticsOverlayController(AppSettings *settings, QObject *parent, CalendarClock clock)
    : QObject(parent), m_settings(settings)
    , m_dungeons(new DungeonStatsModel(this)), m_jobs(new JobStatsModel(this))
    , m_clock(std::move(clock))
    , m_attemptPeriod(validPeriod(settings ? settings->statisticsOverlayAttemptPeriod() : QString()))
    , m_geometry(validGeometry(settings ? settings->statisticsOverlayGeometry() : QRect()))
    , m_form(validForm(settings ? settings->statisticsOverlayForm() : QString()))
{
    if (!m_clock)
        m_clock = [] { return QDateTime::currentDateTime(); };
    QStringList enabled = validModules(settings ? settings->statisticsOverlayModules(defaultModules()) : defaultModules());
    if (settings && settings->statisticsOverlayModuleVersion() < 1) {
        QStringList legacy = defaultModules();
        legacy.removeOne(QStringLiteral("attempts"));
        const bool completeLegacy = enabled.size() == legacy.size()
            && std::ranges::all_of(legacy, [&enabled](const QString &key) { return enabled.contains(key); });
        if (completeLegacy)
            enabled.prepend(QStringLiteral("attempts"));
        settings->setStatisticsOverlayModules(enabled);
        settings->setStatisticsOverlayModuleVersion(1);
    }
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
    m_calendarTimer = new QTimer(this);
    m_calendarTimer->setObjectName(QStringLiteral("statisticsOverlayCalendarTimer"));
    m_calendarTimer->setInterval(30000);
    connect(m_calendarTimer, &QTimer::timeout, this, &StatisticsOverlayController::checkAttemptCalendar);
    onLanguageChanged(this, [this] {
        Q_EMIT moduleOptionsChanged();
        Q_EMIT modulesChanged();
        Q_EMIT attemptPeriodOptionsChanged();
        Q_EMIT attemptsChanged();
    });
}
QStringList StatisticsOverlayController::defaultModules() { return moduleKeys(); }
QVariantList StatisticsOverlayController::attemptPeriodOptions() const
{
    QVariantList options;
    for (const auto &period : kPeriods)
        options.append(QVariantMap{{QStringLiteral("key"), QString::fromLatin1(period.key)},
                                   {QStringLiteral("title"), tr(period.title)}});
    return options;
}
QString StatisticsOverlayController::attemptsError() const
{
    return m_attemptsFailed ? tr("排本次数读取失败，请重试") : QString();
}
QJsonObject StatisticsOverlayController::attemptFilter(const QString &period, const QDate &today, const QTimeZone &zone)
{
    int days = 0;
    for (const auto &option : kPeriods)
        if (period == QLatin1String(option.key)) {
            days = option.days;
            break;
        }
    if (days == 0 || !today.isValid() || !zone.isValid())
        return {};
    const QDate first = today.addDays(1 - days);
    return {{QStringLiteral("date_field"), QStringLiteral("history_date")},
            {QStringLiteral("from_utc"), first.startOfDay(zone).toUTC().toString(Qt::ISODateWithMs)},
            {QStringLiteral("to_utc"), today.addDays(1).startOfDay(zone).addMSecs(-1).toUTC().toString(Qt::ISODateWithMs)},
            {QStringLiteral("history_from_day"), first.toString(Qt::ISODate)},
            {QStringLiteral("history_to_day"), today.toString(Qt::ISODate)}};
}
bool StatisticsOverlayController::attemptsApplicable() const
{
    return m_visible && m_form == QLatin1String("full") && m_modules.contains(QStringLiteral("attempts"));
}
bool StatisticsOverlayController::hasPeriodStatistics() const
{
    return m_modules.contains(QStringLiteral("attempts"))
        || m_modules.contains(QStringLiteral("dungeons")) || m_modules.contains(QStringLiteral("jobs"));
}
bool StatisticsOverlayController::statisticsApplicable() const
{
    return m_visible && m_form == QLatin1String("full") && hasPeriodStatistics();
}
bool StatisticsOverlayController::statisticsConnected() const
{
    return m_backend && m_backend->isConnected();
}
void StatisticsOverlayController::invalidateAttempts()
{
    ++m_attemptGeneration;
    // setBackend also invalidates each model's pending pagination callbacks.
    m_dungeons->setBackend(m_backend);
    m_jobs->setBackend(m_backend);
    m_attemptsKnown = false;
    m_attemptsLoading = false;
    m_attemptsFailed = false;
    Q_EMIT attemptsChanged();
}
void StatisticsOverlayController::updateAttemptsActivity()
{
    invalidateAttempts();
    if (statisticsApplicable()) {
        m_calendarTimer->start();
        refreshAttempts();
    } else {
        m_calendarTimer->stop();
    }
}
void StatisticsOverlayController::setBackend(IBackend *backend)
{
    if (m_backend == backend)
        return;
    if (m_backend)
        disconnect(m_backend, nullptr, this, nullptr);
    m_backend = backend;
    invalidateAttempts();
    if (backend) {
        connect(backend, &IBackend::connectionChanged, this, [this] {
            invalidateAttempts();
            refreshAttempts();
        });
        connect(backend, &QObject::destroyed, this, [this] {
            m_backend = nullptr;
            invalidateAttempts();
            if (attemptsApplicable()) {
                m_attemptsFailed = true;
                Q_EMIT attemptsChanged();
            }
        });
    }
    refreshAttempts();
}
void StatisticsOverlayController::refreshAttempts()
{
    if (!statisticsApplicable())
        return;
    const QDateTime now = m_clock();
    m_attemptDate = now.date();
    m_attemptZoneId = now.timeZone().id();
    m_attemptUtcOffset = now.offsetFromUtc();
    const QJsonObject filter = attemptFilter(m_attemptPeriod, now.date(), now.timeZone());
    if (m_modules.contains(QStringLiteral("dungeons")))
        m_dungeons->setFilter(filter.toVariantMap());
    if (m_modules.contains(QStringLiteral("jobs")))
        m_jobs->setFilter(filter.toVariantMap());
    if (!attemptsApplicable())
        return;
    const quint64 generation = ++m_attemptGeneration;
    m_attemptsKnown = false;
    m_attemptsLoading = m_backend && m_backend->isConnected();
    m_attemptsFailed = !m_attemptsLoading;
    Q_EMIT attemptsChanged();
    if (!m_attemptsLoading)
        return;
    m_backend->getDashboardStats(filter)
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            if (generation != m_attemptGeneration || !attemptsApplicable())
                return;
            const QJsonValue value = QJsonValue::fromVariant(payload.value(QStringLiteral("attempt_count")));
            const double count = value.toDouble(-1);
            const bool valid = ok && value.isDouble() && std::isfinite(count) && count >= 0
                && count <= std::numeric_limits<int>::max() && std::trunc(count) == count;
            m_attemptsKnown = valid;
            m_attemptsLoading = false;
            m_attemptsFailed = !valid;
            if (valid)
                m_attemptCount = static_cast<int>(count);
            Q_EMIT attemptsChanged();
        });
}
void StatisticsOverlayController::checkAttemptCalendar()
{
    if (!statisticsApplicable())
        return;
    const QDateTime now = m_clock();
    if (now.date() != m_attemptDate || now.timeZone().id() != m_attemptZoneId
        || now.offsetFromUtc() != m_attemptUtcOffset)
        refreshAttempts();
}
void StatisticsOverlayController::setAttemptPeriod(const QString &period)
{
    const QString bounded = validPeriod(period);
    if (m_attemptPeriod == bounded)
        return;
    m_attemptPeriod = bounded;
    if (m_settings)
        m_settings->setStatisticsOverlayAttemptPeriod(bounded);
    invalidateAttempts();
    Q_EMIT attemptPeriodChanged();
    refreshAttempts();
}
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
    updateAttemptsActivity();
}
void StatisticsOverlayController::hide()
{
    if (!m_visible)
        return;
    m_visible = false;
    setLocked(false);
    Q_EMIT visibleChanged();
    updateAttemptsActivity();
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
    const bool changedStatistics = m_modules.contains(QStringLiteral("attempts")) != enabled.contains(QStringLiteral("attempts"))
        || m_modules.contains(QStringLiteral("dungeons")) != enabled.contains(QStringLiteral("dungeons"))
        || m_modules.contains(QStringLiteral("jobs")) != enabled.contains(QStringLiteral("jobs"));
    m_order = order;
    m_modules = enabled;
    storeModules();
    if (changedStatistics)
        updateAttemptsActivity();
}
void StatisticsOverlayController::toggleModule(const QString &key)
{
    if (!m_order.contains(key)) return;
    QStringList enabled = m_modules;
    if (!enabled.removeOne(key)) enabled.append(key);
    m_modules = enabledInOrder(m_order, enabled);
    storeModules();
    if (key == QLatin1String("attempts") || key == QLatin1String("dungeons") || key == QLatin1String("jobs"))
        updateAttemptsActivity();
}
void StatisticsOverlayController::moveModule(const QString &key, int direction)
{
    if (direction != -1 && direction != 1) return;
    moveModuleTo(key, static_cast<int>(m_order.indexOf(key)) + direction);
}
void StatisticsOverlayController::moveModuleTo(const QString &key, int index)
{
    const qsizetype from = m_order.indexOf(key);
    if (from < 0) return;
    const qsizetype to = qBound(static_cast<qsizetype>(0), static_cast<qsizetype>(index), m_order.size() - 1);
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
    updateAttemptsActivity();
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
    if (m_attemptPeriod != QLatin1String("all")) {
        m_attemptPeriod = QStringLiteral("all");
        if (m_settings)
            m_settings->setStatisticsOverlayAttemptPeriod(m_attemptPeriod);
        Q_EMIT attemptPeriodChanged();
    }
    setRowLimit(kDefaultRowLimit);
    setOpacity(kDefaultOpacity);
    setLabels(false);
    updateAttemptsActivity();
}
} // namespace mr
