#include "CalibrationController.h"

#include "IBackend.h"

#include <QDateTime>
#include <QJsonArray>
#include <QJsonObject>
#include <QTimeZone>

#include <algorithm>

namespace mr {
namespace {

QDateTime parseUtc(const QVariant &value)
{
    QDateTime stamp = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
    if (!stamp.isValid())
        stamp = QDateTime::fromString(value.toString(), Qt::ISODate);
    if (stamp.isValid() && stamp.timeSpec() == Qt::LocalTime)
        stamp.setTimeZone(QTimeZone::UTC);
    return stamp;
}

} // namespace

CalibrationController::CalibrationController(QObject *parent)
    : QObject(parent)
    , m_shared(new SharedCalibrationController(this))
{
}

void CalibrationController::setBackend(IBackend *backend)
{
    m_backend = backend;
    m_shared->setBackend(backend);
}

QString CalibrationController::rejectedMessage()
{
    return tr("有事件被标为不对，这次校准作废；再打一把随机任务后会重新核对。");
}

QString CalibrationController::queueInferenceText()
{
    return tr("按「你申请了哪个随机任务」加「你进了哪个副本」来记录");
}

void CalibrationController::refreshFromCaptureStatus(const QVariantMap &capture)
{
    // Shared calibration reads the same snapshot, including the profile origin that
    // lives beside the calibration object rather than inside it.
    m_shared->refreshFromCaptureStatus(capture);
    const QVariant raw = capture.value(QStringLiteral("calibration"));
    if (!raw.isValid() || raw.isNull()) {
        publish(QStringLiteral("IDLE"), QString(), {}, {}, {}, false, false);
        return;
    }
    const QVariantMap calibration = raw.toMap();

    QStringList blockers;
    const auto blockerValues = calibration.value(QStringLiteral("blockers")).toList();
    for (const auto &value : blockerValues) {
        const QString sentence = value.toString();
        if (!sentence.isEmpty())
            blockers.append(sentence);
    }

    QVariantList events;
    const auto eventValues = calibration.value(QStringLiteral("events")).toList();
    for (const auto &value : eventValues) {
        QVariantMap event = value.toMap();
        if (event.value(QStringLiteral("event_id")).toString().isEmpty())
            continue;
        // Local wall-clock, because that is the clock the user watched while
        // playing. The raw stamp stays on the map for anything that needs it.
        const QDateTime at = parseUtc(event.value(QStringLiteral("at_utc")));
        event.insert(QStringLiteral("time_text"),
                     at.isValid() ? at.toLocalTime().toString(QStringLiteral("HH:mm")) : QString());
        events.append(event);
    }
    std::stable_sort(events.begin(), events.end(), [](const QVariant &left, const QVariant &right) {
        // Wall clock, not session time: evidence may span a re-login, and session clocks
        // restart at zero.
        const QString leftAt = left.toMap().value(QStringLiteral("at_utc")).toString();
        const QString rightAt = right.toMap().value(QStringLiteral("at_utc")).toString();
        if (leftAt != rightAt)
            return leftAt < rightAt;
        return left.toMap().value(QStringLiteral("t_ms")).toLongLong()
             < right.toMap().value(QStringLiteral("t_ms")).toLongLong();
    });

    const QVariant progress = calibration.value(QStringLiteral("progress"));
    const QString state = calibration.value(QStringLiteral("state")).toString();
    // A local profile already written while observation continues is the provisional
    // case; the contract needs no new field to say it, because those two facts cannot
    // both be true of a calibration that finished.
    // WAITING is the same calibration with no capture session under it (the game is closed).
    const bool provisional = (state == QLatin1String("OBSERVING") || state == QLatin1String("WAITING"))
        && !calibration.value(QStringLiteral("local_profile_id")).toString().isEmpty();
    publish(state, calibration.value(QStringLiteral("game_build")).toString(), blockers,
            progress.isValid() && !progress.isNull() ? progress.toMap() : QVariantMap(), events,
            provisional,
            // Optional: a Collector that does not report it offers no rollback, which is the
            // same thing a Collector reporting false says.
            calibration.value(QStringLiteral("retired_local_profile_available")).toBool());
}

void CalibrationController::publish(const QString &state, const QString &gameBuild,
                                    const QStringList &blockers, const QVariantMap &progress,
                                    const QVariantList &events, bool provisional,
                                    bool retiredLocalProfileAvailable)
{
    int required = 0;
    for (const auto &value : events) {
        if (value.toMap().value(QStringLiteral("requires_confirmation")).toBool())
            ++required;
    }
    const QString nextState = state.isEmpty() ? QStringLiteral("IDLE") : state;
    if (m_state == nextState && m_gameBuild == gameBuild && m_blockers == blockers
        && m_progress == progress && m_events == events && m_confirmCount == required
        && m_provisional == provisional
        && m_retiredLocalProfileAvailable == retiredLocalProfileAvailable) {
        return;
    }
    m_state = nextState;
    m_gameBuild = gameBuild;
    m_provisional = provisional;
    m_retiredLocalProfileAvailable = retiredLocalProfileAvailable;
    m_blockers = blockers;
    m_progress = progress;
    m_events = events;
    m_confirmCount = required;
    Q_EMIT changed();
}

void CalibrationController::confirm(const QVariantMap &verdicts)
{
    if (m_busy || !m_backend)
        return;
    QJsonArray payload;
    for (const auto &value : std::as_const(m_events)) {
        const QVariantMap event = value.toMap();
        if (!event.value(QStringLiteral("requires_confirmation")).toBool())
            continue;
        const QString id = event.value(QStringLiteral("event_id")).toString();
        // Either shape: a bare verdict, or a map carrying the name the player corrected.
        // Nothing but a relabel has anything to add, so the short form stays legal.
        const QVariant raw = verdicts.value(id);
        const QVariantMap answer = raw.canConvert<QVariantMap>() ? raw.toMap() : QVariantMap();
        const QString verdict = answer.isEmpty() ? raw.toString()
                                                 : answer.value(QStringLiteral("verdict")).toString();
        const QString rouletteName = answer.value(QStringLiteral("roulette_name")).toString();
        if (verdict != QLatin1String("CORRECT") && verdict != QLatin1String("WRONG")
            && !(verdict == QLatin1String("RELABEL") && !rouletteName.isEmpty())) {
            // A missing verdict is not a silent CORRECT. The dialog keeps its
            // button disabled until every row is answered; this is the guard
            // for anything that calls the slot directly.
            m_error = tr("还有事件没有核对，请把每一条都点“对”或“错”。");
            Q_EMIT changed();
            return;
        }
        QJsonObject entry{{QStringLiteral("event_id"), id}, {QStringLiteral("verdict"), verdict}};
        if (verdict == QLatin1String("RELABEL"))
            entry.insert(QStringLiteral("roulette_name"), rouletteName);
        payload.append(entry);
    }
    if (payload.isEmpty())
        return;

    m_busy = true;
    m_error.clear();
    Q_EMIT changed();
    m_backend->confirmCalibration(payload)->whenDone(this,
        [this](bool ok, const QVariantMap &result, const QString &code, const QString &message) {
        m_busy = false;
        if (ok) {
            m_lastResult = result;
            m_error.clear();
            Q_EMIT changed();
            Q_EMIT confirmed(result.value(QStringLiteral("profile_id")).toString(),
                             result.value(QStringLiteral("bound_in_session")).toBool());
            return;
        }
        if (code == QLatin1String("ERR_CALIBRATION_REJECTED")) {
            m_error = rejectedMessage();
            Q_EMIT changed();
            Q_EMIT rejected(m_error);
            return;
        }
        // A player cannot act on a raw error token, and the card shows this text
        // verbatim; the code stays in the log, not on the page.
        Q_UNUSED(code);
        m_error = message.isEmpty() ? tr("启用本机校准失败，请稍后再试。") : message;
        Q_EMIT changed();
    });
}

void CalibrationController::discard()
{
    sendDiscard(false, false);
}

void CalibrationController::recalibrate()
{
    sendDiscard(true, false);
}

void CalibrationController::restoreLocalProfile()
{
    sendDiscard(false, true);
}

void CalibrationController::sendDiscard(bool retireLocalProfile, bool restoreLocalProfile)
{
    if (m_busy || !m_backend)
        return;
    // A profile change is what both flags ask for, and the card must not keep showing what
    // the previous attempt said about a state that no longer exists.
    const bool changesProfile = retireLocalProfile || restoreLocalProfile;
    m_busy = true;
    m_error.clear();
    Q_EMIT changed();
    m_backend->discardCalibration(retireLocalProfile, restoreLocalProfile)->whenDone(this,
        [this, retireLocalProfile, restoreLocalProfile, changesProfile](
            bool ok, const QVariantMap &result, const QString &code, const QString &message) {
        m_busy = false;
        if (!ok) {
            // The Collector's refusals here are already written for the player ("上一份本机
            // 校准已经无法使用，请重新校准。"), so they are shown verbatim; the token stays in
            // the log.
            Q_UNUSED(code);
            m_error = !message.isEmpty()
                ? message
                : retireLocalProfile ? tr("重新校准失败，请稍后再试。")
                : restoreLocalProfile ? tr("恢复上一份本机校准失败，请稍后再试。")
                                      : tr("重新观察失败，请稍后再试。");
            Q_EMIT changed();
            return;
        }
        // The Collector threw the draft away, so nothing derived from it may stay
        // on screen: progress, timeline and blockers all go with it.
        const QString state = result.value(QStringLiteral("state")).toString();
        m_lastResult.clear();
        // 重新观察丢掉的是草稿，不是已经写出并生效的本机档案：provisional 保持原样。
        // 重新校准 does stop that profile, and what is in force is not this controller's to
        // decide: the capture status is re-read and says so.
        publish(state.isEmpty() ? m_state : state, m_gameBuild, QStringList(), QVariantMap(),
                QVariantList(), changesProfile ? false : m_provisional,
                // The rollback consumed the retired file; a retirement produced one. Either way
                // the next capture status is what decides, and it is asked for below.
                restoreLocalProfile ? false : m_retiredLocalProfileAvailable);
        Q_EMIT changed();
        if (changesProfile)
            Q_EMIT refreshRequested();
    });
}

} // namespace mr
