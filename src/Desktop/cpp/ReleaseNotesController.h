#pragma once

#include <QObject>
#include <QString>

namespace mr {

class AppSettings;

/// 当前版本的离线更新说明。借用 AppSettings（须比本对象长寿），不访问 Collector 或网络。
class ReleaseNotesController final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QString version READ version CONSTANT)
    Q_PROPERTY(QString text READ text CONSTANT)
    Q_PROPERTY(bool pending READ pending NOTIFY changed)

public:
    /// 从随包 CHANGELOG 和当前编译版本读取；缺少当前段落时保持不可展示。
    explicit ReleaseNotesController(AppSettings *settings, QObject *parent = nullptr);
    /// 指定版本与日志的入口用于确定性测试；不修改给定日志或主动写设置。
    ReleaseNotesController(AppSettings *settings, const QString &version,
                           const QString &changelog, QObject *parent = nullptr);

    QString version() const { return m_version; }
    QString text() const { return m_text; }
    bool pending() const { return m_pending; }

    /// 用户关闭已展示弹窗时调用。同步已读最高版本；退出阶段、空正文及降级不写入。
    Q_INVOKABLE void acknowledge();

    /// SemVer 优先级；非法当前版本返回 false，空/非法已读版本等价首次使用。
    static bool isNewerVersion(const QString &current, const QString &seen);
    /// 精确提取完整版本的二级标题段落，转换为不加载资源的纯文本；无匹配返回空。
    static QString extractNotes(const QString &changelog, const QString &version);

Q_SIGNALS:
    void changed();

private:
    AppSettings *m_settings;
    QString m_version;
    QString m_text;
    bool m_pending = false;
    bool m_quitting = false;
};

} // namespace mr
