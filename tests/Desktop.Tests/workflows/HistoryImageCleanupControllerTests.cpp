#include "DesktopTestRunner.h"
#include "FileTestSupport.h"
#include "TestCollectorGuard.h"
#include "workflows/HistoryImageCleanupController.h"
#include "AppController.h"
#include "IBackend.h"
#include "MockBackend.h"
#include "NoteImageStore.h"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QJsonArray>
#include <QPointer>
#include <QScopeGuard>
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

/// The whole application on a MockBackend whose cleanup queue answers at once: pages of up
/// to 2000 run ids, and acknowledgements that fail while failAck is set.
class QueueCleanupBackend final : public mr::MockBackend {
public:
    QStringList pending;
    QList<QStringList> acknowledgements;
    bool failAck = false;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        if (type != QLatin1String("GetPendingImageCleanup") && type != QLatin1String("AcknowledgeImageCleanup"))
            return MockBackend::request(type, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("cleanup"), type, this);
        if (type == QLatin1String("GetPendingImageCleanup")) {
            QJsonArray ids; for (const QString &id : pending.mid(0, 2000)) ids.append(id);
            reply->succeed({{QStringLiteral("run_ids"), ids}});
        } else {
            QStringList ids;
            for (const auto &id : payload.value(QStringLiteral("run_ids")).toArray()) ids.append(id.toString());
            acknowledgements.append(ids);
            if (failAck) { reply->fail(QStringLiteral("ERR_PIPE_CLOSED"), QStringLiteral("连接中断"));
            } else {
                for (const QString &id : ids) pending.removeAll(id);
                reply->succeed({{QStringLiteral("acknowledged_count"), ids.size()}});
            }
        }
        return reply;
    }
};
bool putFile(const QString &path) {
    QFile file(path); return file.open(QIODevice::WriteOnly) && file.write("fixture") == 7;
}

class HistoryImageCleanupControllerTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void initTestCase()
    {
        // The application-level tests below build an AppController: never against the user's
        // Collector or data folder (TestCollectorGuard.h).
        mrtest::disableCollectorLaunch();
    }

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

    void cleanupRejectsTraversalAndUnexpectedDirectories()
    {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        QVERIFY(QDir().mkpath(store.runDirectory(firstRun()) + QStringLiteral("/nested")));
        QVERIFY(putFile(store.runDirectory(firstRun()) + QStringLiteral("/kept.png")));
        QVERIFY(!store.cleanupRun(QStringLiteral("../escape")).value(QStringLiteral("ok")).toBool());
        QVERIFY(!store.cleanupRun(firstRun()).value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(store.runDirectory(firstRun()) + QStringLiteral("/kept.png")));
        QVERIFY(QDir(store.runDirectory(firstRun())).rmdir(QStringLiteral("nested")));
        QVERIFY(store.cleanupRun(firstRun()).value(QStringLiteral("ok")).toBool());
        QVERIFY(QDir(store.rootDirectory()).exists());
        QVERIFY(!QDir(store.runDirectory(firstRun())).exists());
        QVERIFY(store.cleanupRun(firstRun()).value(QStringLiteral("ok")).toBool());
    }

    void cleanupRejectsJunctionWithoutTouchingItsTarget()
    {
#ifdef Q_OS_WIN
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        const QString outside = dir.filePath(QStringLiteral("outside"));
        QVERIFY(QDir().mkpath(store.rootDirectory()));
        QVERIFY(QDir().mkpath(outside));
        QVERIFY(putFile(outside + QStringLiteral("/keep.png")));
        if (!mrtest::makeJunction(store.runDirectory(firstRun()), outside)) QSKIP("mklink /J unavailable");
        auto unlink = qScopeGuard([&] { QDir(store.rootDirectory()).rmdir(firstRun()); });
        QVERIFY(!store.cleanupRun(firstRun()).value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(outside + QStringLiteral("/keep.png")));
#else
        QSKIP("Windows junction regression");
#endif
    }

    void cleanupAcknowledgesOnlySuccessAndRetriesFailedAcknowledgement()
    {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        QVERIFY(QDir().mkpath(store.runDirectory(firstRun())));
        QVERIFY(QDir().mkpath(store.runDirectory(secondRun()) + QStringLiteral("/nested")));
        QVERIFY(putFile(store.runDirectory(firstRun()) + QStringLiteral("/note.png")));
        QueueCleanupBackend backend;
        backend.pending = {firstRun(), secondRun()};
        backend.failAck = true;
        mr::AppController app(&backend, nullptr);
        app.setNoteImageStore(&store);
        QTRY_VERIFY(!app.historyImageCleanupRunning());
        QCOMPARE(backend.acknowledgements.size(), 1);
        QCOMPARE(backend.acknowledgements.first(), QStringList({firstRun()}));
        QCOMPARE(backend.pending.size(), 2);
        QVERIFY(app.historyImageCleanupFeedback().contains(QStringLiteral("清理结果还没保存")));
        backend.failAck = false;
        QVERIFY(QDir(store.runDirectory(secondRun())).rmdir(QStringLiteral("nested")));
        app.retryHistoryImageCleanup();
        QTRY_VERIFY(!app.historyImageCleanupRunning());
        QVERIFY(backend.pending.isEmpty());
        QVERIFY(app.historyImageCleanupFeedback().isEmpty());
    }

    void cleanupDrainsMoreThanOneFullQueuePage()
    {
        QTemporaryDir dir;
        mr::NoteImageStore store(dir.filePath(QStringLiteral("images")));
        QueueCleanupBackend backend;
        for (int i = 1; i <= 2001; ++i)
            backend.pending.append(QStringLiteral("00000000-0000-0000-0000-%1").arg(i, 12, 16, QLatin1Char('0')));
        mr::AppController app(&backend, nullptr);
        app.setNoteImageStore(&store);
        QTRY_VERIFY_WITH_TIMEOUT(backend.pending.isEmpty(), 10000);
        QCOMPARE(backend.acknowledgements.size(), 2);
        QCOMPARE(backend.acknowledgements.first().size(), 2000);
        QCOMPARE(backend.acknowledgements.last().size(), 1);
        QVERIFY(!app.historyImageCleanupRunning());
    }
};
}

MR_DESKTOP_QTEST_SUITE(HistoryImageCleanupController, HistoryImageCleanupControllerTests, QGuiApplication)
#include "HistoryImageCleanupControllerTests.moc"
