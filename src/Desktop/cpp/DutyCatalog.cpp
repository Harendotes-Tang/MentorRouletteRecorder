#include "DutyCatalog.h"

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>
#include <QStringList>

#include <algorithm>

namespace {

Q_LOGGING_CATEGORY(lcDuties, "mr.duties")

/// The bundled catalogue. Only content_id, level and expansion are consumed
/// for the fields the contract cannot carry; the localized name and category
/// are used only when a run has none of its own.
constexpr auto kResource = ":/data/duties/cn.2026-09-04.json";

bool hasValue(const QVariantMap &map, const QString &key)
{
    const auto it = map.constFind(key);
    if (it == map.constEnd())
        return false;
    const QVariant value = it.value();
    if (!value.isValid() || value.isNull())
        return false;
    if (value.typeId() == QMetaType::QString)
        return !value.toString().isEmpty();
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
    QFile file(QString::fromLatin1(kResource));
    if (!file.open(QIODevice::ReadOnly)) {
        qCWarning(lcDuties) << "cannot open" << kResource;
        return;
    }
    QJsonParseError error{};
    const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError) {
        qCWarning(lcDuties) << kResource << "parse error" << error.errorString();
        return;
    }

    const QJsonObject root = doc.object();
    m_dataVersion = root.value(QStringLiteral("data_version")).toString();

    QList<QVariantMap> rows;
    const QJsonArray duties = root.value(QStringLiteral("duties")).toArray();
    rows.reserve(duties.size());
    for (const QJsonValue &value : duties) {
        const QJsonObject duty = value.toObject();
        const QJsonValue contentId = duty.value(QStringLiteral("content_id"));
        if (!contentId.isDouble())
            continue;

        QVariantMap row;
        row.insert(QStringLiteral("content_id"), qint64(contentId.toDouble()));
        if (duty.value(QStringLiteral("territory_id")).isDouble()) {
            row.insert(QStringLiteral("territory_id"),
                       qint64(duty.value(QStringLiteral("territory_id")).toDouble()));
        }
        row.insert(QStringLiteral("duty_name"),
                   duty.value(QStringLiteral("localized_name")).toString());
        row.insert(QStringLiteral("duty_category"),
                   duty.value(QStringLiteral("duty_category")).toString());
        if (duty.value(QStringLiteral("level")).isDouble())
            row.insert(QStringLiteral("duty_level"), duty.value(QStringLiteral("level")).toInt());
        const QString expansion = duty.value(QStringLiteral("expansion")).toString();
        row.insert(QStringLiteral("duty_expansion"), expansion);
        const QString category = duty.value(QStringLiteral("duty_category")).toString();
        row.insert(QStringLiteral("party_size"),
                   partySizeGroup(category,
                                  duty.value(QStringLiteral("party_size")).toVariant()));
        row.insert(QStringLiteral("difficulty"),
                   difficultyForName(duty.value(QStringLiteral("localized_name")).toString()));
        row.insert(QStringLiteral("version"), versionForExpansion(expansion));

        m_byContentId.insert(qint64(contentId.toDouble()), row);
        rows.append(row);
    }

    std::stable_sort(rows.begin(), rows.end(),
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

    m_ordered.reserve(rows.size());
    for (const QVariantMap &row : std::as_const(rows))
        m_ordered.append(row);
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
    if (trimmed == QString::fromUtf8("终极之战"))
        return QString::fromUtf8("极");
    for (const QString &suffix : extremeSuffixes) {
        if (trimmed.endsWith(suffix))
            return QString::fromUtf8("极");
    }
    return QString::fromUtf8("普通");
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

QVariantMap DutyCatalog::lookup(const QVariant &contentId) const
{
    if (!contentId.isValid() || contentId.isNull())
        return {};
    bool ok = false;
    const qint64 key = contentId.toLongLong(&ok);
    return ok ? m_byContentId.value(key) : QVariantMap();
}

QVariantMap DutyCatalog::enrich(const QVariantMap &run) const
{
    const QVariantMap row = lookup(run.value(QStringLiteral("content_id")));
    if (row.isEmpty())
        return run;

    QVariantMap out = run;
    for (const QString &key : {QStringLiteral("duty_level"),
                               QStringLiteral("duty_expansion"),
                               QStringLiteral("duty_category"),
                               QStringLiteral("duty_name")}) {
        if (!hasValue(out, key) && hasValue(row, key))
            out.insert(key, row.value(key));
    }
    return out;
}

} // namespace mr
