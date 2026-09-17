#include "IBackend.h"

#include "IpcFraming.h"

#include <QDateTime>
#include <QTimer>

#include <memory>
#include <utility>

namespace mr {

BackendReply::BackendReply(QString requestId, QString messageType, QObject *parent)
    : QObject(parent)
    , m_requestId(std::move(requestId))
    , m_messageType(std::move(messageType))
{
}

void BackendReply::succeed(const QJsonObject &payload)
{
    if (m_finished)
        return;
    m_ok = true;
    m_payload = payload;
    finish();
}

void BackendReply::fail(const QString &code, const QString &message, const QJsonObject &details)
{
    if (m_finished)
        return;
    m_ok = false;
    m_payload = {};
    m_errorCode = code;
    m_errorMessage = message;
    m_errorDetails = details;
    finish();
}

void BackendReply::finish()
{
    m_finished = true;
    Q_EMIT done(m_ok, m_payload.toVariantMap(), m_errorCode, m_errorMessage);
    deleteLater();
}

void BackendReply::whenDone(QObject *context, Handler handler)
{
    if (!handler)
        return;
    // The guard makes the two delivery paths mutually exclusive: either done()
    // fires later, or the read-back below sees a reply that already finished
    // inside IBackend::request(). Never both.
    const auto delivered = std::make_shared<bool>(false);
    const auto deliver = [delivered, handler = std::move(handler)](
                             bool ok, const QVariantMap &payload, const QString &code,
                             const QString &message) {
        if (*delivered)
            return;
        *delivered = true;
        handler(ok, payload, code, message);
    };
    connect(this, &BackendReply::done, context ? context : this, deliver);
    if (m_finished)
        deliver(m_ok, m_payload.toVariantMap(), m_errorCode, m_errorMessage);
}

IBackend::~IBackend() = default;

// ---------------------------------------------------------------------------
// Thin, typed wrappers. Keeping the payload assembly here means the two
// backends cannot drift apart in how they name fields.
// ---------------------------------------------------------------------------

BackendReply *IBackend::getVersion()
{
    return request(QStringLiteral("GetVersion"));
}

BackendReply *IBackend::getStatus()
{
    return request(QStringLiteral("GetStatus"));
}

BackendReply *IBackend::getCaptureStatus()
{
    return request(QStringLiteral("GetCaptureStatus"));
}

BackendReply *IBackend::getProtocolProfileStatus()
{
    return request(QStringLiteral("GetProtocolProfileStatus"));
}

BackendReply *IBackend::getCaptureSettings()
{
    return request(QStringLiteral("GetCaptureSettings"));
}

BackendReply *IBackend::updateCaptureSettings(const QJsonObject &changes)
{
    // Sent verbatim. $defs/UpdateCaptureSettingsRequest is
    // additionalProperties:false and every key is optional, so the caller
    // decides what changes and this wrapper never invents a field.
    return request(QStringLiteral("UpdateCaptureSettings"), changes);
}

BackendReply *IBackend::listCaptureAdapters()
{
    return request(QStringLiteral("ListCaptureAdapters"));
}

BackendReply *IBackend::startCapture(const QString &adapterId)
{
    QJsonObject payload;
    if (!adapterId.isEmpty())
        payload.insert(QStringLiteral("adapter_id"), adapterId);
    return request(QStringLiteral("StartCapture"), payload);
}

BackendReply *IBackend::stopCapture()
{
    return request(QStringLiteral("StopCapture"));
}

BackendReply *IBackend::startCaptureValidation(const QString &adapterId)
{
    QJsonObject payload;
    if (!adapterId.isEmpty())
        payload.insert(QStringLiteral("adapter_id"), adapterId);
    return request(QStringLiteral("StartCaptureValidation"), payload);
}

BackendReply *IBackend::getCaptureValidationStatus()
{
    return request(QStringLiteral("GetCaptureValidationStatus"));
}

BackendReply *IBackend::addCaptureValidationMarker(const QString &marker)
{
    return request(QStringLiteral("AddCaptureValidationMarker"),
                   QJsonObject{{QStringLiteral("marker"), marker}});
}

BackendReply *IBackend::stopCaptureValidation()
{
    return request(QStringLiteral("StopCaptureValidation"));
}

BackendReply *IBackend::confirmCalibration(const QJsonArray &verdicts)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("verdicts"), verdicts);
    return request(QStringLiteral("ConfirmCalibration"), payload);
}

BackendReply *IBackend::discardCalibration()
{
    return request(QStringLiteral("DiscardCalibration"));
}

BackendReply *IBackend::getCalibrationShareCode()
{
    return request(QStringLiteral("GetCalibrationShareCode"));
}

BackendReply *IBackend::checkSharedCalibration()
{
    return request(QStringLiteral("CheckSharedCalibration"));
}

BackendReply *IBackend::importCalibrationCode(const QString &code)
{
    return request(QStringLiteral("ImportCalibrationCode"),
                   QJsonObject{{QStringLiteral("code"), code}});
}

BackendReply *IBackend::acceptSharedQueueInference()
{
    return request(QStringLiteral("AcceptSharedQueueInference"));
}

BackendReply *IBackend::rejectSharedCalibration()
{
    return request(QStringLiteral("RejectSharedCalibration"));
}

BackendReply *IBackend::getCurrentRun()
{
    return request(QStringLiteral("GetCurrentRun"));
}

BackendReply *IBackend::subscribeLiveEvents()
{
    return request(QStringLiteral("SubscribeLiveEvents"));
}

BackendReply *IBackend::queryRuns(const QJsonObject &filter, int page, int pageSize,
                                  const QJsonObject &sort)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), filter);
    payload.insert(QStringLiteral("page"), page);
    payload.insert(QStringLiteral("page_size"), pageSize);
    if (!sort.isEmpty())
        payload.insert(QStringLiteral("sort"), sort);
    return request(QStringLiteral("QueryRuns"), payload);
}

BackendReply *IBackend::getRunRevisions(const QString &runId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    return request(QStringLiteral("GetRunRevisions"), payload);
}

BackendReply *IBackend::getRunEvents(const QString &runId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    return request(QStringLiteral("GetRunEvents"), payload);
}

BackendReply *IBackend::queryCandidateObservations(const QString &sessionId,
                                                   const QString &fromUtc,
                                                   const QString &toUtc,
                                                   int page, int pageSize)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("session_id"),
                   sessionId.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(sessionId));
    payload.insert(QStringLiteral("from_utc"),
                   fromUtc.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(fromUtc));
    payload.insert(QStringLiteral("to_utc"),
                   toUtc.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(toUtc));
    payload.insert(QStringLiteral("page"), page);
    payload.insert(QStringLiteral("page_size"), pageSize);
    return request(QStringLiteral("QueryCandidateObservations"), payload);
}

BackendReply *IBackend::reviewCandidateObservation(const QString &id,
                                                   const QString &verdict,
                                                   const QString &note)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("observation_id"), id);
    payload.insert(QStringLiteral("verdict"), verdict);
    payload.insert(QStringLiteral("note"),
                   note.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(note));
    return request(QStringLiteral("ReviewCandidateObservation"), payload);
}

BackendReply *IBackend::exportCandidateEvidence(const QString &targetPath)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("target_path"),
                   targetPath.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(targetPath));
    return request(QStringLiteral("ExportCandidateEvidence"), payload);
}

BackendReply *IBackend::getDashboardStats(const QJsonObject &filter,
                                         const QString &trendGranularity)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), filter);
    // Omitted rather than defaulted here: $defs/StatsRequest is
    // additionalProperties:false and the Collector already owns the default,
    // so sending "day" explicitly would be this client restating a decision it
    // does not make.
    if (!trendGranularity.isEmpty())
        payload.insert(QStringLiteral("trend_granularity"), trendGranularity);
    return request(QStringLiteral("GetDashboardStats"), payload);
}

BackendReply *IBackend::getDungeonStats(const QJsonObject &filter, int page, int pageSize)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), filter);
    payload.insert(QStringLiteral("page"), page);
    payload.insert(QStringLiteral("page_size"), pageSize);
    return request(QStringLiteral("GetDungeonStats"), payload);
}

BackendReply *IBackend::getJobStats(const QJsonObject &filter, int page, int pageSize)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), filter);
    payload.insert(QStringLiteral("page"), page);
    payload.insert(QStringLiteral("page_size"), pageSize);
    return request(QStringLiteral("GetJobStats"), payload);
}

BackendReply *IBackend::getResultStats(const QJsonObject &filter)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("filter"), filter);
    return request(QStringLiteral("GetResultStats"), payload);
}

BackendReply *IBackend::createManualRun(const QJsonObject &run, const QString &reason)
{
    QJsonObject payload = run;
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("CreateManualRun"), payload);
}

BackendReply *IBackend::correctRun(const QString &runId, int expectedRevision,
                                   const QJsonObject &changes, const QString &reason)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), expectedRevision);
    payload.insert(QStringLiteral("changes"), changes);
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("CorrectRun"), payload);
}

BackendReply *IBackend::softDeleteRun(const QString &runId, int expectedRevision,
                                      const QString &reason)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), expectedRevision);
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("SoftDeleteRun"), payload);
}

BackendReply *IBackend::restoreRun(const QString &runId, int expectedRevision,
                                   const QString &reason)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), expectedRevision);
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("RestoreRun"), payload);
}

BackendReply *IBackend::undoRevision(const QString &runId, int expectedRevision,
                                     const QString &reason)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), expectedRevision);
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("UndoRevision"), payload);
}

BackendReply *IBackend::updateAchievementBaseline(int goalCount,
                                                  int baselineCompletedCount,
                                                  const QString &reason,
                                                  const QString &baselineEffectiveAtUtc)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("goal_count"), goalCount);
    payload.insert(QStringLiteral("baseline_completed_count"), baselineCompletedCount);

    // Required by the contract and by the Collector's parser: omitting it is an
    // ERR_BAD_REQUEST, not a defaulted field.
    payload.insert(QStringLiteral("baseline_effective_at"),
                   baselineEffectiveAtUtc.isEmpty()
                       ? ipc::utcTimestamp(QDateTime::currentDateTimeUtc())
                       : baselineEffectiveAtUtc);
    payload.insert(QStringLiteral("reason"), reason);
    return request(QStringLiteral("UpdateAchievementBaseline"), payload);
}

BackendReply *IBackend::setRunReflection(const QString &runId, const QString &mood,
                                         const QString &text)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("mood"), mood);
    // Sent as typed, including an empty string: that is how the user deletes a
    // reflection, so it must not be dropped from the payload here.
    payload.insert(QStringLiteral("text"), text);
    return request(QStringLiteral("SetRunReflection"), payload);
}

BackendReply *IBackend::getReflectionSummary(int recentLimit)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("recent_limit"), qBound(0, recentLimit, 20));
    return request(QStringLiteral("GetReflectionSummary"), payload);
}

BackendReply *IBackend::exportCsv(const QString &targetPath, const QJsonObject &filter)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("target_path"), targetPath);
    payload.insert(QStringLiteral("filter"), filter);
    return request(QStringLiteral("ExportCsv"), payload);
}

BackendReply *IBackend::exportJson(const QString &targetPath, const QJsonObject &filter)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("target_path"), targetPath);
    payload.insert(QStringLiteral("filter"), filter);
    return request(QStringLiteral("ExportJson"), payload);
}

BackendReply *IBackend::backupDatabase(const QString &targetPath)
{
    QJsonObject payload;
    if (!targetPath.isEmpty())
        payload.insert(QStringLiteral("target_path"), targetPath);
    return request(QStringLiteral("BackupDatabase"), payload);
}

BackendReply *IBackend::exportDiagnosticsReport(const QString &targetPath)
{
    QJsonObject payload;
    if (!targetPath.isEmpty())
        payload.insert(QStringLiteral("target_path"), targetPath);
    return request(QStringLiteral("ExportDiagnosticsReport"), payload);
}

BackendReply *IBackend::checkDatabaseIntegrity()
{
    return request(QStringLiteral("CheckDatabaseIntegrity"));
}

BackendReply *IBackend::getSpeechSettings()
{
    return request(QStringLiteral("GetSpeechSettings"));
}

BackendReply *IBackend::updateSpeechSettings(const QVariantMap &fields)
{
    // $defs/UpdateSpeechSettingsRequest is additionalProperties:false, so a key
    // this build does not know is dropped here instead of being refused there.
    static const char *const kStringFields[] = {"provider", "azure_region", "openai_base_url",
                                                "openai_model", "voice"};
    QJsonObject payload;
    for (const char *name : kStringFields) {
        const QString key = QLatin1String(name);
        const auto it = fields.constFind(key);
        if (it == fields.constEnd())
            continue;
        const bool isNull = !it->isValid() || it->isNull();
        // provider is an enum: null is not one of its values.
        if (isNull && key == QLatin1String("provider"))
            continue;
        payload.insert(key, isNull ? QJsonValue(QJsonValue::Null) : QJsonValue(it->toString()));
    }
    // Write-only. Present means "replace" (or "delete" when empty); absent
    // means "keep" - so the key is never sent unless the user typed one or
    // asked for it to be cleared.
    const auto key = fields.constFind(QStringLiteral("api_key"));
    if (key != fields.constEnd() && key->isValid() && !key->isNull())
        payload.insert(QStringLiteral("api_key"), key->toString());
    return request(QStringLiteral("UpdateSpeechSettings"), payload);
}

BackendReply *IBackend::synthesizeSpeech(const QString &text, int ratePercent, bool test)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("text"), text);
    payload.insert(QStringLiteral("rate_percent"), qBound(50, ratePercent, 200));
    payload.insert(QStringLiteral("test"), test);
    return request(QStringLiteral("SynthesizeSpeech"), payload);
}

} // namespace mr
