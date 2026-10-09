#pragma once

// ---------------------------------------------------------------------------
// 备注图片 / note images.
//
// Image files a player attaches to a run's 备注. They are plain files under the
// software's own install directory, one folder per run:
//
//     <install dir>\note-images\<run_id>\<yyyyMMdd-HHmmss-zzz>-<nn>.<ext>
//
// The folder is the whole record: the Collector, the database, the IPC contract,
// exports and backups know nothing about these files, and nothing here ever
// leaves this machine. The location is deliberately the install directory and
// not %LOCALAPPDATA%: the installer proposes D:\MentorRecorder precisely so the
// software's bulk stays off the system drive, and a folder of screenshots is
// the bulk. MR_NOTE_IMAGE_DIR moves the root, for tests and for a player who
// wants it elsewhere.
//
// Everything is synchronous file I/O on the GUI thread: an image is copied
// once, when the player saves the dialog, and a listing is a directory read.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QString>
#include <QStringList>
#include <QVariantList>
#include <QVariantMap>

#include <functional>

namespace mr {

class NoteImageStore : public QObject
{
    Q_OBJECT
    /// The root folder, with forward slashes (QDir form).
    Q_PROPERTY(QString rootDirectory READ rootDirectory CONSTANT)
    /// The root folder the way Windows shows it, for the hint under the strip.
    Q_PROPERTY(QString rootDirectoryNative READ rootDirectoryNative CONSTANT)
    Q_PROPERTY(int maxImagesPerRun READ maxImagesPerRun CONSTANT)
    /// Enables an image-only Paste shortcut; text-only clipboards keep normal text paste.
    Q_PROPERTY(bool clipboardHasImage READ clipboardHasImage NOTIFY clipboardChanged)

public:
    /// Folder under the install directory.
    static constexpr const char *kFolderName = "note-images";
    /// Environment variable that moves the root somewhere else.
    static constexpr const char *kRootVariable = "MR_NOTE_IMAGE_DIR";
    /// One image; a screenshot is a few MB, anything past this is not a note.
    static constexpr qint64 kMaxImageBytes = 20 * 1024 * 1024;
    /// Images per run. A note is a handful of screenshots, not an album.
    static constexpr int kMaxImagesPerRun = 20;
    /// Pixels per image, read from the header before anything is decoded. A 4K
    /// screenshot is 8.3 million; an 8K one 33 million. A small file that
    /// declares a giant canvas is a decompression bomb, not a screenshot, and is
    /// refused here rather than left to whatever the image library's own cap is.
    static constexpr qint64 kMaxImagePixels = 50 * 1000 * 1000;

    /// Copies one file; QFile::copy in production. A test replaces it to make a
    /// copy fail after an earlier one in the same batch succeeded.
    using CopyFunction = std::function<bool(const QString &source, const QString &target)>;

    /// The production store: MR_NOTE_IMAGE_DIR, else <applicationDirPath>/note-images.
    explicit NoteImageStore(QObject *parent = nullptr);
    /// A store rooted at \a rootDirectory, for tests.
    explicit NoteImageStore(const QString &rootDirectory, QObject *parent = nullptr);

    /// The root for one process: \a overrideDirectory when it is not blank,
    /// otherwise the note-images folder beside the executable in \a applicationDir.
    static QString resolveRoot(const QString &overrideDirectory, const QString &applicationDir);
    /// True for the canonical lower-case UUID the Collector issues as a run id.
    /// A run id names a folder, so nothing that could walk the tree is accepted.
    static bool isValidRunId(const QString &runId);
    /// File suffixes an attachment may carry, lower case, without the dot.
    static QStringList supportedSuffixes();
    /// The file-chooser filter: 图片文件 (*.png *.jpg ...).
    static QString nameFilter();

    QString rootDirectory() const { return m_root; }
    QString rootDirectoryNative() const;
    int maxImagesPerRun() const { return kMaxImagesPerRun; }
    /// <root>/<run_id>; empty for an invalid run id.
    QString runDirectory(const QString &runId) const;

    /// The images of one run, oldest first. Each row carries path, url, name
    /// and byte_count. An unknown run, or a run without a folder, is an empty
    /// list, never an error.
    Q_INVOKABLE QVariantList imagesFor(const QString &runId) const;
    /// Looks at a file the player picked without copying it: {ok, error, path,
    /// url, name, byte_count, format}. A refusal names the reason in Chinese.
    Q_INVOKABLE QVariantMap inspect(const QString &sourcePath) const;
    /// Opens the system file chooser. Empty when the player cancelled.
    Q_INVOKABLE QString pickImage();
    /// Applies one dialog's staged changes for \a runId: copies every source
    /// file in \a adds into the run's folder, then deletes every path in
    /// \a removes that lies inside it. Every source is checked before anything
    /// is done, so a bad file fails the whole call with nothing applied, removals
    /// included. Once copying starts it is all-or-nothing (a failure removes the
    /// copies this call made); a removal that fails is reported and the other
    /// removals continue. Returns {ok, error, added (source paths), removed}.
    Q_INVOKABLE QVariantMap commit(const QString &runId, const QStringList &adds,
                                   const QStringList &removes);
    /// The one-step forms for places without a staging step (the detail panel,
    /// the 心得 dialog): the file is copied, or deleted, right away.
    /// {ok, error, added, removed} as commit() returns them.
    Q_INVOKABLE QVariantMap addFile(const QString &runId, const QString &sourcePath);
    /// pickImage() followed by addFile(); ok with nothing added when cancelled.
    Q_INVOKABLE QVariantMap addPicked(const QString &runId);
    /// Copies the current clipboard image immediately, exactly as addPicked().
    /// Encoded clipboard data is bounded before decode; a temporary PNG passes
    /// through commit()'s existing size/count/copy checks and is then removed.
    /// A missing or invalid image returns {ok:false,error,added:[],removed:[]}.
    Q_INVOKABLE QVariantMap addClipboard(const QString &runId);
    bool clipboardHasImage() const;
    Q_INVOKABLE QVariantMap removeOne(const QString &runId, const QString &path);
    /// file:/// URL for an Image source.
    Q_INVOKABLE static QString urlFor(const QString &path);

    /// Replaces the file copy used by commit(); for tests only.
    void setCopyFunctionForTesting(CopyFunction copy);

Q_SIGNALS:
    /// The folder of \a runId changed through commit().
    void imagesChanged(const QString &runId);
    /// The system clipboard changed; no data is decoded by this notification.
    void clipboardChanged();

private:
    struct Inspection {
        bool ok = false;
        QString error;
        QString absolutePath;
        QString suffix;
        qint64 byteCount = 0;
    };

    static Inspection inspectFile(const QString &sourcePath);
    static QString suffixForFormat(const QByteArray &format);
    /// Deletes the run folder when nothing is left in it, so a run whose images
    /// were all removed leaves no empty folder behind.
    static void pruneEmptyFolder(const QString &directory);
    /// True when the run folder at \a directory, if it exists, is a real folder
    /// under the root as the file system resolves it. A junction or symbolic
    /// link planted there would make every write and delete land elsewhere
    /// while the path still looked right; nothing is written to or deleted
    /// from such a folder.
    bool runFolderIsGenuine(const QString &directory) const;

    QString m_root;
    CopyFunction m_copy;
};

} // namespace mr
