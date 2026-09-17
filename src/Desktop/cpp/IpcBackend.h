#pragma once

// ---------------------------------------------------------------------------
// IBackend implementation on top of the Named Pipe client.
//
// Without a running Collector every request fails with a clear
// "Collector 未连接" message.
// ---------------------------------------------------------------------------

#include "IBackend.h"
#include "IpcClient.h"

namespace mr {

class IpcBackend : public IBackend
{
    Q_OBJECT

public:
    /// serverName is the full local-socket name to connect to; empty means
    /// the current user's per-user pipe. Only a test harness has a reason to
    /// pass one: a Collector it launched on a throw-away database must be
    /// reached on its own pipe, never on the one the user's Desktop is using.
    explicit IpcBackend(QObject *parent = nullptr, const QString &serverName = {});

    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override;
    QString connectionDetail() const override;

    BackendReply *request(const QString &messageType,
                          const QJsonObject &payload = {}) override;

    IpcClient *client() { return m_client; }

    /// Fixed request_id stamped into every sample built by
    /// \ref buildRequestForTest, so the committed fixtures are byte-stable.
    static const char *testRequestId();

    /// Build - without sending - the exact request envelope the typed IBackend
    /// wrapper for \a messageType produces.
    ///
    /// This exists so tests/Fixtures/ipc-requests/*.json can be generated from
    /// the shipping payload assembly rather than hand-written next to it: the
    /// Collector-side contract test validates those samples against
    /// contracts/ipc-v1.schema.json, so a field this client renames or drops
    /// fails the build on both sides.
    ///
    /// \a args supplies the wrapper's parameters by contract field name
    /// (for example "run_id", "expected_revision", "changes", "reason").
    static QJsonObject buildRequestForTest(const QString &messageType,
                                           const QJsonObject &args = {});

private:
    IpcClient *m_client = nullptr;
};

} // namespace mr
