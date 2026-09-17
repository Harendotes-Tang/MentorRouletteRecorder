#include "RoleCatalog.h"

#include <QDir>
#include <QFile>
#include <QUrl>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLoggingCategory>

namespace {

Q_LOGGING_CATEGORY(lcRoles, "mr.icons")

constexpr auto kManifest = ":/resources/icons/roles/manifest.json";
constexpr auto kFallbackKey = "allrounder";

/// Relative paths baked in as a last resort: if the manifest ever fails to
/// parse the UI still shows framed glyphs instead of empty squares.
QString builtinPath(const QString &key)
{
    return QStringLiteral("resources/icons/roles/") + key + QStringLiteral(".png");
}

} // namespace

namespace mr {

RoleCatalog::RoleCatalog(QObject *parent) : QObject(parent)
{
    load();
}

QStringList RoleCatalog::roleGroups()
{
    return {QString::fromUtf8("坦克"),
            QString::fromUtf8("治疗"),
            QString::fromUtf8("近战"),
            QString::fromUtf8("远程物理"),
            QString::fromUtf8("魔法"),
            QString::fromUtf8("未知")};
}

QString RoleCatalog::roleKey(const QString &roleZh)
{
    const QString role = roleZh.trimmed();
    if (role == QString::fromUtf8("坦克") || role == QLatin1String("TANK"))
        return QStringLiteral("tank");
    if (role == QString::fromUtf8("治疗") || role == QLatin1String("HEALER"))
        return QStringLiteral("healer");
    if (role == QString::fromUtf8("近战"))
        return QStringLiteral("melee");
    if (role == QString::fromUtf8("远程物理") || role == QString::fromUtf8("远程"))
        return QStringLiteral("ranged");
    if (role == QString::fromUtf8("魔法"))
        return QStringLiteral("magic");
    // 未知 / 其他 / empty / anything unmapped -> the in-game All-Rounder plate.
    return QString::fromLatin1(kFallbackKey);
}

void RoleCatalog::load()
{
    QFile file(QString::fromLatin1(kManifest));
    if (file.open(QIODevice::ReadOnly)) {
        QJsonParseError error{};
        const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
        if (error.error != QJsonParseError::NoError) {
            qCWarning(lcRoles) << kManifest << "parse error" << error.errorString();
        } else {
            const QJsonArray entries = doc.object().value(QStringLiteral("entries")).toArray();
            for (const QJsonValue &value : entries) {
                const QJsonObject object = value.toObject();
                const QString key = object.value(QStringLiteral("key")).toString();
                const QString path = object.value(QStringLiteral("path")).toString();
                if (key.isEmpty() || path.isEmpty())
                    continue;
                m_iconsByKey.insert(key, QStringLiteral("resources/icons/") + path);
            }
        }
    } else {
        qCWarning(lcRoles) << "cannot open" << kManifest;
    }

    // Every key the mapping can produce must resolve, manifest or not.
    for (const QString &role : roleGroups()) {
        const QString key = roleKey(role);
        if (!m_iconsByKey.contains(key))
            m_iconsByKey.insert(key, builtinPath(key));
    }
}

QString RoleCatalog::roleIconResource(const QString &roleZh) const
{
    const QString key = roleKey(roleZh);
    const QString path = m_iconsByKey.value(key, builtinPath(key));
    return QStringLiteral(":/") + path;
}

QString RoleCatalog::roleIconSource(const QString &roleZh) const
{
    // A player's own extracted glyph first, then a personal build's embedded copy,
    // else empty: RoleIcon then draws its original text badge. Distributed builds
    // carry no game art (resources/icons/LICENSE-NOTE.md).
    const QString key = roleKey(roleZh);
    const QString relative = m_iconsByKey.value(key, builtinPath(key));
    // Manifest paths are compiled in, but never let one escape the icons directory.
    if (relative.contains(QLatin1String("..")) || relative.contains(QLatin1Char(':'))
        || relative.startsWith(QLatin1Char('/')) || relative.startsWith(QLatin1Char('\\')))
        return {};
    const QString localAppData = qEnvironmentVariable("LOCALAPPDATA");
    if (!localAppData.isEmpty()) {
        const QString user = QDir(localAppData).filePath(
            QStringLiteral("MentorRecorder/icons/") + relative.mid(QStringLiteral("resources/icons/").size()));
        if (QFile::exists(user))
            return QUrl::fromLocalFile(user).toString();
    }
    if (QFile::exists(QStringLiteral(":/") + relative))
        return QStringLiteral("qrc:/") + relative;
    return {};
}

} // namespace mr
