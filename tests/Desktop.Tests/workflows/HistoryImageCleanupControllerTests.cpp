#include "DesktopTestRunner.h"
#include "workflows/HistoryImageCleanupController.h"
#include "IBackend.h"
#include "NoteImageStore.h"

#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QPointer>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>

#include <memory>

namespace {
QString firstRun()
{
    return QStringLiteral("00000000-0000-0000-0000-000000000001");
}
QString secondRun()
{
    return QStringLiteral("00000000-0000-0000-0000-000000000002");
}

class CleanupBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("cleanup-controlled"); }
    bool isConnected() const override { return true; }
    int reads = 0;
    QList<QStringList> acknowledgements;
    QPointer<mr::BackendReply> pendingRead;
    QPointer<mr::BackendReply> pendingAck;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QStringLiteral("cleanup-test"), type, this);
        if (type == QLatin1String("GetPendingImageCleanup")) {
            ++reads;
            pendingRead = reply;
        } else if (type == QLatin1String("AcknowledgeImageCleanup")) {
            QStringList ids;
            for (const auto &id : payload.value(QStringLiteral("run_ids")).toArray())
                ids.append(id.toString());
            acknowledgements.append(ids);
            pendingAck = reply;
        }
        return reply;
    }
};

class HistoryImageCleanupControllerTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void oneReadAtATimeAndFailureAllowsRetry()
    {
        QTemporaryDir root;
        mr::NoteImageStore store(root.path());
        CleanupBackend backend;
        mr::HistoryImageCleanupController controller(&backend);
        controller.setStore(&store);
        QVERIFY(controller.running());
        controller.retry();
        QCOMPARE(backend.reads, 1);
        backend.pendingRead->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("closed"));
        QVERIFY(!controller.running());
        QVERIFY(controller.feedback().contains(QStringLiteral("closed")));
        controller.retry();
        QCOMPARE(backend.reads, 2);
    }

    void acknowledgesSuccessfulFilesAndRetriesFailedReceipt()
    {
        QTemporaryDir root;
        mr::NoteImageStore store(root.path());
        QVERIFY(QDir().mkpath(store.runDirectory(firstRun())));
        QVERIFY(QDir().mkpath(store.runDirectory(secondRun()) + QStringLiteral("/nested")));
        QFile image(store.runDirectory(firstRun()) + QStringLiteral("/note.png"));
        QVERIFY(image.open(QIODevice::WriteOnly));
        image.write("fixture");
        image.close();
        CleanupBackend backend;
        mr::HistoryImageCleanupController controller(&backend);
        controller.setStore(&store);
        backend.pendingRead->succeed({{QStringLiteral("run_ids"), QJsonArray{firstRun(), secondRun()}}});
        QTRY_COMPARE(backend.acknowledgements.size(), 1);
        QCOMPARE(backend.acknowledgements.first(), QStringList{firstRun()});
        QVERIFY(!QFile::exists(image.fileName()));
        backend.pendingAck->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("closed"));
        QVERIFY(!controller.running());
        QVERIFY(controller.feedback().contains(QStringLiteral("清理结果还没保存")));
        controller.retry();
        backend.pendingRead->succeed({{QStringLiteral("run_ids"), QJsonArray{firstRun()}}});
        QTRY_COMPARE(backend.acknowledgements.size(), 2);
        backend.pendingAck->succeed({});
        QVERIFY(!controller.running());
        QVERIFY(controller.feedback().isEmpty());
    }

    void lateReadAfterDestructionDoesNotRemoveFiles()
    {
        QTemporaryDir root;
        mr::NoteImageStore store(root.path());
        QVERIFY(QDir().mkpath(store.runDirectory(firstRun())));
        CleanupBackend backend;
        auto controller = std::make_unique<mr::HistoryImageCleanupController>(&backend);
        controller->setStore(&store);
        controller.reset();
        backend.pendingRead->succeed({{QStringLiteral("run_ids"), QJsonArray{firstRun()}}});
        QCoreApplication::processEvents();
        QVERIFY(QDir(store.runDirectory(firstRun())).exists());
        QVERIFY(backend.acknowledgements.isEmpty());
    }

    void unavailableStoreStopsBatchWithoutAcknowledgement()
    {
        QTemporaryDir root;
        CleanupBackend backend;
        auto store = std::make_unique<mr::NoteImageStore>(root.path());
        mr::HistoryImageCleanupController controller(&backend);
        controller.setStore(store.get());
        store.reset();
        backend.pendingRead->succeed({{QStringLiteral("run_ids"), QJsonArray{firstRun()}}});
        QVERIFY(!controller.running());
        QVERIFY(backend.acknowledgements.isEmpty());
        QVERIFY(controller.feedback().contains(QStringLiteral("图片服务已关闭")));
    }

    void backendDestructionBetweenRowsLeavesReceiptUnconfirmed()
    {
        QTemporaryDir root;
        mr::NoteImageStore store(root.path());
        auto backend = std::make_unique<CleanupBackend>();
        mr::HistoryImageCleanupController controller(backend.get());
        controller.setStore(&store);
        backend->pendingRead->succeed({{QStringLiteral("run_ids"), QJsonArray{firstRun()}}});
        QVERIFY(controller.running());
        backend.reset();
        QTRY_VERIFY(!controller.running());
        QVERIFY(controller.feedback().contains(QStringLiteral("清理结果还没保存")));
    }
};
}

MR_DESKTOP_QTEST_SUITE(HistoryImageCleanupController, HistoryImageCleanupControllerTests, QGuiApplication)
#include "HistoryImageCleanupControllerTests.moc"
