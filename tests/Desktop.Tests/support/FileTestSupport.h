#pragma once

#include <QByteArray>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QProcess>
#include <QString>

namespace mrtest {

/// Writes \a bytes to \a path, creating its folder first; true when every byte was written.
inline bool writeFile(const QString &path, const QByteArray &bytes)
{
    QDir().mkpath(QFileInfo(path).absolutePath());
    QFile file(path);
    return file.open(QIODevice::WriteOnly | QIODevice::Truncate) && file.write(bytes) == bytes.size();
}

/// A directory junction at \a link to \a target (cmd's mklink /J; needs no privilege, unlike
/// a symbolic link). False when mklink did not finish within ten seconds or did not succeed,
/// e.g. off Windows.
inline bool makeJunction(const QString &link, const QString &target)
{
    QProcess mklink;
    mklink.start(QStringLiteral("cmd.exe"),
                 {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"),
                  QDir::toNativeSeparators(link), QDir::toNativeSeparators(target)});
    return mklink.waitForFinished(10000) && mklink.exitStatus() == QProcess::NormalExit
        && mklink.exitCode() == 0;
}

} // namespace mrtest
