#pragma once

// ---------------------------------------------------------------------------
// This test binary may never start a real Collector.
//
// A default-constructed CollectorProcess launches the Collector it resolves,
// with no --db, --pipe or --log-dir: against the *user's* production database,
// taking their per-user serve lease, outliving the test as an orphan. A release
// build resolves only the file beside the executable - and scripts/build.ps1
// copies a real Collector beside these tests for the integration suite - so a
// test names its stub explicitly (CollectorProcess(path, parent)) and never
// default-constructs a supervisor that could start.
//
// A build compiled with MR_DEV_COLLECTOR_DISCOVERY also walks up into the source
// tree; there MR_COLLECTOR_PATH is authoritative and exclusive (see
// CollectorProcess::resolveDefaultExecutable), and CTest points it at a path
// that cannot exist. This provides the same guarantee from inside the binary,
// for the .exe run directly.
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
