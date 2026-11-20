#pragma once

// ---------------------------------------------------------------------------
// Tells the Collector the 显示时区 (SetDisplayTimeZone, contracts/ipc-v1.schema.json).
//
// The Collector cuts the days it computes itself - the days of a month that
// have records, the history_date anchor of a record known only by its day, the
// clock times in its sentences - in the zone it holds. It holds it in memory
// only, so this sends DisplayTimeZone::id() every time the backend connects and
// every time DisplayTimeZone::notifier() announces another zone; "" (跟随系统)
// travels as time_zone: null.
//
// applied() asks for anything cached from the Collector's days to be read
// again. It is emitted when the Collector acknowledges a zone the player chose
// while connected (or one whose acknowledgement a dropped connection lost), and
// on connecting when the zone is one of the player's own: answers that raced
// ahead of the acknowledgement may then be the computer's zone's. Following
// the system on a fresh connection changes nothing and emits nothing.
//
// A Collector older than the message refuses it (ERR_BAD_REQUEST with
// ipc.unknown_message_type, ERR_UNKNOWN_MESSAGE from some builds): that is
// logged at debug level and otherwise ignored - its days stay the computer's.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QString>

namespace mr {

class BackendReply;
class IBackend;

class DisplayTimeZoneSync final : public QObject
{
    Q_OBJECT

public:
    explicit DisplayTimeZoneSync(IBackend *backend, QObject *parent = nullptr);

    /// The zone the Collector last acknowledged on this connection ("" for the
    /// computer's), empty also before any acknowledgement; see acknowledged().
    QString acknowledgedZone() const { return m_acknowledgedZone; }
    /// True once the Collector has acknowledged a zone on this connection.
    bool acknowledged() const { return m_acknowledged; }

Q_SIGNALS:
    /// The Collector now cuts days in the 显示时区: reload what it computed.
    void applied();

private:
    void onConnectionChanged();
    void send(bool afterChange);
    void finish(BackendReply *reply, quint64 generation, bool afterChange, bool ok);

    IBackend *m_backend = nullptr;
    quint64 m_generation = 0;
    // A change sent but not acknowledged: the next connection reports it as one.
    bool m_changeUnacknowledged = false;
    bool m_acknowledged = false;
    QString m_acknowledgedZone;
};

} // namespace mr
