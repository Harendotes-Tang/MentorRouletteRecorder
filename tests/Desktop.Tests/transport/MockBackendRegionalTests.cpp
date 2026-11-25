#include "DesktopTestRunner.h"
#include "MockBackendTestSupport.h"
#include "TestCollectorGuard.h"
#include "AutomaticRecordingController.h"
#include "DutyCatalog.h"
#include "MockBackend.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QMap>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTest>

namespace {

using mrtest::Answer;
using mrtest::ask;
using mrtest::askWithId;
using mrtest::autoRun;
using mrtest::collectorChinese;
using mrtest::importStatuses;
using mrtest::previewImport;
using mrtest::runCount;

/// The id of \a region's first character profile, or 0 when the region has none.
int regionProfileId(const mr::MockBackend &backend, const QString &region)
{
    for (const auto &value : backend.characterProfiles()) {
        if (value.toObject().value("region").toString() == region)
            return value.toObject().value("profile_id").toInt();
    }
    return 0;
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

    void resultSharesUseOnlyAttemptsWithinTheRegionFilter()
    {
        mr::MockBackend backend;
        QJsonObject completed = autoRun(QStringLiteral("11111111-1111-4111-8111-111111111111"),
                                        QStringLiteral("COMPLETED"), true);
        completed.insert("region", "CN");
        QJsonObject first = autoRun(QStringLiteral("22222222-2222-4222-8222-222222222222"),
                                    QStringLiteral("UNKNOWN"), false);
        first.insert("region", "TC");
        first.insert("source", "MANUAL");
        first.insert("mentor_roulette_id", QJsonValue::Null);
        first.insert("matched_at_utc", QJsonValue::Null);
        first.insert("ended_at_utc", QJsonValue::Null);
        QJsonObject second = first;
        second.insert("run_id", "33333333-3333-4333-8333-333333333333");
        backend.resetRuns({completed, first, second});

        const auto unknown = [](const QJsonObject &stats) {
            for (const auto &entry : stats.value("buckets").toArray()) {
                const QJsonObject bucket = entry.toObject();
                if (bucket.value("result").toString() == QLatin1String("UNKNOWN"))
                    return bucket;
            }
            return QJsonObject{};
        };

        const QJsonObject all = backend.resultStats({});
        QCOMPARE(all.value("attempt_count").toInt(), 1);
        QCOMPARE(unknown(all).value("count").toInt(), 2);
        QCOMPARE(unknown(all).value("share").toDouble(-1.0), 0.0);

        const QJsonObject tc = backend.resultStats({{"region", "TC"}});
        QCOMPARE(tc.value("attempt_count").toInt(-1), 0);
        QCOMPARE(unknown(tc).value("count").toInt(), 2);
        QVERIFY(unknown(tc).value("share").isNull());
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
    // from disk, so the profile decides instead of waiting for the install path for good.
    // The profile answer says the region is unsupported (LiveProtocolPipeline.ProfileUnavailableText),
    // and with AppController's forwarding of it the projection reaches the live sentence
    // 「目前还不支持这个区服的自动记录…」 (audit 2026-11-20 J-3, Jb-4).
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
        const QJsonObject profile = ask(backend, QStringLiteral("GetProtocolProfileStatus")).payload;
        QCOMPARE(profile.value("game_build").toString(), capture.value("game_build").toString());
        QCOMPARE(profile.value("status").toString(), QStringLiteral("UNSUPPORTED_BUILD"));
        QCOMPARE(profile.value("message_ref").toObject().value("key").toString(),
                 QStringLiteral("profile.unavailable.region_unsupported"));
        QCOMPARE(profile.value("message").toString(),
                 collectorChinese("profile.unavailable.region_unsupported"));

        mr::AutomaticRecordingController recording(&backend);
        QTRY_COMPARE(recording.state(), QStringLiteral("blocked"));
        // AppController::regionUnsupported, forwarded as AppController does.
        recording.setRegionUnsupported(profile.value("status").toString() == QLatin1String("UNSUPPORTED_BUILD")
            && profile.value("message_ref").toObject().value("key").toString()
                   == QLatin1String("profile.unavailable.region_unsupported"));
        QTRY_VERIFY2(recording.message().contains(QString::fromUtf8("目前还不支持这个区服的自动记录")),
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

    // Audit 2026-11-19 J-1, J-2: the mock answers a region move as the Collector does. A target
    // that is gone is "not found", a record already in the target is a duplicate, and a move is
    // taken back by another move, never by undoing one record's revision.
    void regionMovesRefuseWhatTheCollectorRefusesAndAreNotUndone()
    {
        const QString first = QStringLiteral("11111111-1111-4111-8111-111111111111");
        const QString twin = QStringLiteral("22222222-2222-4222-8222-222222222222");
        const auto manual = [](const QString &id, const QString &region) {
            QJsonObject run = autoRun(id, QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
            run.remove(QStringLiteral("capture_session_id"));
            run.insert(QStringLiteral("region"), region);
            // The duplicate rule compares records that name their job and duty.
            run.insert(QStringLiteral("job_id"), 24);
            run.insert(QStringLiteral("content_id"), 5);
            return run;
        };
        const QJsonArray runs{QJsonObject{{"run_id", first}, {"expected_revision", 1}}};

        mr::MockBackend crowded;
        crowded.resetRuns({manual(first, QStringLiteral("CN")), manual(twin, QStringLiteral("TC"))});
        const int crowdedTarget = regionProfileId(crowded, QStringLiteral("TC"));
        QVERIFY(crowdedTarget > 0);
        QCOMPARE(ask(crowded, QStringLiteral("PreviewRunRegionMigration"),
                     {{"runs", runs}, {"region", "TC"}, {"profile_id", 999}}).code, QStringLiteral("ERR_NOT_FOUND"));
        const Answer duplicate = ask(crowded, QStringLiteral("PreviewRunRegionMigration"),
                                     {{"runs", runs}, {"region", "TC"}, {"profile_id", crowdedTarget}});
        QCOMPARE(duplicate.code, QStringLiteral("ERR_BAD_REQUEST"));
        QVERIFY2(duplicate.message.contains(QString::fromUtf8("重复")), qPrintable(duplicate.message));

        mr::MockBackend backend;
        backend.resetRuns({manual(first, QStringLiteral("CN"))});
        const int target = regionProfileId(backend, QStringLiteral("TC"));
        const Answer preview = ask(backend, QStringLiteral("PreviewRunRegionMigration"),
                                   {{"runs", runs}, {"region", "TC"}, {"profile_id", target}});
        QVERIFY2(preview.ok, qPrintable(preview.message));
        const Answer moved = ask(backend, QStringLiteral("BatchMutateRuns"),
            {{"action", "move_region"}, {"runs", runs}, {"reason", "move"}, {"region", "TC"}, {"profile_id", target},
             {"expected_baseline", preview.payload.value("target_baseline")}, {"baseline_overlap", 0}});
        QVERIFY2(moved.ok, qPrintable(moved.message));
        QCOMPARE(ask(backend, QStringLiteral("UndoRevision"),
                     {{"run_id", first}, {"expected_revision", 2}, {"reason", "undo"}}).code,
                 QStringLiteral("ERR_UNDO_NOT_ALLOWED"));
    }

    // Audit 2026-11-20 J-2: RunRepository.FindRegionMigrationDuplicate keys on the content (the
    // name only without one), the job and each known time, the import's source time included;
    // two records are one only when no match or entry time that both know differs.
    void regionMoveDuplicatesAreTheCollectorsKeys()
    {
        const QString first = QStringLiteral("11111111-1111-4111-8111-111111111111");
        const QString second = QStringLiteral("22222222-2222-4222-8222-222222222222");
        const auto record = [](const QString &id, const QString &source) {
            QJsonObject run = autoRun(id, QStringLiteral("COMPLETED"), true);
            run.insert(QStringLiteral("source"), source);
            run.insert(QStringLiteral("region"), QStringLiteral("CN"));
            run.insert(QStringLiteral("job_id"), 24);
            run.insert(QStringLiteral("content_id"), 5);
            return run;
        };
        const QJsonArray both{QJsonObject{{"run_id", first}, {"expected_revision", 1}},
                              QJsonObject{{"run_id", second}, {"expected_revision", 1}}};
        const auto previewMove = [&both](mr::MockBackend &backend) {
            return ask(backend, QStringLiteral("PreviewRunRegionMigration"),
                       {{"runs", both}, {"region", "TC"}, {"profile_id", regionProfileId(backend, QStringLiteral("TC"))}});
        };

        // Two copies of one imported record that know only the source's time.
        const auto imported = [&record](const QString &id) {
            QJsonObject run = record(id, QStringLiteral("IMPORT"));
            run.insert(QStringLiteral("matched_at_utc"), QJsonValue(QJsonValue::Null));
            run.insert(QStringLiteral("entered_at_utc"), QJsonValue(QJsonValue::Null));
            run.insert(QStringLiteral("import_metadata"),
                       QJsonObject{{"source_recorded_at_utc", "2026-09-01T12:00:00.000Z"}});
            return run;
        };
        mr::MockBackend copies;
        copies.resetRuns({imported(first), imported(second)});
        const Answer refused = previewMove(copies);
        QCOMPARE(refused.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(refused.message,
                 collectorChinese("history.region.duplicate"));

        // One match time but two different entry times: two records, moved together.
        QJsonObject later = record(second, QStringLiteral("MANUAL"));
        later.insert(QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-01T12:02:00.000Z"));
        mr::MockBackend distinct;
        distinct.resetRuns({record(first, QStringLiteral("MANUAL")), later});
        const Answer moved = previewMove(distinct);
        QVERIFY2(moved.ok, qPrintable(moved.message));
    }

    // Audit 2026-11-20 J-1: CharacterIdentityService.ConfirmRegistration refuses a confirmation
    // whose offer moved on with identity.registration_moved; the profile itself still exists.
    void aStaleRegistrationConfirmationSaysTheOfferMoved()
    {
        mr::MockBackend backend;
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0, true);
        const int profile = regionProfileId(backend, QStringLiteral("CN"));
        QVERIFY(profile > 0);
        QVERIFY(ask(backend, QStringLiteral("ResolveCharacterIdentity"),
                    {{"identity_id", identity}, {"action", "IGNORE"}}).ok);
        const Answer confirm = ask(backend, QStringLiteral("ResolveCharacterIdentity"),
            {{"identity_id", identity}, {"action", "BIND"}, {"profile_id", profile},
             {"display_name", QString::fromUtf8("白露")}});
        QCOMPARE(confirm.code, QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(confirm.message,
                 collectorChinese("identity.registration_moved"));
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

    void thePendingIdentityFixtureWorksWithAndWithoutProfiles()
    {
        // --mock-pending-identity alone: the region keeps its single unnamed profile.
        mr::MockBackend plain;
        plain.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        QCOMPARE(plain.characterProfiles().size(), 2); // CN and TC, one each
        QCOMPARE(plain.characterIdentities().size(), 1);
        QCOMPARE(plain.characterIdentities().first().toMap().value(QStringLiteral("run_count")).toInt(), 2);
        const QJsonObject stats = plain.dashboardStats({{QStringLiteral("region"), QStringLiteral("CN")}});
        const QJsonObject cn = stats.value(QStringLiteral("achievements")).toArray().first().toObject();
        QCOMPARE(cn.value(QStringLiteral("unassigned_run_count")).toInt(), 2);
        QCOMPARE(cn.value(QStringLiteral("identification")).toString(), QStringLiteral("AUTOMATIC"));
        QCOMPARE(stats.value(QStringLiteral("identities")).toArray().size(), 1);

        // After --mock-profiles: it claims two of the three runs that fixture left to nobody.
        mr::MockBackend profiles;
        profiles.setCharacterProfilesFixture();
        const auto unassigned = [](const mr::MockBackend &backend) {
            return backend.dashboardStats({{QStringLiteral("region"), QStringLiteral("CN")}})
                .value(QStringLiteral("achievements")).toArray().first().toObject()
                .value(QStringLiteral("unassigned_run_count")).toInt();
        };
        const int before = unassigned(profiles);
        QCOMPARE(before, 3);
        profiles.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        QCOMPARE(profiles.characterIdentities().first().toMap().value(QStringLiteral("run_count")).toInt(), 2);
        QCOMPARE(unassigned(profiles), before);
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
