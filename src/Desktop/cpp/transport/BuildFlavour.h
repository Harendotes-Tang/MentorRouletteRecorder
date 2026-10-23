#pragma once

// ---------------------------------------------------------------------------
// Development build versus distribution build.
//
// Every build of the Desktop is a development build unless CMake was configured
// with -DMR_DEV_BUILD=OFF, which only scripts/package.ps1 does (through
// scripts/build.ps1 -Distribution). A development build keeps everything it
// touches apart from an installed copy of this software on the same account:
//
//   * the Collector's data root defaults to %LOCALAPPDATA%\MentorRecorder-dev
//     and is handed to the child through MR_DATA_DIR, so its database, logs,
//     backups, calibration and profiles all land there;
//   * the Desktop's own desktop.ini lives in that folder as well;
//   * the per-user pipe carries a ".dev" segment, and with it the serve lease,
//     the stop event and the single-instance key, so the two Desktops never
//     meet, neither can connect to the other's Collector, and both can run at
//     the same time;
//   * the version shown in the title bar and on the 关于 page ends in "dev".
//
// The reason is a database that only moves forward (migrations/README.md): a
// build from an IDE or from build\ that opened the installed copy's database
// would migrate it to a schema the installed copy refuses to start on. Running
// one must never be able to do that (docs/architecture.md section 2.3,
// docs/build-and-package.md section 3.7).
//
// Every rule here takes the flavour as a parameter so that one test binary
// covers both; thisBuild() is what the rest of the Desktop passes.
// ---------------------------------------------------------------------------

#include <QString>

namespace mr::build {

enum class Flavour {
    /// The packaged software: %LOCALAPPDATA%\MentorRecorder and the per-user pipe
    /// the Collector derives itself.
    Distribution,
    /// Anything else: %LOCALAPPDATA%\MentorRecorder-dev, a ".dev" pipe and "dev"
    /// beside the version on screen.
    Development,
};

/// The flavour this binary was compiled as (CMake option MR_DEV_BUILD).
constexpr Flavour thisBuild()
{
#ifdef MR_DEV_BUILD
    return Flavour::Development;
#else
    return Flavour::Distribution;
#endif
}

constexpr bool isDevelopmentBuild()
{
    return thisBuild() == Flavour::Development;
}

/// Folder under %LOCALAPPDATA% that holds a build of \a flavour's data: the
/// Collector's data root when MR_DATA_DIR is not set, and the Desktop's
/// desktop.ini always. "MentorRecorder" or "MentorRecorder-dev".
QString dataFolderName(Flavour flavour);
/// dataFolderName(thisBuild()).
QString dataFolderName();

/// The version as the title bar and the 关于 page show it: \a version itself
/// for a distribution build, "<version> dev" for a development build. Only the
/// label changes; the version the update check and the release notes compare
/// stays MR_APP_VERSION.
QString versionLabel(const QString &version, Flavour flavour);
/// versionLabel(MR_APP_VERSION, thisBuild()).
QString versionLabel();

/// A NUL-terminated narrow string naming this binary's flavour:
/// "MR_BUILD_FLAVOUR=development" or "MR_BUILD_FLAVOUR=distribution".
///
/// scripts/package.ps1 (Assert-DesktopExecutable) looks for exactly these bytes
/// in the executable: the development stamp fails the package, and a binary
/// without the distribution stamp is not one it knows. main() logs the stamp
/// at start-up, which is what keeps it in the binary.
const char *flavourStamp();

} // namespace mr::build
