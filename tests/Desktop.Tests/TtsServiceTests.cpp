// ---------------------------------------------------------------------------
// tst_ttsservice - template substitution and rate/volume mapping.
//
// These tests must pass on a machine with no speech engine at all, so nothing
// here asks the service to actually produce sound: the pure helpers are static,
// and the announcement path is observed through TtsService::spoke().
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "CalibrationController.h"
#include "CollectorProcess.h"
#include "MockBackend.h"
#include "TtsService.h"

#include "IBackend.h"

#include <QDateTime>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QTextToSpeech>
#include <QDir>
#include <QStandardPaths>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QScopeGuard>
#include <QTimer>
#include <memory>

namespace {

/// A backend that answers nothing and only emits the LiveEvent shapes the
/// Collector really sends ($defs/LiveEvent), so the shell's event routing can
/// be pinned on a machine that can never run a live capture.
class EventOnlyBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override { return m_connected; }

    /// Drop or restore the pipe, exactly as IpcClient does. Used to replay the
    /// bus's reconnect behaviour: the same events, with the same event_ids,
    /// delivered a second time.
    void setConnected(bool connected)
    {
        if (m_connected == connected)
            return;
        m_connected = connected;
        Q_EMIT connectionChanged();
    }

    /// One failed connect attempt, the way IpcClient reports one: the pipe is
    /// still down, and connectionChanged fires again.
    void reportConnectFailure() { Q_EMIT connectionChanged(); }

    /// GetDashboardStats refuses. The terminal announcement must then not
    /// invent a progress number, and the cards must keep what they had.
    bool failDashboard = false;
    /// How many further CorrectRun calls answer ERR_REVISION_CONFLICT.
    int correctRunConflicts = 0;
    /// What GetRunRevisions answers with.
    QJsonObject revisions;

    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject &payload = {}) override
    {
        ++m_requestsByType[messageType];
        m_lastPayloadByType.insert(messageType, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("test"), messageType, this);
        if (messageType == QLatin1String("GetDashboardStats") && failDashboard) {
            QTimer::singleShot(0, reply, [reply] {
                reply->fail(QStringLiteral("ERR_INTERNAL"),
                            QString::fromUtf8("统计读取失败。"));
            });
            return reply;
        }
        if (messageType == QLatin1String("CorrectRun") && correctRunConflicts > 0) {
            --correctRunConflicts;
            QTimer::singleShot(0, reply, [reply] {
                reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"),
                            QString::fromUtf8("记录已被修改。"));
            });
            return reply;
        }
        // GetDashboardStats answers with the numbers the fixture was told to
        // report, so a test can prove the terminal announcement waited for the
        // refreshed count instead of speaking the previous one.
        QJsonObject answer;
        if (messageType == QLatin1String("GetDashboardStats"))
            answer = m_dashboard;
        else if (messageType == QLatin1String("GetRunRevisions"))
            answer = revisions;
        else if (messageType == QLatin1String("CorrectRun"))
            answer = m_correctRunAnswer;
        else if (messageType == QLatin1String("BackupDatabase"))
            answer = QJsonObject{{QStringLiteral("integrity_check_passed"), true},
                                 {QStringLiteral("target_path"), QStringLiteral("backup.db")}};
        QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
        return reply;
    }

    void setCorrectRunAnswer(const QString &runId, int revision)
    {
        m_correctRunAnswer = QJsonObject{
            {QStringLiteral("run_id"), runId},
            {QStringLiteral("revision"), revision},
            {QStringLiteral("audit_event_id"), QStringLiteral("audit")}};
    }

    int countOf(const QString &messageType) const
    {
        return m_requestsByType.value(messageType, 0);
    }
    QJsonObject lastPayloadOf(const QString &messageType) const
    {
        return m_lastPayloadByType.value(messageType);
    }
    void resetCounts()
    {
        m_requestsByType.clear();
        m_lastPayloadByType.clear();
    }

    /// The $defs/DashboardStats every GetDashboardStats answers with.
    void setDashboard(int completedCount, int baselineCount, int goalCount,
                      int pendingReview = 0)
    {
        m_dashboard.insert(QStringLiteral("completed_count"), completedCount);
        m_dashboard.insert(QStringLiteral("baseline_completed_count"), baselineCount);
        m_dashboard.insert(QStringLiteral("goal_count"), goalCount);
        m_dashboard.insert(QStringLiteral("unfinished_pending_review"), pendingReview);
    }

    void emitEvent(const QVariantMap &event) { Q_EMIT liveEvent(event); }

private:
    QHash<QString, int> m_requestsByType;
    QHash<QString, QJsonObject> m_lastPayloadByType;
    QJsonObject m_dashboard;
    QJsonObject m_correctRunAnswer;
    bool m_connected = true;
};

/// A Collector stub that answers "another instance already holds the serve
/// lease" and exits, written into \a directory. Returns its path, or an empty
/// string when it could not be written.
QString writeAlreadyRunningStub(const QString &directory)
{
    const QString path =
        QDir(directory).absoluteFilePath(QStringLiteral("already-running.cmd"));
    QFile script(path);
    if (!script.open(QIODevice::WriteOnly | QIODevice::Text))
        return QString();
    script.write(QByteArrayLiteral("@echo off\r\nexit /b ")
                 + QByteArray::number(mr::CollectorProcess::kAlreadyRunningExitCode)
                 + QByteArrayLiteral("\r\n"));
    script.close();
    return path;
}

/// Record \a pid as the holder of the per-user serve lease, the way the
/// Collector does.
bool writeServePid(const QString &directory, qint64 pid)
{
    QFile file(QDir(directory).absoluteFilePath(QStringLiteral("serve.pid")));
    if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
        return false;
    file.write(QByteArray::number(pid));
    file.close();
    return true;
}

/// Backdate serve.pid so the takeover stops reading it as "written seconds
/// ago", which is the one state in which nothing may be ended.
bool backdateServePid(const QString &directory, qint64 milliseconds)
{
    QFile file(QDir(directory).absoluteFilePath(QStringLiteral("serve.pid")));
    if (!file.open(QIODevice::ReadWrite))
        return false;
    const bool ok = file.setFileTime(QDateTime::currentDateTime().addMSecs(-milliseconds),
                                     QFileDevice::FileModificationTime);
    file.close();
    return ok;
}

/// $defs/LiveEvent.sequence is monotonic per subscription, and the shell drops
/// anything at or below the sequence it has already adopted. Reusing one number
/// for every fixture event would therefore mean only the first one is seen, so
/// the helper counts the way the bus does.
qint64 g_sequence = 0;

QVariantMap liveEvent(const QString &eventType, const QString &kind)
{
    QVariantMap event;
    // Every event on the wire carries its own event_id, and that id - not the
    // sequence - is what the shell de-duplicates a reconnect replay by. A
    // fixture that reused one id for every event would have every event after
    // the first silently dropped, which is not what the bus does.
    event.insert(QStringLiteral("event_id"),
                 QStringLiteral("11111111-2222-4333-8444-%1")
                     .arg(g_sequence + 1, 12, 10, QLatin1Char('0')));
    event.insert(QStringLiteral("event_type"), eventType);
    event.insert(QStringLiteral("kind"), kind);
    event.insert(QStringLiteral("emitted_at_utc"),
                 QStringLiteral("2026-09-04T11:00:00.000Z"));
    event.insert(QStringLiteral("sequence"), ++g_sequence);
    return event;
}

/// A run that ended just now, so the "ended before this session" guard the
/// announcements and the result question share lets it through.
QVariantMap freshRun(const QString &runId, bool pendingReview = false)
{
    QVariantMap run;
    run.insert(QStringLiteral("run_id"), runId);
    run.insert(QStringLiteral("revision"), 3);
    run.insert(QStringLiteral("duty_name"), QString::fromUtf8("天狼星灯塔"));
    run.insert(QStringLiteral("result"),
               pendingReview ? QStringLiteral("UNKNOWN") : QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("pending_review"), pendingReview);
    run.insert(QStringLiteral("ended_at_utc"),
               QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs));
    return run;
}

QVariantMap runFinishedEvent(const QString &state, const QVariantMap &run)
{
    QVariantMap event = liveEvent(QStringLiteral("RunFinished"),
                                  QStringLiteral("run_finished"));
    event.insert(QStringLiteral("state"), state);
    event.insert(QStringLiteral("run"), run);
    return event;
}

QVariantMap stateChangedEvent(const QString &state, const QVariantMap &run)
{
    QVariantMap event = liveEvent(QStringLiteral("StateChanged"),
                                  QStringLiteral("run_state_changed"));
    event.insert(QStringLiteral("state"), state);
    event.insert(QStringLiteral("match_from_queue"), false);
    event.insert(QStringLiteral("run"), run);
    return event;
}

} // namespace

class TtsServiceTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void appControllerWithoutSupervisorHasNoProcessAuthority()
    {
        mr::AppSettings settings;
        EventOnlyBackend backend;
        backend.setConnected(false);
        mr::AppController controller(&backend, &settings);
        QVERIFY(!controller.collectorForTest());
        QVERIFY(controller.findChildren<mr::CollectorProcess *>().isEmpty());
        for (int attempt = 0; attempt < 6; ++attempt)
            backend.reportConnectFailure();
        QVERIFY(!controller.collectorForTest());
        QCOMPARE(controller.collectorState(), QStringLiteral("missing"));
    }

    void injectedSupervisorIsBorrowedAndMayDisappear()
    {
        mr::AppSettings settings;
        EventOnlyBackend backend;
        auto supervisor = std::make_unique<mr::CollectorProcess>();
        mr::AppController controller(&backend, &settings, nullptr, supervisor.get());
        QCOMPARE(controller.collectorForTest(), supervisor.get());
        QVERIFY(!supervisor->parent());
        supervisor.reset();
        QVERIFY(!controller.collectorForTest());
        backend.setConnected(false);
        backend.reportConnectFailure();
        QCOMPARE(controller.collectorState(), QStringLiteral("missing"));
    }
    void render_substitutesKnownPlaceholdersOnly();
    void rateAndVolume_mapAndClamp();
    void announce_usesTemplatesAndHonoursMasterSwitch();
    void preview_speaksEvenWhenTheMasterSwitchIsOff();
    void mockBackend_emitsLiveEventsForEveryTransition();
    void appController_announcesEveryLiveTransition();
    void appController_onlyAnnouncesExplicitServerMatches_data();
    void appController_onlyAnnouncesExplicitServerMatches();
    void appController_speaksAgainWhenTheSameMatchIsOfferedAgain();
    void appController_routesEveryContractEventKind();
    void announcementKind_mapsEveryRunState_data();
    void announcementKind_mapsEveryRunState();
    void appController_announcesTerminalStatesFromRunFinishedOnly_data();
    void appController_announcesTerminalStatesFromRunFinishedOnly();
    void appController_finishedLineCarriesTheRefreshedProgress();
    void appController_ignoresReplayedAndOutOfOrderEvents();
    void appController_speaksTheDutyCarriedByTheStateEvent();
    void appController_ignoresTheSameEventsReplayedAfterAReconnect();
    void appController_acceptsARestartedCollectorsLowSequences();
    void appController_keepsTheDashboardAndDropsTheNumbersWhenItCannotBeRead();
    void appController_speaksNoNumbersWhenTheDashboardWasNeverRead();
    void appController_queuesAResultQuestionRaisedWhileTheDialogIsBusy();
    void appController_retriesAResultConfirmationAfterARevisionConflict();
    void appController_adoptsTheRevisionARunUpdatedCarries();
    void appController_runsTheDailyBackupOnTheFirstConnection();
    void appController_neverRelaunchesAReusedCollectorOnEveryFailedConnect();
    void appController_takesAVacatedLeaseOnTheFirstFailedConnect();
    void tts_voiceIsOnlyReconfiguredWhileTheEngineIsReady();
    void voices_areListedChineseFirstWithReadableLabels();
    void savedVoice_fallsBackToTheDefaultWhenItCannotBeUsed();
    void setVoice_persistsLocalChoicesAndIgnoresOtherProviders();
    void appController_ignoresHeartbeats();
    void appController_asksToConfirmTheResultOfAFinishedMentorDuty();
    void appController_confirmingSendsOneAuditedCorrection();
    void mockBackend_refusesSettingPendingReviewTrue();
};

void TtsServiceTests::render_substitutesKnownPlaceholdersOnly()
{
    QVariantMap values;
    values.insert(QStringLiteral("duty"), QString::fromUtf8("天狼星灯塔"));
    values.insert(QStringLiteral("progress"), 1446);
    values.insert(QStringLiteral("remaining"), 554);

    QCOMPARE(mr::TtsService::render(QString::fromUtf8("进入 {duty}"), values),
             QString::fromUtf8("进入 天狼星灯塔"));
    QCOMPARE(mr::TtsService::render(
                 QString::fromUtf8("导随完成，当前 {progress} 次，剩余 {remaining}"), values),
             QString::fromUtf8("导随完成，当前 1446 次，剩余 554"));

    // No placeholder at all: the template is returned untouched.
    QCOMPARE(mr::TtsService::render(QString::fromUtf8("指导者任务匹配成功"), values),
             QString::fromUtf8("指导者任务匹配成功"));

    // An unknown placeholder stays visible instead of turning into an empty
    // string, so a typo in the settings page is obvious when it is spoken.
    QCOMPARE(mr::TtsService::render(QStringLiteral("{duty}/{typo}"), values),
             QString::fromUtf8("天狼星灯塔/{typo}"));

    // One pass over the template, never over the result: a duty name that
    // happens to contain a placeholder is spoken as it is written. Replacing
    // key by key over the growing output would substitute it a second time,
    // which is a template injection through a name the Collector received
    // from the network.
    QVariantMap hostile = values;
    hostile.insert(QStringLiteral("duty"), QStringLiteral("{progress}"));
    QCOMPARE(mr::TtsService::render(QStringLiteral("{duty}"), hostile),
             QStringLiteral("{progress}"));

    // An opening brace with no closing one is text, not a broken placeholder.
    QCOMPARE(mr::TtsService::render(QString::fromUtf8("剩余 {remaining"), values),
             QString::fromUtf8("剩余 {remaining"));

    QCOMPARE(mr::TtsService::render(QString(), values), QString());
}

void TtsServiceTests::rateAndVolume_mapAndClamp()
{
    QCOMPARE(mr::TtsService::rateFromPercent(100), 0.0);
    QCOMPARE(mr::TtsService::rateFromPercent(50), -0.5);
    QCOMPARE(mr::TtsService::rateFromPercent(200), 1.0);
    QCOMPARE(mr::TtsService::rateFromPercent(150), 0.5);
    // Out of range on both ends clamps into [-1, 1] rather than wrapping.
    QCOMPARE(mr::TtsService::rateFromPercent(500), 1.0);
    QCOMPARE(mr::TtsService::rateFromPercent(-400), -1.0);

    QCOMPARE(mr::TtsService::volumeFromPercent(0), 0.0);
    QCOMPARE(mr::TtsService::volumeFromPercent(80), 0.8);
    QCOMPARE(mr::TtsService::volumeFromPercent(100), 1.0);
    QCOMPARE(mr::TtsService::volumeFromPercent(140), 1.0);
    QCOMPARE(mr::TtsService::volumeFromPercent(-20), 0.0);
}

void TtsServiceTests::announce_usesTemplatesAndHonoursMasterSwitch()
{
    mr::AppSettings settings;
    const bool wasEnabled = settings.ttsEnabled();
    const QString savedEntered = settings.templateEntered();
    settings.setTtsEnabled(true);
    settings.setTemplateEntered(QString::fromUtf8("进入 {duty}"));

    mr::TtsService service(&settings);
    QSignalSpy spy(&service, &mr::TtsService::spoke);

    QVariantMap values;
    values.insert(QStringLiteral("duty"), QString::fromUtf8("石卫塔"));
    service.announce(QStringLiteral("entered"), values);

    QCOMPARE(spy.count(), 1);
    QCOMPARE(spy.at(0).at(0).toString(), QStringLiteral("entered"));
    QCOMPARE(spy.at(0).at(1).toString(), QString::fromUtf8("进入 石卫塔"));

    // An unknown kind has no template, so nothing is announced.
    service.announce(QStringLiteral("no-such-kind"), values);
    QCOMPARE(spy.count(), 1);

    settings.setTtsEnabled(false);
    service.announce(QStringLiteral("entered"), values);
    QCOMPARE(spy.count(), 1);

    settings.setTtsEnabled(wasEnabled);
    settings.setTemplateEntered(savedEntered);
}

void TtsServiceTests::preview_speaksEvenWhenTheMasterSwitchIsOff()
{
    mr::AppSettings settings;
    const bool wasEnabled = settings.ttsEnabled();
    const QString savedCompleted = settings.templateCompleted();
    settings.setTtsEnabled(false);
    settings.setTemplateCompleted(
        QString::fromUtf8("导随完成，当前 {progress} 次，剩余 {remaining}"));

    mr::TtsService service(&settings);
    QSignalSpy spy(&service, &mr::TtsService::spoke);

    service.preview(QStringLiteral("completed"));
    QCOMPARE(spy.count(), 1);
    const QString spoken = spy.at(0).at(1).toString();
    QVERIFY(spoken.startsWith(QString::fromUtf8("导随完成，当前 ")));
    QVERIFY(!spoken.contains(QLatin1Char('{')));

    settings.setTtsEnabled(wasEnabled);
    settings.setTemplateCompleted(savedCompleted);
}

void TtsServiceTests::mockBackend_emitsLiveEventsForEveryTransition()
{
    mr::MockBackend backend;
    QSignalSpy spy(&backend, &mr::IBackend::liveEvent);

    backend.simulateRunTransitions(QStringLiteral("COMPLETED"));
    // Three transitions plus the RunFinished the bus always publishes with a
    // terminal state - StateChanged can skip that state entirely when the next
    // duty pops immediately, so RunFinished is the one that always arrives.
    QCOMPARE(spy.count(), 4);
    QCOMPARE(spy.at(0).at(0).toMap().value(QStringLiteral("state")).toString(),
             QStringLiteral("MENTOR_MATCHED"));
    QCOMPARE(spy.at(1).at(0).toMap().value(QStringLiteral("state")).toString(),
             QStringLiteral("ENTERED_DUTY"));
    QCOMPARE(spy.at(2).at(0).toMap().value(QStringLiteral("state")).toString(),
             QStringLiteral("COMPLETED"));
    QCOMPARE(spy.at(0).at(0).toMap().value(QStringLiteral("event_type")).toString(),
             QStringLiteral("StateChanged"));
    QCOMPARE(spy.at(3).at(0).toMap().value(QStringLiteral("event_type")).toString(),
             QStringLiteral("RunFinished"));
    QCOMPARE(spy.at(3).at(0).toMap().value(QStringLiteral("state")).toString(),
             QStringLiteral("COMPLETED"));

    // Every event is numbered the way $defs/LiveEvent.sequence is: strictly
    // increasing, because the shell drops anything it has already adopted.
    for (int i = 1; i < spy.count(); ++i) {
        QVERIFY(spy.at(i).at(0).toMap().value(QStringLiteral("sequence")).toLongLong()
                > spy.at(i - 1).at(0).toMap().value(QStringLiteral("sequence")).toLongLong());
    }

    // Switching the simulated live mode is itself a transition.
    spy.clear();
    backend.setLiveMode(mr::MockBackend::LiveMode::None);
    QCOMPARE(spy.count(), 1);
    QCOMPARE(spy.at(0).at(0).toMap().value(QStringLiteral("state")).toString(),
             QStringLiteral("IDLE"));
}

void TtsServiceTests::appController_announcesEveryLiveTransition()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);

    mr::MockBackend backend;
    mr::AppController controller(&backend, &settings);
    QVERIFY(controller.tts() != nullptr);

    QSignalSpy spy(controller.tts(), &mr::TtsService::spoke);
    // The mock's run ended on its fixed fixture date, which is long before this
    // process started; without moving the cutoff every terminal announcement is
    // correctly treated as a replay of old history.
    controller.setReflectionPromptCutoffForTest(
        QDateTime::fromString(QStringLiteral("2020-01-01T00:00:00.000Z"),
                              Qt::ISODateWithMs));
    backend.simulateRunTransitions(QStringLiteral("COMPLETED"));

    // 匹配 and 进本 are spoken straight from StateChanged; the terminal line
    // waits for the dashboard reply chained onto RunFinished.
    QCOMPARE(spy.count(), 2);
    QCOMPARE(spy.at(0).at(0).toString(), QStringLiteral("matched"));
    QCOMPARE(spy.at(1).at(0).toString(), QStringLiteral("entered"));
    QTRY_COMPARE_WITH_TIMEOUT(spy.count(), 3, 3000);
    QCOMPARE(spy.at(2).at(0).toString(), QStringLiteral("completed"));

    // Every abnormal terminal state maps onto the same 异常结束 template, and
    // the same run cannot be announced twice for the same state.
    spy.clear();
    backend.emitRunFinished(QStringLiteral("DISCONNECTED"));
    backend.emitRunFinished(QStringLiteral("DISCONNECTED"));
    QTRY_COMPARE_WITH_TIMEOUT(spy.count(), 1, 3000);
    QCOMPARE(spy.at(0).at(0).toString(), QStringLiteral("aborted"));

    backend.emitRunFinished(QStringLiteral("INTERRUPTED"));
    QTRY_COMPARE_WITH_TIMEOUT(spy.count(), 2, 3000);
    QCOMPARE(spy.at(1).at(0).toString(), QStringLiteral("aborted"));

    // A terminal state on StateChanged alone stays silent: RunFinished is the
    // event that always arrives, and announcing from both would say it twice.
    spy.clear();
    backend.emitStateChanged(QStringLiteral("LEFT_OR_ABANDONED"));
    QTest::qWait(50);
    QCOMPARE(spy.count(), 0);
}

void TtsServiceTests::appController_speaksAgainWhenTheSameMatchIsOfferedAgain()
{
    // Reported from a real evening: the match popped and was announced, somebody withdrew, the
    // finder re-formed the party, and the second popup was silent. The Collector now sends a
    // second MENTOR_MATCHED for the same run with a higher match_offer; the duplicate guard,
    // which exists for replays after a reconnect, must not mistake it for one.
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);
    const auto run = freshRun(QStringLiteral("offered-again"));
    auto first = stateChangedEvent(QStringLiteral("MENTOR_MATCHED"), run);
    first.insert(QStringLiteral("match_from_queue"), false);
    first.insert(QStringLiteral("match_offer"), 1);
    backend.emitEvent(first);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);

    // A replay of the same offer stays silent.
    backend.emitEvent(first);
    QTest::qWait(50);
    QCOMPARE(spoke.count(), 1);

    // A new event of its own (its own event id), as the Collector sends it.
    auto second = stateChangedEvent(QStringLiteral("MENTOR_MATCHED"), run);
    second.insert(QStringLiteral("match_from_queue"), false);
    second.insert(QStringLiteral("match_offer"), 2);
    backend.emitEvent(second);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 2, 3000);
    QCOMPARE(spoke.last().at(0).toString(), QStringLiteral("matched"));
}

void TtsServiceTests::appController_onlyAnnouncesExplicitServerMatches_data()
{
    QTest::addColumn<QVariant>("matchFromQueue");
    QTest::addColumn<QString>("calibrationState");
    QTest::addColumn<bool>("announceMatch");
    QTest::newRow("queue after restart") << QVariant(true) << "IDLE" << false;
    QTest::newRow("queue while observing") << QVariant(true) << "OBSERVING" << false;
    QTest::newRow("queue while upgrade is ready") << QVariant(true) << "READY" << false;
    QTest::newRow("queue while upgrade is done") << QVariant(true) << "DONE" << false;
    QTest::newRow("old event without source") << QVariant() << "IDLE" << false;
    QTest::newRow("malformed source") << QVariant(QStringLiteral("false")) << "IDLE" << false;
    QTest::newRow("server match with stale provisional status") << QVariant(false) << "OBSERVING" << true;
    QTest::newRow("server match after restart") << QVariant(false) << "IDLE" << true;
}

void TtsServiceTests::appController_onlyAnnouncesExplicitServerMatches()
{
    QFETCH(QVariant, matchFromQueue);
    QFETCH(QString, calibrationState);
    QFETCH(bool, announceMatch);
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    controller.calibration()->refreshFromCaptureStatus({
        {QStringLiteral("calibration"), QVariantMap{
            {QStringLiteral("state"), calibrationState},
            {QStringLiteral("local_profile_id"), QStringLiteral("cn.local")}}}});
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);
    const auto run = freshRun(QStringLiteral("match-source"));
    auto matched = stateChangedEvent(QStringLiteral("MENTOR_MATCHED"), run);
    if (matchFromQueue.isValid())
        matched.insert(QStringLiteral("match_from_queue"), matchFromQueue);
    else
        matched.remove(QStringLiteral("match_from_queue"));
    backend.emitEvent(matched);
    QTest::qWait(50);
    QCOMPARE(spoke.count(), announceMatch ? 1 : 0);
    if (announceMatch)
        QCOMPARE(spoke.first().at(0).toString(), QStringLiteral("matched"));

    // Replayed queue events remain silent; the real entry must still be announced.
    backend.emitEvent(matched);
    backend.emitEvent(stateChangedEvent(QStringLiteral("ENTERED_DUTY"), run));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), announceMatch ? 2 : 1, 3000);
    QCOMPARE(spoke.last().at(0).toString(), QStringLiteral("entered"));
}

void TtsServiceTests::appController_routesEveryContractEventKind()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);

    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    // run_state_changed: re-reads the current run and announces the transition.
    backend.resetCounts();
    QVariantMap state = liveEvent(QStringLiteral("StateChanged"),
                                  QStringLiteral("run_state_changed"));
    state.insert(QStringLiteral("state"), QStringLiteral("ENTERED_DUTY"));
    backend.emitEvent(state);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);
    QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("entered"));
    QVERIFY(backend.countOf(QStringLiteral("GetCurrentRun")) >= 1);
    QVERIFY(backend.countOf(QStringLiteral("GetDashboardStats")) >= 1);

    // stats_invalidated: statistics only, never a TTS announcement.
    backend.resetCounts();
    backend.emitEvent(liveEvent(QStringLiteral("DiagnosticsMessage"),
                                QStringLiteral("stats_invalidated")));
    QVERIFY(backend.countOf(QStringLiteral("GetDashboardStats")) >= 1);
    QVERIFY(backend.countOf(QStringLiteral("GetDungeonStats")) >= 1);
    QVERIFY(backend.countOf(QStringLiteral("GetJobStats")) >= 1);
    QCOMPARE(spoke.count(), 1);

    // collector_status: the capture status object rides along and is adopted
    // before the follow-up GetStatus lands.
    backend.resetCounts();
    QVariantMap status = liveEvent(QStringLiteral("CaptureStatusChanged"),
                                   QStringLiteral("collector_status"));
    QVariantMap capture;
    capture.insert(QStringLiteral("state"), QStringLiteral("RUNNING"));
    capture.insert(QStringLiteral("npcap_installed"), true);
    capture.insert(QStringLiteral("ffxiv_running"), true);
    status.insert(QStringLiteral("capture"), capture);
    backend.emitEvent(status);
    QCOMPARE(controller.capturing(), true);
    QCOMPARE(controller.npcapInstalled(), true);
    QVERIFY(backend.countOf(QStringLiteral("GetStatus")) >= 1);

    // run_created: the history list is reloaded, statistics are not re-queried
    // by this kind (stats_invalidated follows it on the wire).
    backend.resetCounts();
    backend.emitEvent(liveEvent(QStringLiteral("RunStarted"),
                                QStringLiteral("run_created")));
    QVERIFY(backend.countOf(QStringLiteral("QueryRuns")) >= 1);
}

void TtsServiceTests::announcementKind_mapsEveryRunState_data()
{
    QTest::addColumn<QString>("state");
    QTest::addColumn<QString>("kind");

    QTest::newRow("matched") << "MENTOR_MATCHED" << "matched";
    QTest::newRow("entered") << "ENTERED_DUTY" << "entered";
    QTest::newRow("completed") << "COMPLETED" << "completed";
    // The only terminal state the shipping CN profile can actually reach: the
    // duty ended, nobody observed a victory, so the line asks instead of
    // claiming a 通关. Such a run does not count toward the goal until the
    // user confirms it.
    QTest::newRow("unknown-final") << "UNKNOWN_FINAL_STATE" << "finished";
    QTest::newRow("left") << "LEFT_OR_ABANDONED" << "aborted";
    QTest::newRow("disconnected") << "DISCONNECTED" << "aborted";
    QTest::newRow("interrupted") << "INTERRUPTED" << "aborted";
    QTest::newRow("interrupted-pending") << "INTERRUPTED_PENDING_REVIEW" << "aborted";
    // Declining a duty pop is ordinary play, not an abnormal end.
    QTest::newRow("cancelled") << "CANCELLED_BEFORE_ENTRY" << "";
    QTest::newRow("idle") << "IDLE" << "";
    QTest::newRow("empty") << "" << "";
}

void TtsServiceTests::announcementKind_mapsEveryRunState()
{
    QFETCH(QString, state);
    QFETCH(QString, kind);
    QCOMPARE(mr::AppController::announcementKind(state), kind);
}

void TtsServiceTests::appController_announcesTerminalStatesFromRunFinishedOnly_data()
{
    QTest::addColumn<QString>("state");
    QTest::addColumn<QString>("kind");

    QTest::newRow("completed") << "COMPLETED" << "completed";
    QTest::newRow("unknown-final") << "UNKNOWN_FINAL_STATE" << "finished";
    QTest::newRow("left") << "LEFT_OR_ABANDONED" << "aborted";
    QTest::newRow("disconnected") << "DISCONNECTED" << "aborted";
    QTest::newRow("interrupted") << "INTERRUPTED" << "aborted";
    QTest::newRow("interrupted-pending") << "INTERRUPTED_PENDING_REVIEW" << "aborted";
    QTest::newRow("cancelled") << "CANCELLED_BEFORE_ENTRY" << "";
}

void TtsServiceTests::appController_announcesTerminalStatesFromRunFinishedOnly()
{
    QFETCH(QString, state);
    QFETCH(QString, kind);

    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    backend.setDashboard(10, 1400, 2000);
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    // 1. The same terminal state on StateChanged says nothing at all.
    backend.emitEvent(stateChangedEvent(state, freshRun(QStringLiteral("run-state"))));
    QTest::qWait(50);
    QCOMPARE(spoke.count(), 0);

    // 2. RunFinished is the event that speaks - or, for a declined pop, that
    //    deliberately stays silent.
    backend.emitEvent(runFinishedEvent(state, freshRun(QStringLiteral("run-finished"))));
    if (kind.isEmpty()) {
        QTest::qWait(80);
        QCOMPARE(spoke.count(), 0);
        return;
    }
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);
    QCOMPARE(spoke.at(0).at(0).toString(), kind);

    // 3. The very same run finishing again - the shape of a bus replay after a
    //    reconnect - is not announced a second time.
    backend.emitEvent(runFinishedEvent(state, freshRun(QStringLiteral("run-finished"))));
    QTest::qWait(80);
    QCOMPARE(spoke.count(), 1);
}

void TtsServiceTests::appController_finishedLineCarriesTheRefreshedProgress()
{
    // {progress} has to come from the dashboard refreshed after this run.
    // Reading the snapshot the shell already holds speaks the count from
    // *before* the run, always one behind.
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);
    settings.setTemplateFinished(
        QString::fromUtf8("导随结束，请确认是否通关，已确认 {progress} 次"));

    EventOnlyBackend backend;
    backend.setDashboard(45, 1400, 2000);
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    // The Collector counted the run before it published RunFinished.
    backend.setDashboard(46, 1400, 2000);
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-progress"), true)));

    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);
    QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("finished"));
    QCOMPARE(spoke.at(0).at(1).toString(),
             QString::fromUtf8("导随结束，请确认是否通关，已确认 1446 次"));
}

void TtsServiceTests::appController_ignoresReplayedAndOutOfOrderEvents()
{
    // On every (re)connect the bus replays up to 64 recent events without
    // marking them as replays. Acting on them again re-announces finished runs
    // and fires a burst of refreshes.
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    QVariantMap matched = stateChangedEvent(QStringLiteral("MENTOR_MATCHED"),
                                            freshRun(QStringLiteral("run-seq")));
    backend.emitEvent(matched);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);

    backend.resetCounts();
    // The identical event again, and one from before it: both are already
    // accounted for, so neither speaks nor starts a refresh.
    backend.emitEvent(matched);
    QVariantMap older = matched;
    older.insert(QStringLiteral("sequence"),
                 matched.value(QStringLiteral("sequence")).toLongLong() - 1);
    older.insert(QStringLiteral("state"), QStringLiteral("ENTERED_DUTY"));
    backend.emitEvent(older);
    QTest::qWait(80);
    QCOMPARE(spoke.count(), 1);
    QCOMPARE(backend.countOf(QStringLiteral("GetCurrentRun")), 0);
    QCOMPARE(backend.countOf(QStringLiteral("GetDashboardStats")), 0);

    // A newer sequence is adopted normally.
    backend.emitEvent(stateChangedEvent(QStringLiteral("ENTERED_DUTY"),
                                        freshRun(QStringLiteral("run-seq"))));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 2, 3000);
    QCOMPARE(spoke.at(1).at(0).toString(), QStringLiteral("entered"));
}

/// 「进入 {duty}」 is spoken the instant the transition arrives, and the
/// current-run card is fetched asynchronously - so at that moment it still
/// holds the MENTOR_MATCHED snapshot, whose duty_name is empty. The name has to
/// come off the run the event itself carries, or every single 进本 line says
/// 「进入 未知副本」.
void TtsServiceTests::appController_speaksTheDutyCarriedByTheStateEvent()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);

    // This backend answers GetCurrentRun with nothing at all, so anything the
    // line says about the duty can only have come from the event.
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    backend.emitEvent(stateChangedEvent(QStringLiteral("ENTERED_DUTY"),
                                        freshRun(QStringLiteral("run-duty"))));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);
    QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("entered"));
    QCOMPARE(spoke.at(0).at(1).toString(), QString::fromUtf8("进入 天狼星灯塔"));

    // With no run on the event there is nothing to name, and the line says so
    // rather than inventing one.
    QVariantMap bare = liveEvent(QStringLiteral("StateChanged"),
                                 QStringLiteral("run_state_changed"));
    bare.insert(QStringLiteral("state"), QStringLiteral("MENTOR_MATCHED"));
    bare.insert(QStringLiteral("match_from_queue"), false);
    backend.emitEvent(bare);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 2, 3000);
    QCOMPARE(spoke.at(1).at(0).toString(), QStringLiteral("matched"));
}

/// The bus replays up to 64 recent events to every new subscriber. Resetting
/// the sequence watermark on disconnect makes a reconnect to the *same*
/// Collector re-process all of them: the same runs announced again and a burst
/// of a hundred-odd IPC requests. event_id is what tells a replay from a
/// genuinely new event.
void TtsServiceTests::appController_ignoresTheSameEventsReplayedAfterAReconnect()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    backend.setDashboard(45, 1400, 2000);
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    const QVariantMap matched = stateChangedEvent(QStringLiteral("MENTOR_MATCHED"),
                                                  freshRun(QStringLiteral("run-replay")));
    const QVariantMap finished = runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                                  freshRun(QStringLiteral("run-replay"), true));
    backend.emitEvent(matched);
    backend.emitEvent(finished);
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 2, 3000);

    // The pipe drops and comes back. The reconnect refresh is expected; what
    // must not happen is the replay being acted on a second time.
    backend.setConnected(false);
    backend.setConnected(true);
    QTest::qWait(120);
    backend.resetCounts();

    backend.emitEvent(matched);
    backend.emitEvent(finished);
    QTest::qWait(120);

    QCOMPARE(spoke.count(), 2);
    QCOMPARE(backend.countOf(QStringLiteral("GetCurrentRun")), 0);
    QCOMPARE(backend.countOf(QStringLiteral("GetDashboardStats")), 0);
    QCOMPARE(backend.countOf(QStringLiteral("QueryRuns")), 0);
}

/// A Collector that really restarted numbers its events from the beginning
/// again. Those are new events, not a replay, and the watermark must follow
/// them down instead of silencing the whole new session.
void TtsServiceTests::appController_acceptsARestartedCollectorsLowSequences()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    backend.emitEvent(stateChangedEvent(QStringLiteral("MENTOR_MATCHED"),
                                        freshRun(QStringLiteral("run-before"))));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);

    backend.setConnected(false);
    backend.setConnected(true);
    QTest::qWait(120);

    // Sequence 1 again, with an event_id this process has never seen.
    QVariantMap restarted = stateChangedEvent(QStringLiteral("ENTERED_DUTY"),
                                              freshRun(QStringLiteral("run-after")));
    restarted.insert(QStringLiteral("sequence"), 1);
    backend.emitEvent(restarted);

    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 2, 3000);
    QCOMPARE(spoke.at(1).at(0).toString(), QStringLiteral("entered"));
}

/// A failed GetDashboardStats is not evidence that the numbers changed.
/// Blanking the snapshot empties every card and makes the achievement progress
/// read as "baseline + 0"; the previous answer is at worst this run behind.
void TtsServiceTests::appController_keepsTheDashboardAndDropsTheNumbersWhenItCannotBeRead()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    backend.setDashboard(45, 1400, 2000);
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);
    QTRY_VERIFY_WITH_TIMEOUT(!controller.dashboard().isEmpty(), 3000);

    backend.failDashboard = true;
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-stale"), true)));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);

    // The cards still hold the last answer...
    QVERIFY(!controller.dashboard().isEmpty());
    QCOMPARE(controller.dashboard().value(QStringLiteral("completed_count")).toInt(), 45);
    // ...and the line speaks it rather than "baseline + 0".
    QCOMPARE(spoke.at(0).at(1).toString(),
             QString::fromUtf8("导随结束，请确认是否通关，已确认 1445 次"));
}

/// When the dashboard has never been read at all there is no number to speak,
/// so the terminal line drops the count instead of announcing a wrong one.
void TtsServiceTests::appController_speaksNoNumbersWhenTheDashboardWasNeverRead()
{
    mr::AppSettings settings;
    settings.setTtsEnabled(true);
    settings.setConfirmPrompt(false);

    EventOnlyBackend backend;
    backend.failDashboard = true;
    mr::AppController controller(&backend, &settings);
    QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);

    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-blind"), true)));
    QTRY_COMPARE_WITH_TIMEOUT(spoke.count(), 1, 3000);
    QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("finished"));
    QCOMPARE(spoke.at(0).at(1).toString(),
             QString::fromUtf8("导随结束，请确认是否通关"));
    QVERIFY(controller.dashboard().isEmpty());
}

/// The dialog drops a request that arrives while it is already showing one.
/// Marking the run as asked at emit time loses that question for good.
void TtsServiceTests::appController_queuesAResultQuestionRaisedWhileTheDialogIsBusy()
{
    mr::AppSettings settings;
    settings.setConfirmPrompt(true);

    EventOnlyBackend backend;
    backend.setDashboard(10, 1400, 2000, 1);
    mr::AppController controller(&backend, &settings);
    QSignalSpy asked(&controller, &mr::AppController::resultConfirmationRequested);

    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-first"), true)));
    QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 1, 3000);
    // QML says the dialog really opened for this run.
    controller.resultConfirmationShown(QStringLiteral("run-first"));

    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-second"), true)));
    QTest::qWait(150);
    QCOMPARE(asked.count(), 1);

    controller.resultConfirmationClosed();
    QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 2, 3000);
    QCOMPARE(asked.at(1).at(0).toMap().value(QStringLiteral("run_id")).toString(),
             QStringLiteral("run-second"));

    // The acknowledged one is never asked about again.
    controller.resultConfirmationShown(QStringLiteral("run-second"));
    controller.resultConfirmationClosed();
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-first"), true)));
    QTest::qWait(150);
    QCOMPARE(asked.count(), 2);
}

/// The dialog sends the revision run_finished handed it. Any automatic
/// correction that lands first bumps that number, so the answer the user
/// already gave is refused until the new revision is read back.
void TtsServiceTests::appController_retriesAResultConfirmationAfterARevisionConflict()
{
    mr::AppSettings settings;
    EventOnlyBackend backend;
    backend.correctRunConflicts = 1;
    backend.setCorrectRunAnswer(QStringLiteral("run-ask"), 7);
    backend.revisions = QJsonObject{
        {QStringLiteral("items"),
         QJsonArray{QJsonObject{{QStringLiteral("revision"), 1}},
                    QJsonObject{{QStringLiteral("revision"), 7}}}}};

    mr::AppController controller(&backend, &settings);
    QSignalSpy succeeded(&controller, &mr::AppController::mutationSucceeded);
    QSignalSpy failed(&controller, &mr::AppController::mutationFailed);
    QSignalSpy revisions(&controller, &mr::AppController::runRevisionChanged);
    QTest::qWait(80);
    backend.resetCounts();

    controller.resolveRunResult(QStringLiteral("run-ask"), 3, QStringLiteral("COMPLETED"),
                                QString::fromUtf8("用户确认通关"), 24);

    QTRY_COMPARE_WITH_TIMEOUT(succeeded.count(), 1, 3000);
    QCOMPARE(failed.count(), 0);
    QCOMPARE(backend.countOf(QStringLiteral("CorrectRun")), 2);
    QCOMPARE(backend.countOf(QStringLiteral("GetRunRevisions")), 1);
    QCOMPARE(backend.lastPayloadOf(QStringLiteral("CorrectRun"))
                 .value(QStringLiteral("changes")).toObject()
                 .value(QStringLiteral("job_id")).toInt(), 24);
    // The retry used the revision that was read back, not the stale one.
    QCOMPARE(backend.lastPayloadOf(QStringLiteral("CorrectRun"))
                 .value(QStringLiteral("expected_revision"))
                 .toInt(),
             7);
    QCOMPARE(revisions.count(), 1);
    QCOMPARE(revisions.at(0).at(1).toInt(), 7);
    QCOMPARE(succeeded.at(0).at(0).toString(), QStringLiteral("review"));
}

/// A run_updated for the run a dialog is holding carries the new revision.
/// Adopting it is what keeps the next save from being refused.
void TtsServiceTests::appController_adoptsTheRevisionARunUpdatedCarries()
{
    mr::AppSettings settings;
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    QTest::qWait(80);

    QVariantMap selected;
    selected.insert(QStringLiteral("run_id"), QStringLiteral("run-open"));
    selected.insert(QStringLiteral("revision"), 3);
    controller.selectRun(selected);
    QCOMPARE(controller.selectedRun().value(QStringLiteral("revision")).toInt(), 3);

    QSignalSpy revisions(&controller, &mr::AppController::runRevisionChanged);
    QVariantMap run = freshRun(QStringLiteral("run-open"));
    run.insert(QStringLiteral("revision"), 9);
    QVariantMap updated = liveEvent(QStringLiteral("RunUpdated"),
                                    QStringLiteral("run_updated"));
    updated.insert(QStringLiteral("run"), run);
    backend.emitEvent(updated);

    QTRY_COMPARE_WITH_TIMEOUT(revisions.count(), 1, 3000);
    QCOMPARE(revisions.at(0).at(0).toString(), QStringLiteral("run-open"));
    QCOMPARE(revisions.at(0).at(1).toInt(), 9);
    QCOMPARE(controller.selectedRun().value(QStringLiteral("revision")).toInt(), 9);
}

/// A backup needs a Collector, and at start-up there is not one yet: the child
/// has only just been launched. Sending it there fails instantly, burns the one
/// attempt this session gets and puts 「备份失败：Collector 未连接。」 on screen at
/// every launch, while the backup itself never happens.
void TtsServiceTests::appController_runsTheDailyBackupOnTheFirstConnection()
{
    QFile::remove(mr::AppSettings::filePath());
    mr::AppSettings settings;
    settings.setAutoBackup(true);
    settings.setLastAutoBackupDate(QString());

    EventOnlyBackend backend;
    backend.setConnected(false);
    mr::AppController controller(&backend, &settings);
    QTest::qWait(80);
    backend.resetCounts();

    controller.runDailyBackupIfDue();
    QTest::qWait(80);
    QCOMPARE(backend.countOf(QStringLiteral("BackupDatabase")), 0);
    QVERIFY2(!controller.toastMessage().contains(QString::fromUtf8("备份失败")),
             qPrintable(controller.toastMessage()));

    backend.setConnected(true);
    QTRY_COMPARE_WITH_TIMEOUT(backend.countOf(QStringLiteral("BackupDatabase")), 1, 3000);

    // Still only once per session, however often the pipe comes and goes.
    backend.setConnected(false);
    backend.setConnected(true);
    QTest::qWait(150);
    QCOMPARE(backend.countOf(QStringLiteral("BackupDatabase")), 1);
    QFile::remove(mr::AppSettings::filePath());
}

/// A Collector that exited with ERR_ALREADY_RUNNING is not a fault: another
/// instance holds the per-user serve lease and is serving the pipe. Launching a
/// child on every failed reconnect only produces the same exit again, every 0.5
/// to 15 seconds, for as long as the program stays open, leaving a UI that says
/// 「复用已有实例」 while never connecting.
void TtsServiceTests::appController_neverRelaunchesAReusedCollectorOnEveryFailedConnect()
{
#ifndef Q_OS_WIN
    QSKIP("The stub needs a Windows batch file.");
#else
    QTemporaryDir stubDirectory;
    QTemporaryDir dataDirectory;
    QVERIFY(stubDirectory.isValid());
    QVERIFY(dataDirectory.isValid());

    const QString stub = writeAlreadyRunningStub(stubDirectory.path());
    QVERIFY(!stub.isEmpty());
    const QByteArray previousPath = qgetenv("MR_COLLECTOR_PATH");
    const QByteArray previousData = qgetenv("MR_DATA_DIR");
    qputenv("MR_COLLECTOR_PATH", QFile::encodeName(stub));
    qputenv("MR_DATA_DIR", QFile::encodeName(dataDirectory.path()));
    // A serve.pid that names somebody else and was not written just now: the
    // only state in which four failed connects may take the lease at all.
    QVERIFY(writeServePid(dataDirectory.path(), 424242));
    QVERIFY(backdateServePid(dataDirectory.path(), 60000));

    mr::AppSettings settings;
    EventOnlyBackend backend;
    backend.setConnected(false);
    mr::CollectorProcess supervisor;
    mr::AppController controller(&backend, &settings, nullptr, &supervisor);
    auto *collector = controller.collectorForTest();
    QVERIFY(collector);
    // The takeover must never reach the real system tools, nor the stop event
    // the developer's own Collector is waiting on.
    collector->setStopEventNameForTest(
        QStringLiteral("Local\\mr-tts-test-%1.stop").arg(QCoreApplication::applicationPid()));
    collector->setTakeoverTimingsForTest(150, 80);
    int toolCalls = 0;
    collector->setToolRunnerForTest([&toolCalls](const QString &, const QStringList &) {
        ++toolCalls;
        // "no task matches": whatever serve.pid named is not a Collector.
        return mr::CollectorProcess::ToolResult{
            0, QString::fromUtf8("信息: 没有运行的任务匹配指定标准。")};
    });
    QTRY_COMPARE_WITH_TIMEOUT(collector->stateToken(), QStringLiteral("reused"), 15000);

    // Three failed reconnects: nothing is launched, nothing is listed, nothing
    // is taken over.
    for (int attempt = 0; attempt < 3; ++attempt)
        backend.reportConnectFailure();
    QTest::qWait(250);
    QCOMPARE(collector->stateToken(), QStringLiteral("reused"));
    QVERIFY(!collector->isRunning());
    QCOMPARE(toolCalls, 0);
    QVERIFY(controller.toastMessage().isEmpty());

    // The fourth says the holder is not answering. Even then the holder is
    // asked through its stop event and given a grace before anything else.
    backend.reportConnectFailure();
    QTRY_VERIFY_WITH_TIMEOUT(
        controller.toastMessage().contains(QString::fromUtf8("采集服务")), 15000);
    QVERIFY(toolCalls > 0);

    qputenv("MR_COLLECTOR_PATH", previousPath);
    qputenv("MR_DATA_DIR", previousData);
#endif
}

/// The ordinary hand-off is not a takeover and must not look like one.
///
/// When the Collector this Desktop was reusing quits it removes its serve.pid,
/// so nothing holds the lease. Waiting out four failed reconnects (about seven
/// seconds) and then showing a toast naming 任务管理器 would alarm the user on a
/// completely normal path: the second window simply starts its own Collector,
/// on the first failed connect, and says nothing.
void TtsServiceTests::appController_takesAVacatedLeaseOnTheFirstFailedConnect()
{
#ifndef Q_OS_WIN
    QSKIP("The stub needs a Windows batch file.");
#else
    QTemporaryDir stubDirectory;
    QTemporaryDir dataDirectory;
    QVERIFY(stubDirectory.isValid());
    QVERIFY(dataDirectory.isValid());

    const QString stub = writeAlreadyRunningStub(stubDirectory.path());
    QVERIFY(!stub.isEmpty());
    const QByteArray previousPath = qgetenv("MR_COLLECTOR_PATH");
    const QByteArray previousData = qgetenv("MR_DATA_DIR");
    qputenv("MR_COLLECTOR_PATH", QFile::encodeName(stub));
    // No serve.pid: the holder took it with it when it quit.
    qputenv("MR_DATA_DIR", QFile::encodeName(dataDirectory.path()));

    mr::AppSettings settings;
    EventOnlyBackend backend;
    backend.setConnected(false);
    mr::CollectorProcess supervisor;
    mr::AppController controller(&backend, &settings, nullptr, &supervisor);
    auto *collector = controller.collectorForTest();
    QVERIFY(collector);
    collector->setStopEventNameForTest(
        QStringLiteral("Local\\mr-tts-vacant-%1.stop").arg(QCoreApplication::applicationPid()));
    int toolCalls = 0;
    collector->setToolRunnerForTest([&toolCalls](const QString &, const QStringList &) {
        ++toolCalls;
        return mr::CollectorProcess::ToolResult{0, QString()};
    });
    QTRY_COMPARE_WITH_TIMEOUT(collector->stateToken(), QStringLiteral("reused"), 15000);
    const int restartsBefore = collector->restartCount();

    // One failed reconnect is enough.
    backend.reportConnectFailure();
    QTRY_VERIFY_WITH_TIMEOUT(collector->stateToken() != QStringLiteral("reused"), 5000);
    // Nothing was listed, nothing was killed and the user was told nothing.
    QCOMPARE(toolCalls, 0);
    QVERIFY2(controller.toastMessage().isEmpty(),
             qPrintable(controller.toastMessage()));
    // It is a fresh launch, not the crash-restart backoff.
    QCOMPARE(collector->restartCount(), restartsBefore);

    qputenv("MR_COLLECTOR_PATH", previousPath);
    qputenv("MR_DATA_DIR", previousData);
#endif
}

/// The SAPI plugin declares itself Ready inside setVoice and starts the next
/// queued utterance, so re-applying the selection on a Speaking stateChanged
/// cuts the line that is playing off mid-word.
void TtsServiceTests::tts_voiceIsOnlyReconfiguredWhileTheEngineIsReady()
{
    QVERIFY(mr::TtsService::voiceApplicableInState(int(QTextToSpeech::Ready)));
    QVERIFY(!mr::TtsService::voiceApplicableInState(int(QTextToSpeech::Speaking)));
    QVERIFY(!mr::TtsService::voiceApplicableInState(int(QTextToSpeech::Paused)));
    QVERIFY(!mr::TtsService::voiceApplicableInState(int(QTextToSpeech::Error)));
    QVERIFY(!mr::TtsService::voiceApplicableInState(int(QTextToSpeech::Synthesizing)));
}

namespace {

QList<mr::TtsService::VoiceEntry> sampleVoices()
{
    return {
        {QStringLiteral("Microsoft David Desktop"), QLocale(QLocale::English, QLocale::UnitedStates),
         QVoice::Male},
        {QStringLiteral("Microsoft Huihui Desktop"), QLocale(QLocale::Chinese, QLocale::China),
         QVoice::Female},
        {QStringLiteral("Microsoft Hanhan Desktop"), QLocale(QLocale::Chinese, QLocale::Taiwan),
         QVoice::Female},
        {QStringLiteral("Microsoft Kangkang Desktop"), QLocale(QLocale::Chinese, QLocale::China),
         QVoice::Male},
        {QStringLiteral("Neutral"), QLocale(QLocale::Chinese, QLocale::China), QVoice::Unknown},
    };
}

} // namespace

/// Settings · 播报 lists the machine's voices with the Chinese ones on top, each
/// with an id the choice is stored under.
void TtsServiceTests::voices_areListedChineseFirstWithReadableLabels()
{
    const auto ordered = mr::TtsService::orderVoices(sampleVoices());
    QStringList names;
    for (const auto &voice : ordered)
        names.append(voice.name);
    QCOMPARE(names, (QStringList{QStringLiteral("Microsoft Huihui Desktop"),
                                 QStringLiteral("Microsoft Kangkang Desktop"),
                                 QStringLiteral("Neutral"),
                                 QStringLiteral("Microsoft David Desktop"),
                                 QStringLiteral("Microsoft Hanhan Desktop")}));

    const QVariantList rows = mr::TtsService::describeVoices(ordered);
    QCOMPARE(rows.size(), 5);
    const QVariantMap first = rows.first().toMap();
    QCOMPARE(first.value(QStringLiteral("id")).toString(),
             QStringLiteral("local:Microsoft Huihui Desktop"));
    QCOMPARE(first.value(QStringLiteral("name")).toString(),
             QStringLiteral("Microsoft Huihui Desktop"));
    QCOMPARE(first.value(QStringLiteral("label")).toString(),
             QString::fromUtf8("Microsoft Huihui Desktop（中文 · 女声）"));
    QCOMPARE(first.value(QStringLiteral("locale")).toString(), QStringLiteral("zh-CN"));
    QCOMPARE(first.value(QStringLiteral("provider")).toString(), QStringLiteral("local"));
    QCOMPARE(rows.at(1).toMap().value(QStringLiteral("label")).toString(),
             QString::fromUtf8("Microsoft Kangkang Desktop（中文 · 男声）"));
    // No gender: only the language is said.
    QCOMPARE(rows.at(2).toMap().value(QStringLiteral("label")).toString(),
             QString::fromUtf8("Neutral（中文）"));
    QCOMPARE(rows.at(3).toMap().value(QStringLiteral("label")).toString(),
             QString::fromUtf8("Microsoft David Desktop（英语 · 男声）"));
    // Chinese outside the mainland names its territory.
    QVERIFY(rows.at(4).toMap().value(QStringLiteral("label")).toString()
                .startsWith(QString::fromUtf8("Microsoft Hanhan Desktop（中文 · ")));
    QCOMPARE(mr::TtsService::localVoiceId(QStringLiteral("X")), QStringLiteral("local:X"));
}

/// Anything that does not name an installed local voice means "use today's
/// default" - never an error, never silence.
void TtsServiceTests::savedVoice_fallsBackToTheDefaultWhenItCannotBeUsed()
{
    const auto ordered = mr::TtsService::orderVoices(sampleVoices());
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("local:Microsoft Kangkang Desktop")), 1);
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("local:Microsoft David Desktop")), 3);
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QString()), -1);
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("local:")), -1);
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("local:Uninstalled Voice")), -1);
    // A bare name is not an id.
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("Microsoft Huihui Desktop")), -1);
    // An online voice is not a local one: the engine uses its default voice,
    // which is also what an online sentence falls back to.
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("azure:zh-CN-XiaoxiaoNeural")), -1);
    QCOMPARE(mr::TtsService::savedVoiceIndex(ordered, QStringLiteral("openai:alloy")), -1);
    QCOMPARE(mr::TtsService::savedVoiceIndex({}, QStringLiteral("local:Microsoft Huihui Desktop")), -1);
}

void TtsServiceTests::setVoice_persistsLocalChoicesAndIgnoresOtherProviders()
{
    mr::AppSettings settings;
    const QString saved = settings.ttsVoice();
    // Restored however the test ends: the INI is shared by the whole binary.
    const auto restore = qScopeGuard([&settings, saved] { settings.setTtsVoice(saved); });
    settings.setTtsVoice(QString());

    mr::TtsService service(&settings);
    // Ids of no known provider, and prefixes without a name, are not stored.
    service.setVoice(QStringLiteral("google:cmn-CN-Standard-A"));
    QCOMPARE(settings.ttsVoice(), QString());
    service.setVoice(QStringLiteral("azure:"));
    QCOMPARE(settings.ttsVoice(), QString());
    service.setVoice(QStringLiteral("local:"));
    QCOMPARE(settings.ttsVoice(), QString());

    // Online voices (P4b) are stored as chosen; the local engine keeps its
    // default voice, which is what an online sentence falls back to.
    service.setVoice(QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QCOMPARE(settings.ttsVoice(), QStringLiteral("azure:zh-CN-XiaoxiaoNeural"));
    QVERIFY(service.onlineVoiceSelected());
    // No Collector offers online speech here, so the id in use stays local.
    QVERIFY(!service.voiceId().startsWith(QStringLiteral("azure:")));
    service.setVoice(QStringLiteral("openai:alloy"));
    QCOMPARE(settings.ttsVoice(), QStringLiteral("openai:alloy"));
    service.setVoice(QString());
    QVERIFY(!service.onlineVoiceSelected());

    // A voice that is not installed is stored (it may come back) but not used.
    service.setVoice(QStringLiteral("local:Uninstalled Voice"));
    QCOMPARE(settings.ttsVoice(), QStringLiteral("local:Uninstalled Voice"));
    QVERIFY(service.voiceId() != QLatin1String("local:Uninstalled Voice"));
    QVERIFY(service.voiceName() != QLatin1String("Uninstalled Voice"));

    // With a real engine on this machine, a listed voice is actually switched to,
    // and clearing the choice goes back to the default. A machine without an
    // engine, or one whose voice list is still arriving, only runs the part above.
    const bool settled = QTest::qWaitFor(
        [&service] { return !service.voices().isEmpty() || !service.isAvailable(); }, 5000);
    if (settled && service.isAvailable() && !service.voices().isEmpty()) {
        const QVariantMap last = service.voices().constLast().toMap();
        const QString id = last.value(QStringLiteral("id")).toString();
        service.setVoice(id);
        QCOMPARE(settings.ttsVoice(), id);
        QTRY_COMPARE_WITH_TIMEOUT(service.voiceName(), last.value(QStringLiteral("name")).toString(), 3000);
        QCOMPARE(service.voiceId(), id);

        service.setVoice(QString());
        QCOMPARE(settings.ttsVoice(), QString());
        QTRY_VERIFY_WITH_TIMEOUT(!service.voiceId().isEmpty(), 3000);
        QVERIFY(service.voiceId().startsWith(QStringLiteral("local:")));
    }
}

void TtsServiceTests::appController_ignoresHeartbeats()
{
    // A heartbeat says only that the pipe is alive. Falling through to the
    // catch-all would fire a GetStatus - and the handlers hanging off it -
    // every five seconds.
    mr::AppSettings settings;
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);
    // Let the start-up refresh finish first, or its own GetStatus would be
    // mistaken for one a heartbeat caused.
    QTest::qWait(80);

    backend.resetCounts();
    backend.emitEvent(liveEvent(QStringLiteral("Heartbeat"), QStringLiteral("heartbeat")));
    backend.emitEvent(liveEvent(QStringLiteral("Heartbeat"), QStringLiteral("heartbeat")));
    QTest::qWait(50);
    QCOMPARE(backend.countOf(QStringLiteral("GetStatus")), 0);

    // An event this build does not name yet still refreshes the status, so a
    // future kind is never silently dropped.
    QVariantMap unknown = liveEvent(QStringLiteral("ProfileStatusChanged"),
                                    QStringLiteral("profile_status_changed"));
    backend.emitEvent(unknown);
    QVERIFY(backend.countOf(QStringLiteral("GetStatus")) >= 1);
}

void TtsServiceTests::appController_asksToConfirmTheResultOfAFinishedMentorDuty()
{
    mr::AppSettings settings;
    settings.setConfirmPrompt(true);

    EventOnlyBackend backend;
    backend.setDashboard(10, 1400, 2000, 1);
    mr::AppController controller(&backend, &settings);
    QSignalSpy asked(&controller, &mr::AppController::resultConfirmationRequested);

    // A run that really did end in a confirmed COMPLETED needs no question.
    backend.emitEvent(runFinishedEvent(QStringLiteral("COMPLETED"),
                                       freshRun(QStringLiteral("run-known"))));
    QTest::qWait(80);
    QCOMPARE(asked.count(), 0);

    // The CN case: finished, no observable result, pending_review.
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-ask"), true)));
    QTRY_COMPARE_WITH_TIMEOUT(asked.count(), 1, 3000);
    QCOMPARE(asked.at(0).at(0).toMap().value(QStringLiteral("run_id")).toString(),
             QStringLiteral("run-ask"));

    // Asked once per run, never again on a replay.
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-ask"), true)));
    QTest::qWait(80);
    QCOMPARE(asked.count(), 1);

    // A run that ended before this session started is history, not a question.
    QVariantMap old = freshRun(QStringLiteral("run-old"), true);
    old.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2020-01-01T00:00:00.000Z"));
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"), old));
    QTest::qWait(80);
    QCOMPARE(asked.count(), 1);

    // And the switch really switches it off.
    settings.setConfirmPrompt(false);
    backend.emitEvent(runFinishedEvent(QStringLiteral("UNKNOWN_FINAL_STATE"),
                                       freshRun(QStringLiteral("run-quiet"), true)));
    QTest::qWait(80);
    QCOMPARE(asked.count(), 1);
    settings.setConfirmPrompt(true);
}

void TtsServiceTests::appController_confirmingSendsOneAuditedCorrection()
{
    mr::AppSettings settings;
    EventOnlyBackend backend;
    mr::AppController controller(&backend, &settings);

    backend.resetCounts();
    controller.resolveRunResult(QStringLiteral("run-ask"), 3, QStringLiteral("COMPLETED"),
                                QString::fromUtf8("用户确认通关"));
    QCOMPARE(backend.countOf(QStringLiteral("CorrectRun")), 1);

    const QJsonObject sent = backend.lastPayloadOf(QStringLiteral("CorrectRun"));
    QCOMPARE(sent.value(QStringLiteral("run_id")).toString(), QStringLiteral("run-ask"));
    QCOMPARE(sent.value(QStringLiteral("expected_revision")).toInt(), 3);
    QCOMPARE(sent.value(QStringLiteral("reason")).toString(),
             QString::fromUtf8("用户确认通关"));
    const QJsonObject changes = sent.value(QStringLiteral("changes")).toObject();
    // 通关 is the only thing that makes achievement_progress grow, and writing
    // the result is *also* what takes the run off the 待复核 list: CorrectRun
    // acknowledges pending_review even if the result is unchanged. The separate
    // acknowledgement action may send pending_review:false, but choosing an
    // outcome already carries that decision, so exactly one key is needed here.
    QCOMPARE(changes.keys(), QStringList{QStringLiteral("result")});
    QCOMPARE(changes.value(QStringLiteral("result")).toString(),
             QStringLiteral("COMPLETED"));

    // 未通关 uses the same path with the honest result.
    backend.resetCounts();
    controller.resolveRunResult(QStringLiteral("run-ask"), 3,
                                QStringLiteral("LEFT_OR_ABANDONED"),
                                QString::fromUtf8("用户确认未通关"));
    QCOMPARE(backend.lastPayloadOf(QStringLiteral("CorrectRun"))
                 .value(QStringLiteral("changes"))
                 .toObject()
                 .value(QStringLiteral("result"))
                 .toString(),
             QStringLiteral("LEFT_OR_ABANDONED"));

    // A correction with no reason is refused before it reaches the Collector.
    backend.resetCounts();
    QSignalSpy failures(&controller, &mr::AppController::mutationFailed);
    controller.resolveRunResult(QStringLiteral("run-ask"), 3, QStringLiteral("COMPLETED"),
                                QStringLiteral("   "));
    QCOMPARE(backend.countOf(QStringLiteral("CorrectRun")), 0);
    QCOMPARE(failures.count(), 1);
    QCOMPARE(failures.at(0).at(0).toString(), QStringLiteral("ERR_REASON_REQUIRED"));
}

void TtsServiceTests::mockBackend_refusesSettingPendingReviewTrue()
{
    // The fixture must enforce the same one-way acknowledgement as Collector:
    // pending_review:false is allowed, but only the state machine may set true.
    mr::MockBackend backend;

    // A run the fixture really does flag for review, so the accepted half can
    // also prove the flag clears without ever being sent.
    QJsonObject pendingFilter;
    pendingFilter.insert(QStringLiteral("pending_review"), true);

    QString runId;
    QString currentResult;
    int revision = -1;
    bool listed = false;
    backend.queryRuns(pendingFilter, 1, 1)
        ->whenDone(&backend, [&](bool ok, const QVariantMap &payload, const QString &,
                                 const QString &) {
            listed = ok;
            const QVariantList items = payload.value(QStringLiteral("items")).toList();
            if (items.isEmpty())
                return;
            const QVariantMap run = items.first().toMap();
            runId = run.value(QStringLiteral("run_id")).toString();
            revision = run.value(QStringLiteral("revision")).toInt();
            currentResult = run.value(QStringLiteral("result")).toString();
        });
    QTRY_VERIFY_WITH_TIMEOUT(listed, 3000);
    QVERIFY(!runId.isEmpty());

    // Also change the result so the refusal proves that an invalid review flag
    // cannot be bypassed by including another legitimate correction.
    const QString answer = currentResult == QLatin1String("COMPLETED")
                               ? QStringLiteral("LEFT_OR_ABANDONED")
                               : QStringLiteral("COMPLETED");
    QJsonObject changes;
    changes.insert(QStringLiteral("result"), answer);
    changes.insert(QStringLiteral("pending_review"), true);
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), revision);
    payload.insert(QStringLiteral("reason"), QString::fromUtf8("用户确认通关"));
    payload.insert(QStringLiteral("changes"), changes);

    QString code;
    QString message;
    bool done = false;
    backend.request(QStringLiteral("CorrectRun"), payload)
        ->whenDone(&backend, [&](bool ok, const QVariantMap &, const QString &errorCode,
                                 const QString &errorMessage) {
            done = true;
            QVERIFY(!ok);
            code = errorCode;
            message = errorMessage;
        });
    QTRY_VERIFY_WITH_TIMEOUT(done, 3000);
    QCOMPARE(code, QStringLiteral("ERR_BAD_REQUEST"));
    QVERIFY(message.contains(QStringLiteral("pending_review")));

    // The same outcome correction without the forbidden true flag is accepted.
    changes.remove(QStringLiteral("pending_review"));
    bool corrected = false;
    backend.correctRun(runId, revision, changes, QString::fromUtf8("用户确认通关"))
        ->whenDone(&backend, [&](bool ok, const QVariantMap &reply, const QString &,
                                 const QString &) {
            corrected = ok;
            // CorrectRun is also the acknowledgement: 待复核 clears with it.
            QCOMPARE(reply.value(QStringLiteral("run"))
                         .toMap()
                         .value(QStringLiteral("pending_review"))
                         .toBool(),
                     false);
        });
    QTRY_VERIFY_WITH_TIMEOUT(corrected, 3000);
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    // Keep the developer's real desktop.ini untouched.
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    TtsServiceTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "TtsServiceTests.moc"
