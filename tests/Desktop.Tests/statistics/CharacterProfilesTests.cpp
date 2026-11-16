// ---------------------------------------------------------------------------
// 角色档案 (character profiles) through the API QML binds to (Runtime.app).
//
// Against the offline MockBackend, which follows the Collector's rules: a player with one
// character keeps the interface they had (characterFeaturesVisible false, the same goal
// and progress), a second character or a run assigned to none switches the features on,
// each profile keeps its own progress, baselineProfileId edits the named profile only,
// 改到角色 moves the checked history rows, and a finished run is announced with its own
// profile's numbers - or with none when it belongs to no character.
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
