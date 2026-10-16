#include "CollectorText.h"

#include "CollectorTextCatalog.h"
#include "GameNames.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QHash>
#include <QList>
#include <QRegularExpression>
#include <QVariantList>
#include <QVariantMap>

#include <cmath>
#include <optional>
#include <utility>

namespace mr::collector_text {

namespace {

// The tr() context of every Collector text (i18n/*.ts).
constexpr char kContext[] = "CollectorText";

// A reference names other references through text_ref arguments. The deepest
// the Collector writes is an import refusal listing header errors: up to eight
// joined by import.table.errors_joined inside import.source.unreadable, ten
// levels. Deeper is refused (the Chinese is shown), so a peer cannot recurse
// without bound.
constexpr int kMaxDepth = 16;

enum class Kind { Number, Text, Duty, Job, Roulette, Clock, Ref };

struct Arg
{
    QString name;
    Kind kind = Kind::Text;
};

struct Entry
{
    const char *source = nullptr;
    const char *key = nullptr;
    QList<Arg> args;
};

std::optional<Kind> kindNamed(const QString &name)
{
    static const QHash<QString, Kind> kinds{
        {QStringLiteral("number"), Kind::Number},     {QStringLiteral("text"), Kind::Text},
        {QStringLiteral("duty"), Kind::Duty},         {QStringLiteral("job"), Kind::Job},
        {QStringLiteral("roulette"), Kind::Roulette}, {QStringLiteral("clock"), Kind::Clock},
        {QStringLiteral("text_ref"), Kind::Ref},
    };
    const auto it = kinds.constFind(name);
    if (it == kinds.constEnd())
        return std::nullopt;
    return *it;
}

const QHash<QString, Entry> &table()
{
    static const QHash<QString, Entry> entries = [] {
        QHash<QString, Entry> built;
        for (const CatalogueEntry *it = catalogueBegin(); it != catalogueEnd(); ++it) {
            Entry entry;
            entry.source = it->source.text;
            entry.key = it->source.key;
            const QString spec = QString::fromUtf8(it->args);
            for (const QString &part : spec.split(QLatin1Char(','), Qt::SkipEmptyParts)) {
                const qsizetype colon = part.indexOf(QLatin1Char(':'));
                const std::optional<Kind> kind = kindNamed(part.mid(colon + 1));
                // The generator writes only known kinds; a hand-edited table that
                // does not is a build defect the catalogue test reports.
                entry.args.append({part.left(colon), kind.value_or(Kind::Text)});
            }
            built.insert(QString::fromUtf8(entry.key), std::move(entry));
        }
        return built;
    }();
    return entries;
}

std::optional<QString> renderRef(const QVariant &ref, int depth, bool translate);

// The Collector's placeholders for a name it does not know are its own words,
// not game text, so they are translated like the sentence around them.
const CatalogueEntry::Source kUnknownNames[] = {
    QT_TRANSLATE_NOOP3("CollectorText", "未知副本", "name.unknown_duty"),
    QT_TRANSLATE_NOOP3("CollectorText", "未知", "name.unknown_job"),
    QT_TRANSLATE_NOOP3("CollectorText", "未知随机任务", "name.unknown_roulette"),
};

QString gameName(Kind kind, const QString &recorded)
{
    const auto &unknown = kUnknownNames[kind == Kind::Duty ? 0 : kind == Kind::Job ? 1 : 2];
    if (recorded == QString::fromUtf8(unknown.text))
        return QCoreApplication::translate(kContext, unknown.text, unknown.key);
    GameNames *names = GameNames::shared();
    if (!names || recorded.isEmpty())
        return recorded;
    if (kind == Kind::Duty)
        return names->duty(QVariant(), recorded);
    if (kind == Kind::Job)
        return names->job(QVariant(), recorded);
    return names->rouletteByName(recorded);
}

std::optional<QString> renderArg(const Arg &arg, const QVariant &value, int depth, bool translate)
{
    if (!value.isValid() || value.isNull())
        return std::nullopt;
    switch (arg.kind) {
    case Kind::Number: {
        // A JSON integer arrives as qlonglong, or as a double when it was
        // written with a fraction or exponent; a string or a boolean is not a
        // number, however QVariant would convert it.
        switch (value.typeId()) {
        case QMetaType::Int:
        case QMetaType::UInt:
        case QMetaType::LongLong:
        case QMetaType::ULongLong:
            return QString::number(value.toLongLong());
        case QMetaType::Double: {
            const double number = value.toDouble();
            if (number != std::trunc(number) || std::abs(number) > 9.0e15)
                return std::nullopt;
            return QString::number(qlonglong(number));
        }
        default:
            return std::nullopt;
        }
    }
    case Kind::Text:
        if (value.typeId() != QMetaType::QString)
            return std::nullopt;
        return value.toString();
    case Kind::Duty:
    case Kind::Job:
    case Kind::Roulette: {
        if (value.typeId() != QMetaType::QString)
            return std::nullopt;
        // Simplified Chinese is the record's own spelling.
        return translate ? gameName(arg.kind, value.toString()) : value.toString();
    }
    case Kind::Clock: {
        const QDateTime at = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
        if (!at.isValid())
            return std::nullopt;
        return at.toLocalTime().toString(QStringLiteral("HH:mm"));
    }
    case Kind::Ref:
        return renderRef(value, depth + 1, translate);
    }
    return std::nullopt;
}

std::optional<QString> renderRef(const QVariant &ref, int depth, bool translate)
{
    if (depth > kMaxDepth || ref.typeId() != QMetaType::QVariantMap)
        return std::nullopt;
    const QVariantMap map = ref.toMap();
    const auto entry = table().constFind(map.value(QStringLiteral("key")).toString());
    if (entry == table().constEnd())
        return std::nullopt;

    const QVariantMap args = map.value(QStringLiteral("args")).toMap();
    QHash<QString, QString> values;
    for (const Arg &arg : entry->args) {
        std::optional<QString> value = renderArg(arg, args.value(arg.name), depth, translate);
        if (!value)
            return std::nullopt;
        values.insert(arg.name, *value);
    }

    // One pass over the translated template: a value that itself contains
    // "{name}" (a file name, the player's own words) is never expanded again.
    const QString translated = translate
                                   ? QCoreApplication::translate(kContext, entry->source, entry->key)
                                   : QString::fromUtf8(entry->source);
    static const QRegularExpression kPlaceholder(QStringLiteral("\\{([a-z][a-z0-9_]*)\\}"));
    QString out;
    out.reserve(translated.size() + 16);
    qsizetype from = 0;
    for (auto it = kPlaceholder.globalMatch(translated); it.hasNext();) {
        const QRegularExpressionMatch match = it.next();
        const auto value = values.constFind(match.captured(1));
        if (value == values.constEnd())
            continue; // not one of ours: left as written
        out += QStringView(translated).mid(from, match.capturedStart() - from);
        out += *value;
        from = match.capturedEnd();
    }
    out += QStringView(translated).mid(from);
    return out;
}

} // namespace

QString render(const QVariant &ref, const QString &fallback)
{
    std::optional<QString> text = renderRef(ref, 0, true);
    return text ? *text : fallback;
}

QString chinese(const QVariant &ref)
{
    return renderRef(ref, 0, false).value_or(QString());
}

QVariantMap textRef(const char *key, const QVariantMap &args)
{
    QVariantMap ref{{QStringLiteral("key"), QString::fromUtf8(key)}};
    if (!args.isEmpty())
        ref.insert(QStringLiteral("args"), args);
    return ref;
}

LiveText live(const QVariant &ref, const QString &fallback)
{
    if (!renderRef(ref, 0, true))
        return LiveText::verbatim(fallback);
    return LiveText([ref, fallback] { return render(ref, fallback); });
}

QStringList renderList(const QVariant &refs, const QVariant &fallbacks)
{
    const QStringList chinese = fallbacks.toStringList();
    const QVariantList list = refs.toList();
    if (list.size() != chinese.size())
        return chinese;
    QStringList out;
    out.reserve(chinese.size());
    for (qsizetype i = 0; i < chinese.size(); ++i)
        out.append(render(list.at(i), chinese.at(i)));
    return out;
}

LiveText replyErrorText(const BackendReply *reply, const QString &message)
{
    return live(reply ? reply->errorRef() : QVariant(), message);
}

bool knows(const QString &key)
{
    return table().contains(key);
}

int size()
{
    return int(table().size());
}

} // namespace mr::collector_text
