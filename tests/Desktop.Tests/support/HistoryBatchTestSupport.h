#pragma once

#include "IBackend.h"
#include "IpcClient.h"

#include <QJsonArray>
#include <QPointer>

#include <ranges>

namespace {

const QString &firstId()
{
    static const QString value = QStringLiteral("00000000-0000-0000-0000-000000000001");
    return value;
}
inline const QString &secondId()
{
    static const QString value = QStringLiteral("00000000-0000-0000-0000-000000000002");
    return value;
}
QVariantMap row(const QString &id, int revision, bool deleted = false)
{
    return {{QStringLiteral("run_id"), id}, {QStringLiteral("revision"), revision},
            {QStringLiteral("soft_deleted"), deleted}, {QStringLiteral("duty_name"), QStringLiteral("测试副本")}};
}
class ControlledBackend final : public mr::IBackend {
public:
    struct Call { QString type; QJsonObject payload; QString id; QPointer<mr::BackendReply> reply; };
    QList<Call> calls;
    mr::IpcClient *transport = nullptr;
    QString backendName() const override { return QStringLiteral("controlled"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &type, const QJsonObject &payload = {}) override {
        auto *reply = new mr::BackendReply(QString::number(calls.size()), type, this);
        calls.append({.type=type, .payload=payload, .id={}, .reply=reply});
        return reply;
    }
    mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload, const QString &id) override {
        auto *reply = transport ? new mr::BackendReply(id, type, this) : request(type, payload);
        if (transport) calls.append({.type=type, .payload=payload, .id=id, .reply=reply});
        else calls.last().id = id;
        if (transport) transport->send(reply, type, payload);
        return reply;
    }
    const Call &last(const QString &type) const {
        for (const auto & call : std::ranges::reverse_view(calls)) if (call.type == type) return call;
        static const Call missing;
        return missing;
    }
    void completePage(const QVariantMap &run, int page = 1, int total = 2) const {
        last(QStringLiteral("QueryRuns")).reply->succeed({
            {QStringLiteral("items"), QJsonArray{QJsonObject::fromVariantMap(run)}},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), total}}}});
    }
};

} // namespace
