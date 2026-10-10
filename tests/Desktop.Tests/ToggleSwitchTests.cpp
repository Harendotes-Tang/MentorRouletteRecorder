#include <QDir>
#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QTest>
#include <memory>

struct ToggleScene
{
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;
    bool create(const QString &style = QStringLiteral("classic"))
    {
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        engine.rootContext()->setContextProperty(QStringLiteral("ForceUiStyle"), style);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 360; height: 240; visible: true
    property bool stored: false
    property bool acceptRequests: true
    property int requests: 0
    property bool lastRequested: false
    Column { x: 30; y: 30; spacing: 20
        Button { objectName: "before"; text: "Before" }
        ToggleSwitch { objectName: "toggle"; checked: stored
            onToggled: function(value) {
                requests++; lastRequested = value
                if (acceptRequests) stored = value
            }
        }
        Button { objectName: "after"; text: "After" }
    }
})", QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + "/ToggleProbe.qml"));
        root.reset(component.create());
        errors = component.errorString();
        return root != nullptr;
    }
    QQuickWindow *window() { return qobject_cast<QQuickWindow *>(root.get()); }
    QQuickItem *item(const char *name) { return root->findChild<QQuickItem *>(QString::fromLatin1(name)); }
    bool focusBefore()
    {
        if (!QTest::qWaitForWindowExposed(window())) return false;
        item("before")->forceActiveFocus(Qt::TabFocusReason);
        return QTest::qWaitFor([&] { return item("before")->hasActiveFocus(); });
    }
    bool focusWithinToggle()
    {
        for (QQuickItem *item = window()->activeFocusItem(); item; item = item->parentItem())
            if (item == this->item("toggle")) return true;
        return false;
    }
};
class ToggleSwitchTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + "/Theme.qml"), "MentorRecorder", 1, 0, "Theme");
        qmlRegisterType(QUrl::fromLocalFile(root + "/components/ToggleSwitch.qml"), "MentorRecorder", 1, 0, "ToggleSwitch");
    }
    void tabSpaceAndBacktabOperateExactlyOnce()
    {
        ToggleScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.focusBefore());
        QTest::keyClick(scene.window(), Qt::Key_Tab);
        QVERIFY(scene.focusWithinToggle());
        QTest::keyClick(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("requests").toInt(), 1);
        QCOMPARE(scene.root->property("stored").toBool(), true);
        QTest::keyClick(scene.window(), Qt::Key_Tab);
        QVERIFY(scene.item("after")->hasActiveFocus());
        QTest::keyClick(scene.window(), Qt::Key_Backtab, Qt::ShiftModifier);
        QVERIFY(scene.focusWithinToggle());
        QTest::keyClick(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("requests").toInt(), 2);
        QCOMPARE(scene.root->property("stored").toBool(), false);
    }
    void ownerRejectionAndExternalChangesKeepTheBinding()
    {
        ToggleScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.root->setProperty("acceptRequests", false);
        QVERIFY(scene.focusBefore());
        QTest::keyClick(scene.window(), Qt::Key_Tab);
        QTest::keyClick(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("requests").toInt(), 1);
        QQuickItem *native = scene.item("toggleActivation");
        QVERIFY(native);
        QVERIFY(!native->property("checked").toBool());
        QVERIFY(!scene.item("toggle")->property("checked").toBool());
        scene.root->setProperty("stored", true);
        QVERIFY(native->property("checked").toBool());
        QVERIFY(scene.item("toggle")->property("checked").toBool());
        QTest::keyClick(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("requests").toInt(), 2);
        QVERIFY(!scene.root->property("lastRequested").toBool());
        QVERIFY(scene.item("toggle")->property("checked").toBool());
        scene.root->setProperty("stored", false);
        QVERIFY(!native->property("checked").toBool());
        QVERIFY(!scene.item("toggle")->property("checked").toBool());
    }
    void keyboardFocusAndReturnAndCancelledPress_data()
    {
        QTest::addColumn<QString>("style");
        QTest::newRow("classic") << QStringLiteral("classic");
        QTest::newRow("eorzea") << QStringLiteral("eorzea");
        QTest::newRow("harendotes") << QStringLiteral("harendotes");
    }
    void keyboardFocusAndReturnAndCancelledPress()
    {
        QFETCH(QString, style);
        ToggleScene scene;
        QVERIFY2(scene.create(style), qPrintable(scene.errors));
        QVERIFY(scene.focusBefore());
        QTest::keyClick(scene.window(), Qt::Key_Tab);
        QVERIFY(scene.focusWithinToggle());
        auto *ring = scene.item("toggleFocusRing");
        QVERIFY(ring && ring->isVisible());
        QTest::keyPress(scene.window(), Qt::Key_Space);
        QVERIFY(scene.item("toggle")->property("pressed").toBool());
        scene.item("after")->forceActiveFocus(Qt::TabFocusReason);
        QTest::keyRelease(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("requests").toInt(), 0);
        QVERIFY(!scene.item("toggle")->property("pressed").toBool());
        QVERIFY(!ring->isVisible());
        QTest::keyClick(scene.window(), Qt::Key_Backtab, Qt::ShiftModifier);
        QVERIFY(scene.focusWithinToggle());
        QTest::keyClick(scene.window(), Qt::Key_Return);
        QCOMPARE(scene.root->property("requests").toInt(), 1);
        QVERIFY(scene.item("toggle")->property("checked").toBool());
        QTest::qWait(50);
        const QString evidence = qEnvironmentVariable("MR_DESKTOP_FIX_SCREENSHOTS");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QVERIFY(scene.window()->grabWindow().save(QDir(evidence).filePath(QStringLiteral("toggle-%1.png").arg(style))));
        }
    }
    void disabledSwitchIsSkippedAndDoesNotActivate()
    {
        ToggleScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        scene.item("toggle")->setEnabled(false);
        QVERIFY(scene.focusBefore());
        QTest::keyClick(scene.window(), Qt::Key_Tab);
        QVERIFY(scene.item("after")->hasActiveFocus());
        const auto point = scene.item("toggle")->mapToScene(QPointF(10, 10)).toPoint();
        QTest::mouseClick(scene.window(), Qt::LeftButton, Qt::NoModifier, point);
        QCOMPARE(scene.root->property("requests").toInt(), 0);
    }
    void mouseStillRequestsOneChange()
    {
        ToggleScene scene;
        QVERIFY2(scene.create(), qPrintable(scene.errors));
        QVERIFY(scene.focusBefore());
        const auto point = scene.item("toggle")->mapToScene(QPointF(10, 10)).toPoint();
        QTest::mouseClick(scene.window(), Qt::LeftButton, Qt::NoModifier, point);
        QCOMPARE(scene.root->property("requests").toInt(), 1);
        QVERIFY(scene.item("toggle")->property("checked").toBool());
    }
};
int main(int argc, char **argv)
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ToggleSwitchTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ToggleSwitchTests.moc"
