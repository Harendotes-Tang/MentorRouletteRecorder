#include "DisplayTimeZoneSync.h"

#include "DisplayTimeZone.h"
#include "IBackend.h"

#include <QDateTime>
#include <QJsonObject>
#include <QJsonValue>
#include <QLoggingCategory>
#include <QPointer>

namespace {
Q_LOGGING_CATEGORY(lcDisplayZone, "mr.timezone")

/// A Collector that predates SetDisplayTimeZone: the shipping one says
/// ERR_BAD_REQUEST with ipc.unknown_message_type, other builds ERR_UNKNOWN_MESSAGE.
bool unknownMessage(const mr::BackendReply *reply)
{
    if (reply->errorCode() == QLatin1String("ERR_UNKNOWN_MESSAGE"))
        return true;
    return reply->errorCode() == QLatin1String("ERR_BAD_REQUEST")
           && reply->errorRef().toMap().value(QStringLiteral("key")).toString()
                  == QLatin1String("ipc.unknown_message_type");
}
} // namespace

namespace mr {

DisplayTimeZoneSync::DisplayTimeZoneSync(IBackend *backend, QObject *parent)
    : QObject(parent), m_backend(backend)
{
    if (!m_backend)
        return;
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

void DisplayTimeZoneSync::send(bool afterChange)
{
    const quint64 generation = ++m_generation;
    BackendReply *reply = m_backend->setDisplayTimeZone(DisplayTimeZone::id());
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, guard, generation, afterChange](bool ok, const QVariantMap &,
                                                                  const QString &, const QString &) {
        if (guard)
            finish(guard, generation, afterChange, ok);
    });
}

void DisplayTimeZoneSync::finish(BackendReply *reply, quint64 generation, bool afterChange, bool ok)
{
    // A newer request, or another connection, has the last word.
    if (generation != m_generation)
        return;
    if (!ok) {
        if (unknownMessage(reply)) {
            qCDebug(lcDisplayZone) << "the Collector does not know SetDisplayTimeZone; its days stay the computer's";
        } else {
            qCWarning(lcDisplayZone) << "the Collector refused the display time zone" << DisplayTimeZone::id()
                                     << reply->errorCode() << reply->errorMessage();
        }
        return;
    }

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

} // namespace mr
