#pragma once

// ---------------------------------------------------------------------------
// Role (职能) icon catalogue, exposed to QML as the `Roles` singleton.
//
// Built at construction from :/resources/icons/roles/manifest.json, produced by
// the icon research task. The PNGs are extracted FINAL FANTASY XIV assets:
// (C) SQUARE ENIX CO., LTD. - see resources/icons/LICENSE-NOTE.md.
// PUBLIC_DISTRIBUTION_READY = false.
//
// The catalogue keys on the fine-grained Chinese role group used by the
// prototype's legend (坦克 / 治疗 / 近战 / 远程物理 / 魔法 / 未知); anything it
// does not know about - including a null or empty value - resolves to the
// in-game All-Rounder icon so a role slot is never blank.
// ---------------------------------------------------------------------------

#include <QHash>
#include <QObject>
#include <QQmlEngine>
#include <QString>
#include <QStringList>

namespace mr {

class RoleCatalog : public QObject
{
    Q_OBJECT
    QML_NAMED_ELEMENT(Roles)
    QML_SINGLETON

public:
    explicit RoleCatalog(QObject *parent = nullptr);

    /// The six role groups in legend order, 未知 last.
    Q_INVOKABLE static QStringList roleGroups();

    /// Catalogue key (tank / healer / melee / ranged / magic / allrounder).
    Q_INVOKABLE static QString roleKey(const QString &roleZh);

    /// `qrc:` URL of the 64x64 framed role icon; never empty.
    Q_INVOKABLE QString roleIconSource(const QString &roleZh) const;

    /// Resource path (":/...") of the same icon, for tests and diagnostics.
    Q_INVOKABLE QString roleIconResource(const QString &roleZh) const;

private:
    void load();

    QHash<QString, QString> m_iconsByKey; // key -> "resources/icons/roles/x.png"
};

} // namespace mr
