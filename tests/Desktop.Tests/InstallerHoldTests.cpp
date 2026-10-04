// ---------------------------------------------------------------------------
// tst_installerhold - the installer held from its checksum to its start
// (InstallerVerifier.h, docs/privacy-boundary.md §8.6), on real files.
//
// What it pins, without the shell and without elevation:
//   * a program held exactly as 立即安装 holds the installer still starts - a
//     copy of whoami.exe runs to exit code 0 - while nobody can open it for
//     writing, rename it, delete it or rename the folder it is in, during the
//     run and after it; once released, it can be deleted;
//   * a file somebody already has open for writing cannot be held;
//   * the checks are made on what is held: a folder cannot be held, a file
//     reached through a junction is not the file at the path that names it,
//     and a symbolic link is held as the link and is no regular file;
//   * what is read through the hold is the file's content.
//
// What it does not show: the shell's start with elevation (ShellExecute, the
// consent prompt and the service that starts the elevated process, each of
// which opens the file too) and the installer reading itself while it is
// still held. Those need a real machine.
// ---------------------------------------------------------------------------

#include "InstallerVerifier.h"

#include <QByteArray>
#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QProcess>
#include <QTemporaryDir>
#include <QTest>

// Last: it brings <windows.h>.
#include "InstallerTampering.h"

namespace {

constexpr auto kInstallerName = "MentorRecorder-9.9.9-setup.exe";

bool writeFile(const QString &path, const QByteArray &bytes)
{
    QDir().mkpath(QFileInfo(path).absolutePath());
    QFile file(path);
    return file.open(QIODevice::WriteOnly | QIODevice::Truncate) && file.write(bytes) == bytes.size();
}

/// A directory junction at \a link to \a target; needs no privilege.
bool makeJunction(const QString &link, const QString &target)
{
    return QProcess::execute(QStringLiteral("cmd.exe"),
                             {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"),
                              QDir::toNativeSeparators(link), QDir::toNativeSeparators(target)})
           == 0;
}

} // namespace

class InstallerHoldTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void aHeldProgramStillStartsButCannotBeReplaced()
    {
        const QString source =
            QDir(qEnvironmentVariable("SystemRoot", QStringLiteral("C:/Windows")))
                .filePath(QStringLiteral("System32/whoami.exe"));
        if (!QFileInfo::exists(source))
            QSKIP("this machine has no whoami.exe to start");
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        const QString program = directory.filePath(QStringLiteral("updates/") + QLatin1String(kInstallerName));
        QVERIFY(QDir().mkpath(QFileInfo(program).absolutePath()));
        QVERIFY(QFile::copy(source, program));

        mr::InstallerHold hold;
        QVERIFY(hold.open(program));
        QVERIFY(hold.isRegularFile());
        QVERIFY(hold.isAt(program));

        QProcess process;
        process.start(program, {});
        QVERIFY2(process.waitForStarted(10000), qPrintable(process.errorString()));
        // Beside the running program...
        const mrtest::Tampering whileRunning = mrtest::tryToReplace(program);
        QVERIFY(process.waitForFinished(10000));
        QCOMPARE(process.exitStatus(), QProcess::NormalExit);
        QCOMPARE(process.exitCode(), 0);
        QVERIFY2(!whileRunning.any(), qPrintable(whileRunning.describe()));
        // ...and once it has ended, when only the hold stands in the way.
        const mrtest::Tampering afterwards = mrtest::tryToReplace(program);
        QVERIFY2(!afterwards.any(), qPrintable(afterwards.describe()));

        hold.release();
        QVERIFY(!hold.isHeld());
        QVERIFY(QFile::remove(program));
    }

    void aFileSomebodyIsWritingCannotBeHeld()
    {
        QTemporaryDir directory;
        const QString path = directory.filePath(QLatin1String(kInstallerName));
        QVERIFY(writeFile(path, QByteArrayLiteral("installer")));
        {
            QFile writer(path);
            QVERIFY(writer.open(QIODevice::ReadWrite));
            mr::InstallerHold hold;
            QVERIFY(!hold.open(path));
            QVERIFY(!hold.isHeld());
        }
        mr::InstallerHold hold;
        QVERIFY(hold.open(path));
    }

    void aFolderCannotBeHeld()
    {
        QTemporaryDir directory;
        const QString path = directory.filePath(QLatin1String(kInstallerName));
        QVERIFY(QDir().mkpath(path));
        mr::InstallerHold hold;
        QVERIFY(!hold.open(path));
        QVERIFY(!hold.isRegularFile());
        QVERIFY(!hold.isAt(path));
        QVERIFY(!hold.open(directory.filePath(QStringLiteral("missing.exe"))));
    }

    void aFileReachedThroughAJunctionIsNotTheFileAtThatPath()
    {
        QTemporaryDir data;
        QTemporaryDir elsewhere;
        const QString real = elsewhere.filePath(QLatin1String(kInstallerName));
        QVERIFY(writeFile(real, QByteArrayLiteral("installer")));
        const QString link = data.filePath(QStringLiteral("updates"));
        if (!makeJunction(link, elsewhere.path()))
            QSKIP("this machine cannot create a directory junction");
        const QString reported = link + QLatin1Char('/') + QLatin1String(kInstallerName);

        mr::InstallerHold hold;
        // The system follows a junction in the folder part; what is held is
        // then judged by where it really is.
        QVERIFY(hold.open(reported));
        QVERIFY(hold.isRegularFile());
        QVERIFY(!hold.isAt(reported));
        QVERIFY(hold.isAt(real));
        hold.release();
        QDir().rmdir(link);
    }

    void aSymbolicLinkIsHeldAsTheLinkAndIsNoRegularFile()
    {
        QTemporaryDir directory;
        const QString target = directory.filePath(QStringLiteral("elsewhere.exe"));
        QVERIFY(writeFile(target, QByteArrayLiteral("installer")));
        const QString link = directory.filePath(QLatin1String(kInstallerName));
        // A file symbolic link needs a privilege (or developer mode) that a
        // test machine may not grant.
        const int made = QProcess::execute(QStringLiteral("cmd.exe"),
            {QStringLiteral("/c"), QStringLiteral("mklink"), QDir::toNativeSeparators(link),
             QDir::toNativeSeparators(target)});
        if (made != 0 || !QFileInfo(link).isSymLink())
            QSKIP("this machine cannot create a file symbolic link");
        mr::InstallerHold hold;
        QVERIFY(hold.open(link));
        QVERIFY(!hold.isRegularFile());
    }

    void whatIsReadThroughTheHoldIsTheFile()
    {
        QTemporaryDir directory;
        const QString path = directory.filePath(QLatin1String(kInstallerName));
        QByteArray bytes;
        for (int index = 0; index < 10000; ++index)
            bytes.append(char('a' + index % 26));
        QVERIFY(writeFile(path, bytes));

        mr::InstallerHold hold;
        QVERIFY(hold.open(path));
        QByteArray read;
        bool ok = false;
        for (QByteArray slice = hold.read(4096, &ok); ok && !slice.isEmpty(); slice = hold.read(4096, &ok))
            read.append(slice);
        QVERIFY(ok);
        QCOMPARE(read, bytes);

        // The hasher reads the same way, from where the hold stands.
        QVERIFY(hold.open(path));
        mr::InstallerHasher hasher;
        QString digest;
        bool hashed = false;
        QObject::connect(&hasher, &mr::InstallerHasher::finished, &hasher,
                         [&digest, &hashed](bool success, const QString &sha256) {
            hashed = success;
            digest = sha256;
        });
        QVERIFY(hasher.start(&hold, 1000));
        QTRY_VERIFY(hashed);
        QCOMPARE(digest, QString::fromLatin1(QCryptographicHash::hash(bytes, QCryptographicHash::Sha256).toHex()));
        // The hash does not let go of the file: that is for its owner.
        QVERIFY(hold.isHeld());
        QVERIFY(!mrtest::tryToReplace(path).any());

        hold.release();
        QVERIFY(!hold.read(16, &ok).size());
        QVERIFY(!ok);
        QVERIFY(!hasher.start(&hold));
    }
};

QTEST_GUILESS_MAIN(InstallerHoldTests)

#include "InstallerHoldTests.moc"
