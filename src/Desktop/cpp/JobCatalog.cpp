#include <QDir>
#include <QFile>
#include <QUrl>
#include "JobCatalog.h"

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>
#include <QVariantList>
#include <QVariantMap>

namespace {

Q_LOGGING_CATEGORY(lcIcons, "mr.icons")

QJsonDocument readJson(const QString &resourcePath)
{
    QFile file(resourcePath);
    if (!file.open(QIODevice::ReadOnly)) {
        qCWarning(lcIcons) << "cannot open" << resourcePath;
        return {};
    }
    QJsonParseError error{};
    const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError)
        qCWarning(lcIcons) << resourcePath << "parse error" << error.errorString();
    return doc;
}

QString roleFromRaw(const QString &raw)
{
    if (raw == QLatin1String("tank"))
        return QStringLiteral("TANK");
    if (raw == QLatin1String("healer"))
        return QStringLiteral("HEALER");
    if (raw.contains(QLatin1String("dps")))
        return QStringLiteral("DPS");
    return QStringLiteral("UNKNOWN");
}

/// Where an icon comes from, in order of preference: a PNG the player extracted
/// themselves into %LOCALAPPDATA%\MentorRecorder\icons\<relative>, then a PNG baked
/// into the binary by a personal build (MR_BUNDLE_GAME_ICONS), else nothing - the
/// QML components draw a text badge in that case. Distributed builds carry no game art.
bool isContainedRelativePath(const QString &relative)
{
    if (relative.isEmpty() || relative.contains(QLatin1String("..")) || relative.startsWith(QLatin1Char('/'))
        || relative.startsWith(QLatin1Char('\\')) || relative.contains(QLatin1Char(':')))
        return false;
    return QDir::cleanPath(relative) == relative;
}

QString resolveIconUrl(const QString &relative)
{
    if (!isContainedRelativePath(relative))
        return {};
    const QString localAppData = qEnvironmentVariable("LOCALAPPDATA");
    if (!localAppData.isEmpty()) {
        const QString user = QDir(localAppData).filePath(QStringLiteral("MentorRecorder/icons/") + relative);
        if (QFile::exists(user))
            return QUrl::fromLocalFile(user).toString();
    }
    if (QFile::exists(QStringLiteral(":/resources/icons/") + relative))
        return QStringLiteral("qrc:/resources/icons/") + relative;
    return {};
}

} // namespace

namespace mr {

JobCatalog::JobCatalog(QObject *parent) : QObject(parent)
{
    loadJobs();
    loadCategories();
}

void JobCatalog::loadJobs()
{
    const QJsonDocument doc = readJson(QStringLiteral(":/resources/icons/manifest.json"));
    const QJsonArray jobs = doc.object().value(QStringLiteral("jobs")).toArray();

    for (const QJsonValue &value : jobs) {
        const QJsonObject object = value.toObject();
        Job job;
        job.id = object.value(QStringLiteral("id")).toInt();
        job.name = object.value(QStringLiteral("zh_name")).toString();
        if (job.name.isEmpty())
            job.name = object.value(QStringLiteral("en_name")).toString();
        job.abbreviation = object.value(QStringLiteral("abbreviation")).toString();
        job.roleGroup = object.value(QStringLiteral("role")).toString();
        job.role = roleFromRaw(object.value(QStringLiteral("role_raw")).toString());

        const QString framed = object.value(QStringLiteral("icon_framed"))
                                   .toObject()
                                   .value(QStringLiteral("file"))
                                   .toString();
        const QString plain = object.value(QStringLiteral("icon"))
                                  .toObject()
                                  .value(QStringLiteral("file"))
                                  .toString();
        job.framedIcon = resolveIconUrl(framed);
        job.plainIcon = resolveIconUrl(plain);

        if (job.id > 0)
            m_jobs.insert(job.id, job);
    }

    if (m_jobs.isEmpty())
        qCWarning(lcIcons) << "job catalogue is empty - icons will fall back to text";
}

void JobCatalog::loadCategories()
{
    const QJsonDocument doc = readJson(QStringLiteral(":/resources/icons/content_types.json"));
    const QJsonArray rows = doc.object().value(QStringLiteral("rows")).toArray();

    for (const QJsonValue &value : rows) {
        const QJsonObject object = value.toObject();
        const QString zh = object.value(QStringLiteral("zh_name")).toString();
        const QString file = object.value(QStringLiteral("file")).toString();
        if (zh.isEmpty() || file.isEmpty())
            continue;
        m_categoryIcons.insert(zh, resolveIconUrl(file));
    }

    // 团队任务 (alliance raids) has no ContentType row of its own; the game
    // shares the Raids icon. See content_types.json "note".
    const auto raids = m_categoryIcons.constFind(QString::fromUtf8("大型任务"));
    if (raids != m_categoryIcons.constEnd())
        m_categoryIcons.insert(QString::fromUtf8("团队任务"), *raids);
}

const JobCatalog::Job *JobCatalog::find(const QVariant &jobId) const
{
    if (!jobId.isValid() || jobId.isNull())
        return nullptr;
    bool ok = false;
    const int id = jobId.toInt(&ok);
    if (!ok)
        return nullptr;
    const auto it = m_jobs.constFind(id);
    return it == m_jobs.constEnd() ? nullptr : &(*it);
}

QString JobCatalog::framedIcon(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->framedIcon : QString();
}

QString JobCatalog::plainIcon(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->plainIcon : QString();
}

QString JobCatalog::jobName(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->name : QString::fromUtf8("未知");
}

QString JobCatalog::abbreviation(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->abbreviation : QStringLiteral("?");
}

QString JobCatalog::role(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->role : QStringLiteral("UNKNOWN");
}

QString JobCatalog::roleGroup(const QVariant &jobId) const
{
    const Job *job = find(jobId);
    return job ? job->roleGroup : QString::fromUtf8("未知");
}

QString JobCatalog::tokenForRoleGroup(const QString &group)
{
    if (group == QString::fromUtf8("坦克"))     return QStringLiteral("accent");
    if (group == QString::fromUtf8("治疗"))     return QStringLiteral("green");
    if (group == QString::fromUtf8("近战"))     return QStringLiteral("red");
    if (group == QString::fromUtf8("远程物理")) return QStringLiteral("orange");
    if (group == QString::fromUtf8("魔法"))     return QStringLiteral("purple");
    return QStringLiteral("neutral400");
}

QString JobCatalog::roleGroupToken(const QVariant &jobId) const
{
    return tokenForRoleGroup(roleGroup(jobId));
}

QString JobCatalog::categoryIcon(const QVariant &dutyCategory) const
{
    if (!dutyCategory.isValid() || dutyCategory.isNull())
        return {};
    return m_categoryIcons.value(dutyCategory.toString());
}

QVariantList JobCatalog::allJobs() const
{
    QList<int> ids = m_jobs.keys();
    std::sort(ids.begin(), ids.end());

    QVariantList out;
    out.reserve(ids.size());
    for (int id : std::as_const(ids)) {
        const Job &job = m_jobs[id];
        QVariantMap entry;
        entry.insert(QStringLiteral("job_id"), job.id);
        entry.insert(QStringLiteral("job_name"), job.name);
        entry.insert(QStringLiteral("abbreviation"), job.abbreviation);
        entry.insert(QStringLiteral("role"), job.role);
        entry.insert(QStringLiteral("role_group"), job.roleGroup);
        entry.insert(QStringLiteral("icon"), job.framedIcon);
        out.append(entry);
    }
    return out;
}

} // namespace mr
