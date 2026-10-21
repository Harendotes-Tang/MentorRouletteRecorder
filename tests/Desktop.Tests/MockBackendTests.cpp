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
#include "CollectorText.h"
#include "DutyCatalog.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "TtsService.h"

#include <QDateTime>
#include <QFile>
#include <QGuiApplication>
#include <QHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPointer>
#include <QScopeGuard>
#include <QSet>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTextToSpeech>
#include <QTimeZone>

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

/// One request sent under \a requestId, the way a retried mutation is resent, waited for.
/// The reply carries that id.
Answer askWithId(mr::IBackend &backend, const QString &type, const QJsonObject &payload,
                 const QString &requestId)
{
    Answer answer;
    mr::BackendReply *reply = backend.requestWithId(type, payload, requestId);
    if (reply->requestId() != requestId)
        answer.code = QStringLiteral("TEST_WRONG_REQUEST_ID");
    reply->whenDone(&backend,
        [&answer](bool ok, const QVariantMap &result, const QString &code, const QString &message) {
            answer.done = true;
            answer.ok = ok;
            answer.payload = QJsonObject::fromVariantMap(result);
            if (answer.code != QLatin1String("TEST_WRONG_REQUEST_ID"))
                answer.code = code;
            answer.message = message;
        });
    if (!QTest::qWaitFor([&answer] { return answer.done; }, 3000))
        answer.code = QStringLiteral("TEST_NO_REPLY");
    return answer;
}

/// The Collector's Chinese for the catalogue sentence \a key.
QString collectorChinese(const char *key, const QVariantMap &args = {})
{
    return mr::collector_text::chinese(mr::collector_text::textRef(key, args));
}

int runCount(mr::MockBackend &backend)
{
    return ask(backend, QStringLiteral("QueryRuns"),
               {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("include_deleted"), true}}},
                {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}})
        .payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")).toInt();
}

/// The Collector's two copies of one sentence agree: \a ref names a catalogue
/// key this build knows, and renders exactly \a chinese (contracts/collector-texts.md).
bool sameSentence(const QJsonValue &chinese, const QJsonValue &ref)
{
    const QVariantMap map = ref.toObject().toVariantMap();
    return chinese.isString() && !chinese.toString().isEmpty()
           && mr::collector_text::knows(map.value(QStringLiteral("key")).toString())
           && mr::collector_text::chinese(map) == chinese.toString();
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

/// A whole (unpaged) import preview of \a rows: a JSON file's text, or the recognised
/// rows of screenshots, read in UTC+8.
Answer previewImport(mr::MockBackend &backend, const QString &kind, const QJsonArray &rows,
                     const QString &sourceName)
{
    QJsonObject payload{{QStringLiteral("source_kind"), kind},
                        {QStringLiteral("source_name"), sourceName},
                        {QStringLiteral("time_zone"), QStringLiteral("+08:00")}};
    if (kind == QLatin1String("JSON"))
        payload.insert(QStringLiteral("text"), QString::fromUtf8(QJsonDocument(rows).toJson(QJsonDocument::Compact)));
    else
        payload.insert(QStringLiteral("rows"), rows);
    return ask(backend, QStringLiteral("PreviewRunImport"), payload);
}

/// Each preview row's status, in row order.
QStringList importStatuses(const Answer &preview)
{
    QStringList statuses;
    for (const QJsonValue &row : preview.payload.value(QStringLiteral("rows")).toArray())
        statuses.append(row.toObject().value(QStringLiteral("status")).toString());
    return statuses;
}

/// Commits every row of \a preview.
Answer commitWholePreview(mr::MockBackend &backend, const Answer &preview)
{
    QJsonArray numbers;
    for (const QJsonValue &row : preview.payload.value(QStringLiteral("rows")).toArray())
        numbers.append(row.toObject().value(QStringLiteral("row_number")));
    return ask(backend, QStringLiteral("CommitRunImport"),
               {{QStringLiteral("preview_id"), preview.payload.value(QStringLiteral("preview_id"))},
                {QStringLiteral("row_numbers"), numbers},
                {QStringLiteral("confirm_own_records"), true}});
}

} // namespace

class MockBackendTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void localizedNameSearchPreservesOriginalNotesAndOtherFilters()
    {
        mr::MockBackend backend;
        const auto row = [](const QString &id, int jobId, const QString &jobName, const QString &note) {
            QJsonObject run = autoRun(id, QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("job_id"), jobId);
            run.insert(QStringLiteral("job_name"), jobName);
            run.insert(QStringLiteral("note"), note);
            return run;
        };
        backend.resetRuns(QJsonArray{
            row(QStringLiteral("job"), 19, QString::fromUtf8("骑士"), QStringLiteral("unrelated")),
            row(QStringLiteral("note"), 24, QString::fromUtf8("白魔法师"), QStringLiteral("helped a Paladin")),
            row(QStringLiteral("alias-in-note"), 24, QString::fromUtf8("白魔法师"), QString::fromUtf8("helped 骑士")),
            row(QStringLiteral("unrelated"), 24, QString::fromUtf8("白魔法师"), QStringLiteral("unrelated"))});
        const auto query = [&backend](const QJsonObject &filter) {
            return ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter}});
        };
        QJsonObject filter{{QStringLiteral("text"), QStringLiteral("Paladin")},
                           {QStringLiteral("name_text"), QString::fromUtf8("骑士")}};
        const Answer both = query(filter);
        QVERIFY(both.ok);
        QCOMPARE(both.payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")).toInt(), 2);
        const Answer names = query({{QStringLiteral("name_text"), QString::fromUtf8("骑士")}});
        QCOMPARE(names.payload.value(QStringLiteral("items")).toArray().size(), 1);
        QCOMPARE(names.payload.value(QStringLiteral("items")).toArray().first().toObject()
                     .value(QStringLiteral("run_id")).toString(), QStringLiteral("job"));
        filter.insert(QStringLiteral("job_id"), QJsonArray{24});
        const Answer constrained = query(filter);
        QCOMPARE(constrained.payload.value(QStringLiteral("items")).toArray().size(), 1);
        QCOMPARE(constrained.payload.value(QStringLiteral("items")).toArray().first().toObject()
                     .value(QStringLiteral("run_id")).toString(), QStringLiteral("note"));
        const Answer calendar = ask(backend, QStringLiteral("QueryRunCalendarDays"),
            {{QStringLiteral("month"), QStringLiteral("2026-09")}, {QStringLiteral("filter"), filter}});
        QVERIFY2(calendar.ok, qPrintable(calendar.message));
        QCOMPARE(calendar.payload.value(QStringLiteral("days")).toArray(), QJsonArray{QStringLiteral("2026-09-01")});
    }

    void calendarDaysUseFullSetIgnoreDateBoundsAndKeepLocalHistoryDays()
    {
        mr::MockBackend backend;
        QJsonArray rows;
        const auto archival = [](const QString &id, const QString &day, int job, bool deleted) {
            QJsonObject row = autoRun(id, QStringLiteral("COMPLETED"), false);
            row.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
            row.insert(QStringLiteral("matched_at_utc"), QJsonValue::Null);
            row.insert(QStringLiteral("ended_at_utc"), QJsonValue::Null);
            row.insert(QStringLiteral("job_id"), job);
            row.insert(QStringLiteral("soft_deleted"), deleted);
            row.insert(QStringLiteral("import_metadata"), QJsonObject{
                {QStringLiteral("source_recorded_at"), day},
                {QStringLiteral("source_recorded_at_utc"), QJsonValue::Null}});
            return row;
        };
        for (int i = 0; i < 205; ++i)
            rows.append(archival(QString::number(i), QStringLiteral("2026-09-01"), 24, false));
        rows.append(archival(QStringLiteral("last"), QStringLiteral("2026/9/30"), 24, false));
        rows.append(archival(QStringLiteral("deleted"), QStringLiteral("2026-09-15"), 19, true));
        rows.append(archival(QStringLiteral("unknown"), QStringLiteral("unknown"), 24, false));
        rows.append(archival(QStringLiteral("invalid"), QStringLiteral("2026-09-31"), 24, false));
        const QDateTime boundary(QDate(2026, 9, 1), QTime(0, 1), QTimeZone::systemTimeZone());
        QJsonObject instant = autoRun(QStringLiteral("instant"), QStringLiteral("COMPLETED"), true);
        instant.insert(QStringLiteral("matched_at_utc"), boundary.toUTC().toString(Qt::ISODateWithMs));
        instant.insert(QStringLiteral("job_id"), 25);
        rows.append(instant);
        QJsonObject historicalPending = archival(QStringLiteral("historical-pending"), QStringLiteral("2026-09-20"), 26, false);
        historicalPending.insert(QStringLiteral("pending_review"), true);
        rows.append(historicalPending);
        QJsonObject livePending = instant;
        livePending.insert(QStringLiteral("run_id"), QStringLiteral("live-pending"));
        livePending.insert(QStringLiteral("job_id"), 26);
        livePending.insert(QStringLiteral("pending_review"), true);
        rows.append(livePending);
        backend.resetRuns(rows);
        QJsonObject filter{{QStringLiteral("job_id"), QJsonArray{24}},
            {QStringLiteral("from_utc"), QStringLiteral("2026-09-02T00:00:00.000Z")},
            {QStringLiteral("to_utc"), QStringLiteral("2026-09-03T00:00:00.000Z")},
            {QStringLiteral("history_from_day"), QStringLiteral("2026-09-02")},
            {QStringLiteral("history_to_day"), QStringLiteral("2026-09-03")}};
        const auto query = [&] {
            return ask(backend, QStringLiteral("QueryRunCalendarDays"), {
                {QStringLiteral("month"), QStringLiteral("2026-09")}, {QStringLiteral("filter"), filter}});
        };
        Answer answer = query();
        QVERIFY(answer.ok);
        QCOMPARE(answer.payload.value(QStringLiteral("days")).toArray(),
            QJsonArray({QStringLiteral("2026-09-01"), QStringLiteral("2026-09-30")}));
        filter.insert(QStringLiteral("job_id"), QJsonArray{19});
        QVERIFY(query().payload.value(QStringLiteral("days")).toArray().isEmpty());
        filter.insert(QStringLiteral("deleted_only"), true);
        QCOMPARE(query().payload.value(QStringLiteral("days")).toArray(), QJsonArray{QStringLiteral("2026-09-15")});
        filter = {{QStringLiteral("job_id"), QJsonArray{25}}};
        QCOMPARE(query().payload.value(QStringLiteral("days")).toArray(), QJsonArray{QStringLiteral("2026-09-01")});
        filter = {{QStringLiteral("job_id"), QJsonArray{26}}, {QStringLiteral("pending_review"), true}};
        QCOMPARE(query().payload.value(QStringLiteral("days")).toArray(), QJsonArray{QStringLiteral("2026-09-01")});
        const Answer invalid = ask(backend, QStringLiteral("QueryRunCalendarDays"), {{QStringLiteral("month"), QStringLiteral("2026-13")}});
        QVERIFY(!invalid.ok);
        QCOMPARE(invalid.code, QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(!ask(backend, QStringLiteral("QueryRunCalendarDays"), {
            {QStringLiteral("month"), QStringLiteral("2026-09")}, {QStringLiteral("page"), 1}}).ok);
        QVERIFY(!ask(backend, QStringLiteral("QueryRunCalendarDays"), {
            {QStringLiteral("month"), QStringLiteral("2026-09")},
            {QStringLiteral("filter"), QJsonObject{{QStringLiteral("unexpected"), true}}}}).ok);
    }

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
    void deletedOnlyFiltersBeforePaginationAndDoesNotAffectStatistics()
    {
        mr::MockBackend backend;
        const QJsonObject filter{{QStringLiteral("deleted_only"), true}, {QStringLiteral("include_deleted"), true}};
        const Answer all = ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter},
            {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}});
        QVERIFY(all.ok);
        const QJsonArray items = all.payload.value(QStringLiteral("items")).toArray();
        QVERIFY(!items.isEmpty());
        for (const auto &item : items) QVERIFY(item.toObject().value(QStringLiteral("soft_deleted")).toBool());
        const Answer page = ask(backend, QStringLiteral("QueryRuns"), {{QStringLiteral("filter"), filter},
            {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 1}});
        QCOMPARE(page.payload.value(QStringLiteral("items")).toArray().size(), 1);
        QCOMPARE(page.payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")),
                 all.payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")));
        const Answer stats = ask(backend, QStringLiteral("GetResultStats"), {{QStringLiteral("filter"), filter}});
        const Answer normalStats = ask(backend, QStringLiteral("GetResultStats"));
        QCOMPARE(stats.payload, normalStats.payload);
    }
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

    // B3-1 (owner's report; withdraws CS7-D4): the baseline is the count completed before
    // the software was installed, and every contributing completion it recorded is added
    // on top, whenever it ended - a record ending before the effective time, or carrying
    // no time at all, included. baseline_effective_at is still stored and answered, as
    // information only. The mock left out what ended before a changed baseline's time, so
    // 227 -> 229 with 5 completions recorded read 229 instead of 234.
    void progressAddsEveryContributingCompletionOnTopOfTheBaseline()
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
        QJsonObject offGoal = completed(QStringLiteral("66666666-6666-4666-8666-666666666666"),
                                        QStringLiteral("2026-09-06T13:00:00.000Z"),
                                        QStringLiteral("2026-09-06T13:01:00.000Z"),
                                        QStringLiteral("2026-09-06T13:20:00.000Z"));
        offGoal.insert(QStringLiteral("contributes_to_goal"), false);
        backend.resetRuns(QJsonArray{
            // Ended before the baseline's effective time.
            completed(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                      QStringLiteral("2026-09-04T23:00:00.000Z"), QStringLiteral("2026-09-04T23:01:00.000Z"),
                      QStringLiteral("2026-09-04T23:59:59.999Z")),
            // Ended exactly then, and later.
            completed(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                      QStringLiteral("2026-09-04T23:30:00.000Z"), QStringLiteral("2026-09-04T23:31:00.000Z"),
                      effective),
            completed(QStringLiteral("33333333-3333-4333-8333-333333333333"),
                      QStringLiteral("2026-09-06T12:00:00.000Z"), QStringLiteral("2026-09-06T12:01:00.000Z"),
                      QStringLiteral("2026-09-06T12:20:00.000Z")),
            // No end.
            completed(QStringLiteral("44444444-4444-4444-8444-444444444444"),
                      QStringLiteral("2026-09-07T12:00:00.000Z"), QStringLiteral("2026-09-07T12:01:00.000Z"),
                      none),
            // No time at all.
            completed(QStringLiteral("55555555-5555-4555-8555-555555555555"), none, none, none),
            // Taken off the goal: not part of the progress (review OK-1).
            offGoal});

        const auto save = [&backend](int baseline, const QString &at) {
            return ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                       {{QStringLiteral("goal_count"), 2000},
                        {QStringLiteral("baseline_completed_count"), baseline},
                        {QStringLiteral("baseline_effective_at"), at},
                        {QStringLiteral("reason"), QString::fromUtf8("核对")}});
        };
        const Answer set = save(227, effective);
        QVERIFY(set.ok);
        QCOMPARE(set.payload.value(QStringLiteral("baseline_effective_at")).toString(), effective);
        QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("achievement_progress")).toInt(), 232);
        QCOMPARE(stats.value(QStringLiteral("remaining")).toInt(), 1768);

        // The owner's change: a new baseline, effective now - every record still counts.
        const QString now = QStringLiteral("2026-10-04T08:00:00.000Z");
        const Answer changed = save(229, now);
        QVERIFY(changed.ok);
        QCOMPARE(changed.payload.value(QStringLiteral("baseline_effective_at")).toString(), now);
        stats = backend.dashboardStats({});
        QCOMPARE(stats.value(QStringLiteral("baseline_completed_count")).toInt(), 229);
        QCOMPARE(stats.value(QStringLiteral("achievement_progress")).toInt(), 234);
        QCOMPARE(stats.value(QStringLiteral("remaining")).toInt(), 1766);

        const Answer zero = save(0, QStringLiteral("2026-10-04T09:00:00.000Z"));
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
            QStringLiteral("contributes_to_goal"), QStringLiteral("note"), QStringLiteral("joined_in_progress"),
            QStringLiteral("soft_deleted"), QStringLiteral("pending_review"), QStringLiteral("manually_corrected")};
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

    // Joined in progress, mirrored from the Collector: the dashboard counts it over the attempts
    // with the share over the attempts whose entry was seen (docs/statistics-definitions.md 12.2),
    // the filter lists only the runs recorded that way, and filling the flag in where the
    // software saw nothing is not a correction while overruling a recorded value is.
    void joinedInProgressIsCountedListedAndCorrectedLikeTheCollectorDoes()
    {
        QJsonObject joined = autoRun(QStringLiteral("44444444-4444-4444-8444-444444444444"),
                                     QStringLiteral("COMPLETED"), true);
        joined.insert(QStringLiteral("joined_in_progress"), true);
        QJsonObject fresh = autoRun(QStringLiteral("55555555-5555-4555-8555-555555555555"),
                                    QStringLiteral("COMPLETED"), true);
        fresh.insert(QStringLiteral("joined_in_progress"), false);
        const QJsonObject unseen = autoRun(QStringLiteral("66666666-6666-4666-8666-666666666666"),
                                           QStringLiteral("COMPLETED"), true);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{joined, fresh, unseen});

        const Answer dashboard = ask(backend, QStringLiteral("GetDashboardStats"),
                                     {{QStringLiteral("filter"), QJsonObject()}});
        QVERIFY2(dashboard.ok, qPrintable(dashboard.code + QLatin1Char(' ') + dashboard.message));
        QCOMPARE(dashboard.payload.value(QStringLiteral("attempt_count")).toInt(), 3);
        QCOMPARE(dashboard.payload.value(QStringLiteral("joined_in_progress_count")).toInt(), 1);
        QCOMPARE(dashboard.payload.value(QStringLiteral("joined_in_progress_rate")).toDouble(), 0.5);

        const Answer listed = ask(backend, QStringLiteral("QueryRuns"),
                                  {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("joined_in_progress"), true}}},
                                   {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 50}});
        QVERIFY2(listed.ok, qPrintable(listed.code + QLatin1Char(' ') + listed.message));
        const QJsonArray items = listed.payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(items.size(), 1);
        QCOMPARE(items.first().toObject().value(QStringLiteral("run_id")).toString(),
                 joined.value(QStringLiteral("run_id")).toString());

        const auto marked = [](const Answer &answer) {
            return answer.payload.value(QStringLiteral("run")).toObject()
                .value(QStringLiteral("manually_corrected")).toBool();
        };
        const QString unseenId = unseen.value(QStringLiteral("run_id")).toString();
        Answer answer = correct(backend, unseenId, {{QStringLiteral("joined_in_progress"), true}});
        QVERIFY2(answer.ok, qPrintable(answer.code + QLatin1Char(' ') + answer.message));
        QVERIFY(!marked(answer));
        answer = correct(backend, unseenId, {{QStringLiteral("joined_in_progress"), false}});
        QVERIFY2(answer.ok, qPrintable(answer.code + QLatin1Char(' ') + answer.message));
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
    // Wherever the mock imitates a Collector sentence it sends the catalogue key
    // beside the Chinese, exactly as the Collector does (contracts/collector-texts.md
    // §2.1), so screenshots in other languages show what a player would see.
    void collectorSentencesTravelWithTheirKeys()
    {
        mr::MockBackend backend;
        backend.setLiveMode(mr::MockBackend::LiveMode::None);
        backend.setMidstreamSuspected(true);
        backend.setUpdateAvailable(true);
        backend.setUpdateDownloadFixture(QStringLiteral("failed"));
        Answer status = ask(backend, QStringLiteral("GetStatus"));
        QVERIFY(status.ok);
        const QJsonObject capture = status.payload.value(QStringLiteral("capture")).toObject();
        QVERIFY(sameSentence(capture.value(QStringLiteral("hint")), capture.value(QStringLiteral("hint_ref"))));
        const QJsonArray warnings = status.payload.value(QStringLiteral("warnings")).toArray();
        const QJsonArray warningRefs = status.payload.value(QStringLiteral("warning_refs")).toArray();
        QCOMPARE(warningRefs.size(), warnings.size());
        QVERIFY(warningRefs.at(0).isNull()); // the mock's own line
        const QJsonObject download = status.payload.value(QStringLiteral("update")).toObject()
                                         .value(QStringLiteral("download")).toObject();
        QCOMPARE(download.value(QStringLiteral("state")).toString(), QStringLiteral("FAILED"));
        QVERIFY(sameSentence(download.value(QStringLiteral("message")), download.value(QStringLiteral("message_ref"))));

        backend.setNpcapMissing(true);
        status = ask(backend, QStringLiteral("GetStatus"));
        const QJsonObject npcap = status.payload.value(QStringLiteral("npcap")).toObject();
        QVERIFY(sameSentence(npcap.value(QStringLiteral("install_hint")), npcap.value(QStringLiteral("install_hint_ref"))));
        const QJsonArray npcapWarnings = status.payload.value(QStringLiteral("warnings")).toArray();
        QCOMPARE(npcapWarnings.size(), 2);
        QVERIFY(sameSentence(npcapWarnings.at(1), status.payload.value(QStringLiteral("warning_refs")).toArray().at(1)));
        const Answer adapters = ask(backend, QStringLiteral("ListCaptureAdapters"));
        QVERIFY(sameSentence(adapters.payload.value(QStringLiteral("install_hint")),
                             adapters.payload.value(QStringLiteral("install_hint_ref"))));
        backend.setNpcapMissing(false);

        for (const QString &fixture : {QStringLiteral("ready"), QStringLiteral("blocked")}) {
            backend.setCalibrationFixture(fixture);
            const QJsonObject calibration = ask(backend, QStringLiteral("GetStatus")).payload
                                                .value(QStringLiteral("capture")).toObject()
                                                .value(QStringLiteral("calibration")).toObject();
            const QJsonArray events = calibration.value(QStringLiteral("events")).toArray();
            QVERIFY(!events.isEmpty());
            for (const QJsonValue &event : events) {
                QVERIFY2(sameSentence(event.toObject().value(QStringLiteral("label")),
                                      event.toObject().value(QStringLiteral("label_ref"))),
                         qPrintable(event.toObject().value(QStringLiteral("label")).toString()));
            }
            const QJsonArray blockers = calibration.value(QStringLiteral("blockers")).toArray();
            const QJsonArray blockerRefs = calibration.value(QStringLiteral("blocker_refs")).toArray();
            QCOMPARE(blockerRefs.size(), blockers.size());
            for (qsizetype i = 0; i < blockers.size(); ++i)
                QVERIFY(sameSentence(blockers.at(i), blockerRefs.at(i)));
            if (fixture == QLatin1String("blocked"))
                QCOMPARE(blockers.size(), 1);
        }

        backend.setValidationFixture(QStringLiteral("RECORDING"));
        const Answer validation = ask(backend, QStringLiteral("GetCaptureValidationStatus"));
        QVERIFY(sameSentence(validation.payload.value(QStringLiteral("message")),
                             validation.payload.value(QStringLiteral("message_ref"))));

        // The Azure voice labels are catalogue sentences; the OpenAI names are names.
        const Answer speech = ask(backend, QStringLiteral("GetSpeechSettings"));
        const QJsonArray azure = speech.payload.value(QStringLiteral("azure_voices")).toArray();
        QVERIFY(!azure.isEmpty());
        for (const QJsonValue &voice : azure) {
            QVERIFY(sameSentence(voice.toObject().value(QStringLiteral("label")),
                                 voice.toObject().value(QStringLiteral("label_ref"))));
        }
        for (const QJsonValue &voice : speech.payload.value(QStringLiteral("openai_voices")).toArray())
            QVERIFY(!voice.toObject().contains(QStringLiteral("label_ref")));

        // A refusal: message_ref beside the message, as $defs/ErrorPayload carries it.
        // The validation fixture stopped the formal capture: stopping it is refused.
        mr::BackendReply *reply = backend.request(QStringLiteral("StopCapture"), {});
        const QPointer<mr::BackendReply> guard(reply);
        QString message;
        QVariant ref;
        bool done = false;
        reply->whenDone(&backend, [&](bool ok, const QVariantMap &, const QString &code, const QString &text) {
            QVERIFY(!ok);
            QCOMPARE(code, QStringLiteral("ERR_CAPTURE_NOT_RUNNING"));
            message = text;
            ref = guard ? guard->errorRef() : QVariant();
            done = true;
        });
        QTRY_VERIFY(done);
        QVERIFY(sameSentence(message, QJsonObject::fromVariantMap(ref.toMap())));
        QCOMPARE(ref.toMap().value(QStringLiteral("key")).toString(), QStringLiteral("capture.stop.not_running"));
        // A refusal the mock wrote itself carries none.
        const Answer unknown = ask(backend, QStringLiteral("NoSuchMessage"));
        QVERIFY(!unknown.ok);
    }

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

    // J-2: StatisticsRepository.GetDungeonStats - one row per duty identity (DutyKey: the
    // content_id, else the one duty the observed zone hosts, else the zone itself), every
    // group listed even when nothing in it was an attempt, named by the most recently
    // updated run that names it and otherwise by the catalogue, last seen at its latest
    // match, and ordered by attempts, then content_id (null first), then name.
    void dungeonStatisticsGroupByDutyIdentityLikeTheCollector()
    {
        const mr::DutyCatalog *duties = mr::DutyCatalog::shared();
        const QVariantMap sastasha = duties->lookup(4);
        const qint64 sastashaZone = sastasha.value(QStringLiteral("territory_id")).toLongLong();
        QVERIFY(sastashaZone > 0);
        // A zone three duties share: the catalogue names it by its lowest content id
        // (DutyCatalog.FindByTerritory) but it never identifies one of them.
        const qint64 sharedZone = 832;
        const QString sharedZoneName = duties->lookup(640).value(QStringLiteral("duty_name")).toString();
        QCOMPARE(duties->lookup(641).value(QStringLiteral("territory_id")).toLongLong(), sharedZone);
        QVERIFY(!sharedZoneName.isEmpty());

        const auto run = [](const char *id, const char *result, bool entered, const QJsonValue &content,
                            const QJsonValue &zone, const QJsonValue &name, const char *updated) {
            QJsonObject value = autoRun(QString::fromLatin1(id), QString::fromLatin1(result), entered);
            value.insert(QStringLiteral("content_id"), content);
            value.insert(QStringLiteral("territory_id"), zone);
            value.insert(QStringLiteral("duty_name"), name);
            value.insert(QStringLiteral("duty_category"), QJsonValue(QJsonValue::Null));
            value.insert(QStringLiteral("updated_at_utc"), QString::fromLatin1(updated));
            return value;
        };
        const QJsonValue none(QJsonValue::Null);
        QJsonObject cancelledEarly = run("44444444-4444-4444-8444-444444444444", "CANCELLED_BEFORE_ENTRY", false,
                                         17, none, none, "2026-09-01T12:10:00.000Z");
        cancelledEarly.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-03T08:00:00.000Z"));
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{
            run("11111111-1111-4111-8111-111111111111", "COMPLETED", true, 4, sastashaZone,
                QString::fromUtf8("沙斯塔夏溶洞"), "2026-09-01T12:10:00.000Z"),
            run("22222222-2222-4222-8222-222222222222", "COMPLETED", true, none, sastashaZone, none,
                "2026-09-02T12:10:00.000Z"),
            cancelledEarly,
            run("55555555-5555-4555-8555-555555555555", "CANCELLED_BEFORE_ENTRY", false, 2, none, none,
                "2026-09-01T12:10:00.000Z"),
            run("66666666-6666-4666-8666-666666666666", "LEFT_OR_ABANDONED", true, none, sharedZone, none,
                "2026-09-01T12:10:00.000Z"),
            run("77777777-7777-4777-8777-777777777777", "COMPLETED", true, none, none, none,
                "2026-09-01T12:10:00.000Z")});

        const QJsonArray rows = backend.dungeonStats({});
        QCOMPARE(rows.size(), 5);
        const auto row = [&rows](int index) { return rows.at(index).toObject(); };

        // The zone-only run joins the run that observed the content id: one duty, one row.
        QCOMPARE(row(0).value(QStringLiteral("content_id")).toInt(), 4);
        QCOMPARE(row(0).value(QStringLiteral("attempt_count")).toInt(), 2);
        QCOMPARE(row(0).value(QStringLiteral("completed_count")).toInt(), 2);
        QCOMPARE(row(0).value(QStringLiteral("duty_name")).toString(), QString::fromUtf8("沙斯塔夏溶洞"));
        QCOMPARE(row(0).value(QStringLiteral("duty_category")).toString(),
                 sastasha.value(QStringLiteral("duty_category")).toString());

        // One attempt each, both without a content id: ordered by name.
        const QString unknownDuty = QString::fromUtf8("未知副本");
        const bool zoneFirst = sharedZoneName < unknownDuty;
        const QJsonObject zoneRow = row(zoneFirst ? 1 : 2);
        const QJsonObject unknownRow = row(zoneFirst ? 2 : 1);
        QVERIFY(zoneRow.value(QStringLiteral("content_id")).isNull());
        QCOMPARE(zoneRow.value(QStringLiteral("duty_name")).toString(), sharedZoneName);
        QCOMPARE(zoneRow.value(QStringLiteral("attempt_count")).toInt(), 1);
        QVERIFY(unknownRow.value(QStringLiteral("content_id")).isNull());
        QCOMPARE(unknownRow.value(QStringLiteral("duty_name")).toString(), unknownDuty);
        QVERIFY(unknownRow.value(QStringLiteral("duty_category")).isNull());

        // No attempt at all: still listed, with a zero and no rate, content_id 2 before 17.
        QCOMPARE(row(3).value(QStringLiteral("content_id")).toInt(), 2);
        QCOMPARE(row(4).value(QStringLiteral("content_id")).toInt(), 17);
        QCOMPARE(row(4).value(QStringLiteral("attempt_count")).toInt(), 0);
        QVERIFY(row(4).value(QStringLiteral("completion_rate")).isNull());
        QCOMPARE(row(4).value(QStringLiteral("duty_name")).toString(),
                 duties->lookup(17).value(QStringLiteral("duty_name")).toString());
        QCOMPARE(row(4).value(QStringLiteral("last_seen_utc")).toString(),
                 QStringLiteral("2026-09-03T08:00:00.000Z"));

        // The most recently updated run that names the duty names the row.
        backend.resetRuns(QJsonArray{
            run("11111111-1111-4111-8111-111111111111", "COMPLETED", true, 4, sastashaZone,
                QString::fromUtf8("旧名字"), "2026-09-01T12:10:00.000Z"),
            run("22222222-2222-4222-8222-222222222222", "COMPLETED", true, 4, sastashaZone,
                QString::fromUtf8("新名字"), "2026-09-05T12:10:00.000Z"),
            run("33333333-3333-4333-8333-333333333333", "COMPLETED", true, 4, sastashaZone,
                QString::fromUtf8("中间的名字"), "2026-09-03T12:10:00.000Z")});
        QCOMPARE(backend.dungeonStats({}).at(0).toObject().value(QStringLiteral("duty_name")).toString(),
                 QString::fromUtf8("新名字"));
    }

    // RV2-5 (audit 2026-10-20 H1-4, RV2-4): StatisticsRepository.DutyKey - a zone the duty
    // table does not know names no duty, so its runs join the runs without any duty identity
    // in the one 未知副本 row, and that row is called 未知副本 whatever a run in it calls its
    // duty. A zone several duties share keeps its own row.
    void runsInZonesTheDutyTableDoesNotKnowJoinTheOneUnknownDutyRow()
    {
        const mr::DutyCatalog *duties = mr::DutyCatalog::shared();
        const qint64 newZone = 99998;
        const qint64 otherNewZone = 99999;
        QVERIFY(duties->lookupByTerritory(newZone).isEmpty());
        QVERIFY(duties->lookupByTerritory(otherNewZone).isEmpty());
        const qint64 sharedZone = 832;
        QCOMPARE(duties->lookup(641).value(QStringLiteral("territory_id")).toLongLong(), sharedZone);
        const QString sharedZoneName = duties->lookup(640).value(QStringLiteral("duty_name")).toString();

        const auto run = [](const char *id, const char *result, const QJsonValue &zone, const QJsonValue &name,
                            const QJsonValue &category, const char *updated) {
            QJsonObject value = autoRun(QString::fromLatin1(id), QString::fromLatin1(result), true);
            value.insert(QStringLiteral("content_id"), QJsonValue(QJsonValue::Null));
            value.insert(QStringLiteral("territory_id"), zone);
            value.insert(QStringLiteral("duty_name"), name);
            value.insert(QStringLiteral("duty_category"), category);
            value.insert(QStringLiteral("updated_at_utc"), QString::fromLatin1(updated));
            return value;
        };
        const QJsonValue none(QJsonValue::Null);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{
            run("11111111-1111-4111-8111-111111111111", "COMPLETED", newZone, none, none, "2026-09-01T12:10:00.000Z"),
            run("22222222-2222-4222-8222-222222222222", "LEFT_OR_ABANDONED", otherNewZone, none, none,
                "2026-09-02T12:10:00.000Z"),
            run("33333333-3333-4333-8333-333333333333", "COMPLETED", none, none, none, "2026-09-03T12:10:00.000Z"),
            // An imported row whose 副本 cell matched no duty: free text, the most recent of the four.
            run("44444444-4444-4444-8444-444444444444", "COMPLETED", none, QString::fromUtf8("自由填写的副本"),
                QString::fromUtf8("自由填写的类别"), "2026-09-09T12:10:00.000Z"),
            run("55555555-5555-4555-8555-555555555555", "COMPLETED", sharedZone, none, none,
                "2026-09-01T12:10:00.000Z")});

        const QJsonArray rows = backend.dungeonStats({});
        QCOMPARE(rows.size(), 2);
        const QJsonObject unknown = rows.at(0).toObject();
        QVERIFY(unknown.value(QStringLiteral("content_id")).isNull());
        QCOMPARE(unknown.value(QStringLiteral("duty_name")).toString(), QString::fromUtf8("未知副本"));
        QVERIFY(unknown.value(QStringLiteral("duty_category")).isNull());
        QCOMPARE(unknown.value(QStringLiteral("attempt_count")).toInt(), 4);
        QCOMPARE(unknown.value(QStringLiteral("completed_count")).toInt(), 3);
        const QJsonObject shared = rows.at(1).toObject();
        QVERIFY(shared.value(QStringLiteral("content_id")).isNull());
        QCOMPARE(shared.value(QStringLiteral("duty_name")).toString(), sharedZoneName);
        QCOMPARE(shared.value(QStringLiteral("attempt_count")).toInt(), 1);
    }

    // J-2: StatisticsRepository.GetJobStats - one row per job_id, every group listed even
    // when nothing in it was an attempt, named and roled by the job catalogue rather than
    // by whatever the run carries, ordered by attempts, then job_id (null first).
    void jobStatisticsNameJobsFromTheCatalogueLikeTheCollector()
    {
        const mr::JobCatalog jobs;
        const auto run = [](const char *id, const char *result, bool entered, const QJsonValue &job,
                            const char *name, const char *role) {
            QJsonObject value = autoRun(QString::fromLatin1(id), QString::fromLatin1(result), entered);
            value.insert(QStringLiteral("job_id"), job);
            value.insert(QStringLiteral("job_name"), QString::fromUtf8(name));
            value.insert(QStringLiteral("role"), QString::fromLatin1(role));
            return value;
        };
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{
            run("11111111-1111-4111-8111-111111111111", "CANCELLED_BEFORE_ENTRY", false, 24, "白魔法师", "HEALER"),
            run("22222222-2222-4222-8222-222222222222", "COMPLETED", true, 21, "战士", "TANK"),
            run("33333333-3333-4333-8333-333333333333", "COMPLETED", true, 19, "改过的名字", "DPS"),
            run("44444444-4444-4444-8444-444444444444", "COMPLETED", true, QJsonValue(QJsonValue::Null),
                "某个职业", "DPS")});

        const QJsonArray rows = backend.jobStats({});
        QCOMPARE(rows.size(), 4);
        const auto row = [&rows](int index) { return rows.at(index).toObject(); };
        QVERIFY(row(0).value(QStringLiteral("job_id")).isNull());
        QCOMPARE(row(0).value(QStringLiteral("job_name")).toString(), QString::fromUtf8("未知"));
        QCOMPARE(row(0).value(QStringLiteral("role")).toString(), QStringLiteral("UNKNOWN"));
        QCOMPARE(row(1).value(QStringLiteral("job_id")).toInt(), 19);
        QCOMPARE(row(1).value(QStringLiteral("job_name")).toString(), jobs.jobName(19));
        QCOMPARE(row(1).value(QStringLiteral("role")).toString(), jobs.role(19));
        QCOMPARE(row(1).value(QStringLiteral("role")).toString(), QStringLiteral("TANK"));
        QCOMPARE(row(2).value(QStringLiteral("job_id")).toInt(), 21);
        QCOMPARE(row(3).value(QStringLiteral("job_id")).toInt(), 24);
        QCOMPARE(row(3).value(QStringLiteral("attempt_count")).toInt(), 0);
        QCOMPARE(row(3).value(QStringLiteral("completed_count")).toInt(), 0);
        QVERIFY(row(3).value(QStringLiteral("completion_rate")).isNull());
    }

    // J-3a: RunMutationService.CorrectRun - a correction may not put an automatic run into
    // the shape of one still in flight (UNKNOWN, no end, not pending review): it would leave
    // every statistic and the review list and never be closed again. The run the state
    // machine is still following has that shape already and stays correctable.
    void aCorrectionCannotPutAnAutomaticRunBackInFlight()
    {
        const QString runId = QStringLiteral("11111111-1111-4111-8111-111111111111");
        const QString openId = QStringLiteral("22222222-2222-4222-8222-222222222222");
        QJsonObject open = autoRun(openId, QStringLiteral("UNKNOWN"), true);
        open.insert(QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null));
        open.insert(QStringLiteral("duration_ms"), QJsonValue(QJsonValue::Null));
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{autoRun(runId, QStringLiteral("COMPLETED"), true), open});

        const Answer refused = correct(backend, runId, {{QStringLiteral("result"), QStringLiteral("UNKNOWN")},
                                                        {QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null)}});
        QCOMPARE(refused.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(refused.message, collectorChinese("mutation.unknown_needs_end"));
        const QJsonObject stored = ask(backend, QStringLiteral("QueryRuns"),
                                       {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("run_id"), QJsonArray{runId}}}}})
                                       .payload.value(QStringLiteral("items")).toArray().at(0).toObject();
        QCOMPARE(stored.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));
        QCOMPARE(stored.value(QStringLiteral("revision")).toInt(), 1);

        // An unknown result with an end time is a closed run, and may be recorded.
        QVERIFY(correct(backend, runId, {{QStringLiteral("result"), QStringLiteral("UNKNOWN")}}).ok);
        // The run still being followed may be corrected as before.
        QVERIFY(correct(backend, openId, {{QStringLiteral("note"), QStringLiteral("x")}}).ok);
    }

    // J-3b: RunMutationService.Apply / BatchMutateRuns - a mutation resent under the same
    // request_id (HistoryController keeps the id of a request it stopped waiting for) is
    // answered from its first outcome and applied once; the same id for another request is
    // refused with ERR_IDEMPOTENCY_CONFLICT (contracts/error-codes.md).
    void aResentMutationIsAppliedOnceAndAnsweredAsAReplay()
    {
        mr::MockBackend backend;
        const int before = runCount(backend);
        const QJsonObject manual{
            {QStringLiteral("content_id"), 4},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-02T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-02T12:01:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-02T12:20:00.000Z")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("reason"), QString::fromUtf8("补录")}};
        const QString createId = QStringLiteral("aaaaaaaa-0000-4000-8000-000000000001");
        const Answer created = askWithId(backend, QStringLiteral("CreateManualRun"), manual, createId);
        const Answer resent = askWithId(backend, QStringLiteral("CreateManualRun"), manual, createId);
        QVERIFY2(created.ok, qPrintable(created.code));
        QVERIFY2(resent.ok, qPrintable(resent.code));
        QVERIFY(created.payload.contains(QStringLiteral("idempotent_replay")));
        QVERIFY(!created.payload.value(QStringLiteral("idempotent_replay")).toBool());
        QVERIFY(resent.payload.value(QStringLiteral("idempotent_replay")).toBool());
        const QString runId = created.payload.value(QStringLiteral("run_id")).toString();
        QCOMPARE(resent.payload.value(QStringLiteral("run_id")).toString(), runId);
        QCOMPARE(runCount(backend), before + 1);

        QJsonObject other = manual;
        other.insert(QStringLiteral("note"), QStringLiteral("x"));
        const Answer reused = askWithId(backend, QStringLiteral("CreateManualRun"), other, createId);
        QCOMPARE(reused.code, QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
        QCOMPARE(reused.message, collectorChinese("idempotency.request_reused"));
        QCOMPARE(runCount(backend), before + 1);

        const QJsonObject correction{
            {QStringLiteral("run_id"), runId}, {QStringLiteral("expected_revision"), 1},
            {QStringLiteral("changes"), QJsonObject{{QStringLiteral("note"), QString::fromUtf8("核对")}}},
            {QStringLiteral("reason"), QString::fromUtf8("核对")}};
        const QString correctId = QStringLiteral("aaaaaaaa-0000-4000-8000-000000000002");
        QVERIFY(askWithId(backend, QStringLiteral("CorrectRun"), correction, correctId).ok);
        const Answer again = askWithId(backend, QStringLiteral("CorrectRun"), correction, correctId);
        QVERIFY2(again.ok, qPrintable(again.code)); // not ERR_REVISION_CONFLICT, not applied twice
        QVERIFY(again.payload.value(QStringLiteral("idempotent_replay")).toBool());
        QCOMPARE(again.payload.value(QStringLiteral("revision")).toInt(), 2);
        const auto revisions = [&backend, &runId] {
            return ask(backend, QStringLiteral("GetRunRevisions"), {{QStringLiteral("run_id"), runId}})
                .payload.value(QStringLiteral("items")).toArray().size();
        };
        QCOMPARE(revisions(), 2);

        const QJsonObject batch{
            {QStringLiteral("action"), QStringLiteral("soft_delete")},
            {QStringLiteral("reason"), QString::fromUtf8("重复")},
            {QStringLiteral("runs"), QJsonArray{QJsonObject{{QStringLiteral("run_id"), runId},
                                                            {QStringLiteral("expected_revision"), 2}}}}};
        const QString batchId = QStringLiteral("aaaaaaaa-0000-4000-8000-000000000003");
        const Answer deleted = askWithId(backend, QStringLiteral("BatchMutateRuns"), batch, batchId);
        QVERIFY2(deleted.ok, qPrintable(deleted.code));
        const Answer replayed = askWithId(backend, QStringLiteral("BatchMutateRuns"), batch, batchId);
        QVERIFY2(replayed.ok, qPrintable(replayed.code)); // not ERR_ALREADY_DELETED
        QVERIFY(replayed.payload.value(QStringLiteral("idempotent_replay")).toBool());
        QCOMPARE(replayed.payload.value(QStringLiteral("changed_count")).toInt(), 1);
        QCOMPARE(revisions(), 3);

        QJsonObject restore = batch;
        restore.insert(QStringLiteral("action"), QStringLiteral("restore"));
        restore.insert(QStringLiteral("runs"), QJsonArray{QJsonObject{{QStringLiteral("run_id"), runId},
                                                                      {QStringLiteral("expected_revision"), 3}}});
        const Answer reusedBatch = askWithId(backend, QStringLiteral("BatchMutateRuns"), restore, batchId);
        QCOMPARE(reusedBatch.code, QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
        QCOMPARE(reusedBatch.message, collectorChinese("history.batch.request_reused"));
        QCOMPARE(revisions(), 3);
    }

    // J-3c: RunMutationService.BatchMutateRuns - a selection whose deletion state does not
    // fit the action is refused with the Collector's own codes and sentences, nothing is
    // applied, and an applied batch says it was no replay.
    void aBatchRefusesAMismatchedDeletionStateWithTheCollectorsCodes()
    {
        const QString liveId = QStringLiteral("11111111-1111-4111-8111-111111111111");
        const QString deletedId = QStringLiteral("22222222-2222-4222-8222-222222222222");
        QJsonObject deleted = autoRun(deletedId, QStringLiteral("COMPLETED"), true);
        deleted.insert(QStringLiteral("soft_deleted"), true);
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{autoRun(liveId, QStringLiteral("COMPLETED"), true), deleted});
        const auto batch = [&backend](const char *action, const QString &runId) {
            return ask(backend, QStringLiteral("BatchMutateRuns"),
                       {{QStringLiteral("action"), QString::fromLatin1(action)},
                        {QStringLiteral("reason"), QString::fromUtf8("整理")},
                        {QStringLiteral("runs"), QJsonArray{QJsonObject{{QStringLiteral("run_id"), runId},
                                                                        {QStringLiteral("expected_revision"), 1}}}}});
        };

        const Answer again = batch("soft_delete", deletedId);
        QCOMPARE(again.code, QStringLiteral("ERR_ALREADY_DELETED"));
        QCOMPARE(again.message, collectorChinese("history.batch.already_deleted"));
        for (const char *action : {"restore", "purge"}) {
            const Answer refused = batch(action, liveId);
            QCOMPARE(refused.code, QStringLiteral("ERR_NOT_DELETED"));
            QCOMPARE(refused.message, collectorChinese("history.batch.not_deleted"));
        }
        QCOMPARE(runCount(backend), 2);

        const Answer applied = batch("soft_delete", liveId);
        QVERIFY2(applied.ok, qPrintable(applied.code));
        QCOMPARE(applied.payload.value(QStringLiteral("changed_count")).toInt(), 1);
        QVERIFY(applied.payload.contains(QStringLiteral("idempotent_replay")));
        QVERIFY(!applied.payload.value(QStringLiteral("idempotent_replay")).toBool());
    }

    // J-3d: RequestParsers.Paging + RunRepository.Query - a page or page_size out of range
    // is refused, never clamped, and a page past the end is answered as asked and empty, so
    // the history list's own past-the-end recovery (RunListModel) runs offline as well.
    void queryRunsRefusesOutOfRangePagingAndAnswersAPagePastTheEnd()
    {
        mr::MockBackend backend;
        const auto query = [&backend](const QJsonObject &paging) {
            QJsonObject payload = paging;
            payload.insert(QStringLiteral("filter"), QJsonObject());
            return ask(backend, QStringLiteral("QueryRuns"), payload);
        };
        for (int size : {0, 201}) {
            const Answer refused = query({{QStringLiteral("page"), 1}, {QStringLiteral("page_size"), size}});
            QCOMPARE(refused.code, QStringLiteral("ERR_BAD_REQUEST"));
            QCOMPARE(refused.message, collectorChinese("ipc.field.out_of_range",
                                                       {{QStringLiteral("field"), QStringLiteral("page_size")},
                                                        {QStringLiteral("min"), 1}, {QStringLiteral("max"), 200}}));
        }
        QCOMPARE(query({{QStringLiteral("page"), 0}, {QStringLiteral("page_size"), 50}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));

        const Answer defaults = query({});
        QVERIFY(defaults.ok);
        const QJsonObject defaultInfo = defaults.payload.value(QStringLiteral("page_info")).toObject();
        QCOMPARE(defaultInfo.value(QStringLiteral("page")).toInt(), 1);
        QCOMPARE(defaultInfo.value(QStringLiteral("page_size")).toInt(), 50);
        const int total = defaultInfo.value(QStringLiteral("total")).toInt();
        QVERIFY(total > 0);

        const Answer past = query({{QStringLiteral("page"), 99}, {QStringLiteral("page_size"), 50}});
        QVERIFY(past.ok);
        QVERIFY(past.payload.value(QStringLiteral("items")).toArray().isEmpty());
        const QJsonObject pastInfo = past.payload.value(QStringLiteral("page_info")).toObject();
        QCOMPARE(pastInfo.value(QStringLiteral("page")).toInt(), 99);
        QCOMPARE(pastInfo.value(QStringLiteral("total")).toInt(), total);
    }

    // J-3e: RunFilterSql - pending_review true is the actionable review list, which leaves
    // imported history out (source IMPORT, import metadata, or an IMPORT creation revision),
    // in the listing as in the calendar.
    void thePendingReviewListLeavesOutImportedHistory()
    {
        const QString flaggedId = QStringLiteral("11111111-1111-4111-8111-111111111111");
        QJsonObject flagged = autoRun(flaggedId, QStringLiteral("INTERRUPTED"), true);
        flagged.insert(QStringLiteral("pending_review"), true);
        QJsonObject imported = autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                                       QStringLiteral("UNKNOWN"), true);
        imported.insert(QStringLiteral("source"), QStringLiteral("IMPORT"));
        imported.insert(QStringLiteral("mentor_roulette_id"), QJsonValue(QJsonValue::Null));
        imported.insert(QStringLiteral("pending_review"), true);
        QJsonObject withMetadata = autoRun(QStringLiteral("33333333-3333-4333-8333-333333333333"),
                                           QStringLiteral("UNKNOWN"), true);
        withMetadata.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
        withMetadata.insert(QStringLiteral("pending_review"), true);
        withMetadata.insert(QStringLiteral("import_metadata"),
                            QJsonObject{{QStringLiteral("source_kind"), QStringLiteral("CSV")},
                                        {QStringLiteral("source_name"), QJsonValue(QJsonValue::Null)},
                                        {QStringLiteral("source_recorded_at"), QJsonValue(QJsonValue::Null)},
                                        {QStringLiteral("source_recorded_at_utc"), QJsonValue(QJsonValue::Null)},
                                        {QStringLiteral("imported_at_utc"), QStringLiteral("2026-09-03T12:00:00.000Z")},
                                        {QStringLiteral("incomplete"), true}});
        mr::MockBackend backend;
        backend.resetRuns(QJsonArray{flagged, imported, withMetadata});

        const Answer listed = ask(backend, QStringLiteral("QueryRuns"),
                                  {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("pending_review"), true}}}});
        QVERIFY(listed.ok);
        const QJsonArray items = listed.payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(items.size(), 1);
        QCOMPARE(items.at(0).toObject().value(QStringLiteral("run_id")).toString(), flaggedId);
        QCOMPARE(listed.payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")).toInt(), 1);
        // Without the filter all three are listed.
        QCOMPARE(runCount(backend), 3);
    }

    // J-4: the offline backend answers every message of the contract, or refuses it on
    // purpose - never with 「模拟后端不支持该消息」, the answer to a message it forgot.
    // Event and Error are what the Collector sends; nobody requests them.
    void everyContractMessageIsAnsweredOrDeliberatelyRefused()
    {
        QFile schema(QStringLiteral(MR_SOURCE_DIR "/contracts/ipc-v1.schema.json"));
        QVERIFY(schema.open(QIODevice::ReadOnly));
        const QJsonArray types = QJsonDocument::fromJson(schema.readAll()).object()
                                     .value(QStringLiteral("$defs")).toObject()
                                     .value(QStringLiteral("MessageType")).toObject()
                                     .value(QStringLiteral("enum")).toArray();
        QVERIFY(types.size() > 50);
        const QSet<QString> notRequests{QStringLiteral("Event"), QStringLiteral("Error")};
        const QString forgotten = QString::fromUtf8("模拟后端不支持该消息");
        QTemporaryDir data;
        QVERIFY(data.isValid());
        mr::MockBackend backend;
        backend.setDataDirectory(data.path());
        for (const QJsonValue &value : types) {
            const QString type = value.toString();
            const Answer answer = ask(backend, type);
            QVERIFY2(answer.done, qPrintable(type));
            if (notRequests.contains(type)) {
                QVERIFY2(!answer.ok, qPrintable(type));
                continue;
            }
            QVERIFY2(!answer.message.startsWith(forgotten),
                     qPrintable(type + QStringLiteral(": ") + answer.message));
        }
    }

    // J-4: RunImportService - a preview of edited candidate rows (the screenshot path, and
    // every 重新校验) is classified as a whole and handed out in pages that share one
    // summary; the commit saves the selected rows once, deducts the completions the player
    // says the baseline already holds, and answers a resend from its receipt.
    void anImportIsPreviewedPageByPageAndCommittedOnce()
    {
        const mr::JobCatalog jobs;
        const QString sastasha = mr::DutyCatalog::shared()->lookup(4).value(QStringLiteral("duty_name")).toString();
        QVERIFY(!sastasha.isEmpty());
        const QJsonArray rows{
            QJsonObject{{QStringLiteral("duty_name"), sastasha}, {QStringLiteral("job_name"), jobs.jobName(19)},
                        {QStringLiteral("result"), QStringLiteral("COMPLETED")},
                        {QStringLiteral("source_recorded_at"), QStringLiteral("2026-08-01")}},
            QJsonObject{{QStringLiteral("content_id"), 17}, {QStringLiteral("job_id"), 24},
                        {QStringLiteral("result"), QString::fromUtf8("完成")},
                        {QStringLiteral("entered_at_utc"), QStringLiteral("2026-08-02T12:01:00.000Z")},
                        {QStringLiteral("ended_at_utc"), QStringLiteral("2026-08-02T12:20:00.000Z")}},
            QJsonObject{}};
        const QJsonObject source{{QStringLiteral("source_kind"), QStringLiteral("SCREENSHOT")},
                                 {QStringLiteral("source_name"), QString::fromUtf8("截图")},
                                 {QStringLiteral("time_zone"), QStringLiteral("Asia/Shanghai")},
                                 {QStringLiteral("page_size"), 2}, {QStringLiteral("rows"), rows}};
        mr::MockBackend backend;
        backend.setAchievement(2000, 10);
        const int before = runCount(backend);

        const Answer first = ask(backend, QStringLiteral("PreviewRunImport"), source);
        QVERIFY2(first.ok, qPrintable(first.code + QStringLiteral(" ") + first.message));
        const QString previewId = first.payload.value(QStringLiteral("preview_id")).toString();
        QVERIFY(!previewId.isEmpty());
        QCOMPARE(first.payload.value(QStringLiteral("source_kind")).toString(), QStringLiteral("SCREENSHOT"));
        const QJsonObject info = first.payload.value(QStringLiteral("page_info")).toObject();
        QCOMPARE(info.value(QStringLiteral("page")).toInt(), 1);
        QCOMPARE(info.value(QStringLiteral("page_size")).toInt(), 2);
        QCOMPARE(info.value(QStringLiteral("total")).toInt(), 3);
        const QJsonObject summary = first.payload.value(QStringLiteral("summary")).toObject();
        QCOMPARE(summary.value(QStringLiteral("new")).toInt(), 2);
        QCOMPARE(summary.value(QStringLiteral("invalid")).toInt(), 1);
        QCOMPARE(summary.value(QStringLiteral("incomplete")).toInt(), 1);
        QCOMPARE(summary.value(QStringLiteral("total")).toInt(), 3);
        const QJsonArray firstRows = first.payload.value(QStringLiteral("rows")).toArray();
        QCOMPARE(firstRows.size(), 2);
        const QJsonObject named = firstRows.at(0).toObject();
        QCOMPARE(named.value(QStringLiteral("row_number")).toInt(), 1);
        QCOMPARE(named.value(QStringLiteral("status")).toString(), QStringLiteral("new"));
        QVERIFY(named.value(QStringLiteral("can_import")).toBool());
        QVERIFY(named.value(QStringLiteral("incomplete")).toBool());
        const QJsonObject namedRun = named.value(QStringLiteral("run")).toObject();
        QCOMPARE(namedRun.value(QStringLiteral("content_id")).toInt(), 4);
        QCOMPARE(namedRun.value(QStringLiteral("job_id")).toInt(), 19);
        QCOMPARE(namedRun.value(QStringLiteral("source")).toString(), QStringLiteral("IMPORT"));
        QCOMPARE(namedRun.value(QStringLiteral("import_metadata")).toObject()
                     .value(QStringLiteral("source_recorded_at")).toString(), QStringLiteral("2026-08-01"));
        const QJsonArray warnings = named.value(QStringLiteral("warnings")).toArray();
        const QJsonArray warningRefs = named.value(QStringLiteral("warning_refs")).toArray();
        QCOMPARE(warningRefs.size(), warnings.size());
        QVERIFY(warnings.contains(collectorChinese("import.warning.source_date_only")));
        QVERIFY(warnings.contains(collectorChinese("import.warning.time_missing")));
        for (qsizetype i = 0; i < warnings.size(); ++i)
            QVERIFY(sameSentence(warnings.at(i), warningRefs.at(i)));
        const QJsonObject timed = firstRows.at(1).toObject().value(QStringLiteral("run")).toObject();
        QCOMPARE(timed.value(QStringLiteral("duration_ms")).toDouble(), 1140000.0);
        QCOMPARE(timed.value(QStringLiteral("result")).toString(), QStringLiteral("COMPLETED"));

        const Answer second = ask(backend, QStringLiteral("PreviewRunImport"),
                                  {{QStringLiteral("preview_id"), previewId}, {QStringLiteral("page"), 2}});
        QVERIFY2(second.ok, qPrintable(second.message));
        QCOMPARE(second.payload.value(QStringLiteral("summary")).toObject(), summary);
        const QJsonArray secondRows = second.payload.value(QStringLiteral("rows")).toArray();
        QCOMPARE(secondRows.size(), 1);
        const QJsonObject empty = secondRows.at(0).toObject();
        QCOMPARE(empty.value(QStringLiteral("row_number")).toInt(), 3);
        QCOMPARE(empty.value(QStringLiteral("status")).toString(), QStringLiteral("invalid"));
        QVERIFY(empty.value(QStringLiteral("run")).isNull());
        QCOMPARE(empty.value(QStringLiteral("errors")).toArray().at(0).toString(), collectorChinese("import.row.empty"));
        QCOMPARE(ask(backend, QStringLiteral("PreviewRunImport"),
                     {{QStringLiteral("preview_id"), previewId}, {QStringLiteral("page"), 3}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));

        QCOMPARE(ask(backend, QStringLiteral("CommitRunImport"),
                     {{QStringLiteral("preview_id"), previewId}, {QStringLiteral("row_numbers"), QJsonArray{1}},
                      {QStringLiteral("confirm_own_records"), false}}).message,
                 collectorChinese("import.commit.own_records_required"));
        QCOMPARE(ask(backend, QStringLiteral("CommitRunImport"),
                     {{QStringLiteral("preview_id"), previewId}, {QStringLiteral("row_numbers"), QJsonArray{1, 3}},
                      {QStringLiteral("confirm_own_records"), true}}).message,
                 collectorChinese("import.commit.rows_have_errors"));
        QCOMPARE(runCount(backend), before);

        const QJsonObject commit{{QStringLiteral("preview_id"), previewId},
                                 {QStringLiteral("row_numbers"), QJsonArray{1, 2}},
                                 {QStringLiteral("confirm_own_records"), true},
                                 {QStringLiteral("deduct_from_baseline"), true}};
        const QString commitId = QStringLiteral("bbbbbbbb-0000-4000-8000-000000000001");
        const Answer saved = askWithId(backend, QStringLiteral("CommitRunImport"), commit, commitId);
        QVERIFY2(saved.ok, qPrintable(saved.code + QStringLiteral(" ") + saved.message));
        QCOMPARE(saved.payload.value(QStringLiteral("imported_count")).toInt(), 2);
        QCOMPARE(saved.payload.value(QStringLiteral("replayed")).toBool(), false);
        QCOMPARE(saved.payload.value(QStringLiteral("baseline_deducted_count")).toInt(), 2);
        QCOMPARE(saved.payload.value(QStringLiteral("baseline_completed_count")).toInt(), 8);
        const QJsonArray runIds = saved.payload.value(QStringLiteral("run_ids")).toArray();
        QCOMPARE(runIds.size(), 2);
        const QJsonArray outcomes = saved.payload.value(QStringLiteral("rows")).toArray();
        QCOMPARE(outcomes.size(), 2);
        QCOMPARE(outcomes.at(0).toObject().value(QStringLiteral("status")).toString(), QStringLiteral("imported"));
        QCOMPARE(runCount(backend), before + 2);
        QCOMPARE(backend.dashboardStats({}).value(QStringLiteral("baseline_completed_count")).toInt(), 8);
        const QJsonArray chain = ask(backend, QStringLiteral("GetRunRevisions"),
                                     {{QStringLiteral("run_id"), runIds.at(0)}})
                                     .payload.value(QStringLiteral("items")).toArray();
        QCOMPARE(chain.size(), 1);
        QCOMPARE(chain.at(0).toObject().value(QStringLiteral("change_kind")).toString(), QStringLiteral("IMPORT"));

        // A resend, under the same id or another, is answered from the receipt.
        const Answer resent = askWithId(backend, QStringLiteral("CommitRunImport"), commit, commitId);
        QVERIFY2(resent.ok, qPrintable(resent.message));
        QVERIFY(resent.payload.value(QStringLiteral("replayed")).toBool());
        QCOMPARE(resent.payload.value(QStringLiteral("run_ids")).toArray(), runIds);
        QVERIFY(ask(backend, QStringLiteral("CommitRunImport"), commit).payload.value(QStringLiteral("replayed")).toBool());
        QCOMPARE(runCount(backend), before + 2);
        QCOMPARE(backend.dashboardStats({}).value(QStringLiteral("baseline_completed_count")).toInt(), 8);
        QJsonObject narrower = commit;
        narrower.insert(QStringLiteral("row_numbers"), QJsonArray{1});
        const Answer changed = ask(backend, QStringLiteral("CommitRunImport"), narrower);
        QCOMPARE(changed.code, QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
        QCOMPARE(changed.message, collectorChinese("import.commit.selection_changed"));

        // The same rows again are already in the library.
        QJsonObject whole = source;
        whole.remove(QStringLiteral("page_size"));
        const Answer again = ask(backend, QStringLiteral("PreviewRunImport"), whole);
        QVERIFY2(again.ok, qPrintable(again.message));
        QVERIFY(!again.payload.contains(QStringLiteral("page_info")));
        const QJsonArray againRows = again.payload.value(QStringLiteral("rows")).toArray();
        QCOMPARE(againRows.size(), 3);
        QCOMPARE(againRows.at(0).toObject().value(QStringLiteral("status")).toString(), QStringLiteral("duplicate"));
        QCOMPARE(againRows.at(0).toObject().value(QStringLiteral("matching_run_id")), runIds.at(0));
        QVERIFY(!againRows.at(0).toObject().value(QStringLiteral("can_import")).toBool());
    }

    // J-4: the offline backend reads a JSON source the way the Collector does, and refuses -
    // saying so - the table, workbook and backup sources it does not parse.
    void anImportFromJsonIsReadAndOtherFilesAreRefusedOnPurpose()
    {
        mr::MockBackend backend;
        const Answer json = ask(backend, QStringLiteral("PreviewRunImport"),
                                {{QStringLiteral("source_kind"), QStringLiteral("JSON")},
                                 {QStringLiteral("text"), QStringLiteral("[{\"content_id\": 4, \"result\": \"COMPLETED\"}]")}});
        QVERIFY2(json.ok, qPrintable(json.message));
        QCOMPARE(json.payload.value(QStringLiteral("rows")).toArray().size(), 1);
        QCOMPARE(json.payload.value(QStringLiteral("summary")).toObject().value(QStringLiteral("new")).toInt(), 1);
        const Answer notArray = ask(backend, QStringLiteral("PreviewRunImport"),
                                    {{QStringLiteral("source_kind"), QStringLiteral("JSON")},
                                     {QStringLiteral("text"), QStringLiteral("{}")}});
        QCOMPARE(notArray.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(notArray.message,
                 collectorChinese("import.source.unreadable",
                                  {{QStringLiteral("detail"), mr::collector_text::textRef("import.json.not_array")}}));
        const Answer table = ask(backend, QStringLiteral("PreviewRunImport"),
                                 {{QStringLiteral("source_kind"), QStringLiteral("CSV")},
                                  {QStringLiteral("text"), QStringLiteral("duty_name,result\nx,COMPLETED")}});
        QCOMPARE(table.code, QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(!table.message.isEmpty());
        QVERIFY(!table.message.startsWith(QString::fromUtf8("模拟后端不支持该消息")));
        QCOMPARE(ask(backend, QStringLiteral("PreviewRunImport"),
                     {{QStringLiteral("source_kind"), QStringLiteral("DOCX")}}).message,
                 collectorChinese("import.preview.kind_unsupported"));
    }

    // RV2-5 (audit 2026-10-20 H2-1): RunImportService.PreviewSource - a row without its own
    // identity is known by its own cells (ordinal order, trimmed, empty cells left out) and how
    // many identical rows precede it, never by the whole file it came in. Re-importing an edited
    // file marks only the rows already imported as duplicates, wherever they moved.
    void aReimportedFileMarksOnlyTheRowsAlreadyImportedAsDuplicates()
    {
        const QString sastasha = mr::DutyCatalog::shared()->lookup(4).value(QStringLiteral("duty_name")).toString();
        QVERIFY(!sastasha.isEmpty());
        const auto row = [&sastasha](const char *day, int job) {
            return QJsonObject{{QStringLiteral("duty_name"), sastasha}, {QStringLiteral("job_id"), job},
                               {QStringLiteral("source_recorded_at"), QString::fromLatin1(day)},
                               {QStringLiteral("result"), QString::fromUtf8("通关")}};
        };
        const QString file = QStringLiteral("personal-records.json");
        const QStringList newRows{QStringLiteral("new"), QStringLiteral("new")};

        mr::MockBackend appending;
        appending.resetRuns({});
        const Answer first = previewImport(appending, QStringLiteral("JSON"),
                                           {row("2026-09-01", 21), row("2026-09-02", 21)}, file);
        QVERIFY2(first.ok, qPrintable(first.message));
        QCOMPARE(importStatuses(first), newRows);
        QCOMPARE(commitWholePreview(appending, first).payload.value(QStringLiteral("imported_count")).toInt(), 2);
        const Answer appended = previewImport(appending, QStringLiteral("JSON"),
                                              {row("2026-09-01", 21), row("2026-09-02", 21), row("2026-09-03", 21)}, file);
        QCOMPARE(importStatuses(appended),
                 (QStringList{QStringLiteral("duplicate"), QStringLiteral("duplicate"), QStringLiteral("new")}));
        QVERIFY(!appended.payload.value(QStringLiteral("rows")).toArray().at(0).toObject()
                     .value(QStringLiteral("can_import")).toBool());
        // A padded cell or an empty one added does not change who the row is; another job does.
        QJsonObject padded = row("2026-09-01", 21);
        padded.insert(QStringLiteral("duty_name"), QStringLiteral("  ") + sastasha + QStringLiteral(" "));
        padded.insert(QStringLiteral("note"), QString());
        const Answer edited = previewImport(appending, QStringLiteral("JSON"), {padded, row("2026-09-02", 19)}, file);
        QCOMPARE(importStatuses(edited), (QStringList{QStringLiteral("duplicate"), QStringLiteral("new")}));

        mr::MockBackend moving;
        moving.resetRuns({});
        const QJsonObject twice = row("2026-09-01", 21);
        QCOMPARE(commitWholePreview(moving, previewImport(moving, QStringLiteral("JSON"), {twice, twice}, file))
                     .payload.value(QStringLiteral("imported_count")).toInt(),
                 2);
        const Answer reordered = previewImport(moving, QStringLiteral("JSON"), {row("2026-09-05", 19), twice, twice}, file);
        QCOMPARE(importStatuses(reordered),
                 (QStringList{QStringLiteral("new"), QStringLiteral("duplicate"), QStringLiteral("duplicate")}));
        // A third identical row is a run the library may already hold: offered, never pre-judged new.
        const Answer third = previewImport(moving, QStringLiteral("JSON"), {twice, twice, twice}, file);
        QCOMPARE(importStatuses(third), (QStringList{QStringLiteral("duplicate"), QStringLiteral("duplicate"),
                                                     QStringLiteral("possible_duplicate")}));
        QVERIFY(third.payload.value(QStringLiteral("rows")).toArray().at(2).toObject()
                    .value(QStringLiteral("can_import")).toBool());
    }

    // RV2-5 (audit 2026-10-20 H2-1): RunImportService.FindNearby - a run of the same duty on
    // the same source day, with the same job where both name one, may be this record when at
    // least one of the two has no clock time. Two clock times decide on their own, and a
    // live-captured run has no source day to match.
    void aRowOnTheSameSourceDayAsAnImportedRunIsAPossibleDuplicate()
    {
        const mr::DutyCatalog *duties = mr::DutyCatalog::shared();
        const QString sastasha = duties->lookup(4).value(QStringLiteral("duty_name")).toString();
        const QString other = duties->lookup(17).value(QStringLiteral("duty_name")).toString();
        QVERIFY(!sastasha.isEmpty() && !other.isEmpty() && other != sastasha);
        const auto row = [](const QString &duty, const char *recorded, int job) {
            QJsonObject value{{QStringLiteral("duty_name"), duty},
                              {QStringLiteral("source_recorded_at"), QString::fromLatin1(recorded)},
                              {QStringLiteral("result"), QStringLiteral("COMPLETED")}};
            if (job > 0)
                value.insert(QStringLiteral("job_id"), job);
            return value;
        };

        mr::MockBackend backend;
        // A live-captured run of the duty that day, sorted before any generated id.
        QJsonObject live = autoRun(QStringLiteral("00000000-0000-4000-8000-000000000001"),
                                   QStringLiteral("CANCELLED_BEFORE_ENTRY"), false);
        live.insert(QStringLiteral("content_id"), 4);
        live.insert(QStringLiteral("duty_name"), sastasha);
        live.insert(QStringLiteral("job_id"), 21);
        backend.resetRuns(QJsonArray{live});
        const Answer stored = commitWholePreview(
            backend, previewImport(backend, QStringLiteral("JSON"), {row(sastasha, "2026-09-01", 21)},
                                   QStringLiteral("old.json")));
        QVERIFY2(stored.ok, qPrintable(stored.message));
        const QString storedId = stored.payload.value(QStringLiteral("run_ids")).toArray().at(0).toString();
        QVERIFY(!storedId.isEmpty());

        const Answer pasted = previewImport(backend, QStringLiteral("JSON"),
                                            {row(sastasha, "2026-09-01", 21), row(sastasha, "2026-09-01 20:30", 21),
                                             row(sastasha, "2026.9.1", 0), row(sastasha, "2026-09-01", 19),
                                             row(sastasha, "2026-09-02", 21), row(other, "2026-09-01", 21)},
                                            QString::fromUtf8("粘贴的表格"));
        QVERIFY2(pasted.ok, qPrintable(pasted.message));
        QCOMPARE(importStatuses(pasted),
                 (QStringList{QStringLiteral("possible_duplicate"), QStringLiteral("possible_duplicate"),
                              QStringLiteral("possible_duplicate"), QStringLiteral("new"), QStringLiteral("new"),
                              QStringLiteral("new")}));
        const QJsonArray rows = pasted.payload.value(QStringLiteral("rows")).toArray();
        for (int i = 0; i < 3; ++i) {
            QCOMPARE(rows.at(i).toObject().value(QStringLiteral("matching_run_id")).toString(), storedId);
            QVERIFY(rows.at(i).toObject().value(QStringLiteral("can_import")).toBool());
        }

        mr::MockBackend timed;
        timed.resetRuns({});
        QVERIFY(commitWholePreview(timed, previewImport(timed, QStringLiteral("JSON"),
                                                        {row(sastasha, "2026-09-01 20:30", 21)},
                                                        QStringLiteral("old.json"))).ok);
        QCOMPARE(importStatuses(previewImport(timed, QStringLiteral("JSON"), {row(sastasha, "2026-09-01 22:10", 21)},
                                              QString::fromUtf8("粘贴的表格"))),
                 QStringList{QStringLiteral("new")});
    }

    // RV2-5 (audit 2026-10-20 I3-1): RunImportService.Render - the same card read twice in one
    // batch (overlapping screenshots, one picture added twice) differs only in what the reading
    // added; the later copy is a possible duplicate of the first. The twin key is the duty, the
    // clock time and the reflection text, so date-only rows of one day are never twins.
    void theSameCardTwiceInOneBatchIsOfferedOnceAsNew()
    {
        const mr::JobCatalog jobs;
        const QString sastasha = mr::DutyCatalog::shared()->lookup(4).value(QStringLiteral("duty_name")).toString();
        QVERIFY(!sastasha.isEmpty());
        const auto card = [&](const QString &text, const char *time, int job) {
            return QJsonObject{{QStringLiteral("duty_name"), sastasha}, {QStringLiteral("job_name"), jobs.jobName(job)},
                               {QStringLiteral("reflection_text"), text},
                               {QStringLiteral("source_recorded_at"), QString::fromLatin1(time)},
                               {QStringLiteral("result"), QStringLiteral("COMPLETED")}};
        };
        const QString thought = QString::fromUtf8("合成截图心得");
        QJsonObject overlapping = card(thought, "2026-10-08 20:30:00", 19);
        overlapping.insert(QStringLiteral("warnings"), QJsonArray{QString::fromUtf8("职业是按图标推测的")});
        mr::MockBackend backend;
        backend.resetRuns({});
        const Answer preview = previewImport(
            backend, QStringLiteral("SCREENSHOT"),
            {card(thought, "2026-10-08 20:30:00", 21), card(QString::fromUtf8("另一条心得"), "2026-10-08 20:30:00", 21),
             card(thought, "2026-10-08 20:30:00", 21), overlapping, card(thought, "2026-10-08 21:30:00", 21)},
            QString::fromUtf8("合成截图 等 2 张截图"));
        QVERIFY2(preview.ok, qPrintable(preview.message));
        QCOMPARE(importStatuses(preview),
                 (QStringList{QStringLiteral("new"), QStringLiteral("new"), QStringLiteral("possible_duplicate"),
                              QStringLiteral("possible_duplicate"), QStringLiteral("new")}));
        const QJsonArray rows = preview.payload.value(QStringLiteral("rows")).toArray();
        const QString firstCopy = rows.at(0).toObject().value(QStringLiteral("run")).toObject()
                                      .value(QStringLiteral("run_id")).toString();
        QVERIFY(!firstCopy.isEmpty());
        QCOMPARE(rows.at(2).toObject().value(QStringLiteral("matching_run_id")).toString(), firstCopy);
        QCOMPARE(rows.at(3).toObject().value(QStringLiteral("matching_run_id")).toString(), firstCopy);
        QVERIFY(rows.at(2).toObject().value(QStringLiteral("can_import")).toBool());
        QCOMPARE(preview.payload.value(QStringLiteral("summary")).toObject()
                     .value(QStringLiteral("possible_duplicate")).toInt(),
                 2);

        const QJsonObject dateOnly{{QStringLiteral("duty_name"), sastasha}, {QStringLiteral("job_id"), 21},
                                   {QStringLiteral("source_recorded_at"), QStringLiteral("2026-10-09")},
                                   {QStringLiteral("result"), QStringLiteral("COMPLETED")}};
        QCOMPARE(importStatuses(previewImport(backend, QStringLiteral("SCREENSHOT"), {dateOnly, dateOnly},
                                              QString::fromUtf8("截图"))),
                 (QStringList{QStringLiteral("new"), QStringLiteral("new")}));
    }

    // J-4: CalibrationEvidenceHandlers / CaptureValidationController.GetTemplateObservation -
    // the offline backend has never completed a session or an observation, so it says
    // which of the Collector's reasons applies instead of refusing the message.
    void calibrationEvidenceSaysWhyNothingIsReady()
    {
        mr::MockBackend backend;
        const Answer none = ask(backend, QStringLiteral("GetCalibrationEvidence"));
        QVERIFY2(none.ok, qPrintable(none.message));
        QCOMPARE(none.payload.value(QStringLiteral("available")).toBool(), false);
        QCOMPARE(none.payload.value(QStringLiteral("kind")).toString(), QStringLiteral("CODE_VERIFICATION"));
        QVERIFY(none.payload.value(QStringLiteral("evidence_json")).isNull());
        QVERIFY(none.payload.value(QStringLiteral("issue_url")).isNull());
        QCOMPARE(none.payload.value(QStringLiteral("evidence_in_url")).toBool(), false);
        QCOMPARE(none.payload.value(QStringLiteral("reason")).toString(), QStringLiteral("NO_CURRENT_CANDIDATE"));
        const Answer badHash = ask(backend, QStringLiteral("GetCalibrationEvidence"),
                                   {{QStringLiteral("code_sha256"), QStringLiteral("XYZ")}});
        QCOMPARE(badHash.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(badHash.message, collectorChinese("calibration.evidence_hash_invalid"));

        backend.setSharedCalibrationFixture(QStringLiteral("verifying"));
        const QJsonArray candidates = ask(backend, QStringLiteral("GetCaptureStatus")).payload
                                          .value(QStringLiteral("calibration")).toObject()
                                          .value(QStringLiteral("shared")).toObject()
                                          .value(QStringLiteral("candidates")).toArray();
        QCOMPARE(candidates.size(), 1);
        // The one candidate is the one the summary would describe.
        QCOMPARE(ask(backend, QStringLiteral("GetCalibrationEvidence")).payload.value(QStringLiteral("reason")).toString(),
                 QStringLiteral("NO_COMPLETED_HEALTHY_SESSION"));
        QCOMPARE(ask(backend, QStringLiteral("GetCalibrationEvidence"),
                     {{QStringLiteral("code_sha256"), QString(64, QLatin1Char('0'))}})
                     .payload.value(QStringLiteral("reason")).toString(),
                 QStringLiteral("CANDIDATE_NOT_OBSERVED"));

        const Answer observation = ask(backend, QStringLiteral("GetTemplateObservation"));
        QVERIFY2(observation.ok, qPrintable(observation.message));
        QCOMPARE(observation.payload.value(QStringLiteral("available")).toBool(), false);
        QCOMPARE(observation.payload.value(QStringLiteral("kind")).toString(), QStringLiteral("TEMPLATE_OBSERVATION"));
        QCOMPARE(observation.payload.value(QStringLiteral("reason")).toString(), QStringLiteral("NO_OBSERVATION"));
        backend.setValidationFixture(QStringLiteral("RECORDING"));
        QCOMPARE(ask(backend, QStringLiteral("GetTemplateObservation")).payload.value(QStringLiteral("reason")).toString(),
                 QStringLiteral("OBSERVATION_ACTIVE"));
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
