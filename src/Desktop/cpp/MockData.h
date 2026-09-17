#pragma once

// ---------------------------------------------------------------------------
// Small helpers shared by the two MockBackend translation units.
// Nothing here is specific to the mock data itself; the predicates encode the
// filtering rules of docs/statistics-definitions.md.
// ---------------------------------------------------------------------------

#include <QCryptographicHash>
#include <QDateTime>
#include <QJsonObject>
#include <QJsonValue>
#include <QString>
#include <QTimeZone>

namespace mr::mock {

inline QString isoUtc(const QDateTime &dt)
{
    return dt.toUTC().toString(Qt::ISODateWithMs);
}

inline QDateTime fromIso(const QJsonValue &value)
{
    if (!value.isString())
        return {};
    QDateTime dt = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
    if (!dt.isValid())
        dt = QDateTime::fromString(value.toString(), Qt::ISODate);
    if (dt.isValid() && dt.timeSpec() == Qt::LocalTime)
        dt.setTimeZone(QTimeZone::UTC);
    return dt;
}

/// Deterministic pseudo-UUID so mock ids stay stable between processes.
inline QString mockUuid(const QString &seed)
{
    const QByteArray hash =
        QCryptographicHash::hash(seed.toUtf8(), QCryptographicHash::Md5).toHex();
    return QStringLiteral("%1-%2-%3-%4-%5")
        .arg(QString::fromLatin1(hash.mid(0, 8)),
             QString::fromLatin1(hash.mid(8, 4)),
             QString::fromLatin1(hash.mid(12, 4)),
             QString::fromLatin1(hash.mid(16, 4)),
             QString::fromLatin1(hash.mid(20, 12)));
}

/// docs/statistics-definitions.md 1 - "confirmed mentor".
inline bool isConfirmedMentor(const QJsonObject &run)
{
    const QString source = run.value(QStringLiteral("source")).toString();
    if (source == QLatin1String("MANUAL"))
        return true;
    if (source == QLatin1String("AUTO_NETWORK"))
        return !run.value(QStringLiteral("mentor_roulette_id")).isNull();
    if (source == QLatin1String("IMPORT"))
        return run.value(QStringLiteral("import_confirmed_mentor")).toBool(false);
    return false;
}

/// docs/statistics-definitions.md 2 - only runs that actually entered a duty
/// take part in attempt_count and therefore in every rate.
inline bool hasEntered(const QJsonObject &run)
{
    const QJsonValue value = run.value(QStringLiteral("entered_at_utc"));
    return value.isString() && !value.toString().isEmpty();
}

inline bool isSoftDeleted(const QJsonObject &run)
{
    return run.value(QStringLiteral("soft_deleted")).toBool(false);
}

/// A JSON number when \a defined, JSON null otherwise. Undefined statistics
/// are null and are rendered as an em dash - never as zero.
inline QJsonValue nullableNumber(bool defined, double value)
{
    return defined ? QJsonValue(value) : QJsonValue(QJsonValue::Null);
}

} // namespace mr::mock
