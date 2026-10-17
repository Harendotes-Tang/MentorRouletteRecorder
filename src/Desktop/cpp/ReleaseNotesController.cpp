#include "ReleaseNotesController.h"

#include "AppSettings.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>
#include <QStringList>

namespace {

QString bundledChangelog()
{
    QFile file(QStringLiteral(":/release-notes/CHANGELOG.md"));
    return file.open(QIODevice::ReadOnly) ? QString::fromUtf8(file.readAll()) : QString();
}

QByteArray bundledPresentation()
{
    QFile file(QStringLiteral(":/release-notes/presentation.json"));
    return file.open(QIODevice::ReadOnly) ? file.readAll() : QByteArray();
}

struct Version {
    QStringList numbers;
    QStringList prerelease;
    bool valid = false;
};

Version parseVersion(const QString &version)
{
    static const QRegularExpression pattern(QStringLiteral(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)"
        "(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?"
        "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$"));
    const auto match = pattern.match(version);
    if (!match.hasMatch())
        return {};
    Version parsed{{match.captured(1), match.captured(2), match.captured(3)}, {}, true};
    if (!match.captured(4).isEmpty())
        parsed.prerelease = match.captured(4).split(QLatin1Char('.'));
    static const QRegularExpression numeric(QStringLiteral("^[0-9]+$"));
    for (const auto &part : parsed.prerelease) {
        if (numeric.match(part).hasMatch() && part.size() > 1 && part.startsWith(QLatin1Char('0')))
            return {};
    }
    return parsed;
}

/// 数字串按长度、再按字符比较，避免版本段转整数溢出。
int compareNumber(const QString &left, const QString &right)
{
    if (left.size() != right.size())
        return left.size() > right.size() ? 1 : -1;
    return QString::compare(left, right, Qt::CaseSensitive);
}

QStringList versionBody(const QString &changelog, const QString &version)
{
    if (!parseVersion(version).valid)
        return {};
    static const QRegularExpression heading(QStringLiteral("^## +\\[([^\\]]+)\\](?:[ \\t].*)?$"));
    QStringList body;
    bool selected = false;
    for (QString line : changelog.split(QLatin1Char('\n'))) {
        if (line.endsWith(QLatin1Char('\r')))
            line.chop(1);
        if (line.startsWith(QLatin1String("## "))) {
            if (selected)
                break;
            const auto match = heading.match(line);
            selected = match.hasMatch() && match.captured(1) == version;
        } else if (selected) {
            body.append(line);
        }
    }
    return body;
}

QString categoryId(const QString &section)
{
    if (section == QString::fromUtf8("新增") || section.compare("Added", Qt::CaseInsensitive) == 0)
        return QStringLiteral("added");
    if (section == QString::fromUtf8("修复") || section.compare("Fixed", Qt::CaseInsensitive) == 0)
        return QStringLiteral("fixed");
    if (section == QString::fromUtf8("改进") || section.compare("Changed", Qt::CaseInsensitive) == 0
        || section.compare("Improved", Qt::CaseInsensitive) == 0)
        return QStringLiteral("improved");
    return section.isEmpty() ? QStringLiteral("other") : QStringLiteral("other:") + section;
}

} // namespace

namespace mr {

ReleaseNotesController::ReleaseNotesController(AppSettings *settings, QObject *parent)
    : ReleaseNotesController(settings, QStringLiteral(MR_APP_VERSION), bundledChangelog(), parent)
{
}

ReleaseNotesController::ReleaseNotesController(AppSettings *settings, const QString &version,
                                               const QString &changelog, QObject *parent)
    : QObject(parent), m_settings(settings), m_version(version), m_text(extractNotes(changelog, version)),
      m_entries(parseEntries(changelog, version, bundledPresentation()))
{
    m_pending = settings && !m_text.isEmpty()
        && isNewerVersion(version, settings->releaseNotesSeenVersion());
    if (auto *application = QCoreApplication::instance())
        connect(application, &QCoreApplication::aboutToQuit, this, [this] { m_quitting = true; });
}

bool ReleaseNotesController::isNewerVersion(const QString &current, const QString &seen)
{
    const auto left = parseVersion(current);
    const auto right = parseVersion(seen);
    if (!left.valid)
        return false;
    if (!right.valid)
        return true;
    for (int i = 0; i < 3; ++i) {
        const int order = compareNumber(left.numbers[i], right.numbers[i]);
        if (order != 0)
            return order > 0;
    }
    if (left.prerelease.isEmpty() || right.prerelease.isEmpty())
        return !right.prerelease.isEmpty() && left.prerelease.isEmpty();
    static const QRegularExpression numeric(QStringLiteral("^[0-9]+$"));
    for (qsizetype i = 0; i < qMin(left.prerelease.size(), right.prerelease.size()); ++i) {
        const auto &a = left.prerelease[i];
        const auto &b = right.prerelease[i];
        const bool aNumber = numeric.match(a).hasMatch();
        const bool bNumber = numeric.match(b).hasMatch();
        if (aNumber != bNumber)
            return !aNumber;
        const int order = aNumber ? compareNumber(a, b) : QString::compare(a, b, Qt::CaseSensitive);
        if (order != 0)
            return order > 0;
    }
    return left.prerelease.size() > right.prerelease.size();
}

QString ReleaseNotesController::extractNotes(const QString &changelog, const QString &version)
{
    QStringList body;
    for (QString line : versionBody(changelog, version)) {
        // 本地纯文本，不解释图片、HTML、外部链接或资源引用。
        line.remove(QRegularExpression(QStringLiteral("^#{3,6} +")));
        line.replace(QRegularExpression(QStringLiteral("^[-*] +")), QString::fromUtf8("• "));
        line.remove(QLatin1Char('`'));
        body.append(line);
    }
    return body.join(QLatin1Char('\n')).trimmed();
}

QVariantList ReleaseNotesController::parseEntries(const QString &changelog, const QString &version,
                                                  const QByteArray &presentation)
{
    const auto root = QJsonDocument::fromJson(presentation).object();
    const auto annotations = root.value("version").toString() == version
                                 ? root.value("entries").toArray() : QJsonArray();
    QVariantList entries;
    QString section;
    QStringList lines;
    bool highlighted = false;
    const auto flush = [&] {
        QString detail = lines.join(QLatin1Char('\n')).trimmed();
        lines.clear();
        detail.remove(QLatin1Char('`'));
        if (detail.isEmpty())
            return;
        const int sentenceEnd = detail.indexOf(QRegularExpression(QString::fromUtf8("[。！？\\n]")));
        const QString summary = sentenceEnd >= 0 ? detail.left(sentenceEnd + 1) : detail;
        const int titleEnd = summary.indexOf(QRegularExpression(QString::fromUtf8("[：，。！？\\n]")));
        const QString title = titleEnd > 0 ? summary.left(titleEnd) : summary;
        QVariantMap entry{{"id", QString::number(entries.size())}, {"category", categoryId(section)},
                          {"section", section}, {"title", title}, {"summary", summary},
                          {"detail", detail}, {"tag", QString()}, {"highlight", false},
                          {"action", QString()}};
        const QString digest = QString::fromLatin1(QCryptographicHash::hash(
                                       detail.toUtf8(), QCryptographicHash::Sha256).toHex());
        for (const auto &value : annotations) {
            const auto annotation = value.toObject();
            if (annotation.value("bodySha256").toString() != digest)
                continue;
            for (const auto &key : {"title", "summary", "tag"}) {
                const auto text = annotation.value(QLatin1String(key)).toString().trimmed();
                if (!text.isEmpty())
                    entry[QLatin1String(key)] = text;
            }
            if (annotation.value("action").toString() == QLatin1String("language-settings"))
                entry["action"] = QStringLiteral("language-settings");
            const bool highlight = !highlighted && annotation.value("highlight").toBool();
            entry["highlight"] = highlight;
            highlighted = highlighted || highlight;
            break;
        }
        entries.append(entry);
    };
    static const QRegularExpression category(QStringLiteral("^###[ \\t]+(.+)$"));
    static const QRegularExpression bullet(QStringLiteral("^[-*][ \\t]+"));
    for (QString line : versionBody(changelog, version)) {
        const auto heading = category.match(line);
        if (heading.hasMatch()) {
            flush();
            section = heading.captured(1).trimmed();
        } else if (bullet.match(line).hasMatch()) {
            flush();
            line.remove(bullet);
            lines.append(line);
        } else if (!lines.isEmpty() || !line.trimmed().isEmpty()) {
            lines.append(line.trimmed());
        }
    }
    flush();
    return entries;
}

QVariantMap ReleaseNotesController::counts() const
{
    QVariantMap counts;
    for (const auto &value : m_entries) {
        const auto category = value.toMap().value("category").toString();
        counts[category] = counts.value(category).toInt() + 1;
    }
    return counts;
}

bool ReleaseNotesController::prerelease() const
{
    return !parseVersion(m_version).prerelease.isEmpty();
}

void ReleaseNotesController::acknowledge()
{
    if (m_quitting || !m_settings || m_text.isEmpty() || !m_pending)
        return;
    if (isNewerVersion(m_version, m_settings->releaseNotesSeenVersion()))
        m_settings->setReleaseNotesSeenVersion(m_version);
    m_pending = false;
    Q_EMIT changed();
}

} // namespace mr
