#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "DutyCatalog.h"
#include "MockBackend.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QMap>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

namespace {

struct Answer {
    bool done = false;
    bool ok = false;
    QJsonObject payload;
    QString code;
    QString message;
};

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

int runCount(mr::MockBackend &backend)
{
    return ask(backend, QStringLiteral("QueryRuns"),
               {{QStringLiteral("filter"), QJsonObject{{QStringLiteral("include_deleted"), true}}},
                {QStringLiteral("page"), 1}, {QStringLiteral("page_size"), 200}})
        .payload.value(QStringLiteral("page_info")).toObject().value(QStringLiteral("total")).toInt();
}

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

QStringList importStatuses(const Answer &preview)
{
    QStringList statuses;
    for (const auto &row : preview.payload.value(QStringLiteral("rows")).toArray())
        statuses.append(row.toObject().value(QStringLiteral("status")).toString());
    return statuses;
}

class MockBackendRegionalTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void regionsIsolateHistoryDutiesAndLifetimeAchievements()
    {
        mr::MockBackend backend;
        auto regional = [](const QString &id, const QString &region) {
            QJsonObject run = autoRun(id, QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("region"), region);
            run.insert(QStringLiteral("content_id"), 4);
            return run;
        };
        QJsonObject global = regional(QStringLiteral("33333333-3333-4333-8333-333333333333"), QStringLiteral("GLOBAL"));
        global.insert(QStringLiteral("contributes_to_goal"), false);
        QJsonObject deleted = regional(QStringLiteral("44444444-4444-4444-8444-444444444444"), QStringLiteral("TC"));
        deleted.insert(QStringLiteral("soft_deleted"), true);
        backend.resetRuns({regional(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("CN")),
                           regional(QStringLiteral("22222222-2222-4222-8222-222222222222"), QStringLiteral("TC")),
                           global, deleted});
        backend.setAchievement(100, 10);
        backend.setAchievement(QStringLiteral("TC"), 50, 20);
        backend.setAchievement(QStringLiteral("UNKNOWN"), 30, 5);
        const QJsonObject all = backend.dashboardStats({});
        QCOMPARE(all.value("completed_count").toInt(), 3);
        QCOMPARE(all.value("achievement_progress").toInt(), 11); // legacy scalar remains CN
        const QJsonArray achievements = all.value("achievements").toArray();
        QCOMPARE(achievements.size(), 4); // settings-only UNKNOWN and data-only GLOBAL stay visible
        QMap<QString, QJsonObject> byRegion;
        for (const auto &entry : achievements)
            byRegion.insert(entry.toObject().value("region").toString(), entry.toObject());
        QCOMPARE(byRegion.value("CN").value("completed_count").toInt(), 1);
        QCOMPARE(byRegion.value("TC").value("achievement_progress").toInt(), 21);
        QCOMPARE(byRegion.value("GLOBAL").value("completed_count").toInt(), 0);
        QCOMPARE(byRegion.value("UNKNOWN").value("baseline_completed_count").toInt(), 5);
        const QJsonObject filtered = backend.dashboardStats({{"region", "TC"}, {"from_utc", "2027-01-01T00:00:00.000Z"}});
        QCOMPARE(filtered.value("completed_count").toInt(), 0);
        QCOMPARE(filtered.value("achievement_progress").toInt(), 21);
        QCOMPARE(filtered.value("achievements").toArray().size(), 1);
        QCOMPARE(filtered.value("achievements").toArray().first().toObject().value("region").toString(), QStringLiteral("TC"));
        const QJsonArray duties = backend.dungeonStats({});
        QCOMPARE(duties.size(), 3); // identical content IDs remain separate regional rows
        for (const auto &entry : duties) {
            QCOMPARE(entry.toObject().value("content_id").toInt(), 4);
            QCOMPARE(entry.toObject().value("completed_count").toInt(), 1);
            QVERIFY(!entry.toObject().value("region").toString().isEmpty());
        }
        for (const auto *region : {"CN", "TC", "GLOBAL", "UNKNOWN"}) {
            const QJsonObject filter{{"region", QString::fromLatin1(region)}};
            const Answer query = ask(backend, QStringLiteral("QueryRuns"), {{"filter", filter}});
            QVERIFY(query.ok);
            QCOMPARE(query.payload.value("items").toArray().size(), qstrcmp(region, "UNKNOWN") == 0 ? 0 : 1);
        }
        QCOMPARE(ask(backend, QStringLiteral("QueryRuns"), {{"filter", QJsonObject{{"region", QJsonValue::Null}}}})
                     .payload.value("items").toArray().size(), 3);
        const Answer calendar = ask(backend, QStringLiteral("QueryRunCalendarDays"),
            {{"month", "2026-09"}, {"filter", QJsonObject{{"region", "TC"}}}});
        QVERIFY(calendar.ok);
        QCOMPARE(ask(backend, QStringLiteral("ExportCsv"), {{"filter", QJsonObject{{"region", "TC"}}}})
                     .payload.value("row_count").toInt(), 1);
        QCOMPARE(ask(backend, QStringLiteral("QueryRuns"), {{"filter", QJsonObject{{"region", "tc"}}}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));
    }

    void tcCaptureNeverReusesCnFixtureSuccessAndManualRunsKeepTheirRegion()
    {
        mr::MockBackend backend;
        backend.resetRuns({});
        backend.setCalibrationFixture(QStringLiteral("done"));
        backend.setRecordingFixture(QStringLiteral("listening"));
        QVERIFY(ask(backend, QStringLiteral("UpdateCaptureSettings"), {{"region_override", "TC"}, {"allow_without_profile", true}}).ok);
        const Answer status = ask(backend, QStringLiteral("GetCaptureStatus"));
        QVERIFY(status.ok);
        QCOMPARE(status.payload.value("region").toString(), QStringLiteral("TC"));
        QCOMPARE(status.payload.value("profile_status").toString(), QStringLiteral("UNSUPPORTED_BUILD"));
        QVERIFY(!status.payload.value("profile_matches_build").toBool());
        QVERIFY(status.payload.value("profile_id").isNull());
        QVERIFY(status.payload.value("candidate_hypotheses").toArray().isEmpty());
        QCOMPARE(status.payload.value("parse_ok_count").toInt(), 0);
        const Answer profile = ask(backend, QStringLiteral("GetProtocolProfileStatus"));
        QCOMPARE(profile.payload.value("region").toString(), QStringLiteral("TC"));
        QCOMPARE(profile.payload.value("status").toString(), QStringLiteral("UNSUPPORTED_BUILD"));
        QCOMPARE(ask(backend, QStringLiteral("GetCurrentRun")).payload.value("state").toString(), QStringLiteral("IDLE"));
        QSignalSpy events(&backend, &mr::IBackend::liveEvent);
        backend.simulateRunTransitions(QStringLiteral("COMPLETED"));
        QCOMPARE(events.size(), 0);
        QCOMPARE(runCount(backend), 0);
        QCOMPARE(ask(backend, QStringLiteral("GetCalibrationShareCode")).code, QStringLiteral("ERR_CALIBRATION_NOT_READY"));
        QVERIFY(!ask(backend, QStringLiteral("UpdateCaptureSettings"),
            {{"region_override", "TC"}, {"candidate_validation_enabled", true}, {"research_payload_opcodes", QJsonArray{"0x034b"}}}).ok);
        QJsonObject manual{{"region", "TC"}, {"content_id", 5}, {"result", "COMPLETED"},
            {"entered_at_utc", "2026-09-01T12:01:00.000Z"}, {"ended_at_utc", "2026-09-01T12:10:00.000Z"}, {"reason", "manual"}};
        const Answer created = ask(backend, QStringLiteral("CreateManualRun"), manual);
        QVERIFY2(created.ok, qPrintable(created.message));
        const QJsonObject run = created.payload.value("run").toObject();
        QCOMPARE(run.value("region").toString(), QStringLiteral("TC"));
        const QVariantMap tcDuty = mr::DutyCatalog::shared()->lookup(5, QStringLiteral("TC"));
        QVERIFY(!tcDuty.isEmpty());
        QCOMPARE(run.value("territory_id").toInt(), tcDuty.value("territory_id").toInt());
        manual.remove("region");
        QCOMPARE(ask(backend, QStringLiteral("CreateManualRun"), manual).payload.value("run").toObject().value("region").toString(),
                 QStringLiteral("UNKNOWN"));
    }

    void achievementBaselineUpdatesAndReplaysKeepTheOwningRegion()
    {
        mr::MockBackend backend;
        backend.resetRuns({});
        backend.setAchievement(100, 10);
        backend.setAchievement(QStringLiteral("TC"), 50, 20);
        const QString id = QStringLiteral("aaaaaaaa-0000-4000-8000-000000000010");
        const QJsonObject change{{"region", "TC"}, {"goal_count", 200}, {"baseline_completed_count", 30}, {"reason", "check"}};
        const Answer saved = askWithId(backend, QStringLiteral("UpdateAchievementBaseline"), change, id);
        QVERIFY(saved.ok);
        QCOMPARE(saved.payload.value("region").toString(), QStringLiteral("TC"));
        QCOMPARE(backend.dashboardStats({}).value("baseline_completed_count").toInt(), 10);
        QCOMPARE(backend.dashboardStats({{"region", "TC"}}).value("goal_count").toInt(), 200);
        const Answer replay = askWithId(backend, QStringLiteral("UpdateAchievementBaseline"), change, id);
        QVERIFY(replay.ok);
        QVERIFY(replay.payload.value("idempotent_replay").toBool());
        QCOMPARE(replay.payload.value("audit_event_id"), saved.payload.value("audit_event_id"));
        QJsonObject other = change;
        other.insert("region", "CN");
        QCOMPARE(askWithId(backend, QStringLiteral("UpdateAchievementBaseline"), other, id).code,
                 QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
        other.remove("region");
        const Answer legacy = ask(backend, QStringLiteral("UpdateAchievementBaseline"), other);
        QVERIFY(legacy.ok);
        QCOMPARE(legacy.payload.value("region").toString(), QStringLiteral("CN"));
        QCOMPARE(backend.dashboardStats({{"region", "TC"}}).value("baseline_completed_count").toInt(), 30);
        other.insert("region", "zh-TW");
        QVERIFY(!ask(backend, QStringLiteral("UpdateAchievementBaseline"), other).ok);
    }

    void importsPreserveRegionalDuplicatesAndDeductOnlyTheirOwnBaseline()
    {
        mr::MockBackend backend;
        backend.resetRuns({});
        backend.setAchievement(100, 10);
        backend.setAchievement(QStringLiteral("TC"), 50, 20);
        QJsonObject cn{{"region", "CN"}, {"content_id", 5}, {"result", "COMPLETED"},
            {"entered_at_utc", "2026-09-01T12:01:00.000Z"}, {"ended_at_utc", "2026-09-01T12:10:00.000Z"}};
        QJsonObject tc = cn;
        tc.insert("region", "TC");
        tc.insert("game_build", "2026.09.14.0000.0000");
        const Answer preview = previewImport(backend, QStringLiteral("JSON"), {cn, tc}, QStringLiteral("regions.json"));
        QVERIFY2(preview.ok, qPrintable(preview.message));
        QCOMPARE(importStatuses(preview), (QStringList{QStringLiteral("new"), QStringLiteral("new")}));
        QCOMPARE(preview.payload.value("rows").toArray().at(1).toObject().value("run").toObject()
                     .value("game_build").toString(), QStringLiteral("2026.09.14.0000.0000"));
        const QJsonObject commit{{"preview_id", preview.payload.value("preview_id")}, {"row_numbers", QJsonArray{1, 2}},
            {"confirm_own_records", true}, {"deduct_from_baseline", true}};
        const Answer saved = ask(backend, QStringLiteral("CommitRunImport"), commit);
        QVERIFY(saved.ok);
        QCOMPARE(saved.payload.value("imported_count").toInt(), 2);
        QCOMPARE(saved.payload.value("baseline_deducted_count").toInt(), 2);
        QCOMPARE(saved.payload.value("baseline_completed_count").toInt(), 9);
        const QJsonArray adjustments = saved.payload.value("baseline_adjustments").toArray();
        QCOMPARE(adjustments.size(), 2);
        QCOMPARE(adjustments.at(0).toObject().value("region").toString(), QStringLiteral("CN"));
        QCOMPARE(adjustments.at(0).toObject().value("baseline_deducted_count").toInt(), 1);
        QCOMPARE(adjustments.at(1).toObject().value("region").toString(), QStringLiteral("TC"));
        QCOMPARE(adjustments.at(1).toObject().value("baseline_completed_count").toInt(), 19);
        const Answer replay = ask(backend, QStringLiteral("CommitRunImport"), commit);
        QVERIFY(replay.ok);
        QVERIFY(replay.payload.value("replayed").toBool());
        QCOMPARE(replay.payload.value("baseline_adjustments").toArray(), adjustments);
        QCOMPARE(backend.dashboardStats({{"region", "TC"}}).value("baseline_completed_count").toInt(), 19);
        QCOMPARE(importStatuses(previewImport(backend, QStringLiteral("JSON"), {cn, tc}, QStringLiteral("regions.json"))),
                 (QStringList{QStringLiteral("duplicate"), QStringLiteral("duplicate")}));
        mr::MockBackend crossRegion;
        QJsonObject existing = autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("COMPLETED"), true);
        existing.insert("region", "CN");
        existing.insert("content_id", 5);
        crossRegion.resetRuns({existing});
        QCOMPARE(importStatuses(previewImport(crossRegion, QStringLiteral("JSON"), {tc}, QStringLiteral("new-tc.json"))),
                 QStringList{QStringLiteral("new")});
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(MockBackendRegional)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    MockBackendRegionalTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "MockBackendRegionalTests.moc"
