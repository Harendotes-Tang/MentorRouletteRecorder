#include "DailyBackupScheduler.h"

namespace mr {

DailyBackupScheduler::DailyBackupScheduler(ContextProvider readContext, QObject *parent)
    : QObject(parent), m_readContext(std::move(readContext))
{
    m_dailyTimer.setInterval(60 * 60 * 1000);
    connect(&m_dailyTimer, &QTimer::timeout, this, &DailyBackupScheduler::runIfDue);
    m_afterDutyTimer.setSingleShot(true);
    // 完成提示、心得保存及播报先使用同一管道，避免备份长请求拖住它们。
    m_afterDutyTimer.setInterval(5 * 60 * 1000);
    connect(&m_afterDutyTimer, &QTimer::timeout, this, &DailyBackupScheduler::runIfDue);
}

void DailyBackupScheduler::runIfDue()
{
    if (!m_active) {
        m_active = true;
        m_dailyTimer.start();
    }
    const Context context = m_readContext();
    const bool inDuty = context.runKnown && context.dutyInProgress;
    // 即使今天已经备份或开关关闭，仍记住导随结束，以便稍后启用/跨午夜时避让。
    if (inDuty) {
        m_dutySeen = true;
        m_afterDutyTimer.stop();
    } else if (context.runKnown && m_dutySeen) {
        m_dutySeen = false;
        m_afterDutyTimer.start();
    }
    if (!context.enabled)
        return;
    const QString today = m_today().toString(QStringLiteral("yyyy-MM-dd"));
    if (context.lastBackupDate == today || m_attemptedOn == today)
        return;
    if (!context.connected || !context.runKnown || inDuty || m_afterDutyTimer.isActive())
        return;
    // 先登记尝试，允许接收方同步完成或重入；失败当天不刷屏，次日可再次尝试。
    m_attemptedOn = today;
    Q_EMIT backupRequested();
}

void DailyBackupScheduler::checkIfActive()
{
    if (m_active)
        runIfDue();
}

void DailyBackupScheduler::recordSuccess()
{
    Q_EMIT backupRecorded(m_today().toString(QStringLiteral("yyyy-MM-dd")));
}

} // namespace mr
