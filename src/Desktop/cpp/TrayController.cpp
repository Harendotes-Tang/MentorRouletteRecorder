#include "TrayController.h"

#include "AppController.h"
#include "AppSettings.h"
#include "LiveText.h"

#include <QAction>
#include <QCloseEvent>
#include <QApplication>
#include <QIcon>
#include <QMenu>
#include <QPainter>
#include <QPixmap>
#include <QSystemTrayIcon>
#include <QTimer>
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
    return {pixmap};
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

void TrayController::quitApplication()
{
    requestQuit();
    QCoreApplication::quit();
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
    m_showAction = m_menu->addAction(QString());
    connect(m_showAction, &QAction::triggered, this, &TrayController::showWindow);
    m_pendingWorkAction = m_menu->addAction(QString());
    m_pendingWorkAction->setObjectName(QStringLiteral("pendingWorkAction"));
    m_pendingWorkAction->setEnabled(m_controller != nullptr);
    connect(m_pendingWorkAction, &QAction::triggered, this, [this] {
        showWindow();
        // The main window is shown before its overlay is requested. Leave
        // one event-loop turn for its visibility bindings to catch up.
        QTimer::singleShot(0, this, [this] {
            if (m_controller)
                m_controller->recovery()->requestInbox();
        });
    });
    if (m_controller) {
        connect(m_controller->recovery(), &RecoveryController::changed,
                this, &TrayController::retranslate);
    }
    m_statisticsAction = m_menu->addAction(QString());
    m_statisticsAction->setObjectName(QStringLiteral("statisticsFloatingWindowAction"));
    m_unlockStatisticsAction = m_menu->addAction(QString());
    m_unlockStatisticsAction->setObjectName(QStringLiteral("unlockStatisticsFloatingWindowAction"));
    if (m_controller) {
        auto *overlay = m_controller->statisticsOverlay();
        connect(m_statisticsAction, &QAction::triggered, overlay, &StatisticsOverlayController::toggle);
        connect(m_unlockStatisticsAction, &QAction::triggered, overlay, &StatisticsOverlayController::unlock);
        connect(overlay, &StatisticsOverlayController::visibleChanged, this, &TrayController::retranslate);
        connect(overlay, &StatisticsOverlayController::lockedChanged, this, &TrayController::retranslate);
    } else {
        m_statisticsAction->setEnabled(false);
    }

    // With a controller its text is the capture action's label (refreshCaptureAction).
    m_captureAction = m_menu->addAction(tr("开始捕获"));
    connect(m_captureAction, &QAction::triggered, this, [this] {
        if (m_controller)
            m_controller->toggleCapture();
    });

    m_menu->addSeparator();
    m_quitAction = m_menu->addAction(QString());
    connect(m_quitAction, &QAction::triggered, this, [] { quitApplication(); });

    m_tray = new QSystemTrayIcon(this);
    m_tray->setIcon(fallbackIcon());
    m_tray->setContextMenu(m_menu.get());
    retranslate();
    onLanguageChanged(this, &TrayController::retranslate);
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
    quitApplication();
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

void TrayController::retranslate()
{
    if (!m_tray)
        return;
    m_showAction->setText(tr("显示主窗口"));
    if (m_pendingWorkAction)
        m_pendingWorkAction->setText(tr("待处理（%1）")
                                       .arg(m_controller ? m_controller->recovery()->count() : 0));
    if (m_statisticsAction) {
        m_statisticsAction->setText(m_controller && m_controller->statisticsOverlay()->visible()
                                   ? tr("隐藏统计浮窗") : tr("显示统计浮窗"));
        m_unlockStatisticsAction->setText(tr("解锁统计浮窗"));
        m_unlockStatisticsAction->setVisible(m_controller && m_controller->statisticsOverlay()->locked());
    }
    m_quitAction->setText(tr("退出"));
    m_tray->setToolTip(tr("FF14 导随记录器"));
    refreshCaptureAction();
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
