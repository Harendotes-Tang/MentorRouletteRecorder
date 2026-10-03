#include "TestCollectorGuard.h"
#include "GameSelectionController.h"
#include <QGuiApplication>
#include <QSignalSpy>
#include <QTest>

class SelectionBackend : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }
    bool connected = true, hold = false;
    mr::BackendReply *pending = nullptr;
    QJsonObject capture{{"game_selection_required", true}, {"ffxiv_process_id", QJsonValue::Null},
        {"game_processes", QJsonArray{
            QJsonObject{{"process_id", 101}, {"started_at_utc", "2026-10-02T01:00:00.000Z"}, {"selection_token", "first"}},
            QJsonObject{{"process_id", 202}, {"started_at_utc", "2026-10-02T02:00:00.000Z"}, {"selection_token", "second"}}}}};
    QList<QJsonObject> selections;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QStringLiteral("test"), type, this);
        if (type == QLatin1String("SelectGameProcess")) {
            selections.append(payload);
            if (hold) { pending = reply; return reply; }
            capture["ffxiv_process_id"] = payload["process_id"];
            capture["game_selection_required"] = false;
        }
        reply->succeed(capture);
        return reply;
    }
};

class GameSelectionTests : public QObject
{
    Q_OBJECT
private slots:
    void selectionReasonHasItsOwnMessage_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::addColumn<QString>("expected");
        QTest::newRow("multiple") << QStringLiteral("MULTIPLE") << QStringLiteral("多个游戏客户端");
        QTest::newRow("exited") << QStringLiteral("EXITED") << QStringLiteral("所选游戏已退出");
        QTest::newRow("unknown identity") << QStringLiteral("IDENTITY_UNAVAILABLE") << QStringLiteral("启动时间");
    }

    void selectionReasonHasItsOwnMessage()
    {
        QFETCH(QString, reason);
        QFETCH(QString, expected);
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        backend.capture["game_selection_reason"] = reason;
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(selection.selectionMessage().contains(expected));
        selection.select(0);
        QVERIFY(selection.selectionMessage().isEmpty());
        // Asked again once the chosen client is gone. (A re-ask that still lists
        // the client just pinned, milliseconds after the reply, is a snapshot from
        // before the switch: aStatusReadBeforeTheChoiceCannotUndoIt.)
        backend.capture["game_selection_required"] = true;
        backend.capture["game_processes"] = QJsonArray{backend.capture["game_processes"].toArray().at(1)};
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(!selection.selectionMessage().isEmpty());
        backend.connected = false;
        emit backend.connectionChanged();
        QVERIFY(selection.selectionMessage().isEmpty());
    }

    // 审查 OD-1：列表空了就没有可选的游戏；「记录对象」卡片不得要求选择。
    void nothingListedIsNothingToChoose_data()
    {
        QTest::addColumn<QString>("reason");
        QTest::newRow("only client closed") << QStringLiteral("EXITED");
        QTest::newRow("every client closed") << QStringLiteral("MULTIPLE");
    }

    void nothingListedIsNothingToChoose()
    {
        QFETCH(QString, reason);
        SelectionBackend backend;
        backend.capture["game_selection_reason"] = reason;
        backend.capture["game_processes"] = QJsonArray();
        mr::GameSelectionController selection(&backend);
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(!selection.required());
        QVERIFY(selection.selectionMessage().isEmpty());
        QVERIFY(selection.choices().isEmpty());
    }

    // 审查 OI-2：只列出一个客户端时按实际数量措辞。
    void aSingleListedClientIsNotCalledSeveral()
    {
        SelectionBackend backend;
        backend.capture["game_selection_reason"] = "MULTIPLE";
        backend.capture["game_processes"] = QJsonArray{backend.capture["game_processes"].toArray().at(1)};
        mr::GameSelectionController selection(&backend);
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(selection.required());
        QVERIFY2(!selection.selectionMessage().contains(QString::fromUtf8("多个")),
                 qPrintable(selection.selectionMessage()));
        QVERIFY(selection.selectionMessage().contains(QString::fromUtf8("选择")));
    }

    void foregroundIsIgnoredUntilTheUserStartsPicking()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        selection.observe(backend.capture.toVariantMap());
        selection.observeForegroundProcess(202);
        QVERIFY(backend.selections.isEmpty());
        selection.beginPick();
        QVERIFY(selection.picking());
        selection.observeForegroundProcess(999);
        QVERIFY(backend.selections.isEmpty());
        selection.observeForegroundProcess(202);
        QCOMPARE(backend.selections.size(), 1);
        QCOMPARE(backend.selections.first()["process_id"].toInt(), 202);
        QCOMPARE(backend.selections.first()["selection_token"].toString(), QStringLiteral("second"));
        QVERIFY(!selection.picking());
        QVERIFY(!selection.required());
        selection.observeForegroundProcess(101);
        QCOMPARE(backend.selections.size(), 1);
    }

    void cancelDoesNotChangeTheTarget()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        selection.beginPick();
        selection.cancelPick();
        selection.observeForegroundProcess(101);
        QVERIFY(backend.selections.isEmpty());
    }

    void unchangedPollDoesNotResetTheChoiceList()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        QSignalSpy choices(&selection, &mr::GameSelectionController::choicesChanged);
        selection.observe(backend.capture.toVariantMap());
        QCOMPARE(choices.count(), 1);
        selection.observe(backend.capture.toVariantMap());
        QCOMPARE(choices.count(), 1);
        selection.select(1);
        QCOMPARE(backend.selections.size(), 1);
        QCOMPARE(backend.selections.first()["process_id"].toInt(), 202);
    }

    void pickerKeepsTheOriginalTokenWhenPidIsReused()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        selection.beginPick();
        auto processes = backend.capture["game_processes"].toArray();
        auto replaced = processes[1].toObject();
        replaced["selection_token"] = "replacement";
        processes[1] = replaced;
        backend.capture["game_processes"] = processes;
        selection.observe(backend.capture.toVariantMap());
        selection.observeForegroundProcess(202);
        QCOMPARE(backend.selections.first()["selection_token"].toString(), QStringLiteral("second"));
    }

    // 审查 OD-2：「已锁定所选游戏」只说刚才那一次切换；所选游戏退出后不能与
    // 「所选游戏已退出」并排出现。一次失败的说明也在局面变化后退场。
    void aChangedTargetRetiresTheLastSelectionMessage()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        selection.observe(backend.capture.toVariantMap());
        selection.select(0);
        QVERIFY(selection.message().contains(QString::fromUtf8("已锁定")));
        // An unchanged poll keeps it.
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(selection.message().contains(QString::fromUtf8("已锁定")));
        // The chosen client exits; the other one is still listed.
        backend.capture["game_selection_required"] = true;
        backend.capture["game_selection_reason"] = "EXITED";
        backend.capture["ffxiv_process_id"] = QJsonValue::Null;
        backend.capture["game_processes"] = QJsonArray{backend.capture["game_processes"].toArray().at(1)};
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(selection.required());
        QVERIFY2(selection.message().isEmpty(), qPrintable(selection.message()));

        // A refusal is retired the same way once the Collector resumes on its own.
        backend.hold = true;
        selection.select(0);
        backend.pending->fail(QStringLiteral("ERR_TEST"), QStringLiteral("refused"));
        QCOMPARE(selection.message(), QStringLiteral("refused"));
        backend.capture["game_selection_required"] = false;
        backend.capture["ffxiv_process_id"] = 202;
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(selection.message().isEmpty());
    }

    // 审查 S2-3：选择之前读到、却在选择回应之后才送达的状态（切换途中发布的状态
    // 事件），不能把界面翻回「请选择」或改回旧的记录对象。
    void aStatusReadBeforeTheChoiceCannotUndoIt()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        const QVariantMap beforeChoice = backend.capture.toVariantMap();
        selection.observe(beforeChoice);
        selection.select(1);
        QVERIFY(!selection.required());
        const QString locked = selection.currentLabel();
        QVERIFY(!locked.isEmpty());

        selection.observe(beforeChoice);
        QVERIFY(!selection.required());
        QVERIFY(selection.selectionMessage().isEmpty());
        QCOMPARE(selection.currentLabel(), locked);

        // A snapshot with the other client still locked is just as old.
        QVariantMap otherLocked = beforeChoice;
        otherLocked.insert(QStringLiteral("game_selection_required"), false);
        otherLocked.insert(QStringLiteral("ffxiv_process_id"), 101);
        selection.observe(otherLocked);
        QCOMPARE(selection.currentLabel(), locked);

        // The chosen client really exiting is never held back.
        QVariantMap exited = beforeChoice;
        exited.insert(QStringLiteral("game_selection_reason"), QStringLiteral("EXITED"));
        exited.insert(QStringLiteral("game_processes"),
                      QVariantList{beforeChoice.value(QStringLiteral("game_processes")).toList().first()});
        selection.observe(exited);
        QVERIFY(selection.required());
    }

    // The guard is only for snapshots that crossed the reply: once the switch has
    // settled, the Collector's word is taken as it comes.
    void aLaterContradictionIsAdopted()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        const QVariantMap beforeChoice = backend.capture.toVariantMap();
        selection.observe(beforeChoice);
        selection.select(1);
        QTest::qWait(mr::GameSelectionController::kSelectionSettleMs + 100);
        selection.observe(beforeChoice);
        QVERIFY(selection.required());
    }

    // 审查 S2-4：采集服务掉线的那一刻点中的窗口不能被悄悄丢掉。
    void aPickMadeWhileDisconnectedSaysItDidNotTakeEffect()
    {
        SelectionBackend backend;
        mr::GameSelectionController selection(&backend);
        selection.beginPick();
        QVERIFY(selection.picking());
        backend.connected = false;  // the pipe is down; its signal has not run yet
        selection.observeForegroundProcess(202);
        QVERIFY(backend.selections.isEmpty());
        QVERIFY(!selection.picking());
        QVERIFY(!selection.busy());
        QVERIFY2(!selection.message().isEmpty(), "the pick was dropped without a word");
    }

    void disconnectDiscardsAnOldSelectionReply()
    {
        SelectionBackend backend;
        backend.hold = true;
        mr::GameSelectionController selection(&backend);
        QSignalSpy selected(&selection, &mr::GameSelectionController::selected);
        selection.observe(backend.capture.toVariantMap());
        selection.select(0);
        QVERIFY(selection.busy());
        backend.connected = false;
        emit backend.connectionChanged();
        backend.pending->succeed(backend.capture);
        QVERIFY(!selection.busy());
        QVERIFY(selection.choices().isEmpty());
        QCOMPARE(selected.count(), 0);
    }
};

int main(int argc, char **argv)
{
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    GameSelectionTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "GameSelectionTests.moc"
