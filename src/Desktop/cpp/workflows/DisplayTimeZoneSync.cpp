#include "DisplayTimeZoneSync.h"

#include "DisplayTimeZone.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QGuiApplication>
#include <QJsonObject>
#include <QJsonValue>
#include <QLoggingCategory>
#include <QPointer>
#include <QTimeZone>
#include <QTimer>

namespace {
Q_LOGGING_CATEGORY(lcDisplayZone, "mr.timezone")
} // namespace

namespace mr {

bool isUnknownMessageReply(const BackendReply *reply)
{
    if (!reply)
        return false;
    if (reply->errorCode() == QLatin1String("ERR_UNKNOWN_MESSAGE"))
        return true;
    return reply->errorCode() == QLatin1String("ERR_BAD_REQUEST")
           && reply->errorRef().toMap().value(QStringLiteral("key")).toString()
                  == QLatin1String("ipc.unknown_message_type");
}

DisplayTimeZoneSync::DisplayTimeZoneSync(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend), m_systemZoneId([] { return QTimeZone::systemTimeZoneId(); })
{
    if (!m_backend)
        return;
    // Windows' zone can change while both processes run; the Collector, told
    // "the computer's", would keep the old one until the next connection (re-review RA-5).
    if (auto *app = qobject_cast<QGuiApplication *>(QCoreApplication::instance())) {
        connect(app, &QGuiApplication::applicationStateChanged, this, [this](Qt::ApplicationState state) {
            if (state == Qt::ApplicationActive)
                checkSystemZone();
        });
    }
    connect(m_backend, &IBackend::connectionChanged, this, &DisplayTimeZoneSync::onConnectionChanged);
    connect(m_backend, &QObject::destroyed, this, [this] {
        ++m_generation;
        m_backend = nullptr;
    });
    if (DisplayTimeZone *zone = DisplayTimeZone::notifier()) {
        connect(zone, &DisplayTimeZone::changed, this, [this] {
            m_changeUnacknowledged = true;
            if (m_backend && m_backend->isConnected())
                send(true);
        });
    }
    // A backend that is up before anyone listens (the offline mock) never
    // announces the connection: tell it now.
    if (m_backend->isConnected())
        send(false);
}

void DisplayTimeZoneSync::onConnectionChanged()
{
    // Whatever was in flight belongs to the previous connection.
    ++m_generation;
    m_acknowledged = false;
    m_acknowledgedZone.clear();
    if (m_backend && m_backend->isConnected())
        send(m_changeUnacknowledged);
}

void DisplayTimeZoneSync::checkSystemZone()
{
    if (!DisplayTimeZone::followsSystem() || !m_backend || !m_backend->isConnected())
        return;
    if (m_systemZoneId() == m_systemZoneSent)
        return;
    m_changeUnacknowledged = true;
    send(true);
}

void DisplayTimeZoneSync::send(bool afterChange, int attempt)
{
    const quint64 generation = ++m_generation;
    m_systemZoneSent = m_systemZoneId();
    BackendReply *reply = m_backend->setDisplayTimeZone(DisplayTimeZone::id());
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, guard, generation, afterChange, attempt](bool ok, const QVariantMap &,
                                                                           const QString &, const QString &) {
        if (guard)
            finish(guard, generation, afterChange, ok, attempt);
    });
}

void DisplayTimeZoneSync::finish(BackendReply *reply, quint64 generation, bool afterChange, bool ok, int attempt)
{
    // A newer request, or another connection, has the last word.
    if (generation != m_generation)
        return;
    if (!ok) {
        if (isUnknownMessageReply(reply)) {
            qCDebug(lcDisplayZone) << "the Collector does not know SetDisplayTimeZone; its days stay the computer's";
            return;
        }
        qCWarning(lcDisplayZone) << "the Collector refused the display time zone" << DisplayTimeZone::id()
                                 << reply->errorCode() << reply->errorMessage();
        // A request that never left waits for the reconnection, which sends it again.
        if (reply->neverSent())
            return;
        // A refusal of the zone itself will not change; anything else (a timeout
        // behind a long request, an internal error) is tried again a few times,
        // as the recorded-region read is (audit 2026-11-26, I1-1).
        const bool definite = reply->errorCode() == QLatin1String("ERR_BAD_REQUEST");
        if (!definite && attempt + 1 < kAttempts) {
            QTimer::singleShot(kRetryMs, this, [this, generation, afterChange, attempt] {
                if (generation == m_generation && m_backend && m_backend->isConnected())
                    send(afterChange, attempt + 1);
            });
            return;
        }
        // A refusal is known; attempts that all went unanswered may have been
        // applied, and the next connection sends the zone again (re-review RD-2).
        setRefused(true, !definite);
        return;
    }

    setRefused(false);
    const QJsonObject payload = reply->payload();
    m_acknowledged = true;
    m_acknowledgedZone = payload.value(QStringLiteral("time_zone")).toString();
    m_changeUnacknowledged = false;
    const QJsonValue offset = payload.value(QStringLiteral("utc_offset_minutes"));
    const int expected = QDateTime::currentDateTimeUtc().toTimeZone(DisplayTimeZone::current()).offsetFromUtc() / 60;
    if (offset.isDouble() && offset.toInt() != expected) {
        qCWarning(lcDisplayZone) << "the Collector's display time zone is" << offset.toInt()
                                 << "minutes from UTC, the Desktop's" << expected;
    }
    if (afterChange || !m_acknowledgedZone.isEmpty())
        Q_EMIT applied();
}

void DisplayTimeZoneSync::setRefused(bool refused, bool unconfirmed)
{
    unconfirmed = refused && unconfirmed;
    if (m_refused == refused && m_unconfirmed == unconfirmed)
        return;
    m_refused = refused;
    m_unconfirmed = unconfirmed;
    Q_EMIT refusedChanged();
}

} // namespace mr
