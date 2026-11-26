#include "DesktopTestRunner.h"
#include "DisplayTimeZone.h"
#include "ExportController.h"
#include "Formatters.h"
#include "HistoryBatchTestSupport.h"
#include "HistoryController.h"
#include "IpcFraming.h"
#include "MockBackend.h"
#include "TestCollectorGuard.h"
#include "TestNamedPipe.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTimeZone>

#include <algorithm>
#include <utility>

namespace {

constexpr int kLargeCount = 2003;

QString selectionRunId(int index)
{
    return QStringLiteral("10000000-0000-4000-8000-%1").arg(index, 12, 16, QLatin1Char('0'));
}

QJsonArray selectionRuns()
{
    const QDateTime start(QDate(2026, 9, 1), QTime(12, 0), QTimeZone::UTC);
    QJsonArray rows;
    for (int index = 0; index < kLargeCount; ++index) {
        const auto entered = start.addSecs(index * 1000);
        QJsonObject run = QJsonObject::fromVariantMap(row(selectionRunId(index), 1));
        run.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
        run.insert(QStringLiteral("region"), QStringLiteral("CN"));
        run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        run.insert(QStringLiteral("matched_at_utc"), entered.addSecs(-10).toString(Qt::ISODateWithMs));
        run.insert(QStringLiteral("entered_at_utc"), entered.toString(Qt::ISODateWithMs));
        run.insert(QStringLiteral("ended_at_utc"), entered.addSecs(120).toString(Qt::ISODateWithMs));
        run.insert(QStringLiteral("contributes_to_goal"), true);
        rows.append(run);
    }
    return rows;
}

/// Every run of selectionRuns() as a batch target at its first revision.
QJsonArray selectionTargets()
{
    QJsonArray targets;
    for (int index = 0; index < kLargeCount; ++index)
        targets.append(QJsonObject{{QStringLiteral("run_id"), selectionRunId(index)},
                                   {QStringLiteral("expected_revision"), 1}});
    return targets;
}

class SelectionMockBackend final : public mr::MockBackend
{
public:
    struct Call {
        QString type;
        QJsonObject payload;
        QString id;
    };

    QList<Call> chunks;
    QList<Call> actions;
    QList<QVariantMap> exports;
    QList<QVariantMap> batches;
    bool dropBatchReply = false;

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        if (type == QLatin1String("StageHistorySelection"))
            chunks.append({type, payload, {}});
        auto *reply = MockBackend::request(type, payload);
        if (type == QLatin1String("ExportCsv") || type == QLatin1String("ExportJson")) {
            reply->whenDone(this, [this](bool ok, const QVariantMap &answer, const QString &, const QString &) {
                if (ok)
                    exports.append(answer);
            });
        }
        return reply;
    }

    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload,
                                    const QString &requestId) override
    {
        actions.append({type, payload, requestId});
        auto *reply = MockBackend::requestWithId(type, payload, requestId);
        if (type != QLatin1String("BatchMutateRuns"))
            return reply;
        const bool drop = std::exchange(dropBatchReply, false);
        mr::BackendReply *reported = drop ? new mr::BackendReply(requestId, type, this) : reply;
        reply->whenDone(this, [this, drop, reported](bool ok, const QVariantMap &answer,
                                                    const QString &code, const QString &message) {
            if (ok)
                batches.append(answer);
            if (drop) {
                if (ok)
                    reported->failTransport(QStringLiteral("ERR_INTERNAL"), QStringLiteral("reply lost"));
                else
                    reported->fail(code, message);
            }
        });
        return reported;
    }
};

class HistorySelectionTests final : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void anIncompletePageKeepsThePreviousSelection()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 4), true);
        QSignalSpy feedback(&history, &mr::HistoryController::toastRequested);
        history.checkAllMatchingRuns();

        const QString message = QStringLiteral("QueryHistorySelection");
        const QString id = QStringLiteral("ce7e2992-6de5-4efc-8c15-60bdda7bcb84");
        QJsonArray initial;
        for (int index = 0; index < 200; ++index)
            initial.append(QJsonObject::fromVariantMap(row(selectionRunId(index), 1)));
        const auto reply = [&](int page, const QJsonArray &items, int total) {
            backend.last(message).reply->succeed({{QStringLiteral("selection_id"), id},
                {QStringLiteral("items"), items}, {QStringLiteral("page_info"),
                QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("page_size"), 200},
                            {QStringLiteral("total"), total}}}});
        };

        reply(1, initial, 201);
        QTRY_COMPARE(backend.last(message).payload.value(QStringLiteral("page")).toInt(), 2);
        QCOMPARE(backend.last(message).payload.value(QStringLiteral("selection_id")).toString(), id);
        QVERIFY(history.checkAllLoading());
        QCOMPARE(history.checkedRunIds(), QStringList{firstId()});
        reply(2, {}, 201);
        QVERIFY(!history.checkAllLoading());
        QCOMPARE(history.checkedRunIds(), QStringList{firstId()});
        QCOMPARE(feedback.count(), 1);
        QVERIFY(backend.last(message).payload.value(QStringLiteral("release")).toBool());
    }

    void cleanup()
    {
        mr::DisplayTimeZone::set(QString());
    }

    void aZoneChangeRestartsACheckAllOnTheRebasedDays()
    {
        mr::DisplayTimeZone::set(QStringLiteral("Asia/Tokyo"));
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setHistoryFilter(
            {{QStringLiteral("from_utc"), mr::Formatters::dayBoundaryUtc(QStringLiteral("2026-09-15"), false)},
             {QStringLiteral("to_utc"), mr::Formatters::dayBoundaryUtc(QStringLiteral("2026-09-15"), true)}});
        QSignalSpy feedback(&history, &mr::HistoryController::toastRequested);
        const QString message = QStringLiteral("QueryHistorySelection");
        const auto sentCount = [&backend, &message] {
            return std::ranges::count_if(backend.calls, [&message](const ControlledBackend::Call &call) {
                return call.type == message;
            });
        };
        history.checkAllMatchingRuns();
        QCOMPARE(sentCount(), 1);
        const auto first = backend.last(message);
        QCOMPARE(first.payload.value(QStringLiteral("filter")).toObject().value(QStringLiteral("from_utc")).toString(),
                 QStringLiteral("2026-09-14T15:00:00.000Z"));

        // 显示时区 changes while the first page is still being read: the frozen
        // snapshot is dropped and 勾选全部 starts again on the same day's new bounds,
        // rather than stopping without a word (re-review RD-1).
        mr::DisplayTimeZone::set(QStringLiteral("America/New_York"));
        QVERIFY(history.checkAllLoading());
        QCOMPARE(sentCount(), 2);
        const auto restarted = backend.last(message);
        QCOMPARE(restarted.payload.value(QStringLiteral("page")).toInt(), 1);
        QVERIFY(!restarted.payload.contains(QStringLiteral("selection_id")));
        const QJsonObject filter = restarted.payload.value(QStringLiteral("filter")).toObject();
        QCOMPARE(filter.value(QStringLiteral("from_utc")).toString(), QStringLiteral("2026-09-15T04:00:00.000Z"));
        QCOMPARE(filter.value(QStringLiteral("to_utc")).toString(), QStringLiteral("2026-09-16T03:59:59.999Z"));
        QCOMPARE(feedback.count(), 0);
    }

    void anOlderCollectorKeepsTheSelectionAndPromptsForAnUpdate()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 4), true);
        QSignalSpy feedback(&history, &mr::HistoryController::toastRequested);

        history.checkAllMatchingRuns();
        // What the Collector really answers for a type it does not know
        // (MessageDispatcher: ERR_BAD_REQUEST, ipc.unknown_message_type; audit 2026-11-26, I2-4).
        backend.last(QStringLiteral("QueryHistorySelection")).reply->fail(
            QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("收到本版本不支持的消息类型，请确认两端版本一致。"), {},
            QJsonObject{{QStringLiteral("key"), QStringLiteral("ipc.unknown_message_type")}});

        QVERIFY(!history.checkAllLoading());
        QCOMPARE(history.checkedRunIds(), QStringList{firstId()});
        QCOMPARE(feedback.count(), 1);
        const auto notice = feedback.takeFirst().first().toString();
        QVERIFY(notice.contains(QStringLiteral("已保留现有勾选")));
        QVERIFY(notice.contains(QStringLiteral("请更新软件后重试")));
        QVERIFY(!backend.last(QStringLiteral("QueryRuns")).reply);
    }

    void aConnectionChangeStopsBeforeSendingTheStagedAction()
    {
        ControlledBackend backend;
        auto *result = backend.requestWithHistorySelection(
            QStringLiteral("BatchMutateRuns"), mr::IBackend::prepareHistorySelection(
                {{QStringLiteral("action"), QStringLiteral("soft_delete")}}, selectionTargets()));
        const QString stage = QStringLiteral("StageHistorySelection");
        QTRY_VERIFY(backend.last(stage).reply);
        const auto chunk = backend.last(stage);
        bool unsent = false;
        result->whenDone(this, [&, result](bool ok, const QVariantMap &, const QString &, const QString &) {
            unsent = !ok && result->neverSent();
        });
        Q_EMIT backend.connectionChanged();
        QVERIFY(unsent);
        chunk.reply->succeed({{QStringLiteral("selection_id"), chunk.payload.value(QStringLiteral("selection_id"))},
                              {QStringLiteral("next_chunk_index"), 1}, {QStringLiteral("sealed"), false}});
        QCoreApplication::processEvents();
        QVERIFY(!backend.last(QStringLiteral("BatchMutateRuns")).reply);
        QCOMPARE(backend.last(stage).payload, chunk.payload);
    }

    void aStageTimeoutDoesNotReportAnUnknownWrite()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        for (int index = 0; index < kLargeCount; ++index)
            history.setRunChecked(row(selectionRunId(index), 1), true);
        QSignalSpy moveState(&history, &mr::HistoryController::regionMovePendingChanged);
        QSignalSpy refresh(&history, &mr::HistoryController::refreshRequested);

        history.assignCheckedRunsToProfile(17, QStringLiteral("调整档案"));
        const QString stage = QStringLiteral("StageHistorySelection");
        QTRY_VERIFY(backend.last(stage).reply);
        backend.last(stage).reply->failTransport(QStringLiteral("ERR_TIMEOUT"), QStringLiteral("上传超时"));

        QVERIFY(!history.batchRunning());
        QVERIFY(history.batchFeedback().contains(QStringLiteral("操作尚未发送")));
        QCOMPARE(refresh.count(), 0);
        QCOMPARE(moveState.size(), 2);
        QCOMPARE(moveState.at(0), QVariantList({true, false}));
        QCOMPARE(moveState.at(1), QVariantList({false, false}));
        QCOMPARE(history.checkedRunCount(), kLargeCount);
        QVERIFY(!backend.last(QStringLiteral("BatchMutateRuns")).reply);
    }

    void aStageErrorRetainsItsMessageReferenceAndDetails()
    {
        ControlledBackend backend;
        auto *result = backend.requestWithHistorySelection(
            QStringLiteral("PreviewRunRegionMigration"),
            mr::IBackend::prepareHistorySelection({}, selectionTargets()));
        const QString stage = QStringLiteral("StageHistorySelection");
        QTRY_VERIFY(backend.last(stage).reply);
        const QJsonObject ref{{QStringLiteral("key"), QStringLiteral("history.selection.chunk_mismatch")}};
        const QJsonObject details{{QStringLiteral("chunk_index"), 0}};
        backend.last(stage).reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("错误"), details, ref);

        QVERIFY(result->isFinished());
        QVERIFY(result->neverSent());
        QVERIFY(!result->transportFailed());
        QCOMPARE(result->errorCode(), QStringLiteral("ERR_BAD_REQUEST"));
        QCOMPARE(result->errorRef().toMap(), ref.toVariantMap());
        QCOMPARE(result->errorDetails(), details.toVariantMap());
        QVERIFY(!backend.last(QStringLiteral("PreviewRunRegionMigration")).reply);
    }

    void aFrozenLargeSelectionExportsOnlyItsCheckedMembers()
    {
        SelectionMockBackend backend;
        QJsonArray dataset = selectionRuns();
        backend.resetRuns(dataset, {});
        mr::HistoryController history(&backend);
        history.setRunChecked(dataset.first().toObject().toVariantMap(), true);
        backend.setReplyDelay(25, QStringLiteral("QueryHistorySelection"));
        history.checkAllMatchingRuns();

        // The Collector freezes membership before delivering its first page. Insert
        // another matching row in its live data while that first page is in flight.
        QJsonObject extra = dataset.at(0).toObject();
        extra.insert(QStringLiteral("run_id"), selectionRunId(kLargeCount));
        dataset.append(extra);
        backend.resetRuns(dataset, {});
        QTRY_COMPARE_WITH_TIMEOUT(history.checkedRunCount(), kLargeCount, 10000);
        QVERIFY(!history.checkedRunIds().contains(selectionRunId(kLargeCount)));
        history.setRunChecked(dataset.at(100).toObject().toVariantMap(), false);

        QTemporaryDir output;
        QVERIFY(output.isValid());
        mr::ExportController exporter;
        exporter.setBackend(&backend);
        exporter.setTargetOverride(output.path());
        exporter.setHistorySort(QStringLiteral("history_date"), true);
        exporter.setCheckedRunIds(history.checkedRunIds());
        const auto selected = history.largeCheckedBatchTargets();
        QCOMPARE(selected.size(), kLargeCount - 1);
        exporter.exportCsv(selected);
        exporter.exportJson(selected);
        QTRY_COMPARE_WITH_TIMEOUT(backend.exports.size(), 2, 10000);
        for (const auto &result : std::as_const(backend.exports))
            QCOMPARE(result.value(QStringLiteral("row_count")).toInt(), kLargeCount - 1);
        QCOMPARE(backend.actions.size(), 2);
        const QString key = backend.actions.first().payload.value(QStringLiteral("selection_id")).toString();
        QCOMPARE(key.size(), 64);
        for (const auto &call : std::as_const(backend.actions)) {
            QCOMPARE(call.payload.value(QStringLiteral("selection_id")).toString(), key);
            QVERIFY(!call.payload.contains(QStringLiteral("filter")));
            const auto sort = call.payload.value(QStringLiteral("sort")).toObject();
            QCOMPARE(sort.value(QStringLiteral("field")).toString(), QStringLiteral("history_date"));
            QCOMPARE(sort.value(QStringLiteral("direction")).toString(), QStringLiteral("asc"));
        }
        QMap<int, QJsonArray> chunks;
        for (const auto &call : std::as_const(backend.chunks)) {
            const auto runs = call.payload.value(QStringLiteral("runs")).toArray();
            QVERIFY(runs.size() >= 1 && runs.size() <= mr::IBackend::kHistorySelectionPageSize);
            chunks.insert(call.payload.value(QStringLiteral("chunk_index")).toInt(), runs);
        }
        QStringList sentIds;
        for (const auto &runs : std::as_const(chunks))
            for (const auto &run : runs)
                sentIds.append(run.toObject().value(QStringLiteral("run_id")).toString());
        QStringList expected = history.checkedRunIds();
        expected.sort();
        QCOMPARE(sentIds, expected);
    }

    void largeBatchRetainsRevisionsAndReusesTheRequestAfterALostReply()
    {
        SelectionMockBackend backend;
        QJsonArray dataset = selectionRuns();
        backend.resetRuns(dataset, {});
        mr::HistoryController history(&backend);
        history.setRunChecked(dataset.at(0).toObject().toVariantMap(), true);
        auto edited = dataset.at(0).toObject();
        edited.insert(QStringLiteral("revision"), 2);
        dataset.replace(0, edited);
        backend.resetRuns(dataset, {});
        history.checkAllMatchingRuns();
        QTRY_COMPARE_WITH_TIMEOUT(history.checkedRunCount(), kLargeCount, 10000);

        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("测试批量"));
        QTRY_VERIFY_WITH_TIMEOUT(!history.batchRunning(), 10000);
        QCOMPARE(history.checkedRunCount(), kLargeCount);
        QVERIFY(backend.batches.isEmpty());
        QVERIFY(!history.batchFeedback().isEmpty());
        const auto firstChunk = backend.chunks.first().payload.value(QStringLiteral("runs")).toArray();
        QCOMPARE(firstChunk.at(0).toObject().value(QStringLiteral("expected_revision")).toInt(), 1);

        history.clearCheckedRuns();
        history.checkAllMatchingRuns();
        QTRY_COMPARE_WITH_TIMEOUT(history.checkedRunCount(), kLargeCount, 10000);
        backend.dropBatchReply = true;
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("测试批量"));
        QTRY_VERIFY_WITH_TIMEOUT(!history.batchRunning(), 15000);
        QCOMPARE(history.checkedRunCount(), kLargeCount);
        QCOMPARE(backend.batches.size(), 1);
        const auto prior = backend.actions.last();
        QVERIFY(!prior.id.isEmpty());
        QVERIFY(!prior.payload.contains(QStringLiteral("runs")));

        const auto firstChecked = history.checkedRunIds().first();
        const auto previous = std::find_if(dataset.cbegin(), dataset.cend(), [&firstChecked](const QJsonValue &value) {
            return value.toObject().value(QStringLiteral("run_id")).toString() == firstChecked;
        });
        QVERIFY(previous != dataset.cend());
        const auto rechecked = (*previous).toObject().toVariantMap();
        history.setRunChecked(rechecked, false);
        history.setRunChecked(rechecked, true);
        QCOMPARE(history.checkedRunIds().last(), firstChecked);
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("测试批量"));
        QTRY_COMPARE_WITH_TIMEOUT(history.checkedRunCount(), 0, 15000);
        QCOMPARE(backend.batches.size(), 2);
        QCOMPARE(backend.batches.last().value(QStringLiteral("changed_count")).toInt(), kLargeCount);
        QVERIFY(backend.batches.last().value(QStringLiteral("idempotent_replay")).toBool());
        QCOMPARE(backend.actions.last().id, prior.id);
        QCOMPARE(backend.actions.last().payload, prior.payload);
    }

    void transportFailureClassificationIsLocalAndTerminal()
    {
        mr::BackendReply business(QStringLiteral("business"), QStringLiteral("BatchMutateRuns"));
        business.fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("database refusal"),
                      {{QStringLiteral("transport_failed"), true}});
        business.failTransport(QStringLiteral("ERR_INTERNAL"), QStringLiteral("late timeout"));
        QVERIFY(!business.transportFailed());
        QVERIFY(!business.neverSent());
        QCOMPARE(business.errorMessage(), QStringLiteral("database refusal"));
        mr::BackendReply transport(QStringLiteral("transport"), QStringLiteral("BatchMutateRuns"));
        bool observed = false;
        transport.whenDone(this, [&transport, &observed](bool, const QVariantMap &, const QString &, const QString &) {
            observed = transport.transportFailed();
        });
        transport.failTransport(QStringLiteral("ERR_INTERNAL"), QStringLiteral("timeout"));
        QVERIFY(observed);
        transport.failUnsent(QStringLiteral("ERR_INTERNAL"), QStringLiteral("late disconnect"));
        QVERIFY(!transport.neverSent());
        QCOMPARE(transport.errorMessage(), QStringLiteral("timeout"));
    }

    void realTransportBatchFailureKeepsSelectionAndRetryIdentity_data()
    {
        QTest::addColumn<QString>("failure");
        for (const auto *failure : {"timeout", "disconnect", "unsent", "business"})
            QTest::newRow(failure) << QString::fromLatin1(failure);
    }

    void realTransportBatchFailureKeepsSelectionAndRetryIdentity()
    {
#ifndef Q_OS_WIN
        QSKIP("The production named-pipe transport fixture requires Windows.");
#else
        QFETCH(QString, failure);
        mrtest::TestPipe pipe("batch");
        QVERIFY(pipe.isValid());
        mr::IpcClient client;
        client.setServerName(pipe.serverName);
        client.setRequestTimeoutMs(20);
        if (failure != QLatin1String("unsent")) {
            client.start();
            QTRY_VERIFY(client.isConnected());
        }
        ControlledBackend backend;
        backend.transport = &client;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 3), true);
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto first = backend.last(QStringLiteral("BatchMutateRuns"));
        if (failure != QLatin1String("unsent")) {
            QByteArray bytes;
            QJsonObject request;
            bool readOk = true;
            bool received = false;
            QTRY_VERIFY(([&] {
                if (received || !readOk) return true;
                readOk = pipe.appendAvailable(bytes);
                received = readOk && mr::ipc::takeFrame(bytes, request) == mr::ipc::FrameStatus::Ok;
                return !readOk || received;
            })());
            QVERIFY(readOk);
            QCOMPARE(request.value(QStringLiteral("request_id")).toString(), first.id);
            if (failure == QLatin1String("disconnect")) pipe.close();
            if (failure == QLatin1String("business")) {
                QVERIFY(pipe.writeFrame(mr::ipc::encodeFrame({
                    {QStringLiteral("protocol_version"), 1}, {QStringLiteral("request_id"), first.id},
                    {QStringLiteral("message_type"), QStringLiteral("Error")}, {QStringLiteral("ok"), false},
                    {QStringLiteral("error"), QJsonObject{{QStringLiteral("code"), QStringLiteral("ERR_INTERNAL")},
                                                        {QStringLiteral("message"), QStringLiteral("数据库写入失败")}}}})));
            }
        }
        QTRY_VERIFY(!history.batchRunning());
        QCOMPARE(history.checkedRunIds(), QStringList({firstId()}));
        if (failure == QLatin1String("business")) {
            QVERIFY(history.batchFeedback().contains(QStringLiteral("数据库写入失败")));
            QVERIFY(history.batchFeedback().contains(QStringLiteral("重新勾选")));
            QVERIFY(!history.batchFeedback().contains(QStringLiteral("同一请求")));
            return;
        }
        QVERIFY2(history.batchFeedback().contains(QStringLiteral("保留勾选和原因")), qPrintable(history.batchFeedback()));
        QVERIFY(!history.batchFeedback().contains(QStringLiteral("重新勾选")));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto retry = backend.last(QStringLiteral("BatchMutateRuns"));
        QCOMPARE(retry.id, first.id);
        QCOMPARE(retry.payload, first.payload);
        client.stop();
#endif
    }

    void uncertainBatchRetryKeepsRequestIdentity()
    {
        ControlledBackend backend;
        mr::HistoryController history(&backend);
        history.setRunChecked(row(firstId(), 3), true);
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto first = backend.last(QStringLiteral("BatchMutateRuns"));
        first.reply->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("结果未知"));
        history.mutateCheckedRuns(QStringLiteral("soft_delete"), QStringLiteral("原因"));
        const auto retried = backend.last(QStringLiteral("BatchMutateRuns"));
        QVERIFY(!first.id.isEmpty());
        QCOMPARE(retried.id, first.id);
        retried.reply->succeed({{QStringLiteral("changed_count"), 1}});
        QCOMPARE(history.checkedRunCount(), 0);
    }
};

} // namespace

MR_DESKTOP_TEST_SUITE(HistorySelection)
{
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    HistorySelectionTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "HistorySelectionTests.moc"
