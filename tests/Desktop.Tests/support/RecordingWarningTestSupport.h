#pragma once

#include "IBackend.h"

#include <QPointer>

namespace {

class WarningBackend : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return connected; }
    bool connected = true;
    bool statusFails = false;
    /// Leaves GetCaptureSettings unanswered, as while a poll is on its way.
    bool holdSettings = false;
    QPointer<mr::BackendReply> heldSettings;
    QJsonObject capture{{"ffxiv_running", true}, {"ffxiv_process_id", 123},
                        {"profile_status", "VERIFIED"}, {"state", "RUNNING"},
                        {"silent_reason", "NONE"}};

    void reconnect(bool value)
    {
        connected = value;
        Q_EMIT connectionChanged();
    }

    mr::BackendReply *request(const QString &type, const QJsonObject & = {}) override
    {
        auto *reply = new mr::BackendReply(QString::number(++requests), type, this);
        if (type == QLatin1String("GetCaptureSettings") && holdSettings) {
            heldSettings = reply;
        } else if (type == QLatin1String("GetCaptureSettings")) {
            reply->succeed({{"follow_game", true}});
        } else if (type == QLatin1String("GetCaptureValidationStatus")) {
            reply->succeed({{"active", false}, {"state", "IDLE"}});
        } else if (type == QLatin1String("GetStatus")) {
            if (statusFails)
                reply->fail(QStringLiteral("ERR_TEST"), QStringLiteral("unavailable"));
            else
                reply->succeed({{"capture", capture}, {"game", QJsonObject{{"install_path_readable", true}}}});
        } else {
            reply->succeed({});
        }
        return reply;
    }

    void recover()
    {
        capture["state"] = "RUNNING";
        capture["silent_reason"] = "NONE";
        capture["last_error_code"] = "NONE";
        capture["messages_decoded"] = 1;
        capture["ipc_decoded"] = 1;
        capture.remove("calibration");
        capture.remove("path_recovery");
        capture["profile_status"] = "VERIFIED";
    }

    void startRecovery()
    {
        capture["silent_reason"] = "MIDSTREAM";
        capture["messages_decoded"] = 0;
        capture["ipc_decoded"] = 0;
        capture["path_recovery"] = QJsonObject{{"state", "RECOVERING"}, {"attempts", 0}, {"retry_after_ms", 0}};
    }

private:
    int requests = 0;
};

} // namespace
