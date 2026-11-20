#include "DisplayTimeZone.h"

#include <QGlobalStatic>

#include <algorithm>
#include <cstdlib>
#include <utility>

namespace {

Q_GLOBAL_STATIC(mr::DisplayTimeZone, displayTimeZoneNotifier)

struct ChosenZone
{
    QString id;
    QTimeZone zone{QTimeZone::LocalTime};
};

ChosenZone &chosen()
{
    static ChosenZone state;
    return state;
}

// One or two zones per offset in common use, west to east; the settings order
// them by the offset they have now. Zones Qt does not know on this machine are
// left out (curatedIds()).
constexpr const char *kCuratedIds[] = {
    "Pacific/Pago_Pago",          // -11
    "Pacific/Honolulu",           // -10
    "Pacific/Marquesas",          // -09:30
    "America/Anchorage",          // -09 / -08
    "America/Los_Angeles",        // -08 / -07
    "America/Denver",             // -07 / -06
    "America/Phoenix",            // -07
    "America/Chicago",            // -06 / -05
    "America/Mexico_City",        // -06
    "America/New_York",           // -05 / -04
    "America/Bogota",             // -05
    "America/Halifax",            // -04 / -03
    "America/Caracas",            // -04
    "America/St_Johns",           // -03:30 / -02:30
    "America/Sao_Paulo",          // -03
    "America/Argentina/Buenos_Aires", // -03
    "Atlantic/South_Georgia",     // -02
    "Atlantic/Azores",            // -01 / +00
    "UTC",                        // +00
    "Europe/London",              // +00 / +01
    "Europe/Paris",               // +01 / +02
    "Europe/Berlin",              // +01 / +02
    "Europe/Athens",              // +02 / +03
    "Africa/Cairo",               // +02 / +03
    "Europe/Moscow",              // +03
    "Europe/Istanbul",            // +03
    "Asia/Tehran",                // +03:30
    "Asia/Dubai",                 // +04
    "Asia/Kabul",                 // +04:30
    "Asia/Karachi",               // +05
    "Asia/Kolkata",               // +05:30
    "Asia/Kathmandu",             // +05:45
    "Asia/Dhaka",                 // +06
    "Asia/Yangon",                // +06:30
    "Asia/Bangkok",               // +07
    "Asia/Jakarta",               // +07
    "Asia/Shanghai",              // +08
    "Asia/Taipei",                // +08
    "Asia/Hong_Kong",             // +08
    "Asia/Singapore",             // +08
    "Australia/Perth",            // +08
    "Asia/Tokyo",                 // +09
    "Asia/Seoul",                 // +09
    "Australia/Darwin",           // +09:30
    "Australia/Adelaide",         // +09:30 / +10:30
    "Australia/Brisbane",         // +10
    "Australia/Sydney",           // +10 / +11
    "Pacific/Noumea",             // +11
    "Pacific/Auckland",           // +12 / +13
    "Pacific/Tongatapu",          // +13
    "Pacific/Kiritimati",         // +14
};

bool knownZone(const QString &id)
{
    return !id.isEmpty() && QTimeZone(id.toUtf8()).isValid();
}

// The offered list keeps to the ids this machine lists itself; "UTC" is Qt's own.
bool offeredZone(const QString &id)
{
    return knownZone(id)
        && (id == QLatin1String("UTC") || QTimeZone::isTimeZoneIdAvailable(id.toUtf8()));
}

} // namespace

namespace mr {

DisplayTimeZone::DisplayTimeZone(QObject *parent) : QObject(parent) {}

DisplayTimeZone *DisplayTimeZone::notifier()
{
    return displayTimeZoneNotifier();
}

QString DisplayTimeZone::id()
{
    return chosen().id;
}

QString DisplayTimeZone::normalized(const QString &id)
{
    const QString trimmed = id.trimmed();
    return knownZone(trimmed) ? trimmed : QString();
}

void DisplayTimeZone::set(const QString &id)
{
    const QString next = normalized(id);
    ChosenZone &state = chosen();
    if (state.id == next)
        return;
    state.id = next;
    state.zone = next.isEmpty() ? QTimeZone(QTimeZone::LocalTime) : QTimeZone(next.toUtf8());
    if (DisplayTimeZone *object = notifier())
        Q_EMIT object->changed();
}

QTimeZone DisplayTimeZone::current()
{
    return chosen().zone;
}

QDateTime DisplayTimeZone::toDisplay(const QDateTime &instant)
{
    return instant.isValid() ? instant.toTimeZone(current()) : QDateTime();
}

QDateTime DisplayTimeZone::now()
{
    return QDateTime::currentDateTimeUtc().toTimeZone(current());
}

QDate DisplayTimeZone::today()
{
    return now().date();
}

QDate DisplayTimeZone::dateOf(const QDateTime &instant)
{
    return toDisplay(instant).date();
}

QDateTime DisplayTimeZone::fromWallClock(const QDate &day, const QTime &clock)
{
    if (!day.isValid() || !clock.isValid())
        return {};
    return QDateTime(day, clock, current());
}

QString DisplayTimeZone::offsetText(const QTimeZone &zone, const QDateTime &at)
{
    const QDateTime instant = at.isValid() ? at : QDateTime::currentDateTimeUtc();
    const int seconds = instant.toTimeZone(zone).offsetFromUtc();
    const int minutes = std::abs(seconds) / 60;
    return QStringLiteral("%1%2:%3")
        .arg(seconds < 0 ? QLatin1Char('-') : QLatin1Char('+'))
        .arg(minutes / 60, 2, 10, QLatin1Char('0'))
        .arg(minutes % 60, 2, 10, QLatin1Char('0'));
}

QString DisplayTimeZone::offsetLabel(const QTimeZone &zone, const QDateTime &at)
{
    return QStringLiteral("UTC") + offsetText(zone, at);
}

QStringList DisplayTimeZone::curatedIds()
{
    const QDateTime now = QDateTime::currentDateTimeUtc();
    QList<std::pair<int, QString>> zones;
    for (const char *raw : kCuratedIds) {
        const QString zoneId = QString::fromLatin1(raw);
        if (!offeredZone(zoneId))
            continue;
        zones.append({now.toTimeZone(QTimeZone(zoneId.toUtf8())).offsetFromUtc(), zoneId});
    }
    std::ranges::stable_sort(zones, {}, &std::pair<int, QString>::first);
    QStringList ids;
    ids.reserve(zones.size());
    for (const auto &zone : std::as_const(zones))
        ids.append(zone.second);
    return ids;
}

} // namespace mr
