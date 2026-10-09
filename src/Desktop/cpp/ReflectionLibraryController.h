#pragma once

#include <QObject>
#include <QPointer>
#include <QVariantMap>

#include "RunListModel.h"

namespace mr {
class IBackend;

/// 全部心得拥有自己的分页和筛选；借用 Collector 门面，不改变历史页的模型。
class ReflectionLibraryController final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(mr::RunListModel *runs READ runs CONSTANT)
    Q_PROPERTY(QVariantMap filter READ filter NOTIFY filterChanged)
    Q_PROPERTY(bool active READ active WRITE setActive NOTIFY activeChanged)
public:
    explicit ReflectionLibraryController(IBackend *backend, QObject *parent = nullptr);
    RunListModel *runs() const { return m_runs; }
    QVariantMap filter() const { return m_filter; }
    bool active() const { return m_active; }
    Q_INVOKABLE void setActive(bool active);
    /// 只接受列表现有筛选字段，始终限定为未删除且有心得的记录。
    Q_INVOKABLE void setFilter(const QVariantMap &filter);
    Q_INVOKABLE void reload();
Q_SIGNALS:
    void filterChanged();
    void activeChanged();
private:
    QPointer<IBackend> m_backend;
    RunListModel *m_runs;
    QVariantMap m_filter;
    bool m_active = false;
    bool m_dirty = true;
};
}
