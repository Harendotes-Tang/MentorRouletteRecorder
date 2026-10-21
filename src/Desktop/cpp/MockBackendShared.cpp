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

#include <QRegularExpression>
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

/// \a gate is null for the fixtures that predate the graded gates: a Collector before
/// 1.1.0 sends no criteria[].gate.
QJsonObject criterion(const char *message, const char *verdict, const char *reason,
                      const char *gate = nullptr)
{
    const bool contradicted = qstrcmp(verdict, "CONTRADICTED") == 0;
    QJsonObject row{{QStringLiteral("message"), QLatin1String(message)},
                    {QStringLiteral("verdict"), QLatin1String(verdict)},
                    {QStringLiteral("reason"), QString::fromUtf8(reason)},
                    {QStringLiteral("contradicting_sessions"), contradicted ? 2 : 0}};
    if (gate)
        row.insert(QStringLiteral("gate"), QLatin1String(gate));
    return row;
}

/// One $defs/SharedCalibrationCandidate. \a provenance is null for the fixtures that
/// predate the graded gates (plans/shared-calibration.md §18.3), so the desktop's
/// "not reported" path - what a Collector before 1.1.0 produces - stays covered too.
QJsonObject candidate(const char *source, const char *status, const char *verdict,
                      const char *matchSource, const char *provenance = nullptr,
                      bool auditPending = false)
{
    const bool passed = qstrcmp(verdict, "PASS") == 0;
    const bool waiting = qstrcmp(verdict, "WAIT") == 0;
    // A PUBLISHED code binds on the login burst alone, so its match criterion is
    // still being audited while it records rather than holding the bind back. Every
    // published fixture is one the Collector judges that way: nothing usable records,
    // no conflict mark, no better-submitted rival (SharedCalibrationSession.GateFor).
    // The zone marker is always required; with no provenance no gate is sent at all.
    const char *zoneGate = provenance ? "REQUIRED" : nullptr;
    const char *popGate = !provenance ? nullptr
        : qstrcmp(provenance, "PUBLISHED") == 0 ? "AUDIT" : "REQUIRED";
    const QJsonArray criteria{
        criterion("ZONE_INITIALIZATION", waiting && !auditPending ? "WAIT" : "PASS",
                  waiting && !auditPending ? "还没有见到登录时的换区。" : "登录时的换区与它声明的一致。",
                  zoneGate),
        criterion("CONTENT_FINDER_POP", auditPending ? "WAIT" : verdict,
                  auditPending ? "还在核对排本之后的匹配报文。"
                  : passed     ? "排本之后按声明的形状收到了匹配报文。"
                  : waiting    ? "还没有排过本。"
                               : "两次抓包健康的会话里排本后都进了副本，却都没有出现它声明的匹配报文。",
                  popGate)};
    QJsonObject row{{QStringLiteral("sha12"), QStringLiteral("67ef1bb97e65")},
                    {QStringLiteral("source"), QLatin1String(source)},
                    {QStringLiteral("match_source"), QLatin1String(matchSource)},
                    {QStringLiteral("status"), QLatin1String(status)},
                    {QStringLiteral("verdict"), QLatin1String(verdict)},
                    {QStringLiteral("criteria"), criteria},
                    {QStringLiteral("staging_overflowed"), false}};
    if (provenance) {
        row.insert(QStringLiteral("provenance"), QLatin1String(provenance));
        row.insert(QStringLiteral("audit_pending"), auditPending);
    }
    return row;
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
    if (state == QLatin1String("verifying") || state == QLatin1String("manual")
        || state == QLatin1String("imported-published")
        || state == QLatin1String("imported-unpublished"))
        return QStringLiteral("VERIFYING");
    if (state == QLatin1String("consent")) return QStringLiteral("AWAITING_CONSENT");
    if (state == QLatin1String("verified") || state == QLatin1String("verified-auditing")
        || state == QLatin1String("verified-proven"))
        return QStringLiteral("VERIFIED");
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
    if (state == QLatin1String("verified-auditing")) {
        // §18.4: bound at the login burst, recording, with the match still audited.
        return {candidate("DOWNLOADED", "IN_USE", "PASS", "REPLY_STATE", "PUBLISHED", true)};
    }
    if (state == QLatin1String("verified-proven")) {
        // §20.6: it recorded one complete duty here and nothing is audited any more, so the
        // watch is over and its code may be confirmed to other players. The §20.5 marks ride
        // along - it won a dispute over the match message on its shape - and the desktop
        // shows none of them.
        QJsonObject proven = candidate("DOWNLOADED", "PROVEN", "PASS", "REPLY_STATE", "PUBLISHED", false);
        proven.insert(QStringLiteral("disputed"), QJsonArray{QStringLiteral("CONTENT_FINDER_POP")});
        proven.insert(QStringLiteral("preferred"), true);
        proven.insert(QStringLiteral("shape_prior"), true);
        return {proven};
    }
    // §18.5: a pasted code the last index read lists is judged like a downloaded one;
    // one no index knows waits for the match and the duty entry as well.
    if (state == QLatin1String("imported-published"))
        return {candidate("MANUAL", "VERIFYING", "WAIT", "REPLY_STATE", "PUBLISHED")};
    if (state == QLatin1String("imported-unpublished"))
        return {candidate("MANUAL", "VERIFYING", "WAIT", "REPLY_STATE", "IMPORTED")};
    if (state == QLatin1String("rejected"))
        return {candidate("DOWNLOADED", "REJECTED", "CONTRADICTED", "ANNOUNCEMENT")};
    return {};
}

QJsonValue lastFetchFor(const QString &state)
{
    if (state == QLatin1String("unavailable")) return QStringLiteral("INDEX_UNAVAILABLE");
    if (state == QLatin1String("none-for-build")) return QStringLiteral("NONE_FOR_BUILD");
    // imported-unpublished is the one state with no index behind it: nothing was ever
    // downloaded for this build, which is exactly why the pasted code counts as IMPORTED.
    static const QStringList fetched{QStringLiteral("verifying"), QStringLiteral("consent"),
                                     QStringLiteral("verified"), QStringLiteral("verified-auditing"),
                                     QStringLiteral("verified-proven"), QStringLiteral("rejected"), QStringLiteral("user-rejected"),
                                     QStringLiteral("imported-published")};
    return fetched.contains(state) ? QJsonValue(QStringLiteral("OK")) : QJsonValue(QJsonValue::Null);
}

QJsonObject outcome(const char *token)
{
    return {{QStringLiteral("outcome"), QLatin1String(token)}};
}

/// An ImportCalibrationCode answer: the sentence in \a message and its catalogue
/// reference in \a messageRef, as SharedCalibrationHandlers.ImportResponse writes them.
QJsonObject importAnswer(const char *result, const QJsonValue &reason, const QString &message,
                         const QJsonObject &messageRef, const QJsonValue &sha256 = QJsonValue::Null)
{
    return {{QStringLiteral("outcome"), QLatin1String(result)},
            {QStringLiteral("reason"), reason},
            {QStringLiteral("message"), message},
            {QStringLiteral("message_ref"), messageRef},
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
    // A proven shared profile ends the watch the way the Collector does (MarkDone): DONE.
    if (m_calibrationState.isEmpty()) {
        m_calibrationState = state == QLatin1String("share") || state == QLatin1String("verified-proven")
                                 ? QStringLiteral("done")
                                 : QStringLiteral("observing");
    }
}

bool MockBackend::sharedProfileBound() const
{
    return m_sharedState == QLatin1String("verified")
        || m_sharedState == QLatin1String("verified-auditing")
        || m_sharedState == QLatin1String("verified-proven");
}

QJsonObject MockBackend::sharedCalibrationStatus() const
{
    const QString state = m_sharedState;
    const bool verified = sharedProfileBound();
    const bool auditing = state == QLatin1String("verified-auditing");
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
            {QStringLiteral("user_rejected"), state == QLatin1String("user-rejected")},
            {QStringLiteral("audit_pending"), auditing}};
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
    // calibration happens to be running. Another player's profile is handed out once it
    // has proven itself here (plans/shared-calibration.md §20.5), with basis VERIFIED and
    // the form's 来源 prefilled to match; before that it is refused as not proven yet.
    const bool shared = sharedProfileBound();
    const bool proven = m_sharedState == QLatin1String("verified-proven");
    if (shared ? proven : localProfileBound()) {
        // The two options of the issue form's 来源 dropdown (plans/shared-calibration.md §20.4).
        const char *basisLabel = proven ? "其他玩家分享，已在我的软件里核实通过并正常记录"
                                        : "本机校准得到，并逐条核对过校准时间线";
        QUrlQuery query;
        query.addQueryItem(QStringLiteral("template"), QStringLiteral("share-calibration.yml"));
        query.addQueryItem(QStringLiteral("title"), QString::fromUtf8("[共享校准] CN 2026.09.01.0000.0000"));
        query.addQueryItem(QStringLiteral("basis"), QString::fromUtf8(basisLabel));
        query.addQueryItem(QStringLiteral("code"), QLatin1String(kVectorCode));
        QUrl url(QStringLiteral("https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/new"));
        url.setQuery(query);
        return {{QStringLiteral("code"), QLatin1String(kVectorCode)},
                {QStringLiteral("code_sha256"), QLatin1String(kVectorSha256)},
                {QStringLiteral("issue_url"), url.toString(QUrl::FullyEncoded)},
                {QStringLiteral("code_in_url"), true},
                {QStringLiteral("basis"), proven ? QStringLiteral("VERIFIED") : QStringLiteral("CALIBRATED")}};
    }
    *errorCode = QStringLiteral("ERR_SHARE_CODE_UNAVAILABLE");
    *errorMessage = QString::fromUtf8(shared ? "这份共享校准还在核对中，等它完整记录过一次随机任务后再告诉其他玩家。"
                                             : "当前没有可以分享的本机校准档案。");
    *errorDetails = {{QStringLiteral("reason"),
                      shared ? QStringLiteral("SHARED_NOT_PROVEN") : QStringLiteral("NO_PROFILE")}};
    return {};
}

QJsonObject MockBackend::importCodePayload(const QJsonObject &payload, QString *errorCode,
                                           QString *errorMessage)
{
    // The Collector's sentences (SharedCalibrationSession.Import, ShareCodeRejection): a
    // code it cannot read is 「校准码未导入：<why>」, the why a sentence of its own.
    const auto notImported = [](const char *reason) {
        const CollectorSentence why = collectorSentence(reason);
        return collectorSentence("sharing.import.not_imported",
                                 {{QStringLiteral("reason"), why.ref.toVariantMap()}});
    };
    const auto importRefusal = [](const char *result, const QString &reason, const CollectorSentence &sentence) {
        return importAnswer(result, reason, sentence.chinese, sentence.ref);
    };
    const QJsonValue value = payload.value(QStringLiteral("code"));
    if (payload.size() != 1 || !value.isString() || value.toString().size() > 65536)
        return badRequest(errorCode, errorMessage, "缺少必填字段 code。");
    const QString code = value.toString().trimmed();
    if (code.isEmpty())
        return importRefusal("MALFORMED", QStringLiteral("E_SHARE_CODE_EMPTY"),
                             notImported("sharing.code.empty"));
    if (code.size() > 4096)
        return importRefusal("MALFORMED", QStringLiteral("E_SHARE_CODE_TOO_LONG"),
                             collectorSentence("sharing.import.code_too_long", {{QStringLiteral("max"), 4096}}));
    if (!code.startsWith(QStringLiteral("MRC1.")))
        return importRefusal("MALFORMED", QStringLiteral("E_SHARE_CODE_NOT_A_CODE"),
                             notImported("sharing.code.not_a_code"));
    if (m_calibrationState != QLatin1String("observing"))
        return importRefusal("NOT_APPLICABLE", QStringLiteral("NOT_CALIBRATING"),
                             collectorSentence("sharing.import.not_calibrating"));
    if (m_sharedState == QLatin1String("user-rejected"))
        return importRefusal("NOT_APPLICABLE", QStringLiteral("USER_REJECTED"),
                             collectorSentence("sharing.import.user_rejected"));
    if (code != QLatin1String(kVectorCode))
        return importRefusal("MALFORMED", QStringLiteral("UNBUILDABLE"),
                             collectorSentence("sharing.import.unbuildable"));
    m_sharedState = QStringLiteral("manual");
    emitCalibrationChangedLater();
    // No index lists the mock's code: the Collector's wording for an unpublished one.
    const CollectorSentence applied = collectorSentence("sharing.import.applied_unpublished");
    return importAnswer("APPLIED", QJsonValue::Null, applied.chinese, applied.ref,
                        QLatin1String(kVectorSha256));
}

QString MockBackend::checkSharedOutcome()
{
    if (m_calibrationState != QLatin1String("observing")
        || m_sharedState == QLatin1String("user-rejected") || sharedProfileBound())
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
    const bool withdrawn = sharedProfileBound();
    const int dropped = candidatesFor(m_sharedState).size() > 0 && !withdrawn
                            && m_sharedState != QLatin1String("rejected") ? 1 : 0;
    // A withdrawn shared profile leaves nothing of this machine's own behind it in these
    // fixtures, so the Collector re-arms calibration. Left at done / idle, the mock would
    // report a local profile that never existed and offer 分享给其他玩家 for it.
    if (withdrawn && (m_calibrationState == QLatin1String("done")
                      || m_calibrationState == QLatin1String("idle")))
        m_calibrationState = QStringLiteral("observing");
    m_sharedState = QStringLiteral("user-rejected");
    emitCalibrationChangedLater();
    return {{QStringLiteral("withdrawn_profile_id"),
             withdrawn ? QJsonValue(QLatin1String(kSharedProfileId)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("dropped_candidates"), dropped}};
}

QJsonObject MockBackend::calibrationEvidence(const QString &messageType, const QJsonObject &payload,
                                             QString *errorCode, QString *errorMessage)
{
    // CalibrationEvidenceHandlers.Response for evidence that is not there: no summary, no
    // form address, and the Collector's reason. This backend has never completed a healthy
    // session after a candidate was registered, nor sealed a metadata observation.
    const auto unavailable = [](const char *kind, const char *reason) {
        return QJsonObject{{QStringLiteral("available"), false},
                           {QStringLiteral("kind"), QLatin1String(kind)},
                           {QStringLiteral("evidence_json"), QJsonValue(QJsonValue::Null)},
                           {QStringLiteral("issue_url"), QJsonValue(QJsonValue::Null)},
                           {QStringLiteral("evidence_in_url"), false},
                           {QStringLiteral("reason"), QLatin1String(reason)}};
    };
    if (messageType == QLatin1String("GetTemplateObservation")) {
        // CaptureValidationController.GetTemplateObservation: a session still running or
        // draining is OBSERVATION_ACTIVE; the synthetic fixtures never sealed a trace.
        const bool active = m_validationState == QLatin1String("WAITING")
                            || m_validationState == QLatin1String("RECORDING")
                            || m_validationState == QLatin1String("STOPPING");
        return unavailable("TEMPLATE_OBSERVATION", active ? "OBSERVATION_ACTIVE" : "NO_OBSERVATION");
    }

    // CalibrationEvidenceHandlers.Get, then CalibrationEvidenceCollector.Export over an
    // evidence store that holds nothing.
    static const QRegularExpression sha256(QStringLiteral("^[0-9a-f]{64}$"));
    const QJsonValue code = payload.value(QStringLiteral("code_sha256"));
    const bool chosen = !code.isUndefined() && !code.isNull();
    if (chosen && !sha256.match(code.toString()).hasMatch())
        return refuseWith(errorCode, errorMessage, QStringLiteral("ERR_BAD_REQUEST"),
                          "calibration.evidence_hash_invalid");
    // Every fixture candidate is the committed test vector, whether or not its row names
    // the full identity (code_sha256 is optional on the wire).
    QStringList candidates;
    for (const QJsonValue &candidate : candidatesFor(m_sharedState)) {
        const QJsonValue identity = candidate.toObject().value(QStringLiteral("code_sha256"));
        candidates.append(identity.isString() ? identity.toString() : QLatin1String(kVectorSha256));
    }
    if (candidates.isEmpty())
        return unavailable("CODE_VERIFICATION", "NO_CURRENT_CANDIDATE");
    if (!chosen && candidates.size() != 1)
        return unavailable("CODE_VERIFICATION", "SELECT_CANDIDATE");
    if (chosen && !candidates.contains(code.toString()))
        return unavailable("CODE_VERIFICATION", "CANDIDATE_NOT_OBSERVED");
    return unavailable("CODE_VERIFICATION", "NO_COMPLETED_HEALTHY_SESSION");
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
