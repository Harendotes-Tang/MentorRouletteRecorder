#include "IpcClient.h"

#include "IBackend.h"
#include "IpcFraming.h"
#include "PipeName.h"

#include <QDateTime>
#include <QJsonDocument>
#include <QLoggingCategory>
#include <QSet>

namespace {
Q_LOGGING_CATEGORY(lcIpc, "mr.ipc")

constexpr int kMinBackoffMs = 500;
constexpr int kMaxBackoffMs = 15000;

/// The message types that read or copy the whole database, or write one.
///
/// Nothing else on the wire is unbounded: every query is paged and every
/// status poll answers from memory. These walk the records - or, for
/// ConfirmCalibration, write a profile and reload a directory - so their
/// duration grows with the user's history and cannot share a deadline chosen
/// for a status poll.
bool isLongRunning(const QString &messageType)
{
    static const QSet<QString> kLongRunning{
        QStringLiteral("BackupDatabase"),
        QStringLiteral("ExportCsv"),
        QStringLiteral("ExportJson"),
        QStringLiteral("ExportDiagnosticsReport"),
        QStringLiteral("ExportCandidateEvidence"),
        // Not queries: an accepted calibration validates and atomically writes
        // a profile file, then re-reads the whole profile directory, and both
        // calibration verdicts wait on the same coordinator as that write.
        QStringLiteral("ConfirmCalibration"),
        QStringLiteral("DiscardCalibration"),
    };
    return kLongRunning.contains(messageType);
}

/// Set MR_IPC_TRACE=1 to have every outgoing request written to stderr.
///
/// Requests only, never responses: a development aid for checking that a page
/// really sends the filter it claims to. Off unless the variable is set.
bool traceEnabled()
{
    static const bool enabled = qEnvironmentVariableIntValue("MR_IPC_TRACE") != 0;
    return enabled;
}
} // namespace

namespace mr {

IpcClient::IpcClient(QObject *parent)
    : QObject(parent), m_serverName(ipc::currentUserServerName())
{
    m_socket = new QLocalSocket(this);
    connect(m_socket, &QLocalSocket::connected, this, &IpcClient::onConnected);
    connect(m_socket, &QLocalSocket::disconnected, this, &IpcClient::onDisconnected);
    connect(m_socket, &QLocalSocket::errorOccurred, this, &IpcClient::onError);
    connect(m_socket, &QLocalSocket::readyRead, this, &IpcClient::onReadyRead);

    m_reconnectTimer.setSingleShot(true);
    connect(&m_reconnectTimer, &QTimer::timeout, this, [this] {
        if (m_running && m_socket->state() == QLocalSocket::UnconnectedState)
            m_socket->connectToServer(m_serverName);
    });

    m_timeoutTimer.setInterval(1000);
    connect(&m_timeoutTimer, &QTimer::timeout, this, [this] {
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        const QStringList ids = m_pending.keys();
        for (const QString &id : ids) {
            const Pending pending = m_pending.value(id);
            if (pending.deadline > now)
                continue;
            m_pending.remove(id);
            if (pending.reply) {
                pending.reply->fail(
                    QStringLiteral("ERR_INTERNAL"),
                    QString::fromUtf8("Collector 未在超时时间内响应。"));
            }
        }
        if (m_pending.isEmpty())
            m_timeoutTimer.stop();
    });
}

IpcClient::~IpcClient()
{
    stop();
}

int IpcClient::timeoutForMessageType(const QString &messageType, int defaultMs)
{
    if (messageType == QLatin1String("SynthesizeSpeech"))
        return kSpeechRequestTimeoutMs;
    if (messageType == QLatin1String("CheckUpdateNow"))
        return kUpdateCheckRequestTimeoutMs;
    return isLongRunning(messageType) ? kLongRequestTimeoutMs : defaultMs;
}

void IpcClient::setServerName(const QString &serverName)
{
    if (m_serverName == serverName)
        return;
    m_serverName = serverName;
    if (m_running) {
        stop();
        start();
    }
}

bool IpcClient::isConnected() const
{
    return m_socket && m_socket->state() == QLocalSocket::ConnectedState;
}

void IpcClient::start()
{
    if (m_running)
        return;
    m_running = true;
    m_backoffMs = kMinBackoffMs;

    if (m_serverName.isEmpty()) {
        m_lastError = QString::fromUtf8("无法确定当前用户 SID，管道名不可用。");
        Q_EMIT connectionChanged();
        return;
    }
    m_socket->connectToServer(m_serverName);
}

void IpcClient::stop()
{
    m_running = false;
    m_reconnectTimer.stop();
    m_timeoutTimer.stop();
    failAllPending(QStringLiteral("ERR_INTERNAL"),
                   QString::fromUtf8("与 Collector 的连接已关闭。"));
    if (m_socket && m_socket->state() != QLocalSocket::UnconnectedState)
        m_socket->abort();
    m_buffer.clear();
}

void IpcClient::onConnected()
{
    m_backoffMs = kMinBackoffMs;
    m_lastError.clear();
    m_buffer.clear();
    qCInfo(lcIpc) << "connected to" << m_serverName;
    Q_EMIT connectionChanged();
}

void IpcClient::onDisconnected()
{
    // Pending requests are failed, never silently replayed: a mutation may
    // already have been applied on the other side.
    failAllPending(QStringLiteral("ERR_INTERNAL"),
                   QString::fromUtf8("与 Collector 的连接中断，请重试。"));
    m_buffer.clear();
    Q_EMIT connectionChanged();
    scheduleReconnect();
}

void IpcClient::onError(QLocalSocket::LocalSocketError error)
{
    Q_UNUSED(error)
    m_lastError = m_socket->errorString();
    failAllPending(QStringLiteral("ERR_INTERNAL"), m_lastError);
    Q_EMIT connectionChanged();
    scheduleReconnect();
}

void IpcClient::scheduleReconnect()
{
    if (!m_running || m_reconnectTimer.isActive())
        return;
    m_reconnectTimer.start(m_backoffMs);
    m_backoffMs = qMin(m_backoffMs * 2, kMaxBackoffMs);
}

void IpcClient::onReadyRead()
{
    m_buffer.append(m_socket->readAll());

    for (;;) {
        QJsonObject envelope;
        const ipc::FrameStatus status = ipc::takeFrame(m_buffer, envelope);
        if (status == ipc::FrameStatus::Incomplete)
            return;
        if (status == ipc::FrameStatus::Oversized) {
            abortStream(QString::fromUtf8("收到超过 4 MiB 的帧，连接已断开。"));
            return;
        }
        if (status == ipc::FrameStatus::BadJson) {
            abortStream(QString::fromUtf8("收到无法解析的帧，连接已断开。"));
            return;
        }
        dispatch(envelope);
    }
}

void IpcClient::abortStream(const QString &reason)
{
    qCWarning(lcIpc) << reason;
    m_lastError = reason;
    m_buffer.clear();
    failAllPending(QStringLiteral("ERR_PROTOCOL_VERSION"), reason);
    m_socket->abort();
    Q_EMIT connectionChanged();
    scheduleReconnect();
}

void IpcClient::dispatch(const QJsonObject &envelope)
{
    // One decoder for both branches ("ok" and message_type == "Error") and for
    // both copies of the error object, so the two cannot drift apart. See
    // ipc::readEnvelope in IpcFraming.cpp.
    const ipc::EnvelopeView view = ipc::readEnvelope(envelope);

    if (view.protocolVersion != ipc::kProtocolVersion) {
        abortStream(QString::fromUtf8("协议版本不匹配，需要重新安装以使两端版本一致。"));
        return;
    }

    if (view.kind == ipc::EnvelopeKind::Event) {
        Q_EMIT eventReceived(view.payload);
        return;
    }

    const auto it = m_pending.constFind(view.requestId);
    if (it == m_pending.constEnd()) {
        qCDebug(lcIpc) << "response for unknown request_id" << view.requestId;
        return;
    }
    const Pending pending = *it;
    m_pending.remove(view.requestId);
    if (m_pending.isEmpty())
        m_timeoutTimer.stop();

    if (!pending.reply)
        return;

    if (!view.ok) {
        pending.reply->fail(view.errorCode, view.errorMessage, view.errorDetails);
        return;
    }
    pending.reply->succeed(view.payload);
}

void IpcClient::failAllPending(const QString &code, const QString &message)
{
    const auto pending = m_pending;
    m_pending.clear();
    m_timeoutTimer.stop();
    for (const Pending &entry : pending) {
        if (entry.reply)
            entry.reply->fail(code, message);
    }
}

void IpcClient::send(BackendReply *reply, const QString &messageType,
                     const QJsonObject &payload, int timeoutMs)
{
    if (!isConnected()) {
        reply->fail(QStringLiteral("ERR_INTERNAL"),
                    QString::fromUtf8("Collector 未连接。"));
        scheduleReconnect();
        return;
    }

    if (traceEnabled()) {
        // The online-speech key is write-only and never logged: the trace shows
        // that one was sent, not what it was (docs/privacy-boundary.md §8.3).
        QJsonObject traced = payload;
        if (traced.contains(QStringLiteral("api_key"))) {
            traced.insert(QStringLiteral("api_key"),
                          traced.value(QStringLiteral("api_key")).toString().isEmpty()
                              ? QStringLiteral("<clear>")
                              : QStringLiteral("<redacted>"));
        }
        qCInfo(lcIpc).noquote()
            << "-->" << messageType
            << QString::fromUtf8(QJsonDocument(traced).toJson(QJsonDocument::Compact));
    }

    const QByteArray frame =
        ipc::encodeFrame(ipc::makeRequest(reply->requestId(), messageType, payload));
    if (frame.isEmpty()) {
        reply->fail(QStringLiteral("ERR_BAD_REQUEST"),
                    QString::fromUtf8("请求过大，超过 4 MiB 帧上限。"));
        return;
    }

    const int deadlineMs =
        timeoutMs > 0 ? timeoutMs : timeoutForMessageType(messageType, m_requestTimeoutMs);

    Pending pending;
    pending.reply = reply;
    pending.deadline = QDateTime::currentMSecsSinceEpoch() + deadlineMs;
    m_pending.insert(reply->requestId(), pending);
    if (!m_timeoutTimer.isActive())
        m_timeoutTimer.start();

    // A refused or partial write means the request never reached the Collector;
    // unchecked, it would wait out its whole deadline and then blame the
    // Collector for not answering something it never received.
    if (m_socket->write(frame) != frame.size()) {
        m_pending.remove(reply->requestId());
        if (m_pending.isEmpty())
            m_timeoutTimer.stop();
        const QString detail = m_socket->errorString();
        qCWarning(lcIpc) << "short write for" << messageType << detail;
        reply->fail(QStringLiteral("ERR_INTERNAL"),
                    detail.isEmpty()
                        ? QString::fromUtf8("请求没能完整发给 Collector，请重试。")
                        : QString::fromUtf8("请求没能完整发给 Collector，请重试。%1").arg(detail));
    }
}

} // namespace mr
