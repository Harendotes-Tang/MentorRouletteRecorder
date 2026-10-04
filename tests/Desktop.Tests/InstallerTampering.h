#pragma once

// ---------------------------------------------------------------------------
// What another process of the same user could try against the downloaded
// installer while 立即安装 holds it (InstallerVerifier.h): open it for writing,
// rename it, rename the folder it is in, delete it. Each attempt is made the
// plain Windows way, with the most generous sharing a writer could offer, and
// one that succeeds is undone where it can be, so the next attempt meets the
// same file.
// ---------------------------------------------------------------------------

#include <QDir>
#include <QFileInfo>
#include <QString>
#include <QStringList>
#include <QtGlobal>

#ifdef Q_OS_WIN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace mrtest {

/// Which of the attempts succeeded.
struct Tampering
{
    bool written = false;
    bool renamed = false;
    bool folderRenamed = false;
    bool deleted = false;

    bool any() const { return written || renamed || folderRenamed || deleted; }

    QString describe() const
    {
        QStringList done;
        if (written)
            done.append(QStringLiteral("opened for writing"));
        if (renamed)
            done.append(QStringLiteral("renamed"));
        if (folderRenamed)
            done.append(QStringLiteral("its folder renamed"));
        if (deleted)
            done.append(QStringLiteral("deleted"));
        return done.isEmpty() ? QStringLiteral("nothing succeeded")
                              : QStringLiteral("the installer could be ") + done.join(QStringLiteral(", "));
    }
};

inline Tampering tryToReplace(const QString &path)
{
    Tampering result;
#ifdef Q_OS_WIN
    const QString file = QDir::toNativeSeparators(QFileInfo(path).absoluteFilePath());
    const QString movedFile = file + QStringLiteral(".moved");
    const QString folder = QDir::toNativeSeparators(QFileInfo(path).absolutePath());
    const QString movedFolder = folder + QStringLiteral("-moved");
    const auto wide = [](const QString &value) { return reinterpret_cast<const wchar_t *>(value.utf16()); };

    const HANDLE writer = ::CreateFileW(wide(file), GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (writer != INVALID_HANDLE_VALUE) {
        result.written = true;
        ::CloseHandle(writer);
    }
    if (::MoveFileExW(wide(folder), wide(movedFolder), 0)) {
        result.folderRenamed = true;
        ::MoveFileExW(wide(movedFolder), wide(folder), 0);
    }
    if (::MoveFileExW(wide(file), wide(movedFile), 0)) {
        result.renamed = true;
        ::MoveFileExW(wide(movedFile), wide(file), 0);
    }
    result.deleted = ::DeleteFileW(wide(file)) != 0;
#else
    Q_UNUSED(path);
#endif
    return result;
}

} // namespace mrtest
