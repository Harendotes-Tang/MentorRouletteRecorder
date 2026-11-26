#pragma once

#include <QImage>
#include <QObject>
#include <QPointer>
#include <QSharedPointer>
#include <QString>
#include <QVariantList>
#include <functional>

class QQuickItemGrabResult;
class QQuickItem;
class QWindow;
namespace mr {
/// 保存完整的 QML 分享卡片；不读取或更改记录、心得和采集数据。
class ReflectionShareController final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString feedback READ feedback NOTIFY changed)
    Q_PROPERTY(QString savedPath READ savedPath NOTIFY changed)
    Q_PROPERTY(QVariantList batchResults READ batchResults NOTIFY changed)
    Q_PROPERTY(int successfulCount READ successfulCount NOTIFY changed)
    Q_PROPERTY(int failedCount READ failedCount NOTIFY changed)
public:
    explicit ReflectionShareController(QObject *parent = nullptr);
    bool busy() const { return m_busy; }
    QString feedback() const { return m_feedback; }
    QString savedPath() const { return m_savedPath; }
    QVariantList batchResults() const { return m_batchResults; }
    int successfulCount() const;
    int failedCount() const;
    Q_INVOKABLE void resetFeedback();
    Q_INVOKABLE void savePicked(QObject *item);
    /// 根据卡片的实际记录生成可读建议名；不创建文件，重名处理由选定目录负责。
    Q_INVOKABLE QString suggestedFileName(QObject *item, const QString &fallbackLabel = {}) const;
    /// 空路径表示取消。capture 返回异步；只在 PNG 原子提交成功后发 saved。
    Q_INVOKABLE void saveTo(QObject *object, const QString &path);
    /// entries 为 {run_id, label, item}；借用显示中的完整卡片，只向用户选定目录写入。
    Q_INVOKABLE void saveBatchPicked(const QVariantList &entries, bool numberFiles = false);
    Q_INVOKABLE void saveBatchTo(const QVariantList &entries, const QString &directory, bool numberFiles = false);
    /// 保留成功项，只重新抓取本批失败项；重试仍生成独特文件名。
    Q_INVOKABLE void retryFailed();
    /// 实际 PNG 写入路径，也供测试核验失败和取消；不建立目标父目录。
    static bool writePng(const QImage &image, const QString &path, QString *error);
    /// Test seam: the capture wait (15 s) in \a milliseconds, so a test can outlast it.
    void setCaptureTimeoutForTesting(int milliseconds) { m_captureTimeoutMs = milliseconds; }
Q_SIGNALS:
    void changed();
    void saved(const QString &path);
private:
    using CaptureDone = std::function<void(const QString &, const QString &)>;
    struct BatchEntry {
        QString runId;
        QString label;
        QPointer<QQuickItem> item;
    };
    void captureTo(QObject *object, const QString &path, bool rejectExisting, const CaptureDone &done);
    void capturePrepared(QObject *object, const QString &path, bool rejectExisting, const CaptureDone &done);
    /// Calls \a expired 15 s on unless capture \a generation has moved on. While \a window is
    /// shown but minimised (not exposed) it draws no frame, so the wait starts again instead:
    /// a batch carries on once the window is restored rather than failing every image.
    void armCaptureTimeout(QWindow *window, quint64 generation, std::function<void()> expired);
    void processNextBatch();
    QString uniqueBatchPath(const BatchEntry &entry, int index) const;
    void startBatch(const QList<int> &indices);
    void finish(const QString &feedback, const QString &savedPath = {});
    bool m_busy = false;
    int m_captureTimeoutMs = 15000;
    QString m_feedback;
    QString m_savedPath;
    QSharedPointer<QQuickItemGrabResult> m_grab;
    quint64 m_generation = 0;
    QList<BatchEntry> m_batchEntries;
    QVariantList m_batchResults;
    QList<int> m_pendingBatch;
    QString m_batchDirectory;
    bool m_numberBatchFiles = false;
};
}
