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
//
// A message the Desktop sends in more than one shape (PreviewRunImport: a source,
// the next page of a frozen preview, the unpaged fallback) has its further shapes
// pinned the same way under variants/, named <message>.<shape>.json.
// ---------------------------------------------------------------------------

#include "DesktopTestRunner.h"
#include "SharedCalibrationTestSupport.h"
#include "TestCollectorGuard.h"
#include "TestNamedPipe.h"
#include "HistoryController.h"
#include "ImportRecordsController.h"
#include "IpcBackend.h"
#include "IpcFraming.h"

#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSignalSpy>
#include <QTest>
#include <QTimer>

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

/// The payload HistoryController itself sends as \a messageType when 迁移区服 is previewed
/// and confirmed for one checked manual run (defined below PayloadRecorder). IpcBackend
/// passes these two messages through unchanged, so the samples pin the real sender's
/// fields, not arguments this test made up (review 2026-11-20 I5-2).
QJsonObject recordedRegionMigrationPayload(const QString &messageType);

/// Wrapper arguments per message type. Empty for the messages that take none.
QJsonObject argsFor(const QString &messageType)
{
    QJsonObject args;

    if (messageType == QLatin1String("SelectGameProcess")) {
        args.insert(QStringLiteral("process_id"), 4321);
        args.insert(QStringLiteral("selection_token"), QStringLiteral("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"));
    } else if (messageType == QLatin1String("StartCapture")
        || messageType == QLatin1String("StartCaptureValidation")) {
        args.insert(QStringLiteral("adapter_id"), QStringLiteral("NPF_TEST_ADAPTER"));
    } else if (messageType == QLatin1String("AddCaptureValidationMarker")) {
        args.insert(QStringLiteral("marker"), QStringLiteral("queued"));
    } else if (messageType == QLatin1String("QueryRunCalendarDays")) {
        args.insert(QStringLiteral("month"), QStringLiteral("2026-09"));
        args.insert(QStringLiteral("filter"), sampleFilter());
    } else if (messageType == QLatin1String("QueryHistorySelection")) {
        args.insert(QStringLiteral("filter"), sampleFilter());
        args.insert(QStringLiteral("page"), 1);
    } else if (messageType == QLatin1String("StageHistorySelection")) {
        args.insert(QStringLiteral("selection_id"),
                    QStringLiteral("beb8b3308966d7324a7afe7a4c2709539ca899c920a0e2f20108f2ab3e390df6"));
        args.insert(QStringLiteral("chunk_index"), 0);
        args.insert(QStringLiteral("runs"), QJsonArray{QJsonObject{
            {QStringLiteral("run_id"), QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")},
            {QStringLiteral("expected_revision"), 1}}});
        args.insert(QStringLiteral("last"), true);
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
    } else if (messageType == QLatin1String("DutyCommunity")) {
        args.insert(QStringLiteral("action"), QStringLiteral("list"));
    } else if (messageType == QLatin1String("GetReflectionSummary")) {
        args.insert(QStringLiteral("recent_limit"), 3);
    } else if (messageType == QLatin1String("ExportCsv") ||
               messageType == QLatin1String("ExportJson")) {
        // The history list's sort travels with an export (contracts/CHANGELOG.md
        // 2026-11-03); the sample pins the shape, earliest first.
        QJsonObject sort;
        sort.insert(QStringLiteral("field"), QStringLiteral("history_date"));
        sort.insert(QStringLiteral("direction"), QStringLiteral("asc"));
        args.insert(QStringLiteral("target_path"), QStringLiteral("D:/exports/mentor.out"));
        args.insert(QStringLiteral("filter"), sampleFilter());
        args.insert(QStringLiteral("sort"), sort);
    } else if (messageType == QLatin1String("BackupDatabase")) {
        args.insert(QStringLiteral("target_path"), QStringLiteral("D:/backups/mentor.db"));
    } else if (messageType == QLatin1String("ExportDiagnosticsReport") ||
               messageType == QLatin1String("CheckDatabaseIntegrity") ||
               messageType == QLatin1String("CheckUpdateNow") ||
               messageType == QLatin1String("CancelUpdateDownload") ||
               messageType == QLatin1String("GetSpeechSettings")) {
        // These samples intentionally have no arguments. ExportDiagnosticsReport
        // deliberately omits target_path so the Collector chooses its managed
        // folder. The committed samples originate on the Collector side
        // (tests/Fixtures/README.md); this wrapper must preserve their payloads.
    } else if (messageType == QLatin1String("StartUpdateDownload")) {
        // The one optional field set, so the sample pins its name: the maintainer
        // tools' 重新下载最新正式版. 下载并安装 itself sends {} (the default false).
        args.insert(QStringLiteral("reinstall"), true);
    } else if (messageType == QLatin1String("ResolveUpdateMirrorOffer")) {
        args.insert(QStringLiteral("offer_id"), QStringLiteral("11111111-2222-4333-8444-666666666706"));
        args.insert(QStringLiteral("use_gitcode"), true);
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
    } else if (messageType == QLatin1String("GetCalibrationEvidence")) {
        args.insert(QStringLiteral("code_sha256"), QString(64, QLatin1Char('a')));
    } else if (messageType == QLatin1String("PreviewRunRegionMigration")) {
        args = recordedRegionMigrationPayload(messageType);
    } else if (messageType == QLatin1String("BatchMutateRuns")) {
        // The hand-written sample's values (tests/Fixtures/README.md), now built by the wrapper.
        args.insert(QStringLiteral("action"), QStringLiteral("soft_delete"));
        args.insert(QStringLiteral("runs"), QJsonArray{QJsonObject{
            {QStringLiteral("run_id"), QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")},
            {QStringLiteral("expected_revision"), 1}}});
        args.insert(QStringLiteral("reason"), QString::fromUtf8("批量删除"));
    } else if (messageType == QLatin1String("CreateCharacterProfile")) {
        // make_current false: the field stays off the wire (the contract's default).
        args.insert(QStringLiteral("region"), QStringLiteral("CN"));
        args.insert(QStringLiteral("display_name"), QString::fromUtf8("合成角色"));
        args.insert(QStringLiteral("goal_count"), 2000);
        args.insert(QStringLiteral("baseline_completed_count"), 120);
    } else if (messageType == QLatin1String("UpdateCharacterProfile")) {
        args.insert(QStringLiteral("profile_id"), 2);
        args.insert(QStringLiteral("display_name"), QString::fromUtf8("合成角色二"));
        args.insert(QStringLiteral("make_current"), true);
    } else if (messageType == QLatin1String("DeleteCharacterProfile")) {
        // An id no fresh library holds: ContractRequestSampleTests sends every sample to a
        // real Collector, whose refusal of the last or current profile of a region would be
        // ERR_BAD_REQUEST. An unknown id is ERR_NOT_FOUND and still reads the request whole.
        args.insert(QStringLiteral("profile_id"), 999);
    } else if (messageType == QLatin1String("ResolveCharacterIdentity")) {
        // 自动识别角色 → 是这个角色: BIND names the profile. An identity no fresh library holds,
        // so the real Collector of ContractRequestSampleTests answers ERR_NOT_FOUND.
        args.insert(QStringLiteral("identity_id"), 999);
        args.insert(QStringLiteral("action"), QStringLiteral("BIND"));
        args.insert(QStringLiteral("profile_id"), 1);
    } else if (messageType == QLatin1String("UpdateHistoryRetentionSettings")) {
        args.insert(QStringLiteral("retention_days"), 30);
    } else if (messageType == QLatin1String("AcknowledgeImageCleanup")) {
        args.insert(QStringLiteral("run_ids"), QJsonArray{QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")});
    } else if (messageType == QLatin1String("PreviewRunImport")) {
        // The first request of an import: edited candidates, a paged preview of 100 rows.
        args.insert(QStringLiteral("source_kind"), QStringLiteral("SCREENSHOT"));
        args.insert(QStringLiteral("source_name"), QStringLiteral("synthetic"));
        args.insert(QStringLiteral("time_zone"), QStringLiteral("+08:00"));
        args.insert(QStringLiteral("page_size"), 100);
        args.insert(QStringLiteral("rows"), QJsonArray{QJsonObject{
            {QStringLiteral("duty_name"), QString::fromUtf8("合成测试副本")},
            {QStringLiteral("reflection_text"), QString::fromUtf8("合成测试心得")},
            {QStringLiteral("source_recorded_at"), QStringLiteral("2026-10-08 20:30:00")}}});
    } else if (messageType == QLatin1String("CommitRunImport")) {
        args.insert(QStringLiteral("preview_id"), QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c"));
        args.insert(QStringLiteral("row_numbers"), QJsonArray{1});
        args.insert(QStringLiteral("deduct_from_baseline"), true);
    } else if (messageType == QLatin1String("ImportCalibrationCode")) {
        // tests/Fixtures/shared-calibration/vectors.json valid[0].code: the
        // sample the Collector side wrote first (contracts/CHANGELOG.md).
        args.insert(QStringLiteral("code"), QString::fromLatin1(mrtest::kVectorCode));
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
        QStringLiteral("SelectGameProcess"),
        QStringLiteral("StopCapture"),
        QStringLiteral("StartCaptureValidation"),
        QStringLiteral("GetCaptureValidationStatus"),
        QStringLiteral("AddCaptureValidationMarker"),
        QStringLiteral("StopCaptureValidation"),
        QStringLiteral("ConfirmCalibration"),
        QStringLiteral("DiscardCalibration"),
        QStringLiteral("GetCalibrationShareCode"),
        QStringLiteral("GetCalibrationEvidence"),
        QStringLiteral("GetTemplateObservation"),
        QStringLiteral("CheckSharedCalibration"),
        QStringLiteral("ImportCalibrationCode"),
        QStringLiteral("AcceptSharedQueueInference"),
        QStringLiteral("RejectSharedCalibration"),
        QStringLiteral("GetCurrentRun"),
        QStringLiteral("SubscribeLiveEvents"),
        QStringLiteral("QueryRuns"),
        QStringLiteral("QueryHistorySelection"),
        QStringLiteral("StageHistorySelection"),
        QStringLiteral("QueryRunCalendarDays"),
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
        QStringLiteral("DutyCommunity"),
        QStringLiteral("ExportCsv"),
        QStringLiteral("ExportJson"),
        QStringLiteral("BackupDatabase"),
        QStringLiteral("ExportDiagnosticsReport"),
        QStringLiteral("CheckDatabaseIntegrity"),
        QStringLiteral("GetSpeechSettings"),
        QStringLiteral("UpdateSpeechSettings"),
        QStringLiteral("SynthesizeSpeech"),
        QStringLiteral("CheckUpdateNow"),
        QStringLiteral("StartUpdateDownload"),
        QStringLiteral("CancelUpdateDownload"),
        QStringLiteral("ResolveUpdateMirrorOffer"),
        QStringLiteral("BatchMutateRuns"),
        QStringLiteral("PreviewRunRegionMigration"),
        QStringLiteral("GetHistoryRetentionSettings"),
        QStringLiteral("UpdateHistoryRetentionSettings"),
        QStringLiteral("GetPendingImageCleanup"),
        QStringLiteral("AcknowledgeImageCleanup"),
        QStringLiteral("PreviewRunImport"),
        QStringLiteral("CommitRunImport"),
        QStringLiteral("CreateCharacterProfile"),
        QStringLiteral("UpdateCharacterProfile"),
        QStringLiteral("DeleteCharacterProfile"),
        QStringLiteral("ResolveCharacterIdentity"),
        // No args: the sample pins time_zone: null (跟随系统), which every parallel
        // Collector test expects; the chosen-zone shape is the variant below.
        QStringLiteral("SetDisplayTimeZone"),
    };
}

/// A further shape of a message, pinned as variants/<file>.json.
struct RequestVariant
{
    QString file;
    QString messageType;
    QJsonObject args;
};

QList<RequestVariant> requestVariants()
{
    return {
        // ImportRecordsController: every page after the first of a frozen preview.
        {.file=QStringLiteral("PreviewRunImport.page"), .messageType=QStringLiteral("PreviewRunImport"),
         .args=QJsonObject{{QStringLiteral("preview_id"), QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c")},
                     {QStringLiteral("page"), 2}}},
        // A file read for the first time, with the column mapping the dialog offers.
        {.file=QStringLiteral("PreviewRunImport.file"), .messageType=QStringLiteral("PreviewRunImport"),
         .args=QJsonObject{{QStringLiteral("source_kind"), QStringLiteral("XLSX")},
                     {QStringLiteral("source_name"), QStringLiteral("personal-records.xlsx")},
                     {QStringLiteral("time_zone"), QStringLiteral("+09:00")},
                     {QStringLiteral("page_size"), 100},
                     {QStringLiteral("file_path"), QStringLiteral("D:/imports/personal-records.xlsx")},
                     {QStringLiteral("column_mapping"), QJsonObject{{QString::fromUtf8("评论"), QStringLiteral("reflection_text")}}}}},
        // The unpaged fallback, sent once to a Collector that refuses page_size.
        {.file=QStringLiteral("PreviewRunImport.unpaged"), .messageType=QStringLiteral("PreviewRunImport"),
         .args=QJsonObject{{QStringLiteral("source_kind"), QStringLiteral("PASTE")},
                     {QStringLiteral("source_name"), QString::fromUtf8("粘贴的表格")},
                     {QStringLiteral("time_zone"), QStringLiteral("+08:00")},
                     {QStringLiteral("text"), QString::fromUtf8("副本\t评论\n合成测试副本\t合成测试心得")}}},
        // No baseline question answered: deduct_from_baseline does not travel.
        {.file=QStringLiteral("CommitRunImport.keep-baseline"), .messageType=QStringLiteral("CommitRunImport"),
         .args=QJsonObject{{QStringLiteral("preview_id"), QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c")},
                     {QStringLiteral("row_numbers"), QJsonArray{1, 2, 5}}}},
        // 角色档案: the records of the profile's region are filed under it.
        {.file=QStringLiteral("CommitRunImport.profile"), .messageType=QStringLiteral("CommitRunImport"),
         .args=QJsonObject{{QStringLiteral("preview_id"), QStringLiteral("6f1d2c3b-4a5e-4f60-8b7c-9d0e1f2a3b4c")},
                     {QStringLiteral("row_numbers"), QJsonArray{1}},
                     {QStringLiteral("profile_id"), 2}}},
        // One character's goal and baseline, named by profile rather than by region.
        {.file=QStringLiteral("UpdateAchievementBaseline.profile"), .messageType=QStringLiteral("UpdateAchievementBaseline"),
         .args=QJsonObject{{QStringLiteral("goal_count"), 1000},
                     {QStringLiteral("baseline_completed_count"), 37},
                     {QStringLiteral("baseline_effective_at"), QLatin1String(kStamp)},
                     {QStringLiteral("reason"), QString::fromUtf8("设定角色起始次数")},
                     {QStringLiteral("profile_id"), 2}}},
        // 历史记录 → 改到角色: checked runs filed under one profile.
        {.file=QStringLiteral("BatchMutateRuns.assign-profile"), .messageType=QStringLiteral("BatchMutateRuns"),
         .args=QJsonObject{{QStringLiteral("action"), QStringLiteral("assign_profile")},
                     {QStringLiteral("profile_id"), 2},
                     {QStringLiteral("runs"), QJsonArray{QJsonObject{
                         {QStringLiteral("run_id"), QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")},
                         {QStringLiteral("expected_revision"), 1}}}},
                     {QStringLiteral("reason"), QString::fromUtf8("改到角色")}}},
        {.file=QStringLiteral("BatchMutateRuns.move-region"), .messageType=QStringLiteral("BatchMutateRuns"),
         .args=recordedRegionMigrationPayload(QStringLiteral("BatchMutateRuns"))},
        // ... and under none: profile_id travels as an explicit null.
        {.file=QStringLiteral("BatchMutateRuns.unassign-profile"), .messageType=QStringLiteral("BatchMutateRuns"),
         .args=QJsonObject{{QStringLiteral("action"), QStringLiteral("assign_profile")},
                     {QStringLiteral("runs"), QJsonArray{QJsonObject{
                         {QStringLiteral("run_id"), QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")},
                         {QStringLiteral("expected_revision"), 1}}}},
                     {QStringLiteral("reason"), QString::fromUtf8("不记在任何角色名下")}}},
        // 切换角色: make_current alone, the name left as it is.
        {.file=QStringLiteral("UpdateCharacterProfile.select"), .messageType=QStringLiteral("UpdateCharacterProfile"),
         .args=QJsonObject{{QStringLiteral("profile_id"), 2}, {QStringLiteral("make_current"), true}}},
        // 自动识别角色 → 不统计: IGNORE carries no profile_id.
        {.file=QStringLiteral("ResolveCharacterIdentity.ignore"), .messageType=QStringLiteral("ResolveCharacterIdentity"),
         .args=QJsonObject{{QStringLiteral("identity_id"), 999}, {QStringLiteral("action"), QStringLiteral("IGNORE")}}},
        // 登记并绑定原有记录: BIND that also names the upgraded profile (audit 2026-11-19, G-4).
        {.file=QStringLiteral("ResolveCharacterIdentity.register"), .messageType=QStringLiteral("ResolveCharacterIdentity"),
         .args=QJsonObject{{QStringLiteral("identity_id"), 999}, {QStringLiteral("action"), QStringLiteral("BIND")},
                     {QStringLiteral("profile_id"), 1}, {QStringLiteral("display_name"), QString::fromUtf8("合成角色四")}}},
        // 新角色: the waiting identity is bound to the profile it creates.
        {.file=QStringLiteral("CreateCharacterProfile.identity"), .messageType=QStringLiteral("CreateCharacterProfile"),
         .args=QJsonObject{{QStringLiteral("region"), QStringLiteral("CN")},
                     {QStringLiteral("display_name"), QString::fromUtf8("合成角色三")},
                     {QStringLiteral("goal_count"), 2000},
                     {QStringLiteral("baseline_completed_count"), 0},
                     {QStringLiteral("identity_id"), 999}}},
        // 重新识别: the characters bound to the profile are asked about again.
        {.file=QStringLiteral("UpdateCharacterProfile.forget"), .messageType=QStringLiteral("UpdateCharacterProfile"),
         .args=QJsonObject{{QStringLiteral("profile_id"), 2}, {QStringLiteral("forget_identities"), true}}},
        // 显示时区 chosen in 设置 › 通用: the IANA id travels as it is.
        {.file=QStringLiteral("SetDisplayTimeZone.chosen"), .messageType=QStringLiteral("SetDisplayTimeZone"),
         .args=QJsonObject{{QStringLiteral("time_zone"), QStringLiteral("Asia/Tokyo")}}},
        // 自动识别角色 switched off; only the one key travels.
        {.file=QStringLiteral("UpdateCaptureSettings.identification"), .messageType=QStringLiteral("UpdateCaptureSettings"),
         .args=QJsonObject{{QStringLiteral("changes"),
                            QJsonObject{{QStringLiteral("character_identification_enabled"), false}}}}},
    };
}

/// Keeps the payload of every request the shipping IBackend wrappers build.
class PayloadRecorder final : public mr::IBackend
{
public:
    QHash<QString, QJsonObject> payloads;
    /// Messages answered (on the next turn of the event loop) with these payloads;
    /// every other request stays unanswered.
    QHash<QString, QJsonObject> answers;
    QString backendName() const override { return QStringLiteral("payload-recorder"); }
    bool isConnected() const override { return true; }
    mr::BackendReply *request(const QString &messageType, const QJsonObject &payload) override
    {
        payloads.insert(messageType, payload);
        auto *reply = new mr::BackendReply(QStringLiteral("recorded"), messageType, this);
        if (answers.contains(messageType)) {
            const QJsonObject answer = answers.value(messageType);
            QTimer::singleShot(0, reply, [reply, answer] { reply->succeed(answer); });
        }
        return reply;
    }
};

QJsonObject recordedRegionMigrationPayload(const QString &messageType)
{
    // The values of the hand-written samples: run bbbb… at revision 1 moved to GLOBAL,
    // character 4, whose baseline the preview reads as 1444; one run overlaps it.
    PayloadRecorder backend;
    backend.answers.insert(QStringLiteral("PreviewRunRegionMigration"),
                           QJsonObject{{QStringLiteral("target_baseline"), 1444}});
    mr::HistoryController history(&backend);
    history.setRunChecked({{QStringLiteral("run_id"), QStringLiteral("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")},
                           {QStringLiteral("revision"), 1}, {QStringLiteral("source"), QStringLiteral("MANUAL")},
                           {QStringLiteral("region"), QStringLiteral("CN")}},
                          true);
    history.previewRegionMigration(QStringLiteral("GLOBAL"), 4);
    if (messageType == QLatin1String("BatchMutateRuns")) {
        if (!QTest::qWaitFor([&history] { return !history.regionMigrationPreview().isEmpty(); }, 2000))
            return {};
        history.movePreviewedRuns(1, QString::fromUtf8("更正原始区服"));
    }
    return backend.payloads.value(messageType);
}

/// The committed sample at \a path. Empty, with \a error saying whether the file could not
/// be read, did not parse or held no JSON object, when it is unusable.
QJsonObject readSample(const QString &path, QString &error)
{
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) {
        error = QStringLiteral("%1 could not be read: %2").arg(path, file.errorString());
        return {};
    }
    QJsonParseError parse{};
    const QJsonObject sample = QJsonDocument::fromJson(file.readAll(), &parse).object();
    if (parse.error != QJsonParseError::NoError)
        error = QStringLiteral("%1 did not parse: %2 at offset %3").arg(path, parse.errorString()).arg(parse.offset);
    else if (sample.isEmpty())
        error = QStringLiteral("%1 holds no JSON object").arg(path);
    return sample;
}

/// Writes \a envelope where a maintainer copies regenerated samples from.
bool writeEnvelope(const QString &path, const QJsonObject &envelope)
{
    QFile file(path);
    if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate))
        return false;
    return file.write(QJsonDocument(envelope).toJson(QJsonDocument::Indented)) >= 0;
}

/// Compares \a envelope with the committed sample at \a path field by field rather
/// than as whole documents: the payload and the two envelope fields this client
/// decides are what the contract pins. request_id is not one of them - a sample
/// written on the Collector side carries its own id, and this client's constant is
/// already asserted by buildsOneSamplePerMessageType. Callers stop on
/// QTest::currentTestFailed().
void compareWithSample(const QJsonObject &envelope, const QString &path, const QString &drift)
{
    QString error;
    const QJsonObject sample = readSample(path, error);
    QVERIFY2(error.isEmpty(), qPrintable(error));
    QCOMPARE(sample.value(QStringLiteral("message_type")), envelope.value(QStringLiteral("message_type")));
    QCOMPARE(sample.value(QStringLiteral("protocol_version")), envelope.value(QStringLiteral("protocol_version")));
    QVERIFY2(sample.value(QStringLiteral("payload")) == envelope.value(QStringLiteral("payload")), qPrintable(drift));
}

} // namespace

namespace {
class IpcRequestTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void buildsOneSamplePerMessageType();
    void samplesMatchTheCommittedFixtures();
    void variantsMatchTheCommittedFixtures();
    void reflectionRequestsKeepAnEmptyTextAndClampTheRecentLimit();
    void exportRequestsReplaceOnlyAConfirmedTarget();
    void characterIdentityRequestsCarryOnlyTheirFields();
    void candidateEvidenceRequestsCarryAnswersOnlyWhenWritten();
    void duplicateIdKeepsFirstReply_data();
    void duplicateIdKeepsFirstReply();
    void importCommitReusesIdAfterOfflineRetry();
    void tearingDownRejectsReentrantRequests_data();
    void tearingDownRejectsReentrantRequests();
};
} // namespace

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

        const QString path = QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR) + QLatin1Char('/') + type + QStringLiteral(".json");
        QVERIFY2(writeEnvelope(path, envelope), qPrintable(path));
    }
    const QString variants = QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR) + QStringLiteral("/variants");
    QVERIFY(QDir().mkpath(variants));
    for (const RequestVariant &variant : requestVariants()) {
        const QJsonObject envelope = mr::IpcBackend::buildRequestForTest(variant.messageType, variant.args);
        QCOMPARE(envelope.value(QStringLiteral("message_type")).toString(), variant.messageType);
        const QString path = variants + QLatin1Char('/') + variant.file + QStringLiteral(".json");
        QVERIFY2(writeEnvelope(path, envelope), qPrintable(path));
    }
}

void IpcRequestTests::samplesMatchTheCommittedFixtures()
{
    for (const QString &type : allMessageTypes()) {
        compareWithSample(mr::IpcBackend::buildRequestForTest(type, argsFor(type)),
                          QStringLiteral(MR_IPC_REQUEST_FIXTURE_DIR) + QLatin1Char('/') + type + QStringLiteral(".json"),
                          type + QStringLiteral(" drifted from its committed sample; regenerate from ")
                              + QStringLiteral(MR_IPC_REQUEST_OUTPUT_DIR));
        if (QTest::currentTestFailed())
            return;
    }
}

// G-3 / I1-1 (audit 2026-10-20): the import dialog's other request shapes are pinned too.
void IpcRequestTests::variantsMatchTheCommittedFixtures()
{
    for (const RequestVariant &variant : requestVariants()) {
        compareWithSample(mr::IpcBackend::buildRequestForTest(variant.messageType, variant.args),
                          QStringLiteral(MR_IPC_REQUEST_FIXTURE_DIR) + QStringLiteral("/variants/")
                              + variant.file + QStringLiteral(".json"),
                          variant.file + QStringLiteral(" drifted from its committed sample"));
        if (QTest::currentTestFailed())
            return;
    }
}

// An empty text is a deletion and must survive into SetRunReflection's payload, and a
// recent_limit out of range is clamped to $defs/GetReflectionSummaryRequest. The other
// fields of both requests are pinned by the committed samples above.
void IpcRequestTests::reflectionRequestsKeepAnEmptyTextAndClampTheRecentLimit()
{
    QJsonObject args = argsFor(QStringLiteral("SetRunReflection"));
    args.insert(QStringLiteral("text"), QString());
    QVERIFY(mr::IpcBackend::buildRequestForTest(QStringLiteral("SetRunReflection"), args)
                .value(QStringLiteral("payload")).toObject().contains(QStringLiteral("text")));
    QCOMPARE(mr::IpcBackend::buildRequestForTest(QStringLiteral("GetReflectionSummary"),
                                                 {{QStringLiteral("recent_limit"), 99}})
                 .value(QStringLiteral("payload")).toObject().value(QStringLiteral("recent_limit")).toInt(),
             20);
}

// Audit 2026-11-03 G-2: a target the save dialog already confirmed replacing travels with
// overwrite = true (contracts/ipc-v1.schema.json ExportRequest, ExportDiagnosticsReportRequest);
// every other export leaves the field out, so the Collector keeps refusing an existing file
// and the committed samples above stay as they are.
void IpcRequestTests::exportRequestsReplaceOnlyAConfirmedTarget()
{
    PayloadRecorder backend;
    const QString target = QStringLiteral("D:/exports/mentor.out");
    for (const bool confirmed : {false, true}) {
        backend.exportCsv(target, sampleFilter(), confirmed);
        backend.exportJson(target, sampleFilter(), confirmed);
        backend.exportDiagnosticsReport(target, confirmed);
        for (const QString &type : {QStringLiteral("ExportCsv"), QStringLiteral("ExportJson"),
                                   QStringLiteral("ExportDiagnosticsReport")}) {
            const QJsonObject payload = backend.payloads.value(type);
            QCOMPARE(payload.value(QStringLiteral("target_path")).toString(), target);
            QCOMPARE(payload.contains(QStringLiteral("overwrite")), confirmed);
            if (confirmed)
                QCOMPARE(payload.value(QStringLiteral("overwrite")), QJsonValue(true));
        }
        QCOMPARE(backend.payloads.value(QStringLiteral("ExportCsv")).value(QStringLiteral("filter")).toObject(),
                 sampleFilter());
    }
}

// 自动识别角色: the optional ids travel only when there is one (the contract's minimum is 1),
// so a request without an identity is byte for byte what an older Collector already reads.
void IpcRequestTests::characterIdentityRequestsCarryOnlyTheirFields()
{
    PayloadRecorder backend;
    backend.resolveCharacterIdentity(7, QStringLiteral("ASK_AGAIN"), 0);
    QCOMPARE(backend.payloads.value(QStringLiteral("ResolveCharacterIdentity")),
             (QJsonObject{{QStringLiteral("identity_id"), 7}, {QStringLiteral("action"), QStringLiteral("ASK_AGAIN")}}));
    backend.resolveCharacterIdentity(7, QStringLiteral("BIND"), 3);
    QCOMPARE(backend.payloads.value(QStringLiteral("ResolveCharacterIdentity")).value(QStringLiteral("profile_id")),
             QJsonValue(3));
    backend.createCharacterProfile(QStringLiteral("CN"), QStringLiteral("A"), 2000, 0, false);
    QVERIFY(!backend.payloads.value(QStringLiteral("CreateCharacterProfile")).contains(QStringLiteral("identity_id")));
    backend.createCharacterProfile(QStringLiteral("CN"), QStringLiteral("A"), 2000, 0, false, {}, 7);
    QCOMPARE(backend.payloads.value(QStringLiteral("CreateCharacterProfile")).value(QStringLiteral("identity_id")),
             QJsonValue(7));
    backend.forgetCharacterIdentities(4);
    QCOMPARE(backend.payloads.value(QStringLiteral("UpdateCharacterProfile")),
             (QJsonObject{{QStringLiteral("profile_id"), 4}, {QStringLiteral("forget_identities"), true}}));
}

// 第二轮 (EvidenceCandidateRound.qml): the tester's four answers travel as round_answers
// ($defs/CandidateRoundAnswers), trimmed, at most 100 characters, blanks and unknown keys
// left out; with nothing written the request is the committed sample's, without the field.
void IpcRequestTests::candidateEvidenceRequestsCarryAnswersOnlyWhenWritten()
{
    PayloadRecorder backend;
    const QString target = QStringLiteral("D:/exports/candidate.json");
    backend.exportCandidateEvidence(target);
    QCOMPARE(backend.payloads.value(QStringLiteral("ExportCandidateEvidence")),
             (QJsonObject{{QStringLiteral("target_path"), target}}));
    backend.exportCandidateEvidence({}, {{QStringLiteral("duty"), QStringLiteral("   ")},
                                         {QStringLiteral("job_before"), QString()}});
    QCOMPARE(backend.payloads.value(QStringLiteral("ExportCandidateEvidence")),
             (QJsonObject{{QStringLiteral("target_path"), QJsonValue(QJsonValue::Null)}}));

    backend.exportCandidateEvidence(target, {{QStringLiteral("first_roulette"), QString::fromUtf8(" 练级迷宫 ")},
                                             {QStringLiteral("job_before"), QString::fromUtf8("骑士")},
                                             {QStringLiteral("job_after"), QString()},
                                             {QStringLiteral("duty"), QString(130, QLatin1Char('x'))},
                                             {QStringLiteral("note"), QStringLiteral("not a contract field")}});
    QCOMPARE(backend.payloads.value(QStringLiteral("ExportCandidateEvidence")).value(QStringLiteral("round_answers")).toObject(),
             (QJsonObject{{QStringLiteral("first_roulette"), QString::fromUtf8("练级迷宫")},
                          {QStringLiteral("job_before"), QString::fromUtf8("骑士")},
                          {QStringLiteral("duty"), QString(100, QLatin1Char('x'))}}));

    // The pipe dispatch of a test harness request carries the answers too.
    const QJsonObject envelope = mr::IpcBackend::buildRequestForTest(
        QStringLiteral("ExportCandidateEvidence"),
        QJsonObject{{QStringLiteral("target_path"), target},
                    {QStringLiteral("round_answers"), QJsonObject{{QStringLiteral("job_after"), QString::fromUtf8("学者")}}}});
    QCOMPARE(envelope.value(QStringLiteral("payload")).toObject().value(QStringLiteral("round_answers")).toObject(),
             (QJsonObject{{QStringLiteral("job_after"), QString::fromUtf8("学者")}}));
}

void IpcRequestTests::duplicateIdKeepsFirstReply_data()
{
    QTest::addColumn<bool>("answerFirst");
    QTest::newRow("response") << true;
    QTest::newRow("timeout") << false;
}

void IpcRequestTests::duplicateIdKeepsFirstReply()
{
#ifndef Q_OS_WIN
    QSKIP("The production named-pipe transport fixture requires Windows.");
#else
    QFETCH(bool, answerFirst);
    mrtest::TestPipe pipe("request-id");
    QVERIFY(pipe.isValid());
    mr::IpcClient client;
    client.setServerName(pipe.serverName);
    client.start();
    QTRY_VERIFY(client.isConnected());

    const QString id = mr::ipc::newRequestId();
    const QString firstType = QStringLiteral("GetVersion");
    const QString otherType = QStringLiteral("GetStatus");
    auto *first = new mr::BackendReply(id, firstType, &client);
    QSignalSpy firstDone(first, &mr::BackendReply::done);
    client.send(first, firstType, {}, answerFirst ? mr::IpcClient::kDefaultRequestTimeoutMs : 20);
    auto *other = new mr::BackendReply(id, otherType, &client);
    QSignalSpy otherDone(other, &mr::BackendReply::done);
    client.send(other, otherType, {});
    QCOMPARE(otherDone.size(), 1);
    QVERIFY(other->neverSent());
    QCOMPARE(other->errorCode(), QStringLiteral("ERR_BAD_REQUEST"));
    QVERIFY(firstDone.isEmpty());

    QByteArray bytes;
    QJsonObject request;
    QTRY_VERIFY(!request.isEmpty() || pipe.takeRequest(bytes, request));
    QCOMPARE(request, mr::ipc::makeRequest(id, firstType, {}));
    const QJsonObject payload{{QStringLiteral("collector_version"), QStringLiteral("test-version")}};
    if (answerFirst)
        QVERIFY(pipe.writeReply(request, payload));
    QTRY_COMPARE(firstDone.size(), 1);
    QCOMPARE(firstDone.first().at(0).toBool(), answerFirst);
    QCOMPARE(firstDone.first().at(2).toString(), answerFirst ? QString() : QStringLiteral("ERR_INTERNAL"));
    if (answerFirst)
        QCOMPARE(firstDone.first().at(1).toMap(), payload.toVariantMap());

    auto *retry = new mr::BackendReply(id, firstType, &client);
    QSignalSpy retryDone(retry, &mr::BackendReply::done);
    client.send(retry, firstType, {});
    QJsonObject retryRequest;
    QTRY_VERIFY(!retryRequest.isEmpty() || pipe.takeRequest(bytes, retryRequest));
    QCOMPARE(retryRequest, request);
    QVERIFY(pipe.writeReply(retryRequest, payload));
    QTRY_COMPARE(retryDone.size(), 1);
    QVERIFY(retryDone.first().at(0).toBool());
    QCOMPARE(retryDone.first().at(1).toMap(), payload.toVariantMap());
    QCOMPARE(otherDone.size(), 1);
#endif
}

void IpcRequestTests::importCommitReusesIdAfterOfflineRetry()
{
#ifndef Q_OS_WIN
    QSKIP("The production named-pipe transport fixture requires Windows.");
#else
    mrtest::TestPipe pipe("import-commit");
    QVERIFY(pipe.isValid());
    class ImportBackend final : public mr::IpcBackend
    {
    public:
        using IpcBackend::IpcBackend;

        mr::BackendReply *requestWithId(const QString &type, const QJsonObject &payload, const QString &id) override
        {
            if (type != QLatin1String("CommitRunImport") || !shortCommitDeadline)
                return IpcBackend::requestWithId(type, payload, id);
            // Exercise the real timeout path without waiting for the two-minute import deadline.
            auto *reply = new mr::BackendReply(id, type, this);
            client()->send(reply, type, payload, 20);
            return reply;
        }

        bool shortCommitDeadline = true;
    } backend(nullptr, pipe.serverName);
    QTRY_VERIFY(backend.isConnected());
    mr::ImportRecordsController controller;
    controller.setBackend(&backend);
    controller.importText(QStringLiteral("副本\n合成副本"));
    QByteArray bytes;
    QJsonObject previewRequest;
    QTRY_VERIFY(!previewRequest.isEmpty() || pipe.takeRequest(bytes, previewRequest));
    QCOMPARE(previewRequest.value("message_type").toString(), QStringLiteral("PreviewRunImport"));
    const QJsonObject row{{"row_number", 1}, {"status", "new"}, {"can_import", true}, {"incomplete", true},
        {"candidate", QJsonObject{{"duty_name", QStringLiteral("合成副本")}}}};
    const QJsonObject preview{{"preview_id", "test-preview"}, {"rows", QJsonArray{row}},
        {"summary", QJsonObject{{"total", 1}, {"new", 1}, {"duplicate", 0}, {"conflict", 0}}}};
    QVERIFY(pipe.writeReply(previewRequest, preview));
    QTRY_VERIFY(controller.previewValid());
    controller.setOwnRecordsConfirmed(true);
    controller.commit();
    QJsonObject request;
    QTRY_VERIFY(!request.isEmpty() || pipe.takeRequest(bytes, request));
    QCOMPARE(request.value("message_type").toString(), QStringLiteral("CommitRunImport"));
    QVERIFY(!request.value("request_id").toString().isEmpty());
    QTRY_VERIFY(controller.pendingCommitConfirmation());

    backend.client()->stop();
    pipe.close();
    QVERIFY(!backend.isConnected());
    controller.commit();
    QVERIFY(!controller.committing());
    QVERIFY(controller.pendingCommitConfirmation());
    controller.updateCandidate(0, {{"duty_name", QStringLiteral("不得修改")}});
    controller.setAllImportableSelected(false);
    controller.cancel();
    controller.reset();
    QCOMPARE(controller.selectedCount(), 1);
    QCOMPARE(controller.currentCandidate().value("duty_name").toString(), QStringLiteral("合成副本"));
    QVERIFY(controller.canCommit());

    QVERIFY(pipe.reopen());
    backend.shortCommitDeadline = false;
    backend.client()->start();
    QTRY_VERIFY(backend.isConnected());
    QSignalSpy imported(&controller, &mr::ImportRecordsController::imported);
    controller.commit();
    QJsonObject retry;
    QTRY_VERIFY(!retry.isEmpty() || pipe.takeRequest(bytes, retry));
    QCOMPARE(retry, request);
    const QJsonObject receipt{{"imported_count", 1}, {"replayed", true},
        {"rows", QJsonArray{QJsonObject{{"row_number", 1}, {"status", "imported"}}}}};
    QVERIFY(pipe.writeReply(retry, receipt));
    QTRY_COMPARE(imported.size(), 1);
    QVERIFY(!controller.pendingCommitConfirmation());
    QVERIFY(controller.rows().isEmpty());
    QCOMPARE(imported.first().first().toMap(), receipt.toVariantMap());
#endif
}

void IpcRequestTests::tearingDownRejectsReentrantRequests_data()
{
    QTest::addColumn<bool>("badFrame");
    QTest::newRow("stop") << false;
    QTest::newRow("unreadable-response") << true;
}

void IpcRequestTests::tearingDownRejectsReentrantRequests()
{
#ifndef Q_OS_WIN
    QSKIP("The production named-pipe transport fixture requires Windows.");
#else
    QFETCH(bool, badFrame);
    mrtest::TestPipe pipe("teardown");
    QVERIFY(pipe.isValid());
    mr::IpcClient client;
    client.setServerName(pipe.serverName);
    client.start();
    QTRY_VERIFY(client.isConnected());

    const QString id = mr::ipc::newRequestId();
    const QString type = QStringLiteral("CommitRunImport");
    auto *sent = new mr::BackendReply(id, type, &client);
    auto *retry = new mr::BackendReply(id, type, &client);
    bool transportFailed = false;
    bool stillConnected = false;
    bool neverSent = false;
    QString sentError;
    QString retryError;
    QObject subscription;
    sent->whenDone(&subscription, [&](bool, const QVariantMap &, const QString &code, const QString &) {
        transportFailed = sent->transportFailed();
        stillConnected = client.isConnected();
        sentError = code;
        client.send(retry, type, {});
    });
    retry->whenDone(&subscription, [&](bool, const QVariantMap &, const QString &code, const QString &) {
        neverSent = retry->neverSent();
        retryError = code;
    });

    client.send(sent, type, {});
    QByteArray bytes;
    QJsonObject request;
    QTRY_VERIFY(!request.isEmpty() || pipe.takeRequest(bytes, request));
    QCOMPARE(request, mr::ipc::makeRequest(id, type, {}));
    if (badFrame) {
        // The response frame carries one byte, which cannot be a JSON object.
        QVERIFY(pipe.writeFrame(QByteArray::fromHex("0100000021")));
    } else {
        client.stop();
    }
    QTRY_VERIFY(!sentError.isEmpty());
    QCOMPARE(sentError, badFrame ? QStringLiteral("ERR_PROTOCOL_VERSION") : QStringLiteral("ERR_INTERNAL"));
    QVERIFY(transportFailed);
    QVERIFY(!stillConnected);
    QCOMPARE(retryError, QStringLiteral("ERR_INTERNAL"));
    QVERIFY(neverSent);
#endif
}

MR_DESKTOP_TEST_SUITE(IpcRequest)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    IpcRequestTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "IpcRequestTests.moc"
