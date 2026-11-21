#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
#include "LanguageManager.h"
#include "RecordingWarningTestSupport.h"

#include <QApplication>
#include <QJsonArray>
#include <QSignalSpy>
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
        if (fixture == QLatin1String("silent")) {
            backend.capture["silent_reason"] = "NO_PACKETS_ON_ADAPTER";
        } else if (fixture == QLatin1String("blocked")) {
            backend.capture["profile_status"] = "NONE";
        } else {
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
        QCOMPARE(recording.recoveryPending(), !exitGame);
        backend.capture["ffxiv_running"] = true;
        backend.startRecovery();
        recording.refresh();
        QCOMPARE(recording.warningSerial(), serial + 1);
        QCOMPARE(warnings.count(), 2);
        QVERIFY(recording.warningPending());
        QVERIFY(!recording.recoveryPending());
    }

    void firstReadAfterConfirmedUnavailabilityOffersRecoveryOnce()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QVERIFY(recording.warningActive());
        recording.acknowledgeWarning();
        backend.recover();
        backend.capture.remove("ipc_decoded");
        recording.refresh();
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.recoveryPending());
        backend.capture["ipc_decoded"] = 0; // A control segment decoded, but no game IPC.
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("listening"));
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.recoveryPending());

        backend.recover();
        recording.refresh();
        QVERIFY(!recording.warningActive());
        QVERIFY(!recording.warningPending());
        QVERIFY(recording.recoveryPending());
        recording.acknowledgeWarning();
        recording.refresh();
        QVERIFY(!recording.recoveryPending());
    }

    void unconfirmedGameMatchDoesNotReportRecovery_data()
    {
        QTest::addColumn<int>("warningGame");
        QTest::newRow("different-game") << 123;
        QTest::newRow("previous-game-unknown") << 0;
    }

    void unconfirmedGameMatchDoesNotReportRecovery()
    {
        QFETCH(int, warningGame);
        WarningBackend backend;
        backend.startRecovery();
        backend.capture["ffxiv_process_id"] = warningGame;
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QVERIFY(recording.warningActive());
        backend.recover();
        backend.capture["ffxiv_process_id"] = 456;
        recording.refresh();
        QVERIFY(!recording.warningActive());
        QVERIFY(!recording.recoveryPending());
    }

    void recoveryNeedsFreshGameData_data()
    {
        QTest::addColumn<bool>("replacement");
        QTest::addColumn<bool>("legacy");
        QTest::newRow("same-session") << false << false;
        QTest::newRow("new-session") << true << false;
        QTest::newRow("legacy") << false << true;
        QTest::newRow("legacy-new-session") << true << true;
    }

    void recoveryNeedsFreshGameData()
    {
        QFETCH(bool, replacement);
        QFETCH(bool, legacy);
        WarningBackend backend;
        backend.startRecovery();
        const QString counter = legacy ? QStringLiteral("parse_ok_count") : QStringLiteral("ipc_decoded");
        if (legacy)
            backend.capture.remove("ipc_decoded");
        backend.capture[counter] = 12;
        backend.capture["capture_session_id"] = "first";
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        QVERIFY(recording.warningActive());
        backend.recover();
        if (legacy)
            backend.capture.remove("ipc_decoded");
        if (replacement)
            backend.capture["capture_session_id"] = "second";
        const int baseline = replacement ? 0 : 12;
        backend.capture[counter] = baseline;
        recording.refresh();
        QVERIFY(recording.warningActive());
        QVERIFY(!recording.recoveryPending());
        backend.capture[counter] = baseline + 1;
        recording.refresh();
        QVERIFY(recording.recoveryPending());
    }

    void pendingRecoveryIsWithdrawnWhenFormalListeningEnds_data()
    {
        QTest::addColumn<QString>("transition");
        QTest::newRow("disconnected") << QStringLiteral("disconnected");
        QTest::newRow("status-unavailable") << QStringLiteral("status-unavailable");
        QTest::newRow("choosing-game") << QStringLiteral("choosing-game");
        QTest::newRow("calibrating") << QStringLiteral("calibrating");
    }

    void pendingRecoveryIsWithdrawnWhenFormalListeningEnds()
    {
        QFETCH(QString, transition);
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        backend.recover();
        recording.refresh();
        QVERIFY(recording.recoveryPending());
        if (transition == QLatin1String("disconnected")) {
            backend.reconnect(false);
        } else {
            if (transition == QLatin1String("status-unavailable"))
                backend.statusFails = true;
            else if (transition == QLatin1String("choosing-game")) {
                backend.capture["game_selection_required"] = true;
                backend.capture["game_processes"] = QJsonArray{QJsonObject{{"process_id", 456}}};
            } else {
                backend.capture["profile_status"] = "NONE";
                backend.capture["calibration"] = QJsonObject{{"state", "OBSERVING"}};
            }
            recording.refresh();
        }
        QVERIFY(!recording.recoveryPending());
    }

    // Audit 2026-11-19 L3-7: a status event that arrives while the next poll's settings are
    // still being read says nothing about the capture and must not take the notice back.
    void pendingRecoverySurvivesAStatusEventDuringTheNextPoll()
    {
        WarningBackend backend;
        backend.startRecovery();
        mr::AutomaticRecordingController recording(&backend);
        recording.refresh();
        backend.recover();
        recording.refresh();
        QVERIFY(recording.recoveryPending());

        backend.holdSettings = true;
        recording.refresh();
        QVERIFY(backend.heldSettings);
        recording.observeCapture(backend.capture.toVariantMap());
        QVERIFY(recording.recoveryPending());

        backend.holdSettings = false;
        backend.heldSettings->succeed({{"follow_game", true}});
        QVERIFY(recording.recoveryPending());
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
        QVERIFY(!recording.recoveryPending());
        backend.capture["ffxiv_process_id"] = 123;
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

    void recoveryIsRaisedOncePerWarnedEpisode()
    {
        WarningBackend backend;
        mr::AutomaticRecordingController recording(&backend);
        QSignalSpy recoveries(&recording, &mr::AutomaticRecordingController::recoveryRaised);
        // Ordinary listening that was never interrupted has nothing to recover from.
        backend.recover();
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("listening"));
        backend.capture["ipc_decoded"] = 2;
        recording.refresh();
        QCOMPARE(recoveries.count(), 0);

        backend.startRecovery();
        recording.refresh();
        QVERIFY(recording.warningActive());
        QCOMPARE(recoveries.count(), 0);
        backend.recover();
        recording.refresh();
        QVERIFY(recording.recoveryPending());
        QCOMPARE(recoveries.count(), 1);
        // The pending notice is one announcement, however long it stays.
        backend.capture["ipc_decoded"] = 2;
        recording.refresh();
        QCOMPARE(recoveries.count(), 1);
        recording.acknowledgeWarning();
        recording.refresh();
        QCOMPARE(recoveries.count(), 1);
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
