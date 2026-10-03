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
#include <QSet>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

namespace {

struct Answer {
    bool done = false;
    bool ok = false;
    QJsonObject payload;
    QString code;
};

/// One request, waited for.
Answer ask(mr::IBackend &backend, const QString &type, const QJsonObject &payload = {})
{
    Answer answer;
    backend.request(type, payload)->whenDone(&backend,
        [&answer](bool ok, const QVariantMap &result, const QString &code, const QString &) {
            answer.done = true;
            answer.ok = ok;
            answer.payload = QJsonObject::fromVariantMap(result);
            answer.code = code;
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
        settings.setTtsEnabled(true);
        mr::MockBackend backend;
        mr::AppController controller(&backend, &settings);
        QSignalSpy spoke(controller.tts(), &mr::TtsService::spoke);
        backend.simulateRunTransitions(QString());
        QCOMPARE(spoke.size(), 2);
        QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("matched"));
        QCOMPARE(spoke.at(1).at(0).toString(), QStringLiteral("entered"));
        settings.setTtsEnabled(wasEnabled);
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
