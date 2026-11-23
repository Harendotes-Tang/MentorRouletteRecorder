// Stand-ins for the Collector a CollectorProcess supervises, and the serve.pid file through
// which the Collector names the holder of the per-user serve lease. The stubs are one-line
// batch files: no system binary exits with a chosen code on demand.
#pragma once

#include "BuildFlavour.h"
#include "CollectorProcess.h"
#include "PipeName.h"

#include <QByteArray>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileDevice>
#include <QString>

namespace mrtest {

/// Writes a batch file \a fileName into \a directory: "@echo off", then \a body, CRLF line
/// endings throughout. Returns its path, or an empty string when it could not be written.
inline QString writeStubScript(const QString &directory, const QString &fileName, const QByteArray &body)
{
    QString path = QDir(directory).absoluteFilePath(fileName);
    QFile script(path);
    if (!script.open(QIODevice::WriteOnly))
        return {};
    script.write(QByteArrayLiteral("@echo off\r\n") + body);
    script.close();
    return path;
}

/// A program that exits with the Collector's ERR_ALREADY_RUNNING code.
inline QString alreadyRunningProgram(const QString &directory)
{
#ifdef Q_OS_WIN
    return writeStubScript(directory, QStringLiteral("already-running.cmd"),
                           QByteArrayLiteral("exit /b ")
                               + QByteArray::number(mr::CollectorProcess::kAlreadyRunningExitCode)
                               + QByteArrayLiteral("\r\n"));
#else
    Q_UNUSED(directory)
    return QString();
#endif
}

/// A Collector stand-in that refuses to start the way the Collector does: one
/// line on standard error, then \a exitCode. Every launch also appends a line
/// to runs.txt beside it, so a test can tell whether anything launched it again.
inline QString refusingProgram(const QString &directory, int exitCode, const QByteArray &stderrLine)
{
#ifdef Q_OS_WIN
    return writeStubScript(directory, QStringLiteral("refusing.cmd"),
                           QByteArrayLiteral("echo run>>\"%~dp0runs.txt\"\r\n>&2 echo ")
                               + stderrLine + QByteArrayLiteral("\r\nexit /b ")
                               + QByteArray::number(exitCode) + QByteArrayLiteral("\r\n"));
#else
    Q_UNUSED(directory)
    Q_UNUSED(exitCode)
    Q_UNUSED(stderrLine)
    return QString();
#endif
}

/// How many times a refusingProgram() stub in \a directory was launched.
inline int launchesIn(const QString &directory)
{
    QFile runs(QDir(directory).absoluteFilePath(QStringLiteral("runs.txt")));
    if (!runs.open(QIODevice::ReadOnly | QIODevice::Text))
        return 0;
    return static_cast<int>(runs.readAll().count('\n'));
}

/// The pid file the child of this build writes: serve.pid, or the development
/// pipe's own file. Derived here on its own rather than read from
/// CollectorProcess::servePidFileName(), which BuildFlavourTests pins.
inline QString servePidName()
{
    const QString pipe = mr::ipc::currentUserPipeName();
    return mr::build::isDevelopmentBuild() && !pipe.isEmpty()
        ? QStringLiteral("serve.%1.pid").arg(pipe) : QStringLiteral("serve.pid");
}

/// Write \a pid into this build's serve.pid file inside \a directory, the way
/// the Collector records the holder of the per-user serve lease.
inline bool writeServePid(const QString &directory, qint64 pid)
{
    QFile file(QDir(directory).absoluteFilePath(servePidName()));
    if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
        return false;
    file.write(QByteArray::number(pid));
    file.close();
    return true;
}

/// Backdate serve.pid so the takeover stops reading it as "written seconds
/// ago", which is the one state in which nothing may be ended.
inline bool ageServePid(const QString &directory, qint64 milliseconds)
{
    QFile file(QDir(directory).absoluteFilePath(servePidName()));
    if (!file.open(QIODevice::ReadWrite))
        return false;
    const bool ok = file.setFileTime(QDateTime::currentDateTime().addMSecs(-milliseconds),
                                     QFileDevice::FileModificationTime);
    file.close();
    return ok;
}

} // namespace mrtest
