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

/// Mirrors MentorRun.IsConfirmedMentor, including confirmed imported history.
inline bool isConfirmedMentor(const QJsonObject &run)
{
    const QString source = run.value(QStringLiteral("source")).toString();
    const QJsonObject metadata = run.value(QStringLiteral("import_metadata")).toObject();
    if (source == QLatin1String("IMPORT") || !metadata.isEmpty())
        return !run.value(QStringLiteral("pending_review")).toBool(false)
            && run.value(QStringLiteral("result")).toString() != QLatin1String("UNKNOWN")
            && (run.value(QStringLiteral("mentor_roulette_id")).isDouble()
                || metadata.value(QStringLiteral("mentor_confirmed")).toBool(false));
    if (source == QLatin1String("MANUAL"))
        return true;
    if (source == QLatin1String("AUTO_NETWORK"))
        return !run.value(QStringLiteral("mentor_roulette_id")).isNull();
    return false;
}

/// Mirrors StatisticsRepository.IsAttempt: known imported outcomes count even
/// when source history did not provide the gameplay timestamps.
inline bool hasEntered(const QJsonObject &run)
{
    const QJsonValue value = run.value(QStringLiteral("entered_at_utc"));
    if (value.isString() && !value.toString().isEmpty()) return true;
    const QString result = run.value(QStringLiteral("result")).toString();
    return (run.value(QStringLiteral("source")).toString() == QLatin1String("IMPORT")
            || run.value(QStringLiteral("import_metadata")).isObject())
        && result != QLatin1String("UNKNOWN") && result != QLatin1String("CANCELLED_BEFORE_ENTRY");
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
