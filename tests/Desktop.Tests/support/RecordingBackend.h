#pragma once

#include "IBackend.h"

#include <QJsonObject>
#include <QList>
#include <QPointer>
#include <QString>

#include <optional>

namespace mrtest {

/// A backend that records every request - its type, payload, request id and reply - and
/// leaves the reply for the test to answer, or answers it at once with immediateAnswer when
/// that is set. Replies are numbered in the order the requests arrived.
class RecordingBackend : public mr::IBackend
{
public:
    struct Call {
        QString type;
        QJsonObject payload;
        QString requestId;
        QPointer<mr::BackendReply> reply;
    };

    QList<Call> calls;
    bool connected = true;
    std::optional<QJsonObject> immediateAnswer;

    QString backendName() const override { return QStringLiteral("recording"); }
    bool isConnected() const override { return connected; }
    /// Changes isConnected() and tells the listeners, as a reconnecting transport does.
    void setConnected(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({.type = type, .payload = payload, .requestId = {}, .reply = reply});
        if (immediateAnswer)
            reply->succeed(*immediateAnswer);
        return reply;
    }

    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload, const QString &id) override
    {
        auto *reply = request(type, payload);
        calls.last().requestId = id;
        return reply;
    }

    /// The latest request; there must have been one.
    const Call &last() const { return calls.last(); }
};

} // namespace mrtest
