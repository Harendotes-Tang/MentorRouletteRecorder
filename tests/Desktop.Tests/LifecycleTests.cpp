// ---------------------------------------------------------------------------
// Collector supervision, IPC request deadlines and first-run disclosure
// persistence.
//
// None of these needs a Collector: the process supervisor is driven against a
// stub executable, the deadline policy is a pure function, and the disclosure
// store is an INI file inside QStandardPaths' test root.
//
// Test mode is enabled once, in main(), before anything runs. A QVERIFY that
// aborts a slot skips that slot's cleanup, so a per-slot toggle could leave it
// off and let the next QFile::remove(AppSettings::filePath()) delete the user's
// real %LOCALAPPDATA% settings file.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "AppSettings.h"
#include "CollectorProcess.h"
#include "IBackend.h"
#include "IpcClient.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QSettings>
#include <QSignalSpy>
#include <QStandardPaths>
#include <QPointer>
#include <QTemporaryDir>
#include <QTest>
#include <QTextStream>
#include <QTimer>

#ifdef Q_OS_WIN
// libstdc++ on MinGW defines NOMINMAX itself; see CollectorProcess.cpp.
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif

namespace {

/// A program that exists, starts, and exits immediately.
///
/// CollectorProcess always passes "--serve", so the stub has to be something
/// that refuses an unknown switch and quits rather than waiting for input;
/// hostname.exe prints a usage line and exits in well under a second. It stands
/// in for a Collector that dies right after launch.
QString shortLivedProgram()
{
#ifdef Q_OS_WIN
    const QString candidate =
        QDir(qEnvironmentVariable("WINDIR", QStringLiteral("C:/Windows")))
            .absoluteFilePath(QStringLiteral("System32/hostname.exe"));
    return QFileInfo::exists(candidate) ? candidate : QString();
#else
    return QStringLiteral("/bin/true");
#endif
}

/// A program that stays alive until it is killed, so a stop() that waits can be
/// timed. PAUSE blocks on the standard input QProcess gives it and nothing ever
/// writes to that pipe; it also ignores the WM_CLOSE terminate() sends, which
/// is the worst case stop() has to stay fast through.
QString longLivedProgram(const QString &directory)
{
#ifdef Q_OS_WIN
    const QString path = QDir(directory).absoluteFilePath(QStringLiteral("long-lived.cmd"));
    QFile script(path);
    if (!script.open(QIODevice::WriteOnly | QIODevice::Text))
        return QString();
    script.write(QByteArrayLiteral("@echo off\r\npause > nul\r\n"));
    script.close();
    return path;
#else
    Q_UNUSED(directory)
    return QStringLiteral("/bin/sleep");
#endif
}

/// A program that exits with the Collector's "the lease is held but the holder
/// is not answering" code.
QString unresponsiveHolderProgram(const QString &directory)
{
#ifdef Q_OS_WIN
    const QString path =
        QDir(directory).absoluteFilePath(QStringLiteral("unresponsive-holder.cmd"));
    QFile script(path);
    if (!script.open(QIODevice::WriteOnly | QIODevice::Text))
        return QString();
    script.write(QByteArrayLiteral("@echo off\r\nexit /b ")
                 + QByteArray::number(mr::CollectorProcess::kUnresponsiveHolderExitCode)
                 + QByteArrayLiteral("\r\n"));
    script.close();
    return path;
#else
    Q_UNUSED(directory)
    return QString();
#endif
}

/// A program that exits with the Collector's ERR_ALREADY_RUNNING code.
///
/// Written into \a directory as a one-line batch file: no system binary exits
/// with a chosen code on demand.
QString alreadyRunningProgram(const QString &directory)
{
#ifdef Q_OS_WIN
    const QString path =
        QDir(directory).absoluteFilePath(QStringLiteral("already-running.cmd"));
    QFile script(path);
    if (!script.open(QIODevice::WriteOnly | QIODevice::Text))
        return QString();
    script.write(QByteArrayLiteral("@echo off\r\nexit /b ")
                 + QByteArray::number(mr::CollectorProcess::kAlreadyRunningExitCode)
                 + QByteArrayLiteral("\r\n"));
    script.close();
    return path;
#else
    Q_UNUSED(directory)
    return QString();
#endif
}

/// Write \a pid into a serve.pid file inside \a directory, the way the
/// Collector records the holder of the per-user serve lease.
bool writeServePid(const QString &directory, qint64 pid)
{
    QFile file(QDir(directory).absoluteFilePath(QStringLiteral("serve.pid")));
    if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
        return false;
    file.write(QByteArray::number(pid));
    file.close();
    return true;
}

/// Backdate serve.pid so the takeover stops reading it as "written seconds
/// ago", which is the one state in which nothing may be ended.
bool ageServePid(const QString &directory, qint64 milliseconds)
{
    QFile file(QDir(directory).absoluteFilePath(QStringLiteral("serve.pid")));
    if (!file.open(QIODevice::ReadWrite))
        return false;
    const bool ok = file.setFileTime(QDateTime::currentDateTime().addMSecs(-milliseconds),
                                     QFileDevice::FileModificationTime);
    file.close();
    return ok;
}

/// One tasklist/taskkill call the takeover made.
struct ToolCall {
    QString tool;
    QStringList arguments;

    bool forced() const { return arguments.contains(QStringLiteral("/F")); }
};

/// A stop-event name no Collector is waiting on.
///
/// Every takeover sets the holder's stop event, and the shipping name is
/// derived from the current user's SID - i.e. it is exactly the event the
/// developer's own running Collector is waiting on. A test must never set that
/// one.
QString isolatedStopEventName()
{
    return QStringLiteral("Local\\mr-lifecycle-test-%1.stop")
        .arg(QCoreApplication::applicationPid());
}

/// A backend whose *child* fails every request still in flight when it is
/// destroyed - which is exactly the shape IpcBackend has: the IpcClient that
/// owns the pending table is a child object, and QObject deletes children from
/// inside ~QObject, after the backend's own destructor body has run.
class ClosingChannel : public QObject
{
public:
    using QObject::QObject;
    ~ClosingChannel() override
    {
        for (const QPointer<mr::BackendReply> &reply : std::as_const(pending)) {
            if (reply) {
                reply->fail(QStringLiteral("ERR_INTERNAL"),
                            QString::fromUtf8("与 Collector 的连接已关闭。"));
            }
        }
    }
    QList<QPointer<mr::BackendReply>> pending;
};

class ClosingBackend final : public mr::IBackend
{
public:
    ClosingBackend() : m_channel(new ClosingChannel(this)) {}
    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QStringLiteral("test"), messageType, this);
        m_channel->pending.append(reply);
        return reply;
    }

private:
    ClosingChannel *m_channel;
};

} // namespace

class LifecycleTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void settingsAreOnlyEverWrittenInsideTheTestRoot();
    void missingExecutableNeverLaunchesAnything();
    void theCollectorIsAlwaysToldOurProcessIdSoItCannotBeOrphaned();
    void unexpectedExitRestartsWithAGrowingBackoff();
    void aConnectedBackendStopsTheRestartLoop();
    void stopCancelsAPendingRestart();
    void anAlreadyRunningExitIsReusedWithoutARestartLoop();
    void stopDoesNotBlockTheGuiThreadForSeconds();
    void stopEventIsNamedAfterThePipe();
    void stopAsksThroughTheCollectorsStopEventBeforeKilling();
    void theServeLeasePidIsReadFromTheCollectorsDataDirectory();
    void systemToolsAreJudgedByExitCodeNotByLocalisedOutput();
    void aFreshServeLeaseHolderIsNeverEnded();
    void aHolderThatLeavesOnItsOwnIsNeverEnded();
    void aWedgedHolderIsForcedOnlyAsALastResort();
    void aHolderThatRefusesToBeEndedIsReportedRatherThanClaimedEnded();
    void anUnresponsiveHolderExitAsksBeforeItEndsAnything();
    void aVanishedHolderJustLetsOurOwnChildTakeTheLease();
    void aFailedRequestFromADestroyedBackendNeverReachesAController();

    void exportsAndBackupsGetTheirOwnDeadline();

    void disclosureAcknowledgementPersists();
    void raisingTheDisclosureVersionInvalidatesTheAcknowledgement();
};

/// Two slots below remove AppSettings::filePath(). This is the guard that the
/// path they remove is the throw-away one and never the user's own settings.
void LifecycleTests::settingsAreOnlyEverWrittenInsideTheTestRoot()
{
    QVERIFY(QStandardPaths::isTestModeEnabled());
    const QString settingsFile = QDir::fromNativeSeparators(mr::AppSettings::filePath());
    const QString testRoot = QDir::fromNativeSeparators(
        QStandardPaths::writableLocation(QStandardPaths::GenericConfigLocation));
    QVERIFY(!testRoot.isEmpty());
    QVERIFY2(settingsFile.startsWith(testRoot, Qt::CaseInsensitive),
             qPrintable(QStringLiteral("settings resolve to %1, outside the test root %2")
                            .arg(settingsFile, testRoot)));
}

void LifecycleTests::missingExecutableNeverLaunchesAnything()
{
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString absent =
        QDir(directory.path()).absoluteFilePath(QStringLiteral("no-such-collector.exe"));

    mr::CollectorProcess collector(absent, nullptr);
    QVERIFY(!collector.isAvailable());
    QCOMPARE(collector.stateToken(), QStringLiteral("missing"));

    collector.start();
    QVERIFY(!collector.isRunning());
    QCOMPARE(collector.stateToken(), QStringLiteral("missing"));
    QCOMPARE(collector.restartCount(), 0);
    // A missing binary is a layout problem, not something to retry forever.
    QVERIFY(!collector.restartPending());
}

void LifecycleTests::theCollectorIsAlwaysToldOurProcessIdSoItCannotBeOrphaned()
{
    // stop() covers the orderly exit. Nothing covers a hard kill, so the child
    // is handed our pid at launch and watches it: that is the only thing
    // standing between "Desktop force-killed" and "Collector still serving a
    // pipe nobody is connected to, holding the per-user serve lease".
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub =
        QDir(directory.path()).absoluteFilePath(QStringLiteral("collector-stub.exe"));

    mr::CollectorProcess collector(stub, nullptr);
    const QStringList arguments = collector.processArguments();

    QVERIFY(arguments.contains(QStringLiteral("--serve")));
    const int flag = arguments.indexOf(QStringLiteral("--parent-pid"));
    QVERIFY2(flag >= 0, "the collector must be launched with --parent-pid");
    QVERIFY(flag + 1 < arguments.size());

    bool numeric = false;
    const qint64 pid = arguments.at(flag + 1).toLongLong(&numeric);
    QVERIFY(numeric);
    QCOMPARE(pid, QCoreApplication::applicationPid());

    // The watchdog's pid-recycling guard needs our start time as well.
    const int startFlag = arguments.indexOf(QStringLiteral("--parent-start-time"));
    QVERIFY2(startFlag >= 0, "the collector must be told the parent's start time");
    QVERIFY(startFlag + 1 < arguments.size());
    bool ticksNumeric = false;
    const qulonglong ticks = arguments.at(startFlag + 1).toULongLong(&ticksNumeric);
    QVERIFY(ticksNumeric);
    // Later than 2020-01-01 as .NET UTC ticks and not in the future.
    QVERIFY(ticks > 637134336000000000ULL);
    const qulonglong nowTicks =
        qulonglong(QDateTime::currentDateTimeUtc().toMSecsSinceEpoch()) * 10000ULL
        + 621355968000000000ULL;
    QVERIFY(ticks <= nowTicks);
}

void LifecycleTests::unexpectedExitRestartsWithAGrowingBackoff()
{
    const QString program = shortLivedProgram();
    if (program.isEmpty())
        QSKIP("No short-lived stub program available on this machine.");

    mr::CollectorProcess collector(program, nullptr);
    QVERIFY(collector.isAvailable());

    QSignalSpy restarts(&collector, &mr::CollectorProcess::restarted);
    collector.start();

    // The stub exits on its own; the supervisor must schedule a restart rather
    // than leaving the Desktop permanently without a Collector.
    QTRY_VERIFY_WITH_TIMEOUT(collector.restartPending(), 10000);
    QCOMPARE(collector.stateToken(), QStringLiteral("exited"));
    QVERIFY(collector.statusText().contains(QStringLiteral("重启")));

    QTRY_VERIFY_WITH_TIMEOUT(restarts.count() >= 2, 15000);
    QCOMPARE(restarts.at(0).at(0).toInt(), 1);
    QCOMPARE(restarts.at(0).at(1).toInt(), mr::CollectorProcess::minBackoffMs());
    // Exponential, and never below the first delay.
    QCOMPARE(restarts.at(1).at(0).toInt(), 2);
    QVERIFY(restarts.at(1).at(1).toInt() > restarts.at(0).at(1).toInt());
    QVERIFY(restarts.at(1).at(1).toInt() <= mr::CollectorProcess::maxBackoffMs());

    collector.stop();
    QVERIFY(!collector.isRunning());
}

void LifecycleTests::aConnectedBackendStopsTheRestartLoop()
{
    const QString program = shortLivedProgram();
    if (program.isEmpty())
        QSKIP("No short-lived stub program available on this machine.");

    mr::CollectorProcess collector(program, nullptr);
    QSignalSpy restarts(&collector, &mr::CollectorProcess::restarted);

    // This is the "another instance already holds the serve lease" case: our
    // child exits immediately, but the pipe is served, so restarting would be
    // a pointless loop.
    collector.noteBackendConnected(true);
    collector.start();

    QTRY_COMPARE_WITH_TIMEOUT(collector.stateToken(), QStringLiteral("reused"), 10000);
    QTest::qWait(mr::CollectorProcess::minBackoffMs() * 3);
    QCOMPARE(restarts.count(), 0);
    QCOMPARE(collector.restartCount(), 0);
    QVERIFY(collector.statusText().contains(QStringLiteral("复用")));
}

void LifecycleTests::stopCancelsAPendingRestart()
{
    const QString program = shortLivedProgram();
    if (program.isEmpty())
        QSKIP("No short-lived stub program available on this machine.");

    mr::CollectorProcess collector(program, nullptr);
    QSignalSpy restarts(&collector, &mr::CollectorProcess::restarted);
    collector.start();

    QTRY_VERIFY_WITH_TIMEOUT(collector.restartPending(), 10000);
    collector.stop();

    QVERIFY(!collector.restartPending());
    QCOMPARE(collector.stateToken(), QStringLiteral("stopped"));
    QTest::qWait(mr::CollectorProcess::minBackoffMs() * 3);
    // A deliberate stop is never undone by the backoff.
    QCOMPARE(restarts.count(), 0);
}

/// The Collector has its own exit code for "another instance already holds the
/// serve lease", so this does not depend on our pipe having connected first. A
/// child that lost that race must not be read as a crash and restarted forever.
void LifecycleTests::anAlreadyRunningExitIsReusedWithoutARestartLoop()
{
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = alreadyRunningProgram(directory.path());
    if (stub.isEmpty())
        QSKIP("No way to fake an ERR_ALREADY_RUNNING exit on this machine.");

    mr::CollectorProcess collector(stub, nullptr);
    QSignalSpy restarts(&collector, &mr::CollectorProcess::restarted);
    // Deliberately NOT connected: the exit code alone has to carry the decision.
    collector.noteBackendConnected(false);
    collector.start();

    QTRY_COMPARE_WITH_TIMEOUT(collector.stateToken(), QStringLiteral("reused"), 10000);
    QVERIFY(collector.statusText().contains(QString::fromUtf8("\u590d\u7528")));
    QTest::qWait(mr::CollectorProcess::minBackoffMs() * 3);
    QCOMPARE(restarts.count(), 0);
    QVERIFY(!collector.restartPending());
}

/// stop() runs from the destructor, on the GUI thread, while the window is
/// already gone. Six seconds of waiting there is six seconds of an application
/// that has visibly stopped existing but has not quit.
void LifecycleTests::stopDoesNotBlockTheGuiThreadForSeconds()
{
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = longLivedProgram(directory.path());
    if (stub.isEmpty())
        QSKIP("No long-lived stub program available on this machine.");

    mr::CollectorProcess collector(stub, nullptr);
    collector.start();
    QTRY_VERIFY_WITH_TIMEOUT(collector.isRunning(), 10000);

    QElapsedTimer elapsed;
    elapsed.start();
    collector.stop();
    const qint64 blockedMs = elapsed.elapsed();

    QCOMPARE(collector.stateToken(), QStringLiteral("stopped"));
    QVERIFY(!collector.isRunning());
    QVERIFY2(blockedMs < 2000,
             qPrintable(QStringLiteral("stop() blocked the GUI thread for %1 ms")
                            .arg(blockedMs)));
}

/// The Collector derives the name from the pipe, so both ends have to spell it
/// the same way: "Local\\<pipe name>.stop".
void LifecycleTests::stopEventIsNamedAfterThePipe()
{
    QCOMPARE(mr::CollectorProcess::stopEventName(QStringLiteral("MentorRecorder.abc.v1")),
             QStringLiteral("Local\\MentorRecorder.abc.v1.stop"));
    QVERIFY(mr::CollectorProcess::stopEventName(QString()).isEmpty());
}

/// Qt's terminate() posts WM_CLOSE, which a console child launched with
/// CREATE_NO_WINDOW and blocked in ServeAsync() never sees. Without the stop
/// event the quit ends in TerminateProcess, skipping everything the Collector
/// only does on an orderly shutdown: deleting the temporary copy of
/// ffxiv_dx11.exe and closing the capture session row.
void LifecycleTests::stopAsksThroughTheCollectorsStopEventBeforeKilling()
{
#ifndef Q_OS_WIN
    QSKIP("The stop event is a Win32 object.");
#else
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = longLivedProgram(directory.path());
    if (stub.isEmpty())
        QSKIP("No long-lived stub program available on this machine.");

    // Our own event, never the one a Collector serving the user's real pipe is
    // waiting on: setting that would stop the machine's actual recording.
    const QString name = QStringLiteral("Local\\MentorRecorderTest.%1.stop")
                             .arg(QCoreApplication::applicationPid());
    HANDLE event = ::CreateEventW(nullptr, TRUE, FALSE,
                                  reinterpret_cast<const wchar_t *>(name.utf16()));
    QVERIFY(event != nullptr);
    QCOMPARE(::WaitForSingleObject(event, 0), DWORD(WAIT_TIMEOUT));

    mr::CollectorProcess collector(stub, nullptr);
    collector.setStopEventNameForTest(name);
    collector.start();
    QTRY_VERIFY_WITH_TIMEOUT(collector.isRunning(), 10000);

    QElapsedTimer elapsed;
    elapsed.start();
    collector.stop();
    const qint64 blockedMs = elapsed.elapsed();

    // The request was made...
    QCOMPARE(::WaitForSingleObject(event, 0), DWORD(WAIT_OBJECT_0));
    // ...the stub ignored it, so the fallback still ended the child...
    QVERIFY(!collector.isRunning());
    QCOMPARE(collector.stateToken(), QStringLiteral("stopped"));
    QVERIFY(!collector.lastStopWasGraceful());
    // ...and the whole thing stayed bounded.
    QVERIFY2(blockedMs < mr::CollectorProcess::gracefulStopMs() + 2000,
             qPrintable(QStringLiteral("stop() took %1 ms").arg(blockedMs)));
    ::CloseHandle(event);
#endif
}

/// The takeover has to find the holder the same way the Collector records it:
/// serve.pid under MR_DATA_DIR, or under its logs/ folder.
void LifecycleTests::theServeLeasePidIsReadFromTheCollectorsDataDirectory()
{
    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(directory.path()));

    QCOMPARE(mr::CollectorProcess::collectorDataDirectory(),
             QDir::cleanPath(QDir(directory.path()).absolutePath()));
    // Nothing recorded yet.
    QCOMPARE(mr::CollectorProcess::readServeLeasePid(), qint64(0));

    QVERIFY(writeServePid(directory.path(), 4242));
    QCOMPARE(mr::CollectorProcess::readServeLeasePid(), qint64(4242));

    // Garbage is not a pid.
    QVERIFY(writeServePid(directory.path(), 0));
    QCOMPARE(mr::CollectorProcess::readServeLeasePid(), qint64(0));

    // And the logs/ layout is looked at as well.
    QFile::remove(QDir(directory.path()).absoluteFilePath(QStringLiteral("serve.pid")));
    QVERIFY(QDir(directory.path()).mkpath(QStringLiteral("logs")));
    QVERIFY(writeServePid(QDir(directory.path()).absoluteFilePath(QStringLiteral("logs")), 77));
    QCOMPARE(mr::CollectorProcess::readServeLeasePid(), qint64(77));

    qputenv("MR_DATA_DIR", previous);
}

/// taskkill and tasklist are judged by exit code and by parsed fields, never by
/// searching their output for "ERROR".
///
/// This product ships to a Chinese-language audience, and on a Chinese Windows
/// taskkill's failure reads 「错误: 无法终止 PID 为 N 的进程。」 - no ASCII
/// "ERROR" anywhere in it. Searching the output for "ERROR" reads every such
/// failure, an elevated holder refusing access included, as a success and tells
/// the user 「已结束它」 while nothing has happened.
void LifecycleTests::systemToolsAreJudgedByExitCodeNotByLocalisedOutput()
{
    QVERIFY(mr::CollectorProcess::taskkillSucceeded(0));
    QVERIFY(!mr::CollectorProcess::taskkillSucceeded(1));
    QVERIFY(!mr::CollectorProcess::taskkillSucceeded(
        mr::CollectorProcess::kTaskkillNoSuchProcess));
    // "could not be run at all" is not a success either.
    QVERIFY(!mr::CollectorProcess::taskkillSucceeded(-1));

    const QString row = QStringLiteral(
        "\"MentorRecorder.Collector.exe\",\"4242\",\"Console\",\"1\",\"31,204 K\"\r\n");
    QVERIFY(mr::CollectorProcess::tasklistNamesCollector(row, 4242));
    // Right image, wrong pid. A bare substring search would have said yes.
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(row, 4243));
    // Right pid, wrong image: serve.pid was stale and Windows reused the number.
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(
        QStringLiteral("\"notepad.exe\",\"4242\",\"Console\",\"1\",\"9,001 K\"\r\n"), 4242));
    // Case is not part of the identity of a Windows file name.
    QVERIFY(mr::CollectorProcess::tasklistNamesCollector(
        QStringLiteral("\"mentorrecorder.collector.EXE\",\"4242\",\"Console\",\"1\",\"1 K\""),
        4242));
    // The localised "nothing matched" sentence is not a row, in either language.
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(
        QString::fromUtf8("信息: 没有运行的任务匹配指定标准。"), 4242));
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(
        QStringLiteral("INFO: No tasks are running which match the specified criteria."), 4242));
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(QString(), 4242));
    QVERIFY(!mr::CollectorProcess::tasklistNamesCollector(row, 0));
}

/// A Collector that took the lease seconds ago is never ended, however silent
/// it is.
///
/// Silence from an instance that new is what a database migration looks like
/// from outside: the lease is taken and serve.pid written before the pipe can
/// answer anything, and the first start after an upgrade runs migrations in
/// exactly that window. Ending it there kills a healthy process in the middle
/// of writing the user's database.
void LifecycleTests::aFreshServeLeaseHolderIsNeverEnded()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    // Written now, so it is as fresh as a serve.pid ever gets.
    QVERIFY(writeServePid(data.path(), 424242));
    QVERIFY(mr::CollectorProcess::serveLeasePidIsFresh());
    QVERIFY(mr::CollectorProcess::serveLeasePidAgeMs()
            < mr::CollectorProcess::servePidProtectionMs());

    mr::CollectorProcess collector(
        QDir(directory.path()).absoluteFilePath(QStringLiteral("nothing.exe")), nullptr);
    collector.setStopEventNameForTest(isolatedStopEventName());
    collector.setTakeoverTimingsForTest(120, 60);
    QList<ToolCall> calls;
    collector.setToolRunnerForTest(
        [&calls](const QString &tool, const QStringList &arguments) {
            calls.append(ToolCall{tool, arguments});
            return mr::CollectorProcess::ToolResult{0, QString()};
        });
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);

    // The owner's failed-connect counter has tripped. It still may not kill.
    collector.requestServeLeaseTakeover();
    QTest::qWait(400);
    QVERIFY2(calls.isEmpty(), "a fresh serve.pid must not even be listed, let alone killed");
    QCOMPARE(takeovers.count(), 1);
    QVERIFY(takeovers.at(0).at(1).toString().contains(QString::fromUtf8("刚刚启动")));

    // Asked again and again while the migration runs: still nothing ended, and
    // not a toast per attempt either.
    for (int attempt = 0; attempt < 5; ++attempt)
        collector.requestServeLeaseTakeover();
    QTest::qWait(300);
    QVERIFY(calls.isEmpty());
    QCOMPARE(takeovers.count(), 1);

    qputenv("MR_DATA_DIR", previous);
}

/// The stop event alone is usually enough, and when it is, nothing is ended.
///
/// The holder is asked through its own graceful-stop event first and then given
/// a grace to act on it. A holder that leaves within that window is picked up by
/// serve.pid disappearing, and our own child takes the freed lease - with no
/// tasklist, no taskkill and nothing for the user to read.
void LifecycleTests::aHolderThatLeavesOnItsOwnIsNeverEnded()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = longLivedProgram(directory.path());
    if (stub.isEmpty()) {
        qputenv("MR_DATA_DIR", previous);
        QSKIP("No long-lived stub program available on this machine.");
    }

    QVERIFY(writeServePid(data.path(), 424242));
    QVERIFY(ageServePid(data.path(), 60000));
    QVERIFY(!mr::CollectorProcess::serveLeasePidIsFresh());

    mr::CollectorProcess collector(stub, nullptr);
    collector.setStopEventNameForTest(isolatedStopEventName());
    // A long grace: the point is that the holder leaves inside it.
    collector.setTakeoverTimingsForTest(4000, 60);
    QList<ToolCall> calls;
    collector.setToolRunnerForTest(
        [&calls](const QString &tool, const QStringList &arguments) {
            calls.append(ToolCall{tool, arguments});
            return mr::CollectorProcess::ToolResult{0, QString()};
        });
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);

    collector.requestServeLeaseTakeover();
    QTest::qWait(500);
    // Still inside the grace: asked, not ended, and our child not started yet.
    QVERIFY(calls.isEmpty());
    QVERIFY(!collector.isRunning());

    // The holder acts on the stop event and removes its serve.pid on the way out.
    QVERIFY(QFile::remove(QDir(data.path()).absoluteFilePath(QStringLiteral("serve.pid"))));

    QTRY_VERIFY_WITH_TIMEOUT(collector.isRunning(), 5000);
    QVERIFY2(calls.isEmpty(), "the holder left on its own; nothing may have been run at it");
    QCOMPARE(takeovers.count(), 0);

    collector.stop();
    qputenv("MR_DATA_DIR", previous);
}

/// Forcing is the last step of the sequence, never the first.
///
/// Order: list the pid, ask taskkill without /F, wait the grace, list again, and
/// only then /F - never straight to /F on our child's exit code 6.
void LifecycleTests::aWedgedHolderIsForcedOnlyAsALastResort()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = longLivedProgram(directory.path());
    if (stub.isEmpty()) {
        qputenv("MR_DATA_DIR", previous);
        QSKIP("No long-lived stub program available on this machine.");
    }

    QVERIFY(writeServePid(data.path(), 424242));
    QVERIFY(ageServePid(data.path(), 60000));

    mr::CollectorProcess collector(stub, nullptr);
    collector.setStopEventNameForTest(isolatedStopEventName());
    collector.setTakeoverTimingsForTest(120, 80);
    QList<ToolCall> calls;
    // A holder that ignores WM_CLOSE - which is what a console Collector with no
    // message loop is - and only goes when it is forced.
    bool holderAlive = true;
    collector.setToolRunnerForTest(
        [&calls, &holderAlive](const QString &tool, const QStringList &arguments) {
            calls.append(ToolCall{tool, arguments});
            if (tool == QStringLiteral("tasklist.exe")) {
                return mr::CollectorProcess::ToolResult{
                    0,
                    holderAlive ? QStringLiteral("\"MentorRecorder.Collector.exe\",\"424242\","
                                                 "\"Console\",\"1\",\"31,204 K\"\r\n")
                                : QString::fromUtf8("信息: 没有运行的任务匹配指定标准。")};
            }
            if (arguments.contains(QStringLiteral("/F")))
                holderAlive = false;
            return mr::CollectorProcess::ToolResult{0, QString()};
        });
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);

    collector.requestServeLeaseTakeover();
    QTRY_VERIFY_WITH_TIMEOUT(takeovers.count() >= 1, 8000);

    QCOMPARE(calls.size(), 4);
    QCOMPARE(calls.at(0).tool, QStringLiteral("tasklist.exe"));
    QCOMPARE(calls.at(1).tool, QStringLiteral("taskkill.exe"));
    QVERIFY2(!calls.at(1).forced(), "the first taskkill must be the polite one");
    QCOMPARE(calls.at(2).tool, QStringLiteral("tasklist.exe"));
    QCOMPARE(calls.at(3).tool, QStringLiteral("taskkill.exe"));
    QVERIFY2(calls.at(3).forced(), "/F is only reached after the polite kill was ignored");

    QVERIFY(takeovers.at(0).at(0).toBool());
    QVERIFY(takeovers.at(0).at(1).toString().contains(QString::fromUtf8("已结束它")));
    // One message per takeover, not one per step.
    QTest::qWait(300);
    QCOMPARE(takeovers.count(), 1);
    QTRY_VERIFY_WITH_TIMEOUT(collector.isRunning(), 5000);

    collector.stop();
    qputenv("MR_DATA_DIR", previous);
}

/// A taskkill that was refused is reported as a refusal.
///
/// An elevated Collector answers taskkill with exit code 1 and a localised
/// sentence. The user has to be told to end it themselves, never shown a
/// success message.
void LifecycleTests::aHolderThatRefusesToBeEndedIsReportedRatherThanClaimedEnded()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    QVERIFY(writeServePid(data.path(), 424242));
    QVERIFY(ageServePid(data.path(), 60000));

    mr::CollectorProcess collector(
        QDir(directory.path()).absoluteFilePath(QStringLiteral("nothing.exe")), nullptr);
    collector.setStopEventNameForTest(isolatedStopEventName());
    collector.setTakeoverTimingsForTest(120, 80);
    collector.setToolRunnerForTest([](const QString &tool, const QStringList &) {
        if (tool == QStringLiteral("tasklist.exe")) {
            return mr::CollectorProcess::ToolResult{
                0, QStringLiteral("\"MentorRecorder.Collector.exe\",\"424242\","
                                  "\"Console\",\"1\",\"31,204 K\"\r\n")};
        }
        // What a Chinese Windows prints when it refuses. No ASCII "ERROR".
        return mr::CollectorProcess::ToolResult{
            1,
            QString::fromUtf8("错误: 无法终止 PID 为 424242 的进程。原因: 拒绝访问。")};
    });
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);

    collector.requestServeLeaseTakeover();
    QTRY_VERIFY_WITH_TIMEOUT(takeovers.count() >= 1, 8000);

    QCOMPARE(takeovers.count(), 1);
    QCOMPARE(takeovers.at(0).at(0).toBool(), false);
    const QString message = takeovers.at(0).at(1).toString();
    QVERIFY(message.contains(QString::fromUtf8("无法结束它")));
    QVERIFY(!message.contains(QString::fromUtf8("已结束它")));

    qputenv("MR_DATA_DIR", previous);
}

/// Exit code 6 says the lease holder did not answer a probe. It does not say
/// the holder is dead, and it must not, on its own, end anything.
///
/// What it does is ask: the holder's stop event is set and the holder is watched
/// for. Here serve.pid names this very test process - never a Collector - so the
/// lease reads as vacant and our own child simply takes it, silently and
/// bounded by the attempt cap.
void LifecycleTests::anUnresponsiveHolderExitAsksBeforeItEndsAnything()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = unresponsiveHolderProgram(directory.path());
    if (stub.isEmpty()) {
        qputenv("MR_DATA_DIR", previous);
        QSKIP("No way to fake an exit code on this machine.");
    }

    QVERIFY(writeServePid(data.path(), QCoreApplication::applicationPid()));

    mr::CollectorProcess collector(stub, nullptr);
    collector.setStopEventNameForTest(isolatedStopEventName());
    collector.setTakeoverTimingsForTest(200, 80);
    QList<ToolCall> calls;
    collector.setToolRunnerForTest(
        [&calls](const QString &tool, const QStringList &arguments) {
            calls.append(ToolCall{tool, arguments});
            return mr::CollectorProcess::ToolResult{0, QString()};
        });
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);
    collector.start();

    // The child exits 6, the lease reads as vacant, our child is started again -
    // up to the cap, and then the supervisor settles rather than looping. Each
    // cycle is one launch of a batch file, so a few seconds runs it all out.
    QTest::qWait(3000);
    QCOMPARE(collector.stateToken(), QStringLiteral("reused"));
    QVERIFY2(calls.isEmpty(), "exit code 6 alone may not list or kill anything");
    QCOMPARE(takeovers.count(), 0);

    // Bounded until the pipe comes back, however often it is asked.
    for (int attempt = 0; attempt < 10; ++attempt)
        collector.requestServeLeaseTakeover();
    QTest::qWait(300);
    QVERIFY(calls.isEmpty());
    QCOMPARE(takeovers.count(), 0);
    QCOMPARE(collector.stateToken(), QStringLiteral("reused"));

    collector.stop();
    qputenv("MR_DATA_DIR", previous);
}

/// The ordinary "the first Desktop closed, so this one takes the lease" path
/// must survive the change: with no live holder to end, the takeover simply
/// starts our own child.
void LifecycleTests::aVanishedHolderJustLetsOurOwnChildTakeTheLease()
{
    QTemporaryDir data;
    QVERIFY(data.isValid());
    const QByteArray previous = qgetenv("MR_DATA_DIR");
    qputenv("MR_DATA_DIR", QFile::encodeName(data.path()));

    QTemporaryDir directory;
    QVERIFY(directory.isValid());
    const QString stub = longLivedProgram(directory.path());
    if (stub.isEmpty()) {
        qputenv("MR_DATA_DIR", previous);
        QSKIP("No long-lived stub program available on this machine.");
    }

    // No serve.pid at all: nothing is holding the lease.
    mr::CollectorProcess collector(stub, nullptr);
    QSignalSpy takeovers(&collector, &mr::CollectorProcess::leaseTakeover);
    collector.noteBackendConnected(true);
    collector.noteBackendConnected(false);
    collector.requestServeLeaseTakeover();

    QCOMPARE(takeovers.count(), 1);
    QVERIFY(takeovers.at(0).at(0).toBool());
    QVERIFY(takeovers.at(0).at(1).toString().contains(QString::fromUtf8("重新启动")));
    // Our own child was launched rather than the takeover merely complaining.
    QVERIFY2(collector.stateToken() == QStringLiteral("starting")
                 || collector.stateToken() == QStringLiteral("running"),
             qPrintable(collector.stateToken()));
    QTRY_VERIFY_WITH_TIMEOUT(collector.isRunning(), 10000);

    collector.stop();
    qputenv("MR_DATA_DIR", previous);
}

/// IpcClient fails every request still in flight from its own destructor, and
/// that destructor runs from inside ~QObject of the backend - after the
/// backend's own destructor body. A controller callback that reached for
/// m_backend there was calling a pure virtual on an object whose dynamic type
/// was already QObject (review finding M-1).
void LifecycleTests::aFailedRequestFromADestroyedBackendNeverReachesAController()
{
    QFile::remove(mr::AppSettings::filePath());
    mr::AppSettings settings;

    auto *backend = new ClosingBackend;
    auto *controller = new mr::AppController(backend, &settings);
    // Let the start-up refresh put its requests on the wire; none of them are
    // ever answered, so they are all still pending.
    QTest::qWait(60);

    // The backend goes first, as it does when both are children of the
    // application object.
    delete backend;
    QTest::qWait(60);
    delete controller;
    QTest::qWait(20);

    // Reaching here at all is the assertion: the failure callbacks ran with a
    // half-destroyed backend and must not have touched it.
    QVERIFY(true);
    QFile::remove(mr::AppSettings::filePath());
}

/// A backup or an export walks the whole database. Sharing a status poll's
/// deadline makes the Desktop report a failure while the Collector is still
/// working, then discard the eventual success as an unknown request_id.
void LifecycleTests::exportsAndBackupsGetTheirOwnDeadline()
{
    const int ordinary = mr::IpcClient::kDefaultRequestTimeoutMs;
    const QStringList longRunning{QStringLiteral("BackupDatabase"),
                                  QStringLiteral("ExportCsv"),
                                  QStringLiteral("ExportJson"),
                                  QStringLiteral("ExportDiagnosticsReport"),
                                  QStringLiteral("ExportCandidateEvidence")};
    for (const QString &type : longRunning) {
        QCOMPARE(mr::IpcClient::timeoutForMessageType(type, ordinary),
                 mr::IpcClient::kLongRequestTimeoutMs);
    }
    QVERIFY(mr::IpcClient::kLongRequestTimeoutMs >= 120000);

    const QStringList ordinaryTypes{QStringLiteral("GetStatus"),
                                    QStringLiteral("QueryRuns"),
                                    QStringLiteral("CorrectRun"),
                                    QStringLiteral("GetRunEvents"),
                                    QStringLiteral("SetRunReflection")};
    for (const QString &type : ordinaryTypes)
        QCOMPARE(mr::IpcClient::timeoutForMessageType(type, ordinary), ordinary);

    // Shortening the ordinary deadline must not shorten the exports with it.
    QCOMPARE(mr::IpcClient::timeoutForMessageType(QStringLiteral("ExportCsv"), 50),
             mr::IpcClient::kLongRequestTimeoutMs);
    QCOMPARE(mr::IpcClient::timeoutForMessageType(QStringLiteral("GetStatus"), 50), 50);
}

void LifecycleTests::disclosureAcknowledgementPersists()
{
    // Test mode is on for the whole binary (see main); the file is removed so
    // this test starts from "never acknowledged".
    QFile::remove(mr::AppSettings::filePath());

    {
        mr::AppSettings settings;
        QVERIFY(!settings.disclosureAcknowledged());
        QVERIFY(settings.disclosureAcknowledgedAt().isEmpty());
        QCOMPARE(settings.acknowledgedDisclosureVersion(), 0);

        QSignalSpy changed(&settings, &mr::AppSettings::disclosureChanged);
        settings.acknowledgeDisclosure(QStringLiteral("2026-09-04T11:00:00.000Z"));
        QCOMPARE(changed.count(), 1);
        QVERIFY(settings.disclosureAcknowledged());
        QCOMPARE(settings.acknowledgedDisclosureVersion(),
                 mr::AppSettings::kDisclosureVersion);
    }

    {
        // A second object, i.e. the next launch, must still see it.
        mr::AppSettings reopened;
        QVERIFY(reopened.disclosureAcknowledged());
        QCOMPARE(reopened.disclosureAcknowledgedAt(),
                 QStringLiteral("2026-09-04T11:00:00.000Z"));

        reopened.resetDisclosureAcknowledgement();
        QVERIFY(!reopened.disclosureAcknowledged());
        QVERIFY(reopened.disclosureAcknowledgedAt().isEmpty());
    }

    QFile::remove(mr::AppSettings::filePath());
}

void LifecycleTests::raisingTheDisclosureVersionInvalidatesTheAcknowledgement()
{
    QFile::remove(mr::AppSettings::filePath());

    {
        mr::AppSettings settings;
        settings.acknowledgeDisclosure(QStringLiteral("2026-09-04T11:00:00.000Z"));
        QVERIFY(settings.disclosureAcknowledged());
    }

    // Simulate an acknowledgement of an older text by writing a lower version
    // directly: the page must come back rather than carry the stale consent.
    {
        mr::AppSettings settings;
        settings.setValue(QStringLiteral("ui/disclosure_acknowledged_version"),
                          mr::AppSettings::kDisclosureVersion - 1);
    }
    {
        mr::AppSettings settings;
        QVERIFY(!settings.disclosureAcknowledged());
    }

    QFile::remove(mr::AppSettings::filePath());
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    // Once, before anything runs: a slot that aborts on a QVERIFY never reaches
    // its own cleanup, so a per-slot toggle could leave this off with a
    // QFile::remove still to come.
    QStandardPaths::setTestModeEnabled(true);
    QGuiApplication app(argc, argv);
    LifecycleTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "LifecycleTests.moc"
