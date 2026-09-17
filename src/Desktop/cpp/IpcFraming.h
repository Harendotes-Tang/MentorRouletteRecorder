#pragma once

// ---------------------------------------------------------------------------
// IPC framing for the Collector Named Pipe (protocol_version = 1).
//
//   frame := uint32 little-endian byte length | that many bytes of UTF-8 JSON
//
// The functions here are deliberately free of any transport so they can be
// unit tested without a pipe. See contracts/ipc-v1.schema.json.
// ---------------------------------------------------------------------------

#include <QByteArray>
#include <QDateTime>
#include <QJsonObject>
#include <QString>

namespace mr::ipc {

/// protocol_version carried in every envelope.
inline constexpr int kProtocolVersion = 1;

/// Largest frame we are willing to encode or decode. Anything bigger is a
/// protocol error, never an allocation.
inline constexpr qint64 kMaxFrameBytes = 4LL * 1024 * 1024;

/// Result of trying to pull one frame out of a receive buffer.
enum class FrameStatus {
    Ok,          ///< a complete, well-formed envelope was extracted
    Incomplete,  ///< not enough bytes yet; keep reading
    Oversized,   ///< the declared length exceeds kMaxFrameBytes
    BadJson,     ///< the payload is not a UTF-8 JSON object
};

/// Encode one envelope. Returns an empty QByteArray when the encoded JSON
/// would exceed kMaxFrameBytes.
QByteArray encodeFrame(const QJsonObject &envelope);

/// Try to take the first frame out of \a buffer.
///
/// On FrameStatus::Ok the consumed bytes are removed from \a buffer and
/// \a out holds the decoded object. On Incomplete the buffer is untouched.
/// On Oversized / BadJson the caller must drop the connection: the stream can
/// no longer be resynchronised.
FrameStatus takeFrame(QByteArray &buffer, QJsonObject &out);

/// Build a request envelope. \a requestId must be a UUID string.
QJsonObject makeRequest(const QString &requestId,
                        const QString &messageType,
                        const QJsonObject &payload);

/// Fresh UUID in the canonical 8-4-4-4-12 form (no braces).
QString newRequestId();

/// Format \a moment as the contract's $defs/UtcTimestamp: UTC ISO-8601 with
/// milliseconds and a literal 'Z' (for example 2026-09-04T11:22:33.456Z).
QString utcTimestamp(const QDateTime &moment);

/// What a decoded envelope turned out to be.
enum class EnvelopeKind {
    Response,  ///< an answer to a request; correlate by requestId
    Event,     ///< a live event; payload is a $defs/LiveEvent
};

/// A decoded response or event envelope.
///
/// Additive tolerance is deliberate: any top-level or payload field the
/// contract gains later is simply carried along, never a parse failure. The
/// Collector writes both an `error` object and a copy of it in `payload`
/// (docs/architecture.md 3.2); \ref readEnvelope prefers `error` and falls back
/// to `payload`, so both spellings and both `ok` / `message_type == "Error"`
/// branches produce the same result.
struct EnvelopeView {
    EnvelopeKind kind = EnvelopeKind::Response;
    int protocolVersion = -1;
    QString requestId;
    QString messageType;
    bool ok = true;
    QJsonObject payload;
    QString errorCode;
    QString errorMessage;
    QJsonObject errorDetails;
};

/// Decode one response or event envelope. Never fails: an envelope missing
/// `ok` is treated as a success (the field is required by the contract, but an
/// older peer that omits it must not be read as an unexplained failure), and a
/// failure with no readable code becomes ERR_INTERNAL.
EnvelopeView readEnvelope(const QJsonObject &envelope);

} // namespace mr::ipc
