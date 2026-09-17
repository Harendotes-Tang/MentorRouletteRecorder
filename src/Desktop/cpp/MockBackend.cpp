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

} // namespace

namespace mr {

MockBackend::MockBackend(QObject *parent)
    : IBackend(parent), m_now(QDate(2026, 9, 4), QTime(21, 40, 12), QTimeZone::LocalTime)
{
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
QVariantMap MockBackend::liveEventEnvelope(const QString &eventType, const QString &kind)
{
    QVariantMap event;
    event.insert(QStringLiteral("event_id"), ipc::newRequestId());
    event.insert(QStringLiteral("event_type"), eventType);
    event.insert(QStringLiteral("kind"), kind);
    event.insert(QStringLiteral("emitted_at_utc"), isoUtc(m_now));
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
            if (draft.result != QLatin1String("INTERRUPTED")
                && draft.result != QLatin1String("UNKNOWN")) {
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
    manualRevision.insert(QStringLiteral("changes"), QJsonArray());
    m_revisions.append(manualRevision);

    // Two runs the crash-recovery service closed without evidence. They keep
    // their captured result and carry pending_review, exactly as
    // CrashRecoveryService writes them, so the dashboard banner, the history
    // marker and the 确认 action have something real-shaped to render.
    for (int index : {62, 63}) {
        QJsonObject review = m_runs.at(index).toObject();
        review.insert(QStringLiteral("pending_review"), true);
        review.insert(QStringLiteral("detection_confidence"), QStringLiteral("LOW"));
        review.insert(QStringLiteral("note"),
                      QString::fromUtf8("进程异常退出后由崩溃恢复关闭，结果待人工确认"));
        m_runs.replace(index, review);
    }

    // A soft-deleted duplicate: listed only on request, never counted.
    QJsonObject deleted = m_runs.at(55).toObject();
    deleted.insert(QStringLiteral("soft_deleted"), true);
    deleted.insert(QStringLiteral("note"), QString::fromUtf8("重复记录（replay 测试产生）"));
    m_runs.replace(55, deleted);

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
    status.insert(QStringLiteral("last_valid_event_at_utc"),
                  running && !candidateEnabled ? QJsonValue(isoUtc(m_now.addSecs(-128)))
                                               : QJsonValue(QJsonValue::Null));
    status.insert(QStringLiteral("last_valid_event_kind"),
                  running && !candidateEnabled ? QJsonValue(QStringLiteral("DUTY_RESULT"))
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
        status.insert(QStringLiteral("ffxiv_running"), m_recordingFixture != QLatin1String("waiting"));
        status.insert(QStringLiteral("profile_status"), m_recordingFixture == QLatin1String("listening")
                      ? QStringLiteral("VERIFIED") : QStringLiteral("NONE"));
        if (m_recordingFixture == QLatin1String("checking")) status.remove(QStringLiteral("profile_status"));
        if (m_recordingFixture == QLatin1String("waiting")) status.insert(QStringLiteral("state"), QStringLiteral("STOPPED"));
        // Keep SYNTHETIC_ONLY provenance even when exercising the VERIFIED branch.
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

    struct Step {
        const char *kind;
        const char *field;
        const char *opcode;
    };
    static const Step kSteps[] = {
        {"MENTOR_MATCH", "matched_at_utc", "0x0142"},
        {"DUTY_ENTER", "entered_at_utc", "0x01A3"},
        {"DUTY_RESULT", "ended_at_utc", "0x0271"},
    };

    QJsonArray events;
    int ordinal = 0;
    for (const Step &step : kSteps) {
        const QJsonValue at = run.value(QLatin1String(step.field));
        if (!at.isString())
            continue;
        ++ordinal;

        QJsonObject parsed;
        if (QLatin1String(step.kind) == QLatin1String("MENTOR_MATCH")) {
            parsed.insert(QStringLiteral("roulette_id"),
                          run.value(QStringLiteral("mentor_roulette_id")));
        } else if (QLatin1String(step.kind) == QLatin1String("DUTY_ENTER")) {
            parsed.insert(QStringLiteral("content_id"),
                          run.value(QStringLiteral("content_id")));
            parsed.insert(QStringLiteral("territory_id"),
                          run.value(QStringLiteral("territory_id")));
        } else {
            parsed.insert(QStringLiteral("result"), run.value(QStringLiteral("result")));
        }

        QJsonObject entry;
        entry.insert(QStringLiteral("event_id"),
                     mockUuid(QStringLiteral("event-%1-%2").arg(index).arg(ordinal)));
        entry.insert(QStringLiteral("event_type"), QString::fromLatin1(step.kind));
        entry.insert(QStringLiteral("observed_at_utc"), at);
        entry.insert(QStringLiteral("direction"), QStringLiteral("S2C"));
        entry.insert(QStringLiteral("opcode"), QString::fromLatin1(step.opcode));
        // A 12-hex-digit prefix of a payload hash, exactly like the trace sink
        // writes: never the payload itself.
        entry.insert(QStringLiteral("payload_hash"),
                     mockUuid(QStringLiteral("hash-%1-%2").arg(index).arg(ordinal))
                         .remove(QLatin1Char('-'))
                         .left(12));
        entry.insert(QStringLiteral("parser_status"), QStringLiteral("OK"));
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
        m_goalCount = qMax(1, payload.value(QStringLiteral("goal_count")).toInt(m_goalCount));
        m_baselineCompletedCount =
            qMax(0, payload.value(QStringLiteral("baseline_completed_count"))
                        .toInt(m_baselineCompletedCount));
        QJsonObject result;
        result.insert(QStringLiteral("goal_count"), m_goalCount);
        result.insert(QStringLiteral("baseline_completed_count"), m_baselineCompletedCount);
        result.insert(QStringLiteral("baseline_effective_at"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
        result.insert(QStringLiteral("updated_at_utc"),
                      isoUtc(QDateTime::currentDateTimeUtc()));
        result.insert(QStringLiteral("audit_event_id"), ipc::newRequestId());
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
        revision.insert(QStringLiteral("changes"), QJsonArray());
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
        *errorMessage = QString::fromUtf8("该记录已被修改，请刷新后重试。");
        return {};
    }

    if (messageType == QLatin1String("SoftDeleteRun")) {
        if (isSoftDeleted(run)) {
            *errorCode = QStringLiteral("ERR_ALREADY_DELETED");
            *errorMessage = QString::fromUtf8("该记录已经是删除状态。");
            return {};
        }
        run.insert(QStringLiteral("soft_deleted"), true);
        touchRun(run, QStringLiteral("SOFT_DELETE"), reason,
                 QJsonArray{changeEntry(QStringLiteral("soft_deleted"), false, true)});
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
            for (const auto *field : {"duty_name", "duty_category"}) {
                const QString key = QString::fromLatin1(field);
                if (!changes.contains(key))
                    derive(key, QJsonValue::fromVariant(duty.value(key)));
            }
        }
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
        run.insert(QStringLiteral("manually_corrected"), true);
        touchRun(run, QStringLiteral("CORRECT"), reason, applied);
    } else if (messageType == QLatin1String("UndoRevision")) {
        // Replay the newest revision's old_value as a *new* revision.
        // run_revisions stays append-only; nothing is ever deleted.
        int newest = 0;
        QJsonObject target;
        for (const QJsonValue &value : std::as_const(m_revisions)) {
            const QJsonObject entry = value.toObject();
            if (entry.value(QStringLiteral("run_id")).toString() != runId)
                continue;
            const int revision = entry.value(QStringLiteral("revision")).toInt();
            if (revision > newest) {
                newest = revision;
                target = entry;
            }
        }
        if (newest <= 1 || target.isEmpty()) {
            *errorCode = QStringLiteral("ERR_UNDO_NOT_ALLOWED");
            *errorMessage = QString::fromUtf8("首个修订不可撤销。");
            return {};
        }

        QJsonArray applied;
        for (const QJsonValue &value : target.value(QStringLiteral("changes")).toArray()) {
            const QJsonObject change = value.toObject();
            const QString field = change.value(QStringLiteral("field")).toString();
            if (field.isEmpty())
                continue;
            const QJsonValue restored = change.value(QStringLiteral("old_value"));
            applied.append(changeEntry(field, run.value(field), restored));
            run.insert(field, restored);
        }
        if (applied.isEmpty()) {
            *errorCode = QStringLiteral("ERR_NO_CHANGES");
            *errorMessage = QString::fromUtf8("上一次修正没有可回放的字段。");
            return {};
        }
        touchRun(run, QStringLiteral("UNDO"), reason, applied);
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
        if (m_calibrationState.isEmpty()) {
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
        const QString runId = payload.value(QStringLiteral("run_id")).toString();
        QJsonArray items;
        for (const QJsonValue &value : std::as_const(m_revisions)) {
            if (value.toObject().value(QStringLiteral("run_id")).toString() == runId)
                items.append(value);
        }
        QJsonObject pageInfo;
        pageInfo.insert(QStringLiteral("page"), 1);
        pageInfo.insert(QStringLiteral("page_size"), qMax(1, int(items.size())));
        pageInfo.insert(QStringLiteral("total"), int(items.size()));
        result.insert(QStringLiteral("items"), items);
        result.insert(QStringLiteral("page_info"), pageInfo);
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
        const QJsonArray items = messageType == QLatin1String("GetDungeonStats")
                                     ? dungeonStats(filter)
                                     : jobStats(filter);
        QJsonObject pageInfo;
        pageInfo.insert(QStringLiteral("page"), 1);
        pageInfo.insert(QStringLiteral("page_size"), qMax(1, int(items.size())));
        pageInfo.insert(QStringLiteral("total"), int(items.size()));
        result.insert(QStringLiteral("items"), items);
        result.insert(QStringLiteral("page_info"), pageInfo);
        if (messageType == QLatin1String("GetDungeonStats")) {
            // How many distinct duties match the filter, not how many rows this
            // page carries.
            result.insert(QStringLiteral("distinct_count"), int(items.size()));
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
    // asynchronous behaviour as the real IPC backend.
    const bool failed = !errorCode.isEmpty();
    QTimer::singleShot(0, reply, [reply, failed, result, errorCode, errorMessage, errorDetails] {
        if (failed)
            reply->fail(errorCode, errorMessage, errorDetails);
        else
            reply->succeed(result);
    });
    return reply;
}

} // namespace mr
