// One instance of a named pipe this test process serves, standing in for the Collector's server
// end: created by this process, so its owner is the current user. Windows only.
#pragma once

#include <QtGlobal>

#ifdef Q_OS_WIN

#include <QByteArray>
#include <QScopeGuard>
#include <QString>
#include <QUuid>

#include <utility>

// libstdc++ on MinGW defines NOMINMAX itself; see CollectorProcess.cpp.
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>

namespace mrtest {

/// \a purpose goes into the pipe's name, which is otherwise unique to this instance.
/// Reads and writes are bounded, for checking the real request frame a client sends and
/// returning an answer of the test's choosing.
struct TestPipe {
    const QString serverName;
    HANDLE handle = INVALID_HANDLE_VALUE;

    explicit TestPipe(const char *purpose)
        : serverName(QStringLiteral("\\\\.\\pipe\\MentorRecorderTest.") + QLatin1String(purpose)
                     + QLatin1Char('.') + QUuid::createUuid().toString(QUuid::WithoutBraces))
    {
        handle = ::CreateNamedPipeW(reinterpret_cast<const wchar_t *>(serverName.utf16()),
                                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
    }
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

} // namespace mrtest

#endif // Q_OS_WIN
