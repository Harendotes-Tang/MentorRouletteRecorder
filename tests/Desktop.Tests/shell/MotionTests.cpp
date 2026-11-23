// 动效 (docs/ui-design.md): the reduced-motion switch, the circular theme
// reveal and the classic panel entrance. The completion-trend chart has its
// own suite, TrendMotionTests.
//
// Every scene here is real QML from src/Desktop/qml (MotionSceneFixture.h).

#include "DesktopTestRunner.h"
#include "MotionSceneFixture.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "TrayController.h"

#include <QApplication>
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

#include <algorithm>
#include <cmath>
#include <functional>
#include <memory>

namespace {

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

QObject *overlayOf(const QQuickItem *reveal)
{
    return reveal->property("overlay").value<QObject *>();
}

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
    void renderingResumesAfterRepeatedHideAndShow();
    void hiddenPageStopsItsEntrance();
    void classicPanelsPopWhenTheirPageOpens();
    void eorzeaPanelsKeepTheirOwnEntrance();
};
} // namespace

void MotionTests::initTestCase()
{
    QVERIFY(mrtest::registerShippingQml());
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
        QVERIFY2(durations >= 12, qPrintable(QString::number(durations)));
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

void MotionTests::hiddenPageStopsItsEntrance()
{
    Scene scene;
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 400; height: 300; visible: true
    PageHost { objectName: "host"; index: 1; anchors.fill: parent
        Card { objectName: "panel"; width: 100; height: 40 }
    }
})", false, QStringLiteral("classic")), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    scene.controller.navigate(1);
    auto *panel = scene.item(QStringLiteral("panel"));
    QVERIFY(panel);
    QCOMPARE(panel->opacity(), 0.0);
    scene.controller.navigate(0);
    QCOMPARE(panel->opacity(), 1.0);
    auto *entrance = panel->property("popIn").value<QObject *>();
    QVERIFY(entrance);
    QTRY_VERIFY_WITH_TIMEOUT(!entrance->property("running").toBool(), 50);
    scene.window()->hide();
    scene.controller.navigate(1);
    auto *host = scene.item(QStringLiteral("host"));
    QVERIFY(host && !host->childItems().isEmpty());
    QCOMPARE(host->childItems().first()->opacity(), 1.0);
    QVERIFY(!entrance->property("running").toBool());
    scene.controller.navigate(0);
    scene.window()->show();
    scene.controller.navigate(1);
    QVERIFY(entrance->property("running").toBool());
    scene.engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
    QTRY_COMPARE_WITH_TIMEOUT(panel->opacity(), 1.0, 50);
    QTRY_VERIFY_WITH_TIMEOUT(!entrance->property("running").toBool(), 50);
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
