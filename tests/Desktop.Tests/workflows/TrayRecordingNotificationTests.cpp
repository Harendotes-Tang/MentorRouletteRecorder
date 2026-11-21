#include "AppController.h"
#include "AutomaticRecordingController.h"
#include "CollectorProcess.h"
#include "DesktopTestRunner.h"
#include "RecordingWarningTestSupport.h"
#include "TestCollectorGuard.h"
#include "TrayController.h"

#include <QApplication>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QWindow>

#include <memory>

namespace {

class WarningTray : public mr::TrayController
{
public:
    WarningTray(mr::AppController *controller, int &deliveries, QWindow *window = nullptr)
        : mr::TrayController(controller, nullptr, window), m_deliveries(deliveries)
    {
    }

    QString lastMessage;
    int recoveries = 0;

protected:
    void deliverRecordingWarning(const QString &message) override
    {
        ++m_deliveries;
        lastMessage = message;
    }
    void deliverRecordingRecovery() override { ++recoveries; }

private:
    int &m_deliveries;
};

class TrayRecordingNotificationTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void trayRechecksAfterAConsumerSynchronouslyRecovers()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        app.recording()->refresh();
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        QSignalSpy requests(&tray, &mr::TrayController::recordingWarningRequested);
        connect(&tray, &mr::TrayController::recordingWarningRequested, &app, [&] {
            backend.recover();
            app.recording()->refresh();
        });
        QCoreApplication::processEvents();
        QCOMPARE(requests.count(), 1);
        QVERIFY(!app.recording()->warningActive());
        QCOMPARE(deliveries, 0);
    }

    void trayConsumerCanDestroyTrayBeforeNativeDelivery()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        app.recording()->refresh();
        int deliveries = 0;
        auto tray = std::make_unique<WarningTray>(&app, deliveries);
        QSignalSpy requests(tray.get(), &mr::TrayController::recordingWarningRequested);
        connect(tray.get(), &mr::TrayController::recordingWarningRequested, &app, [&] { tray.reset(); });
        QCoreApplication::processEvents();
        QCOMPARE(requests.count(), 1);
        QVERIFY(!tray);
        QCOMPARE(deliveries, 0);
    }

    void trayConsumerCanDestroyControllerBeforeNativeDelivery()
    {
        WarningBackend backend;
        backend.startRecovery();
        auto app = std::make_unique<mr::AppController>(&backend, nullptr);
        app->recording()->refresh();
        int deliveries = 0;
        WarningTray tray(app.get(), deliveries);
        QSignalSpy requests(&tray, &mr::TrayController::recordingWarningRequested);
        connect(&tray, &mr::TrayController::recordingWarningRequested, &backend, [&] { app.reset(); });
        QCoreApplication::processEvents();
        QCOMPARE(requests.count(), 1);
        QVERIFY(!app);
        QCOMPARE(deliveries, 0);
    }

    void trayStartupReplaySurvivesControllerDeletedByADirectWarning()
    {
        WarningBackend backend;
        auto app = std::make_unique<mr::AppController>(&backend, nullptr);
        app->recording()->refresh();
        QVERIFY(!app->recording()->warningActive());
        int deliveries = 0;
        WarningTray tray(app.get(), deliveries);
        QSignalSpy requests(&tray, &mr::TrayController::recordingWarningRequested);
        connect(&tray, &mr::TrayController::recordingWarningRequested, &backend, [&] { app.reset(); });
        // The direct warning is consumed before the constructor's 0ms replay.
        // Its observer destroys the owner; that later replay must see a null
        // controller rather than dereference the old raw address.
        backend.startRecovery();
        app->recording()->refresh();
        QVERIFY(!app);
        QCOMPARE(requests.count(), 1);
        QCoreApplication::processEvents();
        QCOMPARE(requests.count(), 1);
        QCOMPARE(deliveries, 0);
    }

    void trayDiscardsSupersededSerialBeforeNativeDelivery()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        app.recording()->refresh();
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        QSignalSpy requests(&tray, &mr::TrayController::recordingWarningRequested);
        bool replaced = false;
        connect(&tray, &mr::TrayController::recordingWarningRequested, &app, [&] {
            if (replaced)
                return;
            replaced = true;
            backend.capture["ffxiv_process_id"] = 456;
            app.recording()->refresh();
        });
        QCoreApplication::processEvents();
        QCOMPARE(requests.count(), 2);
        QVERIFY(replaced);
        QCOMPARE(deliveries, 1);
    }

    void trayLateSubscriptionConsumesOnceEvenAfterUiAcknowledgement()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        app.recording()->refresh();
        QVERIFY(app.recording()->warningActive());
        const auto serial = app.recording()->warningSerial();
        app.recording()->acknowledgeWarning();
        mr::TrayController tray(&app, nullptr, nullptr);
        QSignalSpy notifications(&tray, &mr::TrayController::recordingWarningRequested);
        QTRY_COMPARE(notifications.count(), 1);
        QCOMPARE(notifications.first().first().toULongLong(), serial);
        app.recording()->refresh();
        backend.capture["path_recovery"] = QJsonObject{{"state", "FAILED"}};
        app.recording()->refresh();
        QCoreApplication::processEvents();
        QCOMPARE(notifications.count(), 1);
        backend.recover();
        app.recording()->refresh();
        backend.startRecovery();
        app.recording()->refresh();
        QTRY_COMPARE(notifications.count(), 2);
        QCOMPARE(notifications.last().first().toULongLong(), serial + 1);
    }

    // Started in the tray at login, a background service that cannot start is
    // said once in the notification area: the hidden window's toast and title
    // bar reach nobody (review I1-2).
    void hiddenWindowIsToldOnceThatAMissingServiceCannotRecord()
    {
        WarningBackend backend;
        backend.connected = false;
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        mr::CollectorProcess collector(directory.filePath(QStringLiteral("absent/MentorRecorder.Collector.exe")), nullptr);
        QCOMPARE(collector.state(), mr::CollectorProcess::State::Missing);
        mr::AppController app(&backend, nullptr, nullptr, &collector);
        QWindow window; // never shown, as under --autostart
        int deliveries = 0;
        WarningTray tray(&app, deliveries, &window);
        QTRY_COMPARE(deliveries, 1);
        // The headline holds for every cause; the reason is the supervisor's own sentence.
        QCOMPARE(tray.lastMessage, QString::fromUtf8("后台服务不可用，暂时不能自动记录。") + collector.statusText());
        QTest::qWait(100);
        QCOMPARE(deliveries, 1);
    }

    void unreachableServiceIsReportedOncePerEpisodeAndOnlyToAHiddenWindow()
    {
        WarningBackend backend;
        backend.connected = false;
        mr::AppController app(&backend, nullptr);
        app.setServiceNoticeDelayForTest(30);
        QWindow window;
        int deliveries = 0;
        WarningTray tray(&app, deliveries, &window);
        QTRY_COMPARE(deliveries, 1);
        // A fixed sentence: no exit code, no retry countdown that is stale once
        // shown, and nothing claiming the service never started (review RV3-3).
        QCOMPARE(tray.lastMessage, QString::fromUtf8("后台服务不可用，暂时不能自动记录。"
                                                     "已经一分钟没有连上后台服务，软件会继续自动重试。"));
        QTest::qWait(120);
        QCOMPARE(deliveries, 1);
        backend.reconnect(true);
        QVERIFY(app.serviceUnavailableMessage().isEmpty());
        // A window on screen says it itself, in its toast and title bar.
        window.show();
        backend.reconnect(false);
        QTRY_VERIFY(!app.serviceUnavailableMessage().isEmpty());
        QTest::qWait(60);
        QCOMPARE(deliveries, 1);
        // A new episode while the window is hidden is told again.
        window.hide();
        backend.reconnect(true);
        backend.reconnect(false);
        QTRY_COMPARE(deliveries, 2);
    }

    void trayAnnouncesRecoveryOnceAfterAWarning()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        QSignalSpy requests(&tray, &mr::TrayController::recordingRecoveryRequested);
        app.recording()->refresh();
        QTRY_COMPARE(deliveries, 1);
        QCOMPARE(tray.recoveries, 0);
        // 「我知道了」 on the warning does not end its episode; the recovery is still said.
        app.recording()->acknowledgeWarning();
        backend.recover();
        app.recording()->refresh();
        QCOMPARE(requests.count(), 1);
        QCOMPARE(tray.recoveries, 1);
        app.recording()->refresh();
        QCoreApplication::processEvents();
        QCOMPARE(tray.recoveries, 1);
        QCOMPARE(deliveries, 1);
    }

    void trayStaysQuietWithoutARecovery_data()
    {
        QTest::addColumn<QString>("ending");
        QTest::newRow("never-interrupted") << QStringLiteral("never-interrupted");
        QTest::newRow("game-exited") << QStringLiteral("game-exited");
        QTest::newRow("another-game") << QStringLiteral("another-game");
        QTest::newRow("maintenance") << QStringLiteral("maintenance");
    }

    void trayStaysQuietWithoutARecovery()
    {
        QFETCH(QString, ending);
        WarningBackend backend;
        if (ending != QLatin1String("never-interrupted"))
            backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        app.recording()->refresh();
        QCoreApplication::processEvents();
        if (ending == QLatin1String("game-exited")) {
            backend.capture["ffxiv_running"] = false;
            app.recording()->refresh();
            backend.capture["ffxiv_running"] = true;
        } else if (ending == QLatin1String("another-game")) {
            backend.capture["ffxiv_process_id"] = 456;
        } else if (ending == QLatin1String("maintenance")) {
            // The fault ends while the maintainer tools own the capture: the
            // warning was withdrawn on entry, so there is nothing to answer.
            app.recording()->setMaintenance(true);
            backend.recover();
            app.recording()->setMaintenance(false);
        }
        backend.recover();
        app.recording()->refresh();
        backend.capture["ipc_decoded"] = 2;
        app.recording()->refresh();
        QCoreApplication::processEvents();
        QCOMPARE(app.recording()->state(), QStringLiteral("listening"));
        QCOMPARE(tray.recoveries, 0);
    }

    void trayConsumesOnlyTheActiveRecoverySerial_data()
    {
        QTest::addColumn<bool>("supersedes");
        QTest::newRow("duplicate-signal") << false;
        QTest::newRow("new-episode") << true;
    }

    void trayConsumesOnlyTheActiveRecoverySerial()
    {
        QFETCH(bool, supersedes);
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        auto *recording = app.recording();
        recording->refresh();
        QVERIFY(recording->warningActive());
        const auto firstSerial = recording->warningSerial();

        QSignalSpy requests(&tray, &mr::TrayController::recordingRecoveryRequested);
        bool reentered = false;
        connect(&tray, &mr::TrayController::recordingRecoveryRequested, &app, [&] {
            if (reentered)
                return;
            reentered = true;
            if (supersedes) {
                backend.startRecovery();
                recording->observeCapture(backend.capture.toVariantMap());
                backend.recover();
                recording->observeCapture(backend.capture.toVariantMap());
            } else {
                Q_EMIT recording->recoveryRaised();
            }
        });
        backend.recover();
        recording->refresh();

        QVERIFY(reentered);
        QVERIFY(recording->recoveryPending());
        QCOMPARE(recording->warningSerial(), firstSerial + (supersedes ? 1 : 0));
        QCOMPARE(requests.count(), supersedes ? 2 : 1);
        QCOMPARE(tray.recoveries, 1);
    }

    void trayDropsARecoveryItsObserverDismissed()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        int deliveries = 0;
        WarningTray tray(&app, deliveries);
        app.recording()->refresh();
        connect(&tray, &mr::TrayController::recordingRecoveryRequested, &app,
                [&] { app.recording()->acknowledgeWarning(); });
        backend.recover();
        app.recording()->refresh();
        QCOMPARE(tray.recoveries, 0);
    }

    void trayDoesNotDeliverWarningThatRecoveredBeforeReplay()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AppController app(&backend, nullptr);
        app.recording()->refresh();
        mr::TrayController tray(&app, nullptr, nullptr);
        QSignalSpy notifications(&tray, &mr::TrayController::recordingWarningRequested);
        backend.recover();
        app.recording()->refresh();
        QCoreApplication::processEvents();
        QCOMPARE(notifications.count(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(TrayRecordingNotification)
{
    mrtest::disableCollectorLaunch();
    QApplication app(argc, argv);
    TrayRecordingNotificationTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "TrayRecordingNotificationTests.moc"
