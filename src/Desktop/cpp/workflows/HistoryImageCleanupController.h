#pragma once

#include "LiveText.h"

#include <QObject>
#include <QPointer>
#include <QStringList>
#include <QTimer>

namespace mr {

class IBackend;
class NoteImageStore;

/// 历史记录清理后的附件任务：按页获取、逐记录让出事件循环、只确认成功项。
/// 借用后端及图片存储，拥有重试定时器和在途队列；回调以本对象为上下文，销毁后不执行。
class HistoryImageCleanupController final : public QObject
{
    Q_OBJECT
public:
    explicit HistoryImageCleanupController(IBackend *backend, QObject *parent = nullptr);
    /// 非空存储启动分钟重试并立即检查；空值停止定时检查，在途批次按原失败路径收尾。
    void setStore(NoteImageStore *store);
    void retry();
    bool running() const { return m_running; }
    QString feedback() const { return m_feedback.text(); }

Q_SIGNALS:
    void changed();
    void toastRequested(const QString &message);

private:
    void drain();
    void finish(const LiveText &feedback);

    QPointer<IBackend> m_backend;
    QPointer<NoteImageStore> m_store;
    QTimer m_timer;
    bool m_running = false;
    LiveText m_feedback;
    QStringList m_ids;
    QStringList m_successful;
    QStringList m_failures;
    int m_index = 0;
};

} // namespace mr
