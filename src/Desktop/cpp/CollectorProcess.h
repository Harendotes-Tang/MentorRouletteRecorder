#pragma once

// ---------------------------------------------------------------------------
// Supervises the Collector child process.
//
// The executable name is fixed and resolved next to our own binary; no path
// ever comes from a setting, an argument, a message or the environment. The
// only exceptions are the test-only constructor overload below and a build
// compiled with MR_DEV_COLLECTOR_DISCOVERY, which also honours
// MR_COLLECTOR_PATH and finds the Collector in a source checkout.
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
//               exponential backoff unless the stop was requested by us, or
//               the child refused to start for a reason no restart can cure
//               (\ref isStartRefused).
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
    /// Resolve the Collector executable: beside the exe, or in its collector/
    /// folder. Only an MR_DEV_COLLECTOR_DISCOVERY build also honours
    /// MR_COLLECTOR_PATH and the build output of a source checkout.
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
    /// A child that stayed up at least this long starts the backoff afresh
    /// when it exits. A connection alone does not: a Collector that crashes a
    /// few seconds after every start must not be restarted every second.
    static constexpr int stableRunMs() { return 60000; }

    /// The Collector's exit code for a command line it cannot parse. The
    /// command line is ours and the same on every restart.
    static constexpr int kBadArgumentsExitCode = 2;
    /// The Collector's exit code for "cannot do its job": the database failed
    /// its integrity check or a migration, or the data folder is unusable.
    /// Read before the pipe opens, a restart meets the same file again.
    static constexpr int kCannotServeExitCode = 3;

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
    /// before it falls back to terminate()/kill(). The Collector's own shutdown
    /// may wait up to five seconds for the pipe to drain and five more for the
    /// calibration evidence to flush, and its watchdog ends it at ten; less
    /// than that kills it in the middle of a write.
    static constexpr int gracefulStopMs() { return 10000; }
    /// Test-only: shorten that wait so a stub that ignores the request does
    /// not hold a test for the whole budget.
    void setGracefulStopMsForTest(int milliseconds) { m_gracefulStopMs = milliseconds; }
    /// Test-only: replaces \ref stableRunMs.
    void setStableRunMsForTest(int milliseconds) { m_stableRunMs = milliseconds; }
    /// Test-only: replaces \ref refusedRetryMs.
    void setRefusedRetryMsForTest(int milliseconds) { m_refusedRetryTimer.setInterval(milliseconds); }

    /// True once the child refused to start in a way the restart loop cannot
    /// cure: \ref kBadArgumentsExitCode, \ref kCannotServeExitCode before it
    /// ever served the pipe, or Windows refusing the launch twice in a row.
    /// Neither the backoff nor the owner's reconnect loop restarts it then.
    /// Only a \ref kCannotServeExitCode refusal - a database or folder another
    /// program may be holding for a while - is tried again, quietly, every
    /// \ref refusedRetryMs; an explicit start() always tries at once.
    bool isStartRefused() const { return m_startRefused; }
    static constexpr int refusedRetryMs() { return 5 * 60 * 1000; }
    /// The sentence a player reads for a refused start with \a exitCode: the
    /// last line the child wrote to its standard error, without the error
    /// token or "failed:" in front of it, or a sentence of ours when there is
    /// nothing usable. \a stderrTail is UTF-8, which the Collector writes.
    static QString exitExplanation(int exitCode, const QByteArray &stderrTail);
    /// How much of the child's standard error is kept for that sentence.
    static constexpr int kStderrTailBytes = 4096;

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
    /// serving the user's real pipe is waiting on. It also lifts the check
    /// that the child is a Collector at all, which a stub never is; the
    /// serve.pid check in \ref childHoldsServeLease still applies.
    void setStopEventNameForTest(const QString &name) { m_stopEventName = name; }

    /// True when our own child is running and no serve.pid names another
    /// process - i.e. our child is the instance that holds, or is about to
    /// hold, the per-user serve lease.
    ///
    /// The Collector writes serve.pid only once it serves, after the database
    /// checks and migrations, so a missing file is our child still starting.
    /// A file naming another process means another Collector holds the lease
    /// and ours is only checking on it before it exits: the stop event is per
    /// user, not per process, and setting it then would stop the instance that
    /// is recording.
    bool childHoldsServeLease() const;

    /// True when the last stop() reached the child through its stop event
    /// rather than through terminate()/kill().
    bool lastStopWasGraceful() const { return m_lastStopWasGraceful; }

    /// Where the Collector keeps its data: MR_DATA_DIR when set, otherwise
    /// %LOCALAPPDATA%\MentorRecorder (mirrors Storage/DatabasePaths.cs). Read
    /// from Windows itself, never through QStandardPaths: its test mode, which
    /// a screenshot run switches on, would move the answer under ...\qttest
    /// while the Collector keeps writing to the real folder.
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
    /// The child refused to start for a reason no restart can cure.
    void startRefused(const QString &message);

private:
    void wireProcess();
    void setState(State state, const QString &detail = {});
    void onFinished(int exitCode, QProcess::ExitStatus status);
    void scheduleRestart();
    /// Stop the restart loop after a start it cannot cure, and say why - once
    /// per reason. \a retryLater arms the quiet retry.
    void refuseStart(const QString &reason, bool retryLater);
    /// Keep the last \ref kStderrTailBytes of what the child wrote to stderr.
    void collectStderr();
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
    /// The quiet retry after a refused exit 3 (\ref refusedRetryMs).
    QTimer m_refusedRetryTimer;
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
    int m_gracefulStopMs = gracefulStopMs();
    int m_stableRunMs = stableRunMs();
    bool m_startRefused = false;
    /// The pipe was connected while the current child ran: it got as far as
    /// serving, so an exit 3 later is a fault of a running Collector, worth a
    /// restart, rather than a refused start.
    bool m_childServed = false;
    /// Windows refused to launch the child this many times in a row.
    int m_failedLaunches = 0;
    /// The end of the current child's standard error.
    QByteArray m_stderrTail;
    /// The reason last reported through \ref startRefused, so a quiet retry
    /// refused for the same reason says nothing new. Cleared once a child
    /// serves.
    QString m_lastRefusal;
    /// Empty in the shipping build; a test answers the tools from here.
    ToolRunner m_toolRunner;
};

} // namespace mr
