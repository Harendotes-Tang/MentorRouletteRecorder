#include "MockBackend.h"

#include "IpcFraming.h"
#include "MockData.h"

#include <QRegularExpression>
#include <QSet>
#include <QStringList>

#include <algorithm>

using namespace mr::mock;

namespace {

QJsonObject badRequest(QString *code, QString *message, const QString &detail)
{
    *code = QStringLiteral("ERR_BAD_REQUEST");
    *message = detail;
    return {};
}

bool hasUnknownKeys(const QJsonObject &payload, const QStringList &allowed)
{
    for (auto it = payload.begin(); it != payload.end(); ++it) {
        if (!allowed.contains(it.key()))
            return true;
    }
    return false;
}

bool isUuid(const QJsonValue &value)
{
    static const QRegularExpression pattern(QStringLiteral(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"));
    return value.isString() && pattern.match(value.toString()).hasMatch();
}

bool isOptionalUtc(const QJsonValue &value)
{
    return value.isUndefined() || value.isNull()
           || (value.isString() && value.toString().endsWith(QLatin1Char('Z'))
               && fromIso(value).isValid());
}

QString hash12(const QString &seed)
{
    return QString::fromLatin1(QCryptographicHash::hash(seed.toUtf8(),
                                                       QCryptographicHash::Sha256)
                                  .toHex().left(12));
}

} // namespace

namespace mr {

void MockBackend::setCandidateFixture()
{
    m_candidateObservations = {};
    m_candidateReviews = {};
    m_captureSettings.insert(QStringLiteral("candidate_validation_enabled"), true);
    m_captureSettings.insert(QStringLiteral("research_payload_opcodes"), QJsonArray());
    m_capturing = false;
    m_liveMode = LiveMode::None;

    // Rows are synthetic metadata only. The newer session has 240 entries:
    // connection 1's anchors at indices 25 and 255 straddle a 200-row query page.
    for (int i = 0; i < 260; ++i) {
        const bool older = i < 20;
        const int sessionOrdinal = older ? 1 : 2;
        const int ordinal = older ? i : i - 20;
        const QDateTime started = older ? m_now.addDays(-1).addSecs(-3600)
                                       : m_now.addSecs(-3600);
        const qint64 elapsed = qint64(ordinal + 1) * 15000;
        const QDateTime at = started.addMSecs(elapsed);
        const bool anchor = i == 2 || i == 17 || i == 25 || i == 35 || i == 248
                            || i == 251 || i == 255 || i == 259;
        int connection = ordinal % 3 + 1;
        if (i == 2 || i == 17 || i == 25 || i == 255)
            connection = 1;
        else if (i == 35 || i == 248)
            connection = 2;
        else if (i == 251 || i == 259)
            connection = 3;

        const bool registration = !anchor && i % 4 == 0;
        const QString id = mockUuid(QStringLiteral("candidate-observation-%1").arg(i));
        QJsonObject row{
            {QStringLiteral("observation_id"), id},
            {QStringLiteral("capture_session_id"),
             mockUuid(QStringLiteral("candidate-session-%1").arg(sessionOrdinal))},
            {QStringLiteral("profile_id"), QStringLiteral("cn.2026.08.05.candidate")},
            {QStringLiteral("hypothesis_name"),
             anchor ? QStringLiteral("ZONE_LOAD")
                    : registration ? QStringLiteral("QUEUE_REGISTRATION")
                                   : QStringLiteral("FINDER_STATE_NOTIFICATION")},
            {QStringLiteral("group_name"),
             anchor ? QStringLiteral("zone_load")
                    : registration ? QStringLiteral("queue") : QStringLiteral("finder")},
            {QStringLiteral("direction"),
             anchor ? QStringLiteral("NONE")
                    : registration ? QStringLiteral("C2S") : QStringLiteral("S2C")},
            {QStringLiteral("opcode"),
             anchor ? QJsonValue(QJsonValue::Null) : QJsonValue(registration ? 0x03bb : 0x0323)},
            {QStringLiteral("payload_length"),
             anchor ? QJsonValue(QJsonValue::Null) : QJsonValue(registration ? 128 : 40)},
            {QStringLiteral("payload_hash12"),
             anchor ? QJsonValue(QJsonValue::Null) : QJsonValue(hash12(id))},
            {QStringLiteral("connection_tag"),
             hash12(QStringLiteral("candidate-session-%1-connection-%2")
                        .arg(sessionOrdinal).arg(connection))},
            {QStringLiteral("observed_at_utc"), isoUtc(at)},
            {QStringLiteral("t_ms"), double(elapsed)},
            {QStringLiteral("first_observed_at_utc"),
             anchor ? QJsonValue(isoUtc(at.addMSecs(-2000))) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("last_observed_at_utc"),
             anchor ? QJsonValue(isoUtc(at)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("first_t_ms"),
             anchor ? QJsonValue(double(elapsed - 2000)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("last_t_ms"),
             anchor ? QJsonValue(double(elapsed)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("review_verdict"), QJsonValue(QJsonValue::Null)},
            {QStringLiteral("review_note"), QJsonValue(QJsonValue::Null)},
            {QStringLiteral("reviewed_at_utc"), QJsonValue(QJsonValue::Null)},
        };
        if (i % 4 != 0) {
            const QString verdict = i % 4 == 1 ? QStringLiteral("CORRECT")
                                    : i % 4 == 2 ? QStringLiteral("WRONG")
                                                 : QStringLiteral("UNSURE");
            const QString note = i % 4 == 1 ? QString::fromUtf8("模拟核对：与回忆中的弹窗时间一致")
                                 : i % 4 == 2 ? QString::fromUtf8("模拟核对：此时只是传送")
                                              : QString::fromUtf8("模拟核对：无法确认对应事件");
            row.insert(QStringLiteral("review_verdict"), verdict);
            row.insert(QStringLiteral("review_note"), note);
            row.insert(QStringLiteral("reviewed_at_utc"), isoUtc(m_now));
            m_candidateReviews.append(QJsonObject{
                {QStringLiteral("review_id"), mockUuid(QStringLiteral("candidate-review-%1").arg(i))},
                {QStringLiteral("observation_id"), id},
                {QStringLiteral("request_id"), mockUuid(QStringLiteral("candidate-request-%1").arg(i))},
                {QStringLiteral("verdict"), verdict},
                {QStringLiteral("note"), note},
                {QStringLiteral("reviewed_at_utc"), isoUtc(m_now)},
            });
        }
        m_candidateObservations.append(row);
    }
    // Fixture selection has no state-machine/live-event side effects.
    Q_EMIT connectionChanged();
}

QJsonObject MockBackend::queryCandidatePayload(const QJsonObject &payload,
                                               QString *errorCode,
                                               QString *errorMessage) const
{
    const QJsonValue session = payload.value(QStringLiteral("session_id"));
    const QJsonValue fromValue = payload.value(QStringLiteral("from_utc"));
    const QJsonValue toValue = payload.value(QStringLiteral("to_utc"));
    const QDateTime from = fromIso(fromValue);
    const QDateTime to = fromIso(toValue);
    const QJsonValue pageValue = payload.value(QStringLiteral("page"));
    const QJsonValue sizeValue = payload.value(QStringLiteral("page_size"));
    const int page = pageValue.isUndefined() ? 1 : pageValue.toInt(-1);
    const int size = sizeValue.isUndefined() ? 50 : sizeValue.toInt(-1);
    if (hasUnknownKeys(payload, {QStringLiteral("session_id"), QStringLiteral("from_utc"),
                                 QStringLiteral("to_utc"), QStringLiteral("page"),
                                 QStringLiteral("page_size")})
        || (!session.isUndefined() && !session.isNull() && !isUuid(session))
        || !isOptionalUtc(fromValue) || !isOptionalUtc(toValue)
        || (from.isValid() && to.isValid() && from > to)
        || page < 1 || page > 1000000 || size < 1 || size > 200) {
        return badRequest(errorCode, errorMessage, QString::fromUtf8("候选查询条件无效。"));
    }

    QList<QJsonObject> selected;
    for (const QJsonValue &value : m_candidateObservations) {
        const QJsonObject row = value.toObject();
        const QDateTime at = fromIso(row.value(QStringLiteral("observed_at_utc")));
        if ((!session.isString()
             || row.value(QStringLiteral("capture_session_id")) == session)
            && (!from.isValid() || at >= from) && (!to.isValid() || at <= to)) {
            selected.append(row);
        }
    }
    std::sort(selected.begin(), selected.end(), [](const QJsonObject &a, const QJsonObject &b) {
        const QString left = a.value(QStringLiteral("observed_at_utc")).toString();
        const QString right = b.value(QStringLiteral("observed_at_utc")).toString();
        return left == right ? a.value(QStringLiteral("observation_id")).toString()
                                   > b.value(QStringLiteral("observation_id")).toString()
                             : left > right;
    });
    QJsonArray items;
    const int offset = (page - 1) * size;
    const int end = qMin(offset + size, int(selected.size()));
    for (int i = offset; i < end; ++i)
        items.append(selected.at(i));
    return {{QStringLiteral("items"), items},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page},
                                                       {QStringLiteral("page_size"), size},
                                                       {QStringLiteral("total"), int(selected.size())}}}};
}

QJsonObject MockBackend::reviewCandidatePayload(const QJsonObject &payload,
                                                QString *errorCode,
                                                QString *errorMessage)
{
    const QJsonValue id = payload.value(QStringLiteral("observation_id"));
    const QString verdict = payload.value(QStringLiteral("verdict")).toString();
    const QJsonValue noteValue = payload.value(QStringLiteral("note"));
    if (hasUnknownKeys(payload, {QStringLiteral("observation_id"), QStringLiteral("verdict"),
                                 QStringLiteral("note")})
        || !isUuid(id) || (verdict != QLatin1String("CORRECT") && verdict != QLatin1String("WRONG")
                          && verdict != QLatin1String("UNSURE"))
        || (!noteValue.isUndefined() && !noteValue.isNull() && !noteValue.isString())
        || noteValue.toString().size() > 2000) {
        return badRequest(errorCode, errorMessage, QString::fromUtf8("核对结论无效，备注最多 2000 字符。"));
    }
    const QString note = noteValue.toString().trimmed();
    const QJsonValue storedNote = note.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(note);
    for (qsizetype i = 0; i < m_candidateObservations.size(); ++i) {
        QJsonObject row = m_candidateObservations.at(i).toObject();
        if (row.value(QStringLiteral("observation_id")) != id)
            continue;
        const QString at = isoUtc(m_now);
        row.insert(QStringLiteral("review_verdict"), verdict);
        row.insert(QStringLiteral("review_note"), storedNote);
        row.insert(QStringLiteral("reviewed_at_utc"), at);
        m_candidateObservations.replace(i, row);
        m_candidateReviews.append(QJsonObject{
            {QStringLiteral("review_id"), ipc::newRequestId()},
            {QStringLiteral("observation_id"), id},
            {QStringLiteral("request_id"), ipc::newRequestId()},
            {QStringLiteral("verdict"), verdict},
            {QStringLiteral("note"), storedNote},
            {QStringLiteral("reviewed_at_utc"), at},
        });
        return {{QStringLiteral("observation_id"), id},
                {QStringLiteral("review_verdict"), verdict},
                {QStringLiteral("reviewed_at_utc"), at}};
    }
    *errorCode = QStringLiteral("ERR_CANDIDATE_OBSERVATION_NOT_FOUND");
    *errorMessage = QString::fromUtf8("候选观测不存在，请刷新列表。");
    return {};
}

QJsonObject MockBackend::exportCandidatePayload(const QJsonObject &payload) const
{
    // The mock returns the contract shape without creating evidence files.
    // Both displayed paths explicitly identify the result as a simulation.
    const QString label = QString::fromUtf8("（模拟后端未写入文件）");
    const QString path = label + payload.value(QStringLiteral("target_path")).toString();
    return {{QStringLiteral("target_path"), path},
            {QStringLiteral("sha256_path"), label},
            {QStringLiteral("sha256"), QString(64, QLatin1Char('0'))},
            {QStringLiteral("observation_count"), int(m_candidateObservations.size())},
            {QStringLiteral("review_count"), int(m_candidateReviews.size())},
            {QStringLiteral("byte_count"), 0},
            {QStringLiteral("completed_at_utc"), isoUtc(m_now)}};
}

void MockBackend::emitCaptureStatusChanged()
{
    QVariantMap event = liveEventEnvelope(QStringLiteral("CaptureStatusChanged"),
                                          QStringLiteral("collector_status"));
    event.insert(QStringLiteral("capture"), captureStatus().toVariantMap());
    Q_EMIT liveEvent(event);
}

// ---------------------------------------------------------------------------
// 本机校准 fixtures.
//
// The timeline below is the one section 3 step 4 describes, carrying the event
// ids of tests/Fixtures/ipc-requests/ConfirmCalibration.json so that clicking
// through the mock produces exactly the committed request sample. Nothing here
// was ever observed: this backend has no capture pipeline, writes no profile,
// and its "enabled" answer only moves a string in memory.
// ---------------------------------------------------------------------------

void MockBackend::setCalibrationFixture(const QString &state)
{
    m_calibrationState = state;
}

bool MockBackend::localProfileBound() const
{
    return m_calibrationState == QLatin1String("done") || m_calibrationState == QLatin1String("idle");
}

QJsonObject MockBackend::calibrationStatus() const
{
    if (m_calibrationState.isEmpty())
        return {};

    // The state after a restart: the local profile records, the coordinator holds no
    // template and observes nothing, so every field of the calibration itself is empty
    // and only 共享校准 is reported beside it. The calibration card is not shown at all.
    if (m_calibrationState == QLatin1String("idle")) {
        QJsonObject idle{{QStringLiteral("state"), QStringLiteral("IDLE")},
                         {QStringLiteral("game_build"), QJsonValue::Null},
                         {QStringLiteral("template_profile_id"), QJsonValue::Null},
                         {QStringLiteral("local_profile_id"), QJsonValue::Null},
                         {QStringLiteral("bound_at_utc"), QJsonValue::Null},
                         {QStringLiteral("blockers"), QJsonArray()},
                         {QStringLiteral("progress"), QJsonValue::Null},
                         {QStringLiteral("events"), QJsonArray()}};
        if (!m_sharedState.isEmpty())
            idle.insert(QStringLiteral("shared"), sharedCalibrationStatus());
        return idle;
    }

    const bool observing = m_calibrationState == QLatin1String("observing");
    const bool ready = m_calibrationState == QLatin1String("ready");
    const bool blocked = m_calibrationState == QLatin1String("blocked");
    const bool done = m_calibrationState == QLatin1String("done");
    const QDateTime start = m_now.addSecs(-3600);
    // Only a draft still waiting for a decision asks for one; a finished or
    // voided draft asks nothing.
    const bool asks = ready;

    const auto entryFor = [&start](const QString &kind, qint64 tMs, const QString &label,
                                   bool requiresConfirmation, const QJsonValue &rouletteId,
                                   const QJsonValue &territoryId, const QJsonValue &dutyName) {
        QJsonObject entry;
        entry.insert(QStringLiteral("event_id"), kind + QLatin1Char('-') + QString::number(tMs));
        entry.insert(QStringLiteral("kind"), kind);
        entry.insert(QStringLiteral("at_utc"), isoUtc(start.addMSecs(tMs)));
        entry.insert(QStringLiteral("t_ms"), double(tMs));
        entry.insert(QStringLiteral("label"), label);
        entry.insert(QStringLiteral("roulette_id"), rouletteId);
        entry.insert(QStringLiteral("territory_id"), territoryId);
        entry.insert(QStringLiteral("duty_name"), dutyName);
        entry.insert(QStringLiteral("requires_confirmation"), requiresConfirmation);
        return entry;
    };

    QJsonArray events;
    events.append(entryFor(QStringLiteral("login"), 0, QString::fromUtf8("登录进入游戏"),
                           false, QJsonValue::Null, QJsonValue::Null, QJsonValue::Null));
    events.append(entryFor(QStringLiteral("finder_request"), 60000,
                           QString::fromUtf8("排本：练级迷宫"), asks, 1,
                           QJsonValue::Null, QJsonValue::Null));
    if (!observing) {
        events.append(entryFor(QStringLiteral("pop"), 120000,
                               QString::fromUtf8("匹配弹窗：练级迷宫"),
                               asks, 1, QJsonValue::Null, QJsonValue::Null));
        events.append(entryFor(QStringLiteral("duty_enter"), 124000,
                               QString::fromUtf8("进入副本：沙斯塔夏溶洞"),
                               asks, QJsonValue::Null, 1036,
                               QString::fromUtf8("沙斯塔夏溶洞")));
        events.append(entryFor(QStringLiteral("duty_exit"), 214000,
                               QString::fromUtf8("离开副本"), asks,
                               QJsonValue::Null, QJsonValue::Null, QJsonValue::Null));
    }

    QJsonObject progress;
    progress.insert(QStringLiteral("finder_request_seen"), true);
    progress.insert(QStringLiteral("pop_seen"), !observing && !blocked);
    // The observing fixture stops at low-strength shape evidence so screenshots
    // exercise the card's "awaiting entry verification" state. This mock does
    // not claim that the shape came from a real duty pop.
    progress.insert(QStringLiteral("pop_shape_seen"), observing);
    progress.insert(QStringLiteral("zone_clusters"), observing ? 1 : 3);
    progress.insert(QStringLiteral("duty_entry_seen"), !observing);
    progress.insert(QStringLiteral("duty_exit_seen"), !observing);

    QJsonArray blockers;
    if (blocked) {
        blockers.append(QString::fromUtf8(
            "这次更新改了报文的结构，"
            "本机校准做不了，"
            "需要维护者出一份新档案。"));
    } else if (m_sharedState == QLatin1String("verified")) {
        // The Collector's own sentence while a shared profile is still being watched.
        blockers.append(QString::fromUtf8(
            "正在用其他玩家分享的校准记录导随，这份校准已经在本机流量里核实过。"
            "完整记录一次进本和出本之后校准就结束；在那之前软件会继续在后台核对，"
            "对不上会自动撤下并改回本机校准。"));
    }

    QJsonObject calibration;
    calibration.insert(QStringLiteral("state"),
                       observing ? QStringLiteral("OBSERVING")
                                 : ready ? QStringLiteral("READY")
                                         : blocked ? QStringLiteral("BLOCKED")
                                                   : QStringLiteral("DONE"));
    calibration.insert(QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000"));
    calibration.insert(QStringLiteral("template_profile_id"), QStringLiteral("cn.2026.08.05"));
    calibration.insert(QStringLiteral("local_profile_id"),
                       done ? QJsonValue(QStringLiteral("cn.2026.09.01.local"))
                            : QJsonValue(QJsonValue::Null));
    calibration.insert(QStringLiteral("bound_at_utc"),
                       done ? QJsonValue(isoUtc(start.addMSecs(220000)))
                            : QJsonValue(QJsonValue::Null));
    calibration.insert(QStringLiteral("blockers"), blockers);
    calibration.insert(QStringLiteral("progress"), progress);
    calibration.insert(QStringLiteral("events"), events);
    if (!m_sharedState.isEmpty())
        calibration.insert(QStringLiteral("shared"), sharedCalibrationStatus());
    return calibration;
}

QJsonObject MockBackend::applyCalibrationVerdicts(const QJsonObject &payload, QString *errorCode,
                                                  QString *errorMessage)
{
    if (m_calibrationState != QLatin1String("ready")) {
        *errorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
        *errorMessage = QString::fromUtf8("现在没有可核对的校准结果。");
        return {};
    }
    const QJsonArray verdicts = payload.value(QStringLiteral("verdicts")).toArray();
    if (verdicts.isEmpty()) {
        return badRequest(errorCode, errorMessage,
                          QString::fromUtf8("核对结果不能为空。"));
    }
    for (const QJsonValue &value : verdicts) {
        const QString verdict = value.toObject().value(QStringLiteral("verdict")).toString();
        if (verdict != QLatin1String("CORRECT") && verdict != QLatin1String("WRONG")) {
            return badRequest(errorCode, errorMessage,
                              QString::fromUtf8("核对结果只能是对或错。"));
        }
        if (verdict == QLatin1String("WRONG")) {
            // The draft is void; the Collector goes back to observing and the
            // user plays one more roulette.
            m_calibrationState = QStringLiteral("observing");
            *errorCode = QStringLiteral("ERR_CALIBRATION_REJECTED");
            *errorMessage = QString::fromUtf8(
                "有事件被标为不对，"
                "本次校准草稿已作废。");
            return {};
        }
    }
    m_calibrationState = QStringLiteral("done");
    QJsonObject result;
    result.insert(QStringLiteral("profile_id"), QStringLiteral("cn.2026.09.01.local"));
    result.insert(QStringLiteral("profile_path"),
                  QString::fromUtf8("（模拟后端未写入任何文件）"));
    result.insert(QStringLiteral("bound_in_session"), true);
    return result;
}

QJsonObject MockBackend::applyCaptureSettings(const QJsonObject &payload,
                                              QString *errorCode,
                                              QString *errorMessage)
{
    const QStringList allowed{QStringLiteral("follow_game"), QStringLiteral("autostart"),
                              QStringLiteral("adapter_id"), QStringLiteral("log_retention_days"),
                              QStringLiteral("allow_without_profile"), QStringLiteral("region_override"),
                              QStringLiteral("candidate_validation_enabled"),
                              QStringLiteral("auto_calibration_enabled"),
                              QStringLiteral("shared_calibration_enabled"),
                              QStringLiteral("research_payload_opcodes")};
    if (hasUnknownKeys(payload, allowed))
        return badRequest(errorCode, errorMessage, QString::fromUtf8("捕获设置含未知字段。"));
    QJsonObject next = captureSettings();
    for (auto it = payload.begin(); it != payload.end(); ++it) {
        const QString key = it.key();
        const QJsonValue value = it.value();
        if (key == QLatin1String("follow_game") || key == QLatin1String("autostart")
            || key == QLatin1String("allow_without_profile")
            || key == QLatin1String("candidate_validation_enabled")
            || key == QLatin1String("auto_calibration_enabled")
            || key == QLatin1String("shared_calibration_enabled")) {
            if (!value.isBool())
                return badRequest(errorCode, errorMessage, QString::fromUtf8("捕获开关必须是布尔值。"));
        } else if (key == QLatin1String("log_retention_days")) {
            const int days = value.toInt(-1);
            if (days < 1 || days > 90)
                return badRequest(errorCode, errorMessage, QString::fromUtf8("日志保留天数必须在 1 到 90 之间。"));
        } else if (key == QLatin1String("adapter_id")) {
            if (!value.isNull() && (!value.isString() || value.toString().size() > 400))
                return badRequest(errorCode, errorMessage, QString::fromUtf8("网卡标识必须是字符串或留空。"));
        } else if (key == QLatin1String("region_override")) {
            if (!value.isNull() && (!value.isString() || (value.toString() != QLatin1String("CN")
                                                         && value.toString() != QLatin1String("GLOBAL"))))
                return badRequest(errorCode, errorMessage, QString::fromUtf8("区服只能是 CN、GLOBAL 或留空。"));
        } else if (key == QLatin1String("research_payload_opcodes") && !value.isArray()) {
            return badRequest(errorCode, errorMessage, QString::fromUtf8("研究白名单必须是数组，清空请传 []。"));
        }
        next.insert(key, value);
    }
    if (payload.contains(QStringLiteral("research_payload_opcodes"))) {
        const QJsonArray requested = next.value(QStringLiteral("research_payload_opcodes")).toArray();
        if (requested.size() > 32)
            return badRequest(errorCode, errorMessage, QString::fromUtf8("研究白名单最多 32 项。"));
        if (!requested.isEmpty() && !next.value(QStringLiteral("candidate_validation_enabled")).toBool())
            return badRequest(errorCode, errorMessage, QString::fromUtf8("请先开启候选档案验证，再设置研究白名单。"));

        // Use the same closed catalogue as the displayed candidate chips: only
        // declared, bounded, non-obfuscated hypotheses of at most 512 bytes.
        // No unknown or unbounded opcode can be enabled through manual input.
        QSet<QString> eligible;
        const auto hypotheses = captureStatus().value(QStringLiteral("candidate_hypotheses")).toArray();
        for (const auto &value : hypotheses) {
            const auto hypothesis = value.toObject();
            if (hypothesis.value(QStringLiteral("research_eligible")).toBool())
                eligible.insert(hypothesis.value(QStringLiteral("opcode")).toString());
        }
        QSet<QString> unique;
        for (const QJsonValue &value : requested) {
            const QString token = value.toString().toLower();
            if (!value.isString() || !eligible.contains(token) || unique.contains(token))
                return badRequest(errorCode, errorMessage,
                                  QString::fromUtf8("只接受不重复、已声明且长度上限不超过 512 字节的非混淆候选 opcode。"));
            unique.insert(token);
        }
        QStringList sorted = unique.values();
        std::sort(sorted.begin(), sorted.end());
        QJsonArray normalized;
        for (const QString &token : sorted)
            normalized.append(token);
        next.insert(QStringLiteral("research_payload_opcodes"), normalized);
    }
    m_captureSettings = next;
    return captureSettings();
}

} // namespace mr
