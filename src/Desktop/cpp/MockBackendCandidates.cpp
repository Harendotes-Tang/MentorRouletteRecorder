#include "MockBackend.h"

#include "IpcFraming.h"
#include "MockData.h"

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
        const qint64 elapsed = static_cast<qint64>(ordinal + 1) * 15000;
        const QDateTime at = started.addMSecs(elapsed);
        const bool anchor = i == 2 || i == 17 || i == 25 || i == 35 || i == 248
                            || i == 251 || i == 255 || i == 259;
        int connection = (ordinal % 3) + 1;
        if (i == 2 || i == 17 || i == 25 || i == 255)
            connection = 1;
        else if (i == 35 || i == 248)
            connection = 2;
        else if (i == 251 || i == 259)
            connection = 3;

        const bool registration = !anchor && i % 4 == 0;
        const QString id = mockUuid(QStringLiteral("candidate-observation-%1").arg(i));
        QString hypothesisName;
        QString groupName;
        QString direction;
        if (anchor) {
            hypothesisName = QStringLiteral("ZONE_LOAD");
            groupName = QStringLiteral("zone_load");
            direction = QStringLiteral("NONE");
        } else if (registration) {
            hypothesisName = QStringLiteral("QUEUE_REGISTRATION");
            groupName = QStringLiteral("queue");
            direction = QStringLiteral("C2S");
        } else {
            hypothesisName = QStringLiteral("FINDER_STATE_NOTIFICATION");
            groupName = QStringLiteral("finder");
            direction = QStringLiteral("S2C");
        }
        QJsonObject row{
            {QStringLiteral("observation_id"), id},
            {QStringLiteral("capture_session_id"),
             mockUuid(QStringLiteral("candidate-session-%1").arg(sessionOrdinal))},
            {QStringLiteral("profile_id"), QStringLiteral("cn.2026.08.05.candidate")},
            {QStringLiteral("hypothesis_name"), hypothesisName},
            {QStringLiteral("group_name"), groupName},
            {QStringLiteral("direction"), direction},
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
            {QStringLiteral("t_ms"), static_cast<double>(elapsed)},
            {QStringLiteral("first_observed_at_utc"),
             anchor ? QJsonValue(isoUtc(at.addMSecs(-2000))) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("last_observed_at_utc"),
             anchor ? QJsonValue(isoUtc(at)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("first_t_ms"),
             anchor ? QJsonValue(static_cast<double>(elapsed - 2000)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("last_t_ms"),
             anchor ? QJsonValue(static_cast<double>(elapsed)) : QJsonValue(QJsonValue::Null)},
            {QStringLiteral("review_verdict"), QJsonValue(QJsonValue::Null)},
            {QStringLiteral("review_note"), QJsonValue(QJsonValue::Null)},
            {QStringLiteral("reviewed_at_utc"), QJsonValue(QJsonValue::Null)},
        };
        if (i % 4 != 0) {
            QString verdict;
            QString note;
            if (i % 4 == 1) {
                verdict = QStringLiteral("CORRECT");
                note = QString::fromUtf8("模拟核对：与回忆中的弹窗时间一致");
            } else if (i % 4 == 2) {
                verdict = QStringLiteral("WRONG");
                note = QString::fromUtf8("模拟核对：此时只是传送");
            } else {
                verdict = QStringLiteral("UNSURE");
                note = QString::fromUtf8("模拟核对：无法确认对应事件");
            }
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
    for (const auto &value : m_candidateObservations) {
        const QJsonObject row = value.toObject();
        const QDateTime at = fromIso(row.value(QStringLiteral("observed_at_utc")));
        if ((!session.isString()
             || row.value(QStringLiteral("capture_session_id")) == session)
            && (!from.isValid() || at >= from) && (!to.isValid() || at <= to)) {
            selected.append(row);
        }
    }
    std::ranges::sort(selected, [](const QJsonObject &a, const QJsonObject &b) {
        const QString left = a.value(QStringLiteral("observed_at_utc")).toString();
        const QString right = b.value(QStringLiteral("observed_at_utc")).toString();
        return left == right ? a.value(QStringLiteral("observation_id")).toString()
                                   > b.value(QStringLiteral("observation_id")).toString()
                             : left > right;
    });
    QJsonArray items;
    const int offset = (page - 1) * size;
    const int end = qMin(offset + size, static_cast<int>(selected.size()));
    for (int i = offset; i < end; ++i)
        items.append(selected.at(i));
    return {{QStringLiteral("items"), items},
            {QStringLiteral("page_info"), QJsonObject{{QStringLiteral("page"), page},
                                                       {QStringLiteral("page_size"), size},
                                                       {QStringLiteral("total"), static_cast<int>(selected.size())}}}};
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

void MockBackend::setCandidateRoundFixture(bool gameRunning)
{
    m_candidateRound = true;
    m_candidateRoundGameRunning = gameRunning;
    m_capturing = false;
    m_liveMode = LiveMode::None;
    m_captureSettings.insert(QStringLiteral("candidate_validation_enabled"), false);
    m_captureSettings.insert(QStringLiteral("research_payload_opcodes"), QJsonArray());
}

QJsonArray MockBackend::candidateRoundHypotheses()
{
    struct Row
    {
        const char *name;
        const char *label;
        const char *opcode;
        const char *direction;
        const char *group;
        int length;
    };
    static const Row rows[] = {
        {"QUEUE_REGISTRATION", "排本申请（候选）", "0x0215", "C2S", "queue", 24},
        {"FINDER_ACTION_022B", "排本前的客户端操作（候选）", "0x022b", "C2S", "queue", 24},
        {"FINDER_STATE_NOTIFICATION", "副本查找器状态更新（候选）", "0x016f", "S2C", "finder", 40},
        {"POP_CANDIDATE_0332", "匹配成功候选 A", "0x0332", "S2C", "queue", 16},
        {"POP_CANDIDATE_02CB", "匹配成功候选 B（或确认进度）", "0x02cb", "S2C", "queue", 24},
        {"QUEUE_SIDE_03BF", "排本附带报文 03bf", "0x03bf", "S2C", "queue", 40},
        {"QUEUE_SIDE_0337", "排本附带报文 0337", "0x0337", "S2C", "queue", 8},
        {"ZONE_INIT_031F", "进本／换区 031f", "0x031f", "S2C", "zone_load", 456},
        {"TERRITORY_CANDIDATE_027D", "副本识别候选 A", "0x027d", "S2C", "identity", 136},
        {"TERRITORY_CANDIDATE_00AB", "副本识别候选 B", "0x00ab", "S2C", "identity", 136},
        {"ZONE_MEMBER_010D", "进本／换区 010d", "0x010d", "S2C", "zone_load", 640},
        {"ZONE_MEMBER_0346", "进本／换区 0346", "0x0346", "S2C", "zone_load", 808},
        {"JOB_CANDIDATE_02F5", "职业识别候选 02f5", "0x02f5", "S2C", "identity", 16},
        {"JOB_CANDIDATE_02D8", "职业识别候选 02d8", "0x02d8", "S2C", "identity", 16},
        {"JOB_CANDIDATE_03B0", "职业识别候选 03b0", "0x03b0", "S2C", "identity", 16},
        {"JOB_CANDIDATE_02FF", "职业识别候选 02ff", "0x02ff", "S2C", "identity", 16},
        {"ZONE_MEMBER_0317", "进本／换区 0317", "0x0317", "S2C", "zone_load", 1216},
        {"ZONE_MEMBER_02FC", "进本／换区 02fc", "0x02fc", "S2C", "zone_load", 664},
        {"ZONE_MEMBER_0176", "进本／换区 0176", "0x0176", "S2C", "zone_load", 656},
    };
    QJsonArray hypotheses;
    for (const Row &row : rows) {
        hypotheses.append(QJsonObject{
            {QStringLiteral("profile_id"), QStringLiteral("global.2026.11.03.candidate")},
            {QStringLiteral("name"), QString::fromUtf8(row.name)},
            {QStringLiteral("label"), QString::fromUtf8(row.label)},
            {QStringLiteral("opcode"), QString::fromUtf8(row.opcode)},
            {QStringLiteral("direction"), QString::fromUtf8(row.direction)},
            {QStringLiteral("group"), QString::fromUtf8(row.group)},
            {QStringLiteral("expected_length"), row.length},
            // ResearchPayloadPolicy.IsEligible: bounded by 512 bytes, nothing obfuscated.
            {QStringLiteral("research_eligible"), row.length <= 512},
        });
    }
    return hypotheses;
}

void MockBackend::applyCandidateRoundFixture(QJsonObject &status) const
{
    // LiveProtocolPipeline.ToCaptureStatus for a region nothing records: the build is read,
    // the formal profile fails closed, and the exact-build candidate is described whether or
    // not validation is on. Its id is reported only while validation is on.
    const bool enabled = captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool();
    const bool running = m_candidateRoundGameRunning;
    const bool capturing = running && enabled && !m_npcapMissing;
    const QJsonValue null(QJsonValue::Null);
    status.insert(QStringLiteral("region"), QStringLiteral("GLOBAL"));
    status.insert(QStringLiteral("game_build"), QStringLiteral("2026.11.03.0000.0000"));
    status.insert(QStringLiteral("profile_status"), QStringLiteral("UNSUPPORTED_BUILD"));
    status.insert(QStringLiteral("profile_matches_build"), false);
    status.insert(QStringLiteral("candidate_validation_enabled"), enabled);
    status.insert(QStringLiteral("candidate_profile_id"),
                  enabled ? QJsonValue(QStringLiteral("global.2026.11.03.candidate")) : null);
    status.insert(QStringLiteral("candidate_hypotheses"), candidateRoundHypotheses());
    status.insert(QStringLiteral("ffxiv_running"), running);
    status.insert(QStringLiteral("ffxiv_process_id"), running ? QJsonValue(18244) : null);
    if (!running)
        status.insert(QStringLiteral("game_processes"), QJsonArray());
    status.insert(QStringLiteral("state"), capturing ? QStringLiteral("RUNNING") : QStringLiteral("STOPPED"));
    status.insert(QStringLiteral("capture_session_id"),
                  capturing ? QJsonValue(mockUuid(QStringLiteral("capture-session-1"))) : null);
    status.insert(QStringLiteral("started_at_utc"), capturing ? QJsonValue(isoUtc(m_now)) : null);
    status.insert(QStringLiteral("recent_parser_errors"), QJsonArray());
    for (const auto *field : {"profile_id", "profile_origin", "oodle_signature_source", "oodle_profile_id",
                             "oodle_profile_status", "last_valid_event_at_utc", "last_valid_event_kind",
                             "calibration_bound_at_utc"})
        status.insert(QString::fromLatin1(field), null);
    for (const auto *field : {"parse_ok_count", "parse_fail_count", "duplicate_count", "ignored_count",
                             "messages_decoded", "ipc_decoded", "message_rate_per_second"})
        status.insert(QString::fromLatin1(field), 0);
    status.remove(QStringLiteral("calibration"));
}

QJsonObject MockBackend::exportCandidatePayload(const QJsonObject &payload) const
{
    // The mock returns the contract shape without creating evidence files.
    // Both displayed paths explicitly identify the result as a simulation.
    const QString label = QString::fromUtf8("（模拟后端未写入文件）");
    const QString path = label + payload.value(QStringLiteral("target_path")).toString();
    return {{QStringLiteral("target_path"), path},
            // The sidecar sits next to the evidence file, as CandidateEvidenceExporter writes it.
            {QStringLiteral("sha256_path"), path + QStringLiteral(".sha256")},
            {QStringLiteral("sha256"), QString(64, QLatin1Char('0'))},
            {QStringLiteral("observation_count"), static_cast<int>(m_candidateObservations.size())},
            {QStringLiteral("review_count"), static_cast<int>(m_candidateReviews.size())},
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
        idle.insert(QStringLiteral("retired_local_profile_available"),
                    m_retiredLocalProfileAvailable);
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

    // Labels are the Collector's own (CalibrationDraft.BuildEvents): the Chinese
    // and the catalogue reference beside it (contracts/collector-texts.md).
    const auto entryFor = [&start](const QString &kind, qint64 tMs, const CollectorSentence &label,
                                   bool requiresConfirmation, const QJsonValue &rouletteId,
                                   const QJsonValue &territoryId, const QJsonValue &dutyName) {
        QJsonObject entry;
        entry.insert(QStringLiteral("event_id"), kind + QLatin1Char('-') + QString::number(tMs));
        entry.insert(QStringLiteral("kind"), kind);
        entry.insert(QStringLiteral("at_utc"), isoUtc(start.addMSecs(tMs)));
        entry.insert(QStringLiteral("t_ms"), static_cast<double>(tMs));
        entry.insert(QStringLiteral("label"), label.chinese);
        entry.insert(QStringLiteral("label_ref"), label.ref);
        entry.insert(QStringLiteral("roulette_id"), rouletteId);
        entry.insert(QStringLiteral("territory_id"), territoryId);
        entry.insert(QStringLiteral("duty_name"), dutyName);
        entry.insert(QStringLiteral("requires_confirmation"), requiresConfirmation);
        return entry;
    };

    const QVariantMap roulette{{QStringLiteral("roulette"), QString::fromUtf8("练级迷宫")}};
    const QString duty = QString::fromUtf8("沙斯塔夏溶洞");
    QJsonArray events;
    events.append(entryFor(QStringLiteral("login"), 0, collectorSentence("calibration.event.login"),
                           false, QJsonValue::Null, QJsonValue::Null, QJsonValue::Null));
    events.append(entryFor(QStringLiteral("finder_request"), 60000,
                           collectorSentence("calibration.event.finder_request", roulette), asks, 1,
                           QJsonValue::Null, QJsonValue::Null));
    if (!observing) {
        events.append(entryFor(QStringLiteral("pop"), 120000,
                               collectorSentence("calibration.event.pop", roulette),
                               asks, 1, QJsonValue::Null, QJsonValue::Null));
        events.append(entryFor(QStringLiteral("duty_enter"), 124000,
                               collectorSentence("calibration.event.duty_enter_named",
                                                 {{QStringLiteral("duty"), duty}}),
                               asks, QJsonValue::Null, 1036, duty));
        // Which entry the exit closes, as the Collector says it.
        events.append(entryFor(QStringLiteral("duty_exit"), 214000,
                               collectorSentence("calibration.event.duty_exit_for",
                                                 {{QStringLiteral("entered"), isoUtc(start.addMSecs(124000))}}),
                               asks, QJsonValue::Null, QJsonValue::Null, QJsonValue::Null));
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

    // blocker_refs parallels blockers, entry for entry.
    QJsonArray blockers;
    QJsonArray blockerRefs;
    const auto block = [&blockers, &blockerRefs](const CollectorSentence &sentence) {
        blockers.append(sentence.chinese);
        blockerRefs.append(sentence.ref);
    };
    if (blocked) {
        // A build whose duty messages this version cannot read (CalibrationDraft).
        block(collectorSentence("calibration.blocker.duty_unsupported"));
    } else if (m_sharedState == QLatin1String("verified")) {
        // The Collector's own sentence while a shared profile is still being watched
        // (CalibrationCoordinator, retaining).
        block(collectorSentence("calibration.shared.retaining"));
    }

    QJsonObject calibration;
    QString calibrationState = QStringLiteral("DONE");
    if (observing)
        calibrationState = QStringLiteral("OBSERVING");
    else if (ready)
        calibrationState = QStringLiteral("READY");
    else if (blocked)
        calibrationState = QStringLiteral("BLOCKED");
    calibration.insert(QStringLiteral("state"), calibrationState);
    calibration.insert(QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000"));
    calibration.insert(QStringLiteral("template_profile_id"), QStringLiteral("cn.2026.08.05"));
    // A proven shared profile ends calibration as DONE under its own id, as the Collector's
    // MarkDone does; otherwise DONE is this machine's own calibration.
    QJsonValue localProfileId(QJsonValue::Null);
    if (done)
        localProfileId = sharedProfileBound() ? QStringLiteral("cn.2026.09.01.0000.0000.shared")
                                            : QStringLiteral("cn.2026.09.01.local");
    calibration.insert(QStringLiteral("local_profile_id"), localProfileId);
    calibration.insert(QStringLiteral("bound_at_utc"),
                       done ? QJsonValue(isoUtc(start.addMSecs(220000)))
                            : QJsonValue(QJsonValue::Null));
    calibration.insert(QStringLiteral("blockers"), blockers);
    calibration.insert(QStringLiteral("blocker_refs"), blockerRefs);
    calibration.insert(QStringLiteral("progress"), progress);
    calibration.insert(QStringLiteral("events"), events);
    calibration.insert(QStringLiteral("retired_local_profile_available"),
                       m_retiredLocalProfileAvailable);
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
    for (const auto &value : verdicts) {
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
                              QStringLiteral("update_check_enabled"),
                              QStringLiteral("character_identification_enabled"),
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
            || key == QLatin1String("shared_calibration_enabled")
            || key == QLatin1String("update_check_enabled")
            || key == QLatin1String("character_identification_enabled")) {
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
                                                         && value.toString() != QLatin1String("GLOBAL")
                                                         && value.toString() != QLatin1String("TC"))))
                return badRequest(errorCode, errorMessage, QString::fromUtf8("区服只能是 CN、TC、GLOBAL 或留空。"));
        } else if (key == QLatin1String("research_payload_opcodes") && !value.isArray()) {
            return badRequest(errorCode, errorMessage, QString::fromUtf8("研究白名单必须是数组，清空请传 []。"));
        }
        next.insert(key, value);
    }
    const QString nextRegion = next.value(QStringLiteral("region_override")).toString(QStringLiteral("CN"));
    if (nextRegion != QLatin1String("CN") && !payload.contains(QStringLiteral("research_payload_opcodes")))
        next.insert(QStringLiteral("research_payload_opcodes"), QJsonArray());
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
        const auto hypotheses = m_candidateRound ? candidateRoundHypotheses()
            : nextRegion == QLatin1String("CN") ? declaredCandidateHypotheses() : QJsonArray();
        for (const auto &value : hypotheses) {
            const auto hypothesis = value.toObject();
            if (hypothesis.value(QStringLiteral("research_eligible")).toBool())
                eligible.insert(hypothesis.value(QStringLiteral("opcode")).toString());
        }
        QSet<QString> unique;
        for (const auto &value : requested) {
            const QString token = value.toString().toLower();
            if (!value.isString() || !eligible.contains(token) || unique.contains(token))
                return badRequest(errorCode, errorMessage,
                                  QString::fromUtf8("只接受不重复、已声明且长度上限不超过 512 字节的非混淆候选 opcode。"));
            unique.insert(token);
        }
        QStringList sorted = unique.values();
        std::ranges::sort(sorted);
        QJsonArray normalized;
        for (const QString &token : sorted)
            normalized.append(token);
        next.insert(QStringLiteral("research_payload_opcodes"), normalized);
    }
    // Every region's RegionalAchievement.identification follows the switch.
    const QString identification = QStringLiteral("character_identification_enabled");
    if (captureSettings().value(identification).toBool(true) != next.value(identification).toBool(true))
        emitStatsInvalidatedLater();
    m_captureSettings = next;
    m_lastCaptureSettingsUpdate = payload;
    return captureSettings();
}

} // namespace mr
