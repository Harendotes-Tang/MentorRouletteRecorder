#include "ReflectionLibraryController.h"
#include "IBackend.h"

namespace mr {
ReflectionLibraryController::ReflectionLibraryController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend), m_runs(new RunListModel(this))
{
    m_filter.insert(QStringLiteral("with_reflection"), true);
    m_runs->setBackend(backend);
    connect(m_runs, &QAbstractItemModel::modelReset, this, [this] {
        // 已勾选且仍出现在当前页的记录使用新投影；页外选择保留原有完整内容。
        for (int row = 0; row < m_runs->rowCount(); ++row) {
            const QVariantMap run = m_runs->runAt(row);
            const QString id = run.value(QStringLiteral("run_id")).toString();
            if (m_selectedRuns.contains(id))
                m_selectedRuns.insert(id, run);
        }
        Q_EMIT selectionChanged();
    });
    connect(m_runs, &RunListModel::loadingChanged, this, &ReflectionLibraryController::selectionChanged);
    if (!backend)
        return;
    connect(backend, &QObject::destroyed, this, [this] { m_runs->setBackend(nullptr); });
    connect(backend, &IBackend::connectionChanged, this, [this] {
        m_dirty = true;
        if (m_active)
            reload();
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
    // QueryRuns.text covers duty/job/note; it does not search reflection text.
    const QString text = filter.value(QStringLiteral("text")).toString().trimmed().left(200);
    if (!text.isEmpty())
        next.insert(QStringLiteral("text"), text);
    for (const QString &key : {QStringLiteral("job_id"), QStringLiteral("duty_category")}) {
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
    result.reserve(m_selectedIds.size());
    for (const QString &id : m_selectedIds)
        result.append(m_selectedRuns.value(id));
    return result;
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

void ReflectionLibraryController::toggleSelected(const QVariantMap &run)
{
    const QString id = run.value(QStringLiteral("run_id")).toString();
    if (id.isEmpty() || run.value(QStringLiteral("reflection")).toMap()
                            .value(QStringLiteral("text")).toString().isEmpty())
        return;
    if (m_selectedRuns.remove(id))
        m_selectedIds.removeAll(id);
    else {
        m_selectedIds.append(id);
        m_selectedRuns.insert(id, run);
    }
    Q_EMIT selectionChanged();
}

void ReflectionLibraryController::setPageSelected(bool selected)
{
    if (m_runs->isLoading() || !m_runs->loadError().isEmpty())
        return;
    for (int row = 0; row < m_runs->rowCount(); ++row) {
        const QVariantMap run = m_runs->runAt(row);
        const QString id = run.value(QStringLiteral("run_id")).toString();
        if (isSelected(id) != selected)
            toggleSelected(run);
    }
}

void ReflectionLibraryController::clearSelection()
{
    if (m_selectedIds.isEmpty())
        return;
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
}
