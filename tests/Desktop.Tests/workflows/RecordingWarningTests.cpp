#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AutomaticRecordingController.h"
#include "LanguageManager.h"
#include "TrayController.h"

#include <QApplication>
#include <QJsonArray>
#include <QSignalSpy>
#include <QScopeGuard>
#include <QTest>
#include <QTranslator>

#include <memory>

namespace {

class WarningTranslator : public QTranslator
{
public:
    QString translate(const char *context, const char *sourceText, const char *, int) const override
    {
        if (qstrcmp(context, "mr::AutomaticRecordingController") != 0 || !sourceText)
            return {};
        return QStringLiteral("[translated] ") + QString::fromUtf8(sourceText);
    }
    bool isEmpty() const override { return false; }
};

class WarningBackend : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }
    bool connected = true;
    bool statusFails = false;
    QJsonObject capture{{"ffxiv_running", true}, {"ffxiv_process_id", 123},
                        {"profile_status", "VERIFIED"}, {"state", "RUNNING"},
                        {"silent_reason", "NONE"}};

    void reconnect(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++requests), type, this);
        if (type == QLatin1String("GetCaptureSettings"))
            reply->succeed({{"follow_game", true}});
        else if (type == QLatin1String("GetCaptureValidationStatus"))
            reply->succeed({{"active", false}, {"state", "IDLE"}});
        else if (type == QLatin1String("GetStatus")) {
            if (statusFails)
                reply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("unavailable"));
            else
                reply->succeed({{"capture", capture}, {"game", QJsonObject{{"install_path_readable", true}}}});
        } else
            reply->succeed({});
        return reply;
    }

    void recover()
    {
        capture["state"] = "RUNNING";
        capture["silent_reason"] = "NONE";
        capture["last_error_code"] = "NONE";
        capture.remove("calibration");
        capture.remove("path_recovery");
        capture["profile_status"] = "VERIFIED";
    }

    void startRecovery()
    {
        capture["silent_reason"] = "MIDSTREAM";
        capture["path_recovery"] = QJsonObject{{"state", "RECOVERING"}, {"attempts", 0}, {"retry_after_ms", 0}};
    }

private:
    int requests = 0;
};

class WarningTray : public mr::TrayController
{
public:
    WarningTray(mr::AppController *controller, int &deliveries)
        : mr::TrayController(controller, nullptr, nullptr), m_deliveries(deliveries)
    {
    }

protected:
    void deliverRecordingWarning(const QString &) override { ++m_deliveries; }

private:
    int &m_deliveries;
};

class RecordingWarningTests : public QObject
{
    Q_OBJECT
private slots:
    void recoveryWarnsOnFirstConfirmedSnapshot()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        QSignalSpy incidents(&recording, &mr::AutomaticRecordingController::incidentRaised);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("recovering"));
        QVERIFY(recording.warningActive());
        QVERIFY(recording.warningPending());
        QCOMPARE(recording.warningSerial(), quint64(1));
        QCOMPARE(warnings.count(), 1);
        QCOMPARE(incidents.count(), 0);
        QVERIFY(!recording.blocked());
        QVERIFY(!recording.pendingAlert());
        QVERIFY(recording.warningMessage().contains(QStringLiteral("恢复")));
        QVERIFY(recording.warningMessage().contains(QStringLiteral("记录")));
        QVERIFY(!recording.warningMessage().contains(QStringLiteral("无需操作")));
    }

    void confirmedStatesWarn_data()
    {
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("state");
        QTest::newRow("silent") << QStringLiteral("silent") << QStringLiteral("listening_silent");
        QTest::newRow("blocked") << QStringLiteral("blocked") << QStringLiteral("blocked");
        QTest::newRow("calibration") << QStringLiteral("calibration") << QStringLiteral("calibration_blocked");
    }

    void confirmedStatesWarn()
    {
        QFETCH(QString, fixture);
        QFETCH(QString, state);
        WarningBackend backend;
        if (fixture == QLatin1String("silent"))
            backend.capture["silent_reason"] = "NO_PACKETS_ON_ADAPTER";
        else if (fixture == QLatin1String("blocked"))
            backend.capture["profile_status"] = "NONE";
        else {
            backend.capture["profile_status"] = "NONE";
            backend.capture["calibration"] = QJsonObject{{"state", "BLOCKED"},
                {"blockers", QJsonArray{QStringLiteral("需要重新核对")}}};
        }
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        QCOMPARE(recording.state(), state);
        QVERIFY(recording.warningActive());
        QVERIFY(recording.warningPending());
        QCOMPARE(warnings.count(), 1);
        QVERIFY(!recording.warningMessage().isEmpty());
    }

    void acknowledgementAndFailureKeepOneEpisode()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        const auto serial = recording.warningSerial();
        recording.acknowledgeWarning();
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.warningPending());
        for (int poll = 0; poll < 3; ++poll)
            recording.refresh();
        backend.capture["path_recovery"] = QJsonObject{{"state", "FAILED"}};
        recording.refresh();
        QVERIFY(recording.blocked());
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.warningPending());
        QCOMPARE(recording.warningSerial(), serial);
        QCOMPARE(warnings.count(), 1);
        recording.acknowledgeWarning();
        QVERIFY(!recording.pendingAlert());
    }

    void readyCalibrationWithUnreadableFactsWarns_data()
    {
        QTest::addColumn<bool>("recovering");
        QTest::newRow("recovering") << true;
        QTest::newRow("silent") << false;
    }

    void readyCalibrationWithUnreadableFactsWarns()
    {
        QFETCH(bool, recovering);
        WarningBackend backend;
        backend.capture["profile_status"] = "NONE";
        backend.capture["calibration"] = QJsonObject{{"state", "READY"}, {"events", QJsonArray{}}};
        if (recovering)
            backend.startRecovery();
        else
            backend.capture["silent_reason"] = "NO_PACKETS_ON_ADAPTER";
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("calibration_ready"));
        QVERIFY(recording.warningActive());
        QVERIFY(recording.warningPending());
        QCOMPARE(warnings.count(), 1);
        const auto serial = recording.warningSerial();
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial);
        QCOMPARE(warnings.count(), 1);
    }

    void calibrationProgressDoesNotRearmAnAcknowledgedEpisode()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        recording.acknowledgeWarning();
        const auto serial = recording.warningSerial();
        backend.recover();
        backend.capture["profile_status"] = "NONE";
        backend.capture["calibration"] = QJsonObject{{"state", "READY"}, {"events", QJsonArray{}}};
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("calibration_ready"));
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.warningPending());
        QCOMPARE(recording.warningSerial(), serial);
        backend.capture["calibration"] = QJsonObject{{"state", "OBSERVING"}};
        backend.startRecovery();
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("recovering"));
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.warningPending());
        QCOMPARE(recording.warningSerial(), serial);
        QCOMPARE(warnings.count(), 1);
    }

    void healthyOrExitedGameAllowsAnotherWarning_data()
    {
        QTest::addColumn<bool>("exitGame");
        QTest::newRow("healthy") << false;
        QTest::newRow("exit") << true;
    }

    void healthyOrExitedGameAllowsAnotherWarning()
    {
        QFETCH(bool, exitGame);
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        const auto serial = recording.warningSerial();
        if (exitGame)
            backend.capture["ffxiv_running"] = false;
        else
            backend.recover();
        recording.refresh();
        QVERIFY(!recording.warningActive());
        QVERIFY(!recording.warningPending());
        QVERIFY(recording.warningMessage().isEmpty());
        backend.capture["ffxiv_running"] = true;
        backend.startRecovery();
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial + 1);
        QCOMPARE(warnings.count(), 2);
        QVERIFY(recording.warningPending());
    }

    void anotherGameHasItsOwnEpisode()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        recording.acknowledgeWarning();
        const auto serial = recording.warningSerial();
        backend.capture["ffxiv_process_id"] = 456;
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial + 1);
        QCOMPARE(warnings.count(), 2);
        QVERIFY(recording.warningPending());
    }

    void languageChangeRefreshesTextWithoutReplayingAcknowledgedWarning()
    {
        mr::LanguageManager language(nullptr);
        language.initialize(QStringLiteral("zh-CN"));
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        recording.acknowledgeWarning();
        const auto serial = recording.warningSerial();
        const auto original = recording.warningMessage();
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        QSignalSpy changes(&recording, &mr::AutomaticRecordingController::changed);
        WarningTranslator translator;
        QCoreApplication::installTranslator(&translator);
        const auto removeTranslator = qScopeGuard([&] { QCoreApplication::removeTranslator(&translator); });
        QVERIFY(QMetaObject::invokeMethod(&language, "languageChanged", Qt::DirectConnection));
        QVERIFY(changes.count() > 0);
        QCOMPARE(recording.warningMessage(), QStringLiteral("[translated] ") + original);
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial);
        QCOMPARE(warnings.count(), 0);
        QVERIFY(!recording.warningPending());
    }

    void missingOrDisconnectedFactsDoNotClearWarning()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        recording.acknowledgeWarning();
        const auto serial = recording.warningSerial();
        const auto warning = recording.warningMessage();
        backend.capture.remove("ffxiv_process_id");
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial);
        backend.capture["ffxiv_process_id"] = 0;
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial);
        backend.statusFails = true;
        recording.refresh();
        recording.observeCapture({});
        backend.reconnect(false);
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.warningPending());
        QCOMPARE(recording.warningSerial(), serial);
        QCOMPARE(recording.warningMessage(), warning);
        backend.reconnect(true);
        QVERIFY(recording.warningActive());
        backend.statusFails = false;
        backend.recover();
        recording.refresh();
        QVERIFY(!recording.warningActive());
    }

    void maintenanceAndIntentionalObservationWithdrawWarning_data()
    {
        QTest::addColumn<bool>("maintenance");
        QTest::newRow("maintenance") << true;
        QTest::newRow("observation") << false;
    }

    void maintenanceAndIntentionalObservationWithdrawWarning()
    {
        QFETCH(bool, maintenance);
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        if (maintenance)
            recording.setMaintenance(true);
        else
            recording.setUserObservation(true);
        QVERIFY(!recording.warningActive());
        QVERIFY(!recording.warningPending());
        recording.observeCapture(backend.capture.toVariantMap());
        QCOMPARE(warnings.count(), 0);
        if (maintenance)
            recording.setMaintenance(false);
        else
            recording.setUserObservation(false);
        QVERIFY(recording.warningActive());
        QCOMPARE(warnings.count(), 1);
    }

    void waitingDoesNotWarn()
    {
        WarningBackend backend;
        backend.capture["ffxiv_running"] = false;
        backend.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("waiting"));
        QVERIFY(!recording.warningActive());
        QCOMPARE(recording.warningSerial(), quint64(0));
        QCOMPARE(warnings.count(), 0);
    }

    void reentrantRecoveryDoesNotRaiseAStaleWarning()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy warnings(&recording, &mr::AutomaticRecordingController::warningRaised);
        bool recovered = false;
        connect(&recording, &mr::AutomaticRecordingController::changed, &recording, [&] {
            if (recovered || !recording.warningActive())
                return;
            recovered = true;
            backend.recover();
            recording.observeCapture(backend.capture.toVariantMap());
        });
        recording.refresh();
        QVERIFY(recovered);
        QVERIFY(!recording.warningActive());
        QCOMPARE(warnings.count(), 0);
    }

    void warningObserversCanDestroyTheController_data()
    {
        QTest::addColumn<bool>("destroyOnChanged");
        QTest::newRow("changed") << true;
        QTest::newRow("warning") << false;
    }

    void warningObserversCanDestroyTheController()
    {
        QFETCH(bool, destroyOnChanged);
        WarningBackend backend;
        backend.capture["profile_status"] = "NONE";
        auto recording = std::make_unique<mr::AutomaticRecordingController>(&backend);
        if (destroyOnChanged) {
            connect(recording.get(), &mr::AutomaticRecordingController::changed, &backend, [&] {
                if (recording && recording->warningActive())
                    recording.reset();
            });
        } else {
            connect(recording.get(), &mr::AutomaticRecordingController::warningRaised,
                    &backend, [&] { recording.reset(); });
        }
        recording->refresh();
        QVERIFY(!recording);
    }

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

MR_DESKTOP_TEST_SUITE(RecordingWarning)
{
    mrtest::disableCollectorLaunch();
    QApplication app(argc, argv);
    RecordingWarningTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "RecordingWarningTests.moc"
