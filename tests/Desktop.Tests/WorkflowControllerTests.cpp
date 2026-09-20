#include "HistoryController.h"
#include "StatisticsController.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QJsonArray>
#include <QSignalSpy>
#include <QTest>
#include <memory>

namespace {

// An explicitly completed backend: these tests prove ownership and ordering,
// independent of the application shell, a QML engine, and process supervision.
class ControlledBackend final : public mr::IBackend
{
public:
    struct Call {
        QString type;
        QJsonObject payload;
        QPointer<mr::BackendReply> reply;
    };
    QList<Call> calls;
    bool synchronous = false;
    bool reject = false;
    QJsonObject answer;

    QString backendName() const override { return QStringLiteral("controlled"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({type, payload, reply});
        if (synchronous) {
            if (reject)
                reply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("refused"));
            else
                reply->succeed(answer);
        }
        return reply;
    }
    Call last(const QString &type) const
    {
        for (auto it = calls.crbegin(); it != calls.crend(); ++it)
            if (it->type == type)
                return *it;
        return {};
    }
    int count(const QString &type) const
    {
        int result = 0;
        for (const auto &call : calls)
            result += call.type == type;
        return result;
    }
};

QVariantMap run(const QString &id, int revision = 2)
{
    return {{QStringLiteral("run_id"), id}, {QStringLiteral("revision"), revision},
            {QStringLiteral("pending_review"), true}};
}

QJsonObject revisions(int revision)
{
    return {{QStringLiteral("items"), QJsonArray{
        QJsonObject{{QStringLiteral("revision"), 1}},
        QJsonObject{{QStringLiteral("revision"), revision}}}}};
}

QJsonObject dashboard(int count)
{
    QJsonArray buckets;
    for (int i = 0; i < 10; ++i)
        buckets.append(QJsonObject{{QStringLiteral("start_utc"),
                                    QStringLiteral("2026-09-%1T00:00:00Z")
                                        .arg(i + 1, 2, 10, QLatin1Char('0'))},
                                   {QStringLiteral("completed_count"), 1}});
    return {{QStringLiteral("completed_count"), count},
            {QStringLiteral("goal_count"), 2000},
            {QStringLiteral("trend"), QJsonObject{
                {QStringLiteral("granularity"), QStringLiteral("day")},
                {QStringLiteral("buckets"), buckets}}}};
}
}

class WorkflowControllerTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void statisticsAdoptionPrecedesSignalsAndCompletion()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        QCOMPARE(statistics.dungeons()->parent(), &statistics);
        QCOMPARE(statistics.jobs()->parent(), &statistics);
        QStringList order;
        connect(&statistics, &mr::StatisticsController::dashboardChanged, this, [&] {
            QCOMPARE(statistics.dashboard().value(QStringLiteral("completed_count")).toInt(), 42);
            QCOMPARE(statistics.completedLast7Days(), 7);
            QCOMPARE(statistics.completedLast30Days(), 10);
            order << QStringLiteral("dashboard");
        });
        connect(&statistics, &mr::StatisticsController::trendChanged, this,
                [&] { order << QStringLiteral("trend"); });
        connect(&statistics, &mr::StatisticsController::dashboardRequestFinished, this,
                [&](bool ok) { QVERIFY(ok); order << QStringLiteral("projection"); });
        statistics.requestDashboard([&](bool ok) {
            QVERIFY(ok);
            QCOMPARE(statistics.trendBuckets().size(), 10);
            order << QStringLiteral("completion");
        });
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(dashboard(42));
        QCOMPARE(order, (QStringList{QStringLiteral("dashboard"), QStringLiteral("trend"),
                                    QStringLiteral("projection"), QStringLiteral("completion")}));
        statistics.setTrendWindowDays(7);
        QCOMPARE(statistics.trendBuckets().size(), 7);
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 1);
    }

    void synchronousStatisticsAndFailedReadRetainSnapshot()
    {
        ControlledBackend backend;
        backend.synchronous = true;
        backend.answer = dashboard(18);
        mr::StatisticsController statistics(&backend);
        int completed = 0;
        statistics.requestDashboard([&](bool ok) { QVERIFY(ok); ++completed; });
        QCOMPARE(completed, 1);
        QSignalSpy changed(&statistics, &mr::StatisticsController::dashboardChanged);
        backend.reject = true;
        statistics.requestDashboard([&](bool ok) { QVERIFY(!ok); ++completed; });
        QCOMPARE(completed, 2);
        QCOMPARE(changed.count(), 0);
        QCOMPARE(statistics.dashboard().value(QStringLiteral("completed_count")).toInt(), 18);
        QCOMPARE(statistics.trendBuckets().size(), 10);
    }

    void baselineFailureSignalOrderAndSuccessfulRefresh()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        QStringList order;
        connect(&statistics, &mr::StatisticsController::baselineFailed, this,
                [&] { order << QStringLiteral("baseline"); });
        connect(&statistics, &mr::StatisticsController::mutationFailed, this,
                [&] { order << QStringLiteral("mutation"); });
        connect(&statistics, &mr::StatisticsController::toastRequested, this,
                [&] { order << QStringLiteral("toast"); });
        statistics.updateAchievementBaseline(2000, -1, QStringLiteral("reason"));
        backend.last(QStringLiteral("UpdateAchievementBaseline")).reply->fail(
            QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("invalid"));
        QCOMPARE(order, (QStringList{QStringLiteral("baseline"), QStringLiteral("mutation"),
                                    QStringLiteral("toast")}));
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 0);
        statistics.updateAchievementBaseline(2000, 12, QStringLiteral("reason"));
        backend.last(QStringLiteral("UpdateAchievementBaseline")).reply->succeed({});
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 1);
    }

    void oldSelectionRepliesCannotOverwriteNewRecord()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        QCOMPARE(history.runs()->parent(), &history);
        history.selectRun(run(QStringLiteral("A")));
        auto oldRevisions = backend.last(QStringLiteral("GetRunRevisions")).reply;
        history.refreshRunEvents();
        auto oldEvents = backend.last(QStringLiteral("GetRunEvents")).reply;
        history.selectRun(run(QStringLiteral("B"), 4));
        history.refreshRunEvents();
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(revisions(4));
        backend.last(QStringLiteral("GetRunEvents")).reply->succeed(
            {{QStringLiteral("events"), QJsonArray{QJsonObject{{QStringLiteral("kind"), "B"}}}}});
        oldRevisions->succeed(revisions(99));
        oldEvents->succeed({{QStringLiteral("events"), QJsonArray{QJsonObject{{"kind", "A"}}}}});
        QCOMPARE(history.selectedRun().value(QStringLiteral("run_id")).toString(), QStringLiteral("B"));
        QCOMPARE(history.selectedRunRevisions().last().toMap().value(QStringLiteral("revision")).toInt(), 4);
        QCOMPARE(history.selectedRunEvents().first().toMap().value(QStringLiteral("kind")).toString(), QStringLiteral("B"));
        QVERIFY(history.selectedRunCanUndo());
        history.selectRun(run(QStringLiteral("B")));
        QVERIFY(!history.hasSelection());
        QCOMPARE(history.runEventsState(), QStringLiteral("idle"));
    }

    void filtersAndMutationWhitelistPreserveContract()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        const QVariantMap filter{{QStringLiteral("pending_review"), true}};
        history.setHistoryFilter(filter);
        QCOMPARE(history.historyFilter(), filter);
        QCOMPARE(backend.last(QStringLiteral("QueryRuns")).payload.value(QStringLiteral("filter")).toObject(),
                 QJsonObject::fromVariantMap(filter));
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        history.createManualRun({}, QStringLiteral(" "));
        QCOMPARE(failed.count(), 1);
        QCOMPARE(backend.count(QStringLiteral("CreateManualRun")), 0);
        history.correctSelectedRun({}, QStringLiteral("reason"));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 0);
        history.selectRun(run(QStringLiteral("A")));
        history.correctSelectedRun({{QStringLiteral("note"), "ok"},
                                    {QStringLiteral("duty_level"), 100}}, QStringLiteral("reason"));
        const auto changes = backend.last(QStringLiteral("CorrectRun")).payload.value(QStringLiteral("changes")).toObject();
        QCOMPARE(changes.size(), 1);
        QCOMPARE(changes.value(QStringLiteral("note")).toString(), QStringLiteral("ok"));
    }

    void aCorrectionIsRefusedOnceTheSelectionIsNoLongerTheRunTheDialogShowed()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(run(QStringLiteral("B")));
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        history.correctSelectedRun({{QStringLiteral("job_id"), 34}}, QStringLiteral("reason"),
                                   QStringLiteral("A"));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 0);
        QCOMPARE(failed.count(), 1);
        QCOMPARE(failed.first().first().toString(), QStringLiteral("ERR_SELECTION_CHANGED"));

        history.correctSelectedRun({{QStringLiteral("job_id"), 34}}, QStringLiteral("reason"),
                                   QStringLiteral("B"));
        QCOMPARE(backend.last(QStringLiteral("CorrectRun")).payload.value(QStringLiteral("run_id")).toString(),
                 QStringLiteral("B"));
    }

    void pendingReviewFallbackAndSingleConflictRetry()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.updatePendingReviewCount(1);
        backend.last(QStringLiteral("QueryRuns")).reply->succeed(
            {{QStringLiteral("items"), QJsonArray{QJsonObject::fromVariantMap(run(QStringLiteral("A"), 3))}}});
        history.resolveRunResult(QStringLiteral("A"), -1, QStringLiteral("COMPLETED"), QStringLiteral("reason"));
        auto correction = backend.last(QStringLiteral("CorrectRun"));
        QCOMPARE(correction.payload.value(QStringLiteral("expected_revision")).toInt(), 3);
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        QSignalSpy revised(&history, &mr::HistoryController::runRevisionChanged);
        correction.reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(revisions(5));
        QCOMPARE(revised.count(), 1);
        correction = backend.last(QStringLiteral("CorrectRun"));
        QCOMPARE(correction.payload.value(QStringLiteral("expected_revision")).toInt(), 5);
        correction.reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale again"));
        QCOMPARE(failed.count(), 1);
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 2);
        QCOMPARE(backend.count(QStringLiteral("GetRunRevisions")), 1);
        history.updatePendingReviewCount(0);
        QVERIFY(history.pendingReviewRun().isEmpty());
    }

    void synchronousMutationAndEventFailureAreObservedOnce()
    {
        ControlledBackend backend;
        backend.synchronous = true;
        backend.reject = true;
        mr::HistoryController history(&backend);
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        QSignalSpy toast(&history, &mr::HistoryController::toastRequested);
        history.selectRun(run(QStringLiteral("A")));
        history.correctSelectedRun({{QStringLiteral("note"), "ok"}}, QStringLiteral("reason"));
        QCOMPARE(failed.count(), 1);
        QCOMPARE(toast.count(), 1);
        history.refreshRunEvents();
        QCOMPARE(history.runEventsState(), QStringLiteral("error"));
        QCOMPARE(history.runEventsMessage(), QStringLiteral("refused"));
    }

    void destructionDisconnectsPendingRepliesAndBackendLossIsSafe()
    {
        ControlledBackend backend;
        int completed = 0;
        auto statistics = std::make_unique<mr::StatisticsController>(&backend);
        statistics->requestDashboard([&](bool) { ++completed; });
        auto pending = backend.last(QStringLiteral("GetDashboardStats")).reply;
        statistics.reset();
        pending->succeed(dashboard(9));
        QCOMPARE(completed, 0);

        auto history = std::make_unique<mr::HistoryController>(&backend);
        history->selectRun(run(QStringLiteral("A")));
        auto revisionReply = backend.last(QStringLiteral("GetRunRevisions")).reply;
        history->refreshRunEvents();
        auto eventsReply = backend.last(QStringLiteral("GetRunEvents")).reply;
        history.reset();
        revisionReply->succeed(revisions(2));
        eventsReply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("gone"));

        auto shortBackend = std::make_unique<ControlledBackend>();
        mr::HistoryController retainedHistory(shortBackend.get());
        mr::StatisticsController retainedStats(shortBackend.get());
        retainedHistory.selectRun(run(QStringLiteral("B")));
        retainedStats.refreshDashboard();
        shortBackend.reset();
        retainedHistory.resolveRunResult(QStringLiteral("B"), 2, QStringLiteral("COMPLETED"), QStringLiteral("reason"));
        retainedHistory.refreshRunEvents();
        retainedHistory.setHistoryFilter({{QStringLiteral("pending_review"), true}});
        retainedHistory.runs()->reload();
        retainedStats.refreshDashboard();
        retainedStats.dungeons()->reload();
        retainedStats.jobs()->reload();
        retainedStats.updateAchievementBaseline(2000, 1, QStringLiteral("reason"));
        QVERIFY(retainedStats.dashboard().isEmpty());
    }
};

QTEST_GUILESS_MAIN(WorkflowControllerTests)
#include "WorkflowControllerTests.moc"
