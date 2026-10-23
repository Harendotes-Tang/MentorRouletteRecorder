#include "GameNames.h"

#include "DutyCatalog.h"
#include "JobCatalog.h"
#include "LanguageManager.h"

#include <QCoreApplication>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>
#include <QSet>

namespace {

Q_LOGGING_CATEGORY(lcNames, "mr.names")

constexpr auto kNamesResource = ":/data/i18n/game-names.2026-10-15.json";
constexpr auto kRoulettesResource = ":/data/roulettes/cn.json";
constexpr auto kSourceCode = "zh-CN";

// The prototype legend's role groups are this project's own words, not game
// text, so they are ordinary translations (context mr::GameNames).
const char *const kRoleGroups[] = {
    QT_TRANSLATE_NOOP("mr::GameNames", "坦克"),
    QT_TRANSLATE_NOOP("mr::GameNames", "治疗"),
    QT_TRANSLATE_NOOP("mr::GameNames", "近战"),
    QT_TRANSLATE_NOOP("mr::GameNames", "远程物理"),
    QT_TRANSLATE_NOOP("mr::GameNames", "魔法"),
    QT_TRANSLATE_NOOP("mr::GameNames", "其他"),
};

QJsonObject readObject(const char *resource)
{
    QFile file(QString::fromLatin1(resource));
    if (!file.open(QIODevice::ReadOnly)) {
        qCWarning(lcNames) << "cannot open" << resource;
        return {};
    }
    QJsonParseError error{};
    const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError) {
        qCWarning(lcNames) << resource << "parse error" << error.errorString();
        return {};
    }
    return doc.object();
}

QHash<QString, QString> namesOf(const QJsonObject &entry)
{
    QHash<QString, QString> names;
    for (auto it = entry.constBegin(); it != entry.constEnd(); ++it) {
        if (it.value().isString() && !it.value().toString().isEmpty())
            names.insert(it.key(), it.value().toString());
    }
    return names;
}

bool toId(const QVariant &value, qint64 *id)
{
    if (!value.isValid() || value.isNull())
        return false;
    bool ok = false;
    const qint64 parsed = value.toLongLong(&ok);
    if (!ok || parsed <= 0)
        return false;
    *id = parsed;
    return true;
}

} // namespace

namespace mr {

GameNames::GameNames(QObject *parent) : QObject(parent)
{
    load();
}

GameNames *GameNames::shared()
{
    static GameNames instance;
    return &instance;
}

void GameNames::load()
{
    const QJsonObject root = readObject(kNamesResource);
    m_dataVersion = root.value(QStringLiteral("data_version")).toString();

    const auto byId = [](const QJsonObject &map) {
        QHash<qint64, Names> table;
        for (auto it = map.constBegin(); it != map.constEnd(); ++it) {
            bool ok = false;
            const qint64 id = it.key().toLongLong(&ok);
            if (ok)
                table.insert(id, namesOf(it.value().toObject()));
        }
        return table;
    };
    m_duties = byId(root.value(QStringLiteral("duties")).toObject());
    m_jobs = byId(root.value(QStringLiteral("jobs")).toObject());
    m_roulettes = byId(root.value(QStringLiteral("roulettes")).toObject());

    const QJsonObject categories = root.value(QStringLiteral("duty_categories")).toObject();
    for (auto it = categories.constBegin(); it != categories.constEnd(); ++it)
        m_categories.insert(it.key(), namesOf(it.value().toObject()));
    const QJsonObject expansions = root.value(QStringLiteral("expansions")).toObject();
    for (auto it = expansions.constBegin(); it != expansions.constEnd(); ++it)
        m_expansions.insert(it.key(), namesOf(it.value().toObject()));

    // The Chinese client's names find the id of a record that carries none.
    for (const QVariant &row : DutyCatalog::shared()->allDuties()) {
        const QVariantMap duty = row.toMap();
        const QString name = duty.value(QStringLiteral("duty_name")).toString();
        const QVariantMap resolved = DutyCatalog::shared()->findByName(name);
        if (!name.isEmpty() && !resolved.isEmpty())
            m_dutyIdByName.insert(name, resolved.value(QStringLiteral("content_id")).toLongLong());
    }
    const JobCatalog jobs;
    for (const QVariant &row : jobs.allJobs()) {
        const QVariantMap job = row.toMap();
        const QString name = job.value(QStringLiteral("job_name")).toString();
        if (!name.isEmpty() && !m_jobIdByName.contains(name))
            m_jobIdByName.insert(name, job.value(QStringLiteral("job_id")).toLongLong());
    }
    const QJsonObject roulettes = readObject(kRoulettesResource);
    for (const auto &value : roulettes.value(QStringLiteral("roulettes")).toArray()) {
        const QJsonObject roulette = value.toObject();
        const QString name = roulette.value(QStringLiteral("localized_name")).toString();
        if (!name.isEmpty())
            m_rouletteIdByName.insert(name, roulette.value(QStringLiteral("roulette_id")).toInteger());
    }
}

QString GameNames::pick(const QHash<qint64, Names> &table, const QString &code,
                        const QVariant &id, qint64 fallbackId, const QString &recorded)
{
    if (code == QLatin1String(kSourceCode))
        return recorded;
    qint64 key = 0;
    if (!toId(id, &key))
        key = fallbackId;
    const auto it = table.constFind(key);
    if (it == table.constEnd())
        return recorded;
    const QString name = it->value(code);
    return name.isEmpty() ? recorded : name;
}

QString GameNames::dutyIn(const QString &code, const QVariant &contentId,
                          const QString &recorded) const
{
    QString name = pick(m_duties, code, contentId, m_dutyIdByName.value(recorded), recorded);
    if (!name.isEmpty() || code != QLatin1String(kSourceCode))
        return name;
    // 简体中文 without a recorded name: the catalogue's.
    return DutyCatalog::shared()->lookup(contentId).value(QStringLiteral("duty_name")).toString();
}

QString GameNames::jobIn(const QString &code, const QVariant &jobId, const QString &recorded) const
{
    return pick(m_jobs, code, jobId, m_jobIdByName.value(recorded), recorded);
}

QString GameNames::duty(const QVariant &contentId, const QString &recorded) const
{
    return dutyIn(LanguageManager::currentCode(), contentId, recorded);
}

bool GameNames::dutyNameAmbiguous(const QVariant &contentId) const
{
    return DutyCatalog::shared()->lookup(contentId).value(QStringLiteral("name_ambiguous")).toBool();
}

QString GameNames::job(const QVariant &jobId, const QString &recorded) const
{
    return jobIn(LanguageManager::currentCode(), jobId, recorded);
}

QString GameNames::roulette(const QVariant &rouletteId, const QString &recorded) const
{
    return pick(m_roulettes, LanguageManager::currentCode(), rouletteId,
                m_rouletteIdByName.value(recorded), recorded);
}

QStringList GameNames::recordedSearchNames(const QString &text) const
{
    return recordedSearchNamesIn(LanguageManager::currentCode(), text);
}

QStringList GameNames::recordedSearchNamesIn(const QString &code, const QString &text) const
{
    const QString query = text.trimmed();
    if (query.isEmpty() || code == QLatin1String(kSourceCode))
        return {};

    // The names themselves, not a piece they share: 骑士 is inside 暗黑骑士 and
    // 龙骑士, so no substring of it finds Paladin runs alone. The Collector
    // matches these by equality (RunFilter.name_exact).
    QSet<QString> recorded;
    const auto collect = [&](const QHash<QString, qint64> &idByName,
                             const QHash<qint64, Names> &table) {
        for (auto it = idByName.constBegin(); it != idByName.constEnd(); ++it) {
            const QString shown = table.value(it.value()).value(code);
            if (!shown.isEmpty() && shown.contains(query, Qt::CaseInsensitive))
                recorded.insert(it.key());
        }
    };
    collect(m_dutyIdByName, m_duties);
    collect(m_jobIdByName, m_jobs);
    // More than the contract takes is no name search; the text still matches.
    if (recorded.size() > kMaxSearchNames)
        return {};
    // Sorted, so the same text always gives the same filter.
    QStringList names(recorded.cbegin(), recorded.cend());
    names.sort();
    return names;
}

QString GameNames::rouletteByName(const QString &recorded) const
{
    return roulette(QVariant(), recorded);
}

QString GameNames::category(const QString &recorded) const
{
    const QString code = LanguageManager::currentCode();
    if (code == QLatin1String(kSourceCode))
        return recorded;
    const QString name = m_categories.value(recorded).value(code);
    return name.isEmpty() ? recorded : name;
}

QString GameNames::expansion(const QString &englishKey) const
{
    const QString name = m_expansions.value(englishKey).value(LanguageManager::currentCode());
    return name.isEmpty() ? englishKey : name;
}

QString GameNames::roleGroup(const QString &recorded) const
{
    for (const char *group : kRoleGroups) {
        if (recorded == QString::fromUtf8(group))
            return QCoreApplication::translate("mr::GameNames", group);
    }
    return recorded;
}

} // namespace mr
