#pragma once

#include <QObject>
#include <QPointer>
#include <QHash>
#include <QStringList>
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
    Q_PROPERTY(int selectedCount READ selectedCount NOTIFY selectionChanged)
    Q_PROPERTY(QStringList selectedIds READ selectedIds NOTIFY selectionChanged)
    Q_PROPERTY(QVariantList selectedRuns READ selectedRuns NOTIFY selectionChanged)
    Q_PROPERTY(bool canShareSelection READ canShareSelection NOTIFY selectionChanged)
    Q_PROPERTY(bool currentPageSelected READ currentPageSelected NOTIFY selectionChanged)
public:
    explicit ReflectionLibraryController(IBackend *backend, QObject *parent = nullptr);
    RunListModel *runs() const { return m_runs; }
    QVariantMap filter() const { return m_filter; }
    bool active() const { return m_active; }
    int selectedCount() const { return static_cast<int>(m_selectedIds.size()); }
    QStringList selectedIds() const { return m_selectedIds; }
    /// 未完成刷新、读取失败或断线时不暴露缓存投影；勾选 ID 仍保留供重试。
    QVariantList selectedRuns() const;
    bool canShareSelection() const;
    bool currentPageSelected() const;
    Q_INVOKABLE bool isSelected(const QString &runId) const;
    /// 选择按稳定 ID 保存完整投影，翻页不丢失；全选只影响当前页。
    Q_INVOKABLE void toggleSelected(const QVariantMap &run);
    Q_INVOKABLE void setPageSelected(bool selected);
    Q_INVOKABLE void clearSelection();
    Q_INVOKABLE void confirmFilterChange();
    Q_INVOKABLE void cancelFilterChange();
    Q_INVOKABLE void setActive(bool active);
    /// 只接受列表现有筛选字段，始终限定为未删除且有心得的记录。
    Q_INVOKABLE void setFilter(const QVariantMap &filter);
    Q_INVOKABLE void reload();
Q_SIGNALS:
    void filterChanged();
    void activeChanged();
    void selectionChanged();
    void filterChangeNeedsConfirmation();
private:
    void applyFilter(const QVariantMap &next);
    /// 重新读取已勾选的记录：心得已清空或记录已删除的移出选择，其余换成新投影。
    /// 读取失败时保留相应选择，但整批禁止分享，直到下一次全部刷新成功。
    void refreshSelection();
    void invalidateSelection();
    bool setSelected(const QVariantMap &run, bool selected);
    void selectionEdited();
    QPointer<IBackend> m_backend;
    RunListModel *m_runs;
    QVariantMap m_filter;
    bool m_active = false;
    bool m_dirty = true;
    QStringList m_selectedIds;
    QHash<QString, QVariantMap> m_selectedRuns;
    QVariantMap m_pendingFilter;
    quint64 m_selectionGeneration = 0;
    qsizetype m_selectionPending = 0;
    bool m_selectionFailed = false;
    bool m_selectionFresh = true;
};
}
