#pragma once

// ---------------------------------------------------------------------------
// Job and duty-category icon catalogue, exposed to QML as the `Jobs` singleton.
//
// Built at construction from :/resources/icons/manifest.json and
// :/resources/icons/content_types.json, both produced by the icon research
// task. The PNGs are extracted FINAL FANTASY XIV assets:
// (C) SQUARE ENIX CO., LTD. - see resources/icons/LICENSE-NOTE.md.
// PUBLIC_DISTRIBUTION_READY = false.
// ---------------------------------------------------------------------------

#include <QHash>
#include <QObject>
#include <QQmlEngine>
#include <QString>
#include <QVariant>

namespace mr {

class JobCatalog : public QObject
{
    Q_OBJECT
    QML_NAMED_ELEMENT(Jobs)
    QML_SINGLETON
    Q_PROPERTY(int jobCount READ jobCount CONSTANT)

public:
    explicit JobCatalog(QObject *parent = nullptr);

    int jobCount() const { return static_cast<int>(m_jobs.size()); }

    /// 64x64 role-framed icon for \a jobId, or an empty string when unknown.
    Q_INVOKABLE QString framedIcon(const QVariant &jobId) const;
    /// 56x56 plain glyph for \a jobId, or an empty string when unknown.
    Q_INVOKABLE QString plainIcon(const QVariant &jobId) const;
    /// Localised job name; "未知" when \a jobId is null or unmapped.
    Q_INVOKABLE QString jobName(const QVariant &jobId) const;
    /// Three-letter abbreviation, or "?" when unknown.
    Q_INVOKABLE QString abbreviation(const QVariant &jobId) const;
    /// TANK / HEALER / DPS / UNKNOWN, matching $defs/Role.
    Q_INVOKABLE QString role(const QVariant &jobId) const;
    /// Fine-grained Chinese role group used by the prototype's colour legend.
    Q_INVOKABLE QString roleGroup(const QVariant &jobId) const;
    /// Palette token for the role group, resolved by Theme.token().
    Q_INVOKABLE QString roleGroupToken(const QVariant &jobId) const;
    /// Palette token for an arbitrary role-group name.
    Q_INVOKABLE static QString tokenForRoleGroup(const QString &group);

    /// Duty-category icon (四人迷宫 / 讨伐歼灭战 / ...), empty when unmapped.
    Q_INVOKABLE QString categoryIcon(const QVariant &dutyCategory) const;

    /// Every known job, sorted by id: id / name / abbreviation / role group.
    Q_INVOKABLE QVariantList allJobs() const;

private:
    struct Job {
        int id = 0;
        QString name;
        QString abbreviation;
        QString roleGroup;
        QString role;
        QString framedIcon;
        QString plainIcon;
    };

    void loadJobs();
    void loadCategories();
    const Job *find(const QVariant &jobId) const;

    QHash<int, Job> m_jobs;
    QHash<QString, QString> m_categoryIcons;
};

} // namespace mr
