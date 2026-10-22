#pragma once

#include <QSet>

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

    int count() const { return static_cast<int>(m_byContentId.size()); }
    QString dataVersion() const { return m_dataVersion; }

    /// The catalogue row for \a contentId, or an empty map. Keys:
    /// content_id, territory_id, duty_name, duty_category, duty_level,
    /// duty_expansion, party_size, difficulty, version, name_ambiguous and
    /// preferred_for_name_matching (both selection metadata, never stored names).
    ///
    /// party_size is the wizard's 人数 group (4 / 8 / 24, 0 = 其他), difficulty
    /// is 普通 / 极 / 零式 and version the "2.x" .. "7.x" label (empty when the
    /// expansion is unknown). See partySizeGroup(), difficultyForName() and
    /// versionForExpansion().
    Q_INVOKABLE QVariantMap lookup(const QVariant &contentId) const;

    /// Exact CN name resolution: one identity, or one explicitly preferred
    /// identity among same-name rows. Other ambiguous names remain unresolved.
    /// GLOBAL cannot be inferred from this CN-only catalogue.
    Q_INVOKABLE QVariantMap findByName(const QString &name,
                                      const QString &region = QString()) const;

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

    /// 通关占比的极神身份：仅接受目录中已知的整数 content_id，并使用目录
    /// 中文类型和名称判断讨伐极难度，排除幻巧战；不改变 difficulty 字段、
    /// 难度筛选或记录中的类型，也不根据调用方的显示名称推断身份。
    bool isExtremeTrial(const QVariant &contentId) const;

    /// "2.x" for A Realm Reborn up to "7.x" for Dawntrail; empty otherwise.
    static QString versionForExpansion(const QString &expansion);

    /// 显示用分类：目录确认的标准 24 人大型任务显示为团队任务，其他显式
    /// 分类保留。只接受已知编号或唯一区域；本地目录不能替其他区服作判断。
    /// 此函数不修改记录，空身份或无法确认的类别保持原值。默认只投影团队
    /// 分类；fillMissingFromCatalog 供 enrich 补齐旧界面的目录展示，不用于筛选。
    QString displayedCategory(const QVariantMap &run, bool fillMissingFromCatalog = false) const;

    /// 补充显示用等级、资料片、名称与分类。标准24人本的旧分类只在返回的
    /// 副本中派生为团队任务，原始记录和其他人工分类不会被改写。
    QVariantMap enrich(const QVariantMap &run) const;

    /// Every row, sorted by expansion then level then name. Used by the
    /// history page's duty filter.
    QVariantList allDuties() const { return m_ordered; }

private:
    void load();

    QHash<qint64, QVariantMap> m_byContentId;
    QHash<QString, QList<QVariantMap>> m_byName;
    QHash<qint64, QList<QVariantMap>> m_byTerritoryId;
    QSet<qint64> m_allianceRaidIds;
    QVariantList m_ordered;
    QString m_dataVersion;
};

} // namespace mr
