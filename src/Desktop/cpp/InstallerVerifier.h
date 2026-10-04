#pragma once

// ---------------------------------------------------------------------------
// 立即安装: the Desktop's own checks of the installer the Collector downloaded,
// made before the file is started (docs/privacy-boundary.md §8.6):
//
//   * where it is   - an absolute path whose directory is exactly
//                     <Collector data directory>/updates and whose name is
//                     exactly MentorRecorder-<version>-setup.exe;
//   * what it is    - a regular file, and neither it nor that directory is a
//                     link of any kind;
//   * what it holds - its SHA-256, computed now, equals the one reported.
//
// The bytes that are started must be the bytes that were hashed: the file is
// opened once (InstallerHold), others may only read it while it is held, the
// last two checks are made on what that handle refers to, the hash is read
// through it, and it stays held until the installer has started.
//
// The data directory is the one CollectorProcess derives the way the Collector
// does, never a path taken from the status. Nothing here starts anything, and
// nothing here reaches the network: the file is only read.
// ---------------------------------------------------------------------------

#include <QByteArray>
#include <QCryptographicHash>
#include <QObject>
#include <QString>
#include <QTimer>

namespace mr {
namespace installer {

/// MentorRecorder-<version>-setup.exe, or an empty string when \a version is
/// not X.Y.Z - the only shape $defs/UpdateDownload.version may take.
QString fileName(const QString &version);

/// True for exactly 64 lower-case hex digits ($defs/UpdateDownload.sha256).
bool isSha256(const QString &value);

/// True when \a path names the installer of \a version inside
/// <\a dataDirectory>/updates: absolute, already in its clean form (no "." or
/// ".." segment, no doubled separator), its directory that folder (letter case
/// aside, as Windows compares paths) and its file name exactly fileName().
bool isInUpdatesDirectory(const QString &path, const QString &dataDirectory,
                          const QString &version);

/// True when \a path is a regular file, and neither it nor the directory that
/// holds it is a symbolic link, a junction, a shortcut or any other reparse
/// point: what is started must be the file the Collector wrote, not something
/// a link leads to.
bool isRegularFile(const QString &path);

} // namespace installer

/// The installer, opened once for reading while others may only read it too:
/// as long as it is held no process can write to it, rename it or delete it,
/// and Windows renames no folder above a file that is open, so its path keeps
/// naming these very bytes. Starting it is reading it - the loader opens an
/// image for reading and execution while sharing read - so a held file can
/// still be started. Released by release() or when the object goes.
class InstallerHold final
{
public:
    InstallerHold() = default;
    ~InstallerHold();
    InstallerHold(const InstallerHold &) = delete;
    InstallerHold &operator=(const InstallerHold &) = delete;

    /// Holds the file at \a path, letting go of whatever was held before. A
    /// link in its last segment is opened as the link, never followed. False
    /// when it cannot be held that way - missing, a folder, or open elsewhere
    /// for writing or deleting - and nothing is held then.
    bool open(const QString &path);
    bool isHeld() const { return m_handle != nullptr; }
    /// What is held is a regular file on a disk: not a folder, and not a
    /// symbolic link, junction or any other reparse point.
    bool isRegularFile() const;
    /// What is held is the file at exactly \a path (letter case aside, as
    /// Windows compares), reached through real folders only: the path the
    /// system resolves for this handle, every link followed, is \a path.
    bool isAt(const QString &path) const;
    /// Reads on from where the last read ended, at most \a maxBytes. Empty at
    /// the end of the file; \a ok is false when the read failed.
    QByteArray read(qint64 maxBytes, bool *ok);
    /// Lets go of the file; nothing is held afterwards.
    void release();

private:
    Qt::HANDLE m_handle = nullptr;
};

/// SHA-256 of one held file, read a slice per turn of the event loop so the
/// window stays responsive while a 60 MB installer is read. It reads through
/// the hold and never releases it: that is for the hold's owner, who decides
/// whether what was hashed is started. Deleting the object, or cancel(), stops
/// it; the application's own exit does as well, since nothing runs once the
/// event loop has returned.
class InstallerHasher final : public QObject
{
    Q_OBJECT

public:
    /// What one turn reads when nobody says otherwise.
    static constexpr qint64 kDefaultSliceBytes = 1024 * 1024;

    explicit InstallerHasher(QObject *parent = nullptr);

    /// Starts reading \a file from where it stands, \a sliceBytes per turn.
    /// False when nothing is held, and finished() then never comes. \a file
    /// must outlive the run.
    bool start(InstallerHold *file, qint64 sliceBytes = kDefaultSliceBytes);
    /// Stops the run in progress; its finished() never comes.
    void cancel();
    bool isRunning() const { return m_timer.isActive(); }

Q_SIGNALS:
    /// Once per run. \a ok is false when a read failed part way; \a sha256 is
    /// the digest in lower-case hex, empty then.
    void finished(bool ok, const QString &sha256);

private:
    void readSlice();

    InstallerHold *m_file = nullptr;
    QCryptographicHash m_hash{QCryptographicHash::Sha256};
    QTimer m_timer;
    qint64 m_sliceBytes = kDefaultSliceBytes;
};

} // namespace mr
