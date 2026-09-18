// ---------------------------------------------------------------------------
// tst_ipcrequests - the request side of the IPC contract.
//
// For every message the Desktop can send, this test builds the request envelope
// through the shipping IBackend wrappers (IpcBackend::buildRequestForTest),
// writes it under the build directory, and compares it with the committed
// sample in tests/Fixtures/ipc-requests/.
//
// Those same committed samples are validated against
// contracts/ipc-v1.schema.json by the Collector-side
// ContractRequestSampleTests, so a field this client renames, drops or adds
// fails here first and in the Collector suite second. Regenerate the samples
// deliberately, by copying the files this test writes, and read the diff.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "IpcBackend.h"
#include "IpcFraming.h"

#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QTest>

namespace {

constexpr auto kRunId = "9f1d3f2a-6b47-4a01-9a52-8c0d7f1e4b33";
constexpr auto kStamp = "2026-09-04T12:00:00.000Z";

QJsonObject sampleFilter()
{
    QJsonObject filter;
    filter.insert(QStringLiteral("from_utc"), QStringLiteral("2026-08-01T00:00:00.000Z"));
    filter.insert(QStringLiteral("to_utc"), QStringLiteral("2026-09-01T00:00:00.000Z"));
    filter.insert(QStringLiteral("date_field"), QStringLiteral("entered_at_utc"));
    filter.insert(QStringLiteral("include_deleted"), false);
    return filter;
}

/// Wrapper arguments per message type. Empty for the messages that take none.
QJsonObject argsFor(const QString &messageType)
{
    QJsonObject args;

    if (messageType == QLatin1String("StartCapture")
        || messageType == QLatin1String("StartCaptureValidation")) {
        args.insert(QStringLiteral("adapter_id"), QStringLiteral("NPF_TEST_ADAPTER"));
    } else if (messageType == QLatin1String("AddCaptureValidationMarker")) {
        args.insert(QStringLiteral("marker"), QStringLiteral("queued"));
    } else if (messageType == QLatin1String("QueryRuns")) {
        QJsonObject sort;
        sort.insert(QStringLiteral("field"), QStringLiteral("entered_at_utc"));
        sort.insert(QStringLiteral("direction"), QStringLiteral("desc"));
        args.insert(QStringLiteral("filter"), sampleFilter());
        args.insert(QStringLiteral("sort"), sort);
        args.insert(QStringLiteral("page"), 2);
        args.insert(QStringLiteral("page_size"), 50);
    } else if (messageType == QLatin1String("GetRunRevisions")
               || messageType == QLatin1String("GetRunEvents")) {
        args.insert(QStringLiteral("run_id"), QLatin1String(kRunId));
    } else if (messageType == QLatin1String("UpdateCaptureSettings")) {
        // Every optional key at once, so the sample pins the whole shape of
        // $defs/UpdateCaptureSettingsRequest rather than one lucky subset.
        QJsonObject changes;
        changes.insert(QStringLiteral("follow_game"), true);
        changes.insert(QStringLiteral("autostart"), false);
        changes.insert(QStringLiteral("adapter_id"), QJsonValue::Null);
        changes.insert(QStringLiteral("log_retention_days"), 7);
        changes.insert(QStringLiteral("allow_without_profile"), false);
        changes.insert(QStringLiteral("candidate_validation_enabled"), false);
        changes.insert(QStringLiteral("research_payload_opcodes"), QJsonArray());
        changes.insert(QStringLiteral("region_override"), QJsonValue::Null);
        changes.insert(QStringLiteral("update_check_enabled"), true);
        args.insert(QStringLiteral("changes"), changes);
    } else if (messageType == QLatin1String("ReviewCandidateObservation")) {
        args.insert(QStringLiteral("observation_id"),
                    QStringLiteral("22222222-2222-4222-8222-222222222222"));
        args.insert(QStringLiteral("verdict"), QStringLiteral("CORRECT"));
        args.insert(QStringLiteral("note"), QString::fromUtf8("与实际弹窗一致"));
    } else if (messageType == QLatin1String("GetDashboardStats")) {
        args.insert(QStringLiteral("filter"), sampleFilter());
        args.insert(QStringLiteral("trend_granularity"), QStringLiteral("week"));
    } else if (messageType == QLatin1String("GetResultStats")) {
        args.insert(QStringLiteral("filter"), sampleFilter());
    } else if (messageType == QLatin1String("GetDungeonStats") ||
               messageType == QLatin1String("GetJobStats")) {
        args.insert(QStringLiteral("filter"), sampleFilter());
        args.insert(QStringLiteral("page"), 1);
        args.insert(QStringLiteral("page_size"), 200);
    } else if (messageType == QLatin1String("CreateManualRun")) {
        QJsonObject run;
        run.insert(QStringLiteral("content_id"), 900001);
        run.insert(QStringLiteral("duty_name"), QString::fromUtf8("\u77f3\u536b\u5854"));
        run.insert(QStringLiteral("duty_category"), QString::fromUtf8("\u526f\u672c"));
        run.insert(QStringLiteral("job_id"), 19);
        run.insert(QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T11:58:00.000Z"));
        run.insert(QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T12:00:00.000Z"));
        run.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:26:00.000Z"));
        run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        run.insert(QStringLiteral("contributes_to_goal"), true);
        run.insert(QStringLiteral("note"),
                   QString::fromUtf8("\u7a0b\u5e8f\u672a\u8fd0\u884c\u65f6\u8865\u5f55"));
        args.insert(QStringLiteral("run"), run);
        args.insert(QStringLiteral("reason"), QString::fromUtf8("\u624b\u5de5\u8865\u5f55"));
    } else if (messageType == QLatin1String("CorrectRun")) {
        QJsonObject changes;
        changes.insert(QStringLiteral("result"), QStringLiteral("LEFT_OR_ABANDONED"));
        changes.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:20:00.000Z"));
        changes.insert(QStringLiteral("note"),
                       QString::fromUtf8("\u961f\u53cb\u622a\u56fe\u786e\u8ba4"));
        args.insert(QStringLiteral("run_id"), QLatin1String(kRunId));
        args.insert(QStringLiteral("expected_revision"), 3);
        args.insert(QStringLiteral("changes"), changes);
        args.insert(QStringLiteral("reason"), QString::fromUtf8("\u7ed3\u679c\u8bef\u5224"));
    } else if (messageType == QLatin1String("SoftDeleteRun") ||
               messageType == QLatin1String("RestoreRun")) {
        args.insert(QStringLiteral("run_id"), QLatin1String(kRunId));
        args.insert(QStringLiteral("expected_revision"), 4);
        args.insert(QStringLiteral("reason"), QString::fromUtf8("\u91cd\u590d\u8bb0\u5f55"));
    } else if (messageType == QLatin1String("UndoRevision")) {
        args.insert(QStringLiteral("run_id"), QLatin1String(kRunId));
        args.insert(QStringLiteral("expected_revision"), 4);
        args.insert(QStringLiteral("reason"),
                    QString::fromUtf8("\u64a4\u9500\u4e0a\u4e00\u6b21\u4fee\u6b63"));
    } else if (messageType == QLatin1String("UpdateAchievementBaseline")) {
        args.insert(QStringLiteral("goal_count"), 2000);
        args.insert(QStringLiteral("baseline_completed_count"), 137);
        args.insert(QStringLiteral("baseline_effective_at"), QLatin1String(kStamp));
        args.insert(QStringLiteral("reason"),
                    QString::fromUtf8("\u9996\u6b21\u8bbe\u5b9a\u57fa\u7ebf"));
    } else if (messageType == QLatin1String("SetRunReflection")) {
        // 导随心得: no reason and no expected_revision - the diary entry is the
        // user's own text, not a correction of a captured fact.
        args.insert(QStringLiteral("run_id"),
                    QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c"));
        args.insert(QStringLiteral("mood"), QStringLiteral("good"));
        args.insert(QStringLiteral("text"),
                    QString::fromUtf8("新人坦克第一次打灯塔，全程语音提醒机制，"
                                      "最后一个 boss 一次过。"));
    } else if (messageType == QLatin1String("GetReflectionSummary")) {
        args.insert(QStringLiteral("recent_limit"), 3);
    } else if (messageType == QLatin1String("ExportCsv") ||
               messageType == QLatin1String("ExportJson")) {
        args.insert(QStringLiteral("target_path"), QStringLiteral("D:/exports/mentor.out"));
        args.insert(QStringLiteral("filter"), sampleFilter());
    } else if (messageType == QLatin1String("BackupDatabase")) {
        args.insert(QStringLiteral("target_path"), QStringLiteral("D:/backups/mentor.db"));
    } else if (messageType == QLatin1String("ExportDiagnosticsReport")) {
        // Deliberately no target_path: the sample pins the shape the 导出 button
        // sends when the user lets the Collector choose its managed folder.
    } else if (messageType == QLatin1String("CheckDatabaseIntegrity")) {
        // No arguments: $defs/EmptyPayload. The committed sample was written on
        // the Collector side first (tests/Fixtures/README.md); this wrapper must
        // produce exactly that payload.
    } else if (messageType == QLatin1String("CheckUpdateNow")) {
        // No arguments: $defs/EmptyPayload. The committed sample was written on
        // the Collector side first (tests/Fixtures/README.md); this wrapper must
        // produce exactly that payload.
    } else if (messageType == QLatin1String("GetSpeechSettings")) {
        // {}: the Collector side wrote this sample first (tests/Fixtures/README.md).
    } else if (messageType == QLatin1String("UpdateSpeechSettings")) {
        // All six optional fields at once, as in the hand-written sample: an
        // obvious fake key and a documentation host, never a real service.
        args.insert(QStringLiteral("fields"), QJsonObject{
            {QStringLiteral("provider"), QStringLiteral("openai_compatible")},
            {QStringLiteral("azure_region"), QStringLiteral("eastasia")},
            {QStringLiteral("openai_base_url"), QStringLiteral("https://api.example.com/v1")},
            {QStringLiteral("openai_model"), QStringLiteral("gpt-4o-mini-tts")},
            {QStringLiteral("voice"), QStringLiteral("alloy")},
            {QStringLiteral("api_key"), QStringLiteral("test-key-0000")},
            // Not a contract field: the wrapper must drop it.
            {QStringLiteral("unknown_field"), QStringLiteral("dropped")}});
    } else if (messageType == QLatin1String("SynthesizeSpeech")) {
        args.insert(QStringLiteral("text"),
                    QString::fromUtf8("匹配成功：导随任务，距离目标还差 12 次"));
        args.insert(QStringLiteral("rate_percent"), 100);
        args.insert(QStringLiteral("test"), false);
    } else if (messageType == QLatin1String("ConfirmCalibration")) {
        // The four events of the calibration confirmation timeline,
        // in timeline order. The login row of that timeline is not here: only
        // events with requires_confirmation are ever ruled on.
        QJsonArray verdicts;
        for (const char *eventId : {"finder_request-60000", "pop-120000",
                                    "duty_enter-124000", "duty_exit-214000"}) {
            verdicts.append(QJsonObject{
                {QStringLiteral("event_id"), QLatin1String(eventId)},
                {QStringLiteral("verdict"), QStringLiteral("CORRECT")}});
        }
        args.insert(QStringLiteral("verdicts"), verdicts);
    } else if (messageType == QLatin1String("ImportCalibrationCode")) {
        // tests/Fixtures/shared-calibration/vectors.json valid[0].code: the
        // sample the Collector side wrote first (contracts/CHANGELOG.md).
        args.insert(QStringLiteral("code"),
                    QStringLiteral("MRC1.XY_LasMwEEX_ZdbG6G3LuxKyKyG02ZRShCSPExXbMrIdaEP-vWoKpeksZjH3wZkLHO2Axq2hb6EBRpgqiS4JLUme24IC3qMzcfKxRWgEZUwWMNjFn8wc1-TzEZ62-8cX83x4OGyzf4oTNBf4jWgqRQEz9uiXmMzZ9ivO0Lzyt2sBCY8hjrljs8vRBYeptwuaKcUu9GjCN5cfyx-0uiTyr2s-WSZVdvBOeYrEaVu1zAuUXU00tcxxL1qJqqtITTWz3AkvW4XVP_1WmlLIfB93v2bwMzS0gM844p3Cr18"));
    }

    return args;
}

QStringList allMessageTypes()
{
    return {
        QStringLiteral("GetVersion"),
        QStringLiteral("GetStatus"),
        QStringLiteral("GetCaptureStatus"),
        QStringLiteral("GetProtocolProfileStatus"),
        QStringLiteral("GetCaptureSettings"),
        QStringLiteral("UpdateCaptureSettings"),
        QStringLiteral("ListCaptureAdapters"),
        QStringLiteral("StartCapture"),
        QStringLiteral("StopCapture"),
        QStringLiteral("StartCaptureValidation"),
        QStringLiteral("GetCaptureValidationStatus"),
        QStringLiteral("AddCaptureValidationMarker"),
        QStringLiteral("StopCaptureValidation"),
        QStringLiteral("ConfirmCalibration"),
        QStringLiteral("DiscardCalibration"),
        QStringLiteral("GetCalibrationShareCode"),
        QStringLiteral("CheckSharedCalibration"),
        QStringLiteral("ImportCalibrationCode"),
        QStringLiteral("AcceptSharedQueueInference"),
        QStringLiteral("RejectSharedCalibration"),
        QStringLiteral("GetCurrentRun"),
        QStringLiteral("SubscribeLiveEvents"),
        QStringLiteral("QueryRuns"),
        QStringLiteral("GetRunRevisions"),
        QStringLiteral("GetRunEvents"),
        QStringLiteral("QueryCandidateObservations"),
        QStringLiteral("ReviewCandidateObservation"),
        QStringLiteral("ExportCandidateEvidence"),
        QStringLiteral("GetDashboardStats"),
        QStringLiteral("GetDungeonStats"),
        QStringLiteral("GetJobStats"),
        QStringLiteral("GetResultStats"),
        QStringLiteral("CreateManualRun"),
        QStringLiteral("CorrectRun"),
        QStringLiteral("SoftDeleteRun"),
        QStringLiteral("RestoreRun"),
        QStringLiteral("UndoRevision"),
        QStringLiteral("UpdateAchievementBaseline"),
        QStringLiteral("SetRunReflection"),
        QStringLiteral("GetReflectionSummary"),
        QStringLiteral("ExportCsv"),
        QStringLiteral("ExportJson"),
        QStringLiteral("BackupDatabase"),
        QStringLiteral("ExportDiagnosticsReport"),
        QStringLiteral("CheckDatabaseIntegrity"),
        QStringLiteral("GetSpeechSettings"),
        QStringLiteral("UpdateSpeechSettings"),
        QStringLiteral("SynthesizeSpeech"),
        QStringLiteral("CheckUpdateNow"),
    };
}

} // namespace

class IpcRequestTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void buildsOneSamplePerMessageType();
    void samplesMatchTheCommittedFixtures();
};

void IpcRequestTests::buildsOneSamplePerMessageType()
{
    QVERIFY(QDir().mkpath(QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR)));

    for (const QString &type : allMessageTypes()) {
        const QJsonObject envelope = mr::IpcBackend::buildRequestForTest(type, argsFor(type));
        QVERIFY2(!envelope.isEmpty(), qPrintable(type));
        QCOMPARE(envelope.value(QStringLiteral("message_type")).toString(), type);
        QCOMPARE(envelope.value(QStringLiteral("protocol_version")).toInt(),
                 mr::ipc::kProtocolVersion);
        QCOMPARE(envelope.value(QStringLiteral("request_id")).toString(),
                 QString::fromLatin1(mr::IpcBackend::testRequestId()));
        QVERIFY(envelope.value(QStringLiteral("payload")).isObject());

        QFile file(QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR) + QLatin1Char('/') + type +
                   QStringLiteral(".json"));
        QVERIFY2(file.open(QIODevice::WriteOnly | QIODevice::Truncate),
                 qPrintable(file.errorString()));
        file.write(QJsonDocument(envelope).toJson(QJsonDocument::Indented));
        file.close();
    }
}

void IpcRequestTests::samplesMatchTheCommittedFixtures()
{
    for (const QString &type : allMessageTypes()) {
        const QJsonObject envelope = mr::IpcBackend::buildRequestForTest(type, argsFor(type));

        QFile file(QStringLiteral(MR_IPC_REQUEST_FIXTURE_DIR) + QLatin1Char('/') + type +
                   QStringLiteral(".json"));
        QVERIFY2(file.open(QIODevice::ReadOnly),
                 qPrintable(file.fileName() + QStringLiteral(": ") + file.errorString()));
        QJsonParseError error{};
        const QJsonDocument committed = QJsonDocument::fromJson(file.readAll(), &error);
        file.close();
        QCOMPARE(error.error, QJsonParseError::NoError);

        // Compared field by field rather than as whole documents: the payload
        // and the two envelope fields this client decides are what the contract
        // pins. request_id is not one of them - a sample written on the
        // Collector side carries its own id, and this client's constant is
        // already asserted by buildsOneSamplePerMessageType above.
        const QJsonObject sample = committed.object();
        QCOMPARE(sample.value(QStringLiteral("message_type")),
                 envelope.value(QStringLiteral("message_type")));
        QCOMPARE(sample.value(QStringLiteral("protocol_version")),
                 envelope.value(QStringLiteral("protocol_version")));
        QVERIFY2(sample.value(QStringLiteral("payload")) == envelope.value(QStringLiteral("payload")),
                 qPrintable(type + QStringLiteral(" drifted from its committed sample; ") +
                            QStringLiteral("regenerate from ") +
                            QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR)));
    }
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    IpcRequestTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "IpcRequestTests.moc"
