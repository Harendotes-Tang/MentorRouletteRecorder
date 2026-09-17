#include "TrayController.h"

#include "AppController.h"
#include "AppSettings.h"

#include <QAction>
#include <QCloseEvent>
#include <QApplication>
#include <QIcon>
#include <QMenu>
#include <QPainter>
#include <QPixmap>
#include <QSystemTrayIcon>
#include <QWindow>

namespace {

bool g_quitRequested = false;

/// A tiny generated glyph, so the tray needs no embedded FINAL FANTASY XIV
/// asset.
QIcon fallbackIcon()
{
    QPixmap pixmap(32, 32);
    pixmap.fill(Qt::transparent);
    QPainter painter(&pixmap);
    painter.setRenderHint(QPainter::Antialiasing, true);
    painter.setPen(Qt::NoPen);
    painter.setBrush(QColor(QStringLiteral("#0a84ff")));
    painter.drawRoundedRect(QRectF(1, 1, 30, 30), 8, 8);
    painter.setPen(QPen(QColor(QStringLiteral("#ffffff")), 3));
    painter.setBrush(Qt::NoBrush);
    painter.drawEllipse(QRectF(8, 8, 16, 16));
    painter.end();
    return QIcon(pixmap);
}

} // namespace

namespace mr {

bool TrayController::quitRequested()
{
    return g_quitRequested;
}

void TrayController::requestQuit()
{
    g_quitRequested = true;
}

TrayController::TrayController(AppController *controller, AppSettings *settings,
                               QWindow *window, QObject *parent)
    : QObject(parent)
    , m_controller(controller)
    , m_settings(settings)
    , m_window(window)
{
    if (!QSystemTrayIcon::isSystemTrayAvailable())
        return;

    m_menu = std::make_unique<QMenu>();
    QAction *showAction = m_menu->addAction(QString::fromUtf8("显示"));
    connect(showAction, &QAction::triggered, this, &TrayController::showWindow);

    m_captureAction = m_menu->addAction(QString::fromUtf8("开始捕获"));
    connect(m_captureAction, &QAction::triggered, this, [this] {
        if (m_controller)
            m_controller->toggleCapture();
    });

    m_menu->addSeparator();
    QAction *quitAction = m_menu->addAction(QString::fromUtf8("退出"));
    connect(quitAction, &QAction::triggered, this, [] {
        requestQuit();
        QCoreApplication::quit();
    });

    m_tray = new QSystemTrayIcon(this);
    m_tray->setIcon(fallbackIcon());
    m_tray->setToolTip(QString::fromUtf8("FF14 导随记录器"));
    m_tray->setContextMenu(m_menu.get());
    connect(m_tray, &QSystemTrayIcon::activated, this,
            [this](QSystemTrayIcon::ActivationReason reason) {
                if (reason == QSystemTrayIcon::Trigger
                    || reason == QSystemTrayIcon::DoubleClick) {
                    showWindow();
                }
            });
    connect(m_tray, &QSystemTrayIcon::messageClicked, this, &TrayController::showWindow);
    m_tray->show();

    if (m_controller) {
        connect(m_controller->recording(), &AutomaticRecordingController::incidentRaised, this, [this] {
            if (m_window && (!m_window->isVisible() || m_window->windowState() == Qt::WindowMinimized))
                m_tray->showMessage(tr("无法自动记录"), m_controller->recording()->message(),
                                    QSystemTrayIcon::Warning);
        });
        connect(m_controller, &AppController::maintainerToolsChanged, this, &TrayController::refreshCaptureAction);
        connect(m_controller, &AppController::validationChanged, this,
                &TrayController::refreshCaptureAction);
    }
    refreshCaptureAction();

    if (m_window)
        m_window->installEventFilter(this);
}

bool TrayController::eventFilter(QObject *watched, QEvent *event)
{
    if (watched != m_window || event->type() != QEvent::Close || quitRequested())
        return QObject::eventFilter(watched, event);

    if (m_tray && m_settings && m_settings->minimizeToTray()) {
        event->ignore();
        m_window->hide();
        return true;
    }

    // 关闭时最小化到托盘 is off: closing the window ends the process. Quitting
    // explicitly is what runs the destructors that stop the Collector child,
    // so a closed window never leaves an orphaned server behind.
    requestQuit();
    QCoreApplication::quit();
    return QObject::eventFilter(watched, event);
}

// Out of line: std::unique_ptr<QMenu> needs the complete type to destroy it,
// and the header only forward-declares QMenu.
TrayController::~TrayController() = default;

void TrayController::refreshCaptureAction()
{
    if (!m_captureAction || !m_controller)
        return;
    m_captureAction->setText(m_controller->captureActionLabel());
    m_captureAction->setVisible(m_controller->maintainerToolsVisible());
    m_captureAction->setEnabled(m_controller->captureActionEnabled());
}

void TrayController::showWindow()
{
    if (!m_window)
        return;
    m_window->showNormal();
    m_window->raise();
    m_window->requestActivate();
}

} // namespace mr
