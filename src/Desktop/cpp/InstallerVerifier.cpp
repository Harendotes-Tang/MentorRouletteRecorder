#include "InstallerVerifier.h"

#include <QDir>
#include <QFileInfo>
#include <QRegularExpression>

#ifdef Q_OS_WIN
#include <windows.h>

#include <string>
#endif

namespace mr {
namespace installer {
namespace {

/// A symbolic link, a junction, a shortcut, or - on Windows - any other
/// reparse point Qt has no name for.
bool isLink(const QString &path)
{
    const QFileInfo info(path);
    if (info.isSymbolicLink() || info.isJunction() || info.isShortcut())
        return true;
#ifdef Q_OS_WIN
    // Read from the entry itself: a reparse point is never followed here.
    const DWORD attributes = ::GetFileAttributesW(
        reinterpret_cast<const wchar_t *>(QDir::toNativeSeparators(path).utf16()));
    if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
        return true;
#endif
    return false;
}

} // namespace

QString fileName(const QString &version)
{
    static const QRegularExpression shape(QStringLiteral("^\\d+\\.\\d+\\.\\d+$"));
    if (!shape.match(version).hasMatch())
        return {};
    return QStringLiteral("MentorRecorder-%1-setup.exe").arg(version);
}

bool isSha256(const QString &value)
{
    static const QRegularExpression shape(QStringLiteral("^[0-9a-f]{64}$"));
    return shape.match(value).hasMatch();
}

bool isInUpdatesDirectory(const QString &path, const QString &dataDirectory,
                          const QString &version)
{
    const QString name = fileName(version);
    const QString root = QDir::fromNativeSeparators(dataDirectory.trimmed());
    if (name.isEmpty() || root.isEmpty() || !QDir::isAbsolutePath(root))
        return false;
    // The path is judged as written: one that only reaches the folder after a
    // "..", or names it relative to wherever this process happens to stand, is
    // not the file the Collector reported writing.
    const QString reported = QDir::fromNativeSeparators(path);
    if (!QDir::isAbsolutePath(reported) || QDir::cleanPath(reported) != reported)
        return false;
    const QFileInfo info(reported);
    const QString expectedDirectory = QDir::cleanPath(root + QStringLiteral("/updates"));
    return info.fileName() == name
           && info.path().compare(expectedDirectory, Qt::CaseInsensitive) == 0;
}

bool isRegularFile(const QString &path)
{
    const QFileInfo info(path);
    if (!info.exists() || !info.isFile() || isLink(path))
        return false;
    return !isLink(info.absolutePath());
}

} // namespace installer

InstallerHold::~InstallerHold()
{
    release();
}

bool InstallerHold::open(const QString &path)
{
    release();
#ifdef Q_OS_WIN
    const QString native = QDir::toNativeSeparators(path);
    // Read access, and others may only read as well: a writer, a rename or a
    // delete - which needs the file opened for deleting - is refused while this
    // handle is open, and a writer that already has it open makes this open
    // fail instead. A link at the last segment is opened as itself, so
    // isRegularFile() refuses it rather than following it.
    const HANDLE handle = ::CreateFileW(reinterpret_cast<const wchar_t *>(native.utf16()), GENERIC_READ,
                                        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return false;
    m_handle = handle;
    return true;
#else
    // Elsewhere nothing can be held this way, so nothing is started.
    Q_UNUSED(path);
    return false;
#endif
}

bool InstallerHold::isRegularFile() const
{
#ifdef Q_OS_WIN
    if (!m_handle || ::GetFileType(m_handle) != FILE_TYPE_DISK)
        return false;
    FILE_ATTRIBUTE_TAG_INFO tag = {};
    if (!::GetFileInformationByHandleEx(m_handle, FileAttributeTagInfo, &tag, sizeof(tag)))
        return false;
    return (tag.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) == 0;
#else
    return false;
#endif
}

bool InstallerHold::isAt(const QString &path) const
{
#ifdef Q_OS_WIN
    if (!m_handle)
        return false;
    std::wstring resolved(MAX_PATH, L'\0');
    DWORD length = ::GetFinalPathNameByHandleW(m_handle, resolved.data(), DWORD(resolved.size()),
                                               FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (length >= resolved.size()) {
        // Too small: the answer is the size needed, terminator included.
        resolved.assign(length, L'\0');
        length = ::GetFinalPathNameByHandleW(m_handle, resolved.data(), DWORD(resolved.size()),
                                             FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    }
    if (length == 0 || length >= resolved.size())
        return false;
    QString held = QString::fromWCharArray(resolved.data(), int(length));
    // \\?\C:\... on a drive, \\?\UNC\server\share\... on a share.
    static const QString kUnc = QStringLiteral("\\\\?\\UNC\\");
    static const QString kLocal = QStringLiteral("\\\\?\\");
    if (held.startsWith(kUnc, Qt::CaseInsensitive))
        held = QStringLiteral("\\\\") + held.mid(kUnc.size());
    else if (held.startsWith(kLocal))
        held = held.mid(kLocal.size());
    return QDir::fromNativeSeparators(held).compare(QDir::fromNativeSeparators(path), Qt::CaseInsensitive) == 0;
#else
    Q_UNUSED(path);
    return false;
#endif
}

QByteArray InstallerHold::read(qint64 maxBytes, bool *ok)
{
    *ok = false;
#ifdef Q_OS_WIN
    if (!m_handle)
        return {};
    // A slice is never more than this, however much is asked for.
    constexpr qint64 kMostPerRead = 16 * 1024 * 1024;
    const DWORD wanted = DWORD(qBound<qint64>(1, maxBytes, kMostPerRead));
    QByteArray bytes(qsizetype(wanted), Qt::Uninitialized);
    DWORD received = 0;
    if (!::ReadFile(m_handle, bytes.data(), wanted, &received, nullptr))
        return {};
    bytes.truncate(qsizetype(received));
    *ok = true;
    return bytes;
#else
    Q_UNUSED(maxBytes);
    return {};
#endif
}

void InstallerHold::release()
{
#ifdef Q_OS_WIN
    if (m_handle)
        ::CloseHandle(m_handle);
#endif
    m_handle = nullptr;
}

InstallerHasher::InstallerHasher(QObject *parent)
    : QObject(parent)
{
    m_timer.setInterval(0);
    connect(&m_timer, &QTimer::timeout, this, &InstallerHasher::readSlice);
}

bool InstallerHasher::start(InstallerHold *file, qint64 sliceBytes)
{
    cancel();
    m_sliceBytes = qMax<qint64>(1, sliceBytes);
    m_hash.reset();
    if (!file || !file->isHeld())
        return false;
    m_file = file;
    m_timer.start();
    return true;
}

void InstallerHasher::cancel()
{
    m_timer.stop();
    m_file = nullptr;
}

void InstallerHasher::readSlice()
{
    bool ok = false;
    const QByteArray slice = m_file ? m_file->read(m_sliceBytes, &ok) : QByteArray();
    if (!ok) {
        cancel();
        Q_EMIT finished(false, QString());
        return;
    }
    // Read on until a read finds nothing more.
    if (!slice.isEmpty()) {
        m_hash.addData(slice);
        return;
    }
    cancel();
    Q_EMIT finished(true, QString::fromLatin1(m_hash.result().toHex()));
}

} // namespace mr
