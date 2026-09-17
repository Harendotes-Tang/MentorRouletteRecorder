#pragma once

// ---------------------------------------------------------------------------
// This test binary may never start a real Collector.
//
// AppController supervises a Collector child whenever its backend calls itself
// "ipc" and is not connected, and CollectorProcess resolves that executable by
// walking up to six directories out of the build tree - which, in a source
// checkout, finds the real one. Such a child gets no --db, --pipe or --log-dir,
// so it runs against the *user's* production database, takes their per-user
// serve lease, and outlives the test as an orphan.
//
// CTest sets MR_COLLECTOR_PATH for every Desktop test to a path that cannot
// exist, which disables the launch (the variable is authoritative and
// exclusive; see CollectorProcess::resolveDefaultExecutable). This provides the
// same guarantee from inside the binary, for the .exe run directly.
//
// Call it as the first statement of main().
// ---------------------------------------------------------------------------

#include <QByteArray>
#include <QDir>
#include <QFile>
#include <QCoreApplication>
#include <QtGlobal>

namespace mrtest {

/// Point MR_COLLECTOR_PATH at a path that cannot exist, unless the caller has
/// already set it (CTest, or a harness that launched its own Collector), and
/// move the Collector data directory somewhere disposable.
///
/// The data directory matters because the serve-lease takeover reads serve.pid
/// out of it and ends the process it names; against the real
/// %LOCALAPPDATA%\MentorRecorder that would be the developer's own Collector.
inline void disableCollectorLaunch()
{
    if (qEnvironmentVariableIsEmpty("MR_COLLECTOR_PATH")) {
        qputenv("MR_COLLECTOR_PATH",
                QByteArrayLiteral("no-such-collector/MentorRecorder.Collector.exe"));
    }
    if (qEnvironmentVariableIsEmpty("MR_DATA_DIR")) {
        const QString scratch =
            QDir::temp().absoluteFilePath(QStringLiteral("MentorRecorder-tests-no-data"));
        qputenv("MR_DATA_DIR", QFile::encodeName(scratch));
    }
}

} // namespace mrtest
