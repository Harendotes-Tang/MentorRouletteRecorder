// ---------------------------------------------------------------------------
// 副本提醒与导随宏 fixtures.
//
// DutyCommunity against an in-memory library: a handful of local entries, some of them
// written for the duty the mock's live run enters (content 17), and a small public library
// that is listed only after the player allowed it and asked for a refresh. Nothing is sent
// anywhere: this backend has no network client, and the GitHub actions answer as a build
// without a configured app does. The local actions refuse what the Collector's
// CommunityWorkspace and CommunityHandlers refuse (the field set, ids, enumerations, lengths, the
// character rule, tags and links, revisions, a reused request_id, an install before the library
// was read), and install brings an entry's linked entries along as the Collector does, so the
// dialog's refusal paths can be reached here too and a test that passes here passes there.
// ---------------------------------------------------------------------------

#include "MockBackend.h"

#include <QJsonArray>
#include <QRegularExpression>
#include <QSet>

#include <algorithm>
#include <cmath>
#include <iterator>
#include <limits>

namespace mr {
namespace {

constexpr int MockDutyId = 17;
constexpr int MaxLocalEntries = 400;

/// The fields of an entry's content (CommunityContent.Fields); a published entry has more.
constexpr const char *DraftFields[] = {"schema_version", "id", "type", "content_id", "region", "language", "title",
                                       "body", "boss", "phase", "role", "tags", "game_version", "links", "source"};

/// CommunityContent.Draft: the content fields of a published entry.
QJsonObject draftOf(const QJsonObject &entry)
{
    QJsonObject draft;
    for (const char *key : DraftFields)
        draft.insert(QString::fromLatin1(key), entry.value(QString::fromLatin1(key)));
    return draft;
}

/// CommunityContent.MacroError: a chat message is one /p or /a line of at most 500 UTF-8 bytes,
/// a macro at most `maxLines` lines of 180 bytes each.
QString macroError(const QString &body, const QString &mode, qsizetype maxLines)
{
    const QStringList lines = QString(body).replace(QStringLiteral("\r\n"), QStringLiteral("\n")).split(QLatin1Char('\n'));
    if (body.contains(QChar(0)) || std::any_of(lines.cbegin(), lines.cend(),
                                               [](const QString &line) { return line.contains(QLatin1Char('\r')); }))
        return QStringLiteral("invalid_body");
    const bool chat = mode == QLatin1String("chat");
    static const QRegularExpression chatCommand(QStringLiteral("\\A/[pa][ \\t]+\\S"));
    if (chat && (lines.size() != 1 || !chatCommand.match(body).hasMatch()))
        return QStringLiteral("single_chat_required");
    if (lines.size() > maxLines)
        return QStringLiteral("macro_lines");
    const qsizetype limit = chat ? 500 : 180;
    for (const QString &line : lines) {
        if (line.toUtf8().size() > limit)
            return chat ? QStringLiteral("chat_length") : QStringLiteral("macro_length");
    }
    return {};
}

/// CommunityContent.Integer: a JSON number that is a whole `int`.
bool integerValue(const QJsonValue &value, int *out)
{
    if (!value.isDouble())
        return false;
    const double number = value.toDouble();
    if (number != std::floor(number) || number < std::numeric_limits<int>::min()
        || number > std::numeric_limits<int>::max())
        return false;
    *out = static_cast<int>(number);
    return true;
}

/// CommunityContent.IsId: a lower-case GUID in the "D" form, not the empty one.
bool isId(const QString &value)
{
    static const QRegularExpression pattern(
        QStringLiteral("\\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z"));
    return pattern.match(value).hasMatch() && value != QLatin1String("00000000-0000-0000-0000-000000000000");
}

/// CommunityContent.ValidText: no control character (a multi-line body keeps its line feeds and
/// tabs). `strict` adds the characters refused since audit 2026-11-26 R1-4: bidi controls and the
/// line and paragraph separators everywhere, zero-width characters in a single-line field.
bool validText(const QString &text, bool multiline, bool strict)
{
    const QString normalized = multiline ? QString(text).replace(QStringLiteral("\r\n"), QStringLiteral("\n")) : text;
    for (const QChar c : normalized) {
        const char16_t u = c.unicode();
        const bool control = u <= 0x1F || (u >= 0x7F && u <= 0x9F);
        if (control && !(multiline && (u == u'\n' || u == u'\t')))
            return false;
        if (!strict)
            continue;
        if ((u >= 0x202A && u <= 0x202E) || (u >= 0x2066 && u <= 0x2069) || u == 0x2028 || u == 0x2029)
            return false;
        if (!multiline && ((u >= 0x200B && u <= 0x200D) || u == 0x2060 || u == 0xFEFF))
            return false;
    }
    return true;
}

/// CommunityContent.String: `key` as text of at most `limit` characters that the character rule
/// accepts, or false.
bool textValue(const QJsonObject &data, const QString &key, qsizetype limit, bool multiline, bool strict, QString *out)
{
    const QJsonValue value = data.value(key);
    if (!value.isString())
        return false;
    *out = value.toString();
    return out->size() <= limit && validText(*out, multiline, strict);
}

/// CommunityContent.ValidateLocalDraft, field by field and in its order: the refusal, or "".
/// The characters refused since audit 2026-11-26 are refused only in a field whose value differs
/// from `stored`, the entry's saved content (empty for a new entry): published or older local
/// content is not judged again by the newer rule, so an older entry can still be edited (RC-1, RC-6).
QString draftError(const QJsonObject &item, const QJsonObject &stored)
{
    const auto strict = [&](const QString &key) { return stored.isEmpty() || stored.value(key) != item.value(key); };
    const auto refused = [](const QString &key) { return QStringLiteral("invalid_") + key; };
    const auto text = [&](const QString &key, qsizetype limit, QString *out) {
        return textValue(item, key, limit, key == QLatin1String("body"), strict(key), out);
    };
    const QString invalid = QStringLiteral("invalid_content");
    if (item.size() != qsizetype(std::size(DraftFields))
        || !std::all_of(std::begin(DraftFields), std::end(DraftFields),
                        [&](const char *key) { return item.contains(QLatin1String(key)); }))
        return invalid;
    int number = 0;
    if (!integerValue(item.value(QStringLiteral("schema_version")), &number))
        return refused(QStringLiteral("schema_version"));
    if (number != 1)
        return invalid;
    QString id;
    if (!text(QStringLiteral("id"), 36, &id))
        return refused(QStringLiteral("id"));
    if (!isId(id))
        return invalid;
    const struct { const char *key; qsizetype limit; QStringList allowed; } enums[] = {
        {"type", 8, {QStringLiteral("reminder"), QStringLiteral("macro")}},
        {"region", 6, {QStringLiteral("ALL"), QStringLiteral("CN"), QStringLiteral("TC"), QStringLiteral("GLOBAL")}},
        {"language", 7, {QStringLiteral("zh-Hans"), QStringLiteral("zh-Hant"), QStringLiteral("en"), QStringLiteral("ja")}},
    };
    QString value;
    for (const auto &field : enums) {
        const QString key = QString::fromLatin1(field.key);
        if (!text(key, field.limit, &value))
            return refused(key);
        if (!field.allowed.contains(value))
            return invalid;
    }
    if (!integerValue(item.value(QStringLiteral("content_id")), &number))
        return refused(QStringLiteral("content_id"));
    if (number < 0 || number > 65535)
        return invalid;
    if (!text(QStringLiteral("title"), 120, &value) || value.trimmed().isEmpty())
        return QStringLiteral("invalid_title");
    for (const char *field : {"boss", "phase", "role", "game_version"}) {
        const QString key = QString::fromLatin1(field);
        if (!text(key, 80, &value))
            return refused(key);
    }
    if (!text(QStringLiteral("source"), 240, &value))
        return refused(QStringLiteral("source"));
    // CommunityContent.Values: at most ten distinct single-line texts that `accept` takes.
    const auto list = [&](const QString &key, const auto &accept) {
        const QJsonValue node = item.value(key);
        if (!node.isArray() || node.toArray().size() > 10)
            return false;
        QSet<QString> seen;
        for (const QJsonValue &entry : node.toArray()) {
            if (!entry.isString() || !validText(entry.toString(), false, strict(key)) || !accept(entry.toString())
                || seen.contains(entry.toString()))
                return false;
            seen.insert(entry.toString());
        }
        return true;
    };
    if (!list(QStringLiteral("tags"), [](const QString &tag) { return !tag.isEmpty() && tag.size() <= 40; }))
        return refused(QStringLiteral("tags"));
    if (!list(QStringLiteral("links"), [&id](const QString &link) { return isId(link) && link != id; }))
        return refused(QStringLiteral("links"));
    QString body;
    if (!text(QStringLiteral("body"), 1600, &body))
        return QStringLiteral("invalid_body");
    if (body.trimmed().isEmpty())
        return QStringLiteral("empty_body");
    if (item.value(QStringLiteral("type")).toString() == QLatin1String("macro"))
        return macroError(body, QStringLiteral("macro"), std::numeric_limits<qsizetype>::max());
    return {};
}

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
        // The reminder links a macro, so installing it brings the macro along.
        if (entry.value(QStringLiteral("id")).toString() == mockId(105))
            entry.insert(QStringLiteral("links"), QJsonArray{mockId(104)});
        // Published before the zero-width characters were refused in a title (an emoji joined by
        // U+200D): the library still offers it and its copy can be edited (audit 2026-11-26 RC-1).
        if (entry.value(QStringLiteral("id")).toString() == mockId(106))
            entry.insert(QStringLiteral("title"), entry.value(QStringLiteral("title")).toString()
                                                      + QString::fromUtf8("\xF0\x9F\x91\xA9\xE2\x80\x8D\xF0\x9F\x8D\xB3"));
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

QJsonObject status(const QString &value)
{
    return {{QStringLiteral("status"), value}, {QStringLiteral("app_configured"), false}};
}

QJsonObject status(const char *value)
{
    return status(QString::fromLatin1(value));
}

int rowIndex(const QJsonArray &rows, const QString &id)
{
    for (int index = 0; index < rows.size(); ++index) {
        if (rows.at(index).toObject().value(QStringLiteral("content")).toObject().value(QStringLiteral("id")).toString() == id)
            return index;
    }
    return -1;
}

QJsonObject contentAt(const QJsonArray &rows, int index)
{
    return rows.at(index).toObject().value(QStringLiteral("content")).toObject();
}

/// The published entry `id` of the mock's public library, or an empty object.
QJsonObject publishedEntry(const QString &id)
{
    const QJsonArray entries = seededPublic();
    for (const QJsonValue &entry : entries) {
        if (entry.toObject().value(QStringLiteral("id")).toString() == id)
            return entry.toObject();
    }
    return {};
}

bool withdrawn(const QJsonObject &entry)
{
    return entry.value(QStringLiteral("status")).toString() == QLatin1String("withdrawn");
}

/// CommunityWorkspace.GetRemoteSet: the entry, then the entries it links to directly that are
/// still offered; nothing while the library was never read or the entry is not offered.
QList<QJsonObject> remoteSet(const QString &id, bool fetched)
{
    const QJsonObject entry = fetched ? publishedEntry(id) : QJsonObject();
    if (entry.isEmpty() || withdrawn(entry))
        return {};
    QList<QJsonObject> items{entry};
    for (const QJsonValue &link : entry.value(QStringLiteral("links")).toArray()) {
        const QJsonObject target = publishedEntry(link.toString());
        if (!target.isEmpty() && !withdrawn(target))
            items.append(target);
    }
    return items;
}

QJsonValue authorId(const QJsonObject &entry)
{
    return entry.value(QStringLiteral("author")).toObject().value(QStringLiteral("id"));
}

/// CommunityWorkspace.KeepInstalledLinks: links only to entries `local` holds or `installing` adds.
QJsonObject keepInstalledLinks(const QJsonArray &local, QJsonObject draft, const QSet<QString> &installing)
{
    QJsonArray kept;
    for (const QJsonValue &link : draft.value(QStringLiteral("links")).toArray()) {
        if (installing.contains(link.toString()) || rowIndex(local, link.toString()) >= 0)
            kept.append(link);
    }
    draft.insert(QStringLiteral("links"), kept);
    return draft;
}

/// CommunityWorkspace.AddMissing: the entries not here yet, unless an entry that is here was not
/// taken from the same author's public entry.
QJsonObject addMissing(QJsonArray &local, const QList<QJsonObject> &items)
{
    QList<QJsonObject> missing;
    for (const QJsonObject &item : items) {
        if (rowIndex(local, item.value(QStringLiteral("id")).toString()) < 0)
            missing.append(item);
    }
    if (local.size() + missing.size() > MaxLocalEntries)
        return status("local_limit");
    QSet<QString> installing;
    for (const QJsonObject &item : items) {
        const QString itemId = item.value(QStringLiteral("id")).toString();
        installing.insert(itemId);
        const int index = rowIndex(local, itemId);
        if (index < 0)
            continue;
        const QJsonValue upstream = local.at(index).toObject().value(QStringLiteral("upstream"));
        if (!upstream.isObject() || authorId(upstream.toObject()) != authorId(item))
            return status("local_conflict");
    }
    // Saved in reverse so the selected entry appears above its newly installed links; the local
    // copy holds the content alone and the published fields stay with `upstream`.
    for (auto it = missing.crbegin(); it != missing.crend(); ++it) {
        QJsonObject row = localRow(keepInstalledLinks(local, draftOf(*it), installing), 0, false);
        row.insert(QStringLiteral("upstream"), *it);
        local.prepend(row);
    }
    return status("installed");
}

/// The local actions of CommunityWorkspace.Change against `local`, the mock's local rows. A
/// refusal the Collector throws as a FormatException (a malformed or missing field) sets `thrown`:
/// it rolls the change back and leaves no receipt.
QJsonObject communityChange(QJsonArray &local, bool fetched, const QString &action, const QString &id,
                            const QJsonObject &payload, bool *thrown)
{
    const auto refuse = [thrown](const QString &value) {
        *thrown = true;
        return status(value);
    };
    const auto revisionOf = [&](int index) { return local.at(index).toObject().value(QStringLiteral("revision")).toInt(); };
    int revision = 0;
    const auto readInteger = [&](const char *key, int *out) {
        return integerValue(payload.value(QString::fromLatin1(key)), out);
    };
    // CommunityWorkspace.Id for `local_id`: text first, then the id form.
    const auto readLocalId = [&](QString *out) -> QString {
        if (!textValue(payload, QStringLiteral("local_id"), 36, false, true, out))
            return QStringLiteral("invalid_local_id");
        return isId(*out) ? QString() : QStringLiteral("invalid_id");
    };

    if (action == QLatin1String("save")) {
        const QJsonValue contentValue = payload.value(QStringLiteral("content"));
        if (!contentValue.isObject())
            return refuse(QStringLiteral("invalid_content"));
        const QJsonObject item = contentValue.toObject();
        const int index = rowIndex(local, item.value(QStringLiteral("id")).toString());
        const QJsonObject existing = index >= 0 ? local.at(index).toObject() : QJsonObject();
        if (const QString error = draftError(item, existing.value(QStringLiteral("content")).toObject()); !error.isEmpty())
            return refuse(error);
        if (!readInteger("revision", &revision))
            return refuse(QStringLiteral("invalid_revision"));
        // A new entry is saved at revision 0; an existing one at the revision it was read at.
        if ((index >= 0 ? revisionOf(index) : 0) != revision)
            return status("local_conflict");
        if (index < 0 && local.size() >= MaxLocalEntries)
            return status("local_limit");
        if (index >= 0 && contentAt(local, index).value(QStringLiteral("type")) != item.value(QStringLiteral("type")))
            return status("invalid_type");
        for (const QJsonValue &link : item.value(QStringLiteral("links")).toArray()) {
            const int target = rowIndex(local, link.toString());
            if (target < 0 || contentAt(local, target).value(QStringLiteral("type")) == item.value(QStringLiteral("type")))
                return status("invalid_links");
        }
        int position = 0;
        if (!readInteger("position", &position))
            return refuse(QStringLiteral("invalid_position"));
        if (position < -9999 || position > 9999)
            return status("invalid_position");
        const QJsonValue favorite = payload.value(QStringLiteral("favorite"));
        if (!favorite.isBool())
            return refuse(QStringLiteral("invalid_favorite"));
        QJsonObject row{{QStringLiteral("content"), item}, {QStringLiteral("revision"), revision + 1},
                        {QStringLiteral("position"), position}, {QStringLiteral("favorite"), favorite}};
        if (index >= 0) {
            const QJsonValue upstream = existing.value(QStringLiteral("upstream"));
            if (!upstream.isUndefined())
                row.insert(QStringLiteral("upstream"), upstream);
            if (existing.value(QStringLiteral("content")).toObject() == item) {
                local.replace(index, row);
                return status("saved");
            }
            local.removeAt(index);
        }
        local.prepend(row);
        return status("saved");
    }
    if (action == QLatin1String("delete")) {
        if (!isId(id))
            return refuse(QStringLiteral("invalid_id"));
        const int index = rowIndex(local, id);
        if (index < 0)
            return status("local_conflict");
        if (!readInteger("revision", &revision))
            return refuse(QStringLiteral("invalid_revision"));
        if (revisionOf(index) != revision)
            return status("local_conflict");
        local.removeAt(index);
        // Entries that linked to it lose the link, and that is a change of theirs.
        for (int other = 0; other < local.size(); ++other) {
            QJsonObject row = local.at(other).toObject();
            QJsonObject item = row.value(QStringLiteral("content")).toObject();
            QJsonArray links = item.value(QStringLiteral("links")).toArray();
            int link = 0;
            while (link < links.size() && links.at(link).toString() != id)
                ++link;
            if (link >= links.size())
                continue;
            links.removeAt(link);
            item.insert(QStringLiteral("links"), links);
            row.insert(QStringLiteral("content"), item);
            row.insert(QStringLiteral("revision"), row.value(QStringLiteral("revision")).toInt() + 1);
            local.replace(other, row);
        }
        return status("deleted");
    }
    if (action == QLatin1String("fork")) {
        if (!isId(id))
            return refuse(QStringLiteral("invalid_id"));
        QString newId;
        if (const QString error = readLocalId(&newId); !error.isEmpty())
            return refuse(error);
        const int index = rowIndex(local, id);
        // Only an entry taken from the public library has a personal version.
        if (index < 0 || !local.at(index).toObject().value(QStringLiteral("upstream")).isObject())
            return status("local_conflict");
        if (!readInteger("revision", &revision))
            return refuse(QStringLiteral("invalid_revision"));
        if (revisionOf(index) != revision || rowIndex(local, newId) >= 0)
            return status("local_conflict");
        if (local.size() >= MaxLocalEntries)
            return status("local_limit");
        QJsonObject row = local.at(index).toObject();
        QJsonObject item = row.value(QStringLiteral("content")).toObject();
        item.insert(QStringLiteral("id"), newId);
        row.insert(QStringLiteral("content"), item);
        row.insert(QStringLiteral("revision"), 1);
        local.prepend(row);
        return status("saved");
    }

    // install and apply take the entry and its linked entries from the library the Collector
    // last read; before any read there is nothing to take.
    const QList<QJsonObject> items = remoteSet(id, fetched);
    if (items.isEmpty())
        return status("source_unavailable");
    if (action == QLatin1String("install")) {
        if (rowIndex(local, id) >= 0)
            return status("already_installed");
        return addMissing(local, items);
    }
    const QJsonObject remote = items.constFirst();
    QString localId = id;
    const QJsonValue localValue = payload.value(QStringLiteral("local_id"));
    if (!localValue.isUndefined() && !localValue.isNull()) {
        if (const QString error = readLocalId(&localId); !error.isEmpty())
            return refuse(error);
    }
    const int index = rowIndex(local, localId);
    const QJsonObject old = index >= 0 ? local.at(index).toObject() : QJsonObject();
    const QJsonObject basis = old.value(QStringLiteral("upstream")).toObject();
    if (index < 0 || !old.value(QStringLiteral("upstream")).isObject()
        || basis.value(QStringLiteral("id")).toString() != id)
        return status("local_conflict");
    if (!readInteger("revision", &revision))
        return refuse(QStringLiteral("invalid_revision"));
    if (revisionOf(index) != revision || authorId(basis) != authorId(remote))
        return status("local_conflict");
    const int remoteRevision = remote.value(QStringLiteral("revision")).toInt();
    int sourceRevision = 0;
    if (!readInteger("source_revision", &sourceRevision))
        return refuse(QStringLiteral("invalid_source_revision"));
    if (remoteRevision != sourceRevision || remoteRevision <= basis.value(QStringLiteral("revision")).toInt())
        return status("source_changed");
    QString mode;
    if (!textValue(payload, QStringLiteral("mode"), 8, false, true, &mode)
        || (mode != QLatin1String("keep") && mode != QLatin1String("replace")))
        return refuse(QStringLiteral("invalid_mode"));
    QJsonObject draft = draftOf(remote);
    draft.insert(QStringLiteral("id"), localId);
    if (mode == QLatin1String("keep")) {
        // What the player changed since adopting the source stays theirs.
        const QJsonObject content = old.value(QStringLiteral("content")).toObject();
        for (const char *field : DraftFields) {
            const QString key = QString::fromLatin1(field);
            if (basis.value(key) != content.value(key))
                draft.insert(key, content.value(key));
        }
    }
    if (const QJsonObject installed = addMissing(local, items.mid(1));
        installed.value(QStringLiteral("status")).toString() != QLatin1String("installed"))
        return installed;
    QJsonObject row = old;
    row.insert(QStringLiteral("content"), keepInstalledLinks(local, draft, {}));
    row.insert(QStringLiteral("revision"), revision + 1);
    row.insert(QStringLiteral("upstream"), remote);
    local.removeAt(rowIndex(local, localId));
    local.prepend(row);
    return status("updated");
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
    const bool install = action == QLatin1String("install") || action == QLatin1String("apply");
    if (install || action == QLatin1String("save") || action == QLatin1String("delete")
        || action == QLatin1String("fork")) {
        // CommunityWorkspace.Change reads the entry id of install and apply before anything else.
        if (install && !isId(id))
            return status("invalid_id");
        // CommunityRepository.Change: a request_id already answered replays that answer, and is
        // refused when it now names another request. A refusal the Collector throws (a malformed
        // field) leaves no receipt.
        const QString requestId = m_requestId;
        if (!requestId.isEmpty()) {
            const auto receipt = m_appliedRequests.constFind(requestId);
            if (receipt != m_appliedRequests.constEnd())
                return receipt->fingerprint == m_requestFingerprint ? receipt->result : status("request_conflict");
        }
        bool thrown = false;
        const QJsonObject result = communityChange(m_communityLocal, m_communityFetched, action, id, payload, &thrown);
        if (!requestId.isEmpty() && !thrown) {
            m_appliedRequests.insert(requestId, AppliedRequest{.messageType = QStringLiteral("DutyCommunity"),
                                                               .fingerprint = m_requestFingerprint,
                                                               .result = result});
        }
        return result;
    }
    if (action == QLatin1String("copy")) {
        // CommunityHandlers.Copy: a chat message and a macro are held to their limits, and the
        // body to the character rule of a multi-line field.
        QString body;
        QString mode;
        if (!textValue(payload, QStringLiteral("body"), 1600, true, true, &body))
            return status("invalid_body");
        if (!textValue(payload, QStringLiteral("mode"), 8, false, true, &mode))
            return status("invalid_mode");
        if (body.trimmed().isEmpty())
            return status("empty_body");
        if (mode != QLatin1String("reminder") && mode != QLatin1String("chat") && mode != QLatin1String("macro"))
            return status("invalid_mode");
        if (mode != QLatin1String("reminder")) {
            if (const QString error = macroError(body, mode, 15); !error.isEmpty())
                return status(error);
        }
        QJsonObject result = status("copy_ready");
        result.insert(QStringLiteral("body"), body);
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
