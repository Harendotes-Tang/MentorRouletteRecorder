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
        backend.capture["game_selection_required"] = true;
        selection.observe(backend.capture.toVariantMap());
        QVERIFY(!selection.selectionMessage().isEmpty());
        backend.connected = false;
        emit backend.connectionChanged();
        QVERIFY(selection.selectionMessage().isEmpty());
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
