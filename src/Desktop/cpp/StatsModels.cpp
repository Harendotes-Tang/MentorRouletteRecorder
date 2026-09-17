#include "StatsModels.h"

#include "DutyCatalog.h"
#include "IBackend.h"
#include "JobCatalog.h"

#include <QJsonArray>
#include <QJsonValue>
#include <QStringList>
#include <QVariantList>

namespace mr {

StatsRowsModel::StatsRowsModel(QObject *parent) : QAbstractListModel(parent) {}

void StatsRowsModel::setBackend(IBackend *backend)
{
    m_backend = backend;
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
    if (!m_backend)
        return;

    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), m_filter);
    payload.insert(QStringLiteral("page"), 1);
    payload.insert(QStringLiteral("page_size"), 200);

    m_loading = true;
    Q_EMIT loadingChanged();

    BackendReply *reply = m_backend->request(messageType(), payload);
    reply->whenDone(this,
            [this](bool ok, const QVariantMap &response,
                   const QString &code, const QString &message) {
                m_loading = false;
                Q_EMIT loadingChanged();

                beginResetModel();
                m_rows.clear();
                m_distinctCount = 0;
                if (ok) {
                    const QJsonObject object = QJsonObject::fromVariantMap(response);
                    const QJsonArray items =
                        object.value(QStringLiteral("items")).toArray();
                    m_rows.reserve(items.size());
                    for (const QJsonValue &value : items)
                        m_rows.append(decorate(value.toObject()));

                    // distinct_count is the Collector's own answer to "how many
                    // different duties are there"; page_info.total is the same
                    // number for an unfiltered page. Either beats counting the
                    // rows this page happens to hold.
                    const QJsonValue distinct = object.value(QStringLiteral("distinct_count"));
                    if (distinct.isDouble()) {
                        m_distinctCount = distinct.toInt();
                    } else {
                        m_distinctCount = object.value(QStringLiteral("page_info"))
                                              .toObject()
                                              .value(QStringLiteral("total"))
                                              .toInt();
                    }
                }
                endResetModel();
                Q_EMIT countChanged();

                if (!ok)
                    Q_EMIT loadFailed(code, message);
            });
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
