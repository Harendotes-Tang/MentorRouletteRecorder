#include "HistoryController.h"
#include "RunListModel.h"
#include "StatisticsController.h"
#include "IBackend.h"

#include "IpcBackend.h"

#include <QCoreApplication>
#include <QJsonArray>
#include <QSignalSpy>
#include <QTest>
#include <QUuid>
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
        /// The request_id the envelope would carry: the caller's, or a fresh
        /// one when it named none, as IpcBackend does.
        QString requestId;
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
        calls.append({type, payload, reply, QUuid::createUuid().toString(QUuid::WithoutBraces)});
        if (synchronous) {
            if (reject)
                reply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("refused"));
            else
                reply->succeed(answer);
        }
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload,
                                    const QString &requestId) override
    {
        auto *reply = request(type, payload);
        if (!requestId.isEmpty())
            calls.last().requestId = requestId;
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

/// A revision chain whose newest row changed the given fields.
QJsonObject revisionsChanging(int revision, const QStringList &fields)
{
    QJsonArray changes;
    for (const QString &field : fields)
        changes.append(QJsonObject{{QStringLiteral("field"), field}});
    return {{QStringLiteral("items"), QJsonArray{
        QJsonObject{{QStringLiteral("revision"), 1}},
        QJsonObject{{QStringLiteral("revision"), revision},
                    {QStringLiteral("changes"), changes}}}}};
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

/// A dashboard answer carrying the achievement settings and the Collector's progress.
QJsonObject achievement(int goal, int baseline, int progress)
{
    return {{QStringLiteral("goal_count"), goal},
            {QStringLiteral("baseline_completed_count"), baseline},
            {QStringLiteral("achievement_progress"), progress},
            {QStringLiteral("remaining"), qMax(0, goal - progress)}};
}

/// $defs/IpcResponsePayloads.UpdateAchievementBaseline: no achievement_progress.
QJsonObject baselineSaved(int goal, int baseline)
{
    return {{QStringLiteral("goal_count"), goal},
            {QStringLiteral("baseline_completed_count"), baseline},
            {QStringLiteral("baseline_effective_at"), QStringLiteral("2026-10-03T08:00:00.000Z")},
            {QStringLiteral("updated_at_utc"), QStringLiteral("2026-10-03T08:00:00.000Z")},
            {QStringLiteral("audit_event_id"), QStringLiteral("00000000-0000-4000-8000-000000000001")}};
}

/// What the Collector answers to one GetRunRevisions request for a chain of
/// \a total revisions: ascending, page 1 and 50 rows unless asked otherwise.
QJsonObject revisionPage(const QJsonObject &request, int total)
{
    const int page = request.value(QStringLiteral("page")).toInt(1);
    const int size = request.value(QStringLiteral("page_size")).toInt(50);
    QJsonArray items;
    for (int revision = (page - 1) * size + 1; revision <= qMin(page * size, total); ++revision)
        items.append(QJsonObject{{QStringLiteral("revision"), revision}});
    return {{QStringLiteral("items"), items},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page},
                                                      {QStringLiteral("page_size"), size},
                                                      {QStringLiteral("total"), total}}}};
}

QJsonObject statsPage(int page, int total, int firstId = 1)
{
    QJsonArray items;
    for (int i = (page - 1) * 200; i < qMin(page * 200, total); ++i)
        items.append(QJsonObject{{QStringLiteral("content_id"), firstId + i},
                                 {QStringLiteral("attempt_count"), 1}});
    return {{QStringLiteral("items"), items}, {QStringLiteral("distinct_count"), total},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page},
                {QStringLiteral("page_size"), 200}, {QStringLiteral("total"), total}}}};
}
}

class WorkflowControllerTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void lateTrendRepliesCannotReplaceTheSelectedSeries_data()
    {
        QTest::addColumn<QString>("nextMode");
        QTest::addColumn<bool>("oldFails");
        for (const auto &mode : {QStringLiteral("day"), QStringLiteral("week"), QStringLiteral("month")}) {
            QTest::newRow(qPrintable(mode + QStringLiteral("-success"))) << mode << false;
            QTest::newRow(qPrintable(mode + QStringLiteral("-failure"))) << mode << true;
        }
    }

    void lateTrendRepliesCannotReplaceTheSelectedSeries()
    {
        QFETCH(QString, nextMode);
        QFETCH(bool, oldFails);
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(dashboard(10));
        const auto dayBuckets = statistics.trendBuckets();
        statistics.setTrendMode(QStringLiteral("week"));
        const auto oldReply = backend.last(QStringLiteral("GetDashboardStats")).reply;

        // The player either returns to cached days, selects months, or requests
        // a fresher week snapshot before the previous query has answered.
        if (nextMode == QStringLiteral("week"))
            statistics.refreshTrend();
        else
            statistics.setTrendMode(nextMode);
        if (nextMode != QStringLiteral("day")) {
            backend.last(QStringLiteral("GetDashboardStats")).reply->succeed({
                {QStringLiteral("trend"), QJsonObject{
                    {QStringLiteral("granularity"), nextMode},
                    {QStringLiteral("buckets"), QJsonArray{QJsonObject{
                        {QStringLiteral("start_utc"), QStringLiteral("2026-09-01T00:00:00Z")},
                        {QStringLiteral("completed_count"), 7}}}}}}});
        }
        const auto expected = statistics.trendBuckets();
        QVERIFY(!expected.isEmpty());
        if (nextMode == QStringLiteral("day"))
            QCOMPARE(expected, dayBuckets);
        else
            QCOMPARE(expected.first().toMap().value(QStringLiteral("count")).toInt(), 7);

        if (oldFails)
            oldReply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("late failure"));
        else
            oldReply->succeed({{QStringLiteral("trend"), QJsonObject{
                {QStringLiteral("granularity"), QStringLiteral("week")},
                {QStringLiteral("buckets"), QJsonArray{QJsonObject{
                    {QStringLiteral("start_utc"), QStringLiteral("2026-09-07T00:00:00Z")},
                    {QStringLiteral("completed_count"), 999}}}}}}});
        QCOMPARE(statistics.trendMode(), nextMode);
        QCOMPARE(statistics.trendBuckets(), expected);
    }

    void statisticsLoadsAllPagesBeforePublishingCounts()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        auto *model = statistics.dungeons();
        QSignalSpy published(model, &mr::StatsRowsModel::countChanged);
        model->reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 401));
        QVERIFY(model->isLoading());
        QCOMPARE(published.count(), 0);
        QTRY_COMPARE(backend.count(QStringLiteral("GetDungeonStats")), 2);
        QCOMPARE(backend.last(QStringLiteral("GetDungeonStats")).payload.value("page").toInt(), 2);
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(2, 401));
        QVERIFY(model->isLoading());
        QCOMPARE(published.count(), 0);
        QTRY_COMPARE(backend.count(QStringLiteral("GetDungeonStats")), 3);
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(3, 401));
        QVERIFY(!model->isLoading());
        QCOMPARE(published.count(), 1);
        QCOMPARE(model->distinctCount(), 401);
        QCOMPARE(model->totalAttemptCount(), 401);
        QCOMPARE(model->topRows(0).size(), 401);
        QCOMPARE(model->topRows(10).size(), 10);
        QCOMPARE(statistics.dutyOptions().size(), 401);
        QCOMPARE(model->rowAt(400).value("content_id").toInt(), 401);
    }

    void supersededStatisticsPagesAndFailuresCannotReplaceNewFilter()
    {
        ControlledBackend backend;
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        QSignalSpy failures(&model, &mr::StatsRowsModel::loadFailed);
        model.reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 201));
        QTRY_COMPARE(backend.count(QStringLiteral("GetDungeonStats")), 2);
        auto oldPage = backend.last(QStringLiteral("GetDungeonStats")).reply;
        model.setFilter({{QStringLiteral("content_id"), QVariantList{999}}});
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 1, 999));
        oldPage->succeed(statsPage(2, 201));
        QCOMPARE(model.rowCount(), 1);
        QCOMPARE(model.rowAt(0).value("content_id").toInt(), 999);
        model.reload();
        auto oldFailure = backend.last(QStringLiteral("GetDungeonStats")).reply;
        model.reload();
        oldFailure->fail(QStringLiteral("ERR_TEST"), QStringLiteral("old failure"));
        QVERIFY(model.isLoading());
        QCOMPARE(failures.count(), 0);
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 1, 998));
        QCOMPARE(model.rowAt(0).value("content_id").toInt(), 998);
    }

    void failedOrChangedStatisticsPageNeverPublishesPartialTotals_data()
    {
        QTest::addColumn<bool>("changed");
        QTest::newRow("failure") << false;
        QTest::newRow("changed-total") << true;
    }

    void failedOrChangedStatisticsPageNeverPublishesPartialTotals()
    {
        QFETCH(bool, changed);
        ControlledBackend backend;
        mr::DungeonStatsModel model;
        model.setBackend(&backend);
        QSignalSpy failures(&model, &mr::StatsRowsModel::loadFailed);
        model.reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 201));
        QTRY_COMPARE(backend.count(QStringLiteral("GetDungeonStats")), 2);
        if (changed)
            backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(2, 202));
        else
            backend.last(QStringLiteral("GetDungeonStats")).reply->fail("ERR_TEST", "failed");
        QVERIFY(!model.isLoading());
        QCOMPARE(model.rowCount(), 0);
        QCOMPARE(model.totalAttemptCount(), 0);
        QCOMPARE(failures.count(), 1);
    }

    // 审查 OD-3 / OI-1：读取失败要有自己的状态，页面据此说「读取失败」，而不是把
    // 清空的模型当成真实的「0 条 / 0 个副本」。下一次成功读取清除它。
    void aFailedLoadIsAStateOfItsOwn()
    {
        ControlledBackend backend;
        mr::RunListModel runs;
        mr::DungeonStatsModel dungeons;
        runs.setBackend(&backend);
        dungeons.setBackend(&backend);
        QVERIFY(runs.loadError().isEmpty());
        QVERIFY(dungeons.loadError().isEmpty());

        runs.reload();
        backend.last(QStringLiteral("QueryRuns")).reply->fail(
            QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("text 超过 200 个字符的上限。"));
        QCOMPARE(runs.loadError(), QStringLiteral("text 超过 200 个字符的上限。"));
        dungeons.reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->fail(QStringLiteral("ERR_INTERNAL"), QString());
        QVERIFY(!dungeons.loadError().isEmpty());

        runs.reload();
        backend.last(QStringLiteral("QueryRuns")).reply->succeed(
            {{"items", QJsonArray{}}, {"page_info", QJsonObject{{"page", 1}, {"total", 0}}}});
        QVERIFY(runs.loadError().isEmpty());
        dungeons.reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 1));
        QVERIFY(dungeons.loadError().isEmpty());
    }

    // 审查 S2-2：副本统计读取失败不得清空历史页的副本选项（副本 chip 会变成「指定副本」）。
    void aFailedDungeonReadKeepsTheDutyOptions()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        statistics.dungeons()->reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->succeed(statsPage(1, 3));
        QCOMPARE(statistics.dutyOptions().size(), 3);
        statistics.dungeons()->reload();
        backend.last(QStringLiteral("GetDungeonStats")).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                                                     QStringLiteral("gone"));
        QCOMPARE(statistics.dutyOptions().size(), 3);
    }

    // 审查 OI-1：读取失败不是「待复核记录没了」，也不是「趋势归零」。
    void aFailedReadKeepsThePendingReviewRunAndTheTrend()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.refreshPendingReviewRun();
        backend.last(QStringLiteral("QueryRuns")).reply->succeed(
            {{QStringLiteral("items"), QJsonArray{QJsonObject::fromVariantMap(run(QStringLiteral("A"), 3))}}});
        QCOMPARE(history.pendingReviewRun().value(QStringLiteral("run_id")).toString(), QStringLiteral("A"));
        history.refreshPendingReviewRun();
        backend.last(QStringLiteral("QueryRuns")).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                                              QStringLiteral("timeout"));
        QCOMPARE(history.pendingReviewRun().value(QStringLiteral("run_id")).toString(), QStringLiteral("A"));
        // An answer that there is nothing left still empties it.
        history.refreshPendingReviewRun();
        backend.last(QStringLiteral("QueryRuns")).reply->succeed({{QStringLiteral("items"), QJsonArray{}}});
        QVERIFY(history.pendingReviewRun().isEmpty());

        mr::StatisticsController statistics(&backend);
        statistics.setTrendMode(QStringLiteral("week"));
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed({{QStringLiteral("trend"), QJsonObject{
            {QStringLiteral("granularity"), QStringLiteral("week")},
            {QStringLiteral("buckets"), QJsonArray{QJsonObject{
                {QStringLiteral("start_utc"), QStringLiteral("2026-09-07T00:00:00Z")},
                {QStringLiteral("completed_count"), 4}}}}}}});
        QCOMPARE(statistics.trendBuckets().size(), 1);
        statistics.refreshTrend();
        backend.last(QStringLiteral("GetDashboardStats")).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                                                       QStringLiteral("timeout"));
        QCOMPARE(statistics.trendBuckets().size(), 1);
        QCOMPARE(statistics.trendBuckets().first().toMap().value(QStringLiteral("count")).toInt(), 4);
    }

    // 审查 OI-5：修订链按页读取（默认第 1 页、50 条、升序）。超过一页时最新的
    // 修订必须读到，否则修正历史缺最新几条，「撤销」永远不出现。
    void theRevisionListReachesTheNewestOfALongChain()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(run(QStringLiteral("A"), 260));
        int answered = 0;
        for (int index = 0; index < backend.calls.size() && answered < 10; ++index) {
            const auto call = backend.calls.at(index);
            if (call.type != QLatin1String("GetRunRevisions"))
                continue;
            call.reply->succeed(revisionPage(call.payload, 260));
            ++answered;
        }
        QCOMPARE(history.selectedRunRevisions().size(), 260);
        QCOMPARE(history.selectedRunRevisions().last().toMap().value(QStringLiteral("revision")).toInt(), 260);
        QVERIFY(history.selectedRunCanUndo());
        for (const auto &call : backend.calls) {
            if (call.type == QLatin1String("GetRunRevisions"))
                QVERIFY(call.payload.value(QStringLiteral("page_size")).toInt() <= 200);
        }
    }

    // 审查 S2-6：实时事件把选中记录推进到新修订时，修订列表也要跟着重读，否则
    // 修正历史停在旧的一条，「撤销」按钮消失直到重新选中。
    void aLiveRevisionReloadsTheRevisionList()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(run(QStringLiteral("A"), 2));
        auto call = backend.last(QStringLiteral("GetRunRevisions"));
        call.reply->succeed(revisionPage(call.payload, 2));
        QVERIFY(history.selectedRunCanUndo());
        const int before = backend.count(QStringLiteral("GetRunRevisions"));

        history.adoptRunRevisionFromEvent({{"run_id", "A"}, {"revision", 3}, {"result", "COMPLETED"}});
        QCOMPARE(backend.count(QStringLiteral("GetRunRevisions")), before + 1);
        call = backend.last(QStringLiteral("GetRunRevisions"));
        call.reply->succeed(revisionPage(call.payload, 3));
        QCOMPARE(history.selectedRunRevisions().size(), 3);
        QVERIFY(history.selectedRunCanUndo());

        // The same revision again changes nothing and reads nothing.
        history.adoptRunRevisionFromEvent({{"run_id", "A"}, {"revision", 3}, {"result", "COMPLETED"}});
        QCOMPARE(backend.count(QStringLiteral("GetRunRevisions")), before + 1);
    }

    void anOpenFormKeepsItsRevisionWhileTheSelectedRecordRefreshes()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        auto original = run(QStringLiteral("A"), 1);
        original.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
        history.selectRun(original);
        history.adoptRunRevisionFromEvent({{"run_id", "A"}, {"revision", 2}, {"result", "COMPLETED"}});
        QCOMPARE(history.selectedRun().value("result").toString(), QStringLiteral("COMPLETED"));
        history.correctSelectedRun({{"result", "LEFT_OR_ABANDONED"}}, "reason", "A", 1);
        auto correction = backend.last(QStringLiteral("CorrectRun"));
        QCOMPARE(correction.payload.value("expected_revision").toInt(), 1);
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        correction.reply->fail("ERR_REVISION_CONFLICT", "stale");
        QCOMPARE(failed.count(), 1);
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 1);
        // An older event must not regress the detail panel either.
        history.adoptRunRevisionFromEvent(QJsonObject::fromVariantMap(original));
        QCOMPARE(history.selectedRun().value("revision").toInt(), 2);
        QCOMPARE(history.selectedRun().value("result").toString(), QStringLiteral("COMPLETED"));
        history.clearSelection();
        history.correctSelectedRun({{"note", "draft"}}, "reason", "A", 1);
        QCOMPARE(failed.count(), 2);
        QCOMPARE(failed.last().first().toString(), QStringLiteral("ERR_SELECTION_CHANGED"));
    }

    void mutationRepliesLeaveANewerSelectionAlone_data()
    {
        QTest::addColumn<bool>("deleting");
        QTest::newRow("correction") << false;
        QTest::newRow("deletion") << true;
    }

    void mutationRepliesLeaveANewerSelectionAlone()
    {
        QFETCH(bool, deleting);
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(run(QStringLiteral("A")));
        if (deleting)
            history.softDeleteSelectedRun("reason");
        else
            history.correctSelectedRun({{"note", "draft"}}, "reason", "A", 2);
        auto reply = backend.last(deleting ? "SoftDeleteRun" : "CorrectRun").reply;
        QVERIFY(reply);
        history.selectRun(run(QStringLiteral("B")));
        reply->succeed({{"run_id", "A"}, {"revision", 3},
                        {"run", QJsonObject::fromVariantMap(run(QStringLiteral("A"), 3))}});
        QCOMPARE(history.selectedRun().value("run_id").toString(), QStringLiteral("B"));
    }

    void obsoleteHistoryQueryCannotMoveThePageOrClearTheNewRows()
    {
        ControlledBackend backend;
        mr::RunListModel model;
        model.setBackend(&backend);
        model.reload();
        auto oldReply = backend.last(QStringLiteral("QueryRuns")).reply;
        model.setFilter({{"pending_review", true}});
        const QJsonObject page{{"items", QJsonArray{QJsonObject{{"run_id", "new"}}}},
                               {"page_info", QJsonObject{{"page", 1}, {"total", 1}}}};
        backend.last(QStringLiteral("QueryRuns")).reply->succeed(page);
        oldReply->succeed({{"items", QJsonArray{}}, {"page_info", QJsonObject{{"page", 9}, {"total", 90}}}});
        QCOMPARE(model.page(), 1);
        QCOMPARE(model.total(), 1);
        QCOMPARE(model.runAt(0).value("run_id").toString(), QStringLiteral("new"));
        model.reload();
        oldReply = backend.last(QStringLiteral("QueryRuns")).reply;
        model.reload();
        QSignalSpy failed(&model, &mr::RunListModel::loadFailed);
        oldReply->fail("ERR_TEST", "old failure");
        QVERIFY(model.isLoading());
        QCOMPARE(failed.count(), 0);
        backend.last(QStringLiteral("QueryRuns")).reply->succeed(page);
        QCOMPARE(model.rowCount(), 1);
    }

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
        // A save is sent only once the stored settings have been read (CS7-D3).
        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(achievement(2000, 0, 0));
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
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 1);
        statistics.updateAchievementBaseline(2000, 12, QStringLiteral("reason"));
        backend.last(QStringLiteral("UpdateAchievementBaseline")).reply->succeed({});
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 2);
    }

    // CS7-D1：保存成功后的提示从 UpdateAchievementBaseline 的回应里读进度，可那份回应
    // 不带 achievement_progress（contracts/ipc-v1.schema.json），于是总是「进度 0」。
    // 进度取自保存之后重读的统计；重读失败时提示里不给进度，而不是给一个错的。
    void theSaveToastTakesItsProgressFromTheReReadDashboard()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        QStringList toasts;
        connect(&statistics, &mr::StatisticsController::toastRequested, this,
                [&](const QString &message) { toasts << message; });
        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(achievement(2000, 100, 104));

        statistics.updateAchievementBaseline(2000, 120, QStringLiteral("reason"));
        backend.last(QStringLiteral("UpdateAchievementBaseline")).reply->succeed(baselineSaved(2000, 120));
        // The reply has no progress to tell; the re-read has.
        QVERIFY2(toasts.isEmpty(), qPrintable(toasts.join(QLatin1Char('\n'))));
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 2);
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(achievement(2000, 120, 131));
        QCOMPARE(toasts, QStringList{QString::fromUtf8("基数已设为 120 · 目标 2000 · 进度 131 · 已重算")});

        // The re-read fails: the save stands, and no figure is made up for it.
        toasts.clear();
        statistics.updateAchievementBaseline(1800, 120, QStringLiteral("reason"));
        backend.last(QStringLiteral("UpdateAchievementBaseline")).reply->succeed(baselineSaved(1800, 120));
        QCOMPARE(backend.count(QStringLiteral("GetDashboardStats")), 3);
        backend.last(QStringLiteral("GetDashboardStats")).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                                                      QStringLiteral("gone"));
        QCOMPARE(toasts, QStringList{QString::fromUtf8("基数已设为 120 · 目标 1800 · 已保存")});
    }

    // CS7-D3：第一份统计读回之前，目标与基数只是默认值；这时的保存会把基数写成 0。
    // 读回之前不发送；连接断开后，在新连接里重新读回之前同样不发送，断开前发出、断开
    // 之后才回来的读取不算数。
    void aBaselineSaveWaitsForTheStoredSettingsOfThisConnection()
    {
        ControlledBackend backend;
        mr::StatisticsController statistics(&backend);
        QSignalSpy refused(&statistics, &mr::StatisticsController::baselineFailed);
        QSignalSpy loaded(&statistics, &mr::StatisticsController::achievementSettingsLoadedChanged);
        QVERIFY(!statistics.achievementSettingsLoaded());
        statistics.updateAchievementBaseline(1500, 0, QStringLiteral("reason"));
        QCOMPARE(backend.count(QStringLiteral("UpdateAchievementBaseline")), 0);
        QCOMPARE(refused.count(), 1);

        // A failed read tells nothing about what is stored.
        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->fail(QStringLiteral("ERR_INTERNAL"),
                                                                      QStringLiteral("gone"));
        QVERIFY(!statistics.achievementSettingsLoaded());

        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(achievement(1000, 640, 650));
        QVERIFY(statistics.achievementSettingsLoaded());
        QCOMPARE(loaded.count(), 1);
        statistics.updateAchievementBaseline(1500, 640, QStringLiteral("reason"));
        QCOMPARE(backend.count(QStringLiteral("UpdateAchievementBaseline")), 1);

        // The connection drops while a read is out; its late answer is not this connection's.
        statistics.refreshDashboard();
        const QPointer<mr::BackendReply> stale = backend.last(QStringLiteral("GetDashboardStats")).reply;
        statistics.forgetAchievementSettings();
        QVERIFY(!statistics.achievementSettingsLoaded());
        QCOMPARE(loaded.count(), 2);
        stale->succeed(achievement(1000, 640, 650));
        QVERIFY(!statistics.achievementSettingsLoaded());
        statistics.updateAchievementBaseline(1500, 640, QStringLiteral("reason"));
        QCOMPARE(backend.count(QStringLiteral("UpdateAchievementBaseline")), 1);
        QCOMPARE(refused.count(), 2);

        statistics.refreshDashboard();
        backend.last(QStringLiteral("GetDashboardStats")).reply->succeed(achievement(1000, 640, 650));
        QVERIFY(statistics.achievementSettingsLoaded());
        QCOMPARE(loaded.count(), 3);
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
        const QVariantMap filter{{QStringLiteral("pending_review"), true},
                                 {QStringLiteral("date_field"), QStringLiteral("history_date")}};
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

    void jobSupplementRetriesPastOutcomeChangesButDoesNotOverwriteAnotherJob()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        history.supplementRunJob(QStringLiteral("A"), 3, 19, QStringLiteral("补录职业"));
        auto correction = backend.last(QStringLiteral("CorrectRun"));
        QCOMPARE(correction.payload.value(QStringLiteral("changes")).toObject(),
                 QJsonObject({{QStringLiteral("job_id"), 19}}));
        correction.reply->fail(QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(4, {QStringLiteral("result"), QStringLiteral("pending_review")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 2);
        correction = backend.last(QStringLiteral("CorrectRun"));
        QCOMPARE(correction.payload.value(QStringLiteral("expected_revision")).toInt(), 4);
        QCOMPARE(correction.payload.value(QStringLiteral("changes")).toObject(),
                 QJsonObject({{QStringLiteral("job_id"), 19}}));

        history.supplementRunJob(QStringLiteral("A"), 4, 19, QStringLiteral("补录职业"));
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(5, {QStringLiteral("job_id")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 3);
        QCOMPARE(failed.count(), 1);
    }

    /// The stale dialog's answer must not overwrite a result someone else has
    /// already recorded: the retry after a conflict only proceeds when the
    /// intervening revisions left result, pending_review and job_id alone.
    void conflictRetryStopsWhenAnotherPlaceAlreadyAnsweredTheResult()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.resolveRunResult(QStringLiteral("A"), 3, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"));
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        QSignalSpy revised(&history, &mr::HistoryController::runRevisionChanged);
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(4, {QStringLiteral("result"), QStringLiteral("pending_review")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 1);
        QCOMPARE(failed.count(), 1);
        QCOMPARE(failed.first().first().toString(), QStringLiteral("ERR_REVISION_CONFLICT"));
        QCOMPARE(revised.count(), 1);
        QCOMPARE(revised.first().at(1).toInt(), 4);

        // A note-only correction in between is no reason to drop the answer.
        history.resolveRunResult(QStringLiteral("A"), 4, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"));
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(5, {QStringLiteral("note")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 3);
        QCOMPARE(backend.last(QStringLiteral("CorrectRun")).payload
                     .value(QStringLiteral("expected_revision")).toInt(), 5);
        QCOMPARE(failed.count(), 1);

        // job_id only matters when this answer carries a job of its own.
        history.resolveRunResult(QStringLiteral("A"), 5, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"));
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(6, {QStringLiteral("job_id")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 5);
        history.resolveRunResult(QStringLiteral("A"), 6, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"), 19);
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            revisionsChanging(7, {QStringLiteral("job_id")}));
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 6);
        QCOMPARE(failed.count(), 2);
    }

    /// GetRunRevisions pages oldest-first, 50 rows by default. The retry must ask
    /// for the page holding the revisions after the stale one, take the current
    /// revision from page_info.total, and give up when the unseen revisions do
    /// not fit on that page.
    void conflictRetryReadsThePageAfterTheStaleRevision()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        QSignalSpy failed(&history, &mr::HistoryController::mutationFailed);
        QSignalSpy revised(&history, &mr::HistoryController::runRevisionChanged);

        history.resolveRunResult(QStringLiteral("A"), 250, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"));
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        auto request = backend.last(QStringLiteral("GetRunRevisions")).payload;
        QCOMPARE(request.value(QStringLiteral("page")).toInt(), 2);
        QCOMPARE(request.value(QStringLiteral("page_size")).toInt(), 200);
        QJsonObject page{{QStringLiteral("items"), QJsonArray{
                              QJsonObject{{QStringLiteral("revision"), 201}},
                              QJsonObject{{QStringLiteral("revision"), 251},
                                          {QStringLiteral("changes"), QJsonArray{
                                               QJsonObject{{QStringLiteral("field"), QStringLiteral("note")}}}}}}},
                         {QStringLiteral("page_info"),
                          QJsonObject{{QStringLiteral("page"), 2},
                                      {QStringLiteral("page_size"), 200},
                                      {QStringLiteral("total"), 251}}}};
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(page);
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 2);
        QCOMPARE(backend.last(QStringLiteral("CorrectRun")).payload
                     .value(QStringLiteral("expected_revision")).toInt(), 251);
        QCOMPARE(failed.count(), 0);

        // The chain grew past the requested page: the current revision is only
        // known from total, what changed is not, so nothing is retried.
        history.resolveRunResult(QStringLiteral("A"), 3, QStringLiteral("COMPLETED"),
                                 QStringLiteral("reason"));
        backend.last(QStringLiteral("CorrectRun")).reply->fail(
            QStringLiteral("ERR_REVISION_CONFLICT"), QStringLiteral("stale"));
        QCOMPARE(backend.last(QStringLiteral("GetRunRevisions")).payload
                     .value(QStringLiteral("page")).toInt(), 1);
        backend.last(QStringLiteral("GetRunRevisions")).reply->succeed(
            {{QStringLiteral("items"), QJsonArray{QJsonObject{{QStringLiteral("revision"), 200}}}},
             {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), 1},
                                                       {QStringLiteral("page_size"), 200},
                                                       {QStringLiteral("total"), 205}}}});
        QCOMPARE(backend.count(QStringLiteral("CorrectRun")), 3);
        QCOMPARE(failed.count(), 1);
        QCOMPARE(revised.last().at(1).toInt(), 205);
    }

    /// A create or a correction the client stopped waiting for may still have
    /// been applied - the Collector answers one connection in order, and a
    /// backup ahead of it can outlast the 8 s deadline. Pressing 提交 again must
    /// resend it under the same request_id, so the Collector's idempotency
    /// answers with the first result instead of writing a second record
    /// (review OH-2).
    void aMutationTheClientGaveUpOnIsResentUnderItsOwnRequestId()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        const QVariantMap fields{{QStringLiteral("content_id"), 1036},
                                 {QStringLiteral("result"), QStringLiteral("COMPLETED")}};
        const QString reason = QString::fromUtf8("补录遗漏的导随记录");

        history.createManualRun(fields, reason);
        const auto first = backend.last(QStringLiteral("CreateManualRun"));
        first.reply->fail(QStringLiteral("ERR_INTERNAL"),
                          QString::fromUtf8("Collector 未在超时时间内响应。"));
        history.createManualRun(fields, reason);
        const auto resent = backend.last(QStringLiteral("CreateManualRun"));
        QCOMPARE(resent.requestId, first.requestId);

        // Answered: the next record is a request of its own.
        resent.reply->succeed({{QStringLiteral("run_id"), QStringLiteral("run-1")},
                               {QStringLiteral("revision"), 1}});
        history.createManualRun(fields, reason);
        const auto next = backend.last(QStringLiteral("CreateManualRun"));
        QVERIFY(next.requestId != first.requestId);

        // Different content never borrows an unanswered id: the Collector
        // would refuse it as ERR_IDEMPOTENCY_CONFLICT.
        next.reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
        QVariantMap other = fields;
        other.insert(QStringLiteral("content_id"), 1037);
        history.createManualRun(other, reason);
        QVERIFY(backend.last(QStringLiteral("CreateManualRun")).requestId != next.requestId);

        // The same holds for a correction of the record a form is showing.
        history.selectRun(run(QStringLiteral("A"), 2));
        const QVariantMap changes{{QStringLiteral("note"), QStringLiteral("x")}};
        history.correctSelectedRun(changes, reason, QStringLiteral("A"), 2);
        const auto correction = backend.last(QStringLiteral("CorrectRun"));
        correction.reply->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
        history.correctSelectedRun(changes, reason, QStringLiteral("A"), 2);
        QCOMPARE(backend.last(QStringLiteral("CorrectRun")).requestId, correction.requestId);
    }

    /// The id a caller names is the one on the wire.
    void theIpcBackendSendsTheRequestIdItIsGiven()
    {
        mr::IpcBackend backend(nullptr,
                               QStringLiteral("\\\\.\\pipe\\MentorRecorderTest.no-such-pipe.%1")
                                   .arg(QCoreApplication::applicationPid()));
        const QString id = QStringLiteral("6f8f4c7e-2a71-4f0e-9b6e-0f2a5f9f9f11");
        QCOMPARE(backend.correctRun(QStringLiteral("A"), 2, {}, QStringLiteral("r"), id)->requestId(),
                 id);
        QCOMPARE(backend.createManualRun({}, QStringLiteral("r"), id)->requestId(), id);
        // Without one, every request gets a fresh id of its own.
        const QString a = backend.getStatus()->requestId();
        const QString b = backend.getStatus()->requestId();
        QVERIFY(!a.isEmpty() && a != b);
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

    // DT-10：采集服务不提供逐事件摘要时，详情页「事件」一栏的说明此前句中带着机器码
    // （ERR_UNKNOWN_MESSAGE）。普通用户从不看到机器码，这一栏也不知道维护者工具是否打开。
    void anUnsupportedEventSummaryIsExplainedWithoutACode()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.selectRun(run(QStringLiteral("A")));
        history.refreshRunEvents();
        auto *reply = backend.last(QStringLiteral("GetRunEvents")).reply.data();
        QVERIFY(reply);
        reply->fail(QStringLiteral("ERR_UNKNOWN_MESSAGE"), QStringLiteral("unknown message_type"));
        QCOMPARE(history.runEventsState(), QStringLiteral("unsupported"));
        QCOMPARE(history.runEventsMessage(), QString::fromUtf8("当前 Collector 不提供逐事件摘要。"));
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
