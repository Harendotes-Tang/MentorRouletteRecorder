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
// A current Collector's failed answer (a timeout behind a long request, an
// internal error) is tried again up to kAttempts times, kRetryMs apart; an
// acknowledgement after a retry emits applied() as the first answer would have.
// A refusal of the zone (ERR_BAD_REQUEST), or a last failed attempt, sets
// refused() until a later zone is acknowledged, so 设置 can say that the
// background service still cuts days in the computer's zone; unconfirmed()
// tells the second case from the first (a timed-out request may have been
// applied), and either is sent again on the next connection.
//
// While 显示时区 follows the computer, the computer's own zone can change under
// a running app: each time the application becomes active the computer's zone
// id is compared with the one in force at the last send, and a different one
// is sent again as a change (re-review RA-5).
// ---------------------------------------------------------------------------

#include <QByteArray>
#include <QObject>
#include <QString>

#include <functional>
#include <utility>

namespace mr {

class BackendReply;
class IBackend;

/// True when \a reply is a Collector's refusal of a message type it does not
/// know: the shipping Collector says ERR_BAD_REQUEST with
/// ipc.unknown_message_type, other builds ERR_UNKNOWN_MESSAGE. False for null.
bool isUnknownMessageReply(const BackendReply *reply);

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
    /// True when a current Collector refused the 显示时区, or never answered it
    /// after every attempt: its own days are still the computer's.
    bool refused() const { return m_refused; }
    /// True when refused() comes from attempts that all went unanswered rather
    /// than from a refusal: whether the zone was applied is not known.
    bool unconfirmed() const { return m_unconfirmed; }

    /// While following the computer, sends the zone again when the computer's
    /// zone id differs from the one in force at the last send. Called whenever
    /// the application becomes active.
    void checkSystemZone();
    /// Test seam: where the computer's zone id is read (QTimeZone::systemTimeZoneId
    /// unless set). Set it before the backend connects.
    void setSystemZoneIdSource(std::function<QByteArray()> source) { m_systemZoneId = std::move(source); }

    static constexpr int kAttempts = 3;
    static constexpr int kRetryMs = 3000;

Q_SIGNALS:
    /// The Collector now cuts days in the 显示时区: reload what it computed.
    void applied();
    /// refused() or unconfirmed() changed.
    void refusedChanged();

private:
    void onConnectionChanged();
    void send(bool afterChange, int attempt = 0);
    void finish(BackendReply *reply, quint64 generation, bool afterChange, bool ok, int attempt);
    void setRefused(bool refused, bool unconfirmed = false);

    IBackend *m_backend = nullptr;
    bool m_refused = false;
    bool m_unconfirmed = false;
    std::function<QByteArray()> m_systemZoneId;
    // The computer's zone id when the zone was last sent.
    QByteArray m_systemZoneSent;
    quint64 m_generation = 0;
    // A change sent but not acknowledged: the next connection reports it as one.
    bool m_changeUnacknowledged = false;
    bool m_acknowledged = false;
    QString m_acknowledgedZone;
};

} // namespace mr
