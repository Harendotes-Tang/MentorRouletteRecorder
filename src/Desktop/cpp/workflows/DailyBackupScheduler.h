#pragma once

#include <QDate>
#include <QObject>
#include <QString>
#include <QTimer>

#include <functional>
#include <utility>

namespace mr {

/// 每日备份的尝试频率与导随后避让。仅消费当前状态快照，不拥有后端或设置；所有入口
/// 均在 QObject 所属线程调用。父对象销毁时，两只定时器一并停止，不再请求备份。
class DailyBackupScheduler final : public QObject
{
    Q_OBJECT
public:
    struct Context {
        bool enabled = false;
        QString lastBackupDate;
        bool connected = false;
        bool runKnown = false;
        bool dutyInProgress = false;
    };
    using ContextProvider = std::function<Context()>;

    /// readContext 必须非空，每次检查返回即时快照；其捕获对象必须活到本调度器销毁。
    explicit DailyBackupScheduler(ContextProvider readContext, QObject *parent = nullptr);

    bool active() const { return m_active; }
    /// 首次调用启用小时检查。离线、当前记录未知、导随中或刚结束均不消耗当日尝试。
    void runIfDue();
    /// 当前记录读取成功后调用；从未由交互启动的调度器保持停用。
    void checkIfActive();
    /// 只在备份确认成功后调用，以完成时的本地日期通知设置存储。
    void recordSuccess();

    void setTodayForTest(std::function<QDate()> today) { m_today = std::move(today); }
    void setCheckIntervalForTest(int milliseconds) { m_dailyTimer.setInterval(milliseconds); }
    void setAfterDutyIntervalForTest(int milliseconds) { m_afterDutyTimer.setInterval(milliseconds); }

Q_SIGNALS:
    void backupRequested();
    void backupRecorded(const QString &date);

private:
    ContextProvider m_readContext;
    std::function<QDate()> m_today = [] { return QDate::currentDate(); };
    QString m_attemptedOn;
    bool m_active = false;
    bool m_dutySeen = false;
    QTimer m_dailyTimer;
    QTimer m_afterDutyTimer;
};

} // namespace mr
