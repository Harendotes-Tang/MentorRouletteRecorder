#pragma once

#include <QDateTime>
#include <QJsonObject>
#include <QList>
#include <QObject>
#include <QSet>
#include <QString>
#include <QVariantMap>

#include <functional>

namespace mr {

/// 完成事件的刷新、提示队列和播报时序。只借用回调，不拥有后端、恢复日志或语音引擎；
/// 所有入口及回调都在所属 QObject 线程执行，组合入口须保证回调捕获对象活到本对象销毁。
class RunCompletionCoordinator final : public QObject
{
    Q_OBJECT
public:
    using DashboardDone = std::function<void(bool numbersKnown)>;

    /// 组合入口提供的窄操作。requestDashboard 可同步完成，也可在本对象销毁后完成；
    /// 调用 done 前须先接纳统计快照及其通知，并且至多调用一次。所有回调必须提供；
    /// 空 done 表示只刷新，不等待播报。
    struct Hooks {
        std::function<void()> refreshHistory;
        std::function<void()> refreshCurrentRun;
        std::function<void()> refreshReflections;
        std::function<void()> refreshRecovery;
        std::function<void(const QJsonObject &)> adoptRunRevision;
        std::function<void(DashboardDone)> requestDashboard;
        std::function<QString()> databaseKey;
        std::function<bool()> identityVerified;
        std::function<bool()> reflectionPromptEnabled;
        std::function<bool()> resultPromptEnabled;
        std::function<void(const QVariantMap &, const QString &kind)> offerRecovery;
    };

    /// 一次操作所属的恢复库和连接代次；未知库的首次身份接纳仍属于同一上下文。
    struct Context {
        QString databaseKey;
        quint64 generation = 0;
    };

    explicit RunCompletionCoordinator(Hooks hooks, QObject *parent = nullptr,
                                      QDateTime cutoffUtc = QDateTime::currentDateTimeUtc());

    /// StateChanged 已刷新当前记录并处理在途播报；此处先刷新统计，再按完成状态登记心得。
    void stateChanged(const QString &state, const QJsonObject &run);
    /// 依次刷新历史、当前记录、心得和恢复项，再接纳事件修订；只有 finished 才登记结果
    /// 问题并等待本次统计完成后请求终态播报。非完成事件仍可提供已完成记录的心得提示。
    void runChanged(const QString &state, const QJsonObject &run, bool finished);

    Context captureContext() const;
    bool isCurrentContext(const Context &context) const;
    /// 断线/重连令旧统计和保存回调失效。clearPrompts 仅用于恢复库切换，在界面关闭旧
    /// 编辑器前丢弃其队列和确认状态；已播报状态与启动截止时间跨连接和库切换保留。
    void invalidateContext(bool clearPrompts);

    /// 对旧会话记录、已写心得、已询问结果或关闭了对应设置的记录静默忽略；先持久化
    /// 恢复项，身份确认后才加入最多 16 条的自动提示队列。
    void offerReflection(const QJsonObject &run);
    void offerResult(const QString &state, const QJsonObject &run);
    void reflectionPromptShown(const QString &runId);
    void reflectionPromptClosed();
    void reflectionPromptDeferred(const QString &runId);
    void resultConfirmationShown(const QString &runId);
    void resultConfirmationClosed();
    void resultConfirmationDeferred(const QString &runId);
    /// 其他入口已回答结果时，撤销同记录的待提示问题；不表示当前弹窗已关闭。
    void runReviewed(const QString &runId);
    /// 保存或明确清空心得后，本会话不再自动提示该记录。
    void reflectionSaved(const QString &runId);

    bool emittedBeforeThisSession(const QVariantMap &event) const;
    bool endedDuringThisSession(const QJsonObject &run) const;
    /// 为在途播报和完成播报共用去重集合；重复匹配 offer>1 使用独立键，空 run_id 不去重。
    bool claimAnnouncement(const QString &runId, const QString &state, int matchOffer = 0);
    static QString announcementKind(const QString &state);
    void setCutoffForTest(const QDateTime &utc) { m_cutoffUtc = utc; }

Q_SIGNALS:
    void reflectionPromptRequested(const QVariantMap &run);
    void resultConfirmationRequested(const QVariantMap &run);
    /// 统计快照已完成接纳；numbersKnown 是本次读取结果，失败时呈现层可保留已有快照。
    void announcementRequested(const QString &state, const QJsonObject &run, bool numbersKnown);

private:
    void emitNextReflectionPrompt();
    void emitNextResultConfirmation();
    Hooks m_hooks;
    quint64 m_generation = 0;
    QDateTime m_cutoffUtc;
    QSet<QString> m_announcedRunStates;
    QSet<QString> m_promptedRunIds;
    QSet<QString> m_confirmedRunIds;
    QList<QJsonObject> m_queuedResultRuns;
    QSet<QString> m_offeredResultRunIds;
    bool m_resultConfirmationBusy = false;
    QList<QJsonObject> m_queuedReflectionRuns;
    QSet<QString> m_offeredReflectionRunIds;
    bool m_reflectionPromptBusy = false;
    static constexpr int kMaximumQueuedPrompts = 16;
};

} // namespace mr
