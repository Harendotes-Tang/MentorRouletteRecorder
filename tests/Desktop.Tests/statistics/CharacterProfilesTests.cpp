// ---------------------------------------------------------------------------
// 角色档案 (character profiles) through the API QML binds to (Runtime.app).
//
// Against the offline MockBackend, which follows the Collector's rules: a player with one
// character keeps the interface they had (characterFeaturesVisible false, the same goal
// and progress), a second character or a run assigned to none switches the features on,
// each profile keeps its own progress, baselineProfileId edits the named profile only,
// 改到角色 moves the checked history rows, and a finished run is announced with its own
// profile's numbers - or with none when it belongs to no character.
//
// 自动识别角色: a character the Collector told apart and bound to no profile shows the
// features too; BIND / IGNORE / ASK_AGAIN / forget / create-with-identity answer through
// characterProfileSaved / Failed, and the identification switch round-trips.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "IpcFraming.h"
#include "MockBackend.h"
#include "TtsService.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QJsonArray>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QTest>
#include <QTextToSpeech>

namespace {

QJsonObject cnRun(const QString &id)
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("region"), QStringLiteral("CN")},
            {QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
            {QStringLiteral("mentor_roulette_id"), 9},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("content_id"), 5},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-01T12:01:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")},
            {QStringLiteral("duration_ms"), 540000.0},
            {QStringLiteral("contributes_to_goal"), true},
            {QStringLiteral("manually_created"), false},
            {QStringLiteral("manually_corrected"), false},
            {QStringLiteral("soft_deleted"), false},
            {QStringLiteral("pending_review"), false},
            {QStringLiteral("created_at_utc"), QStringLiteral("2026-09-01T12:00:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-09-01T12:10:00.000Z")}};
}

/// Three CN completions under the region's own profile: goal 100, baseline 10, progress 13.
void seed(mr::MockBackend &backend)
{
    backend.resetRuns({cnRun(QStringLiteral("11111111-1111-4111-8111-111111111111")),
                       cnRun(QStringLiteral("22222222-2222-4222-8222-222222222222")),
                       cnRun(QStringLiteral("33333333-3333-4333-8333-333333333333"))});
    backend.setAchievement(100, 10);
}

/// Reads everything once and waits until the profiles of the answer are held.
bool load(mr::AppController &app)
{
    app.refreshAll();
    return QTest::qWaitFor([&app] {
        return !app.characterProfiles().isEmpty() && app.achievementSettingsLoaded();
    }, 3000);
}

int profileNumber(mr::AppController &app, int profileId, const char *field)
{
    return app.characterProfile(profileId).value(QLatin1String(field), -1).toInt();
}

class CharacterProfilesTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void oneProfileLeavesTheInterfaceAsItWas()
    {
        mr::MockBackend backend;
        seed(backend);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        const QVariantList cn = app.characterProfilesForRegion(QStringLiteral("CN"));
        QCOMPARE(cn.size(), 1);
        QCOMPARE(cn.first().toMap().value(QStringLiteral("display_name")).toString(), QString());
        QVERIFY(!app.characterFeaturesVisible(QStringLiteral("CN")));
        QVERIFY(!app.characterFeaturesVisible(QString()));
        QCOMPARE(app.goalCount(), 100);
        QCOMPARE(app.baselineCount(), 10);
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 13);
        QCOMPARE(app.baselineProfileId(), 0);
        QVERIFY(app.characterProfile(999).isEmpty());
        // The wire maps unchanged.
        QCOMPARE(app.characterProfiles().first().toMap().value(QStringLiteral("achievement_progress")).toInt(), 13);
    }

    void aRunAssignedToNoneAlsoShowsTheFeatures()
    {
        mr::MockBackend backend;
        QJsonObject loose = cnRun(QStringLiteral("44444444-4444-4444-8444-444444444444"));
        loose.insert(QStringLiteral("profile_id"), QJsonValue(QJsonValue::Null));
        backend.resetRuns({cnRun(QStringLiteral("11111111-1111-4111-8111-111111111111")), loose});
        backend.setAchievement(100, 0);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_VERIFY(app.characterFeaturesVisible(QStringLiteral("CN")));
        QVERIFY(app.characterFeaturesVisible(QString()));
        QVERIFY(!app.characterFeaturesVisible(QStringLiteral("TC")));
        // The run of nobody counts in nobody's progress.
        QCOMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 1);
    }

    void aSecondProfileShowsTheFeaturesAndEachKeepsItsOwnProgress()
    {
        mr::MockBackend backend;
        seed(backend);
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 3, false);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QVERIFY(app.characterFeaturesVisible(QStringLiteral("CN")));
        QVERIFY(app.characterFeaturesVisible(QString()));
        QVERIFY(!app.characterFeaturesVisible(QStringLiteral("TC")));
        QCOMPARE(profileNumber(app, second, "achievement_progress"), 3);
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 13);
        QCOMPARE(app.goalCount(), 100);

        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        QSignalSpy failed(&app, &mr::AppController::characterProfileFailed);
        QSignalSpy saving(&app, &mr::AppController::characterProfileSavingChanged);
        app.createCharacterProfile(QStringLiteral("TC"), QString::fromUtf8("台服角色"), 300, 12, false);
        QVERIFY(app.characterProfileSaving());
        QTRY_COMPARE(saved.size(), 1);
        QVERIFY(!app.characterProfileSaving());
        QCOMPARE(saving.size(), 2);
        QCOMPARE(saved.at(0).at(1).toString(), QStringLiteral("create"));
        const int created = saved.at(0).at(0).toInt();
        // The reply's list is applied at once, before any read.
        QCOMPARE(app.characterProfilesForRegion(QStringLiteral("TC")).size(), 2);
        QCOMPARE(profileNumber(app, created, "baseline_completed_count"), 12);
        QVERIFY(app.characterFeaturesVisible(QStringLiteral("TC")));

        app.renameCharacterProfile(created, QString::fromUtf8("台服角色二"));
        QTRY_COMPARE(saved.size(), 2);
        QCOMPARE(saved.at(1).at(1).toString(), QStringLiteral("rename"));
        QCOMPARE(app.characterProfile(created).value(QStringLiteral("display_name")).toString(),
                 QString::fromUtf8("台服角色二"));
        app.selectCharacterProfile(second);
        QTRY_COMPARE(saved.size(), 3);
        QCOMPARE(saved.at(2).at(1).toString(), QStringLiteral("select"));
        QVERIFY(app.characterProfile(second).value(QStringLiteral("is_current")).toBool());
        // The dashboard is read again, as after stats_invalidated: CN now projects B.
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 3);

        app.deleteCharacterProfile(second); // the current one
        QTRY_COMPARE(failed.size(), 1);
        QCOMPARE(failed.at(0).at(0).toString(), QStringLiteral("delete"));
        QCOMPARE(failed.at(0).at(1).toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(failed.at(0).at(2).toString(),
                 QString::fromUtf8("新记录正记在这个角色名下。请先切换到其他角色，再删除它。"));
        app.renameCharacterProfile(created, QString());
        QTRY_COMPARE(failed.size(), 2);
        QCOMPARE(failed.at(1).at(0).toString(), QStringLiteral("rename"));
        app.deleteCharacterProfile(created);
        QTRY_COMPARE(saved.size(), 4);
        QCOMPARE(saved.at(3).at(1).toString(), QStringLiteral("delete"));
        QVERIFY(app.characterProfile(created).isEmpty());
        QVERIFY(!app.characterFeaturesVisible(QStringLiteral("TC")));
    }

    void baselineProfileIdEditsTheNamedProfileOnly()
    {
        mr::MockBackend backend;
        seed(backend);
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 3, false);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QSignalSpy changed(&app, &mr::AppController::baselineProfileIdChanged);
        app.setBaselineProfileId(999); // unknown: refused
        QCOMPARE(app.baselineProfileId(), 0);
        app.setBaselineProfileId(second);
        QCOMPARE(app.baselineProfileId(), second);
        QCOMPARE(changed.size(), 1);
        QCOMPARE(app.goalCount(), 50);
        QCOMPARE(app.baselineCount(), 3);

        QSignalSpy baselineSaved(&app, &mr::AppController::baselineSaved);
        app.updateAchievementBaseline(60, 8, QStringLiteral("fix"));
        QTRY_COMPARE(baselineSaved.size(), 1);
        QTRY_COMPARE(profileNumber(app, second, "baseline_completed_count"), 8);
        QCOMPARE(app.goalCount(), 60);
        // The current profile - what the region's card shows - is left alone.
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("baseline_completed_count")).toInt(), 10);
        QCOMPARE(app.achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("goal_count")).toInt(), 100);

        // Another region resets it, and a profile of another region is refused.
        app.setBaselineRegion(QStringLiteral("TC"));
        QCOMPARE(app.baselineProfileId(), 0);
        app.setBaselineProfileId(second);
        QCOMPARE(app.baselineProfileId(), 0);

        // So does the profile going away.
        app.setBaselineRegion(QStringLiteral("CN"));
        app.setBaselineProfileId(second);
        QCOMPARE(app.baselineProfileId(), second);
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.deleteCharacterProfile(second);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(app.baselineProfileId(), 0);
        QCOMPARE(app.goalCount(), 100);
    }

    void assignProfileMovesTheCheckedRunsAndRefreshes()
    {
        mr::MockBackend backend;
        seed(backend);
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 0, false);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() == 3);
        app.setHistoryRunChecked(app.runs()->runAt(0), true);
        app.setHistoryRunChecked(app.runs()->runAt(1), true);
        QCOMPARE(app.checkedHistoryRunCount(), 2);
        QSignalSpy refreshed(&app, &mr::AppController::characterProfilesChanged);
        app.assignCheckedHistoryRunsToProfile(second, QStringLiteral("move"));
        QVERIFY(app.historyBatchRunning());
        QTRY_VERIFY(!app.historyBatchRunning());
        QCOMPARE(app.checkedHistoryRunCount(), 0);
        QVERIFY2(app.historyBatchFeedback().contains(QLatin1Char('2')), qPrintable(app.historyBatchFeedback()));
        QTRY_COMPARE(profileNumber(app, second, "run_count"), 2);
        QVERIFY(refreshed.size() >= 1);
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 11);

        // The history filter passes both keys through to QueryRuns unchanged.
        app.setHistoryFilter({{QStringLiteral("profile_id"), second}});
        QCOMPARE(app.historyFilter().value(QStringLiteral("profile_id")).toInt(), second);
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->total() == 2);
        app.setHistoryFilter({{QStringLiteral("profile_unassigned"), true}});
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->total() == 0);

        // 0 files the checked rows under no character.
        app.resetHistoryFilter();
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() == 3);
        app.setHistoryRunChecked(app.runs()->runAt(2), true);
        app.assignCheckedHistoryRunsToProfile(0, QStringLiteral("nobody"));
        QTRY_VERIFY(!app.historyBatchRunning() && app.checkedHistoryRunCount() == 0);
        QTRY_COMPARE(app.achievementForRegion(QStringLiteral("CN"))
                         .value(QStringLiteral("unassigned_run_count")).toInt(), 1);
        // A refusal keeps the rows checked and says why.
        app.setHistoryFilter({{QStringLiteral("profile_unassigned"), true}});
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() == 1);
        app.setHistoryRunChecked(app.runs()->runAt(0), true);
        app.assignCheckedHistoryRunsToProfile(0, QStringLiteral("again"));
        QTRY_VERIFY(!app.historyBatchRunning());
        QCOMPARE(app.checkedHistoryRunCount(), 1);
        QVERIFY2(app.historyBatchFeedback().contains(QString::fromUtf8("已经都记在这里")),
                 qPrintable(app.historyBatchFeedback()));
    }

    void theAnnouncementUsesTheRunsProfileAndNoNumbersForNobody()
    {
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        mr::AppSettings settings(directory.filePath(QStringLiteral("settings.ini")), nullptr);
        settings.setTtsEnabled(true);
        mr::MockBackend backend;
        // The sample history stays with the first character; the second one is current, so
        // the live run is filed under it.
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 1000, 260, true);
        mr::AppController app(&backend, &settings, nullptr, nullptr, mr::TtsService::EngineMode::Mock);
        const auto *engine = app.tts()->findChild<QTextToSpeech *>();
        if (!engine || !app.tts()->isAvailable())
            QSKIP("Qt's mock speech engine plugin is not installed.");
        QVERIFY(load(app));
        QSignalSpy spoke(app.tts(), &mr::TtsService::spoke);
        backend.emitRunFinished(QStringLiteral("COMPLETED"));
        QTRY_COMPARE(spoke.size(), 1);
        QCOMPARE(spoke.at(0).at(0).toString(), QStringLiteral("completed"));
        const QString line = spoke.at(0).at(1).toString();
        QVERIFY2(line.contains(QStringLiteral("260")) && line.contains(QStringLiteral("740")), qPrintable(line));
        QCOMPARE(profileNumber(app, second, "achievement_progress"), 260);

        // A run that belongs to no character counts in nobody's progress.
        QJsonObject run = cnRun(QStringLiteral("55555555-5555-4555-8555-555555555555"));
        run.insert(QStringLiteral("ended_at_utc"), QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs));
        run.insert(QStringLiteral("profile_id"), QJsonValue(QJsonValue::Null));
        const QVariantMap event{{QStringLiteral("event_id"), mr::ipc::newRequestId()},
                                {QStringLiteral("event_type"), QStringLiteral("RunFinished")},
                                {QStringLiteral("kind"), QStringLiteral("run_finished")},
                                {QStringLiteral("emitted_at_utc"),
                                 QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)},
                                {QStringLiteral("state"), QStringLiteral("COMPLETED")},
                                {QStringLiteral("run"), run.toVariantMap()}};
        Q_EMIT backend.liveEvent(event);
        QTRY_COMPARE(spoke.size(), 2);
        QCOMPARE(spoke.at(1).at(1).toString(), QString::fromUtf8("导随完成"));
        QVERIFY(app.achievementForRun(run.toVariantMap()).isEmpty());
        // Without the field (an older Collector) the run counts for its region.
        run.remove(QStringLiteral("profile_id"));
        QCOMPARE(app.achievementForRun(run.toVariantMap()).value(QStringLiteral("profile_id")).toInt(), second);
    }

    // -- 自动识别角色 ------------------------------------------------------------

    void aWaitingIdentityShowsTheFeaturesForOneProfile()
    {
        mr::MockBackend backend;
        seed(backend);
        // No run of its own: the identity alone switches the features on.
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.characterIdentities().size(), 1);
        QCOMPARE(app.characterProfilesForRegion(QStringLiteral("CN")).size(), 1);
        QCOMPARE(app.achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("unassigned_run_count")).toInt(), 0);
        QVERIFY(app.characterFeaturesVisible(QStringLiteral("CN")));
        QVERIFY(app.characterFeaturesVisible(QString()));
        QVERIFY(!app.characterFeaturesVisible(QStringLiteral("TC")));
        const QVariantList pending = app.pendingCharacterIdentities(QStringLiteral("CN"));
        QCOMPARE(pending.size(), 1);
        QCOMPARE(pending.first().toMap().value(QStringLiteral("identity_id")).toInt(), identity);
        QCOMPARE(pending.first().toMap().value(QStringLiteral("state")).toString(), QStringLiteral("PENDING"));
        QCOMPARE(app.pendingCharacterIdentities(QString()).size(), 1);
        QVERIFY(app.pendingCharacterIdentities(QStringLiteral("TC")).isEmpty());
        QCOMPARE(app.ignoredCharacterIdentityCount(QStringLiteral("CN")), 0);
        QCOMPARE(app.characterIdentification(QStringLiteral("CN")), QStringLiteral("AUTOMATIC"));
        QCOMPARE(app.characterIdentification(QStringLiteral("TC")), QStringLiteral("MANUAL_REGION"));
        QCOMPARE(app.characterIdentification(QStringLiteral("GLOBAL")), QStringLiteral("AUTOMATIC"));
        QCOMPARE(app.characterIdentification(QStringLiteral("UNKNOWN")), QString()); // never read
        QVERIFY(app.characterIdentificationEnabled());
        QCOMPARE(app.characterProfilesForRegion(QStringLiteral("CN")).first().toMap()
                     .value(QStringLiteral("identity_count")).toInt(), 0);
    }

    void bindMovesTheRunsAndIsNoMilestone()
    {
        mr::MockBackend backend;
        seed(backend);
        // B is current with goal 2 and progress 1: two more completions cross a lap of the same
        // profile, which a move of old runs must not celebrate.
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 2, 1, true);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        QCOMPARE(backend.characterIdentities().first().toMap().value(QStringLiteral("run_count")).toInt(), 2);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 1);
        QCOMPARE(app.achievementForRegion(QStringLiteral("CN")).value(QStringLiteral("unassigned_run_count")).toInt(), 2);
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() == 3);

        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        QSignalSpy milestone(&app, &mr::AppController::regionalAchievementMilestoneReached);
        QSignalSpy identitiesChanged(&app, &mr::AppController::characterIdentitiesChanged);
        app.bindCharacterIdentity(identity, second);
        QVERIFY(app.characterProfileSaving());
        QTRY_COMPARE(saved.size(), 1);
        QVERIFY(!app.characterProfileSaving());
        QCOMPARE(saved.at(0).at(0).toInt(), second);
        QCOMPARE(saved.at(0).at(1).toString(), QStringLiteral("bind"));
        // The reply's lists are applied at once.
        QVERIFY(identitiesChanged.size() >= 1);
        QVERIFY(app.characterIdentities().isEmpty());
        QCOMPARE(profileNumber(app, second, "run_count"), 2);
        QCOMPARE(profileNumber(app, second, "identity_count"), 1);
        // Statistics and the history list are read again.
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 3);
        QTRY_COMPARE(app.achievementForRegion(QStringLiteral("CN"))
                         .value(QStringLiteral("unassigned_run_count")).toInt(), 0);
        app.setHistoryFilter({{QStringLiteral("profile_id"), second}});
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->total() == 2);
        QTest::qWait(50); // the Collector's own stats_invalidated arrives too
        QCOMPARE(milestone.size(), 0);
        QCOMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 3);
    }

    void aReadAnsweredDuringABindPrimesInsteadOfCelebrating()
    {
        mr::MockBackend backend;
        seed(backend);
        // B is current, goal 2, progress 1; the bind takes it to 3 - a lap crossed by moved runs.
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 2, 1, true);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 1);
        QTest::qWait(50); // nothing of the first load left on the wire
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        QSignalSpy milestone(&app, &mr::AppController::regionalAchievementMilestoneReached);
        // A read sent before the bind is answered, with the numbers before it, while the bind
        // is still unanswered; so is the read the Collector's stats_invalidated asks for.
        backend.setReplyDelay(150, QStringLiteral("GetDashboardStats"));
        backend.setReplyDelay(400, QStringLiteral("ResolveCharacterIdentity"));
        app.refreshDashboard();
        app.bindCharacterIdentity(identity, second);
        QTRY_COMPARE_WITH_TIMEOUT(saved.size(), 1, 3000);
        backend.setReplyDelay(0, QStringLiteral("GetDashboardStats"));
        app.refreshDashboard();
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 3);
        QTest::qWait(300); // the reads the bind's answer asked for arrive as well
        QCOMPARE(milestone.size(), 0);
    }

    void bindSwitchesOnlyToTheLoggedInCharacter()
    {
        mr::MockBackend backend;
        seed(backend);
        const int original = backend.characterProfiles().first().toObject().value(QStringLiteral("profile_id")).toInt();
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 0, false);
        const int elsewhere = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        const int playing = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        backend.setLoggedInCharacterIdentity(playing);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        // Not the character logged in: its runs move, the current profile stays.
        app.bindCharacterIdentity(elsewhere, second);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(profileNumber(app, second, "run_count"), 1);
        QVERIFY(app.characterProfile(original).value(QStringLiteral("is_current")).toBool());
        QVERIFY(!app.characterProfile(second).value(QStringLiteral("is_current")).toBool());
        // The one logged in: the Collector switches to its profile.
        app.bindCharacterIdentity(playing, second);
        QTRY_COMPARE(saved.size(), 2);
        QCOMPARE(profileNumber(app, second, "run_count"), 2);
        QVERIFY(app.characterProfile(second).value(QStringLiteral("is_current")).toBool());
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 2);
    }

    void aRunMovedByHandLeavesItsIdentity()
    {
        mr::MockBackend backend;
        seed(backend);
        const int original = backend.characterProfiles().first().toObject().value(QStringLiteral("profile_id")).toInt();
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 0, false);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        app.setHistoryFilter({{QStringLiteral("profile_unassigned"), true}});
        QTRY_VERIFY(!app.runs()->isLoading() && app.runs()->rowCount() == 2);
        app.setHistoryRunChecked(app.runs()->runAt(0), true);
        app.assignCheckedHistoryRunsToProfile(original, QStringLiteral("by hand"));
        QTRY_VERIFY(!app.historyBatchRunning() && app.checkedHistoryRunCount() == 0);
        QCOMPARE(backend.characterIdentities().first().toMap().value(QStringLiteral("run_count")).toInt(), 1);
        // A later BIND moves only the run still waiting, not the one moved by hand.
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.bindCharacterIdentity(identity, second);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(profileNumber(app, second, "run_count"), 1);
        QCOMPARE(profileNumber(app, original, "run_count"), 2);
    }

    void createWithAnIdentityBindsItToTheNewProfile()
    {
        mr::MockBackend backend;
        seed(backend);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 2);
        const int playing = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        backend.setLoggedInCharacterIdentity(playing);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.pendingCharacterIdentities(QStringLiteral("CN")).size(), 2);
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.createCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("新角色"), 100, 5, false, identity);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(saved.at(0).at(1).toString(), QStringLiteral("create"));
        const int created = saved.at(0).at(0).toInt();
        // Not the character logged in, and make_current false: the region keeps its current one.
        QVERIFY(!app.characterProfile(created).value(QStringLiteral("is_current")).toBool());
        QCOMPARE(profileNumber(app, created, "run_count"), 2);
        QCOMPARE(profileNumber(app, created, "identity_count"), 1);
        QCOMPARE(profileNumber(app, created, "achievement_progress"), 7);
        QCOMPARE(app.pendingCharacterIdentities(QStringLiteral("CN")).size(), 1);
        QTRY_COMPARE(app.dashboard().value(QStringLiteral("achievement_progress")).toInt(), 11);
        // The character logged in: its new profile becomes the current one.
        app.createCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("在玩的"), 100, 0, false, playing);
        QTRY_COMPARE(saved.size(), 2);
        QVERIFY(app.characterProfile(saved.at(1).at(0).toInt()).value(QStringLiteral("is_current")).toBool());
        QTRY_VERIFY(app.characterIdentities().isEmpty());

        // An identity already bound is refused, and nothing is created.
        QSignalSpy failed(&app, &mr::AppController::characterProfileFailed);
        app.createCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("又一个"), 100, 0, false, identity);
        QTRY_COMPARE(failed.size(), 1);
        QCOMPARE(failed.at(0).at(0).toString(), QStringLiteral("create"));
        QCOMPARE(failed.at(0).at(1).toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(app.characterProfilesForRegion(QStringLiteral("CN")).size(), 3);
    }

    void createDropsTheIdentityBeforeItIsReadAgain()
    {
        mr::MockBackend backend;
        seed(backend);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.characterIdentities().size(), 1);
        QTest::qWait(50);
        // Every later read is held, so only the create's own answer can change the list.
        backend.setReplyDelay(5000, QStringLiteral("GetDashboardStats"));
        qsizetype listedAtSave = -1;
        connect(&app, &mr::AppController::characterProfileSaved, &app,
                [&app, &listedAtSave] { listedAtSave = app.characterIdentities().size(); });
        QSignalSpy changed(&app, &mr::AppController::characterIdentitiesChanged);
        app.createCharacterProfile(QStringLiteral("CN"), QString::fromUtf8("新角色"), 100, 0, false, identity);
        QTRY_VERIFY(listedAtSave >= 0);
        QCOMPARE(listedAtSave, 0);
        QCOMPARE(changed.size(), 1);
        QVERIFY(app.pendingCharacterIdentities(QStringLiteral("CN")).isEmpty());
    }

    void aRefusalForAnIdentityThatIsGoneReadsTheListsAgain()
    {
        mr::MockBackend backend;
        seed(backend);
        const int gone = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        const int other = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        const int profile = backend.characterProfiles().first().toObject().value(QStringLiteral("profile_id")).toInt();
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.characterIdentities().size(), 2);
        backend.dropCharacterIdentity(gone);
        QSignalSpy failed(&app, &mr::AppController::characterProfileFailed);
        app.bindCharacterIdentity(gone, profile);
        QTRY_COMPARE(failed.size(), 1);
        QCOMPARE(failed.at(0).at(0).toString(), QStringLiteral("bind"));
        QCOMPARE(failed.at(0).at(1).toString(), QStringLiteral("ERR_NOT_FOUND"));
        // The prompt stops asking about it: the lists are read again.
        QTRY_COMPARE(app.characterIdentities(), backend.characterIdentities());
        QCOMPARE(app.characterIdentities().size(), 1);
        QCOMPARE(app.characterIdentities().first().toMap().value(QStringLiteral("identity_id")).toInt(), other);
    }

    void ignoreThenAskAgainFlipsTheStateInTurn()
    {
        mr::MockBackend backend;
        seed(backend);
        const int first = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        const int second = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        const int taiwan = backend.addCharacterIdentity(QStringLiteral("TC"), QStringLiteral("IGNORED"), 0);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_COMPARE(app.characterIdentities().size(), 3);
        QCOMPARE(app.ignoredCharacterIdentityCount(QStringLiteral("TC")), 1);
        QCOMPARE(app.ignoredCharacterIdentityCount(QString()), 1);
        // An ignored identity still shows the features: the player can change their mind.
        QVERIFY(app.characterFeaturesVisible(QStringLiteral("TC")));

        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.ignoreCharacterIdentity(first);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(saved.at(0).at(0).toInt(), 0);
        QCOMPARE(saved.at(0).at(1).toString(), QStringLiteral("ignore"));
        app.ignoreCharacterIdentity(second);
        QTRY_COMPARE(saved.size(), 2);
        QCOMPARE(app.ignoredCharacterIdentityCount(QStringLiteral("CN")), 2);
        QVERIFY(app.pendingCharacterIdentities(QStringLiteral("CN")).isEmpty());

        // Both CN identities, one request after the other, and one answer at the end; the
        // saving flag stays up across the chain. TC's is left alone.
        QSignalSpy saving(&app, &mr::AppController::characterProfileSavingChanged);
        app.askAgainAboutCharacterIdentities(QStringLiteral("CN"));
        QVERIFY(app.characterProfileSaving());
        QTRY_COMPARE(saved.size(), 3);
        QCOMPARE(saved.at(2).at(1).toString(), QStringLiteral("ask_again"));
        QCOMPARE(saving.size(), 2);
        QCOMPARE(app.ignoredCharacterIdentityCount(QStringLiteral("CN")), 0);
        QCOMPARE(app.pendingCharacterIdentities(QStringLiteral("CN")).size(), 2);
        QCOMPARE(app.ignoredCharacterIdentityCount(QStringLiteral("TC")), 1);
        // Nothing left to ask again about: answered without a request.
        app.askAgainAboutCharacterIdentities(QStringLiteral("CN"));
        QCOMPARE(saved.size(), 4);
        QCOMPARE(saved.at(3).at(1).toString(), QStringLiteral("ask_again"));
        Q_UNUSED(taiwan);
    }

    void refusedIdentityRequestsSurfaceAsFailures()
    {
        mr::MockBackend backend;
        seed(backend);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 0);
        const int taiwanProfile = backend.addCharacterProfile(QStringLiteral("TC"), QStringLiteral("T"), 100, 0, false);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QSignalSpy failed(&app, &mr::AppController::characterProfileFailed);
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.bindCharacterIdentity(identity, taiwanProfile);
        QTRY_COMPARE(failed.size(), 1);
        QVERIFY(!app.characterProfileSaving());
        QCOMPARE(failed.at(0).at(0).toString(), QStringLiteral("bind"));
        QCOMPARE(failed.at(0).at(1).toString(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(failed.at(0).at(2).toString(), QString::fromUtf8("所选角色不属于这条记录的区服。"));
        app.ignoreCharacterIdentity(999);
        QTRY_COMPARE(failed.size(), 2);
        QCOMPARE(failed.at(1).at(0).toString(), QStringLiteral("ignore"));
        QCOMPARE(failed.at(1).at(1).toString(), QStringLiteral("ERR_NOT_FOUND"));
        app.forgetCharacterIdentities(999);
        QTRY_COMPARE(failed.size(), 3);
        QCOMPARE(failed.at(2).at(0).toString(), QStringLiteral("forget"));
        QCOMPARE(failed.at(2).at(1).toString(), QStringLiteral("ERR_NOT_FOUND"));
        QCOMPARE(saved.size(), 0);
        QVERIFY(!app.characterProfileSaving());
        // The identity is still waiting.
        QCOMPARE(app.pendingCharacterIdentities(QStringLiteral("CN")).size(), 1);
    }

    void forgetReturnsTheBoundIdentitiesToPending()
    {
        mr::MockBackend backend;
        seed(backend);
        const int second = backend.addCharacterProfile(QStringLiteral("CN"), QStringLiteral("B"), 50, 0, false);
        const int identity = backend.addCharacterIdentity(QStringLiteral("CN"), QStringLiteral("PENDING"), 1);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QSignalSpy saved(&app, &mr::AppController::characterProfileSaved);
        app.bindCharacterIdentity(identity, second);
        QTRY_COMPARE(saved.size(), 1);
        QCOMPARE(profileNumber(app, second, "identity_count"), 1);
        app.forgetCharacterIdentities(second);
        QTRY_COMPARE(saved.size(), 2);
        QCOMPARE(saved.at(1).at(0).toInt(), second);
        QCOMPARE(saved.at(1).at(1).toString(), QStringLiteral("forget"));
        QCOMPARE(profileNumber(app, second, "identity_count"), 0);
        // The statistics read brings the identity back, waiting, its runs kept by the profile.
        QTRY_COMPARE(app.pendingCharacterIdentities(QStringLiteral("CN")).size(), 1);
        const QVariantMap back = app.pendingCharacterIdentities(QStringLiteral("CN")).first().toMap();
        QCOMPARE(back.value(QStringLiteral("identity_id")).toInt(), identity);
        QCOMPARE(back.value(QStringLiteral("run_count")).toInt(), 0);
        QCOMPARE(profileNumber(app, second, "run_count"), 1);
    }

    void theIdentificationSwitchRoundTrips()
    {
        mr::MockBackend backend;
        seed(backend);
        mr::AppController app(&backend, nullptr);
        QVERIFY(load(app));
        QTRY_VERIFY(app.captureSettingsLoaded());
        QVERIFY(app.characterIdentificationEnabled());
        QCOMPARE(app.captureSettings().value(QStringLiteral("character_identification_enabled")).toBool(), true);
        QTRY_COMPARE(app.characterIdentification(QStringLiteral("CN")), QStringLiteral("AUTOMATIC"));

        app.setCharacterIdentificationEnabled(false);
        QVERIFY(!app.characterIdentificationEnabled()); // optimistic while the write waits
        QTRY_COMPARE(backend.lastCaptureSettingsUpdate(),
                     (QJsonObject{{QStringLiteral("character_identification_enabled"), false}}));
        QTRY_COMPARE(app.characterIdentification(QStringLiteral("CN")), QStringLiteral("MANUAL_OFF"));
        QTRY_COMPARE(app.characterIdentification(QStringLiteral("TC")), QStringLiteral("MANUAL_OFF"));
        QVERIFY(!app.characterIdentificationEnabled());

        app.setCharacterIdentificationEnabled(true);
        QTRY_COMPARE(app.characterIdentification(QStringLiteral("CN")), QStringLiteral("AUTOMATIC"));
        QTRY_COMPARE(app.characterIdentification(QStringLiteral("TC")), QStringLiteral("MANUAL_REGION"));
        QVERIFY(app.characterIdentificationEnabled());
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

MR_DESKTOP_TEST_SUITE(CharacterProfiles)
{
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    CharacterProfilesTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "CharacterProfilesTests.moc"
