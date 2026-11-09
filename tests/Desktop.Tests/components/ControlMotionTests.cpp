#include "DesktopTestRunner.h"
#include "QmlComponentTestSupport.h"
#include "QmlRuntime.h"
#include <QGuiApplication>
#include <QQuickItem>
#include <QQuickStyle>
#include <QSignalSpy>
#include <QTest>

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
} // namespace

MR_DESKTOP_TEST_SUITE(ControlMotion)
{
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ControlMotionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ControlMotionTests.moc"
