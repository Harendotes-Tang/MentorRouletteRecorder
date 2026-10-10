#include "StatsModels.h"

#include "DutyCatalog.h"
#include "IBackend.h"
#include "JobCatalog.h"

#include <QJsonArray>
#include <QFile>
#include <QJsonDocument>
#include <QJsonValue>
#include <QMap>
#include <QSet>
#include <QStringList>
#include <QTimer>
#include <QVariantList>

namespace mr {

namespace {

qint64 completionCount(const QJsonObject &row)
{
    return qMax(qint64(0), row.value(QStringLiteral("completed_count")).toInteger());
}

struct MainScenarioMembership {
    QSet<qint64> contentIds;
    bool available = false;
};

const MainScenarioMembership &mainScenarioMembership()
{
    // Display classification only: never infer a roulette or create Collector data.
    static const MainScenarioMembership membership = [] {
        MainScenarioMembership result;
        QFile file(QStringLiteral(":/resources/statistics/main-scenario-membership.json"));
        if (!file.open(QIODevice::ReadOnly)) {
            qWarning("cannot open installed main-scenario membership resource");
            return result;
        }
        QJsonParseError error{};
        const QJsonObject root = QJsonDocument::fromJson(file.readAll(), &error).object();
        if (error.error != QJsonParseError::NoError
            || root.value(QStringLiteral("schema_version")).toInt() != 1
            || root.value(QStringLiteral("scope")).toString() != QLatin1String("MENTOR_COMPLETIONS")) {
            qWarning("invalid installed main-scenario membership resource");
            return result;
        }
        for (const QJsonValue &value : root.value(QStringLiteral("duties")).toArray()) {
            const QJsonValue id = value.toObject().value(QStringLiteral("content_id"));
            if (!id.isDouble() || id.toInteger() <= 0
                || double(id.toInteger()) != id.toDouble()
                || DutyCatalog::shared()->lookup(id.toVariant()).isEmpty()) {
                qWarning("main-scenario member is absent from installed duty catalogue");
                return MainScenarioMembership{};
            }
            result.contentIds.insert(id.toInteger());
        }
        result.available = !result.contentIds.isEmpty();
        return result;
    }();
    return membership;
}

QVariantMap completionBucket(const QString &label, qint64 count, qint64 total,
                             const QString &token)
{
    return {{QStringLiteral("label"), label},
            {QStringLiteral("completed_count"), count},
            {QStringLiteral("share"), total > 0 ? QVariant(double(count) / double(total)) : QVariant()},
            {QStringLiteral("color_token"), token}};
}

QString dutyCategory(const QJsonObject &row)
{
    QString category = row.value(QStringLiteral("duty_category")).toString().trimmed();
    if (category.isEmpty()) {
        category = DutyCatalog::shared()->lookup(row.value(QStringLiteral("content_id")).toVariant())
                       .value(QStringLiteral("duty_category")).toString().trimmed();
    }
    return category.isEmpty() ? QString::fromUtf8("未识别") : category;
}

} // namespace

StatsRowsModel::StatsRowsModel(QObject *parent) : QAbstractListModel(parent) {}

void StatsRowsModel::setBackend(IBackend *backend)
{
    ++m_loadGeneration;
    m_backend = backend;
    if (m_loading) {
        m_loading = false;
        Q_EMIT loadingChanged();
    }
}

int StatsRowsModel::rowCount(const QModelIndex &parent) const
{
    return parent.isValid() ? 0 : int(m_rows.size());
}

QVariant StatsRowsModel::data(const QModelIndex &index, int role) const
{
    if (!index.isValid() || index.row() < 0 || index.row() >= m_rows.size())
        return {};
    if (role != RowRole)
        return {};
    return m_rows.at(index.row()).toVariantMap();
}

QHash<int, QByteArray> StatsRowsModel::roleNames() const
{
    return {{RowRole, QByteArrayLiteral("row")}};
}

int StatsRowsModel::distinctCount() const
{
    return qMax(m_distinctCount, int(m_rows.size()));
}

int StatsRowsModel::maxAttemptCount() const
{
    int max = 1;
    for (const QJsonObject &row : m_rows)
        max = qMax(max, row.value(QStringLiteral("attempt_count")).toInt());
    return max;
}

int StatsRowsModel::totalAttemptCount() const
{
    int total = 0;
    for (const QJsonObject &row : m_rows)
        total += row.value(QStringLiteral("attempt_count")).toInt();
    return total;
}

void StatsRowsModel::setFilter(const QVariantMap &filter)
{
    m_filter = QJsonObject::fromVariantMap(filter);
    reload();
}

QVariantMap StatsRowsModel::rowAt(int row) const
{
    if (row < 0 || row >= m_rows.size())
        return {};
    return m_rows.at(row).toVariantMap();
}

QVariantList StatsRowsModel::topRows(int limit) const
{
    QVariantList out;
    const int count = limit <= 0 ? int(m_rows.size())
                                 : qMin(limit, int(m_rows.size()));
    out.reserve(count);
    for (int i = 0; i < count; ++i)
        out.append(m_rows.at(i).toVariantMap());
    return out;
}

void StatsRowsModel::reload()
{
    const quint64 generation = ++m_loadGeneration;
    if (!m_backend)
        return;

    m_loading = true;
    Q_EMIT loadingChanged();
    loadPage(generation, 1, {});
}

void StatsRowsModel::loadPage(quint64 generation, int page, QList<QJsonObject> rows, int total)
{
    if (generation != m_loadGeneration || !m_backend)
        return;
    constexpr int pageSize = 200;
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), m_filter);
    payload.insert(QStringLiteral("page"), page);
    payload.insert(QStringLiteral("page_size"), pageSize);

    BackendReply *reply = m_backend->request(messageType(), payload);
    reply->whenDone(this,
            [this, generation, page, rows = std::move(rows), total, pageSize](
                bool ok, const QVariantMap &response,
                const QString &code, const QString &message) mutable {
                // A filter change or a live refresh supersedes every page of
                // the preceding request, including a failure arriving late.
                if (generation != m_loadGeneration)
                    return;
                if (!ok) {
                    failLoad(generation, code, message);
                    return;
                }

                const QJsonObject object = QJsonObject::fromVariantMap(response);
                const QJsonArray items = object.value(QStringLiteral("items")).toArray();
                const QJsonObject info = object.value(QStringLiteral("page_info")).toObject();
                const int reportedTotal = info.value(QStringLiteral("total")).toInt(
                    object.value(QStringLiteral("distinct_count")).toInt(int(items.size())));
                if (total < 0)
                    total = reportedTotal;
                if (reportedTotal != total || total < 0
                    || info.value(QStringLiteral("page")).toInt(page) != page
                    || info.value(QStringLiteral("page_size")).toInt(pageSize) != pageSize
                    || items.size() != qMin(pageSize, qMax(0, total - int(rows.size())))) {
                    failLoad(generation, QStringLiteral("ERR_BAD_RESPONSE"),
                             tr("统计分页在读取时发生变化，请刷新后重试。"));
                    return;
                }
                for (const QJsonValue &value : items)
                    rows.append(decorate(value.toObject()));
                if (rows.size() < total) {
                    // Queue the next page so a synchronously completed backend
                    // cannot recurse through an arbitrarily large catalogue.
                    QTimer::singleShot(0, this,
                        [this, generation, page, rows = std::move(rows), total]() mutable {
                            loadPage(generation, page + 1, std::move(rows), total);
                        });
                    return;
                }

                beginResetModel();
                m_rows = std::move(rows);
                m_distinctCount = total;
                m_loading = false;
                endResetModel();
                setLoadError({});
                Q_EMIT countChanged();
                if (generation == m_loadGeneration)
                    Q_EMIT loadingChanged();
            });
}

void StatsRowsModel::setLoadError(const QString &error)
{
    if (m_loadError == error)
        return;
    m_loadError = error;
    Q_EMIT loadErrorChanged();
}

void StatsRowsModel::failLoad(quint64 generation, const QString &code, const QString &message)
{
    if (generation != m_loadGeneration)
        return;
    beginResetModel();
    m_rows.clear();
    m_distinctCount = 0;
    m_loading = false;
    endResetModel();
    // Set before countChanged, so whoever rebuilds from the emptied rows can
    // tell a failed read from an answer of zero (review OD-3 / S2-2).
    setLoadError(message.isEmpty() ? code : message);
    Q_EMIT countChanged();
    if (generation == m_loadGeneration) {
        Q_EMIT loadingChanged();
        Q_EMIT loadFailed(code, message);
    }
}

QJsonObject DungeonStatsModel::decorate(const QJsonObject &row) const
{
    // $defs/DungeonStatsRow is additionalProperties:false and carries neither
    // duty_level nor duty_expansion. The bundled catalogue fills the second line
    // of each row for display only; a value the Collector did send always wins.
    QJsonObject out = row;
    const QVariantMap catalogue =
        DutyCatalog::shared()->lookup(row.value(QStringLiteral("content_id")).toVariant());

    if (!out.value(QStringLiteral("duty_level")).isDouble()
        && catalogue.contains(QStringLiteral("duty_level"))) {
        out.insert(QStringLiteral("duty_level"),
                   catalogue.value(QStringLiteral("duty_level")).toInt());
    }
    if (out.value(QStringLiteral("duty_expansion")).toString().isEmpty()
        && !catalogue.value(QStringLiteral("duty_expansion")).toString().isEmpty()) {
        out.insert(QStringLiteral("duty_expansion"),
                   catalogue.value(QStringLiteral("duty_expansion")).toString());
    }
    if (out.value(QStringLiteral("duty_name")).toString().isEmpty()
        && !catalogue.value(QStringLiteral("duty_name")).toString().isEmpty()) {
        out.insert(QStringLiteral("duty_name"),
                   catalogue.value(QStringLiteral("duty_name")).toString());
    }

    if (out.value(QStringLiteral("duty_meta")).toString().isEmpty()) {
        const QString expansion = out.value(QStringLiteral("duty_expansion")).toString();
        const QJsonValue level = out.value(QStringLiteral("duty_level"));
        QStringList parts;
        if (!expansion.isEmpty())
            parts.append(expansion);
        if (level.isDouble())
            parts.append(QString::fromUtf8("%1级").arg(level.toInt()));
        if (!parts.isEmpty())
            out.insert(QStringLiteral("duty_meta"), parts.join(QString::fromUtf8(" · ")));
    }
    return out;
}

qint64 DungeonStatsModel::totalCompletedCount() const
{
    qint64 total = 0;
    for (const QJsonObject &row : m_rows)
        total += completionCount(row);
    return total;
}

QVariantList DungeonStatsModel::categoryBreakdown() const
{
    // Keep ordinary categories stable, retain any additional canonical category,
    // and always make the unknown bucket visible, including an explicit zero.
    const QStringList order{QString::fromUtf8("四人迷宫"), QString::fromUtf8("讨伐歼灭战"),
                            QString::fromUtf8("大型任务"), QString::fromUtf8("团队任务"),
                            QString::fromUtf8("行会令")};
    QMap<QString, qint64> counts;
    for (const QString &category : order)
        counts.insert(category, 0);
    const QString unknown = QString::fromUtf8("未识别");
    counts.insert(unknown, 0);
    for (const QJsonObject &row : m_rows)
        counts[dutyCategory(row)] += completionCount(row);

    QStringList categories = order;
    for (auto it = counts.cbegin(); it != counts.cend(); ++it) {
        if (!order.contains(it.key()) && it.key() != unknown)
            categories.append(it.key());
    }
    categories.append(unknown);
    const QStringList tokens{QStringLiteral("blue"), QStringLiteral("orange"),
                             QStringLiteral("teal"), QStringLiteral("purple"),
                             QStringLiteral("green"), QStringLiteral("yellow")};
    const qint64 total = totalCompletedCount();
    QVariantList out;
    for (qsizetype i = 0; i < categories.size(); ++i) {
        const QString &category = categories.at(i);
        out.append(completionBucket(category, counts.value(category), total,
                   category == unknown ? QStringLiteral("neutral500") : tokens.at(i % tokens.size())));
    }
    return out;
}

QVariantList DungeonStatsModel::specialDutyBreakdown() const
{
    const auto &membership = mainScenarioMembership();
    qint64 counts[4]{};
    for (const QJsonObject &row : m_rows) {
        const QJsonValue id = row.value(QStringLiteral("content_id"));
        const QVariantMap catalogue = DutyCatalog::shared()->lookup(id.toVariant());
        int group = 3; // Missing/unknown identity is not evidence of an "other" duty.
        if (id.isDouble() && membership.available && membership.contentIds.contains(id.toInteger()))
            group = 0;
        else if (!catalogue.isEmpty()
                 && catalogue.value(QStringLiteral("duty_category")).toString() == QString::fromUtf8("行会令"))
            group = 1;
        else if (!catalogue.isEmpty() && membership.available)
            group = 2;
        counts[group] += completionCount(row);
    }
    const qint64 total = totalCompletedCount();
    return {completionBucket(QString::fromUtf8("主线副本"), counts[0], total, QStringLiteral("blue")),
            completionBucket(QString::fromUtf8("行会令"), counts[1], total, QStringLiteral("green")),
            completionBucket(QString::fromUtf8("其他副本"), counts[2], total, QStringLiteral("orange")),
            completionBucket(QString::fromUtf8("未识别"), counts[3], total, QStringLiteral("neutral500"))};
}

QString JobStatsModel::roleGroupOf(const QJsonObject &row)
{
    // 1. A backend that already speaks the prototype's fine-grained groups
    //    (the mock does) wins.
    const QString supplied = row.value(QStringLiteral("role_group")).toString();
    if (!supplied.isEmpty())
        return supplied;

    // 2. ipc-v1's $defs/JobStatsRow has no role_group: it carries job_id and the
    //    coarse $defs/Role (TANK / HEALER / DPS / UNKNOWN). The fine group is
    //    derived from job_id through the shipping catalogue, which is the same
    //    mapping the table's icons use.
    const QJsonValue jobId = row.value(QStringLiteral("job_id"));
    if (jobId.isDouble()) {
        static const JobCatalog catalog;
        const QString derived = catalog.roleGroup(jobId.toVariant());
        if (!derived.isEmpty() && derived != QString::fromUtf8("未知"))
            return derived;
    }

    // 3. Only the coarse role is known. TANK and HEALER map one-to-one; a DPS
    //    whose job the catalogue does not know stays 未知 rather than being
    //    assigned to a melee / ranged / magic bucket we cannot justify.
    const QString role = row.value(QStringLiteral("role")).toString();
    if (role == QLatin1String("TANK"))
        return QString::fromUtf8("坦克");
    if (role == QLatin1String("HEALER"))
        return QString::fromUtf8("治疗");
    return QString::fromUtf8("未知");
}

QVariantList JobStatsModel::roleBreakdown() const
{
    // Fixed order so the legend never reshuffles between refreshes, and so the
    // "未知" group is always present rather than hidden.
    static const char *kOrder[] = {"坦克", "治疗", "近战", "远程物理", "魔法", "未知"};

    QVariantList out;
    for (const char *name : kOrder) {
        const QString group = QString::fromUtf8(name);
        int attempts = 0;
        for (const QJsonObject &row : m_rows) {
            if (roleGroupOf(row) == group)
                attempts += row.value(QStringLiteral("attempt_count")).toInt();
        }
        QVariantMap entry;
        entry.insert(QStringLiteral("role_group"), group);
        entry.insert(QStringLiteral("attempt_count"), attempts);
        entry.insert(QStringLiteral("color_token"), JobCatalog::tokenForRoleGroup(group));
        out.append(entry);
    }
    return out;
}

} // namespace mr
