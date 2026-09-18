#pragma once

// ---------------------------------------------------------------------------
// Named Pipe *client*.
//
// This class opens a QLocalSocket to \\.\pipe\MentorRecorder.<sid hash>.v1.
// It never creates a server, never binds a port and never touches the network
// stack; see docs/privacy-boundary.md and the NET-005 / NET-006 rules in
// tools/static-boundary-check/rules.json.
//
// Reconnect policy: the client reconnects with an exponential backoff, but it
// does NOT replay pending requests. Mutating requests are idempotent by
// request_id (contracts/error-codes.md), so a resend is only ever done by the
// caller, deliberately reusing the same id.
// ---------------------------------------------------------------------------

#include <QByteArray>
#include <QHash>
#include <QJsonObject>
#include <QLocalSocket>
#include <QObject>
#include <QPointer>
#include <QTimer>

namespace mr {

class BackendReply;

class IpcClient : public QObject
{
    Q_OBJECT

public:
    explicit IpcClient(QObject *parent = nullptr);
    ~IpcClient() override;

    /// The full \\.\pipe\... name this client connects to.
    QString serverName() const { return m_serverName; }
    void setServerName(const QString &serverName);

    bool isConnected() const;
    QString lastError() const { return m_lastError; }

    /// Per-request timeout in milliseconds, for everything that is not a
    /// long-running export or backup.
    int requestTimeoutMs() const { return m_requestTimeoutMs; }
    void setRequestTimeoutMs(int milliseconds) { m_requestTimeoutMs = milliseconds; }

    /// Deadline for an export, a backup or a diagnostics report.
    ///
    /// These read or copy the whole database, which takes longer than any
    /// status poll. Under the ordinary deadline a Collector that is still
    /// working is reported as a failure, and its eventual answer is then
    /// discarded as a response to an unknown request_id.
    static constexpr int kLongRequestTimeoutMs = 120000;
    /// Deadline every other message type gets.
    static constexpr int kDefaultRequestTimeoutMs = 8000;
    /// Deadline for SynthesizeSpeech. The Collector answers it in the
    /// background after up to 8 s in its queue and 8 s on the wire, and the
    /// contract asks a client to allow at least 20 s
    /// ($defs/SynthesizeSpeechRequest). TtsService gives up a little earlier
    /// on its own and speaks the sentence with the local voice.
    static constexpr int kSpeechRequestTimeoutMs = 25000;
    /// Deadline for CheckUpdateNow. The Collector answers it only after its
    /// own HTTP GET has finished or given up, and that budget is 15 s
    /// (docs/privacy-boundary.md §8.4). Under the ordinary deadline the
    /// button would report a failure the check had not yet reached.
    static constexpr int kUpdateCheckRequestTimeoutMs = 20000;

    /// Deadline for one message type. \a defaultMs is what a message type
    /// that is not long-running gets, so a caller can shorten every ordinary
    /// request without also shortening the exports.
    static int timeoutForMessageType(const QString &messageType, int defaultMs);

    void start();
    void stop();

    /// Send one envelope and correlate the response by request_id.
    /// Takes ownership of \a reply's lifetime bookkeeping only; \a reply
    /// deletes itself once it fires.
    /// \a timeoutMs overrides the per-message-type deadline; -1 (the default)
    /// selects it from \ref timeoutForMessageType.
    void send(BackendReply *reply, const QString &messageType,
              const QJsonObject &payload, int timeoutMs = -1);

Q_SIGNALS:
    void connectionChanged();
    void eventReceived(const QJsonObject &liveEvent);

private:
    void onConnected();
    void onDisconnected();
    void onError(QLocalSocket::LocalSocketError error);
    void onReadyRead();
    void scheduleReconnect();
    void dispatch(const QJsonObject &envelope);
    void failAllPending(const QString &code, const QString &message);
    void abortStream(const QString &reason);

    struct Pending {
        QPointer<BackendReply> reply;
        qint64 deadline = 0;
    };

    QLocalSocket *m_socket = nullptr;
    QTimer m_reconnectTimer;
    QTimer m_timeoutTimer;
    QByteArray m_buffer;
    QHash<QString, Pending> m_pending;
    QString m_serverName;
    QString m_lastError;
    int m_backoffMs = 500;
    int m_requestTimeoutMs = kDefaultRequestTimeoutMs;
    bool m_running = false;
};

} // namespace mr
