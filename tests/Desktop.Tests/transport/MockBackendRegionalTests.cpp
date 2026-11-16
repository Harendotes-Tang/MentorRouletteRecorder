#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
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

    // RunMutationService.ApplyChangeSet: a corrected duty comes from the run's own region's
    // catalogue (rule 8: a CN duty catalogue never names a TC record), and an explicit null
    // (未知副本) takes the observed zone with it (OG-1).
    void aCorrectedDutyComesFromTheRunsRegionAndAClearedDutyTakesItsZone()
    {
        mr::MockBackend backend;
        backend.resetRuns({});
        const Answer created = ask(backend, QStringLiteral("CreateManualRun"),
            {{"region", "TC"}, {"content_id", 4}, {"result", "COMPLETED"},
             {"entered_at_utc", "2026-09-01T12:01:00.000Z"}, {"ended_at_utc", "2026-09-01T12:10:00.000Z"},
             {"reason", "manual"}});
        QVERIFY2(created.ok, qPrintable(created.message));
        const QString runId = created.payload.value("run_id").toString();
        int revision = created.payload.value("revision").toInt();
        const auto correctDuty = [&](const QJsonValue &contentId) {
            const Answer answer = ask(backend, QStringLiteral("CorrectRun"),
                {{"run_id", runId}, {"expected_revision", revision},
                 {"changes", QJsonObject{{"content_id", contentId}}}, {"reason", "check"}});
            if (answer.ok)
                revision = answer.payload.value("revision").toInt();
            return answer;
        };
        const auto *duties = mr::DutyCatalog::shared();

        const QVariantMap tcName = duties->lookup(17, QStringLiteral("TC"));
        QVERIFY(!tcName.isEmpty());
        QVERIFY(tcName.value("duty_name") != duties->lookup(17, QStringLiteral("CN")).value("duty_name"));
        const Answer renamed = correctDuty(17);
        QVERIFY2(renamed.ok, qPrintable(renamed.message));
        QCOMPARE(renamed.payload.value("run").toObject().value("duty_name").toString(),
                 tcName.value("duty_name").toString());

        const QVariantMap tcZone = duties->lookup(5, QStringLiteral("TC"));
        QVERIFY(tcZone.value("territory_id") != duties->lookup(5, QStringLiteral("CN")).value("territory_id"));
        const Answer moved = correctDuty(5);
        QVERIFY2(moved.ok, qPrintable(moved.message));
        QCOMPARE(moved.payload.value("run").toObject().value("territory_id").toInteger(),
                 tcZone.value("territory_id").toLongLong());

        const Answer cleared = correctDuty(QJsonValue(QJsonValue::Null));
        QVERIFY2(cleared.ok, qPrintable(cleared.message));
        const QJsonObject unknown = cleared.payload.value("run").toObject();
        QVERIFY(unknown.value("content_id").isNull());
        QVERIFY(unknown.value("territory_id").isNull());
        QVERIFY(unknown.value("duty_name").isNull());
    }

    // CaptureWire.StatusExtras: GetStatus always carries the game (with install_path_readable)
    // and the two Oodle disclosures. Under a TC override the client's build is still read
    // from disk, so the profile decides, and the projection reaches the live 「还没有适用于
    // 当前游戏版本的支持文件」 instead of waiting for the install path for good.
    void theStatusCarriesTheGameAndATcOverrideLetsTheProfileDecide()
    {
        mr::MockBackend backend;
        const Answer cn = ask(backend, QStringLiteral("GetStatus"));
        QVERIFY(cn.ok);
        const QJsonObject game = cn.payload.value("game").toObject();
        QVERIFY(game.value("running").toBool());
        QVERIFY(game.value("install_path_readable").toBool());
        QCOMPARE(game.value("region").toString(), QStringLiteral("CN"));
        QCOMPARE(game.value("game_build").toString(),
                 cn.payload.value("capture").toObject().value("game_build").toString());
        QCOMPARE(game.value("instance_count").toInt(), 1);
        QCOMPARE(cn.payload.value("oodle_mode").toString(), QStringLiteral("FfxivTcp"));
        QVERIFY(cn.payload.value("reads_game_executable").toBool());

        QVERIFY(ask(backend, QStringLiteral("UpdateCaptureSettings"), {{"region_override", "TC"}}).ok);
        const Answer tc = ask(backend, QStringLiteral("GetStatus"));
        const QJsonObject capture = tc.payload.value("capture").toObject();
        QCOMPARE(capture.value("profile_status").toString(), QStringLiteral("UNSUPPORTED_BUILD"));
        QVERIFY(!capture.value("game_build").toString().isEmpty());
        QCOMPARE(tc.payload.value("game").toObject().value("region").toString(), QStringLiteral("TC"));
        QCOMPARE(tc.payload.value("game").toObject().value("game_build").toString(),
                 capture.value("game_build").toString());
        QCOMPARE(ask(backend, QStringLiteral("GetProtocolProfileStatus")).payload.value("game_build").toString(),
                 capture.value("game_build").toString());

        mr::AutomaticRecordingController recording(&backend);
        QTRY_COMPARE(recording.state(), QStringLiteral("blocked"));
        QVERIFY2(recording.message().contains(QString::fromUtf8("还没有适用于当前游戏版本的支持文件")),
                 qPrintable(recording.message()));
    }

    // 角色档案: every region starts with one unnamed current profile that carries the
    // region's numbers, so a player with one character sees what they saw before.
    void aRegionStartsWithOneUnnamedCurrentProfileCarryingItsNumbers()
    {
        mr::MockBackend backend;
        const QJsonObject stats = backend.dashboardStats({});
        const QJsonArray profiles = stats.value("profiles").toArray();
        QCOMPARE(profiles.size(), 2); // CN and TC, as SettingsRepository.EnsureDefaults
        const QJsonObject cn = profiles.at(0).toObject();
        QCOMPARE(cn.value("region").toString(), QStringLiteral("CN"));
        QCOMPARE(cn.value("display_name").toString(), QString());
        QVERIFY(cn.value("is_current").toBool());
        QCOMPARE(cn.value("baseline_completed_count").toInt(), 1374);
        QCOMPARE(profiles.at(1).toObject().value("region").toString(), QStringLiteral("TC"));
        QJsonObject entry;
        for (const auto &value : stats.value("achievements").toArray())
            if (value.toObject().value("region") == QStringLiteral("CN")) entry = value.toObject();
        QCOMPARE(entry.value("profile_id"), cn.value("profile_id"));
        QCOMPARE(entry.value("profile_count").toInt(), 1);
        QCOMPARE(entry.value("unassigned_run_count").toInt(), 0);
        QCOMPARE(entry.value("achievement_progress"), cn.value("achievement_progress"));
        QCOMPARE(stats.value("achievement_progress"), cn.value("achievement_progress"));
        // Every sample run, and the live one, is filed under it.
        const Answer page = ask(backend, QStringLiteral("QueryRuns"),
                                {{"filter", QJsonObject{{"region", "CN"}}}, {"page_size", 200}});
        QVERIFY(!page.payload.value("items").toArray().isEmpty());
        for (const auto &run : page.payload.value("items").toArray())
            QCOMPARE(run.toObject().value("profile_id"), cn.value("profile_id"));
        QCOMPARE(ask(backend, QStringLiteral("GetCurrentRun")).payload.value("run").toObject().value("profile_id"),
                 cn.value("profile_id"));
    }

    void profileMessagesRefuseWhatTheCollectorRefuses()
    {
        mr::MockBackend backend;
        backend.resetRuns({autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("COMPLETED"), true)});
        QSignalSpy events(&backend, &mr::IBackend::liveEvent);
        const auto create = [&backend](const QString &name, bool current = false) {
            return ask(backend, QStringLiteral("CreateCharacterProfile"),
                       {{"region", "CN"}, {"display_name", name}, {"goal_count", 500},
                        {"baseline_completed_count", 7}, {"make_current", current}});
        };
        const Answer created = create(QStringLiteral("  白露  "));
        QVERIFY2(created.ok, qPrintable(created.message));
        const int second = created.payload.value("profile_id").toInt();
        QCOMPARE(created.payload.value("region").toString(), QStringLiteral("CN"));
        QVERIFY(!created.payload.value("deleted").toBool());
        QCOMPARE(created.payload.value("profiles").toArray().size(), 3);
        QCOMPARE(backend.dashboardStats({}).value("profiles").toArray().at(1).toObject().value("display_name").toString(),
                 QString::fromUtf8("白露")); // trimmed, after CN's first and before TC
        QTRY_COMPARE(events.size(), 1);
        QCOMPARE(events.at(0).at(0).toMap().value("kind").toString(), QStringLiteral("stats_invalidated"));
        QCOMPARE(create(QString::fromUtf8("白露")).code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(create(QStringLiteral("   ")).code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(create(QString(25, QLatin1Char('x'))).code, QStringLiteral("ERR_BAD_REQUEST"));
        for (int i = 3; i <= 16; ++i)
            QVERIFY(create(QStringLiteral("c%1").arg(i)).ok);
        QCOMPARE(create(QStringLiteral("seventeen")).code, QStringLiteral("ERR_BAD_REQUEST"));

        const int first = backend.characterProfiles().at(0).toObject().value("profile_id").toInt();
        const int tc = backend.dashboardStats({{"region", "TC"}}).value("achievements").toArray()
                           .first().toObject().value("profile_id").toInt();
        const auto update = [&backend](const QJsonObject &payload) {
            return ask(backend, QStringLiteral("UpdateCharacterProfile"), payload);
        };
        QCOMPARE(update({{"profile_id", second}}).code, QStringLiteral("ERR_BAD_REQUEST")); // asks for nothing
        QCOMPARE(update({{"profile_id", 999}, {"make_current", true}}).code, QStringLiteral("ERR_NOT_FOUND"));
        QCOMPARE(update({{"profile_id", second}, {"display_name", "c3"}}).code, QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(update({{"profile_id", second}, {"display_name", QString::fromUtf8("白露二")}}).ok);
        QVERIFY(update({{"profile_id", second}, {"make_current", true}}).ok);
        QVERIFY(backend.dashboardStats({}).value("achievements").toArray().first().toObject().value("profile_id").toInt()
                == second);

        const auto remove = [&backend](int id) {
            return ask(backend, QStringLiteral("DeleteCharacterProfile"), {{"profile_id", id}});
        };
        QCOMPARE(remove(999).code, QStringLiteral("ERR_NOT_FOUND"));
        QCOMPARE(remove(tc).code, QStringLiteral("ERR_BAD_REQUEST"));     // the last of its region
        QCOMPARE(remove(second).code, QStringLiteral("ERR_BAD_REQUEST")); // the current one
        const Answer owning = remove(first);                              // still owns a run
        QCOMPARE(owning.code, QStringLiteral("ERR_BAD_REQUEST"));
        const Answer gone = remove(backend.characterProfiles().at(2).toObject().value("profile_id").toInt());
        QVERIFY2(gone.ok, qPrintable(gone.message));
        QVERIFY(gone.payload.value("deleted").toBool());
        QCOMPARE(backend.characterProfiles().size(), 16);
    }

    void eachProfileKeepsItsOwnProgressAndAFilterNamesOne()
    {
        mr::MockBackend backend;
        const auto cnRun = [](const char *id) {
            QJsonObject run = autoRun(QString::fromLatin1(id), QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("region"), QStringLiteral("CN"));
            return run;
        };
        backend.resetRuns({cnRun("11111111-1111-4111-8111-111111111111"),
                           cnRun("22222222-2222-4222-8222-222222222222")});
        backend.setAchievement(100, 10);
        const int first = backend.characterProfiles().at(0).toObject().value("profile_id").toInt();
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 3, true);
        // setAchievement now addresses the new current profile.
        backend.setAchievement(QStringLiteral("CN"), 60, 4);
        QJsonObject manual{{"region", "CN"}, {"content_id", 5}, {"result", "COMPLETED"},
            {"entered_at_utc", "2026-09-01T12:01:00.000Z"}, {"ended_at_utc", "2026-09-01T12:10:00.000Z"}, {"reason", "manual"}};
        QCOMPARE(ask(backend, QStringLiteral("CreateManualRun"), manual).payload.value("run").toObject().value("profile_id").toInt(),
                 second);
        manual.insert("profile_id", first);
        QCOMPARE(ask(backend, QStringLiteral("CreateManualRun"), manual).payload.value("run").toObject().value("profile_id").toInt(),
                 first);
        manual.insert("region", "TC");
        QCOMPARE(ask(backend, QStringLiteral("CreateManualRun"), manual).code, QStringLiteral("ERR_BAD_REQUEST"));
        manual.insert("profile_id", 999);
        QCOMPARE(ask(backend, QStringLiteral("CreateManualRun"), manual).code, QStringLiteral("ERR_NOT_FOUND"));

        const QJsonObject all = backend.dashboardStats({});
        const QJsonObject a = all.value("profiles").toArray().at(0).toObject();
        const QJsonObject b = all.value("profiles").toArray().at(1).toObject();
        QCOMPARE(a.value("achievement_progress").toInt(), 13); // 10 + its three completions
        QCOMPARE(a.value("run_count").toInt(), 3);
        QCOMPARE(b.value("achievement_progress").toInt(), 5);  // 4 + its one
        QCOMPARE(all.value("achievement_progress").toInt(), 5); // the current profile's
        QCOMPARE(all.value("achievements").toArray().first().toObject().value("profile_count").toInt(), 2);
        const QJsonObject named = backend.dashboardStats({{"profile_id", first}});
        QCOMPARE(named.value("achievement_progress").toInt(), 13);
        QCOMPARE(named.value("goal_count").toInt(), 100);
        QCOMPARE(named.value("completed_count").toInt(), 3);
        QCOMPARE(named.value("achievements").toArray().first().toObject().value("achievement_progress").toInt(), 5);
        QCOMPARE(named.value("profiles").toArray(), all.value("profiles").toArray()); // unaffected by any filter

        const auto total = [&backend](const QJsonObject &filter) {
            const Answer page = ask(backend, QStringLiteral("QueryRuns"), {{"filter", filter}});
            return page.ok ? page.payload.value("page_info").toObject().value("total").toInt() : -1;
        };
        QCOMPARE(total({{"profile_id", first}}), 3);
        QCOMPARE(total({{"profile_id", second}}), 1);
        QCOMPARE(total({{"profile_unassigned", true}}), 0);
        const Answer both = ask(backend, QStringLiteral("QueryRuns"),
                                {{"filter", QJsonObject{{"profile_id", first}, {"profile_unassigned", true}}}});
        QCOMPARE(both.code, QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY(ask(backend, QStringLiteral("QueryRunCalendarDays"),
                    {{"month", "2026-09"}, {"filter", QJsonObject{{"profile_id", first}}}}).ok);

        // A baseline named by profile edits that profile only.
        const Answer baseline = ask(backend, QStringLiteral("UpdateAchievementBaseline"),
            {{"profile_id", first}, {"goal_count", 100}, {"baseline_completed_count", 20}, {"reason", "fix"}});
        QVERIFY(baseline.ok);
        QCOMPARE(baseline.payload.value("profile_id").toInt(), first);
        QCOMPARE(backend.characterProfiles().at(0).toObject().value("achievement_progress").toInt(), 23);
        QCOMPARE(backend.characterProfiles().at(1).toObject().value("baseline_completed_count").toInt(), 4);
        QCOMPARE(ask(backend, QStringLiteral("UpdateAchievementBaseline"),
                     {{"profile_id", 999}, {"baseline_completed_count", 1}, {"reason", "fix"}}).code,
                 QStringLiteral("ERR_NOT_FOUND"));
    }

    void assignProfileMovesRunsOnceAndKeepsEveryRegionApart()
    {
        mr::MockBackend backend;
        QJsonObject tcRun = autoRun(QStringLiteral("33333333-3333-4333-8333-333333333333"), QStringLiteral("COMPLETED"), true);
        tcRun.insert(QStringLiteral("region"), QStringLiteral("TC"));
        backend.resetRuns({autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"), QStringLiteral("COMPLETED"), true),
                           autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"), QStringLiteral("COMPLETED"), true),
                           tcRun});
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 0, false);
        const QJsonArray both{QJsonObject{{"run_id", "11111111-1111-4111-8111-111111111111"}, {"expected_revision", 1}},
                              QJsonObject{{"run_id", "22222222-2222-4222-8222-222222222222"}, {"expected_revision", 1}}};
        const auto assign = [&backend](const QJsonArray &runs, const QJsonValue &profile) {
            QJsonObject payload{{"action", "assign_profile"}, {"runs", runs}, {"reason", "move"}};
            if (!profile.isUndefined())
                payload.insert("profile_id", profile);
            return ask(backend, QStringLiteral("BatchMutateRuns"), payload);
        };
        QCOMPARE(assign(both, QJsonValue(QJsonValue::Undefined)).code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(ask(backend, QStringLiteral("BatchMutateRuns"),
                     {{"action", "soft_delete"}, {"runs", both}, {"reason", "x"}, {"profile_id", second}}).code,
                 QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(assign(both, 999).code, QStringLiteral("ERR_NOT_FOUND"));
        const Answer moved = assign(both, second);
        QVERIFY2(moved.ok, qPrintable(moved.message));
        QCOMPARE(moved.payload.value("changed_count").toInt(), 2);
        QCOMPARE(moved.payload.value("action").toString(), QStringLiteral("assign_profile"));
        const Answer revisions = ask(backend, QStringLiteral("GetRunRevisions"),
                                     {{"run_id", "11111111-1111-4111-8111-111111111111"}});
        const QJsonObject last = revisions.payload.value("items").toArray().last().toObject();
        QCOMPARE(last.value("revision").toInt(), 2);
        QCOMPARE(last.value("changes").toArray().first().toObject().value("field").toString(), QStringLiteral("profile_id"));
        QCOMPARE(last.value("changes").toArray().first().toObject().value("new_value").toInt(), second);
        QCOMPARE(backend.characterProfiles().at(1).toObject().value("completed_count").toInt(), 2);
        // Runs already there are not moved again; the stale revision is a conflict.
        QCOMPARE(assign(both, second).code, QStringLiteral("ERR_REVISION_CONFLICT"));
        const QJsonArray fresh{QJsonObject{{"run_id", "11111111-1111-4111-8111-111111111111"}, {"expected_revision", 2}}};
        QCOMPARE(assign(fresh, second).code, QStringLiteral("ERR_NO_CHANGES"));
        const QJsonArray foreign{QJsonObject{{"run_id", "33333333-3333-4333-8333-333333333333"}, {"expected_revision", 1}}};
        QCOMPARE(assign(foreign, second).code, QStringLiteral("ERR_BAD_REQUEST"));
        // null: assigned to no profile, counted in nobody's progress.
        QVERIFY(assign(fresh, QJsonValue(QJsonValue::Null)).ok);
        const QJsonObject stats = backend.dashboardStats({});
        QCOMPARE(stats.value("achievements").toArray().first().toObject().value("unassigned_run_count").toInt(), 1);
        QCOMPARE(backend.characterProfiles().at(1).toObject().value("completed_count").toInt(), 1);
        const Answer loose = ask(backend, QStringLiteral("QueryRuns"), {{"filter", QJsonObject{{"profile_unassigned", true}}}});
        QCOMPARE(loose.payload.value("items").toArray().size(), 1);
        QVERIFY(loose.payload.value("items").toArray().first().toObject().value("profile_id").isNull());
    }

    void anImportNamedForAProfileFilesItsRegionsRecordsThere()
    {
        mr::MockBackend backend;
        backend.resetRuns({});
        backend.setAchievement(100, 10);
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 5, false);
        QJsonObject cn{{"region", "CN"}, {"content_id", 5}, {"result", "COMPLETED"},
            {"entered_at_utc", "2026-09-01T12:01:00.000Z"}, {"ended_at_utc", "2026-09-01T12:10:00.000Z"}};
        QJsonObject tc = cn;
        tc.insert("region", "TC");
        const Answer preview = previewImport(backend, QStringLiteral("JSON"), {cn, tc}, QStringLiteral("profiles.json"));
        QVERIFY2(preview.ok, qPrintable(preview.message));
        const Answer saved = ask(backend, QStringLiteral("CommitRunImport"),
            {{"preview_id", preview.payload.value("preview_id")}, {"row_numbers", QJsonArray{1, 2}},
             {"confirm_own_records", true}, {"deduct_from_baseline", true}, {"profile_id", second}});
        QVERIFY2(saved.ok, qPrintable(saved.message));
        const QJsonArray adjustments = saved.payload.value("baseline_adjustments").toArray();
        QCOMPARE(adjustments.size(), 2);
        QCOMPARE(adjustments.at(0).toObject().value("profile_id").toInt(), second);
        QCOMPARE(adjustments.at(0).toObject().value("baseline_completed_count").toInt(), 4);
        QCOMPARE(backend.dashboardStats({}).value("baseline_completed_count").toInt(), 10); // CN's current is untouched
        QCOMPARE(backend.characterProfiles().at(1).toObject().value("achievement_progress").toInt(), 5);
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
