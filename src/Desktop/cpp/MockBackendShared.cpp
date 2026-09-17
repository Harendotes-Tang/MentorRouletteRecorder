// ---------------------------------------------------------------------------
// 共享校准 fixtures.
//
// Nothing here was downloaded, decoded or verified: this backend has no network
// client and no capture pipeline. The one code it accepts and hands out is the
// committed test vector (tests/Fixtures/shared-calibration/vectors.json
// valid[0]), so importing it through the mock sends exactly the committed
// request sample. Every refusal sentence is the Collector's own wording.
// ---------------------------------------------------------------------------

#include "MockBackend.h"
#include "MockData.h"

#include <QTimer>
#include <QUrl>
#include <QUrlQuery>

#include <initializer_list>
#include <utility>

using namespace mr::mock;

namespace mr {
namespace {

constexpr auto kSharedProfileId = "cn.2026.09.01.0000.0000.shared";
constexpr auto kVectorCode =
    "MRC1.XY_LasMwEEX_ZdbG6G3LuxKyKyG02ZRShCSPExXbMrIdaEP-vWoKpeksZjH3wZkLHO2Axq2hb6EBRpgqiS4JLUme24IC3qMzcfKxRWgEZUwWMNjFn8wc1-TzEZ62-8cX83x4OGyzf4oTNBf4jWgqRQEz9uiXmMzZ9ivO0Lzyt2sBCY8hjrljs8vRBYeptwuaKcUu9GjCN5cfyx-0uiTyr2s-WSZVdvBOeYrEaVu1zAuUXU00tcxxL1qJqqtITTWz3AkvW4XVP_1WmlLIfB93v2bwMzS0gM844p3Cr18";
constexpr auto kVectorSha256 = "67ef1bb97e6510b4bfdc8ab049007f6b8224761b659c741108963d5e2bfe197b";

QJsonObject criterion(const char *message, const char *verdict, const char *reason)
{
    const bool contradicted = qstrcmp(verdict, "CONTRADICTED") == 0;
    return {{QStringLiteral("message"), QLatin1String(message)},
            {QStringLiteral("verdict"), QLatin1String(verdict)},
            {QStringLiteral("reason"), QString::fromUtf8(reason)},
            {QStringLiteral("contradicting_sessions"), contradicted ? 2 : 0}};
}

QJsonObject candidate(const char *source, const char *status, const char *verdict,
                      const char *matchSource)
{
    const bool passed = qstrcmp(verdict, "PASS") == 0;
    const bool waiting = qstrcmp(verdict, "WAIT") == 0;
    const QJsonArray criteria{
        criterion("ZONE_INITIALIZATION", waiting ? "WAIT" : "PASS",
                  waiting ? "还没有见到登录时的换区。" : "登录时的换区与它声明的一致。"),
        criterion("CONTENT_FINDER_POP", verdict,
                  passed    ? "排本之后按声明的形状收到了匹配报文。"
                  : waiting ? "还没有排过本。"
                            : "两次抓包健康的会话里排本后都进了副本，却都没有出现它声明的匹配报文。")};
    return {{QStringLiteral("sha12"), QStringLiteral("67ef1bb97e65")},
            {QStringLiteral("source"), QLatin1String(source)},
            {QStringLiteral("match_source"), QLatin1String(matchSource)},
            {QStringLiteral("status"), QLatin1String(status)},
            {QStringLiteral("verdict"), QLatin1String(verdict)},
            {QStringLiteral("criteria"), criteria},
            {QStringLiteral("staging_overflowed"), false}};
}

QJsonArray attempts(std::initializer_list<std::pair<const char *, const char *>> tried)
{
    QJsonArray rows;
    for (const auto &[source, outcome] : tried) {
        rows.append(QJsonObject{{QStringLiteral("source"), QLatin1String(source)},
                                {QStringLiteral("outcome"), QLatin1String(outcome)}});
    }
    return rows;
}

QString phaseFor(const QString &state)
{
    if (state == QLatin1String("fetching")) return QStringLiteral("FETCHING");
    if (state == QLatin1String("verifying") || state == QLatin1String("manual"))
        return QStringLiteral("VERIFYING");
    if (state == QLatin1String("consent")) return QStringLiteral("AWAITING_CONSENT");
    if (state == QLatin1String("verified")) return QStringLiteral("VERIFIED");
    if (state == QLatin1String("rejected") || state == QLatin1String("user-rejected"))
        return QStringLiteral("REJECTED");
    if (state == QLatin1String("unavailable")) return QStringLiteral("UNAVAILABLE");
    return QStringLiteral("NONE");
}

QJsonArray candidatesFor(const QString &state)
{
    if (state == QLatin1String("verifying"))
        return {candidate("DOWNLOADED", "VERIFYING", "WAIT", "REPLY_STATE")};
    if (state == QLatin1String("manual"))
        return {candidate("MANUAL", "VERIFYING", "WAIT", "REPLY_STATE")};
    if (state == QLatin1String("consent"))
        return {candidate("DOWNLOADED", "AWAITING_CONSENT", "PASS", "QUEUE_REQUEST")};
    if (state == QLatin1String("verified"))
        return {candidate("DOWNLOADED", "IN_USE", "PASS", "REPLY_STATE")};
    if (state == QLatin1String("rejected"))
        return {candidate("DOWNLOADED", "REJECTED", "CONTRADICTED", "ANNOUNCEMENT")};
    return {};
}

QJsonValue lastFetchFor(const QString &state)
{
    if (state == QLatin1String("unavailable")) return QStringLiteral("INDEX_UNAVAILABLE");
    if (state == QLatin1String("none-for-build")) return QStringLiteral("NONE_FOR_BUILD");
    static const QStringList fetched{QStringLiteral("verifying"), QStringLiteral("consent"),
                                     QStringLiteral("verified"), QStringLiteral("rejected"),
                                     QStringLiteral("user-rejected")};
    return fetched.contains(state) ? QJsonValue(QStringLiteral("OK")) : QJsonValue(QJsonValue::Null);
}

QJsonObject outcome(const char *token)
{
    return {{QStringLiteral("outcome"), QLatin1String(token)}};
}

QJsonObject importAnswer(const char *result, const QJsonValue &reason, const char *message,
                         const QJsonValue &sha256 = QJsonValue::Null)
{
    return {{QStringLiteral("outcome"), QLatin1String(result)},
            {QStringLiteral("reason"), reason},
            {QStringLiteral("message"), QString::fromUtf8(message)},
            {QStringLiteral("code_sha256"), sha256}};
}

QJsonObject badRequest(QString *errorCode, QString *errorMessage, const char *message)
{
    *errorCode = QStringLiteral("ERR_BAD_REQUEST");
    *errorMessage = QString::fromUtf8(message);
    return {};
}

} // namespace

void MockBackend::setSharedCalibrationFixture(const QString &state)
{
    m_sharedState = state;
    // Only when --mock-calibration said nothing: `--mock-calibration idle --mock-shared share`
    // is the restart, where the local profile records and no calibration card is shown.
    if (m_calibrationState.isEmpty()) {
        m_calibrationState = state == QLatin1String("share") ? QStringLiteral("done")
                                                             : QStringLiteral("observing");
    }
}

QJsonObject MockBackend::sharedCalibrationStatus() const
{
    const QString state = m_sharedState;
    const bool verified = state == QLatin1String("verified");
    const QJsonValue lastFetch = lastFetchFor(state);
    const QJsonArray tried = state == QLatin1String("unavailable")
        ? attempts({{"GITHUB_RAW", "TIMEOUT"}, {"CDN_PRIMARY", "DNS_OR_CONNECT"},
                    {"CDN_FALLBACK", "DNS_OR_CONNECT"}})
        : (lastFetch.isNull() ? QJsonArray() : attempts({{"GITHUB_RAW", "OK"}}));
    const QJsonArray candidates = candidatesFor(state);
    return {{QStringLiteral("phase"), phaseFor(state)},
            {QStringLiteral("candidates"), candidates},
            {QStringLiteral("last_fetch_status"), lastFetch},
            {QStringLiteral("last_index_attempts"), tried},
            {QStringLiteral("profile_id"),
             verified ? QJsonValue(QLatin1String(kSharedProfileId)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("bound_at_utc"),
             verified ? QJsonValue(isoUtc(m_now.addSecs(-1800))) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("last_refusal"),
             state == QLatin1String("rejected") ? QJsonValue(QStringLiteral("CONTRADICTED"))
                                                : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("rejected_candidates"), state == QLatin1String("rejected") ? 1 : 0},
            {QStringLiteral("user_rejected"), state == QLatin1String("user-rejected")}};
}

bool MockBackend::isSharedCalibrationMessage(const QString &messageType)
{
    static const QStringList types{QStringLiteral("GetCalibrationShareCode"),
                                   QStringLiteral("CheckSharedCalibration"),
                                   QStringLiteral("ImportCalibrationCode"),
                                   QStringLiteral("AcceptSharedQueueInference"),
                                   QStringLiteral("RejectSharedCalibration")};
    return types.contains(messageType);
}

QJsonObject MockBackend::applySharedCalibration(const QString &messageType, const QJsonObject &payload,
                                                QString *errorCode, QString *errorMessage,
                                                QJsonObject *errorDetails)
{
    if (messageType == QLatin1String("ImportCalibrationCode"))
        return importCodePayload(payload, errorCode, errorMessage);
    if (!payload.isEmpty())
        return badRequest(errorCode, errorMessage, "该请求不带任何字段。");
    if (messageType == QLatin1String("GetCalibrationShareCode"))
        return shareCodePayload(errorCode, errorMessage, errorDetails);
    if (messageType == QLatin1String("CheckSharedCalibration"))
        return {{QStringLiteral("outcome"), checkSharedOutcome()}};
    if (messageType == QLatin1String("AcceptSharedQueueInference")) {
        if (m_sharedState != QLatin1String("consent"))
            return outcome("NOTHING_TO_ACCEPT");
        m_sharedState = QStringLiteral("verified");
        emitCalibrationChangedLater();
        return outcome("ACCEPTED");
    }
    return rejectSharedPayload();
}

QJsonObject MockBackend::shareCodePayload(QString *errorCode, QString *errorMessage,
                                          QJsonObject *errorDetails) const
{
    // Like the Collector: whatever the profile in force is decides, not whether
    // calibration happens to be running.
    const bool shared = m_sharedState == QLatin1String("verified");
    if (localProfileBound() && !shared) {
        QUrlQuery query;
        query.addQueryItem(QStringLiteral("template"), QStringLiteral("share-calibration.yml"));
        query.addQueryItem(QStringLiteral("title"), QString::fromUtf8("[共享校准] CN 2026.09.01.0000.0000"));
        query.addQueryItem(QStringLiteral("code"), QLatin1String(kVectorCode));
        QUrl url(QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new"));
        url.setQuery(query);
        return {{QStringLiteral("code"), QLatin1String(kVectorCode)},
                {QStringLiteral("code_sha256"), QLatin1String(kVectorSha256)},
                {QStringLiteral("issue_url"), url.toString(QUrl::FullyEncoded)},
                {QStringLiteral("code_in_url"), true}};
    }
    *errorCode = QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE");
    *errorMessage = QString::fromUtf8(shared ? "当前档案来自其他玩家分享的校准，不能再生成校准码。"
                                             : "当前没有可以分享的本机校准档案。");
    *errorDetails = {{QStringLiteral("reason"), shared ? QStringLiteral("SHARED") : QStringLiteral("NO_PROFILE")}};
    return {};
}

QJsonObject MockBackend::importCodePayload(const QJsonObject &payload, QString *errorCode,
                                           QString *errorMessage)
{
    const QJsonValue value = payload.value(QStringLiteral("code"));
    if (payload.size() != 1 || !value.isString() || value.toString().size() > 65536)
        return badRequest(errorCode, errorMessage, "缺少必填字段 code。");
    const QString code = value.toString().trimmed();
    if (code.isEmpty())
        return importAnswer("MALFORMED", QStringLiteral("E_SHARE_CODE_EMPTY"), "这不是一份能识别的校准码：内容为空。");
    if (code.size() > 4096)
        return importAnswer("MALFORMED", QStringLiteral("E_SHARE_CODE_TOO_LONG"),
                            "这不是一份能识别的校准码：内容太长，校准码最多 4096 个字符。");
    if (!code.startsWith(QStringLiteral("MRC1.")))
        return importAnswer("MALFORMED", QStringLiteral("E_SHARE_CODE_NOT_A_CODE"),
                            "这不是一份能识别的校准码：它不是本软件生成的校准码。");
    if (m_calibrationState != QLatin1String("observing"))
        return importAnswer("NOT_APPLICABLE", QStringLiteral("NOT_CALIBRATING"),
                            "当前客户端不在校准中，暂时不需要导入校准码。");
    if (m_sharedState == QLatin1String("user-rejected"))
        return importAnswer("NOT_APPLICABLE", QStringLiteral("USER_REJECTED"),
                            "你已经选择这个游戏版本不用其他玩家的共享校准；在校准卡片上点「重新观察」后才能导入校准码。");
    if (code != QLatin1String(kVectorCode))
        return importAnswer("MALFORMED", QStringLiteral("UNBUILDABLE"),
                            "（模拟后端）这份校准码无法在本机的随包模板上生成协议档案。");
    m_sharedState = QStringLiteral("manual");
    emitCalibrationChangedLater();
    return importAnswer("APPLIED", QJsonValue::Null, "校准码已导入，登录或排本时会在本机流量里自动核实。",
                        QLatin1String(kVectorSha256));
}

QString MockBackend::checkSharedOutcome()
{
    if (m_calibrationState != QLatin1String("observing")
        || m_sharedState == QLatin1String("user-rejected") || m_sharedState == QLatin1String("verified"))
        return QStringLiteral("NOT_NEEDED");
    if (!captureSettings().value(QStringLiteral("shared_calibration_enabled")).toBool(true))
        return QStringLiteral("DISABLED");
    if (m_sharedState == QLatin1String("fetching"))
        return QStringLiteral("ALREADY_FETCHING");
    m_sharedState = QStringLiteral("fetching");
    emitCalibrationChangedLater();
    return QStringLiteral("STARTED");
}

QJsonObject MockBackend::rejectSharedPayload()
{
    if (m_calibrationState.isEmpty()) {
        return {{QStringLiteral("withdrawn_profile_id"), QJsonValue::Null},
                {QStringLiteral("dropped_candidates"), 0}};
    }
    const bool withdrawn = m_sharedState == QLatin1String("verified");
    const int dropped = candidatesFor(m_sharedState).size() > 0 && !withdrawn
                            && m_sharedState != QLatin1String("rejected") ? 1 : 0;
    m_sharedState = QStringLiteral("user-rejected");
    emitCalibrationChangedLater();
    return {{QStringLiteral("withdrawn_profile_id"),
             withdrawn ? QJsonValue(QLatin1String(kSharedProfileId)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("dropped_candidates"), dropped}};
}

void MockBackend::emitCalibrationChangedLater()
{
    QTimer::singleShot(0, this, [this] {
        QVariantMap event = liveEventEnvelope(QStringLiteral("CalibrationChanged"),
                                              QStringLiteral("calibration_changed"));
        const QString state = calibrationStatus().value(QStringLiteral("state")).toString();
        event.insert(QStringLiteral("calibration_state"), state.isEmpty() ? QStringLiteral("IDLE") : state);
        event.insert(QStringLiteral("ready"), state == QLatin1String("READY"));
        Q_EMIT liveEvent(event);
    });
}

} // namespace mr
