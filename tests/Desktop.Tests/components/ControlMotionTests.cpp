#include "DesktopTestRunner.h"
#include "MotionSceneFixture.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "QmlRuntime.h"
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

#include <memory>

namespace {
class ControlMotionTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase() { mrtest::registerSourceQmlComponents(); }
    void pressedControlKeepsItsHitArea_data();
    void pressedControlKeepsItsHitArea();
    void reducedMotionSettlesSwitchKnob();
    void reducedMotionCompletesColourFeedback();
    void hoverFadeRestsOnTheHoverColour_data();
    void hoverFadeRestsOnTheHoverColour();
};

void ControlMotionTests::pressedControlKeepsItsHitArea_data()
{
    QTest::addColumn<QByteArray>("control");
    QTest::addColumn<QByteArray>("signal");
    QTest::newRow("button") << QByteArray("AppButton { text: 'Action';") << QByteArray("clicked()");
    QTest::newRow("navigation") << QByteArray("NavItem { label: 'Page';") << QByteArray("clicked()");
    QTest::newRow("chip") << QByteArray("PickChip { text: 'Choice';") << QByteArray("picked()");
    QTest::newRow("surface") << QByteArray("PickSurface { accessibleName: 'Choice';") << QByteArray("picked()");
}

void ControlMotionTests::pressedControlKeepsItsHitArea()
{
    QFETCH(QByteArray, control);
    QFETCH(QByteArray, signal);
    mrtest::QmlComponentScene scene;
    auto *runtime = mr::QmlRuntime::forEngine(&scene.engine);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, false);
    const QByteArray qml = "import QtQuick\nimport QtQuick.Window\nimport MentorRecorder\n"
        "Window { width: 320; height: 160; visible: true\n" + control
        + " objectName: 'control'; x: 30; y: 30; width: 160; height: 40 } }";
    QVERIFY2(scene.create(qml, QStringLiteral("/components/ControlMotionProbe.qml")), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *item = scene.root->findChild<QQuickItem *>(QStringLiteral("control"));
    QVERIFY(item);
    const auto methodIndex = item->metaObject()->indexOfSignal(signal.constData());
    QVERIFY(methodIndex >= 0);
    QSignalSpy actions(item, item->metaObject()->method(methodIndex));
    const QRectF original = item->mapRectToScene(QRectF(0, 0, item->width(), item->height()));
    const QPoint position = original.center().toPoint();
    QTest::mousePress(scene.window(), Qt::LeftButton, Qt::NoModifier, position);
    QTest::qWait(45);
    QCOMPARE(item->mapRectToScene(QRectF(0, 0, item->width(), item->height())), original);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, true);
    if (item->property("visualScale").isValid())
        QTRY_COMPARE_WITH_TIMEOUT(item->property("visualScale").toReal(), 1.0, 100);
    QCOMPARE(item->scale(), 1.0);
    QTest::mouseRelease(scene.window(), Qt::LeftButton, Qt::NoModifier, position);
    QCOMPARE(actions.count(), 1);
    item->forceActiveFocus(Qt::TabFocusReason);
    QTest::keyClick(scene.window(), Qt::Key_Space);
    QCOMPARE(actions.count(), 2);
}

void ControlMotionTests::reducedMotionSettlesSwitchKnob()
{
    mrtest::QmlComponentScene scene;
    auto *runtime = mr::QmlRuntime::forEngine(&scene.engine);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, false);
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window { width: 200; height: 100; visible: true
    ToggleSwitch { objectName: "switch"; x: 30; y: 30 }
})", QStringLiteral("/components/ControlMotionProbe.qml")), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *control = scene.root->findChild<QQuickItem *>(QStringLiteral("switch"));
    auto *knob = scene.root->findChild<QQuickItem *>(QStringLiteral("toggleKnob"));
    QVERIFY(control && knob);
    control->setProperty("checked", true);
    QTest::qWait(45);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, true);
    QTRY_COMPARE_WITH_TIMEOUT(knob->x(), control->width() - knob->width() - 2, 100);
    QCOMPARE(knob->width(), 16.0);
}

void ControlMotionTests::reducedMotionCompletesColourFeedback()
{
    mrtest::QmlComponentScene scene;
    auto *runtime = mr::QmlRuntime::forEngine(&scene.engine);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, false);
    QVERIFY2(scene.create(R"(import QtQuick
import QtQuick.Window
import MentorRecorder
Window { width: 200; height: 100; visible: true
    PickSurface { objectName: "surface"; x: 20; y: 20; width: 160; height: 50
        property color selectedColour: Theme.accentMuted
    }
})", QStringLiteral("/components/ControlMotionProbe.qml")), qPrintable(scene.errors));
    QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
    auto *surface = scene.root->findChild<QQuickItem *>(QStringLiteral("surface"));
    QVERIFY(surface);
    surface->setProperty("selected", true);
    QTest::qWait(30);
    runtime->setValue(mr::QmlRuntime::Field::ReduceMotion, true);
    QCOMPARE(surface->property("color").value<QColor>(), surface->property("selectedColour").value<QColor>());
}

// ColorAnimation interpolates straight RGBA, and "transparent" is #00000000: a fade from it
// to the classic light fill (#f2f2f7) passes through half-opaque grey, so every sidebar
// item the pointer crosses flashes a dark plate. A control at rest must hold its hover
// colour at zero alpha, so the fade only ever changes alpha. Each control declares that
// colour as `hoverColor` (a classic ghost button hovers to accent-100, everything else to
// the fill).
void ControlMotionTests::hoverFadeRestsOnTheHoverColour_data()
{
    QTest::addColumn<QString>("style");
    QTest::addColumn<QString>("theme");
    for (const auto &style : {QStringLiteral("classic"), QStringLiteral("eorzea")}) {
        for (const auto &theme : {QStringLiteral("light"), QStringLiteral("dark")})
            QTest::newRow(qPrintable(style + QLatin1Char('-') + theme)) << style << theme;
    }
}

void ControlMotionTests::hoverFadeRestsOnTheHoverColour()
{
    QFETCH(QString, style);
    QFETCH(QString, theme);
    QuietBackend backend;
    mr::AppController controller{&backend, nullptr};
    controller.setThemeMode(theme);
    QQmlEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
    engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
    engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
    QQmlComponent component(&engine);
    component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 400; height: 200; visible: true
    Column {
        NavItem { objectName: "nav"; width: 180; number: "01"; label: "nav" }
        AppButton { objectName: "ghost"; variant: "ghost"; text: "ghost" }
        AppButton { objectName: "primary"; variant: "primary"; text: "primary" }
    }
})", QUrl());
    const std::unique_ptr<QObject> root(component.create());
    QString errors;
    for (const auto &error : component.errors())
        errors += error.toString() + QLatin1Char('\n');
    QVERIFY2(root, qPrintable(errors));

    for (const auto &name : {QStringLiteral("nav"), QStringLiteral("ghost")}) {
        const QObject *control = root->findChild<QObject *>(name);
        QVERIFY2(control, qPrintable(name));
        QVERIFY2(!control->property("hovered").toBool(), qPrintable(name));
        const QVariant hoverValue = control->property("hoverColor");
        QVERIFY2(hoverValue.isValid(), qPrintable(name + QStringLiteral(" declares no hoverColor")));
        const QColor hover = hoverValue.value<QColor>();
        QVERIFY2(hover.alpha() > 0, qPrintable(name));
        const auto *background = control->property("background").value<QObject *>();
        QVERIFY2(background, qPrintable(name));
        const QColor rest = background->property("color").value<QColor>();
        const QString detail = QStringLiteral("%1 rests on %2, hover is %3")
                                   .arg(name, rest.name(QColor::HexArgb), hover.name(QColor::HexArgb));
        QVERIFY2(rest.alpha() == 0, qPrintable(detail));
        QVERIFY2(rest.rgb() == hover.rgb(), qPrintable(detail));
    }

    // In eorzea the primary button's base sits under the gold gradient, which hides at
    // once when the style flips to classic; a base resting on transparent black then
    // fades up to the accent through black.
    const QObject *primary = root->findChild<QObject *>(QStringLiteral("primary"));
    QVERIFY(primary);
    const auto *primaryBackground = primary->property("background").value<QObject *>();
    QVERIFY(primaryBackground);
    const QColor primaryRest = primaryBackground->property("color").value<QColor>();
    QVERIFY2(primaryRest.rgba() != qRgba(0, 0, 0, 0),
             qPrintable(QStringLiteral("primary rests on %1").arg(primaryRest.name(QColor::HexArgb))));
}
} // namespace

MR_DESKTOP_TEST_SUITE(ControlMotion)
{
    // The hover test builds an AppController: never against the user's Collector, data
    // folder (TestCollectorGuard.h) or settings. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ControlMotionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ControlMotionTests.moc"
