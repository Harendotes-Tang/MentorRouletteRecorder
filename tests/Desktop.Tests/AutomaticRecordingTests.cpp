#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
#include "AppController.h"
#include <QSignalSpy>
#include <QJsonArray>
#include <QGuiApplication>
#include <QTest>

// Synchronous replies exercise BackendReply::whenDone's already-completed path;
// held status replies reproduce a previous connection finishing after reconnect.
class RecordingBackend : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }
    bool connected = true, failSettings = false, failWrite = false, failStop = false;
    bool holdStatus = false, holdStop = false, drainStop = false;
    mr::BackendReply *held = nullptr;
    QJsonObject settings{{"follow_game", true}, {"candidate_validation_enabled", true},
                         {"research_payload_opcodes", QJsonArray{17, 29}}};
    QJsonObject validation{{"active", false}, {"state", "IDLE"}};
    QJsonObject game{{"install_path_readable", true}};
    QJsonObject capture{{"ffxiv_running", true}, {"ffxiv_process_id", 123},
                        {"profile_status", "VERIFIED"}, {"state", "RUNNING"},
                        {"candidate_validation_enabled", true}};
    QList<QJsonObject> writes;
    QStringList calls;
    void reconnect() { connected = false; emit connectionChanged(); connected = true; emit connectionChanged(); }
    mr::BackendReply *request(const QString &type, const QJsonObject &p = {}) override {
        calls.append(type);
        auto *r = new mr::BackendReply(QString::number(calls.size()), type, this);
        if (type == QLatin1String("GetCaptureSettings")) {
            if (failSettings) r->fail("ERR_TEST", "settings unavailable"); else r->succeed(settings);
        } else if (type == QLatin1String("UpdateCaptureSettings")) {
            writes.append(p);
            if (failWrite) r->fail("ERR_TEST", "write refused");
            else { for (auto i = p.begin(); i != p.end(); ++i) settings.insert(i.key(), i.value()); r->succeed(settings); }
        } else if (type == QLatin1String("GetCaptureValidationStatus")) r->succeed(validation);
        else if (type == QLatin1String("StopCaptureValidation")) {
            if (holdStop) { held = r; holdStop = false; return r; }
            if (drainStop) { validation["state"] = "STOPPING"; r->succeed(validation); return r; }
            if (failStop) r->fail("ERR_TEST", "stop refused");
            else { validation.insert("active", false); r->succeed(validation); }
        } else if (type == QLatin1String("GetStatus")) {
            if (holdStatus) { held = r; holdStatus = false; } else r->succeed({{"capture", capture}, {"game", game}});
        } else r->succeed({});
        return r;
    }
};

class AutomaticRecordingTests : public QObject
{
    Q_OBJECT
private slots:
    void noGameHasNoPopup() {
        RecordingBackend b; b.capture["ffxiv_running"] = false; b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b); QSignalSpy alerts(&c, &mr::AutomaticRecordingController::incidentRaised);
        c.refresh(); QCOMPARE(c.state(), "waiting"); QCOMPARE(alerts.count(), 0); QVERIFY(!c.pendingAlert());
    }
    void incidentAcknowledgementRecoveryAndNewSession() {
        RecordingBackend b; b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b); QSignalSpy alerts(&c, &mr::AutomaticRecordingController::incidentRaised);
        c.refresh(); QVERIFY(c.blocked()); QVERIFY(c.pendingAlert()); QCOMPARE(alerts.count(), 1);
        c.acknowledge(); c.refresh(); QVERIFY(c.blocked()); QVERIFY(!c.pendingAlert()); QCOMPARE(alerts.count(), 1);
        b.capture["profile_status"] = "VERIFIED"; c.refresh(); QCOMPARE(c.state(), "listening"); QVERIFY(!c.pendingAlert());
        b.capture["profile_status"] = "NONE"; c.refresh(); QCOMPARE(alerts.count(), 2);
        b.capture["ffxiv_process_id"] = 456; c.refresh(); QCOMPARE(alerts.count(), 3);
    }
    void unreadableInstallPathIsNamedInsteadOfAProfileMismatch() {
        RecordingBackend b; b.capture["profile_status"] = "NONE"; b.game["install_path_readable"] = false;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.blocked()); QVERIFY(c.message().contains(QStringLiteral("无法读取游戏安装路径")));
        b.game["install_path_readable"] = true; c.refresh();
        QVERIFY(c.blocked()); QVERIFY(c.message().contains(QStringLiteral("协议档案不匹配")));
    }
    void deferredFaultClearsOnlyOnConfirmedRecovery() {
        RecordingBackend b; b.capture["profile_status"] = "NONE"; mr::AutomaticRecordingController c(&b);
        c.refresh(); QVERIFY(c.pendingAlert());
        b.capture.remove("ffxiv_running"); c.refresh(); QVERIFY(c.blocked()); QVERIFY(c.pendingAlert());
        b.capture["ffxiv_running"] = false; c.refresh(); QVERIFY(!c.pendingAlert()); QVERIFY(!c.blocked());
    }
    void candidateDoesNotBlockVerified() {
        RecordingBackend b; mr::AutomaticRecordingController c(&b); c.refresh(); QCOMPARE(c.state(), "listening");
        QVERIFY(!c.blocked()); QVERIFY(!b.calls.contains("StartCaptureValidation"));
    }
    void restoresOnlyFollowAndConfirmsReply() {
        RecordingBackend b; b.settings["follow_game"] = false;
        const auto candidate = b.settings["candidate_validation_enabled"], whitelist = b.settings["research_payload_opcodes"];
        mr::AutomaticRecordingController c(&b); c.refresh(); c.refresh();
        QCOMPARE(b.writes.size(), 1); QCOMPARE(b.writes.first(), QJsonObject({{"follow_game", true}}));
        QCOMPARE(b.settings["candidate_validation_enabled"], candidate); QCOMPARE(b.settings["research_payload_opcodes"], whitelist);
        QCOMPARE(c.state(), "listening");
        b.settings["follow_game"] = false; c.refresh(); QVERIFY(c.blocked()); QCOMPARE(b.writes.size(), 1);
        c.retry(); QCOMPARE(b.writes.size(), 2); QCOMPARE(c.state(), "listening");
    }
    void failedWriteDoesNotLoopAndExternalRecoveryWorks() {
        RecordingBackend b; b.settings["follow_game"] = false; b.failWrite = true;
        mr::AutomaticRecordingController c(&b); c.refresh(); c.refresh();
        QVERIFY(c.blocked()); QVERIFY(c.retryAvailable()); QCOMPARE(b.writes.size(), 1);
        b.settings["follow_game"] = true; c.refresh(); QCOMPARE(c.state(), "listening"); QVERIFY(!c.retryAvailable());
    }
    void validationStopFailureRecoveryAndSafeStop() {
        RecordingBackend b; b.validation["active"] = true; b.failStop = true;
        mr::AutomaticRecordingController c(&b); c.refresh(); c.refresh();
        QVERIFY(c.blocked()); QCOMPARE(b.calls.count("StopCaptureValidation"), 1);
        b.validation["active"] = false; c.refresh(); QCOMPARE(c.state(), "listening");
        b.validation["active"] = true; b.failStop = false; c.retry();
        QCOMPARE(c.state(), "listening"); QCOMPARE(b.calls.count("StopCaptureValidation"), 2);
    }
    void acceptedStopDrainsWithoutFailureOrRepeatedStop() {
        RecordingBackend b; b.validation["active"] = true; b.validation["state"] = "RECORDING";
        b.drainStop = true;
        mr::AutomaticRecordingController c(&b); QSignalSpy alerts(&c, &mr::AutomaticRecordingController::incidentRaised);
        c.refresh(); QCOMPARE(c.state(), "checking"); QVERIFY(!c.retryAvailable()); QVERIFY(!c.blocked());
        c.refresh(); QCOMPARE(c.state(), "checking"); QCOMPARE(b.calls.count("StopCaptureValidation"), 1);
        QCOMPARE(alerts.count(), 0);
        b.validation["active"] = false; b.validation["state"] = "COMPLETED"; c.refresh();
        QCOMPARE(c.state(), "listening"); QCOMPARE(b.calls.count("StopCaptureValidation"), 1);
    }
    void pendingStopImmediatelyRemovesListeningStatus() {
        RecordingBackend b; mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), "listening");
        b.validation["active"] = true; b.holdStop = true; c.refresh();
        QCOMPARE(c.state(), "checking"); QVERIFY(b.held);
        b.validation["active"] = false; b.held->succeed(b.validation);
        QCOMPARE(c.state(), "listening");
    }
    void staleConnectionCannotRecoverCurrentIncident() {
        RecordingBackend b; b.holdStatus = true; mr::AutomaticRecordingController c(&b);
        c.refresh(); auto *old = b.held; QVERIFY(old);
        b.capture["profile_status"] = "NONE"; b.reconnect(); QVERIFY(c.blocked());
        old->succeed({{"capture", QJsonObject{{"ffxiv_running", false}}}});
        QVERIFY(c.blocked()); QVERIFY(c.pendingAlert());
    }
    void auxiliaryFailureNeverHidesConfirmedProtocolBlocker() {
        RecordingBackend b; b.failSettings = true; b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b); c.refresh(); QVERIFY(c.blocked());
        b.capture["profile_status"] = "VERIFIED"; c.refresh(); QVERIFY(c.blocked());
        b.failSettings = false; c.refresh(); QCOMPARE(c.state(), "listening");
    }
    // ---------------------------------------------------------------- silence --
    // RUNNING + VERIFIED describes configuration, not traffic. Only a decoded
    // message counter is evidence that packets are actually arriving.
    void runningAndVerifiedButDecodingNothingIsNotListening() {
        RecordingBackend b;
        b.capture["messages_decoded"] = 0; b.capture["decode_errors"] = 0;
        b.capture["uptime_ms"] = 600000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.state() != QStringLiteral("listening"));
        QCOMPARE(c.state(), QStringLiteral("listening_silent"));
        QVERIFY(c.message().contains(QStringLiteral("还没有收到可识别的游戏数据")));
        QVERIFY(!c.blocked());
    }
    // A capture that has only just started has measured nothing yet, so silence
    // must not be reported against it.
    void aFreshCaptureIsNotAccusedOfSilence() {
        RecordingBackend b;
        b.capture["messages_decoded"] = 0; b.capture["uptime_ms"] = 12000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening"));
    }
    void unmeasuredCountersNeverInventSilence() {
        RecordingBackend b; b.capture["uptime_ms"] = 600000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening"));
    }
    void midstreamSilenceNamesTheReloginRatherThanACode() {
        RecordingBackend b; b.capture["silent_reason"] = "MIDSTREAM";
        b.capture["messages_decoded"] = 0; b.capture["uptime_ms"] = 4000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.state() != QStringLiteral("listening"));
        QVERIFY(c.message().contains(QStringLiteral("回到标题画面重新登录")));
        QVERIFY(c.blocked());
    }
    void adapterSilenceSendsTheUserToTheAdapterChoice() {
        RecordingBackend b; b.capture["silent_reason"] = "NO_PACKETS_ON_ADAPTER";
        b.capture["messages_decoded"] = 0; b.capture["uptime_ms"] = 4000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening_silent"));
        QVERIFY(c.message().contains(QStringLiteral("重新选择网卡")));
    }
    void unknownSilentReasonFallsBackInsteadOfLeakingTheToken() {
        RecordingBackend b; b.capture["silent_reason"] = "SOMETHING_NEW";
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening_silent"));
        QVERIFY(!c.message().contains(QStringLiteral("SOMETHING_NEW")));
        QVERIFY(c.message().contains(QStringLiteral("还没有收到可识别的游戏数据")));
    }
    void theCollectorsOwnHintWinsOverOurWording() {
        RecordingBackend b; b.capture["silent_reason"] = "NO_STREAM_OWNERSHIP";
        b.capture["hint"] = QString::fromUtf8("换一张网卡试试。");
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening_silent"));
        QCOMPARE(c.message(), QString::fromUtf8("换一张网卡试试。"));
    }
    void anExplicitNoneWithTrafficIsGreenAgain() {
        RecordingBackend b; b.capture["silent_reason"] = "NONE";
        b.capture["messages_decoded"] = 128744; b.capture["uptime_ms"] = 600000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening"));
        QVERIFY(!c.blocked());
    }
    // An explicit NONE is the Collector's own measurement; the desktop must not
    // override it with the local uptime heuristic.
    void anExplicitNoneOutranksTheLocalHeuristic() {
        RecordingBackend b; b.capture["silent_reason"] = "NONE";
        b.capture["messages_decoded"] = 0; b.capture["uptime_ms"] = 600000;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening"));
    }
    // 无 jargon: a raw ERR_ code is not something a player can act on.
    void captureErrorsAreNamedInChineseNotAsACode() {
        RecordingBackend b; b.capture["last_error_code"] = "ERR_NPCAP_MISSING";
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.blocked());
        QVERIFY(c.message().contains(QStringLiteral("Npcap")));
        QVERIFY(!c.message().contains(QStringLiteral("ERR_NPCAP_MISSING")));
    }
    void anUnknownCaptureErrorStillReadsAsChinese() {
        RecordingBackend b; b.capture["last_error_code"] = "ERR_SOMETHING_NEW";
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.blocked());
        QVERIFY(c.message().startsWith(QStringLiteral("采集链路受阻")));
        // The raw token may only trail the sentence, never replace the explanation.
        QVERIFY(c.message().indexOf(QStringLiteral("ERR_SOMETHING_NEW")) > 10);
    }
    // ---------------------------------------------------------------- polling --
    // Three requests every two seconds while the game is not running is wasted
    // traffic: nothing they read can change until the game starts.
    void pollingBacksOffWhileTheGameIsNotRunning() {
        RecordingBackend b; b.capture["ffxiv_running"] = false;
        mr::AutomaticRecordingController c(&b);
        QCOMPARE(c.pollIntervalMs(), mr::AutomaticRecordingController::kActivePollMs);
        c.refresh();
        QCOMPARE(c.state(), "waiting");
        QCOMPARE(c.pollIntervalMs(), mr::AutomaticRecordingController::kIdlePollMs);

        // A status that says the game is running brings the fast period back.
        b.capture["ffxiv_running"] = true; c.refresh();
        QCOMPARE(c.state(), "listening");
        QCOMPARE(c.pollIntervalMs(), mr::AutomaticRecordingController::kActivePollMs);

        // So does a reconnect, before any observation: nothing may be assumed
        // about the game across a connection change.
        b.capture["ffxiv_running"] = false; c.refresh();
        QCOMPARE(c.pollIntervalMs(), mr::AutomaticRecordingController::kIdlePollMs);
        b.holdStatus = true; b.reconnect();
        QCOMPARE(c.pollIntervalMs(), mr::AutomaticRecordingController::kActivePollMs);
    }

    // The 2 s poll returns the value the Collector held when asked. Adopting it
    // while a local edit is still waiting out the 400 ms debounce snaps the
    // switch back under the user's cursor.
    void settingsPollNeverOverwritesAnEditThatIsStillPending() {
        RecordingBackend b; b.settings["autostart"] = false;
        mr::AppController app(&b, nullptr);
        QCOMPARE(app.captureSettings().value("autostart").toBool(), false);

        // The user moves the switch: optimistic locally, written after 400 ms.
        app.updateCaptureSetting("autostart", true);
        QCOMPARE(app.captureSettings().value("autostart").toBool(), true);

        // The poll lands mid-debounce and confirms the Collector's stale value.
        app.recording()->refresh();
        QCOMPARE(app.captureSettings().value("autostart").toBool(), true);

        // The debounced write goes out and is confirmed.
        QTRY_VERIFY(!b.writes.isEmpty());
        QCOMPARE(b.writes.last(), QJsonObject({{"autostart", true}}));
        QCOMPARE(app.captureSettings().value("autostart").toBool(), true);

        // With nothing pending the poll is authoritative again, so an external
        // change still reaches the page.
        b.settings["autostart"] = false;
        app.recording()->refresh();
        QCOMPARE(app.captureSettings().value("autostart").toBool(), false);
    }

    // 「点重新扫描 FF14 再试」 is only accurate if the rescan re-reads everything
    // the hint names, including the validation snapshot.
    void rescanAlsoRefreshesTheValidationSnapshot() {
        RecordingBackend b;
        mr::AppController app(&b, nullptr);
        const int before = b.calls.count("GetCaptureValidationStatus");
        app.rescanGame();
        QVERIFY2(b.calls.count("GetCaptureValidationStatus") > before,
                 "rescanGame must re-read the capture validation status");
        QVERIFY(b.calls.contains("GetProtocolProfileStatus"));
    }

    void normalMutationIsGatedAndMaintenancePreservesFollowChoice() {
        RecordingBackend b; b.settings["follow_game"] = false;
        mr::AppController app(&b, nullptr);
        app.toggleCapture(); app.updateCaptureSetting("follow_game", false); app.navigate(6);
        QVERIFY(!b.calls.contains("StartCaptureValidation")); QVERIFY(!b.calls.contains("StartCapture"));
        QVERIFY(b.writes.isEmpty()); QCOMPARE(app.currentPage(), 0);
        app.setMaintainerToolsVisible(true); app.recording()->refresh();
        QVERIFY(b.writes.isEmpty());
    }
};
int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    AutomaticRecordingTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "AutomaticRecordingTests.moc"
