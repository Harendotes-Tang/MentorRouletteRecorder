#include "DutyCommunityController.h"

#include "DutyCatalog.h"
#include "IBackend.h"

#include <QClipboard>
#include <QDesktopServices>
#include <QFile>
#include <QFileDialog>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QRegularExpression>
#include <QSaveFile>
#include <QStringDecoder>
#include <QUuid>
#include <QUrlQuery>

namespace mr {
namespace {

constexpr auto GuideDirectory = "https://ff14.org/duty/";
constexpr auto Repo = "https://github.com/Harendotes-Tang/MentorRecorder-Calibrations";
constexpr qint64 MaxFileBytes = 700'000;

const QJsonObject &guideLinks()
{
    static const QJsonObject links = [] {
        QFile file(QStringLiteral(":/data/duty-guides/ff14-org.json"));
        if (!file.open(QIODevice::ReadOnly))
            return QJsonObject{};
        return QJsonDocument::fromJson(file.readAll()).object().value(QStringLiteral("links")).toObject();
    }();
    return links;
}

QVariantMap identity(const QVariantMap &run)
{
    const auto region = run.value(QStringLiteral("region")).toString();
    if (region != QLatin1String("CN") && region != QLatin1String("TC") && region != QLatin1String("GLOBAL"))
        return {};
    const auto confidence = run.value(QStringLiteral("detection_confidence")).toString();
    if (run.value(QStringLiteral("source")).toString() == QLatin1String("AUTO_NETWORK")
        && (confidence == QLatin1String("LOW") || confidence == QLatin1String("NONE")))
        return {};
    const auto *catalog = DutyCatalog::shared();
    const auto contentId = run.value(QStringLiteral("content_id"));
    const auto territory = run.value(QStringLiteral("territory_id")).toInt();
    const auto entry = contentId.isNull() && territory > 0
        ? catalog->uniqueDutyForTerritory(territory, region)
        : catalog->lookup(contentId, region);
    if (entry.isEmpty() || (territory > 0 && territory != entry.value(QStringLiteral("territory_id")).toInt()))
        return {};
    return {{QStringLiteral("region"), region},
            {QStringLiteral("content_id"), entry.value(QStringLiteral("content_id"))},
            {QStringLiteral("territory_id"), entry.value(QStringLiteral("territory_id"))}};
}

} // namespace

DutyCommunityController::DutyCommunityController(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend) {}

QUrl DutyCommunityController::guideUrl(const QVariantMap &run)
{
    const auto duty = identity(run);
    const QUrl directory(QString::fromLatin1(GuideDirectory));
    const auto link = guideLinks().value(duty.value(QStringLiteral("content_id")).toString()).toArray();
    if (link.size() < 2)
        return directory;
    const auto regionOverrides = link.size() == 3 ? link.at(2).toObject() : QJsonObject{};
    const int expectedTerritory = regionOverrides.value(duty.value(QStringLiteral("region")).toString())
        .toInt(link.at(0).toInt());
    if (expectedTerritory != duty.value(QStringLiteral("territory_id")).toInt())
        return directory;
    const auto path = link.at(1).toString();
    static const QRegularExpression safePath(QStringLiteral("\\A[1-9][0-9]*\\.htm\\z"));
    return safePath.match(path).hasMatch() ? directory.resolved(QUrl(path)) : directory;
}

bool DutyCommunityController::hasGuide(const QVariantMap &run) const
{
    return guideUrl(run) != QUrl(QString::fromLatin1(GuideDirectory));
}

bool DutyCommunityController::openGuide(const QVariantMap &run) const
{
    return QDesktopServices::openUrl(guideUrl(run));
}

QVariantMap DutyCommunityController::dutyIdentity(const QVariantMap &run) const { return identity(run); }
QString DutyCommunityController::newId() const { return QUuid::createUuid().toString(QUuid::WithoutBraces); }

QVariantList DutyCommunityController::findDuties(const QString &region, const QString &query) const
{
    const auto term = query.trimmed();
    if (term.isEmpty())
        return {};
    QVariantList matches;
    const auto duties = DutyCatalog::shared()->allDuties(region == QLatin1String("ALL") ? QStringLiteral("CN") : region);
    for (const auto &duty : duties) {
        const auto row = duty.toMap();
        const auto id = row.value(QStringLiteral("content_id")).toString();
        const auto name = row.value(QStringLiteral("duty_name")).toString();
        if (id != term && !name.contains(term, Qt::CaseInsensitive))
            continue;
        matches.append(QVariantMap{{QStringLiteral("content_id"), row.value(QStringLiteral("content_id"))},
            {QStringLiteral("label"), name + QStringLiteral(" · ") + id}});
        if (matches.size() == 25)
            break;
    }
    return matches;
}

void DutyCommunityController::request(int ticket, const QString &action, const QVariantMap &payload)
{
    if (!m_backend) {
        emit answered(ticket, {}, tr("记录服务未连接"));
        return;
    }
    auto args = QJsonObject::fromVariantMap(payload);
    args.insert(QStringLiteral("action"), action);
    m_backend->dutyCommunity(args)->whenDone(this, [this, ticket](bool ok, const QVariantMap &result,
                                                     const QString &, const QString &error) {
        emit answered(ticket, result, ok ? QString() : error);
    });
}

QString DutyCommunityController::exportFile(const QVariantList &items) const
{
    if (items.isEmpty() || items.size() > 20)
        return QStringLiteral("invalid_file");
    QJsonArray rows;
    for (const auto &value : items) {
        auto row = QJsonObject::fromVariantMap(value.toMap());
        row.remove(QStringLiteral("revision"));
        rows.append(row);
    }
    const QJsonObject document{{QStringLiteral("schema_version"), 1}, {QStringLiteral("items"), rows}};
    const auto bytes = QJsonDocument(document).toJson(QJsonDocument::Indented);
    if (bytes.size() > MaxFileBytes)
        return QStringLiteral("invalid_file");
    const auto path = QFileDialog::getSaveFileName(nullptr, tr("导出提醒和宏"),
        QStringLiteral("mentor-reminders.json"), tr("JSON 文件 (*.json)"));
    if (path.isEmpty())
        return {};
    QSaveFile file(path);
    return file.open(QIODevice::WriteOnly) && file.write(bytes) == bytes.size() && file.commit()
        ? QStringLiteral("exported") : QStringLiteral("file_error");
}

QVariantMap DutyCommunityController::pickImport() const
{
    const auto path = QFileDialog::getOpenFileName(nullptr, tr("导入提醒和宏"), {}, tr("JSON 文件 (*.json)"));
    if (path.isEmpty())
        return {};
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly))
        return {{QStringLiteral("status"), QStringLiteral("file_error")}};
    const auto bytes = file.read(MaxFileBytes + 1);
    if (bytes.isEmpty() || bytes.size() > MaxFileBytes)
        return {{QStringLiteral("status"), QStringLiteral("invalid_file")}};
    QStringDecoder decoder(QStringDecoder::Utf8);
    const QString data = decoder.decode(bytes);
    if (decoder.hasError())
        return {{QStringLiteral("status"), QStringLiteral("invalid_file")}};
    // The Collector checks the JSON structure, duplicate keys and nesting depth.
    return {{QStringLiteral("data"), data}};
}

bool DutyCommunityController::copy(const QString &value) const
{
    auto *clipboard = QGuiApplication::clipboard();
    if (!clipboard)
        return false;
    clipboard->setText(value);
    return clipboard->text() == value;
}

bool DutyCommunityController::openGitHub(const QString &target, int issue) const
{
    if (target == QLatin1String("device"))
        return QDesktopServices::openUrl(QUrl(QStringLiteral("https://github.com/login/device")));
    if (target == QLatin1String("issue") && issue > 0)
        return QDesktopServices::openUrl(QUrl(QString::fromLatin1(Repo) + QStringLiteral("/issues/") + QString::number(issue)));
    if (target != QLatin1String("fallback"))
        return false;
    QUrl url(QString::fromLatin1(Repo) + QStringLiteral("/issues/new"));
    QUrlQuery query;
    query.addQueryItem(QStringLiteral("template"), QStringLiteral("share-community-content.yml"));
    query.addQueryItem(QStringLiteral("title"), QStringLiteral("MentorRecorder community contribution"));
    url.setQuery(query);
    return QDesktopServices::openUrl(url);
}

} // namespace mr
