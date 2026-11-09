// Real Qt popup transitions: reversal, reduced motion, keyboard and modal cleanup.
#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"

#include <QDir>
#include <QGuiApplication>
#include <QFontDatabase>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QSignalSpy>
#include <QStandardPaths>
#include <memory>

namespace {
struct DialogScene {
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create(bool reduced, bool overlayParent = false, const QByteArray &custom = {})
    {
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), reduced);
        QQmlComponent component(&engine);
        QByteArray source = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    id: shell
    property bool reenterOnScale: false
    function finishMotion() { PopupMotion.finish(popup) }
    width: 480; height: 320; visible: true
    property alias popup: popup
    property alias button: trigger
    property int activations: 0
    Button {
        id: trigger
        x: 10; y: 10; width: 100; height: 40
        text: "Open"
        onClicked: { activations++; popup.open() }
    }
    MotionDialog {
        id: popup
        onScaleChanged: {
            if (shell.reenterOnScale && scale === 1) {
                shell.reenterOnScale = false
                close(); open()
            }
        }
        x: 120; y: 80; width: 240; height: 140
        modal: true; focus: true
        closePolicy: Popup.CloseOnEscape
        TextField { width: 180; text: "preserved" }
    }
})";
        if (overlayParent)
            source.replace("id: popup", "id: popup; parent: Overlay.overlay");
        component.setData(custom.isEmpty() ? source : custom, QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        return root != nullptr;
    }
    QObject *popup() const { return root->property("popup").value<QObject *>(); }
    QQuickWindow *window() const { return qobject_cast<QQuickWindow *>(root.get()); }
    bool call(const char *method) const { return QMetaObject::invokeMethod(popup(), method); }
    bool moving() const { return popup()->property("motionRunning").toBool(); }
    void reduce() const { engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true); }
};

class DialogMotionTests : public QObject {
    Q_OBJECT
private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const QString &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")}) {
            const auto files = QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files);
            for (const auto &file : files) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    void entranceAndExitReachTheirTargets()
    {
        DialogScene scene;
        QVERIFY2(scene.create(false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(scene.call("open"));
        QVERIFY(scene.moving());
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
        QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
        QVERIFY(scene.call("close"));
        QVERIFY(scene.moving());
        QTRY_VERIFY(!scene.popup()->property("visible").toBool());
        QVERIFY(!scene.moving());
    }

    void reverseEntranceAndReopenPreservesLifecycle()
    {
        DialogScene scene;
        QVERIFY2(scene.create(false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QSignalSpy closed(scene.popup(), SIGNAL(closed()));
        QVERIFY(scene.call("open"));
        QTRY_VERIFY_WITH_TIMEOUT(scene.popup()->property("opacity").toDouble() > 0.0, 100);
        QVERIFY(scene.moving());
        QVERIFY(scene.call("close"));
        QVERIFY(scene.call("open"));
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
        QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
        // Reversal does not falsely report a completed close.
        QCOMPARE(closed.count(), 0);
        QVERIFY(scene.call("close"));
        QTRY_COMPARE(closed.count(), 1);
        QVERIFY(!scene.popup()->property("visible").toBool());
    }

    void reduceDuringTransition_data()
    {
        QTest::addColumn<bool>("closing");
        QTest::newRow("enter") << false;
        QTest::newRow("exit") << true;
    }
    void reduceDuringTransition()
    {
        QFETCH(bool, closing);
        DialogScene scene;
        QVERIFY2(scene.create(false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(scene.call("open"));
        if (closing) {
            QTRY_VERIFY(scene.popup()->property("opened").toBool());
            QVERIFY(scene.call("close"));
        }
        QVERIFY(scene.moving());
        scene.reduce();
        // No wait for the original 220/120ms deadline is permitted.
        QTRY_VERIFY_WITH_TIMEOUT(!scene.moving(), 50);
        QCOMPARE(scene.popup()->property("visible").toBool(), !closing);
        if (!closing) {
            QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
            QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
        }
    }

    void completingOldEntranceDoesNotFinalizeAReentrantNewEntrance()
    {
        DialogScene scene;
        QVERIFY2(scene.create(false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(scene.call("open"));
        QVERIFY(scene.moving());
        scene.root->setProperty("reenterOnScale", true);
        QVERIFY(QMetaObject::invokeMethod(scene.root.get(), "finishMotion"));
        QVERIFY(!scene.root->property("reenterOnScale").toBool());
        QVERIFY(scene.moving());
        QVERIFY(!scene.popup()->property("opened").toBool());
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
        QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
    }

    void evidenceControllerCanDismissAndReplaceAnAnimatingPreview()
    {
        const QByteArray source = R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 720; visible: true
    property alias popup: popup
    property alias evidence: evidence
    QtObject {
        id: evidence
        property bool visible: false
        property string kind: "TEMPLATE_OBSERVATION"
        property var candidateChoices: []
        property string selectedCode: ""
        property bool busy: false
        property string reason: ""
        property string evidenceJson: "{}"
        property bool canSubmit: false
        property bool evidenceInUrl: false
        signal changed()
        function prepare() { visible = true; changed() }
        function dismiss() { visible = false; changed() }
    }
    CalibrationEvidenceDialog { id: popup; evidenceController: evidence }
})";
        DialogScene scene;
        QVERIFY2(scene.create(false, false, source), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        auto *evidence = scene.root->property("evidence").value<QObject *>();
        QVERIFY(evidence);
        QVERIFY(QMetaObject::invokeMethod(evidence, "prepare"));
        QVERIFY(scene.moving());
        QVERIFY(QMetaObject::invokeMethod(evidence, "dismiss"));
        QTRY_VERIFY(!scene.popup()->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(evidence, "prepare"));
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QVERIFY(scene.call("close"));
        QVERIFY(!evidence->property("visible").toBool());
        QVERIFY(QMetaObject::invokeMethod(evidence, "prepare"));
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QVERIFY(evidence->property("visible").toBool());
        QTest::qWait(250);
        QVERIFY(evidence->property("visible").toBool());
        QVERIFY(scene.popup()->property("opened").toBool());
    }

    void closedAnimatedDialogReopensWithReducedMotion()
    {
        DialogScene scene;
        QVERIFY2(scene.create(false, true), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QSignalSpy closed(scene.popup(), SIGNAL(closed()));
        QVERIFY(scene.call("open"));
        QVERIFY(scene.moving());
        QTRY_VERIFY(scene.popup()->property("opened").toBool());
        QVERIFY(scene.call("close"));
        QTRY_VERIFY(!scene.popup()->property("visible").toBool());
        QCOMPARE(closed.count(), 1);
        scene.reduce();
        QVERIFY(scene.call("open"));
        QTRY_VERIFY_WITH_TIMEOUT(scene.popup()->property("opened").toBool(), 50);
        QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
        QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
        QVERIFY(!scene.moving());
    }

    void hidingWindowSettlesThePopup()
    {
        DialogScene scene;
        QVERIFY2(scene.create(false), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(scene.call("open"));
        QVERIFY(scene.moving());
        scene.window()->hide();
        QTRY_VERIFY_WITH_TIMEOUT(!scene.moving(), 50);
        QCOMPARE(scene.popup()->property("opacity").toDouble(), 1.0);
        QCOMPARE(scene.popup()->property("scale").toDouble(), 1.0);
        scene.window()->show();
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        QVERIFY(!scene.moving());
    }

    void reducedStartupAndKeyboardLeaveNoModalBlocker()
    {
        DialogScene scene;
        QVERIFY2(scene.create(true), qPrintable(scene.errors));
        QVERIFY(QTest::qWaitForWindowExposed(scene.window()));
        scene.window()->requestActivate();
        auto *button = scene.root->property("button").value<QObject *>();
        QVERIFY(button);
        QVERIFY(QMetaObject::invokeMethod(button, "forceActiveFocus"));
        QTest::keyClick(scene.window(), Qt::Key_Space);
        QCOMPARE(scene.root->property("activations").toInt(), 1);
        QTRY_VERIFY_WITH_TIMEOUT(scene.popup()->property("opened").toBool(), 50);
        QVERIFY(!scene.moving());
        QTest::keyClick(scene.window(), Qt::Key_Escape);
        QTRY_VERIFY_WITH_TIMEOUT(!scene.popup()->property("visible").toBool(), 50);
        QVERIFY(!scene.moving());
        QTest::mouseClick(scene.window(), Qt::LeftButton, Qt::NoModifier, QPoint(50, 30));
        QCOMPARE(scene.root->property("activations").toInt(), 2);
        QTRY_VERIFY_WITH_TIMEOUT(scene.popup()->property("opened").toBool(), 50);
    }
};
}

MR_DESKTOP_TEST_SUITE(DialogMotion)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
#ifdef Q_OS_WIN
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QStringLiteral("Fonts/msyh.ttc")));
#endif
    DialogMotionTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "DialogMotionTests.moc"
