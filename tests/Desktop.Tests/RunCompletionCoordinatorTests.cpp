#include "RunCompletionCoordinator.h"

#include <QCoreApplication>
#include <QSignalSpy>
#include <QStringList>
#include <QTest>

#include <utility>

namespace {
QJsonObject completedRun(const QString &id = QStringLiteral("run-one"))
{
    return {{QStringLiteral("run_id"), id},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-10-23T10:00:00Z")}};
}

struct Harness
{
    QStringList calls;
    QString database = QStringLiteral("database-one");
    bool verified = true;
    bool reflectionEnabled = true;
    bool resultEnabled = true;
    bool synchronous = false;
    bool statisticsOk = true;
    QList<mr::RunCompletionCoordinator::DashboardDone> waiting;

    mr::RunCompletionCoordinator::Hooks hooks()
    {
        return {
            .refreshHistory = [this] { calls << QStringLiteral("history"); },
            .refreshCurrentRun = [this] { calls << QStringLiteral("current"); },
            .refreshReflections = [this] { calls << QStringLiteral("reflections"); },
            .refreshRecovery = [this] { calls << QStringLiteral("recovery"); },
            .adoptRunRevision = [this](const QJsonObject &) { calls << QStringLiteral("revision"); },
            .requestDashboard = [this](mr::RunCompletionCoordinator::DashboardDone done) {
                calls << QStringLiteral("statistics");
                if (synchronous) {
                    if (done) done(statisticsOk);
                } else if (done) {
                    waiting.append(std::move(done));
                }
            },
            .databaseKey = [this] { return database; },
            .identityVerified = [this] { return verified; },
            .reflectionPromptEnabled = [this] { return reflectionEnabled; },
            .resultPromptEnabled = [this] { return resultEnabled; },
            .offerRecovery = [this](const QVariantMap &, const QString &kind) { calls << kind + QStringLiteral("-offer"); },
        };
    }
};

QDateTime cutoff()
{
    return QDateTime::fromString(QStringLiteral("2026-10-23T09:00:00Z"), Qt::ISODate);
}
} // namespace

class RunCompletionCoordinatorTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void completionPreservesPromptBeforeStatisticsAndSpeech_data();
    void completionPreservesPromptBeforeStatisticsAndSpeech();
    void lateStatisticsIgnoreChangedContext_data();
    void lateStatisticsIgnoreChangedContext();
    void firstDatabaseIdentityKeepsPendingCompletion();
    void changedDatabaseDiscardsPendingStatistics();
    void deletingOwnerDiscardsPendingStatistics();
    void deletingCoordinatorDuringRefreshStopsTheFlow();
    void promptListenerCanInvalidateCompletionBeforeStatistics();
    void stateChangeReadsStatisticsWithoutTerminalSpeech();
    void promptsWaitForAcknowledgmentAndRespectResultDialog();
    void unverifiedIdentityPersistsWithoutOpeningPrompt();
    void replayAndRepeatedCompletionDoNotSpeakAgain();
    void scopeResetDropsOldDeferredPrompt();
};

void RunCompletionCoordinatorTests::completionPreservesPromptBeforeStatisticsAndSpeech_data()
{
    QTest::addColumn<bool>("synchronous");
    QTest::addColumn<bool>("ok");
    QTest::addColumn<bool>("unknownResult");
    for (const bool synchronous : {false, true})
        for (const bool ok : {false, true})
            for (const bool unknown : {false, true})
                QTest::newRow(qPrintable(QStringLiteral("%1-%2-%3").arg(synchronous).arg(ok).arg(unknown)))
                    << synchronous << ok << unknown;
}

void RunCompletionCoordinatorTests::completionPreservesPromptBeforeStatisticsAndSpeech()
{
    QFETCH(bool, synchronous);
    QFETCH(bool, ok);
    QFETCH(bool, unknownResult);
    Harness harness;
    harness.synchronous = synchronous;
    harness.statisticsOk = ok;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    connect(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested, this,
            [&harness] { harness.calls << QStringLiteral("reflection-prompt"); });
    connect(&coordinator, &mr::RunCompletionCoordinator::resultConfirmationRequested, this,
            [&harness] { harness.calls << QStringLiteral("result-prompt"); });
    connect(&coordinator, &mr::RunCompletionCoordinator::announcementRequested, this,
            [&harness] { harness.calls << QStringLiteral("speech"); });
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    auto run = completedRun();
    const QString state = unknownResult ? QStringLiteral("UNKNOWN_FINAL_STATE") : QStringLiteral("COMPLETED");
    if (unknownResult) {
        run.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
        run.insert(QStringLiteral("pending_review"), true);
    }
    coordinator.runChanged(state, run, true);
    const QString promptKind = unknownResult ? QStringLiteral("result") : QStringLiteral("reflection");
    QStringList expected{QStringLiteral("history"), QStringLiteral("current"), QStringLiteral("reflections"),
                         QStringLiteral("recovery"), QStringLiteral("revision"), promptKind + QStringLiteral("-offer"),
                         promptKind + QStringLiteral("-prompt"), QStringLiteral("statistics")};
    if (!synchronous) {
        QCOMPARE(harness.calls, expected);
        QCOMPARE(speech.count(), 0);
        QCOMPARE(harness.waiting.size(), 1);
        harness.waiting.takeFirst()(ok);
    }
    expected << QStringLiteral("speech");
    QCOMPARE(harness.calls, expected);
    QCOMPARE(speech.count(), 1);
    QCOMPARE(speech.first().at(0).toString(), state);
    QCOMPARE(speech.first().at(1).toJsonObject(), run);
    QCOMPARE(speech.first().at(2).toBool(), ok);
}

void RunCompletionCoordinatorTests::lateStatisticsIgnoreChangedContext_data()
{
    QTest::addColumn<bool>("resetPrompts");
    QTest::newRow("connection-changed") << false;
    QTest::newRow("database-switched-away-and-back") << true;
}

void RunCompletionCoordinatorTests::lateStatisticsIgnoreChangedContext()
{
    QFETCH(bool, resetPrompts);
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    coordinator.runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    const auto original = coordinator.captureContext();
    coordinator.invalidateContext(resetPrompts);
    QVERIFY(!coordinator.isCurrentContext(original));
    harness.database = QStringLiteral("database-two");
    coordinator.invalidateContext(resetPrompts);
    harness.database = QStringLiteral("database-one");
    harness.waiting.takeFirst()(true);
    QCOMPARE(speech.count(), 0);
}

void RunCompletionCoordinatorTests::firstDatabaseIdentityKeepsPendingCompletion()
{
    Harness harness;
    harness.database.clear();
    harness.verified = false;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    coordinator.runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    harness.database = QStringLiteral("database-one");
    harness.waiting.takeFirst()(false);
    QCOMPARE(speech.count(), 1);
    QCOMPARE(speech.first().at(2).toBool(), false);
}

void RunCompletionCoordinatorTests::changedDatabaseDiscardsPendingStatistics()
{
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    coordinator.runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    // The identity comparison also guards a replacement observed before its
    // owner has delivered the usual invalidation notification.
    harness.database = QStringLiteral("database-two");
    harness.waiting.takeFirst()(true);
    QCOMPARE(speech.count(), 0);
}

void RunCompletionCoordinatorTests::deletingOwnerDiscardsPendingStatistics()
{
    Harness harness;
    int announcements = 0;
    auto *owner = new QObject;
    auto *coordinator = new mr::RunCompletionCoordinator(harness.hooks(), owner, cutoff());
    connect(coordinator, &mr::RunCompletionCoordinator::announcementRequested, this,
            [&announcements] { ++announcements; });
    coordinator->runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    delete owner;
    harness.waiting.takeFirst()(true);
    QCOMPARE(announcements, 0);
}

void RunCompletionCoordinatorTests::deletingCoordinatorDuringRefreshStopsTheFlow()
{
    Harness harness;
    auto hooks = harness.hooks();
    mr::RunCompletionCoordinator *coordinator = nullptr;
    hooks.refreshHistory = [&] {
        harness.calls << QStringLiteral("history");
        delete coordinator;
        coordinator = nullptr;
    };
    coordinator = new mr::RunCompletionCoordinator(std::move(hooks), nullptr, cutoff());
    coordinator->runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    QVERIFY(!coordinator);
    QCOMPARE(harness.calls, QStringList{QStringLiteral("history")});
}

void RunCompletionCoordinatorTests::promptListenerCanInvalidateCompletionBeforeStatistics()
{
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    connect(&coordinator, &mr::RunCompletionCoordinator::resultConfirmationRequested, this,
            [&coordinator] { coordinator.invalidateContext(true); });
    auto run = completedRun();
    run.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
    run.insert(QStringLiteral("pending_review"), true);
    coordinator.runChanged(QStringLiteral("UNKNOWN_FINAL_STATE"), run, true);
    QVERIFY(!harness.calls.contains(QStringLiteral("statistics")));
    QVERIFY(harness.waiting.isEmpty());
}

void RunCompletionCoordinatorTests::stateChangeReadsStatisticsWithoutTerminalSpeech()
{
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy prompts(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested);
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    coordinator.stateChanged(QStringLiteral("COMPLETED"), completedRun());
    QCOMPARE(harness.calls, QStringList({QStringLiteral("statistics"), QStringLiteral("reflection-offer")}));
    QCOMPARE(prompts.count(), 1);
    QCOMPARE(speech.count(), 0);
    QVERIFY(harness.waiting.isEmpty());
}

void RunCompletionCoordinatorTests::promptsWaitForAcknowledgmentAndRespectResultDialog()
{
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy results(&coordinator, &mr::RunCompletionCoordinator::resultConfirmationRequested);
    QSignalSpy reflections(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested);
    auto pending = completedRun(QStringLiteral("pending"));
    pending.insert(QStringLiteral("pending_review"), true);
    coordinator.offerResult(QStringLiteral("UNKNOWN_FINAL_STATE"), pending);
    QCOMPARE(results.count(), 1);
    coordinator.resultConfirmationShown(QStringLiteral("pending"));
    coordinator.offerReflection(completedRun(QStringLiteral("reflection")));
    QCOMPARE(reflections.count(), 0);
    coordinator.resultConfirmationClosed();
    coordinator.reflectionPromptClosed();
    QCOMPARE(reflections.count(), 1);
    coordinator.reflectionPromptShown(QStringLiteral("reflection"));
    coordinator.offerReflection(completedRun(QStringLiteral("reflection")));
    QCOMPARE(reflections.count(), 1);
    coordinator.offerReflection(completedRun(QStringLiteral("next")));
    coordinator.reflectionPromptClosed();
    QCOMPARE(reflections.count(), 2);
    QCOMPARE(reflections.last().at(0).toMap().value(QStringLiteral("run_id")).toString(), QStringLiteral("next"));
}

void RunCompletionCoordinatorTests::unverifiedIdentityPersistsWithoutOpeningPrompt()
{
    Harness harness;
    harness.verified = false;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy prompts(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested);
    coordinator.offerReflection(completedRun());
    QCOMPARE(harness.calls, QStringList{QStringLiteral("reflection-offer")});
    QCOMPARE(prompts.count(), 0);
    harness.verified = true;
    coordinator.offerReflection(completedRun());
    QCOMPARE(prompts.count(), 1);
}

void RunCompletionCoordinatorTests::replayAndRepeatedCompletionDoNotSpeakAgain()
{
    Harness harness;
    harness.synchronous = true;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy prompts(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested);
    QSignalSpy speech(&coordinator, &mr::RunCompletionCoordinator::announcementRequested);
    auto old = completedRun(QStringLiteral("old"));
    old.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-10-23T08:00:00Z"));
    coordinator.runChanged(QStringLiteral("COMPLETED"), old, true);
    QCOMPARE(prompts.count(), 0);
    QCOMPARE(speech.count(), 0);
    coordinator.runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    coordinator.runChanged(QStringLiteral("COMPLETED"), completedRun(), true);
    QCOMPARE(prompts.count(), 1);
    QCOMPARE(speech.count(), 1);
}

void RunCompletionCoordinatorTests::scopeResetDropsOldDeferredPrompt()
{
    Harness harness;
    mr::RunCompletionCoordinator coordinator(harness.hooks(), nullptr, cutoff());
    QSignalSpy prompts(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested);
    connect(&coordinator, &mr::RunCompletionCoordinator::reflectionPromptRequested, this,
            [&coordinator](const QVariantMap &run) {
        coordinator.reflectionPromptDeferred(run.value(QStringLiteral("run_id")).toString());
    });
    coordinator.offerReflection(completedRun());
    coordinator.invalidateContext(true);
    QCoreApplication::processEvents();
    QCOMPARE(prompts.count(), 1);
    coordinator.offerReflection(completedRun());
    QCOMPARE(prompts.count(), 2);
}

QTEST_GUILESS_MAIN(RunCompletionCoordinatorTests)
#include "RunCompletionCoordinatorTests.moc"
