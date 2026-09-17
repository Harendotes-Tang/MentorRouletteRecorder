#include "CollectorProcess.h"

#include "PipeName.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QLoggingCategory>
#include <QStandardPaths>

#include <utility>

#ifdef Q_OS_WIN
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace {
Q_LOGGING_CATEGORY(lcCollector, "mr.collector")

// How long stop() waits after terminate(). On Windows terminate() only posts
// WM_CLOSE to top-level windows and to the main thread's message queue, and the
// Collector has neither, so this wait is nearly always spent in full before
// kill(). Deliberately short: the graceful request has already been made and
// answered by then, or there was no stop event to make it through.
constexpr int kStopGraceMs = 500;
/// Just long enough to reap the killed child, never long enough to be seen.
constexpr int kReapGraceMs = 200;

/// The Collector executable file name, as it appears in the system task list.
const char *kCollectorImageName = "MentorRecorder.Collector.exe";

/// A system tool from %SystemRoot%\System32, by absolute path.
///
/// Never by bare name: that resolves through PATH. These two tools end a
/// process the user can see in Task Manager, so which binary runs must not
/// depend on the environment.
QString systemTool(const QString &fileName)
{
#ifdef Q_OS_WIN
    const QString root = qEnvironmentVariable("SystemRoot", QStringLiteral("C:/Windows"));
    const QString path = QDir(root).absoluteFilePath(QStringLiteral("System32/") + fileName);
    return QFileInfo::exists(path) ? path : QString();
#else
    Q_UNUSED(fileName)
    return QString();
#endif
}

/// Deadline for one system-tool call. Short enough that a stuck tool cannot
/// stall the sequence; it runs asynchronously either way, so it never blocks
/// the GUI thread.
constexpr int kToolTimeoutMs = 1500;

/// What a tool call reports when it could not be run or had to be killed.
constexpr int kToolFailedExitCode = -1;

/// How often the holder grace re-checks. Small enough that the ordinary
/// hand-off (the holder removes its serve.pid and goes) is picked up at once.
constexpr int kHolderProbeMs = 250;

/// Split one line of "tasklist /FO CSV" output into its fields.
///
/// The format is minimal CSV: every field is quoted, a quote inside a field is
/// doubled. Written out rather than pattern-matched because a misplaced field
/// boundary reads one pid's row as another's.
QStringList splitCsvRow(const QString &line)
{
    QStringList fields;
    QString field;
    bool inQuotes = false;
    for (int index = 0; index < line.size(); ++index) {
        const QChar character = line.at(index);
        if (inQuotes) {
            if (character != QLatin1Char('"')) {
                field.append(character);
            } else if (index + 1 < line.size() && line.at(index + 1) == QLatin1Char('"')) {
                field.append(QLatin1Char('"'));
                ++index;
            } else {
                inQuotes = false;
            }
            continue;
        }
        if (character == QLatin1Char('"'))
            inQuotes = true;
        else if (character == QLatin1Char(','))
            fields.append(std::exchange(field, QString()));
        else
            field.append(character);
    }
    fields.append(field);
    return fields;
}
} // namespace

namespace mr {

CollectorProcess::CollectorProcess(QObject *parent)
    : CollectorProcess(resolveDefaultExecutable(), parent)
{
}

/// Where the Collector is looked for.
///
/// MR_COLLECTOR_PATH, when set, is authoritative and exclusive: it is returned
/// whether or not the file exists, so pointing it at nothing is a supported way
/// to say "never launch a Collector from this process".
///
/// Otherwise, in order:
///   1. next to the Desktop executable (release layout, scripts/build.ps1,
///      the CMake mr_stage_collector target);
///   2. a "collector" sub-directory next to the executable;
///   3. the C# build output inside a source checkout, walking up from the
///      build directory (plain IDE builds that never staged anything).
/// The first existing file wins. When nothing exists the path beside the
/// executable is returned so the "missing" state names where it was expected.
QString CollectorProcess::resolveDefaultExecutable()
{
    const QString exeName = QStringLiteral("MentorRecorder.Collector.exe");
    const QDir appDir(QCoreApplication::applicationDirPath());
    const QString beside = appDir.absoluteFilePath(exeName);

    // An explicit override is the *only* candidate: falling back to a discovered
    // binary when the named one does not exist would launch something the
    // operator did not ask for - including the real Collector against the user's
    // production data directory during a test run.
    const QString overridePath = qEnvironmentVariable("MR_COLLECTOR_PATH");
    if (!overridePath.isEmpty()) {
        const QString resolved = QDir::cleanPath(overridePath);
        if (QFileInfo::exists(resolved))
            qCInfo(lcCollector) << "collector resolved at" << resolved << "(override)";
        else
            qCWarning(lcCollector) << "MR_COLLECTOR_PATH does not exist:" << resolved;
        return resolved;
    }

    QStringList candidates;
    candidates << beside;
    candidates << appDir.absoluteFilePath(QStringLiteral("collector/") + exeName);

    static const QStringList devLayouts = {
        QStringLiteral("src/Collector/bin/x64/Release/net8.0-windows/win-x64/"),
        QStringLiteral("src/Collector/bin/x64/Debug/net8.0-windows/win-x64/"),
        QStringLiteral("src/Collector/bin/Release/net8.0-windows/win-x64/"),
        QStringLiteral("src/Collector/bin/Debug/net8.0-windows/win-x64/"),
    };
    QDir probe = appDir;
    for (int depth = 0; depth < 6; ++depth) {
        for (const QString &layout : devLayouts)
            candidates << probe.absoluteFilePath(layout + exeName);
        if (!probe.cdUp())
            break;
    }

    for (const QString &candidate : std::as_const(candidates)) {
        if (QFileInfo::exists(candidate)) {
            qCInfo(lcCollector) << "collector resolved at" << candidate;
            return candidate;
        }
    }
    qCWarning(lcCollector) << "collector not found; looked at" << candidates;
    return beside;
}

CollectorProcess::CollectorProcess(QString executablePath, QObject *parent)
    : QObject(parent)
    , m_executablePath(std::move(executablePath))
{
    wireProcess();

    m_restartTimer.setSingleShot(true);
    connect(&m_restartTimer, &QTimer::timeout, this, [this] {
        if (m_stopRequested || isRunning())
            return;
        // Re-checked here, not only at exit time: our child can lose the race
        // with the pipe, exiting because another Collector already holds the
        // per-user serve lease before we ever saw the socket connect.
        if (m_backendConnected) {
            setState(State::Reused);
            return;
        }
        ++m_restartCount;
        qCInfo(lcCollector) << "restarting collector, attempt" << m_restartCount;
        start();
        // The delay that actually elapsed, not the one queued for next time.
        Q_EMIT restarted(m_restartCount, m_scheduledDelayMs);
    });

    // The takeover's waits are timers, not waitForFinished(): waiting on the GUI
    // thread freezes the window for seconds at a time, and exit code 6 repeats.
    m_holderGraceTimer.setInterval(kHolderProbeMs);
    connect(&m_holderGraceTimer, &QTimer::timeout, this, &CollectorProcess::onHolderGraceTick);
    m_toolTimeoutTimer.setSingleShot(true);
    connect(&m_toolTimeoutTimer, &QTimer::timeout, this, [this] {
        if (m_tool && m_tool->state() != QProcess::NotRunning) {
            qCWarning(lcCollector) << "system tool timed out:" << m_tool->program();
            m_tool->kill();
        }
    });

    m_state = isAvailable() ? State::Idle : State::Missing;
}

CollectorProcess::~CollectorProcess()
{
    stop();
}

namespace {

// Our own creation time as .NET UTC ticks (100 ns since 0001-01-01), the form the
// Collector's --parent-start-time accepts. Empty when the kernel refuses to say.
QString ownStartTimeUtcTicks()
{
#ifdef Q_OS_WIN
    FILETIME creation{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &creation, &exited, &kernel, &user))
        return {};
    const quint64 fileTime =
        (quint64(creation.dwHighDateTime) << 32) | quint64(creation.dwLowDateTime);
    // FILETIME counts 100 ns intervals since 1601-01-01; .NET ticks since 0001-01-01.
    constexpr quint64 kFileTimeEpochAsTicks = 504911232000000000ULL;
    return QString::number(fileTime + kFileTimeEpochAsTicks);
#else
    return {};
#endif
}

} // namespace

void CollectorProcess::wireProcess()
{
    m_process = new QProcess(this);
    m_process->setProgram(m_executablePath);
    // --parent-pid makes a hard kill of this process survivable: stop() covers the
    // orderly exit, but Task Manager, Stop-Process -Force and a crashed shell skip it,
    // and the child would keep serving the pipe and holding the per-user serve lease.
    // Told our pid, the Collector watches it and shuts down the way Ctrl+C would.
    // --parent-start-time lets that watchdog tell our pid from a recycled one, so a
    // Collector outliving a crashed Desktop cannot adopt an unrelated process
    // (src/Collector/Diagnostics/ParentProcessWatchdog.cs).
    QStringList arguments{QStringLiteral("--serve"),
                          QStringLiteral("--parent-pid"),
                          QString::number(QCoreApplication::applicationPid())};
    const QString startedAt = ownStartTimeUtcTicks();
    if (!startedAt.isEmpty())
        arguments << QStringLiteral("--parent-start-time") << startedAt;
    m_process->setArguments(arguments);
    m_process->setWorkingDirectory(QFileInfo(m_executablePath).absolutePath());

    connect(m_process, &QProcess::started, this, [this] {
        m_startedAtMs = QDateTime::currentMSecsSinceEpoch();
        setState(State::Running);
    });
    connect(m_process, &QProcess::finished, this, &CollectorProcess::onFinished);
    connect(m_process, &QProcess::errorOccurred, this, [this](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart)
            return;
        setState(State::Exited, m_process->errorString());
        scheduleRestart();
    });
}

bool CollectorProcess::isAvailable() const
{
    return QFileInfo::exists(m_executablePath);
}

QStringList CollectorProcess::processArguments() const
{
    return m_process ? m_process->arguments() : QStringList();
}

bool CollectorProcess::isRunning() const
{
    return m_process && m_process->state() != QProcess::NotRunning;
}

QString CollectorProcess::stateToken() const
{
    switch (m_state) {
    case State::Missing:  return QStringLiteral("missing");
    case State::Idle:     return QStringLiteral("idle");
    case State::Starting: return QStringLiteral("starting");
    case State::Running:  return QStringLiteral("running");
    case State::Reused:   return QStringLiteral("reused");
    case State::Exited:   return QStringLiteral("exited");
    case State::Stopped:  return QStringLiteral("stopped");
    }
    return QStringLiteral("idle");
}

QString CollectorProcess::statusText() const
{
    switch (m_state) {
    case State::Missing:
        return QString::fromUtf8("未找到 Collector（期望位置：%1）。请运行 scripts/build.ps1、"
                                 "在 CMake 中启用 MR_STAGE_COLLECTOR，或设置 MR_COLLECTOR_PATH")
            .arg(QDir::toNativeSeparators(m_executablePath));
    case State::Idle:
        return QString::fromUtf8("Collector 可用，未启动");
    case State::Starting:
        return QString::fromUtf8("Collector 启动中…");
    case State::Running:
        return QString::fromUtf8("Collector 运行中");
    case State::Reused:
        if (!m_detail.isEmpty())
            return QString::fromUtf8("Collector 复用已有实例（%1）").arg(m_detail);
        return QString::fromUtf8("Collector 运行中（复用已有实例）");
    case State::Exited:
        if (m_restartTimer.isActive()) {
            return QString::fromUtf8("Collector 已退出（%1），%2 秒后重启（第 %3 次）")
                .arg(m_detail)
                .arg(double(m_scheduledDelayMs) / 1000.0, 0, 'f', 1)
                .arg(m_restartCount + 1);
        }
        return QString::fromUtf8("Collector 已退出（%1）").arg(m_detail);
    case State::Stopped:
        return QString::fromUtf8("Collector 已停止");
    }
    return QString();
}

void CollectorProcess::setState(State state, const QString &detail)
{
    if (m_state == state && m_detail == detail)
        return;
    m_state = state;
    m_detail = detail;
    Q_EMIT stateChanged();
}

void CollectorProcess::start()
{
    if (isRunning())
        return;
    if (!isAvailable()) {
        qCInfo(lcCollector) << "collector executable not present:" << m_executablePath;
        setState(State::Missing);
        return;
    }
    m_stopRequested = false;
    setState(State::Starting);
    m_process->start();
}

QString CollectorProcess::stopEventName(const QString &pipeName)
{
    if (pipeName.isEmpty())
        return {};
    // The Local\ namespace is per-session, the same scope the pipe itself has:
    // two users on one machine never share either object.
    return QStringLiteral("Local\\%1.stop").arg(pipeName);
}

QString CollectorProcess::currentStopEventName()
{
    return stopEventName(ipc::currentUserPipeName());
}

bool CollectorProcess::childHoldsServeLease() const
{
    if (!m_process || m_process->state() == QProcess::NotRunning)
        return false;
    const qint64 recorded = readServeLeasePid();
    return recorded > 0 && recorded == m_process->processId();
}

/// Set the Collector graceful-stop event.
///
/// This is the whole point of an orderly exit: the shutdown path behind that
/// event is what deletes the temporary copy of ffxiv_dx11.exe the decoder had
/// to load Oodle from, closes the capture_sessions row so the next start does
/// not read it as a crash, and writes the shutdown.stopped log line.
/// terminate() reaches none of it - Qt implements it as WM_CLOSE, and a console
/// child launched with CREATE_NO_WINDOW whose main thread is blocked inside
/// ServeAsync() never sees a window message.
bool CollectorProcess::signalStopEvent(bool requireOwnership)
{
#ifdef Q_OS_WIN
    const bool overridden = !m_stopEventName.isEmpty();
    // The event is named after the pipe, and the pipe is per user, not per
    // process. A second Desktop whose own child exited on the serve lease must
    // not set it while quitting: the instance waiting on it is the *first*
    // Desktop's Collector, which is still recording.
    if (requireOwnership && !overridden && !childHoldsServeLease())
        return false;
    const QString name = overridden ? m_stopEventName : currentStopEventName();
    if (name.isEmpty())
        return false;
    // Opened, never created: when the event does not exist the child is an
    // older Collector without the handshake, and we fall back to terminate().
    HANDLE event = ::OpenEventW(EVENT_MODIFY_STATE, FALSE,
                                reinterpret_cast<const wchar_t *>(name.utf16()));
    if (event == nullptr)
        return false;
    const bool signalled = ::SetEvent(event) != 0;
    ::CloseHandle(event);
    if (signalled)
        qCInfo(lcCollector) << "graceful stop requested through" << name;
    return signalled;
#else
    return false;
#endif
}

void CollectorProcess::stop()
{
    m_stopRequested = true;
    m_restartTimer.stop();
    abortTakeover();
    m_lastStopWasGraceful = false;
    if (!isRunning()) {
        if (m_state != State::Missing)
            setState(State::Stopped);
        return;
    }

    // Ask first; fall back to terminate()/kill() only when there is no stop event
    // to ask through, or the child ignores it.
    if (signalStopEvent(/*requireOwnership=*/true)
        && m_process->waitForFinished(gracefulStopMs())) {
        m_lastStopWasGraceful = true;
        setState(State::Stopped);
        return;
    }

    m_process->terminate();
    if (!m_process->waitForFinished(kStopGraceMs)) {
        m_process->kill();
        m_process->waitForFinished(kReapGraceMs);
    }
    setState(State::Stopped);
}

// ---------------------------------------------------------------------------
// Serve-lease takeover
// ---------------------------------------------------------------------------

QString CollectorProcess::collectorDataDirectory()
{
    // Mirrors src/Collector/Storage/DatabasePaths.cs: MR_DATA_DIR wins, and the
    // managed location is %LOCALAPPDATA%\MentorRecorder.
    const QString dataDirectory = qEnvironmentVariable("MR_DATA_DIR");
    if (!dataDirectory.trimmed().isEmpty())
        return QDir::cleanPath(QDir(dataDirectory).absolutePath());
    const QString local =
        QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation);
    if (local.isEmpty())
        return {};
    return QDir(local).absoluteFilePath(QStringLiteral("MentorRecorder"));
}

QString CollectorProcess::serveLeasePidPath()
{
    const QString root = collectorDataDirectory();
    if (root.isEmpty())
        return {};
    // The Collector writes it beside its logs; the managed layout puts those in
    // a logs/ folder under the same root, so both places are looked at.
    const QStringList candidates{
        QDir(root).absoluteFilePath(QStringLiteral("serve.pid")),
        QDir(root).absoluteFilePath(QStringLiteral("logs/serve.pid")),
    };
    for (const QString &candidate : candidates) {
        if (QFileInfo::exists(candidate))
            return candidate;
    }
    return {};
}

qint64 CollectorProcess::readServeLeasePid()
{
    const QString path = serveLeasePidPath();
    if (path.isEmpty())
        return 0;
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly | QIODevice::Text))
        return 0;
    bool numeric = false;
    const qint64 pid = QString::fromLatin1(file.read(64)).trimmed().toLongLong(&numeric);
    return numeric && pid > 0 ? pid : 0;
}

qint64 CollectorProcess::serveLeasePidAgeMs()
{
    const QString path = serveLeasePidPath();
    if (path.isEmpty())
        return -1;
    const QDateTime written = QFileInfo(path).lastModified();
    if (!written.isValid())
        return -1;
    // Clamp: a clock that moved backwards must not make a file look ancient.
    return qMax<qint64>(0, written.msecsTo(QDateTime::currentDateTime()));
}

bool CollectorProcess::serveLeasePidIsFresh()
{
    const qint64 age = serveLeasePidAgeMs();
    return age >= 0 && age < servePidProtectionMs();
}

bool CollectorProcess::serveLeaseLooksVacant() const
{
    const qint64 pid = readServeLeasePid();
    if (pid <= 0)
        return true;
    if (pid == QCoreApplication::applicationPid())
        return true;
    // Our own child is supervised by the restart logic above; ending it here
    // would race that, and it is never what a takeover means.
    if (m_process && m_process->state() != QProcess::NotRunning
        && pid == m_process->processId()) {
        return true;
    }
    return false;
}

bool CollectorProcess::taskkillSucceeded(int exitCode)
{
    // kTaskkillNoSuchProcess is deliberately not a success here: "no such
    // process" is answered earlier, from the task list, and reaching it from a
    // kill means the caller's picture of the holder was already wrong.
    return exitCode == 0;
}

bool CollectorProcess::tasklistNamesCollector(const QString &csvOutput, qint64 pid)
{
    if (pid <= 0)
        return false;
    const QString wanted = QString::number(pid);
    const auto lines = csvOutput.split(QLatin1Char('\n'), Qt::SkipEmptyParts);
    for (const QString &line : lines) {
        const QString row = line.trimmed();
        if (row.isEmpty())
            continue;
        const QStringList fields = splitCsvRow(row);
        // "Image Name","PID","Session Name","Session#","Mem Usage"
        if (fields.size() < 2)
            continue;
        if (fields.at(1).trimmed() != wanted)
            continue;
        if (fields.at(0).trimmed().compare(QLatin1String(kCollectorImageName),
                                           Qt::CaseInsensitive)
            == 0) {
            return true;
        }
    }
    return false;
}

void CollectorProcess::setTakeoverTimingsForTest(int holderGrace, int politeKillGrace)
{
    m_holderGraceMs = holderGrace;
    m_politeKillGraceMs = politeKillGrace;
}

void CollectorProcess::runTool(const QString &tool, const QStringList &arguments,
                               std::function<void(int, const QString &)> then)
{
    // A test answers the tools from a function. The answer is still delivered
    // through the event loop, so the state machine sees exactly the ordering it
    // sees in production.
    if (m_toolRunner) {
        const ToolResult result = m_toolRunner(tool, arguments);
        QTimer::singleShot(0, this, [then = std::move(then), result] {
            then(result.exitCode, result.output);
        });
        return;
    }

    const QString program = systemTool(tool);
    if (program.isEmpty()) {
        QTimer::singleShot(0, this,
                           [then = std::move(then)] { then(kToolFailedExitCode, QString()); });
        return;
    }

    if (m_tool) {
        m_tool->disconnect(this);
        m_tool->deleteLater();
    }
    m_tool = new QProcess(this);
    m_tool->setProgram(program);
    m_tool->setArguments(arguments);
    m_tool->setProcessChannelMode(QProcess::MergedChannels);
    connect(m_tool, &QProcess::finished, this,
            [this, then](int exitCode, QProcess::ExitStatus status) {
                m_toolTimeoutTimer.stop();
                const QString output = QString::fromLocal8Bit(m_tool->readAll());
                const int code = status == QProcess::NormalExit ? exitCode : kToolFailedExitCode;
                // Never continue the sequence from inside finished(): the next
                // step deletes this very QProcess.
                QTimer::singleShot(0, this, [then, code, output] { then(code, output); });
            });
    connect(m_tool, &QProcess::errorOccurred, this, [this, then](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart)
            return;
        m_toolTimeoutTimer.stop();
        QTimer::singleShot(0, this, [then] { then(kToolFailedExitCode, QString()); });
    });
    m_tool->start();
    m_toolTimeoutTimer.start(kToolTimeoutMs);
}

/// Start our own child now that the lease is free (or believed to be). Clears
/// the backoff and the "another instance owns this" belief, and is bounded by
/// the attempt cap, because the child started here can exit on the same lease.
bool CollectorProcess::tryRelaunchForLease()
{
    if (m_stopRequested)
        return false;
    if (m_takeoverAttempts >= kMaxTakeoverAttempts) {
        setState(State::Reused, QString::fromUtf8("占用者无响应"));
        return false;
    }
    ++m_takeoverAttempts;
    m_backendConnected = false;
    m_backoffMs = minBackoffMs();
    m_restartTimer.stop();
    start();
    return true;
}

void CollectorProcess::emitTakeoverOnce(bool ok, const QString &message)
{
    if (m_takeoverToasted)
        return;
    m_takeoverToasted = true;
    Q_EMIT leaseTakeover(ok, message);
}

void CollectorProcess::finishTakeover()
{
    m_takeoverBusy = false;
    m_takeoverRequested = false;
    m_holderGraceTimer.stop();
    m_toolTimeoutTimer.stop();
}

void CollectorProcess::abortTakeover()
{
    m_takeoverRequested = false;
    if (m_tool) {
        m_tool->disconnect(this);
        if (m_tool->state() != QProcess::NotRunning)
            m_tool->kill();
        m_tool->deleteLater();
        m_tool = nullptr;
    }
    finishTakeover();
}

void CollectorProcess::beginHolderGrace()
{
    if (m_stopRequested || m_takeoverBusy)
        return;
    m_takeoverBusy = true;
    m_takeoverToasted = false;
    // Ask before doing anything else. A holder wedged on one thread can still
    // act on this, and the shutdown behind it is the only path that closes the
    // capture session row and deletes the temporary game-executable copies.
    signalStopEvent(/*requireOwnership=*/false);
    m_holderGraceUntilMs = QDateTime::currentMSecsSinceEpoch() + m_holderGraceMs;
    m_holderGraceTimer.start();
    // Probe once straight away: the ordinary hand-off is already over by now.
    onHolderGraceTick();
}

void CollectorProcess::onHolderGraceTick()
{
    if (m_stopRequested) {
        abortTakeover();
        return;
    }
    if (m_backendConnected) {
        // Somebody is serving the pipe after all. Nothing to take over, and the
        // holder gets its politeness back: the next stall starts from scratch.
        m_holderGraceDone = false;
        finishTakeover();
        return;
    }
    if (serveLeaseLooksVacant()) {
        // The holder removed its serve.pid and left - the ordinary "the other
        // Desktop quit" hand-off. Take the lease, say nothing: there is nothing
        // here for the user to act on.
        m_holderGraceDone = false;
        finishTakeover();
        tryRelaunchForLease();
        return;
    }
    if (QDateTime::currentMSecsSinceEpoch() < m_holderGraceUntilMs)
        return;

    // It had its chance.
    m_holderGraceTimer.stop();
    m_holderGraceDone = true;
    if (!m_takeoverRequested) {
        // The owner's failed-connect counter has not tripped yet. Ending a
        // process needs both halves, so stop here and wait to be asked.
        finishTakeover();
        return;
    }
    continueTakeover();
}

void CollectorProcess::requestServeLeaseTakeover()
{
    if (m_stopRequested)
        return;
    m_takeoverRequested = true;
    if (m_takeoverBusy)
        return; // folded into the running grace; it will continue from there
    if (m_takeoverAttempts >= kMaxTakeoverAttempts)
        return;

    if (serveLeaseLooksVacant()) {
        // No live holder to take the lease from: it exited, its serve.pid is
        // stale, or it is old enough not to write one. Our own child just tries
        // for the lease, which is also the ordinary "the first Desktop closed"
        // hand-off. If something is still holding it, that child exits with
        // ERR_ALREADY_RUNNING and the state returns to Reused; the attempt cap
        // and a successful connection bound this.
        m_takeoverRequested = false;
        m_takeoverToasted = false;
        if (tryRelaunchForLease()) {
            emitTakeoverOnce(
                true, QString::fromUtf8("未找到占用采集服务的进程，已重新启动采集服务。"));
        }
        return;
    }

    if (serveLeasePidIsFresh()) {
        // It took the lease seconds ago, and silence from a Collector that new
        // is what a database migration looks like from outside. Wait it out.
        qCInfo(lcCollector) << "serve-lease holder is only" << serveLeasePidAgeMs()
                            << "ms old; not ending it";
        if (!m_freshHolderReported) {
            m_freshHolderReported = true;
            m_takeoverToasted = false;
            emitTakeoverOnce(true,
                             QString::fromUtf8("采集服务刚刚启动（进程 %1），"
                                               "可能正在升级数据库，请稍候。")
                                 .arg(readServeLeasePid()));
        }
        return;
    }

    if (!m_holderGraceDone) {
        beginHolderGrace();
        return;
    }

    m_takeoverBusy = true;
    m_takeoverToasted = false;
    continueTakeover();
}

void CollectorProcess::continueTakeover()
{
    m_takeoverRequested = false;
    if (m_stopRequested) {
        abortTakeover();
        return;
    }
    if (m_takeoverAttempts >= kMaxTakeoverAttempts) {
        finishTakeover();
        return;
    }
    ++m_takeoverAttempts;
    m_takeoverPid = readServeLeasePid();
    if (serveLeaseLooksVacant()) {
        finishTakeover();
        if (tryRelaunchForLease()) {
            emitTakeoverOnce(
                true, QString::fromUtf8("未找到占用采集服务的进程，已重新启动采集服务。"));
        }
        return;
    }
    if (serveLeasePidIsFresh()) {
        finishTakeover();
        return;
    }

    // serve.pid can be stale and Windows reuses pids, so the pid alone is not
    // evidence. The system task list answers "which image is this pid right
    // now" without this process opening a handle to it
    // (docs/privacy-boundary.md section 4.2).
    runTool(QStringLiteral("tasklist.exe"),
            {QStringLiteral("/FI"), QStringLiteral("PID eq %1").arg(m_takeoverPid),
             QStringLiteral("/FO"), QStringLiteral("CSV"), QStringLiteral("/NH")},
            [this](int exitCode, const QString &output) { onHolderListed(exitCode, output); });
}

void CollectorProcess::onHolderListed(int exitCode, const QString &output)
{
    Q_UNUSED(exitCode)
    if (m_stopRequested || !m_takeoverBusy)
        return;
    if (!tasklistNamesCollector(output, m_takeoverPid)) {
        // Whatever serve.pid named, it is not a Collector any more.
        finishTakeover();
        if (tryRelaunchForLease()) {
            emitTakeoverOnce(
                true, QString::fromUtf8("未找到占用采集服务的进程，已重新启动采集服务。"));
        }
        return;
    }
    // Politely first. taskkill without /F posts WM_CLOSE, which a console
    // Collector will usually ignore - but a holder that can act on it exits
    // through its own shutdown path instead of losing whatever it was writing.
    runTool(QStringLiteral("taskkill.exe"),
            {QStringLiteral("/PID"), QString::number(m_takeoverPid)},
            [this](int code, const QString &out) { onPoliteKillFinished(code, out); });
}

void CollectorProcess::onPoliteKillFinished(int exitCode, const QString &output)
{
    Q_UNUSED(output)
    if (m_stopRequested || !m_takeoverBusy)
        return;
    if (exitCode == kTaskkillNoSuchProcess) {
        finishTakeover();
        if (tryRelaunchForLease()) {
            emitTakeoverOnce(
                true, QString::fromUtf8("未找到占用采集服务的进程，已重新启动采集服务。"));
        }
        return;
    }
    // Whether or not the tool claims to have delivered the request, only the
    // task list can say whether it was acted on.
    QTimer::singleShot(m_politeKillGraceMs, this, [this] {
        if (m_stopRequested || !m_takeoverBusy)
            return;
        runTool(QStringLiteral("tasklist.exe"),
                {QStringLiteral("/FI"), QStringLiteral("PID eq %1").arg(m_takeoverPid),
                 QStringLiteral("/FO"), QStringLiteral("CSV"), QStringLiteral("/NH")},
                [this](int code, const QString &out) { onHolderRelisted(code, out); });
    });
}

void CollectorProcess::onHolderRelisted(int exitCode, const QString &output)
{
    Q_UNUSED(exitCode)
    if (m_stopRequested || !m_takeoverBusy)
        return;
    if (!tasklistNamesCollector(output, m_takeoverPid)) {
        qCInfo(lcCollector) << "serve-lease holder" << m_takeoverPid
                            << "left on request; starting our own";
        finishTakeover();
        if (tryRelaunchForLease()) {
            emitTakeoverOnce(true,
                             QString::fromUtf8("采集服务被一个没有响应的旧实例（进程 %1）占用，"
                                               "已请求它退出并重新启动采集服务。")
                                 .arg(m_takeoverPid));
        }
        return;
    }
    runTool(QStringLiteral("taskkill.exe"),
            {QStringLiteral("/PID"), QString::number(m_takeoverPid), QStringLiteral("/F")},
            [this](int code, const QString &out) { onForcedKillFinished(code, out); });
}

void CollectorProcess::onForcedKillFinished(int exitCode, const QString &output)
{
    Q_UNUSED(output)
    if (m_stopRequested || !m_takeoverBusy)
        return;
    const qint64 pid = m_takeoverPid;
    finishTakeover();
    if (!taskkillSucceeded(exitCode)) {
        // An elevated holder refuses access, and on a Chinese Windows it says
        // so in Chinese - which is why this is judged by the exit code.
        qCWarning(lcCollector) << "taskkill refused for" << pid << "exit code" << exitCode;
        emitTakeoverOnce(false,
                         QString::fromUtf8("采集服务被没有响应的旧实例（进程 %1）占用，"
                                           "本软件无法结束它。请在任务管理器中结束它后重试。")
                             .arg(pid));
        return;
    }
    qCInfo(lcCollector) << "stalled serve-lease holder" << pid << "ended; starting our own";
    if (tryRelaunchForLease()) {
        emitTakeoverOnce(true,
                         QString::fromUtf8("采集服务被一个没有响应的旧实例（进程 %1）占用，"
                                           "已结束它并重新启动采集服务。")
                             .arg(pid));
    }
}

void CollectorProcess::noteBackendConnected(bool connected)
{
    m_backendConnected = connected;
    if (!connected)
        return;
    // A live pipe resets the backoff and re-arms the takeover, so a holder that
    // stalls later is asked politely again before anything is ended.
    m_backoffMs = minBackoffMs();
    m_takeoverAttempts = 0;
    m_holderGraceDone = false;
    m_freshHolderReported = false;
    // Anything still in flight was aimed at a holder that is now answering.
    abortTakeover();
    m_restartTimer.stop();
    if (m_state == State::Exited || m_state == State::Idle || m_state == State::Starting) {
        if (!isRunning())
            setState(State::Reused);
    }
}

void CollectorProcess::onFinished(int exitCode, QProcess::ExitStatus status)
{
    if (m_stopRequested) {
        setState(State::Stopped);
        return;
    }

    const qint64 lifetime = m_startedAtMs > 0
                                ? QDateTime::currentMSecsSinceEpoch() - m_startedAtMs
                                : 0;

    // The Collector's own answer to "another instance already holds the per-user
    // serve lease". A distinct exit code, so this does not depend on our pipe
    // having connected first: our child can lose that race, and the lifetime
    // guess below would then read a correct refusal as a crash and restart.
    if (status == QProcess::NormalExit && exitCode == kAlreadyRunningExitCode) {
        setState(State::Reused);
        return;
    }

    // The lease is held by an instance that did not answer a GetVersion probe:
    // nobody serves the pipe, so neither waiting nor restarting our child helps.
    // Silence is not evidence that the holder is dead - a Collector migrating the
    // database looks the same - so this only asks through the stop event and
    // watches; ending it is left to the owner's failed-connect counter.
    if (status == QProcess::NormalExit && exitCode == kUnresponsiveHolderExitCode) {
        setState(State::Reused, QString::fromUtf8("占用者无响应"));
        // Queued: the grace can start our own child, and starting a QProcess
        // from inside its own finished() slot is not a thing to rely on.
        QTimer::singleShot(0, this, [this] { beginHolderGrace(); });
        return;
    }

    // Older Collectors have no such code. A short-lived non-zero exit while our
    // pipe is connected means the same thing and must not trigger a restart loop.
    if (m_backendConnected && lifetime < shortLivedMs()) {
        setState(State::Reused);
        return;
    }

    const QString detail = status == QProcess::CrashExit
                               ? QString::fromUtf8("崩溃")
                               : QString::fromUtf8("代码 %1").arg(exitCode);
    setState(State::Exited, detail);
    scheduleRestart();
}

void CollectorProcess::scheduleRestart()
{
    if (m_stopRequested || !isAvailable() || m_restartTimer.isActive())
        return;
    m_scheduledDelayMs = m_backoffMs;
    m_restartTimer.start(m_scheduledDelayMs);
    // Emitted so the shell can re-render "N 秒后重启" while the timer runs.
    Q_EMIT stateChanged();
    m_backoffMs = qMin(m_backoffMs * 2, maxBackoffMs());
}

} // namespace mr
