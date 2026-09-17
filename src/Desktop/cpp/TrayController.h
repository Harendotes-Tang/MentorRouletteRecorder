#pragma once

// ---------------------------------------------------------------------------
// Windows notification-area icon.
//
// Menu (prototype "托盘菜单：显示 / 开始捕获 / 退出"):
//   显示        - raise the main window
//   开始/停止捕获 - forwards to AppController::toggleCapture(), i.e. to the backend
//   退出        - quit for real, bypassing the minimise-to-tray close handler
//
// The icon is only created when the platform actually has a tray (never under
// the offscreen platform used for screenshots and CI).
// ---------------------------------------------------------------------------

#include <QObject>

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

public Q_SLOTS:
    void showWindow();

protected:
    /// Turns the window's close into "hide to tray" while 关闭时最小化到托盘
    /// is on and a tray actually exists.
    bool eventFilter(QObject *watched, QEvent *event) override;

private:
    void refreshCaptureAction();

    AppController *m_controller = nullptr;
    AppSettings *m_settings = nullptr;
    QWindow *m_window = nullptr;
    QSystemTrayIcon *m_tray = nullptr;
    /// Owned outright: a QMenu is a QWidget and cannot be parented to a plain
    /// QObject, so nothing else would ever delete it.
    std::unique_ptr<QMenu> m_menu;
    QAction *m_captureAction = nullptr;
};

} // namespace mr
