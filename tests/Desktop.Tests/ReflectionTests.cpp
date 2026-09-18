// ---------------------------------------------------------------------------
// tst_reflections - 导随心得 (run reflections).
//
// Covers the whole Desktop side of the feature without a Collector: the two new
// persisted settings, the mock backend's SetRunReflection / GetReflectionSummary
// and its 有心得 filter, the controller's save / clear / summary refresh, the
// 通关后弹出 prompt (including the two cases where it must stay silent) and the
// request builders the committed IPC fixtures are generated from.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "IBackend.h"
#include "IpcBackend.h"
#include "MockBackend.h"
#include "RunListModel.h"

#include <QDateTime>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonObject>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTimer>

namespace {

/// A backend that answers every request with an empty payload and can push one
/// hand-written $defs/LiveEvent, so the prompt rules can be pinned on events
/// this machine can never actually produce.
class EventBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override { return true; }

    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject &payload = {}) override
    {
        Q_UNUSED(payload)
        auto *reply = new mr::BackendReply(QStringLiteral("test"), messageType, this);
        QTimer::singleShot(0, reply, [reply] { reply->succeed({}); });
        return reply;
    }

    void emitEvent(const QVariantMap &event) { Q_EMIT liveEvent(event); }
};

/// $defs/LiveEvent.sequence is monotonic per subscription and the shell drops
/// anything at or below the sequence it has already adopted, so a fixture that
/// reused one number would only ever be seen once.
qint64 g_sequence = 0;

/// One run_updated event carrying \a run, the shape the Collector publishes
/// after a DUTY_RESULT is written.
QVariantMap runUpdatedEvent(const QVariantMap &run)
{
    QVariantMap event;
    // One id per event, as on the wire: the shell drops a repeated event_id as
    // a reconnect replay, so a fixture that reused one would only ever be
    // acted on once.
    event.insert(QStringLiteral("event_id"),
                 QStringLiteral("11111111-2222-4333-8444-%1")
                     .arg(g_sequence + 1, 12, 10, QLatin1Char('0')));
    event.insert(QStringLiteral("event_type"), QStringLiteral("RunUpdated"));
    event.insert(QStringLiteral("kind"), QStringLiteral("run_updated"));
    event.insert(QStringLiteral("emitted_at_utc"),
                 QStringLiteral("2026-09-04T11:00:00.000Z"));
    event.insert(QStringLiteral("sequence"), ++g_sequence);
    event.insert(QStringLiteral("run"), run);
    return event;
}

/// Every fixture above ends on 2026-09-04; a cutoff before that day keeps the
/// prompt's "ended before this session" guard out of tests that are about
/// something else.
QDateTime fixtureCutoff()
{
    return QDateTime::fromString(QStringLiteral("2026-09-01T00:00:00.000Z"),
                                 Qt::ISODateWithMs);
}

QVariantMap completedRun(const QString &runId, const QVariant &reflection = {})
{
    QVariantMap run;
    run.insert(QStringLiteral("run_id"), runId);
    run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    run.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T11:00:00.000Z"));
    run.insert(QStringLiteral("duty_name"), QString::fromUtf8("石卫塔"));
    run.insert(QStringLiteral("reflection"), reflection);
    return run;
}

/// The run the mock offers as the 补录 target, as a plain map.
QVariantMap nextPendingRun(const mr::MockBackend &backend)
{
    return backend.reflectionSummary(3)
        .value(QStringLiteral("next_pending"))
        .toObject()
        .toVariantMap();
}

QVariantMap firstRecentRun(const mr::MockBackend &backend)
{
    return backend.reflectionSummary(3)
        .value(QStringLiteral("recent"))
        .toArray()
        .first()
        .toObject()
        .value(QStringLiteral("run"))
        .toObject()
        .toVariantMap();
}

int summaryCount(const mr::AppController &controller, const char *key)
{
    return controller.reflectionSummary().value(QLatin1String(key)).toInt();
}

} // namespace

class ReflectionTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void settings_roundTripReflectPromptAndUiStyle();
    void mockBackend_summarisesTheSeededReflections();
    void mockBackend_filtersRunsThatHaveAReflection();
    void appController_savesAndClearsTheSelectedRunsReflection();
    void appController_promptsOncePerCompletedRun();
    void appController_staysSilentWhenDisabledOrAlreadyWritten();
    void appController_neverPromptsForRunsThatEndedBeforeTheSession();
    void ipcBackend_buildsBothReflectionRequests();
};

void ReflectionTests::settings_roundTripReflectPromptAndUiStyle()
{
    QFile::remove(mr::AppSettings::filePath());

    {
        mr::AppSettings settings;
        // Defaults: the prompt is on and the workbench (classic) skin is the one
        // that ships (2026-09 redesign, decision 1).
        QCOMPARE(settings.reflectPrompt(), true);
        QCOMPARE(settings.uiStyle(), QStringLiteral("classic"));

        QSignalSpy general(&settings, &mr::AppSettings::generalChanged);
        QSignalSpy appearance(&settings, &mr::AppSettings::appearanceChanged);

        settings.setReflectPrompt(false);
        QCOMPARE(general.count(), 1);
        settings.setReflectPrompt(false); // idempotent
        QCOMPARE(general.count(), 1);

        settings.setUiStyle(QStringLiteral("classic")); // already the default
        QCOMPARE(appearance.count(), 0);
        settings.setUiStyle(QStringLiteral("eorzea"));
        QCOMPARE(appearance.count(), 1);

        // Anything outside the three shipped styles keeps the previous value.
        settings.setUiStyle(QStringLiteral("garlean"));
        settings.setUiStyle(QString());
        QCOMPARE(settings.uiStyle(), QStringLiteral("eorzea"));
        QCOMPARE(appearance.count(), 1);

        // The archive palette is the third style and round-trips like eorzea.
        settings.setUiStyle(QStringLiteral("harendotes"));
        QCOMPARE(settings.uiStyle(), QStringLiteral("harendotes"));
        QCOMPARE(appearance.count(), 2);
        settings.setUiStyle(QStringLiteral("eorzea"));
        QCOMPARE(appearance.count(), 3);
    }

    {
        mr::AppSettings reopened;
        QCOMPARE(reopened.reflectPrompt(), false);
        QCOMPARE(reopened.uiStyle(), QStringLiteral("eorzea"));
    }

    QFile::remove(mr::AppSettings::filePath());
}

void ReflectionTests::mockBackend_summarisesTheSeededReflections()
{
    mr::MockBackend backend;
    const QJsonObject summary = backend.reflectionSummary(3);

    // The prototype seeds exactly six (DOC/表单提交后设计, const RF=).
    QCOMPARE(summary.value(QStringLiteral("reflection_count")).toInt(), 6);
    QVERIFY(summary.value(QStringLiteral("pending_completed_count")).toInt() > 0);

    const QJsonArray recent = summary.value(QStringLiteral("recent")).toArray();
    QCOMPARE(recent.size(), 3);

    QString previous;
    for (const QJsonValue &value : recent) {
        const QJsonObject entry = value.toObject();
        const QJsonObject reflection = entry.value(QStringLiteral("reflection")).toObject();
        QVERIFY(entry.value(QStringLiteral("run")).isObject());
        QVERIFY(!reflection.value(QStringLiteral("text")).toString().isEmpty());
        const QString mood = reflection.value(QStringLiteral("mood")).toString();
        QVERIFY(mood == QLatin1String("good") || mood == QLatin1String("ok")
                || mood == QLatin1String("bad"));

        // Ordered by reflection.updated_at_utc, newest first.
        const QString stamp = reflection.value(QStringLiteral("updated_at_utc")).toString();
        if (!previous.isEmpty())
            QVERIFY2(stamp <= previous, qPrintable(stamp + QStringLiteral(" > ") + previous));
        previous = stamp;
    }

    // next_pending is a COMPLETED run that has no 心得 yet.
    const QJsonObject pending = summary.value(QStringLiteral("next_pending")).toObject();
    QCOMPARE(pending.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
    QVERIFY(!pending.value(QStringLiteral("reflection")).isObject());
    QVERIFY(!pending.value(QStringLiteral("run_id")).toString().isEmpty());

    // recent_limit is honoured and clamped at both ends.
    QCOMPARE(backend.reflectionSummary(0).value(QStringLiteral("recent")).toArray().size(), 0);
    QCOMPARE(backend.reflectionSummary(20).value(QStringLiteral("recent")).toArray().size(), 6);
}

void ReflectionTests::mockBackend_filtersRunsThatHaveAReflection()
{
    mr::MockBackend backend;
    mr::RunListModel model;
    model.setBackend(&backend);
    model.setPageSize(50);

    QVariantMap filter;
    filter.insert(QStringLiteral("with_reflection"), true);
    model.setFilter(filter);
    QTRY_COMPARE_WITH_TIMEOUT(model.total(), 6, 3000);
    for (int row = 0; row < model.rowCount(); ++row) {
        QVERIFY(!model.runAt(row)
                     .value(QStringLiteral("reflection"))
                     .toMap()
                     .value(QStringLiteral("text"))
                     .toString()
                     .isEmpty());
    }

    // Without the chip the whole (non-deleted) dataset is listed again.
    model.setFilter({});
    QTRY_VERIFY_WITH_TIMEOUT(model.total() > 6, 3000);
}

void ReflectionTests::appController_savesAndClearsTheSelectedRunsReflection()
{
    QFile::remove(mr::AppSettings::filePath());
    mr::AppSettings settings;
    mr::MockBackend backend;
    mr::AppController controller(&backend, &settings);
    QTRY_COMPARE_WITH_TIMEOUT(summaryCount(controller, "reflection_count"), 6, 3000);

    QSignalSpy saved(&controller, &mr::AppController::reflectionSaved);

    // 1. 补录 on the run the summary offers: the selection carries the new
    //    reflection and the count goes up by one.
    const QVariantMap pending = nextPendingRun(backend);
    const QString pendingId = pending.value(QStringLiteral("run_id")).toString();
    controller.openHistoryForRun(pending);
    QCOMPARE(controller.currentPage(), 1);
    QCOMPARE(controller.selectedRun().value(QStringLiteral("run_id")).toString(), pendingId);

    controller.saveReflection(pendingId, QStringLiteral("ok"),
                              QString::fromUtf8("  补录：新人不熟机制，讲了一遍。  "));
    QTRY_COMPARE_WITH_TIMEOUT(saved.count(), 1, 3000);
    QCOMPARE(saved.at(0).at(0).toString(), pendingId);
    QCOMPARE(saved.at(0).at(1).toBool(), false);

    const QVariantMap written =
        controller.selectedRun().value(QStringLiteral("reflection")).toMap();
    QCOMPARE(written.value(QStringLiteral("mood")).toString(), QStringLiteral("ok"));
    // The Collector trims; the Desktop must not send the untrimmed string and
    // then display something else than what was stored.
    QCOMPARE(written.value(QStringLiteral("text")).toString(),
             QString::fromUtf8("补录：新人不熟机制，讲了一遍。"));
    QCOMPARE(controller.reflectionSaving(), false);
    QCOMPARE(controller.toastMessage(),
             QString::fromUtf8("笔记已保存 · ")
                 + pending.value(QStringLiteral("duty_name")).toString());
    QTRY_COMPARE_WITH_TIMEOUT(summaryCount(controller, "reflection_count"), 7, 3000);

    // 2. An empty text clears it: cleared = true, the field goes back to null.
    controller.saveReflection(pendingId, QStringLiteral("ok"), QStringLiteral("   "));
    QTRY_COMPARE_WITH_TIMEOUT(saved.count(), 2, 3000);
    QCOMPARE(saved.at(1).at(1).toBool(), true);
    QVERIFY(controller.selectedRun().value(QStringLiteral("reflection")).isNull());
    QCOMPARE(controller.toastMessage(), QString::fromUtf8("已清空该记录的笔记"));
    QTRY_COMPARE_WITH_TIMEOUT(summaryCount(controller, "reflection_count"), 6, 3000);

    // 3. A run that is not in the dataset is refused with the Collector's own
    //    code, and nothing is invented locally.
    QSignalSpy failed(&controller, &mr::AppController::reflectionFailed);
    controller.saveReflection(QStringLiteral("11111111-2222-4333-8444-555555555555"),
                              QStringLiteral("good"), QStringLiteral("x"));
    QTRY_COMPARE_WITH_TIMEOUT(failed.count(), 1, 3000);
    QCOMPARE(failed.at(0).at(0).toString(), QStringLiteral("ERR_NOT_FOUND"));
    QCOMPARE(saved.count(), 2);
    QFile::remove(mr::AppSettings::filePath());
}

void ReflectionTests::appController_promptsOncePerCompletedRun()
{
    QFile::remove(mr::AppSettings::filePath());
    mr::AppSettings settings;
    settings.setReflectPrompt(true);

    mr::MockBackend backend;
    mr::AppController controller(&backend, &settings);
    controller.setReflectionPromptCutoffForTest(fixtureCutoff());
    QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);

    backend.simulateRunTransitions(QStringLiteral("COMPLETED"));
    QCOMPARE(prompts.count(), 1);
    const QVariantMap run = prompts.at(0).at(0).toMap();
    QVERIFY(!run.value(QStringLiteral("run_id")).toString().isEmpty());
    QVERIFY(run.value(QStringLiteral("reflection")).isNull());

    // The same run finishing twice on the wire must not re-open the dialog.
    backend.emitStateChanged(QStringLiteral("COMPLETED"));
    QCOMPARE(prompts.count(), 1);

    // The dashboard's 补录 target is a stored run, never the live one.
    QVERIFY(run.value(QStringLiteral("run_id")).toString()
            != nextPendingRun(backend).value(QStringLiteral("run_id")).toString());
    QFile::remove(mr::AppSettings::filePath());
}

void ReflectionTests::appController_staysSilentWhenDisabledOrAlreadyWritten()
{
    QFile::remove(mr::AppSettings::filePath());

    // 1. 通关后弹出心得窗口 off: no prompt, whatever arrives.
    {
        mr::AppSettings settings;
        settings.setReflectPrompt(false);
        EventBackend backend;
        mr::AppController controller(&backend, &settings);
        controller.setReflectionPromptCutoffForTest(fixtureCutoff());
        QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);

        backend.emitEvent(runUpdatedEvent(completedRun(QStringLiteral("run-off"))));
        QCOMPARE(prompts.count(), 0);
    }

    // 2. Switch on: a finished run with no 心得 prompts, one that already has
    //    one does not, and a run that has not finished does not either.
    {
        mr::AppSettings settings;
        settings.setReflectPrompt(true);
        EventBackend backend;
        mr::AppController controller(&backend, &settings);
        controller.setReflectionPromptCutoffForTest(fixtureCutoff());
        QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);

        backend.emitEvent(runUpdatedEvent(completedRun(QStringLiteral("run-plain"))));
        QCOMPARE(prompts.count(), 1);

        QVariantMap reflection;
        reflection.insert(QStringLiteral("mood"), QStringLiteral("good"));
        reflection.insert(QStringLiteral("text"), QString::fromUtf8("已经写过了。"));
        reflection.insert(QStringLiteral("created_at_utc"),
                          QStringLiteral("2026-09-04T11:05:00.000Z"));
        reflection.insert(QStringLiteral("updated_at_utc"),
                          QStringLiteral("2026-09-04T11:05:00.000Z"));
        backend.emitEvent(
            runUpdatedEvent(completedRun(QStringLiteral("run-written"), reflection)));
        QCOMPARE(prompts.count(), 1);

        QVariantMap running = completedRun(QStringLiteral("run-running"));
        running.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
        running.insert(QStringLiteral("ended_at_utc"), QVariant());
        backend.emitEvent(runUpdatedEvent(running));
        QCOMPARE(prompts.count(), 1);
    }

    QFile::remove(mr::AppSettings::filePath());
}

void ReflectionTests::appController_neverPromptsForRunsThatEndedBeforeTheSession()
{
    // The Collector replays its last events to every client that connects, so
    // a run that finished hours ago - or one a test harness wrote - reaches a
    // freshly started Desktop as a live run_finished. It is history, not 刚刚完成.
    QFile::remove(mr::AppSettings::filePath());
    mr::AppSettings settings;
    settings.setReflectPrompt(true);
    EventBackend backend;

    // 1. Default cutoff is construction time: the 2026-09-04 fixture is old.
    {
        mr::AppController controller(&backend, &settings);
        QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);
        backend.emitEvent(runUpdatedEvent(completedRun(QStringLiteral("run-replayed"))));
        QCOMPARE(prompts.count(), 0);

        // A run that ends after the session started is the real thing.
        QVariantMap fresh = completedRun(QStringLiteral("run-fresh"));
        fresh.insert(QStringLiteral("ended_at_utc"),
                     QDateTime::currentDateTimeUtc().addSecs(1).toString(Qt::ISODateWithMs));
        backend.emitEvent(runUpdatedEvent(fresh));
        QCOMPARE(prompts.count(), 1);
        QCOMPARE(prompts.at(0).at(0).toMap().value(QStringLiteral("run_id")).toString(),
                 QStringLiteral("run-fresh"));
    }

    // 2. The same replayed run through the state-change path stays silent too.
    {
        mr::AppController controller(&backend, &settings);
        QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);
        QVariantMap event;
        event.insert(QStringLiteral("kind"), QStringLiteral("run_state_changed"));
        event.insert(QStringLiteral("state"), QStringLiteral("COMPLETED"));
        event.insert(QStringLiteral("run"), completedRun(QStringLiteral("run-replayed-state")));
        backend.emitEvent(event);
        QCOMPARE(prompts.count(), 0);
    }

    // 3. Moving the cutoff behind the fixture lets it through: the guard is
    //    the timestamp comparison, nothing else about the run.
    {
        mr::AppController controller(&backend, &settings);
        controller.setReflectionPromptCutoffForTest(fixtureCutoff());
        QSignalSpy prompts(&controller, &mr::AppController::reflectionPromptRequested);
        backend.emitEvent(runUpdatedEvent(completedRun(QStringLiteral("run-replayed"))));
        QCOMPARE(prompts.count(), 1);
    }
    QFile::remove(mr::AppSettings::filePath());
}

void ReflectionTests::ipcBackend_buildsBothReflectionRequests()
{
    QJsonObject args;
    args.insert(QStringLiteral("run_id"),
                QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c"));
    args.insert(QStringLiteral("mood"), QStringLiteral("good"));
    args.insert(QStringLiteral("text"), QString::fromUtf8("一次过。"));

    const QJsonObject set =
        mr::IpcBackend::buildRequestForTest(QStringLiteral("SetRunReflection"), args);
    QCOMPARE(set.value(QStringLiteral("message_type")).toString(),
             QStringLiteral("SetRunReflection"));
    const QJsonObject setPayload = set.value(QStringLiteral("payload")).toObject();
    QCOMPARE(setPayload.keys(),
             QStringList({QStringLiteral("mood"), QStringLiteral("run_id"),
                          QStringLiteral("text")}));
    QCOMPARE(setPayload.value(QStringLiteral("run_id")).toString(),
             args.value(QStringLiteral("run_id")).toString());

    // An empty text is a deletion and must survive into the payload.
    args.insert(QStringLiteral("text"), QString());
    const QJsonObject cleared =
        mr::IpcBackend::buildRequestForTest(QStringLiteral("SetRunReflection"), args);
    QVERIFY(cleared.value(QStringLiteral("payload")).toObject().contains(QStringLiteral("text")));

    QJsonObject summaryArgs;
    summaryArgs.insert(QStringLiteral("recent_limit"), 3);
    const QJsonObject summary =
        mr::IpcBackend::buildRequestForTest(QStringLiteral("GetReflectionSummary"),
                                            summaryArgs);
    const QJsonObject summaryPayload = summary.value(QStringLiteral("payload")).toObject();
    QCOMPARE(summaryPayload.keys(), QStringList({QStringLiteral("recent_limit")}));
    QCOMPARE(summaryPayload.value(QStringLiteral("recent_limit")).toInt(), 3);

    // Out-of-range values are clamped to $defs/GetReflectionSummaryRequest.
    summaryArgs.insert(QStringLiteral("recent_limit"), 99);
    QCOMPARE(mr::IpcBackend::buildRequestForTest(QStringLiteral("GetReflectionSummary"),
                                                 summaryArgs)
                 .value(QStringLiteral("payload"))
                 .toObject()
                 .value(QStringLiteral("recent_limit"))
                 .toInt(),
             20);
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    // Keep the developer's real desktop.ini untouched.
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    ReflectionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReflectionTests.moc"
