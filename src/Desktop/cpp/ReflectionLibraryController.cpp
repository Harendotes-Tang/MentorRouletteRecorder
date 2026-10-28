#include "ReflectionLibraryController.h"
#include "IBackend.h"

#include <QJsonArray>
#include <QJsonObject>

namespace mr {
ReflectionLibraryController::ReflectionLibraryController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend), m_runs(new RunListModel(this))
{
    m_filter.insert(QStringLiteral("with_reflection"), true);
    m_runs->setBackend(backend);
    // 列表分页回复不能覆盖选中记录的独立刷新结果：它可能来自更早的快照。
    connect(m_runs, &QAbstractItemModel::modelReset, this, &ReflectionLibraryController::selectionChanged);
    connect(m_runs, &RunListModel::loadingChanged, this, &ReflectionLibraryController::selectionChanged);
    if (!backend)
        return;
    connect(backend, &QObject::destroyed, this, [this] {
        invalidateSelection();
        m_runs->setBackend(nullptr);
    });
    connect(backend, &IBackend::connectionChanged, this, [this] {
        m_dirty = true;
        if (m_active)
            reload();
        else
            invalidateSelection();
    });
    connect(backend, &IBackend::liveEvent, this, [this](const QVariantMap &event) {
        const QString kind = event.value(QStringLiteral("kind")).toString();
        const QString type = event.value(QStringLiteral("event_type")).toString();
        if (!kind.startsWith(QStringLiteral("run_")) && !type.startsWith(QStringLiteral("Run"))
            && kind != QStringLiteral("stats_invalidated"))
            return;
        m_dirty = true;
        if (m_active)
            reload();
        else
            invalidateSelection();
    });
}

void ReflectionLibraryController::setActive(bool active)
{
    if (m_active == active)
        return;
    m_active = active;
    Q_EMIT activeChanged();
    if (active && m_dirty)
        reload();
}

void ReflectionLibraryController::setFilter(const QVariantMap &filter)
{
    QVariantMap next{{QStringLiteral("with_reflection"), true}};
    // text keeps note words; name_exact adds only the duties and jobs a
    // translated name names. Neither searches reflection text. Keep both across
    // selection confirmation.
    const QString text = filter.value(QStringLiteral("text")).toString().trimmed().left(200);
    if (!text.isEmpty())
        next.insert(QStringLiteral("text"), text);
    for (const QString &key : {QStringLiteral("job_id"), QStringLiteral("duty_category"),
                               QStringLiteral("name_exact")}) {
        const QVariantList values = filter.value(key).toList();
        if (!values.isEmpty())
            next.insert(key, values);
    }
    if (m_filter == next && !m_dirty)
        return;
    if (m_filter != next && selectedCount() > 0) {
        m_pendingFilter = next;
        Q_EMIT filterChangeNeedsConfirmation();
        return;
    }
    applyFilter(next);
}

void ReflectionLibraryController::applyFilter(const QVariantMap &next)
{
    m_filter = next;
    Q_EMIT filterChanged();
    m_dirty = false;
    m_runs->setFilter(next);
}

QVariantList ReflectionLibraryController::selectedRuns() const
{
    QVariantList result;
    if (!canShareSelection())
        return result;
    result.reserve(m_selectedIds.size());
    for (const QString &id : m_selectedIds)
        result.append(m_selectedRuns.value(id));
    return result;
}

bool ReflectionLibraryController::canShareSelection() const
{
    return m_selectionFresh && !m_selectedIds.isEmpty() && m_backend && m_backend->isConnected();
}

bool ReflectionLibraryController::isSelected(const QString &runId) const
{
    return m_selectedRuns.contains(runId);
}

bool ReflectionLibraryController::currentPageSelected() const
{
    if (m_runs->isLoading() || m_runs->rowCount() == 0 || !m_runs->loadError().isEmpty())
        return false;
    for (int row = 0; row < m_runs->rowCount(); ++row)
        if (!isSelected(m_runs->runAt(row).value(QStringLiteral("run_id")).toString()))
            return false;
    return true;
}

bool ReflectionLibraryController::setSelected(const QVariantMap &run, bool selected)
{
    const QString id = run.value(QStringLiteral("run_id")).toString();
    if (id.isEmpty() || isSelected(id) == selected
        || (selected && run.value(QStringLiteral("reflection")).toMap()
                            .value(QStringLiteral("text")).toString().isEmpty()))
        return false;
    if (selected) {
        m_selectedIds.append(id);
        m_selectedRuns.insert(id, run);
    } else {
        m_selectedRuns.remove(id);
        m_selectedIds.removeAll(id);
    }
    return true;
}

void ReflectionLibraryController::selectionEdited()
{
    // 勾选变化使旧批次作废，尤其不能让「取消后重新勾选」接受先前的回复。
    if (!m_selectionFresh || m_dirty || m_runs->isLoading() || !m_runs->loadError().isEmpty()
        || !m_backend || !m_backend->isConnected())
        refreshSelection();
    else
        Q_EMIT selectionChanged();
}

void ReflectionLibraryController::toggleSelected(const QVariantMap &run)
{
    if (setSelected(run, !isSelected(run.value(QStringLiteral("run_id")).toString())))
        selectionEdited();
}

void ReflectionLibraryController::setPageSelected(bool selected)
{
    if (m_runs->isLoading() || !m_runs->loadError().isEmpty())
        return;
    bool changed = false;
    for (int row = 0; row < m_runs->rowCount(); ++row)
        changed = setSelected(m_runs->runAt(row), selected) || changed;
    // 整页操作只发起一次刷新，不能为每条记录重启整批查询。
    if (changed)
        selectionEdited();
}

void ReflectionLibraryController::clearSelection()
{
    if (m_selectedIds.isEmpty())
        return;
    ++m_selectionGeneration;
    m_selectionPending = 0;
    m_selectionFresh = true;
    m_selectedIds.clear();
    m_selectedRuns.clear();
    Q_EMIT selectionChanged();
}

void ReflectionLibraryController::confirmFilterChange()
{
    if (m_pendingFilter.isEmpty())
        return;
    const QVariantMap next = m_pendingFilter;
    m_pendingFilter.clear();
    clearSelection();
    applyFilter(next);
}

void ReflectionLibraryController::cancelFilterChange()
{
    m_pendingFilter.clear();
    Q_EMIT filterChanged(); // 让筛选控件恢复到实际生效的条件。
}

void ReflectionLibraryController::reload()
{
    refreshSelection();
    if (!m_backend)
        return;
    if (m_dirty && m_runs->total() == 0 && m_runs->page() == 1) {
        m_dirty = false;
        m_runs->setFilter(m_filter);
    } else {
        m_dirty = false;
        m_runs->reload();
    }
}

void ReflectionLibraryController::invalidateSelection()
{
    ++m_selectionGeneration;
    m_selectionPending = 0;
    m_selectionFresh = m_selectedIds.isEmpty();
    Q_EMIT selectionChanged();
}

void ReflectionLibraryController::refreshSelection()
{
    // 页外的勾选只是一份副本：心得清空、记录删除（含批量删除，只发 stats_invalidated）
    // 或在别的页改过心得后，「生成分享图片」不能再用旧内容（审查 I4-1）。
    invalidateSelection();
    if (!m_backend || !m_backend->isConnected() || m_selectedIds.isEmpty())
        return;
    const quint64 generation = m_selectionGeneration;
    const QStringList selectedIds = m_selectedIds;
    constexpr qsizetype kChunk = 200; // RunFilter.run_id ≤ 2000，page_size ≤ 200
    m_selectionPending = (selectedIds.size() + kChunk - 1) / kChunk;
    m_selectionFailed = false;
    for (qsizetype start = 0; start < selectedIds.size(); start += kChunk) {
        const QStringList ids = selectedIds.mid(start, kChunk);
        const QJsonObject filter{{QStringLiteral("with_reflection"), true},
                                 {QStringLiteral("run_id"), QJsonArray::fromStringList(ids)}};
        BackendReply *reply = m_backend->queryRuns(filter, 1, static_cast<int>(ids.size()));
        reply->whenDone(this, [this, generation, ids](bool ok, const QVariantMap &payload,
                                                      const QString &, const QString &) {
            // 读取失败不等于记录已删除；较早的回答让位于较新的一次。
            if (generation != m_selectionGeneration)
                return;
            --m_selectionPending;
            if (!ok || !payload.contains(QStringLiteral("items"))
                || !payload.value(QStringLiteral("items")).canConvert<QVariantList>()) {
                m_selectionFailed = true;
                Q_EMIT selectionChanged();
                return;
            }
            QHash<QString, QVariantMap> fresh;
            for (const QVariant &item : payload.value(QStringLiteral("items")).toList()) {
                const QVariantMap run = item.toMap();
                fresh.insert(run.value(QStringLiteral("run_id")).toString(), run);
            }
            for (const QString &id : ids) {
                if (!m_selectedRuns.contains(id))
                    continue;
                const QVariantMap run = fresh.value(id);
                const bool shareable = !run.value(QStringLiteral("soft_deleted")).toBool()
                    && !run.value(QStringLiteral("reflection")).toMap()
                            .value(QStringLiteral("text")).toString().isEmpty();
                if (shareable) {
                    m_selectedRuns.insert(id, run);
                } else {
                    m_selectedRuns.remove(id);
                    m_selectedIds.removeAll(id);
                }
            }
            m_selectionFresh = m_selectionPending == 0 && !m_selectionFailed;
            Q_EMIT selectionChanged();
        });
    }
}
}
