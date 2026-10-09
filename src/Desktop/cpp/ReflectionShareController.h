#pragma once

#include <QImage>
#include <QObject>
#include <QSharedPointer>
#include <QString>

class QQuickItemGrabResult;
namespace mr {
/// 保存完整的 QML 分享卡片；不读取或更改记录、心得和采集数据。
class ReflectionShareController final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString feedback READ feedback NOTIFY changed)
    Q_PROPERTY(QString savedPath READ savedPath NOTIFY changed)
public:
    explicit ReflectionShareController(QObject *parent = nullptr);
    bool busy() const { return m_busy; }
    QString feedback() const { return m_feedback; }
    QString savedPath() const { return m_savedPath; }
    Q_INVOKABLE void resetFeedback();
    Q_INVOKABLE void savePicked(QObject *item);
    /// 空路径表示取消。capture 返回异步；只在 PNG 原子提交成功后发 saved。
    Q_INVOKABLE void saveTo(QObject *item, const QString &path);
    /// 实际 PNG 写入路径，也供测试核验失败和取消；不建立目标父目录。
    static bool writePng(const QImage &image, const QString &path, QString *error);
Q_SIGNALS:
    void changed();
    void saved(const QString &path);
private:
    void finish(const QString &feedback, const QString &savedPath = {});
    bool m_busy = false;
    QString m_feedback;
    QString m_savedPath;
    QSharedPointer<QQuickItemGrabResult> m_grab;
    quint64 m_generation = 0;
};
}
