#include "HistoryImageCleanupController.h"

#include "IBackend.h"
#include "NoteImageStore.h"

#include <QCoreApplication>

namespace mr {

HistoryImageCleanupController::HistoryImageCleanupController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
{
    m_timer.setInterval(60000);
    connect(&m_timer, &QTimer::timeout, this, &HistoryImageCleanupController::retry);
    onLanguageChanged(this, &HistoryImageCleanupController::changed);
}

void HistoryImageCleanupController::setStore(NoteImageStore *store)
{
    m_store = store;
    if (store) {
        m_timer.start();
        retry();
    } else {
        m_timer.stop();
    }
}

void HistoryImageCleanupController::retry()
{
    if (!m_store || !m_backend || !m_backend->isConnected() || m_running)
        return;
    m_running = true;
    Q_EMIT changed();
    m_backend->getPendingImageCleanup()
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (!ok) {
                finish(LiveText([reason = message.isEmpty() ? code : message] {
                    return QCoreApplication::translate("mr::AppController", "没能获取待清理的图片：%1").arg(reason);
                }));
                return;
            }
            m_ids = payload.value(QStringLiteral("run_ids")).toStringList();
            m_successful.clear();
            m_failures.clear();
            m_index = 0;
            drain();
        });
}

void HistoryImageCleanupController::drain()
{
    if (!m_store) {
        finish(LiveText([] {
            return QCoreApplication::translate("mr::AppController", "图片服务已关闭，清理任务仍保留，稍后重试。");
        }));
        return;
    }
    if (m_index < m_ids.size()) {
        const QString id = m_ids.at(m_index++);
        const QVariantMap result = m_store->cleanupRun(id);
        if (result.value(QStringLiteral("ok")).toBool())
            m_successful.append(id);
        else
            m_failures.append(id.left(8) + QStringLiteral(": ") + result.value(QStringLiteral("error")).toString());
        // 每个记录之间让出事件循环，不阻塞整批最多 2000 条的清理。
        QTimer::singleShot(0, this, &HistoryImageCleanupController::drain);
        return;
    }
    const LiveText failures = m_failures.isEmpty() ? LiveText()
        : LiveText([count = m_failures.size(), first = m_failures.first()] {
              return QCoreApplication::translate("mr::AppController", "%1 条记录的图片暂时没能清理，软件会自动重试。%2")
                  .arg(count).arg(first);
          });
    if (m_successful.isEmpty()) {
        finish(failures);
        return;
    }
    if (!m_backend) {
        // 后端先析构时不能确认；已删除文件仍可幂等重试，服务端待办保持未确认。
        finish(LiveText([failures] {
            return QCoreApplication::translate("mr::AppController", "图片已清理，但清理结果还没保存，软件会自动重试：%1 %2")
                .arg(QStringLiteral("ERR_PIPE_CLOSED"), failures.text());
        }));
        return;
    }
    m_backend->acknowledgeImageCleanup(m_successful)
        ->whenDone(this, [this, failures](bool ok, const QVariantMap &, const QString &code, const QString &message) {
            if (!ok) {
                finish(LiveText([reason = message.isEmpty() ? code : message, failures] {
                    return QCoreApplication::translate("mr::AppController", "图片已清理，但清理结果还没保存，软件会自动重试：%1 %2")
                        .arg(reason, failures.text());
                }));
                return;
            }
            const bool nextBatch = failures.isEmpty() && m_ids.size() == 2000;
            finish(failures);
            if (nextBatch)
                QTimer::singleShot(0, this, &HistoryImageCleanupController::retry);
        });
}

void HistoryImageCleanupController::finish(const LiveText &feedback)
{
    const QString text = feedback.text();
    const bool different = text != m_feedback.text();
    m_running = false;
    m_feedback = feedback;
    Q_EMIT changed();
    if (different && !text.isEmpty())
        Q_EMIT toastRequested(text);
}

} // namespace mr
