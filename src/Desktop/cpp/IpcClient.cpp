#include "IpcClient.h"

#include "IBackend.h"
#include "IpcFraming.h"
#include "PipeName.h"

#include <QDateTime>
#include <QJsonDocument>
#include <QLoggingCategory>
#include <QSet>

#ifdef Q_OS_WIN
// libstdc++ on MinGW defines NOMINMAX itself; see CollectorProcess.cpp.
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#  include <aclapi.h>
#  include <vector>
#endif

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
        // Imports parse bounded files and recheck/write a batch transaction;
        // both are deferred by the Collector, so a status-poll deadline is insufficient.
        QStringLiteral("PreviewRunImport"),
        QStringLiteral("CommitRunImport"),
        // PRAGMA integrity_check reads the whole database file, which is why
        // the Collector answers it off the connection's read loop
        // (MessageDispatcher.AsynchronousMessageTypes). Its duration grows with
        // the user's history exactly like an export's, so it belongs here
        // rather than getting a named constant: the two constants below exist
        // because their deadline comes from a budget the Collector documents (a
        // queue slot, one HTTP request), not from the size of the database
        // (2026-09-21 review finding 2).
        QStringLiteral("CheckDatabaseIntegrity"),
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
                    tr("后台服务暂时没有响应，操作结果还未确认。请稍后检查结果。"));
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
        m_lastError = [] { return tr("无法确认当前 Windows 用户，暂时不能连接后台服务。"); };
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
                   tr("与后台服务的连接已关闭。"));
    if (m_socket && m_socket->state() != QLocalSocket::UnconnectedState)
        m_socket->abort();
    m_buffer.clear();
}

bool IpcClient::pipeOwnedByCurrentUser(qintptr pipeHandle)
{
#ifdef Q_OS_WIN
    // The owner recorded in the pipe's own security descriptor, read through
    // the handle we already hold - the check .NET's PipeOptions.CurrentUserOnly
    // makes. The Collector's pipe is created with this user's token, so its
    // owner is the token's user, or its default owner (the Administrators group
    // when elevated). Another standard account can make neither the owner.
    PSID owner = nullptr;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (::GetSecurityInfo(reinterpret_cast<HANDLE>(pipeHandle), SE_KERNEL_OBJECT,
                          OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr,
                          &descriptor)
            != ERROR_SUCCESS
        || owner == nullptr) {
        if (descriptor)
            ::LocalFree(descriptor);
        return false;
    }

    bool owned = false;
    HANDLE token = nullptr;
    // Our own process token only; no other process is opened.
    if (::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) {
        for (const TOKEN_INFORMATION_CLASS kind : {TokenUser, TokenOwner}) {
            DWORD needed = 0;
            ::GetTokenInformation(token, kind, nullptr, 0, &needed);
            if (needed == 0)
                continue;
            std::vector<unsigned char> buffer(needed);
            if (!::GetTokenInformation(token, kind, buffer.data(), needed, &needed))
                continue;
            const PSID sid = kind == TokenUser
                                 ? reinterpret_cast<const TOKEN_USER *>(buffer.data())->User.Sid
                                 : reinterpret_cast<const TOKEN_OWNER *>(buffer.data())->Owner;
            if (sid && ::EqualSid(owner, sid)) {
                owned = true;
                break;
            }
        }
        ::CloseHandle(token);
    }
    ::LocalFree(descriptor);
    return owned;
#else
    Q_UNUSED(pipeHandle)
    return true;
#endif
}

void IpcClient::onConnected()
{
    const qintptr handle = m_socket->socketDescriptor();
    const bool owned = m_ownerCheck ? m_ownerCheck(handle) : pipeOwnedByCurrentUser(handle);
    if (!owned) {
        // Another account created the pipe first, under the name this user's
        // Collector would serve. Nothing - an online-speech key above all - may
        // be written to it (review OH-4).
        qCWarning(lcIpc) << "refused" << m_serverName << ": the pipe belongs to another account";
        m_buffer.clear();
        m_lastError = [] { return tr("后台服务属于另一个 Windows 用户，无法连接。请在当前用户下重新启动软件。"); };
        m_socket->abort();
        if (!m_reconnectTimer.isActive()) {
            // abort() reported no disconnect of its own.
            Q_EMIT connectionChanged();
            scheduleReconnect();
        }
        return;
    }

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
                   tr("与后台服务的连接已中断，暂时无法确认操作结果。连接恢复后，请先检查操作结果。"));
    m_buffer.clear();
    Q_EMIT connectionChanged();
    scheduleReconnect();
}

void IpcClient::onError(QLocalSocket::LocalSocketError error)
{
    Q_UNUSED(error)
    m_lastError = LiveText::verbatim(m_socket->errorString());
    failAllPending(QStringLiteral("ERR_INTERNAL"), m_lastError.text());
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
            abortStream([] { return tr("后台服务返回的数据量过大，连接已断开。请重启软件，并先检查上次操作的结果。"); });
            return;
        }
        if (status == ipc::FrameStatus::BadJson) {
            abortStream([] { return tr("后台服务返回的数据无法读取，连接已断开。请重启软件，并先检查上次操作的结果。"); });
            return;
        }
        dispatch(envelope);
    }
}

void IpcClient::abortStream(const LiveText &reason)
{
    qCWarning(lcIpc) << reason.text();
    m_lastError = reason;
    m_buffer.clear();
    failAllPending(QStringLiteral("ERR_PROTOCOL_VERSION"), reason.text());
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
        abortStream([] { return tr("界面和后台服务的版本不匹配，请重新安装软件后重试。"); });
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
        pending.reply->fail(view.errorCode, view.errorMessage, view.errorDetails, view.errorRef);
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
    // The three refusals below are made before anything is written, so they
    // say the request never left (BackendReply::failUnsent): a form may stay
    // editable after one, never after a request that went out unanswered.
    if (!isConnected()) {
        reply->failUnsent(QStringLiteral("ERR_INTERNAL"),
                          tr("后台服务尚未连接。"));
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
        reply->failUnsent(QStringLiteral("ERR_BAD_REQUEST"),
                          tr("本次操作的数据量过大，无法发送。请减少数据量后重试。"));
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
        reply->failUnsent(QStringLiteral("ERR_INTERNAL"),
                          detail.isEmpty()
                              ? tr("操作还没有完整发送给后台服务，请重试。")
                              : tr("操作还没有完整发送给后台服务，请重试。%1").arg(detail));
    }
}

} // namespace mr
