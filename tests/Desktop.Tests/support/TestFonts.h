// Fonts for suites that read or measure rendered text. The offscreen platform lists no system
// fonts, so a suite that captures or measures a window loads the fonts the application would
// find itself. Call these after the application object exists.
#pragma once

#include <QDir>
#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QString>
#include <QStringList>
#include <QtGlobal>

#include <initializer_list>

namespace mrtest {

/// Loads each of \a files (relative to %WINDIR%, e.g. "Fonts/msyh.ttc") and makes the
/// families they hold the application font when any of them loaded. Returns those families,
/// in load order without repeats; empty off Windows, where nothing is loaded.
inline QStringList useWindowsFonts(std::initializer_list<const char *> files)
{
    QStringList families;
#ifdef Q_OS_WIN
    const QString windowsDir = qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows"));
    for (const char *file : files) {
        const int id = QFontDatabase::addApplicationFont(QDir(windowsDir).filePath(QString::fromLatin1(file)));
        for (const QString &family : QFontDatabase::applicationFontFamilies(id)) {
            if (!families.contains(family))
                families.append(family);
        }
    }
    if (!families.isEmpty()) {
        QFont font;
        font.setFamilies(families);
        QGuiApplication::setFont(font);
    }
#else
    Q_UNUSED(files)
#endif
    return families;
}

/// main.cpp's screenshot font initialization: offscreen defaults may otherwise choose a
/// condensed fallback and conceal real layout overflow.
inline void useScreenshotFontStack()
{
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/msyh.ttc"));
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/simhei.ttf"));
    QFontDatabase::addApplicationFont(QStringLiteral("C:/Windows/Fonts/consola.ttf"));
    QFontDatabase::addApplicationFont(QString::fromUtf8(MR_DESKTOP_QML_DIR) + QStringLiteral("/../resources/fonts/IBMPlexMono-SemiBold.ttf"));
    QFont font;
    font.setFamilies({QStringLiteral("Microsoft YaHei UI"), QStringLiteral("Microsoft YaHei"), QStringLiteral("Segoe UI")});
    QGuiApplication::setFont(font);
}

} // namespace mrtest
