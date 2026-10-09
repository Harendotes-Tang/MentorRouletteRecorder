#include "ReflectionLibraryController.h"
#include "IBackend.h"

namespace mr {
ReflectionLibraryController::ReflectionLibraryController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend), m_runs(new RunListModel(this))
{
    m_filter.insert(QStringLiteral("with_reflection"), true);
    m_runs->setBackend(backend);
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
    m_filter = next;
    Q_EMIT filterChanged();
    m_dirty = false;
    m_runs->setFilter(next);
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
