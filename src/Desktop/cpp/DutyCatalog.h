#pragma once

// ---------------------------------------------------------------------------
// Display-only duty reference data.
//
// $defs/Run and $defs/DungeonStatsRow are additionalProperties:false and carry
// neither duty_level nor duty_expansion. This catalogue fills those two cells on
// the desktop side without touching the contract: the bundled
// data/duties/<region>.<date>.json is joined by content_id purely for display.
//
// What it is NOT: it never identifies a roulette, never influences a protocol
// profile, and is never written back to the Collector. A content_id that is not
// in the file simply keeps rendering an em dash.
// ---------------------------------------------------------------------------

#include <QHash>
#include <QObject>
#include <QString>
#include <QVariantList>
#include <QVariantMap>

namespace mr {

/// Not exposed to QML: the join happens in the models and in AppController, so
/// a page can never render a level the Collector did not report *and* the
/// catalogue does not know either.
class DutyCatalog : public QObject
{
    Q_OBJECT

    Q_PROPERTY(int count READ count CONSTANT)
    Q_PROPERTY(QString dataVersion READ dataVersion CONSTANT)

public:
    explicit DutyCatalog(QObject *parent = nullptr);

    /// Process-wide instance. The data is bundled, read-only and 200 KB of
    /// JSON, so the models, the controller and QML share one parse.
    static DutyCatalog *shared();

    int count() const { return int(m_byContentId.size()); }
    QString dataVersion() const { return m_dataVersion; }

    /// The catalogue row for \a contentId, or an empty map. Keys:
    /// content_id, territory_id, duty_name, duty_category, duty_level,
    /// duty_expansion, party_size, difficulty, version.
    ///
    /// party_size is the wizard's 人数 group (4 / 8 / 24, 0 = 其他), difficulty
    /// is 普通 / 极 / 零式 and version the "2.x" .. "7.x" label (empty when the
    /// expansion is unknown). See partySizeGroup(), difficultyForName() and
    /// versionForExpansion().
    Q_INVOKABLE QVariantMap lookup(const QVariant &contentId) const;

    /// What the catalogue can say about a duty known only by its zone: the
    /// fields on which every row with that territory_id agrees. A run recorded
    /// from ZONE_TERRITORY carries no content_id (docs/data-model.md, schema 8),
    /// and this is how its 资料片 · 等级 line is still filled in. Two duties that
    /// share a zone but differ in level keep that field empty rather than guess.
    Q_INVOKABLE QVariantMap lookupByTerritory(const QVariant &territoryId) const;

    /// 人数 group of a duty: 四人迷宫 4, 讨伐歼灭战 8, 团队任务 24, 行会令 and
    /// anything else 0. 大型任务 holds both 8-player raids and 24-player
    /// alliance raids; the data file's own party_size (ContentMemberType)
    /// decides, and a row without one counts as 8.
    static int partySizeGroup(const QString &category, const QVariant &partySize);

    /// 零式 when the name contains 零式; 极 for the extreme trials (…歼殛战,
    /// and the later extremes the Chinese client names differently, such as
    /// …忆想歼灭战 or 终极之战); 普通 otherwise.
    static QString difficultyForName(const QString &name);

    /// "2.x" for A Realm Reborn up to "7.x" for Dawntrail; empty otherwise.
    static QString versionForExpansion(const QString &expansion);

    /// \a run with duty_level / duty_expansion (and duty_category / duty_name
    /// when the run has none) filled in from the catalogue. Fields the run
    /// already carries always win: the Collector's own value is the record,
    /// this is only a fallback for what the contract cannot transport.
    QVariantMap enrich(const QVariantMap &run) const;

    /// Every row, sorted by expansion then level then name. Used by the
    /// history page's duty filter.
    QVariantList allDuties() const { return m_ordered; }

private:
    void load();

    QHash<qint64, QVariantMap> m_byContentId;
    QHash<qint64, QList<QVariantMap>> m_byTerritoryId;
    QVariantList m_ordered;
    QString m_dataVersion;
};

} // namespace mr
