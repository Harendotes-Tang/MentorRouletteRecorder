#include "IpcBackend.h"

#include "IpcFraming.h"

#include <QJsonArray>
#include <QJsonValue>

namespace mr {
namespace {

// ---------------------------------------------------------------------------
// A backend that answers nothing and only records what it was asked to send.
// It is what makes buildRequestForTest honest: the samples come out of the very
// same IBackend wrappers the UI calls, not out of a second copy of them.
// ---------------------------------------------------------------------------
class CapturingBackend final : public IBackend
{
public:
    QString backendName() const override { return QStringLiteral("capture"); }
    bool isConnected() const override { return false; }

    BackendReply *request(const QString &messageType, const QJsonObject &payload) override
    {
        m_messageType = messageType;
        m_payload = payload;
        // Nothing ever completes this reply; it is deleted with this object.
        return new BackendReply(QStringLiteral("unused"), messageType, this);
    }

    QString messageType() const { return m_messageType; }
    QJsonObject payload() const { return m_payload; }

private:
    QString m_messageType;
    QJsonObject m_payload;
};

QJsonObject objectArg(const QJsonObject &args, const char *name)
{
    return args.value(QLatin1String(name)).toObject();
}

QString stringArg(const QJsonObject &args, const char *name)
{
    return args.value(QLatin1String(name)).toString();
}

int intArg(const QJsonObject &args, const char *name, int fallback)
{
    const QJsonValue value = args.value(QLatin1String(name));
    return value.isDouble() ? value.toInt() : fallback;
}

} // namespace

IpcBackend::IpcBackend(QObject *parent, const QString &serverName)
    : IBackend(parent)
    , m_client(new IpcClient(this))
{
    if (!serverName.isEmpty())
        m_client->setServerName(serverName);
    connect(m_client, &IpcClient::connectionChanged,
            this, &IBackend::connectionChanged);
    connect(m_client, &IpcClient::eventReceived, this,
            [this](const QJsonObject &event) { Q_EMIT liveEvent(event.toVariantMap()); });
    m_client->start();
}

bool IpcBackend::isConnected() const
{
    return m_client->isConnected();
}

QString IpcBackend::connectionDetail() const
{
    if (m_client->isConnected())
        return m_client->serverName();
    const QString error = m_client->lastError();
    return error.isEmpty()
               ? QString::fromUtf8("Collector 未连接 · %1").arg(m_client->serverName())
               : QString::fromUtf8("Collector 未连接 · %1").arg(error);
}

BackendReply *IpcBackend::request(const QString &messageType, const QJsonObject &payload)
{
    auto *reply = new BackendReply(ipc::newRequestId(), messageType, this);
    m_client->send(reply, messageType, payload);
    return reply;
}

const char *IpcBackend::testRequestId()
{
    return "11111111-2222-4333-8444-555555555555";
}

QJsonObject IpcBackend::buildRequestForTest(const QString &messageType,
                                            const QJsonObject &args)
{
    CapturingBackend backend;

    if (messageType == QLatin1String("GetVersion"))
        backend.getVersion();
    else if (messageType == QLatin1String("GetStatus"))
        backend.getStatus();
    else if (messageType == QLatin1String("GetCaptureStatus"))
        backend.getCaptureStatus();
    else if (messageType == QLatin1String("StartCaptureValidation"))
        backend.startCaptureValidation(stringArg(args, "adapter_id"));
    else if (messageType == QLatin1String("GetCaptureValidationStatus"))
        backend.getCaptureValidationStatus();
    else if (messageType == QLatin1String("AddCaptureValidationMarker"))
        backend.addCaptureValidationMarker(stringArg(args, "marker"));
    else if (messageType == QLatin1String("StopCaptureValidation"))
        backend.stopCaptureValidation();
    else if (messageType == QLatin1String("ConfirmCalibration"))
        backend.confirmCalibration(args.value(QLatin1String("verdicts")).toArray());
    else if (messageType == QLatin1String("DiscardCalibration"))
        backend.discardCalibration();
    else if (messageType == QLatin1String("GetCalibrationShareCode"))
        backend.getCalibrationShareCode();
    else if (messageType == QLatin1String("CheckSharedCalibration"))
        backend.checkSharedCalibration();
    else if (messageType == QLatin1String("ImportCalibrationCode"))
        backend.importCalibrationCode(stringArg(args, "code"));
    else if (messageType == QLatin1String("AcceptSharedQueueInference"))
        backend.acceptSharedQueueInference();
    else if (messageType == QLatin1String("RejectSharedCalibration"))
        backend.rejectSharedCalibration();
    else if (messageType == QLatin1String("GetProtocolProfileStatus"))
        backend.getProtocolProfileStatus();
    else if (messageType == QLatin1String("GetCaptureSettings"))
        backend.getCaptureSettings();
    else if (messageType == QLatin1String("UpdateCaptureSettings"))
        backend.updateCaptureSettings(objectArg(args, "changes"));
    else if (messageType == QLatin1String("ListCaptureAdapters"))
        backend.listCaptureAdapters();
    else if (messageType == QLatin1String("StartCapture"))
        backend.startCapture(stringArg(args, "adapter_id"));
    else if (messageType == QLatin1String("StopCapture"))
        backend.stopCapture();
    else if (messageType == QLatin1String("GetCurrentRun"))
        backend.getCurrentRun();
    else if (messageType == QLatin1String("SubscribeLiveEvents"))
        backend.subscribeLiveEvents();
    else if (messageType == QLatin1String("QueryRuns"))
        backend.queryRuns(objectArg(args, "filter"), intArg(args, "page", 1),
                          intArg(args, "page_size", 50), objectArg(args, "sort"));
    else if (messageType == QLatin1String("GetRunRevisions"))
        backend.getRunRevisions(stringArg(args, "run_id"));
    else if (messageType == QLatin1String("GetRunEvents"))
        backend.getRunEvents(stringArg(args, "run_id"));
    else if (messageType == QLatin1String("QueryCandidateObservations"))
        backend.queryCandidateObservations(stringArg(args, "session_id"),
                                             stringArg(args, "from_utc"),
                                             stringArg(args, "to_utc"),
                                             intArg(args, "page", 1),
                                             intArg(args, "page_size", 50));
    else if (messageType == QLatin1String("ReviewCandidateObservation"))
        backend.reviewCandidateObservation(stringArg(args, "observation_id"),
                                             stringArg(args, "verdict"),
                                             stringArg(args, "note"));
    else if (messageType == QLatin1String("ExportCandidateEvidence"))
        backend.exportCandidateEvidence(stringArg(args, "target_path"));
    else if (messageType == QLatin1String("GetDashboardStats"))
        backend.getDashboardStats(objectArg(args, "filter"),
                                  stringArg(args, "trend_granularity"));
    else if (messageType == QLatin1String("GetDungeonStats"))
        backend.getDungeonStats(objectArg(args, "filter"), intArg(args, "page", 1),
                                intArg(args, "page_size", 200));
    else if (messageType == QLatin1String("GetJobStats"))
        backend.getJobStats(objectArg(args, "filter"), intArg(args, "page", 1),
                            intArg(args, "page_size", 200));
    else if (messageType == QLatin1String("GetResultStats"))
        backend.getResultStats(objectArg(args, "filter"));
    else if (messageType == QLatin1String("CreateManualRun"))
        backend.createManualRun(objectArg(args, "run"), stringArg(args, "reason"));
    else if (messageType == QLatin1String("CorrectRun"))
        backend.correctRun(stringArg(args, "run_id"), intArg(args, "expected_revision", 1),
                           objectArg(args, "changes"), stringArg(args, "reason"));
    else if (messageType == QLatin1String("SoftDeleteRun"))
        backend.softDeleteRun(stringArg(args, "run_id"),
                              intArg(args, "expected_revision", 1), stringArg(args, "reason"));
    else if (messageType == QLatin1String("RestoreRun"))
        backend.restoreRun(stringArg(args, "run_id"),
                           intArg(args, "expected_revision", 1), stringArg(args, "reason"));
    else if (messageType == QLatin1String("UndoRevision"))
        backend.undoRevision(stringArg(args, "run_id"),
                             intArg(args, "expected_revision", 1),
                             stringArg(args, "reason"));
    else if (messageType == QLatin1String("UpdateAchievementBaseline"))
        backend.updateAchievementBaseline(intArg(args, "goal_count", 2000),
                                          intArg(args, "baseline_completed_count", 0),
                                          stringArg(args, "reason"),
                                          stringArg(args, "baseline_effective_at"));
    else if (messageType == QLatin1String("SetRunReflection"))
        backend.setRunReflection(stringArg(args, "run_id"), stringArg(args, "mood"),
                                 stringArg(args, "text"));
    else if (messageType == QLatin1String("GetReflectionSummary"))
        backend.getReflectionSummary(intArg(args, "recent_limit", 3));
    else if (messageType == QLatin1String("ExportCsv"))
        backend.exportCsv(stringArg(args, "target_path"), objectArg(args, "filter"));
    else if (messageType == QLatin1String("ExportJson"))
        backend.exportJson(stringArg(args, "target_path"), objectArg(args, "filter"));
    else if (messageType == QLatin1String("BackupDatabase"))
        backend.backupDatabase(stringArg(args, "target_path"));
    else if (messageType == QLatin1String("ExportDiagnosticsReport"))
        backend.exportDiagnosticsReport(stringArg(args, "target_path"));
    else if (messageType == QLatin1String("CheckDatabaseIntegrity"))
        backend.checkDatabaseIntegrity();
    else if (messageType == QLatin1String("GetSpeechSettings"))
        backend.getSpeechSettings();
    else if (messageType == QLatin1String("UpdateSpeechSettings"))
        backend.updateSpeechSettings(objectArg(args, "fields").toVariantMap());
    else if (messageType == QLatin1String("SynthesizeSpeech"))
        backend.synthesizeSpeech(stringArg(args, "text"), intArg(args, "rate_percent", 100),
                                 args.value(QLatin1String("test")).toBool(false));
    else if (messageType == QLatin1String("CheckUpdateNow"))
        backend.checkUpdateNow();
    else
        return {};

    return ipc::makeRequest(QString::fromLatin1(testRequestId()),
                            backend.messageType(), backend.payload());
}

} // namespace mr
