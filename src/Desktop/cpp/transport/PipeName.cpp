#include "PipeName.h"

#include <QCryptographicHash>

#ifdef Q_OS_WIN
#  include <windows.h>
#  include <sddl.h>
#  include <vector>
#endif

namespace mr::ipc {

QString pipeNameForSid(const QString &sidString)
{
    if (sidString.isEmpty())
        return {};

    const QByteArray digest = QCryptographicHash::hash(
        sidString.toUtf8(), QCryptographicHash::Sha256);

    const QString hex =
        QString::fromLatin1(digest.left(kSidHashBytes).toHex());

    return QStringLiteral("MentorRecorder.%1.v1").arg(hex);
}

QString localSocketServerName(const QString &pipeName)
{
    if (pipeName.isEmpty())
        return {};
    return QStringLiteral("\\\\.\\pipe\\") + pipeName;
}

QString currentUserSidString()
{
#ifdef Q_OS_WIN
    HANDLE token = nullptr;
    // Our own process token only. No other process is ever opened.
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token))
        return {};

    DWORD needed = 0;
    ::GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
    if (needed == 0) {
        ::CloseHandle(token);
        return {};
    }

    std::vector<unsigned char> buffer(needed);
    if (!::GetTokenInformation(token, TokenUser, buffer.data(), needed, &needed)) {
        ::CloseHandle(token);
        return {};
    }
    ::CloseHandle(token);

    const auto *user = reinterpret_cast<const TOKEN_USER *>(buffer.data());
    LPWSTR text = nullptr;
    if (!::ConvertSidToStringSidW(user->User.Sid, &text) || text == nullptr)
        return {};

    const QString sid = QString::fromWCharArray(text);
    ::LocalFree(text);
    return sid;
#else
    return {};
#endif
}

QString currentUserServerName()
{
    return localSocketServerName(currentUserPipeName());
}

QString currentUserPipeName()
{
    return pipeNameForSid(currentUserSidString());
}

} // namespace mr::ipc
