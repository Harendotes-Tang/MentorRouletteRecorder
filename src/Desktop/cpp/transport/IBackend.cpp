#include "IBackend.h"

#include "CollectorText.h"
#include "IpcFraming.h"

#include <QCryptographicHash>
#include <QDateTime>
#include <QPointer>
#include <QTimer>

#include <algorithm>
#include <memory>
#include <utility>

namespace mr {

namespace {

using HistoryTarget = QPair<QString, int>;

QList<HistoryTarget> historyTargets(const QJsonArray &targets)
{
    QList<HistoryTarget> result;
    result.reserve(targets.size());
    for (const auto &entry : targets) {
        const QJsonObject target = entry.toObject();
        result.append({target.value(QStringLiteral("run_id")).toString().toLower(),
                       target.value(QStringLiteral("expected_revision")).toInt()});
    }
    std::ranges::sort(result);
    return result;
}

QString historySelectionDigest(const QList<HistoryTarget> &targets)
{
    QCryptographicHash digest(QCryptographicHash::Sha256);
    for (const auto &[id, revision] : targets) {
        const QByteArray line = id.toUtf8() + ':' + QByteArray::number(revision) + '\n';
        digest.addData(QByteArrayView(line));
    }
    return QString::fromLatin1(digest.result().toHex());
}

class HistorySelectionRequest final : public QObject
{
public:
    HistorySelectionRequest(IBackend *backend, BackendReply *result, QJsonObject payload,
                            QList<HistoryTarget> targets, QString selectionId)
        : QObject(result), m_backend(backend), m_result(result), m_payload(std::move(payload)),
          m_targets(std::move(targets)), m_selectionId(std::move(selectionId))
    {
        connect(m_backend, &IBackend::connectionChanged, this, [this] {
            if (!m_actionSent && !m_result->isFinished())
                m_result->failUnsent(QStringLiteral("ERR_INTERNAL"), m_backend->connectionDetail());
        });
        QTimer::singleShot(0, this, &HistorySelectionRequest::sendChunk);
    }

private:
    void sendChunk()
    {
        if (m_result->isFinished())
            return;
        const auto start = static_cast<qsizetype>(m_index) * IBackend::kHistorySelectionPageSize;
        const auto end = qMin(start + IBackend::kHistorySelectionPageSize, m_targets.size());
        QJsonArray targets;
        for (qsizetype i = start; i < end; ++i) {
            const auto &[id, revision] = m_targets.at(i);
            targets.append(QJsonObject{{QStringLiteral("run_id"), id},
                                       {QStringLiteral("expected_revision"), revision}});
        }
        const bool last = end == m_targets.size();
        auto *stage = m_backend->request(QStringLiteral("StageHistorySelection"),
            {{QStringLiteral("selection_id"), m_selectionId},
             {QStringLiteral("chunk_index"), m_index}, {QStringLiteral("runs"), targets},
             {QStringLiteral("last"), last}});
        const QPointer<BackendReply> guard(stage);
        stage->whenDone(this,
            [this, last, guard](bool ok, const QVariantMap &answer, const QString &code, const QString &message) {
                if (m_result->isFinished())
                    return;
                if (!ok) {
                    // The actual export, preview or mutation has not been sent.
                    if (guard)
                        m_result->forwardFailure(*guard, true);
                    else
                        m_result->failUnsent(code, message);
                    return;
                }
                const bool sealed = answer.value(QStringLiteral("sealed")).toBool();
                const int next = answer.value(QStringLiteral("next_chunk_index")).toInt();
                const auto chunks = (m_targets.size() - 1) / IBackend::kHistorySelectionPageSize + 1;
                if (answer.value(QStringLiteral("selection_id")).toString() != m_selectionId
                    || (sealed ? next != chunks : last || next <= m_index || next >= chunks)) {
                    m_result->failUnsent(QStringLiteral("ERR_INTERNAL"),
                        IBackend::tr("后台服务未确认完整的勾选记录，请重试。"));
                    return;
                }
                if (sealed) {
                    sendAction();
                    return;
                }
                m_index = next;
                QTimer::singleShot(0, this, &HistorySelectionRequest::sendChunk);
            });
    }

    void sendAction()
    {
        m_actionSent = true;
        BackendReply *action = m_backend->requestWithId(m_result->messageType(), m_payload, m_result->requestId());
        const QPointer<BackendReply> guard(action);
        action->whenDone(this, [this, guard](bool ok, const QVariantMap &answer,
                                             const QString &code, const QString &message) {
            if (ok) {
                m_result->succeed(QJsonObject::fromVariantMap(answer));
            } else if (guard) {
                m_result->forwardFailure(*guard);
            } else {
                m_result->fail(code, message);
            }
        });
    }

    IBackend *m_backend;
    BackendReply *m_result;
    QJsonObject m_payload;
    QList<HistoryTarget> m_targets;
    QString m_selectionId;
    int m_index = 0;
    bool m_actionSent = false;
};

} // namespace

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

void BackendReply::fail(const QString &code, const QString &message, const QJsonObject &details,
                        const QJsonObject &messageRef)
{
    if (m_finished)
        return;
    m_ok = false;
    m_payload = {};
    m_errorCode = code;
    m_errorRef = messageRef;
    m_errorMessage = messageRef.isEmpty()
                         ? message
                         : collector_text::render(messageRef.toVariantMap(), message);
    m_errorDetails = details;
    finish();
}

void BackendReply::failUnsent(const QString &code, const QString &message)
{
    if (m_finished)
        return;
    // Before fail(), so a handler that runs inside it already reads true.
    m_neverSent = true;
    fail(code, message);
}

void BackendReply::failTransport(const QString &code, const QString &message)
{
    if (m_finished)
        return;
    m_transportFailed = true;
    fail(code, message);
}

void BackendReply::forwardFailure(const BackendReply &source, bool actionNeverSent)
{
    if (m_finished)
        return;
    m_neverSent = actionNeverSent || source.m_neverSent;
    m_transportFailed = !m_neverSent && source.m_transportFailed;
    fail(source.m_errorCode, source.m_errorMessage, source.m_errorDetails, source.m_errorRef);
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

BackendReply *IBackend::requestWithId(const QString &messageType, const QJsonObject &payload,
                                      const QString &requestId)
{
    Q_UNUSED(requestId)
    return request(messageType, payload);
}

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

BackendReply *IBackend::selectGameProcess(int processId, const QString &selectionToken)
{
    return request(QStringLiteral("SelectGameProcess"),
                   {{QStringLiteral("process_id"), processId},
                    {QStringLiteral("selection_token"), selectionToken}});
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

BackendReply *IBackend::discardCalibration(bool retireLocalProfile, bool restoreLocalProfile)
{
    // Omitted rather than sent as false: the contract's default is false, and an empty
    // payload is what every Collector since the message existed understands. Never both -
    // the two are opposites and the Collector answers ERR_BAD_REQUEST.
    QJsonObject payload;
    if (retireLocalProfile)
        payload.insert(QStringLiteral("retire_local_profile"), true);
    else if (restoreLocalProfile)
        payload.insert(QStringLiteral("restore_local_profile"), true);
    return request(QStringLiteral("DiscardCalibration"), payload);
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

BackendReply *IBackend::queryRunCalendarDays(const QString &month, const QJsonObject &filter)
{
    return request(QStringLiteral("QueryRunCalendarDays"),
                   {{QStringLiteral("month"), month}, {QStringLiteral("filter"), filter}});
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

BackendReply *IBackend::getRunRevisions(const QString &runId, int page, int pageSize)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    if (page > 0)
        payload.insert(QStringLiteral("page"), page);
    if (pageSize > 0)
        payload.insert(QStringLiteral("page_size"), pageSize);
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

QJsonObject IBackend::candidateRoundAnswersPayload(const QVariantMap &answers)
{
    constexpr int kMaxAnswerLength = 100; // $defs/CandidateRoundAnswers maxLength
    QJsonObject object;
    for (const char *key : {"first_roulette", "job_before", "job_after", "duty"}) {
        QString text = answers.value(QLatin1String(key)).toString().trimmed();
        if (text.size() > kMaxAnswerLength)
            text = text.left(text.at(kMaxAnswerLength - 1).isHighSurrogate() ? kMaxAnswerLength - 1
                                                                            : kMaxAnswerLength).trimmed();
        if (!text.isEmpty())
            object.insert(QLatin1String(key), text);
    }
    return object;
}

BackendReply *IBackend::exportCandidateEvidence(const QString &targetPath, const QVariantMap &roundAnswers,
                                                 const QString &roundId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("target_path"),
                   targetPath.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(targetPath));
    // Left out rather than sent empty or null: a request without answers stays the one an
    // older Collector already reads.
    const QJsonObject answers = candidateRoundAnswersPayload(roundAnswers);
    if (!answers.isEmpty())
        payload.insert(QStringLiteral("round_answers"), answers);
    if (!roundId.isEmpty())
        payload.insert(QStringLiteral("candidate_round_id"), roundId);
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

BackendReply *IBackend::createManualRun(const QJsonObject &run, const QString &reason,
                                       const QString &requestId)
{
    QJsonObject payload = run;
    payload.insert(QStringLiteral("reason"), reason);
    return requestWithId(QStringLiteral("CreateManualRun"), payload, requestId);
}

BackendReply *IBackend::correctRun(const QString &runId, int expectedRevision,
                                   const QJsonObject &changes, const QString &reason,
                                   const QString &requestId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_id"), runId);
    payload.insert(QStringLiteral("expected_revision"), expectedRevision);
    payload.insert(QStringLiteral("changes"), changes);
    payload.insert(QStringLiteral("reason"), reason);
    return requestWithId(QStringLiteral("CorrectRun"), payload, requestId);
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
                                                  const QString &baselineEffectiveAtUtc,
                                                  const QString &region, int profileId)
{
    QJsonObject payload;
    if (!region.isEmpty()) payload.insert(QStringLiteral("region"), region);
    // Omitted rather than 0: the contract's minimum is 1, and an absent field is the
    // region's current profile, which is what every older caller means.
    if (profileId > 0) payload.insert(QStringLiteral("profile_id"), profileId);
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

BackendReply *IBackend::createCharacterProfile(const QString &region, const QString &displayName,
                                               int goalCount, int baselineCompletedCount,
                                               bool makeCurrent, const QString &requestId,
                                               int identityId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("region"), region);
    payload.insert(QStringLiteral("display_name"), displayName);
    payload.insert(QStringLiteral("goal_count"), goalCount);
    payload.insert(QStringLiteral("baseline_completed_count"), baselineCompletedCount);
    if (makeCurrent)
        payload.insert(QStringLiteral("make_current"), true);
    // Omitted rather than 0: the contract's minimum is 1, and an older Collector never sees it.
    if (identityId > 0)
        payload.insert(QStringLiteral("identity_id"), identityId);
    return requestWithId(QStringLiteral("CreateCharacterProfile"), payload, requestId);
}

BackendReply *IBackend::updateCharacterProfile(int profileId, const QString &displayName,
                                               bool makeCurrent, const QString &requestId)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("profile_id"), profileId);
    // A null name keeps the stored one; an empty one is sent and refused by the Collector.
    if (!displayName.isNull())
        payload.insert(QStringLiteral("display_name"), displayName);
    if (makeCurrent)
        payload.insert(QStringLiteral("make_current"), true);
    return requestWithId(QStringLiteral("UpdateCharacterProfile"), payload, requestId);
}

BackendReply *IBackend::deleteCharacterProfile(int profileId, const QString &requestId)
{
    return requestWithId(QStringLiteral("DeleteCharacterProfile"),
                         QJsonObject{{QStringLiteral("profile_id"), profileId}}, requestId);
}

BackendReply *IBackend::forgetCharacterIdentities(int profileId, const QString &requestId)
{
    return requestWithId(QStringLiteral("UpdateCharacterProfile"),
                         QJsonObject{{QStringLiteral("profile_id"), profileId},
                                     {QStringLiteral("forget_identities"), true}},
                         requestId);
}

BackendReply *IBackend::resolveCharacterIdentity(int identityId, const QString &action, int profileId,
                                                 const QString &requestId, const QString &displayName)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("identity_id"), identityId);
    payload.insert(QStringLiteral("action"), action);
    if (profileId > 0)
        payload.insert(QStringLiteral("profile_id"), profileId);
    if (!displayName.isNull())
        payload.insert(QStringLiteral("display_name"), displayName);
    return requestWithId(QStringLiteral("ResolveCharacterIdentity"), payload, requestId);
}

IBackend::StagedHistoryRequest IBackend::prepareHistorySelection(QJsonObject payload, const QJsonArray &targets)
{
    Q_ASSERT(targets.size() > kInlineHistorySelectionLimit);
    auto canonical = historyTargets(targets);
    payload.remove(QStringLiteral("runs"));
    payload.insert(QStringLiteral("selection_id"), historySelectionDigest(canonical));
    return {std::move(payload), std::move(canonical)};
}

BackendReply *IBackend::requestWithHistorySelection(const QString &messageType, StagedHistoryRequest request,
                                                    const QString &requestId)
{
    const QString selectionId = request.payload.value(QStringLiteral("selection_id")).toString();
    auto *reply = new BackendReply(requestId.isEmpty() ? ipc::newRequestId() : requestId, messageType, this);
    ++m_historySelectionUsers[selectionId];
    // Two actions can stage the same hash concurrently. Leave it available until
    // the final user no longer needs it, even when one finishes or fails first.
    connect(reply, &BackendReply::done, this, [this, selectionId] {
        auto users = m_historySelectionUsers.find(selectionId);
        if (--users.value() == 0) {
            m_historySelectionUsers.erase(users);
            releaseHistorySelection(selectionId);
        }
    });
    new HistorySelectionRequest(this, reply, std::move(request.payload), std::move(request.targets), selectionId);
    return reply;
}

QString IBackend::historySelectionKey(const QJsonArray &targets)
{
    return historySelectionDigest(historyTargets(targets));
}

void IBackend::releaseHistorySelection(const QString &selectionId)
{
    if (!selectionId.isEmpty() && isConnected())
        request(QStringLiteral("QueryHistorySelection"),
                {{QStringLiteral("selection_id"), selectionId}, {QStringLiteral("release"), true}});
}

QJsonObject IBackend::batchMutationPayload(const QString &action, const QJsonArray &runs,
                                            const QString &reason, int profileId,
                                            QJsonObject migration)
{
    QJsonObject payload = std::move(migration);
    payload.insert(QStringLiteral("action"), action);
    payload.insert(QStringLiteral("runs"), runs);
    payload.insert(QStringLiteral("reason"), reason.trimmed());
    if (action == QLatin1String("assign_profile"))
        payload.insert(QStringLiteral("profile_id"),
                       profileId > 0 ? QJsonValue(profileId) : QJsonValue(QJsonValue::Null));
    return payload;
}

BackendReply *IBackend::getHistoryRetentionSettings()
{
    return request(QStringLiteral("GetHistoryRetentionSettings"));
}

BackendReply *IBackend::updateHistoryRetentionSettings(int retentionDays)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("retention_days"), retentionDays);
    return request(QStringLiteral("UpdateHistoryRetentionSettings"), payload);
}

BackendReply *IBackend::getPendingImageCleanup()
{
    return request(QStringLiteral("GetPendingImageCleanup"));
}

BackendReply *IBackend::acknowledgeImageCleanup(const QStringList &runIds)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("run_ids"), QJsonArray::fromStringList(runIds));
    return request(QStringLiteral("AcknowledgeImageCleanup"), payload);
}

QJsonObject IBackend::previewRunImportPayload(const RunImportSource &source)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("source_kind"), source.kind);
    payload.insert(QStringLiteral("source_name"), source.name);
    payload.insert(QStringLiteral("time_zone"), source.timeZone);
    if (source.pageSize > 0)
        payload.insert(QStringLiteral("page_size"), source.pageSize);
    if (!source.rows.isEmpty())
        payload.insert(QStringLiteral("rows"), source.rows);
    else if (!source.filePath.isEmpty())
        payload.insert(QStringLiteral("file_path"), source.filePath);
    else if (!source.text.isEmpty())
        payload.insert(QStringLiteral("text"), source.text);
    if (!source.columnMapping.isEmpty())
        payload.insert(QStringLiteral("column_mapping"), source.columnMapping);
    return payload;
}

BackendReply *IBackend::previewRunImport(const RunImportSource &source)
{
    return request(QStringLiteral("PreviewRunImport"), previewRunImportPayload(source));
}

BackendReply *IBackend::previewRunImportPage(const QString &previewId, int page)
{
    QJsonObject payload;
    payload.insert(QStringLiteral("preview_id"), previewId);
    payload.insert(QStringLiteral("page"), page);
    return request(QStringLiteral("PreviewRunImport"), payload);
}

BackendReply *IBackend::commitRunImport(const QString &previewId, const QList<int> &rowNumbers,
                                        bool deductFromBaseline, const QString &requestId,
                                        int profileId)
{
    QJsonArray numbers;
    for (int number : rowNumbers)
        numbers.append(number);
    QJsonObject payload;
    payload.insert(QStringLiteral("preview_id"), previewId);
    payload.insert(QStringLiteral("row_numbers"), numbers);
    payload.insert(QStringLiteral("confirm_own_records"), true);
    if (deductFromBaseline)
        payload.insert(QStringLiteral("deduct_from_baseline"), true);
    if (profileId > 0)
        payload.insert(QStringLiteral("profile_id"), profileId);
    return requestWithId(QStringLiteral("CommitRunImport"), payload, requestId);
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

BackendReply *IBackend::exportCsv(const QString &targetPath, const QJsonObject &filter, bool replace,
                                  const QJsonObject &sort, const QJsonArray &largeTargets)
{
    return exportRuns(QStringLiteral("ExportCsv"), targetPath, filter, replace, sort, largeTargets);
}

BackendReply *IBackend::exportJson(const QString &targetPath, const QJsonObject &filter, bool replace,
                                   const QJsonObject &sort, const QJsonArray &largeTargets)
{
    return exportRuns(QStringLiteral("ExportJson"), targetPath, filter, replace, sort, largeTargets);
}

BackendReply *IBackend::exportRuns(const QString &messageType, const QString &targetPath,
                                   const QJsonObject &filter, bool replace, const QJsonObject &sort,
                                   const QJsonArray &largeTargets)
{
    QJsonObject payload{{QStringLiteral("target_path"), targetPath}};
    if (replace)
        payload.insert(QStringLiteral("overwrite"), true);
    if (!sort.isEmpty())
        payload.insert(QStringLiteral("sort"), sort);
    if (!largeTargets.isEmpty())
        return requestWithHistorySelection(messageType, prepareHistorySelection(std::move(payload), largeTargets));
    payload.insert(QStringLiteral("filter"), filter);
    return request(messageType, payload);
}

BackendReply *IBackend::backupDatabase(const QString &targetPath)
{
    QJsonObject payload;
    if (!targetPath.isEmpty())
        payload.insert(QStringLiteral("target_path"), targetPath);
    return request(QStringLiteral("BackupDatabase"), payload);
}

BackendReply *IBackend::exportDiagnosticsReport(const QString &targetPath, bool replace)
{
    QJsonObject payload;
    if (!targetPath.isEmpty())
        payload.insert(QStringLiteral("target_path"), targetPath);
    if (replace)
        payload.insert(QStringLiteral("overwrite"), true);
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

BackendReply *IBackend::checkUpdateNow()
{
    return request(QStringLiteral("CheckUpdateNow"));
}

BackendReply *IBackend::startUpdateDownload(bool reinstall)
{
    // Omitted rather than sent as false: the contract's default is false, as
    // DiscardCalibration's flags are.
    QJsonObject payload;
    if (reinstall)
        payload.insert(QStringLiteral("reinstall"), true);
    return request(QStringLiteral("StartUpdateDownload"), payload);
}

BackendReply *IBackend::cancelUpdateDownload()
{
    return request(QStringLiteral("CancelUpdateDownload"));
}

BackendReply *IBackend::setDisplayTimeZone(const QString &ianaId)
{
    return request(QStringLiteral("SetDisplayTimeZone"), {
        { QStringLiteral("time_zone"), ianaId.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(ianaId) },
    });
}

BackendReply *IBackend::resolveUpdateMirrorOffer(const QString &offerId, bool useGitCode)
{
    return request(QStringLiteral("ResolveUpdateMirrorOffer"), {
        { QStringLiteral("offer_id"), offerId },
        { QStringLiteral("use_gitcode"), useGitCode },
    });
}

} // namespace mr
