// 新区服采证 (evidence/EvidencePage.qml): the five steps follow the Collector's
// $defs/CaptureValidationStatus, a later marker records the earlier ones as 漏按,
// the 回传说明 is composed from the round, and 开始下一轮 returns to step 1.
#include "DesktopTestRunner.h"
#include "EvidenceSceneFixture.h"
#include "AppController.h"
#include "EvidenceSubmissionController.h"
#include "Formatters.h"
#include "GameSelectionController.h"
#include "MockBackend.h"
#include "QmlComponentTestSupport.h"
#include "TestCollectorGuard.h"

#include <QGuiApplication>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QDateTime>
#include <QHash>
#include <QJsonObject>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>

#include <algorithm>
#include <memory>

namespace {

class EvidencePageTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        // The page is built from the shared controls; the evidence folder itself is
        // registered by QmlRuntimeTestRegistration.cpp like every feature folder.
        mrtest::registerSourceQmlComponents();
    }

    void stepsFollowTheCollectorsState_data()
    {
        QTest::addColumn<QString>("state");
        QTest::addColumn<int>("step");
        QTest::addColumn<QString>("badge");
        QTest::newRow("idle") << QStringLiteral("IDLE") << 1 << QString::fromUtf8("尚未开始");
        // A failed round still has files and a 回传说明 to hand over (review K4-4).
        QTest::newRow("failed") << QStringLiteral("FAILED") << 4 << QString::fromUtf8("失败");
        // The wait is named after what is waited for (2026-11-20); the mock waits for the
        // restart a round asks for when the game was already connected.
        QTest::newRow("waiting") << QStringLiteral("WAITING") << 2 << QString::fromUtf8("等待游戏重新启动");
        QTest::newRow("recording") << QStringLiteral("RECORDING") << 3 << QString::fromUtf8("正在取证");
        QTest::newRow("stopping") << QStringLiteral("STOPPING") << 4 << QString::fromUtf8("正在保存");
        QTest::newRow("completed") << QStringLiteral("COMPLETED") << 4 << QString::fromUtf8("已保存");
        // 取消等待: STOPPING with nothing counted stays at step 2, then the Collector
        // reports COMPLETED with reason CANCELLED and no trace.
        QTest::newRow("cancelling") << QStringLiteral("CANCELLING") << 2 << QString::fromUtf8("正在取消等待");
        QTest::newRow("cancelled") << QStringLiteral("CANCELLED") << 1 << QString::fromUtf8("尚未开始");
    }

    void stepsFollowTheCollectorsState()
    {
        QFETCH(QString, state);
        QFETCH(int, step);
        QFETCH(QString, badge);
        EvidenceScene scene;
        QVERIFY(scene.open(state));
        QString reported = state;
        if (state == QStringLiteral("CANCELLED"))
            reported = QStringLiteral("COMPLETED");
        else if (state == QStringLiteral("CANCELLING"))
            reported = QStringLiteral("STOPPING");
        QTRY_COMPARE(scene.app->validationState(), reported);
        QTRY_COMPARE(scene.stepStatus(step), QStringLiteral("cur"));
        for (int number = 1; number <= 5; ++number) {
            QString expectedStatus = QStringLiteral("todo");
            // The mock's failed round read nothing: only step 1 was really done.
            if (number < step && (number == 1 || state != QStringLiteral("FAILED")))
                expectedStatus = QStringLiteral("done");
            else if (number == step)
                expectedStatus = QStringLiteral("cur");
            QCOMPARE(scene.stepStatus(number), expectedStatus);
            // Only the current step is open until a done one is reopened.
            QCOMPARE(scene.stepOpen(number), number == step);
        }
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), badge);
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusHeadline")), badge);
        // The one action button of the round: 开始采证 before, 取消等待 / 停止采证 while it
        // runs; while the Collector saves, and once it has, there is nothing to press.
        // After a failure 开始采证 waits in the closed step 1 behind the files.
        auto *action = scene.item(QStringLiteral("templateObservationActionButton"));
        if (state == QStringLiteral("STOPPING") || state == QStringLiteral("CANCELLING")
            || state == QStringLiteral("COMPLETED")) {
            QVERIFY(!action);
        } else if (state == QStringLiteral("FAILED")) {
            QVERIFY(action);
            QVERIFY(!action->isVisible());
        } else {
            QVERIFY(action);
            QVERIFY(action->isVisible());
        }
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Audit 2026-11-20, K4-4: a waiting round is named after what it waits for. The
    // Collector waits with WAITING_GAME for a game to start and for one of several running
    // clients to be chosen; the second is not 「等待游戏启动」.
    void theWaitIsNamedAfterWhatItWaitsFor_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::addColumn<QString>("fixture");
        QTest::addColumn<QString>("title");
        QTest::newRow("game") << QStringLiteral("WAITING_GAME") << QString() << QString::fromUtf8("等待游戏启动");
        QTest::newRow("choice") << QStringLiteral("WAITING_GAME") << QStringLiteral("multiple")
                                << QString::fromUtf8("等待选择游戏");
        QTest::newRow("adapter") << QStringLiteral("WAITING_ADAPTER") << QString() << QString::fromUtf8("等待可用的网卡");
        QTest::newRow("identity") << QStringLiteral("WAITING_IDENTITY") << QString() << QString::fromUtf8("等待读取游戏版本");
        QTest::newRow("region") << QStringLiteral("WAITING_REGION") << QString() << QString::fromUtf8("等待识别区服");
        QTest::newRow("connection") << QStringLiteral("WAITING_CONNECTION_CHECK") << QString()
                                    << QString::fromUtf8("等待确认游戏连接");
        QTest::newRow("restart") << QStringLiteral("WAITING_RESTART") << QString() << QString::fromUtf8("等待游戏重新启动");
    }

    void theWaitIsNamedAfterWhatItWaitsFor()
    {
        QFETCH(QString, reason);
        QFETCH(QString, fixture);
        QFETCH(QString, title);
        EvidenceScene scene;
        if (!fixture.isEmpty())
            scene.backend.setRecordingFixture(fixture);
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("WAITING"), QStringLiteral("round-wait-for"));
        scene.backend.validation.insert(QStringLiteral("reason"), reason);
        QVERIFY(scene.open(QStringLiteral("WAITING")));
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("WAITING"));
        QTRY_COMPARE(scene.app->gameSelection()->required(), !fixture.isEmpty());
        QTRY_COMPARE(scene.sessionValue("waitingTitle").toString(), title);
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), title);
        QCOMPARE(scene.warnings->size(), 0);
    }

    void cancellingTheWaitReturnsToStepOne()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("WAITING")));
        QTRY_COMPARE(scene.stepStatus(2), QStringLiteral("cur"));
        QVERIFY(scene.click(QStringLiteral("templateObservationActionButton")));
        // Nothing was recorded: step 1 again, with the Collector's sentence and no
        // round to show in the sidebar - not 已结束，文件不完整 at step 4.
        QTRY_COMPARE(scene.stepStatus(1), QStringLiteral("cur"));
        QCOMPARE(scene.app->validationState(), QStringLiteral("COMPLETED"));
        for (int number = 2; number <= 5; ++number)
            QCOMPARE(scene.stepStatus(number), QStringLiteral("todo"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("尚未开始"));
        auto *cancelled = scene.item(QStringLiteral("evidenceCancelledText"));
        QVERIFY(cancelled && cancelled->isVisible());
        QCOMPARE(cancelled->property("text").toString(), QString::fromUtf8("已取消等待，未创建取证文件。"));
        QCOMPARE(scene.text(QStringLiteral("evidenceCounter_message_count")), mr::Formatters::dash());
        QVERIFY(scene.item(QStringLiteral("templateObservationActionButton")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aDoneStepReopensFromItsHeaderAndKeepsItsSummary()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QVERIFY(!scene.stepOpen(1));
        auto *step1 = scene.item(QStringLiteral("evidenceStep_1"));
        QVERIFY(step1);
        QVERIFY(QMetaObject::invokeMethod(step1, "toggleRequested"));
        QTRY_VERIFY(scene.stepOpen(1));
        QVERIFY(scene.stepOpen(3));
        const QString summary = scene.text(QStringLiteral("evidenceStepSummary_1"));
        QVERIFY2(summary.contains(QString::fromUtf8("开始于")), qPrintable(summary));
        // A step still to come stays locked.
        auto *step5 = scene.item(QStringLiteral("evidenceStep_5"));
        QVERIFY(step5);
        QVERIFY(QMetaObject::invokeMethod(step5, "toggleRequested"));
        QVERIFY(!scene.stepOpen(5));
        QCOMPARE(scene.warnings->size(), 0);
    }

    void aLaterMarkerRecordsTheEarlierOnesAsSkipped()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        const QString markersBefore = scene.text(QStringLiteral("evidenceCounter_marker_count"));
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_entered")));
        auto *session = scene.session();
        QVERIFY(session);
        QTRY_COMPARE(session->property("markedCount").toInt(), 1);
        const QVariantList marks = session->property("marks").toList();
        QCOMPARE(marks.size(), 5);
        QCOMPARE(marks.at(0).toString(), QStringLiteral("skip"));
        QCOMPARE(marks.at(1).toString(), QStringLiteral("skip"));
        QVERIFY(!marks.at(2).toMap().value(QStringLiteral("at")).toString().isEmpty());
        QVERIFY(marks.at(3).isNull());
        QVERIFY(session->property("chips").toMap().value(QStringLiteral("miss")).toBool());
        // Skipped and done markers are spent; the next one is the only one left to press.
        QVERIFY(!scene.item(QStringLiteral("templateObservationMarker_queued"))->isEnabled());
        QVERIFY(!scene.item(QStringLiteral("templateObservationMarker_entered"))->isEnabled());
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationMarker_victory"))->isEnabled());
        // The Collector received exactly the marker that was pressed.
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceCounter_marker_count")) != markersBefore);
        QTRY_VERIFY(!scene.app->validationFeedback().isEmpty());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void theReportIsComposedFromTheRound()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("COMPLETED")));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        QVERIFY(scene.text(QStringLiteral("evidenceSavedText")).startsWith(QString::fromUtf8("已保存")));
        // Every required file is still to be copied.
        QVERIFY(scene.item(QStringLiteral("evidenceToReportButton"))->isEnabled());
        QVERIFY(scene.click(QStringLiteral("evidenceToReportButton")));
        QTRY_COMPARE(scene.stepStatus(5), QStringLiteral("cur"));
        QCOMPARE(scene.stepStatus(4), QStringLiteral("done"));
        QString report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.startsWith(QString::fromUtf8("【导随记录器 · 新区服采证回报】")), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("软件版本：v") + scene.app->appVersion()), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("计数：报文 ") + mr::Formatters::count(1842)), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("随机类型：指导者随机")), qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("缺失：状态截图、trace.jsonl、trace.jsonl.sha256、诊断报告 JSON")),
                 qPrintable(report));
        QVERIFY2(!report.contains(QStringLiteral("mock-only")), qPrintable(report));

        // Ticking a file moves it from 缺失 to 附件.
        auto *session = scene.session();
        QVERIFY(session);
        QVERIFY(QMetaObject::invokeMethod(session, "toggleFile", Q_ARG(QVariant, QStringLiteral("trace"))));
        QTRY_VERIFY(scene.text(QStringLiteral("evidenceReportText")).contains(QString::fromUtf8("附件：trace.jsonl")));
        report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.contains(QString::fromUtf8("缺失：状态截图、trace.jsonl.sha256、诊断报告 JSON")), qPrintable(report));
        QCOMPARE(scene.text(QStringLiteral("evidenceStepSummary_4")), QString::fromUtf8("已保存 · 文件 1 / 4"));

        // 开始下一轮: the Collector still reports the finished round, the page is at step 1.
        QVERIFY(scene.click(QStringLiteral("evidenceNextRoundButton")));
        QTRY_COMPARE(scene.stepStatus(1), QStringLiteral("cur"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("尚未开始"));
        QCOMPARE(session->property("files").toMap().size(), 0);
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Audit 2026-11-20, I4-1: what the Collector found in the game program by itself (or
    // failed to find) is written only to collector-*.log, so a round that searched asks for
    // the log - and keeps asking once the capture has stopped and its status is gone.
    void aRoundThatSearchedForTheDecoderRequiresTheLog()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-search"), 500, 30);
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-search"));
        QTRY_VERIFY(scene.sessionValue("recording").toBool());
        const auto logRequired = [&scene] {
            const QVariantList required = scene.sessionValue("requiredFiles").toList();
            return std::ranges::any_of(required, [](const QVariant &file) {
                return file.toMap().value(QStringLiteral("key")).toString() == QLatin1String("log");
            });
        };
        QVERIFY(!logRequired());
        // An exact profile: nothing was searched for.
        const auto noteSearch = [&scene](const QString &source, const QString &profileId) {
            QVariantMap capture{{QStringLiteral("oodle_signature_source"), source}};
            if (!profileId.isEmpty()) capture.insert(QStringLiteral("oodle_profile_id"), profileId);
            const QVariant argument(capture);
            return QMetaObject::invokeMethod(scene.session(), "noteOodleSearch", Q_ARG(QVariant, argument));
        };
        QVERIFY(noteSearch(QStringLiteral("profile"), QStringLiteral("cn.2026.08.05")));
        QVERIFY(!logRequired());
        QVERIFY(noteSearch(QStringLiteral("pattern-fallback"), QStringLiteral("discovered.global.2026.11.03")));
        QTRY_VERIFY(logRequired());
        // The required rows come first, so the log is not filed under 按需附加.
        const QVariantList defs = scene.sessionValue("fileDefs").toList();
        QCOMPARE(defs.at(4).toMap().value(QStringLiteral("key")).toString(), QStringLiteral("log"));
        QVERIFY(defs.at(4).toMap().value(QStringLiteral("desc")).toString().contains(QString::fromUtf8("本机找到")));
        // A set found here may have decoded nothing: the row says it was used, never that it worked
        // (re-review 2026-11-20, R5-1).
        QVERIFY(!defs.at(4).toMap().value(QStringLiteral("desc")).toString().contains(QString::fromUtf8("读懂")));

        // Saved: the capture has stopped, the round still asks for the log.
        scene.report(roundStatus(QStringLiteral("COMPLETED"), QStringLiteral("round-search"), 520, 31,
                                 QStringLiteral("C:/mock-only/search/trace.jsonl")));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        QVERIFY(logRequired());
        QVERIFY2(scene.sessionValue("report").toString().contains(QStringLiteral("collector-*.log")),
                 qPrintable(scene.sessionValue("report").toString()));
        QVERIFY2(scene.text(QStringLiteral("evidenceStepSummary_4")).endsWith(QString::fromUtf8("文件 0 / 5")),
                 qPrintable(scene.text(QStringLiteral("evidenceStepSummary_4"))));

        // A search that came up short asks for it too; the next round starts without.
        QVERIFY(QMetaObject::invokeMethod(scene.session(), "startNextRound"));
        QTRY_VERIFY(!logRequired());
        scene.report(roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-search-2"), 10, 5));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-search-2"));
        QTRY_VERIFY(scene.sessionValue("recording").toBool());
        QVERIFY(noteSearch(QStringLiteral("builtin"), QString()));
        QTRY_VERIFY(logRequired());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-1: the Collector starts recording at the title screen, where the game holds
    // no connection and nothing can arrive yet. A 0 there is not an anomaly; the 60 s run
    // from the first status that counts a connection of the game.
    void zeroMessagesBeforeTheGameGoesOnlineAreNoAnomaly()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-title"), 0, 120);
        scene.backend.networkPath = {{QStringLiteral("ipv4_connections"), 0},
                                     {QStringLiteral("ipv6_connections"), 0},
                                     {QStringLiteral("loopback_connections"), 0}};
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-title"));
        QTRY_VERIFY(scene.sessionValue("elapsedSeconds").toInt() >= 120);
        scene.app->refreshStatus();
        QTRY_VERIFY(scene.app->captureStatus().contains(QStringLiteral("network_path")));
        QCOMPARE(scene.stepStatus(2), QStringLiteral("cur"));
        QVERIFY(!scene.sessionValue("anomaly").toBool());
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("正在取证"));
        QVERIFY(!scene.item(QStringLiteral("evidenceAnomalyTitle"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("evidenceStopExportButton"))->isVisible());

        // Logged in: the game holds connections, and the 60 s start now.
        scene.backend.networkPath.insert(QStringLiteral("ipv4_connections"), 3);
        scene.app->refreshStatus();
        QTRY_VERIFY(scene.sessionValue("connectedSince").toDouble() > 0);
        QVERIFY(!scene.sessionValue("anomaly").toBool());
        // A minute on and still nothing: now it is one.
        scene.session()->setProperty("connectedSince", nowMs() - 61000);
        QTRY_VERIFY(scene.sessionValue("anomaly").toBool());
        QCOMPARE(scene.text(QStringLiteral("evidenceAnomalyTitle")), QString::fromUtf8("报文一直为 0"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("数据异常"));
        QVERIFY(scene.item(QStringLiteral("evidenceStopExportButton"))->isVisible());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Audit 2026-11-10, K4-1: when the game's connections cannot be counted (the system table
    // could not be read, or a local proxy carries them), nothing starts the 60 s. A round
    // that has recorded five minutes with no 报文 then says so in amber, with the way out;
    // the red verdict still needs a measured connection.
    void anUncountedConnectionStillSaysNothingArrives()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-uncounted"), 0, 400);
        const QJsonValue unread(QJsonValue::Null);
        scene.backend.networkPath = {{QStringLiteral("ipv4_connections"), unread},
                                     {QStringLiteral("ipv6_connections"), unread},
                                     {QStringLiteral("loopback_connections"), unread}};
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-uncounted"));
        scene.app->refreshStatus();
        QTRY_VERIFY(scene.app->captureStatus().contains(QStringLiteral("network_path")));
        QTRY_VERIFY(scene.sessionValue("quiet").toBool());
        QVERIFY(!scene.sessionValue("anomaly").toBool());
        QCOMPARE(scene.sessionValue("badgeTone").toString(), QStringLiteral("warn"));
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("还没有收到报文"));
        QCOMPARE(scene.text(QStringLiteral("evidenceLiveTitle")), QString::fromUtf8("还没有收到报文"));
        QVERIFY(scene.item(QStringLiteral("evidenceQuietExportButton"))->isVisible());

        // Every connection of the game goes through this machine: still nothing to time by.
        scene.backend.networkPath = {{QStringLiteral("ipv4_connections"), 2},
                                     {QStringLiteral("ipv6_connections"), 0},
                                     {QStringLiteral("loopback_connections"), 2}};
        scene.app->refreshStatus();
        QTRY_COMPARE(scene.app->captureStatus().value(QStringLiteral("network_path")).toMap()
                         .value(QStringLiteral("loopback_connections")).toInt(), 2);
        QVERIFY(scene.sessionValue("quiet").toBool());

        // Counted zeros (the title screen): no hint, the round simply waits.
        scene.backend.networkPath = {{QStringLiteral("ipv4_connections"), 0},
                                     {QStringLiteral("ipv6_connections"), 0},
                                     {QStringLiteral("loopback_connections"), 0}};
        scene.app->refreshStatus();
        QTRY_VERIFY(!scene.sessionValue("quiet").toBool());
        QCOMPARE(scene.text(QStringLiteral("evidenceStatusBadgeText")), QString::fromUtf8("正在取证"));
        QVERIFY(!scene.item(QStringLiteral("evidenceQuietExportButton"))->isVisible());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-3: what the page remembers belongs to one Collector session. The same round
    // seen again after the status went blank keeps it; a new round - even one that starts in
    // the notification that ends a failed one - starts clean.
    void theRoundsMarksBelongToItsSession()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-one"), 500, 30);
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-one"));
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_queued")));
        QTRY_COMPARE(scene.sessionValue("markedCount").toInt(), 1);
        QTRY_VERIFY(!scene.app->markerCommandBusy());

        // The status goes blank (a dropped pipe) and the same round comes back.
        scene.report(QJsonObject());
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QString());
        scene.report(roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-one"), 520, 31));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-one"));
        QCOMPARE(scene.sessionValue("markedCount").toInt(), 1);

        // That round fails: its marks stay with it.
        scene.report(roundStatus(QStringLiteral("FAILED"), QStringLiteral("round-one"), 520, 31));
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("FAILED"));
        QCOMPARE(scene.sessionValue("markedCount").toInt(), 1);
        // The next round starts at once: nothing of the failed one is carried into it.
        scene.report(roundStatus(QStringLiteral("WAITING"), QStringLiteral("round-two")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-two"));
        QVERIFY(scene.marksAreEmpty());
        QCOMPARE(scene.sessionValue("chips").toMap().size(), 0);
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review I4-1: a marker counts once the Collector has taken it. A refused one is not
    // ticked, stays pressable and stays out of the 回传说明, and the refusal is said on the
    // step the player is on.
    void aRefusedMarkerIsNotRecorded()
    {
        EvidenceScene scene;
        scene.backend.refuseMarkers = true;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_entered")));
        QTRY_VERIFY(!scene.app->validationError().isEmpty());
        QTRY_VERIFY(scene.marksAreEmpty());
        QVERIFY(!scene.sessionValue("chips").toMap().value(QStringLiteral("miss")).toBool());
        QCOMPARE(scene.sessionValue("markerLine").toString(), QString::fromUtf8("无"));
        auto *error = scene.item(QStringLiteral("evidenceMarkingError"));
        QVERIFY(error && error->isVisible());
        QVERIFY2(error->property("text").toString().contains(scene.app->validationError()),
                 qPrintable(error->property("text").toString()));
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationMarker_entered"))->isEnabled());

        // Taken the second time: ticked, with the earlier ones as 漏按.
        scene.backend.refuseMarkers = false;
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_entered")));
        QTRY_VERIFY(!scene.app->markerCommandBusy());
        QCOMPARE(scene.sessionValue("markedCount").toInt(), 1);
        QVERIFY(scene.sessionValue("markerLine").toString().contains(QString::fromUtf8("已进入")));
        QVERIFY(scene.sessionValue("chips").toMap().value(QStringLiteral("miss")).toBool());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review RV4-6: while a marker waits for its answer the round cannot be stopped - a stop
    // would drop that answer, refusal included, and the page would count the marker anyway.
    void aMarkerInFlightHoldsTheStop()
    {
        EvidenceScene scene;
        scene.backend.setReplyDelay(400, QStringLiteral("AddCaptureValidationMarker"));
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->validationMarkerEnabled());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        auto *stop = scene.item(QStringLiteral("templateObservationActionButton"));
        QVERIFY(stop);
        QTRY_VERIFY(stop->isVisible() && stop->isEnabled());
        QVERIFY(scene.click(QStringLiteral("templateObservationMarker_left")));
        QVERIFY(scene.app->markerCommandBusy());
        QVERIFY(!scene.app->templateObservationActionEnabled());
        QVERIFY(!stop->isEnabled());
        // Not even through the action itself.
        scene.app->toggleTemplateObservation();
        QCOMPARE(scene.backend.counts.value(QStringLiteral("StopCaptureValidation")), 0);
        // Answered: the stop is offered again, and the marker counts.
        QTRY_VERIFY(!scene.app->markerCommandBusy());
        QTRY_VERIFY(stop->isEnabled());
        QCOMPARE(scene.sessionValue("markedCount").toInt(), 1);
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review I4-2: an adapter the player chose that is no longer listed is not 「自动选择」.
    // The round would wait for it, so the page says so and does not start.
    void aVanishedAdapterIsNotCalledAutomatic()
    {
        EvidenceScene scene;
        scene.backend.setRecordingFixture(QStringLiteral("waiting"));
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_COMPARE(scene.app->captureAdapters().size(), 1);
        QTRY_VERIFY(!scene.app->ffxivRunning());
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationActionButton"))->isEnabled());
        const QString gone = QStringLiteral("\\Device\\NPF_{0B7E3C1A-5D2F-4E6B-9A10-2C4D6E8F0A1B}");
        scene.app->setCaptureAdapterId(gone);
        QCOMPARE(scene.app->captureAdapterId(), gone);
        const QString unavailable = QString::fromUtf8("已选网卡不可用");
        QTRY_COMPARE(scene.sessionValue("adapterLabel").toString(), unavailable);
        QTRY_COMPARE(scene.item(QStringLiteral("evidenceAdapterControl"))->property("currentText").toString(),
                     unavailable);
        QVERIFY(scene.item(QStringLiteral("evidenceAdapterUnavailable"))->isVisible());
        QVERIFY(!scene.item(QStringLiteral("templateObservationActionButton"))->isEnabled());
        QVERIFY(!scene.sessionValue("report").toString().contains(QString::fromUtf8("自动选择")));
        // Back to 自动选择: nothing stands in the way.
        scene.app->setCaptureAdapterId(QString());
        QTRY_VERIFY(scene.item(QStringLiteral("templateObservationActionButton"))->isEnabled());
        QVERIFY(!scene.item(QStringLiteral("evidenceAdapterUnavailable"))->isVisible());
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review L3-1: the round chips draw the session's state. A tap asks for the change and
    // the binding brings it back, so 开始下一轮 clears them and the next tap sets exactly
    // what the 回传说明 then says.
    void roundChipsFollowTheSession()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        auto *chip = scene.item(QStringLiteral("evidenceRoundChip_mid"));
        QVERIFY(chip);
        QVERIFY(scene.tap(QStringLiteral("evidenceRoundChip_mid")));
        QTRY_VERIFY(scene.sessionValue("chips").toMap().value(QStringLiteral("mid")).toBool());
        QVERIFY(chip->property("checked").toBool());
        QVERIFY(QMetaObject::invokeMethod(scene.session(), "startNextRound"));
        QTRY_VERIFY(!chip->property("checked").toBool());
        QVERIFY(!scene.sessionValue("chips").toMap().value(QStringLiteral("mid")).toBool());
        QVERIFY(scene.tap(QStringLiteral("evidenceRoundChip_mid")));
        QTRY_VERIFY(scene.sessionValue("chips").toMap().value(QStringLiteral("mid")).toBool());
        QVERIFY(chip->property("checked").toBool());
        // Step 5's copy of the chips shows the same state.
        QVERIFY(scene.item(QStringLiteral("evidenceReportChip_mid"))->property("checked").toBool());
        QVERIFY(scene.sessionValue("report").toString().contains(QString::fromUtf8("情况：中途加入")));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-4: a failed round goes to its files, with the folder, the logs and a 回传说明
    // that names the failure; 开始下一轮 then returns to step 1.
    void aFailedRoundOffersItsFilesAndAReport()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        const auto failed = roundStatus(QStringLiteral("FAILED"), QStringLiteral("round-failed"), 0, 40,
                                        QStringLiteral("C:/mock-only/failed/trace.jsonl"));
        scene.backend.validation = roundStatus(QStringLiteral("STOPPING"), QStringLiteral("round-failed"), 0, 39);
        scene.backend.validation.insert(QStringLiteral("error_code"), QStringLiteral("ERR_INTERNAL"));
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("STOPPING"));
        QCOMPARE(scene.stepStatus(3), QStringLiteral("todo"));
        scene.report(failed);
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("FAILED"));
        QTRY_COMPARE(scene.stepStatus(4), QStringLiteral("cur"));
        QCOMPARE(scene.stepStatus(3), QStringLiteral("todo"));
        // No message was ever read, so 确认数据可读 is not ticked either.
        QCOMPARE(scene.stepStatus(2), QStringLiteral("todo"));
        QCOMPARE(scene.stepStatus(1), QStringLiteral("done"));
        QCOMPARE(scene.text(QStringLiteral("evidenceSavedText")), QString::fromUtf8("采证失败"));
        auto *folder = scene.item(QStringLiteral("evidenceFileAction_trace"));
        QVERIFY(folder && folder->isVisible());
        auto *logs = scene.item(QStringLiteral("evidenceFileAction_log"));
        QVERIFY(logs && logs->isVisible() && logs->isEnabled());
        QVERIFY(scene.item(QStringLiteral("evidenceFile_trace"))->isEnabled());
        // The list says what its ticks are for; the 角色识别 round's folder row is not here.
        auto *lead = scene.item(QStringLiteral("evidenceFileListLead"));
        QVERIFY(lead && lead->isVisible());
        QVERIFY2(lead->property("text").toString().contains(QString::fromUtf8("不勾也能继续")),
                 qPrintable(lead->property("text").toString()));
        QVERIFY(!scene.item(QStringLiteral("evidenceIdentityFiles"))->isVisible());
        QVERIFY(scene.click(QStringLiteral("evidenceToReportButton")));
        QTRY_COMPARE(scene.stepStatus(5), QStringLiteral("cur"));
        QCOMPARE(scene.stepStatus(3), QStringLiteral("todo"));
        const QString report = scene.text(QStringLiteral("evidenceReportText"));
        QVERIFY2(report.contains(QString::fromUtf8("结果：验证失败，文件可能不完整。详情见本机诊断日志。")),
                 qPrintable(report));
        QVERIFY2(report.contains(QString::fromUtf8("缺失：")), qPrintable(report));
        QVERIFY(scene.click(QStringLiteral("evidenceNextRoundButton")));
        QTRY_COMPARE(scene.stepStatus(1), QStringLiteral("cur"));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-5: before the data is readable, step 2 has its own plain 停止采证.
    void stepTwoOffersAPlainStop()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-quiet"), 20, 10);
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_COMPARE(scene.stepStatus(2), QStringLiteral("cur"));
        auto *stop = scene.item(QStringLiteral("evidenceReadinessStopButton"));
        QVERIFY(stop);
        QTRY_VERIFY(stop->isVisible() && stop->isEnabled());
        QVERIFY(scene.click(QStringLiteral("evidenceReadinessStopButton")));
        QTRY_COMPARE(scene.backend.counts.value(QStringLiteral("StopCaptureValidation")), 1);
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-6: the Collector's 2 h cap counts the wait, and so does the round's clock.
    void theRoundClockCountsTheWait()
    {
        EvidenceScene scene;
        scene.backend.scripted = true;
        scene.backend.validation = roundStatus(QStringLiteral("WAITING"), QStringLiteral("round-wait"));
        QVERIFY(scene.open(QStringLiteral("WAITING")));
        QTRY_COMPARE(scene.sessionValue("roundKey").toString(), QStringLiteral("round-wait"));
        auto *clock = scene.sessionValue("clock").value<QObject *>();
        QVERIFY(clock);
        QTRY_COMPARE(clock->property("seenRound").toString(), QStringLiteral("round-wait"));
        // Ninety seconds of waiting for the game.
        clock->setProperty("seenAt", nowMs() - 90000);
        QTRY_VERIFY(scene.sessionValue("elapsedSeconds").toInt() >= 90);
        QVERIFY(scene.text(QStringLiteral("evidenceElapsed")).startsWith(QStringLiteral("01:")));
        // Recording began thirty seconds ago; the clock still runs from the start of the wait.
        scene.report(roundStatus(QStringLiteral("RECORDING"), QStringLiteral("round-wait"), 500, 30));
        QTRY_COMPARE(scene.app->validationState(), QStringLiteral("RECORDING"));
        QVERIFY(scene.sessionValue("elapsedSeconds").toInt() >= 90);
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-7: step 1 says what the game is doing, not what a start would need.
    void stepOneNeverCallsARunningGameClosed()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("RECORDING")));
        QTRY_VERIFY(scene.app->ffxivRunning());
        QTRY_COMPARE(scene.stepStatus(3), QStringLiteral("cur"));
        QCOMPARE(scene.text(QStringLiteral("evidenceGameCheck")), QString::fromUtf8("游戏正在运行"));
        QCOMPARE(scene.warnings->size(), 0);
    }

    // Review K4-9: the round's pulses breathe only while someone can see them.
    void thePulsesRestWhileThePageIsHidden()
    {
        EvidenceScene scene;
        scene.reduceMotion = false;
        QVERIFY(scene.open(QStringLiteral("WAITING")));
        QTRY_VERIFY(scene.app->validationActive());
        auto *pulse = scene.item(QStringLiteral("evidenceStatusPulse"));
        QVERIFY(pulse);
        QTRY_VERIFY(pulse->property("animating").toBool());
        scene.page->setVisible(false);
        QTRY_VERIFY(!pulse->property("animating").toBool());
        scene.page->setVisible(true);
        QTRY_VERIFY(pulse->property("animating").toBool());
        QCOMPARE(scene.warnings->size(), 0);
    }

    void theBackLinkAsksTheCapturePageToReturn()
    {
        EvidenceScene scene;
        QVERIFY(scene.open(QStringLiteral("IDLE")));
        QSignalSpy back(scene.page, SIGNAL(backRequested()));
        QVERIFY(back.isValid());
        QVERIFY(scene.click(QStringLiteral("evidenceBackButton")));
        QCOMPARE(back.count(), 1);
        QCOMPARE(scene.warnings->size(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(EvidencePage)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    EvidencePageTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "EvidencePageTests.moc"
