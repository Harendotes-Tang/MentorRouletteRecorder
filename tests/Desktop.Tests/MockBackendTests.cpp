// ---------------------------------------------------------------------------
// tst_mockbackend - the mock speaks the contract the Collector speaks.
//
// The screenshots, the QML smoke tests and most controller tests run on
// MockBackend, so every place it answers differently from the Collector is a
// place those tests can pass while the shipping build misbehaves (review
// OJ-6 / OX-6, DT1-X1, DT2-X4, DT3-X2). Each test below names the Collector
// code it mirrors.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "TtsService.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QHash>
#include <QJsonArray>
#include <QJsonObject>
#include <QScopeGuard>
#include <QSet>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>
#include <QTextToSpeech>

namespace {

struct Answer {
    bool done = false;
    bool ok = false;
    QJsonObject payload;
    QString code;
    QString message;
};

/// One request, waited for.
Answer ask(mr::IBackend &backend, const QString &type, const QJsonObject &payload = {})
{
    Answer answer;
    backend.request(type, payload)->whenDone(&backend,
        [&answer](bool ok, const QVariantMap &result, const QString &code, const QString &message) {
            answer.done = true;
            answer.ok = ok;
            answer.payload = QJsonObject::fromVariantMap(result);
            answer.code = code;
            answer.message = message;
        });
    if (!QTest::qWaitFor([&answer] { return answer.done; }, 3000))
        answer.code = QStringLiteral("TEST_NO_REPLY");
    return answer;
}

QJsonObject autoRun(const QString &id, const QString &result, bool entered)
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("mentor_roulette_id"), 9},
            {QStringLiteral("result"), result},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"),
             entered ? QJsonValue(QStringLiteral("2026-09-01T12:01:00.000Z"))
                     : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")},
            {QStringLiteral("duration_ms"), entered ? QJsonValue(540000.0) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("contributes_to_goal"), true},
            {QStringLiteral("manually_created"), false},
            {QStringLiteral("manually_corrected"), false},
            {QStringLiteral("soft_deleted"), false},
            {QStringLiteral("pending_review"), false},
            {QStringLiteral("created_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")}};
}

/// A hand-entered run the tests can correct: 20:00 matched, 20:01 entered,
/// 20:20 ended, so every time-order rule has room on both sides.
QString createManualRun(mr::MockBackend &backend)
{
    const Answer created = ask(backend, QStringLiteral("CreateManualRun"), {
        {QStringLiteral("content_id"), 4},
        {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-02T12:00:00.000Z")},
        {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-02T12:01:00.000Z")},
        {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-02T12:20:00.000Z")},
        {QStringLiteral("duration_ms"), 1140000.0},
        {QStringLiteral("result"), QStringLiteral("COMPLETED")},
        {QStringLiteral("reason"), QString::fromUtf8("补录")}});
    return created.ok ? created.payload.value(QStringLiteral("run_id")).toString() : QString();
}

Answer correct(mr::MockBackend &backend, const QString &runId, const QJsonObject &changes)
{
    const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                            {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("text"), QString()}}},
                             {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
    int revision = 0;
    for (const QJsonValue &item : page.payload.value(QStringLiteral("items")).toArray()) {
        if (item.toObject().value(QStringLiteral("run_id")).toString() == runId)
            revision = item.toObject().value(QStringLiteral("revision")).toInt();
    }
    return ask(backend, QStringLiteral("CorrectRun"),
               {{QStringLiteral("run_id"), runId},
                {QStringLiteral("expected_revision"), revision},
                {QStringLiteral("changes"), changes},
                {QStringLiteral("reason"), QString::fromUtf8("核对")}});
}

} // namespace

class MockBackendTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    // DT2-X4: the dataset is anchored to a fixed date for reproducible
    // screenshots, but a live event is something happening now. AppController
    // ignores state events emitted before it started (review OH-1) and capture
    // events older than its last answer, so a fixed stamp silenced the mock.
    void liveEventsCarryTheTimeTheyAreEmitted()
    {
        mr::MockBackend backend;
        QSignalSpy events(&backend, &mr::IBackend::liveEvent);
        const QDateTime before = QDateTime::currentDateTimeUtc().addSecs(-1);
        backend.emitStateChanged(QStringLiteral("MENTOR_MATCHED"));
        QCOMPARE(events.size(), 1);
        const QDateTime emitted = QDateTime::fromString(
            events.at(0).at(0).toMap().value(QStringLiteral("emitted_at_utc")).toString(),
            Qt::ISODateWithMs);
        QVERIFY(emitted.isValid());
        QVERIFY2(emitted >= before && emitted <= QDateTime::currentDateTimeUtc().addSecs(1),
                 qPrintable(emitted.toString(Qt::ISODateWithMs)));
    }

    void aControllerStartedNowAnnouncesTheMocksMatchAndEntry()
    {
        mr::AppSettings settings;
        const bool wasEnabled = settings.ttsEnabled();
        const auto restore = qScopeGuard([&settings, wasEnabled] { settings.setTtsEnabled(wasEnabled); });
        settings.setTtsEnabled(true);
        mr::MockBackend backend;
        // Qt's silent mock engine, never the machine's own voice (review S33-10).
        mr::AppController controller(&backend, &settings, nullptr, nullptr,
                                     mr::TtsService::EngineMode::Mock);
        const auto *engine = controller.tts()->findChild<QTextToSpeech *>();
        if (!engine || !controller.tts()->isAvailable())
            QSKIP("Qt's mock speech engine plugin is not installed.");
        QCOMPARE(engine->engine(), QStringLiteral("mock"));
        QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);
        backend.simulateRunTransitions(QString());
        QCOMPARE(spoke.size(), 2);
        QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("matched"));
        QCOMPARE(spoke.at(1).at(0).toString(), QStringLiteral("entered"));
    }

    // OX-6: CaptureController / CaptureWire. While several clients wait for a
    // choice nothing is locked: no capture runs, and the build and region of
    // no client are known, so no profile is matched (GameProcessDetection.NotRunning).
    void anUnchosenClientIsAStoppedCaptureWithoutAProfile()
    {
        mr::MockBackend backend;
        backend.setRecordingFixture(QStringLiteral("multiple"));
        Answer status = ask(backend, QStringLiteral("GetCaptureStatus"));
        QVERIFY(status.ok);
        QCOMPARE(status.payload.value(QStringLiteral("state")).toString(), QStringLiteral("STOPPED"));
        QCOMPARE(status.payload.value(QStringLiteral("profile_status")).toString(), QStringLiteral("NONE"));
        QCOMPARE(status.payload.value(QStringLiteral("region")).toString(), QStringLiteral("UNKNOWN"));
        QVERIFY(status.payload.value(QStringLiteral("game_build")).isNull());
        QVERIFY(status.payload.value(QStringLiteral("profile_id")).isNull());
        QVERIFY(status.payload.value(QStringLiteral("capture_session_id")).isNull());
        QVERIFY(!status.payload.value(QStringLiteral("profile_matches_build")).toBool());
        QVERIFY(status.payload.value(QStringLiteral("game_selection_required")).toBool());

        const QJsonObject choice = status.payload.value(QStringLiteral("game_processes"))
                                       .toArray().first().toObject();
        QVERIFY(ask(backend, QStringLiteral("SelectGameProcess"),
                    {{QStringLiteral("process_id"), choice.value(QStringLiteral("process_id"))},
                     {QStringLiteral("selection_token"), choice.value(QStringLiteral("selection_token"))}})
                    .ok);
        status = ask(backend, QStringLiteral("GetCaptureStatus"));
        QCOMPARE(status.payload.value(QStringLiteral("state")).toString(), QStringLiteral("RUNNING"));
        QCOMPARE(status.payload.value(QStringLiteral("profile_status")).toString(), QStringLiteral("VERIFIED"));
        QCOMPARE(status.payload.value(QStringLiteral("region")).toString(), QStringLiteral("CN"));
    }

    // DT1-X1: MessageDispatcher.GetRunRevisions + RunRevisionRepository.ListForRun:
    // oldest first, page/page_size (default 1/50, at most 200), the page asked
    // for, and an unknown run is ERR_NOT_FOUND.
    void revisionsArePagedOldestFirst()
    {
        mr::MockBackend backend;
        const QString runId = createManualRun(backend);
        QVERIFY(!runId.isEmpty());
        QVERIFY(correct(backend, runId, {{QStringLiteral("note"), QStringLiteral("a")}}).ok);
        QVERIFY(correct(backend, runId, {{QStringLiteral("note"), QStringLiteral("b")}}).ok);

        const Answer second = ask(backend, QStringLiteral("GetRunRevisions"),
                                  {{QStringLiteral("run_id"), runId},
                                   {QStringLiteral("page"), 2},
                                   {QStringLiteral("page_size"), 1}});
        QVERIFY(second.ok);
        const QJsonArray items = second.payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(items.size(), 1);
        QCOMPARE(items.first().toObject().value(QStringLiteral("revision")).toInt(), 2);
        const QJsonObject pageInfo = second.payload.value(QStringLiteral("page_info")).toObject();
        QCOMPARE(pageInfo.value(QStringLiteral("page")).toInt(), 2);
        QCOMPARE(pageInfo.value(QStringLiteral("page_size")).toInt(), 1);
        QCOMPARE(pageInfo.value(QStringLiteral("total")).toInt(), 3);

        const Answer whole = ask(backend, QStringLiteral("GetRunRevisions"),
                                 {{QStringLiteral("run_id"), runId}});
        QCOMPARE(whole.payload.value(QStringLiteral("page_info")).toObject()
                     .value(QStringLiteral("page_size")).toInt(), 50);
        QCOMPARE(whole.payload.value(QStringLiteral("items")).toArray().size(), 3);

        const Answer beyond = ask(backend, QStringLiteral("GetRunRevisions"),
                                  {{QStringLiteral("run_id"), runId}, {QStringLiteral("page"), 9}});
        QVERIFY(beyond.ok);
        QVERIFY(beyond.payload.value(QStringLiteral("items")).toArray().isEmpty());
        QCOMPARE(beyond.payload.value(QStringLiteral("page_info")).toObject()
                     .value(QStringLiteral("page")).toInt(), 9);

        QCOMPARE(ask(backend, QStringLiteral("GetRunRevisions"),
                     {{QStringLiteral("run_id"), runId}, {QStringLiteral("page_size"), 201}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(ask(backend, QStringLiteral("GetRunRevisions"),
                     {{QStringLiteral("run_id"), QStringLiteral("00000000-0000-4000-8000-000000000000")}}).code,
                 QStringLiteral("ERR_NOT_FOUND"));
    }

    // DT3-X2: Domain/Events/SemanticEvent.cs names, and the parsed fields
    // SemanticEventProcessor.DetailJson stores for them. Every row has a name a
    // player can read; none reads 其他事件.
    void runEventsUseTheCollectorsEventTypes()
    {
        mr::MockBackend backend;
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{QStringLiteral("filter"), QJsonObject()},
                                 {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        QSet<QString> seen;
        for (const QJsonValue &item : page.payload.value(QStringLiteral("items")).toArray()) {
            const QJsonObject run = item.toObject();
            const Answer events = ask(backend, QStringLiteral("GetRunEvents"),
                                      {{QStringLiteral("run_id"), run.value(QStringLiteral("run_id"))}});
            QVERIFY(events.ok);
            for (const QJsonValue &value : events.payload.value(QStringLiteral("events")).toArray()) {
                const QJsonObject event = value.toObject();
                const QString type = event.value(QStringLiteral("event_type")).toString();
                seen.insert(type);
                QVERIFY2(mr::Formatters::runEventLabel(type) != QString::fromUtf8("其他事件"),
                         qPrintable(type));
                const QJsonValue parsed = event.value(QStringLiteral("parsed"));
                if (type == QLatin1String("CONTENT_FINDER_POP")) {
                    QCOMPARE(parsed.toObject().value(QStringLiteral("roulette_id")),
                             run.value(QStringLiteral("mentor_roulette_id")));
                } else if (type == QLatin1String("ZONE_INITIALIZATION")) {
                    QVERIFY(parsed.toObject().contains(QStringLiteral("territory_id")));
                } else if (type == QLatin1String("DUTY_RESULT")) {
                    QCOMPARE(run.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
                    QVERIFY(parsed.isNull());
                } else if (type == QLatin1String("CONNECTION_LOST")) {
                    QCOMPARE(event.value(QStringLiteral("parser_status")).toString(),
                             QStringLiteral("SYNTHETIC"));
                    QVERIFY(event.value(QStringLiteral("opcode")).isNull());
                }
            }
        }
        QVERIFY(seen.contains(QStringLiteral("CONTENT_FINDER_POP")));
        QVERIFY(seen.contains(QStringLiteral("ZONE_INITIALIZATION")));
        QVERIFY(seen.contains(QStringLiteral("DUTY_RESULT")));
    }

    // OJ-6: RunMutationService.UndoRevision commits ChangeKind.Correct; the
    // contract's change_kind has no UNDO.
    void anUndoIsRecordedAsACorrection()
    {
        mr::MockBackend backend;
        const QString runId = createManualRun(backend);
        QVERIFY(correct(backend, runId, {{QStringLiteral("note"), QStringLiteral("x")}}).ok);
        const Answer undone = ask(backend, QStringLiteral("UndoRevision"),
                                  {{QStringLiteral("run_id"), runId},
                                   {QStringLiteral("expected_revision"), 2},
                                   {QStringLiteral("reason"), QString::fromUtf8("撤销")}});
        QVERIFY(undone.ok);
        const QJsonArray revisions = ask(backend, QStringLiteral("GetRunRevisions"),
                                         {{QStringLiteral("run_id"), runId}})
                                         .payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(revisions.size(), 3);
        QCOMPARE(revisions.last().toObject().value(QStringLiteral("change_kind")).toString(),
                 QStringLiteral("CORRECT"));
    }

    // OJ-6: RunFilterSql - pending_review false keeps only the runs that are
    // not flagged; omitted is no constraint (schema RunFilter.pending_review).
    void pendingReviewFalseKeepsOnlyRunsThatAreNotFlagged()
    {
        mr::MockBackend backend;
        const auto query = [&backend](const QJsonObject &filter) {
            return ask(backend, QStringLiteral("QueryRuns"),
                       {{QStringLiteral("filter"), filter},
                        {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        };
        const int all = query({}).payload.value(QStringLiteral("page_info")).toObject()
                            .value(QStringLiteral("total")).toInt();
        const Answer flagged = query({{QStringLiteral("pending_review"), true}});
        const Answer clear = query({{QStringLiteral("pending_review"), false}});
        const int flaggedTotal = flagged.payload.value(QStringLiteral("page_info")).toObject()
                                     .value(QStringLiteral("total")).toInt();
        QVERIFY(flaggedTotal > 0);
        QCOMPARE(clear.payload.value(QStringLiteral("page_info")).toObject()
                     .value(QStringLiteral("total")).toInt(), all - flaggedTotal);
        for (const QJsonValue &item : clear.payload.value(QStringLiteral("items")).toArray())
            QVERIFY(!item.toObject().value(QStringLiteral("pending_review")).toBool());
    }

    // OJ-6: RunRepository.Query - NULLs last in both directions, run_id breaks ties.
    void anAscendingSortKeepsMissingValuesLast()
    {
        mr::MockBackend backend;
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{QStringLiteral("filter"), QJsonObject()},
                                 {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200},
                                 {QStringLiteral("sort"), QJsonObject{
                                      {QStringLiteral("field"), QStringLiteral("ended_at_utc")},
                                      {QStringLiteral("direction"), QStringLiteral("asc")}}}});
        const QJsonArray items = page.payload.value(QStringLiteral("items")).toArray();
        QVERIFY(items.size() > 2);
        bool sawNull = false;
        QString previous;
        for (const QJsonValue &item : items) {
            const QJsonValue ended = item.toObject().value(QStringLiteral("ended_at_utc"));
            if (ended.isNull()) {
                sawNull = true;
                continue;
            }
            QVERIFY2(!sawNull, "a run with an end time sorted after one without");
            QVERIFY(ended.toString() >= previous);
            previous = ended.toString();
        }
        QVERIFY(sawNull);
    }

    // OJ-6: StatisticsRepository.BuildDashboard - leave_rate counts
    // LEFT_OR_ABANDONED among the attempts only.
    void theLeaveRateCountsOnlyRunsThatEnteredADuty()
    {
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{
            autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("COMPLETED"), true),
            autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"), QStringLiteral("LEFT_OR_ABANDONED"), true),
            autoRun(QStringLiteral("33333333-3333-4333-8333-333333333333"), QStringLiteral("LEFT_OR_ABANDONED"), false)});
        const QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("attempt_count")).toInt(), 2);
        QCOMPARE(stats.value(QStringLiteral("leave_rate")).toDouble(), 0.5);
    }

    // OJ-6: RunMutationService.ApplyChangeSet moves territory_id with the
    // content_id, and RunMutationRules.ValidateFinalValue refuses a record that
    // cannot exist and derives the duration when an end point moved.
    void aCorrectionIsCheckedAsAWholeLikeTheCollectorDoes()
    {
        mr::MockBackend backend;
        const QString runId = createManualRun(backend);
        const QVariantMap duty = mr::DutyCatalog::shared()->lookup(17);
        QVERIFY(duty.value(QStringLiteral("territory_id")).isValid());

        Answer moved = correct(backend, runId, {{QStringLiteral("content_id"), 17}});
        QVERIFY(moved.ok);
        const QJsonObject run = moved.payload.value(QStringLiteral("run")).toObject();
        QCOMPARE(run.value(QStringLiteral("territory_id")).toInteger(),
                 duty.value(QStringLiteral("territory_id")).toLongLong());

        QCOMPARE(correct(backend, runId, {{QStringLiteral("entered_at_utc"),
                                           QStringLiteral("2026-09-02T12:30:00.000Z")}}).code,
                 QStringLiteral("ERR_TIME_ORDER"));
        QCOMPARE(correct(backend, runId, {{QStringLiteral("matched_at_utc"),
                                           QStringLiteral("2026-09-02T12:05:00.000Z")}}).code,
                 QStringLiteral("ERR_TIME_ORDER"));
        QCOMPARE(correct(backend, runId, {{QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null)}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(correct(backend, runId, {{QStringLiteral("result"), QStringLiteral("LEFT_OR_ABANDONED")},
                                          {QStringLiteral("entered_at_utc"), QJsonValue(QJsonValue::Null)}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(correct(backend, runId, {{QStringLiteral("duration_ms"), -1.0}}).code,
                 QStringLiteral("ERR_NEGATIVE_DURATION"));

        const Answer shorter = correct(backend, runId, {{QStringLiteral("ended_at_utc"),
                                                         QStringLiteral("2026-09-02T12:11:00.000Z")}});
        QVERIFY(shorter.ok);
        QCOMPARE(shorter.payload.value(QStringLiteral("run")).toObject()
                     .value(QStringLiteral("duration_ms")).toDouble(), 600000.0);
    }

    // DT4-X4: RunFilterSql.Build(forStatistics) - an automatic run still in flight
    // (UNKNOWN, no end, no review flag) has no outcome yet and takes part in no
    // statistic. One crash recovery handed to the player (pending_review) is over and
    // does, and so does a hand-entered one. The listing shows all of them.
    void statisticsLeaveOutAnAutomaticRunStillInFlight()
    {
        mr::MockBackend backend;
        const auto open = [](const QString &id) {
            QJsonObject run = autoRun(id, QStringLiteral("UNKNOWN"), true);
            run.insert(QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null));
            run.insert(QStringLiteral("duration_ms"), QJsonValue(QJsonValue::Null));
            return run;
        };
        QJsonObject recovered = open(QStringLiteral("55555555-5555-4555-8555-555555555555"));
        recovered.insert(QStringLiteral("pending_review"), true);
        QJsonObject manual = open(QStringLiteral("66666666-6666-4666-8666-666666666666"));
        manual.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
        manual.insert(QStringLiteral("mentor_roulette_id"), QJsonValue(QJsonValue::Null));
        backend.resetRuns(QJsonArray{
            autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("COMPLETED"), true),
            open(QStringLiteral("44444444-4444-4444-8444-444444444444")), recovered, manual});

        const QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("attempt_count")).toInt(), 3);
        QCOMPARE(stats.value(QStringLiteral("completed_count")).toInt(), 1);
        QCOMPARE(stats.value(QStringLiteral("unfinished_pending_review")).toInt(), 1);
        int unknown = -1;
        for (const QJsonValue &bucket : backend.resultStats({}).value(QStringLiteral("buckets")).toArray()) {
            if (bucket.toObject().value(QStringLiteral("result")).toString() == QLatin1String("UNKNOWN"))
                unknown = bucket.toObject().value(QStringLiteral("count")).toInt();
        }
        QCOMPARE(unknown, 2);
        const auto attempts = [](const QJsonArray &rows) {
            int total = 0;
            for (const QJsonValue &row : rows)
                total += row.toObject().value(QStringLiteral("attempt_count")).toInt();
            return total;
        };
        QCOMPARE(attempts(backend.dungeonStats({})), 3);
        QCOMPARE(attempts(backend.jobStats({})), 3);

        const Answer listed = ask(backend, QStringLiteral("QueryRuns"),
                                  {{QStringLiteral("filter"), QJsonObject()},
                                   {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        QCOMPARE(listed.payload.value(QStringLiteral("page_info")).toObject()
                     .value(QStringLiteral("total")).toInt(), 4);
    }

    // DT4-X4: SemanticEventProcessor.CreateRun / AppendPendingRevisions - a run capture
    // recorded opens its chain with a SYSTEM CREATE_AUTO revision describing the row as
    // it was created at the match (DescribeCreation): no job, no entry, no end, result
    // UNKNOWN; whatever was learned or corrected later is not in it. A hand-entered run
    // opens with the player's CREATE_MANUAL instead. The chain ends at the run's revision.
    void everyRunOpensItsRevisionChainWithItsCreation()
    {
        mr::MockBackend backend;
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{QStringLiteral("filter"),
                                  QJsonObject{{QStringLiteral("include_deleted"), true}}},
                                 {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        const QJsonArray runs = page.payload.value(QStringLiteral("items")).toArray();
        QVERIFY(runs.size() > 50);
        int automatic = 0;
        int manual = 0;
        for (const QJsonValue &item : runs) {
            const QJsonObject run = item.toObject();
            const QString runId = run.value(QStringLiteral("run_id")).toString();
            const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                         {{QStringLiteral("run_id"), runId}})
                                         .payload.value(QStringLiteral("items")).toArray();
            QVERIFY2(!chain.isEmpty(), qPrintable(runId));
            const QJsonObject first = chain.first().toObject();
            QCOMPARE(first.value(QStringLiteral("revision")).toInt(), 1);
            QCOMPARE(chain.last().toObject().value(QStringLiteral("revision")).toInt(),
                     run.value(QStringLiteral("revision")).toInt());
            if (run.value(QStringLiteral("source")).toString() == QLatin1String("MANUAL")) {
                ++manual;
                QCOMPARE(first.value(QStringLiteral("change_kind")).toString(), QStringLiteral("CREATE_MANUAL"));
                QCOMPARE(first.value(QStringLiteral("actor")).toString(), QStringLiteral("USER"));
                continue;
            }
            ++automatic;
            QCOMPARE(first.value(QStringLiteral("change_kind")).toString(), QStringLiteral("CREATE_AUTO"));
            QCOMPARE(first.value(QStringLiteral("actor")).toString(), QStringLiteral("SYSTEM"));
            QCOMPARE(first.value(QStringLiteral("changed_at_utc")), run.value(QStringLiteral("matched_at_utc")));
            QHash<QString, QJsonValue> created;
            for (const QJsonValue &value : first.value(QStringLiteral("changes")).toArray()) {
                const QJsonObject change = value.toObject();
                QVERIFY(change.value(QStringLiteral("old_value")).isNull());
                created.insert(change.value(QStringLiteral("field")).toString(),
                               change.value(QStringLiteral("new_value")));
            }
            QCOMPARE(created.value(QStringLiteral("run_id")), run.value(QStringLiteral("run_id")));
            QCOMPARE(created.value(QStringLiteral("mentor_roulette_id")), run.value(QStringLiteral("mentor_roulette_id")));
            QCOMPARE(created.value(QStringLiteral("matched_at_utc")), run.value(QStringLiteral("matched_at_utc")));
            QCOMPARE(created.value(QStringLiteral("source")).toString(), QStringLiteral("AUTO_NETWORK"));
            QCOMPARE(created.value(QStringLiteral("result")).toString(), QStringLiteral("UNKNOWN"));
            QVERIFY(created.contains(QStringLiteral("entered_at_utc")));
            QVERIFY(created.value(QStringLiteral("entered_at_utc")).isNull());
            QVERIFY(created.value(QStringLiteral("job_id")).isNull());
            QCOMPARE(created.value(QStringLiteral("soft_deleted")), QJsonValue(false));
        }
        QVERIFY(automatic > 50);
        QVERIFY(manual > 0);
    }

    // DT4-X4: RunMutationService.CreateManualRun - the duty catalogue supplies the zone,
    // and the duty's name and category the request left out; the job catalogue the job's
    // name; an omitted duration is derived from the two times, an explicit null is kept;
    // and RunMutationRules.ValidateFinalValue refuses a record that cannot exist, storing
    // nothing.
    void aManualRunIsCheckedAndCompletedLikeTheCollectorDoes()
    {
        mr::MockBackend backend;
        const QVariantMap duty = mr::DutyCatalog::shared()->lookup(17);
        QVERIFY(duty.value(QStringLiteral("territory_id")).isValid());
        const QJsonObject request{
            {QStringLiteral("content_id"), 17},
            {QStringLiteral("job_id"), 24},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-02T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-02T12:01:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-02T12:20:00.000Z")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("reason"), QString::fromUtf8("补录")}};
        const auto total = [&backend] {
            return ask(backend, QStringLiteral("QueryRuns"),
                       {{QStringLiteral("filter"), QJsonObject()},
                        {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 1}})
                .payload.value(QStringLiteral("page_info")).toObject()
                .value(QStringLiteral("total")).toInt();
        };
        const int before = total();

        const Answer created = ask(backend, QStringLiteral("CreateManualRun"), request);
        QVERIFY(created.ok);
        const QJsonObject run = created.payload.value(QStringLiteral("run")).toObject();
        QCOMPARE(run.value(QStringLiteral("territory_id")).toInteger(),
                 duty.value(QStringLiteral("territory_id")).toLongLong());
        QCOMPARE(run.value(QStringLiteral("duty_name")).toString(),
                 duty.value(QStringLiteral("duty_name")).toString());
        QCOMPARE(run.value(QStringLiteral("duty_category")).toString(),
                 duty.value(QStringLiteral("duty_category")).toString());
        QCOMPARE(run.value(QStringLiteral("job_name")).toString(), mr::JobCatalog().jobName(24));
        QVERIFY(run.value(QStringLiteral("job_name")).toString() != QString::fromUtf8("未知"));
        QCOMPARE(run.value(QStringLiteral("duration_ms")).toDouble(), 19 * 60 * 1000.0);

        QJsonObject named = request;
        named.insert(QStringLiteral("duty_name"), QString::fromUtf8("玩家自己写的名字"));
        named.insert(QStringLiteral("duration_ms"), QJsonValue(QJsonValue::Null));
        const QJsonObject kept = ask(backend, QStringLiteral("CreateManualRun"), named)
                                     .payload.value(QStringLiteral("run")).toObject();
        QCOMPARE(kept.value(QStringLiteral("duty_name")).toString(), QString::fromUtf8("玩家自己写的名字"));
        QVERIFY(kept.value(QStringLiteral("duration_ms")).isNull());
        QCOMPARE(total(), before + 2);

        const auto refused = [&backend, &request](const QJsonObject &changes) {
            QJsonObject payload = request;
            for (auto it = changes.constBegin(); it != changes.constEnd(); ++it)
                payload.insert(it.key(), it.value());
            return ask(backend, QStringLiteral("CreateManualRun"), payload).code;
        };
        QCOMPARE(refused({{QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-02T12:30:00.000Z")}}),
                 QStringLiteral("ERR_TIME_ORDER"));
        QCOMPARE(refused({{QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-02T12:05:00.000Z")}}),
                 QStringLiteral("ERR_TIME_ORDER"));
        QCOMPARE(refused({{QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null)}}),
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(refused({{QStringLiteral("result"), QStringLiteral("LEFT_OR_ABANDONED")},
                          {QStringLiteral("entered_at_utc"), QJsonValue(QJsonValue::Null)}}),
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(refused({{QStringLiteral("duration_ms"), -1.0}}), QStringLiteral("ERR_NEGATIVE_DURATION"));
        QCOMPARE(total(), before + 2);
    }

    // CS7-D4: RunMutationService.UpdateAchievementBaseline - only a changed baseline moves
    // its effective time. A save that leaves the count as stored (a goal-only edit, or the
    // same number entered again) keeps the stored time, whatever time the request carries,
    // and the reply names the time actually stored. The mock answered "now" every time.
    void aBaselineSaveMovesTheEffectiveTimeOnlyWhenTheBaselineChanges()
    {
        mr::MockBackend backend;
        backend.setAchievement(2000, 1374);
        const auto save = [&backend](int goal, int baseline, const QString &at) {
            return ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                       {{QStringLiteral("goal_count"), goal},
                        {QStringLiteral("baseline_completed_count"), baseline},
                        {QStringLiteral("baseline_effective_at"), at},
                        {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        };
        const QString first = QStringLiteral("2026-10-01T08:00:00.000Z");
        const QString second = QStringLiteral("2026-10-02T09:30:00.000Z");

        const Answer goalOnly = save(1800, 1374, first);
        QVERIFY(goalOnly.ok);
        QCOMPARE(goalOnly.payload.value(QStringLiteral("goal_count")).toInt(), 1800);
        const QString kept = goalOnly.payload.value(QStringLiteral("baseline_effective_at")).toString();
        QVERIFY(!kept.isEmpty());
        QVERIFY2(kept != first, qPrintable(kept));

        const Answer changed = save(1800, 1400, first);
        QVERIFY(changed.ok);
        QCOMPARE(changed.payload.value(QStringLiteral("baseline_effective_at")).toString(), first);

        const Answer sameAgain = save(2000, 1400, second);
        QVERIFY(sameAgain.ok);
        QCOMPARE(sameAgain.payload.value(QStringLiteral("baseline_effective_at")).toString(), first);
        QCOMPARE(sameAgain.payload.value(QStringLiteral("goal_count")).toInt(), 2000);
    }

    // S33-3 (mock parity): RunMutationService.UpdateAchievementBaseline - a save with the
    // stored goal and the stored baseline writes nothing. It answers what is stored - the
    // update time and the effective time - under the id of the history entry that stored
    // them, is no replay, and announces nothing. A real change gets an entry and a time of
    // its own, and the next save that changes nothing answers those. The mock answered a
    // fresh id and the current time every time.
    void aBaselineSaveThatChangesNothingAnswersWhatIsStored()
    {
        mr::MockBackend backend;
        QSignalSpy events(&backend, &mr::IBackend::liveEvent);
        const auto save = [&backend](int goal, int baseline, const QString &at) {
            return ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                       {{QStringLiteral("goal_count"), goal},
                        {QStringLiteral("baseline_completed_count"), baseline},
                        {QStringLiteral("baseline_effective_at"), at},
                        {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        };
        const auto field = [](const Answer &answer, const char *key) {
            return answer.payload.value(QLatin1String(key)).toString();
        };

        // The sample player's settings, saved again unchanged: stored long before today.
        const Answer unchanged = save(2000, 1374, QStringLiteral("2026-10-01T08:00:00.000Z"));
        QVERIFY(unchanged.ok);
        QVERIFY(!unchanged.payload.value(QStringLiteral("idempotent_replay")).toBool());
        const QDateTime storedAt = QDateTime::fromString(field(unchanged, "updated_at_utc"), Qt::ISODateWithMs);
        QVERIFY2(storedAt.isValid() && storedAt < QDateTime::currentDateTimeUtc().addDays(-1),
                 qPrintable(field(unchanged, "updated_at_utc")));
        QVERIFY(!field(unchanged, "audit_event_id").isEmpty());
        const Answer again = save(2000, 1374, QStringLiteral("2026-10-02T09:30:00.000Z"));
        QCOMPARE(field(again, "audit_event_id"), field(unchanged, "audit_event_id"));
        QCOMPARE(field(again, "updated_at_utc"), field(unchanged, "updated_at_utc"));
        QCOMPARE(field(again, "baseline_effective_at"), field(unchanged, "baseline_effective_at"));

        // A changed goal is stored under an entry of its own, and saved again it answers that.
        const Answer changed = save(1800, 1374, QStringLiteral("2026-10-03T10:00:00.000Z"));
        QVERIFY(changed.ok);
        QVERIFY(field(changed, "audit_event_id") != field(unchanged, "audit_event_id"));
        QVERIFY(QDateTime::fromString(field(changed, "updated_at_utc"), Qt::ISODateWithMs) > storedAt);
        const Answer repeated = save(1800, 1374, QStringLiteral("2026-10-04T10:00:00.000Z"));
        QCOMPARE(field(repeated, "audit_event_id"), field(changed, "audit_event_id"));
        QCOMPARE(field(repeated, "updated_at_utc"), field(changed, "updated_at_utc"));
        QCOMPARE(field(repeated, "baseline_effective_at"), field(changed, "baseline_effective_at"));
        QCOMPARE(repeated.payload.value(QStringLiteral("goal_count")).toInt(), 1800);
        QCOMPARE(events.count(), 0);
    }

    // CS7-D4: StatisticsRepository.CountedFrom / CountContributingCompleted - on top of a
    // baseline above 0 only completions that ended at or after its effective time are
    // added (a missing end falls back to the entry, then the match; a row with no time at
    // all is not added). A baseline of 0 holds nothing, so every completion counts.
    void progressAddsOnlyCompletionsEndedSinceAPositiveBaseline()
    {
        mr::MockBackend backend;
        const auto completed = [](const QString &id, const QJsonValue &matched,
                                  const QJsonValue &entered, const QJsonValue &ended) {
            QJsonObject run = autoRun(id, QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("matched_at_utc"), matched);
            run.insert(QStringLiteral("entered_at_utc"), entered);
            run.insert(QStringLiteral("ended_at_utc"), ended);
            return run;
        };
        const QJsonValue none(QJsonValue::Null);
        const QString effective = QStringLiteral("2026-09-05T00:00:00.000Z");
        backend.resetRuns(QJsonArray{
            // Ended before the baseline took effect: already in it.
            completed(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                      QStringLiteral("2026-09-04T23:00:00.000Z"), QStringLiteral("2026-09-04T23:01:00.000Z"),
                      QStringLiteral("2026-09-04T23:59:59.999Z")),
            // Ended exactly then, and later: added.
            completed(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                      QStringLiteral("2026-09-04T23:30:00.000Z"), QStringLiteral("2026-09-04T23:31:00.000Z"),
                      effective),
            completed(QStringLiteral("33333333-3333-4333-8333-333333333333"),
                      QStringLiteral("2026-09-06T12:00:00.000Z"), QStringLiteral("2026-09-06T12:01:00.000Z"),
                      QStringLiteral("2026-09-06T12:20:00.000Z")),
            // No end: placed by its entry, after the effective time.
            completed(QStringLiteral("44444444-4444-4444-8444-444444444444"),
                      QStringLiteral("2026-09-07T12:00:00.000Z"), QStringLiteral("2026-09-07T12:01:00.000Z"),
                      none),
            // No time at all: never added on top of a baseline.
            completed(QStringLiteral("55555555-5555-4555-8555-555555555555"), none, none, none)});

        const Answer set = ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                               {{QStringLiteral("goal_count"), 2000},
                                {QStringLiteral("baseline_completed_count"), 10},
                                {QStringLiteral("baseline_effective_at"), effective},
                                {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        QVERIFY(set.ok);
        QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("achievement_progress")).toInt(), 13);
        QCOMPARE(stats.value(QStringLiteral("remaining")).toInt(), 1987);

        const Answer zero = ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                                {{QStringLiteral("goal_count"), 2000},
                                 {QStringLiteral("baseline_completed_count"), 0},
                                 {QStringLiteral("baseline_effective_at"), QStringLiteral("2026-10-03T00:00:00.000Z")},
                                 {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        QVERIFY(zero.ok);
        stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("achievement_progress")).toInt(), 5);
    }

    // S33-1a: RunMutationService.InitialChanges - a CREATE_MANUAL revision records the
    // whole initial value set, every old value null (docs/manual-correction.md section 2),
    // so what the player first entered stays readable in 修正历史. The mock wrote an
    // empty list, for the runs it created and for its sample one alike.
    void aManualRunsCreationRevisionRecordsEveryInitialValue()
    {
        mr::MockBackend backend;
        const Answer created = ask(backend, QStringLiteral("CreateManualRun"), {
            {QStringLiteral("content_id"), 17},
            {QStringLiteral("job_id"), 24},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-02T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-02T12:01:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-02T12:20:00.000Z")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("reason"), QString::fromUtf8("补录")}});
        QVERIFY(created.ok);
        const Answer samples = ask(backend, QStringLiteral("QueryRuns"),
                                   {{QStringLiteral("filter"),
                                     QJsonObject{{QStringLiteral("source"), QJsonArray{QStringLiteral("MANUAL")}}}},
                                    {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        QJsonArray manual = samples.payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(manual.size(), 2);

        const QStringList fields{
            QStringLiteral("content_id"), QStringLiteral("duty_name"), QStringLiteral("duty_category"),
            QStringLiteral("job_id"), QStringLiteral("job_name"), QStringLiteral("role"),
            QStringLiteral("matched_at_utc"), QStringLiteral("entered_at_utc"),
            QStringLiteral("ended_at_utc"), QStringLiteral("duration_ms"), QStringLiteral("result"),
            QStringLiteral("contributes_to_goal"), QStringLiteral("note"), QStringLiteral("soft_deleted"),
            QStringLiteral("pending_review"), QStringLiteral("manually_corrected")};
        for (const QJsonValue &item : std::as_const(manual)) {
            const QJsonObject run = item.toObject();
            const QJsonObject first = ask(backend, QStringLiteral("GetRunRevisions"),
                                          {{QStringLiteral("run_id"), run.value(QStringLiteral("run_id"))}})
                                          .payload.value(QStringLiteral("items")).toArray().first().toObject();
            QCOMPARE(first.value(QStringLiteral("change_kind")).toString(), QStringLiteral("CREATE_MANUAL"));
            const QJsonArray changes = first.value(QStringLiteral("changes")).toArray();
            QStringList named;
            for (const QJsonValue &value : changes) {
                const QJsonObject change = value.toObject();
                const QString field = change.value(QStringLiteral("field")).toString();
                named.append(field);
                QVERIFY2(change.value(QStringLiteral("old_value")).isNull(), qPrintable(field));
                QVERIFY2(change.contains(QStringLiteral("new_value")), qPrintable(field));
                const QJsonValue stored = run.value(field);
                if (field == QLatin1String("pending_review") || field == QLatin1String("soft_deleted")
                    || field == QLatin1String("manually_corrected")) {
                    QCOMPARE(change.value(QStringLiteral("new_value")), QJsonValue(stored.toBool(false)));
                } else {
                    QCOMPARE(change.value(QStringLiteral("new_value")),
                             stored.isUndefined() ? QJsonValue(QJsonValue::Null) : stored);
                }
            }
            QCOMPARE(named, fields);
        }
    }

    // S33-1b: RunFilterSql.AddContentIds - a duty asked for by its content id also finds
    // the runs that observed only a zone hosting that duty and no other (capture never
    // back-infers the content id). A zone several duties share is never expanded.
    void aContentIdFilterAlsoFindsRunsKnownOnlyByTheDutysOwnZone()
    {
        const mr::DutyCatalog *catalog = mr::DutyCatalog::shared();
        const qint64 ownZone = catalog->lookup(17).value(QStringLiteral("territory_id")).toLongLong();
        const qint64 sharedZone = catalog->lookup(482).value(QStringLiteral("territory_id")).toLongLong();
        QVERIFY(ownZone > 0);
        QVERIFY(sharedZone > 0);
        // The precondition: several duties share that zone, so it names none of them.
        QVERIFY(!catalog->lookupByTerritory(sharedZone).contains(QStringLiteral("duty_name")));
        QVERIFY(catalog->lookupByTerritory(ownZone).contains(QStringLiteral("duty_name")));

        const auto run = [](const QString &id, const QJsonValue &content, qint64 zone) {
            QJsonObject row = autoRun(id, QStringLiteral("COMPLETED"), true);
            row.insert(QStringLiteral("content_id"), content);
            row.insert(QStringLiteral("territory_id"), zone);
            return row;
        };
        const QJsonValue none(QJsonValue::Null);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{
            run(QStringLiteral("11111111-1111-4111-8111-111111111111"), 17, ownZone),
            run(QStringLiteral("22222222-2222-4222-8222-222222222222"), none, ownZone),
            run(QStringLiteral("33333333-3333-4333-8333-333333333333"), 482, sharedZone),
            run(QStringLiteral("44444444-4444-4444-8444-444444444444"), none, sharedZone)});

        const auto found = [&backend](int contentId) {
            const QJsonObject filter{{QStringLiteral("content_id"), QJsonArray{contentId}}};
            QStringList ids;
            for (const QJsonValue &item : ask(backend, QStringLiteral("QueryRuns"),
                                              {{QStringLiteral("filter"), filter},
                                               {QStringLiteral("page"), 1},
                                               {QStringLiteral("page_size"), 200}})
                                              .payload.value(QStringLiteral("items")).toArray())
                ids.append(item.toObject().value(QStringLiteral("run_id")).toString().left(1));
            ids.sort();
            return ids;
        };
        QCOMPARE(found(17), (QStringList{QStringLiteral("1"), QStringLiteral("2")}));
        QCOMPARE(found(482), QStringList{QStringLiteral("3")});
        // Statistics take the same filter.
        QCOMPARE(backend.dashboardStats({{QStringLiteral("content_id"), QJsonArray{17}}})
                     .value(QStringLiteral("attempt_count")).toInt(), 2);
    }

    // S33-1c: the sample run the player deleted carries its SOFT_DELETE revision
    // (RunMutationService.SoftDeleteRun), and the sample runs crash recovery closed carry
    // the SYSTEM revision CrashRecoveryService writes: before it the run was still in
    // flight (UNKNOWN, no end, not pending review); it closed the run as INTERRUPTED, or
    // as 进本前取消 when it never entered, at LOW confidence and pending review. Recovery
    // never infers a clear.
    void theSampleDeletionAndRecoveriesAreInTheirRevisionChains()
    {
        mr::MockBackend backend;
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{QStringLiteral("filter"),
                                  QJsonObject{{QStringLiteral("include_deleted"), true}}},
                                 {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        int deleted = 0;
        int recovered = 0;
        for (const QJsonValue &item : page.payload.value(QStringLiteral("items")).toArray()) {
            const QJsonObject run = item.toObject();
            const bool isDeleted = run.value(QStringLiteral("soft_deleted")).toBool();
            const bool isPending = run.value(QStringLiteral("pending_review")).toBool();
            if (!isDeleted && !isPending)
                continue;
            const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                         {{QStringLiteral("run_id"), run.value(QStringLiteral("run_id"))}})
                                         .payload.value(QStringLiteral("items")).toArray();
            const QJsonObject last = chain.last().toObject();
            QCOMPARE(last.value(QStringLiteral("revision")).toInt(), run.value(QStringLiteral("revision")).toInt());
            QCOMPARE(run.value(QStringLiteral("revision")).toInt(), 2);
            QHash<QString, QJsonObject> changes;
            for (const QJsonValue &value : last.value(QStringLiteral("changes")).toArray())
                changes.insert(value.toObject().value(QStringLiteral("field")).toString(), value.toObject());
            if (isDeleted) {
                ++deleted;
                QCOMPARE(last.value(QStringLiteral("change_kind")).toString(), QStringLiteral("SOFT_DELETE"));
                QCOMPARE(last.value(QStringLiteral("actor")).toString(), QStringLiteral("USER"));
                QCOMPARE(changes.size(), 1);
                QCOMPARE(changes.value(QStringLiteral("soft_deleted")).value(QStringLiteral("old_value")),
                         QJsonValue(false));
                QCOMPARE(changes.value(QStringLiteral("soft_deleted")).value(QStringLiteral("new_value")),
                         QJsonValue(true));
                continue;
            }
            ++recovered;
            QCOMPARE(last.value(QStringLiteral("change_kind")).toString(), QStringLiteral("CORRECT"));
            QCOMPARE(last.value(QStringLiteral("actor")).toString(), QStringLiteral("SYSTEM"));
            const QString result = run.value(QStringLiteral("result")).toString();
            const bool entered = run.value(QStringLiteral("entered_at_utc")).isString();
            // CrashRecoveryService's own sentences, which name no result token (DT-10).
            QCOMPARE(last.value(QStringLiteral("reason")).toString(),
                     entered ? QString::fromUtf8("程序重启时发现未完结记录，已记为中断并标记待复核。")
                             : QString::fromUtf8("程序重启时发现未完结记录，它尚未进入副本，"
                                                 "已记为进本前取消并标记待复核。"));
            QCOMPARE(result, entered ? QStringLiteral("INTERRUPTED") : QStringLiteral("CANCELLED_BEFORE_ENTRY"));
            QCOMPARE(run.value(QStringLiteral("detection_confidence")).toString(), QStringLiteral("LOW"));
            QVERIFY(run.value(QStringLiteral("ended_at_utc")).isString());
            const auto was = [&changes](const char *field) {
                return changes.value(QLatin1String(field)).value(QStringLiteral("old_value"));
            };
            const auto became = [&changes](const char *field) {
                return changes.value(QLatin1String(field)).value(QStringLiteral("new_value"));
            };
            QCOMPARE(was("result"), QJsonValue(QStringLiteral("UNKNOWN")));
            QCOMPARE(became("result"), QJsonValue(result));
            QVERIFY(changes.contains(QStringLiteral("ended_at_utc")));
            QVERIFY(was("ended_at_utc").isNull());
            QCOMPARE(became("ended_at_utc"), run.value(QStringLiteral("ended_at_utc")));
            QCOMPARE(was("pending_review"), QJsonValue(false));
            QCOMPARE(became("pending_review"), QJsonValue(true));
            QCOMPARE(became("detection_confidence"), QJsonValue(QStringLiteral("LOW")));
            QCOMPARE(last.value(QStringLiteral("changed_at_utc")), run.value(QStringLiteral("ended_at_utc")));
        }
        QCOMPARE(deleted, 1);
        QCOMPARE(recovered, 2);
    }

    // S33-1d: RunMutationService.UndoRevision - revision 1 is the record's creation, and a
    // SYSTEM revision that closed an unfinished run cannot be taken back into the shape it
    // closed (no statistic would count the run and no restart would close it again). Both
    // answer ERR_UNDO_NOT_ALLOWED with the Collector's own sentence; the player's own
    // correction of that run can still be undone.
    void anUndoIsRefusedWhereTheCollectorRefusesIt()
    {
        mr::MockBackend backend;
        const auto undo = [&backend](const QString &runId, int revision) {
            return ask(backend, QStringLiteral("UndoRevision"),
                       {{QStringLiteral("run_id"), runId},
                        {QStringLiteral("expected_revision"), revision},
                        {QStringLiteral("reason"), QString::fromUtf8("撤销")}});
        };

        const QString created = createManualRun(backend);
        const Answer creation = undo(created, 1);
        QCOMPARE(creation.code, QStringLiteral("ERR_UNDO_NOT_ALLOWED"));
        QCOMPARE(creation.message,
                 QString::fromUtf8("第 1 条修订是创建记录本身，无法撤销；如需移除请使用软删除。"));

        const Answer pending = ask(backend, QStringLiteral("QueryRuns"),
                                   {{QStringLiteral("filter"),
                                     QJsonObject{{QStringLiteral("pending_review"), true}}},
                                    {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        const QJsonArray recovered = pending.payload.value(QStringLiteral("items")).toArray();
        QVERIFY(!recovered.isEmpty());
        for (const QJsonValue &item : recovered) {
            const QJsonObject run = item.toObject();
            const Answer refused = undo(run.value(QStringLiteral("run_id")).toString(),
                                        run.value(QStringLiteral("revision")).toInt());
            QCOMPARE(refused.code, QStringLiteral("ERR_UNDO_NOT_ALLOWED"));
            QCOMPARE(refused.message,
                     QString::fromUtf8("这条修订是程序为未完结的记录自动写下的。撤销它会让记录回到无法统计、"
                                       "也无法确认的状态，因此不能撤销；如果判断有误，请直接更正这条记录。"));
        }

        const QString runId = recovered.first().toObject().value(QStringLiteral("run_id")).toString();
        const Answer noted = correct(backend, runId, {{QStringLiteral("note"), QString::fromUtf8("看过了")}});
        QVERIFY(noted.ok);
        const int revision = noted.payload.value(QStringLiteral("revision")).toInt();
        const Answer undone = undo(runId, revision);
        QVERIFY2(undone.ok, qPrintable(undone.code + QLatin1Char(' ') + undone.message));
        QCOMPARE(undone.payload.value(QStringLiteral("revision")).toInt(), revision + 1);
        QVERIFY(undone.payload.value(QStringLiteral("run")).toObject().value(QStringLiteral("pending_review")).toBool());
    }

    // S33-1e: MentorRun.IsConfirmedMentor / RunFilterSql - an imported run is a confirmed
    // mentor run when it carries the roulette id, like an automatic one. The mock read a
    // field no contract carries (import_confirmed_mentor) instead.
    void anImportedRunCountsWhenItCarriesTheRouletteId()
    {
        QJsonObject withRoulette = autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                                           QStringLiteral("COMPLETED"), true);
        withRoulette.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        QJsonObject withoutRoulette = autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                                              QStringLiteral("LEFT_OR_ABANDONED"), true);
        withoutRoulette.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        withoutRoulette.insert(QStringLiteral("mentor_roulette_id"), QJsonValue(QJsonValue::Null));
        withoutRoulette.insert(QStringLiteral("import_confirmed_mentor"), true);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{withRoulette, withoutRoulette});

        const QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("attempt_count")).toInt(), 1);
        QCOMPARE(stats.value(QStringLiteral("completed_count")).toInt(), 1);
        QCOMPARE(stats.value(QStringLiteral("leave_rate")).toDouble(), 0.0);
    }

    // DT-10: RunMutationService.SoftDeleteRun commits with clearPendingReview - a deleted run
    // leaves the review list - and its revision records that change too. Restoring it does
    // not flag it again. The mock kept the flag.
    void aSoftDeleteClearsThePendingReviewFlag()
    {
        mr::MockBackend backend;
        const QJsonArray pending = ask(backend, QStringLiteral("QueryRuns"),
                                       {{QStringLiteral("filter"),
                                         QJsonObject{{QStringLiteral("pending_review"), true}}},
                                        {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}})
                                       .payload.value(QStringLiteral("items")).toArray();
        QVERIFY(!pending.isEmpty());
        const QJsonObject before = pending.first().toObject();
        const QString runId = before.value(QStringLiteral("run_id")).toString();
        const int revision = before.value(QStringLiteral("revision")).toInt();

        const Answer deleted = ask(backend, QStringLiteral("SoftDeleteRun"),
                                   {{QStringLiteral("run_id"), runId},
                                    {QStringLiteral("expected_revision"), revision},
                                    {QStringLiteral("reason"), QString::fromUtf8("重复记录")}});
        QVERIFY2(deleted.ok, qPrintable(deleted.code + QLatin1Char(' ') + deleted.message));
        const QJsonObject run = deleted.payload.value(QStringLiteral("run")).toObject();
        QVERIFY(run.value(QStringLiteral("soft_deleted")).toBool());
        QVERIFY(!run.value(QStringLiteral("pending_review")).toBool(true));
        const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                     {{QStringLiteral("run_id"), runId}})
                                     .payload.value(QStringLiteral("items")).toArray();
        QHash<QString, QJsonObject> changes;
        for (const QJsonValue &value : chain.last().toObject().value(QStringLiteral("changes")).toArray())
            changes.insert(value.toObject().value(QStringLiteral("field")).toString(), value.toObject());
        QCOMPARE(changes.size(), 2);
        QCOMPARE(changes.value(QStringLiteral("soft_deleted")).value(QStringLiteral("new_value")), QJsonValue(true));
        QCOMPARE(changes.value(QStringLiteral("pending_review")).value(QStringLiteral("old_value")), QJsonValue(true));
        QCOMPARE(changes.value(QStringLiteral("pending_review")).value(QStringLiteral("new_value")), QJsonValue(false));

        const Answer restored = ask(backend, QStringLiteral("RestoreRun"),
                                    {{QStringLiteral("run_id"), runId},
                                     {QStringLiteral("expected_revision"), revision + 1},
                                     {QStringLiteral("reason"), QString::fromUtf8("删错了")}});
        QVERIFY(restored.ok);
        QVERIFY(!restored.payload.value(QStringLiteral("run")).toObject()
                     .value(QStringLiteral("pending_review")).toBool(true));
    }

    // DT-10: RunMutationService.CorrectRun marks a run 已修正 only when the correction overrules
    // what the software recorded (RunMutationRules.OverrulesTheRecord). Answering a run pending
    // review, filling a duty or job the software left blank, and the note are no corrections; a
    // recorded time, or a settled outcome changed, is - and the revision then says so. Once
    // marked, a run stays marked. The mock marked every correction.
    void aCorrectionMarksTheRunCorrectedOnlyWhenItOverrulesTheRecord()
    {
        QJsonObject pending = autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                                      QStringLiteral("INTERRUPTED"), true);
        pending.insert(QStringLiteral("pending_review"), true);
        QJsonObject blank = autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                                    QStringLiteral("COMPLETED"), true);
        blank.insert(QStringLiteral("job_id"), QJsonValue(QJsonValue::Null));
        blank.insert(QStringLiteral("job_name"), QString::fromUtf8("未知"));
        blank.insert(QStringLiteral("role"), QStringLiteral("UNKNOWN"));
        blank.insert(QStringLiteral("content_id"), QJsonValue(QJsonValue::Null));
        blank.insert(QStringLiteral("duty_name"), QJsonValue(QJsonValue::Null));
        const QJsonObject settled = autoRun(QStringLiteral("33333333-3333-4333-8333-333333333333"),
                                            QStringLiteral("COMPLETED"), true);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{pending, blank, settled});

        const auto marked = [](const Answer &answer) {
            return answer.payload.value(QStringLiteral("run")).toObject()
                .value(QStringLiteral("manually_corrected")).toBool();
        };
        const auto lastChanges = [&backend](const QString &runId) {
            QStringList fields;
            const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                         {{QStringLiteral("run_id"), runId}})
                                         .payload.value(QStringLiteral("items")).toArray();
            for (const QJsonValue &value : chain.last().toObject().value(QStringLiteral("changes")).toArray())
                fields.append(value.toObject().value(QStringLiteral("field")).toString());
            return fields;
        };
        const QString pendingId = pending.value(QStringLiteral("run_id")).toString();
        const QString blankId = blank.value(QStringLiteral("run_id")).toString();
        const QString settledId = settled.value(QStringLiteral("run_id")).toString();

        // How a run pending review went: an answer, not a correction.
        Answer answer = correct(backend, pendingId, {{QStringLiteral("result"), QStringLiteral("COMPLETED")}});
        QVERIFY2(answer.ok, qPrintable(answer.code + QLatin1Char(' ') + answer.message));
        QVERIFY(!marked(answer));
        QVERIFY(!lastChanges(pendingId).contains(QStringLiteral("manually_corrected")));

        // A job and a duty the software left blank, filled in; then a note.
        answer = correct(backend, blankId, {{QStringLiteral("job_id"), 24}, {QStringLiteral("content_id"), 17}});
        QVERIFY2(answer.ok, qPrintable(answer.code + QLatin1Char(' ') + answer.message));
        QVERIFY(!marked(answer));
        answer = correct(backend, blankId, {{QStringLiteral("note"), QString::fromUtf8("补上职业")}});
        QVERIFY(answer.ok);
        QVERIFY(!marked(answer));

        // A recorded time changed: a correction, and its revision says so.
        answer = correct(backend, blankId, {{QStringLiteral("ended_at_utc"),
                                            QStringLiteral("2026-09-01T12:12:00.000Z")}});
        QVERIFY(answer.ok);
        QVERIFY(marked(answer));
        QVERIFY(lastChanges(blankId).contains(QStringLiteral("manually_corrected")));

        // A settled outcome changed: a correction too.
        answer = correct(backend, settledId, {{QStringLiteral("result"), QStringLiteral("LEFT_OR_ABANDONED")}});
        QVERIFY(answer.ok);
        QVERIFY(marked(answer));
        QVERIFY(lastChanges(settledId).contains(QStringLiteral("manually_corrected")));

        // Undoing it puts the values back, never the provenance (RunMutationService.Commit):
        // a run once marked stays marked, whatever comes after.
        const Answer undone = ask(backend, QStringLiteral("UndoRevision"),
                                  {{QStringLiteral("run_id"), settledId},
                                   {QStringLiteral("expected_revision"),
                                    answer.payload.value(QStringLiteral("revision")).toInt()},
                                   {QStringLiteral("reason"), QString::fromUtf8("撤销")}});
        QVERIFY2(undone.ok, qPrintable(undone.code + QLatin1Char(' ') + undone.message));
        QCOMPARE(undone.payload.value(QStringLiteral("run")).toObject()
                     .value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
        QVERIFY(marked(undone));
        QVERIFY(!lastChanges(settledId).contains(QStringLiteral("manually_corrected")));
        answer = correct(backend, settledId, {{QStringLiteral("note"), QString::fromUtf8("队伍解散")}});
        QVERIFY(answer.ok);
        QVERIFY(marked(answer));
    }

    // DT-10: RunMutationService.UndoRevision checks the restored run as a whole
    // (RunMutationRules.ValidateFinalValue): putting back a value the rules refuse is refused,
    // with the Collector's own sentence, and nothing is written. Capture stored this run with
    // its match after its entry; the player fixed the match, and undoing that is refused.
    void anUndoIsCheckedAsAWholeLikeTheCollectorDoes()
    {
        QJsonObject captured = autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                                       QStringLiteral("COMPLETED"), true);
        captured.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-01T12:05:00.000Z"));
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{captured});
        const QString runId = captured.value(QStringLiteral("run_id")).toString();

        const Answer fixed = correct(backend, runId, {{QStringLiteral("matched_at_utc"),
                                                      QStringLiteral("2026-09-01T12:00:00.000Z")}});
        QVERIFY2(fixed.ok, qPrintable(fixed.code + QLatin1Char(' ') + fixed.message));
        const Answer undone = ask(backend, QStringLiteral("UndoRevision"),
                                  {{QStringLiteral("run_id"), runId},
                                   {QStringLiteral("expected_revision"), 2},
                                   {QStringLiteral("reason"), QString::fromUtf8("撤销")}});
        QCOMPARE(undone.code, QStringLiteral("ERR_TIME_ORDER"));
        QCOMPARE(undone.message, QString::fromUtf8("匹配时间不能晚于进入副本的时间。"));
        const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                     {{QStringLiteral("run_id"), runId}})
                                     .payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(chain.size(), 1);
        QCOMPARE(correct(backend, runId, {{QStringLiteral("note"), QStringLiteral("x")}})
                     .payload.value(QStringLiteral("revision")).toInt(), 3);
    }

    // DT-10: RunMutationService.UpdateAchievementBaseline refuses a goal below 1 and a negative
    // baseline with ERR_BAD_REQUEST, its own sentences, and stores nothing. The mock clamped
    // them to 1 and 0 and saved.
    void aGoalBelowOneOrANegativeBaselineIsRefused()
    {
        mr::MockBackend backend;
        backend.setAchievement(2000, 1374);
        const auto save = [&backend](int goal, int baseline) {
            return ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                       {{QStringLiteral("goal_count"), goal},
                        {QStringLiteral("baseline_completed_count"), baseline},
                        {QStringLiteral("baseline_effective_at"), QStringLiteral("2026-10-01T08:00:00.000Z")},
                        {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        };
        const Answer noGoal = save(0, 1374);
        QCOMPARE(noGoal.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(noGoal.message, QString::fromUtf8("目标值必须大于等于 1。"));
        const Answer negative = save(2000, -1);
        QCOMPARE(negative.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(negative.message, QString::fromUtf8("已完成次数不能为负数。"));
        const QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("goal_count")).toInt(), 2000);
        QCOMPARE(stats.value(QStringLiteral("baseline_completed_count")).toInt(), 1374);
        QVERIFY(save(1, 0).ok);
    }

    // DT-10: a run capture recorded and let go is closed - the state machine ends it, or the
    // next start's crash recovery does (CrashRecoveryService). Only the run being followed now
    // reads as in flight (RunMutationRules.ReadsAsInFlight), and that is GetCurrentRun's, not a
    // stored sample's. Four generated UNKNOWN runs had no end and read as 进行中 for good, left
    // out of every statistic.
    void noSampleRunReadsAsStillInFlight()
    {
        mr::MockBackend backend;
        const QJsonArray runs = ask(backend, QStringLiteral("QueryRuns"),
                                    {{QStringLiteral("filter"),
                                      QJsonObject{{QStringLiteral("include_deleted"), true}}},
                                     {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}})
                                    .payload.value(QStringLiteral("items")).toArray();
        QVERIFY(runs.size() > 50);
        int unknown = 0;
        for (const QJsonValue &item : runs) {
            const QJsonObject run = item.toObject();
            QVERIFY2(!mr::Formatters::runInProgress(run.toVariantMap()),
                     qPrintable(run.value(QStringLiteral("run_id")).toString()));
            if (run.value(QStringLiteral("result")).toString() == QLatin1String("UNKNOWN")) {
                ++unknown;
                QVERIFY(run.value(QStringLiteral("ended_at_utc")).isString());
            }
        }
        // The prototype's UNKNOWN runs are still there, ended.
        QVERIFY(unknown > 0);
    }

    // OJ-6: a reply can be held back, so a test can let a later request
    // overtake an earlier one the way a slow Collector answer does.
    void aDelayedReplyCanArriveAfterALaterOne()
    {
        mr::MockBackend backend;
        backend.setReplyDelay(80, QStringLiteral("GetStatus"));
        QStringList order;
        backend.request(QStringLiteral("GetStatus"))->whenDone(&backend,
            [&order](bool, const QVariantMap &, const QString &, const QString &) {
                order.append(QStringLiteral("GetStatus"));
            });
        backend.request(QStringLiteral("GetVersion"))->whenDone(&backend,
            [&order](bool, const QVariantMap &, const QString &, const QString &) {
                order.append(QStringLiteral("GetVersion"));
            });
        QTRY_COMPARE(order.size(), 2);
        QCOMPARE(order, (QStringList{QStringLiteral("GetVersion"), QStringLiteral("GetStatus")}));

        // Without a delay replies keep their order.
        backend.setReplyDelay(0, QStringLiteral("GetStatus"));
        order.clear();
        backend.request(QStringLiteral("GetStatus"))->whenDone(&backend,
            [&order](bool, const QVariantMap &, const QString &, const QString &) {
                order.append(QStringLiteral("GetStatus"));
            });
        backend.request(QStringLiteral("GetVersion"))->whenDone(&backend,
            [&order](bool, const QVariantMap &, const QString &, const QString &) {
                order.append(QStringLiteral("GetVersion"));
            });
        QTRY_COMPARE(order.size(), 2);
        QCOMPARE(order, (QStringList{QStringLiteral("GetStatus"), QStringLiteral("GetVersion")}));
    }
};

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    // Keep the developer's real desktop.ini untouched.
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    MockBackendTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "MockBackendTests.moc"
