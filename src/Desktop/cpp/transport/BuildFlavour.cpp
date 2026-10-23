#include "BuildFlavour.h"

#include <QCoreApplication>

namespace mr::build {

namespace {

// Narrow and NUL-terminated on purpose: scripts/package.ps1 scans the executable
// for exactly these bytes. Chosen under the same #ifdef as thisBuild(), so the
// stamp can never disagree with the behaviour.
#ifdef MR_DEV_BUILD
constexpr char kFlavourStamp[] = "MR_BUILD_FLAVOUR=development";
#else
constexpr char kFlavourStamp[] = "MR_BUILD_FLAVOUR=distribution";
#endif

} // namespace

QString dataFolderName(Flavour flavour)
{
    return flavour == Flavour::Development ? QStringLiteral("MentorRecorder-dev")
                                           : QStringLiteral("MentorRecorder");
}

QString dataFolderName()
{
    return dataFolderName(thisBuild());
}

QString versionLabel(const QString &version, Flavour flavour)
{
    return flavour == Flavour::Development ? version + QStringLiteral(" dev") : version;
}

QString versionLabel()
{
#ifdef MR_APP_VERSION
    return versionLabel(QStringLiteral(MR_APP_VERSION), thisBuild());
#else
    return versionLabel(QCoreApplication::applicationVersion(), thisBuild());
#endif
}

const char *flavourStamp()
{
    return kFlavourStamp;
}

} // namespace mr::build
