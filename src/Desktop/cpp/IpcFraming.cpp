#include "IpcFraming.h"

#include <QJsonDocument>
#include <QUuid>

namespace mr::ipc {

QByteArray encodeFrame(const QJsonObject &envelope)
{
    const QByteArray body =
        QJsonDocument(envelope).toJson(QJsonDocument::Compact);
    if (body.size() > kMaxFrameBytes)
        return {};

    const quint32 length = static_cast<quint32>(body.size());
    QByteArray frame;
    frame.reserve(4 + body.size());
    frame.append(static_cast<char>(length & 0xff));
    frame.append(static_cast<char>((length >> 8) & 0xff));
    frame.append(static_cast<char>((length >> 16) & 0xff));
    frame.append(static_cast<char>((length >> 24) & 0xff));
    frame.append(body);
    return frame;
}

FrameStatus takeFrame(QByteArray &buffer, QJsonObject &out)
{
    if (buffer.size() < 4)
        return FrameStatus::Incomplete;

    const auto *raw = reinterpret_cast<const quint8 *>(buffer.constData());
    const quint32 length = static_cast<quint32>(raw[0])
                         | (static_cast<quint32>(raw[1]) << 8)
                         | (static_cast<quint32>(raw[2]) << 16)
                         | (static_cast<quint32>(raw[3]) << 24);

    if (static_cast<qint64>(length) > kMaxFrameBytes)
        return FrameStatus::Oversized;

    // Widen before adding: `4 + length` in quint32 could wrap for a length near
    // UINT32_MAX. The Oversized check above already rules that out, but the sum
    // should not depend on it.
    const qsizetype frameBytes = 4 + static_cast<qsizetype>(length);
    if (buffer.size() < frameBytes)
        return FrameStatus::Incomplete;

    const QByteArray body = buffer.mid(4, static_cast<qsizetype>(length));

    QJsonParseError error{};
    const QJsonDocument doc = QJsonDocument::fromJson(body, &error);
    if (error.error != QJsonParseError::NoError || !doc.isObject())
        return FrameStatus::BadJson;

    out = doc.object();
    buffer.remove(0, frameBytes);
    return FrameStatus::Ok;
}

QJsonObject makeRequest(const QString &requestId,
                        const QString &messageType,
                        const QJsonObject &payload)
{
    QJsonObject envelope;
    envelope.insert(QStringLiteral("protocol_version"), kProtocolVersion);
    envelope.insert(QStringLiteral("request_id"), requestId);
    envelope.insert(QStringLiteral("message_type"), messageType);
    envelope.insert(QStringLiteral("payload"), payload);
    return envelope;
}

QString newRequestId()
{
    return QUuid::createUuid().toString(QUuid::WithoutBraces);
}

QString utcTimestamp(const QDateTime &moment)
{
    return moment.toUTC().toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzz")) +
           QStringLiteral("Z");
}

EnvelopeView readEnvelope(const QJsonObject &envelope)
{
    EnvelopeView view;
    view.protocolVersion = envelope.value(QStringLiteral("protocol_version")).toInt(-1);
    view.requestId = envelope.value(QStringLiteral("request_id")).toString();
    view.messageType = envelope.value(QStringLiteral("message_type")).toString();
    view.payload = envelope.value(QStringLiteral("payload")).toObject();

    if (view.messageType == QLatin1String("Event")) {
        view.kind = EnvelopeKind::Event;
        view.ok = true;
        return view;
    }

    // "ok" is required by the contract; defaulting a missing one to true keeps a
    // peer that predates the field readable instead of turning every answer into
    // an error with no code.
    const bool isErrorType = view.messageType == QLatin1String("Error");
    view.ok = envelope.value(QStringLiteral("ok")).toBool(!isErrorType);
    if (view.ok)
        return view;

    // Both copies say the same thing; "error" is the authoritative one.
    const QJsonValue error = envelope.value(QStringLiteral("error"));
    const QJsonObject source = error.isObject() ? error.toObject() : view.payload;
    view.errorCode = source.value(QStringLiteral("code"))
                         .toString(QStringLiteral("ERR_INTERNAL"));
    if (view.errorCode.isEmpty())
        view.errorCode = QStringLiteral("ERR_INTERNAL");
    view.errorMessage = source.value(QStringLiteral("message")).toString();
    view.errorDetails = source.value(QStringLiteral("details")).toObject();
    view.payload = {};
    return view;
}

} // namespace mr::ipc
