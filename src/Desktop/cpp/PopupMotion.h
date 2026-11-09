#pragma once

#include <QObject>
#include <QtQmlIntegration/qqmlintegration.h>

namespace mr {

/**
 * @brief 在所属 GUI 线程立即结束主窗口弹窗的当前视觉过渡。
 *
 * QML 引擎拥有本单例；弹窗参数仅借用，不转移所有权、不保留指针。
 * 为保留 Qt 原生焦点恢复和模态遮罩清理，具体收敛调用集中在实现文件。
 * 实现依赖 Qt Quick Templates 私有 ABI，必须与构建时同一 Qt SDK 一起部署；
 * 升级 Qt 时必须复验反向打开、降级、窗口隐藏和 closed/opened 信号次数。
 */
class PopupMotion final : public QObject
{
    Q_OBJECT
    QML_ELEMENT
    QML_SINGLETON
public:
    explicit PopupMotion(QObject *parent = nullptr) : QObject(parent) {}

    /**
     * @brief 完成 popup 正在进行的原生进入或退出过渡，不重新打开或关闭它。
     * @param popup 借用的 Qt Popup/Dialog；空值、非弹窗、异线程对象和无过渡对象均忽略。
     *
     * 进入时恢复透明度与缩放并发出原生打开完成信号；退出时由 Qt 隐藏内容、
     * 移除模态层并恢复焦点。属性通知可触发外部回调销毁弹窗或改变目标，
     * 每步都重新检查对象存活和过渡状态；本方法不会覆盖新目标或再次完成它。
     */
    Q_INVOKABLE void finish(QObject *popup);
};
}
