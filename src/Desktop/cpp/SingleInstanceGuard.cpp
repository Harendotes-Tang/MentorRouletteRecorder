#include "SingleInstanceGuard.h"

#include "PipeName.h"

#ifdef Q_OS_WIN
// libstdc++ on MinGW defines NOMINMAX itself; see CollectorProcess.cpp.
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#  include <QWinEventNotifier>
#endif

namespace mr {

SingleInstanceGuard::SingleInstanceGuard(const QString &key, QObject *parent)
    : QObject(parent)
{
#ifdef Q_OS_WIN
    if (key.isEmpty())
        return;
    const QString eventName = QStringLiteral("Local\\%1.show").arg(key);
    const QString mutexName = QStringLiteral("Local\\%1.instance").arg(key);
    // The event before the mutex: a later instance that finds the mutex can
    // count on the event being there to set. Auto-reset, so each request is
    // consumed by the one wait that sees it.
    m_event = ::CreateEventW(nullptr, FALSE, FALSE,
                             reinterpret_cast<const wchar_t *>(eventName.utf16()));
    m_mutex = ::CreateMutexW(nullptr, FALSE,
                             reinterpret_cast<const wchar_t *>(mutexName.utf16()));
    const DWORD error = ::GetLastError();
    // ERROR_ACCESS_DENIED is a mutex that exists but was created by an
    // elevated first instance: there is a first instance all the same.
    if ((m_mutex != nullptr && error == ERROR_ALREADY_EXISTS)
        || (m_mutex == nullptr && error == ERROR_ACCESS_DENIED)) {
        m_primary = false;
        return;
    }
    // Any other failure leaves the guard off rather than the program refusing
    // to start.
    if (m_event != nullptr) {
        m_notifier = new QWinEventNotifier(m_event, this);
        connect(m_notifier, &QWinEventNotifier::activated, this,
                [this] { Q_EMIT activationRequested(); });
    }
#else
    Q_UNUSED(key)
#endif
}

SingleInstanceGuard::~SingleInstanceGuard()
{
#ifdef Q_OS_WIN
    // The notifier waits on the handle; it goes before the handle is closed.
    delete m_notifier;
    m_notifier = nullptr;
    if (m_event != nullptr)
        ::CloseHandle(m_event);
    if (m_mutex != nullptr)
        ::CloseHandle(m_mutex);
#endif
}

QString SingleInstanceGuard::defaultKey()
{
    const QString pipe = ipc::currentUserPipeName();
    return pipe.isEmpty() ? QString() : pipe + QStringLiteral(".desktop");
}

bool SingleInstanceGuard::signalPrimary() const
{
#ifdef Q_OS_WIN
    if (m_primary || m_event == nullptr)
        return false;
    // The player just started this process, so it may hand the foreground on;
    // without that the first window could only flash its taskbar button.
    ::AllowSetForegroundWindow(ASFW_ANY);
    return ::SetEvent(m_event) != 0;
#else
    return false;
#endif
}

} // namespace mr
