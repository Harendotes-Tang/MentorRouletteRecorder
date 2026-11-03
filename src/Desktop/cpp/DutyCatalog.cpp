#include "DutyCatalog.h"

#include <QDir>
#include <QDate>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>
#include <QStringList>

#include <algorithm>
#include <cmath>
#include <limits>

namespace {

Q_LOGGING_CATEGORY(lcDuties, "mr.duties")

bool hasValue(const QVariantMap &map, const QString &key)
{
    const auto it = map.constFind(key);
    if (it == map.constEnd())
        return false;
    const QVariant &value = it.value();
    if (!value.isValid() || value.isNull())
        return false;
    if (value.typeId() == QMetaType::QString)
        return !value.toString().isEmpty();
    return true;
}

bool numericIdentity(const QVariant &value, qint64 &identity)
{
    switch (value.typeId()) {
    case QMetaType::Int:
    case QMetaType::UInt:
    case QMetaType::LongLong:
    case QMetaType::ULongLong:
    case QMetaType::Float:
    case QMetaType::Double:
        break;
    default:
        return false;
    }
    const double number = value.toDouble();
    if (!std::isfinite(number) || number < 0 || number > std::numeric_limits<int>::max()
        || std::trunc(number) != number)
        return false;
    identity = static_cast<qint64>(number);
    return true;
}

} // namespace

namespace mr {

DutyCatalog::DutyCatalog(QObject *parent) : QObject(parent)
{
    load();
}

DutyCatalog *DutyCatalog::shared()
{
    static DutyCatalog instance;
    return &instance;
}

void DutyCatalog::load()
{
    const QDir directory(QStringLiteral(":/data/duties"));
    const QStringList names = directory.entryList({QStringLiteral("*.json")}, QDir::Files, QDir::Name);
    // TC last: its rows borrow a party size from the regions that carry one
    // (loadRegion), so those are loaded first.
    for (const QString &wanted : {QStringLiteral("CN"), QStringLiteral("GLOBAL"), QStringLiteral("TC")}) {
        for (const QString &name : names) {
            if (name.section(QLatin1Char('.'), 0, 0).toUpper() == wanted)
                loadRegion(directory.filePath(name), wanted);
        }
    }
}

const DutyCatalog::Catalogue &DutyCatalog::catalogue(const QString &region) const
{
    // Regionless records keep the historical CN presentation. A known or invalid
    // region never borrows another service's territory or content identity.
    const QString key = region.isEmpty() || region == QLatin1String("UNKNOWN") ? QStringLiteral("CN") : region;
    const auto found = m_catalogues.constFind(key);
    static const Catalogue empty;
    return found == m_catalogues.cend() ? empty : found.value();
}

void DutyCatalog::loadRegion(const QString &resource, const QString &region)
{
    QFile file(resource);
    if (!file.open(QIODevice::ReadOnly)) {
        qCWarning(lcDuties) << "cannot open" << resource;
        return;
    }
    QJsonParseError error{};
    const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError) {
        qCWarning(lcDuties) << resource << "parse error" << error.errorString();
        return;
    }

    const QJsonObject root = doc.object();
    const QString version = root.value(QStringLiteral("data_version")).toString();
    const QDate parsedVersion = QDate::fromString(version, Qt::ISODate);
    if (root.value(QStringLiteral("region")).toString() != region
        || root.value(QStringLiteral("schema_version")).toInt() != 1
        || root.value(QStringLiteral("sample")).toBool()
        || !parsedVersion.isValid() || parsedVersion.toString(Qt::ISODate) != version)
        return;
    const auto installed = m_catalogues.constFind(region);
    if (installed != m_catalogues.cend() && installed->dataVersion >= version)
        return;
    Catalogue data;
    data.dataVersion = version;

    // data/duties/tc.*.json carries no party_size: null is no information. A
    // 大型任务 row without one borrows the size every other region's row for the
    // same content id states, when they agree - as the Collector composes its
    // catalogue (audit 2026-11-03 H2-2 / L1-3). A TC raid is then an 8- or
    // 24-player duty and a 团队任务, not 其他 with the 「令」 badge.
    const QString raidCategory = QString::fromUtf8("大型任务");
    const auto borrowedPartySize = [this, &region](qint64 id) {
        int agreed = 0;
        for (auto it = m_catalogues.cbegin(); it != m_catalogues.cend(); ++it) {
            const auto stated = it->statedPartySizes.constFind(id);
            if (it.key() == region || stated == it->statedPartySizes.cend())
                continue;
            if (agreed != 0 && stated.value() != agreed)
                return QJsonValue();
            agreed = stated.value();
        }
        return agreed > 0 ? QJsonValue(agreed) : QJsonValue();
    };

    QList<QVariantMap> rows;
    const QJsonArray duties = root.value(QStringLiteral("duties")).toArray();
    rows.reserve(duties.size());
    for (const auto &value : duties) {
        const QJsonObject duty = value.toObject();
        if (!duty.value(QStringLiteral("enabled")).toBool(true)) continue;
        const QJsonValue contentId = duty.value(QStringLiteral("content_id"));
        if (!contentId.isDouble())
            continue;

        QVariantMap row;
        row.insert(QStringLiteral("region"), region);
        row.insert(QStringLiteral("content_id"), static_cast<qint64>(contentId.toDouble()));
        if (duty.value(QStringLiteral("territory_id")).isDouble()) {
            row.insert(QStringLiteral("territory_id"),
                       static_cast<qint64>(duty.value(QStringLiteral("territory_id")).toDouble()));
        }
        row.insert(QStringLiteral("duty_name"),
                   duty.value(QStringLiteral("localized_name")).toString());
        row.insert(QStringLiteral("preferred_for_name_matching"),
                   duty.value(QStringLiteral("preferred_for_name_matching")).isBool()
                       && duty.value(QStringLiteral("preferred_for_name_matching")).toBool());
        const QString sourceCategory = duty.value(QStringLiteral("duty_category")).toString();
        QJsonValue partySize = duty.value(QStringLiteral("party_size"));
        if (partySize.isDouble() && partySize.toDouble() > 0)
            data.statedPartySizes.insert(static_cast<qint64>(contentId.toDouble()), partySize.toInt());
        else if (sourceCategory == raidCategory)
            partySize = borrowedPartySize(static_cast<qint64>(contentId.toDouble()));
        const bool allianceRaid = sourceCategory == raidCategory && partySize.isDouble()
            && partySize.toDouble() == 24;
        if (allianceRaid)
            data.allianceRaidIds.insert(static_cast<qint64>(contentId.toDouble()));
        row.insert(QStringLiteral("duty_category"),
                   allianceRaid ? QString::fromUtf8("团队任务") : sourceCategory);
        if (duty.value(QStringLiteral("level")).isDouble())
            row.insert(QStringLiteral("duty_level"), duty.value(QStringLiteral("level")).toInt());
        const QString expansion = duty.value(QStringLiteral("expansion")).toString();
        row.insert(QStringLiteral("duty_expansion"), expansion);
        const QString category = duty.value(QStringLiteral("duty_category")).toString();
        // A raid of another region with no size to borrow has no 人数 group:
        // unknown, never 0 (其他, the 行会令's group).
        if (region == QLatin1String("CN") || category != raidCategory || partySize.isDouble())
            row.insert(QStringLiteral("party_size"), partySizeGroup(category, partySize.toVariant()));
        row.insert(QStringLiteral("difficulty"),
                   difficultyForName(duty.value(QStringLiteral("localized_name")).toString()));
        row.insert(QStringLiteral("version"), versionForExpansion(expansion));

        rows.append(row);
    }

    QHash<QString, int> nameCounts;
    for (const auto &row : std::as_const(rows))
        ++nameCounts[row.value(QStringLiteral("duty_name")).toString()];
    for (auto &row : rows) {
        const QString name = row.value(QStringLiteral("duty_name")).toString();
        row.insert(QStringLiteral("name_ambiguous"), !name.isEmpty() && nameCounts.value(name) > 1);
        data.byContentId.insert(row.value(QStringLiteral("content_id")).toLongLong(), row);
        data.byName[name].append(row);
        if (row.contains(QStringLiteral("territory_id")))
            data.byTerritoryId[row.value(QStringLiteral("territory_id")).toLongLong()].append(row);
    }

    std::ranges::stable_sort(rows,
                     [](const QVariantMap &a, const QVariantMap &b) {
                         const QString ea = a.value(QStringLiteral("duty_expansion")).toString();
                         const QString eb = b.value(QStringLiteral("duty_expansion")).toString();
                         if (ea != eb)
                             return ea < eb;
                         // Category before level so dungeons stay together the way the duty
                         // finder groups them, rather than trials being dealt in between.
                         const QString ca = a.value(QStringLiteral("duty_category")).toString();
                         const QString cb = b.value(QStringLiteral("duty_category")).toString();
                         if (ca != cb)
                             return ca < cb;
                         const int la = a.value(QStringLiteral("duty_level")).toInt();
                         const int lb = b.value(QStringLiteral("duty_level")).toInt();
                         if (la != lb)
                             return la < lb;
                         // Content id, not name: within a level the game lists content in
                         // the order it was added, which is what the id counts.
                         return a.value(QStringLiteral("content_id")).toLongLong()
                                < b.value(QStringLiteral("content_id")).toLongLong();
                     });

    data.ordered.reserve(rows.size());
    for (const QVariantMap &row : std::as_const(rows))
        data.ordered.append(row);
    m_catalogues.insert(region, data);
}

int DutyCatalog::partySizeGroup(const QString &category, const QVariant &partySize)
{
    if (category == QString::fromUtf8("四人迷宫"))
        return 4;
    if (category == QString::fromUtf8("讨伐歼灭战"))
        return 8;
    if (category == QString::fromUtf8("团队任务"))
        return 24;
    if (category == QString::fromUtf8("大型任务")) {
        bool ok = false;
        const int members = partySize.isValid() && !partySize.isNull() ? partySize.toInt(&ok) : 0;
        return ok && members == 24 ? 24 : 8;
    }
    // 行会令 is played by four, but the wizard files it under 其他 with the
    // categories a roulette never hands out.
    return 0;
}

QString DutyCatalog::difficultyForName(const QString &name)
{
    if (name.contains(QString::fromUtf8("零式")))
        return QString::fromUtf8("零式");
    // The Chinese client names most primal extremes …歼殛战, but the Minstrel's
    // Ballad finales and several other extremes carry a name of their own
    // (…幻想歼灭战, …忆想歼灭战, …诗魂战, 终极之战 …). The list below is what
    // data/duties/cn.2026-09-04.json holds, checked row by row against the
    // English "(Extreme)" / "The Minstrel's Ballad" / "(Unreal)" names: every
    // extreme matches and no normal duty does. Normal trials keep plain
    // …歼灭战 / …讨伐战 / …狩猎战 / …镇魂战.
    static const QStringList extremeSuffixes = {
        QString::fromUtf8("歼殛战"),     QString::fromUtf8("幻想歼灭战"),
        QString::fromUtf8("传奇征龙战"), QString::fromUtf8("梦幻歼灭战"),
        QString::fromUtf8("幽夜歼灭战"), QString::fromUtf8("孤念歼灭战"),
        QString::fromUtf8("幻耀歼灭战"), QString::fromUtf8("晖光歼灭战"),
        QString::fromUtf8("暝暗歼灭战"), QString::fromUtf8("忆想歼灭战"),
        QString::fromUtf8("悲惶歼灭战"), QString::fromUtf8("诗魂战"),
        QString::fromUtf8("上位狩猎战"), QString::fromUtf8("狂想作战"),
        QString::fromUtf8("假想作战"),   QString::fromUtf8("追忆战"),
        // 幻巧战 is the unreal trial: a normal duty at extreme difficulty.
        QString::fromUtf8("幻巧战"),
    };
    const QString trimmed = name.trimmed();
    if (trimmed.startsWith(QString::fromUtf8("極 "))
        || trimmed.startsWith(QString::fromUtf8("極王"))
        || trimmed.startsWith(QString::fromUtf8("幻 "))
        || trimmed == QString::fromUtf8("究極幻想 究極武器破壞作戰")
        || trimmed == QString::fromUtf8("蒼天幻想 圓桌騎士殲滅戰"))
        return QString::fromUtf8("极");
    if (trimmed == QString::fromUtf8("终极之战") || trimmed == QString::fromUtf8("終極之戰"))
        return QString::fromUtf8("极");
    for (const QString &suffix : extremeSuffixes) {
        if (trimmed.endsWith(suffix))
            return QString::fromUtf8("极");
    }
    return QString::fromUtf8("普通");
}

bool DutyCatalog::isExtremeTrial(const QVariant &contentId, const QString &region) const
{
    // The IPC identity is numeric. Do not let a display string or a rounded
    // fractional value acquire a known duty's identity through QVariant coercion.
    switch (contentId.typeId()) {
    case QMetaType::Int:
    case QMetaType::UInt:
    case QMetaType::LongLong:
    case QMetaType::ULongLong:
    case QMetaType::Float:
    case QMetaType::Double:
        break;
    default:
        return false;
    }
    const double numericId = contentId.toDouble();
    if (!std::isfinite(numericId))
        return false;
    const QVariantMap duty = lookup(contentId, region);
    if (duty.isEmpty() || duty.value(QStringLiteral("content_id")).toDouble() != numericId
        || duty.value(QStringLiteral("duty_category")).toString() != QString::fromUtf8("讨伐歼灭战"))
        return false;
    const QString name = duty.value(QStringLiteral("duty_name")).toString().trimmed();
    return !name.startsWith(QString::fromUtf8("幻 ")) && !name.endsWith(QString::fromUtf8("幻巧戰"))
           && !name.endsWith(QString::fromUtf8("幻巧战"))
           && difficultyForName(name) == QString::fromUtf8("极");
}

QString DutyCatalog::versionForExpansion(const QString &expansion)
{
    static const QHash<QString, QString> versions = {
        {QStringLiteral("A Realm Reborn"), QStringLiteral("2.x")},
        {QStringLiteral("Heavensward"), QStringLiteral("3.x")},
        {QStringLiteral("Stormblood"), QStringLiteral("4.x")},
        {QStringLiteral("Shadowbringers"), QStringLiteral("5.x")},
        {QStringLiteral("Endwalker"), QStringLiteral("6.x")},
        {QStringLiteral("Dawntrail"), QStringLiteral("7.x")},
    };
    return versions.value(expansion);
}

QVariantMap DutyCatalog::lookup(const QVariant &contentId, const QString &region) const
{
    const auto &data = catalogue(region);
    if (!contentId.isValid() || contentId.isNull())
        return {};
    bool ok = false;
    const qint64 key = contentId.toLongLong(&ok);
    return ok ? data.byContentId.value(key) : QVariantMap();
}

QVariantMap DutyCatalog::findByName(const QString &name, const QString &region) const
{
    const auto &data = catalogue(region);
    const auto rows = data.byName.value(name);
    if (rows.size() == 1)
        return rows.first();
    QVariantMap preferred;
    for (const auto &row : rows) {
        if (!row.value(QStringLiteral("preferred_for_name_matching")).toBool())
            continue;
        if (!preferred.isEmpty())
            return {};
        preferred = row;
    }
    return preferred;
}

QVariantMap DutyCatalog::lookupByTerritory(const QVariant &territoryId, const QString &region) const
{
    const auto &data = catalogue(region);
    if (!territoryId.isValid() || territoryId.isNull())
        return {};
    bool ok = false;
    const qint64 key = territoryId.toLongLong(&ok);
    if (!ok)
        return {};
    const QList<QVariantMap> rows = data.byTerritoryId.value(key);
    if (rows.isEmpty())
        return {};

    QVariantMap agreed = rows.first();
    for (int i = 1; i < rows.size(); ++i) {
        for (auto it = agreed.begin(); it != agreed.end();) {
            if (rows.at(i).value(it.key()) != it.value())
                it = agreed.erase(it);
            else
                ++it;
        }
    }
    // The identity fields are per duty, never per zone.
    agreed.remove(QStringLiteral("content_id"));
    if (rows.size() > 1)
        agreed.remove(QStringLiteral("duty_name"));
    return agreed;
}

QString DutyCatalog::displayedCategory(const QVariantMap &run, bool fillMissingFromCatalog) const
{
    QString recorded = run.value(QStringLiteral("duty_category")).toString();
    if (!recorded.trimmed().isEmpty() && recorded != QString::fromUtf8("大型任务"))
        return recorded;
    const QString region = run.value(QStringLiteral("region")).toString();
    const auto &data = catalogue(region);

    QVariantMap identity;
    if (hasValue(run, QStringLiteral("content_id"))) {
        qint64 content = 0;
        if (!numericIdentity(run.value(QStringLiteral("content_id")), content))
            return recorded;
        identity = lookup(content, region);
    } else {
        qint64 territory = 0;
        const bool ok = numericIdentity(run.value(QStringLiteral("territory_id")), territory);
        const auto candidates = ok ? data.byTerritoryId.value(territory) : QList<QVariantMap>();
        if (candidates.size() == 1)
            identity = candidates.first();
    }
    if (identity.isEmpty())
        return recorded;
    if (data.allianceRaidIds.contains(identity.value(QStringLiteral("content_id")).toLongLong()))
        return QString::fromUtf8("团队任务");
    if (fillMissingFromCatalog && recorded.trimmed().isEmpty())
        return identity.value(QStringLiteral("duty_category")).toString();
    return recorded;
}

QVariantMap DutyCatalog::enrich(const QVariantMap &run) const
{
    const QString region = run.value(QStringLiteral("region")).toString();
    QVariantMap row = lookup(run.value(QStringLiteral("content_id")), region);
    if (row.isEmpty())
        row = lookupByTerritory(run.value(QStringLiteral("territory_id")), region);
    if (row.isEmpty())
        return run;

    QVariantMap out = run;
    for (const QString &key : {QStringLiteral("duty_level"),
                               QStringLiteral("duty_expansion"),
                               QStringLiteral("duty_name")}) {
        if (!hasValue(out, key) && hasValue(row, key))
            out.insert(key, row.value(key));
    }
    const QString category = displayedCategory(run, true);
    if (!category.isEmpty())
        out.insert(QStringLiteral("duty_category"), category);
    return out;
}

} // namespace mr
