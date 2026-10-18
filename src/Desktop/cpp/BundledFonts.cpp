#include "BundledFonts.h"

#include <QDebug>
#include <QFontDatabase>
#include <QString>

namespace mr {

bool registerBundledFonts()
{
    bool loaded = true;
    for (const char *path : {":/resources/fonts/Cinzel-Regular.ttf",
                             ":/resources/fonts/Cinzel-Bold.ttf",
                             ":/resources/fonts/IBMPlexMono-Medium.ttf",
                             ":/resources/fonts/IBMPlexMono-SemiBold.ttf",
                             ":/resources/fonts/NotoSerifSC-Regular.otf",
                             ":/resources/fonts/NotoSerifSC-Bold.otf",
                             ":/resources/fonts/NotoSansSC-Regular.otf"}) {
        if (QFontDatabase::addApplicationFont(QString::fromLatin1(path)) < 0) {
            qWarning("Cannot register bundled font %s", path);
            loaded = false;
        }
    }
    return loaded;
}

} // namespace mr
