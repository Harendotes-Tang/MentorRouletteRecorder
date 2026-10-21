#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
#include "AppController.h"
#include "CollectorText.h"
#include "CollectorTextTestSupport.h"
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
    // 保留 UpdateCaptureSettings 的回应，使两次设置写入能够同时在途，回应顺序
    // 由测试决定。
    bool holdWrites = false;
    mr::BackendReply *held = nullptr;
    bool holdSettings = false;
    mr::BackendReply *heldSettings = nullptr;
    QString holdFormalType;
    mr::BackendReply *heldFormal = nullptr;
    QList<mr::BackendReply *> heldWrites;
    QList<QJsonObject> heldWritePayloads;
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
    /// 让第 \a index 条被保留的写入返回，携带采集服务此刻的完整设置——真实回应
    /// 同样是全量的，晚一条写入的改动并不在其中。
    void completeWrite(int index) {
        const QJsonObject p = heldWritePayloads.at(index);
        for (auto i = p.begin(); i != p.end(); ++i) settings.insert(i.key(), i.value());
        heldWrites.at(index)->succeed(settings);
    }
    mr::BackendReply *request(const QString &type, const QJsonObject &p = {}) override {
        calls.append(type);
        auto *r = new mr::BackendReply(QString::number(calls.size()), type, this);
        if (!holdFormalType.isEmpty() && type == holdFormalType) {
            holdFormalType.clear(); heldFormal = r; return r;
        }
        if (type == QLatin1String("GetCaptureSettings")) {
            if (holdSettings) { holdSettings = false; heldSettings = r; return r; }
            if (failSettings) r->fail("ERR_TEST", "settings unavailable"); else r->succeed(settings);
        } else if (type == QLatin1String("UpdateCaptureSettings")) {
            writes.append(p);
            if (holdWrites) { heldWrites.append(r); heldWritePayloads.append(p); return r; }
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
    void recordingPollAndPageShareTheSameGameFacts() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        b.capture["profile_status"] = "NONE";
        mr::AppController app(&b, nullptr);
        QVERIFY(!app.ffxivRunning());

        b.capture["ffxiv_running"] = true;
        b.capture["profile_status"] = "UNSUPPORTED_BUILD";
        app.recording()->refresh();
        QVERIFY(app.recording()->blocked());
        QVERIFY(app.ffxivRunning());
        QCOMPARE(app.captureProfileStatus(), QStringLiteral("UNSUPPORTED_BUILD"));

        b.capture["ffxiv_running"] = false;
        app.recording()->refresh();
        QVERIFY(!app.ffxivRunning());
        QVERIFY(!app.recording()->blocked());
        QVERIFY(!app.recording()->pendingAlert());
        QCOMPARE(app.recording()->state(), QStringLiteral("waiting"));
    }
    void oldPageStatusCannotOverwriteANewerRecordingPoll() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        mr::AppController app(&b, nullptr);
        const auto oldCapture = b.capture;
        b.holdStatus = true;
        app.refreshStatus();
        auto *old = b.held;
        QVERIFY(old);
        b.capture["ffxiv_running"] = true;
        b.capture["profile_status"] = "UNSUPPORTED_BUILD";
        app.recording()->refresh();
        old->succeed({{"capture", oldCapture}, {"game", b.game}});
        QVERIFY(app.ffxivRunning());
        QVERIFY(app.recording()->blocked());
    }
    void oldRecordingPollCannotResurrectAClosedGameIncident() {
        RecordingBackend b;
        b.capture["profile_status"] = "NONE";
        mr::AppController app(&b, nullptr);
        b.holdStatus = true;
        app.recording()->refresh();
        auto *old = b.held;
        QVERIFY(old);
        const auto oldCapture = b.capture;
        b.capture["ffxiv_running"] = false;
        app.applyFormalCaptureStatus(b.capture.toVariantMap());
        old->succeed({{"capture", oldCapture}, {"game", b.game}});
        QVERIFY(!app.ffxivRunning());
        QVERIFY(!app.recording()->blocked());
        QVERIFY(!app.recording()->pendingAlert());
        QCOMPARE(app.recording()->state(), QStringLiteral("waiting"));
    }
    void oldConnectionStatusCannotReplaceTheReconnectedGame() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        mr::AppController app(&b, nullptr);
        const auto oldCapture = b.capture;
        b.holdStatus = true;
        app.refreshStatus();
        auto *old = b.held;
        QVERIFY(old);
        b.capture["ffxiv_running"] = true;
        b.capture["profile_status"] = "UNSUPPORTED_BUILD";
        b.reconnect();
        old->succeed({{"capture", oldCapture}, {"game", b.game}});
        QVERIFY(app.ffxivRunning());
        QVERIFY(app.recording()->blocked());
    }
    void staleCaptureDoesNotDiscardUnrelatedStatusInformation() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        mr::AppController app(&b, nullptr);
        const auto oldCapture = b.capture;
        b.holdStatus = true;
        app.refreshStatus();
        auto *old = b.held;
        b.capture["ffxiv_running"] = true;
        app.recording()->refresh();
        old->succeed({{"capture", oldCapture}, {"update", QJsonObject{{"state", "checking"}}}});
        QVERIFY(app.ffxivRunning());
        QCOMPARE(app.collectorStatus().value("update").toMap().value("state").toString(),
                 QStringLiteral("checking"));
    }
    void externalObservationDoesNotReleasePendingInitialization() {
        RecordingBackend b;
        mr::AppController app(&b, nullptr);
        b.calls.clear();
        b.holdSettings = true;
        app.recording()->refresh();
        QVERIFY(b.heldSettings);
        app.applyFormalCaptureStatus(b.capture.toVariantMap());
        app.recording()->refresh();
        QCOMPARE(b.calls.count(QStringLiteral("GetCaptureSettings")), 1);
        b.heldSettings->succeed(b.settings);
        QCOMPARE(app.recording()->state(), QStringLiteral("listening"));
    }
    void aNullRunningFactCannotClearAnIncident() {
        RecordingBackend b;
        b.capture["profile_status"] = "NONE";
        mr::AppController app(&b, nullptr);
        app.recording()->refresh();
        QVERIFY(app.recording()->blocked());
        b.capture["ffxiv_running"] = QJsonValue::Null;
        app.applyFormalCaptureStatus(b.capture.toVariantMap());
        app.refreshStatus();
        QVERIFY(app.ffxivRunning());
        QVERIFY(app.recording()->blocked());
    }
    void unreadablePathDoesNotAlternateBetweenPathAndRegionIncidents_data() {
        QTest::addColumn<QString>("region");
        QTest::newRow("unknown") << QStringLiteral("UNKNOWN");
        QTest::newRow("cn override") << QStringLiteral("CN");
        QTest::newRow("global override") << QStringLiteral("GLOBAL");
    }
    void unreadablePathDoesNotAlternateBetweenPathAndRegionIncidents() {
        QFETCH(QString, region);
        RecordingBackend b;
        b.capture["region"] = region;
        b.capture["game_build"] = QJsonValue::Null;
        b.capture["profile_status"] = "NONE";
        b.game["install_path_readable"] = false;
        mr::AppController app(&b, nullptr);
        app.recording()->refresh();
        QVERIFY(app.recording()->message().contains(QStringLiteral("找不到游戏的安装位置")));
        app.recording()->acknowledge();
        QSignalSpy alerts(app.recording(), &mr::AutomaticRecordingController::incidentRaised);
        app.refreshStatus();
        app.applyFormalCaptureStatus(b.capture.toVariantMap());
        app.recording()->refresh();
        QCOMPARE(alerts.count(), 0);
        QVERIFY(!app.recording()->pendingAlert());
        QVERIFY(app.recording()->message().contains(QStringLiteral("找不到游戏的安装位置")));
    }
    void lateFormalCommandCannotResurrectClosedGame_data() {
        QTest::addColumn<bool>("stopping");
        QTest::newRow("stop") << true;
        QTest::newRow("start") << false;
    }
    void lateFormalCommandCannotResurrectClosedGame() {
        QFETCH(bool, stopping);
        RecordingBackend b;
        b.capture["state"] = stopping ? "RUNNING" : "STOPPED";
        mr::AppController app(&b, nullptr);
        app.setMaintainerToolsVisible(true);
        b.holdFormalType = stopping ? QStringLiteral("StopCapture") : QStringLiteral("StartCapture");
        app.toggleCapture();
        QVERIFY(b.heldFormal);
        const auto oldCapture = b.capture;
        b.capture["ffxiv_running"] = false;
        b.capture["state"] = "STOPPED";
        app.applyFormalCaptureStatus(b.capture.toVariantMap());
        bool resurrected = false;
        connect(&app, &mr::AppController::statusChanged, &app, [&] {
            resurrected = resurrected || app.ffxivRunning();
        });
        b.heldFormal->succeed(oldCapture);
        QVERIFY(!resurrected);
        QVERIFY(!app.ffxivRunning());
    }
    void unknownRegionNamesTheActualRecoverySetting() {
        RecordingBackend b;
        b.capture["region"] = "UNKNOWN";
        b.capture["game_build"] = "2026.09.15.0000.0000";
        b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b);
        c.refresh();
        QVERIFY(c.blocked());
        QVERIFY(c.message().contains(QStringLiteral("区服")));
        QVERIFY(c.message().contains(QStringLiteral("设置 → 通用")));
        QVERIFY(!c.message().contains(QStringLiteral("支持文件")));
        QVERIFY(!b.calls.contains(QStringLiteral("StartCaptureValidation")));
    }
    void unknownBuildIsNotReportedAsAnUnsupportedBuild() {
        RecordingBackend b;
        b.capture["region"] = "CN";
        b.capture["game_build"] = QJsonValue::Null;
        b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b);
        c.refresh();
        QVERIFY(c.blocked());
        QVERIFY(c.message().contains(QStringLiteral("无法读取游戏版本")));
        QVERIFY(!c.message().contains(QStringLiteral("支持文件")));
    }
    void confirmedRegionSaveRechecksCaptureWithoutStartingValidation() {
        RecordingBackend b;
        b.settings["region_override"] = QJsonValue::Null;
        mr::AppController app(&b, nullptr);
        b.holdWrites = true;
        app.updateCaptureSetting(QStringLiteral("region_override"), QStringLiteral("CN"));
        QTRY_COMPARE(b.heldWrites.size(), 1);
        b.calls.clear();
        b.completeWrite(0);
        QCOMPARE(b.writes.last(), QJsonObject({{"region_override", "CN"}}));
        QVERIFY(b.calls.contains(QStringLiteral("GetStatus")));
        QVERIFY(!b.calls.contains(QStringLiteral("StartCaptureValidation")));
        QVERIFY(!b.calls.contains(QStringLiteral("StartCapture")));
    }
    void refusedRegionSaveRestoresTheSettingAndKeepsTheErrorVisible() {
        RecordingBackend b;
        b.settings["region_override"] = QJsonValue::Null;
        mr::AppController app(&b, nullptr);
        b.failWrite = true;
        app.updateCaptureSetting(QStringLiteral("region_override"), QStringLiteral("CN"));
        QTRY_VERIFY(!b.writes.isEmpty());
        QVERIFY(app.captureSettings().value("region_override").isNull());
        QVERIFY(!app.captureSettingsError().isEmpty());
        QVERIFY(!b.calls.contains(QStringLiteral("StartCaptureValidation")));
    }
    void incidentAcknowledgementRecoveryAndNewSession() {
        RecordingBackend b; b.capture["profile_status"] = "NONE";
        mr::AutomaticRecordingController c(&b); QSignalSpy alerts(&c, &mr::AutomaticRecordingController::incidentRaised);
        c.refresh(); QVERIFY(c.blocked()); QVERIFY(c.pendingAlert()); QCOMPARE(alerts.count(), 1);
        c.acknowledge(); c.refresh(); QVERIFY(c.blocked()); QVERIFY(!c.pendingAlert()); QCOMPARE(alerts.count(), 1);
        b.capture["profile_status"] = "VERIFIED"; c.refresh(); QCOMPARE(c.state(), "listening"); QVERIFY(!c.pendingAlert());
        b.capture.insert("profile_status", "NONE"); c.refresh(); QCOMPARE(alerts.count(), 2);
        b.capture["ffxiv_process_id"] = 456; c.refresh(); QCOMPARE(alerts.count(), 3);
    }
    void unreadableInstallPathIsNamedInsteadOfAProfileMismatch() {
        RecordingBackend b; b.capture["profile_status"] = "NONE"; b.game["install_path_readable"] = false;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QVERIFY(c.blocked()); QVERIFY(c.message().contains(QStringLiteral("找不到游戏的安装位置")));
        b.game["install_path_readable"] = true; c.refresh();
        QVERIFY(c.blocked()); QVERIFY(c.message().contains(QStringLiteral("适用于当前游戏版本的支持文件")));
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
        const auto candidate = b.settings["candidate_validation_enabled"];
        const auto whitelist = b.settings["research_payload_opcodes"];
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
        b.validation.insert("active", true); b.failStop = false; c.retry();
        QCOMPARE(c.state(), "listening"); QCOMPARE(b.calls.count("StopCaptureValidation"), 2);
    }
    void explicitObservationPausesMutationsButLegacyCleanupStillWorks() {
        RecordingBackend b;
        mr::AutomaticRecordingController c(&b);
        c.setUserObservation(true);
        b.validation["active"] = true;
        b.validation["state"] = "RECORDING";
        const qsizetype calls = b.calls.size();
        c.refresh(); c.retry(); b.reconnect();
        QCOMPARE(c.state(), "observing");
        QCOMPARE(b.calls.size(), calls);
        QCOMPARE(b.calls.count("StopCaptureValidation"), 0);
        // 显式所有权释放后，原有清理遗留验证工具的初始化契约继续有效。
        c.setUserObservation(false);
        QCOMPARE(b.calls.count("StopCaptureValidation"), 1);
        QCOMPARE(c.state(), "listening");
    }
    void lateAutomaticSnapshotCannotOverwriteExplicitObservation() {
        RecordingBackend b; b.holdStatus = true;
        mr::AutomaticRecordingController c(&b); c.refresh();
        auto *old = b.held; QVERIFY(old);
        c.setUserObservation(true);
        old->succeed({{"capture", b.capture}});
        QCOMPARE(c.state(), "observing");
        QVERIFY(!c.blocked());
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
        QVERIFY(c.message().contains(QStringLiteral("重新检测网卡")));
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
    // contracts/collector-texts.md: hint_ref names the hint by key. The message is a
    // recipe, so the same reading shows the language now on screen; without a key,
    // or with one this build does not know, the Collector's Chinese is shown.
    void theCollectorsHintIsShownInTheInterfaceLanguage() {
        const QVariantMap ref = mrtest::textRef("capture.hint.no_packets");
        const QString chinese = mr::collector_text::chinese(ref);
        QVERIFY(!chinese.isEmpty());
        RecordingBackend b; b.capture["silent_reason"] = "NO_STREAM_OWNERSHIP";
        b.capture["hint"] = chinese;
        b.capture["hint_ref"] = QJsonObject::fromVariantMap(ref);
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("listening_silent"));
        QCOMPARE(c.message(), chinese);
        const int serial = c.messageSerial();
        {
            const mrtest::MarkedLanguage language;
            QCOMPARE(c.message(), mrtest::marked("capture.hint.no_packets", chinese));
            // The next poll says the same thing in the new language: nothing new
            // to say, so a dismissed notice stays dismissed.
            c.refresh();
            QCOMPARE(c.messageSerial(), serial);
            b.capture["hint_ref"] = QJsonObject{{"key", "capture.hint.brand_new"}};
            c.refresh();
            QCOMPARE(c.message(), chinese);
        }
        b.capture.remove("hint_ref");
        c.refresh();
        QCOMPARE(c.message(), chinese);
        // Something else to say moves the serial on.
        b.capture["hint"] = QString::fromUtf8("换一张网卡试试。");
        c.refresh();
        QVERIFY(c.messageSerial() != serial);
    }
    void theFirstBlockerIsShownByItsKey() {
        const QVariantMap first = mrtest::textRef("calibration.blocker.no_login");
        const QVariantMap second = mrtest::textRef("calibration.blocker.finish_duty");
        RecordingBackend b; b.capture["profile_status"] = "UNSUPPORTED_BUILD";
        QJsonObject calibration{{"state", "BLOCKED"},
                                {"blockers", QJsonArray{mr::collector_text::chinese(first),
                                                        mr::collector_text::chinese(second)}},
                                {"blocker_refs", QJsonArray{QJsonObject::fromVariantMap(first),
                                                            QJsonObject::fromVariantMap(second)}},
                                {"events", QJsonArray{}}};
        b.capture["calibration"] = calibration;
        mr::AutomaticRecordingController c(&b); c.refresh();
        QCOMPARE(c.state(), QStringLiteral("calibration_blocked"));
        QVERIFY(c.message().startsWith(mr::collector_text::chinese(first)));
        {
            const mrtest::MarkedLanguage language;
            QVERIFY2(c.message().startsWith(mrtest::marked("calibration.blocker.no_login",
                                                           mr::collector_text::chinese(first))),
                     qPrintable(c.message()));
            QVERIFY(!c.message().contains(QStringLiteral("finish_duty")));
            // A refs list that does not match the blockers is ignored as a whole.
            calibration["blocker_refs"] = QJsonArray{QJsonObject::fromVariantMap(first)};
            b.capture["calibration"] = calibration;
            c.refresh();
            QVERIFY(c.message().startsWith(mr::collector_text::chinese(first)));
        }
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
        QVERIFY(c.message().startsWith(QStringLiteral("暂时不能自动记录")));
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
        b.capture.insert("ffxiv_running", false); c.refresh();
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

    // 第 11 条：两次设置写入重叠时，第一条回应不能代表第二条。它既不得让 2 秒
    // 轮询的旧快照重新生效，也不得把自己那份「更早的」全量设置照单收下——两者
    // 都会把用户刚拨的第二个开关弹回旧值。M-8 只处理了单条写入的情形。
    void settingsPollWaitsForEveryOutstandingWriteNotJustTheFirst() {
        RecordingBackend b;
        b.settings["autostart"] = false;
        b.settings["update_check_enabled"] = false;
        mr::AppController app(&b, nullptr);
        QCOMPARE(app.captureSettings().value("update_check_enabled").toBool(), false);
        b.holdWrites = true;

        // 两次改动各自走完 400 ms 防抖后先后上路，第一条尚未回应。
        app.updateCaptureSetting("autostart", true);
        QTRY_VERIFY(b.heldWrites.size() == 1);
        app.updateCaptureSetting("update_check_enabled", true);
        QTRY_VERIFY(b.heldWrites.size() == 2);

        // 第一条回应到达。它带回的是采集服务处理它时的设置，其中第二个开关还是
        // 旧值；第二条仍在途，故界面必须保留用户刚拨的那一下。
        b.completeWrite(0);
        QCOMPARE(app.captureSettings().value("autostart").toBool(), true);
        QCOMPARE(app.captureSettings().value("update_check_enabled").toBool(), true);

        // 轮询此刻带回同样过时的快照，也不得采纳。
        app.recording()->refresh();
        QCOMPARE(app.captureSettings().value("update_check_enabled").toBool(), true);

        // 第二条回应到达，在途写入清零，轮询重新具有权威。
        b.completeWrite(1);
        QCOMPARE(app.captureSettings().value("update_check_enabled").toBool(), true);
        b.holdWrites = false;
        b.settings["update_check_enabled"] = false;
        app.recording()->refresh();
        QCOMPARE(app.captureSettings().value("update_check_enabled").toBool(), false);
    }

    // 「点重新检测游戏再试」 is only accurate if the rescan re-reads everything
    // the hint names, including the validation snapshot.
    void rescanAlsoRefreshesTheValidationSnapshot() {
        RecordingBackend b;
        mr::AppController app(&b, nullptr);
        const qsizetype before = b.calls.count("GetCaptureValidationStatus");
        app.rescanGame();
        QVERIFY2(b.calls.count("GetCaptureValidationStatus") > before,
                 "rescanGame must re-read the capture validation status");
        QVERIFY(b.calls.contains("GetProtocolProfileStatus"));
    }

    // A choice outranks the profile and waiting messages only while there is
    // something to choose: the listed clients are what the player picks from.
    void gameChoiceOutranksProfileAndWaitingMessages() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        b.capture["profile_status"] = "UNSUPPORTED_BUILD";
        b.capture["game_selection_required"] = true;
        b.capture["game_selection_reason"] = "MULTIPLE";
        b.capture["game_processes"] = QJsonArray{
            QJsonObject{{"process_id", 101}, {"started_at_utc", "2026-10-02T01:00:00.000Z"}, {"selection_token", "first"}},
            QJsonObject{{"process_id", 202}, {"started_at_utc", "2026-10-02T02:00:00.000Z"}, {"selection_token", "second"}}};
        mr::AutomaticRecordingController recording(&b);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("choosing_game"));
        QVERIFY(recording.attention());
        QVERIFY(!recording.pendingAlert());
        QVERIFY(recording.message().contains(QString::fromUtf8("多个游戏客户端")));
        // The selected client exited and another one is still listed.
        b.capture["game_selection_reason"] = "EXITED";
        b.capture["game_processes"] = QJsonArray{b.capture["game_processes"].toArray().at(1)};
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("choosing_game"));
        QVERIFY(recording.message().contains(QString::fromUtf8("已退出")));
    }

    // 审查 OD-1：唯一的客户端正常关闭（EXITED），或几个客户端都关掉了
    // （MULTIPLE），列表里已经没有可选的游戏。这是普通的「等待游戏启动」：
    // 不点橙色、不要求选择，轮询也退回空闲周期。
    void nothingListedToChooseIsOrdinaryWaiting_data() {
        QTest::addColumn<QString>("reason");
        QTest::newRow("only client closed") << QStringLiteral("EXITED");
        QTest::newRow("every client closed") << QStringLiteral("MULTIPLE");
    }
    void nothingListedToChooseIsOrdinaryWaiting() {
        QFETCH(QString, reason);
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        b.capture["ffxiv_process_id"] = QJsonValue::Null;
        b.capture["state"] = "STOPPED";
        b.capture["game_selection_required"] = true;
        b.capture["game_selection_reason"] = reason;
        b.capture["game_processes"] = QJsonArray();
        mr::AutomaticRecordingController recording(&b);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("waiting"));
        QVERIFY(!recording.attention());
        QCOMPARE(recording.pollIntervalMs(), mr::AutomaticRecordingController::kIdlePollMs);
        QVERIFY(recording.message().contains(QString::fromUtf8("等待游戏启动")));
    }

    // 审查 OI-2：只列出一个客户端时，不能说「检测到多个游戏客户端」。
    void aSingleListedClientIsNotCalledSeveral() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        b.capture["game_selection_required"] = true;
        b.capture["game_selection_reason"] = "MULTIPLE";
        b.capture["game_processes"] = QJsonArray{
            QJsonObject{{"process_id", 202}, {"started_at_utc", "2026-10-02T02:00:00.000Z"}, {"selection_token", "second"}}};
        mr::AutomaticRecordingController recording(&b);
        recording.refresh();
        QCOMPARE(recording.state(), QStringLiteral("choosing_game"));
        QVERIFY(recording.attention());
        QVERIFY2(!recording.message().contains(QString::fromUtf8("多个")), qPrintable(recording.message()));
        QVERIFY(recording.message().contains(QString::fromUtf8("选择")));
    }

    // 「重扫 FF14」 must not ask for a choice when the scan found nothing to choose.
    void rescanWithNothingListedDoesNotAskForAChoice() {
        RecordingBackend b;
        b.capture["ffxiv_running"] = false;
        b.capture["ffxiv_process_id"] = QJsonValue::Null;
        b.capture["game_selection_required"] = true;
        b.capture["game_selection_reason"] = "EXITED";
        b.capture["game_processes"] = QJsonArray();
        b.game["running"] = false;
        mr::AppController app(&b, nullptr);
        app.rescanGame();
        QVERIFY2(!app.toastMessage().contains(QString::fromUtf8("记录对象")), qPrintable(app.toastMessage()));
        QVERIFY(app.toastMessage().contains(QString::fromUtf8("未找到正在运行的 FF14")));
        QVERIFY(!app.gameSelection()->required());
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
