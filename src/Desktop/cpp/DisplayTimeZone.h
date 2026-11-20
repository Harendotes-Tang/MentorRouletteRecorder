#pragma once

// ---------------------------------------------------------------------------
// 显示时区 (设置 › 通用): the one zone every record time is shown and typed in.
//
// Times travel and are stored as UTC (every *_at_utc field). Wherever the
// Desktop turns one into a wall clock - a label, a date field, "today", the
// first and last instant of a day - it asks this class for the zone, never the
// operating system: a player whose PC is set to another zone than the one he
// lives in can pick his own (AppSettings::displayTimeZone), and nothing stored
// changes when he does.
//
// Following the system (the default, an empty id) is Qt's LocalTime zone, so
// the result is exactly what QDateTime::toLocalTime() gave before the setting
// existed. What deliberately stays on the system clock: file names of exports
// and backups, and the Collector's own work (trend buckets are cut in UTC,
// exports are UTC, the calendar of days with records is the Collector's).
//
// QML never sees this class: Runtime.fmt (Formatters) formats through it and
// QmlRuntime re-announces `fmt` on changed(), so every binding that formats a
// time re-renders without a restart. C++ controllers that keep formatted text
// connect to changed() themselves.
// ---------------------------------------------------------------------------

#include <QDate>
#include <QDateTime>
#include <QObject>
#include <QString>
#include <QStringList>
#include <QTime>
#include <QTimeZone>

namespace mr {

class DisplayTimeZone final : public QObject
{
    Q_OBJECT

public:
    /// Public for the process-wide notifier only; use the static functions.
    explicit DisplayTimeZone(QObject *parent = nullptr);

    /// Emits changed() after every set() that changes the zone in force.
    static DisplayTimeZone *notifier();

    /// The chosen IANA id, "" while the system zone is followed.
    static QString id();
    /// Choose the zone: an IANA id Qt knows, or "" for the system. An id Qt does
    /// not know is taken as "".
    static void set(const QString &id);
    /// \a id trimmed when Qt knows it, "" otherwise (and for "").
    static QString normalized(const QString &id);
    /// True while no zone of its own is chosen.
    static bool followsSystem() { return id().isEmpty(); }

    /// The zone in force: the chosen one, or the system's (QTimeZone::LocalTime).
    static QTimeZone current();
    /// \a instant as a wall clock in the zone in force; invalid stays invalid.
    static QDateTime toDisplay(const QDateTime &instant);
    /// Now, as a wall clock in the zone in force.
    static QDateTime now();
    /// Today's date in the zone in force.
    static QDate today();
    /// The date \a instant falls on in the zone in force.
    static QDate dateOf(const QDateTime &instant);
    /// The instant a typed date and time of day name in the zone in force.
    static QDateTime fromWallClock(const QDate &day, const QTime &clock);

    /// "+08:00" / "-04:00" / "+05:45": \a zone's offset at \a at (now when invalid).
    static QString offsetText(const QTimeZone &zone, const QDateTime &at = {});
    /// "UTC+08:00": offsetText() as the settings and the hints show it.
    static QString offsetLabel(const QTimeZone &zone, const QDateTime &at = {});

    /// The zones the settings offer, available on this machine, ordered by
    /// their offset now (ties keep the list's own order). The one list.
    static QStringList curatedIds();

Q_SIGNALS:
    void changed();
};

} // namespace mr
