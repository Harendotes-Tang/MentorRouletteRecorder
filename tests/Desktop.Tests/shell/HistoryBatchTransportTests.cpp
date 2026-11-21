#include "DesktopTestRunner.h"
#include "HistoryBatchTestSupport.h"
#include "HistoryController.h"
#include "IpcFraming.h"
#include "TestCollectorGuard.h"

#include <QGuiApplication>
#include <QScopeGuard>
#include <QUuid>

#include <utility>

#ifdef Q_OS_WIN
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif

namespace {

#ifdef Q_OS_WIN
// Same native, current-user pipe fixture as LifecycleTests, with bounded I/O
// for checking the real request frame and returning a business refusal.
struct TestPipe {
    const QString serverName = QStringLiteral("\\\\.\\pipe\\MentorRecorderTest.batch.")
        + QUuid::createUuid().toString(QUuid::WithoutBraces);
    HANDLE handle = ::CreateNamedPipeW(reinterpret_cast<const wchar_t *>(serverName.utf16()),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);

    TestPipe() = default;
    TestPipe(const TestPipe &) = delete;
    TestPipe &operator=(const TestPipe &) = delete;
    ~TestPipe() { close(); }
    bool isValid() const { return handle != INVALID_HANDLE_VALUE; }
    void close() {
        if (!isValid()) return;
        ::CloseHandle(handle);
        handle = INVALID_HANDLE_VALUE;
    }
    // Every operation finishes (or is cancelled and drained) before its event,
    // OVERLAPPED and buffer leave scope. No I/O survives close() or early QVERIFY.
    bool transfer(bool writing, QByteArray &bytes, DWORD &transferred) const {
        OVERLAPPED operation{};
        operation.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return false;
        const auto closeEvent = qScopeGuard([&] { ::CloseHandle(operation.hEvent); });
        const BOOL done = writing
            ? ::WriteFile(handle, bytes.constData(), static_cast<DWORD>(bytes.size()), &transferred, &operation)
            : ::ReadFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()), &transferred, &operation);
        if (done) return true;
        if (::GetLastError() != ERROR_IO_PENDING) return false;
        if (::WaitForSingleObject(operation.hEvent, 2000) != WAIT_OBJECT_0) {
            ::CancelIoEx(handle, &operation);
            // Cancellation is asynchronous; keep the stack and buffer alive.
            ::GetOverlappedResult(handle, &operation, &transferred, TRUE);
            return false;
        }
        return ::GetOverlappedResult(handle, &operation, &transferred, FALSE);
    }
    bool appendAvailable(QByteArray &bytes) const {
        DWORD available = 0;
        if (!::PeekNamedPipe(handle, nullptr, 0, nullptr, &available, nullptr)) return false;
        if (!available) return true;
        QByteArray chunk(available, Qt::Uninitialized);
        DWORD received = 0;
        if (!transfer(false, chunk, received)) return false;
        bytes.append(chunk.constData(), received);
        return true;
    }
    bool writeFrame(QByteArray bytes) const {
        DWORD sent = 0;
        return transfer(true, bytes, sent) && std::cmp_equal(sent, bytes.size());
    }
};
#endif

class HistoryBatchTransportTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void transportFailureClassificationIsLocalAndTerminal() {
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
    void realTransportBatchFailureKeepsSelectionAndRetryIdentity_data() {
        QTest::addColumn<QString>("failure");
        for (const auto *failure : {"timeout", "disconnect", "unsent", "business"})
            QTest::newRow(failure) << QString::fromLatin1(failure);
    }
    void realTransportBatchFailureKeepsSelectionAndRetryIdentity() {
#ifndef Q_OS_WIN
        QSKIP("The production named-pipe transport fixture requires Windows.");
#else
        QFETCH(QString, failure);
        TestPipe pipe;
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
    void uncertainBatchRetryKeepsRequestIdentity() {
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

MR_DESKTOP_TEST_SUITE(HistoryBatchTransport)
{
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    HistoryBatchTransportTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "HistoryBatchTransportTests.moc"
