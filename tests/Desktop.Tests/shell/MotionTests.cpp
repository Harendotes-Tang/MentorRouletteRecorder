// 动效 (docs/ui-design.md): the reduced-motion switch, the circular theme
// reveal, the completion-trend merge / split and the classic panel entrance.
//
// Every scene here is real QML from src/Desktop/qml, loaded from source the way
// UiWorkflowRegressionTests does, on a scripted backend that answers {}.

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "TrayController.h"

#include <QApplication>
#include <QDateTime>
#include <QDir>
#include <QGuiApplication>
#include <QMetaProperty>
#include <QPointer>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>
#include <QTemporaryDir>
#include <QTimeZone>

#include <algorithm>
#include <cmath>
#include <functional>
#include <memory>

namespace {

class QuietBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++m_serial), type, this);
        QTimer::singleShot(0, reply, [reply] { reply->succeed({}); });
        return reply;
    }

private:
    int m_serial = 0;
};

/// A QML scene over a real AppController, with ReduceMotion and the style pinned.
struct Scene {
    QuietBackend backend;
    mr::AppController controller{&backend, nullptr};
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(const QByteArray &qml, bool reduceMotion, const QString &style = QString())
    {
        controller.setThemeMode(QStringLiteral("light"));
        auto *const context = engine.rootContext();
        context->setContextProperty(QStringLiteral("App"), &controller);
        context->setContextProperty(QStringLiteral("ReduceMotion"), reduceMotion);
        context->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QQmlComponent component(&engine);
        component.setData(qml, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }

    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }

    static QQuickItem *find(QQuickItem *from, const QString &name)
    {
        if (!from)
            return nullptr;
        if (from->objectName() == name)
            return from;
        const auto children = from->childItems();
        for (QQuickItem *child : children) {
            if (QQuickItem *match = find(child, name))
                return match;
        }
        return nullptr;
    }

    QQuickItem *item(const QString &name) const
    {
        return window() ? find(window()->contentItem(), name) : nullptr;
    }
};

const QByteArray &kRevealScene()
{
    static const QByteArray value = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    id: shell
    width: 480; height: 320; visible: true
    property int changes: 0
    function toggle(x, y) { reveal.run(function() { shell.changes += 1; App.toggleTheme() }, x, y) }
    Rectangle { id: content; anchors.fill: parent; color: Theme.surface }
    ThemeReveal { id: reveal; objectName: "reveal"; anchors.fill: parent; source: content; softwareFallback: true }
})";
    return value;
}

const QByteArray &kTrendScene()
{
    static const QByteArray value = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 720; height: 260; visible: true
    GraphsTrendChart { objectName: "chart"; anchors.fill: parent; anchors.margins: 10 }
})";
    return value;
}

QObject *overlayOf(const QQuickItem *reveal)
{
    return reveal->property("overlay").value<QObject *>();
}

/// Trend buckets like the Collector's: `count` per slot, `start_utc` per slot.
QVariantList buckets(const QString &granularity, const QList<int> &counts)
{
    const QDate today(2026, 9, 4);
    QList<QDateTime> starts;
    const auto n = static_cast<int>(counts.size());
    for (int i = n - 1; i >= 0; --i) {
        QDate day;
        if (granularity == QLatin1String("week"))
            day = today.addDays(-(today.dayOfWeek() - 1)).addDays(-7LL * i);
        else if (granularity == QLatin1String("month"))
            day = QDate(today.year(), today.month(), 1).addMonths(-i);
        else
            day = today.addDays(-i);
        starts.append(QDateTime(day, QTime(0, 0), QTimeZone::UTC));
    }
    QVariantList out;
    for (int i = 0; i < n; ++i) {
        out.append(QVariantMap{
            {QStringLiteral("label"), starts.at(i).toString(QStringLiteral("MM-dd"))},
            {QStringLiteral("count"), counts.at(i)},
            {QStringLiteral("start_utc"),
             starts.at(i).toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ"))}});
    }
    return out;
}

QList<int> dayCounts()
{
    return {1, 0, 1, 1, 3, 0, 1, 0, 2, 2, 2, 0, 0, 4, 0, 1, 0, 1, 0, 4, 2, 0, 1, 1, 1, 0, 2, 2, 2, 2};
}

QList<int> weekCounts()
{
    return {0, 0, 0, 1, 2, 5, 6, 3, 4, 2, 3, 3};
}

/// The bars Qt Graphs itself drew (QQuickRectangle children of its renderer),
/// in scene coordinates, left to right.
QList<QRectF> graphsBars(QQuickItem *chart)
{
    QList<QRectF> out;
    const QQuickItem *layer = chart->property("motionLayer").value<QQuickItem *>();
    std::function<void(QQuickItem *)> walk = [&](QQuickItem *item) {
        if (item == layer)
            return;
        if (QByteArray(item->metaObject()->className()) == "QQuickRectangle"
            && item->parentItem()
            && QByteArray(item->parentItem()->metaObject()->className()).contains("BarsRenderer"))
            out.append(item->mapRectToScene(QRectF(0, 0, item->width(), item->height())));
        const auto children = item->childItems();
        for (QQuickItem *child : children)
            walk(child);
    };
    const auto children = chart->childItems();
    for (QQuickItem *child : children) {
        if (QByteArray(child->metaObject()->className()).contains("GraphsView"))
            walk(child);
    }
    std::ranges::sort(out, [](const QRectF &a, const QRectF &b) { return a.x() < b.x(); });
    return out;
}

/// The motion layer's bar items, left to right.
QList<QQuickItem *> layerBars(QQuickItem *chart)
{
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QList<QQuickItem *> out;
    const auto children = layer->childItems();
    for (QQuickItem *child : children) {
        if (child->property("progress").isValid())
            out.append(child);
    }
    std::ranges::sort(out, [](QQuickItem *a, QQuickItem *b) { return a->x() < b->x(); });
    return out;
}

bool fuzzyEqual(qreal a, qreal b) { return qAbs(a - b) < 0.01; }

} // namespace

namespace {
class MotionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase();
    void reducedMotionZeroesEveryDuration();
    void reducedMotionNeverCreatesTheRevealOverlay();
    void revealOverlayLivesOnlyForTheAnimation();
    void secondRevealFinishesTheFirstAtOnce();
    void trendSwitchLeavesEveryBarAtRest();
    void trendMergeKeepsSurvivingBarsAndEasing();
    void interruptedTrendSwitchEndsClean();
    void reducedMotionTrendSwitchIsInstant();
    void trendHoverMatchesRenderedBars();
    void renderingResumesAfterRepeatedHideAndShow();
    void classicPanelsPopWhenTheirPageOpens();
    void eorzeaPanelsKeepTheirOwnEntrance();

private:
    static void verifyTrendAtRest(QQuickItem *chart, int bars);
};
} // namespace

void MotionTests::initTestCase()
{
    const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
    QVERIFY(QDir(qmlRoot).exists());
    qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                             "MentorRecorder", 1, 0, "Theme");
    for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs"),
                                  QStringLiteral("/pages"), QStringLiteral("/charts")}) {
        const auto files = QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files);
        for (const auto &file : files) {
            const QByteArray name = file.chopped(4).toUtf8();
            qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                            "MentorRecorder", 1, 0, name.constData());
        }
    }
}

void MotionTests::reducedMotionZeroesEveryDuration()
{
    const QByteArray qml = R"(import QtQuick
import MentorRecorder
QtObject { readonly property QtObject theme: Theme })";
    for (const bool reduce : {true, false}) {
        Scene scene;
        QVERIFY2(scene.create(qml, reduce), qPrintable(scene.errors));
        const QObject *theme = scene.root->property("theme").value<QObject *>();
        QVERIFY(theme);
        QCOMPARE(theme->property("motion").toBool(), !reduce);
        int durations = 0;
        const QMetaObject *meta = theme->metaObject();
        for (int index = 0; index < meta->propertyCount(); ++index) {
            const QMetaProperty property = meta->property(index);
            const QByteArray name = property.name();
            if (!name.startsWith("motion") || name == "motion")
                continue;
            QCOMPARE(property.metaType().id(), QMetaType::Int);
            const int value = property.read(theme).toInt();
            if (reduce)
                QVERIFY2(value == 0, name.constData());
            else
                QVERIFY2(value > 0, name.constData());
            ++durations;
        }
        // motionFast ... motionReveal: the whole vocabulary, not a subset.
        QVERIFY2(durations >= 15, qPrintable(QString::number(durations)));
    }
}

void MotionTests::reducedMotionNeverCreatesTheRevealOverlay()
{
    Scene scene;
    QVERIFY2(scene.create(kRevealScene(), true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *reveal = scene.item(QStringLiteral("reveal"));
    QVERIFY(reveal);
    QVERIFY(!scene.controller.isDark());
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 40), Q_ARG(QVariant, 30)));
    // The theme flips in the same call, and nothing is laid over the window.
    QVERIFY(scene.controller.isDark());
    QCOMPARE(scene.root->property("changes").toInt(), 1);
    QVERIFY(!overlayOf(reveal));
    QVERIFY(!reveal->property("running").toBool());
    QTest::qWait(700);
    QVERIFY(!overlayOf(reveal));
    QCOMPARE(reveal->property("createdCount").toInt(), 0);
}

void MotionTests::revealOverlayLivesOnlyForTheAnimation()
{
    Scene scene;
    QVERIFY2(scene.create(kRevealScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *reveal = scene.item(QStringLiteral("reveal"));
    QVERIFY(reveal);
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 460), Q_ARG(QVariant, 10)));
    // The capture comes first; the theme changes when it is in hand.
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) != nullptr, 5000);
    QVERIFY(scene.controller.isDark());
    QCOMPARE(reveal->property("createdCount").toInt(), 1);
    auto *overlay = qobject_cast<QQuickItem *>(overlayOf(reveal));
    QVERIFY(overlay);
    QVERIFY(overlay->isVisible());
    // Never in the way of the pointer: nothing in the overlay takes a button.
    std::function<void(QQuickItem *)> noInput = [&](QQuickItem *item) {
        QCOMPARE(item->acceptedMouseButtons(), Qt::NoButton);
        QVERIFY(!item->acceptHoverEvents());
        const auto children = item->childItems();
        for (QQuickItem *child : children)
            noInput(child);
    };
    noInput(overlay);
    // To dark: the hole opens from 0 towards the farthest corner (0, 320).
    QCOMPARE(overlay->property("expand").toBool(), true);
    QVERIFY(fuzzyEqual(overlay->property("targetRadius").toReal(), std::hypot(460.0, 310.0)));
    // Gone once the circle is done (520 ms), and not a frame later than needed.
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) == nullptr, 3000);
    QVERIFY(!reveal->property("running").toBool());

    // Back to light: the old frame shrinks from the farthest corner to 0.
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 20), Q_ARG(QVariant, 20)));
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) != nullptr, 5000);
    QVERIFY(!scene.controller.isDark());
    const auto *shrink = qobject_cast<QQuickItem *>(overlayOf(reveal));
    QCOMPARE(shrink->property("expand").toBool(), false);
    QCOMPARE(shrink->property("targetRadius").toReal(), 0.0);
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) == nullptr, 3000);
    QCOMPARE(scene.root->property("changes").toInt(), 2);
}

void MotionTests::secondRevealFinishesTheFirstAtOnce()
{
    Scene scene;
    QVERIFY2(scene.create(kRevealScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *reveal = scene.item(QStringLiteral("reveal"));
    QVERIFY(reveal);

    // A second click while the capture is still pending applies the first
    // change at once and starts over.
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 10), Q_ARG(QVariant, 10)));
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 10), Q_ARG(QVariant, 10)));
    QCOMPARE(scene.root->property("changes").toInt(), 1);
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) != nullptr, 5000);
    QCOMPARE(scene.root->property("changes").toInt(), 2);
    QVERIFY(!scene.controller.isDark());

    // A second click mid-animation drops the running overlay in the same call.
    QPointer<QObject> first = overlayOf(reveal);
    QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "toggle", Q_ARG(QVariant, 10), Q_ARG(QVariant, 10)));
    QVERIFY(overlayOf(reveal) == nullptr);
    QVERIFY(first.isNull() || !qobject_cast<QQuickItem *>(first)->isVisible());
    QTRY_VERIFY_WITH_TIMEOUT(first.isNull(), 2000);
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) != nullptr, 5000);
    QVERIFY(scene.controller.isDark());
    QTRY_VERIFY_WITH_TIMEOUT(overlayOf(reveal) == nullptr, 3000);
    QCOMPARE(scene.root->property("changes").toInt(), 3);
    QCOMPARE(reveal->property("createdCount").toInt(), 2);
}

void MotionTests::renderingResumesAfterRepeatedHideAndShow()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import MentorRecorder
Window {
    id: renderWindow
    width: 480; height: 320; visible: true
    flags: Qt.Window | Qt.FramelessWindowHint | Qt.WindowMinimizeButtonHint
           | Qt.WindowMaximizeButtonHint | Qt.WindowCloseButtonHint
    property bool alternate: false
    Rectangle { anchors.fill: parent; color: renderWindow.alternate ? "red" : "blue" }
    Window {
        objectName: "auxiliary"; width: 120; height: 80; color: "green"
        transientParent: null
        flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint
    }
})", true), qPrintable(scene.errors));
    auto *window = scene.window();
    auto *auxiliary = scene.root->findChild<QQuickWindow *>(QStringLiteral("auxiliary"));
    QVERIFY(window && auxiliary);
    QTemporaryDir settingsDirectory;
    QVERIFY(settingsDirectory.isValid());
    mr::AppSettings settings(settingsDirectory.filePath(QStringLiteral("desktop.ini")), nullptr);
    settings.setMinimizeToTray(true);
    mr::TrayController tray(nullptr, &settings, window);
    QVERIFY(QTest::qWaitForWindowExposed(window));
    int frames = 0;
    QObject frameReceiver;
    // Consume render-thread signals on the GUI thread; no cross-thread counter access.
    const auto connection = connect(window, &QQuickWindow::frameSwapped, &frameReceiver,
                                    [&frames] { ++frames; }, Qt::QueuedConnection);
    const auto disconnect = qScopeGuard([&] { QObject::disconnect(connection); });
    for (const bool auxiliaryVisible : {false, true}) {
        auxiliary->setVisible(auxiliaryVisible);
        for (int cycle = 0; cycle < 6; ++cycle) {
            if (tray.isActive()) {
                QVERIFY(!window->close()); // Close is ignored and converted to hide.
                QVERIFY(!window->isVisible());
            } else {
                window->hide(); // Offscreen platforms do not have a notification area.
            }
            QTest::qWait(40);
            tray.showWindow();
            QVERIFY(QTest::qWaitForWindowExposed(window));
            QTest::qWait(40);
            const int before = frames;
            QVERIFY(window->setProperty("alternate", !window->property("alternate").toBool()));
            // Do not call grabWindow()/update(): either could wake a stalled renderer
            // and conceal the fault this probe is intended to detect.
            QTRY_VERIFY_WITH_TIMEOUT(frames > before, 2000);
        }
    }
}

void MotionTests::trendHoverMatchesRenderedBars()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), true), qPrintable(scene.errors));
    auto *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *hover = chart->findChild<QQuickItem *>(QStringLiteral("trendHoverArea"));
    QVERIFY(hover);
    for (const int width : {720, 1100}) {
        scene.window()->setWidth(width);
        for (const int count : {7, 30}) {
            QVERIFY(chart->setProperty("buckets", buckets(QStringLiteral("day"), QList<int>(count, 5))));
            QTRY_COMPARE(graphsBars(chart).size(), count);
            QTest::qWait(100);
            const auto drawn = graphsBars(chart);
            for (int i = 0; i < count; ++i) {
                QTest::mouseMove(scene.window(), drawn.at(i).center().toPoint());
                QTRY_COMPARE(hover->property("index").toInt(), i);
            }
            // The chart's outer margin is not a data bucket.
            QTest::mouseMove(scene.window(), chart->mapToScene(QPointF(1, 1)).toPoint());
            QTRY_COMPARE(hover->property("index").toInt(), -1);
        }
    }
}

void MotionTests::verifyTrendAtRest(QQuickItem *chart, int bars)
{
    QVERIFY(!chart->property("transitionRunning").toBool());
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(layer);
    QVERIFY(!layer->isVisible());
    const QList<QQuickItem *> items = layerBars(chart);
    QCOMPARE(items.size(), bars);
    for (int i = 0; i < items.size(); ++i) {
        const QQuickItem *bar = items.at(i);
        const QString where = QStringLiteral("bar %1").arg(i);
        QVERIFY2(fuzzyEqual(bar->opacity(), 1.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->property("widthScale").toReal(), 1.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->property("heightScale").toReal(), 1.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->property("shift").toReal(), 0.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->property("progress").toReal(), 1.0), qPrintable(where));
        QVERIFY2(fuzzyEqual(bar->scale(), 1.0), qPrintable(where));
    }
    // Qt Graphs is back on screen, and the layer's resting bars sit exactly on
    // its pixels - the hand-over at the end of every transition is seamless.
    const QQuickItem *view = nullptr;
    const auto children = chart->childItems();
    for (const QQuickItem *child : children) {
        if (QByteArray(child->metaObject()->className()).contains("GraphsView"))
            view = child;
    }
    if (!view) {
        QTest::qFail("The chart has no GraphsView child", __FILE__, __LINE__);
        return;
    }
    QVERIFY(fuzzyEqual(view->opacity(), 1.0));
    QTRY_COMPARE(graphsBars(chart).size(), bars);
    const QList<QRectF> drawn = graphsBars(chart);
    for (int i = 0; i < items.size(); ++i) {
        const QRectF mine = items.at(i)->mapRectToScene(
            QRectF(0, 0, items.at(i)->width(), items.at(i)->height()));
        const QRectF theirs = drawn.at(i);
        const QString detail = QStringLiteral("bar %1: layer (%2,%3 %4x%5) graphs (%6,%7 %8x%9)")
                                   .arg(i).arg(mine.x()).arg(mine.y()).arg(mine.width())
                                   .arg(mine.height()).arg(theirs.x()).arg(theirs.y())
                                   .arg(theirs.width()).arg(theirs.height());
        QVERIFY2(fuzzyEqual(mine.x(), theirs.x()) && fuzzyEqual(mine.width(), theirs.width())
                     && fuzzyEqual(mine.bottom(), theirs.bottom())
                     && fuzzyEqual(mine.height(), theirs.height()),
                 qPrintable(detail));
    }
}

void MotionTests::trendSwitchLeavesEveryBarAtRest()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);

    // The first series is simply shown.
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    // 日 -> 周 merges: the old bars move first, then the new ones grow.
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    QVERIFY(chart->property("transitionRunning").toBool());
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
    QCOMPARE(layerBars(chart).size(), 30);
    QTRY_COMPARE_WITH_TIMEOUT(layer->property("phase").toString(), QStringLiteral("regrow"), 3000);
    QCOMPARE(layerBars(chart).size(), 12);
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 12);
    if (QTest::currentTestFailed())
        return;

    // 周 -> 日 splits.
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QCOMPARE(layerBars(chart).size(), 30);
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    // Same slots, new counts: only the heights move.
    QList<int> changed = dayCounts();
    changed[29] = 6;
    chart->setProperty("buckets", buckets(QStringLiteral("day"), changed));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("height"));
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    // The same answer again (a dashboard refresh) moves nothing.
    chart->setProperty("buckets", buckets(QStringLiteral("day"), changed));
    QVERIFY(!chart->property("transitionRunning").toBool());
}

void MotionTests::trendMergeKeepsSurvivingBarsAndEasing()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QTRY_COMPARE(graphsBars(chart).size(), 30);
    QList<QPointer<QQuickItem>> surviving;
    for (auto *bar : layerBars(chart).mid(0, 12))
        surviving.append(bar);

    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTest::qWait(120);
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));

    // Independently solve the existing merge curve at the live clock position.
    const auto at = [](double p1, double p2, double t) {
        const double u = 1.0 - t;
        return (3.0*u*u*t*p1) + (3.0*u*t*t*p2) + (t*t*t);
    };
    const double x = layer->property("elapsed").toDouble() / 380.0;
    double low = 0.0;
    double high = 1.0;
    for (int step = 0; step < 48; ++step) {
        const double t = (low + high) * 0.5;
        if (at(0.4, 0.2, t) < x) low = t;
        else high = t;
    }
    const double expected = at(0.0, 1.0, (low + high) * 0.5);
    const auto moving = layerBars(chart);
    QCOMPARE(moving.size(), 30);
    for (auto *bar : moving) {
        QVERIFY(std::abs(bar->property("progress").toDouble() - expected) < 0.00001);
        QVERIFY(std::abs(bar->property("widthScale").toDouble() - (1.0 - (0.4 * expected))) < 0.00001);
        QVERIFY(std::abs(bar->opacity() - (1.0 - (0.65 * expected))) < 0.00001);
    }
    for (int index = 0; index < surviving.size(); ++index) {
        QVERIFY(surviving.at(index));
        QCOMPARE(moving.at(index), surviving.at(index).data());
    }
    QTRY_COMPARE_WITH_TIMEOUT(layer->property("phase").toString(), QStringLiteral("regrow"), 3000);
    const auto regrowing = layerBars(chart);
    QCOMPARE(regrowing.size(), 12);
    for (int index = 0; index < surviving.size(); ++index) {
        QVERIFY(surviving.at(index));
        QCOMPARE(regrowing.at(index), surviving.at(index).data());
    }
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 12);
}

void MotionTests::interruptedTrendSwitchEndsClean()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), false), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    auto *layer = chart->property("motionLayer").value<QQuickItem *>();
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QTRY_COMPARE(graphsBars(chart).size(), 30);

    // Back to 日 in the middle of the merge.
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTest::qWait(120);
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    verifyTrendAtRest(chart, 30);
    if (QTest::currentTestFailed())
        return;

    // To 月 in the middle of the regrow that follows a merge, then back to 周
    // (a split) and the same 周 answer again mid-split: every hand-over is
    // cancelled or left alone as it should be, nothing is left behind.
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTRY_COMPARE_WITH_TIMEOUT(layer->property("phase").toString(), QStringLiteral("regrow"), 3000);
    chart->setProperty("buckets", buckets(QStringLiteral("month"), {3, 4, 9, 12, 8, 2}));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("merge"));
    QTRY_COMPARE_WITH_TIMEOUT(layer->property("phase").toString(), QStringLiteral("regrow"), 3000);
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QCOMPARE(layer->property("phase").toString(), QStringLiteral("split"));
    QTest::qWait(60);
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QTRY_VERIFY_WITH_TIMEOUT(!chart->property("transitionRunning").toBool(), 3000);
    // A late phase hand-over from a cancelled run must not restart anything.
    QTest::qWait(600);
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 12);
    if (QTest::currentTestFailed())
        return;
}

void MotionTests::reducedMotionTrendSwitchIsInstant()
{
    Scene scene;
    QVERIFY2(scene.create(kTrendScene(), true), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *chart = scene.item(QStringLiteral("chart"));
    QVERIFY(chart);
    chart->setProperty("buckets", buckets(QStringLiteral("day"), dayCounts()));
    chart->setProperty("buckets", buckets(QStringLiteral("week"), weekCounts()));
    QVERIFY(!chart->property("transitionRunning").toBool());
    verifyTrendAtRest(chart, 12);
    if (QTest::currentTestFailed())
        return;
}

void MotionTests::classicPanelsPopWhenTheirPageOpens()
{
    const QByteArray qml = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 400; height: 300; visible: true
    PageHost {
        index: 1
        anchors.fill: parent
        Column {
            Card { objectName: "first"; width: 100; height: 40 }
            Card { objectName: "second"; width: 100; height: 40 }
        }
    }
})";
    Scene scene;
    QVERIFY2(scene.create(qml, false, QStringLiteral("classic")), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    QQuickItem *first = scene.item(QStringLiteral("first"));
    QQuickItem *second = scene.item(QStringLiteral("second"));
    QVERIFY(first && second);
    QCOMPARE(first->opacity(), 1.0);

    scene.controller.navigate(1);
    // Both start transparent at .97; the second waits one 30 ms step.
    QCOMPARE(first->opacity(), 0.0);
    QVERIFY(fuzzyEqual(first->scale(), 0.97));
    QCOMPARE(second->opacity(), 0.0);
    QTRY_VERIFY_WITH_TIMEOUT(first->opacity() > 0.0, 1000);
    QTRY_VERIFY_WITH_TIMEOUT(fuzzyEqual(first->opacity(), 1.0) && fuzzyEqual(second->opacity(), 1.0), 2000);
    QVERIFY(fuzzyEqual(first->scale(), 1.0));
    QVERIFY(fuzzyEqual(second->scale(), 1.0));
}

void MotionTests::eorzeaPanelsKeepTheirOwnEntrance()
{
    const QByteArray qml = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 400; height: 300; visible: true
    PageHost {
        index: 1
        anchors.fill: parent
        Card { objectName: "panel"; width: 100; height: 40 }
    }
})";
    for (const bool reduce : {false, true}) {
        Scene scene;
        const QString style = reduce ? QStringLiteral("classic") : QStringLiteral("eorzea");
        QVERIFY2(scene.create(qml, reduce, style), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QQuickItem *panel = scene.item(QStringLiteral("panel"));
        QVERIFY(panel);
        scene.controller.navigate(1);
        // Eorzea (and reduced motion) never pops a panel.
        QCOMPARE(panel->opacity(), 1.0);
        QCOMPARE(panel->scale(), 1.0);
    }
}

MR_DESKTOP_TEST_SUITE(Motion)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QApplication app(argc, argv);
    MotionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "MotionTests.moc"
