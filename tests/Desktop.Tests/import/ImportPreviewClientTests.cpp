#include "DesktopTestRunner.h"
#include "ImportPreviewClient.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QJsonArray>
#include <QPointer>
#include <QtTest>
#include <utility>

namespace {

class PreviewBackend final : public mr::IBackend
{
public:
    struct Call { QJsonObject payload; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    bool connected = true;
    QJsonObject immediateAnswer;
    QString backendName() const override { return QStringLiteral("preview-client-test"); }
    bool isConnected() const override { return connected; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({payload, reply});
        if (!immediateAnswer.isEmpty())
            reply->succeed(immediateAnswer);
        return reply;
    }
};

QJsonObject previewPage(int page, const QString &id = QStringLiteral("snapshot"))
{
    return {{"preview_id", id}, {"source_kind", "PASTE"}, {"source_name", "source"},
        {"summary", QJsonObject{{"total", 2}}},
        {"page_info", QJsonObject{{"page", page}, {"page_size", 1}, {"total", 2}}},
        {"rows", QJsonArray{QJsonObject{{"row_number", page}, {"candidate", QJsonObject{{"note", page}}}}}}};
}

mr::RunImportSource source()
{
    mr::RunImportSource value;
    value.kind = QStringLiteral("PASTE");
    value.text = QStringLiteral("source");
    value.pageSize = 100;
    return value;
}

class ImportPreviewClientTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void publishesOnlyTheCompleteSnapshot()
    {
        PreviewBackend backend;
        mr::ImportPreviewClient client;
        int completed = 0;
        mr::ImportPreviewResult result;
        client.start(&backend, source(), [&](mr::ImportPreviewResult answer) { ++completed; result = std::move(answer); });
        backend.calls[0].reply->succeed(previewPage(1));
        QCOMPARE(completed, 0);
        QTRY_COMPARE(backend.calls.size(), 2);
        QCOMPARE(backend.calls[1].payload, (QJsonObject{{"preview_id", "snapshot"}, {"page", 2}}));
        backend.calls[1].reply->succeed(previewPage(2));
        QCOMPARE(completed, 1);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::None);
        QCOMPARE(result.payload.value("rows").toList().size(), 2);
        QVERIFY(!result.payload.contains("page_info"));
    }

    void rejectsDifferentSnapshotsWithoutPublishingPartialRows()
    {
        PreviewBackend backend;
        mr::ImportPreviewClient client;
        int completed = 0;
        mr::ImportPreviewResult result;
        client.start(&backend, source(), [&](mr::ImportPreviewResult answer) { ++completed; result = std::move(answer); });
        backend.calls[0].reply->succeed(previewPage(1));
        QTRY_COMPARE(backend.calls.size(), 2);
        backend.calls[1].reply->succeed(previewPage(2, QStringLiteral("replaced")));
        QCOMPARE(completed, 1);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::Incomplete);
        QVERIFY(result.payload.isEmpty());
    }

    void cancellationSuppressesRepliesAndQueuedContinuation()
    {
        PreviewBackend backend;
        mr::ImportPreviewClient client;
        int completed = 0;
        client.start(&backend, source(), [&](mr::ImportPreviewResult) { ++completed; });
        backend.calls[0].reply->succeed(previewPage(1));
        client.cancel();
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.size(), 1);
        client.start(&backend, source(), [&](mr::ImportPreviewResult) { ++completed; });
        const auto late = backend.calls.last().reply;
        client.cancel();
        late->succeed(previewPage(1));
        QCoreApplication::processEvents();
        QCOMPARE(completed, 0);
        QCOMPARE(backend.calls.size(), 2);
    }

    void destructionSuppressesPendingReplies()
    {
        PreviewBackend backend;
        int completed = 0;
        auto client = std::make_unique<mr::ImportPreviewClient>();
        client->start(&backend, source(), [&](mr::ImportPreviewResult) { ++completed; });
        client.reset();
        backend.calls[0].reply->succeed(previewPage(1));
        QCoreApplication::processEvents();
        QCOMPARE(completed, 0);
        QCOMPARE(backend.calls.size(), 1);
    }

    void synchronousCompletionAndDisconnectedStartFinishOnce()
    {
        PreviewBackend backend;
        backend.immediateAnswer = {{"preview_id", "empty"}, {"summary", QJsonObject{{"total", 0}}},
                                   {"rows", QJsonArray{}}};
        mr::ImportPreviewClient client;
        int completed = 0;
        mr::ImportPreviewResult result;
        const auto receive = [&](mr::ImportPreviewResult answer) { ++completed; result = std::move(answer); };
        client.start(&backend, source(), receive);
        QCOMPARE(completed, 1);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::None);
        backend.connected = false;
        client.start(&backend, source(), receive);
        QCOMPARE(completed, 2);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::Disconnected);
        QCOMPARE(backend.calls.size(), 1);
        QCoreApplication::processEvents();
        QCOMPARE(completed, 2);
    }

    void replacementCannotAdoptThePreviousReply()
    {
        PreviewBackend backend;
        mr::ImportPreviewClient client;
        int oldCompletions = 0;
        int newCompletions = 0;
        client.start(&backend, source(), [&](mr::ImportPreviewResult) { ++oldCompletions; });
        client.start(&backend, source(), [&](mr::ImportPreviewResult) { ++newCompletions; });
        backend.calls[0].reply->succeed(previewPage(1));
        backend.calls[1].reply->succeed({{"preview_id", "replacement"}, {"summary", QJsonObject{{"total", 0}}},
                                       {"rows", QJsonArray{}}});
        QCoreApplication::processEvents();
        QCOMPARE(oldCompletions, 0);
        QCOMPARE(newCompletions, 1);
        QCOMPARE(backend.calls.size(), 2);
    }

    void legacyFallbackIsRestrictedAndCompleteResponseIsValidated()
    {
        PreviewBackend backend;
        mr::ImportPreviewClient client;
        int completed = 0;
        mr::ImportPreviewResult result;
        client.start(&backend, source(), [&](mr::ImportPreviewResult answer) { ++completed; result = std::move(answer); });
        backend.calls[0].reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("legacy"), {},
            {{"key", "ipc.field.unknown"}, {"args", QJsonObject{{"field", "page_size"}}}});
        QTRY_COMPARE(backend.calls.size(), 2);
        QVERIFY(!backend.calls[1].payload.contains("page_size"));
        auto incomplete = previewPage(1);
        incomplete.remove("page_info");
        backend.calls[1].reply->succeed(incomplete);
        QCOMPARE(completed, 1);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::Incomplete);

        client.start(&backend, source(), [&](mr::ImportPreviewResult answer) { ++completed; result = std::move(answer); });
        backend.calls.last().reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("capacity"));
        QCoreApplication::processEvents();
        QCOMPARE(backend.calls.size(), 3);
        QCOMPARE(completed, 2);
        QCOMPARE(result.failure, mr::ImportPreviewFailure::Rejected);
    }
};
}

MR_DESKTOP_QTEST_SUITE(ImportPreviewClient, ImportPreviewClientTests, QCoreApplication)
#include "ImportPreviewClientTests.moc"
