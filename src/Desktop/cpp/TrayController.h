#pragma once

// ---------------------------------------------------------------------------
// Windows notification-area icon.
//
// Menu, top to bottom:
//   显示主窗口          - raise the main window
//   待处理（N）          - raise the main window and open the 待处理 list
//                          (RecoveryController::requestInbox); N is its count
//   显示/隐藏统计浮窗    - StatisticsOverlayController::toggle()
//   解锁统计浮窗        - StatisticsOverlayController::unlock(); only while the
//                          float is locked
//   开始/停止捕获        - forwards to AppController::toggleCapture(), i.e. to the
//                          backend; only while the maintainer tools are shown
//   退出                - quit for real, bypassing the minimise-to-tray close handler
//
// The icon is only created when the platform actually has a tray (never under
// the offscreen platform used for screenshots and CI).
// ---------------------------------------------------------------------------

#include <QObject>
#include <QPointer>

#include <memory>

class QAction;
class QMenu;
class QSystemTrayIcon;
class QWindow;

namespace mr {

class AppController;
class AppSettings;

class TrayController : public QObject
{
    Q_OBJECT

public:
    TrayController(AppController *controller, AppSettings *settings, QWindow *window,
                   QObject *parent = nullptr);
    ~TrayController() override;

    /// False when the platform has no notification area; nothing was created.
    bool isActive() const { return m_tray != nullptr; }

    /// Set by the close handler so 退出 can bypass minimise-to-tray.
    static bool quitRequested();
    static void requestQuit();
    /// The application's one orderly exit: 退出, closing the window while
    /// 关闭时最小化到托盘 is off, and 立即安装 once the installer has started.
    /// The event loop returns and main()'s destructors stop the Collector child
    /// through its stop event (CollectorProcess::stop).
    static void quitApplication();

public Q_SLOTS:
    void showWindow();

Q_SIGNALS:
    /// Emitted once when this consumer accepts a warning serial for sound and
    /// system notification requests. It does not attest to OS/audio delivery.
    /// Offscreen test platforms suppress the native requests after this signal.
    void recordingWarningRequested(quint64 serial, bool systemTrayAvailable);

public:
    /// Turns the window's close into "hide to tray" while 关闭时最小化到托盘
    /// is on and a tray actually exists.
    bool eventFilter(QObject *watched, QEvent *event) override;

protected:
    /// The native delivery boundary; overriding it lets tests count real
    /// dispatch attempts without making sounds or showing desktop messages.
    virtual void deliverRecordingWarning(const QString &message);

private:
    void refreshCaptureAction();
    void notifyRecordingWarning();
    /// The menu's and the icon's own words, set again after a language switch.
    void retranslate();

    /// The queued startup replay can outlive the controller when a direct
    /// warning consumer closes it before that replay reaches the event loop.
    QPointer<AppController> m_controller;
    AppSettings *m_settings = nullptr;
    QWindow *m_window = nullptr;
    QSystemTrayIcon *m_tray = nullptr;
    quint64 m_lastWarningSerial = 0;
    /// Owned outright: a QMenu is a QWidget and cannot be parented to a plain
    /// QObject, so nothing else would ever delete it.
    std::unique_ptr<QMenu> m_menu;
    QAction *m_showAction = nullptr;
    QAction *m_pendingWorkAction = nullptr;
    QAction *m_statisticsAction = nullptr;
    QAction *m_unlockStatisticsAction = nullptr;
    QAction *m_captureAction = nullptr;
    QAction *m_quitAction = nullptr;
};

} // namespace mr
