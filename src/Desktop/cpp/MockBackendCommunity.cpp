// ---------------------------------------------------------------------------
// 副本提醒与导随宏 fixtures.
//
// DutyCommunity against an in-memory library: a handful of local entries, some of them
// written for the duty the mock's live run enters (content 17), and a small public library
// that is listed only after the player allowed it and asked for a refresh. Nothing is sent
// anywhere: this backend has no network client, and the GitHub actions answer as a build
// without a configured app does.
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include <QJsonArray>

namespace mr {
namespace {

constexpr int MockDutyId = 17;

QString mockId(int number)
{
    return QStringLiteral("00000000-0000-4000-8000-%1").arg(number, 12, 10, QLatin1Char('0'));
}

QJsonObject content(int number, const char *type, int contentId, const char *region, const char *title,
                    const char *body, const char *boss = "", const char *phase = "")
{
    return {{QStringLiteral("schema_version"), 1},
            {QStringLiteral("id"), mockId(number)},
            {QStringLiteral("type"), QString::fromLatin1(type)},
            {QStringLiteral("content_id"), contentId},
            {QStringLiteral("region"), QString::fromLatin1(region)},
            {QStringLiteral("language"), QStringLiteral("zh-Hans")},
            {QStringLiteral("title"), QString::fromUtf8(title)},
            {QStringLiteral("body"), QString::fromUtf8(body)},
            {QStringLiteral("boss"), QString::fromUtf8(boss)},
            {QStringLiteral("phase"), QString::fromUtf8(phase)},
            {QStringLiteral("role"), QString()},
            {QStringLiteral("tags"), QJsonArray()},
            {QStringLiteral("game_version"), QString()},
            {QStringLiteral("links"), QJsonArray()},
            {QStringLiteral("source"), QString()}};
}

QJsonObject localRow(const QJsonObject &item, int position, bool favorite)
{
    return {{QStringLiteral("content"), item},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("position"), position},
            {QStringLiteral("favorite"), favorite}};
}

QJsonArray seededLocal()
{
    return {
        localRow(content(1, "macro", MockDutyId, "CN", "进本先说节奏",
                         "/p 大家好～不熟悉机制的话可以说一声 <se.1>\n/p 小怪我们一波一波拉，不赶时间。", "开场"), 0, true),
        localRow(content(2, "reminder", MockDutyId, "CN", "尾王转场记得集合",
                         "转场读条时全员靠近场地中央，等分摊结束再散开。\n治疗提前铺好群体回复。", "尾王", "转场"), 1, false),
        localRow(content(3, "macro", 0, "ALL", "打完道谢", "/p 辛苦了，谢谢大家！<se.3>"), 2, false),
        localRow(content(4, "reminder", 0, "ALL", "中途加入先问进度",
                         "先问现在打到哪里、有没有需要说明的机制，再开怪。"), 3, false),
        localRow(content(5, "macro", 4, "CN", "等动画", "/p 等大家看完动画再开，先准备一下。", "BOSS 1"), 4, false),
    };
}

QJsonArray seededPublic()
{
    const QJsonObject author{{QStringLiteral("login"), QStringLiteral("aurora-mentor")}, {QStringLiteral("id"), 1001}};
    QJsonArray entries;
    const QJsonObject drafts[] = {
        content(101, "macro", MockDutyId, "CN", "开场拉怪说明", "/p 第一波拉到墙边，坦克开减伤。<se.1>", "开场"),
        content(102, "reminder", MockDutyId, "CN", "BOSS 2 点名分散",
                "被点名的人去场边，其余人留在中间。", "BOSS 2"),
        content(103, "macro", 0, "ALL", "新人说明", "/p 第一次来的话跟着我走就行，不用紧张。"),
        content(104, "macro", 0, "ALL", "中途加入询问", "/p 我是中途加入的，现在进度到哪里？"),
        content(105, "reminder", 4, "CN", "尾王击退", "击退前站到 BOSS 脚下，防击退技能留给第二次。", "尾王"),
        // Reads like 103: the public list folds it under 「另有 1 条相似」.
        content(106, "macro", 0, "ALL", "新人说明（感叹号版）", "/p 第一次来的话跟着我走就行，不用紧张！"),
    };
    int issue = 128;
    for (QJsonObject entry : drafts) {
        entry.insert(QStringLiteral("author"), author);
        entry.insert(QStringLiteral("license"), QStringLiteral("CC-BY-4.0"));
        entry.insert(QStringLiteral("status"), issue == 128 ? QStringLiteral("verified") : QStringLiteral("community"));
        entry.insert(QStringLiteral("revision"), 1);
        entry.insert(QStringLiteral("issue"), issue++);
        entry.insert(QStringLiteral("updated_at"), QStringLiteral("2026-11-12"));
        entries.append(entry);
    }
    return entries;
}

QJsonObject status(const char *value)
{
    return {{QStringLiteral("status"), QString::fromLatin1(value)}, {QStringLiteral("app_configured"), false}};
}

int rowIndex(const QJsonArray &rows, const QString &id)
{
    for (int index = 0; index < rows.size(); ++index) {
        if (rows.at(index).toObject().value(QStringLiteral("content")).toObject().value(QStringLiteral("id")).toString() == id)
            return index;
    }
    return -1;
}

} // namespace

QJsonObject MockBackend::dutyCommunity(const QJsonObject &payload)
{
    if (!m_communitySeeded) {
        m_communitySeeded = true;
        m_communityLocal = seededLocal();
    }
    const QString action = payload.value(QStringLiteral("action")).toString();
    const QString id = payload.value(QStringLiteral("id")).toString();
    const auto library = [this] {
        QJsonObject result = status(!m_communityOnline ? "offline" : m_communityFetched ? "ready" : "not_refreshed");
        result.insert(QStringLiteral("online"), m_communityOnline);
        result.insert(QStringLiteral("cached"), m_communityFetched);
        result.insert(QStringLiteral("entries"), m_communityFetched ? seededPublic() : QJsonArray());
        result.insert(QStringLiteral("next"), QJsonValue::Null);
        result.insert(QStringLiteral("generation"), 1);
        return result;
    };

    if (action == QLatin1String("list")) {
        QJsonObject result = status("ready");
        result.insert(QStringLiteral("items"), m_communityLocal);
        result.insert(QStringLiteral("next"), QJsonValue::Null);
        result.insert(QStringLiteral("generation"), 1);
        return result;
    }
    if (action == QLatin1String("browse"))
        return library();
    if (action == QLatin1String("online")) {
        m_communityOnline = payload.value(QStringLiteral("enabled")).toBool();
        return library();
    }
    if (action == QLatin1String("refresh")) {
        m_communityFetched = m_communityFetched || m_communityOnline;
        return library();
    }
    if (action == QLatin1String("save")) {
        QJsonObject row{{QStringLiteral("content"), payload.value(QStringLiteral("content"))},
                        {QStringLiteral("position"), payload.value(QStringLiteral("position"))},
                        {QStringLiteral("favorite"), payload.value(QStringLiteral("favorite"))}};
        const QJsonObject item = row.value(QStringLiteral("content")).toObject();
        if (item.value(QStringLiteral("title")).toString().trimmed().isEmpty())
            return status("invalid_title");
        if (item.value(QStringLiteral("body")).toString().trimmed().isEmpty())
            return status("empty_body");
        const int index = rowIndex(m_communityLocal, item.value(QStringLiteral("id")).toString());
        row.insert(QStringLiteral("revision"), payload.value(QStringLiteral("revision")).toInt() + 1);
        if (index < 0) {
            m_communityLocal.prepend(row);
        } else {
            const QJsonValue upstream = m_communityLocal.at(index).toObject().value(QStringLiteral("upstream"));
            if (!upstream.isUndefined())
                row.insert(QStringLiteral("upstream"), upstream);
            m_communityLocal.replace(index, row);
        }
        return status("saved");
    }
    if (action == QLatin1String("delete")) {
        const int index = rowIndex(m_communityLocal, id);
        if (index < 0)
            return status("local_conflict");
        m_communityLocal.removeAt(index);
        return status("deleted");
    }
    if (action == QLatin1String("install")) {
        if (rowIndex(m_communityLocal, id) >= 0)
            return status("already_installed");
        const QJsonArray entries = seededPublic();
        for (const QJsonValue &entry : entries) {
            if (entry.toObject().value(QStringLiteral("id")).toString() != id)
                continue;
            QJsonObject row = localRow(entry.toObject(), 0, false);
            row.insert(QStringLiteral("upstream"), entry);
            m_communityLocal.prepend(row);
            return status("installed");
        }
        return status("source_unavailable");
    }
    if (action == QLatin1String("fork")) {
        const int index = rowIndex(m_communityLocal, id);
        if (index < 0)
            return status("local_conflict");
        QJsonObject row = m_communityLocal.at(index).toObject();
        QJsonObject item = row.value(QStringLiteral("content")).toObject();
        item.insert(QStringLiteral("id"), payload.value(QStringLiteral("local_id")));
        row.insert(QStringLiteral("content"), item);
        row.insert(QStringLiteral("revision"), 1);
        m_communityLocal.prepend(row);
        return status("saved");
    }
    if (action == QLatin1String("copy")) {
        QJsonObject result = status("copy_ready");
        result.insert(QStringLiteral("body"), payload.value(QStringLiteral("body")));
        return result;
    }
    if (action == QLatin1String("issue_status"))
        return status(payload.value(QStringLiteral("issue")).toInt() == 128 ? "published" : "processing");
    if (action == QLatin1String("prepare_issue")) {
        QJsonObject result = status("prepared");
        result.insert(QStringLiteral("body"), QStringLiteral("mock submission"));
        return result;
    }
    if (action == QLatin1String("account"))
        return status("signed_out");
    if (action == QLatin1String("import_file"))
        return status("invalid_file");
    return status("app_not_configured");
}

} // namespace mr
