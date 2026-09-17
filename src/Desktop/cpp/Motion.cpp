#include "Motion.h"

#include "AppController.h"
#include "RunListModel.h"

#include <QAnimationDriver>
#include <QCoreApplication>
#include <QDir>
#include <QElapsedTimer>
#include <QEventLoop>
#include <QFileInfo>
#include <QImage>
#include <QMetaObject>
#include <QPainter>
#include <QQuickItem>
#include <QQuickItemGrabResult>
#include <QSharedPointer>
#include <QQuickWindow>
#include <QTimer>

#include <cstdio>
#include <functional>

#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace mr::motion {

namespace {

/// The probe's animation clock. It only moves when step() is called; before
/// the probe starts a timer steps it in real time.
class ProbeClock final : public QAnimationDriver
{
public:
    ProbeClock()
    {
        QObject::connect(&m_realTime, &QTimer::timeout, &m_realTime, [this] {
            const qint64 now = m_wall.elapsed();
            step(now - m_wallSeen);
            m_wallSeen = now;
        });
    }

    qint64 elapsed() const override { return m_elapsed; }

    void step(qint64 milliseconds)
    {
        m_elapsed += milliseconds;
        advance();
    }

    void followRealTime(bool follow)
    {
        if (!follow) {
            m_realTime.stop();
            return;
        }
        m_wall.start();
        m_wallSeen = 0;
        m_realTime.start(8);
    }

protected:
    // QUnifiedTimer reads elapsed() as the time since the driver (re)started.
    void start() override
    {
        m_elapsed = 0;
        QAnimationDriver::start();
    }

private:
    qint64 m_elapsed = 0;
    QTimer m_realTime;
    QElapsedTimer m_wall;
    qint64 m_wallSeen = 0;
};

ProbeClock *probeClock = nullptr;

QQuickItem *findItem(QQuickItem *root, const QString &objectName)
{
    if (!root)
        return nullptr;
    if (root->objectName() == objectName)
        return root;
    const auto children = root->childItems();
    for (QQuickItem *child : children) {
        if (QQuickItem *match = findItem(child, objectName))
            return match;
    }
    return nullptr;
}

/// Runs the event loop (clock frozen) until \a condition holds or \a timeoutMs passes.
bool waitFor(const std::function<bool()> &condition, int timeoutMs)
{
    QElapsedTimer timer;
    timer.start();
    while (!condition() && timer.elapsed() < timeoutMs) {
        QEventLoop loop;
        QTimer::singleShot(10, &loop, &QEventLoop::quit);
        loop.exec();
    }
    return condition();
}

void settleEvents()
{
    for (int round = 0; round < 3; ++round)
        QCoreApplication::processEvents(QEventLoop::AllEvents, 20);
}

/// Moves the clock forward in 4 ms steps, letting bindings and posted calls run.
void advanceClock(qint64 milliseconds)
{
    while (milliseconds > 0) {
        const qint64 step = qMin<qint64>(4, milliseconds);
        probeClock->step(step);
        QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
        milliseconds -= step;
    }
}

/// The window content as the next rendered frame shows it. An offscreen window
/// on the GPU renderer has no swap chain, so QQuickWindow::grabWindow() reads
/// back black; an item grab renders into its own texture and works.
bool writeFrame(QQuickWindow *window, const QString &path)
{
    QQuickItem *content = window->contentItem();
    const qreal ratio = window->effectiveDevicePixelRatio();
    const QSharedPointer<QQuickItemGrabResult> grab =
        content->grabToImage((content->size() * ratio).toSize());
    if (!grab) {
        std::fputs("motion probe: the window content cannot be grabbed\n", stderr);
        return false;
    }
    bool ready = false;
    QObject::connect(grab.data(), &QQuickItemGrabResult::ready, grab.data(),
                     [&ready] { ready = true; });
    window->update();
    if (!waitFor([&ready] { return ready; }, 5000) || grab->image().isNull()) {
        std::fputs("motion probe: no frame was rendered for the grab\n", stderr);
        return false;
    }
    QImage frame(grab->image().size(), QImage::Format_RGB32);
    frame.fill(window->color());
    {
        QPainter painter(&frame);
        painter.drawImage(0, 0, grab->image());
    }
    QDir().mkpath(QFileInfo(path).absolutePath());
    if (!frame.save(path)) {
        std::fprintf(stderr, "cannot write %s\n", qPrintable(path));
        return false;
    }
    std::fprintf(stdout, "wrote %s (%dx%d)\n", qPrintable(path), frame.width(), frame.height());
    return true;
}

QQuickItem *trendChart(QQuickWindow *window)
{
    return findItem(window->contentItem(), QStringLiteral("completionTrendChart"));
}

bool trendMoving(QQuickWindow *window)
{
    const QQuickItem *chart = trendChart(window);
    return chart && chart->property("transitionRunning").toBool();
}

/// Switches the trend granularity and waits for the transition to begin.
bool triggerTrend(QQuickWindow *window, AppController *controller, const QString &mode)
{
    if (!trendChart(window)) {
        std::fputs("motion probe: the dashboard renders no Qt Graphs trend chart\n", stderr);
        return false;
    }
    controller->setTrendMode(mode);
    if (!waitFor([window] { return trendMoving(window); }, 3000)) {
        std::fprintf(stderr, "motion probe: switching the trend to %s started no transition\n",
                     qPrintable(mode));
        return false;
    }
    return true;
}

bool triggerTheme(QQuickWindow *window)
{
    QQuickItem *button = findItem(window->contentItem(), QStringLiteral("themeToggleButton"));
    QQuickItem *reveal = findItem(window->contentItem(), QStringLiteral("themeReveal"));
    if (!button || !reveal) {
        std::fputs("motion probe: theme button or reveal layer missing\n", stderr);
        return false;
    }
    QMetaObject::invokeMethod(button, "clicked");
    // The capture needs a rendered frame; the clock stays frozen meanwhile.
    if (!waitFor([reveal] { return reveal->property("overlay").value<QObject *>() != nullptr; },
                 3000)) {
        std::fputs("motion probe: the theme reveal never showed its overlay\n", stderr);
        return false;
    }
    return true;
}

bool triggerHistoryRows(const AppController *controller)
{
    RunListModel *runs = controller->runs();
    bool reset = false;
    const auto connection = QObject::connect(runs, &QAbstractItemModel::modelReset,
                                             runs, [&reset] { reset = true; });
    runs->reload();
    const bool ok = waitFor([&reset] { return reset; }, 3000);
    QObject::disconnect(connection);
    if (!ok)
        std::fputs("motion probe: the history rows were not reloaded\n", stderr);
    return ok;
}

} // namespace

bool systemReducesMotion()
{
#ifdef Q_OS_WIN
#ifndef SPI_GETCLIENTAREAANIMATION
#define SPI_GETCLIENTAREAANIMATION 0x1042
#endif
    BOOL animate = TRUE;
    if (SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animate, 0))
        return animate == FALSE;
    return false;
#else
    return false;
#endif
}

QStringList probeScenarios()
{
    return {QStringLiteral("theme"), QStringLiteral("trend-week"), QStringLiteral("trend-month"),
            QStringLiteral("trend-day"), QStringLiteral("history-rows")};
}

bool isProbeScenario(const QString &name)
{
    return probeScenarios().contains(name);
}

QList<int> probeFrameTimes()
{
    return {0, 80, 160, 260, 400, 600};
}

QString probeFramePath(const QString &screenshotTarget, int milliseconds)
{
    const QFileInfo info(screenshotTarget);
    const QString suffix = info.suffix().isEmpty() ? QStringLiteral("png") : info.suffix();
    return info.dir().filePath(QStringLiteral("%1-t%2.%3")
                                   .arg(info.completeBaseName())
                                   .arg(milliseconds)
                                   .arg(suffix));
}

void installProbeClock()
{
    if (probeClock)
        return;
    probeClock = new ProbeClock;
    probeClock->install();
    probeClock->followRealTime(true);
    // Hand the clock back while the event loop still exists.
    QObject::connect(QCoreApplication::instance(), &QCoreApplication::aboutToQuit, [] {
        if (!probeClock)
            return;
        probeClock->followRealTime(false);
        probeClock->uninstall();
        delete probeClock;
        probeClock = nullptr;
    });
}

int runProbe(QQuickWindow *window, AppController *controller, const QString &scenario,
             const QString &screenshotTarget)
{
    if (!probeClock || !window || !controller) {
        std::fputs("motion probe: not set up\n", stderr);
        return 10;
    }
    // The start-up entrances have ended in real time; from here on the clock
    // only moves when the probe moves it.
    probeClock->followRealTime(false);
    settleEvents();

    if (scenario == QLatin1String("trend-day")) {
        // A split needs a coarser series on screen first.
        if (!triggerTrend(window, controller, QStringLiteral("week")))
            return 11;
        advanceClock(2000);
        settleEvents();
    }

    if (!writeFrame(window, screenshotTarget))
        return 4;

    bool triggered = false;
    if (scenario == QLatin1String("theme"))
        triggered = triggerTheme(window);
    else if (scenario == QLatin1String("trend-week"))
        triggered = triggerTrend(window, controller, QStringLiteral("week"));
    else if (scenario == QLatin1String("trend-month"))
        triggered = triggerTrend(window, controller, QStringLiteral("month"));
    else if (scenario == QLatin1String("trend-day"))
        triggered = triggerTrend(window, controller, QStringLiteral("day"));
    else if (scenario == QLatin1String("history-rows"))
        triggered = triggerHistoryRows(controller);
    if (!triggered)
        return 11;

    qint64 now = 0;
    const QList<int> times = probeFrameTimes();
    for (const int target : times) {
        advanceClock(target - now);
        now = target;
        settleEvents();
        if (!writeFrame(window, probeFramePath(screenshotTarget, target)))
            return 5;
    }

    // Everything must be at rest well after the last frame.
    advanceClock(1500);
    settleEvents();
    const QQuickItem *reveal = findItem(window->contentItem(), QStringLiteral("themeReveal"));
    const bool revealLeft = reveal && reveal->property("overlay").value<QObject *>() != nullptr;
    if (revealLeft || trendMoving(window)) {
        std::fputs("motion probe: an animation is still running after 2.1 s\n", stderr);
        return 12;
    }
    std::fprintf(stdout, "motion probe %s: %lld frames\n", qPrintable(scenario),
                 static_cast<long long>(times.size()));
    return 0;
}

} // namespace mr::motion
