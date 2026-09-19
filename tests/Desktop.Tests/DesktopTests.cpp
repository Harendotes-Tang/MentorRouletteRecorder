#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "IpcFraming.h"
#include "MockBackend.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"
#include "RunListModel.h"
#include "StatsModels.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QJsonDocument>
#include <QFile>
#include <QPointer>
#include <QSignalSpy>
#include <QTest>

namespace {

QString firstRoleGroup(const QVariantList &rows, int index)
{
    return rows.at(index).toMap().value(QStringLiteral("role_group")).toString();
}

/// A valid correction form, as the dialog builds it.
QVariantMap baseForm()
{
    QVariantMap form;
    form.insert(QStringLiteral("reason"), QString::fromUtf8("\u961f\u53cb\u622a\u56fe\u786e\u8ba4"));
    form.insert(QStringLiteral("reason_label"), QString::fromUtf8("\u4fee\u6b63\u539f\u56e0"));
    form.insert(QStringLiteral("date"), QStringLiteral("2026-09-04"));
    form.insert(QStringLiteral("matched"), QStringLiteral("20:00:00"));
    form.insert(QStringLiteral("entered"), QStringLiteral("20:01:11"));
    form.insert(QStringLiteral("ended"), QStringLiteral("20:20:00"));
    form.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
    form.insert(QStringLiteral("duty_name"), QString::fromUtf8("\u77f3\u536b\u5854"));
    form.insert(QStringLiteral("job_name"), QString::fromUtf8("\u9a91\u58eb"));
    form.insert(QStringLiteral("contributes"), true);
    form.insert(QStringLiteral("note"), QString());
    form.insert(QStringLiteral("edit_mode"), false);
    return form;
}

QString validationCode(const QVariantMap &form, const QVariantMap &before = {})
{
    return mr::RunFormValidator::validate(form, before)
        .value(QStringLiteral("code"))
        .toString();
}

int sumAttempts(const QVariantList &rows)
{
    int total = 0;
    for (const QVariant &value : rows)
        total += value.toMap().value(QStringLiteral("attempt_count")).toInt();
    return total;
}

QJsonObject validationSnapshot(const QString &state, const QString &reason = {});

class DeferredBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("ipc"); }
    bool isConnected() const override { return m_connected; }
    QString connectionDetail() const override
    {
        return m_connected ? QStringLiteral("connected")
                           : QStringLiteral("Collector 未连接。");
    }

    mr::BackendReply *request(const QString &messageType,
                              const QJsonObject &payload = {}) override
    {
        auto *reply =
            new mr::BackendReply(QStringLiteral("test-request"), messageType, this);
        m_seenMessages.append(messageType);
        m_seenPayloads.append(payload);
        if (messageType == QLatin1String("SubscribeLiveEvents"))
            ++m_subscribeCalls;

        if (messageType == m_syncFailureType) {
            reply->fail(m_syncFailureCode, m_syncFailureMessage);
            return reply;
        }

        if (!m_autoRespond) {
            m_pending.append(reply);
            return reply;
        }

        const bool connected = m_connected;
        const QJsonObject response = payloadFor(messageType);
        QPointer<mr::BackendReply> guard(reply);
        QTimer::singleShot(0, this, [guard, connected, response] {
            if (!guard)
                return;
            if (!connected) {
                guard->fail(QStringLiteral("ERR_INTERNAL"),
                            QString::fromUtf8("Collector 未连接。"));
                return;
            }
            guard->succeed(response);
        });
        return reply;
    }

    void setConnected(bool connected)
    {
        if (m_connected == connected)
            return;
        m_connected = connected;
        Q_EMIT connectionChanged();
    }

    int subscribeCalls() const { return m_subscribeCalls; }
    const QStringList &seenMessages() const { return m_seenMessages; }
    const QList<QJsonObject> &seenPayloads() const { return m_seenPayloads; }
    int count(const QString &messageType) const { return m_seenMessages.count(messageType); }
    void setAutoRespond(bool value) { m_autoRespond = value; }
    void setSyncFailureType(const QString &value) { m_syncFailureType = value; }
    void setSyncFailure(const QString &type, const QString &code,
                        const QString &message)
    {
        m_syncFailureType = type;
        m_syncFailureCode = code;
        m_syncFailureMessage = message;
    }
    void setProfileStatus(const QString &value) { m_profileStatus = value; }
    void setCaptureState(const QString &value) { m_captureState = value; }

    void completeNext(const QString &messageType, const QJsonObject &payload,
                      bool ok = true, const QString &code = {}, const QString &message = {})
    {
        for (qsizetype i = 0; i < m_pending.size(); ++i) {
            mr::BackendReply *reply = m_pending.at(i);
            if (reply && reply->messageType() == messageType) {
                m_pending.removeAt(i);
                if (ok)
                    reply->succeed(payload);
                else
                    reply->fail(code, message);
                return;
            }
        }
        QFAIL(qPrintable(QStringLiteral("no pending reply for ") + messageType));
    }

private:
    QJsonObject payloadFor(const QString &messageType) const
    {
        if (messageType == QLatin1String("GetStatus")) {
            return QJsonObject{
                {QStringLiteral("capture"),
                 QJsonObject{{QStringLiteral("state"), m_captureState},
                             {QStringLiteral("npcap_installed"), true},
                             {QStringLiteral("ffxiv_running"), false}}}};
        }
        if (messageType == QLatin1String("GetCurrentRun"))
            return QJsonObject{{QStringLiteral("state"), QStringLiteral("IDLE")}};
        if (messageType == QLatin1String("GetProtocolProfileStatus")) {
            return QJsonObject{{QStringLiteral("status"), m_profileStatus},
                               {QStringLiteral("status_label"), m_profileStatus}};
        }
        if (messageType == QLatin1String("GetCaptureValidationStatus"))
            return validationSnapshot(QStringLiteral("IDLE"));
        if (messageType == QLatin1String("GetDashboardStats")) {
            return QJsonObject{
                {QStringLiteral("attempt_count"), 0},
                {QStringLiteral("completed_count"), 0},
                {QStringLiteral("completion_rate"), QJsonValue::Null},
                {QStringLiteral("leave_rate"), QJsonValue::Null},
                {QStringLiteral("cancelled_count"), 0},
                {QStringLiteral("completed_last_7_days"), 0},
                {QStringLiteral("completed_last_30_days"), 0}};
        }
        if (messageType == QLatin1String("QueryRuns")) {
            return QJsonObject{
                {QStringLiteral("items"), QJsonArray{}},
                {QStringLiteral("page"), 1},
                {QStringLiteral("page_size"), 10},
                {QStringLiteral("total"), 0},
                {QStringLiteral("total_pages"), 0}};
        }
        if (messageType == QLatin1String("GetDungeonStats")
            || messageType == QLatin1String("GetJobStats")) {
            return QJsonObject{
                {QStringLiteral("items"), QJsonArray{}},
                {QStringLiteral("page"), 1},
                {QStringLiteral("page_size"), 200},
                {QStringLiteral("total"), 0},
                {QStringLiteral("total_pages"), 0}};
        }
        return QJsonObject{};
    }

    bool m_connected = false;
    bool m_autoRespond = true;
    int m_subscribeCalls = 0;
    QStringList m_seenMessages;
    QList<QJsonObject> m_seenPayloads;
    QList<QPointer<mr::BackendReply>> m_pending;
    QString m_syncFailureType;
    QString m_syncFailureCode = QStringLiteral("ERR_SYNC_TEST");
    QString m_syncFailureMessage = QString::fromUtf8("同步拒绝测试");
    QString m_profileStatus = QStringLiteral("UNVERIFIED");
    QString m_captureState = QStringLiteral("STOPPED");
};

QJsonObject validationSnapshot(const QString &state, const QString &reason)
{
    const bool active = state == QLatin1String("WAITING")
                        || state == QLatin1String("RECORDING")
                        || state == QLatin1String("STOPPING");
    const bool recording = state == QLatin1String("RECORDING")
                           || state == QLatin1String("STOPPING")
                           || state == QLatin1String("COMPLETED");
    QJsonObject status{
        {QStringLiteral("state"), state},
        {QStringLiteral("active"), active},
        {QStringLiteral("reason"), reason.isEmpty() ? state : reason},
        {QStringLiteral("message"), QString::fromUtf8("测试验证状态")},
        {QStringLiteral("session_id"), active || recording
                                          ? QJsonValue(QStringLiteral("validation-test"))
                                          : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("started_at_utc"), recording
                                              ? QJsonValue(QStringLiteral("2026-09-05T00:00:00.000Z"))
                                              : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("ended_at_utc"), state == QLatin1String("COMPLETED")
                                            ? QJsonValue(QStringLiteral("2026-09-05T00:05:00.000Z"))
                                            : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("message_count"), recording ? QJsonValue(12) : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("marker_count"), recording ? QJsonValue(1) : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("decode_error_count"), recording ? QJsonValue(0) : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("queue_dropped"), recording ? QJsonValue(0) : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("truncated"), false},
        {QStringLiteral("trace_path"), state == QLatin1String("COMPLETED")
                                          ? QJsonValue(QStringLiteral("D:/trace/session/trace.jsonl"))
                                          : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("sha256_path"), state == QLatin1String("COMPLETED")
                                           ? QJsonValue(QStringLiteral("D:/trace/session/trace.jsonl.sha256"))
                                           : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("sha256"), state == QLatin1String("COMPLETED")
                                      ? QJsonValue(QString(64, QLatin1Char('a')))
                                      : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("error_code"), QJsonValue(QJsonValue::Null)},
    };
    return status;
}

} // namespace

class DesktopTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void ipcFraming_roundTripsAndRejectsInvalidPayload();
    void ipcEnvelope_readsSuccessErrorAndEventShapes();
    void ipcEnvelope_toleratesAdditiveFields();
    void formatters_renderNullsAndDurations();
    void runListModel_filtersSortsAndPages();
    void jobStatsModel_roleBreakdownKeepsFixedOrder();
    void jobStatsModel_derivesRoleGroupFromTheContractFields();
    void roleCatalog_mapsEveryRoleToAnExistingIcon();
    void appController_rebuildsDutyOptionsAndHandlesCaptureFailure();
    void appController_subscribesAndRefreshesAfterDelayedBackendConnect();
    void appController_routesValidationAndBlocksDuplicateCommands();
    void appController_handlesSynchronousValidationFailure();
    void appController_persistentlyReportsUnsupportedOldCollector();
    void appController_ignoresStaleValidationStatusAfterStop();
    void appController_keepsRecordingOnTransientStatusFailureAndRecovers();
    void appController_disconnectMakesValidationUnknownAndReconnectOnlyPolls();
    void appController_gatesMarkersAndUsesFormalCaptureForVerifiedProfile();
    void appController_stopWhileMarkerPendingDoesNotLatchBusy();
    void appController_ignoresStaleFormalCallbacksAfterReconnect();
    void appController_prioritizesCurrentFormalCaptureOverTerminalValidation();
    void appController_waitsForValidationModeBeforeNewStart();
    void appController_notifiesAggregateCaptureProjectionForFormalStatus();
    void runFormValidator_rejectsEveryPrototypeCase();
    void runFormValidator_buildsTheBeforeAfterDiff();
};

void DesktopTests::ipcFraming_roundTripsAndRejectsInvalidPayload()
{
    const QJsonObject request =
        mr::ipc::makeRequest(QStringLiteral("11111111-1111-1111-1111-111111111111"),
                             QStringLiteral("GetStatus"),
                             QJsonObject{{QStringLiteral("ping"), true}});
    const QByteArray frame = mr::ipc::encodeFrame(request);
    QVERIFY(!frame.isEmpty());

    QByteArray partial = frame.left(3);
    QJsonObject decoded;
    QCOMPARE(mr::ipc::takeFrame(partial, decoded), mr::ipc::FrameStatus::Incomplete);
    QCOMPARE(partial, frame.left(3));

    QByteArray buffer = frame;
    QCOMPARE(mr::ipc::takeFrame(buffer, decoded), mr::ipc::FrameStatus::Ok);
    QCOMPARE(decoded, request);
    QVERIFY(buffer.isEmpty());

    QByteArray oversized;
    oversized.append(char(0x01));
    oversized.append(char(0x00));
    oversized.append(char(0x40));
    oversized.append(char(0x00));
    QCOMPARE(mr::ipc::takeFrame(oversized, decoded), mr::ipc::FrameStatus::Oversized);

    const QByteArray badBody = QByteArrayLiteral("{not-json");
    QByteArray badFrame;
    const quint32 length = quint32(badBody.size());
    badFrame.append(char(length & 0xff));
    badFrame.append(char((length >> 8) & 0xff));
    badFrame.append(char((length >> 16) & 0xff));
    badFrame.append(char((length >> 24) & 0xff));
    badFrame.append(badBody);
    QCOMPARE(mr::ipc::takeFrame(badFrame, decoded), mr::ipc::FrameStatus::BadJson);
}

void DesktopTests::ipcEnvelope_readsSuccessErrorAndEventShapes()
{
    const QString requestId = QStringLiteral("11111111-1111-1111-1111-111111111111");

    // 1. Success: {ok: true, payload}.
    QJsonObject success;
    success.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    success.insert(QStringLiteral("request_id"), requestId);
    success.insert(QStringLiteral("message_type"), QStringLiteral("GetVersion"));
    success.insert(QStringLiteral("ok"), true);
    success.insert(QStringLiteral("payload"),
                   QJsonObject{{QStringLiteral("collector_version"), QStringLiteral("0.1.0")}});

    mr::ipc::EnvelopeView view = mr::ipc::readEnvelope(success);
    QCOMPARE(view.kind, mr::ipc::EnvelopeKind::Response);
    QVERIFY(view.ok);
    QCOMPARE(view.requestId, requestId);
    QCOMPARE(view.payload.value(QStringLiteral("collector_version")).toString(),
             QStringLiteral("0.1.0"));
    QVERIFY(view.errorCode.isEmpty());

    // 2. Failure on a known message type: ok = false, the error object wins and
    //    the payload copy is discarded.
    QJsonObject error;
    error.insert(QStringLiteral("code"), QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
    error.insert(QStringLiteral("message"), QStringLiteral("conflict"));
    error.insert(QStringLiteral("field"), QStringLiteral("request_id"));
    error.insert(QStringLiteral("retryable"), false);
    error.insert(QStringLiteral("details"),
                 QJsonObject{{QStringLiteral("conflict"), QStringLiteral("idempotency")}});

    QJsonObject failure;
    failure.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    failure.insert(QStringLiteral("request_id"), requestId);
    failure.insert(QStringLiteral("message_type"), QStringLiteral("CorrectRun"));
    failure.insert(QStringLiteral("ok"), false);
    failure.insert(QStringLiteral("error"), error);
    failure.insert(QStringLiteral("payload"), error);

    view = mr::ipc::readEnvelope(failure);
    QVERIFY(!view.ok);
    QCOMPARE(view.errorCode, QStringLiteral("ERR_IDEMPOTENCY_CONFLICT"));
    QCOMPARE(view.errorMessage, QStringLiteral("conflict"));
    QCOMPARE(view.errorDetails.value(QStringLiteral("conflict")).toString(),
             QStringLiteral("idempotency"));
    QVERIFY(view.payload.isEmpty());

    // 3. The message_type == "Error" branch, with no "error" object at all:
    //    the payload carries the error and the envelope is still a failure.
    QJsonObject unreadable;
    unreadable.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    unreadable.insert(QStringLiteral("request_id"), requestId);
    unreadable.insert(QStringLiteral("message_type"), QStringLiteral("Error"));
    unreadable.insert(QStringLiteral("payload"),
                      QJsonObject{{QStringLiteral("code"), QStringLiteral("ERR_BAD_REQUEST")},
                                  {QStringLiteral("message"), QStringLiteral("bad json")}});

    view = mr::ipc::readEnvelope(unreadable);
    QVERIFY(!view.ok);
    QCOMPARE(view.errorCode, QStringLiteral("ERR_BAD_REQUEST"));
    QCOMPARE(view.errorMessage, QStringLiteral("bad json"));

    // 4. An event: the payload is the LiveEvent, "kind" and all.
    QJsonObject event;
    event.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    event.insert(QStringLiteral("request_id"), requestId);
    event.insert(QStringLiteral("message_type"), QStringLiteral("Event"));
    event.insert(QStringLiteral("ok"), true);
    event.insert(QStringLiteral("payload"),
                 QJsonObject{{QStringLiteral("event_type"), QStringLiteral("DiagnosticsMessage")},
                             {QStringLiteral("kind"), QStringLiteral("stats_invalidated")},
                             {QStringLiteral("sequence"), 7}});

    view = mr::ipc::readEnvelope(event);
    QCOMPARE(view.kind, mr::ipc::EnvelopeKind::Event);
    QVERIFY(view.ok);
    QCOMPARE(view.payload.value(QStringLiteral("kind")).toString(),
             QStringLiteral("stats_invalidated"));

    // 5. A failure with no readable code still names one.
    QJsonObject codeless;
    codeless.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    codeless.insert(QStringLiteral("request_id"), requestId);
    codeless.insert(QStringLiteral("message_type"), QStringLiteral("GetStatus"));
    codeless.insert(QStringLiteral("ok"), false);
    codeless.insert(QStringLiteral("payload"), QJsonObject{});

    view = mr::ipc::readEnvelope(codeless);
    QVERIFY(!view.ok);
    QCOMPARE(view.errorCode, QStringLiteral("ERR_INTERNAL"));
}

void DesktopTests::ipcEnvelope_toleratesAdditiveFields()
{
    // The contract only ever grows within protocol_version 1, so a field this
    // build has never heard of must be carried, not rejected.
    QJsonObject envelope;
    envelope.insert(QStringLiteral("protocol_version"), mr::ipc::kProtocolVersion);
    envelope.insert(QStringLiteral("request_id"),
                    QStringLiteral("22222222-2222-2222-2222-222222222222"));
    envelope.insert(QStringLiteral("message_type"), QStringLiteral("GetCurrentRun"));
    envelope.insert(QStringLiteral("ok"), true);
    envelope.insert(QStringLiteral("server_hint"), QStringLiteral("from a newer build"));
    envelope.insert(QStringLiteral("payload"),
                    QJsonObject{{QStringLiteral("state"), QStringLiteral("IDLE")},
                                {QStringLiteral("run"), QJsonValue::Null},
                                {QStringLiteral("future_field"), 42}});

    const mr::ipc::EnvelopeView view = mr::ipc::readEnvelope(envelope);
    QVERIFY(view.ok);
    QCOMPARE(view.messageType, QStringLiteral("GetCurrentRun"));
    QCOMPARE(view.payload.value(QStringLiteral("state")).toString(), QStringLiteral("IDLE"));
    QCOMPARE(view.payload.value(QStringLiteral("future_field")).toInt(), 42);

    // A frame carrying such a field still round-trips through the framing layer.
    QByteArray buffer = mr::ipc::encodeFrame(envelope);
    QJsonObject decoded;
    QCOMPARE(mr::ipc::takeFrame(buffer, decoded), mr::ipc::FrameStatus::Ok);
    QCOMPARE(decoded, envelope);

    // An envelope that predates "ok" entirely is read as a success, not as an
    // error with no code.
    QJsonObject legacy = envelope;
    legacy.remove(QStringLiteral("ok"));
    QVERIFY(mr::ipc::readEnvelope(legacy).ok);

    // The timestamp helper produces the contract's $defs/UtcTimestamp shape.
    const QString stamp = mr::ipc::utcTimestamp(
        QDateTime(QDate(2026, 9, 4), QTime(11, 22, 33, 456), QTimeZone::UTC));
    QCOMPARE(stamp, QStringLiteral("2026-09-04T11:22:33.456Z"));
}

void DesktopTests::formatters_renderNullsAndDurations()
{
    QCOMPARE(mr::Formatters::dash(), QString::fromUtf8("—"));
    QCOMPARE(mr::Formatters::duration(QVariant()), QString::fromUtf8("—"));
    QCOMPARE(mr::Formatters::duration(-1), QString::fromUtf8("—"));
    QCOMPARE(mr::Formatters::duration(65 * 1000), QStringLiteral("01:05"));
    QCOMPARE(mr::Formatters::duration((2 * 3600 + 3 * 60 + 4) * 1000),
             QStringLiteral("2:03:04"));
    QCOMPARE(mr::Formatters::percent(QVariant()), QString::fromUtf8("—"));
    QCOMPARE(mr::Formatters::percent(0.996, 1), QStringLiteral("99.6%"));
    QCOMPARE(mr::Formatters::count(42), QStringLiteral("42"));
    QCOMPARE(mr::Formatters::resultLabel(QStringLiteral("DISCONNECTED")),
             QString::fromUtf8("断线"));
    // A duty still being played is stored as UNKNOWN with no end time. The history page read
    // 未知 for it, which is what a finished run with an unconfirmed outcome reads: two different
    // things, and the player asked why a run in progress already had a result.
    const QVariantMap inFlight{{QStringLiteral("source"), QStringLiteral("AUTO_NETWORK")},
                               {QStringLiteral("result"), QStringLiteral("UNKNOWN")},
                               {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-19T04:44:18.000Z")},
                               {QStringLiteral("ended_at_utc"), QVariant()}};
    QVERIFY(mr::Formatters::runInProgress(inFlight));
    QCOMPARE(mr::Formatters::runResultLabel(inFlight), QString::fromUtf8("进行中"));
    QVariantMap finished = inFlight;
    finished.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-19T05:01:20.000Z"));
    QVERIFY(!mr::Formatters::runInProgress(finished));
    QCOMPARE(mr::Formatters::runResultLabel(finished), QString::fromUtf8("未知"));
    QVariantMap recovered = inFlight;
    recovered.insert(QStringLiteral("pending_review"), true);
    QVERIFY(!mr::Formatters::runInProgress(recovered));
    QVariantMap manual = inFlight;
    manual.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
    QVERIFY(!mr::Formatters::runInProgress(manual));
    // Live-run states and confidence never reach the player as raw tokens.
    QCOMPARE(mr::Formatters::stateLabel(QStringLiteral("IDLE")),
             QString::fromUtf8("空闲 · 等待匹配"));
    QCOMPARE(mr::Formatters::stateLabel(QStringLiteral("MENTOR_MATCHED")),
             QString::fromUtf8("已匹配导随"));
    QCOMPARE(mr::Formatters::stateLabel(QStringLiteral("ENTERED_DUTY")),
             QString::fromUtf8("已进入副本"));
    QCOMPARE(mr::Formatters::stateLabel(QStringLiteral("UNKNOWN_FINAL_STATE")),
             QString::fromUtf8("未知"));
    QCOMPARE(mr::Formatters::confidenceLabel(QStringLiteral("HIGH")), QString::fromUtf8("高"));
    QCOMPARE(mr::Formatters::confidenceLabel(QString()), QString::fromUtf8("—"));

    // 最近有效事件 on the capture page: every contract token has a Chinese name;
    // null and a token this build does not know give nothing (time only).
    const QList<QPair<const char *, const char *>> kinds{
        {"CONTENT_FINDER_POP", "匹配成功"}, {"ZONE_INITIALIZATION", "进入区域"},
        {"ZONE_TERRITORY", "识别所在区域"}, {"DUTY_RESULT", "副本结算"},
        {"PLAYER_JOB", "识别职业"}, {"ZONE_LEFT", "离开副本区域"},
        {"INSTANCE_LEFT", "退出副本"}, {"MATCH_CANCELLED", "匹配取消"}};
    for (const auto &kind : kinds) {
        QCOMPARE(mr::Formatters::eventKindLabel(QString::fromLatin1(kind.first)),
                 QString::fromUtf8(kind.second));
    }
    QVERIFY(mr::Formatters::eventKindLabel(QString()).isEmpty());
    QVERIFY(mr::Formatters::eventKindLabel(QStringLiteral("SOMETHING_NEW")).isEmpty());

    // 最近失败 rows: a Chinese sentence per refusal code, never hex or a token.
    for (const char *code : {"E_UNKNOWN_OPCODE", "E_LEN_MISMATCH", "E_OFFSET_OOB",
                             "E_FIELD_CONSTRAINT", "E_PROFILE_UNSUPPORTED", "E_INTERNAL",
                             "E_FROM_THE_FUTURE"}) {
        const QString label = mr::Formatters::parserErrorLabel(QString::fromLatin1(code));
        QVERIFY2(!label.isEmpty(), code);
        QVERIFY2(!label.contains(QStringLiteral("0x")) && !label.contains(QLatin1Char('_')), code);
    }
    QCOMPARE(mr::Formatters::parserErrorLabel(QStringLiteral("E_LEN_MISMATCH")),
             QString::fromUtf8("报文长度与档案不符，已忽略"));
}

void DesktopTests::runListModel_filtersSortsAndPages()
{
    mr::MockBackend backend;
    mr::RunListModel model;
    model.setBackend(&backend);

    model.reload();
    QTRY_VERIFY_WITH_TIMEOUT(model.rowCount() > 0, 3000);
    QCOMPARE(model.page(), 1);
    QCOMPARE(model.pageSize(), 10);
    QVERIFY(model.total() >= model.rowCount());

    model.sortBy(QStringLiteral("duty_name"));
    QTRY_COMPARE_WITH_TIMEOUT(model.sortField(), QStringLiteral("duty_name"), 3000);
    QCOMPARE(model.sortAscending(), false);

    model.sortBy(QStringLiteral("duty_name"));
    QTRY_VERIFY_WITH_TIMEOUT(model.sortAscending(), 3000);

    QVariantMap correctedOnly;
    correctedOnly.insert(QStringLiteral("corrected_only"), true);
    model.setFilter(correctedOnly);
    QTRY_COMPARE_WITH_TIMEOUT(model.total(), 1, 3000);
    QCOMPARE(model.rowCount(), 1);
    QVERIFY(model.runAt(0).value(QStringLiteral("manually_corrected")).toBool());

    QVariantMap includeDeleted;
    includeDeleted.insert(QStringLiteral("include_deleted"), true);
    model.setFilter(includeDeleted);
    QTRY_VERIFY_WITH_TIMEOUT(model.total() > 90, 3000);
    QVERIFY(model.runAt(0).contains(QStringLiteral("run_id")));
}

void DesktopTests::jobStatsModel_roleBreakdownKeepsFixedOrder()
{
    mr::MockBackend backend;
    mr::JobStatsModel model;
    model.setBackend(&backend);

    model.reload();
    QTRY_VERIFY_WITH_TIMEOUT(model.rowCount() > 0, 3000);

    const QVariantList roles = model.roleBreakdown();
    QCOMPARE(roles.size(), 6);
    QCOMPARE(firstRoleGroup(roles, 0), QString::fromUtf8("坦克"));
    QCOMPARE(firstRoleGroup(roles, 1), QString::fromUtf8("治疗"));
    QCOMPARE(firstRoleGroup(roles, 5), QString::fromUtf8("未知"));
    QCOMPARE(sumAttempts(roles), model.totalAttemptCount());
}

void DesktopTests::roleCatalog_mapsEveryRoleToAnExistingIcon()
{
    mr::RoleCatalog roles;

    const QStringList groups = mr::RoleCatalog::roleGroups();
    QCOMPARE(groups.size(), 6);
    QCOMPARE(groups.first(), QString::fromUtf8("坦克"));
    QCOMPARE(groups.last(), QString::fromUtf8("未知"));

    // The legend order must line up with JobStatsModel::roleBreakdown().
    const QStringList expectedKeys{QStringLiteral("tank"),
                                   QStringLiteral("healer"),
                                   QStringLiteral("melee"),
                                   QStringLiteral("ranged"),
                                   QStringLiteral("magic"),
                                   QStringLiteral("allrounder")};

    for (int i = 0; i < groups.size(); ++i) {
        const QString &group = groups.at(i);
        QCOMPARE(mr::RoleCatalog::roleKey(group), expectedKeys.at(i));

        const QString resource = roles.roleIconResource(group);
        QCOMPARE(resource,
                 QStringLiteral(":/resources/icons/roles/") + expectedKeys.at(i)
                     + QStringLiteral(".png"));
        // Game art is not bundled in a distributable build: the source is then empty and
        // RoleIcon draws its badge. A personal build (MR_BUNDLE_GAME_ICONS) resolves it.
        const QString source = roles.roleIconSource(group);
        if (QFile::exists(resource)) {
            QVERIFY(source.startsWith(QStringLiteral("qrc:/resources/icons/roles/"))
                    || source.startsWith(QStringLiteral("file:")));
        } else {
            QVERIFY(source.isEmpty() || source.startsWith(QStringLiteral("file:")));
        }
    }

    // Anything unmapped - a null role, an empty string, the coarse DPS bucket,
    // a job the catalogue has never seen - falls back to All-Rounder.
    const QString allrounder = roles.roleIconResource(QString::fromUtf8("未知"));
    QCOMPARE(roles.roleIconResource(QString()), allrounder);
    QCOMPARE(roles.roleIconResource(QStringLiteral("DPS")), allrounder);
    QCOMPARE(roles.roleIconResource(QString::fromUtf8("其他")), allrounder);
    QCOMPARE(mr::RoleCatalog::roleKey(QString()), QStringLiteral("allrounder"));

    // Coarse English aliases still resolve to the right glyph.
    QCOMPARE(mr::RoleCatalog::roleKey(QStringLiteral("TANK")), QStringLiteral("tank"));
    QCOMPARE(mr::RoleCatalog::roleKey(QStringLiteral("HEALER")), QStringLiteral("healer"));

    // The manifest that drives the mapping ships with the icons.
    QVERIFY(QFile::exists(QStringLiteral(":/resources/icons/roles/manifest.json")));
}

void DesktopTests::jobStatsModel_derivesRoleGroupFromTheContractFields()
{
    const auto group = [](const QJsonObject &row) {
        return mr::JobStatsModel::roleGroupOf(row);
    };

    // A backend that already speaks the prototype's groups wins outright.
    QCOMPARE(group({{QStringLiteral("role_group"), QString::fromUtf8("远程物理")},
                    {QStringLiteral("job_id"), 19}}),
             QString::fromUtf8("远程物理"));

    // ipc-v1's $defs/JobStatsRow has job_id and the coarse role only; the fine
    // group comes from the shipping job catalogue.
    QCOMPARE(group({{QStringLiteral("job_id"), 19}, {QStringLiteral("role"), QStringLiteral("TANK")}}),
             QString::fromUtf8("坦克"));
    QCOMPARE(group({{QStringLiteral("job_id"), 24},
                    {QStringLiteral("role"), QStringLiteral("HEALER")}}),
             QString::fromUtf8("治疗"));

    // No job_id: the coarse role is all there is. TANK and HEALER still map;
    // a DPS whose job is unknown must stay 未知 rather than be guessed into a
    // melee / ranged / magic bucket.
    QCOMPARE(group({{QStringLiteral("job_id"), QJsonValue::Null},
                    {QStringLiteral("role"), QStringLiteral("TANK")}}),
             QString::fromUtf8("坦克"));
    QCOMPARE(group({{QStringLiteral("job_id"), QJsonValue::Null},
                    {QStringLiteral("role"), QStringLiteral("DPS")}}),
             QString::fromUtf8("未知"));
    QCOMPARE(group({{QStringLiteral("job_id"), QJsonValue::Null},
                    {QStringLiteral("role"), QStringLiteral("UNKNOWN")}}),
             QString::fromUtf8("未知"));
    QCOMPARE(group({}), QString::fromUtf8("未知"));
}

void DesktopTests::appController_rebuildsDutyOptionsAndHandlesCaptureFailure()
{
    mr::MockBackend backend;
    backend.setNpcapMissing(true);

    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(!controller.dutyOptions().isEmpty(), 3000);
    QCOMPARE(controller.npcapInstalled(), false);

    // The non-VERIFIED profile routes to validation even when cached Npcap
    // status is missing; the persistent error is still the backend's own
    // ERR_NPCAP_MISSING refusal, not a sentence invented by the UI.
    QSignalSpy refusals(&controller, &mr::AppController::mutationFailed);
    controller.toggleCapture();
    QTRY_COMPARE_WITH_TIMEOUT(controller.toastMessage(),
                              QString::fromUtf8("未检测到 Npcap，无法开始验证。"), 3000);
    QCOMPARE(refusals.count(), 1);
    QCOMPARE(refusals.at(0).at(0).toString(), QStringLiteral("ERR_NPCAP_MISSING"));

    mr::MockBackend runningBackend;
    mr::AppController runningController(&runningBackend, nullptr);
    runningController.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(runningController.capturing(), 3000);

    runningController.toggleCapture();
    QTRY_VERIFY_WITH_TIMEOUT(!runningController.capturing(), 3000);
}

void DesktopTests::appController_subscribesAndRefreshesAfterDelayedBackendConnect()
{
    DeferredBackend backend;
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);

    QCOMPARE(backend.subscribeCalls(), 0);
    QVERIFY(!controller.backendConnected());
    QCOMPARE(controller.npcapInstalled(), false);

    backend.setConnected(true);

    QTRY_VERIFY_WITH_TIMEOUT(controller.backendConnected(), 3000);
    QTRY_COMPARE_WITH_TIMEOUT(backend.subscribeCalls(), 1, 3000);
    QTRY_COMPARE_WITH_TIMEOUT(controller.npcapInstalled(), true, 3000);
    QVERIFY(backend.seenMessages().contains(QStringLiteral("GetStatus")));
    QVERIFY(backend.seenMessages().contains(QStringLiteral("GetCurrentRun")));
}

void DesktopTests::appController_routesValidationAndBlocksDuplicateCommands()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);

    QTRY_VERIFY_WITH_TIMEOUT(controller.validationStatusLoaded(), 3000);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);
    QCOMPARE(controller.protocolProfileStatus(), QStringLiteral("UNVERIFIED"));

    backend.setAutoRespond(false);
    controller.setCaptureAdapterId(QStringLiteral("NPF_TEST_ADAPTER"));
    controller.toggleCapture();
    QCOMPARE(backend.count(QStringLiteral("StartCaptureValidation")), 1);
    QCOMPARE(backend.seenPayloads().last().value(QStringLiteral("adapter_id")).toString(),
             QStringLiteral("NPF_TEST_ADAPTER"));
    QVERIFY(controller.captureCommandBusy());
    QVERIFY(!controller.captureActionEnabled());

    controller.toggleCapture();
    QCOMPARE(backend.count(QStringLiteral("StartCaptureValidation")), 1);

    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("WAITING"),
                                            QStringLiteral("WAITING_GAME")));
    QCOMPARE(controller.validationState(), QStringLiteral("WAITING"));
    QVERIFY(controller.validationActive());
    QCOMPARE(controller.captureActionLabel(), QString::fromUtf8("取消等待"));
    QVERIFY(controller.captureActionEnabled());
    QVERIFY(controller.validationNotice().contains(QString::fromUtf8("仅验证")));

    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StopCaptureValidation"),
                         validationSnapshot(QStringLiteral("IDLE"),
                                            QStringLiteral("CANCELLED")));
    QVERIFY(!controller.validationSaved());
    QCOMPARE(controller.captureModeStatusText(),
             QString::fromUtf8("已取消等待，未创建取证文件"));
}

void DesktopTests::appController_handlesSynchronousValidationFailure()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setSyncFailureType(QStringLiteral("StartCaptureValidation"));
    QSignalSpy failures(&controller, &mr::AppController::mutationFailed);
    controller.toggleCapture();

    QCOMPARE(failures.count(), 1);
    QCOMPARE(failures.at(0).at(0).toString(), QStringLiteral("ERR_SYNC_TEST"));
    QCOMPARE(controller.validationError(), QString::fromUtf8("同步拒绝测试"));
    QVERIFY(!controller.captureCommandBusy());
}

void DesktopTests::appController_persistentlyReportsUnsupportedOldCollector()
{
    DeferredBackend backend;
    backend.setSyncFailure(QStringLiteral("GetCaptureValidationStatus"),
                           QStringLiteral("ERR_BAD_REQUEST"),
                           QString::fromUtf8("未知消息类型"));
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);

    QTRY_VERIFY_WITH_TIMEOUT(controller.validationStatusLoaded(), 3000);
    QVERIFY(!controller.validationAvailable());
    QVERIFY(!controller.captureActionEnabled());
    QVERIFY(controller.validationError().contains(QString::fromUtf8("不支持桌面验证")));

    DeferredBackend verifiedBackend;
    verifiedBackend.setProfileStatus(QStringLiteral("VERIFIED"));
    verifiedBackend.setSyncFailure(QStringLiteral("GetCaptureValidationStatus"),
                                   QStringLiteral("ERR_BAD_REQUEST"),
                                   QString::fromUtf8("未知消息类型"));
    verifiedBackend.setConnected(true);
    mr::AppController verifiedController(&verifiedBackend, nullptr);
    verifiedController.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(verifiedController.validationStatusLoaded(), 3000);
    QTRY_VERIFY_WITH_TIMEOUT(verifiedController.captureActionEnabled(), 3000);
    verifiedBackend.setAutoRespond(false);
    verifiedController.toggleCapture();
    QCOMPARE(verifiedBackend.count(QStringLiteral("StartCapture")), 1);
    QCOMPARE(verifiedBackend.count(QStringLiteral("StartCaptureValidation")), 0);
}

void DesktopTests::appController_ignoresStaleValidationStatusAfterStop()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setAutoRespond(false);
    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("WAITING"),
                                            QStringLiteral("WAITING_RESTART")));

    controller.refreshCaptureValidation();
    controller.refreshCaptureValidation();
    QCOMPARE(backend.count(QStringLiteral("GetCaptureValidationStatus")), 2);
    controller.toggleCapture();
    QCOMPARE(backend.count(QStringLiteral("StopCaptureValidation")), 1);
    backend.completeNext(QStringLiteral("StopCaptureValidation"),
                         validationSnapshot(QStringLiteral("STOPPING")));
    QCOMPARE(controller.validationState(), QStringLiteral("STOPPING"));
    QVERIFY(!controller.captureActionEnabled());

    backend.completeNext(QStringLiteral("GetCaptureValidationStatus"), {}, false,
                         QStringLiteral("ERR_TIMEOUT"),
                         QString::fromUtf8("旧查询超时"));
    QCOMPARE(controller.validationState(), QStringLiteral("STOPPING"));
    QVERIFY(controller.validationAvailable());
}

void DesktopTests::appController_disconnectMakesValidationUnknownAndReconnectOnlyPolls()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setAutoRespond(false);
    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("WAITING"),
                                            QStringLiteral("WAITING_IDENTITY")));
    QCOMPARE(backend.count(QStringLiteral("StartCaptureValidation")), 1);

    backend.setConnected(false);
    QVERIFY(!controller.validationStatusLoaded());
    QVERIFY(controller.validationState().isEmpty());
    QVERIFY(!controller.captureActionEnabled());

    backend.setAutoRespond(true);
    backend.setConnected(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.validationStatusLoaded(), 3000);
    QCOMPARE(controller.validationState(), QStringLiteral("IDLE"));
    QCOMPARE(backend.count(QStringLiteral("StartCaptureValidation")), 1);
}

void DesktopTests::appController_keepsRecordingOnTransientStatusFailureAndRecovers()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setAutoRespond(false);
    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    controller.refreshCaptureValidation();
    backend.completeNext(QStringLiteral("GetCaptureValidationStatus"), {}, false,
                         QStringLiteral("ERR_INTERNAL"),
                         QString::fromUtf8("临时超时"));

    QCOMPARE(controller.validationState(), QStringLiteral("RECORDING"));
    QVERIFY(controller.validationAvailable());
    QVERIFY(controller.captureActionEnabled());
    QVERIFY(controller.validationError().contains(QString::fromUtf8("刷新失败")));

    controller.refreshCaptureValidation();
    backend.completeNext(QStringLiteral("GetCaptureValidationStatus"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    QCOMPARE(controller.validationState(), QStringLiteral("RECORDING"));
    QVERIFY(controller.validationError().isEmpty());
}

void DesktopTests::appController_gatesMarkersAndUsesFormalCaptureForVerifiedProfile()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    QSignalSpy failures(&controller, &mr::AppController::mutationFailed);
    controller.addCaptureValidationMarker(QStringLiteral("queued"));
    QCOMPARE(backend.count(QStringLiteral("AddCaptureValidationMarker")), 0);
    QCOMPARE(failures.count(), 1);

    backend.setAutoRespond(false);
    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    QVERIFY(controller.validationMarkerEnabled());

    controller.addCaptureValidationMarker(QStringLiteral("invalid"));
    QCOMPARE(backend.count(QStringLiteral("AddCaptureValidationMarker")), 0);
    controller.addCaptureValidationMarker(QStringLiteral("queued"));
    controller.addCaptureValidationMarker(QStringLiteral("pop"));
    QCOMPARE(backend.count(QStringLiteral("AddCaptureValidationMarker")), 1);
    QVERIFY(controller.markerCommandBusy());
    backend.completeNext(QStringLiteral("AddCaptureValidationMarker"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    QCOMPARE(controller.validationFeedback(), QString::fromUtf8("标记已接收"));
    QVERIFY(!controller.markerCommandBusy());

    DeferredBackend verifiedBackend;
    verifiedBackend.setProfileStatus(QStringLiteral("VERIFIED"));
    verifiedBackend.setConnected(true);
    mr::AppController verifiedController(&verifiedBackend, nullptr);
    verifiedController.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(verifiedController.captureActionEnabled(), 3000);
    QCOMPARE(verifiedController.protocolProfileStatus(), QStringLiteral("VERIFIED"));

    verifiedBackend.setAutoRespond(false);
    verifiedController.toggleCapture();
    QCOMPARE(verifiedBackend.count(QStringLiteral("StartCapture")), 1);
    QCOMPARE(verifiedBackend.count(QStringLiteral("StartCaptureValidation")), 0);
    verifiedBackend.completeNext(QStringLiteral("StartCapture"), {}, false,
                                 QStringLiteral("ERR_PROFILE_UNSUPPORTED"),
                                 QString::fromUtf8("档案已变更"));
    QCOMPARE(verifiedBackend.count(QStringLiteral("StartCaptureValidation")), 0);
    QVERIFY(!verifiedController.captureCommandBusy());
    QVERIFY(!verifiedController.validationError().isEmpty());
}

void DesktopTests::appController_stopWhileMarkerPendingDoesNotLatchBusy()
{
    DeferredBackend backend;
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setAutoRespond(false);
    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StartCaptureValidation"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    controller.addCaptureValidationMarker(QStringLiteral("queued"));
    QVERIFY(controller.markerCommandBusy());

    controller.toggleCapture();
    backend.completeNext(QStringLiteral("StopCaptureValidation"),
                         validationSnapshot(QStringLiteral("COMPLETED")));
    backend.completeNext(QStringLiteral("AddCaptureValidationMarker"),
                         validationSnapshot(QStringLiteral("RECORDING")));
    QVERIFY(!controller.markerCommandBusy());
    QCOMPARE(controller.validationState(), QStringLiteral("COMPLETED"));
}

void DesktopTests::appController_ignoresStaleFormalCallbacksAfterReconnect()
{
    DeferredBackend startBackend;
    startBackend.setProfileStatus(QStringLiteral("VERIFIED"));
    startBackend.setConnected(true);
    mr::AppController startController(&startBackend, nullptr);
    startController.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(startController.captureActionEnabled(), 3000);

    startBackend.setAutoRespond(false);
    startController.toggleCapture();
    QCOMPARE(startBackend.count(QStringLiteral("StartCapture")), 1);
    startBackend.setConnected(false);
    startBackend.setAutoRespond(true);
    startBackend.setConnected(true);
    QTRY_VERIFY_WITH_TIMEOUT(startController.captureActionEnabled(), 3000);
    startBackend.completeNext(QStringLiteral("StartCapture"), {}, false,
                              QStringLiteral("ERR_PROFILE_UNSUPPORTED"),
                              QString::fromUtf8("旧请求失败"));
    QCOMPARE(startBackend.count(QStringLiteral("StartCaptureValidation")), 0);

    DeferredBackend stopBackend;
    stopBackend.setProfileStatus(QStringLiteral("VERIFIED"));
    stopBackend.setCaptureState(QStringLiteral("RUNNING"));
    stopBackend.setConnected(true);
    mr::AppController stopController(&stopBackend, nullptr);
    stopController.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(stopController.capturing(), 3000);

    stopBackend.setAutoRespond(false);
    stopController.toggleCapture();
    QCOMPARE(stopBackend.count(QStringLiteral("StopCapture")), 1);
    stopBackend.setConnected(false);
    stopBackend.setCaptureState(QStringLiteral("STOPPED"));
    stopBackend.setAutoRespond(true);
    stopBackend.setConnected(true);
    QTRY_VERIFY_WITH_TIMEOUT(!stopController.capturing(), 3000);
    stopBackend.completeNext(QStringLiteral("StopCapture"),
                             QJsonObject{{QStringLiteral("state"),
                                          QStringLiteral("RUNNING")}});
    QVERIFY(!stopController.capturing());
}

void DesktopTests::appController_prioritizesCurrentFormalCaptureOverTerminalValidation()
{
    const QList<QJsonObject> terminalSnapshots{
        validationSnapshot(QStringLiteral("COMPLETED")),
        validationSnapshot(QStringLiteral("IDLE"), QStringLiteral("CANCELLED")),
        validationSnapshot(QStringLiteral("FAILED"))};

    for (const QJsonObject &terminal : terminalSnapshots) {
        DeferredBackend backend;
        backend.setProfileStatus(QStringLiteral("VERIFIED"));
        backend.setConnected(true);
        mr::AppController controller(&backend, nullptr);
        controller.setMaintainerToolsVisible(true);
        QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

        backend.setAutoRespond(false);
        controller.refreshCaptureValidation();
        backend.completeNext(QStringLiteral("GetCaptureValidationStatus"), terminal);
        QCOMPARE(controller.validationState(),
                 terminal.value(QStringLiteral("state")).toString());

        controller.toggleCapture();
        QCOMPARE(backend.count(QStringLiteral("StartCapture")), 1);
        backend.completeNext(QStringLiteral("StartCapture"),
                             QJsonObject{{QStringLiteral("state"),
                                          QStringLiteral("RUNNING")}});

        QVERIFY(controller.capturing());
        QCOMPARE(controller.captureActionLabel(), QString::fromUtf8("停止捕获"));
        QCOMPARE(controller.captureModeStatusText(),
                 QString::fromUtf8("正式记录监听中"));
        QCOMPARE(controller.captureModeCompactText(), QString::fromUtf8("正式监听中"));
        QCOMPARE(controller.validationStatus(), terminal.toVariantMap());
    }
}

void DesktopTests::appController_waitsForValidationModeBeforeNewStart()
{
    const QStringList activeStates{QStringLiteral("WAITING"),
                                   QStringLiteral("RECORDING")};
    for (const QString &state : activeStates) {
        DeferredBackend backend;
        backend.setProfileStatus(QStringLiteral("VERIFIED"));
        backend.setAutoRespond(false);
        mr::AppController controller(&backend, nullptr);
        controller.setMaintainerToolsVisible(true);
        backend.setConnected(true);

        backend.completeNext(QStringLiteral("GetProtocolProfileStatus"),
                             QJsonObject{{QStringLiteral("status"),
                                          QStringLiteral("VERIFIED")},
                                         {QStringLiteral("status_label"),
                                          QStringLiteral("VERIFIED")}});
        QCOMPARE(controller.protocolProfileStatus(), QStringLiteral("VERIFIED"));
        QVERIFY(!controller.validationStatusLoaded());
        QVERIFY(!controller.captureActionEnabled());
        QCOMPARE(controller.captureActionLabel(), QString::fromUtf8("正在确认状态…"));
        QCOMPARE(controller.captureModeStatusText(),
                 QString::fromUtf8("正在确认捕获状态"));
        QCOMPARE(controller.captureModeCompactText(), QString::fromUtf8("状态未知"));

        controller.toggleCapture();
        QCOMPARE(backend.count(QStringLiteral("StartCapture")), 0);
        QCOMPARE(backend.count(QStringLiteral("StartCaptureValidation")), 0);

        backend.completeNext(QStringLiteral("GetCaptureValidationStatus"), {}, false,
                             QStringLiteral("ERR_INTERNAL"),
                             QString::fromUtf8("首次查询暂时失败"));
        QVERIFY(!controller.validationStatusLoaded());
        QCOMPARE(controller.captureModeStatusText(),
                 QString::fromUtf8("正在确认捕获状态"));
        QCOMPARE(controller.captureModeCompactText(), QString::fromUtf8("状态未知"));

        controller.refreshCaptureValidation();
        backend.completeNext(QStringLiteral("GetCaptureValidationStatus"),
                             validationSnapshot(state));
        QVERIFY(controller.captureActionEnabled());
        QCOMPARE(controller.captureActionLabel(),
                 state == QLatin1String("WAITING") ? QString::fromUtf8("取消等待")
                                                   : QString::fromUtf8("停止验证"));
        QCOMPARE(controller.captureModeStatusText(),
                 state == QLatin1String("WAITING")
                     ? QString::fromUtf8("验证等待中（不自动记录）")
                     : QString::fromUtf8("验证取证中（不自动记录）"));
        QCOMPARE(controller.captureModeCompactText(),
                 state == QLatin1String("WAITING") ? QString::fromUtf8("验证等待中")
                                                   : QString::fromUtf8("验证取证中"));
        controller.toggleCapture();
        QCOMPARE(backend.count(QStringLiteral("StopCaptureValidation")), 1);
    }
}

void DesktopTests::appController_notifiesAggregateCaptureProjectionForFormalStatus()
{
    DeferredBackend backend;
    backend.setProfileStatus(QStringLiteral("VERIFIED"));
    backend.setConnected(true);
    mr::AppController controller(&backend, nullptr);
    controller.setMaintainerToolsVisible(true);
    QTRY_VERIFY_WITH_TIMEOUT(controller.captureActionEnabled(), 3000);

    backend.setAutoRespond(false);
    QSignalSpy projectionChanges(&controller, &mr::AppController::validationChanged);
    controller.refreshStatus();
    projectionChanges.clear();
    backend.completeNext(QStringLiteral("GetStatus"),
                         QJsonObject{{QStringLiteral("capture"),
                                      QJsonObject{{QStringLiteral("state"),
                                                   QStringLiteral("RUNNING")}}}});
    QCOMPARE(projectionChanges.count(), 1);
    QVERIFY(controller.capturing());
    QCOMPARE(controller.captureActionLabel(), QString::fromUtf8("停止捕获"));
    QVERIFY(controller.captureActionEnabled());

    controller.refreshStatus();
    projectionChanges.clear();
    backend.completeNext(QStringLiteral("GetStatus"),
                         QJsonObject{{QStringLiteral("capture"),
                                      QJsonObject{{QStringLiteral("state"),
                                                   QStringLiteral("STOPPED")}}}});
    QCOMPARE(projectionChanges.count(), 1);
    QVERIFY(!controller.capturing());
    QCOMPARE(controller.captureActionLabel(), QString::fromUtf8("开始捕获"));
    QVERIFY(controller.captureActionEnabled());
}

void DesktopTests::runFormValidator_rejectsEveryPrototypeCase()
{
    // The happy path first, so a later failure means the rule fired and not
    // that the fixture was broken to begin with.
    QVERIFY(mr::RunFormValidator::validate(baseForm())
                .value(QStringLiteral("ok"))
                .toBool());

    // ERR_REASON_REQUIRED - blank and whitespace-only both count as missing.
    QVariantMap noReason = baseForm();
    noReason.insert(QStringLiteral("reason"), QString());
    QCOMPARE(validationCode(noReason), QStringLiteral("ERR_REASON_REQUIRED"));
    noReason.insert(QStringLiteral("reason"), QStringLiteral("   "));
    QCOMPARE(validationCode(noReason), QStringLiteral("ERR_REASON_REQUIRED"));
    QVERIFY(mr::RunFormValidator::validate(noReason)
                .value(QStringLiteral("message"))
                .toString()
                .contains(QStringLiteral("ERR_REASON_REQUIRED")));

    // Matched time is mandatory.
    QVariantMap noMatched = baseForm();
    noMatched.insert(QStringLiteral("matched"), QString());
    QCOMPARE(validationCode(noMatched), QStringLiteral("ERR_BAD_REQUEST"));

    // ERR_TIME_ORDER - entry before the match.
    QVariantMap earlyEntry = baseForm();
    earlyEntry.insert(QStringLiteral("entered"), QStringLiteral("19:59:00"));
    QCOMPARE(validationCode(earlyEntry), QStringLiteral("ERR_TIME_ORDER"));

    // ERR_NEGATIVE_DURATION - end before the entry.
    QVariantMap negative = baseForm();
    negative.insert(QStringLiteral("ended"), QStringLiteral("20:00:30"));
    QCOMPARE(validationCode(negative), QStringLiteral("ERR_NEGATIVE_DURATION"));

    // ERR_TIME_ORDER - end before the match when there is no entry at all.
    QVariantMap cancelledBackwards = baseForm();
    cancelledBackwards.insert(QStringLiteral("result"),
                              QStringLiteral("CANCELLED_BEFORE_ENTRY"));
    cancelledBackwards.insert(QStringLiteral("entered"), QString());
    cancelledBackwards.insert(QStringLiteral("ended"), QStringLiteral("19:30:00"));
    QCOMPARE(validationCode(cancelledBackwards), QStringLiteral("ERR_TIME_ORDER"));

    // Entry time is required unless the run was cancelled before entry.
    QVariantMap missingEntry = baseForm();
    missingEntry.insert(QStringLiteral("entered"), QString());
    missingEntry.insert(QStringLiteral("result"), QStringLiteral("LEFT_OR_ABANDONED"));
    QCOMPARE(validationCode(missingEntry), QStringLiteral("ERR_BAD_REQUEST"));

    // ... and CANCELLED_BEFORE_ENTRY with no entry time is fine.
    QVariantMap cancelled = baseForm();
    cancelled.insert(QStringLiteral("entered"), QString());
    cancelled.insert(QStringLiteral("ended"), QStringLiteral("20:00:40"));
    cancelled.insert(QStringLiteral("result"), QStringLiteral("CANCELLED_BEFORE_ENTRY"));
    QVERIFY(mr::RunFormValidator::validate(cancelled)
                .value(QStringLiteral("ok"))
                .toBool());

    // A COMPLETED run must have an end time.
    QVariantMap noEnd = baseForm();
    noEnd.insert(QStringLiteral("ended"), QString());
    QCOMPARE(validationCode(noEnd), QStringLiteral("ERR_BAD_REQUEST"));

    // ERR_NO_CHANGES - "没有任何字段被修改。"
    QVariantMap unchanged = baseForm();
    unchanged.insert(QStringLiteral("edit_mode"), true);
    QVariantMap before = baseForm();
    before.remove(QStringLiteral("edit_mode"));
    QCOMPARE(validationCode(unchanged, before), QStringLiteral("ERR_NO_CHANGES"));
    QCOMPARE(mr::RunFormValidator::validate(unchanged, before)
                 .value(QStringLiteral("message"))
                 .toString(),
             QString::fromUtf8("\u6ca1\u6709\u4efb\u4f55\u5b57\u6bb5\u88ab\u4fee\u6539\u3002"));

    // One changed field is enough to make the same form valid.
    QVariantMap changed = unchanged;
    changed.insert(QStringLiteral("note"), QStringLiteral("fixed"));
    QVERIFY(mr::RunFormValidator::validate(changed, before)
                .value(QStringLiteral("ok"))
                .toBool());

    // Date shape.
    QVERIFY(mr::RunFormValidator::isValidDate(QStringLiteral("2026-09-04")));
    QVERIFY(!mr::RunFormValidator::isValidDate(QStringLiteral("2026-13-04")));
    QVERIFY(!mr::RunFormValidator::isValidDate(QStringLiteral("04/09/2026")));
    QVERIFY(!mr::RunFormValidator::isValidDate(QString()));
}

void DesktopTests::runFormValidator_buildsTheBeforeAfterDiff()
{
    QVariantMap before = baseForm();
    before.remove(QStringLiteral("edit_mode"));
    before.insert(QStringLiteral("result"), QStringLiteral("DISCONNECTED"));
    before.insert(QStringLiteral("ended"), QString());

    QVariantMap after = baseForm();
    after.insert(QStringLiteral("edit_mode"), true);

    const QVariantList rows = mr::RunFormValidator::diff(before, after);
    QCOMPARE(rows.size(), 2);
    QCOMPARE(rows.at(0).toMap().value(QStringLiteral("k")).toString(),
             QString::fromUtf8("\u7ed3\u675f\u65f6\u95f4"));
    QCOMPARE(rows.at(0).toMap().value(QStringLiteral("a")).toString(),
             QString::fromUtf8("\u2014"));
    QCOMPARE(rows.at(0).toMap().value(QStringLiteral("b")).toString(),
             QStringLiteral("20:20:00"));
    QCOMPARE(rows.at(1).toMap().value(QStringLiteral("k")).toString(),
             QString::fromUtf8("\u7ed3\u679c"));
    QCOMPARE(rows.at(1).toMap().value(QStringLiteral("a")).toString(),
             QString::fromUtf8("\u65ad\u7ebf"));
    QCOMPARE(rows.at(1).toMap().value(QStringLiteral("b")).toString(),
             QString::fromUtf8("\u901a\u5173"));

    // Identical inputs produce no rows at all.
    QVERIFY(mr::RunFormValidator::diff(before, before).isEmpty());
}

int main(int argc, char **argv)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QGuiApplication app(argc, argv);
    DesktopTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "DesktopTests.moc"
