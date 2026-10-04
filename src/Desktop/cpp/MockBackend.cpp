#include "MockBackend.h"

#include "IpcFraming.h"
#include "JobCatalog.h"
#include "DutyCatalog.h"
#include "MockData.h"

#include <QDir>
#include <QJsonArray>
#include <QJsonValue>
#include <QSet>
#include <QTimer>

#include <algorithm>
#include <cmath>
#include <utility>

using namespace mr::mock;

namespace {

// ---------------------------------------------------------------------------
// The prototype's linear congruential generator, reproduced bit for bit so the
// dataset matches DOC/mentor-recorder-v2.dc.html.
// ---------------------------------------------------------------------------
class Lcg
{
public:
    explicit Lcg(quint32 seed) : m_state(seed) {}
    double next()
    {
        m_state = static_cast<quint32>(m_state * 1664525u + 1013904223u);
        return static_cast<double>(m_state) / 4294967296.0;
    }

private:
    quint32 m_state;
};

struct Duty {
    int contentId;
    int territoryId;
    const char *name;
    const char *category;
    int level;
    const char *expansion;
};

// Names, levels and versions are the design prototype's. Where the bundled
// catalogue (data/duties/cn.2026-09-04.json) has the same duty, the content_id
// is that duty's real id, so the record wizard's catalogue list and its 最近打过
// chips name the same duty the mock history shows. The others keep the
// prototype's ids, none of which is one of the real ids used here.
const Duty kDuties[] = {
    {  4, 1036, "沙斯塔夏溶洞",         "四人迷宫",   15, "2.0"},
    {  2, 1037, "塔姆·塔拉墓园",        "四人迷宫",   16, "2.0"},
    {  3, 1038, "铜铃铜山",             "四人迷宫",   17, "2.0"},
    { 17, 1039, "天狼星灯塔",           "四人迷宫",   32, "2.0"},
    { 11, 1043, "石卫塔",               "四人迷宫",   28, "2.0"},
    { 12, 1041, "巴尔达姆霸国",         "四人迷宫",   47, "2.0"},
    { 20, 1045, "魔大陆阿济兹拉",       "四人迷宫",   59, "3.0"},
    { 15, 1052, "帝国南方堡",           "四人迷宫",   67, "4.0"},
    { 44, 1064, "红玉海",               "四人迷宫",   70, "4.1"},
    {659, 1070, "格鲁格火山",           "四人迷宫",   73, "5.0"},
    {649, 1073, "水妖幻园",             "四人迷宫",   80, "5.0"},
    {692, 1078, "魔法宫殿",             "四人迷宫",   85, "6.0"},
    {844, 1083, "阿尔扎达尔海底遗迹群", "四人迷宫",   90, "6.0"},
    { 70, 1180, "伊库拉尔堡垒",         "四人迷宫",   93, "7.0"},
    { 72, 1189, "起源之矛",             "四人迷宫",  100, "7.0"},
    { 75, 1194, "绯红蜂巢",             "四人迷宫",  100, "7.2"},
    { 56, 1060, "伊弗利特讨伐战",       "讨伐歼灭战", 20, "2.0"},
    { 57, 1062, "泰坦讨伐战",           "讨伐歼灭战", 34, "2.0"},
    {239, 1090, "神龙歼灭战",           "讨伐歼灭战", 70, "4.0"},
    {687, 1094, "哈迪斯歼灭战",         "讨伐歼灭战", 80, "5.0"},
    {995, 1196, "佐拉加歼灭战",         "讨伐歼灭战",100, "7.1"},
    {120, 1200, "魔航船虚无方舟",       "大型任务",   50, "2.3"},
    {201, 1201, "魔科学研究所",         "大型任务",   60, "3.3"},
    {700, 1202, "复制工厂废墟",         "大型任务",   80, "5.1"},
    {300, 1300, "行会令：新兵训练",     "行会令",     10, "2.0"},
    {301, 1301, "行会令：基础战术",     "行会令",     15, "2.0"},
};
constexpr int kDutyCount = int(sizeof(kDuties) / sizeof(kDuties[0]));

// Extreme, savage and alliance-raid duties, real catalogue rows, so every 人数 /
// 难度 filter of the record wizard has something of the player's own. They are
// placed on a few recent runs after the random draw (generateRuns), which keeps
// the prototype's random stream - and every other run - unchanged.
const Duty kHighEndDuties[] = {
    {1015, 1248, "朱诺：第一巡行",                  "大型任务",   100, "7.1"},
    { 278,  730, "神龙梦幻歼灭战",                  "讨伐歼灭战",  70, "4.3"},
    { 986, 1226, "阿卡狄亚零式登天斗技场 轻量级1",   "大型任务",   100, "7.0"},
    { 866, 1054, "灿烂神域阿格莱亚",                "大型任务",    90, "6.1"},
    { 996, 1201, "佐拉加歼殛战",                    "讨伐歼灭战", 100, "7.0"},
};
constexpr int kHighEndOffsets[] = {1, 4, 8, 12, 16};

struct JobEntry {
    int id;
    const char *name;
    const char *roleGroup;
    const char *role;
};

// Real ClassJob row ids, so the extracted job icons resolve.
const JobEntry kJobs[] = {
    {19, "骑士",     "坦克",     "TANK"},
    {21, "战士",     "坦克",     "TANK"},
    {37, "绝枪战士", "坦克",     "TANK"},
    {24, "白魔法师", "治疗",     "HEALER"},
    {33, "占星术士", "治疗",     "HEALER"},
    {40, "贤者",     "治疗",     "HEALER"},
    {22, "龙骑士",   "近战",     "DPS"},
    {30, "忍者",     "近战",     "DPS"},
    {34, "武士",     "近战",     "DPS"},
    {23, "吟游诗人", "远程物理", "DPS"},
    {31, "机工士",   "远程物理", "DPS"},
    {25, "黑魔法师", "魔法",     "DPS"},
    {27, "召唤师",   "魔法",     "DPS"},
    {35, "赤魔法师", "魔法",     "DPS"},
};
constexpr int kJobCount = int(sizeof(kJobs) / sizeof(kJobs[0]));

QJsonObject changeEntry(const QString &field, const QJsonValue &oldValue,
                        const QJsonValue &newValue)
{
    QJsonObject object;
    object.insert(QStringLiteral("field"), field);
    object.insert(QStringLiteral("old_value"), oldValue);
    object.insert(QStringLiteral("new_value"), newValue);
    return object;
}

/// SemanticEventProcessor.CreateRun + DescribeCreation: the SYSTEM CREATE_AUTO revision
/// a run capture recorded opens its chain with (review DT4-X4). The Collector writes it
/// in the transaction that creates the row at the match, so it describes the run as it
/// stood then - the duty the match named, no job, no entry or end, result UNKNOWN - and
/// nothing that was learned or corrected afterwards.
QJsonObject creationRevision(const QJsonObject &run)
{
    const QJsonValue null(QJsonValue::Null);
    const QJsonValue matched = run.value(QStringLiteral("matched_at_utc"));
    const std::pair<const char *, QJsonValue> created[] = {
        {"run_id", run.value(QStringLiteral("run_id"))},
        {"revision", 1},
        {"capture_session_id", run.value(QStringLiteral("capture_session_id"))},
        {"region", run.value(QStringLiteral("region"))},
        {"protocol_profile_id", run.value(QStringLiteral("protocol_profile_id"))},
        {"mentor_roulette_id", run.value(QStringLiteral("mentor_roulette_id"))},
        {"content_id", run.value(QStringLiteral("content_id"))},
        {"territory_id", run.value(QStringLiteral("territory_id"))},
        {"duty_name", run.value(QStringLiteral("duty_name"))},
        {"duty_category", run.value(QStringLiteral("duty_category"))},
        {"job_id", null},
        {"job_name", null},
        {"role", QStringLiteral("UNKNOWN")},
        {"matched_at_utc", matched},
        {"entered_at_utc", null},
        {"ended_at_utc", null},
        {"duration_ms", null},
        {"result", QStringLiteral("UNKNOWN")},
        {"detection_confidence", QStringLiteral("MEDIUM")},
        {"source", QStringLiteral("AUTO_NETWORK")},
        {"contributes_to_goal", true},
        {"manually_created", false},
        {"manually_corrected", false},
        {"soft_deleted", false},
        {"created_at_utc", matched},
        {"updated_at_utc", matched},
    };
    QJsonArray changes;
    for (const auto &[field, value] : created)
        changes.append(changeEntry(QString::fromLatin1(field), null, value));

    QJsonObject revision;
    revision.insert(QStringLiteral("revision_id"),
                    mockUuid(run.value(QStringLiteral("run_id")).toString() + QStringLiteral(":revision:1")));
    revision.insert(QStringLiteral("run_id"), run.value(QStringLiteral("run_id")));
    revision.insert(QStringLiteral("revision"), 1);
    revision.insert(QStringLiteral("changed_at_utc"), matched);
    revision.insert(QStringLiteral("change_kind"), QStringLiteral("CREATE_AUTO"));
    revision.insert(QStringLiteral("reason"), null);
    revision.insert(QStringLiteral("actor"), QStringLiteral("SYSTEM"));
    revision.insert(QStringLiteral("changes"), changes);
    return revision;
}

/// RunMutationService.InitialChanges: a CREATE_MANUAL revision records the whole initial
/// value set, every old value null (docs/manual-correction.md section 2), so what the
/// player first entered stays readable in 修正历史 (review S33-1).
QJsonArray manualCreationChanges(const QJsonObject &run)
{
    const QJsonValue null(QJsonValue::Null);
    const auto value = [&run, &null](const char *field) {
        const QJsonValue stored = run.value(QLatin1String(field));
        return stored.isUndefined() ? null : stored;
    };
    const auto flag = [&run](const char *field, bool fallback) {
        return QJsonValue(run.value(QLatin1String(field)).toBool(fallback));
    };
    const std::pair<const char *, QJsonValue> created[] = {
        {"content_id", value("content_id")},
        {"duty_name", value("duty_name")},
        {"duty_category", value("duty_category")},
        {"job_id", value("job_id")},
        {"job_name", value("job_name")},
        {"role", value("role")},
        {"matched_at_utc", value("matched_at_utc")},
        {"entered_at_utc", value("entered_at_utc")},
        {"ended_at_utc", value("ended_at_utc")},
        {"duration_ms", value("duration_ms")},
        {"result", value("result")},
        {"contributes_to_goal", flag("contributes_to_goal", true)},
        {"note", value("note")},
        {"soft_deleted", flag("soft_deleted", false)},
        {"pending_review", flag("pending_review", false)},
        {"manually_corrected", flag("manually_corrected", false)},
    };
    QJsonArray changes;
    for (const auto &[field, initial] : created)
        changes.append(changeEntry(QString::fromLatin1(field), null, initial));
    return changes;
}

/// One revision of a sample run, written the way the Collector writes it.
QJsonObject sampleRevision(const QJsonObject &run, const QString &changedAt, const char *kind,
                           const char *actor, const QString &reason, const QJsonArray &changes)
{
    const int number = run.value(QStringLiteral("revision")).toInt();
    QJsonObject revision;
    revision.insert(QStringLiteral("revision_id"),
                    mockUuid(run.value(QStringLiteral("run_id")).toString()
                             + QStringLiteral(":revision:%1").arg(number)));
    revision.insert(QStringLiteral("run_id"), run.value(QStringLiteral("run_id")));
    revision.insert(QStringLiteral("revision"), number);
    revision.insert(QStringLiteral("changed_at_utc"), changedAt);
    revision.insert(QStringLiteral("change_kind"), QString::fromLatin1(kind));
    revision.insert(QStringLiteral("reason"), reason);
    revision.insert(QStringLiteral("actor"), QString::fromLatin1(actor));
    revision.insert(QStringLiteral("changes"), changes);
    return revision;
}

/// RunMutationRules.ReadsAsInFlight: an automatic run still in flight - UNKNOWN, no end,
/// not pending review - which every statistic and the review list leave out.
bool readsAsInFlight(const QJsonObject &run)
{
    return run.value(QStringLiteral("source")).toString() == QLatin1String("AUTO_NETWORK")
           && run.value(QStringLiteral("result")).toString() == QLatin1String("UNKNOWN")
           && !run.value(QStringLiteral("ended_at_utc")).isString()
           && !run.value(QStringLiteral("pending_review")).toBool(false);
}

/// RunMutationRules.OverrulesTheRecord: whether a correction overrules what the software
/// recorded, and so marks the run 已修正. Saying how a run pending review went, filling a
/// duty or job the software left blank (null, or the 未知 / UNKNOWN placeholder), and the
/// note correct nothing; the zone moves with the duty, like the Collector's duty-identity
/// audit field. Everything else - a recorded duty, job or time, a settled outcome - does.
bool overrulesTheRecord(bool wasPendingReview, const QJsonArray &changes)
{
    static const QSet<QString> outcome{QStringLiteral("result"), QStringLiteral("pending_review"),
                                       QStringLiteral("contributes_to_goal")};
    static const QSet<QString> fillable{
        QStringLiteral("job_id"),      QStringLiteral("job_name"),     QStringLiteral("role"),
        QStringLiteral("role_group"),  QStringLiteral("content_id"),   QStringLiteral("duty_name"),
        QStringLiteral("duty_category")};
    static const QSet<QString> neverACorrection{QStringLiteral("manually_corrected"),
                                                QStringLiteral("note"),
                                                QStringLiteral("territory_id")};
    const auto blank = [](const QJsonValue &value) {
        return value.isUndefined() || value.isNull() || value.toString() == QString::fromUtf8("未知")
               || value.toString() == QLatin1String("UNKNOWN")
               || (value.isString() && value.toString().isEmpty());
    };
    for (const QJsonValue &value : changes) {
        const QJsonObject change = value.toObject();
        const QString field = change.value(QStringLiteral("field")).toString();
        if (neverACorrection.contains(field))
            continue;
        if (outcome.contains(field) ? !wasPendingReview
            : fillable.contains(field) ? !blank(change.value(QStringLiteral("old_value")))
                                       : true) {
            return true;
        }
    }
    return false;
}

/// RunMutationService.IsUnsettledShape: the two shapes a closed run must never be put back
/// into - one that reads as still in flight, and one with no entry time whose result is
/// not 进本前取消, which the correction rules refuse to touch.
bool isUnsettledShape(const QJsonObject &run)
{
    return readsAsInFlight(run)
           || (run.value(QStringLiteral("result")).toString() != QLatin1String("CANCELLED_BEFORE_ENTRY")
               && !run.value(QStringLiteral("entered_at_utc")).isString());
}

/// RunMutationRules.ValidateFinalValue, with the Collector's own sentences
/// (Application/Mutations/RunMutationValidation.cs).
bool acceptableFinalValue(const QJsonObject &run, QString *errorCode, QString *errorMessage)
{
    const QDateTime matched = fromIso(run.value(QStringLiteral("matched_at_utc")));
    const QDateTime entered = fromIso(run.value(QStringLiteral("entered_at_utc")));
    const QDateTime ended = fromIso(run.value(QStringLiteral("ended_at_utc")));
    const QString result = run.value(QStringLiteral("result")).toString();
    const auto refuse = [errorCode, errorMessage](const char *code, const char *message) {
        *errorCode = QString::fromLatin1(code);
        *errorMessage = QString::fromUtf8(message);
        return false;
    };
    if (matched.isValid() && entered.isValid() && matched > entered)
        return refuse("ERR_TIME_ORDER", "匹配时间不能晚于进入副本的时间。");
    if (entered.isValid() && ended.isValid() && entered > ended)
        return refuse("ERR_TIME_ORDER", "进入副本的时间不能晚于结束时间。");
    if (result != QLatin1String("CANCELLED_BEFORE_ENTRY") && !entered.isValid())
        return refuse("ERR_BAD_REQUEST",
                      "只有「未进入副本即取消」可以没有进入时间，其余结果都必须填写进入时间。");
    if (result == QLatin1String("COMPLETED") && !ended.isValid())
        return refuse("ERR_BAD_REQUEST", "判定为「已完成」的记录必须填写结束时间。");
    const QJsonValue duration = run.value(QStringLiteral("duration_ms"));
    if (duration.isDouble() && duration.toDouble() < 0.0)
        return refuse("ERR_NEGATIVE_DURATION", "时长不能为负数。");
    return true;
}

} // namespace

namespace mr {

MockBackend::MockBackend(QObject *parent)
    : IBackend(parent), m_now(QDate(2026, 9, 4), QTime(21, 40, 12), QTimeZone::LocalTime)
{
    // The sample player entered the baseline before the 60 days the dataset spans.
    m_baselineEffectiveAt = m_now.addDays(-60).toUTC();
    m_achievementUpdatedAt = m_baselineEffectiveAt;
    m_baselineAuditEventId = mockUuid(QStringLiteral("baseline-audit-1"));
    generateRuns();
}

QString MockBackend::connectionDetail() const
{
    return QString::fromUtf8("模拟数据 · 未连接 Collector");
}

void MockBackend::setNpcapMissing(bool missing)
{
    if (m_npcapMissing == missing)
        return;
    m_npcapMissing = missing;
    if (missing)
        m_capturing = false;
    Q_EMIT connectionChanged();
}

void MockBackend::setLiveMode(LiveMode mode)
{
    if (m_liveMode == mode)
        return;
    m_liveMode = mode;
    emitStateChanged(currentRun().value(QStringLiteral("state")).toString());
}

void MockBackend::setValidationFixture(const QString &state)
{
    m_validationState = state;
    m_capturing = false;
    m_validationMarkerCount = state == QLatin1String("RECORDING")
                                  || state == QLatin1String("STOPPING")
                                  || state == QLatin1String("COMPLETED")
                              ? 2
                              : 0;
    Q_EMIT connectionChanged();
}

void MockBackend::setFormalCaptureRunning(bool running)
{
    m_capturing = running;
    Q_EMIT connectionChanged();
}

void MockBackend::setUpdateAvailable(bool available)
{
    m_updateAvailable = available;
}

void MockBackend::setReplyDelay(int milliseconds, const QString &messageType)
{
    if (milliseconds > 0)
        m_replyDelays.insert(messageType, milliseconds);
    else
        m_replyDelays.remove(messageType);
}

void MockBackend::setMidstreamSuspected(bool suspected)
{
    if (m_midstreamSuspected == suspected)
        return;
    m_midstreamSuspected = suspected;
    // A midstream session is by definition one that is running and decoding
    // nothing, so the fixture turns capture on with it.
    if (suspected)
        m_capturing = true;
    Q_EMIT connectionChanged();
}

/// The $defs/LiveEvent fields every event carries, whatever its kind.
///
/// event_id is required by the contract and is what the shell de-duplicates a
/// reconnect replay by; sequence is monotonic per subscription.
///
/// emitted_at_utc is the real clock, not the dataset's fixed m_now: the shell
/// ignores state events emitted before it started (review OH-1) and capture
/// events older than its last answer, so a stamp from the fixture date made
/// every mock announcement silent (review DT2-X4).
QVariantMap MockBackend::liveEventEnvelope(const QString &eventType, const QString &kind)
{
    QVariantMap event;
    event.insert(QStringLiteral("event_id"), ipc::newRequestId());
    event.insert(QStringLiteral("event_type"), eventType);
    event.insert(QStringLiteral("kind"), kind);
    event.insert(QStringLiteral("emitted_at_utc"), isoUtc(QDateTime::currentDateTimeUtc()));
    event.insert(QStringLiteral("sequence"), ++m_liveSequence);
    return event;
}

void MockBackend::emitStateChanged(const QString &state)
{
    if (state.isEmpty())
        return;
    QVariantMap event = liveEventEnvelope(QStringLiteral("StateChanged"),
                                          QStringLiteral("run_state_changed"));
    event.insert(QStringLiteral("state"), state);
    event.insert(QStringLiteral("match_from_queue"), false);
    // The run rides along, exactly as it does on run_finished. Without it the
    // 进本 announcement has no duty name to speak at the moment it is spoken:
    // the current-run card is fetched asynchronously and still holds the
    // previous state's snapshot (review finding H-3).
    event.insert(QStringLiteral("run"),
                 currentRun().value(QStringLiteral("run")).toObject().toVariantMap());
    Q_EMIT liveEvent(event);
}

void MockBackend::emitRunFinished(const QString &state)
{
    if (state.isEmpty())
        return;
    QVariantMap event = liveEventEnvelope(QStringLiteral("RunFinished"),
                                          QStringLiteral("run_finished"));
    event.insert(QStringLiteral("state"), state);
    event.insert(QStringLiteral("run"),
                 currentRun().value(QStringLiteral("run")).toObject().toVariantMap());
    Q_EMIT liveEvent(event);
}

void MockBackend::simulateRunTransitions(const QString &finalState)
{
    emitStateChanged(QStringLiteral("MENTOR_MATCHED"));
    emitStateChanged(QStringLiteral("ENTERED_DUTY"));
    if (finalState.isEmpty())
        return;
    emitStateChanged(finalState);
    // The real bus always publishes RunFinished for a terminal state, even
    // when StateChanged skipped it because the next duty popped immediately.
    // The terminal announcement and the 本次导随结果 question hang off it.
    if (finalState != QLatin1String("IDLE") && finalState != QLatin1String("MENTOR_MATCHED")
        && finalState != QLatin1String("ENTERED_DUTY")) {
        emitRunFinished(finalState);
    }
}

void MockBackend::resetRuns(const QJsonArray &runs)
{
    m_runs = runs;
    m_revisions = QJsonArray();
}

void MockBackend::setAchievement(int goalCount, int baselineCompletedCount)
{
    m_goalCount = goalCount;
    m_baselineCompletedCount = baselineCompletedCount;
    // Set behind the history's back: no entry stored these values.
    m_baselineAuditEventId.clear();
}

// ---------------------------------------------------------------------------
// Dataset generation
// ---------------------------------------------------------------------------

void MockBackend::generateRuns()
{
    Lcg rng(20260904u);

    struct Draft {
        QDateTime matched;
        QDateTime entered;
        QDateTime ended;
        const Duty *duty = nullptr;
        const JobEntry *job = nullptr;
        QString result;
        bool hasDuration = false;
        qint64 durationMs = 0;
    };

    static const struct { const char *code; double weight; } kWeights[] = {
        {"COMPLETED", 0.78},
        {"LEFT_OR_ABANDONED", 0.09},
        {"CANCELLED_BEFORE_ENTRY", 0.06},
        {"DISCONNECTED", 0.03},
        {"INTERRUPTED", 0.02},
        {"UNKNOWN", 0.02},
    };

    QList<Draft> drafts;
    drafts.reserve(96);

    for (int i = 0; i < 96; ++i) {
        Draft draft;

        const int daysAgo = int(std::floor(std::pow(rng.next(), 1.3) * 60.0));
        QDateTime moment = m_now.addDays(-daysAgo);
        const int hour = 18 + int(std::floor(rng.next() * 5.0));
        const int minute = int(std::floor(rng.next() * 60.0));
        const int second = int(std::floor(rng.next() * 60.0));
        moment.setTime(QTime(hour, minute, second));
        if (moment > m_now)
            moment = m_now.addMSecs(-qint64(3600000.0 * (1.0 + rng.next() * 4.0)));

        // These conditionals must short-circuit exactly like the prototype's
        // ternaries, otherwise the random stream drifts.
        if (rng.next() >= 0.02)
            draft.duty = &kDuties[int(std::pow(rng.next(), 1.6) * kDutyCount)];
        if (rng.next() >= 0.06)
            draft.job = &kJobs[int(std::pow(rng.next(), 1.4) * kJobCount)];

        const double roll = rng.next();
        double acc = 0.0;
        draft.result = QStringLiteral("UNKNOWN");
        for (const auto &weight : kWeights) {
            acc += weight.weight;
            if (roll < acc) {
                draft.result = QString::fromLatin1(weight.code);
                break;
            }
        }

        draft.matched = moment;
        if (draft.result != QLatin1String("CANCELLED_BEFORE_ENTRY")) {
            draft.entered =
                draft.matched.addMSecs(qint64((40.0 + rng.next() * 80.0) * 1000.0));

            double base = 18.0;
            if (draft.duty) {
                const QString category = QString::fromUtf8(draft.duty->category);
                if (category == QString::fromUtf8("讨伐歼灭战"))
                    base = 8.0;
                else if (category == QString::fromUtf8("大型任务"))
                    base = 26.0;
                else if (category == QString::fromUtf8("行会令"))
                    base = 10.0;
                else if (draft.duty->level < 50)
                    base = 17.0;
                else if (draft.duty->level < 80)
                    base = 20.0;
                else
                    base = 22.0;
            }
            const double minutes = draft.result == QLatin1String("COMPLETED")
                                       ? base * (0.8 + rng.next() * 0.5)
                                       : base * (0.15 + rng.next() * 0.6);
            // An UNKNOWN run is one the state machine closed without a result
            // (UNKNOWN_FINAL_STATE), so it has an end like the others. Without one
            // it would read as still in flight for good (RunMutationRules
            // .ReadsAsInFlight): left out of every statistic and listed as 进行中.
            // No draw is spent here, so the random stream is unchanged.
            if (draft.result != QLatin1String("INTERRUPTED")) {
                draft.ended = draft.entered.addMSecs(qint64(minutes * 60000.0));
                draft.hasDuration = true;
                draft.durationMs = draft.entered.msecsTo(draft.ended);
            }
        } else {
            draft.ended =
                draft.matched.addMSecs(qint64((30.0 + rng.next() * 60.0) * 1000.0));
        }

        rng.next(); // the prototype spends one draw on the run id suffix
        drafts.append(draft);
    }

    std::stable_sort(drafts.begin(), drafts.end(),
                     [](const Draft &a, const Draft &b) { return a.matched < b.matched; });

    m_runs = QJsonArray();
    m_revisions = QJsonArray();

    for (int i = 0; i < drafts.size(); ++i) {
        const Draft &draft = drafts.at(i);
        const Duty *duty = draft.duty;
        const JobEntry *job = draft.job;

        QJsonObject run;
        run.insert(QStringLiteral("run_id"), mockUuid(QStringLiteral("mentor-run-%1").arg(i)));
        run.insert(QStringLiteral("revision"), 1);
        run.insert(QStringLiteral("capture_session_id"),
                   mockUuid(QStringLiteral("capture-session-1")));
        run.insert(QStringLiteral("region"), QStringLiteral("CN"));
        run.insert(QStringLiteral("game_build"), QStringLiteral("2026.08.12.0000.0000"));
        run.insert(QStringLiteral("protocol_profile_id"), QStringLiteral("cn/2026.08.12"));
        run.insert(QStringLiteral("mentor_roulette_id"), 9);
        run.insert(QStringLiteral("content_id"),
                   duty ? QJsonValue(duty->contentId) : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("territory_id"),
                   duty ? QJsonValue(duty->territoryId) : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("duty_name"),
                   duty ? QJsonValue(QString::fromUtf8(duty->name))
                        : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("duty_category"),
                   duty ? QJsonValue(QString::fromUtf8(duty->category))
                        : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("duty_level"),
                   duty ? QJsonValue(duty->level) : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("duty_expansion"),
                   duty ? QJsonValue(QString::fromUtf8(duty->expansion))
                        : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("job_id"),
                   job ? QJsonValue(job->id) : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("job_name"),
                   QString::fromUtf8(job ? job->name : "未知"));
        run.insert(QStringLiteral("role"),
                   QString::fromLatin1(job ? job->role : "UNKNOWN"));
        run.insert(QStringLiteral("role_group"),
                   QString::fromUtf8(job ? job->roleGroup : "未知"));
        run.insert(QStringLiteral("matched_at_utc"), isoUtc(draft.matched));
        run.insert(QStringLiteral("entered_at_utc"),
                   draft.entered.isValid() ? QJsonValue(isoUtc(draft.entered))
                                           : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("ended_at_utc"),
                   draft.ended.isValid() ? QJsonValue(isoUtc(draft.ended))
                                         : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("duration_ms"),
                   draft.hasDuration ? QJsonValue(double(draft.durationMs))
                                     : QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("result"), draft.result);
        run.insert(QStringLiteral("detection_confidence"),
                   (duty && job) ? QStringLiteral("HIGH") : QStringLiteral("MEDIUM"));
        run.insert(QStringLiteral("source"), QStringLiteral("AUTO_NETWORK"));
        run.insert(QStringLiteral("contributes_to_goal"), true);
        run.insert(QStringLiteral("manually_created"), false);
        run.insert(QStringLiteral("manually_corrected"), false);
        run.insert(QStringLiteral("soft_deleted"), false);
        run.insert(QStringLiteral("pending_review"), false);
        run.insert(QStringLiteral("note"), QString());
        // Every serialised Run carries the field, null when there is no
        // reflection; seedReflections() fills the prototype's six in below.
        run.insert(QStringLiteral("reflection"), QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("created_at_utc"), isoUtc(draft.matched));
        run.insert(QStringLiteral("updated_at_utc"),
                   isoUtc(draft.ended.isValid() ? draft.ended : draft.matched));
        m_runs.append(run);
    }

    for (int i = 0; i < int(sizeof(kHighEndOffsets) / sizeof(kHighEndOffsets[0])); ++i) {
        const int index = int(m_runs.size()) - kHighEndOffsets[i];
        if (index < 0)
            continue;
        const Duty &duty = kHighEndDuties[i];
        QJsonObject run = m_runs.at(index).toObject();
        run.insert(QStringLiteral("content_id"), duty.contentId);
        run.insert(QStringLiteral("territory_id"), duty.territoryId);
        run.insert(QStringLiteral("duty_name"), QString::fromUtf8(duty.name));
        run.insert(QStringLiteral("duty_category"), QString::fromUtf8(duty.category));
        run.insert(QStringLiteral("duty_level"), duty.level);
        run.insert(QStringLiteral("duty_expansion"), QString::fromUtf8(duty.expansion));
        m_runs.replace(index, run);
    }

    if (m_runs.size() <= 71)
        return;

    // -- the hand-placed edge cases from the prototype ----------------------

    // A DISCONNECTED verdict the user corrected to COMPLETED, with the
    // revision that records why.
    QJsonObject corrected = m_runs.at(10).toObject();
    const QDateTime entered = fromIso(corrected.value(QStringLiteral("entered_at_utc")));
    if (entered.isValid()) {
        const QDateTime ended = entered.addSecs(19 * 60);
        corrected.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        corrected.insert(QStringLiteral("ended_at_utc"), isoUtc(ended));
        corrected.insert(QStringLiteral("duration_ms"), double(19 * 60 * 1000));
        corrected.insert(QStringLiteral("manually_corrected"), true);
        corrected.insert(QStringLiteral("revision"), 2);
        m_runs.replace(10, corrected);

        QJsonArray changes;
        changes.append(changeEntry(QStringLiteral("result"),
                                   QStringLiteral("DISCONNECTED"),
                                   QStringLiteral("COMPLETED")));
        changes.append(changeEntry(QStringLiteral("ended_at_utc"),
                                   QJsonValue(QJsonValue::Null), isoUtc(ended)));

        QJsonObject revision;
        revision.insert(QStringLiteral("revision_id"), mockUuid(QStringLiteral("rev-10-2")));
        revision.insert(QStringLiteral("run_id"), corrected.value(QStringLiteral("run_id")));
        revision.insert(QStringLiteral("revision"), 2);
        revision.insert(QStringLiteral("changed_at_utc"),
                        isoUtc(fromIso(corrected.value(QStringLiteral("matched_at_utc")))
                                   .addSecs(2 * 3600)));
        revision.insert(QStringLiteral("change_kind"), QStringLiteral("CORRECT"));
        revision.insert(QStringLiteral("reason"),
                        QString::fromUtf8("网络中断，实际已通关（队友截图确认）"));
        revision.insert(QStringLiteral("actor"), QStringLiteral("USER"));
        revision.insert(QStringLiteral("changes"), changes);
        m_revisions.append(revision);
    }

    // A manually back-filled run.
    QJsonObject manual = m_runs.at(40).toObject();
    manual.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
    manual.insert(QStringLiteral("manually_created"), true);
    manual.insert(QStringLiteral("mentor_roulette_id"), QJsonValue(QJsonValue::Null));
    manual.insert(QStringLiteral("protocol_profile_id"), QJsonValue(QJsonValue::Null));
    manual.insert(QStringLiteral("capture_session_id"), QJsonValue(QJsonValue::Null));
    manual.insert(QStringLiteral("detection_confidence"), QStringLiteral("NONE"));
    manual.insert(QStringLiteral("job_id"), QJsonValue(QJsonValue::Null));
    manual.insert(QStringLiteral("job_name"), QString::fromUtf8("未知"));
    manual.insert(QStringLiteral("role"), QStringLiteral("UNKNOWN"));
    manual.insert(QStringLiteral("role_group"), QString::fromUtf8("未知"));
    manual.insert(QStringLiteral("note"), QString::fromUtf8("程序未运行时手动补录"));
    m_runs.replace(40, manual);

    QJsonObject manualRevision;
    manualRevision.insert(QStringLiteral("revision_id"), mockUuid(QStringLiteral("rev-40-1")));
    manualRevision.insert(QStringLiteral("run_id"), manual.value(QStringLiteral("run_id")));
    manualRevision.insert(QStringLiteral("revision"), 1);
    manualRevision.insert(QStringLiteral("changed_at_utc"),
                          manual.value(QStringLiteral("created_at_utc")));
    manualRevision.insert(QStringLiteral("change_kind"), QStringLiteral("CREATE_MANUAL"));
    manualRevision.insert(QStringLiteral("reason"), QString::fromUtf8("程序未运行时手动补录"));
    manualRevision.insert(QStringLiteral("actor"), QStringLiteral("USER"));
    manualRevision.insert(QStringLiteral("changes"), manualCreationChanges(manual));
    m_revisions.append(manualRevision);

    // Two runs the crash-recovery service closed without evidence, exactly as
    // CrashRecoveryService closes a run left open: INTERRUPTED, or 进本前取消 when it
    // never entered, at LOW confidence and pending review, ended at the restart, through
    // a SYSTEM revision whose changes say what it was before - still in flight. It never
    // infers a clear and leaves the duration unknown. The dashboard banner, the history
    // marker and the 确认 action have something real-shaped to render.
    for (int index : {62, 63}) {
        QJsonObject review = m_runs.at(index).toObject();
        const bool entered = review.value(QStringLiteral("entered_at_utc")).isString();
        const QJsonValue restartedAt = review.value(QStringLiteral("ended_at_utc"));
        const QString closedAs = entered ? QStringLiteral("INTERRUPTED")
                                         : QStringLiteral("CANCELLED_BEFORE_ENTRY");
        const QJsonArray changes{
            changeEntry(QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null), restartedAt),
            changeEntry(QStringLiteral("result"), QStringLiteral("UNKNOWN"), closedAs),
            changeEntry(QStringLiteral("pending_review"), false, true),
            changeEntry(QStringLiteral("detection_confidence"),
                        review.value(QStringLiteral("detection_confidence")), QStringLiteral("LOW")),
        };
        review.insert(QStringLiteral("result"), closedAs);
        review.insert(QStringLiteral("duration_ms"), QJsonValue(QJsonValue::Null));
        review.insert(QStringLiteral("pending_review"), true);
        review.insert(QStringLiteral("detection_confidence"), QStringLiteral("LOW"));
        review.insert(QStringLiteral("revision"), 2);
        review.insert(QStringLiteral("updated_at_utc"), restartedAt);
        m_runs.replace(index, review);
        m_revisions.append(sampleRevision(
            review, restartedAt.toString(), "CORRECT", "SYSTEM",
            entered ? QString::fromUtf8("程序重启时发现未完结记录，已记为中断并标记待复核。")
                    : QString::fromUtf8("程序重启时发现未完结记录，它尚未进入副本，已记为进本前取消并标记待复核。"),
            changes));
    }

    // A soft-deleted duplicate: listed only on request, never counted. The player
    // deleted it, and said why, through a revision like every other change
    // (RunMutationService.SoftDeleteRun).
    QJsonObject deleted = m_runs.at(55).toObject();
    const QString deletedAt =
        isoUtc(fromIso(deleted.value(QStringLiteral("matched_at_utc"))).addSecs(3 * 3600));
    deleted.insert(QStringLiteral("soft_deleted"), true);
    deleted.insert(QStringLiteral("revision"), 2);
    deleted.insert(QStringLiteral("updated_at_utc"), deletedAt);
    m_runs.replace(55, deleted);
    m_revisions.append(sampleRevision(deleted, deletedAt, "SOFT_DELETE", "USER",
                                      QString::fromUtf8("重复记录"),
                                      QJsonArray{changeEntry(QStringLiteral("soft_deleted"), false, true)}));

    // Two runs with a missing field, so the "unknown" buckets are populated.
    QJsonObject noJob = m_runs.at(70).toObject();
    noJob.insert(QStringLiteral("job_id"), QJsonValue(QJsonValue::Null));
    noJob.insert(QStringLiteral("job_name"), QString::fromUtf8("未知"));
    noJob.insert(QStringLiteral("role"), QStringLiteral("UNKNOWN"));
    noJob.insert(QStringLiteral("role_group"), QString::fromUtf8("未知"));
    noJob.insert(QStringLiteral("detection_confidence"), QStringLiteral("MEDIUM"));
    m_runs.replace(70, noJob);

    QJsonObject noDuty = m_runs.at(71).toObject();
    for (const char *field : {"content_id", "territory_id", "duty_name",
                              "duty_category", "duty_level", "duty_expansion"}) {
        noDuty.insert(QString::fromLatin1(field), QJsonValue(QJsonValue::Null));
    }
    noDuty.insert(QStringLiteral("detection_confidence"), QStringLiteral("MEDIUM"));
    m_runs.replace(71, noDuty);

    // Every run capture recorded opens its chain with its creation; the manual one
    // above opens with its CREATE_MANUAL.
    for (const QJsonValue &value : std::as_const(m_runs)) {
        const QJsonObject run = value.toObject();
        if (run.value(QStringLiteral("source")).toString() == QLatin1String("AUTO_NETWORK"))
            m_revisions.append(creationRevision(run));
    }

    seedReflections();
}

// ---------------------------------------------------------------------------
// 导随心得
//
// The prototype (DOC/表单提交后设计/mentor-recorder-ff14.dc.html, const RF=)
// takes the last nine COMPLETED, non-deleted runs and writes a reflection on
// six of them, five minutes after each run ended. Reproduced literally so the
// dashboard panel and the history flags look like the design.
// ---------------------------------------------------------------------------

void MockBackend::seedReflections()
{
    static const struct { const char *mood; const char *text; } kReflections[] = {
        {"good", "新人坦克第一次打灯塔，全程语音提醒机制，最后一个 boss 一次过。他说以后想练奶妈。"},
        {"ok",   "队里两个新人都不说话，机制靶子吃满，好在没灭。下次进本先打个招呼。"},
        {"bad",  "第二个 boss 灭了三次，DPS 一直站在圈里。忍住没说重话，退本后还是有点烦躁。"},
        {"good", "占星带三个新人打神龙，念了一遍机制顺序，居然全员存活。回城收到两张感谢卡。"},
        {"ok",   "佐拉加，新人不会看地板，一直吃塔。打完顺手给了两条宏。"},
        {"good", "老玩家练小号，节奏极快，十二分钟出本。轻松的一次。"},
    };
    static const int kSlots[] = {0, 2, 3, 5, 6, 8};

    QList<int> completed;
    for (int i = 0; i < m_runs.size(); ++i) {
        const QJsonObject run = m_runs.at(i).toObject();
        if (!isSoftDeleted(run)
            && run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED")) {
            completed.append(i);
        }
    }
    if (completed.size() < 9)
        return;
    const QList<int> lastNine = completed.mid(completed.size() - 9);

    for (int i = 0; i < 6; ++i) {
        QJsonObject run = m_runs.at(lastNine.at(kSlots[i])).toObject();
        const QDateTime ended = fromIso(run.value(QStringLiteral("ended_at_utc")));
        if (!ended.isValid())
            continue;
        const QString at = isoUtc(ended.addSecs(5 * 60));

        QJsonObject reflection;
        reflection.insert(QStringLiteral("mood"), QString::fromLatin1(kReflections[i].mood));
        reflection.insert(QStringLiteral("text"), QString::fromUtf8(kReflections[i].text));
        reflection.insert(QStringLiteral("created_at_utc"), at);
        reflection.insert(QStringLiteral("updated_at_utc"), at);
        run.insert(QStringLiteral("reflection"), reflection);
        m_runs.replace(lastNine.at(kSlots[i]), run);
    }
}

// ---------------------------------------------------------------------------
// Status payloads
// ---------------------------------------------------------------------------

namespace {

QJsonObject mockHypothesis(const char *name, const char *label, const char *opcode,
                           const char *direction, const char *group, int expectedLength,
                           bool researchEligible)
{
    return QJsonObject{
        {QStringLiteral("profile_id"), QStringLiteral("cn.2026.08.05.candidate")},
        {QStringLiteral("name"), QString::fromUtf8(name)},
        {QStringLiteral("label"), QString::fromUtf8(label)},
        {QStringLiteral("opcode"), QString::fromUtf8(opcode)},
        {QStringLiteral("direction"), QString::fromUtf8(direction)},
        {QStringLiteral("group"), QString::fromUtf8(group)},
        {QStringLiteral("expected_length"), expectedLength},
        {QStringLiteral("research_eligible"), researchEligible},
    };
}

// $defs/CandidateHypothesis, pinned to the fixed, non-obfuscated declarations in
// protocol-profiles/cn/cn.2026.08.05.candidate.json. Eligibility is the same closed
// catalogue used by mock settings validation; payloads above 512 bytes stay out.
QJsonArray mockCandidateHypotheses()
{
    return QJsonArray{
        mockHypothesis("QUEUE_REGISTRATION", "排本登记", "0x03bb", "C2S", "queue", 128, true),
        mockHypothesis("QUEUE_REGISTRATION_PRECURSOR", "请求报文（与排本无关）", "0x0104", "C2S", "queue", 8, true),
        mockHypothesis("QUEUE_ACK_B0", "周期性服务器报文（原以为是登记应答）", "0x00b0", "S2C", "queue", 8, true),
        mockHypothesis("QUEUE_ACK_20B", "请求回执（0x0104 的回复）", "0x020b", "S2C", "queue", 8, true),
        mockHypothesis("FINDER_ACTION", "副本查找器操作（第 0 字节 = 随机任务编号）", "0x034b", "C2S", "queue", 24, true),
        mockHypothesis("FINDER_STATE_NOTIFICATION", "副本查找器状态更新", "0x0323", "S2C", "finder", 40, true),
        mockHypothesis("ZONE_LOAD_C2S_0178", "进本／换区 请求 0178", "0x0178", "C2S", "zone_load", 72, true),
        mockHypothesis("ZONE_LOAD_C2S_008F", "进本／换区 请求 008f", "0x008f", "C2S", "zone_load", 8, true),
        mockHypothesis("ZONE_LOAD_C2S_00E8", "进本／换区 请求 00e8", "0x00e8", "C2S", "zone_load", 8, true),
        mockHypothesis("ZONE_LOAD_C2S_024D", "进本／换区 请求 024d", "0x024d", "C2S", "zone_load", 8, true),
        mockHypothesis("ZONE_LOAD_C2S_0187", "进本／换区 请求 0187", "0x0187", "C2S", "zone_load", 8, true),
        mockHypothesis("ZONE_LOAD_C2S_0281", "进本／换区 请求 0281", "0x0281", "C2S", "zone_load", 8, true),
        mockHypothesis("ZONE_LOAD_C2S_01A2", "进本／换区 请求 01a2", "0x01a2", "C2S", "zone_load", 24, true),
        mockHypothesis("ZONE_LOAD_S2C_01B8", "进本／换区 加载 01b8", "0x01b8", "S2C", "zone_load", 3672, false),
        mockHypothesis("ZONE_LOAD_S2C_0077", "进本／换区 加载 0077", "0x0077", "S2C", "zone_load", 640, false),
        mockHypothesis("ZONE_LOAD_S2C_0347", "进本／换区 加载 0347", "0x0347", "S2C", "zone_load", 808, false),
        mockHypothesis("ZONE_LOAD_S2C_014A", "进本／换区 加载 014a", "0x014a", "S2C", "zone_load", 456, true),
        mockHypothesis("ZONE_LOAD_S2C_0325", "进本／换区 加载 0325", "0x0325", "S2C", "zone_load", 144, true),
        mockHypothesis("ZONE_LOAD_S2C_031D", "进本／换区 加载 031d", "0x031d", "S2C", "zone_load", 448, true),
        mockHypothesis("ZONE_LOAD_S2C_0153", "进本／换区 加载 0153", "0x0153", "S2C", "zone_load", 360, true),
        mockHypothesis("ZONE_LOAD_S2C_0214", "进本／换区 加载 0214", "0x0214", "S2C", "zone_load", 424, true),
        mockHypothesis("ZONE_LOAD_S2C_0149", "进本／换区 加载 0149", "0x0149", "S2C", "zone_load", 104, true),
        mockHypothesis("ZONE_LOAD_S2C_025A", "进本／换区 加载 025a", "0x025a", "S2C", "zone_load", 104, true),
        mockHypothesis("ZONE_LOAD_S2C_02FC", "进本／换区 加载 02fc", "0x02fc", "S2C", "zone_load", 104, true),
        mockHypothesis("INIT_ZONE_S2C_028D", "区域初始化（含区域编号）", "0x028d", "S2C", "identity", 136, true),
        mockHypothesis("CLASS_INFO_S2C_0350", "职业信息更新", "0x0350", "S2C", "identity", 16, true),
    };
}

} // namespace

QJsonObject MockBackend::captureStatus() const
{
    const bool running = m_capturing && !m_npcapMissing;
    const bool candidateEnabled = captureSettings()
                                      .value(QStringLiteral("candidate_validation_enabled"))
                                      .toBool();

    QJsonObject status;
    status.insert(QStringLiteral("state"),
                  running ? QStringLiteral("RUNNING") : QStringLiteral("STOPPED"));
    status.insert(QStringLiteral("capture_session_id"),
                  running ? QJsonValue(mockUuid(QStringLiteral("capture-session-1")))
                          : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("npcap_installed"), !m_npcapMissing);
    status.insert(QStringLiteral("npcap_version"),
                  m_npcapMissing ? QJsonValue(QJsonValue::Null)
                                 : QJsonValue(QStringLiteral("1.79")));
    status.insert(QStringLiteral("ffxiv_running"), true);
    status.insert(QStringLiteral("ffxiv_process_id"), 18244);
    status.insert(QStringLiteral("game_selection_required"), false);
    status.insert(QStringLiteral("game_selection_reason"), QStringLiteral("NONE"));
    status.insert(QStringLiteral("game_processes"), QJsonArray{QJsonObject{
        {QStringLiteral("process_id"), 18244},
        {QStringLiteral("started_at_utc"), QStringLiteral("2026-09-04T12:00:00.000Z")},
        {QStringLiteral("selection_token"), QStringLiteral("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")}}});
    status.insert(QStringLiteral("game_build"), candidateEnabled
                      ? QStringLiteral("2026.08.05.0000.0000")
                      : QStringLiteral("2026.08.12.0000.0000"));
    status.insert(QStringLiteral("region"), QStringLiteral("CN"));
    // Candidate mode keeps formal parsing fail-closed. Both mock projections
    // remain explicitly synthetic and never claim a verified event profile.
    status.insert(QStringLiteral("profile_status"), candidateEnabled
                      ? QStringLiteral("UNSUPPORTED_BUILD") : QStringLiteral("UNVERIFIED"));
    status.insert(QStringLiteral("profile_status_label"), QStringLiteral("SYNTHETIC_ONLY"));
    status.insert(QStringLiteral("candidate_validation_enabled"), candidateEnabled);
    status.insert(QStringLiteral("candidate_profile_id"),
                  candidateEnabled ? QJsonValue(QStringLiteral("cn.2026.08.05.candidate"))
                                   : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("candidate_observation_count"), int(m_candidateObservations.size()));
    status.insert(QStringLiteral("candidate_hypotheses"), mockCandidateHypotheses());
    status.insert(QStringLiteral("profile_id"), candidateEnabled
                      ? QJsonValue(QJsonValue::Null) : QJsonValue(QStringLiteral("cn/2026.08.12")));
    status.insert(QStringLiteral("profile_matches_build"), !candidateEnabled && !m_npcapMissing);
    status.insert(QStringLiteral("adapter_id"), QStringLiteral("Ethernet"));
    status.insert(QStringLiteral("adapter_description"),
                  QStringLiteral("Intel(R) Ethernet"));
    status.insert(QStringLiteral("adapter_auto_selected"), true);
    status.insert(QStringLiteral("connection_count"), running ? 1 : 0);
    status.insert(QStringLiteral("started_at_utc"),
                  running ? QJsonValue(isoUtc(m_now.addSecs(-(2 * 3600 + 14 * 60 + 9))))
                          : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("packets_observed"), 918432);
    status.insert(QStringLiteral("packets_dropped"), 0);
    status.insert(QStringLiteral("queue_depth"), running ? 3 : 0);
    status.insert(QStringLiteral("queue_capacity"), 4096);
    status.insert(QStringLiteral("last_error_code"),
                  m_npcapMissing ? QJsonValue(QStringLiteral("ERR_NPCAP_MISSING"))
                                 : QJsonValue(QJsonValue::Null));
    // Constants the user can verify at runtime: the injected hook is never
    // used and the monitor is always the passive Npcap path.
    status.insert(QStringLiteral("monitor_type"), QStringLiteral("WinPCap"));
    status.insert(QStringLiteral("injected_hook_enabled"), false);

    status.insert(QStringLiteral("uptime_ms"),
                  running ? double((2 * 3600 + 14 * 60 + 9) * 1000) : 0.0);

    // The contract's own counter names since contracts/CHANGELOG.md entry 14.
    // The mock reports the same fields the Collector does, so the diagnostics
    // page is exercised offline through exactly the path it uses live.
    status.insert(QStringLiteral("message_rate_per_second"),
                  running && !m_midstreamSuspected ? 38.2 : 0.0);
    status.insert(QStringLiteral("connection_count"), running ? 1 : 0);
    // A midstream session decodes nothing at all and only accumulates decode
    // errors; that is the whole point of the fixture.
    status.insert(QStringLiteral("messages_decoded"),
                  running ? (m_midstreamSuspected ? 0 : 917994) : 0);
    status.insert(QStringLiteral("decode_errors"),
                  running ? (m_midstreamSuspected ? 24518 : 438) : 0);
    status.insert(QStringLiteral("parse_ok_count"), running && !candidateEnabled ? 12879 : 0);
    status.insert(QStringLiteral("parse_fail_count"), running && !candidateEnabled ? 52 : 0);
    status.insert(QStringLiteral("duplicate_count"), running && !candidateEnabled ? 17 : 0);
    // Undeclared opcodes: the bulk of ordinary traffic, counted but not failed
    // (contracts/CHANGELOG.md entry 18).
    status.insert(QStringLiteral("ignored_count"), running && !candidateEnabled ? 904841 : 0);
    // A run that is matched but not yet entered has the match announcement as its
    // most recent valid event (contracts/CHANGELOG.md 2026-09-19), so that fixture
    // is the one that exercises MATCH_ANNOUNCED offline; every other fixture keeps
    // the settled duty result. Before this the mock reported DUTY_RESULT always and
    // no screenshot or test ever reached the new token (2026-09-21 audit, finding 20).
    const bool announced = m_liveMode == LiveMode::Matched;
    status.insert(QStringLiteral("last_valid_event_at_utc"),
                  running && !candidateEnabled
                          ? QJsonValue(isoUtc(m_now.addSecs(announced ? -95 : -128)))
                          : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("last_valid_event_kind"),
                  running && !candidateEnabled
                          ? QJsonValue(announced ? QStringLiteral("MATCH_ANNOUNCED")
                                                 : QStringLiteral("DUTY_RESULT"))
                          : QJsonValue(QJsonValue::Null));
    // Oodle disclosure fields: which signature table the decoder is using, and
    // whether the profile behind it has been verified. Never invented as
    // "verified" - this backend has never seen a real packet.
    status.insert(QStringLiteral("oodle_signature_source"),
                  running ? QJsonValue(QStringLiteral("MACHINA_BUILTIN"))
                          : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("oodle_profile_id"),
                  running ? QJsonValue(QStringLiteral("cn.2026.08.05"))
                          : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("oodle_profile_status"),
                  running ? QJsonValue(QStringLiteral("CANDIDATE"))
                          : QJsonValue(QJsonValue::Null));

    // docs/live-validation-guide.md section 6: the CN client keeps its zone
    // connection across teleports, so a capture started after login never
    // recovers on its own. The Collector says so; the desktop only repeats it.
    status.insert(QStringLiteral("midstream_suspected"),
                  running && m_midstreamSuspected);
    status.insert(QStringLiteral("hint"),
                  running && m_midstreamSuspected
                      ? QJsonValue(QString::fromUtf8(
                            "当前连接缺少可用的解码上下文，"
                            "本次登录不会有记录。请登出到标题画面再重新登录一次（不用关闭游戏），"
                            "之后会自动恢复。"))
                      : QJsonValue(QJsonValue::Null));

    status.insert(QStringLiteral("recent_parser_errors"),
                  candidateEnabled ? QJsonArray() : recentParserErrors());

    // 本机校准. While a draft is being observed or confirmed the build has no
    // profile at all (UNSUPPORTED_BUILD); once one is written on this machine
    // it is VERIFIED like any other, and says where it came from.
    const QJsonObject calibration = calibrationStatus();
    if (!calibration.isEmpty()) {
        // A verified shared profile records while calibration stays armed beside
        // it, so it is bound without being DONE.
        const bool sharedBound = sharedProfileBound();
        const bool bound = sharedBound || localProfileBound();
        status.insert(QStringLiteral("calibration"), calibration);
        status.insert(QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000"));
        status.insert(QStringLiteral("profile_status"),
                      bound ? QStringLiteral("VERIFIED") : QStringLiteral("UNSUPPORTED_BUILD"));
        status.insert(QStringLiteral("profile_origin"),
                      !bound ? QJsonValue(QJsonValue::Null)
                             : QJsonValue(sharedBound ? QStringLiteral("SHARED_CALIBRATION")
                                                      : QStringLiteral("LOCAL_CALIBRATION")));
        status.insert(QStringLiteral("profile_id"),
                      !bound ? QJsonValue(QJsonValue::Null)
                             : QJsonValue(sharedBound ? QStringLiteral("cn.2026.09.01.0000.0000.shared")
                                                      : QStringLiteral("cn.2026.09.01.local")));
        status.insert(QStringLiteral("profile_matches_build"), bound);
        status.insert(QStringLiteral("calibration_bound_at_utc"),
                      calibration.value(QStringLiteral("bound_at_utc")));
    }
    if (!m_recordingFixture.isEmpty()) {
        // The three 游戏未运行 fixtures. Plain "waiting" is the first-ever run:
        // no install directory has been remembered, so the Collector cannot name
        // a version either. The two suffixed ones are the ordinary case after
        // that - the installed version was read off disk and the profile
        // question is already answered - with and without a usable profile.
        const bool waiting = m_recordingFixture.startsWith(QLatin1String("waiting"));
        const bool buildKnown = waiting && m_recordingFixture != QLatin1String("waiting");
        status.insert(QStringLiteral("ffxiv_running"), !waiting);
        status.insert(QStringLiteral("profile_status"),
                      m_recordingFixture == QLatin1String("listening")
                              || m_recordingFixture == QLatin1String("waiting-verified")
                          ? QStringLiteral("VERIFIED") : QStringLiteral("NONE"));
        if (m_recordingFixture == QLatin1String("checking")) status.remove(QStringLiteral("profile_status"));
        if (waiting) {
            status.insert(QStringLiteral("game_processes"), QJsonArray());
            status.insert(QStringLiteral("state"), QStringLiteral("STOPPED"));
            status.insert(QStringLiteral("ffxiv_process_id"), QJsonValue::Null);
        }
        if (waiting && !buildKnown) {
            status.insert(QStringLiteral("game_build"), QJsonValue::Null);
            status.insert(QStringLiteral("region"), QStringLiteral("UNKNOWN"));
        }
        if (buildKnown) {
            status.insert(QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000"));
            status.insert(QStringLiteral("region"), QStringLiteral("CN"));
            const bool verified = m_recordingFixture == QLatin1String("waiting-verified");
            // A profile that ships with the software, so no 来源 to name.
            status.insert(QStringLiteral("profile_id"),
                          verified ? QJsonValue(QStringLiteral("cn/2026.09.01"))
                                   : QJsonValue(QJsonValue::Null));
            status.insert(QStringLiteral("profile_origin"), QJsonValue::Null);
            status.insert(QStringLiteral("profile_matches_build"), verified);
        }
        if (m_recordingFixture == QLatin1String("waiting-calibrating")) {
            // 尚无可用档案：采集服务为这个版本备好校准，但游戏启动前什么也观察不到。
            status.insert(QStringLiteral("profile_status"), QStringLiteral("UNSUPPORTED_BUILD"));
            QJsonObject waitingCalibration{
                {QStringLiteral("state"), QStringLiteral("WAITING")},
                {QStringLiteral("game_build"), QStringLiteral("2026.09.01.0000.0000")},
                {QStringLiteral("template_profile_id"), QStringLiteral("cn.2026.08.05")},
                {QStringLiteral("local_profile_id"), QJsonValue::Null},
                {QStringLiteral("bound_at_utc"), QJsonValue::Null},
                {QStringLiteral("blockers"), QJsonArray()},
                {QStringLiteral("progress"),
                 QJsonObject{{QStringLiteral("finder_request_seen"), false},
                             {QStringLiteral("pop_seen"), false},
                             {QStringLiteral("pop_shape_seen"), false},
                             {QStringLiteral("zone_clusters"), 0},
                             {QStringLiteral("duty_entry_seen"), false},
                             {QStringLiteral("duty_exit_seen"), false}}},
                {QStringLiteral("events"), QJsonArray()}};
            // 共享校准 fixtures still apply, so the closed-game chain can be seen
            // deferring to a card that is fetching or awaiting consent.
            if (!m_sharedState.isEmpty())
                waitingCalibration.insert(QStringLiteral("shared"), sharedCalibrationStatus());
            status.insert(QStringLiteral("calibration"), waitingCalibration);
        }
        // Keep SYNTHETIC_ONLY provenance even when exercising the VERIFIED branch.
    }
    if (m_recordingFixture.startsWith(QLatin1String("multiple"))) {
        auto choices = status.value(QStringLiteral("game_processes")).toArray();
        auto second = choices.first().toObject();
        second["process_id"] = 18245;
        second["started_at_utc"] = QStringLiteral("2026-09-04T12:30:00.000Z");
        second["selection_token"] = QStringLiteral("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
        if (m_recordingFixture == QLatin1String("multiple-exited")) choices = QJsonArray{second};
        else choices.append(second);
        const bool chosen = m_selectedGameProcessId > 0;
        status["game_processes"] = choices;
        status["game_selection_required"] = !chosen;
        status["game_selection_reason"] = chosen ? QStringLiteral("NONE")
            : m_recordingFixture == QLatin1String("multiple-exited") ? QStringLiteral("EXITED") : QStringLiteral("MULTIPLE");
        status["ffxiv_process_id"] = chosen ? QJsonValue(m_selectedGameProcessId) : QJsonValue(QJsonValue::Null);
        status["ffxiv_running"] = chosen;
        status["state"] = chosen ? QStringLiteral("RUNNING") : QStringLiteral("STOPPED");
        status["profile_status"] = chosen ? QStringLiteral("VERIFIED") : QStringLiteral("NONE");
        if (!chosen) {
            // Nothing is locked, so nothing runs and no client's build or region
            // is read: the Collector reports GameProcessDetection.NotRunning and
            // matches no profile (review OX-6).
            status["capture_session_id"] = QJsonValue::Null;
            status["started_at_utc"] = QJsonValue::Null;
            status["game_build"] = QJsonValue::Null;
            status["region"] = QStringLiteral("UNKNOWN");
            status["profile_id"] = QJsonValue::Null;
            status["profile_origin"] = QJsonValue::Null;
            status["profile_matches_build"] = false;
        }
    }
    return status;
}

QJsonObject MockBackend::captureSettings() const
{
    QJsonObject settings = m_captureSettings;
    // Defaults a fresh Collector would report. They are merged rather than
    // replaced so a value written through UpdateCaptureSettings survives.
    // On by default, like the Collector: listening has to precede login.
    if (!settings.contains(QStringLiteral("follow_game")))
        settings.insert(QStringLiteral("follow_game"), true);
    if (!settings.contains(QStringLiteral("autostart")))
        settings.insert(QStringLiteral("autostart"), false);
    if (!settings.contains(QStringLiteral("adapter_id")))
        settings.insert(QStringLiteral("adapter_id"), QJsonValue(QJsonValue::Null));
    if (!settings.contains(QStringLiteral("log_retention_days")))
        settings.insert(QStringLiteral("log_retention_days"), 7);
    if (!settings.contains(QStringLiteral("allow_without_profile")))
        settings.insert(QStringLiteral("allow_without_profile"), false);
    if (!settings.contains(QStringLiteral("region_override")))
        settings.insert(QStringLiteral("region_override"), QJsonValue(QJsonValue::Null));
    if (!settings.contains(QStringLiteral("candidate_validation_enabled")))
        settings.insert(QStringLiteral("candidate_validation_enabled"), false);
    // On by default, like the Collector: off means fail-closed silence on
    // every patch day.
    if (!settings.contains(QStringLiteral("auto_calibration_enabled")))
        settings.insert(QStringLiteral("auto_calibration_enabled"), true);
    // Default on, like the Collector (docs/privacy-boundary.md §8.2). The mock has no
    // network client either way.
    if (!settings.contains(QStringLiteral("shared_calibration_enabled")))
        settings.insert(QStringLiteral("shared_calibration_enabled"), true);
    // Default on, like the Collector (docs/privacy-boundary.md §8.4): notify
    // only, and this backend never reads anything from the network.
    if (!settings.contains(QStringLiteral("update_check_enabled")))
        settings.insert(QStringLiteral("update_check_enabled"), true);
    if (!settings.contains(QStringLiteral("research_payload_opcodes")))
        settings.insert(QStringLiteral("research_payload_opcodes"), QJsonArray());
    return settings;
}

QJsonArray MockBackend::runEvents(const QString &runId) const
{
    const int index = indexOfRun(runId);
    if (index < 0)
        return {};
    const QJsonObject run = m_runs.at(index).toObject();
    // A manually created run never had network events, and inventing some
    // would be the exact kind of lie the events tab exists to avoid.
    if (run.value(QStringLiteral("manually_created")).toBool(false))
        return {};

    // The Collector's own names (Domain/Events/SemanticEvent.cs) and the fields
    // SemanticEventProcessor.DetailJson keeps for them (review DT3-X2). The pop
    // opens the run, the zone load enters the duty, and the run ends with
    // whatever closed it - the result screen only for a clear. opcode null is
    // an event the Collector generates itself.
    struct Step {
        const char *type;
        const char *field;
        const char *opcode;
    };
    const QString result = run.value(QStringLiteral("result")).toString();
    const Step end = result == QLatin1String("COMPLETED")
                         ? Step{"DUTY_RESULT", "ended_at_utc", "0x0271"}
                     : result == QLatin1String("CANCELLED_BEFORE_ENTRY")
                         ? Step{"MATCH_CANCELLED", "ended_at_utc", "0x0142"}
                     : result == QLatin1String("DISCONNECTED")
                         ? Step{"CONNECTION_LOST", "ended_at_utc", nullptr}
                         : Step{"INSTANCE_LEFT", "ended_at_utc", "0x02A4"};
    const Step steps[] = {
        {"CONTENT_FINDER_POP", "matched_at_utc", "0x0142"},
        {"ZONE_INITIALIZATION", "entered_at_utc", "0x01A3"},
        end,
    };

    QJsonArray events;
    int ordinal = 0;
    for (const Step &step : steps) {
        const QJsonValue at = run.value(QLatin1String(step.field));
        if (!at.isString())
            continue;
        ++ordinal;
        const QLatin1String type(step.type);
        const bool observed = step.opcode != nullptr;

        QJsonValue parsed(QJsonValue::Null);
        if (type == QLatin1String("CONTENT_FINDER_POP")) {
            parsed = QJsonObject{
                {QStringLiteral("roulette_id"), run.value(QStringLiteral("mentor_roulette_id"))},
                {QStringLiteral("content_id"), run.value(QStringLiteral("content_id"))}};
        } else if (type == QLatin1String("ZONE_INITIALIZATION")) {
            parsed = QJsonObject{
                {QStringLiteral("content_id"), run.value(QStringLiteral("content_id"))},
                {QStringLiteral("territory_id"), run.value(QStringLiteral("territory_id"))}};
        }

        QJsonObject entry;
        entry.insert(QStringLiteral("event_id"),
                     mockUuid(QStringLiteral("event-%1-%2").arg(index).arg(ordinal)));
        entry.insert(QStringLiteral("event_type"), QString(type));
        entry.insert(QStringLiteral("observed_at_utc"), at);
        entry.insert(QStringLiteral("direction"),
                     observed ? QJsonValue(QStringLiteral("S2C")) : QJsonValue(QJsonValue::Null));
        entry.insert(QStringLiteral("opcode"), observed ? QJsonValue(QString::fromLatin1(step.opcode))
                                                        : QJsonValue(QJsonValue::Null));
        // A 12-hex-digit prefix of a payload hash, exactly like the trace sink
        // writes: never the payload itself.
        entry.insert(QStringLiteral("payload_hash"),
                     observed ? QJsonValue(mockUuid(QStringLiteral("hash-%1-%2").arg(index).arg(ordinal))
                                               .remove(QLatin1Char('-'))
                                               .left(12))
                              : QJsonValue(QJsonValue::Null));
        // $defs/RunEventEntry.parser_status: PARSED / SYNTHETIC / UNKNOWN.
        entry.insert(QStringLiteral("parser_status"),
                     observed ? QStringLiteral("PARSED") : QStringLiteral("SYNTHETIC"));
        entry.insert(QStringLiteral("protocol_profile_id"),
                     run.value(QStringLiteral("protocol_profile_id")));
        entry.insert(QStringLiteral("parsed"), parsed);
        events.append(entry);
    }
    return events;
}

QJsonObject MockBackend::captureValidationStatus() const
{
    const QString state = m_validationState;
    const bool active = state == QLatin1String("WAITING")
                        || state == QLatin1String("RECORDING")
                        || state == QLatin1String("STOPPING");
    const bool measured = state == QLatin1String("RECORDING")
                          || state == QLatin1String("STOPPING")
                          || state == QLatin1String("COMPLETED");
    const bool completed = state == QLatin1String("COMPLETED");
    const bool failed = state == QLatin1String("FAILED");
    QString reason = state;
    QString message = QString::fromUtf8("尚未开始验证。");
    if (state == QLatin1String("WAITING")) {
        reason = QStringLiteral("WAITING_RESTART");
        message = QString::fromUtf8("检测到既有连接；请重启游戏，本次会话会继续等待。");
    } else if (state == QLatin1String("RECORDING")) {
        reason = QStringLiteral("RECORDING");
        message = QString::fromUtf8("正在保存脱敏验证取证，不会自动记录导随。");
    } else if (state == QLatin1String("STOPPING")) {
        reason = QStringLiteral("STOPPING");
        message = QString::fromUtf8("正在停止源并排空已接收标记。");
    } else if (completed) {
        reason = QStringLiteral("COMPLETED");
        message = QString::fromUtf8("模拟取图状态：文件已关闭并生成 SHA256。");
    } else if (failed) {
        reason = QStringLiteral("FAILED");
        message = QString::fromUtf8("模拟写入失败；不得显示为保存成功。");
    }

    const QJsonValue nullValue(QJsonValue::Null);
    return QJsonObject{
        {QStringLiteral("state"), state},
        {QStringLiteral("active"), active},
        {QStringLiteral("reason"), reason},
        {QStringLiteral("message"), message},
        {QStringLiteral("session_id"), state == QLatin1String("IDLE")
                                               ? nullValue
                                               : QJsonValue(QStringLiteral("mock-validation-session"))},
        {QStringLiteral("started_at_utc"), measured
                                                  ? QJsonValue(isoUtc(m_now.addSecs(-312)))
                                                  : nullValue},
        {QStringLiteral("ended_at_utc"), completed || failed
                                                ? QJsonValue(isoUtc(m_now))
                                                : nullValue},
        {QStringLiteral("message_count"), measured ? QJsonValue(1842) : nullValue},
        {QStringLiteral("marker_count"), measured ? QJsonValue(m_validationMarkerCount)
                                                   : nullValue},
        {QStringLiteral("decode_error_count"), measured ? QJsonValue(3) : nullValue},
        {QStringLiteral("queue_dropped"), measured ? QJsonValue(0) : nullValue},
        {QStringLiteral("truncated"), false},
        {QStringLiteral("trace_path"), completed
                                             ? QJsonValue(QStringLiteral("C:/mock-only/trace.jsonl"))
                                             : nullValue},
        {QStringLiteral("sha256_path"), completed
                                              ? QJsonValue(QStringLiteral("C:/mock-only/trace.jsonl.sha256"))
                                              : nullValue},
        {QStringLiteral("sha256"), completed
                                         ? QJsonValue(QString(64, QLatin1Char('a')))
                                         : nullValue},
        {QStringLiteral("error_code"), failed
                                             ? QJsonValue(QStringLiteral("ERR_TRACE_WRITE"))
                                             : nullValue},
    };
}

QJsonArray MockBackend::recentParserErrors() const
{
    // Sample rows from the prototype. They are flagged as mock data here and
    // nowhere else: the IPC backend forwards whatever the Collector reports.
    if (m_npcapMissing)
        return {};

    const auto row = [this](int minutesAgo, const char *code, const char *opcode,
                            const char *message) {
        QJsonObject entry;
        entry.insert(QStringLiteral("at_utc"),
                     m_now.addSecs(-60 * minutesAgo)
                         .toUTC()
                         .toString(QStringLiteral("yyyy-MM-ddTHH:mm:ss.zzzZ")));
        entry.insert(QStringLiteral("code"), QString::fromLatin1(code));
        entry.insert(QStringLiteral("opcode"), QString::fromLatin1(opcode));
        entry.insert(QStringLiteral("direction"), QStringLiteral("S2C"));
        entry.insert(QStringLiteral("message"), QString::fromUtf8(message));
        return entry;
    };
    return QJsonArray{
        row(71, "E_LEN_MISMATCH", "0x01A3",
            "opcode 0x01A3 \u671f\u671b 0x3C0 \u5b57\u8282\uff0c\u5b9e\u9645 0x3B8\uff1b"
            "\u5df2\u5ffd\u7565\uff0c\u72b6\u6001\u672a\u66f4\u65b0"),
        row(2, "E_UNKNOWN_OPCODE", "0x0271",
            "S\u2192C 0x0271 \u4e0d\u5728 profile \u5b9a\u4e49\u4e2d\uff1b\u8ba1\u6570 +1"),
        row(1, "E_FIELD_CONSTRAINT", "0x0142",
            "roulette_id=0 \u4e0d\u6ee1\u8db3\u7ea6\u675f [1,64]"),
    };
}

QJsonObject MockBackend::collectorStatus() const
{
    QJsonObject status;
    status.insert(QStringLiteral("collector_version"), QStringLiteral("0.2.2-mock"));
    status.insert(QStringLiteral("protocol_version"), ipc::kProtocolVersion);
    status.insert(QStringLiteral("schema_version"), 1);
    status.insert(QStringLiteral("database_ready"), true);
    // A real directory: TtsService plays online speech only from the
    // tts-cache folder next to this file (docs/privacy-boundary.md §8.3).
    status.insert(QStringLiteral("database_path"),
                  QDir::toNativeSeparators(dataDirectory()
                                           + QStringLiteral("/mentor_recorder.db")));
    status.insert(QStringLiteral("uptime_ms"), double(2 * 3600 * 1000 + 14 * 60 * 1000));
    status.insert(QStringLiteral("capture"), captureStatus());
    // GetStatus.npcap exactly as CaptureWire.StatusExtras reports it, so the
    // 链路 panel's Npcap column reads the same fields offline as live.
    status.insert(QStringLiteral("npcap"), QJsonObject{
        {QStringLiteral("status"), m_npcapMissing ? QStringLiteral("NOT_INSTALLED")
                                                  : QStringLiteral("READY")},
        {QStringLiteral("installed"), !m_npcapMissing},
        {QStringLiteral("version"), m_npcapMissing ? QJsonValue(QJsonValue::Null)
                                                   : QJsonValue(QStringLiteral("1.79"))},
        {QStringLiteral("winpcap_compatible"), !m_npcapMissing},
        {QStringLiteral("admin_only"), false},
        {QStringLiteral("process_elevated"), false},
        {QStringLiteral("install_hint"),
         m_npcapMissing ? QJsonValue(QString::fromUtf8("本软件不附带 Npcap，请从 npcap.com 自行安装。"))
                        : QJsonValue(QJsonValue::Null)}});

    // GetStatus.update as the Collector reports it. The check itself is the
    // Collector's; this backend never fetches anything, so the verdict is
    // whatever setUpdateAvailable() was told and the version is synthetic.
    status.insert(QStringLiteral("update"), QJsonObject{
        {QStringLiteral("enabled"),
         captureSettings().value(QStringLiteral("update_check_enabled")).toBool(true)},
        {QStringLiteral("update_available"), m_updateAvailable},
        {QStringLiteral("latest_version"), m_updateAvailable
             ? QJsonValue(QStringLiteral("99.9.9")) : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("release_url"), m_updateAvailable
             ? QJsonValue(QStringLiteral(
                   "https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest"))
             : QJsonValue(QJsonValue::Null)},
        {QStringLiteral("last_checked_at_utc"), isoUtc(m_now.addSecs(-3600))},
        {QStringLiteral("last_outcome"), QStringLiteral("OK")}});

    QJsonArray warnings;
    warnings.append(QString::fromUtf8("这是 Phase 1 的模拟后端，数据不是真实记录。"));
    if (m_npcapMissing)
        warnings.append(QString::fromUtf8("未检测到 Npcap，自动记录已停用。"));
    status.insert(QStringLiteral("warnings"), warnings);
    return status;
}

QJsonObject MockBackend::currentRun() const
{
    QJsonObject payload;
    if (m_liveMode == LiveMode::None || !m_capturing || m_npcapMissing
        || captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool()) {
        payload.insert(QStringLiteral("state"), QStringLiteral("IDLE"));
        payload.insert(QStringLiteral("run"), QJsonValue(QJsonValue::Null));
        payload.insert(QStringLiteral("elapsed_ms"), QJsonValue(QJsonValue::Null));
        return payload;
    }

    const bool entered = m_liveMode == LiveMode::Entered;
    const QDateTime matched =
        m_now.addSecs(entered ? -(14 * 60) : -95);
    const QDateTime enteredAt = matched.addSecs(71);

    QJsonObject run;
    run.insert(QStringLiteral("run_id"), mockUuid(QStringLiteral("live-run")));
    run.insert(QStringLiteral("revision"), 1);
    run.insert(QStringLiteral("result"), QStringLiteral("UNKNOWN"));
    run.insert(QStringLiteral("source"), QStringLiteral("AUTO_NETWORK"));
    run.insert(QStringLiteral("mentor_roulette_id"), 9);
    run.insert(QStringLiteral("matched_at_utc"), isoUtc(matched));
    run.insert(QStringLiteral("entered_at_utc"),
               entered ? QJsonValue(isoUtc(enteredAt)) : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("ended_at_utc"), QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("duration_ms"), QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("content_id"), entered ? QJsonValue(17) : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("territory_id"),
               entered ? QJsonValue(1039) : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("duty_name"),
               entered ? QJsonValue(QString::fromUtf8("天狼星灯塔"))
                       : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("duty_category"),
               entered ? QJsonValue(QString::fromUtf8("四人迷宫"))
                       : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("job_id"), entered ? QJsonValue(24) : QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("job_name"),
               QString::fromUtf8(entered ? "白魔法师" : "未知"));
    run.insert(QStringLiteral("role"),
               QString::fromLatin1(entered ? "HEALER" : "UNKNOWN"));
    run.insert(QStringLiteral("detection_confidence"), QStringLiteral("HIGH"));
    run.insert(QStringLiteral("contributes_to_goal"), true);
    run.insert(QStringLiteral("manually_created"), false);
    run.insert(QStringLiteral("manually_corrected"), false);
    run.insert(QStringLiteral("soft_deleted"), false);
    run.insert(QStringLiteral("reflection"), QJsonValue(QJsonValue::Null));
    run.insert(QStringLiteral("created_at_utc"), isoUtc(matched));
    run.insert(QStringLiteral("updated_at_utc"), isoUtc(m_now));

    payload.insert(QStringLiteral("state"),
                   entered ? QStringLiteral("ENTERED_DUTY")
                           : QStringLiteral("MENTOR_MATCHED"));
    payload.insert(QStringLiteral("run"), run);
    payload.insert(QStringLiteral("elapsed_ms"),
                   entered ? QJsonValue(double(enteredAt.msecsTo(m_now)))
                           : QJsonValue(QJsonValue::Null));
    return payload;
}

// ---------------------------------------------------------------------------
// Mutations
// ---------------------------------------------------------------------------

int MockBackend::indexOfRun(const QString &runId) const
{
    for (int i = 0; i < m_runs.size(); ++i) {
        if (m_runs.at(i).toObject().value(QStringLiteral("run_id")).toString() == runId)
            return i;
    }
    return -1;
}

void MockBackend::touchRun(QJsonObject &run, const QString &changeKind,
                           const QString &reason, const QJsonArray &changes)
{
    const int revision = run.value(QStringLiteral("revision")).toInt() + 1;
    run.insert(QStringLiteral("revision"), revision);
    run.insert(QStringLiteral("updated_at_utc"), isoUtc(QDateTime::currentDateTimeUtc()));

    QJsonObject entry;
    entry.insert(QStringLiteral("revision_id"), ipc::newRequestId());
    entry.insert(QStringLiteral("run_id"), run.value(QStringLiteral("run_id")));
    entry.insert(QStringLiteral("revision"), revision);
    entry.insert(QStringLiteral("changed_at_utc"), isoUtc(QDateTime::currentDateTimeUtc()));
    entry.insert(QStringLiteral("change_kind"), changeKind);
    entry.insert(QStringLiteral("reason"), reason);
    entry.insert(QStringLiteral("actor"), QStringLiteral("USER"));
    entry.insert(QStringLiteral("changes"), changes);
    m_revisions.append(entry);
}

QJsonObject MockBackend::applyMutation(const QString &messageType,
                                       const QJsonObject &payload,
                                       QString *errorCode, QString *errorMessage)
{
    const QString reason = payload.value(QStringLiteral("reason")).toString().trimmed();
    if (reason.isEmpty()) {
        *errorCode = QStringLiteral("ERR_REASON_REQUIRED");
        *errorMessage = QString::fromUtf8("必须填写操作原因，请求已拒绝。");
        return {};
    }

    if (messageType == QLatin1String("UpdateAchievementBaseline")) {
        const int baseline = payload.value(QStringLiteral("baseline_completed_count"))
                                 .toInt(m_baselineCompletedCount);
        const int goal = payload.value(QStringLiteral("goal_count")).toInt(m_goalCount);
        // RunMutationService.UpdateAchievementBaseline refuses these, with its own
        // sentences, rather than storing something else than was asked.
        if (goal < 1) {
            *errorCode = QStringLiteral("ERR_BAD_REQUEST");
            *errorMessage = QString::fromUtf8("目标值必须大于等于 1。");
            return {};
        }
        if (baseline < 0) {
            *errorCode = QStringLiteral("ERR_BAD_REQUEST");
            *errorMessage = QString::fromUtf8("已完成次数不能为负数。");
            return {};
        }
        // RunMutationService.UpdateAchievementBaseline (audit 2026-10-03, S33-3): the same
        // goal and the same baseline change nothing, so nothing is written. The answer is
        // what is stored, under the history entry that stored it - a fresh id only when no
        // entry did - and, like the Collector, this backend announces nothing for it.
        const bool changed = goal != m_goalCount || baseline != m_baselineCompletedCount;
        QString auditEventId = m_baselineAuditEventId;
        if (changed) {
            // CS-7: only a changed baseline moves its effective time. The same count - a
            // goal-only edit - keeps the stored time, whatever the request says.
            if (baseline != m_baselineCompletedCount) {
                const QDateTime requested =
                    fromIso(payload.value(QStringLiteral("baseline_effective_at")));
                m_baselineEffectiveAt =
                    requested.isValid() ? requested.toUTC() : QDateTime::currentDateTimeUtc();
            }
            m_goalCount = goal;
            m_baselineCompletedCount = baseline;
            m_achievementUpdatedAt = QDateTime::currentDateTimeUtc();
            m_baselineAuditEventId = ipc::newRequestId();
            auditEventId = m_baselineAuditEventId;
        } else if (auditEventId.isEmpty()) {
            auditEventId = ipc::newRequestId();
        }
        QJsonObject result;
        result.insert(QStringLiteral("goal_count"), m_goalCount);
        result.insert(QStringLiteral("baseline_completed_count"), m_baselineCompletedCount);
        result.insert(QStringLiteral("baseline_effective_at"), isoUtc(m_baselineEffectiveAt));
        result.insert(QStringLiteral("updated_at_utc"), isoUtc(m_achievementUpdatedAt));
        result.insert(QStringLiteral("audit_event_id"), auditEventId);
        result.insert(QStringLiteral("idempotent_replay"), false);
        return result;
    }

    if (messageType == QLatin1String("CreateManualRun")) {
        QJsonObject run = payload;
        run.remove(QStringLiteral("reason"));
        run.insert(QStringLiteral("run_id"), ipc::newRequestId());
        run.insert(QStringLiteral("revision"), 1);
        run.insert(QStringLiteral("source"), QStringLiteral("MANUAL"));
        run.insert(QStringLiteral("manually_created"), true);
        run.insert(QStringLiteral("manually_corrected"), false);
        run.insert(QStringLiteral("soft_deleted"), false);
        run.insert(QStringLiteral("detection_confidence"), QStringLiteral("NONE"));
        run.insert(QStringLiteral("reflection"), QJsonValue(QJsonValue::Null));
        run.insert(QStringLiteral("created_at_utc"), isoUtc(QDateTime::currentDateTimeUtc()));
        run.insert(QStringLiteral("updated_at_utc"), isoUtc(QDateTime::currentDateTimeUtc()));
        if (!run.contains(QStringLiteral("contributes_to_goal")))
            run.insert(QStringLiteral("contributes_to_goal"), true);
        // RunMutationService.CreateManualRun (review DT4-X4): the duty catalogue
        // supplies the zone, and the duty's name and category the request left out;
        // the job catalogue the job's name and role. An omitted duration is derived
        // from the two times - an explicit null stays unknown - and
        // RunMutationRules.ValidateFinalValue refuses a record that cannot exist
        // before anything is stored.
        const QVariantMap duty =
            DutyCatalog::shared()->lookup(run.value(QStringLiteral("content_id")).toVariant());
        const QVariant territory = duty.value(QStringLiteral("territory_id"));
        run.insert(QStringLiteral("territory_id"), territory.isValid()
                                                       ? QJsonValue(territory.toLongLong())
                                                       : QJsonValue(QJsonValue::Null));
        for (const auto *field : {"duty_name", "duty_category"}) {
            const QString key = QString::fromLatin1(field);
            if (!run.value(key).isString())
                run.insert(key, QJsonValue::fromVariant(duty.value(key)));
        }
        const JobCatalog jobs;
        const QVariant jobId = run.value(QStringLiteral("job_id")).toVariant();
        run.insert(QStringLiteral("job_name"), jobs.jobName(jobId));
        run.insert(QStringLiteral("role"), jobs.role(jobId));
        run.insert(QStringLiteral("role_group"), jobs.roleGroup(jobId));
        if (!run.contains(QStringLiteral("duration_ms"))) {
            const QDateTime enteredAt = fromIso(run.value(QStringLiteral("entered_at_utc")));
            const QDateTime endedAt = fromIso(run.value(QStringLiteral("ended_at_utc")));
            run.insert(QStringLiteral("duration_ms"),
                       enteredAt.isValid() && endedAt.isValid()
                           ? QJsonValue(double(enteredAt.msecsTo(endedAt)))
                           : QJsonValue(QJsonValue::Null));
        }
        if (!acceptableFinalValue(run, errorCode, errorMessage))
            return {};
        m_runs.append(run);

        QJsonObject revision;
        revision.insert(QStringLiteral("revision_id"), ipc::newRequestId());
        revision.insert(QStringLiteral("run_id"), run.value(QStringLiteral("run_id")));
        revision.insert(QStringLiteral("revision"), 1);
        revision.insert(QStringLiteral("changed_at_utc"),
                        run.value(QStringLiteral("created_at_utc")));
        revision.insert(QStringLiteral("change_kind"), QStringLiteral("CREATE_MANUAL"));
        revision.insert(QStringLiteral("reason"), reason);
        revision.insert(QStringLiteral("actor"), QStringLiteral("USER"));
        revision.insert(QStringLiteral("changes"), manualCreationChanges(run));
        m_revisions.append(revision);

        QJsonObject result;
        result.insert(QStringLiteral("run_id"), run.value(QStringLiteral("run_id")));
        result.insert(QStringLiteral("revision"), 1);
        result.insert(QStringLiteral("audit_event_id"), ipc::newRequestId());
        result.insert(QStringLiteral("idempotent_replay"), false);
        result.insert(QStringLiteral("run"), run);
        return result;
    }

    const QString runId = payload.value(QStringLiteral("run_id")).toString();
    const int index = indexOfRun(runId);
    if (index < 0) {
        *errorCode = QStringLiteral("ERR_NOT_FOUND");
        *errorMessage = QString::fromUtf8("找不到该记录，请刷新列表后重试。");
        return {};
    }

    QJsonObject run = m_runs.at(index).toObject();
    const int expected = payload.value(QStringLiteral("expected_revision")).toInt(-1);
    if (expected >= 0 && expected != run.value(QStringLiteral("revision")).toInt()) {
        *errorCode = QStringLiteral("ERR_REVISION_CONFLICT");
        *errorMessage = QString::fromUtf8("这条记录已在别处被修改，请刷新后重试。");
        return {};
    }

    if (messageType == QLatin1String("SoftDeleteRun")) {
        if (isSoftDeleted(run)) {
            *errorCode = QStringLiteral("ERR_ALREADY_DELETED");
            *errorMessage = QString::fromUtf8("该记录已经是删除状态。");
            return {};
        }
        run.insert(QStringLiteral("soft_deleted"), true);
        QJsonArray changes{changeEntry(QStringLiteral("soft_deleted"), false, true)};
        // RunMutationService.SoftDeleteRun commits with clearPendingReview: a deleted
        // run leaves the review list, and its revision records that as well.
        if (run.value(QStringLiteral("pending_review")).toBool(false)) {
            run.insert(QStringLiteral("pending_review"), false);
            changes.append(changeEntry(QStringLiteral("pending_review"), true, false));
        }
        touchRun(run, QStringLiteral("SOFT_DELETE"), reason, changes);
    } else if (messageType == QLatin1String("RestoreRun")) {
        if (!isSoftDeleted(run)) {
            *errorCode = QStringLiteral("ERR_NOT_DELETED");
            *errorMessage = QString::fromUtf8("该记录并未被删除。");
            return {};
        }
        run.insert(QStringLiteral("soft_deleted"), false);
        touchRun(run, QStringLiteral("RESTORE"), reason,
                 QJsonArray{changeEntry(QStringLiteral("soft_deleted"), true, false)});
    } else if (messageType == QLatin1String("CorrectRun")) {
        const QJsonObject changes = payload.value(QStringLiteral("changes")).toObject();
        // $defs/CorrectRunRequest.changes is additionalProperties: false, and the
        // Collector enforces exactly this list (RunFields.Correctable).
        // pending_review is an acknowledgement and accepts only false.
        static const QSet<QString> correctable{
            QStringLiteral("content_id"),      QStringLiteral("duty_name"),
            QStringLiteral("duty_category"),   QStringLiteral("job_id"),
            QStringLiteral("matched_at_utc"),  QStringLiteral("entered_at_utc"),
            QStringLiteral("ended_at_utc"),    QStringLiteral("duration_ms"),
            QStringLiteral("result"),          QStringLiteral("contributes_to_goal"),
            QStringLiteral("note"),            QStringLiteral("pending_review")};
        const QString reviewKey = QStringLiteral("pending_review");
        const bool explicitAcknowledgement = changes.contains(reviewKey);
        if (explicitAcknowledgement
            && (!changes.value(reviewKey).isBool() || changes.value(reviewKey).toBool())) {
            *errorCode = QStringLiteral("ERR_BAD_REQUEST");
            *errorMessage = QString::fromUtf8("pending_review 只能改为 false（确认复核）。");
            return {};
        }
        const bool acknowledgesReview = changes.contains(QStringLiteral("result"))
                                        || explicitAcknowledgement;
        QJsonArray applied;
        for (auto it = changes.constBegin(); it != changes.constEnd(); ++it) {
            if (!correctable.contains(it.key())) {
                *errorCode = QStringLiteral("ERR_BAD_REQUEST");
                *errorMessage =
                    QString::fromUtf8("请求包含契约未声明的字段 %1。").arg(it.key());
                return {};
            }
            if (it.key() == reviewKey)
                continue; // Record explicit and result-based acknowledgement once below.
            const QJsonValue oldValue = run.value(it.key());
            if (oldValue == it.value())
                continue;
            applied.append(changeEntry(it.key(), oldValue, it.value()));
            run.insert(it.key(), it.value());
        }
        const auto derive = [&](const QString &key, const QJsonValue &value) {
            if (run.value(key) != value) {
                applied.append(changeEntry(key, run.value(key), value));
                run.insert(key, value);
            }
        };
        if (changes.contains(QStringLiteral("job_id"))) {
            const JobCatalog jobs;
            const QVariant jobId = changes.value(QStringLiteral("job_id")).toVariant();
            derive(QStringLiteral("job_name"), jobs.jobName(jobId));
            derive(QStringLiteral("role"), jobs.role(jobId));
            derive(QStringLiteral("role_group"), jobs.roleGroup(jobId));
        }
        if (changes.contains(QStringLiteral("content_id"))) {
            const auto duty = DutyCatalog::shared()->lookup(changes.value(QStringLiteral("content_id")).toVariant());
            // RunMutationService.ApplyChangeSet: the zone moves with the duty; a
            // duty the catalogue does not know keeps the zone that was observed.
            const QVariant territory = duty.value(QStringLiteral("territory_id"));
            if (territory.isValid())
                derive(QStringLiteral("territory_id"), QJsonValue(territory.toLongLong()));
            for (const auto *field : {"duty_name", "duty_category"}) {
                const QString key = QString::fromLatin1(field);
                if (!changes.contains(key))
                    derive(key, QJsonValue::fromVariant(duty.value(key)));
            }
        }
        // RunMutationRules.ValidateFinalValue: a moved end point re-derives the
        // duration unless the request names one, and the corrected record as a
        // whole must be one that can exist (review OJ-6).
        const QJsonObject before = m_runs.at(index).toObject();
        const bool endpointsMoved =
            run.value(QStringLiteral("entered_at_utc")) != before.value(QStringLiteral("entered_at_utc"))
            || run.value(QStringLiteral("ended_at_utc")) != before.value(QStringLiteral("ended_at_utc"));
        if (endpointsMoved && !changes.contains(QStringLiteral("duration_ms"))) {
            const QDateTime enteredAt = fromIso(run.value(QStringLiteral("entered_at_utc")));
            const QDateTime endedAt = fromIso(run.value(QStringLiteral("ended_at_utc")));
            derive(QStringLiteral("duration_ms"),
                   enteredAt.isValid() && endedAt.isValid()
                       ? QJsonValue(double(enteredAt.msecsTo(endedAt)))
                       : QJsonValue(QJsonValue::Null));
        }
        if (!acceptableFinalValue(run, errorCode, errorMessage))
            return {};
        // A note/job/time edit does not decide the outcome. Supplying the same
        // result still acknowledges a pending record and produces an audit row.
        if (acknowledgesReview && run.value(reviewKey).toBool(false)) {
            applied.append(changeEntry(reviewKey, true, false));
            run.insert(reviewKey, false);
        }
        if (applied.isEmpty()) {
            *errorCode = QStringLiteral("ERR_NO_CHANGES");
            *errorMessage = QString::fromUtf8("没有任何字段被修改。");
            return {};
        }
        // RunMutationService.CorrectRun: only a correction that overrules the record marks
        // the run, and once marked it stays marked; the revision records the change.
        if (overrulesTheRecord(before.value(reviewKey).toBool(false), applied)
            && !run.value(QStringLiteral("manually_corrected")).toBool(false)) {
            applied.append(changeEntry(QStringLiteral("manually_corrected"), false, true));
            run.insert(QStringLiteral("manually_corrected"), true);
        }
        touchRun(run, QStringLiteral("CORRECT"), reason, applied);
    } else if (messageType == QLatin1String("UndoRevision")) {
        // Replay the newest revision's old_value as a *new* revision.
        // run_revisions stays append-only; nothing is ever deleted.
        // RunMutationService.UndoRevision: revision 1 is the record's creation.
        const int newest = run.value(QStringLiteral("revision")).toInt();
        if (newest <= 1) {
            *errorCode = QStringLiteral("ERR_UNDO_NOT_ALLOWED");
            *errorMessage =
                QString::fromUtf8("第 1 条修订是创建记录本身，无法撤销；如需移除请使用软删除。");
            return {};
        }
        QJsonObject target;
        for (const QJsonValue &value : std::as_const(m_revisions)) {
            const QJsonObject entry = value.toObject();
            if (entry.value(QStringLiteral("run_id")).toString() == runId
                && entry.value(QStringLiteral("revision")).toInt() == newest) {
                target = entry;
            }
        }
        if (target.isEmpty()) {
            *errorCode = QStringLiteral("ERR_NOT_FOUND");
            *errorMessage = QString::fromUtf8("找不到该记录，请刷新列表后重试。");
            return {};
        }

        QJsonArray applied;
        for (const QJsonValue &value : target.value(QStringLiteral("changes")).toArray()) {
            const QJsonObject change = value.toObject();
            const QString field = change.value(QStringLiteral("field")).toString();
            // manually_corrected is provenance: RunMutationService.Commit keeps it set
            // through an undo, so the undone correction's mark stays.
            if (field.isEmpty() || field == QLatin1String("manually_corrected"))
                continue;
            const QJsonValue restored = change.value(QStringLiteral("old_value"));
            applied.append(changeEntry(field, run.value(field), restored));
            run.insert(field, restored);
        }
        // The SYSTEM revision that closed an unfinished run cannot be taken back into the
        // shape it closed: no statistic would count the run, and no restart closes it again.
        if (target.value(QStringLiteral("actor")).toString() == QLatin1String("SYSTEM")
            && isUnsettledShape(run)) {
            *errorCode = QStringLiteral("ERR_UNDO_NOT_ALLOWED");
            *errorMessage = QString::fromUtf8(
                "这条修订是程序为未完结的记录自动写下的。撤销它会让记录回到无法统计、也无法确认的状态，"
                "因此不能撤销；如果判断有误，请直接更正这条记录。");
            return {};
        }
        // RunMutationRules.ValidateFinalValue: the restored run as a whole must be one
        // that can exist, whatever it was before.
        if (!acceptableFinalValue(run, errorCode, errorMessage))
            return {};
        if (applied.isEmpty()) {
            *errorCode = QStringLiteral("ERR_NO_CHANGES");
            *errorMessage = QString::fromUtf8("上一次修正没有可回放的字段。");
            return {};
        }
        // RunMutationService.UndoRevision commits an ordinary correction: the
        // contract's change_kind has no UNDO (review OJ-6).
        touchRun(run, QStringLiteral("CORRECT"), reason, applied);
    } else {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = QString::fromUtf8("不支持的消息类型：") + messageType;
        return {};
    }

    m_runs.replace(index, run);

    QJsonObject result;
    result.insert(QStringLiteral("run_id"), runId);
    result.insert(QStringLiteral("revision"), run.value(QStringLiteral("revision")));
    result.insert(QStringLiteral("audit_event_id"), ipc::newRequestId());
    result.insert(QStringLiteral("idempotent_replay"), false);
    result.insert(QStringLiteral("run"), run);
    return result;
}

// ---------------------------------------------------------------------------
// 导随心得 · SetRunReflection / GetReflectionSummary
// ---------------------------------------------------------------------------

QJsonObject MockBackend::applyReflection(const QJsonObject &payload, QString *errorCode,
                                         QString *errorMessage)
{
    const QString runId = payload.value(QStringLiteral("run_id")).toString();
    const int index = indexOfRun(runId);
    if (index < 0) {
        *errorCode = QStringLiteral("ERR_NOT_FOUND");
        *errorMessage = QString::fromUtf8("找不到该记录，请刷新列表后重试。");
        return {};
    }

    const QString mood = payload.value(QStringLiteral("mood")).toString();
    if (mood != QLatin1String("good") && mood != QLatin1String("ok")
        && mood != QLatin1String("bad")) {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = QString::fromUtf8("心情取值无效，只接受 good / ok / bad。");
        return {};
    }

    const QString text = payload.value(QStringLiteral("text")).toString().trimmed();
    if (text.size() > 2000) {
        *errorCode = QStringLiteral("ERR_BAD_REQUEST");
        *errorMessage = QString::fromUtf8("笔记最多 2000 字。");
        return {};
    }

    // A soft-deleted run may still receive a reflection (spec 1.4); the run's
    // own revision and updated_at_utc are deliberately left alone.
    QJsonObject run = m_runs.at(index).toObject();
    const QString stamp = isoUtc(QDateTime::currentDateTimeUtc());

    QJsonValue reflection = QJsonValue(QJsonValue::Null);
    if (!text.isEmpty()) {
        const QJsonObject previous = run.value(QStringLiteral("reflection")).toObject();
        QJsonObject entry;
        entry.insert(QStringLiteral("mood"), mood);
        entry.insert(QStringLiteral("text"), text);
        entry.insert(QStringLiteral("created_at_utc"),
                     previous.value(QStringLiteral("created_at_utc")).toString(stamp));
        entry.insert(QStringLiteral("updated_at_utc"), stamp);
        reflection = entry;
    }
    run.insert(QStringLiteral("reflection"), reflection);
    m_runs.replace(index, run);

    QJsonObject result;
    result.insert(QStringLiteral("run_id"), runId);
    result.insert(QStringLiteral("reflection"), reflection);
    result.insert(QStringLiteral("run"), run);
    return result;
}

QJsonObject MockBackend::reflectionSummary(int recentLimit) const
{
    const int limit = qBound(0, recentLimit, 20);

    QList<QJsonObject> withReflection;
    QList<QJsonObject> pending;
    for (const QJsonValue &value : m_runs) {
        const QJsonObject run = value.toObject();
        if (isSoftDeleted(run))
            continue;
        const bool completed =
            run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED");
        if (run.value(QStringLiteral("reflection")).isObject())
            withReflection.append(run);
        else if (completed)
            pending.append(run);
    }

    // recent: newest reflection first; next_pending: newest matched run first.
    const auto reflectionStamp = [](const QJsonObject &run) {
        return run.value(QStringLiteral("reflection"))
            .toObject()
            .value(QStringLiteral("updated_at_utc"))
            .toString();
    };
    std::stable_sort(withReflection.begin(), withReflection.end(),
                     [&](const QJsonObject &a, const QJsonObject &b) {
                         return reflectionStamp(a) > reflectionStamp(b);
                     });
    std::stable_sort(pending.begin(), pending.end(),
                     [](const QJsonObject &a, const QJsonObject &b) {
                         return a.value(QStringLiteral("matched_at_utc")).toString()
                                > b.value(QStringLiteral("matched_at_utc")).toString();
                     });

    QJsonArray recent;
    for (int i = 0; i < qMin(limit, int(withReflection.size())); ++i) {
        QJsonObject entry;
        entry.insert(QStringLiteral("run"), withReflection.at(i));
        entry.insert(QStringLiteral("reflection"),
                     withReflection.at(i).value(QStringLiteral("reflection")));
        recent.append(entry);
    }

    QJsonObject result;
    result.insert(QStringLiteral("reflection_count"), int(withReflection.size()));
    result.insert(QStringLiteral("pending_completed_count"), int(pending.size()));
    result.insert(QStringLiteral("recent"), recent);
    result.insert(QStringLiteral("next_pending"),
                  pending.isEmpty() ? QJsonValue(QJsonValue::Null)
                                    : QJsonValue(pending.first()));
    return result;
}

// ---------------------------------------------------------------------------
// Dispatch
// ---------------------------------------------------------------------------

BackendReply *MockBackend::request(const QString &messageType, const QJsonObject &payload)
{
    auto *reply = new BackendReply(ipc::newRequestId(), messageType, this);

    QString errorCode;
    QString errorMessage;
    QJsonObject errorDetails;
    QJsonObject result;
    bool handled = true;

    if (messageType == QLatin1String("GetVersion")) {
        result.insert(QStringLiteral("collector_version"), QStringLiteral("0.2.2-mock"));
        result.insert(QStringLiteral("protocol_version"), ipc::kProtocolVersion);
        result.insert(QStringLiteral("build_id"), QStringLiteral("phase1-mock"));
    } else if (messageType == QLatin1String("GetStatus")) {
        result = collectorStatus();
    } else if (messageType == QLatin1String("GetCaptureStatus")) {
        result = captureStatus();
    } else if (messageType == QLatin1String("GetCaptureValidationStatus")) {
        result = captureValidationStatus();
    } else if (messageType == QLatin1String("GetProtocolProfileStatus")) {
        const bool candidate = captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool();
        result.insert(QStringLiteral("status"), candidate
                          ? QStringLiteral("UNSUPPORTED_BUILD") : QStringLiteral("UNVERIFIED"));
        result.insert(QStringLiteral("status_label"), QStringLiteral("SYNTHETIC_ONLY"));
        result.insert(QStringLiteral("profile_id"), candidate
                          ? QJsonValue(QJsonValue::Null) : QJsonValue(QStringLiteral("cn/2026.08.12")));
        result.insert(QStringLiteral("region"), QStringLiteral("CN"));
        result.insert(QStringLiteral("game_build"), candidate
                          ? QStringLiteral("2026.08.05.0000.0000")
                          : QStringLiteral("2026.08.12.0000.0000"));
        result.insert(QStringLiteral("verified_at_utc"), candidate
                          ? QJsonValue(QJsonValue::Null) : QJsonValue(isoUtc(m_now.addDays(-23))));
        result.insert(QStringLiteral("evidence_note"),
                      QString::fromUtf8("模拟数据：仅离线合成事件，"
                                        "从未针对真实流量校验。"));
        result.insert(QStringLiteral("message"), QJsonValue(QJsonValue::Null));
    } else if (messageType == QLatin1String("GetCaptureSettings")) {
        result = captureSettings();
    } else if (messageType == QLatin1String("UpdateCaptureSettings")) {
        result = applyCaptureSettings(payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("ListCaptureAdapters")) {
        QJsonObject adapter;
        adapter.insert(QStringLiteral("adapter_id"), QStringLiteral("Ethernet"));
        adapter.insert(QStringLiteral("description"), QStringLiteral("Intel(R) Ethernet"));
        adapter.insert(QStringLiteral("friendly_name"), QStringLiteral("Ethernet"));
        adapter.insert(QStringLiteral("ipv4_addresses"), QJsonArray{QStringLiteral("192.168.1.23")});
        adapter.insert(QStringLiteral("is_loopback"), false);
        adapter.insert(QStringLiteral("is_up"), true);
        adapter.insert(QStringLiteral("recommended"), true);
        result.insert(QStringLiteral("npcap_installed"), !m_npcapMissing);
        result.insert(QStringLiteral("npcap_version"),
                      m_npcapMissing ? QJsonValue(QJsonValue::Null)
                                     : QJsonValue(QStringLiteral("1.79")));
        result.insert(QStringLiteral("install_hint"),
                      m_npcapMissing
                          ? QJsonValue(QString::fromUtf8(
                                "本软件不附带 Npcap，请从 npcap.com 自行安装。"))
                          : QJsonValue(QJsonValue::Null));
        result.insert(QStringLiteral("adapters"),
                      m_npcapMissing ? QJsonArray() : QJsonArray{adapter});
    } else if (messageType == QLatin1String("SelectGameProcess")) {
        bool found = false;
        for (const auto &value : captureStatus().value(QStringLiteral("game_processes")).toArray()) {
            const auto choice = value.toObject();
            if (choice.value(QStringLiteral("process_id")) == payload.value(QStringLiteral("process_id"))
                && choice.value(QStringLiteral("selection_token")) == payload.value(QStringLiteral("selection_token")))
                found = true;
        }
        if (!found) {
            errorCode = QStringLiteral("ERR_FFXIV_NOT_RUNNING");
            errorMessage = QString::fromUtf8("所选游戏已退出，请重新选择。");
        } else {
            m_selectedGameProcessId = payload.value(QStringLiteral("process_id")).toInt();
            result = captureStatus();
        }
    } else if (messageType == QLatin1String("StartCapture")) {
        if (m_npcapMissing) {
            errorCode = QStringLiteral("ERR_NPCAP_MISSING");
            errorMessage = QString::fromUtf8("未检测到 Npcap，无法开始捕获。");
        } else if (m_capturing) {
            errorCode = QStringLiteral("ERR_CAPTURE_ALREADY_RUNNING");
            errorMessage = QString::fromUtf8("捕获已在运行。");
        } else {
            m_capturing = true;
            result = captureStatus();
            const bool candidate = captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool();
            QTimer::singleShot(0, this, [this, candidate] {
                if (candidate)
                    emitCaptureStatusChanged();
                else
                    emitStateChanged(currentRun().value(QStringLiteral("state")).toString());
            });
        }
    } else if (messageType == QLatin1String("StopCapture")) {
        if (!m_capturing) {
            errorCode = QStringLiteral("ERR_CAPTURE_NOT_RUNNING");
            errorMessage = QString::fromUtf8("捕获并未运行。");
        } else {
            m_capturing = false;
            result = captureStatus();
            const bool candidate = captureSettings().value(QStringLiteral("candidate_validation_enabled")).toBool();
            QTimer::singleShot(0, this, [this, candidate] {
                if (candidate) {
                    emitCaptureStatusChanged();
                } else {
                    // Both halves, exactly as the bus publishes them: the
                    // terminal announcement hangs off RunFinished.
                    emitStateChanged(QStringLiteral("INTERRUPTED_PENDING_REVIEW"));
                    emitRunFinished(QStringLiteral("INTERRUPTED_PENDING_REVIEW"));
                }
            });
        }
    } else if (messageType == QLatin1String("StartCaptureValidation")) {
        if (m_npcapMissing) {
            errorCode = QStringLiteral("ERR_NPCAP_MISSING");
            errorMessage = QString::fromUtf8("未检测到 Npcap，无法开始验证。");
        } else if (m_capturing) {
            errorCode = QStringLiteral("ERR_BAD_REQUEST");
            errorMessage = QString::fromUtf8("正式捕获运行中，不能同时开始验证。");
        } else {
            m_validationState = QStringLiteral("WAITING");
            result = captureValidationStatus();
        }
    } else if (messageType == QLatin1String("AddCaptureValidationMarker")) {
        if (m_validationState != QLatin1String("RECORDING")) {
            errorCode = QStringLiteral("ERR_BAD_REQUEST");
            errorMessage = QString::fromUtf8("只能在 RECORDING 状态添加标记。");
        } else {
            m_lastValidationMarker = payload.value(QStringLiteral("marker")).toString();
            ++m_validationMarkerCount;
            result = captureValidationStatus();
        }
    } else if (messageType == QLatin1String("StopCaptureValidation")) {
        if (m_validationState == QLatin1String("WAITING"))
            m_validationState = QStringLiteral("IDLE");
        else if (m_validationState == QLatin1String("RECORDING"))
            m_validationState = QStringLiteral("COMPLETED");
        result = captureValidationStatus();
    } else if (messageType == QLatin1String("ConfirmCalibration")) {
        result = applyCalibrationVerdicts(payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("DiscardCalibration")) {
        ++m_discardCalibrationCount;
        m_lastDiscardCalibration = payload;
        if (payload.value(QStringLiteral("restore_local_profile")).toBool()) {
            // The rollback: the retired file goes back and records again, so the fixture
            // returns to "a local profile is in force and nothing is being calibrated".
            if (!m_retiredLocalProfileAvailable) {
                errorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
                errorMessage = QString::fromUtf8("没有可以恢复的本机校准。");
            } else {
                m_retiredLocalProfileAvailable = false;
                m_calibrationState = QStringLiteral("idle");
                result.insert(QStringLiteral("state"), QStringLiteral("IDLE"));
            }
        } else if (m_calibrationState.isEmpty()) {
            errorCode = QStringLiteral("ERR_CALIBRATION_NOT_READY");
            errorMessage = QString::fromUtf8("当前没有正在进行的本机校准。");
        } else {
            m_calibrationState = QStringLiteral("observing");
            // 重新观察 also forgets the player's refusal of shared calibration.
            if (m_sharedState == QLatin1String("user-rejected")
                || m_sharedState == QLatin1String("rejected"))
                m_sharedState = QStringLiteral("none");
            result.insert(QStringLiteral("state"), QStringLiteral("OBSERVING"));
        }
    } else if (isSharedCalibrationMessage(messageType)) {
        result = applySharedCalibration(messageType, payload, &errorCode, &errorMessage,
                                        &errorDetails);
    } else if (isSpeechMessage(messageType)) {
        result = applySpeech(messageType, payload, &errorCode, &errorMessage, &errorDetails);
    } else if (messageType == QLatin1String("GetCurrentRun")) {
        result = currentRun();
    } else if (messageType == QLatin1String("SubscribeLiveEvents")) {
        result.insert(QStringLiteral("subscription_id"), ipc::newRequestId());
        result.insert(QStringLiteral("heartbeat_interval_ms"), 5000);
    } else if (messageType == QLatin1String("QueryRuns")) {
        result = queryRunsPayload(payload);
    } else if (messageType == QLatin1String("QueryCandidateObservations")) {
        result = queryCandidatePayload(payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("ReviewCandidateObservation")) {
        result = reviewCandidatePayload(payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("ExportCandidateEvidence")) {
        result = exportCandidatePayload(payload);
    } else if (messageType == QLatin1String("GetRunRevisions")) {
        // MessageDispatcher.GetRunRevisions + RunRevisionRepository.ListForRun:
        // oldest first, page / page_size defaulting to 1 / 50 and refused out
        // of range, the page asked for even when it is past the end, and an
        // unknown run is ERR_NOT_FOUND (review DT1-X1).
        const QString runId = payload.value(QStringLiteral("run_id")).toString();
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        const int pageSize = payload.value(QStringLiteral("page_size")).toInt(50);
        if (indexOfRun(runId) < 0) {
            errorCode = QStringLiteral("ERR_NOT_FOUND");
            errorMessage = QString::fromUtf8("找不到该记录，请刷新列表后重试。");
        } else if (page < 1 || pageSize < 1 || pageSize > 200) {
            errorCode = QStringLiteral("ERR_BAD_REQUEST");
            errorMessage = QString::fromUtf8("page 必须大于等于 1，page_size 必须在 1 到 200 之间。");
        } else {
            QList<QJsonObject> chain;
            for (const QJsonValue &value : std::as_const(m_revisions)) {
                if (value.toObject().value(QStringLiteral("run_id")).toString() == runId)
                    chain.append(value.toObject());
            }
            std::stable_sort(chain.begin(), chain.end(), [](const QJsonObject &a, const QJsonObject &b) {
                return a.value(QStringLiteral("revision")).toInt()
                       < b.value(QStringLiteral("revision")).toInt();
            });
            QJsonArray items;
            const qint64 first = qint64(page - 1) * pageSize;
            for (qint64 i = first; i < qMin(qint64(chain.size()), first + pageSize); ++i)
                items.append(chain.at(int(i)));
            QJsonObject pageInfo;
            pageInfo.insert(QStringLiteral("page"), page);
            pageInfo.insert(QStringLiteral("page_size"), pageSize);
            pageInfo.insert(QStringLiteral("total"), int(chain.size()));
            result.insert(QStringLiteral("items"), items);
            result.insert(QStringLiteral("page_info"), pageInfo);
        }
    } else if (messageType == QLatin1String("GetRunEvents")) {
        const QString runId = payload.value(QStringLiteral("run_id")).toString();
        if (indexOfRun(runId) < 0) {
            errorCode = QStringLiteral("ERR_NOT_FOUND");
            errorMessage = QString::fromUtf8("找不到该记录，请刷新列表后重试。");
        } else {
            result.insert(QStringLiteral("run_id"), runId);
            result.insert(QStringLiteral("events"), runEvents(runId));
        }
    } else if (messageType == QLatin1String("GetDashboardStats")) {
        result = dashboardStats(
            payload.value(QStringLiteral("filter")).toObject(),
            payload.value(QStringLiteral("trend_granularity")).toString());
    } else if (messageType == QLatin1String("GetResultStats")) {
        result = resultStats(payload.value(QStringLiteral("filter")).toObject());
    } else if (messageType == QLatin1String("GetDungeonStats")
               || messageType == QLatin1String("GetJobStats")) {
        const QJsonObject filter = payload.value(QStringLiteral("filter")).toObject();
        const QJsonArray allItems = messageType == QLatin1String("GetDungeonStats")
                                     ? dungeonStats(filter)
                                     : jobStats(filter);
        const int page = qMax(1, payload.value(QStringLiteral("page")).toInt(1));
        const int pageSize = qBound(1, payload.value(QStringLiteral("page_size")).toInt(50), 200);
        QJsonArray items;
        for (qint64 i = qint64(page - 1) * pageSize;
             i < qMin(qint64(allItems.size()), qint64(page) * pageSize); ++i)
            items.append(allItems.at(i));
        QJsonObject pageInfo;
        pageInfo.insert(QStringLiteral("page"), page);
        pageInfo.insert(QStringLiteral("page_size"), pageSize);
        pageInfo.insert(QStringLiteral("total"), int(allItems.size()));
        result.insert(QStringLiteral("items"), items);
        result.insert(QStringLiteral("page_info"), pageInfo);
        if (messageType == QLatin1String("GetDungeonStats")) {
            // How many distinct duties match the filter, not how many rows this
            // page carries.
            result.insert(QStringLiteral("distinct_count"), int(allItems.size()));
        }
    } else if (messageType == QLatin1String("UndoRevision")
               || messageType == QLatin1String("CreateManualRun")
               || messageType == QLatin1String("CorrectRun")
               || messageType == QLatin1String("SoftDeleteRun")
               || messageType == QLatin1String("RestoreRun")
               || messageType == QLatin1String("UpdateAchievementBaseline")) {
        result = applyMutation(messageType, payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("SetRunReflection")) {
        result = applyReflection(payload, &errorCode, &errorMessage);
    } else if (messageType == QLatin1String("GetReflectionSummary")) {
        const QJsonValue limit = payload.value(QStringLiteral("recent_limit"));
        result = reflectionSummary(limit.isDouble() ? limit.toInt() : 3);
    } else if (messageType == QLatin1String("ExportCsv")
               || messageType == QLatin1String("ExportJson")) {
        result.insert(QStringLiteral("target_path"),
                      payload.value(QStringLiteral("target_path")));
        result.insert(QStringLiteral("row_count"), int(m_runs.size()));
        result.insert(QStringLiteral("byte_count"), int(m_runs.size()) * 240);
        result.insert(QStringLiteral("completed_at_utc"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
    } else if (messageType == QLatin1String("BackupDatabase")) {
        result.insert(QStringLiteral("target_path"),
                      payload.value(QStringLiteral("target_path")));
        result.insert(QStringLiteral("byte_count"), 2411520);
        result.insert(QStringLiteral("completed_at_utc"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
        result.insert(QStringLiteral("integrity_check_passed"), true);
    } else if (messageType == QLatin1String("CheckDatabaseIntegrity")) {
        result.insert(QStringLiteral("passed"), true);
        result.insert(QStringLiteral("detail"), QStringLiteral("ok"));
        result.insert(QStringLiteral("checked_at_utc"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
    } else if (messageType == QLatin1String("CheckUpdateNow")) {
        // 检查更新: this backend never fetches anything, so the answer is the
        // very `update` object GetStatus already carries - the verdict
        // --mock-update-available fed it - and the outcome the 检查新版本并提示
        // setting implies. There is no kill switch to simulate offline, so
        // BLOCKED never comes from here.
        const QJsonObject update =
            collectorStatus().value(QStringLiteral("update")).toObject();
        result.insert(QStringLiteral("outcome"),
                      update.value(QStringLiteral("enabled")).toBool()
                          ? QStringLiteral("CHECKED") : QStringLiteral("DISABLED"));
        result.insert(QStringLiteral("update"), update);
    } else if (messageType == QLatin1String("ExportDiagnosticsReport")) {
        // The mock writes nothing: it has no capture pipeline to describe and
        // must never produce a file of invented counters. It answers the wire
        // shape and says so.
        result.insert(QStringLiteral("target_path"),
                      payload.value(QStringLiteral("target_path"))
                          .toString(QString::fromUtf8("（模拟后端未写入任何文件）")));
        result.insert(QStringLiteral("byte_count"), 0);
        result.insert(QStringLiteral("completed_at_utc"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
    } else {
        handled = false;
    }

    if (!handled) {
        errorCode = QStringLiteral("ERR_BAD_REQUEST");
        errorMessage = QString::fromUtf8("模拟后端不支持该消息：") + messageType;
    }

    // Deliver on the next event-loop turn so callers always observe the same
    // asynchronous behaviour as the real IPC backend - or later, when a test
    // asked for this type's answer to be held back (setReplyDelay).
    const bool failed = !errorCode.isEmpty();
    const int delay = m_replyDelays.value(messageType, m_replyDelays.value(QString(), 0));
    QTimer::singleShot(delay, reply, [reply, failed, result, errorCode, errorMessage, errorDetails] {
        if (failed)
            reply->fail(errorCode, errorMessage, errorDetails);
        else
            reply->succeed(result);
    });
    return reply;
}

} // namespace mr
