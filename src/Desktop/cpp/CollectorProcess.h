#pragma once

// ---------------------------------------------------------------------------
// Supervises the Collector child process.
//
// The executable name is fixed and resolved next to our own binary; no path
// ever comes from a setting, an argument or a message. The only exception is
// the test-only constructor overload below.
//
// The child is always launched with "--serve --parent-pid <our pid>": stop()
// handles the orderly exit, but nothing runs on a hard kill, so the Collector
// watches us and stops itself when we are gone rather than leaving an orphan
// holding the per-user serve lease
// (src/Collector/Diagnostics/ParentProcessWatchdog.cs).
//
// Interactive runs use this by default; screenshot and unit-test runs keep the
// deterministic MockBackend and never launch a child process.
//
// Lifecycle, as reported through \ref state:
//
//   Missing   - the executable is not next to us; nothing is ever launched.
//   Idle      - available, not started yet.
//   Starting  - QProcess::start() issued, no "started" signal yet.
//   Running   - the child is alive.
//   Reused    - the child refused to start because another Collector already
//               holds the per-user serve lease. That is a success: the existing
//               instance serves the pipe, so nothing may restart it, here or in
//               the owner's reconnect loop. A holder that stops answering is
//               dealt with by requestServeLeaseTakeover(). Recognised by the
//               ERR_ALREADY_RUNNING exit code (\ref kAlreadyRunningExitCode);
//               on an older Collector, \ref noteBackendConnected plus a short
//               lifetime is the fallback guess.
//   Exited    - the child stopped on its own; a restart is scheduled with an
//               exponential backoff unless the stop was requested by us.
//   Stopped   - we asked it to stop; no restart is scheduled.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QProcess>
#include <QString>
#include <QStringList>
#include <QTimer>

#include <functional>
#include <utility>

namespace mr {

class CollectorProcess : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool available READ isAvailable CONSTANT)
    Q_PROPERTY(bool running READ isRunning NOTIFY stateChanged)
    Q_PROPERTY(QString executablePath READ executablePath CONSTANT)
    Q_PROPERTY(QString statusText READ statusText NOTIFY stateChanged)
    Q_PROPERTY(QString stateToken READ stateToken NOTIFY stateChanged)

public:
    enum class State {
        Missing,
        Idle,
        Starting,
        Running,
        Reused,
        Exited,
        Stopped,
    };
    Q_ENUM(State)

    explicit CollectorProcess(QObject *parent = nullptr);
    /// Resolve the Collector executable (env override, beside the exe, dev tree).
    static QString resolveDefaultExecutable();
    /// Test-only overload: supervises \a executablePath instead of the fixed
    /// sibling binary, so the restart backoff can be exercised against a stub.
    CollectorProcess(QString executablePath, QObject *parent);
    ~CollectorProcess() override;

    /// Absolute path of MentorRecorder.Collector.exe next to this binary.
    QString executablePath() const { return m_executablePath; }
    /// True when that file exists.
    bool isAvailable() const;
    /// Arguments the child is launched with, including the --parent-pid
    /// handshake. Exposed because that handshake is a contract with the
    /// Collector, pinned from both ends by the lifecycle tests.
    QStringList processArguments() const;
    bool isRunning() const;
    QString statusText() const;
    QString stateToken() const;
    State state() const { return m_state; }

    /// How many times an unexpected exit has been followed by a restart.
    int restartCount() const { return m_restartCount; }
    /// Delay the currently scheduled restart is waiting out, in milliseconds.
    int pendingRestartMs() const { return m_restartTimer.isActive() ? m_scheduledDelayMs : 0; }
    /// True while a restart is queued.
    bool restartPending() const { return m_restartTimer.isActive(); }

    /// Backoff bounds. The first restart waits \ref minBackoffMs.
    static constexpr int minBackoffMs() { return 800; }
    static constexpr int maxBackoffMs() { return 30000; }
    /// Below this lifetime an exit is read as "another instance already serves
    /// the pipe" rather than as a crash worth restarting immediately.
    static constexpr int shortLivedMs() { return 1500; }

    /// The Collector's exit code for ERR_ALREADY_RUNNING: it refused to serve
    /// because another instance of it already holds the per-user serve lease.
    /// That is a success for us, not a crash, and it is never restarted.
    static constexpr int kAlreadyRunningExitCode = 4;

    /// The Collector's exit code for "the serve lease is held, but the holder
    /// did not answer a GetVersion probe over several seconds". Unlike \ref
    /// kAlreadyRunningExitCode this is not a success: nobody serves the pipe.
    ///
    /// Silent is not dead - a holder running database migrations looks the same
    /// - so this only starts the sequence: the stop event goes out and the
    /// holder gets \ref holderGraceMs to leave. Ending it also needs the owner's
    /// failed-connect counter to trip; see \ref requestServeLeaseTakeover.
    static constexpr int kUnresponsiveHolderExitCode = 6;

    /// How many serve-lease takeovers may be attempted before the next
    /// successful connection. More than one because the child started after a
    /// takeover can still lose the race for the freed lease; bounded, so a
    /// holder that cannot be cleared is reported rather than fought forever.
    static constexpr int kMaxTakeoverAttempts = 3;

    /// How long a holder gets, after its own stop event has been set, to exit
    /// before it is treated as wedged. Nothing is ever ended inside this
    /// window.
    static constexpr int holderGraceMs() { return 3000; }

    /// A serve.pid written less than this long ago names a Collector that has
    /// only just taken the lease; it is never ended, however silent it looks.
    /// The first start after an upgrade runs migrations, with the pipe still
    /// down, while it writes the user's database.
    static constexpr int servePidProtectionMs() { return 15000; }

    /// taskkill is asked without /F first; this is how long the holder gets to
    /// act on that before the forced attempt.
    static constexpr int politeKillGraceMs() { return 2000; }

    /// taskkill's "there is no such process" code: the holder is already gone,
    /// which is a success for us rather than a refusal.
    static constexpr int kTaskkillNoSuchProcess = 128;

    /// True when a taskkill exit code says the process was ended.
    ///
    /// Judged by the exit code, never by the output: taskkill's messages are
    /// localised, and a refusal on a Chinese Windows
    /// (「错误: 无法终止 PID 为 N 的进程。」) carries no ASCII "ERROR" at all.
    /// 0 is success; 1 is a refusal and \ref kTaskkillNoSuchProcess is
    /// "no such process".
    static bool taskkillSucceeded(int exitCode);

    /// True when \a csvOutput - the output of "tasklist /FO CSV /NH" - carries
    /// a row for \a pid whose image name is MentorRecorder.Collector.exe.
    ///
    /// Both fields are checked: a filter that matches nothing still prints a
    /// localised sentence (「信息: 没有运行的任务匹配指定标准。」) rather than
    /// nothing, and a bare image-name search would accept another pid's row.
    static bool tasklistNamesCollector(const QString &csvOutput, qint64 pid);

    /// Milliseconds since serve.pid was last written, or -1 when there is none.
    static qint64 serveLeasePidAgeMs();
    /// True when serve.pid is too young to be ended (\ref servePidProtectionMs).
    static bool serveLeasePidIsFresh();

    /// True when no serve.pid names a holder other than us - the file is gone,
    /// or it records this process or our own child.
    ///
    /// A file check only: nothing is listed and no handle is opened, so it is
    /// cheap on every failed connect. It separates the ordinary hand-off - the
    /// holder removed its serve.pid on the way out - from a holder that is still
    /// there and not answering.
    bool serveLeaseLooksVacant() const;

    /// The result of one system-tool call: its exit code and its merged output.
    struct ToolResult {
        int exitCode = -1;
        QString output;
    };
    /// Test-only: answer the takeover's tasklist/taskkill calls from a function
    /// instead of from %SystemRoot%\System32, so the decision path - including
    /// the forced kill - can be exercised without ending anything on the
    /// machine running the tests. \a tool is the bare file name.
    using ToolRunner =
        std::function<ToolResult(const QString &tool, const QStringList &arguments)>;
    void setToolRunnerForTest(ToolRunner runner) { m_toolRunner = std::move(runner); }
    /// Test-only: shorten the two waits above so a test need not sleep seconds.
    void setTakeoverTimingsForTest(int holderGrace, int politeKillGrace);

    /// How long stop() waits for the child to honour the graceful stop request
    /// before it falls back to terminate()/kill().
    static constexpr int gracefulStopMs() { return 3000; }

    /// Name of the Collector's graceful-stop event for \a pipeName:
    /// "Local\<pipeName>.stop". Empty when \a pipeName is empty.
    ///
    /// The Collector creates this manual-reset event while it serves; setting
    /// it runs the same shutdown path Ctrl+C does, which is the only path that
    /// deletes the temporary game-executable copies and closes the capture
    /// session row (see src/Collector/Program.cs).
    static QString stopEventName(const QString &pipeName);
    /// \ref stopEventName for the current user's pipe.
    static QString currentStopEventName();
    /// Test-only: use \a name instead of \ref currentStopEventName, so a test
    /// can create and observe its own event rather than the one a Collector
    /// serving the user's real pipe is waiting on. Setting it also lifts the
    /// ownership check below, which a test has no way to satisfy.
    void setStopEventNameForTest(const QString &name) { m_stopEventName = name; }

    /// True when serve.pid names our own child, i.e. our child is the instance
    /// that currently holds the per-user serve lease.
    ///
    /// The stop event is per user, not per process: a second Desktop whose own
    /// child exited on the lease must never set it on the way out, or quitting
    /// the second window would stop the first window's Collector.
    bool childHoldsServeLease() const;

    /// True when the last stop() reached the child through its stop event
    /// rather than through terminate()/kill().
    bool lastStopWasGraceful() const { return m_lastStopWasGraceful; }

    /// Where the Collector keeps its data: MR_DATA_DIR when set, otherwise
    /// %LOCALAPPDATA%\MentorRecorder (mirrors Storage/DatabasePaths.cs).
    static QString collectorDataDirectory();
    /// The serve.pid file the lease holder writes, or an empty string when no
    /// such file exists. Looked for in the data directory and in its logs/
    /// sub-directory, which is where --log-dir puts it.
    static QString serveLeasePidPath();
    /// PID recorded in serve.pid, or 0 when there is none to read.
    static qint64 readServeLeasePid();

public Q_SLOTS:
    /// No-op when the executable is missing; statusText explains the incomplete layout.
    void start();
    /// Ask the child to stop, then kill it after a grace period. Cancels any
    /// pending restart, so a deliberate stop is never undone by the backoff.
    void stop();
    /// Told by the owner whether the Desktop currently has a live pipe. A
    /// short-lived exit while connected means another Collector owns the lease.
    void noteBackendConnected(bool connected);
    /// Free a serve lease held by a Collector that is no longer answering.
    ///
    /// Called by the owner once its failed-connect counter has tripped. Both
    /// halves of the "may we end it" test must hold - that counter, and the
    /// holder having ignored its stop event for \ref holderGraceMs - and a
    /// serve.pid younger than \ref servePidProtectionMs vetoes it regardless.
    ///
    /// The sequence, all of it asynchronous - nothing here blocks the GUI
    /// thread, and nothing is started from inside a QProcess::finished slot:
    ///
    ///   1. serve.pid gone / ours       -> just start our own child.
    ///   2. serve.pid younger than 15 s -> report and wait; never ended.
    ///   3. stop event not yet honoured -> set it, wait, re-probe.
    ///   4. tasklist                    -> is the pid really a Collector?
    ///   5. taskkill without /F, then a 2 s grace and another tasklist.
    ///   6. taskkill /F, only if it is still there.
    ///
    /// No process handle is ever opened (docs/privacy-boundary.md section 4.2):
    /// the identity check and the termination both go through the system tools,
    /// and both are judged by exit code rather than by localised output.
    ///
    /// Bounded at \ref kMaxTakeoverAttempts between successful connections,
    /// because the child started afterwards can still lose the race for the
    /// freed lease; a successful connect resets the count. Reports through
    /// \ref leaseTakeover, at most one message per attempt.
    void requestServeLeaseTakeover();

Q_SIGNALS:
    void stateChanged();
    /// Emitted after an unexpected exit, once the restart has been issued.
    void restarted(int attempt, int delayMs);
    /// Emitted once per \ref requestServeLeaseTakeover attempt. \a message is
    /// a finished Chinese sentence for the toast; \a ok says whether the
    /// stalled holder was actually cleared.
    void leaseTakeover(bool ok, const QString &message);

private:
    void wireProcess();
    void setState(State state, const QString &detail = {});
    void onFinished(int exitCode, QProcess::ExitStatus status);
    void scheduleRestart();
    /// Set the Collector's stop event, if it exists. False when there is no
    /// such event, which is what an older Collector looks like.
    /// \a requireOwnership refuses unless \ref childHoldsServeLease.
    bool signalStopEvent(bool requireOwnership);

    // -- takeover state machine (see requestServeLeaseTakeover) --------------
    /// Our child said the holder is unresponsive: set its stop event and start
    /// the grace. Never ends anything.
    void beginHolderGrace();
    /// One grace tick: has the holder gone, or has the pipe come back?
    void onHolderGraceTick();
    /// Grace is over and the holder is still there: list it and end it.
    void continueTakeover();
    void onHolderListed(int exitCode, const QString &output);
    void onPoliteKillFinished(int exitCode, const QString &output);
    void onHolderRelisted(int exitCode, const QString &output);
    void onForcedKillFinished(int exitCode, const QString &output);
    /// Clear the in-flight flags; the sequence is over.
    void finishTakeover();
    /// Cancel everything in flight - the holder answered, or we are quitting.
    void abortTakeover();
    /// Start our own child now that the serve lease is believed to be free.
    /// False when \ref kMaxTakeoverAttempts is already spent, which is what
    /// keeps a child that exits on the lease again from looping.
    bool tryRelaunchForLease();
    /// \ref leaseTakeover, at most once per attempt.
    void emitTakeoverOnce(bool ok, const QString &message);
    /// Run a %SystemRoot%\System32 tool and hand \a then its exit code and
    /// output. Asynchronous; \a then is invoked from a queued connection, never
    /// from inside QProcess::finished.
    void runTool(const QString &tool, const QStringList &arguments,
                 std::function<void(int, const QString &)> then);

    QProcess *m_process = nullptr;
    QTimer m_restartTimer;
    QString m_executablePath;
    QString m_detail;
    qint64 m_startedAtMs = 0;
    State m_state = State::Idle;
    int m_backoffMs = minBackoffMs();
    int m_scheduledDelayMs = 0;
    int m_restartCount = 0;
    bool m_stopRequested = false;
    bool m_backendConnected = false;
    bool m_lastStopWasGraceful = false;
    /// Takeovers attempted since the last successful connection, capped at
    /// \ref kMaxTakeoverAttempts so a stalled holder that cannot be cleared is
    /// reported a few times rather than on every reconnect attempt.
    int m_takeoverAttempts = 0;
    /// Empty means \ref currentStopEventName; only a test ever sets it.
    QString m_stopEventName;

    // -- takeover state machine ---------------------------------------------
    /// Polls "has the holder gone / has the pipe come back" during the grace.
    QTimer m_holderGraceTimer;
    /// Deadline of the running grace, as ms since the epoch.
    qint64 m_holderGraceUntilMs = 0;
    /// The holder the running sequence is about.
    qint64 m_takeoverPid = 0;
    /// The tasklist/taskkill child currently running, if any.
    QProcess *m_tool = nullptr;
    QTimer m_toolTimeoutTimer;
    /// True while a grace or a tool sequence is in flight; a second request is
    /// folded into the running one rather than starting a parallel sequence.
    bool m_takeoverBusy = false;
    /// True once the current holder has ignored its stop event for a whole
    /// grace. Cleared by a successful connection, so a holder that stalls later
    /// is asked politely again before anything is ended.
    bool m_holderGraceDone = false;
    /// The owner's failed-connect counter has tripped and is waiting for the
    /// grace to finish.
    bool m_takeoverRequested = false;
    /// One \ref leaseTakeover per attempt.
    bool m_takeoverToasted = false;
    /// "It only just started" has been said once; saying it on every attempt
    /// while a migration runs would be a toast every few seconds.
    bool m_freshHolderReported = false;
    int m_holderGraceMs = holderGraceMs();
    int m_politeKillGraceMs = politeKillGraceMs();
    /// Empty in the shipping build; a test answers the tools from here.
    ToolRunner m_toolRunner;
};

} // namespace mr
